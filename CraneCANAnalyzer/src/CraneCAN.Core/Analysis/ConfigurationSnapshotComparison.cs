namespace CraneCAN.Core.Analysis;

public enum ConfigurationSnapshotDifferencePriority
{
    Info,
    Medium,
    High
}

public enum ConfigurationSnapshotDifferenceKind
{
    IdAppeared,
    IdDisappeared,
    ModalDlcChanged,
    ObservedDlcSetChanged,
    ModalDataChanged,
    PeriodChanged,
    ProfileSignalVisibilityChanged
}

public sealed record ConfigurationSnapshotDifference(
    uint? Id,
    bool? IsExtended,
    string SignalName,
    ConfigurationSnapshotDifferenceKind Kind,
    ConfigurationSnapshotDifferencePriority Priority,
    string BaselineValue,
    string CurrentValue,
    string Description);

public sealed record ConfigurationSnapshotComparisonResult(
    int BaselineIdCount,
    int CurrentIdCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ConfigurationSnapshotDifference> Differences)
{
    public int HighCount => Differences.Count(item => item.Priority == ConfigurationSnapshotDifferencePriority.High);
    public int MediumCount => Differences.Count(item => item.Priority == ConfigurationSnapshotDifferencePriority.Medium);
    public int InfoCount => Differences.Count(item => item.Priority == ConfigurationSnapshotDifferencePriority.Info);
    public bool HasDifferences => Differences.Count > 0;
}

public static class ConfigurationSnapshotComparer
{
    private const double StableAgreementThresholdPercent = 90.0;
    private const double PeriodRelativeChangeThreshold = 0.25;
    private const double PeriodMediumChangeThreshold = 0.50;
    private const double MinimumPeriodAbsoluteChangeMilliseconds = 1.0;

