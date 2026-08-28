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

    // --- PI(관류 지수) ---
    // PI에는 절대 스케일이 없다. 같은 사람도 LED 전류·부착 위치·손 온도가 바뀌면
    // 절대값이 통째로 이동하므로, BPM처럼 MapScore(pi, low, high)로 채점하면 안 된다.
    // (GsrAbsLow/High가 지금 겪고 있는 문제가 정확히 그것이다 — 아래 extremeBio 주석 참고)
    // 대신 **개인 baseline 대비 하락률**로 채점한다. 무차원 비율이라 LED 전류·피부색·
    // 밀착도가 약분되고, 그게 애초에 PI를 SpO2 대신 고른 이유이기도 하다.
    //   0% 하락 = 0점,  PiDropMax(50%) 하락 = 100점
    // 문헌상 정신 스트레스 20~40% / 한랭자극 40~70% 하락을 근거로 한 출발점이다.
    // 이 값도 추정이지만 무차원이라 GsrAbsLow/High처럼 단위째로 틀릴 수는 없다.
    private const double PiDropMin = 0.0;
    private const double PiDropMax = 0.5;

    // baseline은 "긴 창의 고분위수"로 잡는다. PI는 평온할 때 가장 높고 수축은 아래로만
    // 가므로 고분위수 ≈ 이완 상태다. 최대값이 아니라 분위수인 이유: 단발 아티팩트 하나가
    // baseline을 부풀리면 그 뒤로 전부 스트레스로 읽힌다.
    //
    // GSR처럼 창의 앞쪽 절반 평균을 쓰지 않는 이유는 드리프트다 — 지속 각성이 baseline을
    // 끌고 올라가면 점수가 0으로 수렴한다. GSR은 절대 수준 점수를 Math.Max로 얹어 때웠지만
    // PI에는 (보정 불가라) 그 탈출구가 없어서 baseline 설계로 해결해야 한다.
    //
    // ⚠️ 남는 한계: 10분 내내 연속 스트레스면 baseline이 따라가 점수가 죽는다. GSR과 같은
    //    종류의 한계지만 시정수가 훨씬 길다(게임 스트레스는 초~분 단위).
    private static readonly TimeSpan PiBaselineWindow = TimeSpan.FromMinutes(10);
    private const double PiBaselinePercentile = 0.85;
    // 이 개수 미만이면 baseline이 의미 없으므로 PI를 융합에서 아예 뺀다
    // (VariabilityMinSamples와 같은 원칙). 2초 주기 기준 30샘플 ≈ 60초.
    private const int PiBaselineMinSamples = 30;
    // PI가 이만큼 끊기면 접촉이 끊긴 것으로 보고 이력을 버린다. 재부착으로 센서 위치가
    // 바뀌면 실제 관류 측정 자체가 달라지므로, 그 경계를 넘어 비교하면 안 된다.
    private static readonly TimeSpan PiContactGap = TimeSpan.FromSeconds(30);

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

    // 멀티모달 융합 가중치 (BPM + 자율신경각성 + 저변동성 + 감정).
    // 입력이 없는 요소는 가중치를 0으로 빼고 남은 것들을 재정규화하므로,
    // 융합 점수는 항상 0~100 스케일을 유지한다:
    //   · 감정     — PC에서 갱신된 값이 없음 (카메라 꺼짐 / 값이 오래됨)
    //   · BPM      — PPG 센서에 피부 접촉이 없음 (BioSignal.Bpm이 null)
    //   · 저변동성 — BPM 시계열에서 계산하므로 BPM을 따라간다
    // GSR은 항상 존재하므로 각성 슬롯이 비는 경우가 없고, 따라서 가중치 합도 0이 되지 않는다.
    //
    // ※ PI는 5번째 요소가 아니라 **GSR과 같은 슬롯**을 쓴다 (WeightArousal). 둘 다 교감신경
    //   각성을 재는 값이고 효과기(땀샘 / 혈관)만 다르므로, 독립 가중치를 주면 각성을 이중
    //   계상하게 된다. 슬롯을 공유하면 기존 가중치를 하나도 건드리지 않아 이미 조정된
    //   Relaxed/Focused/Stressed/Deadly 경계가 그대로 유지된다는 실용적 이점도 있다.
    private const double WeightBpm = 0.35;
    private const double WeightArousal = 0.25;   // GSR + PI 공용 (이전 WeightGsr)
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
    // PI baseline 전용 이력. _window(60샘플 ≈ 2분)를 재사용하지 않는 이유는 PiBaselineWindow가
    // 10분이라 창을 늘려야 하는데, 그러면 GSR baseline과 BPM 변동성 계산까지 같이 바뀌어
    // 이미 조정된 동작이 회귀하기 때문이다. _emotionHistory와 같은 별도 이력 패턴을 따른다.
    private readonly LinkedList<(DateTimeOffset At, double Pi)> _piHistory = new();
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

            TrackPi(sample);

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

    // PI baseline 이력을 갱신한다. 반드시 _lock을 잡은 상태에서 호출해야 한다.
    private void TrackPi(BioSignal sample)
    {
        if (sample.Pi is not double pi || pi <= 0)
        {
            // 접촉 없음 / 박동 미검출. 여기서 이력을 바로 비우지는 않는다 — 한두 샘플
            // 빠지는 것은 흔하고, 그때마다 baseline을 버리면 다시 60초를 기다려야 한다.
            // 실제 폐기는 아래 PiContactGap 판정이 담당한다.
            return;
        }

        // 마지막 기록 이후 PiContactGap 넘게 비어 있었다면 센서를 다시 붙인 것으로 보고
        // 이력을 버린다. 부착 위치가 바뀌면 AC/DC 비율 자체가 달라지므로 그 경계를
        // 넘어선 baseline 비교는 의미가 없다.
        if (_piHistory.Count > 0
            && sample.ReceivedAt - _piHistory.Last!.Value.At > PiContactGap)
        {
            _piHistory.Clear();
        }

        _piHistory.AddLast((sample.ReceivedAt, pi));
        while (_piHistory.Count > 0
            && sample.ReceivedAt - _piHistory.First!.Value.At > PiBaselineWindow)
        {
            _piHistory.RemoveFirst();
        }
    }

    // PiBaselineWindow 안의 PI 값들에서 PiBaselinePercentile 분위수를 뽑는다.
    // 샘플이 부족하면 null — 호출 측이 PI 요소를 융합에서 빼는 신호다.
    private double? PiBaseline(DateTimeOffset now)
    {
        var values = _piHistory
            .Where(x => now - x.At <= PiBaselineWindow)
            .Select(x => x.Pi)
            .ToArray();

        if (values.Length < PiBaselineMinSamples)
        {
            return null;
        }

        Array.Sort(values);
        // 최근접 순위(nearest-rank). 값이 300개 남짓이라 매 갱신마다 정렬해도 부담이 없다.
        int rank = (int)Math.Ceiling(PiBaselinePercentile * values.Length) - 1;
        return values[Math.Clamp(rank, 0, values.Length - 1)];
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
            PiScore = reading.PiScore,
            LowVariabilityScore = reading.LowVariabilityScore,
            EmotionScore = reading.EmotionScore,
            DominantEmotion = reading.DominantEmotion,
            Bpm = _latestBio?.Bpm,
            Gsr = _latestBio?.Gsr ?? 0,
            Pi = _latestBio?.Pi,
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
                TensionState.Calibrating, 0, 0, 0, 0, 0, emotionScore, dominant, at);
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

        // PI: 개인 baseline 대비 **하락률**로 채점한다 (절대값은 쓰지 않는다 — 위 상수 주석 참고).
        // baseline보다 높으면 하락률이 음수가 되고 MapScore가 0으로 clamp한다 — 혈관확장은
        // 스트레스가 아니므로 맞는 동작이다.
        double? piBaseline = PiBaseline(at);
        bool hasPi = sample.Pi is > 0 && piBaseline is > 0;
        int piScore = 0;
        if (hasPi)
        {
            double piDrop = (piBaseline!.Value - sample.Pi!.Value) / piBaseline.Value;
            piScore = MapScore(piDrop, PiDropMin, PiDropMax);
        }

        // GSR과 PI를 하나의 자율신경 각성 점수로 합친다. 둘 다 있으면 **평균**을 쓴다:
        //   · Math.Max는 안 된다. GSR 절대 구간이 아직 미보정이라 헛스파이크가 나는데,
        //     max는 그 잘못된 값이 항상 이기게 만든다.
        //   · 평균이면 정상 PI가 헛스파이크를 끌어내린다 — PI를 도입한 목적(미보정 GSR의
        //     교차 검증)이 바로 이것이다. 반대로 둘이 함께 오르면 서로를 뒷받침한다.
        //   · 혈관은 조용한데 땀샘만 각성했다면 불확실성이 실재하는 것이고, 중간값이 정직하다.
        int arousalScore = hasPi ? (gsrScore + piScore) / 2 : gsrScore;

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
        double weightSum = wBpm + WeightArousal + wLowVariability + wEmotion;
        int composite = (int)Math.Round(
            (bpmScore * wBpm +
             arousalScore * WeightArousal +
             lowVariabilityScore * wLowVariability +
             emotionScore * wEmotion) / weightSum);

        // 승격(override) 조건들 — 가중 평균만 쓰면 평온한(또는 아예 없는) 표정이
        // 실제 위험을 가려버리는 경우들이다. 각 조건은 상태만이 아니라 융합 점수
        // 자체를 끌어올린다. 그래야 대시보드 게이지도 판정과 어긋나지 않는다.
        // 아래 생체 전용 점수도 같은 방식으로 재정규화하되 감정만 뺀다. GSR은 항상
        // 기여하므로 심박이 아예 없어도 분모가 0이 되지 않는다.
        double bioComposite =
            (bpmScore * wBpm + arousalScore * WeightArousal + lowVariabilityScore * wLowVariability)
            / (wBpm + WeightArousal + wLowVariability);
        bool extremeBpm = hasBpm && sample.Bpm.Value >= BpmExtreme;
        // 이 조건은 "센서 전체"를 대변하므로 신호가 하나뿐이어서는 안 된다. 심박도 PI도
        // 없으면 bioComposite는 GSR 점수 하나로 무너지는데, GSR 절대 구간은 아직 실센서
        // 기준으로 보정되지 않았다. 그대로 두면 접촉이 끊길 때마다 Deadly가 뜬다.
        //
        // PI가 있으면 심박이 없어도 이 조건을 허용한다: 각성 점수가 GSR과 PI의 평균이므로
        // 90을 넘으려면 **둘 다** 90 이상이어야 하고, 그건 서로 독립인 두 효과기가 동시에
        // 극단을 가리키는 상황이다. 미보정 GSR 단독으로는 도달할 수 없다.
        bool extremeBio = (hasBpm || hasPi) && bioComposite >= BioOnlyExtreme;
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

        // 가중치는 한 슬롯이지만 GSR/PI 점수는 각각 실어 보낸다 — 대시보드에서 어느 쪽이
        // 말하고 있는지, 둘이 일치하는지가 보여야 미보정 GSR을 판단할 수 있다.
        return new TensionReading(
            state, composite, bpmScore, gsrScore, piScore,
            lowVariabilityScore, emotionScore, dominant, at);
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
