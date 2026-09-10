using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraneCAN.Core.Storage;

public sealed record CraneProjectPackageHashManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentFormat = "CraneCAN.ProjectPackage";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Format { get; init; } = CurrentFormat;
    public string ProjectFileName { get; init; } = string.Empty;
    public Guid ProjectId { get; init; }
    public List<ProjectPackageFileFingerprint> Files { get; init; } = [];
}

/// <summary>
/// Serializes and validates the internal SHA-256 manifest stored in every
/// verified CraneCAN project package. The manifest protects package contents
/// against accidental or undetected modification, but is not a digital
/// signature and therefore does not prove package authorship/authenticity.
/// </summary>
public static class CraneProjectPackageHashManifestCodec
{
    public const string EntryName = "CraneCAN.package-manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static CraneProjectPackageHashManifest Create(
        string projectFileName,
        Guid projectId,
        IEnumerable<ProjectPackageFileFingerprint> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFileName);
        ArgumentNullException.ThrowIfNull(files);

        var manifest = new CraneProjectPackageHashManifest
        {
            ProjectFileName = NormalizeEntryName(projectFileName),
            ProjectId = projectId,
            Files = files
                .Select(NormalizeFingerprint)
                .OrderBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        Validate(manifest);
        return manifest;
    }

    public static void Save(string path, CraneProjectPackageHashManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Не удалось определить каталог package SHA-256 manifest.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        JsonSerializer.Serialize(stream, manifest, Options);
    }

    public static CraneProjectPackageHashManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Package SHA-256 manifest не найден.", fullPath);

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var manifest = JsonSerializer.Deserialize<CraneProjectPackageHashManifest>(stream, Options)
            ?? throw new InvalidDataException("Package SHA-256 manifest пуст или повреждён.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(CraneProjectPackageHashManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != CraneProjectPackageHashManifest.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Schema package SHA-256 manifest {manifest.SchemaVersion} не поддерживается.");
        }
        if (!string.Equals(
                manifest.Format,
                CraneProjectPackageHashManifest.CurrentFormat,
                StringComparison.Ordinal))
        {
            throw new FormatException("Неизвестный формат package SHA-256 manifest.");
        }
        if (manifest.ProjectId == Guid.Empty)
            throw new FormatException("Package SHA-256 manifest не содержит projectId.");
        if (manifest.Files is null || manifest.Files.Count == 0)
            throw new FormatException("Package SHA-256 manifest не содержит fingerprints файлов.");

        var projectFileName = NormalizeEntryName(manifest.ProjectFileName);
        if (projectFileName.Contains('/'))
            throw new FormatException("Project file в package SHA-256 manifest должен находиться в корне ZIP.");
        if (!projectFileName.EndsWith(".canproject", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Project file в package SHA-256 manifest должен иметь расширение .canproject.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalized = NormalizeFingerprint(file);
            if (!names.Add(normalized.EntryName))
            {
                throw new FormatException(
                    $"Package SHA-256 manifest содержит повторяющийся path: {normalized.EntryName}.");
            }
            if (string.Equals(normalized.EntryName, EntryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    "Package SHA-256 manifest не должен включать собственный fingerprint.");
            }
        }

        if (!names.Contains(projectFileName))
        {
            throw new FormatException(
                "Package SHA-256 manifest не содержит fingerprint корневого .canproject.");
        }
    }

    public static ProjectPackageFileFingerprint NormalizeFingerprint(
        ProjectPackageFileFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length < 0)
            throw new FormatException("Package SHA-256 manifest содержит отрицательный размер файла.");

        var entryName = NormalizeEntryName(fingerprint.EntryName);
        var sha256 = fingerprint.Sha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException(
                $"Некорректный SHA-256 для package entry: {entryName}.");
        }

        return fingerprint with
        {
            EntryName = entryName,
            Sha256 = sha256
        };
    }

    public static string NormalizeEntryName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim().Replace('\\', '/');
        if (value.StartsWith('/', StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'))
        {
            throw new FormatException("Package SHA-256 manifest содержит абсолютный path.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                string.IsNullOrWhiteSpace(segment) ||
                segment.IndexOf('\0') >= 0 ||
                segment.Contains(':')))
        {
            throw new FormatException("Package SHA-256 manifest содержит недопустимый path.");
        }

        return string.Join('/', segments);
    }
}
