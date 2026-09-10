using System.Globalization;

namespace CraneCAN.Core.Analysis;

public enum IncidentSignaturePriority
{
    Info,
    Medium,
    High
}

public sealed record IncidentSignatureCandidate(
    uint Id,
    bool IsExtended,
    int? DataIndex,
    IncidentTransitionKind Kind,
    IncidentSignaturePriority Priority,
    int OccurrenceCount,
    int IncidentCount,
    double RepeatabilityPercent,
    double MedianReactionMilliseconds,
    double MinimumReactionMilliseconds,
    double MaximumReactionMilliseconds,
    double TimingSpreadMilliseconds,
    string ModalBaselineValue,
    string ModalObservedValue,
    int TransitionAgreementCount,
    double TransitionAgreementPercent,
    int BreakpointCount,
    IReadOnlyList<string> ProfileSignals,
    string Description);

public sealed record IncidentSignatureAnalysisResult(
    int IncidentCount,
    IReadOnlyList<IncidentSignatureCandidate> Candidates,
    IReadOnlyList<string> Warnings)
{
    public int HighCount =>
        Candidates.Count(candidate => candidate.Priority == IncidentSignaturePriority.High);

    public int MediumCount =>
        Candidates.Count(candidate => candidate.Priority == IncidentSignaturePriority.Medium);

    public int InfoCount =>
        Candidates.Count(candidate => candidate.Priority == IncidentSignaturePriority.Info);

    public IncidentSignatureCandidate? EarliestHigh =>
        Candidates
            .Where(candidate => candidate.Priority == IncidentSignaturePriority.High)
            .OrderBy(candidate => candidate.MedianReactionMilliseconds)
            .FirstOrDefault();
}

public static class IncidentSignatureAnalyzer
{
    public static IncidentSignatureAnalysisResult Analyze(
        IReadOnlyList<IncidentEventChainResult> chains)
    {
        ArgumentNullException.ThrowIfNull(chains);
        if (chains.Count < 2)
            throw new ArgumentException(
                "Для анализа повторяемости нужны минимум два incident.",
                nameof(chains));

        var perIncident = chains
            .Select((chain, index) => BuildIndex(chain, index + 1))
            .ToArray();

        var warnings = new List<string>();
        if (chains.Count < 3)
        {
            warnings.Add(
                "Доступны только два incident. Для устойчивой оценки повторяемости рекомендуется минимум 3 записи.");
        }

        for (var index = 0; index < chains.Count; index++)
        {
            foreach (var warning in chains[index].Warnings)
                warnings.Add($"Incident #{index + 1}: {warning}");
        }

        var keys = perIncident
            .SelectMany(index => index.Keys)
            .Distinct()
            .OrderBy(key => key.Id)
            .ThenBy(key => key.IsExtended)
            .ThenBy(key => key.DataIndex ?? -1)
            .ThenBy(key => key.Kind)
            .ToArray();

        var candidates = new List<IncidentSignatureCandidate>(keys.Length);

        foreach (var key in keys)
        {
            var samples = perIncident
                .Select(index => index.TryGetValue(key, out var step) ? step : null)
                .Where(step => step is not null)
                .Cast<IncidentEventChainStep>()
                .ToArray();

            var occurrenceCount = samples.Length;
            var times = samples
                .Select(step => step.ReactionMilliseconds)
                .OrderBy(value => value)
                .ToArray();

            var transitions = samples
                .GroupBy(step => new TransitionKey(step.BaselineValue, step.ObservedValue))
                .Select(group => new
                {
                    group.Key,
                    Count = group.Count()
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key.BaselineValue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ObservedValue, StringComparer.Ordinal)
                .ToArray();

            var modalTransition = transitions[0];
            var repeatabilityPercent =
                100.0 * occurrenceCount / chains.Count;
            var transitionAgreementPercent =
                100.0 * modalTransition.Count / occurrenceCount;
            var minimum = times[0];
            var maximum = times[^1];
            var spread = maximum - minimum;
            var significantCount = samples.Count(step =>
                step.Priority != IncidentTransitionPriority.Info);

            var priority = Rank(
                chains.Count,
                occurrenceCount,
                modalTransition.Count,
                spread,
                significantCount);

            var profileSignals = samples
                .SelectMany(step => step.ProfileSignals)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            candidates.Add(new IncidentSignatureCandidate(
                key.Id,
                key.IsExtended,
                key.DataIndex,
                key.Kind,
                priority,
                occurrenceCount,
                chains.Count,
                repeatabilityPercent,
                Median(times),
                minimum,
                maximum,
                spread,
                modalTransition.Key.BaselineValue,
                modalTransition.Key.ObservedValue,
                modalTransition.Count,
                transitionAgreementPercent,
                samples.Count(step => step.BreakpointCandidate),
                profileSignals,
                Describe(
                    priority,
                    occurrenceCount,
                    chains.Count,
                    modalTransition.Count,
                    spread)));
        }

        if (candidates.Count == 0)
        {
            warnings.Add(
                "Во всех выбранных incident цепочки событий пусты: сигнатура не сформирована.");
        }
        else if (candidates.All(candidate => candidate.OccurrenceCount < 2))
        {
            warnings.Add(
                "Ни один наблюдаемый CAN-шаг не повторился хотя бы в двух incident.");
        }
        else if (candidates.All(candidate => candidate.Priority == IncidentSignaturePriority.Info))
        {
            warnings.Add(
                "Повторяющиеся шаги есть, но ни один не достиг критериев MEDIUM/HIGH повторяемости.");
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.RepeatabilityPercent)
            .ThenBy(candidate => candidate.MedianReactionMilliseconds)
            .ThenBy(candidate => candidate.Id)
            .ThenBy(candidate => candidate.IsExtended)
            .ThenBy(candidate => candidate.DataIndex ?? -1)
            .ThenBy(candidate => candidate.Kind)
            .ToArray();

        return new IncidentSignatureAnalysisResult(
            chains.Count,
            ordered,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<StepKey, IncidentEventChainStep> BuildIndex(
        IncidentEventChainResult chain,
        int incidentNumber)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var duplicate = chain.Steps
            .GroupBy(KeyOf)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Incident #{incidentNumber} содержит неоднозначный повтор одного шага: " +
                $"ID 0x{duplicate.Key.Id:X}, " +
                $"{(duplicate.Key.IsExtended ? "Extended" : "Standard")}, " +
                $"{LocationText(duplicate.Key.DataIndex)}, {duplicate.Key.Kind}.");
        }

        return chain.Steps.ToDictionary(KeyOf);
    }

