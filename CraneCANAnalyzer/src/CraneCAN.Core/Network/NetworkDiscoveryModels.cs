namespace CraneCAN.Core.Network;

public enum NetworkProtocolEstimate
{
    Unknown,
    ClassicalCanGeneric,
    J1939Likely,
    CanopenLikely,
    MixedOrGateway
}

public enum NetworkNodeProtocol
{
    J1939,
    Canopen,
    GenericExtended
}

public enum NetworkNodeHealthState
{
    Unknown,
    Learning,
    Active,
    Suspect,
    Missing,
    Returned,
    ResetBootObserved
}

public enum NetworkEventKind
{
    NodeAppeared,
    NodeDisappeared,
    NodeReturned,
    AddressClaim,
    AddressConflict,
    AddressCannotClaim,
    CanopenBootUp,
    CanopenStateChanged,
    HeartbeatLost,
    HeartbeatReturned,
    MessageAppeared,
    PeriodicMessageLost,
    PeriodicMessageReturned,
    FlowAppeared
}

public sealed record NetworkEvidence
{
    public string Code { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double Weight { get; init; }
}

public sealed record J1939NameInfo
{
    public ulong RawValue { get; init; }
    public string RawHex => $"0x{RawValue:X16}";
    public uint IdentityNumber { get; init; }
    public ushort ManufacturerCode { get; init; }
    public byte EcuInstance { get; init; }
    public byte FunctionInstance { get; init; }
    public byte Function { get; init; }
    public byte VehicleSystem { get; init; }
    public byte VehicleSystemInstance { get; init; }
    public byte IndustryGroup { get; init; }
    public bool ArbitraryAddressCapable { get; init; }
}

public sealed record NetworkPeriodicStreamSnapshot
{
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public int FrameCount { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public double? AveragePeriodMilliseconds { get; init; }
    public double? MedianPeriodMilliseconds { get; init; }
    public double? MinimumPeriodMilliseconds { get; init; }
    public double? MaximumPeriodMilliseconds { get; init; }
    public double? JitterPercent { get; init; }
    public double? TimeoutMilliseconds { get; init; }
    public DateTimeOffset? ExpectedNextFrame { get; init; }
    public bool IsPeriodic { get; init; }
    public bool IsConfidentPeriodic { get; init; }
    public bool IsCanopenHeartbeat { get; init; }
    public byte? SourceAddress { get; init; }
    public byte? CanopenNodeId { get; init; }
}

public sealed record NetworkNodeSnapshot
{
    public string NodeKey { get; init; } = string.Empty;
    public NetworkNodeProtocol Protocol { get; init; }
    public int Address { get; init; }
    public string AddressText { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public long FrameCount { get; init; }
    public double AverageFrequencyHertz { get; init; }
    public double PeriodicityQuality { get; init; }
    public List<string> PgnOrCobIds { get; init; } = [];
    public List<string> DirectedPeers { get; init; } = [];
    public NetworkNodeHealthState Health { get; init; }
    public double Confidence { get; init; }
    public List<string> Evidence { get; init; } = [];
    public J1939NameInfo? J1939Name { get; init; }
    public string? CanopenNmtState { get; init; }
    public double? HeartbeatPeriodMilliseconds { get; init; }
    public bool BootUpObserved { get; init; }
    public bool EmcyObserved { get; init; }
}

public sealed record NetworkFlowSnapshot
{
    public byte SourceAddress { get; init; }
    public byte? DestinationAddress { get; init; }
    public string Kind { get; init; } = string.Empty;
    public long FrameCount { get; init; }
    public List<string> Pgns { get; init; } = [];
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public double AverageFrequencyHertz { get; init; }
}

public sealed record NetworkEvent
{
    public NetworkEventKind Kind { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string NodeKey { get; init; } = string.Empty;
    public uint? Id { get; init; }
    public bool? IsExtended { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed record CanNetworkSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProgramVersion { get; init; } = "0.7.0";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public string SourceReference { get; init; } = string.Empty;
    public int? Bitrate { get; init; }
    public NetworkProtocolEstimate ProtocolEstimate { get; init; }
    public double ProtocolConfidence { get; init; }
    public List<NetworkEvidence> ProtocolEvidence { get; init; } = [];
    public long FrameCount { get; init; }
    public long StandardFrameCount { get; init; }
    public long ExtendedFrameCount { get; init; }
    public List<NetworkNodeSnapshot> Nodes { get; init; } = [];
    public List<NetworkFlowSnapshot> Flows { get; init; } = [];
    public List<NetworkPeriodicStreamSnapshot> PeriodicStreams { get; init; } = [];
    public List<NetworkEvent> Events { get; init; } = [];
}
