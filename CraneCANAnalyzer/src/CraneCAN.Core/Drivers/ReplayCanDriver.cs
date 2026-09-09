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
    private readonly object _sync = new();
    private CancellationTokenSource? _readCancellation;
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
        await using var reader = PcanTrcCodec.ReadFramesAsync(_path, _defaultChannel, cancellationToken).GetAsyncEnumerator();
        await reader.MoveNextAsync().ConfigureAwait(false);
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
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_open || _readerActive != 0) throw new InvalidOperationException("Предыдущий Replay ещё открыт или завершает чтение.");
            _readCancellation?.Dispose();
            _readCancellation = new CancellationTokenSource();
            while (_stepSignal.Wait(0)) { }
            _received = 0;
            _open = true;
        }
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_open) throw new InvalidOperationException("Replay-канал не открыт.");
            if (_readerActive != 0) throw new InvalidOperationException("Replay поддерживает только один поток чтения.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _readCancellation!.Token);
            _readerActive = 1;
        }
        using var reading = linked;
        var token = linked.Token;

        try
        {
            DateTimeOffset? previous = null;
            await foreach (var frame in PcanTrcCodec.ReadFramesAsync(_path, _defaultChannel, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (!_open)
                {
                    yield break;
                }

                if (_mode == ReplayTimingMode.Step)
                {
                    await _stepSignal.WaitAsync(token).ConfigureAwait(false);
                }
                else if (previous.HasValue)
                {
                    var delay = TimeSpan.FromTicks((long)Math.Max(
                        0, (frame.Timestamp - previous.Value).Ticks / _speedFactor));
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }

                token.ThrowIfCancellationRequested();
                previous = frame.Timestamp;
                Interlocked.Increment(ref _received);
                yield return frame;
            }
        }
        finally
        {
            lock (_sync)
            {
                _readerActive = 0;
                if (_disposed) { _readCancellation?.Dispose(); _stepSignal.Dispose(); }
            }
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
        lock (_sync) { _open = false; _readCancellation?.Cancel(); }
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
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_readerActive == 0) { _readCancellation?.Dispose(); _stepSignal.Dispose(); }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
