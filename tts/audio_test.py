"""
오디오 출력 진단 스크립트.

genie 와 동일한 방식(sd.OutputStream + write, 32000Hz)으로 출력 가능한
각 장치에 0.8초짜리 '삐-' 톤을 차례로 내보냅니다.
어느 [번호]에서 소리가 들렸는지 확인해 genie 재생 장치를 그걸로 맞추면 됩니다.

실행:  python audio_test.py
"""

import time

import numpy as np
import sounddevice as sd

SR = 32000
tone = (0.25 * np.sin(2 * np.pi * 440 * np.arange(int(SR * 0.8)) / SR)).astype("float32")

devs = sd.query_devices()
apis = sd.query_hostapis()

print(f"시스템 기본 출력: {sd.default.device}")
print("─" * 50)

for i, d in enumerate(devs):
    if d["max_output_channels"] <= 0:
        continue
    api = apis[d["hostapi"]]["name"]
    if api not in ("MME", "Windows DirectSound", "Windows WASAPI"):
        continue
    print(f"[{i:2d}] {d['name']}  ({api})", flush=True)
    try:
        with sd.OutputStream(samplerate=SR, channels=1, dtype="float32", device=i) as s:
            s.write(tone)
        time.sleep(0.3)
    except Exception as e:
        print(f"     건너뜀: {str(e)[:60]}")

print("─" * 50)
print("소리가 들린 [번호]를 알려주세요.")
