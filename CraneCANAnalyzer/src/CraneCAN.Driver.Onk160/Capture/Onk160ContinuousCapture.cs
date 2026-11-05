using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraneCAN.Driver.Onk160.Capture;

public sealed class Onk160CaptureMarker
{
    public long Id { get; set; }
    public DateTimeOffset AbsoluteTimestampUtc { get; set; }
    public long MonotonicTimestamp { get; set; }
    public double RelativeMilliseconds { get; set; }
    public long RawByteOffset { get; set; }
    public string Label { get; set; } = "MARK";
    public string Comment { get; set; } = "";
    [JsonIgnore]
    public string RelativeTimeText
    {
        get
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, RelativeMilliseconds));
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
        }
    }
}

public sealed class Onk160CaptureSessionManifest
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SessionId { get; set; }
    public DateTimeOffset StartTimestampUtc { get; set; }
    public long SessionStartMonotonicTimestamp { get; set; }
    public long MonotonicClockFrequency { get; set; } = Stopwatch.Frequency;
    public DateTimeOffset? StopTimestampUtc { get; set; }
    public string ComPort { get; set; } = "";
    public int BaudRate { get; set; } = 38_400;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "Even";
    public int StopBits { get; set; } = 1;
    public string RawFileName { get; set; } = "";
    public string MarkersFileName { get; set; } = "";
    public long TotalBytesReceived { get; set; }
    public long TotalBytesWritten { get; set; }
    public bool IsComplete { get; set; }
}

public sealed record Onk160OpenedSession(
    Onk160CaptureSessionManifest Manifest,
    string ManifestPath,
    string RawPath,
    string MarkersPath,
    IReadOnlyList<Onk160CaptureMarker> Markers);

public sealed class Onk160MarkersDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SessionId { get; set; }
    public List<Onk160CaptureMarker> Markers { get; set; } = [];
}

public static class Onk160SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static (string ManifestPath, string MarkersPath) GetSidecarPaths(string rawPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        var fullRawPath = Path.GetFullPath(rawPath);
        var directory = Path.GetDirectoryName(fullRawPath)
            ?? throw new ArgumentException("RAW path must contain a directory.", nameof(rawPath));
        var stem = Path.GetFileNameWithoutExtension(fullRawPath);
        return (
            Path.Combine(directory, stem + ".session.json"),
            Path.Combine(directory, stem + ".markers.json"));
    }

    public static async Task SaveManifestAsync(
        string manifestPath,
        Onk160CaptureSessionManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await WriteAtomicJsonAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveMarkersAsync(
        string markersPath,
        Guid sessionId,
        IReadOnlyCollection<Onk160CaptureMarker> markers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var document = new Onk160MarkersDocument
        {
            SessionId = sessionId,
            Markers = markers.OrderBy(marker => marker.Id).ToList()
        };
        await WriteAtomicJsonAsync(markersPath, document, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Onk160OpenedSession> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        await using var stream = new FileStream(
            fullManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<Onk160CaptureSessionManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new FormatException("ONK-160 session manifest is empty.");

        if (manifest.SchemaVersion != 1)
            throw new FormatException($"Unsupported ONK-160 session schema: {manifest.SchemaVersion}.");
        if (manifest.MonotonicClockFrequency <= 0 || manifest.SessionStartMonotonicTimestamp <= 0)
            throw new FormatException("Session monotonic clock metadata is invalid.");
        if (manifest.BaudRate != 38_400 || manifest.DataBits != 8 ||
            !string.Equals(manifest.Parity, "Even", StringComparison.OrdinalIgnoreCase) ||
            manifest.StopBits != 1)
            throw new FormatException("Session serial parameters are not 38400 8E1.");

        var directory = Path.GetDirectoryName(fullManifestPath)!;
        ValidateSidecarName(manifest.RawFileName, nameof(manifest.RawFileName));
        ValidateSidecarName(manifest.MarkersFileName, nameof(manifest.MarkersFileName));
        var rawPath = Path.Combine(directory, manifest.RawFileName);
        var markersPath = Path.Combine(directory, manifest.MarkersFileName);
        if (!File.Exists(rawPath))
            throw new FileNotFoundException("RAW file belonging to this ONK-160 session was not found.", rawPath);

        List<Onk160CaptureMarker> markers = [];
        if (File.Exists(markersPath))
        {
            await using var markerStream = new FileStream(
                markersPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var markerDocument = await JsonSerializer.DeserializeAsync<Onk160MarkersDocument>(
                markerStream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (markerDocument is null)
                throw new FormatException("ONK-160 marker file is empty.");
            if (markerDocument.SchemaVersion != 1 || markerDocument.SessionId != manifest.SessionId)
                throw new FormatException("Marker file does not belong to this session.");
            markers = (markerDocument.Markers ?? []).OrderBy(marker => marker.Id).ToList();
            ValidateMarkers(markers);
        }

        return new Onk160OpenedSession(
            manifest, fullManifestPath, rawPath, markersPath, markers);
    }

    private static async Task WriteAtomicJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Keep the original sidecar if cleanup of a temporary file fails.
            }
        }
    }

    private static void ValidateSidecarName(string fileName, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new FormatException($"Invalid session sidecar name: {propertyName}.");
    }

    private static void ValidateMarkers(IReadOnlyList<Onk160CaptureMarker> markers)
    {
        long previousId = 0;
        foreach (var marker in markers)
        {
            if (marker.Id <= previousId || marker.RelativeMilliseconds < 0 || marker.RawByteOffset < 0)
                throw new FormatException("ONK-160 markers contain invalid IDs or timeline offsets.");
            previousId = marker.Id;
        }
    }
}

/// <summary>
/// Minimal byte-oriented source contract. The recorder does not depend on packet decoding.
/// </summary>
public interface IOnk160ByteSource : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    int BytesAvailable { get; }
    void Open();
    int Read(byte[] buffer, int offset, int count);
    void Close();
}

