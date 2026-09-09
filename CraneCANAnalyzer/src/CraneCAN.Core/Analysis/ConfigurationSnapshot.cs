using System.Text.Json;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public sealed record ObservedCanIdSnapshot
{
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public long FrameCount { get; init; }
    public List<int> DlcValues { get; init; } = [];
    public int ModalDlc { get; init; }
    public double ModalDlcAgreementPercent { get; init; }
    public List<byte> ModalBytes { get; init; } = [];
    public List<double> ByteAgreementPercent { get; init; } = [];
    public double FirstOffsetMilliseconds { get; init; }
    public double LastOffsetMilliseconds { get; init; }
    public double? AveragePeriodMilliseconds { get; init; }
    public double? MinimumPeriodMilliseconds { get; init; }
    public double? MaximumPeriodMilliseconds { get; init; }
    public double? FrequencyHertz { get; init; }
}

public sealed record ObservedProfileSignalSnapshot
{
    public Guid SignalId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint CanId { get; init; }
    public bool IsExtended { get; init; }
    public int StartByte { get; init; }
    public int StartBit { get; init; }
    public int BitLength { get; init; }
    public string Confidence { get; init; } = string.Empty;
    public bool ObservedInTrace { get; init; }
}

public sealed record ObservedConfigurationSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProgramVersion { get; init; } = "0.7.0";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string CaptureOrigin { get; init; } = "offline-trc";
    public string SourceFileName { get; init; } = string.Empty;

    public Guid? ProfileId { get; init; }
    public string MachineName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string CanBusName { get; init; } = string.Empty;
    public int? ProfileBitrate { get; init; }

    public DateTimeOffset CaptureStart { get; init; }
    public DateTimeOffset CaptureEnd { get; init; }
    public double CaptureDurationMilliseconds { get; init; }
    public int FrameCount { get; init; }

    public List<ObservedCanIdSnapshot> CanIds { get; init; } = [];
    public List<ObservedProfileSignalSnapshot> ProfileSignals { get; init; } = [];

    public string Interpretation { get; init; } =
        "Read-only снимок наблюдаемой CAN-шины. Это не дамп конфигурации ECU и не подтверждение назначения сигналов.";
}

public static class ConfigurationSnapshotAnalyzer
{
    public static ObservedConfigurationSnapshot Create(
        IEnumerable<CanFrame> frames,
        MachineProfile? profile = null,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var selected = frames
            .Where(frame =>
                frame.Protocol == BusProtocol.ClassicalCan &&
                frame.Direction == CanDirection.Rx &&
                !frame.IsRemote &&
                !frame.IsError)
            .Select(frame =>
            {
                frame.Validate();
                return frame;
            })
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        if (selected.Length == 0)
            throw new InvalidOperationException(
                "Для снимка нет обычных Rx-кадров Classical CAN.");

        var captureStart = selected[0].Timestamp;
        var captureEnd = selected[^1].Timestamp;

        var idSnapshots = selected
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .OrderBy(group => group.Key.Id)
            .ThenBy(group => group.Key.IsExtended)
            .Select(group => BuildIdSnapshot(group.Key.Id, group.Key.IsExtended, group.ToArray(), captureStart))
            .ToList();

        var observedKeys = idSnapshots
            .Select(item => (item.Id, item.IsExtended))
            .ToHashSet();

        var profileSignals = profile is null
            ? []
            : profile.KnownSignals
                .Concat(profile.ExperimentalSignals)
                .OrderBy(signal => signal.CanId)
                .ThenBy(signal => signal.StartByte)
                .ThenBy(signal => signal.StartBit)
                .Select(signal => new ObservedProfileSignalSnapshot
                {
                    SignalId = signal.SignalId,
                    Name = signal.Name,
                    CanId = signal.CanId,
                    IsExtended = signal.IsExtended,
                    StartByte = signal.StartByte,
                    StartBit = signal.StartBit,
                    BitLength = signal.BitLength,
                    Confidence = signal.Confidence.ToString(),
                    ObservedInTrace = observedKeys.Contains((signal.CanId, signal.IsExtended))
                })
                .ToList();

        return new ObservedConfigurationSnapshot
        {
            SourceFileName = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : Path.GetFileName(sourcePath),
            ProfileId = profile?.ProfileId,
            MachineName = profile?.MachineName ?? string.Empty,
            Manufacturer = profile?.Manufacturer ?? string.Empty,
            Model = profile?.Model ?? string.Empty,
            CanBusName = profile?.CanBusName ?? string.Empty,
            ProfileBitrate = profile?.Bitrate,
            CaptureStart = captureStart,
            CaptureEnd = captureEnd,
            CaptureDurationMilliseconds = (captureEnd - captureStart).TotalMilliseconds,
            FrameCount = selected.Length,
            CanIds = idSnapshots,
            ProfileSignals = profileSignals
        };
    }

