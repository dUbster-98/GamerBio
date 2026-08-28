namespace GamerBio.Models;

/// <summary>
/// Snapshot persisted whenever the fused tension crosses INTO the Deadly state
/// (transition-only, so one row per surge). Captures both the fusion output
/// (per-factor scores) and the raw vitals at that moment for later analysis.
/// </summary>
public class DeadlyEvent
{
    public long Id { get; set; }

    /// <summary>When the reading that entered Deadly was generated.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Fused composite tension score (0-100).</summary>
    public int Score { get; set; }

    public int BpmScore { get; set; }
    public int GsrScore { get; set; }
    public int PiScore { get; set; }
    public int LowVariabilityScore { get; set; }
    public int EmotionScore { get; set; }
    public string? DominantEmotion { get; set; }

    /// <summary>Raw vitals from the latest biosignal at the time of entry.
    /// Bpm and Pi are null when the PPG sensor had no usable signal at that moment.
    /// Pi is stored alongside PiScore because the score is relative to a rolling
    /// baseline that is gone by the time anyone reads the row back — without the raw
    /// value the score cannot be re-derived or sanity-checked later.</summary>
    public int? Bpm { get; set; }
    public int Gsr { get; set; }
    public double? Pi { get; set; }
}
