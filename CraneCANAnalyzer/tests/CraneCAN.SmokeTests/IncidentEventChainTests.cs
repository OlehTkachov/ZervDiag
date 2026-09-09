using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
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

        RunTimelineTests(marker);
    }

    private static void RunTimelineTests(DateTimeOffset marker)
    {
        var frames = new List<CanFrame>();
        for (var index = 0; index < 120; index++)
        {
            var value = index switch
            {
                < 40 => (byte)10,
                < 80 => (byte)200,
                _ => (byte)30
            };
            frames.Add(TimelineFrame(
                marker.AddMilliseconds(-3000 + index * 50),
                0x555,
                false,
                2,
                value));
        }

        // Same numeric ID but Extended: must not contaminate Standard plot.
        frames.Add(TimelineFrame(marker, 0x555, true, 2, 250));
        // Too short DATA for DATA[1]: must be ignored.
        frames.Add(new CanFrame
        {
            Timestamp = marker.AddMilliseconds(10),
            Channel = 0,
            Id = 0x555,
            IsExtended = false,
            Data = [0xAA],
            Protocol = BusProtocol.ClassicalCan,
            Direction = CanDirection.Rx
        });
        // Tx frame: must be ignored.
        frames.Add(TimelineFrame(marker.AddMilliseconds(20), 0x555, false, 2, 240) with
        {
            Direction = CanDirection.Tx
        });

        var incident = new PreFaultIncident(
            Guid.NewGuid(),
            marker.AddSeconds(-10),
            marker.AddSeconds(5),
            marker.AddSeconds(5),
            [new IncidentMarker(marker, "fault")],
            frames,
            []);

        var step = new IncidentEventChainStep(
            1,
            0,
            null,
            0x555,
            false,
            1,
            IncidentTransitionKind.ByteChanged,
            IncidentTransitionPriority.High,
            "0x0A",
            "0xC8",
            [],
            null,
            false,
            string.Empty,
            "timeline");

        var timeline = IncidentSignalTimelineAnalyzer.Analyze(
            incident,
            step,
            maximumRenderedPoints: 24);

        Check(timeline.SourceFrameCount == 120 &&
              timeline.RenderedPoints.Count <= 24 &&
              timeline.MinimumRawValue == 10 &&
              timeline.MaximumRawValue == 200,
            "Incident DATA timeline filtering/downsampling is incorrect.");
        Check(Math.Abs(timeline.RenderedPoints[0].RelativeMilliseconds - (-3000)) < 0.001 &&
              Math.Abs(timeline.RenderedPoints[^1].RelativeMilliseconds - 2950) < 0.001,
            "Incident DATA timeline did not preserve first/last samples.");
        Check(timeline.RenderedPoints.Any(point => point.RawValue == 200) &&
              timeline.RenderedPoints.Any(point => point.RawValue == 10) &&
              timeline.RenderedPoints.Any(point => point.RawValue == 30),
            "Incident DATA timeline downsampling lost an observed plateau/extreme.");
        Check(timeline.Warnings.Any(text => text.Contains("визуализации", StringComparison.OrdinalIgnoreCase)),
            "Timeline downsampling warning was not emitted.");

        var invalidStepRejected = false;
        try
        {
            IncidentSignalTimelineAnalyzer.Analyze(
                incident,
                step with { DataIndex = null, Kind = IncidentTransitionKind.IdAppeared });
        }
        catch (InvalidOperationException)
        {
            invalidStepRejected = true;
        }
        Check(invalidStepRejected,
            "Incident timeline accepted a non-DATA event.");
    }

    private static CanFrame TimelineFrame(
        DateTimeOffset timestamp,
        uint id,
        bool extended,
        int dlc,
        byte selectedValue)
    {
        var data = dlc == 1
            ? new byte[] { selectedValue }
            : new byte[] { 0x11, selectedValue };
        return new CanFrame
        {
            Timestamp = timestamp,
            Channel = 0,
            Id = id,
            IsExtended = extended,
            Data = data,
            Protocol = BusProtocol.ClassicalCan,
            Direction = CanDirection.Rx
        };
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
