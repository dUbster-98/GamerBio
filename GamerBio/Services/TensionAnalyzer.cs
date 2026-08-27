using GamerBio.Models;

namespace GamerBio.Services;

public class TensionAnalyzer
{
    // 생체 샘플 보관 창(최근 60개). 여기서 GSR baseline과 BPM 변동성을 뽑는다.
    private const int WindowSize = 60;
    // 이만큼 샘플이 쌓이기 전에는 판정을 내리지 않고 Calibrating을 반환한다.
    private const int CalibrationSize = 5;
    // 변동성(표준편차)을 계산할 때 참조하는 최근 샘플 개수.
    private const int VariabilityWindow = 30;

    // BPM 점수 매핑 구간: 80 이하 = 0점, 130 이상 = 100점.
    private const double BpmLow = 80;
    private const double BpmHigh = 130;
    // GSR 변화율(baseline 대비 상승률) 매핑 구간: 0% = 0점, +40% = 100점.
    private const double GsrDeltaMin = 0.0;
    private const double GsrDeltaMax = 0.4;
    // GSR 절대 수준 구간. 위의 변화율 점수는 "급등"만 잡아낸다 — 높은 상태가
    // 지속되면 baseline 자체가 따라 올라가 변화율이 0으로 수렴하기 때문이다.
    // 절대 수준을 같이 보면 지속적인 각성이 점수에서 사라지지 않는다.
    // 실제 센서 단위가 확정되면 다시 잡을 것 (더미 데이터 기준 대략 100~900).
    private const double GsrAbsLow = 300;
    private const double GsrAbsHigh = 800;
    // BPM 표준편차 매핑 구간. 낮을수록(=변동이 없을수록) 긴장으로 본다.
    private const double StdDevLow = 2;
    private const double StdDevHigh = 10;

    // 변동성은 시계열이 어느 정도 쌓여야 의미가 있다. 샘플 2~3개짜리 표준편차는
    // 생리 신호가 아니라 그냥 노이즈다. 이 개수 미만이면 지어낸 값을 보고하는
    // 대신 이 요소를 융합에서 아예 빼버린다.
    private const int VariabilityMinSamples = 10;

    // 심장이 뛰고 있는데 평온한 표정이나 낮은 GSR에 평균으로 묻혀서는 안 된다.
    // BpmExtreme 이상이면 융합 점수를 Deadly 구간으로 강제로 끌어올린다.
    private const double BpmExtreme = 160;

    // 센서 전체에 대해서도 같은 원칙: 생체 전용 점수(BPM + GSR + 저변동성,
    // 감정 제외)가 이 수준이면 평온한 표정이 Deadly를 거부할 수 없다 —
    // 극단적인 생리 신호가 이긴다.
    private const double BioOnlyExtreme = 90;

    // 감정 창(window) 기반 승격: 최근 구간의 감정 스트레스 점수(fear/angry ×1.0,
    // surprise ×2.0 — EmotionStressWeight 참고)를 평균 내어, Stressed 수준의
    // 센서 값을 Deadly 선 너머로 밀어 올린다.
    //   bioScore + WeightEmotion × 창평균 ≥ StressedCeiling → Deadly
    // 센서가 이미 Deadly에 가까울수록 필요한 감정 지속 시간이 짧아진다.
    // 최소 샘플 수 조건은 단발 프레임 노이즈가 승격에 끼어드는 것을 막는다.
    private static readonly TimeSpan EmotionWindow = TimeSpan.FromSeconds(30);
    private const int EmotionWindowMinSamples = 5;

