using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public sealed record GenericCanConsensus(
    uint Id,
    bool IsExtended,
    int SampleCount,
    int ModalDlc,
    byte[] Bytes,
    double[] ByteAgreementPercent);

public sealed record GenericCanComparison(
    uint Id,
    bool IsExtended,
    int DataIndex,
    byte? ReferenceValue,
    byte? ActionValue,
    byte? XorMask,
    string ChangedBits,
    bool IsChanged,
    double ReferenceAgreementPercent,
    double ActionAgreementPercent,
    int ReferenceSampleCount,
    int ActionSampleCount);

public static class GenericExperimentComparator
{
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
            var maximumLength = Math.Max(referenceConsensus?.Bytes.Length ?? 0, actionConsensus?.Bytes.Length ?? 0);

            for (var dataIndex = 0; dataIndex < maximumLength; dataIndex++)
            {
                var referenceValue = GetByte(referenceConsensus, dataIndex);
                var actionValue = GetByte(actionConsensus, dataIndex);
                var xorMask = referenceValue.HasValue && actionValue.HasValue
                    ? (byte?)(referenceValue.Value ^ actionValue.Value)
                    : null;

                result.Add(new GenericCanComparison(
                    key.Id,
                    key.IsExtended,
                    dataIndex,
                    referenceValue,
                    actionValue,
                    xorMask,
                    DescribeChangedBits(referenceValue, actionValue),
                    referenceValue != actionValue,
                    GetAgreement(referenceConsensus, dataIndex),
                    GetAgreement(actionConsensus, dataIndex),
                    referenceConsensus?.SampleCount ?? 0,
                    actionConsensus?.SampleCount ?? 0));
            }
        }

        return result;
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
                throw new ArgumentException("Generic CAN comparison accepts Classical CAN frames only.", nameof(frames));
            }

            return frame;
        }).ToArray();

        return validated
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => BuildConsensus(group.Key, group.ToArray()));
    }

    private static GenericCanConsensus BuildConsensus(
        (uint Id, bool IsExtended) key,
        IReadOnlyCollection<CanFrame> frames)
    {
        var modalDlc = frames
            .GroupBy(frame => frame.Data.Length)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First()
            .Key;
        var equalLengthFrames = frames.Where(frame => frame.Data.Length == modalDlc).ToArray();
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
            bytes,
            agreement);
    }

    private static byte? GetByte(GenericCanConsensus? consensus, int index) =>
        consensus is not null && index < consensus.Bytes.Length ? consensus.Bytes[index] : null;

    private static double GetAgreement(GenericCanConsensus? consensus, int index) =>
        consensus is not null && index < consensus.ByteAgreementPercent.Length
            ? consensus.ByteAgreementPercent[index]
            : 0;

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
            transitions.Add($"{bit}: {from}→{to}");
        }

        return string.Join("; ", transitions);
    }
}
