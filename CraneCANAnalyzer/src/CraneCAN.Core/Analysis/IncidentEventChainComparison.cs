namespace CraneCAN.Core.Analysis;

public enum IncidentEventChainDifferenceKind
{
    StepOnlyInBaseline,
    StepOnlyInCurrent,
    TransitionChanged,
    TimingChanged,
    BreakpointStateChanged
}

public enum IncidentEventChainDifferencePriority
{
    Info,
    Medium,
    High
}

public sealed record IncidentEventChainDifference(
    uint Id,
    bool IsExtended,
    int? DataIndex,
    IncidentTransitionKind StepKind,
    IncidentEventChainDifferenceKind DifferenceKind,
    IncidentEventChainDifferencePriority Priority,
    double? BaselineReactionMilliseconds,
    double? CurrentReactionMilliseconds,
    string BaselineValue,
    string CurrentValue,
    IReadOnlyList<string> ProfileSignals,
    string Description)
{
    public double EvidenceMilliseconds =>
        BaselineReactionMilliseconds.HasValue && CurrentReactionMilliseconds.HasValue
            ? Math.Min(BaselineReactionMilliseconds.Value, CurrentReactionMilliseconds.Value)
            : BaselineReactionMilliseconds ?? CurrentReactionMilliseconds ?? double.PositiveInfinity;

    public double? TimingDeltaMilliseconds =>
        BaselineReactionMilliseconds.HasValue && CurrentReactionMilliseconds.HasValue
            ? CurrentReactionMilliseconds.Value - BaselineReactionMilliseconds.Value
            : null;
}

public sealed record IncidentEventChainComparisonResult(
    int BaselineStepCount,
    int CurrentStepCount,
    double TimingThresholdMilliseconds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<IncidentEventChainDifference> Differences)
{
    public int HighCount => Differences.Count(item => item.Priority == IncidentEventChainDifferencePriority.High);
    public int MediumCount => Differences.Count(item => item.Priority == IncidentEventChainDifferencePriority.Medium);
    public int InfoCount => Differences.Count(item => item.Priority == IncidentEventChainDifferencePriority.Info);
    public IncidentEventChainDifference? Earliest => Differences.FirstOrDefault();
}