internal sealed class SerialPortOnk160ByteSource : IOnk160ByteSource
{
    private readonly SerialPort _port;

    public SerialPortOnk160ByteSource(string portName)
    {
        _port = new SerialPort(portName, Onk160ContinuousCapture.RequiredBaudRate, Parity.Even, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadBufferSize = 65_536,
            ReadTimeout = 100,
            WriteTimeout = 100,
            ParityReplace = 0
        };
    }

    public string PortName => _port.PortName;
    public bool IsOpen => _port.IsOpen;
    public int BytesAvailable => _port.IsOpen ? _port.BytesToRead : 0;
    public void Open() => _port.Open();
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Close() => _port.Close();
    public void Dispose() => _port.Dispose();
}
/// <summary>
/// Dedicated ONK-160 byte capture. It never invokes the packet decoder and does not share
/// the CAN Live capture path. While connected, the reader continuously drains the source;
/// during STOP it drains bytes already buffered by the source before closing the RAW file.
/// </summary>
public sealed class Onk160ContinuousCapture : IAsyncDisposable
{
    public const int RequiredBaudRate = 38_400;

    private readonly Func<string, IOnk160ByteSource> _sourceFactory;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private IOnk160ByteSource? _source;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _maintenanceTask;
    private FileStream? _rawStream;
    private Onk160CaptureSessionManifest? _manifest;
    private string? _manifestPath;
    private string? _markersPath;
    private List<Onk160CaptureMarker> _markers = [];
    private TaskCompletionSource<bool>? _idleBoundary;
    private long _sessionStartTimestamp;
    private long _sessionStopTimestamp;
    private long _bytesReceived;
    private long _bytesWritten;
    private long _lastDurableBytes;
    private long _markerRevision;
    private long _persistedMarkerRevision;
    private int _markerPersistenceWorker;
    private int _isRecording;
    private int _stopRequested;
    private Exception? _lastError;

    public Onk160ContinuousCapture(Func<string, IOnk160ByteSource>? sourceFactory = null)
    {
        _sourceFactory = sourceFactory ?? (portName => new SerialPortOnk160ByteSource(portName));
    }

