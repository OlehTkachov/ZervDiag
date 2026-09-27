using System.Runtime.InteropServices;
using CraneCAN.Driver.PcanBasic;

internal static class PcanInteropContractTests
{
    public static void Run()
    {
        var assembly = typeof(PcanBasicCanDriver).Assembly;
        var statusType = assembly.GetType("CraneCAN.Driver.PcanBasic.PcanStatus", throwOnError: true)!;
        var messageType = assembly.GetType("CraneCAN.Driver.PcanBasic.PcanMessage", throwOnError: true)!;
        var timestampType = assembly.GetType("CraneCAN.Driver.PcanBasic.PcanTimestamp", throwOnError: true)!;

        uint Status(string name) => Convert.ToUInt32(Enum.Parse(statusType, name));

        Require(Status("BusPassive") == 0x00040000U,
            "PCAN BUSPASSIVE constant must be 0x00040000.");
        Require(Status("IllegalMode") == 0x00080000U,
            "PCAN ILLMODE constant must be 0x00080000.");
        Require(Status("Initialize") == 0x04000000U,
            "PCAN ERROR_INITIALIZE constant must be 0x04000000.");
        Require(Status("IllegalOperation") == 0x08000000U,
            "PCAN ERROR_ILLOPERATION constant must be 0x08000000.");
        Require((Status("AnyBusError") & Status("BusPassive")) != 0,
            "PCAN AnyBusError must include BUSPASSIVE.");

        Require(Marshal.SizeOf(messageType) == 16,
            "PCAN TPCANMsg ABI size must be 16 bytes with native alignment.");
        Require(Marshal.SizeOf(timestampType) == 8,
            "PCAN TPCANTimestamp ABI size must be 8 bytes.");

        Require(PcanBasicCanDriver.IsConnectableChannelCondition(1),
            "PCAN_CHANNEL_AVAILABLE must be connectable.");
        Require(!PcanBasicCanDriver.IsConnectableChannelCondition(2),
            "PCAN_CHANNEL_OCCUPIED must not be offered to a PCAN-Basic client.");
        Require(PcanBasicCanDriver.IsConnectableChannelCondition(3),
            "PCAN_CHANNEL_PCANVIEW must remain connectable.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
