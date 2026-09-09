using System.Globalization;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Storage;

public sealed class LiveTrcWriter : IAsyncDisposable
{
    public static string CreateCaptureFileName(string source, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Capture source must contain ASCII letters, digits or underscores.", nameof(source));
        return $"CraneCAN_{source}_{timestamp.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.trc";
    }

    private readonly StreamWriter _writer;
    private readonly DateTimeOffset _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _number;
    private bool _disposed;

    private LiveTrcWriter(StreamWriter writer, DateTimeOffset start)
    {
        _writer = writer;
        _start = start;
    }

    public static async Task<LiveTrcWriter> CreateAsync(
        string path, DateTimeOffset start, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Atomic create: a name collision must never truncate diagnostic evidence.
        var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        try
        {
            var result = new LiveTrcWriter(writer, start);
            await writer.WriteLineAsync(";$FILEVERSION=2.1".AsMemory(), cancellationToken);
            await writer.WriteLineAsync($";$STARTTIME={start.LocalDateTime.ToOADate().ToString("F10", CultureInfo.InvariantCulture)}".AsMemory(), cancellationToken);
            // PCAN uses a local OA date; retain the exact absolute origin for portable round-trips.
            await writer.WriteLineAsync($";$CRANECAN_STARTUTC={start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}".AsMemory(), cancellationToken);
            await writer.WriteLineAsync(";$COLUMNS=N,O,T,B,I,d,R,L,D".AsMemory(), cancellationToken);
            await writer.WriteLineAsync("; CraneCAN Live Guided raw receive-only capture".AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return result;
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task AppendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame.Validate();
        if (frame.Protocol != BusProtocol.ClassicalCan || frame.Direction != CanDirection.Rx || frame.IsRemote || frame.IsError) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var number = ++_number;
            var offset = (frame.Timestamp - _start).TotalMilliseconds;
            var id = frame.IsExtended ? frame.Id.ToString("X8") : frame.Id.ToString("X3");
            var line = $"{number} {offset.ToString("F3", CultureInfo.InvariantCulture)} DT {frame.Channel + 1} {id} Rx - {frame.Dlc} {frame.DataText}";
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (number % 256 == 0) await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writer.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _writer.FlushAsync().ConfigureAwait(false); _writer.Dispose(); _disposed = true; }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
