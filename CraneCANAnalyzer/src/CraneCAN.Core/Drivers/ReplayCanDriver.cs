using System.Runtime.CompilerServices;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Drivers;

public enum ReplayTimingMode
{
    RealTime,
    Accelerated,
    Step
}

public sealed class ReplayCanDriver : ICanDriver, ICanDriverDiagnostics
{
    private readonly string _path;
    private readonly ReplayTimingMode _mode;
    private readonly double _speedFactor;
    private readonly int _defaultChannel;
    private readonly SemaphoreSlim _stepSignal = new(0);
    private IReadOnlyList<CanFrame>? _frames;
    private bool _open;
    private bool _disposed;
    private int _readerActive;
    private long _received;

    public ReplayCanDriver(
        string path,
        ReplayTimingMode mode = ReplayTimingMode.RealTime,
        double speedFactor = 1,
        int defaultChannel = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (speedFactor <= 0 || !double.IsFinite(speedFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(speedFactor));
        }

        _path = Path.GetFullPath(path);
        _mode = mode;
        _speedFactor = speedFactor;
        _defaultChannel = defaultChannel;
    }

    public string Id => "pcan-trc-replay";
    public string DisplayName => "PCAN-View TRC Replay (LISTEN ONLY)";
    public BusProtocol Protocol => BusProtocol.ClassicalCan;
    public bool SupportsListenOnly => true;
    public bool IsOpen => _open;

    public async Task<IReadOnlyList<CanChannelDescriptor>> DiscoverChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _frames ??= await PcanTrcCodec.LoadAsync(_path, _defaultChannel, cancellationToken)
            .ConfigureAwait(false);
        return [new CanChannelDescriptor("replay-trc", $"Replay: {Path.GetFileName(_path)}")];
    }

    public async Task OpenAsync(CanChannelSettings settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.ListenOnly)
        {
            throw new InvalidOperationException("TRC Replay разрешён только в LISTEN ONLY.");
        }

        if (!string.Equals(settings.ChannelId, "replay-trc", StringComparison.Ordinal))
        {
            throw new ArgumentException("Неизвестный Replay-канал.", nameof(settings));
        }

        await DiscoverChannelsAsync(cancellationToken).ConfigureAwait(false);
        _received = 0;
        _open = true;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_open || _frames is null)
        {
            throw new InvalidOperationException("Replay-канал не открыт.");
        }

        if (Interlocked.Exchange(ref _readerActive, 1) != 0)
        {
            throw new InvalidOperationException("Replay поддерживает только один поток чтения.");
        }

        try
        {
            DateTimeOffset? previous = null;
            foreach (var frame in _frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_open)
                {
                    yield break;
                }

                if (_mode == ReplayTimingMode.Step)
                {
                    await _stepSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (previous.HasValue)
                {
                    var delay = TimeSpan.FromTicks((long)Math.Max(
                        0, (frame.Timestamp - previous.Value).Ticks / _speedFactor));
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                previous = frame.Timestamp;
                Interlocked.Increment(ref _received);
                yield return frame;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readerActive, 0);
        }
    }

    public void AdvanceOne()
    {
        ThrowIfDisposed();
        if (_mode != ReplayTimingMode.Step)
        {
            throw new InvalidOperationException("AdvanceOne доступен только в Step-режиме.");
        }

        _stepSignal.Release();
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _open = false;
        return Task.CompletedTask;
    }

    public CanDriverStatus GetStatus() => new(
        _open, true, Interlocked.Read(ref _received), 0, 0,
        _open ? "CONNECTED — REPLAY LISTEN ONLY" : "DISCONNECTED");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync().ConfigureAwait(false);
        _stepSignal.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