public static class IncidentEventChainComparer
{
    public static IncidentEventChainComparisonResult Compare(
        IncidentEventChainResult baseline,
        IncidentEventChainResult current,
        double timingThresholdMilliseconds = 100.0,
        double majorTimingShiftMilliseconds = 500.0)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        if (!double.IsFinite(timingThresholdMilliseconds) || timingThresholdMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timingThresholdMilliseconds));
        if (!double.IsFinite(majorTimingShiftMilliseconds) ||
            majorTimingShiftMilliseconds < timingThresholdMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(majorTimingShiftMilliseconds));

        var baselineByKey = BuildIndex(baseline.Steps, "эталонной");
        var currentByKey = BuildIndex(current.Steps, "сравниваемой");
        var differences = new List<IncidentEventChainDifference>();

        foreach (var key in baselineByKey.Keys.Union(currentByKey.Keys)
                     .OrderBy(key => key.Id)
                     .ThenBy(key => key.IsExtended)
                     .ThenBy(key => key.DataIndex ?? -1)
                     .ThenBy(key => key.Kind))
        {
            baselineByKey.TryGetValue(key, out var baselineStep);
            currentByKey.TryGetValue(key, out var currentStep);

            if (baselineStep is null)
            {
                differences.Add(MissingStep(currentStep!, baselineSideMissing: true));
                continue;
            }

            if (currentStep is null)
            {
                differences.Add(MissingStep(baselineStep, baselineSideMissing: false));
                continue;
            }

            var signals = baselineStep.ProfileSignals
                .Concat(currentStep.ProfileSignals)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (!string.Equals(baselineStep.BaselineValue, currentStep.BaselineValue, StringComparison.Ordinal) ||
                !string.Equals(baselineStep.ObservedValue, currentStep.ObservedValue, StringComparison.Ordinal))
            {
                differences.Add(new IncidentEventChainDifference(
                    key.Id,
                    key.IsExtended,
                    key.DataIndex,
                    key.Kind,
                    IncidentEventChainDifferenceKind.TransitionChanged,
                    HighestTransitionPriority(baselineStep.Priority, currentStep.Priority),
                    baselineStep.ReactionMilliseconds,
                    currentStep.ReactionMilliseconds,
                    TransitionText(baselineStep),
                    TransitionText(currentStep),
                    signals,
                    "В одном и том же наблюдаемом CAN-шаге отличается переход baseline → observed."));
            }

            var timingDelta = currentStep.ReactionMilliseconds - baselineStep.ReactionMilliseconds;
            if (Math.Abs(timingDelta) >= timingThresholdMilliseconds)
            {
                differences.Add(new IncidentEventChainDifference(
                    key.Id,
                    key.IsExtended,
                    key.DataIndex,
                    key.Kind,
                    IncidentEventChainDifferenceKind.TimingChanged,
                    Math.Abs(timingDelta) >= majorTimingShiftMilliseconds
                        ? IncidentEventChainDifferencePriority.Medium
                        : IncidentEventChainDifferencePriority.Info,
                    baselineStep.ReactionMilliseconds,
                    currentStep.ReactionMilliseconds,
                    TransitionText(baselineStep),
                    TransitionText(currentStep),
                    signals,
                    $"Время появления одинакового шага относительно marker изменилось на {timingDelta:+0.###;-0.###;0} мс."));
            }

            if (baselineStep.BreakpointCandidate != currentStep.BreakpointCandidate)
            {
                differences.Add(new IncidentEventChainDifference(
                    key.Id,
                    key.IsExtended,
                    key.DataIndex,
                    key.Kind,
                    IncidentEventChainDifferenceKind.BreakpointStateChanged,
                    IncidentEventChainDifferencePriority.Medium,
                    baselineStep.ReactionMilliseconds,
                    currentStep.ReactionMilliseconds,
                    baselineStep.BreakpointCandidate ? baselineStep.BreakpointReason : "не отмечен",
                    currentStep.BreakpointCandidate ? currentStep.BreakpointReason : "не отмечен",
                    signals,
                    "Для сопоставленного шага изменился вычисляемый признак «кандидат на разрыв»."));
            }
        }

        var warnings = baseline.Warnings
            .Select(text => "ЭТАЛОН: " + text)
            .Concat(current.Warnings.Select(text => "СРАВНЕНИЕ: " + text))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (baseline.Steps.Count == 0 || current.Steps.Count == 0)
            warnings.Add(
                "Одна из цепочек пуста. Отсутствующие шаги показываются как наблюдаемое различие, но качество сравнения ограничено.");

        var ordered = differences
            .OrderBy(item => item.EvidenceMilliseconds)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .ThenBy(item => item.IsExtended)
            .ThenBy(item => item.DataIndex ?? -1)
            .ThenBy(item => item.DifferenceKind)
            .ToArray();

        return new IncidentEventChainComparisonResult(
            baseline.Steps.Count,
            current.Steps.Count,
            timingThresholdMilliseconds,
            warnings,
            ordered);
    }

    private static Dictionary<StepKey, IncidentEventChainStep> BuildIndex(
        IReadOnlyList<IncidentEventChainStep> steps,
        string label)
    {
        var duplicate = steps.GroupBy(KeyOf).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"В {label} цепочке неоднозначный повтор одного шага: " +
                $"ID 0x{duplicate.Key.Id:X}, DATA[{duplicate.Key.DataIndex?.ToString() ?? "—"}], {duplicate.Key.Kind}.");
        }

        return steps.ToDictionary(KeyOf);
    }

    private static IncidentEventChainDifference MissingStep(
        IncidentEventChainStep existing,
        bool baselineSideMissing)
    {
        var priority = existing.Priority switch
        {
            IncidentTransitionPriority.High => IncidentEventChainDifferencePriority.High,
            IncidentTransitionPriority.Medium => IncidentEventChainDifferencePriority.Medium,
            _ => IncidentEventChainDifferencePriority.Info
        };

        return new IncidentEventChainDifference(
            existing.Id,
            existing.IsExtended,
            existing.DataIndex,
            existing.Kind,
            baselineSideMissing
                ? IncidentEventChainDifferenceKind.StepOnlyInCurrent
                : IncidentEventChainDifferenceKind.StepOnlyInBaseline,
            priority,
            baselineSideMissing ? null : existing.ReactionMilliseconds,
            baselineSideMissing ? existing.ReactionMilliseconds : null,
            baselineSideMissing ? "—" : TransitionText(existing),
            baselineSideMissing ? TransitionText(existing) : "—",
            existing.ProfileSignals.ToArray(),
            baselineSideMissing
                ? "Этот наблюдаемый CAN-шаг есть только во втором incident."
                : "Этот наблюдаемый CAN-шаг есть только в эталонном incident.");
    }

    private static IncidentEventChainDifferencePriority HighestTransitionPriority(
        IncidentTransitionPriority baseline,
        IncidentTransitionPriority current) =>
        baseline == IncidentTransitionPriority.High ||
        current == IncidentTransitionPriority.High
            ? IncidentEventChainDifferencePriority.High
            : baseline == IncidentTransitionPriority.Medium ||
              current == IncidentTransitionPriority.Medium
                ? IncidentEventChainDifferencePriority.Medium
                : IncidentEventChainDifferencePriority.Info;

    private static string TransitionText(IncidentEventChainStep step) =>
        $"{step.BaselineValue} → {step.ObservedValue}";

    private static StepKey KeyOf(IncidentEventChainStep step) =>
        new(step.Id, step.IsExtended, step.DataIndex, step.Kind);

    private readonly record struct StepKey(
        uint Id,
        bool IsExtended,
        int? DataIndex,
        IncidentTransitionKind Kind);
}
