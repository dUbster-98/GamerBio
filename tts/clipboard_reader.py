"""
Genie-TTS 단축키 리더 (로컬 데스크탑 전용 / 경량판)

모델을 한 번만 로딩해두고, 크롬 등에서 텍스트를 드래그한 뒤
단축키를 누르면 그 문장을 등록된 캐릭터 목소리로 바로 읽어줍니다.

    Ctrl+Alt+R    → 드래그한(선택된) 텍스트 읽기
    Ctrl+Alt+S    → 재생 중지
    Ctrl+Alt+W    → 직전 합성 음성을 파일로 저장
    Ctrl+Alt+E    → 레퍼런스 음성 전환 (현재 캐릭터, ref_dir 안 샘플 순환)
    Ctrl+Alt+1..9 → 캐릭터(목소리) 전환  (CHARACTERS 등록 순서)
    Ctrl+Alt+Q    → 종료

캐릭터 추가/전환:
    아래 CHARACTERS 딕셔너리에 한 줄 추가하면 끝. 실행 중에는 Ctrl+Alt+숫자로,
    실행 시점엔 --character <키> 로 시작 캐릭터를 고른다. 새 캐릭터는 처음 전환할 때
    한 번만 로딩되고(수십 초) 이후에는 즉시 전환된다.

레퍼런스(목소리 톤) 교체:
    캐릭터의 ref_dir 폴더에 'X.wav + X.txt(그 음성의 전사)' 쌍을 넣어두면 자동 인식.
    실행 중 Ctrl+Alt+E 로 폴더 안 레퍼런스들을 순환 전환(모델 재로딩 없이 즉시 적용).
    새 레퍼런스 추가 = wav 1개 + 같은 이름 txt 1개를 폴더에 드롭하면 끝(코드 수정 X).
    ※ genie 는 mp3 미지원 → wav/flac/ogg 로 변환해서 둘 것.

설치:  pip install keyboard pyperclip sounddevice numpy
       # genie 는 한국어 G2P 가 포함된 git 버전 필요 (PyPI 2.0.2 에는 한국어 미포함):
       pip install "git+https://github.com/High-Logic/Genie-TTS.git"
실행:  python clipboard_reader.py                 # 기본 캐릭터로 시작
       python clipboard_reader.py --character mika # 특정 캐릭터로 시작
       python clipboard_reader.py --list           # 등록된 캐릭터 목록만 출력

[구조 메모] genie 의 내장 재생기(play=True)는 워커 스레드·오디오 큐·OutputStream 을
세션 간에 재사용한다. 단축키 콜백에서 genie.stop() 으로 끊고 새 세션을 빠르게 시작하면
① 이전 세션 오디오 청크가 다음 세션 끝에 재생되거나(끝에 붙는 레퍼런스 음성),
② 세션 경계 마커가 엇갈려 wait_for_playback_done() 이 무한 대기 → 후킹 스레드까지 데드락.
그래서 여기서는 genie 는 "생성만"(play=False, save_path) 시키고, 재생은 우리가 직접 한다.
모든 합성은 단일 워커 스레드에서 순차 실행 → 세션이 겹치지 않아 위 현상이 구조적으로 없다.
"""

import argparse
import datetime
import os
import queue as _queue
import re
import shutil
import sys
import threading
import time
import wave

import keyboard
import numpy as np
import pyperclip
import sounddevice as sd

import genie_tts as genie

# ── 캐릭터 레지스트리 ─────────────────────────────────────────────────
# 캐릭터(목소리)를 추가하려면 여기 한 항목만 더 넣으면 된다. 키(예: "hutao")는
# 실행 중 Ctrl+Alt+숫자 전환과 --character 인수에 그대로 쓰인다.
#   language: "Korean" / "Japanese" / "English" / "Chinese"
#             (한국어는 genie git 버전 필요 — 파일 상단 설치 안내 참고)
#   ref_dir : 레퍼런스 음성(wav) 들이 든 폴더. 각 X.wav 옆에 같은 이름의 X.txt
#             (그 음성이 실제 말한 문장 = 전사)를 두면 자동 인식된다.
#             실행 중 Ctrl+Alt+E 로 폴더 안 레퍼런스들을 순환 전환(즉시 적용).
_MODELS_ROOT = r"C:\Users\tjdgu\source\repos\GamerBio\tts\CharacterModels\v2ProPlus"

