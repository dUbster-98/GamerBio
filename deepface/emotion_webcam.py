"""
Real-time facial emotion analysis from a webcam, optimized for smooth display.

Key idea: the slow part (face detection + emotion model) runs in a background
thread, while the main loop only grabs frames and draws the latest available
result. This keeps the video smooth at full FPS regardless of how long a single
analysis takes.

Usage:
    python examples/emotion_webcam.py
    python examples/emotion_webcam.py --source 0 --detector opencv
    python examples/emotion_webcam.py --detector retinaface   # more accurate, slower

    # Stream the overlaid video to the Blazor dashboard via MJPEG:
    python examples/emotion_webcam.py --stream --port 8080
    python examples/emotion_webcam.py --stream --headless     # no local window
    #   then in Blazor:  <img src="http://<PC-IP>:8080">

Press 'q' to quit (or Ctrl+C in --headless mode).
"""

# built-in dependencies
import time
import threading
import argparse
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, List, Dict, Optional, Tuple

# 3rd party dependencies
import cv2
import requests  # bundled with deepface

# project dependencies
from deepface import DeepFace

# The model itself always predicts these 7 classes
# (deepface/models/demography/Emotion.py). We can't retrain it, but during
# gameplay two of them are noise:
#   - disgust : almost never a genuine gaming reaction, easily confused
#   - sad     : rarely occurs while gaming, steals probability from neutral
# So we drop them in post-processing and renormalize the remaining classes.
MODEL_LABELS = ["angry", "disgust", "fear", "happy", "sad", "surprise", "neutral"]
DROPPED_LABELS = {"disgust", "sad"}

# Active labels kept for display / decision (gaming-relevant emotions):
#   horror games      -> fear, surprise
#   competitive games -> angry, happy
#   plus neutral as the resting state
EMOTION_LABELS = [e for e in MODEL_LABELS if e not in DROPPED_LABELS]

# A distinct BGR color per emotion for the overlay
EMOTION_COLORS = {
    "angry": (0, 0, 255),
    "fear": (128, 0, 128),
    "happy": (0, 215, 255),
    "surprise": (0, 255, 255),
    "neutral": (200, 200, 200),
}

# Per-class calibration weights applied to the raw model scores before
# renormalizing. The FER2013-trained emotion model systematically *under*-reports
# fear (lowest recall of all classes) and leaks it into surprise/neutral, so we
# boost fear to counteract that bias. Tune live with --fear-boost.
#   > 1.0 makes a class easier to become dominant, < 1.0 harder.
EMOTION_WEIGHTS = {
    "angry": 1.0,
    "fear": 1.8,        # compensate for the model under-detecting fear
    "happy": 1.0,
    "surprise": 1.0,    # slightly damp; it often "wins" what was really fear
    "neutral": 0.9,
}

# The model frequently mislabels a fearful face as "sad" (both have furrowed
# brows / tense features). Instead of discarding the dropped "sad" mass evenly,
# redirect a fraction of it into fear before renormalizing, recovering the fear
# signal hidden inside misclassified-as-sad frames. 0.0 = old behavior (drop
# sad entirely), 1.0 = treat all sad as fear. Tune live with --sad-to-fear.
SAD_TO_FEAR_RATIO = 0.7

# Temporal smoothing. Each frame is classified independently, so even a still
# face produces jittery scores (lighting/sensor noise, detector box wobble). We
# blend each new result into a running average (exponential moving average):
#   smoothed = alpha * new + (1 - alpha) * previous
# Lower alpha = steadier but slower to react; higher = twitchier but snappier.
# Tune live with --smoothing.
EMOTION_SMOOTHING = 0.25

# ============================================================
#  실행 기본값 — 여기만 고치면 명령줄 인수 없이 그대로 돌아갑니다.
#  (명령줄에서 --옵션 을 주면 항상 아래 값을 덮어씁니다.)
# ============================================================
# 표정 캡처를 서버 갤러리로 보낼 엔드포인트. None 이면 자동 캡처 끔.
#   로컬 https 프로필: "https://localhost:7211/api/gallery/capture"
#   로컬 http  프로필: "http://localhost:5026/api/gallery/capture"
#   RPi5 프로덕션:     "http://<RPi-IP>:5000/api/gallery/capture"
DEFAULT_CAPTURE_URL = "https://localhost:7211/api/gallery/capture"
DEFAULT_CAPTURE_EMOTION = "surprise"   # 캡처를 유발할 감정
DEFAULT_CAPTURE_THRESHOLD = 90.0       # raw 점수(0~100) 임계값 — 잘 안 잡히면 낮추기
DEFAULT_CAPTURE_COOLDOWN = 3.0         # 캡처 최소 간격(초)

