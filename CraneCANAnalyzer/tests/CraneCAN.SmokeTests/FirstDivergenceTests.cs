using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;

internal static class FirstDivergenceTests
{
    public static void Run()
    {
        var goodOrigin = DateTimeOffset.UnixEpoch;
        var faultOrigin = goodOrigin.AddHours(3);

        var identicalGood = new[]
        {
            Frame(goodOrigin, 0, 0x100, 1),
            Frame(goodOrigin, 100, 0x100, 2),
            Frame(goodOrigin, 200, 0x100, 3)
        };
        var identicalFault = new[]
        {
            Frame(faultOrigin, 20, 0x100, 1),
            Frame(faultOrigin, 120, 0x100, 2),
            Frame(faultOrigin, 220, 0x100, 3)
        };
        var identical = FirstDivergenceAnalyzer.Analyze(
            identicalGood, identicalFault, TimeSpan.FromMilliseconds(50));
        Check(identical.Candidates.Count == 0 && identical.MatchedFramePairs == 3,
            "Relative-time alignment produced a false GOOD/FAULT divergence.");

        var changedFault = new[]
        {
            Frame(faultOrigin, 10, 0x100, 1),
            Frame(faultOrigin, 110, 0x100, 9),
            Frame(faultOrigin, 210, 0x100, 3)
        };
        var changed = FirstDivergenceAnalyzer.Analyze(
            identicalGood, changedFault, TimeSpan.FromMilliseconds(50));
        Check(changed.Earliest is
              {
                  Id: 0x100,
                  Kind: FirstDivergenceKind.PayloadChanged
              } &&
              changed.Earliest.GoodOffsetMilliseconds == 100 &&
              changed.Earliest.FaultOffsetMilliseconds == 100,
            "First payload divergence was not identified at the expected relative time.");

        var presenceGood = new[]
        {
            Frame(goodOrigin, 0, 0x100, 1),
            Frame(goodOrigin, 50, 0x200, 7)
        };
        var presenceFault = new[]
        {
            Frame(faultOrigin, 0, 0x100, 1),
            Frame(faultOrigin, 25, 0x300, 8)
        };
        var presence = FirstDivergenceAnalyzer.Analyze(presenceGood, presenceFault);
        Check(presence.Candidates.Any(candidate =>
                  candidate.Id == 0x200 && candidate.Kind == FirstDivergenceKind.IdOnlyInGood) &&
              presence.Candidates.Any(candidate =>
                  candidate.Id == 0x300 && candidate.Kind == FirstDivergenceKind.IdOnlyInFault),
            "Whole-ID presence divergence was not preserved.");

        var timingFault = new[]
        {
            Frame(faultOrigin, 0, 0x100, 1),
            Frame(faultOrigin, 180, 0x100, 2),
            Frame(faultOrigin, 280, 0x100, 3)
        };
        var timing = FirstDivergenceAnalyzer.Analyze(
            identicalGood, timingFault, TimeSpan.FromMilliseconds(30));
        Check(timing.Candidates.Count == 0 &&
              timing.UnmatchedGoodFrames > 0 &&
              timing.UnmatchedFaultFrames > 0,
            "Unmatched timing jitter was incorrectly promoted to a fault candidate.");

        var dlcGood = new[] { Frame(goodOrigin, 0, 0x555, 1, 2) };
        var dlcFault = new[] { Frame(faultOrigin, 0, 0x555, 1) };
        var dlc = FirstDivergenceAnalyzer.Analyze(dlcGood, dlcFault);
        Check(dlc.Earliest?.Kind == FirstDivergenceKind.DlcChanged,
            "DLC divergence was not classified separately.");

        var rejected = false;
        try
        {
            FirstDivergenceAnalyzer.Analyze(identicalGood, identicalFault, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Check(rejected, "Invalid First Divergence tolerance was accepted.");
    }

    private static CanFrame Frame(DateTimeOffset origin, int milliseconds, uint id, params byte[] data) => new()
    {
        Timestamp = origin.AddMilliseconds(milliseconds),
        Channel = 0,
        Id = id,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
