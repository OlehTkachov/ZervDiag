using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

internal static class NetworkFalsePositiveSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        TestEventDrivenIdDoesNotBecomeMissingNode();
        TestCanopenLikeIdsDoNotProveCanopen();
    }

    private static void TestEventDrivenIdDoesNotBecomeMissingNode()
    {
        var origin = new DateTimeOffset(2026, 9, 12, 12, 30, 0, TimeSpan.Zero);
        var frames = new[]
        {
            Frame(origin, 0x123, false),
            Frame(origin.AddMilliseconds(700), 0x123, false),
            Frame(origin.AddMilliseconds(2600), 0x123, false),
            Frame(origin.AddMilliseconds(8100), 0x123, false)
        };
        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "event-driven");
        var stream = snapshot.PeriodicStreams.Single(item => item.Id == 0x123 && !item.IsExtended);
        Require(!stream.IsConfidentPeriodic, "Irregular event-driven ID must not become confident periodic.");
        Require(!snapshot.Events.Any(item => item.Kind is NetworkEventKind.NodeDisappeared or NetworkEventKind.PeriodicMessageLost),
            "Irregular event-driven ID must not trigger missing-node events.");
    }

    private static void TestCanopenLikeIdsDoNotProveCanopen()
    {
        var origin = new DateTimeOffset(2026, 9, 12, 12, 40, 0, TimeSpan.Zero);
        var frames = new[]
        {
            Frame(origin, 0x181, false),
            Frame(origin.AddMilliseconds(20), 0x201, false),
            Frame(origin.AddMilliseconds(40), 0x182, false)
        };
        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "canopen-like");
        Require(snapshot.ProtocolEstimate == NetworkProtocolEstimate.ClassicalCanGeneric,
            "PDO-range identifiers without strong evidence must remain generic.");
        Require(!snapshot.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.Canopen),
            "PDO-range identifiers alone must not create CANopen nodes.");
    }

    private static CanFrame Frame(DateTimeOffset time, uint id, bool extended) => new()
    {
        Timestamp = time,
        Channel = 0,
        Id = id,
        Data = [1, 2, 3, 4],
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx,
        IsExtended = extended
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