    public event Action<Exception>? Faulted;

    public bool IsConnected => _source?.IsOpen == true;
    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;
    public bool HasOpenCapture => _rawStream is not null;
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public Exception? LastError => _lastError;
    public string? ConnectedPort => _source?.PortName;
    public Onk160CaptureSessionManifest? CurrentManifest => _manifest;
    public string? CurrentManifestPath => _manifestPath;
    public string? CurrentRawPath => _manifest is null || _manifestPath is null
        ? null
        : Path.Combine(Path.GetDirectoryName(_manifestPath)!, _manifest.RawFileName);

    public static IReadOnlyList<string> DiscoverPorts() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortSortKey)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Task ConnectAsync(string portName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (!portName.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a Windows COM port, for example COM4.", nameof(portName));
        if (IsConnected)
            throw new InvalidOperationException("An ONK-160 COM port is already connected.");
        if (_readerTask is { IsCompleted: false })
            throw new InvalidOperationException("The previous ONK-160 reader has not stopped.");
        if (_readerTask is not null)
        {
            _readerCancellation?.Dispose();
            _readerCancellation = null;
            _readerTask = null;
            try { _source?.Dispose(); } catch { }
            _source = null;
        }

        var source = _sourceFactory(portName.Trim());
        try
        {
            source.Open();
        }
        catch
        {
            source.Dispose();
            throw;
        }
        _source = source;
        Volatile.Write(ref _stopRequested, 0);
        _lastError = null;
        var readerCancellation = new CancellationTokenSource();
        _readerCancellation = readerCancellation;
        _readerTask = Task.Run(() => ReadLoopAsync(source, readerCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }
    public async Task StartRecordingAsync(string rawPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected || _source is null)
            throw new InvalidOperationException("Connect to the ONK-160 COM port before recording.");
        if (IsRecording || _rawStream is not null)
            throw new InvalidOperationException("An ONK-160 recording is already active.");

        var fullRawPath = Path.GetFullPath(rawPath);
        var directory = Path.GetDirectoryName(fullRawPath)!;
        Directory.CreateDirectory(directory);
        var sidecars = Onk160SessionStore.GetSidecarPaths(fullRawPath);
        if (File.Exists(fullRawPath) || File.Exists(sidecars.ManifestPath) || File.Exists(sidecars.MarkersPath))
            throw new IOException("A file with this session name already exists. Choose a new name.");

        var rawStream = new FileStream(
            fullRawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var now = DateTimeOffset.UtcNow;
        var manifest = new Onk160CaptureSessionManifest
        {
            SessionId = Guid.NewGuid(),
            StartTimestampUtc = now,
            SessionStartMonotonicTimestamp = Stopwatch.GetTimestamp(),
            MonotonicClockFrequency = Stopwatch.Frequency,
            ComPort = _source.PortName,
            RawFileName = Path.GetFileName(fullRawPath),
            MarkersFileName = Path.GetFileName(sidecars.MarkersPath)
        };

        lock (_stateLock)
        {
            _rawStream = rawStream;
            _manifest = manifest;
            _manifestPath = sidecars.ManifestPath;
            _markersPath = sidecars.MarkersPath;
            _markers = [];
            _sessionStartTimestamp = manifest.SessionStartMonotonicTimestamp;
            _sessionStopTimestamp = 0;
            _idleBoundary = null;
            _lastError = null;
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _bytesWritten, 0);
            Interlocked.Exchange(ref _lastDurableBytes, 0);
            Volatile.Write(ref _isRecording, 0);
            Volatile.Write(ref _stopRequested, 0);
        }

        try
        {
            // Publish the session sidecars before enabling capture. This avoids racing
            // the first raw write against creation of the manifest and marker document.
            await PersistMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _fileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await rawStream.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(_rawStream, rawStream))
                    _rawStream = null;
            }
            finally
            {
                _fileGate.Release();
            }
            throw;
        }

        Volatile.Write(ref _isRecording, 1);
        _maintenanceCancellation = new CancellationTokenSource();
        _maintenanceTask = Task.Run(
            () => MaintenanceLoopAsync(_maintenanceCancellation.Token), CancellationToken.None);
    }

    /// <summary>Captures timestamps and byte offset before any UI work is performed.</summary>
    public Onk160CaptureMarker AddMarker(string label, string? comment = null)
    {
        var absoluteTimestamp = DateTimeOffset.UtcNow;
        var monotonicTimestamp = Stopwatch.GetTimestamp();
        var byteOffset = Interlocked.Read(ref _bytesWritten);
        if (!IsRecording || _manifest is null)
            throw new InvalidOperationException("Start an ONK-160 recording before adding markers.");

        var marker = new Onk160CaptureMarker
        {
            Id = 1,
            AbsoluteTimestampUtc = absoluteTimestamp,
            MonotonicTimestamp = monotonicTimestamp,
            RelativeMilliseconds = Stopwatch.GetElapsedTime(
                _sessionStartTimestamp, monotonicTimestamp).TotalMilliseconds,
            RawByteOffset = byteOffset,
            Label = string.IsNullOrWhiteSpace(label) ? "MARK" : label.Trim(),
            Comment = comment?.Trim() ?? ""
        };

        lock (_stateLock)
        {
            marker.Id = _markers.Count == 0 ? 1 : _markers[^1].Id + 1;
            _markers.Add(marker);
            Interlocked.Increment(ref _markerRevision);
        }

        QueueMarkerPersistence();
        return marker;
    }

    public IReadOnlyList<Onk160CaptureMarker> GetMarkers()
    {
        lock (_stateLock)
            return _markers.Select(CloneMarker).ToArray();
    }

    public async Task SaveMarkerEditsAsync(
        IReadOnlyCollection<Onk160CaptureMarker> editedMarkers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editedMarkers);
        lock (_stateLock)
        {
            var byId = editedMarkers.ToDictionary(marker => marker.Id);
            foreach (var marker in _markers)
            {
                if (byId.TryGetValue(marker.Id, out var edited))
                {
                    marker.Label = edited.Label;
                    marker.Comment = edited.Comment;
                }
            }
            Interlocked.Increment(ref _markerRevision);
        }
        await PersistMarkersSafelyAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _persistedMarkerRevision, Interlocked.Read(ref _markerRevision));
    }

    public TimeSpan GetElapsed()
    {
        if (_manifest is null)
            return TimeSpan.Zero;
        var end = IsRecording
            ? Stopwatch.GetTimestamp()
            : _sessionStopTimestamp != 0
                ? _sessionStopTimestamp
                : _sessionStartTimestamp;
        return _sessionStartTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_sessionStartTimestamp, end);
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_rawStream is null || _manifest is null)
            return;

        // STOP drains the source's already-buffered bytes; the session stop timestamp is set after that boundary.
        var idleBoundary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _idleBoundary = idleBoundary;
        Volatile.Write(ref _isRecording, 0);
        Volatile.Write(ref _stopRequested, 1);

        if (_readerTask is { IsCompleted: false })
        {
            // Do not close the RAW stream until the in-flight COM read and its disk write are complete.
            // The serial read timeout is 100 ms; the boundary is also signalled on reader faults.
            await idleBoundary.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_maintenanceCancellation is not null)
        {
            _maintenanceCancellation.Cancel();
            if (_maintenanceTask is not null)
            {
                try { await _maintenanceTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { ReportFault(exception); }
            }
            _maintenanceCancellation.Dispose();
            _maintenanceCancellation = null;
            _maintenanceTask = null;
        }

        Exception? finalizationError = null;
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _rawStream;
            if (stream is not null)
            {
                try
                {
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                    Interlocked.Exchange(ref _lastDurableBytes, Interlocked.Read(ref _bytesWritten));
                }
                catch (Exception exception)
                {
                    finalizationError = exception;
                    ReportFault(exception);
                }
                finally
                {
                    try
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        finalizationError ??= exception;
                        ReportFault(exception);
                    }
                    _rawStream = null;
                }
            }
        }
        finally
        {
            _fileGate.Release();
        }

        await WaitForMarkerPersistenceAsync(cancellationToken).ConfigureAwait(false);
        var stopTimestamp = DateTimeOffset.UtcNow;
        _sessionStopTimestamp = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            _manifest.StopTimestampUtc = stopTimestamp;
            _manifest.TotalBytesReceived = Interlocked.Read(ref _bytesReceived);
            _manifest.TotalBytesWritten = Interlocked.Read(ref _lastDurableBytes);
            _manifest.IsComplete = _lastError is null && finalizationError is null &&
                                   _manifest.TotalBytesReceived == _manifest.TotalBytesWritten;
        }

        try
        {
            await PersistMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            finalizationError ??= exception;
            ReportFault(exception);
            lock (_stateLock)
                _manifest.IsComplete = false;
        }

        if (finalizationError is not null)
            throw new IOException("ONK-160 RAW was closed, but final flush or session metadata reported an error.", finalizationError);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Exception? stopError = null;
        try
        {
            await StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopError = exception;
        }
        finally
        {
            var source = _source;
            _source = null;
            if (_readerCancellation is not null)
            {
                _readerCancellation.Cancel();
                try { source?.Close(); } catch { }
                if (_readerTask is not null)
                {
                    try { await _readerTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { stopError ??= exception; }
                }
                _readerCancellation.Dispose();
                _readerCancellation = null;
                _readerTask = null;
            }
            try { source?.Dispose(); }
            catch (Exception exception) { stopError ??= exception; }
        }

        if (stopError is not null)
            throw stopError;
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _fileGate.Dispose();
        _metadataGate.Dispose();
    }

    private async Task ReadLoopAsync(IOnk160ByteSource source, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var recordingAtReadStart = IsRecording;
                int count;
                try
                {
                    count = source.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    if (Volatile.Read(ref _stopRequested) == 1)
                    {
                        _idleBoundary?.TrySetResult(true);
                        break;
                    }
                    continue;
                }

                var stopRequested = Volatile.Read(ref _stopRequested) == 1;
                if (count > 0 && (recordingAtReadStart || IsRecording || stopRequested))
                {
                    Interlocked.Add(ref _bytesReceived, count);
                    await WriteRawChunkAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _stopRequested) == 1)
                {
                    // Drain bytes already buffered by the serial driver at the STOP boundary.
                    if (source.BytesAvailable <= 0)
                    {
                        _idleBoundary?.TrySetResult(true);
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested ||
                                          exception is IOException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportFault(exception);
                try { source.Close(); } catch { }
            }
        }
        catch (Exception exception)
        {
            ReportFault(exception);
            try { source.Close(); } catch { }
        }
    }
    private async Task WriteRawChunkAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _rawStream;
            if (stream is null)
                return;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesWritten, bytes.Length);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_rawStream is not null)
                    {
                        await _rawStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _rawStream.Flush(flushToDisk: true);
                        Interlocked.Exchange(ref _lastDurableBytes, Interlocked.Read(ref _bytesWritten));
                    }
                }
                finally
                {
                    _fileGate.Release();
                }

                await PersistManifestOnlyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFault(exception);
        }
    }

    private async Task PersistManifestOnlyAsync(CancellationToken cancellationToken = default)
    {
        var manifest = _manifest;
        var manifestPath = _manifestPath;
        if (manifest is null || manifestPath is null)
            return;

        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Onk160CaptureSessionManifest snapshot;
            lock (_stateLock)
            {
                manifest.TotalBytesReceived = Interlocked.Read(ref _bytesReceived);
                manifest.TotalBytesWritten = Interlocked.Read(ref _lastDurableBytes);
                snapshot = CloneManifest(manifest);
            }
            await Onk160SessionStore.SaveManifestAsync(manifestPath, snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task PersistMetadataAsync(CancellationToken cancellationToken = default)
    {
        var manifest = _manifest;
        var manifestPath = _manifestPath;
        var markersPath = _markersPath;
        if (manifest is null || manifestPath is null || markersPath is null)
            return;

        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Onk160CaptureSessionManifest manifestSnapshot;
            Onk160CaptureMarker[] markerSnapshot;
            lock (_stateLock)
            {
                manifest.TotalBytesReceived = Interlocked.Read(ref _bytesReceived);
                manifest.TotalBytesWritten = Interlocked.Read(ref _lastDurableBytes);
                manifestSnapshot = CloneManifest(manifest);
                markerSnapshot = _markers.Select(CloneMarker).ToArray();
            }

            await Onk160SessionStore.SaveMarkersAsync(
                markersPath, manifestSnapshot.SessionId, markerSnapshot, cancellationToken)
                .ConfigureAwait(false);
            await Onk160SessionStore.SaveManifestAsync(
                manifestPath, manifestSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task WaitForMarkerPersistenceAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _markerPersistenceWorker) == 1)
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
    }
    private void QueueMarkerPersistence()
    {
        if (!IsRecording || _lastError is not null)
            return;

        if (Interlocked.CompareExchange(ref _markerPersistenceWorker, 1, 0) == 0)
            _ = Task.Run(PersistMarkerWorkerAsync);
    }

    private async Task PersistMarkerWorkerAsync()
    {
        try
        {
            while (IsRecording && _lastError is null)
            {
                var targetRevision = Interlocked.Read(ref _markerRevision);
                if (targetRevision == Interlocked.Read(ref _persistedMarkerRevision))
                    break;

                await PersistMetadataAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _persistedMarkerRevision, targetRevision);
            }
        }
        catch (Exception exception)
        {
            ReportFault(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _markerPersistenceWorker, 0);
            if (IsRecording && _lastError is null &&
                Interlocked.Read(ref _markerRevision) != Interlocked.Read(ref _persistedMarkerRevision))
                QueueMarkerPersistence();
        }
    }

    private async Task PersistMarkersSafelyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PersistMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFault(exception);
            throw;
        }
    }

    private void ReportFault(Exception exception)
    {
        _lastError = exception;
        Volatile.Write(ref _isRecording, 0);
        Volatile.Write(ref _stopRequested, 1);
        try { Faulted?.Invoke(exception); } catch { }
        _idleBoundary?.TrySetResult(true);
    }

    private static int PortSortKey(string name) =>
        name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan(3), out var number) ? number : int.MaxValue;

    private static Onk160CaptureMarker CloneMarker(Onk160CaptureMarker marker) => new()
    {
        Id = marker.Id,
        AbsoluteTimestampUtc = marker.AbsoluteTimestampUtc,
        MonotonicTimestamp = marker.MonotonicTimestamp,
        RelativeMilliseconds = marker.RelativeMilliseconds,
        RawByteOffset = marker.RawByteOffset,
        Label = marker.Label,
        Comment = marker.Comment
    };

    private static Onk160CaptureSessionManifest CloneManifest(Onk160CaptureSessionManifest manifest) => new()
    {
        SchemaVersion = manifest.SchemaVersion,
        SessionId = manifest.SessionId,
        StartTimestampUtc = manifest.StartTimestampUtc,
        SessionStartMonotonicTimestamp = manifest.SessionStartMonotonicTimestamp,
        MonotonicClockFrequency = manifest.MonotonicClockFrequency,
        StopTimestampUtc = manifest.StopTimestampUtc,
        ComPort = manifest.ComPort,
        BaudRate = manifest.BaudRate,
        DataBits = manifest.DataBits,
        Parity = manifest.Parity,
        StopBits = manifest.StopBits,
        RawFileName = manifest.RawFileName,
        MarkersFileName = manifest.MarkersFileName,
        TotalBytesReceived = manifest.TotalBytesReceived,
        TotalBytesWritten = manifest.TotalBytesWritten,
        IsComplete = manifest.IsComplete
    };
}
