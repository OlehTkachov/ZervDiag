namespace CraneCAN.Core.Network;

internal sealed record CanopenNodeContext(
    CanopenObservedNode Node,
    NetworkPeriodicStreamSnapshot? Heartbeat,
    CanopenObservedFrame[] Bootups,
    CanopenObservedFrame? LastHeartbeat,
    uint[] CobIds,
    bool Emcy,
    NetworkNodeHealthState Health);

internal static class CanopenNodeContextBuilder
{
    public static CanopenNodeContext Build(
        CanopenObservedNode node,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams)
    {
        var heartbeat = streams.FirstOrDefault(s => s.CanopenNodeId == node.NodeId && s.IsCanopenHeartbeat);
        var health = heartbeat is null
            ? NetworkNodeHealthState.Learning
            : NetworkPeriodicityAnalyzer.DetermineNodeHealth([heartbeat], end);
        var bootups = node.Frames.Where(x => x.Info.Kind == CanopenObjectKind.BootUp).ToArray();
        if (bootups.Any(x => x.Frame.Timestamp - start > TimeSpan.FromSeconds(1)) &&
            health != NetworkNodeHealthState.Missing)
            health = NetworkNodeHealthState.ResetBootObserved;
        return new CanopenNodeContext(
            node,
            heartbeat,
            bootups,
            node.Frames.LastOrDefault(x => x.Info.Kind == CanopenObjectKind.Heartbeat),
            node.Frames.Select(x => x.Frame.Id).Distinct().ToArray(),
            node.Frames.Any(x => x.Info.Kind == CanopenObjectKind.Emcy),
            health);
    }
}