    private static IncidentSignaturePriority Rank(
        int incidentCount,
        int occurrenceCount,
        int transitionAgreementCount,
        double timingSpreadMilliseconds,
        int significantCount)
    {
        var appearsInAll = occurrenceCount == incidentCount;
        var sameTransitionInAllOccurrences =
            transitionAgreementCount == occurrenceCount;
        var tightTiming =
            timingSpreadMilliseconds <= 250.0;
        var everyOccurrenceIsSignificant =
            significantCount == occurrenceCount;

        if (incidentCount >= 3 &&
            appearsInAll &&
            sameTransitionInAllOccurrences &&
            tightTiming &&
            everyOccurrenceIsSignificant)
        {
            return IncidentSignaturePriority.High;
        }

        var atLeastTwoThirds =
            occurrenceCount >= 2 &&
            occurrenceCount * 3 >= incidentCount * 2;
        var transitionAtLeastTwoThirds =
            transitionAgreementCount * 3 >= occurrenceCount * 2;

        if (atLeastTwoThirds &&
            transitionAtLeastTwoThirds &&
            significantCount > 0)
        {
            return IncidentSignaturePriority.Medium;
        }

        return IncidentSignaturePriority.Info;
    }

    private static string Describe(
        IncidentSignaturePriority priority,
        int occurrenceCount,
        int incidentCount,
        int transitionAgreementCount,
        double timingSpreadMilliseconds) =>
        priority switch
        {
            IncidentSignaturePriority.High =>
                "Шаг наблюдался во всех incident, переход совпал во всех повторениях, " +
                "разброс времени " + timingSpreadMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " мс (≤250 мс).",
            IncidentSignaturePriority.Medium =>
                $"Шаг повторился {occurrenceCount}/{incidentCount}; " +
                $"модальный переход совпал {transitionAgreementCount}/{occurrenceCount}. " +
                "Есть повторяемое наблюдение, но критерии HIGH не выполнены.",
            _ =>
                $"Повторяемость {occurrenceCount}/{incidentCount}; " +
                $"согласие перехода {transitionAgreementCount}/{occurrenceCount}. " +
                "Низкая повторяемость либо только INFO-evidence."
        };

    private static double Median(IReadOnlyList<double> sortedValues)
    {
        if (sortedValues.Count == 0)
            throw new ArgumentException("Нельзя вычислить медиану пустого набора.", nameof(sortedValues));

        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 1
            ? sortedValues[middle]
            : (sortedValues[middle - 1] + sortedValues[middle]) / 2.0;
    }

    private static string LocationText(int? dataIndex) =>
        dataIndex.HasValue ? $"DATA[{dataIndex.Value}]" : "ID/DLC";

    private static StepKey KeyOf(IncidentEventChainStep step) =>
        new(step.Id, step.IsExtended, step.DataIndex, step.Kind);

    private readonly record struct StepKey(
        uint Id,
        bool IsExtended,
        int? DataIndex,
        IncidentTransitionKind Kind);

    private readonly record struct TransitionKey(
        string BaselineValue,
        string ObservedValue);
}
