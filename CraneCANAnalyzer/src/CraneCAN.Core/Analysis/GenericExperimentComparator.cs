using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public enum GenericCanChangePriority
{
    None,
    Low,
    Medium,
    High,
    VeryHigh
}

public enum GenericCanChangeKind
{
    Unchanged,
    MessageAppeared,
    MessageDisappeared,
    UnstableOrAnalogNoise,
    StableByteChange,
    StableSingleBitChange,
    MultipleStableBytes
}

public sealed record GenericCanConsensus(
    uint Id,
    bool IsExtended,
    int SampleCount,
    int ModalDlc,
    double ModalDlcAgreementPercent,
    byte[] Bytes,
    double[] ByteAgreementPercent);

public sealed record GenericCanComparison(
    uint Id,
    bool IsExtended,
    int? DataIndex,
    byte? ReferenceValue,
    byte? ActionValue,
    byte? XorMask,
    string ChangedBits,
    bool IsChanged,
    double ReferenceAgreementPercent,
    double ActionAgreementPercent,
    int ReferenceSampleCount,
    int ActionSampleCount,
    GenericCanChangePriority Priority,
    GenericCanChangeKind Kind,
    string Classification,
    bool IsSignificant)
{
    public string Field => DataIndex.HasValue ? $"DATA[{DataIndex.Value}]" : "Сообщение";
    public bool MessageAppeared => Kind == GenericCanChangeKind.MessageAppeared;
    public bool MessageDisappeared => Kind == GenericCanChangeKind.MessageDisappeared;
}

public static class GenericExperimentComparator
{
    public const double StableAgreementThresholdPercent = 90.0;

    public static IReadOnlyList<GenericCanComparison> Compare(
        IEnumerable<CanFrame> referenceFrames,
        IEnumerable<CanFrame> actionFrames)
    {
        var reference = BuildConsensus(referenceFrames);
        var action = BuildConsensus(actionFrames);
        var keys = reference.Keys
            .Union(action.Keys)
            .OrderBy(key => key.Id)
            .ThenBy(key => key.IsExtended)
            .ToArray();
        var result = new List<GenericCanComparison>();

        foreach (var key in keys)
        {
            reference.TryGetValue(key, out var referenceConsensus);
            action.TryGetValue(key, out var actionConsensus);
            if (referenceConsensus is null || actionConsensus is null)
            {
                result.Add(CreatePresenceChange(key, referenceConsensus, actionConsensus));
                continue;
            }

            var maximumLength = Math.Max(referenceConsensus.Bytes.Length, actionConsensus.Bytes.Length);
            var rows = new List<GenericCanComparison>(maximumLength);
            for (var dataIndex = 0; dataIndex < maximumLength; dataIndex++)
            {
                rows.Add(CreateByteComparison(referenceConsensus, actionConsensus, dataIndex));
            }

            var stableChangedByteCount = rows.Count(row =>
                row.IsChanged &&
                row.ReferenceValue.HasValue &&
                row.ActionValue.HasValue &&
                row.ReferenceAgreementPercent >= StableAgreementThresholdPercent &&
                row.ActionAgreementPercent >= StableAgreementThresholdPercent);

            if (stableChangedByteCount > 1)
            {
                rows = rows.Select(row => row.IsChanged && row.Kind is
                        GenericCanChangeKind.StableByteChange or GenericCanChangeKind.StableSingleBitChange
                    ? row with
                    {
                        Priority = GenericCanChangePriority.High,
                        Kind = GenericCanChangeKind.MultipleStableBytes,
                        Classification = $"устойчиво изменились {stableChangedByteCount} байта этого ID",
                        IsSignificant = true
                    }
                    : row).ToList();
            }

            result.AddRange(rows);
        }

        return result
            .OrderByDescending(row => row.Priority)
            .ThenBy(row => row.Id)
            .ThenBy(row => row.IsExtended)
            .ThenBy(row => row.DataIndex ?? -1)
            .ToArray();
    }

