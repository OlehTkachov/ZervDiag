using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public enum SignalByteOrder
{
    LittleEndian,
    BigEndian
}

public sealed record SignalEnumState
{
    public long Value { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed record MachineSignal
{
    public Guid SignalId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint CanId { get; init; }
    public bool IsExtended { get; init; }
    public int StartByte { get; init; }
    public int StartBit { get; init; }
    public int BitLength { get; init; } = 1;
    public SignalByteOrder ByteOrder { get; init; } = SignalByteOrder.LittleEndian;
    public bool IsSigned { get; init; }
    public double Scale { get; init; } = 1;
    public double Offset { get; init; }
    public string Unit { get; init; } = string.Empty;
    public List<SignalEnumState> EnumStates { get; init; } = [];
    public SignalKnowledgeState Confidence { get; init; } = SignalKnowledgeState.Candidate;
    public List<SignalEvidence> Evidence { get; init; } = [];
    public string Source { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RejectedCandidate
{
    public string CandidateKey { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset RejectedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MachineProfile
{
    public const int CurrentSchemaVersion = 1;

    public Guid ProfileId { get; init; } = Guid.NewGuid();
    public int ProfileSchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? SerialNumber { get; init; }
    public string MachineName { get; init; } = "Новая / неизвестная машина";
    public string Subsystem { get; init; } = string.Empty;
    public string CanBusName { get; init; } = "CAN1";
    public int? Bitrate { get; init; }
    public string CanType { get; init; } = "Classical CAN";
    public List<MachineSignal> KnownSignals { get; init; } = [];
    public List<MachineSignal> ExperimentalSignals { get; init; } = [];
    public List<RejectedCandidate> RejectedCandidates { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ProgramVersion { get; init; } = "0.6.0";
}