CHARACTERS = {
    "hutao": {
        "model_dir": rf"{_MODELS_ROOT}\hutao\tts_models",
        "ref_dir":   rf"{_MODELS_ROOT}\hutao\prompt_wav",
        "language":  "Korean",
    },
    # 예시) 캐릭터를 더 추가하려면 아래 형태로 한 항목씩:
    "mika": {
        "model_dir": rf"{_MODELS_ROOT}\mika\tts_models",
        "ref_dir":   rf"{_MODELS_ROOT}\mika\prompt_wav",
        "language":  "Japanese",
    },
}

DEFAULT_CHARACTER = "hutao"   # --character 미지정 시 시작 캐릭터

# genie 가 레퍼런스로 받아들이는 오디오 확장자 (mp3 미지원 → wav 등으로 변환해 둘 것)
REF_AUDIO_EXTS = (".wav", ".flac", ".ogg", ".aiff", ".aif")

# 워밍업(첫 합성 그래프 초기화)용 짧은 문장 — 캐릭터 언어에 맞춰 선택
WARMUP_TEXT = {
    "Korean":   "안녕하세요.",
    "Japanese": "ウォームアップ。",
    "English":  "Warming up.",
    "Chinese":  "测试。",
}

MAX_CHARS = 5000

# 저장 관련: 매 합성 결과를 임시 wav 로 항상 남겨두고, 단축키로 보관함에 복사
_SCRIPT_DIR    = os.path.dirname(os.path.abspath(__file__))
RECORDINGS_DIR = os.path.join(_SCRIPT_DIR, "recordings")   # 저장 폴더
LAST_WAV       = os.path.join(_SCRIPT_DIR, ".last_synth.wav")  # 직전 합성 임시본
# ─────────────────────────────────────────────────────────────────────

_quit_event = threading.Event()
_ready      = threading.Event()   # 모델 로딩+워밍업 완료 후 set → 그 전 단축키는 안내만

# 합성/재생 파이프라인: 단축키 콜백은 큐에 텍스트만 넣고 즉시 반환(블로킹 금지),
# 단일 워커 스레드가 순차로 [생성 → 재생] 한다.
_req_q      = _queue.Queue()       # 읽기 요청(text) 큐
_interrupt  = threading.Event()    # 현재 재생 중단 + 진행 중 합성 결과 폐기 신호
_last_text  = ""                   # 직전에 합성한 텍스트 (자동 파일명용)

# 캐릭터 전환 상태: 핫키 스레드가 _active_key 만 바꾸고(즉시 반환),
# 실제 모델 로딩은 워커 스레드가 다음 읽기 직전에 한 번만 수행(_loaded 캐시).
_active_key = DEFAULT_CHARACTER    # 현재 활성 캐릭터 키
_loaded     = set()                # genie 에 이미 load_character 된 키
_load_lock  = threading.Lock()     # 동시 로딩 방지

# 레퍼런스 전환 상태: 캐릭터별로 ref_dir 에서 찾은 (오디오, 전사) 목록과 현재 인덱스.
# set_reference_audio 는 가벼워서(모델 재로딩 X) 핫키 스레드에서 바로 적용 가능.
_refs       = {}                   # key -> [(audio_path, transcript), ...]
_ref_idx    = {}                   # key -> 현재 레퍼런스 인덱스


def discover_refs(key: str):
    """캐릭터 ref_dir 에서 'X.wav + X.txt(전사)' 쌍을 찾아 목록으로. 결과를 _refs 캐시.

    X.txt 가 없거나 비어 있으면 그 오디오는 건너뛰고 경고한다(전사가 있어야 품질이 나옴).
    """
    if key in _refs:
        return _refs[key]
    ref_dir = CHARACTERS[key]["ref_dir"]
    pairs = []
    if not os.path.isdir(ref_dir):
        print(f"[경고] [{key}] 레퍼런스 폴더 없음: {ref_dir}")
        _refs[key] = pairs
        return pairs
    for fname in sorted(os.listdir(ref_dir)):
        stem, ext = os.path.splitext(fname)
        if ext.lower() not in REF_AUDIO_EXTS:
            continue
        txt_path = os.path.join(ref_dir, stem + ".txt")
        if not os.path.exists(txt_path):
            print(f"[안내] [{key}] '{fname}': 전사 파일 {stem}.txt 없음 → 건너뜀")
            continue
        with open(txt_path, encoding="utf-8") as f:
            transcript = f.read().strip()
        if not transcript:
            print(f"[안내] [{key}] '{fname}': {stem}.txt 가 비어 있음 → 건너뜀")
            continue
        pairs.append((os.path.join(ref_dir, fname), transcript))
    _refs[key] = pairs
    _ref_idx.setdefault(key, 0)
    return pairs


