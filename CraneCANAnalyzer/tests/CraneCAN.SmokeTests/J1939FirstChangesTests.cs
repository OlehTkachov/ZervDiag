using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

internal static class J1939FirstChangesTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        SourceAddressSwapSuppressesExactIdLifecycle();
        PayloadChangeAcrossSourceAddressBecomesByteChange();
        Pdu1DestinationChangeRemainsDistinct();
        PriorityChangeIsNormalized();
        StandardOnlyIncidentReturnsExplicitWarning();
    }

    private static void SourceAddressSwapSuppressesExactIdLifecycle()
    {
        var marker =
            DateTimeOffset.UnixEpoch.AddHours(5);
        var frames = new List<CanFrame>();

        for (var index = 0; index < 10; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    -2000 + index * 100),
                0x18F004A1u,
                0x10));
        }

        for (var index = 0; index < 6; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    100 + index * 100),
                0x18F004B2u,
                0x10));
        }

        var incident =
            Incident(marker, frames);
        var raw =
            IncidentTransitionAnalyzer.Analyze(
                incident);
        var result =
            J1939FirstChangesAnalyzer.Analyze(
                incident,
                raw);

        Check(
            raw.Candidates.Any(candidate =>
                candidate.IsExtended &&
                candidate.Kind ==
                    IncidentTransitionKind.IdAppeared &&
                candidate.Id == 0x18F004B2u),
            "Raw exact-ID precondition failed: new Source Address was not observed as an ID appearance.");

        Check(
            result.Candidates.All(candidate =>
                candidate.Kind !=
                    IncidentTransitionKind.IdAppeared &&
                candidate.Kind !=
                    IncidentTransitionKind.PeriodicIdStopped),
            "PGN normalization incorrectly kept an SA-only exact-ID lifecycle event.");

        Check(
            result.SourceAddressChanges.Count == 1 &&
            result.SourceAddressChanges[0].Pgn ==
                0x0F004 &&
            result.SourceAddressChanges[0]
                .BaselineSourceAddresses
                .SequenceEqual(new[] { 0xA1 }) &&
            result.SourceAddressChanges[0]
                .SearchSourceAddresses
                .SequenceEqual(new[] { 0xB2 }),
            "PGN normalization did not preserve Source Address change metadata.");

        Check(
            result.SuppressedExactIdLifecycleCandidates >=
                1,
            "PGN normalization did not report suppressed exact-ID lifecycle candidates.");
    }

    private static void PayloadChangeAcrossSourceAddressBecomesByteChange()
    {
        var marker =
            DateTimeOffset.UnixEpoch.AddHours(6);
        var frames = new List<CanFrame>();

        for (var index = 0; index < 10; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    -2000 + index * 100),
                0x18F004A1u,
                0x10));
        }

        for (var index = 0; index < 4; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    100 + index * 100),
                0x18F004B2u,
                0x20));
        }

        var result =
            J1939FirstChangesAnalyzer.Analyze(
                Incident(marker, frames));

        var changed = result.Candidates
            .Single(candidate =>
                candidate.Pgn == 0x0F004 &&
                candidate.Kind ==
                    IncidentTransitionKind.ByteChanged &&
                candidate.DataIndex == 0);

        Check(
            changed.BaselineValue == "0x10" &&
            changed.ObservedValue == "0x20" &&
            changed.BaselineSourceAddresses
                .SequenceEqual(new[] { 0xA1 }) &&
            changed.SearchSourceAddresses
                .SequenceEqual(new[] { 0xB2 }),
            "PGN-normalized byte change lost payload or Source Address context.");

        Check(
            result.Candidates.All(candidate =>
                candidate.Kind !=
                    IncidentTransitionKind.IdAppeared),
            "Payload change across Source Address was incorrectly reported as PGN appearance.");
    }

    private static void Pdu1DestinationChangeRemainsDistinct()
    {
        var marker =
            DateTimeOffset.UnixEpoch.AddHours(7);
        var frames = new List<CanFrame>();

        for (var index = 0; index < 10; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    -2000 + index * 100),
                0x18EAFFA1u,
                0x55));
        }

        for (var index = 0; index < 4; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    100 + index * 100),
                0x18EA80B2u,
                0x55));
        }

        var result =
            J1939FirstChangesAnalyzer.Analyze(
                Incident(marker, frames));

        Check(
            result.Candidates.Any(candidate =>
                candidate.Pgn == 0x0EA00 &&
                candidate.DestinationAddress ==
                    0x80 &&
                candidate.Kind ==
                    IncidentTransitionKind.IdAppeared),
            "PDU1 Destination Address change was incorrectly collapsed by PGN normalization.");

        Check(
            result.SourceAddressChanges.Count == 0,
            "Different PDU1 Destination Address keys were incorrectly compared as one Source Address change.");
    }

    private static void PriorityChangeIsNormalized()
    {
        var marker =
            DateTimeOffset.UnixEpoch.AddHours(8);
        var frames = new List<CanFrame>();

        var priority3Id =
            (0x18F004A1u & 0x03FFFFFFu) |
            (3u << 26);
        var priority6Id =
            0x18F004B2u;

        for (var index = 0; index < 10; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    -2000 + index * 100),
                priority3Id,
                0x33));
        }

        for (var index = 0; index < 4; index++)
        {
            frames.Add(Frame(
                marker.AddMilliseconds(
                    100 + index * 100),
                priority6Id,
                0x33));
        }

        var normalizedA =
            J1939FirstChangesAnalyzer
                .NormalizeMessageKeyCanId(
                    priority3Id);
        var normalizedB =
            J1939FirstChangesAnalyzer
                .NormalizeMessageKeyCanId(
                    priority6Id);

        Check(
            normalizedA == normalizedB,
            "J1939 normalized message key still depends on Priority or Source Address.");

        var result =
            J1939FirstChangesAnalyzer.Analyze(
                Incident(marker, frames));

        Check(
            result.Candidates.Count == 0,
            "Priority/Source Address-only change produced a PGN-normalized First Changes candidate.");
    }

    private static void StandardOnlyIncidentReturnsExplicitWarning()
    {
        var marker =
            DateTimeOffset.UnixEpoch.AddHours(9);
        var frames = new List<CanFrame>();

        for (var index = 0; index < 10; index++)
        {
            frames.Add(new CanFrame
            {
                Timestamp =
                    marker.AddMilliseconds(
                        -2000 + index * 100),
                Channel = 0,
                Id = 0x123,
                IsExtended = false,
                Data = [0x11],
                Protocol =
                    BusProtocol.ClassicalCan,
                Direction =
                    CanDirection.Rx
            });
        }

        frames.Add(new CanFrame
        {
            Timestamp =
                marker.AddMilliseconds(100),
            Channel = 0,
            Id = 0x123,
            IsExtended = false,
            Data = [0x22],
            Protocol =
                BusProtocol.ClassicalCan,
            Direction =
                CanDirection.Rx
        });

        var result =
            J1939FirstChangesAnalyzer.Analyze(
                Incident(marker, frames));

        Check(
            result.Candidates.Count == 0 &&
            result.ExtendedBaselineFrameCount == 0 &&
            result.Warnings.Any(warning =>
                warning.Contains(
                    "нет Rx 29-bit",
                    StringComparison.Ordinal)),
            "Standard-only incident did not return an explicit empty J1939 PGN result.");
    }

    private static CanFrame Frame(
        DateTimeOffset timestamp,
        uint id,
        byte value) =>
        new()
        {
            Timestamp = timestamp,
            Channel = 0,
            Id = id,
            IsExtended = true,
            Data = [value],
            Protocol =
                BusProtocol.ClassicalCan,
            Direction =
                CanDirection.Rx
        };

    private static PreFaultIncident Incident(
        DateTimeOffset marker,
        IReadOnlyList<CanFrame> frames) =>
        new(
            Guid.NewGuid(),
            marker.AddSeconds(-10),
            marker.AddSeconds(5),
            marker.AddSeconds(5),
            [new IncidentMarker(
                marker,
                "test")],
            frames,
            []);

    private static void Check(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(
                message);
    }
}
