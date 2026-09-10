using System.Text.Json;
using System.Text.Json.Serialization;
using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public sealed record CraneProjectResourceUpdate(
    CraneProject Project,
    CraneProjectResource Resource,
    bool ExistingResourceUpdated);

public static class CraneProjectCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static CraneProject Create(
        string? projectName = null,
        string? machineName = null,
        string? manufacturer = null,
        string? model = null,
        string? canBusName = null,
        int? bitrate = null) =>
        new()
        {
            Name = Clean(projectName, "Новый проект CraneCAN"),
            MachineName = Clean(machineName, "Новая / неизвестная машина"),
            Manufacturer = manufacturer?.Trim() ?? string.Empty,
            Model = model?.Trim() ?? string.Empty,
            CanBusName = Clean(canBusName, "CAN1"),
            Bitrate = bitrate
        };

    public static CraneProjectResourceUpdate AddResource(
        CraneProject project,
        string projectPath,
        string resourcePath,
        CraneProjectResourceKind? kind = null,
        string? label = null,
        string? role = null,
        bool setActive = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);

        var relativePath = MakeRelativePath(projectPath, resourcePath);
        var resources = project.Resources.ToList();
        var existing = resources.FirstOrDefault(item =>
            string.Equals(
                NormalizeRelativePath(item.RelativePath),
                relativePath,
                StringComparison.OrdinalIgnoreCase));

        var resolvedKind = kind ?? InferKind(resourcePath);
        CraneProjectResource resource;
        var existingUpdated = existing is not null;
        if (existing is null)
        {
            resource = new CraneProjectResource
            {
                Kind = resolvedKind,
                RelativePath = relativePath,
                Label = Clean(label, Path.GetFileName(resourcePath)),
                Role = role?.Trim() ?? string.Empty
            };
            resources.Add(resource);
        }
        else
        {
            resource = existing with
            {
                Kind = resolvedKind,
                RelativePath = relativePath,
                Label = string.IsNullOrWhiteSpace(label) ? existing.Label : label.Trim(),
                Role = string.IsNullOrWhiteSpace(role) ? existing.Role : role.Trim()
            };
            resources[resources.FindIndex(item => item.ResourceId == existing.ResourceId)] = resource;
        }

        var activeProfile = project.ActiveProfileResourceId;
        var activeExperiment = project.ActiveExperimentResourceId;
        if (setActive && resolvedKind == CraneProjectResourceKind.MachineProfile)
            activeProfile = resource.ResourceId;
        if (setActive && resolvedKind == CraneProjectResourceKind.GuidedExperiment)
            activeExperiment = resource.ResourceId;

        var updated = project with
        {
            ProgramVersion = "0.7.0",
            Resources = resources,
            ActiveProfileResourceId = activeProfile,
            ActiveExperimentResourceId = activeExperiment,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Validate(updated);
        return new CraneProjectResourceUpdate(updated, resource, existingUpdated);
    }

    public static CraneProject RemoveResource(
        CraneProject project,
        Guid resourceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        if (resourceId == Guid.Empty)
            throw new ArgumentException("resourceId не должен быть пустым.", nameof(resourceId));

        var resources = project.Resources
            .Where(item => item.ResourceId != resourceId)
            .ToList();
        if (resources.Count == project.Resources.Count)
            return project;

        var updated = project with
        {
            Resources = resources,
            ActiveProfileResourceId =
                project.ActiveProfileResourceId == resourceId
                    ? null
                    : project.ActiveProfileResourceId,
            ActiveExperimentResourceId =
                project.ActiveExperimentResourceId == resourceId
                    ? null
                    : project.ActiveExperimentResourceId,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Validate(updated);
        return updated;
    }

    public static string ResolveResourcePath(
        string projectPath,
        CraneProjectResource resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(resource);

        var relative = NormalizeRelativePath(resource.RelativePath);
        var directory = ProjectDirectory(projectPath);
        var full = Path.GetFullPath(Path.Combine(
            directory,
            relative.Replace('/', Path.DirectorySeparatorChar)));

        EnsureInsideProject(directory, full);
        return full;
    }

    public static IReadOnlyList<CraneProjectResource> GetMissingResources(
        string projectPath,
        CraneProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);

        return project.Resources
            .Where(resource => !File.Exists(ResolveResourcePath(projectPath, resource)))
            .ToArray();
    }

    public static CraneProjectResourceKind InferKind(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".craneprofile" => CraneProjectResourceKind.MachineProfile,
            ".canexperiment" => CraneProjectResourceKind.GuidedExperiment,
            ".canincident" => CraneProjectResourceKind.Incident,
            ".cansnapshot" => CraneProjectResourceKind.CanSnapshot,
            ".trc" or ".csv" => CraneProjectResourceKind.Trace,
            ".md" or ".txt" => CraneProjectResourceKind.Report,
            ".pdf" or ".docx" or ".xlsx" => CraneProjectResourceKind.Document,
            _ => CraneProjectResourceKind.Other
        };
    }

    public static async Task<CraneProject> SaveAsync(
        string path,
        CraneProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        var normalized = project with
        {
            ProgramVersion = "0.7.0",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Validate(normalized);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Не удалось определить каталог .canproject.");
        Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await JsonSerializer.SerializeAsync(
            stream,
            normalized,
            Options,
            cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public static async Task<CraneProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл CraneCAN project не найден.", fullPath);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var project = await JsonSerializer.DeserializeAsync<CraneProject>(
                stream,
                Options,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Пустой или некорректный файл .canproject.");

        Validate(project);
        foreach (var resource in project.Resources)
            _ = ResolveResourcePath(fullPath, resource);

        return project;
    }

    public static void Validate(CraneProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.SchemaVersion != CraneProject.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Schema .canproject {project.SchemaVersion} не поддерживается.");
        if (project.ProjectId == Guid.Empty)
            throw new FormatException("В .canproject отсутствует projectId.");
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new FormatException("В .canproject отсутствует название проекта.");
        if (project.Bitrate is <= 0)
            throw new FormatException("Bitrate проекта должен быть положительным.");
        if (project.Resources is null)
            throw new FormatException("В .canproject отсутствует список resources.");

        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in project.Resources)
        {
            if (resource.ResourceId == Guid.Empty)
                throw new FormatException("Resource содержит пустой resourceId.");
            if (!ids.Add(resource.ResourceId))
                throw new FormatException(
                    $"Resource ID повторяется: {resource.ResourceId:N}.");

            var normalized = NormalizeRelativePath(resource.RelativePath);
            if (!paths.Add(normalized))
                throw new FormatException(
                    $"Путь resource повторяется: {normalized}.");
        }

        ValidateActive(
            project.ActiveProfileResourceId,
            CraneProjectResourceKind.MachineProfile,
            project.Resources,
            "activeProfileResourceId");
        ValidateActive(
            project.ActiveExperimentResourceId,
            CraneProjectResourceKind.GuidedExperiment,
            project.Resources,
            "activeExperimentResourceId");
    }

    private static void ValidateActive(
        Guid? resourceId,
        CraneProjectResourceKind expectedKind,
        IReadOnlyList<CraneProjectResource> resources,
        string fieldName)
    {
        if (!resourceId.HasValue)
            return;

        var resource = resources.SingleOrDefault(item => item.ResourceId == resourceId.Value)
            ?? throw new FormatException(
                $"{fieldName} указывает на отсутствующий resource.");
        if (resource.Kind != expectedKind)
            throw new FormatException(
                $"{fieldName} указывает на resource типа {resource.Kind}, ожидался {expectedKind}.");
    }

    private static string MakeRelativePath(
        string projectPath,
        string resourcePath)
    {
        var directory = ProjectDirectory(projectPath);
        var fullResourcePath = Path.GetFullPath(resourcePath);
        EnsureInsideProject(directory, fullResourcePath);

        var relative = Path.GetRelativePath(directory, fullResourcePath);
        return NormalizeRelativePath(relative);
    }

    private static string ProjectDirectory(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        return Path.GetDirectoryName(fullProjectPath)
            ?? throw new InvalidOperationException("Не удалось определить каталог .canproject.");
    }

    private static void EnsureInsideProject(
        string projectDirectory,
        string fullResourcePath)
    {
        var relative = Path.GetRelativePath(projectDirectory, fullResourcePath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resource находится вне папки проекта. " +
                "Для переносимого .canproject сначала поместите файл внутрь папки проекта.");
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim().Replace('\\', '/');

        if (Path.IsPathRooted(value) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'))
        {
            throw new FormatException(
                "Resource path в .canproject должен быть относительным.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOf('\0') >= 0))
        {
            throw new FormatException(
                "Resource path в .canproject содержит недопустимый сегмент.");
        }

        return string.Join('/', segments);
    }

    private static string Clean(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
