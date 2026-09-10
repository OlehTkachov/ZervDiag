using System.Text.Json;

namespace CraneCAN.Core.Storage;

public sealed record CraneProjectPackageTrustedFingerprint
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentFormat = "CraneCAN.ProjectPackageFingerprint";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Format { get; init; } = CurrentFormat;
    public Guid ProjectId { get; init; }
    public string ProjectFileName { get; init; } = string.Empty;
    public string ArchiveFileName { get; init; } = string.Empty;
    public long ArchiveBytes { get; init; }
    public string ArchiveSha256 { get; init; } = string.Empty;
    public string PackageManifestSha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ProjectPackageTrustedFingerprintComparison(
    string FingerprintPath,
    bool IsMatch,
    IReadOnlyList<string> Errors)
{
    public string Status => IsMatch ? "MATCH" : "MISMATCH";
}

/// <summary>
/// Stores a small fingerprint outside the ZIP. When this file is obtained
/// through an independent trusted channel, matching it against the ZIP
/// protects against coordinated replacement of both package payload and the
/// internal package manifest. This is integrity evidence, not a digital
/// signature and not proof of authorship.
/// </summary>
public static class CraneProjectPackageTrustedFingerprintCodec
{
    public const string SidecarSuffix = ".cranefingerprint.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetSidecarPath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return Path.GetFullPath(archivePath) + SidecarSuffix;
    }

    public static CraneProjectPackageTrustedFingerprint Create(
        string archivePath,
        Guid projectId,
        string projectFileName,
        long archiveBytes,
        string archiveSha256,
        string packageManifestSha256,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fingerprint = new CraneProjectPackageTrustedFingerprint
        {
            ProjectId = projectId,
            ProjectFileName = FileNameOnly(projectFileName, ".canproject"),
            ArchiveFileName = FileNameOnly(Path.GetFileName(archivePath), ".zip"),
            ArchiveBytes = archiveBytes,
            ArchiveSha256 = NormalizeSha256(archiveSha256, "archiveSha256"),
            PackageManifestSha256 = NormalizeSha256(
                packageManifestSha256,
                "packageManifestSha256"),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
        Validate(fingerprint);
        return fingerprint;
    }

    public static void Save(
        string path,
        CraneProjectPackageTrustedFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fingerprint);
        Validate(fingerprint);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "Не удалось определить каталог внешнего fingerprint.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, fingerprint, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of our private temporary file only.
            }
        }
    }

    public static CraneProjectPackageTrustedFingerprint Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "Внешний CraneCAN package fingerprint не найден.",
                fullPath);

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var fingerprint =
            JsonSerializer.Deserialize<CraneProjectPackageTrustedFingerprint>(
                stream,
                Options)
            ?? throw new InvalidDataException(
                "Внешний CraneCAN package fingerprint пуст или повреждён.");
        Validate(fingerprint);
        return fingerprint;
    }

    public static ProjectPackageTrustedFingerprintComparison Compare(
        string fingerprintPath,
        CraneProjectPackageTrustedFingerprint fingerprint,
        ProjectPackageVerificationResult verification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintPath);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(verification);
        Validate(fingerprint);

        var errors = new List<string>();

        if (!verification.IsValid)
        {
            errors.Add(
                "ZIP не прошёл внутреннюю Project Package Verify; внешний fingerprint нельзя считать подтверждённым.");
        }

        if (!verification.ProjectId.HasValue)
        {
            errors.Add("Project ID не удалось прочитать из ZIP.");
        }
        else if (fingerprint.ProjectId != verification.ProjectId.Value)
        {
            errors.Add(
                $"Project ID mismatch: fingerprint {fingerprint.ProjectId:D}; ZIP {verification.ProjectId.Value:D}.");
        }

        if (fingerprint.ArchiveBytes != verification.ArchiveBytes)
        {
            errors.Add(
                $"ZIP size mismatch: fingerprint {fingerprint.ArchiveBytes}; ZIP {verification.ArchiveBytes} bytes.");
        }

        if (!string.Equals(
                fingerprint.ArchiveSha256,
                verification.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"ZIP SHA-256 mismatch: fingerprint {fingerprint.ArchiveSha256}; ZIP {verification.ArchiveSha256}.");
        }

        if (string.IsNullOrWhiteSpace(verification.PackageManifestSha256))
        {
            errors.Add("Внутренний package manifest SHA-256 не удалось вычислить.");
        }
        else if (!string.Equals(
                     fingerprint.PackageManifestSha256,
                     verification.PackageManifestSha256,
                     StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Internal manifest SHA-256 mismatch: fingerprint {fingerprint.PackageManifestSha256}; ZIP {verification.PackageManifestSha256}.");
        }

        var actualProjectFileName = verification.Files
            .Select(file => file.EntryName)
            .SingleOrDefault(name =>
                !name.Contains('/') &&
                name.EndsWith(".canproject", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(actualProjectFileName) &&
            !string.Equals(
                fingerprint.ProjectFileName,
                actualProjectFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Project filename mismatch: fingerprint {fingerprint.ProjectFileName}; ZIP {actualProjectFileName}.");
        }

        return new ProjectPackageTrustedFingerprintComparison(
            Path.GetFullPath(fingerprintPath),
            errors.Count == 0,
            errors);
    }

    public static void Validate(
        CraneProjectPackageTrustedFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        if (fingerprint.SchemaVersion !=
            CraneProjectPackageTrustedFingerprint.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Schema внешнего fingerprint {fingerprint.SchemaVersion} не поддерживается.");
        }

        if (!string.Equals(
                fingerprint.Format,
                CraneProjectPackageTrustedFingerprint.CurrentFormat,
                StringComparison.Ordinal))
        {
            throw new FormatException(
                "Неизвестный формат внешнего CraneCAN package fingerprint.");
        }

        if (fingerprint.ProjectId == Guid.Empty)
            throw new FormatException(
                "Внешний fingerprint не содержит projectId.");

        _ = FileNameOnly(fingerprint.ProjectFileName, ".canproject");
        _ = FileNameOnly(fingerprint.ArchiveFileName, ".zip");

        if (fingerprint.ArchiveBytes < 0)
            throw new FormatException(
                "Внешний fingerprint содержит отрицательный размер ZIP.");

        _ = NormalizeSha256(fingerprint.ArchiveSha256, "archiveSha256");
        _ = NormalizeSha256(
            fingerprint.PackageManifestSha256,
            "packageManifestSha256");

        if (fingerprint.CreatedAt == default)
            throw new FormatException(
                "Внешний fingerprint не содержит createdAt.");
    }

    private static string FileNameOnly(string value, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (!string.Equals(
                Path.GetFileName(trimmed),
                trimmed,
                StringComparison.Ordinal) ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new FormatException(
                "Внешний fingerprint должен содержать только имя файла без пути.");
        }

        if (!trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Ожидалось имя файла с расширением {extension}.");
        }

        return trimmed;
    }

    private static string NormalizeSha256(string value, string fieldName)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException(
                $"Некорректный SHA-256 в поле {fieldName}.");
        }

        return normalized;
    }
}