    public static IReadOnlyDictionary<(uint Id, bool IsExtended), GenericCanConsensus> BuildConsensus(
        IEnumerable<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var validated = frames.Select(frame =>
        {
            frame.Validate();
            if (frame.Protocol != BusProtocol.ClassicalCan)
            {
                throw new ArgumentException(
                    "Generic CAN comparison accepts Classical CAN frames only.", nameof(frames));
            }

            return frame;
        }).ToArray();

        return validated
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => BuildConsensus(group.Key, group.ToArray()));
    }

    private static GenericCanComparison CreatePresenceChange(
        (uint Id, bool IsExtended) key,
        GenericCanConsensus? reference,
        GenericCanConsensus? action)
    {
        var appeared = reference is null;
        return new GenericCanComparison(
            key.Id,
            key.IsExtended,
            null,
            null,
            null,
            null,
            appeared ? "сообщение появилось" : "сообщение исчезло",
            true,
            0,
            0,
            reference?.SampleCount ?? 0,
            action?.SampleCount ?? 0,
            GenericCanChangePriority.VeryHigh,
            appeared ? GenericCanChangeKind.MessageAppeared : GenericCanChangeKind.MessageDisappeared,
            appeared ? "ID появился в ACTION" : "ID исчез в ACTION",
            true);
    }

    private static GenericCanComparison CreateByteComparison(
        GenericCanConsensus reference,
        GenericCanConsensus action,
        int dataIndex)
    {
        var referenceValue = GetByte(reference, dataIndex);
        var actionValue = GetByte(action, dataIndex);
        var xorMask = referenceValue.HasValue && actionValue.HasValue
            ? (byte?)(referenceValue.Value ^ actionValue.Value)
            : null;
        var isChanged = referenceValue != actionValue;
        var referenceAgreement = GetAgreement(reference, dataIndex);
        var actionAgreement = GetAgreement(action, dataIndex);
        var changedBits = DescribeChangedBits(referenceValue, actionValue);

        var priority = GenericCanChangePriority.None;
        var kind = GenericCanChangeKind.Unchanged;
        var classification = "без устойчивого изменения";
        var significant = false;
        if (isChanged && (!referenceValue.HasValue || !actionValue.HasValue))
        {
            priority = GenericCanChangePriority.High;
            kind = GenericCanChangeKind.StableByteChange;
            classification = referenceValue.HasValue
                ? "байт исчез из-за изменения DLC"
                : "байт появился из-за изменения DLC";
            significant = true;
        }
        else if (isChanged &&
                 (referenceAgreement < StableAgreementThresholdPercent ||
                  actionAgreement < StableAgreementThresholdPercent))
        {
            priority = GenericCanChangePriority.Low;
            kind = GenericCanChangeKind.UnstableOrAnalogNoise;
            classification = "нестабильно / вероятен аналоговый шум";
        }
        else if (isChanged && xorMask.HasValue && IsSingleBit(xorMask.Value))
        {
            priority = GenericCanChangePriority.VeryHigh;
            kind = GenericCanChangeKind.StableSingleBitChange;
            classification = "одиночное устойчивое изменение бита";
            significant = true;
        }
        else if (isChanged)
        {
            priority = GenericCanChangePriority.High;
            kind = GenericCanChangeKind.StableByteChange;
            classification = "устойчивое цифровое изменение байта";
            significant = true;
        }

        return new GenericCanComparison(
            reference.Id,
            reference.IsExtended,
            dataIndex,
            referenceValue,
            actionValue,
            xorMask,
            changedBits,
            isChanged,
            referenceAgreement,
            actionAgreement,
            reference.SampleCount,
            action.SampleCount,
            priority,
            kind,
            classification,
            significant);
    }

    private static GenericCanConsensus BuildConsensus(
        (uint Id, bool IsExtended) key,
        IReadOnlyCollection<CanFrame> frames)
    {
        var modalDlcGroup = frames
            .GroupBy(frame => frame.Data.Length)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First();
        var modalDlc = modalDlcGroup.Key;
        var equalLengthFrames = modalDlcGroup.ToArray();
        var bytes = new byte[modalDlc];
        var agreement = new double[modalDlc];

        for (var index = 0; index < modalDlc; index++)
        {
            var mode = equalLengthFrames
                .GroupBy(frame => frame.Data[index])
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .First();
            bytes[index] = mode.Key;
            agreement[index] = mode.Count() * 100.0 / equalLengthFrames.Length;
        }

        return new GenericCanConsensus(
            key.Id,
            key.IsExtended,
            frames.Count,
            modalDlc,
            modalDlcGroup.Count() * 100.0 / frames.Count,
            bytes,
            agreement);
    }

    private static byte? GetByte(GenericCanConsensus consensus, int index) =>
        index < consensus.Bytes.Length ? consensus.Bytes[index] : null;

    private static double GetAgreement(GenericCanConsensus consensus, int index) =>
        index < consensus.ByteAgreementPercent.Length
            ? consensus.ByteAgreementPercent[index]
            : 0;

    private static bool IsSingleBit(byte value) => value != 0 && (value & (value - 1)) == 0;

    private static string DescribeChangedBits(byte? reference, byte? action)
    {
        if (!reference.HasValue || !action.HasValue)
        {
            return reference.HasValue ? "байт пропал" : "байт появился";
        }

        var mask = reference.Value ^ action.Value;
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

            var from = (reference.Value & bitMask) == 0 ? 0 : 1;
            var to = (action.Value & bitMask) == 0 ? 0 : 1;
            transitions.Add($"bit {bit}: {from}→{to}");
        }

        return string.Join("; ", transitions);
    }
}