    public static ConfigurationSnapshotComparisonResult Compare(
        ObservedConfigurationSnapshot baseline,
        ObservedConfigurationSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ConfigurationSnapshotCodec.Validate(baseline);
        ConfigurationSnapshotCodec.Validate(current);

        var warnings = BuildWarnings(baseline, current);
        var differences = new List<ConfigurationSnapshotDifference>();

        var baselineById = baseline.CanIds.ToDictionary(item => (item.Id, item.IsExtended));
        var currentById = current.CanIds.ToDictionary(item => (item.Id, item.IsExtended));

        foreach (var key in baselineById.Keys.Union(currentById.Keys)
                     .OrderBy(key => key.Id)
                     .ThenBy(key => key.IsExtended))
        {
            baselineById.TryGetValue(key, out var baselineId);
            currentById.TryGetValue(key, out var currentId);

            if (baselineId is null)
            {
                differences.Add(new ConfigurationSnapshotDifference(
                    key.Id,
                    key.IsExtended,
                    string.Empty,
                    ConfigurationSnapshotDifferenceKind.IdAppeared,
                    ConfigurationSnapshotDifferencePriority.High,
                    "—",
                    DescribeId(currentId!),
                    "CAN ID присутствует только в сравниваемом снимке."));
                continue;
            }

            if (currentId is null)
            {
                differences.Add(new ConfigurationSnapshotDifference(
                    key.Id,
                    key.IsExtended,
                    string.Empty,
                    ConfigurationSnapshotDifferenceKind.IdDisappeared,
                    ConfigurationSnapshotDifferencePriority.High,
                    DescribeId(baselineId),
                    "—",
                    "CAN ID присутствовал в эталонном снимке, но отсутствует в сравниваемом."));
                continue;
            }

            CompareSameId(baselineId, currentId, differences);
        }

        CompareProfileSignals(baseline, current, differences, warnings);

        var ordered = differences
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id ?? uint.MaxValue)
            .ThenBy(item => item.IsExtended ?? false)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.SignalName, StringComparer.Ordinal)
            .ToArray();

        return new ConfigurationSnapshotComparisonResult(
            baseline.CanIds.Count,
            current.CanIds.Count,
            warnings,
            ordered);
    }

    private static void CompareSameId(
        ObservedCanIdSnapshot baseline,
        ObservedCanIdSnapshot current,
        ICollection<ConfigurationSnapshotDifference> differences)
    {
        if (baseline.ModalDlc != current.ModalDlc)
        {
            differences.Add(new ConfigurationSnapshotDifference(
                baseline.Id,
                baseline.IsExtended,
                string.Empty,
                ConfigurationSnapshotDifferenceKind.ModalDlcChanged,
                ConfigurationSnapshotDifferencePriority.High,
                baseline.ModalDlc.ToString(),
                current.ModalDlc.ToString(),
                "Изменился наиболее часто наблюдаемый DLC этого ID."));
        }
        else if (!baseline.DlcValues.Order().SequenceEqual(current.DlcValues.Order()))
        {
            differences.Add(new ConfigurationSnapshotDifference(
                baseline.Id,
                baseline.IsExtended,
                string.Empty,
                ConfigurationSnapshotDifferenceKind.ObservedDlcSetChanged,
                ConfigurationSnapshotDifferencePriority.Medium,
                FormatDlcSet(baseline.DlcValues),
                FormatDlcSet(current.DlcValues),
                "Набор наблюдавшихся DLC изменился, хотя modal DLC остался тем же."));
        }

        var changedBytes = Enumerable.Range(0, Math.Min(baseline.ModalBytes.Count, current.ModalBytes.Count))
            .Where(index => baseline.ModalBytes[index] != current.ModalBytes[index])
            .ToArray();

        if (changedBytes.Length > 0)
        {
            var stable = changedBytes.All(index =>
                baseline.ByteAgreementPercent[index] >= StableAgreementThresholdPercent &&
                current.ByteAgreementPercent[index] >= StableAgreementThresholdPercent);

            var transitions = string.Join(", ", changedBytes.Select(index =>
                $"DATA[{index}] {baseline.ModalBytes[index]:X2}→{current.ModalBytes[index]:X2}"));

            differences.Add(new ConfigurationSnapshotDifference(
                baseline.Id,
                baseline.IsExtended,
                string.Empty,
                ConfigurationSnapshotDifferenceKind.ModalDataChanged,
                stable
                    ? ConfigurationSnapshotDifferencePriority.High
                    : ConfigurationSnapshotDifferencePriority.Medium,
                FormatData(baseline.ModalBytes),
                FormatData(current.ModalBytes),
                stable
                    ? $"Устойчиво изменился modal DATA: {transitions}."
                    : $"Modal DATA отличается, но хотя бы один изменившийся байт нестабилен: {transitions}."));
        }

        if (baseline.AveragePeriodMilliseconds is > 0 &&
            current.AveragePeriodMilliseconds is > 0 &&
            baseline.FrameCount >= 3 &&
            current.FrameCount >= 3)
        {
            var baselinePeriod = baseline.AveragePeriodMilliseconds.Value;
            var currentPeriod = current.AveragePeriodMilliseconds.Value;
            var absolute = Math.Abs(currentPeriod - baselinePeriod);
            var relative = absolute / baselinePeriod;

            if (absolute >= MinimumPeriodAbsoluteChangeMilliseconds &&
                relative >= PeriodRelativeChangeThreshold)
            {
                differences.Add(new ConfigurationSnapshotDifference(
                    baseline.Id,
                    baseline.IsExtended,
                    string.Empty,
                    ConfigurationSnapshotDifferenceKind.PeriodChanged,
                    relative >= PeriodMediumChangeThreshold
                        ? ConfigurationSnapshotDifferencePriority.Medium
                        : ConfigurationSnapshotDifferencePriority.Info,
                    $"{baselinePeriod:0.###} мс",
                    $"{currentPeriod:0.###} мс",
                    $"Средний период изменился на {relative * 100:0.#}%. Это наблюдение о тайминге, а не доказательство неисправности."));
            }
        }
    }

    private static void CompareProfileSignals(
        ObservedConfigurationSnapshot baseline,
        ObservedConfigurationSnapshot current,
        ICollection<ConfigurationSnapshotDifference> differences,
        ICollection<string> warnings)
    {
        if (!baseline.ProfileId.HasValue ||
            !current.ProfileId.HasValue ||
            baseline.ProfileId != current.ProfileId)
        {
            if (baseline.ProfileSignals.Count > 0 || current.ProfileSignals.Count > 0)
                warnings.Add("Сравнение видимости сигналов профиля пропущено: snapshot привязаны к разным либо отсутствующим ProfileId.");
            return;
        }

        var baselineSignals = baseline.ProfileSignals.ToDictionary(item => item.SignalId);
        var currentSignals = current.ProfileSignals.ToDictionary(item => item.SignalId);

        foreach (var signalId in baselineSignals.Keys.Intersect(currentSignals.Keys))
        {
            var baselineSignal = baselineSignals[signalId];
            var currentSignal = currentSignals[signalId];
            if (baselineSignal.ObservedInTrace == currentSignal.ObservedInTrace)
                continue;

            differences.Add(new ConfigurationSnapshotDifference(
                baselineSignal.CanId,
                baselineSignal.IsExtended,
                string.IsNullOrWhiteSpace(currentSignal.Name) ? baselineSignal.Name : currentSignal.Name,
                ConfigurationSnapshotDifferenceKind.ProfileSignalVisibilityChanged,
                ConfigurationSnapshotDifferencePriority.Medium,
                baselineSignal.ObservedInTrace ? "ID наблюдался" : "ID не наблюдался",
                currentSignal.ObservedInTrace ? "ID наблюдался" : "ID не наблюдался",
                "Изменилась видимость CAN ID, связанного с сигналом текущего Machine Profile."));
        }

        var onlyBaseline = baselineSignals.Keys.Except(currentSignals.Keys).Count();
        var onlyCurrent = currentSignals.Keys.Except(baselineSignals.Keys).Count();
        if (onlyBaseline > 0 || onlyCurrent > 0)
            warnings.Add(
                $"Набор сигналов одного ProfileId различается между snapshot: только в эталоне {onlyBaseline}, только в сравниваемом {onlyCurrent}.");
    }

    private static List<string> BuildWarnings(
        ObservedConfigurationSnapshot baseline,
        ObservedConfigurationSnapshot current)
    {
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(baseline.MachineName) &&
            !string.IsNullOrWhiteSpace(current.MachineName) &&
            !string.Equals(baseline.MachineName, current.MachineName, StringComparison.Ordinal))
            warnings.Add($"Названия машин различаются: «{baseline.MachineName}» / «{current.MachineName}».");

        if (!string.IsNullOrWhiteSpace(baseline.CanBusName) &&
            !string.IsNullOrWhiteSpace(current.CanBusName) &&
            !string.Equals(baseline.CanBusName, current.CanBusName, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"CAN bus различается: {baseline.CanBusName} / {current.CanBusName}.");

        if (baseline.ProfileBitrate.HasValue &&
            current.ProfileBitrate.HasValue &&
            baseline.ProfileBitrate != current.ProfileBitrate)
            warnings.Add(
                $"Bitrate в Machine Profile различается: {baseline.ProfileBitrate:N0} / {current.ProfileBitrate:N0}. Это метаданные профиля, а не измерение из snapshot.");

        if (!string.Equals(baseline.CaptureOrigin, current.CaptureOrigin, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Источник snapshot различается: {baseline.CaptureOrigin} / {current.CaptureOrigin}.");

        var baselineDuration = baseline.CaptureDurationMilliseconds;
        var currentDuration = current.CaptureDurationMilliseconds;
        if (baselineDuration > 0 && currentDuration > 0)
        {
            var ratio = Math.Max(baselineDuration, currentDuration) /
                        Math.Min(baselineDuration, currentDuration);
            if (ratio > 1.25)
                warnings.Add(
                    $"Длительность записей существенно различается: {baselineDuration / 1000:0.###} / {currentDuration / 1000:0.###} с. Число кадров напрямую сравнивать нельзя.");
        }

        return warnings;
    }

    private static string DescribeId(ObservedCanIdSnapshot item) =>
        $"DLC {item.ModalDlc}; DATA {FormatData(item.ModalBytes)}; кадров {item.FrameCount:N0}";

    private static string FormatDlcSet(IEnumerable<int> values) =>
        string.Join("/", values.Order());

    private static string FormatData(IEnumerable<byte> values) =>
        string.Join(" ", values.Select(value => value.ToString("X2")));
}
