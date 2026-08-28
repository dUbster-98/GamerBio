namespace GamerBio.Models;

public enum TensionState
{
    Calibrating,
    Relaxed,
    Focused,
    Stressed,
    Deadly,
}

/// <summary>Wire format for a fused tension result (SignalR + BioEventBus + Discord).
///
/// GsrScore and PiScore are reported separately even though the fusion feeds them
/// through a single "autonomic arousal" weight slot — they measure the same axis, so
/// weighting them independently would double-count arousal, but a viewer still needs to
/// see which of the two is talking (and whether they agree).</summary>
public record TensionReading(
    TensionState State,
    int Score,
    int BpmScore,
    int GsrScore,
    int PiScore,
    int LowVariabilityScore,
    int EmotionScore,
    string? DominantEmotion,
    DateTimeOffset GeneratedAt
);
