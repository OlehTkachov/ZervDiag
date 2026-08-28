namespace CraneCAN.Core.Profiles;

public sealed record CanNodeDefinition(int Address, string Name, string Confidence);

public sealed record DiagnosticCodeDefinition(string Code, string Meaning);

public sealed record DiscreteSignalDefinition(int Channel, string Meaning);

public sealed record CanSystemProfile(
    string Id,
    string DisplayName,
    string Protocol,
    int? ConfirmedBitrate,
    IReadOnlyList<int> CandidateBitrates,
    IReadOnlyList<CanNodeDefinition> Nodes,
    IReadOnlyList<DiagnosticCodeDefinition> Diagnostics,
    IReadOnlyList<DiscreteSignalDefinition> DiscreteSignals,
    string Notes);
