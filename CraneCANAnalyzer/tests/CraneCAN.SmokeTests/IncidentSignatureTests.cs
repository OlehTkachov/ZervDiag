using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

internal static class IncidentSignatureTests
{
    public static void Run()
    {
        var chains = new[]
        {
            Chain(
                [
                    Step(1, -120, 0x100, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x01", false, "Joystick"),
                    Step(2, 100, 0x200, false, 1, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.Medium, "0x00", "0x80", false, "Enable"),
                    Step(3, 200, 0x300, false, 2, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x10", false, "Valve"),
                    Step(4, 300, 0x123, true, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x01", false)
                ],
                ["CHAIN_WARNING"]),
            Chain(
                [
                    Step(1, -100, 0x100, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x01", true, "Joystick"),
                    Step(2, 160, 0x200, false, 1, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.Medium, "0x00", "0x80", false, "Enable"),
                    Step(3, 210, 0x300, false, 2, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x20", false, "Valve"),
                    Step(4, 450, 0x555, false, null, IncidentTransitionKind.IdAppeared,
                        IncidentTransitionPriority.Info, "absent", "present", false)
                ]),
            Chain(
                [
                    Step(1, -90, 0x100, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x01", true, "Joystick", "JCH command"),
                    Step(2, 220, 0x300, false, 2, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x10", false, "Valve"),
                    Step(3, 320, 0x123, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0x00", "0x01", false)
                ])
        };

        var result = IncidentSignatureAnalyzer.Analyze(chains);

        var stable = result.Candidates.Single(candidate =>
            candidate.Id == 0x100 &&
            !candidate.IsExtended &&
            candidate.DataIndex == 0);

        Check(stable.Priority == IncidentSignaturePriority.High &&
              stable.OccurrenceCount == 3 &&
              Math.Abs(stable.RepeatabilityPercent - 100) < 0.001 &&
              Math.Abs(stable.MedianReactionMilliseconds - (-100)) < 0.001 &&
              Math.Abs(stable.TimingSpreadMilliseconds - 30) < 0.001 &&
              stable.TransitionAgreementCount == 3 &&
              Math.Abs(stable.TransitionAgreementPercent - 100) < 0.001,
            "Stable 3/3 incident signature was not ranked HIGH.");

        Check(stable.BreakpointCount == 2 &&
              stable.ProfileSignals.SequenceEqual(new[] { "JCH command", "Joystick" }),
            "Breakpoint/profile aggregation across incidents is incorrect.");

        var twoOfThree = result.Candidates.Single(candidate => candidate.Id == 0x200);
        Check(twoOfThree.Priority == IncidentSignaturePriority.Medium &&
              twoOfThree.OccurrenceCount == 2 &&
              Math.Abs(twoOfThree.MedianReactionMilliseconds - 130) < 0.001,
            "Repeatable 2/3 signature was not ranked MEDIUM or median is wrong.");

        var inconsistent = result.Candidates.Single(candidate => candidate.Id == 0x300);
        Check(inconsistent.Priority == IncidentSignaturePriority.Medium &&
              inconsistent.OccurrenceCount == 3 &&
              inconsistent.TransitionAgreementCount == 2 &&
              Math.Abs(inconsistent.TransitionAgreementPercent - (200.0 / 3.0)) < 0.001 &&
              inconsistent.ModalObservedValue == "0x10",
            "3/3 occurrence with only 2/3 transition agreement was not downgraded to MEDIUM.");

        var extended = result.Candidates.Single(candidate =>
            candidate.Id == 0x123 && candidate.IsExtended);
        var standard = result.Candidates.Single(candidate =>
            candidate.Id == 0x123 && !candidate.IsExtended);
        Check(extended.OccurrenceCount == 1 &&
              standard.OccurrenceCount == 1 &&
              extended.Priority == IncidentSignaturePriority.Info &&
              standard.Priority == IncidentSignaturePriority.Info,
            "Standard/Extended IDs with the same numeric value were merged.");

        var oneOff = result.Candidates.Single(candidate => candidate.Id == 0x555);
        Check(oneOff.Priority == IncidentSignaturePriority.Info &&
              oneOff.OccurrenceCount == 1,
            "One-off incident event was not kept visible as INFO.");

        Check(result.HighCount == 1 &&
              result.MediumCount == 2 &&
              result.EarliestHigh == stable,
            "Incident signature summary/ranking is incorrect.");

        Check(result.Warnings.Any(text =>
                text.Contains("CHAIN_WARNING", StringComparison.Ordinal)),
            "Event-chain quality warning was not propagated into signature analysis.");

        var twoIncidentResult = IncidentSignatureAnalyzer.Analyze(chains.Take(2).ToArray());
        Check(twoIncidentResult.Warnings.Any(text =>
                text.Contains("минимум 3", StringComparison.OrdinalIgnoreCase)),
            "Two-incident analysis did not warn about weak repeatability evidence.");

        var infoOnly = IncidentSignatureAnalyzer.Analyze(
        [
            Chain([Step(1, 0, 0x700, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.Info, "0", "1", false)]),
            Chain([Step(1, 10, 0x700, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.Info, "0", "1", false)]),
            Chain([Step(1, 20, 0x700, false, 0, IncidentTransitionKind.ByteChanged,
                IncidentTransitionPriority.Info, "0", "1", false)])
        ]);
        Check(infoOnly.Candidates.Single().Priority == IncidentSignaturePriority.Info,
            "INFO-only observations were incorrectly promoted.");

        var empty = IncidentSignatureAnalyzer.Analyze(
        [
            Chain([]),
            Chain([]),
            Chain([])
        ]);
        Check(empty.Candidates.Count == 0 &&
              empty.Warnings.Any(text => text.Contains("сигнатура", StringComparison.OrdinalIgnoreCase)),
            "Empty incident chains did not produce a clear warning.");

        var duplicateRejected = false;
        try
        {
            IncidentSignatureAnalyzer.Analyze(
            [
                Chain(
                [
                    Step(1, 0, 0x321, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0", "1", false),
                    Step(2, 10, 0x321, false, 0, IncidentTransitionKind.ByteChanged,
                        IncidentTransitionPriority.High, "0", "2", false)
                ]),
                Chain([])
            ]);
        }
        catch (InvalidOperationException)
        {
            duplicateRejected = true;
        }
        Check(duplicateRejected,
            "Ambiguous duplicate step inside one incident was silently accepted.");

        var tooFewRejected = false;
        try
        {
            IncidentSignatureAnalyzer.Analyze([Chain([])]);
        }
        catch (ArgumentException)
        {
            tooFewRejected = true;
        }
        Check(tooFewRejected,
            "Incident signature accepted fewer than two incidents.");
    }

    private static IncidentEventChainResult Chain(
        IReadOnlyList<IncidentEventChainStep> steps,
        IReadOnlyList<string>? warnings = null) =>
        new(steps, warnings ?? []);

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
