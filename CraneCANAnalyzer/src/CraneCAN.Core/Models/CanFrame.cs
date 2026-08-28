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

    public int Dlc => Data.Length + 1;
    public string IdText => Id.ToString("X2");
    public string DataText => string.Join(" ", Data.Select(value => value.ToString("X2")));

    public void Validate()
    {
        if (Protocol != BusProtocol.Onk160Serial || Direction != CanDirection.Rx ||
            IsExtended || IsRemote || IsError)
        {
            throw new ArgumentException("The ONK-160 test build accepts passive UART receive packets only.");
        }

        if (Channel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Channel));
        }

        const int maximumDataLength = 63;
        if (Data.Length > maximumDataLength)
        {
            throw new ArgumentException($"Maximum payload length is {maximumDataLength} bytes.", nameof(Data));
        }

        const uint maximumId = 0xFF;
        if (Id > maximumId)
        {
            throw new ArgumentOutOfRangeException(nameof(Id));
        }
    }
}
