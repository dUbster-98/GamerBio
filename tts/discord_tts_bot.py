"""
Genie-TTS 디스코드 봇 (슬래시 명령)

기능:
    /say <text>      → 사용자가 들어있는 음성 채널에 입장해 TTS를 재생
    /sayfile <text>  → 합성한 wav를 텍스트 채널에 파일로 전송
    /leave           → 봇이 음성 채널에서 나감

전제:
    1) 같은 PC(또는 도달 가능한 곳)에서 Genie-TTS 서버가 실행 중이어야 함
         python genie_tts_run.py --server      # 127.0.0.1:8000
    2) 서버에 모델/참조오디오가 로드돼 있어야 함
         - 이 봇이 시작 시 자동으로 load_character + set_reference_audio 호출함 (AUTO_LOAD=True)

설치:
    pip install -U "discord.py[voice]" requests
    + FFmpeg 설치 후 PATH 등록 (음성 채널 재생에 필수)

실행:
    set DISCORD_TOKEN=봇토큰        (PowerShell:  $env:DISCORD_TOKEN="봇토큰")
    python discord_tts_bot.py

디스코드 개발자 포털:
    - Bot 생성 후 토큰 발급
    - (자동 읽기 기능은 안 쓰므로 Message Content Intent 불필요)
    - OAuth2 URL 생성 시 scope: bot + applications.commands,
      권한: Connect, Speak, Send Messages, Attach Files
"""

import io
import os
import tempfile

import discord
import requests
from discord import app_commands

# ────────────────────────────────────────────────────────────────────
# CONFIG
# ────────────────────────────────────────────────────────────────────
DISCORD_TOKEN = os.environ.get("DISCORD_TOKEN", "")   # 환경변수 권장
GUILD_ID      = None        # 정수 길드ID 지정 시 슬래시 명령 즉시 반영(개발용). None이면 글로벌(반영 ~1시간)

GENIE_SERVER  = "http://127.0.0.1:8000"

# 서버에 로드할 캐릭터 설정 (genie_tts_run.py CONFIG와 동일하게)
CHARACTER_NAME = "myvoice"
LANGUAGE       = "kr"
ONNX_MODEL_DIR = r"C:\path\to\onnx_out"
REFERENCE_AUDIO_PATH = r"C:\path\to\reference.wav"
REFERENCE_AUDIO_TEXT = "참조 오디오에서 실제로 발화한 문장 그대로"

AUTO_LOAD = True            # 봇 시작 시 서버에 모델/참조오디오 자동 로드
# ────────────────────────────────────────────────────────────────────


# ── Genie-TTS 서버 호출 헬퍼 ────────────────────────────────────────
def genie_load_character():
    requests.post(f"{GENIE_SERVER}/load_character", json={
        "character_name": CHARACTER_NAME,
        "onnx_model_dir": ONNX_MODEL_DIR,
        "language": LANGUAGE,
    }, timeout=120).raise_for_status()

    requests.post(f"{GENIE_SERVER}/set_reference_audio", json={
        "character_name": CHARACTER_NAME,
        "audio_path": REFERENCE_AUDIO_PATH,
        "audio_text": REFERENCE_AUDIO_TEXT,
        "language": LANGUAGE,
    }, timeout=120).raise_for_status()


def genie_tts(text: str) -> bytes:
    """텍스트 → wav 바이트."""
    resp = requests.post(f"{GENIE_SERVER}/tts", json={
        "character_name": CHARACTER_NAME,
        "text": text,
    }, timeout=120)
    resp.raise_for_status()
    return resp.content


# ── 디스코드 봇 ─────────────────────────────────────────────────────
intents = discord.Intents.default()        # 슬래시 명령만 쓰므로 message_content 불필요
client = discord.Client(intents=intents)
tree = app_commands.CommandTree(client)


@client.event
async def on_ready():
    if AUTO_LOAD:
        try:
            genie_load_character()
            print(f">> Genie 서버에 '{CHARACTER_NAME}' 로드 완료")
        except Exception as e:
            print(f"[경고] 모델 로드 실패(서버 실행 중인지 확인): {e}")

    if GUILD_ID:
        guild = discord.Object(id=GUILD_ID)
        tree.copy_global_to(guild=guild)
        await tree.sync(guild=guild)         # 개발용: 즉시 반영
    else:
        await tree.sync()                    # 글로벌: 반영 ~1시간
    print(f">> 로그인: {client.user}  (슬래시 명령 동기화 완료)")


@tree.command(name="say", description="텍스트를 음성으로 합성해 음성 채널에서 재생")
@app_commands.describe(text="읽을 텍스트")
async def say(interaction: discord.Interaction, text: str):
    # 사용자가 음성 채널에 있어야 함
    if not interaction.user.voice or not interaction.user.voice.channel:
        await interaction.response.send_message("먼저 음성 채널에 들어가 주세요.", ephemeral=True)
        return

    await interaction.response.defer(thinking=True)
    channel = interaction.user.voice.channel

    # 합성
    try:
        wav = genie_tts(text)
    except Exception as e:
        await interaction.followup.send(f"합성 실패: {e}")
        return

    # 임시 wav 저장 (FFmpeg 입력용)
    tmp = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
    tmp.write(wav)
    tmp.close()

    # 음성 채널 연결 (이미 연결돼 있으면 이동)
    vc = interaction.guild.voice_client
    if vc is None:
        vc = await channel.connect()
    elif vc.channel != channel:
        await vc.move_to(channel)

    # 재생 중이면 중지 후 재생
    if vc.is_playing():
        vc.stop()

    def _cleanup(err):
        try:
            os.remove(tmp.name)
        except OSError:
            pass

    source = discord.FFmpegPCMAudio(tmp.name)
    vc.play(source, after=_cleanup)
    await interaction.followup.send(f"🔊 재생: {text[:80]}")


@tree.command(name="sayfile", description="텍스트를 음성으로 합성해 wav 파일로 전송")
@app_commands.describe(text="읽을 텍스트")
async def sayfile(interaction: discord.Interaction, text: str):
    await interaction.response.defer(thinking=True)
    try:
        wav = genie_tts(text)
    except Exception as e:
        await interaction.followup.send(f"합성 실패: {e}")
        return

    file = discord.File(io.BytesIO(wav), filename="tts.wav")
    await interaction.followup.send(content=f"🎧 {text[:80]}", file=file)


@tree.command(name="leave", description="봇이 음성 채널에서 나감")
async def leave(interaction: discord.Interaction):
    vc = interaction.guild.voice_client
    if vc:
        await vc.disconnect()
        await interaction.response.send_message("👋 음성 채널에서 나갔습니다.")
    else:
        await interaction.response.send_message("음성 채널에 연결돼 있지 않습니다.", ephemeral=True)


def main():
    if not DISCORD_TOKEN:
        raise SystemExit("DISCORD_TOKEN 환경변수를 설정하세요.")
    client.run(DISCORD_TOKEN)


if __name__ == "__main__":
    main()
