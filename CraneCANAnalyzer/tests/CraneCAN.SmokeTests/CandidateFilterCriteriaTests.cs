using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

internal static class CandidateFilterCriteriaTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        var directedDiscrete = new GuidedCandidate
        {
            Id = 0x18EF2120,
            IsExtended = true,
            ChangeKind = CandidateChangeKind.StableBit,
            DataIndex = 1,
            BitIndex = 0,
            ReferenceValue = 0x00,
            ActionValue = 0x01,
            ReactionMilliseconds = 4.1
        };

        Require(new CandidateFilterCriteria
        {
            Pgn = 0xEF00,
            SourceAddress = 0x20,
            DestinationAddress = 0x21,
            FlowKind = CandidateNetworkFlowKind.DirectedPdu1,
            DiscreteOnly = true,
            AfterActionOnly = true
        }.Matches(directedDiscrete),
            "Directed PDU1 candidate must match PGN/SA/DA/discrete/after-action filter.");

        Require(!new CandidateFilterCriteria { Pgn = 0xFE21 }.Matches(directedDiscrete),
            "PDU1 destination address must not be treated as part of PGN by candidate filter.");

        var broadcastDiscrete = new GuidedCandidate
        {
            Id = 0x18FC9600,
            IsExtended = true,
            ChangeKind = CandidateChangeKind.StableBit,
            DataIndex = 1,
            BitIndex = 0,
            ReactionMilliseconds = 1018.6
        };

        Require(new CandidateFilterCriteria
        {
            SourceAddress = 0x00,
            FlowKind = CandidateNetworkFlowKind.BroadcastPdu2
        }.Matches(broadcastDiscrete),
            "PDU2 candidate must match broadcast flow filter.");

        Require(!new CandidateFilterCriteria
        {
            FlowKind = CandidateNetworkFlowKind.DirectedPdu1
        }.Matches(broadcastDiscrete),
            "PDU2 candidate must not match directed PDU1 filter.");

        var negativeReaction = directedDiscrete with { ReactionMilliseconds = -214.5 };
        Require(!new CandidateFilterCriteria { AfterActionOnly = true }.Matches(negativeReaction),
            "Negative reaction candidate must be hidden by after-action filter.");

        var numeric = directedDiscrete with
        {
            ChangeKind = CandidateChangeKind.StableByte,
            BitIndex = null,
            ReferenceValue = 0xAC,
            ActionValue = 0xD3
        };
        Require(!new CandidateFilterCriteria { DiscreteOnly = true }.Matches(numeric),
            "Multi-bit numeric byte candidate must not match discrete-only filter.");

        Require(new CandidateFilterCriteria().Matches(numeric),
            "Empty candidate filter must preserve all candidates.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
