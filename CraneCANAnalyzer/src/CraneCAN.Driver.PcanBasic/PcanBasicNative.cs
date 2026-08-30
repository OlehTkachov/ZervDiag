using System.Runtime.InteropServices;

namespace CraneCAN.Driver.PcanBasic;

[Flags]
internal enum PcanStatus : uint
{
    Ok = 0x00000,
    Overrun = 0x00002,
    BusLight = 0x00004,
    BusHeavy = 0x00008,
    BusOff = 0x00010,
    ReceiveQueueEmpty = 0x00020,
    ReceiveQueueOverrun = 0x00040,
    NoDriver = 0x00200,
    IllegalHardware = 0x01400,
    IllegalParameterType = 0x04000,
    IllegalParameterValue = 0x08000,
    Initialize = 0x40000,
    IllegalOperation = 0x80000,
    AnyBusError = BusLight | BusHeavy | BusOff
}

[Flags]
internal enum PcanMessageType : byte
{
    Standard = 0x00,
    Remote = 0x01,
    Extended = 0x02,
    Fd = 0x04,
    Echo = 0x20,
    ErrorFrame = 0x40,
    Status = 0x80
}

internal enum PcanParameter : byte
{
    ListenOnly = 0x08,
    ChannelCondition = 0x0D,
    ReceiveStatus = 0x0F,
    AllowStatusFrames = 0x1E,
    AllowRemoteFrames = 0x1F,
    AllowErrorFrames = 0x20,
    AllowEchoFrames = 0x2C
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct PcanMessage
{
    public uint Id;
    public PcanMessageType Type;
    public byte Length;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct PcanTimestamp
{
    public uint Milliseconds;
    public ushort MillisecondsOverflow;
    public ushort Microseconds;
    public ulong TotalMicroseconds =>
        (((ulong)MillisecondsOverflow << 32) + Milliseconds) * 1_000UL + Microseconds;
}

internal static class PcanBasicNative
{
    private const string Library = "PCANBasic";
    public const uint ParameterOff = 0;
    public const uint ParameterOn = 1;
    public static readonly ushort[] UsbHandles =
    [
        0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x509, 0x50A, 0x50B, 0x50C, 0x50D, 0x50E, 0x50F, 0x510
    ];

    [DllImport(Library, EntryPoint = "CAN_Initialize", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus Initialize(ushort channel, ushort bitrate, byte hardwareType,
        uint ioPort, ushort interrupt);

    [DllImport(Library, EntryPoint = "CAN_Uninitialize", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus Uninitialize(ushort channel);

    [DllImport(Library, EntryPoint = "CAN_Read", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus Read(ushort channel, out PcanMessage message, out PcanTimestamp timestamp);

    [DllImport(Library, EntryPoint = "CAN_GetStatus", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus GetStatus(ushort channel);

    [DllImport(Library, EntryPoint = "CAN_SetValue", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus SetValue(ushort channel, PcanParameter parameter, ref uint value, uint length);

    [DllImport(Library, EntryPoint = "CAN_GetValue", CallingConvention = CallingConvention.Winapi)]
    internal static extern PcanStatus GetValue(ushort channel, PcanParameter parameter, out uint value, uint length);

    internal static string Describe(PcanStatus status) => status switch
    {
        PcanStatus.Ok => "OK",
        _ when (status & PcanStatus.BusOff) != 0 => "BUS-OFF",
        _ when (status & PcanStatus.NoDriver) != 0 => "драйвер PCAN недоступен",
        _ when (status & PcanStatus.ReceiveQueueOverrun) != 0 => "переполнение очереди приёма",
        _ when (status & PcanStatus.Overrun) != 0 => "аппаратный overrun",
        _ when (status & PcanStatus.AnyBusError) != 0 => "ошибка CAN-шины",
        _ => $"PCAN status 0x{(uint)status:X}"
    };
}
