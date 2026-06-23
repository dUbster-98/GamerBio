namespace GamerBio.Models;

public enum TensionState
{
    Calibrating,
    Relaxed,
    Focused,
    Stressed,
}

public record TensionReading(
    TensionState State,
    int Score,
    int BpmScore,
    int GsrScore,
    int LowVariabilityScore,
    DateTimeOffset GeneratedAt
);
