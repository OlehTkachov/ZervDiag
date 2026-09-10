using CraneCAN.Core.Guided;
using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public enum ProjectIntegritySeverity
{
    Information,
    Warning,
    Error
}

public enum ProjectIntegrityCode
{
    Healthy,
    ManifestInvalid,
    EmptyProject,
    ResourceMissing,
    ActiveResourceMissing,
    BoundTraceMissing,
    ExperimentUnreadable,
    DuplicateExperimentRepeat,
    StaleBinding,
    DependencyBound,
    DependencyResolvedAutomatically,
    DependencyAmbiguous,
    DependencyMissing,
    DependencyOutsideProject,
    DependencyUnregistered
}

public sealed record ProjectIntegrityIssue(
    ProjectIntegritySeverity Severity,
    ProjectIntegrityCode Code,
    string Subject,
    string Message);

public sealed record ProjectIntegrityReport(
    IReadOnlyList<ProjectIntegrityIssue> Issues)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == ProjectIntegritySeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == ProjectIntegritySeverity.Warning);
    public int InformationCount => Issues.Count(issue => issue.Severity == ProjectIntegritySeverity.Information);
    public bool IsHealthy => ErrorCount == 0 && WarningCount == 0;
}

/// <summary>
/// Performs an offline integrity audit of a saved CraneCAN project. The audit
/// never modifies resources, experiments or traces and never performs CAN I/O.
/// </summary>
public static class ProjectIntegrityAnalyzer
{
    public static async Task<ProjectIntegrityReport> AnalyzeAsync(
        string projectPath,
        CraneProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(project);

        var issues = new List<ProjectIntegrityIssue>();
        try
        {
            CraneProjectCodec.Validate(project);
        }
        catch (Exception exception)
        {
            issues.Add(new ProjectIntegrityIssue(
                ProjectIntegritySeverity.Error,
                ProjectIntegrityCode.ManifestInvalid,
                ".canproject",
                exception.Message));
            return new ProjectIntegrityReport(issues);
        }

        if (project.Resources.Count == 0)
        {
            issues.Add(new ProjectIntegrityIssue(
                ProjectIntegritySeverity.Warning,
                ProjectIntegrityCode.EmptyProject,
                ".canproject",
                "В проекте нет resources."));
        }

        var boundTraceIds = project.TraceBindings
            .Select(binding => binding.TraceResourceId)
            .ToHashSet();

        foreach (var resource in project.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = CraneProjectCodec.ResolveResourcePath(projectPath, resource);
            if (File.Exists(fullPath))
                continue;

            var isActive =
                project.ActiveProfileResourceId == resource.ResourceId ||
                project.ActiveExperimentResourceId == resource.ResourceId;
            var isBoundTrace = boundTraceIds.Contains(resource.ResourceId);
            var severity = isActive || isBoundTrace
                ? ProjectIntegritySeverity.Error
                : ProjectIntegritySeverity.Warning;
            var code = isBoundTrace
                ? ProjectIntegrityCode.BoundTraceMissing
                : isActive
                    ? ProjectIntegrityCode.ActiveResourceMissing
                    : ProjectIntegrityCode.ResourceMissing;
            var role = isBoundTrace
                ? "Trace resource используется persistent binding, но файл отсутствует."
                : isActive
                    ? "Активный resource проекта отсутствует на диске."
                    : "Resource проекта отсутствует на диске.";

            issues.Add(new ProjectIntegrityIssue(
                severity,
                code,
                resource.RelativePath,
                role));
        }

        foreach (var experimentResource in project.Resources.Where(resource =>
                     resource.Kind == CraneProjectResourceKind.GuidedExperiment))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var experimentPath = CraneProjectCodec.ResolveResourcePath(
                projectPath,
                experimentResource);
            if (!File.Exists(experimentPath))
                continue;

            GuidedExperiment experiment;
            try
            {
                experiment = await GuidedJsonCodec.ReadExperimentAsync(
                        experimentPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                issues.Add(new ProjectIntegrityIssue(
                    ProjectIntegritySeverity.Error,
                    ProjectIntegrityCode.ExperimentUnreadable,
                    experimentResource.RelativePath,
                    $"Guided Experiment не читается: {exception.Message}"));
                continue;
            }

            var duplicateRepeats = experiment.Repeats
                .GroupBy(repeat => repeat.RepeatNumber)
                .Where(group => group.Key <= 0 || group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(value => value)
                .ToArray();
            foreach (var repeatNumber in duplicateRepeats)
            {
                issues.Add(new ProjectIntegrityIssue(
                    ProjectIntegritySeverity.Error,
                    ProjectIntegrityCode.DuplicateExperimentRepeat,
                    experimentResource.RelativePath,
                    repeatNumber <= 0
                        ? "Guided Experiment содержит неположительный RepeatNumber."
                        : $"Guided Experiment содержит RepeatNumber {repeatNumber} более одного раза."));
            }

            var dependencies = BuildDependencies(experiment);
            var expectedKeys = dependencies
                .Select(dependency => (dependency.RepeatNumber, dependency.Role))
                .ToHashSet();

            foreach (var binding in project.TraceBindings.Where(binding =>
                         binding.ExperimentResourceId == experimentResource.ResourceId))
            {
                if (expectedKeys.Contains((binding.RepeatNumber, binding.Role)))
                    continue;

                issues.Add(new ProjectIntegrityIssue(
                    ProjectIntegritySeverity.Error,
                    ProjectIntegrityCode.StaleBinding,
                    experimentResource.RelativePath,
                    $"Persistent binding repeat {binding.RepeatNumber} / {RoleText(binding.Role)} " +
                    "больше не соответствует зависимости в текущем *.canexperiment."));
            }

            foreach (var dependency in dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = ProjectTraceBindingService.GetBinding(
                    project,
                    experimentResource.ResourceId,
                    dependency.RepeatNumber,
                    dependency.Role);
                if (binding is not null)
                {
                    var trace = project.Resources.Single(resource =>
                        resource.ResourceId == binding.TraceResourceId);
                    var tracePath = CraneProjectCodec.ResolveResourcePath(projectPath, trace);
                    if (File.Exists(tracePath))
                    {
                        issues.Add(new ProjectIntegrityIssue(
                            ProjectIntegritySeverity.Information,
                            ProjectIntegrityCode.DependencyBound,
                            DependencySubject(
                                experimentResource,
                                dependency.RepeatNumber,
                                dependency.Role),
                            $"Persistent binding -> {trace.RelativePath}."));
                    }

                    continue;
                }

                issues.Add(ResolveUnboundDependency(
                    projectPath,
                    project,
                    experimentResource,
                    experimentPath,
                    dependency));
            }
        }

        if (issues.All(issue =>
                issue.Severity == ProjectIntegritySeverity.Information))
        {
            issues.Insert(0, new ProjectIntegrityIssue(
                ProjectIntegritySeverity.Information,
                ProjectIntegrityCode.Healthy,
                ".canproject",
                "Структура manifest, resources и TRC dependencies не содержат ошибок или предупреждений."));
        }

        return new ProjectIntegrityReport(issues);
    }

    private static ProjectIntegrityIssue ResolveUnboundDependency(
        string projectPath,
        CraneProject project,
        CraneProjectResource experimentResource,
        string experimentPath,
        TraceDependency dependency)
    {
        var traces = project.Resources
            .Where(resource => resource.Kind == CraneProjectResourceKind.Trace)
            .Select(resource => new TraceCandidate(
                resource,
                CraneProjectCodec.ResolveResourcePath(projectPath, resource)))
            .ToArray();

        var exact = FindExactMatches(
            projectPath,
            experimentPath,
            dependency.RequestedPath,
            traces);
        if (exact.Count == 1)
        {
            return CandidateResult(
                experimentResource,
                dependency,
                exact[0],
                "Однозначно разрешено через Trace resource проекта без persistent binding.");
        }
        if (exact.Count > 1)
        {
            return AmbiguousResult(
                experimentResource,
                dependency,
                exact);
        }

        var fileName = SafeFileName(dependency.RequestedPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var byName = traces.Where(candidate => string.Equals(
                    Path.GetFileName(candidate.FullPath),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (byName.Length == 1)
            {
                return CandidateResult(
                    experimentResource,
                    dependency,
                    byName[0],
                    "Однозначно разрешено по имени Trace resource без persistent binding.");
            }
            if (byName.Length > 1)
            {
                return AmbiguousResult(
                    experimentResource,
                    dependency,
                    byName);
            }
        }

        var direct = ResolveDirectExisting(experimentPath, dependency.RequestedPath);
        if (direct is not null)
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            var insideProject = IsInsideDirectory(projectDirectory, direct);
            return new ProjectIntegrityIssue(
                ProjectIntegritySeverity.Warning,
                insideProject
                    ? ProjectIntegrityCode.DependencyUnregistered
                    : ProjectIntegrityCode.DependencyOutsideProject,
                DependencySubject(
                    experimentResource,
                    dependency.RepeatNumber,
                    dependency.Role),
                insideProject
                    ? $"TRC существует, но не зарегистрирован как Trace resource: {Path.GetFileName(direct)}."
                    : "TRC существует только вне папки проекта. Зависимость не является переносимой.");
        }

        return new ProjectIntegrityIssue(
            ProjectIntegritySeverity.Error,
            ProjectIntegrityCode.DependencyMissing,
            DependencySubject(
                experimentResource,
                dependency.RepeatNumber,
                dependency.Role),
            $"TRC dependency не найдена: {dependency.RequestedPath}.");
    }

    private static ProjectIntegrityIssue CandidateResult(
        CraneProjectResource experimentResource,
        TraceDependency dependency,
        TraceCandidate candidate,
        string message)
    {
        if (!File.Exists(candidate.FullPath))
        {
            return new ProjectIntegrityIssue(
                ProjectIntegritySeverity.Error,
                ProjectIntegrityCode.DependencyMissing,
                DependencySubject(
                    experimentResource,
                    dependency.RepeatNumber,
                    dependency.Role),
                $"Подходящий Trace resource найден, но файл отсутствует: {candidate.Resource.RelativePath}.");
        }

        return new ProjectIntegrityIssue(
            ProjectIntegritySeverity.Information,
            ProjectIntegrityCode.DependencyResolvedAutomatically,
            DependencySubject(
                experimentResource,
                dependency.RepeatNumber,
                dependency.Role),
            $"{message} -> {candidate.Resource.RelativePath}.");
    }

    private static ProjectIntegrityIssue AmbiguousResult(
        CraneProjectResource experimentResource,
        TraceDependency dependency,
        IReadOnlyList<TraceCandidate> candidates) =>
        new(
            ProjectIntegritySeverity.Error,
            ProjectIntegrityCode.DependencyAmbiguous,
            DependencySubject(
                experimentResource,
                dependency.RepeatNumber,
                dependency.Role),
            "Найдено несколько подходящих Trace resources: " +
            string.Join(", ", candidates.Select(candidate => candidate.Resource.RelativePath)) +
            ". Задайте persistent binding.");

    private static IReadOnlyList<TraceCandidate> FindExactMatches(
        string projectPath,
        string experimentPath,
        string sourcePath,
        IReadOnlyList<TraceCandidate> traces)
    {
        var matches = new List<TraceCandidate>();
        string? rootedSource = null;
        try
        {
            if (Path.IsPathRooted(sourcePath))
                rootedSource = Path.GetFullPath(sourcePath);
        }
        catch
        {
            // Malformed legacy path may still be resolved by filename.
        }

        if (rootedSource is not null)
        {
            matches.AddRange(traces.Where(candidate =>
                PathsEqual(candidate.FullPath, rootedSource)));
            if (matches.Count > 0)
                return Distinct(matches);
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            var normalizedSource = NormalizeLooseRelative(sourcePath);
            if (!string.IsNullOrWhiteSpace(normalizedSource))
            {
                matches.AddRange(traces.Where(candidate => string.Equals(
                    NormalizeLooseRelative(candidate.Resource.RelativePath),
                    normalizedSource,
                    StringComparison.OrdinalIgnoreCase)));
                if (matches.Count > 0)
                    return Distinct(matches);
            }

            var experimentDirectory = Path.GetDirectoryName(experimentPath);
            if (!string.IsNullOrWhiteSpace(experimentDirectory))
            {
                try
                {
                    var fromExperiment = Path.GetFullPath(Path.Combine(
                        experimentDirectory,
                        sourcePath));
                    matches.AddRange(traces.Where(candidate =>
                        PathsEqual(candidate.FullPath, fromExperiment)));
                    if (matches.Count > 0)
                        return Distinct(matches);
                }
                catch
                {
                    // Keep checking other safe forms.
                }
            }

            try
            {
                var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
                if (!string.IsNullOrWhiteSpace(projectDirectory))
                {
                    var fromProject = Path.GetFullPath(Path.Combine(
                        projectDirectory,
                        sourcePath));
                    matches.AddRange(traces.Where(candidate =>
                        PathsEqual(candidate.FullPath, fromProject)));
                }
            }
            catch
            {
                // Malformed path is handled as missing below.
            }
        }

        return Distinct(matches);
    }

    private static string? ResolveDirectExisting(
        string experimentPath,
        string sourcePath)
    {
        try
        {
            if (Path.IsPathRooted(sourcePath))
            {
                var full = Path.GetFullPath(sourcePath);
                return File.Exists(full) ? full : null;
            }

            var experimentDirectory = Path.GetDirectoryName(experimentPath);
            if (!string.IsNullOrWhiteSpace(experimentDirectory))
            {
                var relative = Path.GetFullPath(Path.Combine(
                    experimentDirectory,
                    sourcePath));
                if (File.Exists(relative))
                    return relative;
            }

            if (File.Exists(sourcePath))
                return Path.GetFullPath(sourcePath);
        }
        catch
        {
            // Malformed path is reported as missing.
        }

        return null;
    }

    private static IReadOnlyList<TraceDependency> BuildDependencies(
        GuidedExperiment experiment)
    {
        var dependencies = new List<TraceDependency>();
        foreach (var repeat in experiment.Repeats)
        {
            dependencies.Add(new TraceDependency(
                repeat.RepeatNumber,
                ProjectTraceBindingRole.Reference,
                repeat.ReferenceSource.Path));
            dependencies.Add(new TraceDependency(
                repeat.RepeatNumber,
                ProjectTraceBindingRole.Action,
                repeat.ActionSource.Path));
            if (repeat.ReturnSource is not null)
            {
                dependencies.Add(new TraceDependency(
                    repeat.RepeatNumber,
                    ProjectTraceBindingRole.Return,
                    repeat.ReturnSource.Path));
            }
        }

        return dependencies;
    }

    private static IReadOnlyList<TraceCandidate> Distinct(
        IEnumerable<TraceCandidate> candidates) =>
        candidates
            .DistinctBy(candidate => candidate.Resource.ResourceId)
            .ToArray();

    private static bool IsInsideDirectory(string directory, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(directory, path);
            return !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.AltDirectorySeparatorChar,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeLooseRelative(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('.', '/');

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DependencySubject(
        CraneProjectResource experiment,
        int repeatNumber,
        ProjectTraceBindingRole role) =>
        $"{experiment.RelativePath} · repeat {repeatNumber} · {RoleText(role)}";

    private static string RoleText(ProjectTraceBindingRole role) => role switch
    {
        ProjectTraceBindingRole.Reference => "REFERENCE",
        ProjectTraceBindingRole.Action => "ACTION",
        ProjectTraceBindingRole.Return => "RETURN",
        _ => role.ToString().ToUpperInvariant()
    };

    private sealed record TraceCandidate(
        CraneProjectResource Resource,
        string FullPath);

    private sealed record TraceDependency(
        int RepeatNumber,
        ProjectTraceBindingRole Role,
        string RequestedPath);
}
