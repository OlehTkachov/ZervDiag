using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class LivePhysicalSignalMonitorTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        DecodesFreshEngineeringValueAndRate();
        MarksOldValueStale();
        LimitsStatisticsToTrailingWindow();
        IgnoresTxRemoteErrorAndWrongFormatFrames();
        ReportsNoDataAndShortDlc();
        KeepsLatestJ1939SourceAddressSeparate();
        SupportsSignedBigEndianSignal();
    }

    private static void DecodesFreshEngineeringValueAndRate()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:30:00+00:00");
        var signal = Signal(0x401, false, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "Boom length",
            Scale = 0.01,
            Offset = 5,
            Unit = "m"
        };
        var frames = Enumerable.Range(0, 10)
            .Select(index => Frame(
                t0.AddMilliseconds(index * 100),
                0x401,
                false,
                U16Le(1000 + index * 10)))
            .ToArray();

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddMilliseconds(950));

        Check(result.State == LivePhysicalSignalState.Fresh,
            "Recent calibrated signal was not marked FRESH.");
        Check(Approximately(result.CurrentRaw, 1090),
            "Current raw value is incorrect.");
        Check(Approximately(result.CurrentPhysical, 15.9),
            "Scale/Offset were not applied to current engineering value.");
        Check(Approximately(result.MinimumPhysical, 15.0) &&
              Approximately(result.MaximumPhysical, 15.9),
            "Physical min/max are incorrect.");
        Check(Approximately(result.RatePerSecond, 1.0, 1e-9),
            "Least-squares physical rate is incorrect.");
        Check(result.SampleCount == 10 && result.Age == TimeSpan.FromMilliseconds(50),
            "Fresh reading sample count/age is incorrect.");
    }

    private static void MarksOldValueStale()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:31:00+00:00");
        var signal = Signal(0x402, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Pressure",
            Scale = 0.5,
            Unit = "bar"
        };
        var frames = Enumerable.Range(0, 5)
            .Select(index => Frame(t0.AddMilliseconds(index * 100), 0x402, false, [100]))
            .ToArray();

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddSeconds(5),
            new LivePhysicalSignalMonitorOptions
            {
                StaleAfter = TimeSpan.FromSeconds(2),
                StatisticsWindow = TimeSpan.FromSeconds(5)
            });

        Check(result.State == LivePhysicalSignalState.Stale,
            "Old signal was not marked STALE.");
        Check(result.Age > TimeSpan.FromSeconds(4),
            "STALE age is incorrect.");
    }

    private static void LimitsStatisticsToTrailingWindow()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:32:00+00:00");
        var signal = Signal(0x403, false, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "Angle",
            Scale = 0.1,
            Unit = "deg"
        };
        var frames = new[]
        {
            Frame(t0, 0x403, false, U16Le(10)),
            Frame(t0.AddSeconds(6), 0x403, false, U16Le(100)),
            Frame(t0.AddSeconds(7), 0x403, false, U16Le(120))
        };

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddSeconds(7),
            new LivePhysicalSignalMonitorOptions
            {
                StatisticsWindow = TimeSpan.FromSeconds(2),
                StaleAfter = TimeSpan.FromSeconds(2)
            });

        Check(result.SampleCount == 2,
            "Statistics window retained an old sample.");
        Check(Approximately(result.MinimumPhysical, 10) &&
              Approximately(result.MaximumPhysical, 12),
            "Trailing-window min/max include data outside the configured window.");
    }

    private static void IgnoresTxRemoteErrorAndWrongFormatFrames()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:33:00+00:00");
        var signal = Signal(0x404, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Load",
            Unit = "%"
        };
        var frames = new List<CanFrame>
        {
            Frame(t0, 0x404, false, [42]),
            Frame(t0.AddMilliseconds(100), 0x404, false, [43]),
            Frame(t0.AddMilliseconds(200), 0x404, false, [250]) with { Direction = CanDirection.Tx },
            Frame(t0.AddMilliseconds(300), 0x404, false, [251]) with { IsRemote = true },
            Frame(t0.AddMilliseconds(400), 0x404, false, [252]) with { IsError = true },
            Frame(t0.AddMilliseconds(500), 0x404, true, [253]),
            Frame(t0.AddMilliseconds(600), 0x405, false, [254])
        };

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddMilliseconds(200));

        Check(result.SampleCount == 2 && Approximately(result.CurrentRaw, 43),
            "Non-Rx or wrong ID/format frames contaminated the physical monitor.");
        Check(result.ObservedCanIds.SequenceEqual(new[] { 0x404u }),
            "Observed ID list contains unrelated frames.");
    }

    private static void ReportsNoDataAndShortDlc()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:34:00+00:00");
        var signal = Signal(0x406, false, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "Cylinder",
            Unit = "mm"
        };

        var noData = LivePhysicalSignalMonitor.Evaluate(
            signal,
            [Frame(t0, 0x407, false, [1, 2])],
            t0);
        Check(noData.State == LivePhysicalSignalState.NoData &&
              noData.CurrentPhysical is null,
            "Missing signal should produce NO DATA rather than a fabricated value.");

        var shortDlc = LivePhysicalSignalMonitor.Evaluate(
            signal,
            [Frame(t0, 0x406, false, [1])],
            t0);
        Check(shortDlc.State == LivePhysicalSignalState.NoData &&
              shortDlc.MatchingFramesWithShortDlc == 1,
            "Short-DLC matching frame was not reported correctly.");
    }

    private static void KeepsLatestJ1939SourceAddressSeparate()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:35:00+00:00");
        const uint firstSource = 0x18F004A1;
        const uint latestSource = 0x18F004B2;
        var signal = Signal(firstSource, true, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "J1939 speed",
            Protocol = "J1939",
            J1939Pgn = 0x0F004,
            J1939Spn = 190,
            Scale = 0.125,
            Unit = "rpm"
        };
        var frames = new List<CanFrame>();
        for (var index = 0; index < 5; index++)
        {
            frames.Add(Frame(t0.AddMilliseconds(index * 100), firstSource, true, U16Le(8000)));
            frames.Add(Frame(t0.AddMilliseconds(50 + index * 100), latestSource, true, U16Le(9600 + index * 80)));
        }

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddMilliseconds(500));

        Check(result.State == LivePhysicalSignalState.Fresh,
            "J1939 reading should be fresh.");
        Check(result.J1939Pgn == 0x0F004 && result.J1939SourceAddress == 0xB2,
            "J1939 PGN/source metadata is incorrect.");
        Check(result.ObservedCanIds.SequenceEqual(new[] { latestSource }),
            "J1939 statistics mixed source addresses.");
        Check(Approximately(result.CurrentPhysical, 1240),
            "J1939 engineering value is incorrect.");
    }

    private static void SupportsSignedBigEndianSignal()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:36:00+00:00");
        var signal = Signal(0x408, false, 0, 7, 16, SignalByteOrder.BigEndian) with
        {
            Name = "Signed Motorola",
            IsSigned = true,
            Scale = 0.1,
            Offset = 1,
            Unit = "deg"
        };
        var frames = new[]
        {
            Frame(t0, 0x408, false, [0xFF, 0x9C]),
            Frame(t0.AddMilliseconds(300), 0x408, false, [0xFF, 0x9C])
        };

        var result = LivePhysicalSignalMonitor.Evaluate(
            signal,
            frames,
            t0.AddMilliseconds(350));

        Check(Approximately(result.CurrentRaw, -100),
            "Signed BigEndian raw value was decoded incorrectly.");
        Check(Approximately(result.CurrentPhysical, -9),
            "Signed BigEndian engineering value was decoded incorrectly.");
    }

    private static MachineSignal Signal(
        uint id,
        bool extended,
        int startByte,
        int startBit,
        int bitLength,
        SignalByteOrder byteOrder) => new()
    {
        Name = "test",
        CanId = id,
        IsExtended = extended,
        StartByte = startByte,
        StartBit = startBit,
        BitLength = bitLength,
        ByteOrder = byteOrder,
        Scale = 1,
        Offset = 0,
        Unit = "raw"
    };

    private static CanFrame Frame(
        DateTimeOffset timestamp,
        uint id,
        bool extended,
        byte[] data) => new()
    {
        Timestamp = timestamp,
        Channel = 0,
        Id = id,
        IsExtended = extended,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx
    };

    private static byte[] U16Le(int value) =>
        [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];

    private static bool Approximately(double? actual, double expected, double tolerance = 1e-9) =>
        actual.HasValue && Math.Abs(actual.Value - expected) <= tolerance;

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
