using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Projects;

/// <summary>
/// Manages explicit REFERENCE/ACTION/RETURN -> Trace resource bindings stored
/// in a .canproject. A binding uses resource IDs, so it survives moving the
/// whole project folder and never persists an absolute legacy TRC path.
/// </summary>
public static class ProjectTraceBindingService
{
    public static CraneProjectTraceBinding? GetBinding(
        CraneProject project,
        Guid experimentResourceId,
        int repeatNumber,
        ProjectTraceBindingRole role)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.TraceBindings is null)
            throw new FormatException("В .canproject отсутствует traceBindings.");

        var matches = project.TraceBindings
            .Where(binding =>
                binding.ExperimentResourceId == experimentResourceId &&
                binding.RepeatNumber == repeatNumber &&
                binding.Role == role)
            .ToArray();

        if (matches.Length > 1)
        {
            throw new FormatException(
                $"В .canproject повторяется TRC binding: experiment={experimentResourceId:N}, " +
                $"repeat={repeatNumber}, role={role}.");
        }

        return matches.SingleOrDefault();
    }

    public static CraneProject SetBinding(
        CraneProject project,
        Guid experimentResourceId,
        int repeatNumber,
        ProjectTraceBindingRole role,
        Guid traceResourceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        CraneProjectCodec.Validate(project);
        ValidateKey(experimentResourceId, repeatNumber, role, traceResourceId);

        var experimentResource = project.Resources.SingleOrDefault(resource =>
            resource.ResourceId == experimentResourceId)
            ?? throw new InvalidOperationException(
                "Experiment resource для TRC binding отсутствует в проекте.");
        if (experimentResource.Kind != CraneProjectResourceKind.GuidedExperiment)
        {
            throw new InvalidOperationException(
                "TRC binding может ссылаться только на GuidedExperiment resource.");
        }

        var traceResource = project.Resources.SingleOrDefault(resource =>
            resource.ResourceId == traceResourceId)
            ?? throw new InvalidOperationException(
                "Trace resource для TRC binding отсутствует в проекте.");
        if (traceResource.Kind != CraneProjectResourceKind.Trace)
        {
            throw new InvalidOperationException(
                "TRC binding может указывать только на resource типа Trace.");
        }

        var bindings = project.TraceBindings?.ToList()
            ?? throw new FormatException("В .canproject отсутствует traceBindings.");
        var existing = GetBinding(
            project,
            experimentResourceId,
            repeatNumber,
            role);

        var updatedBinding = existing is null
            ? new CraneProjectTraceBinding
            {
                ExperimentResourceId = experimentResourceId,
                RepeatNumber = repeatNumber,
                Role = role,
                TraceResourceId = traceResourceId,
                UpdatedAt = DateTimeOffset.UtcNow
            }
            : existing with
            {
                TraceResourceId = traceResourceId,
                UpdatedAt = DateTimeOffset.UtcNow
            };

        if (existing is null)
        {
            bindings.Add(updatedBinding);
        }
        else
        {
            var index = bindings.FindIndex(binding =>
                binding.BindingId == existing.BindingId);
            bindings[index] = updatedBinding;
        }

        return project with
        {
            ProgramVersion = "0.7.0",
            TraceBindings = bindings,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static CraneProject RemoveBinding(
        CraneProject project,
        Guid experimentResourceId,
        int repeatNumber,
        ProjectTraceBindingRole role)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.TraceBindings is null)
            throw new FormatException("В .canproject отсутствует traceBindings.");

        var bindings = project.TraceBindings
            .Where(binding => !(
                binding.ExperimentResourceId == experimentResourceId &&
                binding.RepeatNumber == repeatNumber &&
                binding.Role == role))
            .ToList();

        if (bindings.Count == project.TraceBindings.Count)
            return project;

        return project with
        {
            ProgramVersion = "0.7.0",
            TraceBindings = bindings,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static CraneProject RemoveBindingsForResource(
        CraneProject project,
        Guid resourceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.TraceBindings is null)
            throw new FormatException("В .canproject отсутствует traceBindings.");

        var bindings = project.TraceBindings
            .Where(binding =>
                binding.ExperimentResourceId != resourceId &&
                binding.TraceResourceId != resourceId)
            .ToList();

        if (bindings.Count == project.TraceBindings.Count)
            return project;

        return project with
        {
            ProgramVersion = "0.7.0",
            TraceBindings = bindings,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static IReadOnlyList<string> ValidateBindings(CraneProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.TraceBindings is null)
            return ["В .canproject отсутствует traceBindings."];

        var issues = new List<string>();
        var bindingIds = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in project.TraceBindings)
        {
            if (binding.BindingId == Guid.Empty || !bindingIds.Add(binding.BindingId))
                issues.Add("TRC binding содержит пустой или повторяющийся bindingId.");
            if (binding.ExperimentResourceId == Guid.Empty)
                issues.Add("TRC binding содержит пустой experimentResourceId.");
            if (binding.TraceResourceId == Guid.Empty)
                issues.Add("TRC binding содержит пустой traceResourceId.");
            if (binding.RepeatNumber <= 0)
                issues.Add("TRC binding содержит неположительный repeatNumber.");
            if (!Enum.IsDefined(typeof(ProjectTraceBindingRole), binding.Role))
                issues.Add($"TRC binding содержит неизвестную роль {binding.Role}.");

            var key =
                $"{binding.ExperimentResourceId:N}:{binding.RepeatNumber}:{binding.Role}";
            if (!keys.Add(key))
                issues.Add($"TRC binding повторяет ключ {key}.");

            var experiment = project.Resources.FirstOrDefault(resource =>
                resource.ResourceId == binding.ExperimentResourceId);
            if (experiment is null)
                issues.Add($"TRC binding {binding.BindingId:N}: experiment resource отсутствует.");
            else if (experiment.Kind != CraneProjectResourceKind.GuidedExperiment)
                issues.Add($"TRC binding {binding.BindingId:N}: experiment resource имеет тип {experiment.Kind}.");

            var trace = project.Resources.FirstOrDefault(resource =>
                resource.ResourceId == binding.TraceResourceId);
            if (trace is null)
                issues.Add($"TRC binding {binding.BindingId:N}: Trace resource отсутствует.");
            else if (trace.Kind != CraneProjectResourceKind.Trace)
                issues.Add($"TRC binding {binding.BindingId:N}: target resource имеет тип {trace.Kind}, а не Trace.");
        }

        return issues.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateKey(
        Guid experimentResourceId,
        int repeatNumber,
        ProjectTraceBindingRole role,
        Guid traceResourceId)
    {
        if (experimentResourceId == Guid.Empty)
            throw new ArgumentException("experimentResourceId не должен быть пустым.", nameof(experimentResourceId));
        if (traceResourceId == Guid.Empty)
            throw new ArgumentException("traceResourceId не должен быть пустым.", nameof(traceResourceId));
        if (repeatNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(repeatNumber), "repeatNumber должен быть положительным.");
        if (!Enum.IsDefined(typeof(ProjectTraceBindingRole), role))
            throw new ArgumentOutOfRangeException(nameof(role), "Неизвестная роль TRC binding.");
    }
}