    // 멀티모달 융합 가중치 (BPM + GSR + 저변동성 + 감정).
    // 입력이 없는 요소는 가중치를 0으로 빼고 남은 것들을 재정규화하므로,
    // 융합 점수는 항상 0~100 스케일을 유지한다:
    //   · 감정     — PC에서 갱신된 값이 없음 (카메라 꺼짐 / 값이 오래됨)
    //   · BPM      — PPG 센서에 피부 접촉이 없음 (BioSignal.Bpm이 null)
    //   · 저변동성 — BPM 시계열에서 계산하므로 BPM을 따라간다
    // GSR만 항상 존재하므로 가중치 합이 0이 되는 경우는 없다.
    private const double WeightBpm = 0.35;
    private const double WeightGsr = 0.25;
    private const double WeightLowVariability = 0.15;
    private const double WeightEmotion = 0.25;

    // 감정은 최근에 도착한 값일 때만 융합한다. 그렇지 않으면 오래된 값(PC 중단
    // 또는 연결 끊김)으로 보고 생체 전용 점수로 폴백한다.
    private static readonly TimeSpan EmotionFreshness = TimeSpan.FromSeconds(5);

    // 각 감정이 "스트레스" 축을 얼마나 밀어올리는지(0~100). angry/fear가 가장 강한
    // 스트레스 신호이고, happy/neutral은 점수를 낮게 유지한다 (CLAUDE.md 로직 참고).
    private static readonly Dictionary<string, double> EmotionStressWeight = new()
    {
        ["angry"] = 1.0,
        ["fear"] = 1.0,
        ["surprise"] = 2.0,
        ["neutral"] = 0.0,
        ["happy"] = 0.0,
    };

    // 상태 경계값: <30 Relaxed / <65 Focused / <85 Stressed / ≥85 Deadly.
    private const int RelaxedCeiling = 30;
    private const int FocusedCeiling = 65;
    private const int StressedCeiling = 85;

    // Deadly 상태가 유지되는 동안에는 진입 시 1회만이 아니라 이 주기로 계속
    // 기록한다 — 지속된 Deadly 구간이 로그에 끊기지 않고 남도록. 진입 자체는
    // 이 주기와 무관하게 항상 즉시 기록된다.
    private static readonly TimeSpan DeadlyRepeatInterval = TimeSpan.FromSeconds(5);

    private readonly LinkedList<BioSignal> _window = new();
    private readonly LinkedList<(DateTimeOffset At, int Stress)> _emotionHistory = new();
    private readonly object _lock = new();
    private BioSignal? _latestBio;
    private EmotionReading? _latestEmotion;
    private TensionState _lastState = TensionState.Calibrating;
    private DateTimeOffset _lastDeadlyRecordAt = DateTimeOffset.MinValue;

    /// <summary>새 생체 샘플을 가장 최근 감정과 융합한다.
    /// <paramref name="deadlyEntry"/>는 Deadly에 진입할 때, 그리고 Deadly가
    /// 지속되는 동안 <see cref="DeadlyRepeatInterval"/>마다 non-null로 채워지므로
    /// 호출 측이 진행 중인 에피소드를 계속 저장할 수 있다.</summary>
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

