namespace GamerBio.Models;

public enum TensionState
{
    Calibrating,
    Relaxed,
    Focused,
    Stressed,
    Deadly,
}

public record TensionReading(
    TensionState State,
    int Score,
    int BpmScore,
    int GsrScore,
    int LowVariabilityScore,
    int EmotionScore,
    string? DominantEmotion,
    DateTimeOffset GeneratedAt
);
