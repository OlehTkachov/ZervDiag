using System.Runtime.CompilerServices;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

internal static class IncidentNetworkAnalyzerSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var origin = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        var frames = new List<CanFrame>();
        for (var ms = -7000; ms < -250; ms += 100)
        {
            frames.Add(Frame(origin.AddMilliseconds(ms), 0x701, false, 0x05));
            frames.Add(Frame(origin.AddMilliseconds(ms), 0x18F00420, true, 1, 2, 3, 4, 5, 6, 7, 8));
        }
        for (var ms = 0; ms < 4000; ms += 100)
            frames.Add(Frame(origin.AddMilliseconds(ms), 0x18F00420, true, 1, 2, 3, 4, 5, 6, 7, 8));

        var incident = new PreFaultIncident(
            Guid.NewGuid(),
            origin.AddSeconds(-10),
            origin.AddSeconds(5),
            origin.AddSeconds(5),
            [new IncidentMarker(origin, "fault")],
            frames.OrderBy(frame => frame.Timestamp).ToArray(),
            []);

        var result = IncidentNetworkAnalyzer.Analyze(incident, 250000);
        Require(result.Before.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.Canopen && node.Address == 1),
            "BEFORE must contain CANopen node 1.");
        Require(result.Changes.Differences.Any(item =>
                item.Kind == NetworkSnapshotDifferenceKind.NodeDisappeared && item.Key == "CANOPEN:01"),
            "Incident network analysis must report a node that disappears after marker.");
    }

    private static CanFrame Frame(DateTimeOffset timestamp, uint id, bool extended, params byte[] data) => new()
    {
        Timestamp = timestamp,
        Channel = 0,
        Id = id,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx,
        IsExtended = extended
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