def apply_reference(key: str):
    """현재 인덱스의 레퍼런스를 genie 에 등록. 모델 미로딩 상태여도 language 를 명시해 안전."""
    pairs = discover_refs(key)
    if not pairs:
        raise RuntimeError(
            f"[{key}] 사용할 레퍼런스가 없습니다. {CHARACTERS[key]['ref_dir']} 에 "
            f"X.wav + X.txt(전사) 쌍을 두세요."
        )
    idx = _ref_idx.get(key, 0) % len(pairs)
    _ref_idx[key] = idx
    audio_path, transcript = pairs[idx]
    genie.set_reference_audio(
        character_name=key,
        audio_path=audio_path,
        audio_text=transcript,
        language=CHARACTERS[key]["language"],   # 모델 로드 전이어도 동작하도록 명시
    )
    return audio_path, transcript


def _is_virtual(name: str) -> bool:
    """가상/래퍼 장치('사운드 매퍼', '주 사운드 드라이버' 등) 판별."""
    low = name.lower()
    return "주 사운드" in name or "primary" in low or "사운드 매퍼" in name or "mapper" in low


def select_output_device():
    """우리 직접 재생(sd.play)용 출력 장치를 MME 실제 스피커로 지정.

    이 PC(Realtek)에서는 DirectSound 출력이 write 는 되지만 실제 소리가 안 났다
    (진단: audio_test.py 에서 MME 2/3 만 들리고 DirectSound 7 은 무음).
    그래서 실측으로 소리가 나는 MME 실제 출력 장치(시스템 기본 = 보통 스피커)를 쓴다.
    가상 '사운드 매퍼' 대신 실제 장치명으로 지정한다.
    참고: MME 는 다른 앱이 장치를 배타 점유하면 드물게 -9999 가 날 수 있다(일시적).
    """
    try:
        devs = sd.query_devices()
        apis = sd.query_hostapis()
        mme_idx = next((i for i, a in enumerate(apis) if a["name"] == "MME"), None)
        if mme_idx is None:
            print("[안내] MME host API 없음. 기본 출력 장치 사용.")
            return

        # 시스템 기본 출력이 실제 장치면 그 이름을 우선 타깃으로
        cur_out = sd.default.device[1]
        target_name = None
        if isinstance(cur_out, int) and cur_out >= 0 and not _is_virtual(devs[cur_out]["name"]):
            target_name = devs[cur_out]["name"]

        def mme_outputs():
            return [i for i, d in enumerate(devs)
                    if d["hostapi"] == mme_idx and d["max_output_channels"] > 0
                    and not _is_virtual(d["name"])]

        chosen = None
        if target_name:
            chosen = next((i for i in mme_outputs() if devs[i]["name"] == target_name), None)
        if chosen is None:
            outs = mme_outputs()
            chosen = outs[0] if outs else None

        if chosen is None:
            print("[안내] MME 실제 출력 장치를 못 찾음. 기본 출력 장치 사용.")
            return

        sd.default.device = (sd.default.device[0], chosen)
        print(f">> 출력 장치: [{chosen}] {devs[chosen]['name']} (MME)")
    except Exception as e:
        print(f"[경고] 출력 장치 선택 실패(무시): {e}")


# 참고: Intel Arc iGPU + DirectML 로 시도했으나 이 모델(자기회귀 T2S 디코더,
# 작은 연산을 토큰마다 반복)에서는 CPU↔GPU 전송·폴백 오버헤드로 오히려 ~2.5배
# 느렸음(68s vs CPU 27s). 그래서 CPU(기본 provider) 유지. GPU 코드는 두지 않음.


