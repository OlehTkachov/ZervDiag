using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public enum IncidentTransitionKind
{
    IdAppeared,
    PeriodicIdStopped,
    DlcChanged,
    ByteChanged
}

public enum IncidentTransitionPriority
{
    Info,
    Medium,
    High
}

public sealed record IncidentTransitionCandidate(
    uint Id,
    bool IsExtended,
    int? DataIndex,
    IncidentTransitionKind Kind,
    IncidentTransitionPriority Priority,
    double ReactionMilliseconds,
    string BaselineValue,
    string ObservedValue,
    double BaselineAgreementPercent,
    int ConfirmationCount,
    string Description);

public sealed record IncidentTransitionAnalysisResult(
    DateTimeOffset MarkerTime,
    DateTimeOffset BaselineStart,
    DateTimeOffset BaselineEnd,
    DateTimeOffset SearchStart,
    DateTimeOffset SearchEnd,
    int BaselineFrameCount,
    int SearchFrameCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<IncidentTransitionCandidate> Candidates)
{
    public IncidentTransitionCandidate? Earliest => Candidates.FirstOrDefault();
    public int HighCount => Candidates.Count(item => item.Priority == IncidentTransitionPriority.High);
    public int MediumCount => Candidates.Count(item => item.Priority == IncidentTransitionPriority.Medium);
    public int InfoCount => Candidates.Count(item => item.Priority == IncidentTransitionPriority.Info);
}

public static class IncidentTransitionAnalyzer
{
    public static readonly TimeSpan BaselineStartOffset = TimeSpan.FromSeconds(-8);
    public static readonly TimeSpan BaselineEndOffset = TimeSpan.FromSeconds(-1);
    public static readonly TimeSpan SearchStartOffset = TimeSpan.FromSeconds(-1);
    public static readonly TimeSpan SearchEndOffset = TimeSpan.FromSeconds(4);

    private const double StableAgreementPercent = 95;
    private const double UsableAgreementPercent = 75;
    private const int HighConfirmationCount = 2;

