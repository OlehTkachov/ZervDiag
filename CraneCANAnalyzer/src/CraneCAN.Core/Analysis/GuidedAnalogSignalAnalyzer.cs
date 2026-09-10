using System.Globalization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public enum AnalogFieldEncoding
{
    ByteUnsigned,
    UInt16LittleEndian,
    UInt16BigEndian
}

public enum AnalogCandidatePriority
{
    Low,
    Medium,
    High
}

public enum AnalogDirection
{
    Increasing,
    Decreasing
}

public sealed record GuidedAnalogRepeatMetrics(
    int RepeatNumber,
    double ReferenceMedian,
    double ReferenceNoise,
    double ActionStartMedian,
    double ActionEndMedian,
    double BaselineShift,
    double ActionSpan,
    AnalogDirection Direction,
    double MonotonicityPercent,
    double TimeCorrelation,
    double ResponseToNoiseRatio,
    double RatePerSecond,
    int DistinctActionValues,
    double EndpointPlateauPercent,
    bool? ReturnedToBaseline,
    double? ReactionMilliseconds);

public sealed record GuidedAnalogCandidate
{
    public string ActionName { get; init; } = string.Empty;
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public int StartByte { get; init; }
    public AnalogFieldEncoding Encoding { get; init; }
    public AnalogDirection Direction { get; init; }
    public AnalogCandidatePriority Priority { get; init; }
    public int Score { get; init; }
    public int RepeatabilityCount { get; init; }
    public int RepeatCount { get; init; }
    public double MedianBaselineShift { get; init; }
    public double MedianActionSpan { get; init; }
    public double MedianReferenceNoise { get; init; }
    public double MedianMonotonicityPercent { get; init; }
    public double MedianAbsoluteTimeCorrelation { get; init; }
    public double MedianResponseToNoiseRatio { get; init; }
    public double MedianRatePerSecond { get; init; }
    public double MedianEndpointPlateauPercent { get; init; }
    public int ReturnToBaselineCount { get; init; }
    public int ReturnObservedCount { get; init; }
    public double? MedianReactionMilliseconds { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<GuidedAnalogRepeatMetrics> Repeats { get; init; } = [];

    public int BitLength => Encoding == AnalogFieldEncoding.ByteUnsigned ? 8 : 16;
    public SignalByteOrder ByteOrder => Encoding == AnalogFieldEncoding.UInt16BigEndian
        ? SignalByteOrder.BigEndian
        : SignalByteOrder.LittleEndian;

    // DBC/Motorola sawtooth convention: a byte-aligned 16-bit BigEndian field
    // starts at MSB bit 7. LittleEndian/U8 start at LSB bit 0.
    public int StartBit => Encoding == AnalogFieldEncoding.UInt16BigEndian ? 7 : 0;

    public string StableKey =>
        $"{Id:X8}:{(IsExtended ? 'E' : 'S')}:{StartByte}:{Encoding}";
}

public sealed record GuidedAnalogAnalysisResult(
    string ActionName,
    int RepeatCount,
    IReadOnlyList<GuidedAnalogCandidate> Candidates,
    IReadOnlyList<string> Warnings)
{
    public int HighCount => Candidates.Count(item => item.Priority == AnalogCandidatePriority.High);
    public int MediumCount => Candidates.Count(item => item.Priority == AnalogCandidatePriority.Medium);
    public int LowCount => Candidates.Count(item => item.Priority == AnalogCandidatePriority.Low);
}

/// <summary>
/// Passive raw-field search for sensor-like CAN values in Guided REFERENCE/ACTION runs.
/// It examines unsigned U8 and byte-aligned unsigned U16 LE/BE fields only.
/// Correlation is against elapsed ACTION time and is a ranking hint, not proof of causality.
/// No CAN transmit path exists in this analyzer.
/// </summary>
public static class GuidedAnalogSignalAnalyzer
{
    private const int MinimumReferenceSamples = 4;
    private const int MinimumActionSamples = 6;
    private const int MinimumDistinctActionValues = 5;

