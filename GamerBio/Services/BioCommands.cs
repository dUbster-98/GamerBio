using Discord.Interactions;

namespace GamerBio.Services;

/// <summary>
/// Slash-command handlers. Each command instance is created per-interaction by
/// Discord.Net's DI, so the singleton <see cref="TensionAnalyzer"/> is injected
/// straight in and we read its latest fused state on demand.
/// </summary>
public class BioCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly TensionAnalyzer _analyzer;
    private readonly RandomPhotoStore _photos;

    public BioCommands(TensionAnalyzer analyzer, RandomPhotoStore photos)
    {
        _analyzer = analyzer;
        _photos = photos;
    }

    [SlashCommand("status", "현재 게이머 텐션 상태를 보여줍니다")]
    public async Task Status()
    {
        var t = _analyzer.Latest();
        var emotion = t.DominantEmotion is null ? "" : $" ({t.DominantEmotion})";
        await RespondAsync(
            $"🎮 **{t.State}** · 텐션 {t.Score}/100\n" +
            $"BPM {t.BpmScore} · GSR {t.GsrScore} · 저변동성 {t.LowVariabilityScore} · 감정 {t.EmotionScore}{emotion}");
    }

    [SlashCommand("bpm", "최근 텐션 상태 기여도 중 심박 점수를 보여줍니다")]
    public async Task Bpm()
    {
        var t = _analyzer.Latest();
        await RespondAsync($"❤️ BPM 기여 점수: {t.BpmScore}/100 (상태: {t.State})");
    }

    [SlashCommand("가챠", "랜덤 사진 한 장을 뽑아줍니다")]
    public async Task Random()
    {
        var path = _photos.PickRandom();
        if (path is null)
        {
            await RespondAsync("📭 가챠 저장소가 비어 있어요.");
            return;
        }

        await RespondWithFileAsync(path, text: $"🎲 오늘의 랜덤 사진: **{Path.GetFileName(path)}**");
    }
}