def ensure_loaded(key: str):
    """캐릭터 모델을 (아직 안 됐으면) genie 에 로드 + 워밍업. 워커 스레드에서 호출.

    수십 초 소요되지만 캐릭터당 단 한 번. 이후 전환은 _loaded 캐시로 즉시.
    """
    if key in _loaded:
        return
    with _load_lock:
        if key in _loaded:          # 락 대기 중 다른 스레드가 끝냈을 수 있음
            return
        cfg = CHARACTERS[key]
        print(f">> [{key}] 모델 로딩 중... (처음 한 번, 수십 초)")
        genie.load_character(
            character_name=key,
            onnx_model_dir=cfg["model_dir"],
            language=cfg["language"],
        )
        audio_path, _ = apply_reference(key)   # ref_dir 에서 현재 레퍼런스 등록
        print(f">> [{key}] 레퍼런스: {os.path.basename(audio_path)}")
        # 워밍업: 첫 합성에서 일어나는 그래프 초기화를 미리 끝낸다.
        # play=False → genie 내장 재생기를 쓰지 않고 생성만 (워커와 동일 경로).
        print(f">> [{key}] 워밍업 중...")
        warm = WARMUP_TEXT.get(cfg["language"], "테스트")
        genie.tts(character_name=key, text=warm, play=False)
        _loaded.add(key)
        print(f">> [{key}] 준비 완료")


def grab_selection() -> str:
    """선택 영역을 Ctrl+C로 복사 → 클립보드가 갱신될 때까지 짧게 폴링."""
    before = pyperclip.paste()

    # 단축키 조합(ctrl/alt)이 눌린 상태이므로 떼고 복사 전송
    keyboard.release("alt")
    keyboard.release("ctrl")
    keyboard.send("ctrl+c")

    # 고정 sleep 대신, 새 내용이 들어올 때까지만 대기 (최대 0.4s)
    deadline = time.time() + 0.4
    while time.time() < deadline:
        cur = pyperclip.paste()
        if cur and cur != before:
            return cur
        time.sleep(0.02)
    return pyperclip.paste()


def _load_wav(path: str):
    """합성된 wav(32kHz mono int16) 를 numpy 배열로 읽는다."""
    with wave.open(path, "rb") as w:
        sr = w.getframerate()
        n = w.getnframes()
        data = np.frombuffer(w.readframes(n), dtype=np.int16)
    return data, sr


def _play(data: np.ndarray, sr: int):
    """우리가 직접 재생. _interrupt 가 서면(새 요청/정지) 즉시 중단."""
    try:
        sd.play(data, sr)
    except Exception as e:
        print(f"[오류] 재생 실패: {e}")
        return
    while not _quit_event.is_set():
        if _interrupt.is_set():
            sd.stop()
            return
        st = sd.get_stream()
        if st is None or not st.active:   # 자연 종료
            return
        time.sleep(0.05)
    sd.stop()


def _synth_play_worker():
    """단일 워커: 큐에서 요청을 받아 순차로 [생성 → 재생]. 세션이 겹치지 않는다."""
    global _last_text
    while not _quit_event.is_set():
        try:
            text = _req_q.get(timeout=0.5)
        except _queue.Empty:
            continue
        if text is None:                  # 종료 신호
            break
        # 그새 더 쌓인 요청이 있으면 가장 최신 것만 처리 (연타 시 중간 요청 스킵)
        while True:
            try:
                text = _req_q.get_nowait()
            except _queue.Empty:
                break
        if text is None:
            break

        _interrupt.clear()
        try:
            key = _active_key            # 이번 요청에 쓸 캐릭터 확정(처리 중 전환돼도 일관)
            ensure_loaded(key)           # 첫 사용 캐릭터면 여기서 1회 로딩
            if _interrupt.is_set():      # 로딩 중 새 요청이 들어옴 → 이 결과는 버림
                print(">> (새 요청으로 취소됨)")
                continue
            print(f">> [{key}] 합성 중... ({len(text)}자, 잠시 기다려 주세요)")
            t0 = time.time()
            # 합성 전 mtime 기록 → genie 가 조용히 실패(예: 레퍼런스 미등록)하면
            # save_path 가 갱신되지 않는다. 그걸 감지해 '직전 음성 재생'을 막는다.
            prev_mtime = os.path.getmtime(LAST_WAV) if os.path.exists(LAST_WAV) else 0
            # genie 는 생성만. 재생기/오디오 큐를 안 쓰므로 데드락·잔재 청크가 없다.
            genie.tts(
                character_name=key,
                text=text,
                play=False,
                split_sentence=True,
                save_path=LAST_WAV,
            )
            if _interrupt.is_set():       # 합성 중 새 요청이 들어옴 → 이 결과는 버림
                print(">> (새 요청으로 취소됨)")
                continue
            if not os.path.exists(LAST_WAV) or os.path.getmtime(LAST_WAV) <= prev_mtime:
                print("[오류] 합성 결과가 생성되지 않았습니다 (genie 가 음성을 만들지 못함). "
                      "레퍼런스 오디오/언어 설정을 확인하세요. (직전 음성은 재생하지 않음)")
                continue
            _last_text = text
            data, sr = _load_wav(LAST_WAV)
            print(f">> 재생 ({time.time() - t0:.1f}s 합성)  · 저장하려면 Ctrl+Alt+W")
            _play(data, sr)
        except Exception as e:
            print(f"[오류] 합성/재생 실패: {e}")