# 대시보드 감정 융합용 엔드포인트. None 이면 송신 안 함.
#   예: "https://localhost:7211/api/emotion"
DEFAULT_POST_URL = None

# 자체서명 https(localhost) 인증서 검증 건너뛰기. 로컬 https 테스트는 True,
# 프로덕션(평문 http)에서는 False 권장. (명령줄 --no-insecure 로 끌 수 있음)
DEFAULT_INSECURE = True

# 서버에 BioMonitor:ApiKey 가 설정돼 있으면 그 값을 넣기. 로컬은 보통 None.
DEFAULT_API_KEY = None


def _face_area(face: Dict[str, Any]) -> int:
    region = face.get("region", {})
    return region.get("w", 0) * region.get("h", 0)


def refocus_emotions(face: Dict[str, Any]) -> Dict[str, Any]:
    """Drop the disabled emotions, calibrate, and renormalize to sum to 100%.

    DeepFace returns all 7 emotions as percentages that sum to ~100. We
    (a) drop disgust/sad which don't occur during gameplay, (b) reclaim part of
    the "sad" score into fear (the two are commonly confused), (c) apply per-class
    weights (EMOTION_WEIGHTS) to correct the model's systematic bias against
    fear, then (d) rescale the remaining classes so they sum to 100. The net
    effect is that the gaming-relevant emotions we care about — especially fear —
    surface reliably instead of getting buried under surprise/neutral/sad.
    """
    emotions = face.get("emotion", {})
    if not emotions:
        return face

    kept = {
        e: float(emotions.get(e, 0.0)) * EMOTION_WEIGHTS.get(e, 1.0)
        for e in EMOTION_LABELS
    }
    # Reclaim the fear signal that the model misfiled as "sad".
    kept["fear"] += float(emotions.get("sad", 0.0)) * SAD_TO_FEAR_RATIO

    total = sum(kept.values())
    if total > 0:
        kept = {e: (v / total) * 100.0 for e, v in kept.items()}

    face["emotion"] = kept
    face["dominant_emotion"] = max(kept, key=kept.get) if kept else "?"
    return face


