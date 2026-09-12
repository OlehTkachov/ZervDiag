using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

internal static class J1939NetworkManagementSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var origin = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var nameA = BitConverter.GetBytes(0x8123456789ABCDEFUL);
        var nameB = BitConverter.GetBytes(0x823456789ABCDEFFUL);
        var frames = new[]
        {
            Frame(origin, 0x18EEFF20, nameA),
            Frame(origin.AddMilliseconds(10), 0x18EEFF20, nameB),
            Frame(origin.AddMilliseconds(20), 0x18EEFFFE, nameA)
        };

        var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "j1939-nm");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.AddressConflict && item.NodeKey == "J1939:20"),
            "Two NAME values on the same SA must produce AddressConflict.");
        Require(snapshot.Events.Any(item => item.Kind == NetworkEventKind.AddressCannotClaim && item.NodeKey == "J1939:FE"),
            "SA 0xFE Address Claim must produce AddressCannotClaim.");
    }

    private static CanFrame Frame(DateTimeOffset time, uint id, byte[] data) => new()
    {
        Timestamp = time,
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
