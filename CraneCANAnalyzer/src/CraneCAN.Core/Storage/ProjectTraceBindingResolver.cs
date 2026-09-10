using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

/// <summary>
/// Adds an explicit persistent binding layer on top of
/// ProjectTraceDependencyResolver. The binding is authoritative: when a saved
/// binding exists, CraneCAN never falls back to another same-name Trace.
/// </summary>
public static class ProjectTraceBindingResolver
{
    private static readonly object Gate = new();
    private static string? _currentExperimentPath;

    public static void SetExperimentContext(string experimentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentPath);
        var fullPath = Path.GetFullPath(experimentPath);
        lock (Gate)
            _currentExperimentPath = fullPath;
    }

    public static void ClearExperimentContext()
    {
        lock (Gate)
            _currentExperimentPath = null;
    }

    public static ProjectTraceResolution Resolve(
        int repeatNumber,
        ProjectTraceBindingRole role,
        string sourcePath)
    {
        string? experimentPath;
        lock (Gate)
            experimentPath = _currentExperimentPath;

        if (string.IsNullOrWhiteSpace(experimentPath))
            return ProjectTraceDependencyResolver.Resolve(sourcePath);

        return Resolve(experimentPath, repeatNumber, role, sourcePath);
    }

    public static ProjectTraceResolution Resolve(
        string experimentPath,
        int repeatNumber,
        ProjectTraceBindingRole role,
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (repeatNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(repeatNumber));
        if (!Enum.IsDefined(typeof(ProjectTraceBindingRole), role))
            throw new ArgumentOutOfRangeException(nameof(role));

        var fullExperimentPath = Path.GetFullPath(experimentPath);
        var owning = FindOwningProjects(fullExperimentPath);

        if (owning.Count > 1)
        {
            return new ProjectTraceResolution(
                sourcePath,
                null,
                ProjectTraceResolutionKind.AmbiguousProject,
                "Эксперимент зарегистрирован сразу в нескольких .canproject. " +
                "Persistent TRC binding не может быть выбран автоматически.",
                Candidates: owning.Select(item => item.ProjectPath).ToArray());
        }

        if (owning.Count == 1)
        {
            var owner = owning[0];
            var binding = ProjectTraceBindingService.GetBinding(
                owner.Project,
                owner.ExperimentResource.ResourceId,
                repeatNumber,
                role);

            if (binding is not null)
            {
                var traceResource = owner.Project.Resources.FirstOrDefault(resource =>
                    resource.ResourceId == binding.TraceResourceId);
                if (traceResource is null)
                {
                    return new ProjectTraceResolution(
                        sourcePath,
                        null,
                        ProjectTraceResolutionKind.ProjectResource,
                        $"Сохранённая TRC привязка repeat {repeatNumber} / {role} указывает на удалённый resource. " +
                        "Очистите или задайте привязку заново.",
                        owner.ProjectPath);
                }

                if (traceResource.Kind != CraneProjectResourceKind.Trace)
                {
                    return new ProjectTraceResolution(
                        sourcePath,
                        null,
                        ProjectTraceResolutionKind.ProjectResource,
                        $"Сохранённая TRC привязка repeat {repeatNumber} / {role} указывает на resource типа " +
                        $"{traceResource.Kind}, а не Trace.",
                        owner.ProjectPath,
                        [traceResource.RelativePath]);
                }

                var resolvedPath = CraneProjectCodec.ResolveResourcePath(
                    owner.ProjectPath,
                    traceResource);
                return new ProjectTraceResolution(
                    sourcePath,
                    resolvedPath,
                    ProjectTraceResolutionKind.ProjectResource,
                    File.Exists(resolvedPath)
                        ? $"Использована сохранённая TRC привязка repeat {repeatNumber} / {role}: " +
                          $"{traceResource.RelativePath}."
                        : $"Сохранённая TRC привязка repeat {repeatNumber} / {role} найдена, " +
                          $"но файл отсутствует: {traceResource.RelativePath}.",
                    owner.ProjectPath,
                    [traceResource.RelativePath]);
            }
        }

        return ProjectTraceDependencyResolver.Resolve(fullExperimentPath, sourcePath);
    }

    private static IReadOnlyList<OwningProject> FindOwningProjects(
        string experimentPath)
    {
        var directory = Path.GetDirectoryName(experimentPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        var matches = new List<OwningProject>();
        var current = new DirectoryInfo(directory);
        var depth = 0;
        while (current is not null && depth++ < 32)
        {
            IEnumerable<string> projectFiles;
            try
            {
                projectFiles = Directory.EnumerateFiles(
                    current.FullName,
                    "*.canproject",
                    SearchOption.TopDirectoryOnly);
            }
            catch
            {
                current = current.Parent;
                continue;
            }

            foreach (var projectPath in projectFiles.OrderBy(
                         path => path,
                         StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var project = CraneProjectCodec.Load(projectPath);
                    var experimentResource = project.Resources
                        .Where(resource =>
                            resource.Kind == CraneProjectResourceKind.GuidedExperiment)
                        .FirstOrDefault(resource => PathsEqual(
                            CraneProjectCodec.ResolveResourcePath(projectPath, resource),
                            experimentPath));
                    if (experimentResource is not null)
                    {
                        matches.Add(new OwningProject(
                            projectPath,
                            project,
                            experimentResource));
                    }
                }
                catch
                {
                    // Broken/unrelated manifests must not hijack dependency resolution.
                }
            }

            current = current.Parent;
        }

        return matches;
    }

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

    private sealed record OwningProject(
        string ProjectPath,
        CraneProject Project,
        CraneProjectResource ExperimentResource);
}
