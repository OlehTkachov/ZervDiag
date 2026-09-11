using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Analysis;

/// <summary>
/// Read-only display filter for Guided Diagnostics candidates. Protocol fields
/// are treated as J1939 hints only; this filter never changes analysis results.
/// </summary>
public sealed record CandidateFilterCriteria
{
    public uint? Pgn { get; init; }
    public byte? SourceAddress { get; init; }
    public byte? DestinationAddress { get; init; }
    public CandidateNetworkFlowKind? FlowKind { get; init; }
    public bool DiscreteOnly { get; init; }
    public bool AfterActionOnly { get; init; }

    public bool IsEmpty =>
        !Pgn.HasValue &&
        !SourceAddress.HasValue &&
        !DestinationAddress.HasValue &&
        !FlowKind.HasValue &&
        !DiscreteOnly &&
        !AfterActionOnly;

    public bool Matches(GuidedCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (DiscreteOnly && !CandidateProtocolHints.IsLikelyDiscrete(candidate))
            return false;

        if (AfterActionOnly &&
            (!candidate.ReactionMilliseconds.HasValue || candidate.ReactionMilliseconds.Value < 0))
            return false;

        var needsProtocolDecode =
            Pgn.HasValue || SourceAddress.HasValue || DestinationAddress.HasValue || FlowKind.HasValue;
        if (!needsProtocolDecode)
            return true;

        if (!CandidateProtocolHints.TryDecodeJ1939(candidate.Id, candidate.IsExtended, out var info))
            return false;

        if (Pgn.HasValue && info.Pgn != Pgn.Value)
            return false;
        if (SourceAddress.HasValue && info.SourceAddress != SourceAddress.Value)
            return false;
        if (DestinationAddress.HasValue && info.DestinationAddress != DestinationAddress.Value)
            return false;

        if (FlowKind.HasValue)
        {
            var actualKind = info.DestinationAddress.HasValue
                ? CandidateNetworkFlowKind.DirectedPdu1
                : CandidateNetworkFlowKind.BroadcastPdu2;
            if (actualKind != FlowKind.Value)
                return false;
        }

        return true;
    }
}
