using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public enum ProjectTraceResolutionKind
{
    OriginalPath,
    ExperimentRelativePath,
    ProjectResource,
    Missing,
    AmbiguousProject,
    AmbiguousTrace
}

public sealed record ProjectTraceResolution(
    string RequestedPath,
    string? ResolvedPath,
    ProjectTraceResolutionKind Kind,
    string Message,
    string? ProjectPath = null,
    IReadOnlyList<string>? Candidates = null)
{
    public bool CanLoad =>
        !string.IsNullOrWhiteSpace(ResolvedPath) && File.Exists(ResolvedPath);

    public bool ReboundToProject => Kind == ProjectTraceResolutionKind.ProjectResource;
}

/// <summary>
/// Resolves TRC paths used by a loaded Guided Experiment without rewriting the
/// original .canexperiment. When the experiment belongs to a .canproject, an
/// internal Trace resource is preferred so the whole project folder can be moved
/// to another PC. Ambiguous matches are rejected rather than guessed.
/// </summary>
public static class ProjectTraceDependencyResolver
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

    public static ProjectTraceResolution Resolve(string sourcePath)
    {
        string? experimentPath;
        lock (Gate)
            experimentPath = _currentExperimentPath;

        return string.IsNullOrWhiteSpace(experimentPath)
            ? ResolveWithoutProjectContext(sourcePath)
            : Resolve(experimentPath, sourcePath);
    }

    public static ProjectTraceResolution Resolve(
        string experimentPath,
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullExperimentPath = Path.GetFullPath(experimentPath);
        var owningProjects = FindOwningProjects(fullExperimentPath);

        if (owningProjects.Count > 1)
        {
            return new ProjectTraceResolution(
                sourcePath,
                null,
                ProjectTraceResolutionKind.AmbiguousProject,
                "Эксперимент зарегистрирован сразу в нескольких .canproject. " +
                "CraneCAN не выбирает проект автоматически.",
                Candidates: owningProjects.Select(item => item.ProjectPath).ToArray());
        }

        if (owningProjects.Count == 1)
        {
            var projectResolution = ResolveFromProject(
                owningProjects[0],
                fullExperimentPath,
                sourcePath);
            if (projectResolution is not null)
                return projectResolution;
        }

        return ResolveDirect(fullExperimentPath, sourcePath);
    }

    private static ProjectTraceResolution ResolveWithoutProjectContext(
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (File.Exists(sourcePath))
        {
            return new ProjectTraceResolution(
                sourcePath,
                Path.GetFullPath(sourcePath),
                ProjectTraceResolutionKind.OriginalPath,
                "Используется исходный TRC путь; контекст .canproject не активен.");
        }

        return new ProjectTraceResolution(
            sourcePath,
            null,
            ProjectTraceResolutionKind.Missing,
            "TRC не найден, а контекст .canproject не активен.");
    }

    private static ProjectTraceResolution? ResolveFromProject(
        OwningProject owning,
        string experimentPath,
        string sourcePath)
    {
        var traces = owning.Project.Resources
            .Where(resource => resource.Kind == CraneProjectResourceKind.Trace)
            .Select(resource => new TraceCandidate(
                resource,
                CraneProjectCodec.ResolveResourcePath(
                    owning.ProjectPath,
                    resource)))
            .ToArray();

        if (traces.Length == 0)
            return null;

        var exact = FindExactMatches(
            owning.ProjectPath,
            experimentPath,
            sourcePath,
            traces);
        if (exact.Count == 1)
            return ProjectResourceResult(owning.ProjectPath, sourcePath, exact[0]);
        if (exact.Count > 1)
            return AmbiguousTraceResult(owning.ProjectPath, sourcePath, exact);

        var sourceFileName = SafeFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceFileName))
            return null;

        var byName = traces
            .Where(candidate => string.Equals(
                Path.GetFileName(candidate.FullPath),
                sourceFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (byName.Length == 1)
            return ProjectResourceResult(owning.ProjectPath, sourcePath, byName[0]);
        if (byName.Length > 1)
            return AmbiguousTraceResult(owning.ProjectPath, sourcePath, byName);

        return null;
    }

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
            // Malformed legacy source path falls through to safe filename matching.
        }

        if (rootedSource is not null)
        {
            matches.AddRange(traces.Where(candidate => PathsEqual(
                candidate.FullPath,
                rootedSource)));
            if (matches.Count > 0)
                return matches;
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
                    return matches;
            }

            var experimentDirectory = Path.GetDirectoryName(experimentPath);
            if (!string.IsNullOrWhiteSpace(experimentDirectory))
            {
                try
                {
                    var fromExperiment = Path.GetFullPath(Path.Combine(
                        experimentDirectory,
                        sourcePath));
                    matches.AddRange(traces.Where(candidate => PathsEqual(
                        candidate.FullPath,
                        fromExperiment)));
                    if (matches.Count > 0)
                        return matches;
                }
                catch
                {
                    // Keep resolution conservative; filename fallback may still work.
                }
            }

            try
            {
                var projectDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(projectPath));
                if (!string.IsNullOrWhiteSpace(projectDirectory))
                {
                    var fromProject = Path.GetFullPath(Path.Combine(
                        projectDirectory,
                        sourcePath));
                    matches.AddRange(traces.Where(candidate => PathsEqual(
                        candidate.FullPath,
                        fromProject)));
                }
            }
            catch
            {
                // Ignore malformed relative path here; caller will report it missing.
            }
        }

        return matches
            .DistinctBy(candidate => candidate.Resource.ResourceId)
            .ToArray();
    }

    private static ProjectTraceResolution ProjectResourceResult(
        string projectPath,
        string sourcePath,
        TraceCandidate candidate)
    {
        var exists = File.Exists(candidate.FullPath);
        return new ProjectTraceResolution(
            sourcePath,
            candidate.FullPath,
            ProjectTraceResolutionKind.ProjectResource,
            exists
                ? $"TRC перепривязан к resource проекта: {candidate.Resource.RelativePath}."
                : $"TRC resource проекта найден, но файл отсутствует: {candidate.Resource.RelativePath}.",
            projectPath,
            [candidate.Resource.RelativePath]);
    }

    private static ProjectTraceResolution AmbiguousTraceResult(
        string projectPath,
        string sourcePath,
        IReadOnlyList<TraceCandidate> candidates) =>
        new(
            sourcePath,
            null,
            ProjectTraceResolutionKind.AmbiguousTrace,
            "В .canproject найдено несколько TRC с подходящим именем/путём. " +
            "Автоматическое перепривязывание остановлено, чтобы не загрузить не ту трассу.",
            projectPath,
            candidates.Select(candidate => candidate.Resource.RelativePath).ToArray());

    private static ProjectTraceResolution ResolveDirect(
        string experimentPath,
        string sourcePath)
    {
        if (Path.IsPathRooted(sourcePath))
        {
            try
            {
                var full = Path.GetFullPath(sourcePath);
                if (File.Exists(full))
                {
                    return new ProjectTraceResolution(
                        sourcePath,
                        full,
                        ProjectTraceResolutionKind.OriginalPath,
                        "Используется существующий исходный TRC путь.");
                }
            }
            catch
            {
                // Report as missing below.
            }
        }
        else
        {
            var experimentDirectory = Path.GetDirectoryName(experimentPath);
            if (!string.IsNullOrWhiteSpace(experimentDirectory))
            {
                try
                {
                    var relative = Path.GetFullPath(Path.Combine(
                        experimentDirectory,
                        sourcePath));
                    if (File.Exists(relative))
                    {
                        return new ProjectTraceResolution(
                            sourcePath,
                            relative,
                            ProjectTraceResolutionKind.ExperimentRelativePath,
                            "TRC найден относительно каталога .canexperiment.");
                    }
                }
                catch
                {
                    // Report as missing below.
                }
            }

            if (File.Exists(sourcePath))
            {
                return new ProjectTraceResolution(
                    sourcePath,
                    Path.GetFullPath(sourcePath),
                    ProjectTraceResolutionKind.OriginalPath,
                    "Используется существующий исходный TRC путь.");
            }
        }

        return new ProjectTraceResolution(
            sourcePath,
            null,
            ProjectTraceResolutionKind.Missing,
            "TRC-зависимость эксперимента не найдена. " +
            "Если эксперимент входит в .canproject, добавьте нужный TRC как Trace resource внутри папки проекта.");
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
                    var ownsExperiment = project.Resources
                        .Where(resource =>
                            resource.Kind == CraneProjectResourceKind.GuidedExperiment)
                        .Any(resource => PathsEqual(
                            CraneProjectCodec.ResolveResourcePath(projectPath, resource),
                            experimentPath));
                    if (ownsExperiment)
                        matches.Add(new OwningProject(projectPath, project));
                }
                catch
                {
                    // An unrelated/broken manifest must not hijack experiment loading.
                }
            }

            current = current.Parent;
        }

        return matches;
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
        path.Trim()
            .Replace('\\', '/')
            .TrimStart('.', '/');

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
        CraneProject Project);

    private sealed record TraceCandidate(
        CraneProjectResource Resource,
        string FullPath);
}
