using System.Text.Json;
using CraneCAN.Core.Live;

namespace CraneCAN.Core.Storage;

public sealed record IncidentSource(string DriverId, string ChannelId, int? Bitrate,
    string? ReplaySourcePath, string? ContinuousRawPath,
    long LostFrames = 0, long ErrorFrames = 0, bool ListenOnlyConfirmed = false);

public sealed record LoadedIncidentPackage(
    string MetadataPath,
    string RawTracePath,
    PreFaultIncident Incident,
    IncidentSource Source,
    string CaptureOrigin,
    DateTimeOffset SavedAt,
    string Interpretation);

public static class PreFaultIncidentCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

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

        var document = new IncidentDocument
        {
            SchemaVersion = 1,
            ProgramVersion = "0.7.0",
            IncidentId = incident.IncidentId,
            SavedAt = DateTimeOffset.UtcNow,
            Source = source,
            CaptureOrigin = source.DriverId == "pcan-trc-replay" ? "replay" : "liveOrUnknown",
            WindowStart = incident.WindowStart,
            WindowEnd = incident.WindowEnd,
            ObservedUntil = incident.ObservedUntil,
            Complete = incident.Complete,
            QualityCodes = incident.QualityCodes,
            Markers = incident.Markers,
            FrameCount = incident.Frames.Count,
            RawTracePath = "capture.trc",
            Interpretation = "Наблюдаемая запись вокруг отметки; причина неисправности не установлена."
        };

        var path = Path.Combine(directory, "incident.canincident");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        return path;
    }

    public static async Task<LoadedIncidentPackage> LoadAsync(
        string metadataPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        var fullMetadataPath = Path.GetFullPath(metadataPath);
        if (!File.Exists(fullMetadataPath))
            throw new FileNotFoundException("Файл CraneCAN incident не найден.", fullMetadataPath);

        IncidentDocument document;
        await using (var stream = new FileStream(fullMetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            document = await JsonSerializer.DeserializeAsync<IncidentDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new FormatException("Пустой или некорректный файл .canincident.");
        }

        ValidateDocument(document);

        var directory = Path.GetDirectoryName(fullMetadataPath)
            ?? throw new FormatException("Не удалось определить каталог .canincident.");
        if (Path.IsPathRooted(document.RawTracePath))
            throw new FormatException("rawTracePath должен быть относительным путём внутри incident-папки.");

        var rawPath = Path.GetFullPath(Path.Combine(directory, document.RawTracePath));
        var relative = Path.GetRelativePath(directory, rawPath);
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new FormatException("rawTracePath выходит за пределы incident-папки.");
        }

        if (!File.Exists(rawPath))
            throw new FileNotFoundException("Связанный capture.trc не найден.", rawPath);

        var frames = await PcanTrcCodec.LoadAsync(rawPath, cancellationToken: cancellationToken);
        if (frames.Count != document.FrameCount)
            throw new FormatException(
                $"Число кадров capture.trc ({frames.Count}) не совпадает с метаданными ({document.FrameCount}).");

        if (frames.Any(frame => frame.Timestamp < document.WindowStart || frame.Timestamp >= document.WindowEnd))
            throw new FormatException("capture.trc содержит кадры вне сохранённого incident-окна.");

        var incident = new PreFaultIncident(
            document.IncidentId,
            document.WindowStart,
            document.WindowEnd,
            document.ObservedUntil,
            document.Markers!.ToArray(),
            frames,
            document.QualityCodes!.ToArray());

        return new LoadedIncidentPackage(
            fullMetadataPath,
            rawPath,
            incident,
            document.Source!,
            document.CaptureOrigin,
            document.SavedAt,
            document.Interpretation);
    }

    private static void ValidateDocument(IncidentDocument document)
    {
        if (document.SchemaVersion != 1)
            throw new NotSupportedException($"Schema .canincident {document.SchemaVersion} не поддерживается.");
        if (document.IncidentId == Guid.Empty)
            throw new FormatException("В .canincident отсутствует incidentId.");
        if (document.Source is null)
            throw new FormatException("В .canincident отсутствуют данные source.");
        if (document.WindowEnd <= document.WindowStart)
            throw new FormatException("Некорректные границы incident-окна.");
        if (document.ObservedUntil < document.WindowStart)
            throw new FormatException("observedUntil расположен раньше начала incident-окна.");
        if (string.IsNullOrWhiteSpace(document.RawTracePath))
            throw new FormatException("В .canincident отсутствует rawTracePath.");
        if (document.FrameCount < 0)
            throw new FormatException("Некорректное число кадров в .canincident.");
        if (document.Markers is null || document.Markers.Count == 0)
            throw new FormatException("В .canincident отсутствует отметка события.");
        if (document.Markers.Any(marker =>
                marker.Timestamp < document.WindowStart || marker.Timestamp >= document.WindowEnd))
            throw new FormatException("Отметка события находится вне incident-окна.");
        if (document.QualityCodes is null)
            throw new FormatException("В .canincident отсутствуют коды качества.");
        if (document.Complete != (document.QualityCodes.Count == 0))
            throw new FormatException("Флаг complete противоречит кодам качества.");
    }

    private sealed record IncidentDocument
    {
        public int SchemaVersion { get; init; }
        public string ProgramVersion { get; init; } = "";
        public Guid IncidentId { get; init; }
        public DateTimeOffset SavedAt { get; init; }
        public IncidentSource? Source { get; init; }
        public string CaptureOrigin { get; init; } = "";
        public DateTimeOffset WindowStart { get; init; }
        public DateTimeOffset WindowEnd { get; init; }
        public DateTimeOffset ObservedUntil { get; init; }
        public bool Complete { get; init; }
        public IReadOnlyList<string>? QualityCodes { get; init; } = [];
        public IReadOnlyList<IncidentMarker>? Markers { get; init; } = [];
        public int FrameCount { get; init; }
        public string RawTracePath { get; init; } = "";
        public string Interpretation { get; init; } = "";
    }
}
