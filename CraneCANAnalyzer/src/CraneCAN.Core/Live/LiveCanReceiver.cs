using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Live;

public sealed class LiveCanReceiver : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ICanDriver _driver;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private LiveTrcWriter? _writer;
    private LiveExperimentSession? _session;
    private bool _stopping;
    private bool _disposed;

    public LiveCanReceiver(ICanDriver driver, LiveCanBuffer? buffer = null)
    {
        if (driver.Protocol != BusProtocol.ClassicalCan) throw new ArgumentException("Требуется Classical CAN.", nameof(driver));
        _driver = driver;
        Buffer = buffer ?? new LiveCanBuffer();
    }

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? ReceiverFaulted;
    public LiveCanBuffer Buffer { get; }
    public bool IsReceiving { get; private set; }
    public string? RawCapturePath { get; private set; }
    public DateTimeOffset? RawCaptureStart { get; private set; }
    public string? LastError { get; private set; }
    public Task Completion { get { lock (_sync) return _readTask ?? Task.CompletedTask; } }
    public CanDriverStatus DriverStatus => _driver is ICanDriverDiagnostics diagnostics
        ? diagnostics.GetStatus()
        : new CanDriverStatus(_driver.IsOpen, _driver.IsOpen && _driver.SupportsListenOnly,
            Buffer.TotalReceived, 0, 0, _driver.IsOpen ? "CONNECTED" : "DISCONNECTED");

    public void AttachSession(LiveExperimentSession? session) { lock (_sync) _session = session; }

    public async Task StartAsync(CanChannelSettings settings, string rawCapturePath,
        DateTimeOffset captureStart, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!settings.ListenOnly) throw new InvalidOperationException("Live Guided разрешён только в LISTEN ONLY.");
        lock (_sync) if (_readTask is not null) throw new InvalidOperationException("Live CAN уже запущен.");
        var writer = await LiveTrcWriter.CreateAsync(rawCapturePath, captureStart, cancellationToken).ConfigureAwait(false);
        try { await _driver.OpenAsync(settings, cancellationToken).ConfigureAwait(false); }
        catch { await writer.DisposeAsync(); throw; }
        lock (_sync)
        {
            _writer = writer;
            _cts = new CancellationTokenSource();
            _stopping = false;
            IsReceiving = true;
            RawCapturePath = Path.GetFullPath(rawCapturePath);
            RawCaptureStart = captureStart;
            _readTask = ReadLoopAsync(_cts.Token);
        }
    }

    public async Task FlushCaptureAsync(CancellationToken cancellationToken = default)
    {
        LiveTrcWriter? writer; lock (_sync) writer = _writer;
        if (writer is not null) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(bool invalidateActiveExperiment = true, CancellationToken cancellationToken = default)
    {
        Task? task; CancellationTokenSource? cts; LiveTrcWriter? writer; LiveExperimentSession? session;
        lock (_sync)
        {
            _stopping = true; task = _readTask; cts = _cts; writer = _writer; session = _session;
            _readTask = null; _cts = null; _writer = null; IsReceiving = false;
        }
        if (invalidateActiveExperiment && session is not null && session.State is not
            (LiveExperimentState.Idle or LiveExperimentState.Completed or LiveExperimentState.Aborted or LiveExperimentState.Analyzing))
            session.Invalidate(LiveSessionWarningCode.CanStreamStopped, "Live CAN остановлен до завершения опыта.", DateTimeOffset.UtcNow);
        cts?.Cancel();
        try
        {
            await _driver.CloseAsync(cancellationToken).ConfigureAwait(false);
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true) { }
        finally
        {
            cts?.Dispose(); if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            lock (_sync) _stopping = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        await _driver.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _driver.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                Buffer.Append(frame);
                LiveExperimentSession? session; LiveTrcWriter? writer;
                lock (_sync) { session = _session; writer = _writer; }
                session?.AppendFrame(frame);
                if (writer is not null) await writer.AppendAsync(frame, cancellationToken).ConfigureAwait(false);
                try { FrameReceived?.Invoke(frame); } catch { }
            }
            if (!cancellationToken.IsCancellationRequested) HandleUnexpectedStop("Поток CAN завершился без команды Stop.", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { if (!_stopping) HandleUnexpectedStop("Соединение с CAN-адаптером потеряно.", ex); }
        finally { IsReceiving = false; }
    }

    private void HandleUnexpectedStop(string message, Exception? exception)
    {
        var detail = exception is null ? message : $"{message} {exception.Message}";
        LiveExperimentSession? session; lock (_sync) { LastError = detail; session = _session; }
        if (session is not null && session.State is not
            (LiveExperimentState.Idle or LiveExperimentState.Completed or LiveExperimentState.Aborted or LiveExperimentState.Analyzing))
            session.Invalidate(exception is null ? LiveSessionWarningCode.CanStreamStopped : LiveSessionWarningCode.DriverDisconnected,
                detail, DateTimeOffset.UtcNow);
        try { ReceiverFaulted?.Invoke(detail); } catch { }
    }
}
