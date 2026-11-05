using CraneCAN.Driver.Onk160.Capture;

internal static class Onk160ContinuousCaptureTests
{
    public static async Task RunAsync()
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
            var start = DateTimeOffset.Parse("2026-11-05T08:31:00.000+02:00");
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
                StopTimestampUtc = start.AddMinutes(5),
                ComPort = "COM99",
                RawFileName = Path.GetFileName(rawPath),
                MarkersFileName = Path.GetFileName(markersPath),
                TotalBytesWritten = bytes.Length,
                IsComplete = true
            };

            await Onk160SessionStore.SaveMarkersAsync(markersPath, sessionId, markers);
            await Onk160SessionStore.SaveManifestAsync(manifestPath, manifest);

            var reopened = await Onk160SessionStore.LoadAsync(manifestPath);
            Require(reopened.Markers.Count == 1_000, "ONK session did not restore 1000 markers.");
            Require(reopened.Manifest.SessionId == sessionId, "ONK session ID was not restored.");
            Require(reopened.Manifest.ComPort == "COM99", "ONK COM port metadata was not restored.");
            Require(reopened.Markers[999].Id == 1_000, "ONK marker sequence changed after reopening.");
            Require(reopened.Markers[999].RawByteOffset == 200_000,
                "ONK marker raw byte offset changed after reopening.");

            var restoredBytes = await File.ReadAllBytesAsync(reopened.RawPath);
            Require(bytes.AsSpan().SequenceEqual(restoredBytes),
                "ONK RAW byte sequence changed after reopening.");
            Require(reopened.Manifest.TotalBytesWritten == restoredBytes.LongLength,
                "ONK manifest byte count does not match the RAW file.");

            Console.WriteLine("ONK-160 continuous capture session tests passed (RAW + 1000 markers).");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
