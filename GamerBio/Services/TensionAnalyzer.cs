using GamerBio.Models;

namespace GamerBio.Services;

public class TensionAnalyzer
{
    private const int WindowSize = 60;
    private const int CalibrationSize = 5;
    private const int VariabilityWindow = 30;

    private const double BpmLow = 80;
    private const double BpmHigh = 130;
    private const double GsrDeltaMin = 0.0;
    private const double GsrDeltaMax = 0.4;
    private const double StdDevLow = 2;
    private const double StdDevHigh = 10;

    // Multimodal fusion weights (BPM + GSR + low-variability + emotion).
    // When no fresh emotion is available, the emotion weight is dropped and the
    // remaining three are renormalized, so the system degrades to bio-only.
    private const double WeightBpm = 0.35;
    private const double WeightGsr = 0.25;
    private const double WeightLowVariability = 0.15;
    private const double WeightEmotion = 0.25;

    // Emotion is only fused if it arrived recently; otherwise it's stale (PC
    // paused / disconnected) and we fall back to biosignal-only scoring.
    private static readonly TimeSpan EmotionFreshness = TimeSpan.FromSeconds(10);

    // How much each emotion pushes the "stress" axis (0-100). angry/fear are the
    // strongest stress signals; happy/neutral keep it low (see CLAUDE.md logic).
    private static readonly Dictionary<string, double> EmotionStressWeight = new()
    {
        ["angry"] = 1.0,
        ["fear"] = 1.0,
        ["surprise"] = 0.5,
        ["neutral"] = 0.0,
        ["happy"] = 0.0,
    };

    private const int RelaxedCeiling = 30;
    private const int FocusedCeiling = 65;

    private readonly LinkedList<BioSignal> _window = new();
    private readonly object _lock = new();
    private BioSignal? _latestBio;
    private EmotionReading? _latestEmotion;

    /// <summary>Fuse a new biosignal sample with the latest known emotion.</summary>
    public TensionReading UpdateBio(BioSignal sample)
    {
        lock (_lock)
        {
            _latestBio = sample;
            _window.AddLast(sample);
            while (_window.Count > WindowSize)
            {
                _window.RemoveFirst();
            }

            return Compute(sample.ReceivedAt);
        }
    }

    /// <summary>Fuse a new emotion reading with the latest known biosignal.</summary>
    public TensionReading UpdateEmotion(EmotionReading emotion)
    {
        lock (_lock)
        {
            _latestEmotion = emotion;
            return Compute(emotion.ReceivedAt);
        }
    }

    private TensionReading Compute(DateTimeOffset at)
    {
        // Emotion contribution (only if fresh).
        bool emotionFresh = _latestEmotion is not null
            && (DateTimeOffset.UtcNow - _latestEmotion.ReceivedAt) < EmotionFreshness;
        int emotionScore = emotionFresh ? EmotionStress(_latestEmotion!.Scores) : 0;
        string? dominant = emotionFresh ? _latestEmotion!.Dominant : null;

        // Need enough biosignal history before the composite is meaningful.
        if (_latestBio is null || _window.Count < CalibrationSize)
        {
            return new TensionReading(
                TensionState.Calibrating, 0, 0, 0, 0, emotionScore, dominant, at);
        }

        var sample = _latestBio;

        int bpmScore = MapScore(sample.Bpm, BpmLow, BpmHigh);

        int baselineCount = Math.Max(1, _window.Count / 2);
        double gsrBaseline = _window.Take(baselineCount).Average(x => x.Gsr);
        double gsrDelta = gsrBaseline > 0 ? (sample.Gsr - gsrBaseline) / gsrBaseline : 0;
        int gsrScore = MapScore(gsrDelta, GsrDeltaMin, GsrDeltaMax);

        var recent = _window.TakeLast(VariabilityWindow).Select(x => (double)x.Bpm).ToArray();
        double mean = recent.Average();
        double variance = recent.Average(b => (b - mean) * (b - mean));
        double stdDev = Math.Sqrt(variance);
        int lowVariabilityScore = 100 - MapScore(stdDev, StdDevLow, StdDevHigh);

        // Weighted fusion; renormalize so the composite stays on a 0-100 scale
        // whether or not emotion is part of the mix this round.
        double wEmotion = emotionFresh ? WeightEmotion : 0.0;
        double weightSum = WeightBpm + WeightGsr + WeightLowVariability + wEmotion;
        int composite = (int)Math.Round(
            (bpmScore * WeightBpm +
             gsrScore * WeightGsr +
             lowVariabilityScore * WeightLowVariability +
             emotionScore * wEmotion) / weightSum);

        TensionState state = composite switch
        {
            < RelaxedCeiling => TensionState.Relaxed,
            < FocusedCeiling => TensionState.Focused,
            _ => TensionState.Stressed,
        };

        return new TensionReading(
            state, composite, bpmScore, gsrScore, lowVariabilityScore, emotionScore, dominant, at);
    }

    // Collapse the per-emotion probabilities into a single 0-100 stress score.
    private static int EmotionStress(IReadOnlyDictionary<string, double> scores)
    {
        double stress = 0;
        foreach (var (emotion, weight) in EmotionStressWeight)
        {
            if (scores.TryGetValue(emotion, out var prob))
            {
                stress += prob * weight;
            }
        }
        return (int)Math.Round(Math.Clamp(stress, 0, 100));
    }

    private static int MapScore(double value, double min, double max)
    {
        if (max <= min) return 0;
        double t = (value - min) / (max - min);
        return (int)Math.Round(Math.Clamp(t, 0, 1) * 100);
    }
}
