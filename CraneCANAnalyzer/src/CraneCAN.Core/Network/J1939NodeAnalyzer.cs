using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class J1939NodeAnalyzer
{
    public static IReadOnlyList<NetworkNodeSnapshot> Build(IReadOnlyList<CanFrame> frames)
    {
        return Array.Empty<NetworkNodeSnapshot>();
    }
}
