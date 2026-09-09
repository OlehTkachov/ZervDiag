using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class ConfigurationSnapshotTests
{
    public static async Task RunAsync()
    {
        var origin = DateTimeOffset.UnixEpoch;
        var frames = new List<CanFrame>
        {
            Frame(origin,   0, 0x100, false, 0x10, 0x20),
            Frame(origin, 100, 0x100, false, 0x10, 0x20),
            Frame(origin, 200, 0x100, false, 0x10, 0x21),
            Frame(origin, 300, 0x100, false, 0x10, 0x20),
            Frame(origin,  50, 0x18FF0101, true, 0x01),
            Frame(origin, 150, 0x18FF0101, true, 0x01, 0x02),
            Frame(origin, 250, 0x18FF0101, true, 0x01, 0x02)
        };

        frames.Add(Frame(origin, 350, 0x555, false, 0xAA) with { Direction = CanDirection.Tx });
        frames.Add(Frame(origin, 360, 0x556, false, 0xBB) with { IsError = true });

        var known = new MachineSignal
        {
            Name = "Known 0x100",
            CanId = 0x100,
            StartByte = 1,
            StartBit = 0,
            BitLength = 8,
            Confidence = SignalKnowledgeState.Confirmed
        };
        var absent = new MachineSignal
        {
            Name = "Absent 0x300",
            CanId = 0x300,
            StartByte = 0,
            StartBit = 0,
            BitLength = 1,
            Confidence = SignalKnowledgeState.Candidate
        };
        var profile = new MachineProfile
        {
            MachineName = "Test crane",
            Manufacturer = "Test",
            Model = "Snapshot",
            CanBusName = "CAN1",
            Bitrate = 250000,
            KnownSignals = [known],
            ExperimentalSignals = [absent]
        };

        var snapshot = ConfigurationSnapshotAnalyzer.Create(frames, profile, @"D:\captures\demo.trc");
        Check(snapshot.FrameCount == 7 && snapshot.CanIds.Count == 2,
            "Configuration snapshot did not filter non-Rx/error frames.");
        Check(snapshot.SourceFileName == "demo.trc" &&
              snapshot.MachineName == "Test crane" &&
              snapshot.ProfileBitrate == 250000,
            "Configuration snapshot provenance/profile metadata is incorrect.");

        var standard = snapshot.CanIds.Single(item => item.Id == 0x100 && !item.IsExtended);
        Check(standard.FrameCount == 4 &&
              standard.ModalDlc == 2 &&
              standard.ModalBytes.SequenceEqual(new byte[] { 0x10, 0x20 }) &&
              Math.Abs(standard.ByteAgreementPercent[0] - 100.0) < 0.001 &&
              Math.Abs(standard.ByteAgreementPercent[1] - 75.0) < 0.001 &&
              Math.Abs((standard.AveragePeriodMilliseconds ?? 0) - 100.0) < 0.001,
            "Stable/modal DATA or period statistics are incorrect.");

        var extended = snapshot.CanIds.Single(item => item.Id == 0x18FF0101 && item.IsExtended);
        Check(extended.DlcValues.SequenceEqual(new[] { 1, 2 }) &&
              extended.ModalDlc == 2 &&
              Math.Abs(extended.ModalDlcAgreementPercent - (200.0 / 3.0)) < 0.001,
            "Variable DLC snapshot is incorrect.");

        Check(snapshot.ProfileSignals.Single(item => item.SignalId == known.SignalId).ObservedInTrace &&
              !snapshot.ProfileSignals.Single(item => item.SignalId == absent.SignalId).ObservedInTrace,
            "Profile signal observation flags are incorrect.");

        var folder = Path.Combine(Path.GetTempPath(), $"cranecan-snapshot-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "snapshot.cansnapshot");
        try
        {
            await ConfigurationSnapshotCodec.SaveAsync(path, snapshot);
            var restored = await ConfigurationSnapshotCodec.LoadAsync(path);
            Check(restored.FrameCount == snapshot.FrameCount &&
                  restored.CanIds.Count == snapshot.CanIds.Count &&
                  restored.CanIds.Single(item => item.Id == 0x100).ModalBytes.SequenceEqual(new byte[] { 0x10, 0x20 }),
                "Configuration snapshot JSON round-trip failed.");

            var invalid = restored with { FrameCount = restored.FrameCount + 1 };
            var rejected = false;
            try
            {
                ConfigurationSnapshotCodec.Validate(invalid);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Check(rejected, "Corrupt configuration snapshot frame count was accepted.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static CanFrame Frame(
        DateTimeOffset origin,
        int milliseconds,
        uint id,
        bool extended,
        params byte[] data) => new()
    {
        Timestamp = origin.AddMilliseconds(milliseconds),
        Channel = 0,
        Id = id,
        IsExtended = extended,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
