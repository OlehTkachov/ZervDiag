using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class IncidentProfileSignalTimelineTests
{
    public static void Run()
    {
        DecoderUnsignedAndScaled();
        DecoderCrossByteLittleEndian();
        DecoderSigned();
        DecoderSixtyFourBitBoundary();
        DecoderBigEndianByteAligned();
        DecoderBigEndianSawtoothCrossByte();
        DecoderBigEndianSignedScaled();
        DecoderBigEndianSixtyFourBitBoundary();
        BigEndianOutOfClassicalCanIsRejected();
        TimelineFiltersAndDecodes();
        TimelineBigEndianDecodes();
        TimelineDownsamplingPreservesExtremes();
    }

    private static void DecoderUnsignedAndScaled()
    {
        var signal = Signal(
            startByte: 1,
            startBit: 0,
            bitLength: 8,
            signed: false,
            scale: 0.5,
            offset: -10);

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[] { 0x00, 0x28 });

        Check(value.RawUnsigned == 40 &&
              !value.RawSigned.HasValue &&
              Math.Abs(value.EngineeringValue - 10) < 0.000001,
            "Unsigned/scaled Machine Profile decode is incorrect.");

        Check(MachineSignalDecoder.RequiredDataLength(signal) == 2 &&
              MachineSignalDecoder.CoversDataByte(signal, 1) &&
              !MachineSignalDecoder.CoversDataByte(signal, 0),
            "Machine Profile field geometry is incorrect.");
    }

    private static void DecoderCrossByteLittleEndian()
    {
        var signal = Signal(
            startByte: 0,
            startBit: 4,
            bitLength: 12);

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[] { 0xA0, 0xBC });

        Check(value.RawUnsigned == 0xBCA &&
              Math.Abs(value.EngineeringValue - 0xBCA) < 0.000001,
            "Cross-byte LittleEndian extraction is incorrect.");
    }

    private static void DecoderSigned()
    {
        var signed8 = MachineSignalDecoder.Decode(
            Signal(
                startByte: 0,
                startBit: 0,
                bitLength: 8,
                signed: true),
            new byte[] { 0xFE });

        Check(signed8.RawSigned == -2 &&
              Math.Abs(signed8.EngineeringValue - (-2)) < 0.000001,
            "Signed 8-bit extraction/sign extension is incorrect.");

        var signed12 = MachineSignalDecoder.Decode(
            Signal(
                startByte: 0,
                startBit: 0,
                bitLength: 12,
                signed: true),
            new byte[] { 0xFE, 0x0F });

        Check(signed12.RawUnsigned == 0xFFE &&
              signed12.RawSigned == -2,
            "Signed 12-bit extraction/sign extension is incorrect.");
    }

    private static void DecoderSixtyFourBitBoundary()
    {
        var signal = Signal(
            startByte: 0,
            startBit: 0,
            bitLength: 64);

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[]
            {
                0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x88
            });

        Check(value.RawUnsigned == 0x8877665544332211UL &&
              MachineSignalDecoder.RequiredDataLength(signal) == 8,
            "64-bit LittleEndian boundary decode is incorrect.");
    }

    private static void DecoderBigEndianByteAligned()
    {
        var signal8 = Signal(
            startByte: 0,
            startBit: 7,
            bitLength: 8) with
        {
            ByteOrder = SignalByteOrder.BigEndian
        };
        var value8 = MachineSignalDecoder.Decode(signal8, new byte[] { 0x12 });

        var signal16 = signal8 with { BitLength = 16 };
        var value16 = MachineSignalDecoder.Decode(
            signal16,
            new byte[] { 0x12, 0x34 });

        Check(value8.RawUnsigned == 0x12 &&
              value16.RawUnsigned == 0x1234 &&
              MachineSignalDecoder.RequiredDataLength(signal16) == 2 &&
              MachineSignalDecoder.CoversDataByte(signal16, 0) &&
              MachineSignalDecoder.CoversDataByte(signal16, 1),
            "Byte-aligned BigEndian/DBC sawtooth decode is incorrect.");
    }

    private static void DecoderBigEndianSawtoothCrossByte()
    {
        var signal = Signal(
            startByte: 1,
            startBit: 3,
            bitLength: 12) with
        {
            ByteOrder = SignalByteOrder.BigEndian
        };

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[] { 0x00, 0x0A, 0xBC });

        Check(value.RawUnsigned == 0xABC &&
              MachineSignalDecoder.RequiredDataLength(signal) == 3 &&
              !MachineSignalDecoder.CoversDataByte(signal, 0) &&
              MachineSignalDecoder.CoversDataByte(signal, 1) &&
              MachineSignalDecoder.CoversDataByte(signal, 2),
            "Non-byte-aligned BigEndian/DBC sawtooth extraction is incorrect.");
    }

    private static void DecoderBigEndianSignedScaled()
    {
        var signal = Signal(
            startByte: 0,
            startBit: 7,
            bitLength: 16,
            signed: true,
            scale: 0.5,
            offset: 10) with
        {
            ByteOrder = SignalByteOrder.BigEndian
        };

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[] { 0xFF, 0x80 });

        Check(value.RawUnsigned == 0xFF80 &&
              value.RawSigned == -128 &&
              Math.Abs(value.EngineeringValue - (-54)) < 0.000001,
            "Signed/scaled BigEndian decode is incorrect.");
    }

    private static void DecoderBigEndianSixtyFourBitBoundary()
    {
        var signal = Signal(
            startByte: 0,
            startBit: 7,
            bitLength: 64) with
        {
            ByteOrder = SignalByteOrder.BigEndian
        };

        var value = MachineSignalDecoder.Decode(
            signal,
            new byte[]
            {
                0x01, 0x23, 0x45, 0x67,
                0x89, 0xAB, 0xCD, 0xEF
            });

        Check(value.RawUnsigned == 0x0123456789ABCDEFUL &&
              MachineSignalDecoder.RequiredDataLength(signal) == 8,
            "64-bit BigEndian boundary decode is incorrect.");
    }

    private static void BigEndianOutOfClassicalCanIsRejected()
    {
        var rejected = false;
        try
        {
            MachineSignalDecoder.ValidateDefinition(
                Signal(
                    startByte: 0,
                    startBit: 0,
                    bitLength: 64) with
                {
                    ByteOrder = SignalByteOrder.BigEndian
                });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Check(rejected,
            "BigEndian field extending beyond Classical CAN DATA[0..7] was accepted.");
    }

    private static void TimelineFiltersAndDecodes()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var signal = Signal(
            startByte: 1,
            startBit: 0,
            bitLength: 8,
            scale: 0.5,
            offset: -10);

        var frames = new[]
        {
            Frame(marker.AddMilliseconds(-1000), 0x123, false, CanDirection.Rx, [0x00, 0x14]),
            Frame(marker.AddMilliseconds(-100), 0x123, false, CanDirection.Rx, [0x00, 0x28]),
            Frame(marker.AddMilliseconds(100), 0x123, false, CanDirection.Rx, [0x00, 0x3C]),
            Frame(marker.AddMilliseconds(1000), 0x123, false, CanDirection.Rx, [0x00, 0x50]),

            // Exact ID but too-short DLC: counted and skipped.
            Frame(marker.AddMilliseconds(150), 0x123, false, CanDirection.Rx, [0x99]),

            // Must not enter the matching source set.
            Frame(marker.AddMilliseconds(200), 0x123, true, CanDirection.Rx, [0x00, 0xFF]),
            Frame(marker.AddMilliseconds(250), 0x123, false, CanDirection.Tx, [0x00, 0xFF]),
            Frame(marker.AddMilliseconds(300), 0x124, false, CanDirection.Rx, [0x00, 0xFF])
        };

        var incident = Incident(marker, frames);
        var step = Step(
            reactionMilliseconds: 100,
            id: 0x123,
            extended: false,
            dataIndex: 1);

        var result =
            IncidentProfileSignalTimelineAnalyzer.Analyze(
                incident,
                step,
                signal);

        Check(result.MatchingFrameCount == 5 &&
              result.ShortFrameCount == 1 &&
              result.SourceFrameCount == 4 &&
              result.RenderedPoints.Count == 4,
            "Profile timeline frame filtering/counting is incorrect.");

        var engineering = result.RenderedPoints
            .Select(point => point.EngineeringValue)
            .ToArray();

        Check(engineering.SequenceEqual(
                  new double[] { 0, 10, 20, 30 }) &&
              Math.Abs(result.MinimumEngineeringValue - 0) < 0.000001 &&
              Math.Abs(result.MaximumEngineeringValue - 30) < 0.000001 &&
              Math.Abs(result.StartMilliseconds - (-1000)) < 0.001 &&
              Math.Abs(result.EndMilliseconds - 1000) < 0.001 &&
              result.Warnings.Any(warning =>
                  warning.Contains("короткого DLC", StringComparison.OrdinalIgnoreCase)),
            "Profile timeline engineering values/range/warnings are incorrect.");
    }

    private static void TimelineBigEndianDecodes()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(15);
        var signal = Signal(
            startByte: 0,
            startBit: 7,
            bitLength: 16,
            scale: 0.1) with
        {
            ByteOrder = SignalByteOrder.BigEndian
        };
        var frames = new[]
        {
            Frame(marker.AddMilliseconds(-10), 0x123, false, CanDirection.Rx, [0x01, 0x00]),
            Frame(marker.AddMilliseconds(10), 0x123, false, CanDirection.Rx, [0x02, 0x00]),
            Frame(marker.AddMilliseconds(20), 0x123, false, CanDirection.Rx, [0xFF])
        };

        var result = IncidentProfileSignalTimelineAnalyzer.Analyze(
            Incident(marker, frames),
            Step(10, 0x123, false, 1),
            signal);

        Check(result.MatchingFrameCount == 3 &&
              result.ShortFrameCount == 1 &&
              result.SourceFrameCount == 2 &&
              Math.Abs(result.RenderedPoints[0].EngineeringValue - 25.6) < 0.000001 &&
              Math.Abs(result.RenderedPoints[1].EngineeringValue - 51.2) < 0.000001,
            "BigEndian Profile timeline decode/DLC handling is incorrect.");
    }

    private static void TimelineDownsamplingPreservesExtremes()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var signal = Signal(
            startByte: 0,
            startBit: 0,
            bitLength: 8);

        var frames = Enumerable.Range(0, 120)
            .Select(index =>
            {
                var raw = index == 50
                    ? (byte)250
                    : index == 75
                        ? (byte)1
                        : (byte)10;

                return Frame(
                    marker.AddMilliseconds(index - 60),
                    0x321,
                    false,
                    CanDirection.Rx,
                    [raw]);
            })
            .ToArray();

        var result =
            IncidentProfileSignalTimelineAnalyzer.Analyze(
                Incident(marker, frames),
                Step(0, 0x321, false, 0),
                signal with { CanId = 0x321 },
                maximumRenderedPoints: 20);

        Check(result.RenderedPoints.Count <= 20 &&
              Math.Abs(result.RenderedPoints[0].RelativeMilliseconds - (-60)) < 0.001 &&
              Math.Abs(result.RenderedPoints[^1].RelativeMilliseconds - 59) < 0.001 &&
              result.RenderedPoints.Any(point =>
                  Math.Abs(point.EngineeringValue - 250) < 0.001) &&
              result.RenderedPoints.Any(point =>
                  Math.Abs(point.EngineeringValue - 1) < 0.001) &&
              result.Warnings.Any(warning =>
                  warning.Contains("сокращены", StringComparison.OrdinalIgnoreCase)),
            "Profile timeline downsampling did not preserve endpoints/extremes.");
    }

    private static MachineSignal Signal(
        int startByte,
        int startBit,
        int bitLength,
        bool signed = false,
        double scale = 1,
        double offset = 0) =>
        new()
        {
            Name = "test",
            CanId = 0x123,
            IsExtended = false,
            StartByte = startByte,
            StartBit = startBit,
            BitLength = bitLength,
            ByteOrder = SignalByteOrder.LittleEndian,
            IsSigned = signed,
            Scale = scale,
            Offset = offset
        };

    private static IncidentEventChainStep Step(
        double reactionMilliseconds,
        uint id,
        bool extended,
        int dataIndex) =>
        new(
            1,
            reactionMilliseconds,
            null,
            id,
            extended,
            dataIndex,
            IncidentTransitionKind.ByteChanged,
            IncidentTransitionPriority.High,
            "0",
            "1",
            [],
            null,
            false,
            string.Empty,
            "test");

    private static PreFaultIncident Incident(
        DateTimeOffset marker,
        IReadOnlyList<CanFrame> frames) =>
        new(
            Guid.NewGuid(),
            marker.AddSeconds(-2),
            marker.AddSeconds(2),
            marker.AddSeconds(2),
            [new IncidentMarker(marker, "test")],
            frames,
            []);

    private static CanFrame Frame(
        DateTimeOffset timestamp,
        uint id,
        bool extended,
        CanDirection direction,
        byte[] data) =>
        new()
        {
            Timestamp = timestamp,
            Channel = 0,
            Id = id,
            IsExtended = extended,
            Data = data,
            Protocol = BusProtocol.ClassicalCan,
            Direction = direction
        };

    private static void Check(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