def _slugify(text: str, maxlen: int = 30) -> str:
    """텍스트를 안전한 파일명 조각으로. 한글/영문/숫자만 남기고 공백은 _."""
    text = re.sub(r"\s+", "_", text.strip())
    text = re.sub(r"[^0-9A-Za-z가-힣_]", "", text)
    return text[:maxlen]


def on_save():
    """직전 합성 음성을 자동 이름(타임스탬프+텍스트)으로 recordings/ 에 바로 저장."""
    if not os.path.exists(LAST_WAV):
        print("[안내] 저장할 합성 결과가 없습니다. 먼저 텍스트를 읽어주세요(Ctrl+Alt+R).")
        return

    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    slug = _slugify(_last_text)
    name = f"{stamp}_{slug}.wav" if slug else f"{stamp}.wav"

    try:
        os.makedirs(RECORDINGS_DIR, exist_ok=True)
        dest = os.path.join(RECORDINGS_DIR, name)
        shutil.copyfile(LAST_WAV, dest)
        print(f">> 저장됨: {dest}")
    except Exception as e:
        print(f"[오류] 저장 실패: {e}")


def on_read():
    """단축키 콜백: 블로킹 없이 선택 텍스트를 큐에 넣고 즉시 반환."""
    if not _ready.is_set():
        print("[안내] 아직 모델 로딩 중입니다. '>> 준비 완료' 가 뜬 뒤에 눌러주세요.")
        return
    text = grab_selection().strip()[:MAX_CHARS]
    if not text:
        print("[안내] 읽을 텍스트가 없습니다 (드래그 후 다시 시도).")
        return
    print(f">> 읽기: {text[:60]}{'...' if len(text) > 60 else ''}")
    _interrupt.set()   # 진행 중인 재생/합성 결과를 중단·폐기
    sd.stop()          # 현재 재생 즉시 정지
    _req_q.put(text)


def on_stop():
    """재생 중지(단축키 콜백). genie.stop() 을 쓰지 않고 우리 재생만 멈춘다."""
    _interrupt.set()
    sd.stop()


def on_switch(key: str):
    """캐릭터 전환(단축키 콜백). _active_key 만 바꾸고 즉시 반환(논블로킹).

    실제 모델 로딩은 다음 읽기 때 워커가 1회만 수행(_loaded 캐시). 처음 쓰는
    캐릭터로 바꾸면 그 첫 읽기만 로딩 시간이 더 걸린다.
    """
    global _active_key
    if key == _active_key:
        print(f">> 이미 [{key}] 목소리입니다.")
        return
    _active_key = key
    state = "로드됨" if key in _loaded else "다음 읽기 때 로딩"
    print(f">> 목소리 전환 → [{key}] ({state})")


def on_ref_next():
    """현재 캐릭터의 레퍼런스 음성을 다음 것으로 순환 전환(단축키 콜백).

    set_reference_audio 는 모델 재로딩이 없어 가벼우므로 여기서 바로 적용한다.
    다음 읽기부터 새 레퍼런스가 쓰인다(진행 중인 합성은 그대로 둠).
    """
    key = _active_key
    try:
        pairs = discover_refs(key)
        if len(pairs) <= 1:
            n = len(pairs)
            print(f">> [{key}] 전환할 레퍼런스가 {'없습니다' if n == 0 else '하나뿐입니다'} "
                  f"({CHARACTERS[key]['ref_dir']} 에 X.wav + X.txt 추가).")
            return
        _ref_idx[key] = (_ref_idx.get(key, 0) + 1) % len(pairs)
        audio_path, transcript = apply_reference(key)
        preview = transcript[:30] + ("…" if len(transcript) > 30 else "")
        print(f">> [{key}] 레퍼런스 → {os.path.basename(audio_path)}  ({preview})")
    except Exception as e:
        print(f"[오류] 레퍼런스 전환 실패: {e}")


