using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;
using CraneCAN.Core.Drivers;

internal static class StreamingScanTests
{
    public static async Task RunAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        foreach (var path in Directory.EnumerateFiles(fixtureDirectory, "*.trc", SearchOption.AllDirectories))
        {
            PcanTrcImportResult imported;
            try { imported = await PcanTrcCodec.LoadWithDiagnosticsAsync(path); }
            catch (FormatException) { continue; }
            var streamed = new List<CraneCAN.Core.Models.CanFrame>();
            await foreach (var frame in PcanTrcCodec.ReadFramesAsync(path)) streamed.Add(frame);
            Check(streamed.Count == imported.Frames.Count && streamed.Zip(imported.Frames).All(pair =>
                pair.First.Timestamp == pair.Second.Timestamp && pair.First.Id == pair.Second.Id &&
                pair.First.IsExtended == pair.Second.IsExtended && pair.First.Channel == pair.Second.Channel &&
                pair.First.Direction == pair.Second.Direction && pair.First.Data.SequenceEqual(pair.Second.Data)),
                "Streaming frames differ from import: " + path);
            var scan = await PcanTrcCodec.ScanAsync(path);
            Check(scan.FrameCount == imported.Frames.Count && scan.FirstTimestamp == imported.Frames[0].Timestamp &&
                scan.LastTimestamp == imported.Frames[^1].Timestamp && scan.TotalLines == imported.TotalLines &&
                scan.UnknownOrMalformedLines == imported.UnknownOrMalformedLines &&
                scan.RemoteFramesSkipped == imported.RemoteFramesSkipped && scan.ErrorFramesSkipped == imported.ErrorFramesSkipped,
                "Streaming scan differs from import: " + path);
        }
        var temp = Path.Combine(Path.GetTempPath(), "cranecan-scan-" + Guid.NewGuid().ToString("N") + ".trc");
        try
        {
            await File.WriteAllTextAsync(temp, "; empty trace\n");
            Check((await PcanTrcCodec.ScanAsync(temp)).FrameCount == 0, "Empty scan invents frames.");
            const int count = 2_000_001;
            using (var writer = new StreamWriter(temp))
            {
                writer.WriteLine(";$FILEVERSION=1.1");
                for (var i = 0; i < count; i++) writer.WriteLine($"{i + 1}) {i}.000 Rx 018F 1 02");
            }
            var reports = new List<PcanTrcScanProgress>();
            var large = await PcanTrcCodec.ScanAsync(temp, new CallbackProgress(reports.Add));
            Check(large.FrameCount == count && large.Ordered && large.ReceiveFrameCount == count &&
                (large.LastTimestamp - large.FirstTimestamp)?.TotalMilliseconds == count - 1,
                "Multi-million streaming scan lost frames or timestamps.");
            Check(reports[0].Percent == 0 && reports[^1].Percent == 100 &&
                reports.Zip(reports.Skip(1)).All(pair => pair.First.Percent <= pair.Second.Percent), "Invalid progress sequence.");
            await using (var driver = new ReplayCanDriver(temp, ReplayTimingMode.Accelerated, 1_000_000_000))
            {
                await driver.OpenAsync(new CanChannelSettings("replay-trc", 250000));
                long read = 0;
                await foreach (var frame in driver.ReadFramesAsync()) read++;
                Check(read == count && driver.GetStatus().ReceivedFrames == count, "Replay retained million-frame limit.");
                await driver.CloseAsync();
                await driver.OpenAsync(new CanChannelSettings("replay-trc", 250000));
                await using var restarted = driver.ReadFramesAsync().GetAsyncEnumerator();
                Check(await restarted.MoveNextAsync() && restarted.Current.Timestamp == large.FirstTimestamp,
                    "Replay did not restart at first frame.");
            }
            await using (var step = new ReplayCanDriver(temp, ReplayTimingMode.Step))
            {
                await step.OpenAsync(new CanChannelSettings("replay-trc", 250000));
                await using var reader = step.ReadFramesAsync().GetAsyncEnumerator();
                var pending = reader.MoveNextAsync().AsTask();
                await step.CloseAsync();
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("Close did not cancel pending step.");
                }
                catch (OperationCanceledException) { }
            }
            using var cancellation = new CancellationTokenSource();
            try
            {
                await PcanTrcCodec.ScanAsync(temp, new CallbackProgress(value =>
                {
                    if (value.FrameCount > 0) cancellation.Cancel();
                }), cancellation.Token);
                throw new InvalidOperationException("Scan ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            // The cancellation path must release the file handle.
            using var exclusive = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(temp); }
    }

    private sealed class CallbackProgress(Action<PcanTrcScanProgress> report) : IProgress<PcanTrcScanProgress>
    {
        public void Report(PcanTrcScanProgress value) => report(value);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
