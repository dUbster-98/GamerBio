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

    public BioCommands(TensionAnalyzer analyzer) => _analyzer = analyzer;

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
}
