using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

internal static class CandidateNetworkFlowAnalyzerTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        var candidates = new[]
        {
            new GuidedCandidate
            {
                Id = 0x18EF2120,
                IsExtended = true,
                ChangeKind = CandidateChangeKind.StableByte,
                DataIndex = 3,
                ReactionMilliseconds = 2124.6
            },
            new GuidedCandidate
            {
                Id = 0x18EF2120,
                IsExtended = true,
                ChangeKind = CandidateChangeKind.StableByte,
                DataIndex = 4,
                ReactionMilliseconds = 2200.0
            },
            new GuidedCandidate
            {
                Id = 0x18FC9600,
                IsExtended = true,
                ChangeKind = CandidateChangeKind.StableBit,
                DataIndex = 1,
                BitIndex = 0,
                ReactionMilliseconds = 1018.6
            },
            new GuidedCandidate
            {
                Id = 0x123,
                IsExtended = false,
                ChangeKind = CandidateChangeKind.StableBit,
                DataIndex = 0,
                BitIndex = 0,
                ReactionMilliseconds = 10
            }
        };

        var summary = CandidateNetworkFlowAnalyzer.Summarize(candidates);

        Require(summary.Count == 2,
            "Expected one PDU1 flow and one PDU2 flow; Standard CAN must be ignored.");

        var directed = summary.Single(item => item.Kind == CandidateNetworkFlowKind.DirectedPdu1);
        Require(
            directed.SourceAddress == 0x20 &&
            directed.DestinationAddress == 0x21 &&
            directed.CandidateCount == 2 &&
            directed.DistinctIdCount == 1 &&
            Math.Abs(directed.FirstReactionMilliseconds!.Value - 2124.6) < 0.001,
            "PDU1 SA/DA grouping failed for 18EF2120.");

        var broadcast = summary.Single(item => item.Kind == CandidateNetworkFlowKind.BroadcastPdu2);
        Require(
            broadcast.SourceAddress == 0x00 &&
            broadcast.DestinationAddress is null &&
            broadcast.CandidateCount == 1 &&
            broadcast.DistinctIdCount == 1 &&
            Math.Abs(broadcast.FirstReactionMilliseconds!.Value - 1018.6) < 0.001,
            "PDU2 grouping failed for 18FC9600.");

        Require(summary[0] == broadcast,
            "Network flow groups must be ordered by earliest observed reaction.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
