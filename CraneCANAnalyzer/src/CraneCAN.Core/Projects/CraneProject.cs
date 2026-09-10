namespace CraneCAN.Core.Projects;

public enum CraneProjectResourceKind
{
    MachineProfile,
    GuidedExperiment,
    Incident,
    CanSnapshot,
    Trace,
    Report,
    Document,
    Other
}

public enum ProjectTraceBindingRole
{
    Reference,
    Action,
    Return
}

public sealed record CraneProjectResource
{
    public Guid ResourceId { get; init; } = Guid.NewGuid();
    public CraneProjectResourceKind Kind { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CraneProjectTraceBinding
{
    public Guid BindingId { get; init; } = Guid.NewGuid();
    public Guid ExperimentResourceId { get; init; }
    public int RepeatNumber { get; init; }
    public ProjectTraceBindingRole Role { get; init; }
    public Guid TraceResourceId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CraneProject
{
    public const int CurrentSchemaVersion = 1;

    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProgramVersion { get; init; } = "0.7.0";
    public string Name { get; init; } = "Новый проект CraneCAN";
    public string MachineName { get; init; } = "Новая / неизвестная машина";
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string CanBusName { get; init; } = "CAN1";
    public int? Bitrate { get; init; }
    public string CanType { get; init; } = "Classical CAN";
    public Guid? ActiveProfileResourceId { get; init; }
    public Guid? ActiveExperimentResourceId { get; init; }
    public List<CraneProjectResource> Resources { get; init; } = [];
    public List<CraneProjectTraceBinding> TraceBindings { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
