using CraneCAN.Core.Drivers;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

internal static class ReplayCoverageTests
{
    public static async Task RunAsync()
    {
        var frames = await PcanTrcCodec.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "live_guided_demo.trc"));
        var configuration = new LiveExperimentConfiguration();
        var coverage = ReplayCoverage.Inspect(frames, configuration);
        Check(coverage.CanComplete && coverage.Available == TimeSpan.FromSeconds(23), "23-second replay should cover default experiment.");
        var longConfiguration = configuration with { ActionDuration = TimeSpan.FromSeconds(30) };
        Check(!ReplayCoverage.Inspect(frames, longConfiguration).CanComplete, "Short replay accepted for 43-second experiment.");
        Check(!ReplayCoverage.Inspect(Array.Empty<CanFrame>(), configuration).CanComplete, "Empty replay accepted.");
        Check(!ReplayCoverage.Inspect(frames.Select(f => f with { Direction = CanDirection.Tx }), configuration).CanComplete, "Transmit-only replay accepted.");
        Check(!ReplayCoverage.Inspect(frames.Reverse(), configuration).CanComplete, "Unordered replay accepted.");
        Check(ReplayCoverage.Inspect(frames.Select(f => f with { Timestamp = f.Timestamp.AddDays(30) }), configuration).CanComplete,
            "Coverage depends on wall clock.");

        foreach (var incomplete in new[] { false, true })
        {
            var rawPath = Path.Combine(Path.GetTempPath(), $"cranecan-eof-{Guid.NewGuid():N}.trc");
            try
            {
                await using var receiver = new LiveCanReceiver(new ReplayCanDriver(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "live_guided_demo.trc"), ReplayTimingMode.Accelerated, 10000));
                var session = new LiveExperimentSession(incomplete ? longConfiguration : configuration);
                session.Start(frames[0].Timestamp);
                receiver.AttachSession(session);
                var faults = 0;
                var ends = 0;
                var marked = false;
                receiver.ReceiverFaulted += _ => faults++;
                receiver.ReplayEnded += () => ends++;
                receiver.FrameReceived += frame =>
                {
                    if (!marked && frame.Timestamp >= frames[0].Timestamp.AddSeconds(20))
                    {
                        receiver.Incidents.Mark("Late marker");
                        marked = true;
                    }
                };
                await receiver.StartAsync(new CanChannelSettings("replay-trc", 250000, ListenOnly: true), rawPath, frames[0].Timestamp);
                await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(ends == 1 && faults == 0 && receiver.LastError is null, "Normal replay EOF reported as CAN fault.");
                Check(incomplete ? session.State == LiveExperimentState.Aborted && session.Warnings.Any(w => w.Code == LiveSessionWarningCode.ReplayExhausted)
                    : session.State == LiveExperimentState.Analyzing, "EOF did not preserve experiment validity.");
                var incident = receiver.Incidents.LastIncident!;
                Check(!incident.Complete && incident.QualityCodes.Contains("REPLAY_ENDED") && incident.QualityCodes.Contains("POST_INTERRUPTED") &&
                    !incident.QualityCodes.Contains("CAPTURE_UNCERTAIN"), "EOF mislabels incomplete incident as capture loss.");
            }
            finally { File.Delete(rawPath); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
