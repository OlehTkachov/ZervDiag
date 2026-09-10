using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class AnalogPhysicalMarkerCaptureTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        CapturesStableLittleEndianRawWithoutScale();
        DecodesSignedAndBigEndianRaw();
        IgnoresNonReceiveAndWrongFormatFrames();
        RejectsMovingSignalAsUnstable();
        EnforcesLiveFreshnessButAllowsReplayHistory();
        RejectsInsufficientFreshSamples();
        KeepsJ1939SourceAddressesSeparate();
    }

    private static void CapturesStableLittleEndianRawWithoutScale()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:00:00+00:00");
        var signal = Signal(0x321, false, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "Boom length raw",
            Scale = 0.01,
            Offset = 5.0,
            Unit = "m"
        };
        var frames = Enumerable.Range(0, 8)
            .Select(index => Frame(t0.AddMilliseconds(index * 80), 0x321, false, U16Le(1234)))
            .ToArray();

        var result = AnalogPhysicalMarkerCapture.Capture(signal, frames);
        Check(result.IsStable, "Stable raw field was classified unstable.");
        Check(result.RawValue == 1234 && result.LatestRawValue == 1234,
            "Marker capture used engineering Scale/Offset instead of raw value.");
        Check(result.SampleCount == 8 && result.LatestObservedCanId == 0x321,
            "Stable marker sample metadata is incorrect.");
        Check(result.RobustSpanRaw == 0 && result.DriftRaw == 0,
            "Constant marker should have zero robust span and drift.");
    }

    private static void DecodesSignedAndBigEndianRaw()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:01:00+00:00");
        var signed = Signal(0x322, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Signed raw",
            IsSigned = true
        };
        var signedFrames = Enumerable.Range(0, 6)
            .Select(index => Frame(t0.AddMilliseconds(index * 100), 0x322, false, [0xF6]))
            .ToArray();
        var signedResult = AnalogPhysicalMarkerCapture.Capture(signed, signedFrames);
        Check(signedResult.RawValue == -10,
            "Signed marker raw value was not sign-extended correctly.");

        var bigEndian = Signal(0x323, false, 0, 7, 16, SignalByteOrder.BigEndian) with
        {
            Name = "Motorola raw"
        };
        var beFrames = Enumerable.Range(0, 6)
            .Select(index => Frame(t0.AddSeconds(1).AddMilliseconds(index * 100),
                0x323, false, [0x12, 0x34]))
            .ToArray();
        var beResult = AnalogPhysicalMarkerCapture.Capture(bigEndian, beFrames);
        Check(beResult.RawValue == 0x1234,
            "BigEndian/Motorola marker raw value is incorrect.");
    }

    private static void IgnoresNonReceiveAndWrongFormatFrames()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:02:00+00:00");
        var signal = Signal(0x324, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Filtered raw"
        };
        var frames = Enumerable.Range(0, 6)
            .Select(index => Frame(t0.AddMilliseconds(index * 100), 0x324, false, [42]))
            .ToList();
        frames.Add(Frame(t0.AddMilliseconds(610), 0x324, false, [250]) with { Direction = CanDirection.Tx });
        frames.Add(Frame(t0.AddMilliseconds(620), 0x324, false, [251]) with { IsRemote = true });
        frames.Add(Frame(t0.AddMilliseconds(630), 0x324, false, [252]) with { IsError = true });
        frames.Add(Frame(t0.AddMilliseconds(640), 0x324, true, [253]));
        frames.Add(Frame(t0.AddMilliseconds(650), 0x325, false, [254]));

        var result = AnalogPhysicalMarkerCapture.Capture(signal, frames);
        Check(result.RawValue == 42 && result.SampleCount == 6,
            "Tx/remote/error/wrong-format frames contaminated passive marker capture.");
        Check(result.ObservedCanIds.SequenceEqual(new[] { 0x324u }),
            "Marker capture reported IDs that do not belong to the selected signal.");
    }

    private static void RejectsMovingSignalAsUnstable()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:03:00+00:00");
        var signal = Signal(0x325, false, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "Moving raw"
        };
        var frames = Enumerable.Range(0, 10)
            .Select(index => Frame(t0.AddMilliseconds(index * 70), 0x325, false, U16Le(1000 + index * 100)))
            .ToArray();

        var result = AnalogPhysicalMarkerCapture.Capture(signal, frames);
        Check(!result.IsStable,
            "Clearly moving raw ramp was accepted as a stable physical marker.");
        Check(result.RobustSpanRaw > result.AllowedStabilityRaw &&
              result.DriftRaw > result.AllowedStabilityRaw,
            "Unstable marker metrics do not show the expected movement.");
    }

    private static void EnforcesLiveFreshnessButAllowsReplayHistory()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:04:00+00:00");
        var signal = Signal(0x326, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Freshness raw"
        };
        var frames = Enumerable.Range(0, 6)
            .Select(index => Frame(t0.AddMilliseconds(index * 100), 0x326, false, [77]))
            .ToArray();

        ExpectThrows<InvalidOperationException>(() => AnalogPhysicalMarkerCapture.Capture(
            signal,
            frames,
            new AnalogMarkerCaptureOptions
            {
                RequireRecentFrame = true,
                ReferenceTime = t0.AddSeconds(10),
                MaximumFrameAge = TimeSpan.FromSeconds(2)
            }), "Stale PCAN-style marker was not rejected.");

        var replay = AnalogPhysicalMarkerCapture.Capture(
            signal,
            frames,
            new AnalogMarkerCaptureOptions { RequireRecentFrame = false });
        Check(replay.IsStable && replay.RawValue == 77,
            "Replay history should be usable without wall-clock freshness enforcement.");
    }

    private static void RejectsInsufficientFreshSamples()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:05:00+00:00");
        var signal = Signal(0x327, false, 0, 0, 8, SignalByteOrder.LittleEndian) with
        {
            Name = "Sparse raw"
        };
        var frames = Enumerable.Range(0, 4)
            .Select(index => Frame(t0.AddMilliseconds(index * 100), 0x327, false, [11]))
            .ToArray();

        ExpectThrows<InvalidOperationException>(() => AnalogPhysicalMarkerCapture.Capture(
            signal,
            frames,
            new AnalogMarkerCaptureOptions { MinimumSamples = 5 }),
            "Marker capture accepted fewer samples than configured.");
    }

    private static void KeepsJ1939SourceAddressesSeparate()
    {
        var t0 = DateTimeOffset.Parse("2026-09-10T12:06:00+00:00");
        const uint firstSourceId = 0x18F004A1;
        const uint latestSourceId = 0x18F004B2;
        var signal = Signal(firstSourceId, true, 0, 0, 16, SignalByteOrder.LittleEndian) with
        {
            Name = "J1939 analog",
            Protocol = "J1939",
            J1939Pgn = 0x0F004,
            J1939Spn = 190
        };
        var frames = new List<CanFrame>();
        for (var index = 0; index < 6; index++)
        {
            frames.Add(Frame(t0.AddMilliseconds(index * 90), firstSourceId, true, U16Le(1000)));
            frames.Add(Frame(t0.AddMilliseconds(20 + index * 90), latestSourceId, true, U16Le(2000)));
        }

        var result = AnalogPhysicalMarkerCapture.Capture(signal, frames);
        Check(result.IsStable && result.RawValue == 2000,
            "J1939 marker mixed raw values from different source addresses.");
        Check(result.J1939Pgn == 0x0F004 && result.J1939SourceAddress == 0xB2,
            "J1939 marker PGN/source metadata is incorrect.");
        Check(result.ObservedCanIds.SequenceEqual(new[] { latestSourceId }),
            "J1939 marker sampling window contains more than the current source address.");
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
        Offset = 0
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

    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