    public static GuidedAnalogAnalysisResult Analyze(
        string actionName,
        IReadOnlyList<GuidedExperimentRun> runs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            throw new InvalidOperationException("Для поиска аналоговых сигналов нужен хотя бы один Guided repeat.");

        var orderedRuns = runs.OrderBy(run => run.RepeatNumber).ToArray();
        var warnings = new List<string>
        {
            "Поиск проверяет raw unsigned U8/U16 поля. Signedness, scale, offset, unit и физический смысл автоматически не назначаются.",
            "Корреляция вычисляется с временем внутри ACTION и используется только для ранжирования. Она не доказывает причинную связь с действием оператора.",
            "Extended CAN анализируется по точному 29-bit ID. Если Source Address J1939 меняется, используйте отдельные PGN-нормализованные инструменты J1939.",
            "Результат получен пассивно из уже записанных Rx CAN кадров. CAN Tx отсутствует."
        };
        if (orderedRuns.Length < 3)
            warnings.Add("Повторов меньше трёх: HIGH-кандидат не формируется; для полевого подтверждения рекомендуется минимум 3 повтора.");

        var observations = new List<FieldObservation>();
        foreach (var run in orderedRuns)
        {
            var reference = IndexFrames(run.ReferenceFrames);
            var action = IndexFrames(run.ActionFrames);
            var returned = run.ReturnFrames is null ? null : IndexFrames(run.ReturnFrames);

            foreach (var key in reference.Keys.Intersect(action.Keys)
                         .OrderBy(key => key.Id)
                         .ThenBy(key => key.IsExtended))
            {
                var referenceFrames = reference[key];
                var actionFrames = action[key];
                var returnFrames = returned is not null && returned.TryGetValue(key, out var foundReturn)
                    ? foundReturn
                    : null;

                var maximumLength = Math.Min(
                    referenceFrames.Max(frame => frame.Data.Length),
                    actionFrames.Max(frame => frame.Data.Length));
                maximumLength = Math.Min(maximumLength, 8);

                for (var startByte = 0; startByte < maximumLength; startByte++)
                {
                    AddIfResponsive(observations, run, key, referenceFrames, actionFrames, returnFrames,
                        startByte, AnalogFieldEncoding.ByteUnsigned);
                    if (startByte + 1 < maximumLength)
                    {
                        AddIfResponsive(observations, run, key, referenceFrames, actionFrames, returnFrames,
                            startByte, AnalogFieldEncoding.UInt16LittleEndian);
                        AddIfResponsive(observations, run, key, referenceFrames, actionFrames, returnFrames,
                            startByte, AnalogFieldEncoding.UInt16BigEndian);
                    }
                }
            }
        }

        var candidates = observations
            .GroupBy(item => item.StableKey, StringComparer.Ordinal)
            .Select(group => Aggregate(actionName, group.ToArray(), orderedRuns.Length))
            .Where(candidate => candidate.Score >= 25)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.RepeatabilityCount)
            .ThenBy(candidate => candidate.Id)
            .ThenBy(candidate => candidate.IsExtended)
            .ThenBy(candidate => candidate.StartByte)
            .ThenBy(candidate => candidate.Encoding)
            .ToArray();

        if (candidates.Length == 0)
            warnings.Add("Устойчивых sensor-like raw полей по заданным критериям не найдено. Проверьте окна REFERENCE/ACTION и повторите физическое действие.");

        return new GuidedAnalogAnalysisResult(
            actionName.Trim(),
            orderedRuns.Length,
            candidates,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static MachineSignal CreateMachineSignal(
        GuidedAnalogCandidate candidate,
        Guid experimentId,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var evidenceDescription =
            $"Guided analog raw candidate {candidate.StableKey}; action={candidate.ActionName}; " +
            $"repeatability={candidate.RepeatabilityCount}/{candidate.RepeatCount}; " +
            $"direction={candidate.Direction}; score={candidate.Score}; " +
            $"median monotonicity={candidate.MedianMonotonicityPercent:0.#}%; " +
            $"|corr(time)|={candidate.MedianAbsoluteTimeCorrelation:0.###}; " +
            $"response/noise={candidate.MedianResponseToNoiseRatio:0.###}. " +
            "Physical meaning, signedness, scale, offset and unit require independent verification.";

        return new MachineSignal
        {
            Name = $"Analog candidate {candidate.Id:X8} DATA[{candidate.StartByte}]",
            Description =
                "Автоматически найденное raw sensor-like поле Guided REFERENCE/ACTION. " +
                "Физический смысл и инженерное масштабирование не подтверждены.",
            CanId = candidate.Id,
            IsExtended = candidate.IsExtended,
            StartByte = candidate.StartByte,
            StartBit = candidate.StartBit,
            BitLength = candidate.BitLength,
            ByteOrder = candidate.ByteOrder,
            IsSigned = false,
            Scale = 1,
            Offset = 0,
            Unit = string.Empty,
            Protocol = "CAN",
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    ExperimentId = experimentId,
                    ExperimentPath = null,
                    CaptureOrigin = "guided-analog-search",
                    Kind = EvidenceKind.RepeatedExperiment,
                    Description = evidenceDescription,
                    SourceReference = $"guided-analog:{experimentId:N}:{candidate.StableKey}",
                    RecordedAt = recordedAt
                }
            ],
            Source = "Guided Analog Signal Search",
            Notes =
                "Raw unsigned candidate. До независимой проверки не назначать физическую величину. " +
                "Scale=1, Offset=0, Unit пустой; CAN Tx отсутствует.",
            CreatedAt = recordedAt,
            UpdatedAt = recordedAt
        };
    }

