using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;
using CraneCAN.Core.Profiles;

internal static class PassiveNetworkDiscoverySmokeTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestJ1939AddressClaimAndName();
        TestCanopenHeartbeatAndBootUp();
        TestHeartbeatDropoutAndReturn();
        TestMixedProtocolDetection();
        TestGoodFaultNetworkComparison();
        TestMachineProfileNetworkMerge();
    }

    private static void TestJ1939AddressClaimAndName()
    {
        var rawName = BuildName(0x12345, 0x123, 0, 0, 0x2A, 0x11, 0, 2, true);
        var data = BitConverter.GetBytes(rawName);
        var frames = new[]
        {
            Frame(0, 0x18EEFF80, true, data),
            Frame(100, 0x18F00480, true, 1,2,3,4,5,6,7,8),
            Frame(200, 0x18FEF680, true, 1,2,3,4,5,6,7,8),
            Frame(300, 0x18EF2180, true, 1,2,3,4,5,6,7,8)
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "j1939-test", 250000);
        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.J1939Likely,
            "Address Claim must produce J1939 likely classification.");
        var node = snapshot.Nodes.Single(item => item.Address == 0x80);
        Require(node.Protocol == NetworkNodeProtocol.J1939, "Address Claim node must be J1939.");
        Require(node.J1939Name is not null, "J1939 NAME must be decoded.");
        Require(node.J1939Name!.ManufacturerCode == 0x123, "J1939 manufacturer code decode failed.");
        Require(node.J1939Name.Function == 0x2A, "J1939 function decode failed.");
        Require(node.DirectedPeers.Contains("0x21"), "PDU1 destination peer must be preserved.");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.AddressClaim),
            "Address Claim event missing.");
    }

    private static void TestCanopenHeartbeatAndBootUp()
    {
        var frames = new[]
        {
            Frame(0, 0x701, false, 0x00),
            Frame(100, 0x701, false, 0x05),
            Frame(200, 0x701, false, 0x05),
            Frame(300, 0x701, false, 0x05),
            Frame(400, 0x701, false, 0x05),
            Frame(110, 0x181, false, 0x11,0x22),
            Frame(210, 0x181, false, 0x12,0x22)
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "canopen-test", 125000);
        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.CanopenLikely,
            "Heartbeat plus Boot-up must produce CANopen likely classification.");
        var node = snapshot.Nodes.Single(item => item.Protocol == NetworkNodeProtocol.Canopen && item.Address == 1);
        Require(node.BootUpObserved, "CANopen Boot-up must be recorded.");
        Require(node.CanopenNmtState == "Operational", "CANopen heartbeat state decode failed.");
        Require(node.HeartbeatPeriodMilliseconds is > 90 and < 110,
            "CANopen heartbeat period should be close to 100 ms.");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.CanopenBootUp),
            "CANopen Boot-up event missing.");
    }

    private static void TestHeartbeatDropoutAndReturn()
    {
        var frames = new List<CanFrame>
        {
            Frame(0, 0x701, false, 0x00),
            Frame(100, 0x701, false, 0x05),
            Frame(200, 0x701, false, 0x05),
            Frame(300, 0x701, false, 0x05),
            Frame(400, 0x701, false, 0x05),
            Frame(1000, 0x701, false, 0x05),
            Frame(1100, 0x701, false, 0x05),
            Frame(1200, 0x701, false, 0x05),
            Frame(1300, 0x701, false, 0x05)
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "dropout-test", 125000);
        var stream = snapshot.PeriodicStreams.Single(item => item.Id == 0x701);
        Require(stream.IsPeriodic, "A single dropout must not destroy periodic stream learning.");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.HeartbeatLost),
            "Heartbeat dropout must create HeartbeatLost event.");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.HeartbeatReturned),
            "Heartbeat recovery must create HeartbeatReturned event.");
    }

    private static void TestMixedProtocolDetection()
    {
        var name = BitConverter.GetBytes(BuildName(1, 1, 0, 0, 1, 1, 0, 0, true));
        var frames = new List<CanFrame>
        {
            Frame(0, 0x18EEFF20, true, name),
            Frame(10, 0x701, false, 0x00),
            Frame(110, 0x701, false, 0x05),
            Frame(210, 0x701, false, 0x05),
            Frame(310, 0x701, false, 0x05)
        };
        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "mixed-test");
        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.MixedOrGateway,
            "Strong J1939 and CANopen evidence must classify as mixed/gateway.");
    }

    private static void TestGoodFaultNetworkComparison()
    {
        var good = new CanNetworkSnapshot
        {
            ProtocolEstimate = NetworkProtocolEstimate.J1939Likely,
            Nodes =
            [
                new NetworkNodeSnapshot { NodeKey = "J1939:20", Protocol = NetworkNodeProtocol.J1939, Address = 0x20, State = "ACTIVE", Identity = "A" },
                new NetworkNodeSnapshot { NodeKey = "J1939:21", Protocol = NetworkNodeProtocol.J1939, Address = 0x21, State = "ACTIVE", Identity = "B" }
            ],
            Flows = [new NetworkFlowSnapshot { SourceAddress = 0x20, DestinationAddress = 0x21, Kind = "PDU1" }]
        };
        var fault = good with
        {
            Nodes = [good.Nodes[0]],
            Flows = []
        };

        var comparison = NetworkSnapshotComparer.Compare(good, fault);
        Require(comparison.Differences.Any(item => item.Kind == NetworkSnapshotDifferenceKind.NodeDisappeared && item.Key == "J1939:21"),
            "GOOD/FAULT must report disappeared node.");
        Require(comparison.Differences.Any(item => item.Kind == NetworkSnapshotDifferenceKind.FlowDisappeared),
            "GOOD/FAULT must report disappeared logical flow.");
    }

    private static void TestMachineProfileNetworkMerge()
    {
        var snapshot = new CanNetworkSnapshot
        {
            ProtocolEstimate = NetworkProtocolEstimate.J1939Likely,
            ProtocolConfidence = 0.8,
            Nodes = [new NetworkNodeSnapshot { NodeKey = "J1939:20", Protocol = NetworkNodeProtocol.J1939, Address = 0x20, AddressText = "SA 0x20", Identity = "observed", State = "ACTIVE", Confidence = 0.8 }]
        };
        var current = new MachineNetworkKnowledge
        {
            Nodes = [new MachineNetworkNodeEvidence { NodeKey = "J1939:20", Identity = "documented ECU", Source = NetworkEvidenceSource.Documentation, Confidence = 1 }]
        };

        var merged = MachineNetworkKnowledge.MergeAutomatic(current, snapshot);
        Require(merged.Nodes.Single().Identity == "documented ECU",
            "Automatic discovery must not overwrite documented network evidence.");
    }

    private static CanFrame Frame(double milliseconds, uint id, bool extended, params byte[] data) => new()
    {
        Timestamp = Origin.AddMilliseconds(milliseconds),
        Channel = 0,
        Id = id,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx,
        IsExtended = extended
    };

    private static ulong BuildName(
        uint identity,
        ushort manufacturer,
        byte ecuInstance,
        byte functionInstance,
        byte function,
        byte vehicleSystem,
        byte vehicleSystemInstance,
        byte industryGroup,
        bool arbitraryAddressCapable)
    {
        ulong value = identity & 0x1FFFFFu;
        value |= ((ulong)manufacturer & 0x7FFu) << 21;
        value |= ((ulong)ecuInstance & 0x07u) << 32;
        value |= ((ulong)functionInstance & 0x1Fu) << 35;
        value |= (ulong)function << 40;
        value |= ((ulong)vehicleSystem & 0x7Fu) << 49;
        value |= ((ulong)vehicleSystemInstance & 0x0Fu) << 56;
        value |= ((ulong)industryGroup & 0x07u) << 60;
        if (arbitraryAddressCapable) value |= 1UL << 63;
        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