class EmotionAnalyzer:
    """Runs DeepFace.analyze in a background thread on the most recent frame."""

    def __init__(self, detector_backend: str = "opencv") -> None:
        self.detector_backend = detector_backend
        self._lock = threading.Lock()
        self._latest_frame: Optional[Any] = None       # newest frame to analyze
        self._results: List[Dict[str, Any]] = []        # last analysis result
        self._infer_ms: float = 0.0                     # last analysis duration
        self._smoothed: Optional[Dict[str, float]] = None  # EMA of primary face
        self.capture_poster: Optional["CapturePoster"] = None  # optional auto-capture
        self._running = True
        self._thread = threading.Thread(target=self._worker, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def submit(self, frame: Any) -> None:
        """Hand the latest frame to the worker (overwrites any pending one)."""
        with self._lock:
            self._latest_frame = frame

    def get_results(self) -> List[Dict[str, Any]]:
        with self._lock:
            return self._results

    def get_infer_ms(self) -> float:
        with self._lock:
            return self._infer_ms

    def stop(self) -> None:
        self._running = False
        self._thread.join(timeout=1.0)

    def _worker(self) -> None:
        while self._running:
            with self._lock:
                frame = self._latest_frame
                self._latest_frame = None

            if frame is None:
                time.sleep(0.005)  # nothing new yet
                continue

            tic = time.time()
            try:
                results = DeepFace.analyze(
                    img_path=frame,
                    actions=["emotion"],
                    detector_backend=self.detector_backend,
                    enforce_detection=False,  # don't raise when no face is found
                    silent=True,
                )
            except Exception:  # pylint: disable=broad-except
                results = []
            infer_ms = (time.time() - tic) * 1000

            # Ignore the synthetic "whole image" result deepface returns when no
            # real face is detected (region covers the full frame), then refocus
            # each face onto the gaming-relevant emotions.
            refocused = [
                refocus_emotions(r) for r in results
                if r.get("face_confidence", 1) != 0
            ]

            # Capture trigger uses the RAW (pre-smoothing) score of the analyzed
            # frame so it fires at the real peak, not after the EMA catches up —
            # and ships THIS exact frame, not a later live re-grab.
            capture: Optional[Tuple[Any, float]] = None

            with self._lock:
                # Smooth the primary (largest) face's scores over time so a still
                # face doesn't make the dominant emotion flicker frame to frame.
                if refocused:
                    primary = max(refocused, key=_face_area)
                    if self.capture_poster is not None:
                        raw = primary["emotion"].get(self.capture_poster.emotion, 0.0)
                        capture = (frame, raw)
                    a = EMOTION_SMOOTHING
                    if self._smoothed is None:
                        smoothed = dict(primary["emotion"])
                    else:
                        smoothed = {
                            e: a * primary["emotion"].get(e, 0.0)
                               + (1 - a) * self._smoothed.get(e, 0.0)
                            for e in EMOTION_LABELS
                        }
                    self._smoothed = smoothed
                    primary["emotion"] = smoothed
                    primary["dominant_emotion"] = max(smoothed, key=smoothed.get)
                else:
                    self._smoothed = None  # face lost; reset so we don't lag

                self._results = refocused
                self._infer_ms = infer_ms

            # Evaluate the trigger outside the analyzer lock (it has its own).
            if capture is not None:
                self.capture_poster.consider(*capture)


def draw_overlay(frame: Any, results: List[Dict[str, Any]]) -> None:
    """Draw face boxes, dominant emotion, and per-emotion probability bars."""
    for face in results:
        region = face.get("region", {})
        x, y = region.get("x", 0), region.get("y", 0)
        w, h = region.get("w", 0), region.get("h", 0)

        dominant = face.get("dominant_emotion", "?")
        color = EMOTION_COLORS.get(dominant, (0, 255, 0))

        # Face box + dominant label
        cv2.rectangle(frame, (x, y), (x + w, y + h), color, 2)
        label = f"{dominant} {face.get('emotion', {}).get(dominant, 0):.0f}%"
        cv2.rectangle(frame, (x, y - 28), (x + w, y), color, -1)
        cv2.putText(frame, label, (x + 4, y - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 2)

        # Probability bars next to the face
        emotions = face.get("emotion", {})
        bar_x = x + w + 8
        bar_y = y
        for emo in EMOTION_LABELS:
            prob = emotions.get(emo, 0.0)
            bar_w = int((prob / 100.0) * 120)
            c = EMOTION_COLORS[emo]
            cv2.rectangle(frame, (bar_x, bar_y), (bar_x + 120, bar_y + 14),
                          (50, 50, 50), -1)
            cv2.rectangle(frame, (bar_x, bar_y), (bar_x + bar_w, bar_y + 14), c, -1)
            cv2.putText(frame, f"{emo[:4]} {prob:4.0f}", (bar_x + 2, bar_y + 11),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.35, (255, 255, 255), 1)
            bar_y += 18


class _FrameBuffer:
    """Holds the latest JPEG frame and lets streaming clients block until a new
    one arrives (so we don't busy-loop resending identical frames)."""

    def __init__(self) -> None:
        self._cond = threading.Condition()
        self._jpeg: Optional[bytes] = None
        self._seq = 0

    def update(self, jpeg: bytes) -> None:
        with self._cond:
            self._jpeg = jpeg
            self._seq += 1
            self._cond.notify_all()

    def next(self, last_seq: int, timeout: float = 5.0) -> Tuple[int, Optional[bytes]]:
        """Return (seq, jpeg) once a frame newer than last_seq is available."""
        with self._cond:
            if self._seq == last_seq:
                self._cond.wait(timeout)
            return self._seq, self._jpeg


class _MjpegHandler(BaseHTTPRequestHandler):
    """Serves the overlaid frames as multipart/x-mixed-replace (MJPEG)."""

    def do_GET(self) -> None:  # noqa: N802 (name mandated by BaseHTTPRequestHandler)
        if self.path in ("/", "/stream.mjpg", "/stream"):
            self._serve_stream()
        elif self.path in ("/healthz", "/health"):
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"ok")
        else:
            self.send_error(404)

    def _serve_stream(self) -> None:
        self.send_response(200)
        self.send_header("Age", "0")
        self.send_header("Cache-Control", "no-cache, private")
        self.send_header("Pragma", "no-cache")
        self.send_header(
            "Content-Type", "multipart/x-mixed-replace; boundary=FRAME"
        )
        self.end_headers()

        buffer: _FrameBuffer = self.server.frame_buffer  # type: ignore[attr-defined]
        last_seq = 0
        try:
            while True:
                last_seq, jpeg = buffer.next(last_seq)
                if jpeg is None:
                    continue
                self.wfile.write(b"--FRAME\r\n")
                self.wfile.write(b"Content-Type: image/jpeg\r\n")
                self.wfile.write(f"Content-Length: {len(jpeg)}\r\n\r\n".encode())
                self.wfile.write(jpeg)
                self.wfile.write(b"\r\n")
        except (BrokenPipeError, ConnectionResetError):
            pass  # client disconnected; just end this handler

    def log_message(self, *args: Any) -> None:  # silence per-request logging
        pass


class MjpegStreamServer:
    """Background HTTP server that streams the latest overlaid frame as MJPEG.

    Point a Blazor dashboard at it directly:  <img src="http://PC:8080">
    """

    def __init__(self, host: str = "0.0.0.0", port: int = 8080,
                 quality: int = 80) -> None:
        self.quality = quality
        self._buffer = _FrameBuffer()
        self._httpd = ThreadingHTTPServer((host, port), _MjpegHandler)
        self._httpd.frame_buffer = self._buffer  # type: ignore[attr-defined]
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def update(self, frame: Any) -> None:
        """Encode a BGR frame to JPEG and publish it to connected clients."""
        ok, buf = cv2.imencode(
            ".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, self.quality]
        )
        if ok:
            self._buffer.update(buf.tobytes())

    def stop(self) -> None:
        self._httpd.shutdown()
        self._httpd.server_close()


class EmotionPoster:
    """Posts the primary face's emotion to the RPi5 /api/emotion endpoint.

    Runs in its own thread and sends at most one request per `interval` seconds
    (latest-wins), so high analysis FPS never floods the server. HTTP failures
    are swallowed — a missed emotion frame is harmless; the next one follows.
    """

    def __init__(self, url: str, api_key: Optional[str] = None,
                 interval: float = 1.0, verify: bool = True) -> None:
        self.url = url
        self.api_key = api_key
        self.interval = interval
        self.verify = verify
        self._logged_err = False
        self._lock = threading.Lock()
        self._payload: Optional[Dict[str, Any]] = None
        self._running = True
        self._thread = threading.Thread(target=self._worker, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def submit(self, results: List[Dict[str, Any]]) -> None:
        """Stash the latest primary-face emotion for the worker to send."""
        if not results:
            return
        primary = max(results, key=_face_area)
        emotions = primary.get("emotion", {})
        if not emotions:
            return
        with self._lock:
            self._payload = {
                "dominant": primary.get("dominant_emotion", "neutral"),
                "scores": {k: round(float(v), 2) for k, v in emotions.items()},
                "timestamp": datetime.now(timezone.utc).isoformat(),
            }

    def _worker(self) -> None:
        headers = {"Content-Type": "application/json"}
        if self.api_key:
            headers["X-Api-Key"] = self.api_key
        while self._running:
            time.sleep(self.interval)
            with self._lock:
                payload = self._payload
                self._payload = None
            if payload is None:
                continue
            try:
                requests.post(self.url, json=payload, headers=headers,
                              timeout=3, verify=self.verify)
                self._logged_err = False  # resurface errors if they recur
            except requests.RequestException as e:
                if not self._logged_err:
                    print(f"[emotion] POST failed → {self.url}: {e}")
                    self._logged_err = True

    def stop(self) -> None:
        self._running = False


class CapturePoster:
    """Ships the exact analyzed frame to the server's gallery-capture endpoint
    when a raw emotion score crosses a threshold.

    Decoupled from EmotionPoster on purpose: it sends an image (not just scores)
    and triggers on the RAW per-frame score, so the saved photo is the real peak
    moment rather than a smoothed/late one. Guard logic mirrors the server's old
    auto-capture: re-arm (one shot per surge) + cooldown (min interval). The
    cheap guard check runs on the analysis thread; JPEG encode + POST happen on
    this worker thread so analysis never blocks on the network.
    """

    def __init__(self, url: str, api_key: Optional[str] = None,
                 emotion: str = "surprise", threshold: float = 90.0,
                 cooldown: float = 3.0, quality: int = 85,
                 verify: bool = True) -> None:
        self.url = url
        self.api_key = api_key
        self.emotion = emotion
        self.threshold = threshold
        self.cooldown = cooldown
        self.quality = quality
        self.verify = verify
        self._lock = threading.Lock()
        self._pending: Optional[Tuple[Any, float]] = None
        self._armed = True
        self._last = 0.0
        self._running = True
        self._logged_err = False
        self._thread = threading.Thread(target=self._worker, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def consider(self, frame: Any, score: float) -> None:
        """Cheap guard check on the analysis thread; stashes the frame to send."""
        if score < self.threshold:
            self._armed = True  # dropped below → re-arm for the next surge
            return
        now = time.time()
        if not self._armed or (now - self._last) < self.cooldown:
            return
        self._armed = False
        self._last = now
        with self._lock:
            self._pending = (frame, score)  # latest-wins

    def _worker(self) -> None:
        headers = {"Content-Type": "image/jpeg"}
        if self.api_key:
            headers["X-Api-Key"] = self.api_key
        while self._running:
            time.sleep(0.02)
            with self._lock:
                item = self._pending
                self._pending = None
            if item is None:
                continue
            frame, score = item
            ok, buf = cv2.imencode(".jpg", frame,
                                   [cv2.IMWRITE_JPEG_QUALITY, self.quality])
            if not ok:
                continue
            params = {"emotion": self.emotion, "score": f"{score:.0f}"}
            try:
                requests.post(self.url, params=params, data=buf.tobytes(),
                              headers=headers, timeout=5, verify=self.verify)
                self._logged_err = False
            except requests.RequestException as e:
                if not self._logged_err:
                    print(f"[capture] POST failed → {self.url}: {e}")
                    self._logged_err = True

    def stop(self) -> None:
        self._running = False


def main() -> None:
    parser = argparse.ArgumentParser(description="Real-time webcam emotion analysis")
    parser.add_argument("--source", default=0,
                        help="Webcam index (0) or video file path")
    parser.add_argument("--detector", default="opencv",
                        help="Face detector: opencv (fast), mtcnn / retinaface (accurate)")
    parser.add_argument("--fear-boost", type=float, default=None,
                        help="Override the fear calibration weight (e.g. 2.5). "
                             "Higher = fear detected more easily.")
    parser.add_argument("--sad-to-fear", type=float, default=None,
                        help="Fraction of the model's 'sad' score redirected into "
                             "fear (0.0-1.0). Higher = more sad reclassified as fear.")
    parser.add_argument("--smoothing", type=float, default=None,
                        help="Temporal smoothing alpha (0.0-1.0). Lower = steadier "
                             "but slower to react. Default 0.3.")
    parser.add_argument("--stream", action="store_true",
                        help="Serve the overlaid video as an MJPEG stream "
                             "(for the Blazor dashboard).")
    parser.add_argument("--host", default="0.0.0.0",
                        help="MJPEG server bind address (default: all interfaces).")
    parser.add_argument("--port", type=int, default=8080,
                        help="MJPEG server port (default: 8080).")
    parser.add_argument("--quality", type=int, default=80,
                        help="MJPEG JPEG quality 1-100 (default: 80).")
    parser.add_argument("--headless", action="store_true",
                        help="Don't open a local window (use with --stream on a "
                             "server). Quit with Ctrl+C.")
    parser.add_argument("--post-url", default=DEFAULT_POST_URL,
                        help="RPi5 emotion endpoint, e.g. "
                             "http://192.168.0.104:5000/api/emotion. Enables "
                             "sending dominant emotion + scores for fusion. "
                             "Default from DEFAULT_POST_URL at top of file.")
    parser.add_argument("--api-key", default=DEFAULT_API_KEY,
                        help="X-Api-Key header value for --post-url / --capture-url "
                             "(must match BioMonitor:ApiKey on the server).")
    parser.add_argument("--post-interval", type=float, default=1.0,
                        help="Min seconds between emotion POSTs (default: 1.0).")
    parser.add_argument("--insecure", action=argparse.BooleanOptionalAction,
                        default=DEFAULT_INSECURE,
                        help="Skip TLS cert verification on --post-url / "
                             "--capture-url (self-signed https://localhost dev "
                             "certs). Use --no-insecure to force verification. "
                             "Default from DEFAULT_INSECURE at top of file.")
    parser.add_argument("--capture-url", default=DEFAULT_CAPTURE_URL,
                        help="Server gallery-capture endpoint, e.g. "
                             "http://localhost:5026/api/gallery/capture. When the "
                             "raw --capture-emotion score crosses the threshold, "
                             "the exact analyzed frame is POSTed and saved. "
                             "Default from DEFAULT_CAPTURE_URL at top of file.")
    parser.add_argument("--capture-emotion", default=DEFAULT_CAPTURE_EMOTION,
                        help="Emotion that triggers auto-capture (default: surprise).")
    parser.add_argument("--capture-threshold", type=float, default=DEFAULT_CAPTURE_THRESHOLD,
                        help="Raw score (0-100) that triggers capture (default: 90).")
    parser.add_argument("--capture-cooldown", type=float, default=DEFAULT_CAPTURE_COOLDOWN,
                        help="Min seconds between auto-captures (default: 3.0).")
    args = parser.parse_args()

    global SAD_TO_FEAR_RATIO, EMOTION_SMOOTHING
    if args.fear_boost is not None:
        EMOTION_WEIGHTS["fear"] = args.fear_boost
    if args.sad_to_fear is not None:
        SAD_TO_FEAR_RATIO = args.sad_to_fear
    if args.smoothing is not None:
        EMOTION_SMOOTHING = args.smoothing

    source = int(args.source) if str(args.source).isdigit() else args.source
    cap = cv2.VideoCapture(source)
    if not cap.isOpened():
        print(f"Cannot open video source: {source}")
        return

    analyzer = EmotionAnalyzer(detector_backend=args.detector)
    analyzer.start()

    stream: Optional[MjpegStreamServer] = None
    if args.stream:
        stream = MjpegStreamServer(args.host, args.port, args.quality)
        stream.start()
        print(f"MJPEG stream: http://{args.host}:{args.port}/  "
              f"(use in Blazor: <img src=\"http://<PC-IP>:{args.port}\">)")

    poster: Optional[EmotionPoster] = None
    if args.post_url:
        if args.insecure:
            import urllib3
            urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
        poster = EmotionPoster(args.post_url, args.api_key, args.post_interval,
                               verify=not args.insecure)
        poster.start()
        print(f"Posting emotion to {args.post_url} every {args.post_interval}s"
              f"{' (TLS verify off)' if args.insecure else ''}")

    if args.capture_url:
        if args.insecure:
            import urllib3
            urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
        cposter = CapturePoster(
            args.capture_url, args.api_key,
            emotion=args.capture_emotion.lower(),
            threshold=args.capture_threshold,
            cooldown=args.capture_cooldown,
            quality=args.quality,
            verify=not args.insecure)
        cposter.start()
        analyzer.capture_poster = cposter
        print(f"Auto-capture: {args.capture_emotion} >= {args.capture_threshold:.0f} "
              f"→ {args.capture_url} (cooldown {args.capture_cooldown:.0f}s)")

    # Display-FPS tracking (independent of analysis speed)
    prev = time.time()
    disp_fps = 0.0

    if args.headless:
        print("Running headless... press Ctrl+C to quit.")
    else:
        print("Running... press 'q' in the window to quit.")

    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break

            # Always feed the worker the freshest frame; it analyzes when free.
            analyzer.submit(frame.copy())

            # Draw the most recent results onto every displayed frame.
            results = analyzer.get_results()
            draw_overlay(frame, results)

            # Hand the latest emotion to the poster (throttled send to RPi5).
            if poster is not None:
                poster.submit(results)

            # HUD: display FPS and analysis latency
            now = time.time()
            disp_fps = 0.9 * disp_fps + 0.1 * (1.0 / max(now - prev, 1e-6))
            prev = now
            infer_ms = analyzer.get_infer_ms()
            hud = f"display {disp_fps:4.1f} FPS | analyze {infer_ms:5.0f} ms ({args.detector})"
            cv2.putText(frame, hud, (10, 25),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)

            # Publish the overlaid frame to MJPEG clients (Blazor dashboard).
            if stream is not None:
                stream.update(frame)

            if not args.headless:
                cv2.imshow("DeepFace - Emotion (press q to quit)", frame)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    break
            elif stream is None:
                # Headless with no stream has no consumer; avoid a hot spin.
                time.sleep(0.01)
    except KeyboardInterrupt:
        pass

    analyzer.stop()
    if stream is not None:
        stream.stop()
    if poster is not None:
        poster.stop()
    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
