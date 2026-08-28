using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public enum Onk160BenchCaptureKind
{
    Normal,
    Changed
}

public sealed record Onk160BenchPacketConsensus(
    byte Header,
    byte[] Bytes,
    int PresentCycles,
    int TotalCycles,
    double[] ByteAgreementPercent)
{
    public double PresencePercent => TotalCycles == 0 ? 0 : PresentCycles * 100.0 / TotalCycles;
}

public sealed record Onk160BenchSnapshot(
    int CycleCount,
    IReadOnlyDictionary<byte, Onk160BenchPacketConsensus> Packets);

public sealed record Onk160BenchComparison(
    byte Header,
    int? DataIndex,
    string Field,
    byte? NormalValue,
    byte? ChangedValue,
    byte? XorMask,
    string ChangedBits,
    bool IsChanged,
    bool IsChecksum,
    double NormalAgreementPercent,
    double ChangedAgreementPercent,
    double NormalPresencePercent,
    double ChangedPresencePercent);

/// <summary>
/// Collects complete ONK-160 polling cycles. A cycle starts with CA and is
/// committed when the next CA arrives. Invalid-checksum packets are excluded
/// from the consensus but the surrounding cycle remains available for
/// detecting missing packets.
/// </summary>
public sealed class Onk160BenchCapture
{
    private readonly List<IReadOnlyDictionary<byte, byte[]>> _cycles = [];
    private readonly Dictionary<byte, byte[]> _currentCycle = [];
    private bool _cycleStarted;

    public Onk160BenchCapture(int targetCycles)
    {
        if (targetCycles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCycles), "Cycle count must be between 1 and 100.");
        }

        TargetCycles = targetCycles;
    }

    public int TargetCycles { get; }
    public int CompletedCycles => _cycles.Count;
    public bool IsComplete => CompletedCycles >= TargetCycles;

    public bool Append(CanFrame frame)
    {
        if (IsComplete || frame.Protocol != BusProtocol.Onk160Serial || frame.Direction != CanDirection.Rx)
        {
            return IsComplete;
        }

        var header = checked((byte)frame.Id);
        if (header == 0xCA)
        {
            CommitCurrentCycle();
            if (IsComplete)
            {
                return true;
            }

            _currentCycle.Clear();
            _cycleStarted = true;
        }

        if (!_cycleStarted || frame.IsChecksumValid == false)
        {
            return IsComplete;
        }

        var bytes = new byte[frame.Data.Length + 1];
        bytes[0] = header;
        frame.Data.CopyTo(bytes, 1);
        _currentCycle[header] = bytes;
        return IsComplete;
    }

    public Onk160BenchSnapshot CreateSnapshot()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException("The requested number of ONK-160 cycles has not been captured yet.");
        }

        var packets = _cycles
            .SelectMany(cycle => cycle.Keys)
            .Distinct()
            .OrderBy(header => header)
            .ToDictionary(header => header, BuildConsensus);

        return new Onk160BenchSnapshot(TargetCycles, packets);
    }

    private void CommitCurrentCycle()
    {
        if (!_cycleStarted || _currentCycle.Count < 2 || IsComplete)
        {
            return;
        }

        _cycles.Add(_currentCycle.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray()));
    }

    private Onk160BenchPacketConsensus BuildConsensus(byte header)
    {
        var samples = _cycles
            .Where(cycle => cycle.ContainsKey(header))
            .Select(cycle => cycle[header])
            .ToArray();
        var modalLength = samples
            .GroupBy(bytes => bytes.Length)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First()
            .Key;
        var equalLengthSamples = samples.Where(bytes => bytes.Length == modalLength).ToArray();
        var consensus = new byte[modalLength];
        var agreement = new double[modalLength];

        for (var index = 0; index < modalLength; index++)
        {
            var mode = equalLengthSamples
                .GroupBy(bytes => bytes[index])
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .First();
            consensus[index] = mode.Key;
            agreement[index] = mode.Count() * 100.0 / equalLengthSamples.Length;
        }

        return new Onk160BenchPacketConsensus(
            header,
            consensus,
            samples.Length,
            TargetCycles,
            agreement);
    }
}

public static class Onk160BenchStateAnalyzer
{
    public static IReadOnlyList<Onk160BenchComparison> Compare(
        Onk160BenchSnapshot normal,
        Onk160BenchSnapshot changed)
    {
        var headers = normal.Packets.Keys
            .Union(changed.Packets.Keys)
            .OrderBy(header => header);
        var result = new List<Onk160BenchComparison>();

        foreach (var header in headers)
        {
            normal.Packets.TryGetValue(header, out var normalPacket);
            changed.Packets.TryGetValue(header, out var changedPacket);
            if (normalPacket is null || changedPacket is null)
            {
                result.Add(new Onk160BenchComparison(
                    header,
                    null,
                    "Наличие пакета",
                    null,
                    null,
                    null,
                    normalPacket is null ? "пакет появился" : "пакет пропал",
                    true,
                    false,
                    0,
                    0,
                    normalPacket?.PresencePercent ?? 0,
                    changedPacket?.PresencePercent ?? 0));
                continue;
            }

            var maximumLength = Math.Max(normalPacket.Bytes.Length, changedPacket.Bytes.Length);
            for (var byteIndex = 1; byteIndex < maximumLength; byteIndex++)
            {
                var normalValue = GetByte(normalPacket.Bytes, byteIndex);
                var changedValue = GetByte(changedPacket.Bytes, byteIndex);
                var xorMask = normalValue.HasValue && changedValue.HasValue
                    ? (byte?)(normalValue.Value ^ changedValue.Value)
                    : null;
                var isChecksum = byteIndex == normalPacket.Bytes.Length - 1 &&
                                 byteIndex == changedPacket.Bytes.Length - 1;
                result.Add(new Onk160BenchComparison(
                    header,
                    byteIndex - 1,
                    isChecksum ? "Контрольная сумма" : $"DATA[{byteIndex - 1}]",
                    normalValue,
                    changedValue,
                    xorMask,
                    DescribeChangedBits(normalValue, changedValue),
                    normalValue != changedValue,
                    isChecksum,
                    GetAgreement(normalPacket, byteIndex),
                    GetAgreement(changedPacket, byteIndex),
                    normalPacket.PresencePercent,
                    changedPacket.PresencePercent));
            }
        }

        return result;
    }

    private static byte? GetByte(IReadOnlyList<byte> bytes, int index) =>
        index < bytes.Count ? bytes[index] : null;

    private static double GetAgreement(Onk160BenchPacketConsensus packet, int index) =>
        index < packet.ByteAgreementPercent.Length ? packet.ByteAgreementPercent[index] : 0;

    private static string DescribeChangedBits(byte? normal, byte? changed)
    {
        if (!normal.HasValue || !changed.HasValue)
        {
            return normal.HasValue ? "байт пропал" : "байт появился";
        }

        var mask = normal.Value ^ changed.Value;
        if (mask == 0)
        {
            return "—";
        }

        var transitions = new List<string>();
        for (var bit = 7; bit >= 0; bit--)
        {
            var bitMask = 1 << bit;
            if ((mask & bitMask) == 0)
            {
                continue;
            }

            var from = (normal.Value & bitMask) == 0 ? 0 : 1;
            var to = (changed.Value & bitMask) == 0 ? 0 : 1;
            transitions.Add($"{bit}: {from}→{to}");
        }

        return string.Join("; ", transitions);
    }
}
