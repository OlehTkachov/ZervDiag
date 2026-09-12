using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

public sealed record IncidentNetworkTimelineEvent(
    double RelativeMilliseconds,
    NetworkEventKind Kind,
    string NodeKey,
    uint? Id,
    string Description);

public sealed record IncidentNetworkAnalysisResult(
    DateTimeOffset MarkerTime,
    CanNetworkSnapshot Before,
    CanNetworkSnapshot After,
    NetworkSnapshotComparisonResult Changes,
    IReadOnlyList<IncidentNetworkTimelineEvent> Timeline,
    IReadOnlyList<string> Warnings);

public static class IncidentNetworkAnalyzer
{
    public static IncidentNetworkAnalysisResult Analyze(PreFaultIncident incident, int? bitrate = null)
    {
        ArgumentNullException.ThrowIfNull(incident);
        if (incident.Markers.Count == 0)
            throw new InvalidOperationException("Incident does not contain a marker.");

        var marker = incident.Markers.OrderBy(item => item.Timestamp).First().Timestamp;
        var beforeStart = marker - TimeSpan.FromSeconds(8);
        var beforeEnd = marker - TimeSpan.FromMilliseconds(250);
        var afterStart = marker;
        var afterEnd = marker + TimeSpan.FromSeconds(4);

        var beforeFrames = Select(incident.Frames, beforeStart, beforeEnd);
        var afterFrames = Select(incident.Frames, afterStart, afterEnd);
        var allFrames = incident.Frames.OrderBy(frame => frame.Timestamp).ToArray();

        var before = PassiveNetworkDiscoveryAnalyzer.Analyze(beforeFrames, "incident-before", bitrate);
        var after = PassiveNetworkDiscoveryAnalyzer.Analyze(afterFrames, "incident-after", bitrate);
        var changes = NetworkSnapshotComparer.Compare(before, after);
        var full = PassiveNetworkDiscoveryAnalyzer.Analyze(allFrames, "incident-full", bitrate);

        var timeline = full.Events
            .Select(item => new IncidentNetworkTimelineEvent(
                (item.Timestamp - marker).TotalMilliseconds,
                item.Kind,
                item.NodeKey,
                item.Id,
                item.Description))
            .OrderBy(item => item.RelativeMilliseconds)
            .ToArray();

        var warnings = new List<string>();
        if (beforeFrames.Length < 4)
            warnings.Add("REFERENCE network window contains few frames.");
        if (afterFrames.Length < 4)
            warnings.Add("POST network window contains few frames.");
        if (incident.QualityCodes.Count > 0)
            warnings.Add("Incident capture quality is limited: " + string.Join(", ", incident.QualityCodes));
        warnings.Add("Network differences are observed correlations and do not by themselves identify a failed ECU.");

        return new IncidentNetworkAnalysisResult(marker, before, after, changes, timeline, warnings);
    }

    private static CanFrame[] Select(IReadOnlyList<CanFrame> frames, DateTimeOffset start, DateTimeOffset end) =>
        frames.Where(frame => frame.Timestamp >= start && frame.Timestamp < end)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();
}
