using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

internal static class IncidentEventChainComparisonTests
{
    public static void Run()
    {
        var baseline = Chain(
            [
                Step(1, -100, 0x100, false, 0, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, "0x00", "0x01", false, "Joystick"),
                Step(2, 50, 0x200, false, 1, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, "0x00", "0x80", false, "Enable"),
                Step(3, 400, 0x300, false, null, IncidentTransitionKind.PeriodicIdStopped,
                    IncidentTransitionPriority.Medium, "100 ms", "stopped", true)
            ]);

        var current = Chain(
            [
                Step(1, 20, 0x100, false, 0, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, "0x00", "0x01", false, "Joystick"),
                Step(2, 60, 0x200, false, 1, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, "0x00", "0x40", true, "Enable"),
                Step(3, 300, 0x400, false, null, IncidentTransitionKind.IdAppeared,
                    IncidentTransitionPriority.High, "absent", "present", false)
            ]);

        var result = IncidentEventChainComparer.Compare(baseline, current);

        Check(result.Differences.Any(item =>
                item.Id == 0x100 &&
                item.DifferenceKind == IncidentEventChainDifferenceKind.TimingChanged &&
                Math.Abs((item.TimingDeltaMilliseconds ?? 0) - 120) < 0.001),
            "Marker-aligned timing change was not detected.");

        Check(result.Differences.Any(item =>
                item.Id == 0x200 &&
                item.DifferenceKind == IncidentEventChainDifferenceKind.TransitionChanged &&
                item.Priority == IncidentEventChainDifferencePriority.High),
            "Different stable transition was not ranked HIGH.");

        Check(result.Differences.Any(item =>
                item.Id == 0x200 &&
                item.DifferenceKind == IncidentEventChainDifferenceKind.BreakpointStateChanged),
            "Changed breakpoint state was not reported.");

        Check(result.Differences.Any(item =>
                item.Id == 0x300 &&
                item.DifferenceKind == IncidentEventChainDifferenceKind.StepOnlyInBaseline),
            "GOOD-only step was not reported.");

        Check(result.Differences.Any(item =>
                item.Id == 0x400 &&
                item.DifferenceKind == IncidentEventChainDifferenceKind.StepOnlyInCurrent),
            "FAULT-only step was not reported.");

        Check(result.Earliest is { Id: 0x100 } &&
              Math.Abs(result.Earliest.EvidenceMilliseconds - (-100)) < 0.001,
            "Earliest observed incident-chain divergence was not ordered correctly.");

        var identical = IncidentEventChainComparer.Compare(baseline, baseline);
        Check(identical.Differences.Count == 0,
            "Identical incident chains produced false differences.");

        var subThreshold = IncidentEventChainComparer.Compare(
            Chain([Step(1, 0, 0x500, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.High, "0", "1", false)]),
            Chain([Step(1, 99, 0x500, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.High, "0", "1", false)]));
        Check(subThreshold.Differences.Count == 0,
            "Timing shift below threshold was reported.");

        var formats = IncidentEventChainComparer.Compare(
            Chain([Step(1, 0, 0x123, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.High, "0", "1", false)]),
            Chain([Step(1, 0, 0x123, true, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.High, "0", "1", false)]));
        Check(formats.Differences.Count == 2 &&
              formats.Differences.Any(item => !item.IsExtended) &&
              formats.Differences.Any(item => item.IsExtended),
            "Standard and Extended IDs with the same numeric value were merged.");

        var badThresholdRejected = false;
        try
        {
            IncidentEventChainComparer.Compare(
                baseline,
                current,
                timingThresholdMilliseconds: 500,
                majorTimingShiftMilliseconds: 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            badThresholdRejected = true;
        }
        Check(badThresholdRejected,
            "Invalid incident comparison timing thresholds were accepted.");
    }

    private static IncidentEventChainResult Chain(
        IReadOnlyList<IncidentEventChainStep> steps) =>
        new(steps, []);

    private static IncidentEventChainStep Step(
        int sequence,
        double reactionMilliseconds,
        uint id,
        bool extended,
        int? dataIndex,
        IncidentTransitionKind kind,
        IncidentTransitionPriority priority,
        string baseline,
        string observed,
        bool breakpoint,
        params string[] profileSignals) =>
        new(
            sequence,
            reactionMilliseconds,
            null,
            id,
            extended,
            dataIndex,
            kind,
            priority,
            baseline,
            observed,
            profileSignals,
            profileSignals.Length > 0 ? SignalKnowledgeState.Candidate : null,
            breakpoint,
            breakpoint ? "test breakpoint" : string.Empty,
            "test");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
