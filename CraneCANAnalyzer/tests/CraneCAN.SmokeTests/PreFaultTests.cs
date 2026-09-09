using System.Text.Json;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

internal static class PreFaultTests
{
    public static async Task RunAsync()
    {
        var recorder = new PreFaultRecorder();
        for (var second = 0; second <= 10; second++) recorder.Append(Frame(second));
        recorder.Mark("Operator marker");
        recorder.Append(Frame(11));
        recorder.Mark("Second marker");
        for (var second = 12; second <= 15; second++) recorder.Append(Frame(second));
        var full = recorder.LastIncident!;
        Check(full.Complete && full.Frames.Count == 15 && full.Markers.Count == 2,
            "Pre/post interval or merged markers are incorrect.");
        Check(full.WindowEnd == DateTimeOffset.UnixEpoch.AddSeconds(15) && !recorder.IsCollecting,
            "Additional marker unexpectedly extended the bounded post window.");
        Check(full.Frames.All(frame => frame.Timestamp >= full.WindowStart && frame.Timestamp < full.WindowEnd),
            "Incident contains a frame outside its half-open interval.");

        var shortCapture = new PreFaultRecorder();
        shortCapture.Append(Frame(1));
        shortCapture.Mark("Early");
        shortCapture.Stop();
        Check(shortCapture.LastIncident!.QualityCodes.Contains("SHORT_PREHISTORY") &&
              shortCapture.LastIncident.QualityCodes.Contains("POST_INTERRUPTED") && !shortCapture.LastIncident.Complete,
            "Incomplete recording was presented as complete.");

        var limited = new PreFaultRecorder(maximumFrames: 3);
        for (var second = 0; second <= 10; second++) limited.Append(Frame(second));
        limited.Mark("Capacity");
        for (var second = 11; second <= 15; second++) limited.Append(Frame(second));
        Check(limited.LastIncident!.Frames.Count == 3 &&
              limited.LastIncident.QualityCodes.Contains("PREHISTORY_FRAME_LIMIT") &&
              limited.LastIncident.QualityCodes.Contains("FRAME_LIMIT"), "Frame limits are not enforced.");

        var uncertain = new PreFaultRecorder();
        for (var second = 0; second <= 10; second++) uncertain.Append(Frame(second));
        uncertain.Mark("Loss");
        uncertain.MarkCaptureUncertain();
        uncertain.Append(Frame(15));
        Check(!uncertain.LastIncident!.Complete && uncertain.LastIncident.QualityCodes.Contains("CAPTURE_UNCERTAIN"),
            "Capture uncertainty was lost.");

        var copied = new PreFaultRecorder();
        var original = Frame(0);
        copied.Append(original);
        original.Data[0] = 255;
        copied.Mark("Copy");
        copied.Stop();
        Check(copied.LastIncident!.Frames[0].Data[0] == 0, "Receive buffer mutation changed incident evidence.");

        var folder = Path.Combine(Path.GetTempPath(), $"cranecan-incident-tests-{Guid.NewGuid():N}");
        try
        {
            var source = new IncidentSource("pcan-trc-replay", "replay-trc", null, "demo.trc", "raw.trc");
            var path = await PreFaultIncidentCodec.SaveAsync(folder, full, source);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Check(json.RootElement.GetProperty("captureOrigin").GetString() == "replay" &&
                  json.RootElement.GetProperty("complete").GetBoolean(), "Incident provenance not serialized.");
            var trc = Path.Combine(Path.GetDirectoryName(path)!, json.RootElement.GetProperty("rawTracePath").GetString()!);
            var restored = await PcanTrcCodec.LoadAsync(trc);
            Check(restored.Count == full.Frames.Count && restored.Select(frame => frame.Timestamp)
                .SequenceEqual(full.Frames.Select(frame => frame.Timestamp)), "Incident TRC timestamps failed round-trip.");
            var secondPath = await PreFaultIncidentCodec.SaveAsync(folder, full, source);
            Check(secondPath != path && File.Exists(path), "Saving an incident overwrote a previous export.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static CanFrame Frame(int second) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(second), Channel = 0, Id = 0x123,
        Data = [(byte)second], Protocol = BusProtocol.ClassicalCan, Direction = CanDirection.Rx
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
