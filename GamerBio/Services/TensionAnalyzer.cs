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
    // Absolute GSR band: the delta score above only catches surges (a sustained
    // high level pushes its own baseline up until the delta reads 0), so the
    // absolute level keeps sustained arousal visible. Tune once real sensor
    // units are known (dummy data runs ~100-900).
    private const double GsrAbsLow = 300;
    private const double GsrAbsHigh = 800;
    private const double StdDevLow = 2;
    private const double StdDevHigh = 10;

    // A racing heart must not be averaged away by a calm face or flat GSR:
    // at BpmExtreme+ the composite is floored straight into Deadly territory.
    private const double BpmExtreme = 160;

    // Likewise for the sensors as a whole: when the bio-only composite (BPM +
    // GSR + low-variability, emotion excluded) is this high, a calm face may
    // not veto Deadly — extreme physiology wins.
    private const double BioOnlyExtreme = 90;

    // Windowed-emotion escalation: average the emotion stress score (fear /
    // angry ×1.0, surprise ×2.0 — see EmotionStressWeight) over the recent
    // window and let it top up Stressed-level sensors across the Deadly line:
    //   bioScore + WeightEmotion × windowAvg ≥ StressedCeiling → Deadly.
    // The closer the sensors already are to Deadly, the less sustained emotion
    // is needed. A minimum sample count keeps single-frame noise from counting.
    private static readonly TimeSpan EmotionWindow = TimeSpan.FromSeconds(30);
    private const int EmotionWindowMinSamples = 5;

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
        ["surprise"] = 2.0,
        ["neutral"] = 0.0,
        ["happy"] = 0.0,
    };

    private const int RelaxedCeiling = 30;
    private const int FocusedCeiling = 65;
    private const int StressedCeiling = 85;

    private readonly LinkedList<BioSignal> _window = new();
    private readonly LinkedList<(DateTimeOffset At, int Stress)> _emotionHistory = new();
    private readonly object _lock = new();
    private BioSignal? _latestBio;
    private EmotionReading? _latestEmotion;
    private TensionState _lastState = TensionState.Calibrating;

    /// <summary>Fuse a new biosignal sample with the latest known emotion.
    /// <paramref name="deadlyEntry"/> is non-null only when this update moved
    /// the state INTO Deadly, so the caller can persist the moment.</summary>
    public TensionReading UpdateBio(BioSignal sample, out DeadlyEvent? deadlyEntry)
    {
        lock (_lock)
        {
            _latestBio = sample;
            _window.AddLast(sample);
            while (_window.Count > WindowSize)
            {
                _window.RemoveFirst();
            }

            var reading = Compute(sample.ReceivedAt);
            deadlyEntry = TrackTransition(reading);
            return reading;
        }
    }

    /// <summary>Recompute and return the current fused state without adding a
    /// new sample. Used by read-only consumers like the Discord /status command.</summary>
    public TensionReading Latest()
    {
        lock (_lock)
        {
            return Compute(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Fuse a new emotion reading with the latest known biosignal.
    /// <paramref name="deadlyEntry"/> is non-null only when this update moved
    /// the state INTO Deadly, so the caller can persist the moment.</summary>
    public TensionReading UpdateEmotion(EmotionReading emotion, out DeadlyEvent? deadlyEntry)
    {
        lock (_lock)
        {
            _latestEmotion = emotion;

            _emotionHistory.AddLast((emotion.ReceivedAt, EmotionStress(emotion.Scores)));
            while (_emotionHistory.Count > 0
                && emotion.ReceivedAt - _emotionHistory.First!.Value.At > EmotionWindow)
            {
                _emotionHistory.RemoveFirst();
            }

            var reading = Compute(emotion.ReceivedAt);
            deadlyEntry = TrackTransition(reading);
            return reading;
        }
    }

    // Average emotion stress score over the readings inside EmotionWindow.
    // Returns 0 when there are too few samples — 0 can never push the
    // escalation sum over the line, so noise or a cold start stays inert.
    private double WindowedEmotionStress(DateTimeOffset now)
    {
        int total = 0;
        double sum = 0;
        foreach (var (at, stress) in _emotionHistory)
        {
            if (now - at > EmotionWindow)
            {
                continue;
            }
            total++;
            sum += stress;
        }
        return total >= EmotionWindowMinSamples ? sum / total : 0;
    }

    // State transitions are tracked only from data updates (UpdateBio/UpdateEmotion),
    // never from read-only Latest() calls, so an idle /status query can't swallow
    // or duplicate a Deadly entry. Returns a persistable snapshot on entry.
    // Must be called while holding _lock.
    private DeadlyEvent? TrackTransition(TensionReading reading)
    {
        var previous = _lastState;
        _lastState = reading.State;
        if (reading.State != TensionState.Deadly || previous == TensionState.Deadly)
        {
            return null;
        }

        return new DeadlyEvent
        {
            OccurredAt = reading.GeneratedAt,
            Score = reading.Score,
            BpmScore = reading.BpmScore,
            GsrScore = reading.GsrScore,
            LowVariabilityScore = reading.LowVariabilityScore,
            EmotionScore = reading.EmotionScore,
            DominantEmotion = reading.DominantEmotion,
            Bpm = _latestBio?.Bpm ?? 0,
            Gsr = _latestBio?.Gsr ?? 0,
        };
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
        // Surge (delta) OR sustained arousal (absolute level), whichever is louder.
        int gsrScore = Math.Max(
            MapScore(gsrDelta, GsrDeltaMin, GsrDeltaMax),
            MapScore(sample.Gsr, GsrAbsLow, GsrAbsHigh));

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

        // Escalation overrides — cases where the weighted average would let a
        // calm face (or a missing one) mask real danger. Each raises the
        // composite itself (not just the state) so the dashboard gauge agrees.
        double bioComposite =
            (bpmScore * WeightBpm + gsrScore * WeightGsr + lowVariabilityScore * WeightLowVariability)
            / (WeightBpm + WeightGsr + WeightLowVariability);
        bool extremeBpm = sample.Bpm >= BpmExtreme;
        bool extremeBio = bioComposite >= BioOnlyExtreme;
        bool sustainedEmotion = bioComposite >= FocusedCeiling
            && bioComposite + WeightEmotion * WindowedEmotionStress(DateTimeOffset.UtcNow)
               >= StressedCeiling;
        if (extremeBpm || extremeBio || sustainedEmotion)
        {
            composite = Math.Max(composite, StressedCeiling);
        }

        TensionState state = composite switch
        {
            < RelaxedCeiling => TensionState.Relaxed,
            < FocusedCeiling => TensionState.Focused,
            < StressedCeiling => TensionState.Stressed,
            _ => TensionState.Deadly,
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
