namespace CraneCAN.Core.Protocols;

public sealed class Onk160PacketParser
{
    private static readonly IReadOnlyDictionary<byte, int> KnownPacketLengths =
        new Dictionary<byte, int>
        {
            [0xCA] = 6,
            [0xE5] = 4,
            [0xE6] = 4,
            [0xE8] = 15,
            [0xF7] = 3
        };

    private readonly List<byte> _buffer = [];

    public IReadOnlyList<Onk160Packet> Append(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset timestamp)
    {
        foreach (var value in bytes)
        {
            _buffer.Add(value);
        }

        var packets = new List<Onk160Packet>();
        while (_buffer.Count > 0)
        {
            var header = _buffer[0];
            if (!KnownPacketLengths.TryGetValue(header, out var expectedLength))
            {
                packets.Add(new Onk160Packet(timestamp, [header], null));
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < expectedLength)
            {
                break;
            }

            var packetBytes = _buffer.GetRange(0, expectedLength).ToArray();
            _buffer.RemoveRange(0, expectedLength);
            packets.Add(new Onk160Packet(timestamp, packetBytes, HasValidChecksum(packetBytes)));
        }

        return packets;
    }

    public void Reset() => _buffer.Clear();

    public static bool HasValidChecksum(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            return false;
        }

        var sum = 0;
        foreach (var value in bytes)
        {
            sum = (sum + value) & 0xFF;
        }

        return sum == 0xFF;
    }
}
