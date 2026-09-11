using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Analysis;

public enum CandidateNetworkFlowKind
{
    DirectedPdu1,
    BroadcastPdu2
}

public sealed record CandidateNetworkFlowSummary(
    byte SourceAddress,
    byte? DestinationAddress,
    CandidateNetworkFlowKind Kind,
    int CandidateCount,
    int DistinctIdCount,
    double? FirstReactionMilliseconds)
{
    public string SourceAddressText => $"0x{SourceAddress:X2}";
    public string DestinationAddressText => DestinationAddress.HasValue
        ? $"0x{DestinationAddress.Value:X2}"
        : "—";
}

/// <summary>
/// Groups Guided Diagnostics candidates by the J1939-like communication path
/// observed in their 29-bit identifiers. This is a protocol hint only: an
/// unknown extended CAN bus is not automatically claimed to be J1939.
/// </summary>
public static class CandidateNetworkFlowAnalyzer
{
    public static IReadOnlyList<CandidateNetworkFlowSummary> Summarize(
        IEnumerable<GuidedCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var groups = new Dictionary<FlowKey, FlowAccumulator>();

        foreach (var candidate in candidates)
        {
            if (!CandidateProtocolHints.TryDecodeJ1939(
                    candidate.Id,
                    candidate.IsExtended,
                    out var info))
            {
                continue;
            }

            var destinationAddress = info.DestinationAddress;
            var kind = destinationAddress.HasValue
                ? CandidateNetworkFlowKind.DirectedPdu1
                : CandidateNetworkFlowKind.BroadcastPdu2;
            var key = new FlowKey(info.SourceAddress, destinationAddress, kind);

            if (!groups.TryGetValue(key, out var accumulator))
            {
                accumulator = new FlowAccumulator();
                groups.Add(key, accumulator);
            }

            accumulator.CandidateCount++;
            accumulator.Ids.Add(candidate.Id);
            if (candidate.ReactionMilliseconds.HasValue &&
                (!accumulator.FirstReactionMilliseconds.HasValue ||
                 candidate.ReactionMilliseconds.Value < accumulator.FirstReactionMilliseconds.Value))
            {
                accumulator.FirstReactionMilliseconds = candidate.ReactionMilliseconds.Value;
            }
        }

        return groups
            .Select(pair => new CandidateNetworkFlowSummary(
                pair.Key.SourceAddress,
                pair.Key.DestinationAddress,
                pair.Key.Kind,
                pair.Value.CandidateCount,
                pair.Value.Ids.Count,
                pair.Value.FirstReactionMilliseconds))
            .OrderBy(item => item.FirstReactionMilliseconds ?? double.PositiveInfinity)
            .ThenByDescending(item => item.CandidateCount)
            .ThenBy(item => item.SourceAddress)
            .ThenBy(item => item.DestinationAddress ?? byte.MaxValue)
            .ToArray();
    }

    private readonly record struct FlowKey(
        byte SourceAddress,
        byte? DestinationAddress,
        CandidateNetworkFlowKind Kind);

    private sealed class FlowAccumulator
    {
        public int CandidateCount { get; set; }
        public HashSet<uint> Ids { get; } = [];
        public double? FirstReactionMilliseconds { get; set; }
    }
}
