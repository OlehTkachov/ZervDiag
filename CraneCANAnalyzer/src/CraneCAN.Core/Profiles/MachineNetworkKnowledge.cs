using CraneCAN.Core.Network;

namespace CraneCAN.Core.Profiles;

public enum NetworkEvidenceSource
{
    ObservedAutomatically,
    Inferred,
    Documentation,
    UserConfirmed,
    Rejected
}

public sealed record MachineNetworkNodeEvidence
{
    public string NodeKey { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public NetworkEvidenceSource Source { get; init; } = NetworkEvidenceSource.ObservedAutomatically;
    public List<string> Evidence { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MachineNetworkFlowEvidence
{
    public string Key { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public NetworkEvidenceSource Source { get; init; } = NetworkEvidenceSource.ObservedAutomatically;
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MachineNetworkKnowledge
{
    public string ProtocolEstimate { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public List<MachineNetworkNodeEvidence> Nodes { get; init; } = [];
    public List<MachineNetworkFlowEvidence> Flows { get; init; } = [];
    public List<string> Evidence { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static MachineNetworkKnowledge FromSnapshot(CanNetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MachineNetworkKnowledge
        {
            ProtocolEstimate = snapshot.ProtocolEstimate.ToString(),
            Confidence = snapshot.ProtocolConfidence,
            Nodes = snapshot.Nodes.Select(node => new MachineNetworkNodeEvidence
            {
                NodeKey = node.NodeKey,
                Protocol = node.Protocol.ToString(),
                Address = node.AddressText,
                Identity = node.Identity,
                State = node.State,
                Confidence = node.Confidence,
                Evidence = node.Evidence.ToList(),
                ObservedAt = snapshot.CreatedAt
            }).ToList(),
            Flows = snapshot.Flows.Select(flow => new MachineNetworkFlowEvidence
            {
                Key = $"{flow.SourceAddress:X2}>{(flow.DestinationAddress.HasValue ? flow.DestinationAddress.Value.ToString("X2") : "PDU2")}",
                Description = $"{flow.Kind}; PGN {string.Join(", ", flow.Pgns)}",
                ObservedAt = snapshot.CreatedAt
            }).ToList(),
            Evidence = snapshot.ProtocolEvidence.Select(item => item.Description).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static MachineNetworkKnowledge MergeAutomatic(MachineNetworkKnowledge? current, CanNetworkSnapshot snapshot)
    {
        var observed = FromSnapshot(snapshot);
        if (current is null)
            return observed;

        var protectedNodes = current.Nodes
            .Where(node => node.Source is NetworkEvidenceSource.UserConfirmed or NetworkEvidenceSource.Documentation)
            .ToDictionary(node => node.NodeKey, StringComparer.Ordinal);
        foreach (var node in observed.Nodes)
            protectedNodes.TryAdd(node.NodeKey, node);

        var protectedFlows = current.Flows
            .Where(flow => flow.Source is NetworkEvidenceSource.UserConfirmed or NetworkEvidenceSource.Documentation)
            .ToDictionary(flow => flow.Key, StringComparer.Ordinal);
        foreach (var flow in observed.Flows)
            protectedFlows.TryAdd(flow.Key, flow);

        return observed with
        {
            Nodes = protectedNodes.Values.OrderBy(node => node.NodeKey, StringComparer.Ordinal).ToList(),
            Flows = protectedFlows.Values.OrderBy(flow => flow.Key, StringComparer.Ordinal).ToList(),
            Evidence = current.Evidence.Concat(observed.Evidence).Distinct(StringComparer.Ordinal).ToList()
        };
    }
}
