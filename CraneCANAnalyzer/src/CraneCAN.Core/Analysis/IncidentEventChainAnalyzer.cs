using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public sealed record IncidentEventChainStep(
    int Sequence,
    double ReactionMilliseconds,
    double? DeltaFromPreviousMilliseconds,
    uint Id,
    bool IsExtended,
    int? DataIndex,
    IncidentTransitionKind Kind,
    IncidentTransitionPriority Priority,
    string BaselineValue,
    string ObservedValue,
    IReadOnlyList<string> ProfileSignals,
    SignalKnowledgeState? HighestSignalConfidence,
    bool BreakpointCandidate,
    string BreakpointReason,
    string Description);

public sealed record IncidentEventChainResult(
    IReadOnlyList<IncidentEventChainStep> Steps,
    IReadOnlyList<string> Warnings)
{
    public int ProfileAnnotatedCount => Steps.Count(step => step.ProfileSignals.Count > 0);
    public int BreakpointCount => Steps.Count(step => step.BreakpointCandidate);
}

public static class IncidentEventChainAnalyzer
{
    private const double LargeGapMilliseconds = 750.0;

    public static IncidentEventChainResult Build(
        IncidentTransitionAnalysisResult transition,
        MachineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var warnings = transition.Warnings.ToList();
        var profileSignals = profile is null
            ? Array.Empty<MachineSignal>()
            : profile.KnownSignals
                .Concat(profile.ExperimentalSignals)
                .Where(signal => signal.Confidence != SignalKnowledgeState.Rejected && IsUsableSignal(signal))
                .ToArray();

        if (profile is null)
            warnings.Add("Machine Profile не загружен: цепочка содержит только наблюдаемые CAN-события.");
        else if (profileSignals.Length == 0)
            warnings.Add("В текущем Machine Profile нет известных/экспериментальных сигналов для аннотации цепочки.");

        var candidates = transition.Candidates
            .OrderBy(candidate => candidate.ReactionMilliseconds)
            .ThenByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id)
            .ThenBy(candidate => candidate.IsExtended)
            .ThenBy(candidate => candidate.DataIndex ?? -1)
            .ToArray();

        var raw = new List<IncidentEventChainStep>(candidates.Length);
        IncidentTransitionCandidate? previous = null;

        foreach (var candidate in candidates)
        {
            var matches = profileSignals
                .Where(signal => Matches(signal, candidate))
                .OrderByDescending(signal => ConfidenceRank(signal.Confidence))
                .ThenBy(signal => signal.Name, StringComparer.Ordinal)
                .ToArray();

            var names = matches
                .Select(signal => string.IsNullOrWhiteSpace(signal.Name)
                    ? $"signal {signal.SignalId:N}"
                    : signal.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            SignalKnowledgeState? confidence = matches.Length == 0
                ? null
                : matches
                    .OrderByDescending(signal => ConfidenceRank(signal.Confidence))
                    .First()
                    .Confidence;

            var delta = previous is null
                ? (double?)null
                : candidate.ReactionMilliseconds - previous.ReactionMilliseconds;

            var breakpointReasons = new List<string>();
            if (candidate.Kind == IncidentTransitionKind.PeriodicIdStopped)
                breakpointReasons.Add("прекратился ранее стабильный периодический CAN ID");

            if (previous is not null &&
                delta >= LargeGapMilliseconds &&
                previous.Priority != IncidentTransitionPriority.Info &&
                candidate.Priority != IncidentTransitionPriority.Info)
            {
                breakpointReasons.Add(
                    $"между соседними значимыми событиями пауза {delta.Value:0.###} мс");
            }

            raw.Add(new IncidentEventChainStep(
                raw.Count + 1,
                candidate.ReactionMilliseconds,
                delta,
                candidate.Id,
                candidate.IsExtended,
                candidate.DataIndex,
                candidate.Kind,
                candidate.Priority,
                candidate.BaselineValue,
                candidate.ObservedValue,
                names,
                confidence,
                breakpointReasons.Count > 0,
                string.Join("; ", breakpointReasons),
                candidate.Description));

            previous = candidate;
        }

        if (raw.Count == 0)
            warnings.Add("Incident First Changes не сформировал кандидатов: цепочка событий пуста.");

        if (profile is not null && raw.Count > 0 && raw.All(step => step.ProfileSignals.Count == 0))
            warnings.Add(
                "Ни один шаг цепочки не сопоставился с сигналами текущего Machine Profile. " +
                "Назначение CAN ID остаётся неизвестным.");

        if (raw.Count(step => step.ProfileSignals.Count > 0) > 0)
            warnings.Add(
                "Аннотация Machine Profile означает только пересечение CAN ID/байтов с ранее сохранённым сигналом; " +
                "она не доказывает причинную роль сигнала в этом incident.");

        return new IncidentEventChainResult(raw, warnings.Distinct().ToArray());
    }

    private static bool Matches(MachineSignal signal, IncidentTransitionCandidate candidate)
    {
        if (signal.CanId != candidate.Id || signal.IsExtended != candidate.IsExtended)
            return false;

        if (!candidate.DataIndex.HasValue)
            return true;

        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBit = checked(firstBit + signal.BitLength - 1);
        var firstByte = firstBit / 8;
        var lastByte = lastBit / 8;
        return candidate.DataIndex.Value >= firstByte &&
               candidate.DataIndex.Value <= lastByte;
    }

    private static bool IsUsableSignal(MachineSignal signal) =>
        signal.StartByte is >= 0 and <= 7 &&
        signal.StartBit is >= 0 and <= 7 &&
        signal.BitLength is > 0 and <= 64 &&
        signal.CanId <= (signal.IsExtended ? 0x1FFFFFFFu : 0x7FFu);

    private static int ConfidenceRank(SignalKnowledgeState state) => state switch
    {
        SignalKnowledgeState.Confirmed => 5,
        SignalKnowledgeState.Probable => 4,
        SignalKnowledgeState.Candidate => 3,
        SignalKnowledgeState.Unknown => 2,
        SignalKnowledgeState.Rejected => 1,
        _ => 0
    };
}
