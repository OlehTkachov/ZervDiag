using System.Collections.Concurrent;
using CraneCAN.Driver.Onk160.Capture;

internal static class Onk160ContinuousCaptureTests
{
    public static async Task RunAsync()
    {
        await RunSessionStoreRoundTripAsync();
        await RunCapturePipelineRoundTripAsync();
        Console.WriteLine("ONK-160 continuous capture tests passed (RAW pipeline + 1000 markers).");
    }

    private static async Task RunSessionStoreRoundTripAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "onk160-session-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rawPath = Path.Combine(directory, "ONK160_test.raw");
            var bytes = Enumerable.Range(0, 256 * 1024)
                .Select(index => unchecked((byte)(index * 73 + (index >> 5))))
                .ToArray();
            await File.WriteAllBytesAsync(rawPath, bytes);

            var (manifestPath, markersPath) = Onk160SessionStore.GetSidecarPaths(rawPath);
            var sessionId = Guid.NewGuid();
            var start = DateTimeOffset.Parse("2026-11-05T06:31:00.000Z");
            var markers = Enumerable.Range(1, 1_000).Select(index => new Onk160CaptureMarker
            {
                Id = index,
                AbsoluteTimestampUtc = start.AddMilliseconds(index * 275),
                MonotonicTimestamp = index * 10_000,
                RelativeMilliseconds = index * 275,
                RawByteOffset = index * 200,
                Label = index % 2 == 0 ? "PRESSURE_OFF" : "MARK",
                Comment = index % 3 == 0 ? "test marker" : ""
            }).ToArray();

            var manifest = new Onk160CaptureSessionManifest
            {
                SessionId = sessionId,
                StartTimestampUtc = start,
                SessionStartMonotonicTimestamp = 1_000_000,
                SessionStopMonotonicTimestamp = 3_001_000_000,
                MonotonicClockFrequency = 10_000_000,
                StopTimestampUtc = start.AddMinutes(5),
                ComPort = "COM99",
                RawFileName = Path.GetFileName(rawPath),
                MarkersFileName = Path.GetFileName(markersPath),
                TotalBytesReceived = bytes.Length,
                TotalBytesWritten = bytes.Length,
                IsComplete = true
            };

            await Onk160SessionStore.SaveMarkersAsync(markersPath, sessionId, markers);
            await Onk160SessionStore.SaveManifestAsync(manifestPath, manifest);
            var markerJson = await File.ReadAllTextAsync(markersPath);
            Require(!markerJson.Contains("RelativeTimeText", StringComparison.Ordinal),
                "Computed marker display text must not be persisted as a second time source.");

            var reopened = await Onk160SessionStore.LoadAsync(manifestPath);
            Require(reopened.Markers.Count == 1_000, "ONK session did not restore 1000 markers.");
            Require(reopened.Manifest.SessionId == sessionId, "ONK session ID was not restored.");
            Require(reopened.Manifest.ComPort == "COM99", "ONK COM port metadata was not restored.");
            Require(reopened.Manifest.TotalBytesReceived == bytes.Length,
                "ONK received byte count was not restored.");
            Require(reopened.Markers[999].Id == 1_000, "ONK marker sequence changed after reopening.");
            Require(reopened.Markers[999].RawByteOffset == 200_000,
                "ONK marker raw byte offset changed after reopening.");

