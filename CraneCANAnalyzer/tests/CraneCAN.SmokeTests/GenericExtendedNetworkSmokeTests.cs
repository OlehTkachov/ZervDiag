using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

internal static class GenericExtendedNetworkSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var origin = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
        var frames = new[]
        {
            Frame(origin, 0x18FF1020),
            Frame(origin.AddMilliseconds(100), 0x18FF1120),
            Frame(origin.AddMilliseconds(200), 0x18FF1021)
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "generic-ext");
        if (snapshot.ProtocolEstimate != NetworkProtocolEstimate.ClassicalCanGeneric)
            throw new InvalidOperationException("Weak Extended traffic must remain generic.");
        if (snapshot.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.J1939))
            throw new InvalidOperationException("Weak Extended traffic must not be labelled as confirmed J1939 nodes.");
        if (!snapshot.Nodes.Any(node => node.Protocol == NetworkNodeProtocol.GenericExtended && node.Address == 0x20))
            throw new InvalidOperationException("Generic Extended suffix group 0x20 is missing.");
    }

    private static CanFrame Frame(DateTimeOffset timestamp, uint id) => new()
    {
        Timestamp = timestamp,
        Channel = 0,
        Id = id,
        Data = [1, 2, 3, 4, 5, 6, 7, 8],
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx,
        IsExtended = true
    };
}
