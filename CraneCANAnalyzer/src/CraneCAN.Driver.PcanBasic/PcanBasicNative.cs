using System.Runtime.InteropServices;

namespace CraneCAN.Driver.PcanBasic;

[Flags]
internal enum PcanStatus : uint
{
    Ok = 0x00000,
    TransmitBufferFull = 0x00001,
    Overrun = 0x00002,
    BusLight = 0x00004,
    BusHeavy = 0x00008,
    BusWarning = BusHeavy,
    BusOff = 0x00010,
    ReceiveQueueEmpty = 0x00020,
    ReceiveQueueOverrun = 0x00040,
    TransmitQueueFull = 0x00080,
    RegisterTest = 0x00100,
    NoDriver = 0x00200,
    HardwareInUse = 0x00400,
    NetInUse = 0x00800,
    IllegalHardware = 0x01400,
    IllegalNet = 0x01800,
    IllegalClient = 0x01C00,
    Resource = 0x02000,
    IllegalParameterType = 0x04000,
    IllegalParameterValue = 0x08000,
    Unknown = 0x10000,
    IllegalData = 0x20000,
    BusPassive = 0x40000,
    IllegalMode = 0x80000,
    Caution = 0x2000000,
    Initialize = 0x4000000,
    IllegalOperation = 0x8000000,
    AnyBusError = BusWarning | BusLight | BusHeavy | BusOff | BusPassive
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

[StructLayout(LayoutKind.Sequential)]
internal struct PcanMessage
{
    public uint Id;
    [MarshalAs(UnmanagedType.U1)] public PcanMessageType Type;
    public byte Length;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;
}

[StructLayout(LayoutKind.Sequential)]
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
    public const uint ChannelUnavailable = 0;
    public const uint ChannelAvailable = 1;
    public const uint ChannelOccupied = 2;
    public const uint ChannelPcanView = 3;

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

    internal static string Describe(PcanStatus status)
    {
        static bool Has(PcanStatus value, PcanStatus flag) => (value & flag) == flag;
        var text = status switch
        {
            PcanStatus.Ok => "OK",
            _ when Has(status, PcanStatus.BusOff) => "BUS-OFF",
            _ when Has(status, PcanStatus.BusPassive) => "BUS-PASSIVE",
            _ when Has(status, PcanStatus.BusHeavy) => "BUS-HEAVY / BUS-WARNING",
            _ when Has(status, PcanStatus.BusLight) => "BUS-LIGHT",
            _ when Has(status, PcanStatus.NoDriver) => "драйвер PCAN недоступен",
            PcanStatus.IllegalHardware => "некорректный аппаратный handle",
            _ when Has(status, PcanStatus.Initialize) => "канал не инициализирован",
            _ when Has(status, PcanStatus.IllegalOperation) => "недопустимая операция",
            _ when Has(status, PcanStatus.IllegalMode) => "неверное состояние драйвера для операции",
            _ when Has(status, PcanStatus.Caution) => "операция выполнена с предупреждением",
            _ when Has(status, PcanStatus.ReceiveQueueOverrun) => "переполнение очереди приёма",
            _ when Has(status, PcanStatus.Overrun) => "аппаратный overrun",
            _ when (status & PcanStatus.AnyBusError) != 0 => "ошибка CAN-шины",
            _ => "неизвестный статус PCAN"
        };
        return $"{text} [0x{(uint)status:X8}]";
    }
}
