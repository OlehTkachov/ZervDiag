using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Analysis;

public enum FirstDivergenceKind
{
    PayloadChanged,
    DlcChanged,
    IdOnlyInGood,
    IdOnlyInFault
}

public sealed record FirstDivergenceCandidate(
    uint Id,
    bool IsExtended,
    FirstDivergenceKind Kind,
    double? GoodOffsetMilliseconds,
    double? FaultOffsetMilliseconds,
    byte[]? GoodData,
    byte[]? FaultData,
    double? MatchDeltaMilliseconds,
    string Description)
{
    public double EvidenceOffsetMilliseconds
    {
        get
        {
            if (GoodOffsetMilliseconds.HasValue && FaultOffsetMilliseconds.HasValue)
                return Math.Max(GoodOffsetMilliseconds.Value, FaultOffsetMilliseconds.Value);
            return GoodOffsetMilliseconds ?? FaultOffsetMilliseconds ?? double.PositiveInfinity;
        }
    }
}

public sealed record FirstDivergenceResult(
    int GoodFrameCount,
    int FaultFrameCount,
    int MatchedFramePairs,
    int UnmatchedGoodFrames,
    int UnmatchedFaultFrames,
    TimeSpan MatchTolerance,
    IReadOnlyList<FirstDivergenceCandidate> Candidates)
{
    public FirstDivergenceCandidate? Earliest => Candidates.FirstOrDefault();
}

/// <summary>
/// Conservative GOOD/FAULT timeline comparison for passive Classical CAN captures.
/// Absolute timestamps are ignored: both traces are aligned to their own first Rx frame.
/// Frames of the same ID are paired only inside a bounded relative-time tolerance.
/// Unmatched individual frames are reported as quality information, not automatically
/// declared a fault. A divergence candidate is emitted for a paired DATA/DLC mismatch
/// or when an entire CAN ID exists in only one trace.
/// </summary>
public static class FirstDivergenceAnalyzer
{
    public static readonly TimeSpan DefaultMatchTolerance = TimeSpan.FromMilliseconds(50);

    public static async Task<FirstDivergenceResult> AnalyzeFilesAsync(
        string goodTrcPath,
        string faultTrcPath,
        TimeSpan? matchTolerance = null,
        int channel = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goodTrcPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(faultTrcPath);
        var good = await PcanTrcCodec.LoadAsync(goodTrcPath, channel, cancellationToken).ConfigureAwait(false);
        var fault = await PcanTrcCodec.LoadAsync(faultTrcPath, channel, cancellationToken).ConfigureAwait(false);
        return Analyze(good, fault, matchTolerance);
    }

    public static FirstDivergenceResult Analyze(
        IEnumerable<CanFrame> goodFrames,
        IEnumerable<CanFrame> faultFrames,
        TimeSpan? matchTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(goodFrames);
        ArgumentNullException.ThrowIfNull(faultFrames);
        var tolerance = matchTolerance ?? DefaultMatchTolerance;
        if (tolerance <= TimeSpan.Zero || tolerance > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(matchTolerance),
                "Допуск сопоставления должен быть больше нуля и не более 5 секунд.");

        var good = Prepare(goodFrames);
        var fault = Prepare(faultFrames);
        if (good.Length == 0)
            throw new InvalidOperationException("GOOD-трасса не содержит обычных Rx-кадров Classical CAN.");
        if (fault.Length == 0)
            throw new InvalidOperationException("FAULT-трасса не содержит обычных Rx-кадров Classical CAN.");

        var goodOrigin = good[0].Timestamp;
        var faultOrigin = fault[0].Timestamp;
        var goodByKey = good
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => group.OrderBy(frame => frame.Timestamp).ToArray());
        var faultByKey = fault
            .GroupBy(frame => (frame.Id, frame.IsExtended))
            .ToDictionary(group => group.Key, group => group.OrderBy(frame => frame.Timestamp).ToArray());

        var matchedPairs = 0;
        var unmatchedGood = 0;
        var unmatchedFault = 0;
        var candidates = new List<FirstDivergenceCandidate>();