    public static IncidentTransitionAnalysisResult Analyze(PreFaultIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        if (incident.Markers.Count == 0)
            throw new InvalidOperationException("В incident отсутствует отметка события.");

        var marker = incident.Markers.OrderBy(item => item.Timestamp).First().Timestamp;
        var baselineStart = Max(incident.WindowStart, marker + BaselineStartOffset);
        var baselineEnd = marker + BaselineEndOffset;
        var searchStart = marker + SearchStartOffset;
        var searchEnd = Min(incident.WindowEnd, marker + SearchEndOffset);

        if (baselineEnd <= baselineStart)
            throw new InvalidOperationException("Недостаточно предыстории до отметки для incident-анализа.");
        if (searchEnd <= searchStart)
            throw new InvalidOperationException("Недостаточно данных около отметки для incident-анализа.");

        var frames = incident.Frames
            .Where(IsUsable)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();
        var baseline = frames
            .Where(frame => frame.Timestamp >= baselineStart && frame.Timestamp < baselineEnd)
            .ToArray();
        var search = frames
            .Where(frame => frame.Timestamp >= searchStart && frame.Timestamp < searchEnd)
            .ToArray();

        if (baseline.Length == 0)
            throw new InvalidOperationException("В baseline-окне incident нет обычных Rx Classical CAN кадров.");

        var warnings = BuildWarnings(
            incident, marker, baselineStart, baselineEnd, searchStart, searchEnd);
        if (search.Length == 0)
            warnings.Add("В search-окне нет CAN-кадров; возможна только осторожная оценка прекращения ранее периодических ID.");
        if (baseline[0].Timestamp > baselineStart.AddMilliseconds(100))
            warnings.Add($"Фактические baseline-кадры начинаются только с {(baseline[0].Timestamp - marker).TotalSeconds:0.###} с относительно marker.");
        var candidates = new List<IncidentTransitionCandidate>();

        var baselineByKey = baseline
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => group.OrderBy(frame => frame.Timestamp).ToArray());
        var searchByKey = search
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => group.OrderBy(frame => frame.Timestamp).ToArray());

        foreach (var key in baselineByKey.Keys.Union(searchByKey.Keys)
                     .OrderBy(key => key.Id)
                     .ThenBy(key => key.IsExtended))
        {
            baselineByKey.TryGetValue(key, out var baselineForId);
            searchByKey.TryGetValue(key, out var searchForId);

            if (baselineForId is null)
            {
                var first = searchForId![0];
                var confirmation = CountConsecutiveEqualFrames(searchForId, 0);
                candidates.Add(new IncidentTransitionCandidate(
                    key.Id,
                    key.IsExtended,
                    null,
                    IncidentTransitionKind.IdAppeared,
                    confirmation >= HighConfirmationCount
                        ? IncidentTransitionPriority.High
                        : IncidentTransitionPriority.Medium,
                    (first.Timestamp - marker).TotalMilliseconds,
                    "ID не наблюдался в baseline",
                    $"DLC {first.Dlc}; DATA {first.DataText}",
                    100,
                    confirmation,
                    "CAN ID появился в окне около отметки. Это наблюдаемое появление сообщения, а не автоматическое доказательство появления физического ECU."));
                continue;
            }

            if (searchForId is null)
            {
                var stopped = TryBuildStoppedCandidate(
                    key.Id, key.IsExtended, baselineForId, marker, searchStart, searchEnd);
                if (stopped is not null) candidates.Add(stopped);
                continue;
            }

            CompareDlcAndBytes(
                key.Id, key.IsExtended, baselineForId, searchForId, marker, candidates);
        }

        var ordered = candidates
            .Where(candidate =>
                candidate.ReactionMilliseconds >= SearchStartOffset.TotalMilliseconds &&
                candidate.ReactionMilliseconds <= SearchEndOffset.TotalMilliseconds)
            .OrderBy(candidate => candidate.ReactionMilliseconds)
            .ThenByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id)
            .ThenBy(candidate => candidate.IsExtended)
            .ThenBy(candidate => candidate.DataIndex ?? -1)
            .ToArray();

        return new IncidentTransitionAnalysisResult(
            marker,
            baselineStart,
            baselineEnd,
            searchStart,
            searchEnd,
            baseline.Length,
            search.Length,
            warnings,
            ordered);
    }

    private static void CompareDlcAndBytes(
        uint id,
        bool isExtended,
        IReadOnlyList<CanFrame> baseline,
        IReadOnlyList<CanFrame> search,
        DateTimeOffset marker,
        ICollection<IncidentTransitionCandidate> candidates)
    {
        var baselineDlcMode = Mode(baseline.Select(frame => frame.Dlc));
        var baselineDlcAgreement = Agreement(
            baseline.Select(frame => frame.Dlc), baselineDlcMode);

        var firstDifferentDlcIndex = IndexOfFirst(
            search, frame => frame.Dlc != baselineDlcMode);
        if (firstDifferentDlcIndex >= 0)
        {
            var first = search[firstDifferentDlcIndex];
            var confirmation = CountConsecutive(
                search,
                firstDifferentDlcIndex,
                frame => frame.Dlc == first.Dlc);

            candidates.Add(new IncidentTransitionCandidate(
                id,
                isExtended,
                null,
                IncidentTransitionKind.DlcChanged,
                baselineDlcAgreement >= StableAgreementPercent &&
                confirmation >= HighConfirmationCount
                    ? IncidentTransitionPriority.High
                    : IncidentTransitionPriority.Medium,
                (first.Timestamp - marker).TotalMilliseconds,
                $"DLC {baselineDlcMode}",
                $"DLC {first.Dlc}",
                baselineDlcAgreement,
                confirmation,
                "Первое наблюдаемое отклонение DLC от baseline modal DLC."));
        }

        var baselineModalFrames = baseline
            .Where(frame => frame.Dlc == baselineDlcMode)
            .ToArray();
        var searchComparable = search
            .Where(frame => frame.Dlc == baselineDlcMode)
            .ToArray();
        if (baselineModalFrames.Length == 0 || searchComparable.Length == 0)
            return;

        for (var dataIndex = 0; dataIndex < baselineDlcMode; dataIndex++)
        {
            var baselineValue = Mode(
                baselineModalFrames.Select(frame => (int)frame.Data[dataIndex]));
            var agreement = Agreement(
                baselineModalFrames.Select(frame => (int)frame.Data[dataIndex]),
                baselineValue);

            var changedIndex = IndexOfFirst(
                searchComparable,
                frame => frame.Data[dataIndex] != baselineValue);
            if (changedIndex < 0) continue;

            var changedFrame = searchComparable[changedIndex];
            var changedValue = changedFrame.Data[dataIndex];
            var confirmation = CountConsecutive(
                searchComparable,
                changedIndex,
                frame => frame.Data[dataIndex] == changedValue);

            var priority = agreement >= StableAgreementPercent &&
                           confirmation >= HighConfirmationCount
                ? IncidentTransitionPriority.High
                : agreement >= UsableAgreementPercent
                    ? IncidentTransitionPriority.Medium
                    : IncidentTransitionPriority.Info;

            candidates.Add(new IncidentTransitionCandidate(
                id,
                isExtended,
                dataIndex,
                IncidentTransitionKind.ByteChanged,
                priority,
                (changedFrame.Timestamp - marker).TotalMilliseconds,
                $"0x{baselineValue:X2}",
                $"0x{changedValue:X2}",
                agreement,
                confirmation,
                priority == IncidentTransitionPriority.High
                    ? $"Устойчивый baseline-байт DATA[{dataIndex}] перешёл в повторяющееся новое значение."
                    : $"DATA[{dataIndex}] отличается от baseline; стабильность/повторяемость недостаточна для HIGH."));
        }
    }

    private static IncidentTransitionCandidate? TryBuildStoppedCandidate(
        uint id,
        bool isExtended,
        IReadOnlyList<CanFrame> baseline,
        DateTimeOffset marker,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd)
    {
        if (baseline.Count < 6) return null;

        var intervals = new List<double>();
        for (var index = 1; index < baseline.Count; index++)
        {
            var interval = (baseline[index].Timestamp - baseline[index - 1].Timestamp).TotalMilliseconds;
            if (interval > 0) intervals.Add(interval);
        }
        if (intervals.Count < 5) return null;

        var sorted = intervals.Order().ToArray();
        var median = Percentile(sorted, 0.5);
        var p10 = Percentile(sorted, 0.1);
        var p90 = Percentile(sorted, 0.9);
        if (median <= 0 || median > 1000 || p10 <= 0 || p90 / p10 > 2.5)
            return null;

        var timeoutMilliseconds = Math.Clamp(median * 5, 250, 5000);
        var detection = baseline[^1].Timestamp.AddMilliseconds(timeoutMilliseconds);
        if (detection < searchStart || detection > searchEnd)
            return null;

        return new IncidentTransitionCandidate(
            id,
            isExtended,
            null,
            IncidentTransitionKind.PeriodicIdStopped,
            IncidentTransitionPriority.Medium,
            (detection - marker).TotalMilliseconds,
            $"период ≈ {median:0.###} мс",
            "ID не наблюдался в search-окне",
            100,
            baseline.Count,
            "Ранее стабильный периодический CAN ID отсутствует в окне около отметки. Причина исчезновения не установлена.");
    }

    private static List<string> BuildWarnings(
        PreFaultIncident incident,
        DateTimeOffset marker,
        DateTimeOffset baselineStart,
        DateTimeOffset baselineEnd,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd)
    {
        var warnings = new List<string>();

        if (!incident.Complete)
            warnings.Add("Incident помечен как неполный/ограниченного качества: " +
                         string.Join(", ", incident.QualityCodes));

        if (incident.Markers.Count > 1)
            warnings.Add($"В incident {incident.Markers.Count} отметок; анализ привязан к самой ранней.");

        var desiredBaselineStart = marker + BaselineStartOffset;
        if (baselineStart > desiredBaselineStart)
            warnings.Add(
                $"Baseline укорочен доступной предысторией: {(baselineEnd - baselineStart).TotalSeconds:0.###} с вместо 7 с.");

        var desiredSearchEnd = marker + SearchEndOffset;
        if (searchEnd < desiredSearchEnd)
            warnings.Add(
                $"Search-окно после отметки укорочено: до +{(searchEnd - marker).TotalSeconds:0.###} с вместо +4 с.");

        return warnings;
    }

    private static bool IsUsable(CanFrame frame)
    {
        frame.Validate();
        return frame.Protocol == BusProtocol.ClassicalCan &&
               frame.Direction == CanDirection.Rx &&
               !frame.IsRemote &&
               !frame.IsError;
    }

    private static int Mode(IEnumerable<int> values) =>
        values.GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;

    private static double Agreement(IEnumerable<int> values, int expected)
    {
        var array = values.ToArray();
        return array.Length == 0
            ? 0
            : 100.0 * array.Count(value => value == expected) / array.Length;
    }

    private static int IndexOfFirst(
        IReadOnlyList<CanFrame> frames,
        Func<CanFrame, bool> predicate)
    {
        for (var index = 0; index < frames.Count; index++)
            if (predicate(frames[index])) return index;
        return -1;
    }

    private static int CountConsecutive(
        IReadOnlyList<CanFrame> frames,
        int startIndex,
        Func<CanFrame, bool> predicate)
    {
        var count = 0;
        for (var index = startIndex; index < frames.Count; index++)
        {
            if (!predicate(frames[index])) break;
            count++;
        }
        return count;
    }

    private static int CountConsecutiveEqualFrames(
        IReadOnlyList<CanFrame> frames,
        int startIndex)
    {
        var reference = frames[startIndex];
        return CountConsecutive(
            frames,
            startIndex,
            frame => frame.Dlc == reference.Dlc &&
                     frame.Data.AsSpan().SequenceEqual(reference.Data));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
