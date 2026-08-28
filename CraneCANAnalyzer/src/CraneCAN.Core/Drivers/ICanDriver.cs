using CraneCAN.Core.Models;

namespace CraneCAN.Core.Drivers;

public interface ICanDriver : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    BusProtocol Protocol { get; }
    bool SupportsListenOnly { get; }
    bool IsOpen { get; }

    Task<IReadOnlyList<CanChannelDescriptor>> DiscoverChannelsAsync(
        CancellationToken cancellationToken = default);

    Task OpenAsync(
        CanChannelSettings settings,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CanFrame> ReadFramesAsync(
        CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}
