namespace CraneCAN.Core.Models;

public sealed record CanFrame
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int Channel { get; init; }
    public required uint Id { get; init; }
    public required byte[] Data { get; init; }
    public BusProtocol Protocol { get; init; } = BusProtocol.Onk160Serial;
    public CanDirection Direction { get; init; } = CanDirection.Rx;
    public bool IsExtended { get; init; }
    public bool IsRemote { get; init; }
    public bool IsError { get; init; }
    public bool? IsChecksumValid { get; init; }

    public int Dlc => Protocol == BusProtocol.Onk160Serial ? Data.Length + 1 : Data.Length;

    public string IdText => Protocol switch
    {
        BusProtocol.Onk160Serial => Id.ToString("X2"),
        BusProtocol.ClassicalCan when IsExtended => Id.ToString("X8"),
        BusProtocol.ClassicalCan => Id.ToString("X3"),
        _ => Id.ToString("X")
    };

    public string DataText => string.Join(" ", Data.Select(value => value.ToString("X2")));

    public void Validate()
    {
        if (Channel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Channel));
        }

        switch (Protocol)
        {
            case BusProtocol.Onk160Serial:
                ValidateOnk160();
                break;
            case BusProtocol.ClassicalCan:
                ValidateClassicalCan();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Protocol), Protocol, "Unsupported bus protocol.");
        }
    }

    private void ValidateOnk160()
    {
        if (Direction != CanDirection.Rx || IsExtended || IsRemote || IsError)
        {
            throw new ArgumentException("ONK-160 accepts passive UART receive packets only.");
        }

        const int maximumDataLength = 63;
        if (Data.Length > maximumDataLength)
        {
            throw new ArgumentException($"Maximum ONK-160 payload length is {maximumDataLength} bytes.", nameof(Data));
        }

        const uint maximumId = 0xFF;
        if (Id > maximumId)
        {
            throw new ArgumentOutOfRangeException(nameof(Id));
        }
    }

    private void ValidateClassicalCan()
    {
        const int maximumDataLength = 8;
        if (Data.Length > maximumDataLength)
        {
            throw new ArgumentException($"Maximum Classical CAN payload length is {maximumDataLength} bytes.", nameof(Data));
        }

        var maximumId = IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (Id > maximumId)
        {
            throw new ArgumentOutOfRangeException(nameof(Id),
                IsExtended
                    ? "Extended CAN identifier must be <= 0x1FFFFFFF."
                    : "Standard CAN identifier must be <= 0x7FF.");
        }

        if (IsChecksumValid.HasValue)
        {
            throw new ArgumentException("ChecksumValid is protocol metadata for ONK-160 and must be null for Classical CAN.");
        }
    }
}