    /// <summary>새 샘플을 추가하지 않고 현재 융합 상태만 다시 계산해 반환한다.
    /// Discord /status 명령처럼 읽기 전용 소비자가 사용한다.</summary>
    public TensionReading Latest()
    {
        lock (_lock)
        {
            return Compute(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>새 감정 값을 가장 최근 생체 샘플과 융합한다.
    /// <paramref name="deadlyEntry"/>는 Deadly에 진입할 때, 그리고 Deadly가
    /// 지속되는 동안 <see cref="DeadlyRepeatInterval"/>마다 non-null로 채워지므로
    /// 호출 측이 진행 중인 에피소드를 계속 저장할 수 있다.</summary>
    public TensionReading UpdateEmotion(EmotionReading emotion, out DeadlyEvent? deadlyEntry)
    {
        lock (_lock)
        {
            _latestEmotion = emotion;

            // 창 기반 승격을 위해 감정 스트레스 점수를 이력에 쌓고,
            // EmotionWindow를 벗어난 오래된 항목은 앞에서부터 버린다.
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

    // EmotionWindow 안에 들어오는 값들의 감정 스트레스 점수 평균.
    // 샘플이 너무 적으면 0을 반환한다 — 0은 승격 합계를 절대 선 너머로 밀 수
    // 없으므로, 노이즈나 시작 직후 상태는 아무 영향도 주지 않는다.
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

    // Deadly 기록은 데이터 갱신(UpdateBio/UpdateEmotion)에서만 유발되고 읽기 전용
    // Latest() 호출에서는 절대 유발되지 않는다 — 한가한 /status 조회가 기록을
    // 삼키거나 중복으로 남기지 못하게 하기 위함이다. Deadly 진입 시, 그리고 유지되는
    // 동안 DeadlyRepeatInterval마다 한 번씩 저장 가능한 스냅샷을 반환한다.
    // 반드시 _lock을 잡은 상태에서 호출해야 한다.
    private DeadlyEvent? TrackTransition(TensionReading reading)
    {
        var previous = _lastState;
        _lastState = reading.State;
        if (reading.State != TensionState.Deadly)
        {
            return null;
        }

        // 진입은 즉시 기록하고, Deadly가 유지되는 동안에는 주기로 스로틀해
        // 고빈도 샘플이 로그를 뒤덮지 않게 한다.
        bool entering = previous != TensionState.Deadly;
        if (!entering && reading.GeneratedAt - _lastDeadlyRecordAt < DeadlyRepeatInterval)
        {
            return null;
        }
        _lastDeadlyRecordAt = reading.GeneratedAt;

        return new DeadlyEvent
        {
            OccurredAt = reading.GeneratedAt,
            Score = reading.Score,
            BpmScore = reading.BpmScore,
            GsrScore = reading.GsrScore,
            LowVariabilityScore = reading.LowVariabilityScore,
            EmotionScore = reading.EmotionScore,
            DominantEmotion = reading.DominantEmotion,
            Bpm = _latestBio?.Bpm,
            Gsr = _latestBio?.Gsr ?? 0,
        };
    }

    private TensionReading Compute(DateTimeOffset at)
    {
        // 감정 기여분 (값이 신선할 때만).
        bool emotionFresh = _latestEmotion is not null
            && (DateTimeOffset.UtcNow - _latestEmotion.ReceivedAt) < EmotionFreshness;
        int emotionScore = emotionFresh ? EmotionStress(_latestEmotion!.Scores) : 0;
        string? dominant = emotionFresh ? _latestEmotion!.Dominant : null;

        // 융합 점수가 의미를 가지려면 생체 이력이 어느 정도 쌓여 있어야 한다.
        if (_latestBio is null || _window.Count < CalibrationSize)
        {
            return new TensionReading(
                TensionState.Calibrating, 0, 0, 0, 0, emotionScore, dominant, at);
        }

        var sample = _latestBio;

        // 웨어러블은 PPG 센서에 피부 접촉이 있을 때만 심박을 보고한다. BPM이 없는
        // 것은 BPM이 0인 것과 다르다: 0으로 채점하면 "완전히 평온"으로 읽히고,
        // 더 나쁘게는 변동성 창을 가짜 평탄선으로 오염시킨다. 그래서 0을 넣는
        // 대신 이 요소를 융합에서 빼버린다.
        bool hasBpm = sample.Bpm is not null;
        int bpmScore = hasBpm ? MapScore(sample.Bpm.Value, BpmLow, BpmHigh) : 0;

        // GSR baseline은 창의 앞쪽 절반(=오래된 샘플들)의 평균으로 잡는다.
        int baselineCount = Math.Max(1, _window.Count / 2);
        double gsrBaseline = _window.Take(baselineCount).Average(x => x.Gsr);
        double gsrDelta = gsrBaseline > 0 ? (sample.Gsr - gsrBaseline) / gsrBaseline : 0;
        // 급등(변화율)과 지속 각성(절대 수준) 중 더 크게 말하는 쪽을 채택한다.
        int gsrScore = Math.Max(
            MapScore(gsrDelta, GsrDeltaMin, GsrDeltaMax),
            MapScore(sample.Gsr, GsrAbsLow, GsrAbsHigh));

        // 변동성 시계열에는 실제로 심박이 담긴 샘플만 넣는다 — 접촉이 끊긴 구간을
        // 이어 붙이면 그 공백 자체가 심한 변동으로 읽히고, 결과적으로 스트레스가
        // 오히려 *낮게* 나온다.
        var recent = _window.TakeLast(VariabilityWindow)
            .Where(x => x.Bpm is not null)
            .Select(x => (double)x.Bpm.Value)
            .ToArray();
        bool hasVariability = hasBpm && recent.Length >= VariabilityMinSamples;
        int lowVariabilityScore = 0;
        if (hasVariability)
        {
            double mean = recent.Average();
            double variance = recent.Average(b => (b - mean) * (b - mean));
            // 표준편차가 작을수록(=변동이 없을수록) 긴장이므로 100에서 뺀다.
            lowVariabilityScore = 100 - MapScore(Math.Sqrt(variance), StdDevLow, StdDevHigh);
        }

        // 가중 융합. 이번 회차에 실제로 입력이 있는 요소들만 남겨 재정규화하므로
        // 융합 점수는 0~100 스케일을 유지한다.
        double wBpm = hasBpm ? WeightBpm : 0.0;
        double wLowVariability = hasVariability ? WeightLowVariability : 0.0;
        double wEmotion = emotionFresh ? WeightEmotion : 0.0;
        double weightSum = wBpm + WeightGsr + wLowVariability + wEmotion;
        int composite = (int)Math.Round(
            (bpmScore * wBpm +
             gsrScore * WeightGsr +
             lowVariabilityScore * wLowVariability +
             emotionScore * wEmotion) / weightSum);

        // 승격(override) 조건들 — 가중 평균만 쓰면 평온한(또는 아예 없는) 표정이
        // 실제 위험을 가려버리는 경우들이다. 각 조건은 상태만이 아니라 융합 점수
        // 자체를 끌어올린다. 그래야 대시보드 게이지도 판정과 어긋나지 않는다.
        // 아래 생체 전용 점수도 같은 방식으로 재정규화하되 감정만 뺀다. GSR은 항상
        // 기여하므로 심박이 아예 없어도 분모가 0이 되지 않는다.
        double bioComposite =
            (bpmScore * wBpm + gsrScore * WeightGsr + lowVariabilityScore * wLowVariability)
            / (wBpm + WeightGsr + wLowVariability);
        bool extremeBpm = hasBpm && sample.Bpm.Value >= BpmExtreme;
        // 이 조건은 "센서 전체"를 대변하므로 센서가 하나뿐이어서는 안 된다. 심박이
        // 없으면 bioComposite는 GSR 점수 하나로 무너지는데, GSR 절대 구간은 아직
        // 실센서 기준으로 보정되지 않았다. 그대로 두면 접촉이 끊길 때마다 Deadly가
        // 뜬다.
        bool extremeBio = hasBpm && bioComposite >= BioOnlyExtreme;
        // 센서가 이미 Focused 이상인 상태에서 최근 30초 감정 평균이 더해져
        // Deadly 선을 넘으면 승격 (단발 공포가 아니라 지속된 공포만 반영).
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

    // 감정별 확률 분포를 하나의 0~100 스트레스 점수로 압축한다.
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

    // 값을 [min, max] 구간에 대해 0~100으로 선형 매핑하고 양끝을 잘라낸다.
    private static int MapScore(double value, double min, double max)
    {
        if (max <= min) return 0;
        double t = (value - min) / (max - min);
        return (int)Math.Round(Math.Clamp(t, 0, 1) * 100);
    }
}
