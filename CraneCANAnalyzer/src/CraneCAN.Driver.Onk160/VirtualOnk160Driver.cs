using System.Runtime.CompilerServices;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Driver.Onk160;

public sealed class VirtualOnk160Driver : ICanDriver
{
    private bool _isOpen;
    private int _cycle;

    public string Id => "onk160-virtual";
    public string DisplayName => "Виртуальный ОНК-160 (демонстрация)";
    public BusProtocol Protocol => BusProtocol.Onk160Serial;
    public bool SupportsListenOnly => true;
    public bool IsOpen => _isOpen;

    public Task<IReadOnlyList<CanChannelDescriptor>> DiscoverChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CanChannelDescriptor> channels =
        [
            new("onk-demo", "Демонстрационный поток ОНК-160")
        ];
        return Task.FromResult(channels);
    }

    public Task OpenAsync(
        CanChannelSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.ListenOnly)
        {
            throw new InvalidOperationException("Демонстрационный драйвер работает только в режиме приёма.");
        }

        if (settings.Bitrate != Onk160SerialDriver.RequiredBitrate)
        {
            throw new NotSupportedException("Демонстрация ОНК-160 использует 38 400 бит/с.");
        }

        _cycle = 0;
        _isOpen = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_isOpen)
        {
            throw new InvalidOperationException("Демонстрационный канал ОНК-160 не открыт.");
        }

        while (_isOpen && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            _cycle++;
            var state = (_cycle / 8) % 4;
            var packets = CreateCycle(state);
            var timestamp = DateTimeOffset.UtcNow;
            for (var index = 0; index < packets.Count; index++)
            {
                var bytes = packets[index];
                yield return new CanFrame
                {
                    Timestamp = timestamp.AddMilliseconds(index),
                    Channel = 0,
                    Id = bytes[0],
                    Data = bytes.Skip(1).ToArray(),
                    Protocol = BusProtocol.Onk160Serial,
                    Direction = CanDirection.Rx,
                    IsChecksumValid = bytes.Length > 1
                        ? Onk160PacketParser.HasValidChecksum(bytes)
                        : null
                };
            }
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isOpen = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<byte[]> CreateCycle(int state)
    {
        var packets = new List<byte[]>
        {
            Convert.FromHexString(state == 3 ? "CA07004811D5" : "CA6B014B116D"),
            Convert.FromHexString(state == 1 ? "E6008099" : "E63500E4"),
            Convert.FromHexString("E57900A1"),
            Convert.FromHexString(state == 3
                ? "E8B3093400D2FF0000FD00690241AD"
                : "E8B2093900B5FF0000FD00690241C6"),
            Convert.FromHexString(state switch
            {
                2 => "F70107",
                3 => "F710F8",
                _ => "F78187"
            })
        };

        if (state == 3)
        {
            packets.Add([0x2A]);
        }

        return packets;
    }
}
