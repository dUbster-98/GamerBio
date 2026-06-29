"""
Genie-TTS 실행 스크립트 (GPT-SoVITS V2ProPlus 모델용)

사용 전:
    pip install genie-tts torch

아래 CONFIG 경로만 본인 환경에 맞게 채운 뒤 실행하세요.
    python genie_tts_run.py            # 합성 (변환된 ONNX 사용)
    python genie_tts_run.py --convert  # .pth/.ckpt -> ONNX 변환만 수행
    python genie_tts_run.py --demo     # 내장 캐릭터(mika)로 설치 동작 확인
    python genie_tts_run.py --server    # FastAPI 추론 서버 실행
"""

import argparse
import os
import sys

# ────────────────────────────────────────────────────────────────────
# CONFIG ─ 본인 환경에 맞게 수정
# ────────────────────────────────────────────────────────────────────
CHARACTER_NAME = "myvoice"
LANGUAGE       = "kr"          # 'kr', 'en', 'jp', 'zh'

# GPT-SoVITS 학습 결과물 (V2ProPlus)
TORCH_PTH_PATH  = r"C:\path\to\SoVITS_weights\내모델.pth"    # SoVITS weights
TORCH_CKPT_PATH = r"C:\path\to\GPT_weights\내모델.ckpt"      # GPT weights

# 변환된 ONNX 모델이 저장/로드될 폴더
ONNX_MODEL_DIR  = r"C:\path\to\onnx_out"

# 참조 오디오 (감정·억양 클로닝용, 3~10초 깨끗한 샘플)
REFERENCE_AUDIO_PATH = r"C:\path\to\reference.wav"
REFERENCE_AUDIO_TEXT = "참조 오디오에서 실제로 발화한 문장 그대로"   # 정확히 일치해야 품질 ↑

# 합성할 텍스트 / 출력 파일
TEXT_TO_SYNTHESIZE = "안녕하세요, 지니 TTS 합성 테스트입니다."
OUTPUT_PATH        = r"C:\path\to\output.wav"

# (선택) 미리 받은 리소스 폴더가 있으면 지정. 없으면 None → 첫 실행 시 자동 다운로드(약 391MB)
GENIE_DATA_DIR = None
# ────────────────────────────────────────────────────────────────────


def _check_path(path, label):
    if not os.path.exists(path):
        print(f"[경고] {label} 경로를 찾을 수 없습니다: {path}")
        return False
    return True


def convert():
    """GPT-SoVITS .pth/.ckpt -> ONNX 변환 (최초 1회)."""
    import genie_tts as genie

    print(">> ONNX 변환 시작...")
    if not _check_path(TORCH_PTH_PATH, ".pth"):
        sys.exit(1)
    if not _check_path(TORCH_CKPT_PATH, ".ckpt"):
        sys.exit(1)
    os.makedirs(ONNX_MODEL_DIR, exist_ok=True)

    genie.convert_to_onnx(
        torch_pth_path=TORCH_PTH_PATH,
        torch_ckpt_path=TORCH_CKPT_PATH,
        output_dir=ONNX_MODEL_DIR,
    )
    print(f">> 변환 완료 → {ONNX_MODEL_DIR}")


def synthesize():
    """변환된 ONNX 모델을 로드해 음성 합성."""
    import genie_tts as genie

    if not os.path.isdir(ONNX_MODEL_DIR) or not os.listdir(ONNX_MODEL_DIR):
        print(f"[안내] ONNX 모델이 없습니다. 먼저 변환을 실행하세요:\n"
              f"        python {os.path.basename(__file__)} --convert")
        sys.exit(1)

    # (1) 캐릭터(목소리) 로드
    genie.load_character(
        character_name=CHARACTER_NAME,
        onnx_model_dir=ONNX_MODEL_DIR,
        language=LANGUAGE,
    )

    # (2) 참조 오디오 설정 (감정/억양 클로닝)
    if _check_path(REFERENCE_AUDIO_PATH, "참조 오디오"):
        genie.set_reference_audio(
            character_name=CHARACTER_NAME,
            audio_path=REFERENCE_AUDIO_PATH,
            audio_text=REFERENCE_AUDIO_TEXT,
        )

    # (3) 합성
    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    genie.tts(
        character_name=CHARACTER_NAME,
        text=TEXT_TO_SYNTHESIZE,
        play=True,
        save_path=OUTPUT_PATH,
    )
    genie.wait_for_playback_done()
    print(f">> 합성 완료 → {OUTPUT_PATH}")


def demo():
    """내장 캐릭터로 설치/오디오 출력 정상 동작 확인."""
    import genie_tts as genie

    genie.load_predefined_character("mika")
    genie.tts(
        character_name="mika",
        text="どうしようかな……やっぱりやりたいかも……！",
        play=True,
    )
    genie.wait_for_playback_done()
    print(">> 데모 재생 완료")


def server():
    """FastAPI 추론 서버 실행."""
    import genie_tts as genie

    genie.start_server(host="0.0.0.0", port=8000, workers=1)


def main():
    parser = argparse.ArgumentParser(description="Genie-TTS 실행 스크립트")
    parser.add_argument("--convert", action="store_true", help=".pth/.ckpt → ONNX 변환만 수행")
    parser.add_argument("--demo", action="store_true", help="내장 캐릭터로 설치 확인")
    parser.add_argument("--server", action="store_true", help="추론 서버 실행")
    args = parser.parse_args()

    if GENIE_DATA_DIR:
        os.environ["GENIE_DATA_DIR"] = GENIE_DATA_DIR  # import 전에 설정해야 함

    if args.convert:
        convert()
    elif args.demo:
        demo()
    elif args.server:
        server()
    else:
        synthesize()


if __name__ == "__main__":
    main()
