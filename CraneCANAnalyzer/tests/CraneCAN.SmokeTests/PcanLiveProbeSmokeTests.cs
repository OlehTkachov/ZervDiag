using System.Runtime.CompilerServices;
using CraneCAN.App;
using CraneCAN.Driver.PcanBasic;

internal static class PcanLiveProbeSmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var bitrates = PcanLiveProbe.CommonBitrates;
        Require(bitrates.Count > 0, "PCAN passive probe bitrate list is empty.");
        Require(bitrates[0] == 250_000, "The field-priority PCAN bitrate should be checked first.");
        Require(bitrates.Distinct().Count() == bitrates.Count, "PCAN passive probe bitrate list contains duplicates.");
        foreach (var bitrate in bitrates)
            Require(PcanBasicCanDriver.TryMapBitrate(bitrate, out _), $"Unsupported probe bitrate: {bitrate}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
