using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

internal static class CandidateProtocolHintsTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        Require(
            CandidateProtocolHints.TryDecodeJ1939(0x0CF00400, true, out var eec1) &&
            eec1.Pgn == 0xF004 &&
            eec1.SourceAddress == 0x00,
            "J1939 EEC1 PGN/SA decoding failed.");

        Require(
            CandidateProtocolHints.TryDecodeJ1939(0x18FEEF00, true, out var feef) &&
            feef.Pgn == 0xFEEF &&
            feef.SourceAddress == 0x00,
            "J1939 FEEF PGN/SA decoding failed.");

        Require(
            CandidateProtocolHints.TryDecodeJ1939(0x18FE2120, true, out var fe21) &&
            fe21.Pgn == 0xFE21 &&
            fe21.SourceAddress == 0x20,
            "J1939 FE21 PGN/SA decoding failed.");

        Require(
            CandidateProtocolHints.TryDecodeJ1939(0x18EAFF80, true, out var request) &&
            request.Pgn == 0xEA00 &&
            request.SourceAddress == 0x80,
            "J1939 PDU1 destination must not be included in PGN.");

        Require(
            !CandidateProtocolHints.TryDecodeJ1939(0x123, false, out _),
            "Standard CAN ID must not be presented as J1939 PGN/SA.");

        Require(
            CandidateProtocolHints.Classify(new GuidedCandidate
            {
                ChangeKind = CandidateChangeKind.StableBit,
                BitIndex = 2
            }) == "дискретный",
            "Stable bit must be classified as discrete.");

        Require(
            CandidateProtocolHints.Classify(new GuidedCandidate
            {
                ChangeKind = CandidateChangeKind.StableByte,
                ReferenceValue = 0x00,
                ActionValue = 0x04
            }) == "дискретный?",
            "Single-bit byte transition must be marked as probable discrete.");

        Require(
            CandidateProtocolHints.Classify(new GuidedCandidate
            {
                ChangeKind = CandidateChangeKind.StableByte,
                ReferenceValue = 0xAC,
                ActionValue = 0xD3
            }) == "числовой / enum?",
            "Multi-bit stable byte transition must remain conservative.");

        Require(
            CandidateProtocolHints.Classify(new GuidedCandidate
            {
                ChangeKind = CandidateChangeKind.Ramp
            }) == "аналоговый",
            "Ramp must be classified as analog.");

        Require(
            CandidateProtocolHints.Classify(new GuidedCandidate
            {
                ChangeKind = CandidateChangeKind.MessageAppeared
            }) == "ID появился",
            "Message appearance classification failed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
