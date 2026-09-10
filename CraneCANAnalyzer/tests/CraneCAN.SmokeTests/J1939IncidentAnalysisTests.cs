using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class J1939IncidentAnalysisTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        TraceAnalysisMatchesPgnAcrossSourceAddresses();
        IncidentEventChainMapsPgnAndEngineeringTransition();
        ProfileTimelineCombinesSourceAddresses();
        Pdu1TraceAnalysisKeepsDestinationAddressFixed();
    }

    private static void TraceAnalysisMatchesPgnAcrossSourceAddresses()
    {
        var signal = EngineSpeedSignal();
        var profile = new MachineProfile
        {
            ExperimentalSignals = [signal]
        };
        var t0 = DateTimeOffset.UnixEpoch.AddHours(1);
        var frames = new[]
        {
            EngineSpeedFrame(t0, 0x18F004A1u, 800),
            EngineSpeedFrame(t0.AddMilliseconds(20), 0x18F004B2u, 1000),
            new CanFrame
            {
                Timestamp = t0.AddMilliseconds(30),
                Channel = 0,
                Id = 0x18F004C3u,
                IsExtended = true,
                Data = [0x00, 0x00, 0x00, 0x40],
                Protocol = BusProtocol.ClassicalCan,
                Direction = CanDirection.Rx
            },
            EngineSpeedFrame(t0.AddMilliseconds(40), 0x18F005A1u, 1500),
            EngineSpeedFrame(t0.AddMilliseconds(50), 0x18F004D4u, 1200) with
            {
                Direction = CanDirection.Tx
            }
        };

        var result = J1939TraceSignalAnalyzer.Analyze(frames, profile);
        var summary = result.Signals.Single();

        Check(
            result.ExtendedRxFrameCount == 4 &&
            result.FramesMatchingProfile == 3 &&
            result.ObservedSignalCount == 1,
            "J1939 TRC frame accounting is incorrect.");
        Check(
            summary.Pgn == 0x0F004 &&
            summary.Spn == 190 &&
            summary.MatchingFrameCount == 3 &&
            summary.DecodedFrameCount == 2 &&
            summary.ShortFrameCount == 1 &&
            summary.SourceAddresses.SequenceEqual(new[] { 0xA1, 0xB2, 0xC3 }) &&
            Math.Abs(summary.MinimumEngineeringValue!.Value - 800) < 0.000001 &&
            Math.Abs(summary.MaximumEngineeringValue!.Value - 1000) < 0.000001 &&
            Math.Abs(summary.LatestEngineeringValue!.Value - 1000) < 0.000001,
            "J1939 TRC PGN matching/engineering aggregation is incorrect.");
    }

    private static void IncidentEventChainMapsPgnAndEngineeringTransition()
    {
        var marker = DateTimeOffset.UnixEpoch.AddHours(2);
        var signal = EngineSpeedSignal();
        var profile = new MachineProfile
        {
            ExperimentalSignals = [signal]
        };
        var baseline = EngineSpeedFrame(
            marker.AddSeconds(-2),
            0x18F004B2u,
            800);
        var observed = EngineSpeedFrame(
            marker.AddMilliseconds(100),
            0x18F004B2u,
            1000);
        var incident = Incident(marker, [baseline, observed]);
        var transition = new IncidentTransitionAnalysisResult(
            marker,
            marker.AddSeconds(-8),
            marker.AddSeconds(-1),
            marker.AddSeconds(-1),
            marker.AddSeconds(4),
            1,
            1,
            [],
            [
                new IncidentTransitionCandidate(
                    0x18F004B2u,
                    true,
                    3,
                    IncidentTransitionKind.ByteChanged,
                    IncidentTransitionPriority.High,
                    100,
                    "0x00",
                    "0x40",
                    100,
                    2,
                    "engine speed byte changed")
            ]);

        var baseChain = IncidentEventChainAnalyzer.Build(
            transition,
            profile);
        Check(
            baseChain.Steps.Single().ProfileSignals.Count == 0,
            "Test precondition failed: exact-ID chain unexpectedly matched canonical J1939 ID.");

        var enriched = J1939IncidentAnalyzer.EnrichEventChain(
            baseChain,
            transition,
            incident,
            profile);
        var step = enriched.Chain.Steps.Single();
        var annotation = enriched.Annotations.Single().Value;

        Check(
            step.ProfileSignals.SequenceEqual(new[] { "EngineSpeed" }) &&
            step.HighestSignalConfidence == SignalKnowledgeState.Probable,
            "J1939 event chain did not map canonical Profile signal by PGN.");
        Check(
            annotation.Pgn == 0x0F004 &&
            annotation.Spns.SequenceEqual(new[] { 190 }) &&
            annotation.ObservedSourceAddress == 0xB2 &&
            annotation.ProtocolText.Contains("PGN 0x0F004", StringComparison.Ordinal) &&
            annotation.ProtocolText.Contains("SPN 190", StringComparison.Ordinal) &&
            annotation.ProtocolText.Contains("SA 0xB2", StringComparison.Ordinal),
            "J1939 event-chain PGN/SPN/SA annotation is incorrect.");
        Check(
            annotation.EngineeringTransition.Contains("800", StringComparison.Ordinal) &&
            annotation.EngineeringTransition.Contains("1000 rpm", StringComparison.Ordinal) &&
            annotation.EngineeringTransition.Contains("SPN 190", StringComparison.Ordinal),
            "J1939 event-chain engineering transition was not decoded from baseline/observed frames.");
        Check(
            enriched.Chain.Warnings.Any(warning =>
                warning.Contains("Source Address", StringComparison.Ordinal)),
            "J1939 PGN-level event-chain matching warning is missing.");
    }

    private static void ProfileTimelineCombinesSourceAddresses()
    {
        var marker = DateTimeOffset.UnixEpoch.AddHours(3);
        var signal = EngineSpeedSignal();
        var frames = new[]
        {
            EngineSpeedFrame(marker.AddMilliseconds(-200), 0x18F004A1u, 800),
            EngineSpeedFrame(marker.AddMilliseconds(-100), 0x18F004B2u, 900),
            EngineSpeedFrame(marker.AddMilliseconds(100), 0x18F004A1u, 1000),
            EngineSpeedFrame(marker.AddMilliseconds(200), 0x18F004B2u, 1100),
            EngineSpeedFrame(marker.AddMilliseconds(250), 0x18F005A1u, 2000)
        };
        var incident = Incident(marker, frames);
        var step = new IncidentEventChainStep(
            1,
            100,
            null,
            0x18F004A1u,
            true,
            3,
            IncidentTransitionKind.ByteChanged,
            IncidentTransitionPriority.High,
            "0x20",
            "0x40",
            [],
            null,
            false,
            string.Empty,
            "J1939 timeline");

        var result = J1939IncidentAnalyzer.AnalyzeProfileTimeline(
            incident,
            step,
            signal);

        Check(
            result.Pgn == 0x0F004 &&
            result.Spn == 190 &&
            result.SourceAddresses.SequenceEqual(new[] { 0xA1, 0xB2 }) &&
            result.Timeline.MatchingFrameCount == 4 &&
            result.Timeline.SourceFrameCount == 4 &&
            result.Timeline.RenderedPoints.Count == 4,
            "J1939 incident Profile Timeline did not merge matching Source Addresses by PGN.");
        Check(
            result.Timeline.RenderedPoints.Select(point => point.EngineeringValue)
                .SequenceEqual(new double[] { 800, 900, 1000, 1100 }) &&
            result.Timeline.Warnings.Any(warning =>
                warning.Contains("нескольких Source Address", StringComparison.Ordinal)),
            "J1939 incident Profile Timeline values/warnings are incorrect.");
    }

    private static void Pdu1TraceAnalysisKeepsDestinationAddressFixed()
    {
        var signal = new MachineSignal
        {
            Name = "RequestByte",
            CanId = 0x18EAFF00u,
            IsExtended = true,
            StartByte = 0,
            StartBit = 0,
            BitLength = 8,
            ByteOrder = SignalByteOrder.LittleEndian,
            Scale = 1,
            Protocol = "J1939",
            J1939Pgn = 0x0EA00,
            J1939Spn = 999,
            Confidence = SignalKnowledgeState.Probable
        };
        var profile = new MachineProfile
        {
            ExperimentalSignals = [signal]
        };
        var t0 = DateTimeOffset.UnixEpoch.AddHours(4);
        var frames = new[]
        {
            SimpleFrame(t0, 0x18EAFF45u, [0x11]),
            SimpleFrame(t0.AddMilliseconds(10), 0x18EA8045u, [0x22])
        };

        var result = J1939TraceSignalAnalyzer.Analyze(frames, profile);
        var summary = result.Signals.Single();

        Check(
            summary.MatchingFrameCount == 1 &&
            summary.DecodedFrameCount == 1 &&
            Math.Abs(summary.LatestEngineeringValue!.Value - 0x11) < 0.000001,
            "J1939 PDU1 trace analysis incorrectly merged a different Destination Address.");
    }

    private static MachineSignal EngineSpeedSignal() =>
        new()
        {
            Name = "EngineSpeed",
            CanId = 0x18F00400u,
            IsExtended = true,
            StartByte = 3,
            StartBit = 0,
            BitLength = 16,
            ByteOrder = SignalByteOrder.LittleEndian,
            IsSigned = false,
            Scale = 0.125,
            Offset = 0,
            Unit = "rpm",
            Protocol = "J1939",
            J1939Pgn = 0x0F004,
            J1939Spn = 190,
            Confidence = SignalKnowledgeState.Probable
        };

    private static CanFrame EngineSpeedFrame(
        DateTimeOffset timestamp,
        uint id,
        double rpm)
    {
        var raw = checked((ushort)Math.Round(rpm / 0.125));
        return SimpleFrame(
            timestamp,
            id,
            [
                0x00,
                0x00,
                0x00,
                (byte)(raw & 0xFF),
                (byte)(raw >> 8),
                0x00,
                0x00,
                0x00
            ]);
    }

    private static CanFrame SimpleFrame(
        DateTimeOffset timestamp,
        uint id,
        byte[] data) =>
        new()
        {
            Timestamp = timestamp,
            Channel = 0,
            Id = id,
            IsExtended = true,
            Data = data,
            Protocol = BusProtocol.ClassicalCan,
            Direction = CanDirection.Rx
        };

    private static PreFaultIncident Incident(
        DateTimeOffset marker,
        IReadOnlyList<CanFrame> frames) =>
        new(
            Guid.NewGuid(),
            marker.AddSeconds(-10),
            marker.AddSeconds(5),
            marker.AddSeconds(5),
            [new IncidentMarker(marker, "test")],
            frames,
            []);

    private static void Check(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
