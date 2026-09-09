using CraneCAN.Core.Analysis;

internal static class ConfigurationSnapshotComparisonTests
{
    public static void Run()
    {
        var profileId = Guid.NewGuid();
        var signalId = Guid.NewGuid();

        var baseline = Snapshot(
            profileId,
            10_000,
            [
                Id(0x100, false, 10, 2, [2], [0x10, 0x20], [100, 100], 100),
                Id(0x200, false, 5, 1, [1], [0x01], [100], 200),
                Id(0x18FF0101, true, 12, 2, [2], [0x05, 0x00], [100, 100], 100)
            ],
            [
                Signal(signalId, "Pressure enable", 0x200, false, true)
            ]);

        var current = Snapshot(
            profileId,
            10_000,
            [
                Id(0x100, false, 10, 3, [3], [0x10, 0x20, 0x00], [100, 100, 100], 100),
                Id(0x18FF0101, true, 12, 2, [2], [0x07, 0x00], [100, 100], 200),
                Id(0x400, false, 5, 1, [1], [0x99], [100], 250)
            ],
            [
                Signal(signalId, "Pressure enable", 0x200, false, false)
            ]);

        var result = ConfigurationSnapshotComparer.Compare(baseline, current);

        Check(result.Differences.Any(item =>
                item.Id == 0x200 &&
                item.Kind == ConfigurationSnapshotDifferenceKind.IdDisappeared &&
                item.Priority == ConfigurationSnapshotDifferencePriority.High),
            "Disappeared snapshot ID was not ranked HIGH.");

        Check(result.Differences.Any(item =>
                item.Id == 0x400 &&
                item.Kind == ConfigurationSnapshotDifferenceKind.IdAppeared),
            "Appeared snapshot ID was not reported.");

        Check(result.Differences.Any(item =>
                item.Id == 0x100 &&
                item.Kind == ConfigurationSnapshotDifferenceKind.ModalDlcChanged),
            "Modal DLC change was not reported.");

        Check(result.Differences.Any(item =>
                item.Id == 0x18FF0101 &&
                item.IsExtended == true &&
                item.Kind == ConfigurationSnapshotDifferenceKind.ModalDataChanged &&
                item.Priority == ConfigurationSnapshotDifferencePriority.High),
            "Stable Extended-ID modal DATA change was not ranked HIGH.");

        Check(result.Differences.Any(item =>
                item.Id == 0x18FF0101 &&
                item.Kind == ConfigurationSnapshotDifferenceKind.PeriodChanged &&
                item.Priority == ConfigurationSnapshotDifferencePriority.Medium),
            "Large period change was not reported as MEDIUM.");

        Check(result.Differences.Any(item =>
                item.Kind == ConfigurationSnapshotDifferenceKind.ProfileSignalVisibilityChanged &&
                item.SignalName == "Pressure enable"),
            "Profile signal visibility change was not reported.");

        var identical = ConfigurationSnapshotComparer.Compare(baseline, baseline);
        Check(!identical.HasDifferences && identical.Warnings.Count == 0,
            "Identical snapshots produced false differences.");

        var variableDlcBaseline = Snapshot(
            profileId,
            10_000,
            [Id(0x555, false, 10, 2, [1, 2], [0x01, 0x02], [100, 100], 100)],
            []);
        var variableDlcCurrent = Snapshot(
            profileId,
            10_000,
            [Id(0x555, false, 10, 2, [2, 3], [0x01, 0x02], [100, 100], 100)],
            []);
        var dlcSetResult = ConfigurationSnapshotComparer.Compare(variableDlcBaseline, variableDlcCurrent);
        Check(dlcSetResult.Differences.Single().Kind ==
              ConfigurationSnapshotDifferenceKind.ObservedDlcSetChanged,
            "Observed DLC-set change with unchanged modal DLC was lost.");

        var differentProfile = current with
        {
            ProfileId = Guid.NewGuid(),
            CaptureDurationMilliseconds = 20_000,
            CaptureEnd = current.CaptureStart.AddSeconds(20)
        };
        var warningResult = ConfigurationSnapshotComparer.Compare(baseline, differentProfile);
        Check(warningResult.Warnings.Any(text => text.Contains("ProfileId", StringComparison.Ordinal)) &&
              warningResult.Warnings.Any(text => text.Contains("Длительность", StringComparison.Ordinal)),
            "Snapshot compatibility warnings were not emitted.");
        Check(!warningResult.Differences.Any(item =>
                item.Kind == ConfigurationSnapshotDifferenceKind.ProfileSignalVisibilityChanged),
            "Profile signals from different ProfileId values were compared.");

        var unstableCurrent = Snapshot(
            profileId,
            10_000,
            [Id(0x777, false, 10, 1, [1], [0x02], [60], 100)],
            []);
        var unstableBaseline = Snapshot(
            profileId,
            10_000,
            [Id(0x777, false, 10, 1, [1], [0x01], [100], 100)],
            []);
        var unstableResult = ConfigurationSnapshotComparer.Compare(unstableBaseline, unstableCurrent);
        Check(unstableResult.Differences.Single(item =>
                item.Kind == ConfigurationSnapshotDifferenceKind.ModalDataChanged).Priority ==
              ConfigurationSnapshotDifferencePriority.Medium,
            "Unstable modal DATA change was incorrectly ranked HIGH.");
    }

    private static ObservedConfigurationSnapshot Snapshot(
        Guid profileId,
        double durationMilliseconds,
        List<ObservedCanIdSnapshot> ids,
        List<ObservedProfileSignalSnapshot> signals)
    {
        var start = DateTimeOffset.UnixEpoch;
        return new ObservedConfigurationSnapshot
        {
            ProfileId = profileId,
            MachineName = "Test crane",
            CanBusName = "CAN1",
            ProfileBitrate = 250_000,
            SourceFileName = "test.trc",
            CaptureStart = start,
            CaptureEnd = start.AddMilliseconds(durationMilliseconds),
            CaptureDurationMilliseconds = durationMilliseconds,
            FrameCount = checked((int)ids.Sum(item => item.FrameCount)),
            CanIds = ids,
            ProfileSignals = signals
        };
    }

    private static ObservedCanIdSnapshot Id(
        uint id,
        bool extended,
        long count,
        int modalDlc,
        List<int> dlcValues,
        List<byte> modalBytes,
        List<double> agreement,
        double averagePeriodMilliseconds) => new()
    {
        Id = id,
        IsExtended = extended,
        FrameCount = count,
        DlcValues = dlcValues,
        ModalDlc = modalDlc,
        ModalDlcAgreementPercent = 100,
        ModalBytes = modalBytes,
        ByteAgreementPercent = agreement,
        FirstOffsetMilliseconds = 0,
        LastOffsetMilliseconds = Math.Max(0, (count - 1) * averagePeriodMilliseconds),
        AveragePeriodMilliseconds = averagePeriodMilliseconds,
        MinimumPeriodMilliseconds = averagePeriodMilliseconds,
        MaximumPeriodMilliseconds = averagePeriodMilliseconds,
        FrequencyHertz = averagePeriodMilliseconds > 0 ? 1000.0 / averagePeriodMilliseconds : null
    };

    private static ObservedProfileSignalSnapshot Signal(
        Guid signalId,
        string name,
        uint canId,
        bool extended,
        bool observed) => new()
    {
        SignalId = signalId,
        Name = name,
        CanId = canId,
        IsExtended = extended,
        StartByte = 0,
        StartBit = 0,
        BitLength = 1,
        Confidence = "Candidate",
        ObservedInTrace = observed
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