        foreach (var key in goodByKey.Keys.Union(faultByKey.Keys)
                     .OrderBy(key => key.Id).ThenBy(key => key.IsExtended))
        {
            goodByKey.TryGetValue(key, out var goodForId);
            faultByKey.TryGetValue(key, out var faultForId);

            if (goodForId is null)
            {
                unmatchedFault += faultForId!.Length;
                var first = faultForId[0];
                candidates.Add(new FirstDivergenceCandidate(
                    key.Id, key.IsExtended, FirstDivergenceKind.IdOnlyInFault,
                    null, Offset(first.Timestamp, faultOrigin),
                    null, first.Data.ToArray(), null,
                    "Этот CAN ID присутствует в FAULT, но полностью отсутствует в GOOD."));
                continue;
            }

            if (faultForId is null)
            {
                unmatchedGood += goodForId.Length;
                var first = goodForId[0];
                candidates.Add(new FirstDivergenceCandidate(
                    key.Id, key.IsExtended, FirstDivergenceKind.IdOnlyInGood,
                    Offset(first.Timestamp, goodOrigin), null,
                    first.Data.ToArray(), null, null,
                    "Этот CAN ID присутствует в GOOD, но полностью отсутствует в FAULT."));
                continue;
            }

            var comparison = CompareSameId(
                key.Id, key.IsExtended,
                goodForId, faultForId,
                goodOrigin, faultOrigin,
                tolerance.TotalMilliseconds);
            matchedPairs += comparison.MatchedPairs;
            unmatchedGood += comparison.UnmatchedGood;
            unmatchedFault += comparison.UnmatchedFault;
            if (comparison.FirstCandidate is not null)
                candidates.Add(comparison.FirstCandidate);
        }

        return new FirstDivergenceResult(
            good.Length,
            fault.Length,
            matchedPairs,
            unmatchedGood,
            unmatchedFault,
            tolerance,
            candidates
                .OrderBy(candidate => candidate.EvidenceOffsetMilliseconds)
                .ThenBy(candidate => candidate.Id)
                .ThenBy(candidate => candidate.IsExtended)
                .ToArray());
    }

    private static KeyComparison CompareSameId(
        uint id,
        bool isExtended,
        IReadOnlyList<CanFrame> good,
        IReadOnlyList<CanFrame> fault,
        DateTimeOffset goodOrigin,
        DateTimeOffset faultOrigin,
        double toleranceMilliseconds)
    {
        var matched = 0;
        var unmatchedGood = 0;
        var unmatchedFault = 0;
        FirstDivergenceCandidate? firstCandidate = null;

        // Pair by ordinal occurrence within the same CAN ID. If a pair is outside
        // the timing tolerance, do not shift either stream to a later occurrence:
        // that could turn a dropped/delayed frame into a false DATA divergence.
        var commonCount = Math.Min(good.Count, fault.Count);
        for (var index = 0; index < commonCount; index++)
        {
            var goodFrame = good[index];
            var faultFrame = fault[index];
            var goodOffset = Offset(goodFrame.Timestamp, goodOrigin);
            var faultOffset = Offset(faultFrame.Timestamp, faultOrigin);
            var distance = Math.Abs(faultOffset - goodOffset);

            if (distance > toleranceMilliseconds)
            {
                unmatchedGood++;
                unmatchedFault++;
                continue;
            }

            matched++;
            if (firstCandidate is not null || goodFrame.Data.AsSpan().SequenceEqual(faultFrame.Data))
                continue;

            var kind = goodFrame.Data.Length == faultFrame.Data.Length
                ? FirstDivergenceKind.PayloadChanged
                : FirstDivergenceKind.DlcChanged;
            firstCandidate = new FirstDivergenceCandidate(
                id,
                isExtended,
                kind,
                goodOffset,
                faultOffset,
                goodFrame.Data.ToArray(),
                faultFrame.Data.ToArray(),
                distance,
                kind == FirstDivergenceKind.DlcChanged
                    ? "Первое сопоставленное по времени сообщение этого ID имеет другой DLC."
                    : "Первое сопоставленное по времени сообщение этого ID имеет другой DATA.");
        }

        unmatchedGood += good.Count - commonCount;
        unmatchedFault += fault.Count - commonCount;
        return new KeyComparison(matched, unmatchedGood, unmatchedFault, firstCandidate);
    }

    private static CanFrame[] Prepare(IEnumerable<CanFrame> frames) =>
        frames
            .Where(frame => frame.Protocol == BusProtocol.ClassicalCan &&
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

    private static double Offset(DateTimeOffset timestamp, DateTimeOffset origin) =>
        (timestamp - origin).TotalMilliseconds;

    private sealed record KeyComparison(
        int MatchedPairs,
        int UnmatchedGood,
        int UnmatchedFault,
        FirstDivergenceCandidate? FirstCandidate);
}
