using System.Text.Json;
using CraneCAN.Core.Live;

namespace CraneCAN.Core.Storage;

public sealed record IncidentSource(string DriverId, string ChannelId, int? Bitrate,
    string? ReplaySourcePath, string? ContinuousRawPath,
    long LostFrames = 0, long ErrorFrames = 0, bool ListenOnlyConfirmed = false);

public static class PreFaultIncidentCodec
{
    public static async Task<string> SaveAsync(string parentDirectory, PreFaultIncident incident,
        IncidentSource source, CancellationToken cancellationToken = default)
    {
        // A unique directory keeps TRC and metadata portable as one folder.
        var directory = Path.Combine(parentDirectory, $"incident_{incident.IncidentId:N}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var rawPath = Path.Combine(directory, "capture.trc");
        await using (var writer = await LiveTrcWriter.CreateAsync(rawPath, incident.WindowStart, cancellationToken))
        {
            foreach (var frame in incident.Frames) await writer.AppendAsync(frame, cancellationToken);
        }
        var document = new
        {
            SchemaVersion = 1, ProgramVersion = "0.7.0", incident.IncidentId,
            SavedAt = DateTimeOffset.UtcNow, Source = source,
            CaptureOrigin = source.DriverId == "pcan-trc-replay" ? "replay" : "liveOrUnknown",
            incident.WindowStart, incident.WindowEnd, incident.ObservedUntil,
            incident.Complete, incident.QualityCodes, incident.Markers,
            FrameCount = incident.Frames.Count, RawTracePath = "capture.trc",
            Interpretation = "Наблюдаемая запись вокруг отметки; причина неисправности не установлена."
        };
        var path = Path.Combine(directory, "incident.canincident");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }, cancellationToken);
        return path;
    }
}
