using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

internal static class J1939TransportProtocolDetectionSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        TestFieldLikeTpTrafficIsJ1939();
        TestInvalidTpControlDoesNotProveJ1939();
    }

    private static void TestFieldLikeTpTrafficIsJ1939()
    {
        var origin = new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);
        var frames = new[]
        {
            Frame(origin,                  0x0CF00300, [1, 2, 3, 4, 5, 6, 7, 8]),
            Frame(origin.AddMilliseconds(5),  0x0CF00400, [1, 2, 3, 4, 5, 6, 7, 8]),
            Frame(origin.AddMilliseconds(10), 0x0CF00A00, [1, 2, 3, 4, 5, 6, 7, 8]),
            Frame(origin.AddMilliseconds(15), 0x18012101, [1, 2, 3, 4, 5, 6, 7, 8]),
            Frame(origin.AddMilliseconds(20), 0x18032101, [1, 2, 3, 4, 5, 6, 7, 8]),
            Frame(origin.AddMilliseconds(25), 0x18EA0001, [0x00, 0xEF, 0x00, 0, 0, 0, 0, 0]),

            // Field trace pattern: SA 0x20 sends J1939 TP.CM (PGN 0xEC00)
            // followed by TP.DT (PGN 0xEB00), plus the transported PDU1 PGN.
            Frame(origin.AddMilliseconds(30), 0x18ECFF20, [0x20, 0x09, 0x00, 0x02, 0xFF, 0x00, 0xEF, 0x00]),
            Frame(origin.AddMilliseconds(35), 0x18EBFF20, [0x01, 1, 2, 3, 4, 5, 6, 7]),
            Frame(origin.AddMilliseconds(40), 0x18EBFF20, [0x02, 8, 9, 0, 0, 0, 0, 0]),
            Frame(origin.AddMilliseconds(45), 0x18EF2120, [1, 2, 3, 4, 5, 6, 7, 8])
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "charge-acc-lpu-regression");

        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.J1939Likely,
            $"Coherent TP.CM/TP.DT traffic must classify as J1939Likely, got {snapshot.ProtocolEstimate}.");
        Require(snapshot.ProtocolConfidence >= 0.55,
            "Coherent J1939 transport traffic must be strong protocol evidence.");
        Require(snapshot.ProtocolEvidence.Any(item => item.Code == "J1939_TP_SESSION"),
            "J1939 TP session evidence is missing.");
        Require(snapshot.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.J1939 && node.Address == 0x20),
            "J1939 source address 0x20 must be represented as a J1939 node.");
        Require(snapshot.Flows.Any(flow => flow.SourceAddress == 0x20),
            "J1939 flows for source address 0x20 are missing.");
        Require(!snapshot.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.GenericExtended),
            "A strongly identified J1939 network must not fall back to GenericExtended nodes.");
    }

    private static void TestInvalidTpControlDoesNotProveJ1939()
    {
        var origin = new DateTimeOffset(2026, 9, 14, 6, 10, 0, TimeSpan.Zero);
        var frames = new[]
        {
            Frame(origin, 0x18ECFF20, [0x42, 0, 0, 0, 0, 0, 0, 0]),
            Frame(origin.AddMilliseconds(20), 0x18EBFF20, [0x01, 1, 2, 3, 4, 5, 6, 7])
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "invalid-tp-control");
        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.ClassicalCanGeneric,
            "EC00/EB00-shaped identifiers with an invalid TP.CM control byte must remain generic.");
        Require(!snapshot.ProtocolEvidence.Any(item => item.Code == "J1939_TP_SESSION"),
            "Invalid TP.CM control byte must not create J1939 TP session evidence.");
    }

    private static CanFrame Frame(DateTimeOffset timestamp, uint id, byte[] data) => new()
    {
        Timestamp = timestamp,
        Channel = 0,
        Id = id,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx,
        IsExtended = true
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
