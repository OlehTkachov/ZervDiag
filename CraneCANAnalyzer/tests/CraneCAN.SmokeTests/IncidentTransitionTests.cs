using CraneCAN.Core.Analysis;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

internal static class IncidentTransitionTests
{
    public static void Run()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var frames = new List<CanFrame>();
        for (var t = 2.0; t < 9.0; t += 0.1) frames.Add(Frame(t, 0x100, false, 0x10, 0x00));
        for (var t = 9.8; t < 14.0; t += 0.1) frames.Add(Frame(t, 0x100, false, 0x10, 0x20));
        frames.Add(Frame(10.3, 0x200, false, 0x55));
        frames.Add(Frame(10.4, 0x200, false, 0x55));
        frames.Add(Frame(10.5, 0x200, false, 0x55));
        for (var t = 2.0; t < 9.0; t += 0.1) frames.Add(Frame(t, 0x300, false, 0x01));
        for (var t = 2.0; t < 9.0; t += 0.1) frames.Add(Frame(t, 0x400, false, 0x00));
        frames.Add(Frame(10.1, 0x400, false, 0x01));
        frames.Add(Frame(10.2, 0x400, false, 0x00));
        frames.Add(Frame(10.3, 0x400, false, 0x00));

        var incident = Incident(marker, frames.OrderBy(frame => frame.Timestamp).ToArray());
        var result = IncidentTransitionAnalyzer.Analyze(incident);

        var byteChange = result.Candidates.Single(candidate => candidate.Id == 0x100 && candidate.Kind == IncidentTransitionKind.ByteChanged && candidate.DataIndex == 1);
        Check(byteChange.Priority == IncidentTransitionPriority.High && Math.Abs(byteChange.ReactionMilliseconds + 200) < 0.001 &&
              byteChange.BaselineValue == "0x00" && byteChange.ObservedValue == "0x20" && byteChange.ConfirmationCount >= 2,
            "Persistent pre-marker byte transition was not ranked/timed correctly.");

        var appeared = result.Candidates.Single(candidate => candidate.Id == 0x200 && candidate.Kind == IncidentTransitionKind.IdAppeared);
        Check(appeared.Priority == IncidentTransitionPriority.High && Math.Abs(appeared.ReactionMilliseconds - 300) < 0.001,
            "Repeated newly appearing ID was not detected.");

        var stopped = result.Candidates.Single(candidate => candidate.Id == 0x300 && candidate.Kind == IncidentTransitionKind.PeriodicIdStopped);
        Check(stopped.Priority == IncidentTransitionPriority.Medium && stopped.ReactionMilliseconds < 0,
            "Periodic ID stop was not detected conservatively.");

        var transient = result.Candidates.Single(candidate => candidate.Id == 0x400 && candidate.Kind == IncidentTransitionKind.ByteChanged);
        Check(transient.Priority == IncidentTransitionPriority.Medium && transient.ConfirmationCount == 1,
            "Single-frame transient was incorrectly promoted to HIGH.");

        Check(result.Candidates.SequenceEqual(result.Candidates.OrderBy(candidate => candidate.ReactionMilliseconds)
                .ThenByDescending(candidate => candidate.Priority).ThenBy(candidate => candidate.Id)
                .ThenBy(candidate => candidate.IsExtended).ThenBy(candidate => candidate.DataIndex ?? -1)),
            "Incident candidates are not ordered by observed event time.");

        var silentFrames = new List<CanFrame>();
        for (var t = 2.0; t < 9.0; t += 0.1) silentFrames.Add(Frame(t, 0x500, false, 0x01));
        var silentResult = IncidentTransitionAnalyzer.Analyze(
            Incident(marker, silentFrames.OrderBy(frame => frame.Timestamp).ToArray()));
        Check(silentResult.SearchFrameCount == 0 &&
              silentResult.Candidates.Any(candidate =>
                  candidate.Id == 0x500 && candidate.Kind == IncidentTransitionKind.PeriodicIdStopped) &&
              silentResult.Warnings.Any(text => text.Contains("search-окне нет CAN-кадров", StringComparison.Ordinal)),
            "Completely silent search window did not preserve periodic-stop diagnosis.");

        var multiMarker = incident with
        {
            Markers = [new IncidentMarker(marker, "first"), new IncidentMarker(marker.AddMilliseconds(500), "second")],
            QualityCodes = ["CAPTURE_UNCERTAIN"]
        };
        var warningResult = IncidentTransitionAnalyzer.Analyze(multiMarker);
        Check(warningResult.Warnings.Any(text => text.Contains("2 отметок", StringComparison.Ordinal)) &&
              warningResult.Warnings.Any(text => text.Contains("CAPTURE_UNCERTAIN", StringComparison.Ordinal)),
            "Incident quality/multiple-marker warnings were lost.");

        var emptyBaseline = Incident(marker,
            [Frame(10.1, 0x100, false, 0x01), Frame(10.2, 0x100, false, 0x01)],
            marker.AddSeconds(-0.5));
        var rejected = false;
        try { IncidentTransitionAnalyzer.Analyze(emptyBaseline); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Incident without usable baseline was accepted.");
    }

    private static PreFaultIncident Incident(DateTimeOffset marker, IReadOnlyList<CanFrame> frames, DateTimeOffset? windowStart = null) => new(
        Guid.NewGuid(), windowStart ?? marker.AddSeconds(-10), marker.AddSeconds(5), marker.AddSeconds(5),
        [new IncidentMarker(marker, "fault")], frames, []);

    private static CanFrame Frame(double absoluteSeconds, uint id, bool extended, params byte[] data) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(absoluteSeconds), Channel = 0, Id = id, IsExtended = extended,
        Data = data, Protocol = BusProtocol.ClassicalCan, Direction = CanDirection.Rx
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
