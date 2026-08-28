namespace CraneCAN.Core.Protocols;

public sealed record Onk160Packet(
    DateTimeOffset Timestamp,
    byte[] Bytes,
    bool? IsChecksumValid)
{
    public byte Header => Bytes[0];
    public ReadOnlyMemory<byte> Payload => Bytes.AsMemory(1);
}
