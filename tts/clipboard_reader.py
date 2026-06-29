"""
Genie-TTS 단축키 리더 (로컬 데스크탑 전용 / 경량판)

모델을 한 번만 로딩해두고, 크롬 등에서 텍스트를 드래그한 뒤
단축키를 누르면 그 문장을 mika 목소리로 바로 읽어줍니다.

    Ctrl+Alt+R  → 드래그한(선택된) 텍스트 읽기
    Ctrl+Alt+S  → 재생 중지
    Ctrl+Alt+W  → 직전 합성 음성을 파일로 저장
    Ctrl+Alt+Q  → 종료

설치:  pip install genie-tts keyboard pyperclip
실행:  python clipboard_reader.py
"""

import datetime
import os
import re
import shutil
import threading
import time

import keyboard
import pyperclip
import sounddevice as sd

import genie_tts as genie

# ── 설정 (mika.py 와 동일) ───────────────────────────────────────────
CHARACTER_NAME = "mika"
LANGUAGE       = "jp"
ONNX_MODEL_DIR = r"C:\Users\shkim3\Documents\GitHub\GamerBio\tts\CharacterModels\v2ProPlus\mika\tts_models"
REF_AUDIO_PATH = r"C:\Users\shkim3\Documents\GitHub\GamerBio\tts\CharacterModels\v2ProPlus\mika\prompt_wav\917575.wav"
REF_AUDIO_TEXT = "私も昔、これと似たようなの持ってたなぁ…。"

MAX_CHARS = 5000

# 저장 관련: 매 합성 결과를 임시 wav 로 항상 남겨두고, 단축키로 보관함에 복사
_SCRIPT_DIR    = os.path.dirname(os.path.abspath(__file__))
RECORDINGS_DIR = os.path.join(_SCRIPT_DIR, "recordings")   # 저장 폴더
LAST_WAV       = os.path.join(_SCRIPT_DIR, ".last_synth.wav")  # 직전 합성 임시본
# ─────────────────────────────────────────────────────────────────────

_play_lock  = threading.Lock()
_quit_event = threading.Event()
_last_text  = ""   # 직전에 합성한 텍스트 (자동 파일명용)


def _is_virtual(name: str) -> bool:
    """가상/래퍼 장치('사운드 매퍼', '주 사운드 드라이버' 등) 판별."""
    low = name.lower()
    return "주 사운드" in name or "primary" in low or "사운드 매퍼" in name or "mapper" in low


def select_output_device():
    """genie 재생용 출력 장치를 MME 실제 스피커로 지정.

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


def load_model():
    print(">> 모델 로딩 중...")
    genie.load_character(
        character_name=CHARACTER_NAME,
        onnx_model_dir=ONNX_MODEL_DIR,
        language=LANGUAGE,
    )
    genie.set_reference_audio(
        character_name=CHARACTER_NAME,
        audio_path=REF_AUDIO_PATH,
        audio_text=REF_AUDIO_TEXT,
    )
    # 워밍업: 첫 합성에서 일어나는 그래프 초기화를 미리 끝내 첫 단축키를 빠르게
    print(">> 워밍업 중...")
    genie.tts(character_name=CHARACTER_NAME, text="ウォームアップ。", play=False)
    genie.wait_for_playback_done()
    print(">> 준비 완료")


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


def _speak_worker(text: str):
    global _last_text
    with _play_lock:
        try:
            # CPU 추론은 긴 문장에서 수십 초 걸릴 수 있어, 진행 중임을 표시
            print(f">> 합성 중... ({len(text)}자, 잠시 기다려 주세요)")
            t0 = time.time()
            genie.tts(
                character_name=CHARACTER_NAME,
                text=text,
                play=True,
                split_sentence=True,  # 문장 단위 스트리밍 재생 (사이 정적 없음)
                save_path=LAST_WAV,   # 재생과 동시에 임시본 저장 (Ctrl+Alt+W로 보관)
            )
            genie.wait_for_playback_done()
            _last_text = text
            print(f">> 재생 완료 ({time.time() - t0:.1f}s)  · 저장하려면 Ctrl+Alt+W")
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
    text = grab_selection().strip()[:MAX_CHARS]
    if not text:
        print("[안내] 읽을 텍스트가 없습니다 (드래그 후 다시 시도).")
        return
    print(f">> 읽기: {text[:60]}{'...' if len(text) > 60 else ''}")
    genie.stop()  # 이전 재생 중지 후 새로 시작
    threading.Thread(target=_speak_worker, args=(text,), daemon=True).start()


def main():
    select_output_device()
    load_model()
    keyboard.add_hotkey("ctrl+alt+r", on_read)
    keyboard.add_hotkey("ctrl+alt+s", genie.stop)
    keyboard.add_hotkey("ctrl+alt+w", on_save)
    keyboard.add_hotkey("ctrl+alt+q", _quit_event.set)

    print("─" * 46)
    print("  Ctrl+Alt+R  →  드래그한 텍스트 읽기")
    print("  Ctrl+Alt+S  →  재생 중지")
    print("  Ctrl+Alt+W  →  직전 합성 음성 저장")
    print("  Ctrl+Alt+Q  →  종료")
    print("─" * 46)
    print("크롬에서 텍스트를 드래그하고 단축키를 누르세요. (대기 중)")

    _quit_event.wait()
    keyboard.unhook_all_hotkeys()


if __name__ == "__main__":
    main()
