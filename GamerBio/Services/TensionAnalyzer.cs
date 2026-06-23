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

    private const double WeightBpm = 0.5;
    private const double WeightGsr = 0.3;
    private const double WeightLowVariability = 0.2;

    private const int RelaxedCeiling = 30;
    private const int FocusedCeiling = 65;

    private readonly LinkedList<BioSignal> _window = new();
    private readonly object _lock = new();

    public TensionReading Update(BioSignal sample)
    {
        lock (_lock)
        {
            _window.AddLast(sample);
            while (_window.Count > WindowSize)
            {
                _window.RemoveFirst();
            }

            if (_window.Count < CalibrationSize)
            {
                return new TensionReading(TensionState.Calibrating, 0, 0, 0, 0, sample.ReceivedAt);
            }

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

            int composite = (int)Math.Round(
                bpmScore * WeightBpm +
                gsrScore * WeightGsr +
                lowVariabilityScore * WeightLowVariability);

            TensionState state = composite switch
            {
                < RelaxedCeiling => TensionState.Relaxed,
                < FocusedCeiling => TensionState.Focused,
                _ => TensionState.Stressed,
            };

            return new TensionReading(state, composite, bpmScore, gsrScore, lowVariabilityScore, sample.ReceivedAt);
        }
    }

    private static int MapScore(double value, double min, double max)
    {
        if (max <= min) return 0;
        double t = (value - min) / (max - min);
        return (int)Math.Round(Math.Clamp(t, 0, 1) * 100);
    }
}