            var restoredBytes = await File.ReadAllBytesAsync(reopened.RawPath);
            Require(bytes.AsSpan().SequenceEqual(restoredBytes),
                "ONK RAW byte sequence changed after reopening.");
            Require(reopened.Manifest.TotalBytesWritten == restoredBytes.LongLength,
                "ONK manifest byte count does not match the RAW file.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    private static async Task RunCapturePipelineRoundTripAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "onk160-pipeline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = new SyntheticOnk160ByteSource("COM99");
        await using var capture = new Onk160ContinuousCapture(_ => source);
        try
        {
            await capture.ConnectAsync("COM99");
            var rawPath = Path.Combine(directory, "ONK160_pipeline.raw");
            await capture.StartRecordingAsync(rawPath);

            using var expected = new MemoryStream();
            var markerTasks = new List<Task<Onk160CaptureMarker>>();
            for (var index = 0; index < 1_000; index++)
            {
                var chunk = Enumerable.Range(0, 512)
                    .Select(offset => unchecked((byte)(index * 19 + offset * 7)))
                    .ToArray();
                expected.Write(chunk);
                source.Enqueue(chunk);

                var label = index % 2 == 0 ? "PRESSURE_OFF" : "KOS_ON";
                markerTasks.Add(capture.AddMarkerAsync(label, "Synthetic pipeline marker"));
            }

            await Task.WhenAll(markerTasks);
            var activeManifestPath = capture.CurrentManifestPath
                ?? throw new InvalidOperationException("Active capture manifest path is missing.");
            var activeSession = await Onk160SessionStore.LoadAsync(activeManifestPath);
            Require(activeSession.Markers.Count == 1_000,
                "AddMarkerAsync returned before all marker sidecars were persisted.");

            // Put the final chunk into the source immediately before STOP. The reader must
            // drain bytes already buffered by the source before closing the RAW file.
            var finalChunk = Convert.FromHexString("CA E5 E6 E8 F7 00 FF 10 2A 55 AA");
            expected.Write(finalChunk);
            source.Enqueue(finalChunk);

            await capture.StopRecordingAsync();

            Require(capture.BytesReceived == expected.Length,
                "Capture reader did not account for every synthetic input byte.");
            Require(capture.BytesWritten == expected.Length,
                "Capture writer did not write every synthetic input byte.");
            Require(capture.CurrentManifest is { IsComplete: true },
                "Capture session was not marked complete after an exact byte round-trip.");
            Require(capture.CurrentManifest!.TotalBytesReceived == expected.Length,
                "Session manifest received-byte count mismatch.");
            Require(capture.CurrentManifest.TotalBytesWritten == expected.Length,
                "Session manifest durable-byte count mismatch.");

            var rawPathAfterStop = capture.CurrentRawPath
                ?? throw new InvalidOperationException("Capture did not retain its RAW path.");
            var savedBytes = await File.ReadAllBytesAsync(rawPathAfterStop);
            Require(expected.ToArray().AsSpan().SequenceEqual(savedBytes),
                "RAW bytes differ from the exact synthetic source sequence.");

            var manifestPath = capture.CurrentManifestPath
                ?? throw new InvalidOperationException("Capture did not retain its session manifest path.");
            var reopened = await Onk160SessionStore.LoadAsync(manifestPath);
            Require(reopened.Markers.Count == 1_000,
                "Capture pipeline did not persist all 1000 in-session markers.");
            Require(reopened.Markers.Select(marker => marker.Id).SequenceEqual(
                    Enumerable.Range(1, 1_000).Select(index => (long)index)),
                "Capture marker IDs are not consecutive.");
            Require(reopened.Markers.Zip(reopened.Markers.Skip(1),
                    (left, right) => right.RawByteOffset >= left.RawByteOffset).All(value => value),
                "Capture marker RAW offsets moved backwards.");
        }
        finally
        {
            await capture.DisconnectAsync();
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class SyntheticOnk160ByteSource(string portName) : IOnk160ByteSource
    {
        private readonly ConcurrentQueue<byte[]> _chunks = new();
        private readonly AutoResetEvent _dataAvailable = new(false);
        private byte[]? _currentChunk;
        private int _currentOffset;
        private int _open;

        public string PortName { get; } = portName;
        public bool IsOpen => Volatile.Read(ref _open) == 1;

        public int BytesAvailable
        {
            get
            {
                var current = _currentChunk is null ? 0 : _currentChunk.Length - _currentOffset;
                return current + _chunks.Sum(chunk => chunk.Length);
            }
        }

        public void Open() => Volatile.Write(ref _open, 1);

        public void Enqueue(byte[] bytes)
        {
            _chunks.Enqueue(bytes.ToArray());
            _dataAvailable.Set();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            while (IsOpen)
            {
                if (_currentChunk is null || _currentOffset >= _currentChunk.Length)
                {
                    _currentChunk = null;
                    _currentOffset = 0;
                    if (_chunks.TryDequeue(out var next))
                        _currentChunk = next;
                }

                if (_currentChunk is not null)
                {
                    var length = Math.Min(count, _currentChunk.Length - _currentOffset);
                    Array.Copy(_currentChunk, _currentOffset, buffer, offset, length);
                    _currentOffset += length;
                    return length;
                }

                if (!_dataAvailable.WaitOne(100))
                    throw new TimeoutException();
            }

            return 0;
        }

        public void Close()
        {
            Volatile.Write(ref _open, 0);
            _dataAvailable.Set();
        }

        public void Dispose()
        {
            Close();
            _dataAvailable.Dispose();
        }
    }
}