    private static ObservedCanIdSnapshot BuildIdSnapshot(
        uint id,
        bool isExtended,
        IReadOnlyList<CanFrame> frames,
        DateTimeOffset captureStart)
    {
        var ordered = frames.OrderBy(frame => frame.Timestamp).ToArray();

        var dlcGroups = ordered
            .GroupBy(frame => frame.Data.Length)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToArray();
        var modalDlcGroup = dlcGroups[0];
        var modalDlc = modalDlcGroup.Key;
        var modalFrames = modalDlcGroup.ToArray();

        var modalBytes = new List<byte>(modalDlc);
        var byteAgreement = new List<double>(modalDlc);
        for (var index = 0; index < modalDlc; index++)
        {
            var mode = modalFrames
                .GroupBy(frame => frame.Data[index])
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .First();
            modalBytes.Add(mode.Key);
            byteAgreement.Add(100.0 * mode.Count() / modalFrames.Length);
        }

        var periods = new List<double>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var period = (ordered[index].Timestamp - ordered[index - 1].Timestamp).TotalMilliseconds;
            if (period >= 0) periods.Add(period);
        }

        var average = periods.Count == 0 ? (double?)null : periods.Average();
        return new ObservedCanIdSnapshot
        {
            Id = id,
            IsExtended = isExtended,
            FrameCount = ordered.Length,
            DlcValues = dlcGroups.Select(group => group.Key).Order().ToList(),
            ModalDlc = modalDlc,
            ModalDlcAgreementPercent = 100.0 * modalFrames.Length / ordered.Length,
            ModalBytes = modalBytes,
            ByteAgreementPercent = byteAgreement,
            FirstOffsetMilliseconds = (ordered[0].Timestamp - captureStart).TotalMilliseconds,
            LastOffsetMilliseconds = (ordered[^1].Timestamp - captureStart).TotalMilliseconds,
            AveragePeriodMilliseconds = average,
            MinimumPeriodMilliseconds = periods.Count == 0 ? null : periods.Min(),
            MaximumPeriodMilliseconds = periods.Count == 0 ? null : periods.Max(),
            FrequencyHertz = average is > 0 ? 1000.0 / average.Value : null
        };
    }
}

public static class ConfigurationSnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task SaveAsync(
        string path,
        ObservedConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ObservedConfigurationSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = await JsonSerializer.DeserializeAsync<ObservedConfigurationSnapshot>(
            stream, Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Файл снимка CAN пуст или имеет неверный JSON.");
        Validate(snapshot);
        return snapshot;
    }

    public static void Validate(ObservedConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != ObservedConfigurationSnapshot.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Неподдерживаемая schema снимка CAN: {snapshot.SchemaVersion}.");
        if (snapshot.FrameCount <= 0 || snapshot.CanIds.Count == 0)
            throw new InvalidDataException("Снимок CAN не содержит кадров.");
        if (snapshot.CaptureEnd < snapshot.CaptureStart)
            throw new InvalidDataException("В снимке CAN перепутаны границы времени.");

        var duplicate = snapshot.CanIds
            .GroupBy(item => (item.Id, item.IsExtended))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException(
                $"В снимке CAN повторяется ID {duplicate.Key.Id:X}.");

        long total = 0;
        foreach (var item in snapshot.CanIds)
        {
            if (item.FrameCount <= 0)
                throw new InvalidDataException($"ID {item.Id:X} имеет нулевое число кадров.");
            if (item.ModalDlc is < 0 or > 8)
                throw new InvalidDataException($"ID {item.Id:X} имеет недопустимый DLC.");
            if (item.DlcValues.Any(dlc => dlc is < 0 or > 8))
                throw new InvalidDataException($"ID {item.Id:X} содержит недопустимое значение DLC.");
            if (item.ModalBytes.Count != item.ModalDlc ||
                item.ByteAgreementPercent.Count != item.ModalDlc)
                throw new InvalidDataException(
                    $"ID {item.Id:X}: размер modal DATA не соответствует modal DLC.");
            if (item.ByteAgreementPercent.Any(value => value is < 0 or > 100) ||
                item.ModalDlcAgreementPercent is < 0 or > 100)
                throw new InvalidDataException($"ID {item.Id:X}: неверный процент согласия.");
            total += item.FrameCount;
        }

        if (total != snapshot.FrameCount)
            throw new InvalidDataException(
                $"Сумма кадров по ID ({total}) не совпадает с frameCount ({snapshot.FrameCount}).");
    }
}
