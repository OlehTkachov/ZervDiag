using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal sealed record NetworkProtocolEstimateResult(NetworkProtocolEstimate Estimate, double Confidence, List<NetworkEvidence> Evidence);

internal static class NetworkProtocolEstimator
{
    public static NetworkProtocolEstimateResult Estimate(IReadOnlyList<CanFrame> frames, IReadOnlyList<NetworkPeriodicStreamSnapshot> streams)
    {
        var evidence = new List<NetworkEvidence>();
        var j = J1939ProtocolScorer.Score(frames, streams, evidence);
        var c = CanopenProtocolScorer.Score(frames, evidence);
        var result = NetworkProtocolEstimate.ClassicalCanGeneric;
        if (j >= 0.5 && c >= 0.5) result = NetworkProtocolEstimate.MixedOrGateway;
        else if (j >= 0.5) result = NetworkProtocolEstimate.J1939Likely;
        else if (c >= 0.5) result = NetworkProtocolEstimate.CanopenLikely;
        return new NetworkProtocolEstimateResult(result, Math.Max(j, c), evidence);
    }
}