    private static Dictionary<(uint Id, bool IsExtended), CanFrame[]> IndexFrames(
        IEnumerable<CanFrame> frames) =>
        frames
            .Where(IsUsable)
            .OrderBy(frame => frame.Timestamp)
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => group.ToArray());

    private static void AddIfResponsive(
        ICollection<FieldObservation> output,
        GuidedExperimentRun run,
        (uint Id, bool IsExtended) key,
        IReadOnlyList<CanFrame> referenceFrames,
        IReadOnlyList<CanFrame> actionFrames,
        IReadOnlyList<CanFrame>? returnFrames,
        int startByte,
        AnalogFieldEncoding encoding)
    {
        var requiredLength = encoding == AnalogFieldEncoding.ByteUnsigned
            ? startByte + 1
            : startByte + 2;
        var reference = Decode(referenceFrames, startByte, encoding, requiredLength);
        var action = Decode(actionFrames, startByte, encoding, requiredLength);
        if (reference.Length < MinimumReferenceSamples || action.Length < MinimumActionSamples)
            return;

        var metrics = AnalyzeRepeat(run, reference, action, returnFrames, startByte, encoding, requiredLength);
        if (metrics is null)
            return;

        output.Add(new FieldObservation(key.Id, key.IsExtended, startByte, encoding, metrics));
    }

    private static GuidedAnalogRepeatMetrics? AnalyzeRepeat(
        GuidedExperimentRun run,
        IReadOnlyList<ValueSample> reference,
        IReadOnlyList<ValueSample> action,
        IReadOnlyList<CanFrame>? returnFrames,
        int startByte,
        AnalogFieldEncoding encoding,
        int requiredLength)
    {
        var referenceValues = reference.Select(sample => sample.Value).ToArray();
        var actionValues = action.Select(sample => sample.Value).ToArray();
        var referenceMedian = Median(referenceValues);
        var referenceNoise = RobustNoise(referenceValues);

        var quartileCount = Math.Max(2, actionValues.Length / 4);
        var actionStartMedian = Median(actionValues.Take(quartileCount));
        var actionEndMedian = Median(actionValues.Skip(actionValues.Length - quartileCount));
        var baselineShift = actionEndMedian - referenceMedian;
        var direction = baselineShift >= 0 ? AnalogDirection.Increasing : AnalogDirection.Decreasing;
        var actionSpan = Percentile(actionValues, 0.95) - Percentile(actionValues, 0.05);
        var noiseDenominator = Math.Max(1.0, referenceNoise);
        var responseToNoise = Math.Abs(baselineShift) / noiseDenominator;
        var distinct = actionValues.Distinct().Count();
        var deadband = Math.Max(1.0, referenceNoise * 0.5);
        var monotonicity = Monotonicity(actionValues, direction, deadband);
        var correlation = PearsonTimeCorrelation(action);
        var rate = RatePerSecond(action);
        var plateau = EndpointPlateauPercent(actionValues, direction, referenceNoise);
        var returned = AnalyzeReturn(returnFrames, startByte, encoding, requiredLength,
            referenceMedian, referenceNoise, Math.Abs(baselineShift));
        var reaction = FindReaction(action, run.ApproximateEventTime, referenceMedian,
            direction, Math.Max(2.0, referenceNoise * 3.0));

        var effectThreshold = Math.Max(4.0, referenceNoise * 3.0);
        var trendEnough = monotonicity >= 60 || Math.Abs(correlation) >= 0.60;
        if (distinct < MinimumDistinctActionValues ||
            Math.Abs(baselineShift) < effectThreshold ||
            !trendEnough)
        {
            return null;
        }

        return new GuidedAnalogRepeatMetrics(
            run.RepeatNumber,
            referenceMedian,
            referenceNoise,
            actionStartMedian,
            actionEndMedian,
            baselineShift,
            actionSpan,
            direction,
            monotonicity,
            correlation,
            responseToNoise,
            rate,
            distinct,
            plateau,
            returned,
            reaction);
    }

    private static GuidedAnalogCandidate Aggregate(
        string actionName,
        IReadOnlyList<FieldObservation> observations,
        int repeatCount)
    {
        var representative = observations[0];
        var directionGroup = observations
            .GroupBy(item => item.Metrics.Direction)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First();
        var direction = directionGroup.Key;
        var consistent = directionGroup
            .Select(item => item.Metrics)
            .OrderBy(item => item.RepeatNumber)
            .ToArray();
        var repeatability = consistent.Select(item => item.RepeatNumber).Distinct().Count();

        var monotonicity = Median(consistent.Select(item => item.MonotonicityPercent));
        var correlation = Median(consistent.Select(item => Math.Abs(item.TimeCorrelation)));
        var responseToNoise = Median(consistent.Select(item => item.ResponseToNoiseRatio));
        var baselineShift = Median(consistent.Select(item => item.BaselineShift));
        var actionSpan = Median(consistent.Select(item => item.ActionSpan));
        var referenceNoise = Median(consistent.Select(item => item.ReferenceNoise));
        var rate = Median(consistent.Select(item => item.RatePerSecond));
        var plateau = Median(consistent.Select(item => item.EndpointPlateauPercent));
        var returnObserved = consistent.Count(item => item.ReturnedToBaseline.HasValue);
        var returned = consistent.Count(item => item.ReturnedToBaseline == true);
        var reactions = consistent
            .Where(item => item.ReactionMilliseconds.HasValue)
            .Select(item => item.ReactionMilliseconds!.Value)
            .ToArray();
        var reaction = reactions.Length == 0 ? (double?)null : Median(reactions);

        var (score, reasons) = Score(
            repeatability,
            repeatCount,
            monotonicity,
            correlation,
            responseToNoise,
            Median(consistent.Select(item => (double)item.DistinctActionValues)),
            reaction,
            returned,
            returnObserved,
            plateau);

        var priority =
            repeatCount >= 3 &&
            repeatability == repeatCount &&
            score >= 70 &&
            monotonicity >= 75 &&
            correlation >= 0.70
                ? AnalogCandidatePriority.High
                : ((repeatability >= 2 && repeatability * 3 >= repeatCount * 2 && score >= 45) ||
                   (repeatCount == 1 && score >= 55))
                    ? AnalogCandidatePriority.Medium
                    : AnalogCandidatePriority.Low;

        return new GuidedAnalogCandidate
        {
            ActionName = actionName.Trim(),
            Id = representative.Id,
            IsExtended = representative.IsExtended,
            StartByte = representative.StartByte,
            Encoding = representative.Encoding,
            Direction = direction,
            Priority = priority,
            Score = score,
            RepeatabilityCount = repeatability,
            RepeatCount = repeatCount,
            MedianBaselineShift = baselineShift,
            MedianActionSpan = actionSpan,
            MedianReferenceNoise = referenceNoise,
            MedianMonotonicityPercent = monotonicity,
            MedianAbsoluteTimeCorrelation = correlation,
            MedianResponseToNoiseRatio = responseToNoise,
            MedianRatePerSecond = rate,
            MedianEndpointPlateauPercent = plateau,
            ReturnToBaselineCount = returned,
            ReturnObservedCount = returnObserved,
            MedianReactionMilliseconds = reaction,
            Reasons = reasons,
            Repeats = observations.Select(item => item.Metrics).OrderBy(item => item.RepeatNumber).ToArray()
        };
    }

    private static (int Score, IReadOnlyList<string> Reasons) Score(
        int repeatability,
        int repeatCount,
        double monotonicity,
        double correlation,
        double responseToNoise,
        double distinctMedian,
        double? reaction,
        int returned,
        int returnObserved,
        double plateau)
    {
        var score = 15;
        var reasons = new List<string> { "sensor-like изменение прошло минимальные критерии" };

        if (repeatCount >= 3 && repeatability == repeatCount)
        {
            score += 30;
            reasons.Add($"одно направление во всех {repeatCount} повторах");
        }
        else if (repeatability == repeatCount)
        {
            score += 15;
            reasons.Add($"повторилось {repeatability}/{repeatCount}");
        }
        else if (repeatability * 3 >= repeatCount * 2)
        {
            score += 12;
            reasons.Add($"направление повторилось {repeatability}/{repeatCount}");
        }
        else
        {
            score -= 15;
            reasons.Add($"низкая повторяемость направления {repeatability}/{repeatCount}");
        }

        if (monotonicity >= 90) { score += 20; reasons.Add("монотонность ≥90%"); }
        else if (monotonicity >= 75) { score += 15; reasons.Add("монотонность ≥75%"); }
        else if (monotonicity >= 60) { score += 8; reasons.Add("монотонность ≥60%"); }

        if (correlation >= 0.90) { score += 15; reasons.Add("|corr(time)| ≥0.90"); }
        else if (correlation >= 0.75) { score += 10; reasons.Add("|corr(time)| ≥0.75"); }
        else if (correlation >= 0.60) { score += 5; reasons.Add("|corr(time)| ≥0.60"); }

        if (responseToNoise >= 10) { score += 15; reasons.Add("response/noise ≥10"); }
        else if (responseToNoise >= 6) { score += 10; reasons.Add("response/noise ≥6"); }
        else if (responseToNoise >= 3) { score += 5; reasons.Add("response/noise ≥3"); }

        if (distinctMedian >= 16) { score += 8; reasons.Add("много промежуточных raw значений"); }
        else if (distinctMedian >= 8) { score += 5; reasons.Add("несколько промежуточных raw значений"); }

        if (reaction.HasValue)
        {
            if (reaction.Value >= -100 && reaction.Value <= 1000)
            {
                score += 5;
                reasons.Add("реакция наблюдается около/после действия");
            }
            else if (reaction.Value < -100)
            {
                score -= 15;
                reasons.Add("изменение начинается заметно до заданного действия");
            }
        }

        if (returnObserved > 0 && returned == returnObserved)
        {
            score += 8;
            reasons.Add($"возврат к baseline {returned}/{returnObserved}");
        }

        if (plateau >= 80)
        {
            score -= 8;
            reasons.Add("длительное плато/возможное насыщение");
        }
        else if (plateau >= 50)
        {
            reasons.Add("заметное конечное плато");
        }

        return (Math.Clamp(score, 0, 100), reasons);
    }

    private static ValueSample[] Decode(
        IReadOnlyList<CanFrame> frames,
        int startByte,
        AnalogFieldEncoding encoding,
        int requiredLength) =>
        frames
            .Where(frame => frame.Data.Length >= requiredLength)
            .Select(frame => new ValueSample(
                frame.Timestamp,
                DecodeValue(frame.Data, startByte, encoding)))
            .ToArray();

    private static double DecodeValue(
        IReadOnlyList<byte> data,
        int startByte,
        AnalogFieldEncoding encoding) => encoding switch
    {
        AnalogFieldEncoding.ByteUnsigned => data[startByte],
        AnalogFieldEncoding.UInt16LittleEndian =>
            data[startByte] | (data[startByte + 1] << 8),
        AnalogFieldEncoding.UInt16BigEndian =>
            (data[startByte] << 8) | data[startByte + 1],
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static bool? AnalyzeReturn(
        IReadOnlyList<CanFrame>? returnFrames,
        int startByte,
        AnalogFieldEncoding encoding,
        int requiredLength,
        double baseline,
        double noise,
        double effect)
    {
        if (returnFrames is null)
            return null;
        var values = returnFrames
            .Where(IsUsable)
            .Where(frame => frame.Data.Length >= requiredLength)
            .Select(frame => DecodeValue(frame.Data, startByte, encoding))
            .ToArray();
        if (values.Length < 3)
            return null;

        var tolerance = Math.Max(2.0, Math.Max(noise * 2.0, effect * 0.15));
        return Math.Abs(Median(values) - baseline) <= tolerance;
    }

    private static double? FindReaction(
        IReadOnlyList<ValueSample> action,
        DateTimeOffset? eventTime,
        double baseline,
        AnalogDirection direction,
        double threshold)
    {
        if (!eventTime.HasValue)
            return null;

        for (var index = 0; index + 1 < action.Count; index++)
        {
            if (Beyond(action[index].Value, baseline, direction, threshold) &&
                Beyond(action[index + 1].Value, baseline, direction, threshold))
            {
                return (action[index].Timestamp - eventTime.Value).TotalMilliseconds;
            }
        }
        return null;
    }

    private static bool Beyond(
        double value,
        double baseline,
        AnalogDirection direction,
        double threshold) =>
        direction == AnalogDirection.Increasing
            ? value - baseline >= threshold
            : baseline - value >= threshold;

    private static double Monotonicity(
        IReadOnlyList<double> values,
        AnalogDirection direction,
        double deadband)
    {
        var significant = 0;
        var aligned = 0;
        for (var index = 1; index < values.Count; index++)
        {
            var delta = values[index] - values[index - 1];
            if (Math.Abs(delta) <= deadband)
                continue;
            significant++;
            if ((direction == AnalogDirection.Increasing && delta > 0) ||
                (direction == AnalogDirection.Decreasing && delta < 0))
                aligned++;
        }

        return significant == 0 ? 0 : 100.0 * aligned / significant;
    }

    private static double PearsonTimeCorrelation(IReadOnlyList<ValueSample> samples)
    {
        if (samples.Count < 3)
            return 0;
        var t0 = samples[0].Timestamp;
        var xs = samples.Select(sample => (sample.Timestamp - t0).TotalSeconds).ToArray();
        var ys = samples.Select(sample => sample.Value).ToArray();
        var xMean = xs.Average();
        var yMean = ys.Average();
        double numerator = 0, xSquared = 0, ySquared = 0;
        for (var index = 0; index < xs.Length; index++)
        {
            var x = xs[index] - xMean;
            var y = ys[index] - yMean;
            numerator += x * y;
            xSquared += x * x;
            ySquared += y * y;
        }

        var denominator = Math.Sqrt(xSquared * ySquared);
        return denominator <= double.Epsilon ? 0 : numerator / denominator;
    }

    private static double RatePerSecond(IReadOnlyList<ValueSample> samples)
    {
        if (samples.Count < 2)
            return 0;
        var seconds = (samples[^1].Timestamp - samples[0].Timestamp).TotalSeconds;
        return seconds <= 0 ? 0 : (samples[^1].Value - samples[0].Value) / seconds;
    }

    private static double EndpointPlateauPercent(
        IReadOnlyList<double> values,
        AnalogDirection direction,
        double referenceNoise)
    {
        if (values.Count == 0)
            return 0;
        var endpoint = direction == AnalogDirection.Increasing
            ? Percentile(values, 0.95)
            : Percentile(values, 0.05);
        var tolerance = Math.Max(1.0, referenceNoise);
        var count = values.Count(value => Math.Abs(value - endpoint) <= tolerance);
        return 100.0 * count / values.Count;
    }

    private static double RobustNoise(IEnumerable<double> values)
    {
        var array = values.OrderBy(value => value).ToArray();
        if (array.Length == 0)
            return 0;
        var median = Median(array);
        var mad = Median(array.Select(value => Math.Abs(value - median)));
        var pRange = Percentile(array, 0.95) - Percentile(array, 0.05);
        return Math.Max(2.0 * 1.4826 * mad, pRange);
    }

    private static double Median(IEnumerable<double> values)
    {
        var array = values.OrderBy(value => value).ToArray();
        if (array.Length == 0)
            return 0;
        var middle = array.Length / 2;
        return array.Length % 2 == 0
            ? (array[middle - 1] + array[middle]) / 2.0
            : array[middle];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0;
        if (sorted.Length == 1)
            return sorted[0];
        var position = (sorted.Length - 1) * Math.Clamp(percentile, 0, 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static bool IsUsable(CanFrame frame) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        !frame.IsRemote &&
        !frame.IsError &&
        frame.Data.Length is >= 1 and <= 8;

    private sealed record ValueSample(DateTimeOffset Timestamp, double Value);

    private sealed record FieldObservation(
        uint Id,
        bool IsExtended,
        int StartByte,
        AnalogFieldEncoding Encoding,
        GuidedAnalogRepeatMetrics Metrics)
    {
        public string StableKey =>
            $"{Id:X8}:{(IsExtended ? 'E' : 'S')}:{StartByte}:{Encoding}";
    }
}
