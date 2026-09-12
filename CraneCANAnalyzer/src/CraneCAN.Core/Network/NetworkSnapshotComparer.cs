namespace CraneCAN.Core.Network;

public enum NetworkSnapshotDifferenceKind
{
    ProtocolChanged,
    NodeAppeared,
    NodeDisappeared,
    NodeStateChanged,
    NodeIdentityChanged,
    FlowAppeared,
    FlowDisappeared,
    StreamAppeared,
    StreamDisappeared,
    StreamPeriodChanged
}

public sealed record NetworkSnapshotDifference
{
    public NetworkSnapshotDifferenceKind Kind { get; init; }
    public string Key { get; init; } = string.Empty;
    public string GoodValue { get; init; } = string.Empty;
    public string FaultValue { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed record NetworkSnapshotComparisonResult(
    IReadOnlyList<NetworkSnapshotDifference> Differences)
{
    public int Count => Differences.Count;
}

public static class NetworkSnapshotComparer
{
    public static NetworkSnapshotComparisonResult Compare(CanNetworkSnapshot good, CanNetworkSnapshot fault)
    {
        ArgumentNullException.ThrowIfNull(good);
        ArgumentNullException.ThrowIfNull(fault);
        var differences = new List<NetworkSnapshotDifference>();

        if (good.ProtocolEstimate != fault.ProtocolEstimate)
            differences.Add(D(NetworkSnapshotDifferenceKind.ProtocolChanged, "protocol", good.ProtocolEstimate.ToString(), fault.ProtocolEstimate.ToString(), "Оценка протокола изменилась."));

        CompareNodes(good, fault, differences);
        CompareFlows(good, fault, differences);
        CompareStreams(good, fault, differences);
        return new NetworkSnapshotComparisonResult(differences);
    }

    private static void CompareNodes(CanNetworkSnapshot good, CanNetworkSnapshot fault, ICollection<NetworkSnapshotDifference> result)
    {
        var a = good.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
        var b = fault.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var left))
            {
                result.Add(D(NetworkSnapshotDifferenceKind.NodeAppeared, key, "—", b[key].State, "Узел появился только в FAULT."));
                continue;
            }
            if (!b.TryGetValue(key, out var right))
            {
                result.Add(D(NetworkSnapshotDifferenceKind.NodeDisappeared, key, left.State, "—", "Узел отсутствует в FAULT."));
                continue;
            }
            if (!string.Equals(left.State, right.State, StringComparison.Ordinal))
                result.Add(D(NetworkSnapshotDifferenceKind.NodeStateChanged, key, left.State, right.State, "Состояние узла изменилось."));
            if (!string.Equals(left.Identity, right.Identity, StringComparison.Ordinal))
                result.Add(D(NetworkSnapshotDifferenceKind.NodeIdentityChanged, key, left.Identity, right.Identity, "Идентификация узла изменилась."));
        }
    }

    private static void CompareFlows(CanNetworkSnapshot good, CanNetworkSnapshot fault, ICollection<NetworkSnapshotDifference> result)
    {
        var a = good.Flows.ToDictionary(FlowKey, StringComparer.Ordinal);
        var b = fault.Flows.ToDictionary(FlowKey, StringComparer.Ordinal);
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!a.ContainsKey(key)) result.Add(D(NetworkSnapshotDifferenceKind.FlowAppeared, key, "—", "present", "Логический путь появился в FAULT."));
            else if (!b.ContainsKey(key)) result.Add(D(NetworkSnapshotDifferenceKind.FlowDisappeared, key, "present", "—", "Логический путь пропал в FAULT."));
        }
    }

    private static void CompareStreams(CanNetworkSnapshot good, CanNetworkSnapshot fault, ICollection<NetworkSnapshotDifference> result)
    {
        var a = good.PeriodicStreams.Where(s => s.IsPeriodic).ToDictionary(StreamKey, StringComparer.Ordinal);
        var b = fault.PeriodicStreams.Where(s => s.IsPeriodic).ToDictionary(StreamKey, StringComparer.Ordinal);
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var left)) { result.Add(D(NetworkSnapshotDifferenceKind.StreamAppeared, key, "—", "periodic", "Периодический поток появился в FAULT.")); continue; }
            if (!b.TryGetValue(key, out var right)) { result.Add(D(NetworkSnapshotDifferenceKind.StreamDisappeared, key, "periodic", "—", "Периодический поток пропал в FAULT.")); continue; }
            if (left.MedianPeriodMilliseconds.HasValue && right.MedianPeriodMilliseconds.HasValue)
            {
                var basePeriod = Math.Max(1.0, left.MedianPeriodMilliseconds.Value);
                var change = Math.Abs(right.MedianPeriodMilliseconds.Value - left.MedianPeriodMilliseconds.Value) / basePeriod;
                if (change >= 0.20)
                    result.Add(D(NetworkSnapshotDifferenceKind.StreamPeriodChanged, key,
                        left.MedianPeriodMilliseconds.Value.ToString("0.###"), right.MedianPeriodMilliseconds.Value.ToString("0.###"),
                        "Медианный период изменился не менее чем на 20 %."));
            }
        }
    }

    private static string FlowKey(NetworkFlowSnapshot flow) => $"{flow.SourceAddress:X2}>{(flow.DestinationAddress.HasValue ? flow.DestinationAddress.Value.ToString("X2") : "PDU2")}:{flow.Kind}";
    private static string StreamKey(NetworkPeriodicStreamSnapshot stream) => $"{(stream.IsExtended ? "E" : "S")}:{stream.Id:X8}";
    private static NetworkSnapshotDifference D(NetworkSnapshotDifferenceKind kind, string key, string good, string fault, string description) => new() { Kind = kind, Key = key, GoodValue = good, FaultValue = fault, Description = description };
}