def main():
    # 콘솔 코드페이지가 UTF-8 이 아니어도(예: cp932/cp949) 한글 로그 print 가
    # UnicodeEncodeError 로 핫키 스레드를 죽이지 않도록 stdout 을 UTF-8 로 고정.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="Genie-TTS 클립보드 리더")
    parser.add_argument("--character", "-c", default=DEFAULT_CHARACTER,
                        help=f"시작 캐릭터 키 (등록: {', '.join(CHARACTERS)})")
    parser.add_argument("--list", action="store_true", help="등록된 캐릭터 목록 출력 후 종료")
    args = parser.parse_args()

    if args.list:
        print("등록된 캐릭터:")
        for i, (k, cfg) in enumerate(CHARACTERS.items(), 1):
            print(f"  [{i}] {k}  ({cfg['language']})")
        return

    if args.character not in CHARACTERS:
        print(f"[오류] 알 수 없는 캐릭터 '{args.character}'. 등록: {', '.join(CHARACTERS)}")
        sys.exit(1)

    global _active_key
    _active_key = args.character

    select_output_device()

    # 단축키를 로딩 '전에' 먼저 등록 → 로딩 중에 눌러도 무반응이 아니라 안내가 뜬다.
    # (콜백은 keyboard 의 별도 후킹 스레드에서 실행되며, 모두 논블로킹이라 묶이지 않는다)
    keyboard.add_hotkey("ctrl+alt+r", on_read)
    keyboard.add_hotkey("ctrl+alt+s", on_stop)
    keyboard.add_hotkey("ctrl+alt+w", on_save)
    keyboard.add_hotkey("ctrl+alt+e", on_ref_next)
    keyboard.add_hotkey("ctrl+alt+q", _quit_event.set)

    # 캐릭터 전환 핫키: 등록 순서대로 Ctrl+Alt+1..9
    char_keys = list(CHARACTERS)[:9]
    for i, k in enumerate(char_keys, 1):
        keyboard.add_hotkey(f"ctrl+alt+{i}", lambda k=k: on_switch(k))

    print("─" * 46)
    print("  Ctrl+Alt+R  →  드래그한 텍스트 읽기")
    print("  Ctrl+Alt+S  →  재생 중지")
    print("  Ctrl+Alt+W  →  직전 합성 음성 저장")
    print("  Ctrl+Alt+E  →  레퍼런스 음성 전환 (현재 캐릭터)")
    if len(char_keys) > 1:
        labels = "  ".join(f"{i}:{k}" for i, k in enumerate(char_keys, 1))
        print(f"  Ctrl+Alt+1..  →  목소리 전환  ({labels})")
    print("  Ctrl+Alt+Q  →  종료")
    print("─" * 46)

    # 시작 캐릭터의 레퍼런스 목록 미리 안내
    refs = discover_refs(_active_key)
    print(f">> 시작 캐릭터: [{_active_key}]  · 레퍼런스 {len(refs)}개")
    for i, (path, txt) in enumerate(refs):
        mark = "→" if i == _ref_idx.get(_active_key, 0) else " "
        preview = txt[:24] + ("…" if len(txt) > 24 else "")
        print(f"   {mark} {os.path.basename(path)}  ({preview})")

    ensure_loaded(_active_key)   # 수십 초 소요 (시작 캐릭터 로딩 + 워밍업)

    # 합성/재생 워커 시작 후 준비 완료
    worker = threading.Thread(target=_synth_play_worker, daemon=True)
    worker.start()
    _ready.set()
    print("크롬에서 텍스트를 드래그하고 단축키를 누르세요. (대기 중)")

    _quit_event.wait()
    keyboard.unhook_all_hotkeys()
    _interrupt.set()
    sd.stop()
    _req_q.put(None)  # 워커 깨워서 종료
    worker.join(timeout=2)


if __name__ == "__main__":
    main()
