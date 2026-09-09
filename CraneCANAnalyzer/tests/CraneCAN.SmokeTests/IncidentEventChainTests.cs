using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

internal static class IncidentEventChainTests
{
    public static void Run()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var transition = new IncidentTransitionAnalysisResult(
            marker,
            marker.AddSeconds(-8),
            marker.AddSeconds(-1),
            marker.AddSeconds(-1),
            marker.AddSeconds(4),
            100,
            80,
            [],
            [
                Candidate(0x100, false, 0, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, -100, "0x00", "0x01"),
                Candidate(0x200, false, 1, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, 50, "0x00", "0x80"),
                Candidate(0x300, false, null, IncidentTransitionKind.PeriodicIdStopped,
                    IncidentTransitionPriority.Medium, 400, "period 100 ms", "stopped"),
                Candidate(0x400, false, null, IncidentTransitionKind.IdAppeared,
                    IncidentTransitionPriority.High, 1600, "absent", "present")
            ]);

        var profile = new MachineProfile
        {
            MachineName = "Test",
            KnownSignals =
            [
                new MachineSignal
                {
                    Name = "Joystick command",
                    CanId = 0x100,
                    StartByte = 0,
                    StartBit = 0,
                    BitLength = 8,
                    Confidence = SignalKnowledgeState.Confirmed
                },
                new MachineSignal
                {
                    Name = "Wrong format",
                    CanId = 0x100,
                    IsExtended = true,
                    StartByte = 0,
                    StartBit = 0,
                    BitLength = 8,
                    Confidence = SignalKnowledgeState.Confirmed
                }
            ],
            ExperimentalSignals =
            [
                new MachineSignal
                {
                    Name = "Enable field",
                    CanId = 0x200,
                    StartByte = 0,
                    StartBit = 4,
                    BitLength = 12,
                    Confidence = SignalKnowledgeState.Candidate
                }
            ]
        };

        var result = IncidentEventChainAnalyzer.Build(transition, profile);

        Check(result.Steps.Count == 4 &&
              result.Steps.Select(step => step.Sequence).SequenceEqual(new[] { 1, 2, 3, 4 }),
            "Event chain sequence was not preserved.");

        var joystick = result.Steps[0];
        Check(joystick.ProfileSignals.SequenceEqual(new[] { "Joystick command" }) &&
              joystick.HighestSignalConfidence == SignalKnowledgeState.Confirmed,
            "Confirmed Machine Profile signal was not mapped to the observed step.");

        var enable = result.Steps[1];
        Check(enable.ProfileSignals.Contains("Enable field") &&
              enable.HighestSignalConfidence == SignalKnowledgeState.Candidate,
            "Multi-byte profile signal did not match an overlapping DATA byte.");

        Check(!joystick.ProfileSignals.Contains("Wrong format"),
            "Standard/Extended identifiers were incorrectly merged during profile matching.");

        var stopped = result.Steps[2];
        Check(stopped.BreakpointCandidate &&
              stopped.BreakpointReason.Contains("периодический", StringComparison.Ordinal),
            "Periodic-ID stop was not highlighted as an objective breakpoint candidate.");

        var largeGap = result.Steps[3];
        Check(largeGap.BreakpointCandidate &&
              largeGap.BreakpointReason.Contains("пауза", StringComparison.Ordinal) &&
              Math.Abs((largeGap.DeltaFromPreviousMilliseconds ?? 0) - 1200) < 0.001,
            "Large temporal gap between significant observed steps was not highlighted.");

        var noProfile = IncidentEventChainAnalyzer.Build(transition, null);
        Check(noProfile.ProfileAnnotatedCount == 0 &&
              noProfile.Warnings.Any(text => text.Contains("не загружен", StringComparison.Ordinal)),
            "Missing Machine Profile warning was not emitted.");

        var warningTransition = transition with
        {
            Warnings = ["CAPTURE_UNCERTAIN"]
        };
        var warningChain = IncidentEventChainAnalyzer.Build(warningTransition, profile);
        Check(warningChain.Warnings.Contains("CAPTURE_UNCERTAIN"),
            "Incident transition quality warnings were not propagated.");

        var infoGapTransition = transition with
        {
            Candidates =
            [
                Candidate(0x500, false, 0, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.Info, 0, "0", "1"),
                Candidate(0x501, false, 0, IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High, 2000, "0", "1")
            ]
        };
        var infoGap = IncidentEventChainAnalyzer.Build(infoGapTransition, profile);
        Check(!infoGap.Steps[1].BreakpointCandidate,
            "Gap adjacent to an INFO-only step was incorrectly promoted to a breakpoint.");
    }

    private static IncidentTransitionCandidate Candidate(
        uint id,
        bool extended,
        int? dataIndex,
        IncidentTransitionKind kind,
        IncidentTransitionPriority priority,
        double reactionMilliseconds,
        string baseline,
        string observed) => new(
            id,
            extended,
            dataIndex,
            kind,
            priority,
            reactionMilliseconds,
            baseline,
            observed,
            100,
            3,
            "test");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
