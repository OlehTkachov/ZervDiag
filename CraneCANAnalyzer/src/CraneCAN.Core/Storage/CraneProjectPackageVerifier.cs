using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public sealed record ProjectPackageVerificationFile(
    string EntryName,
    long RecordedLength,
    long ActualLength,
    string RecordedSha256,
    string ActualSha256,
    bool IsMatch);

public sealed record ProjectPackageVerificationResult(
    string ArchivePath,
    bool IsValid,
    Guid? ProjectId,
    string ProjectName,
    int ResourceCount,
    int FileCount,
    long UncompressedBytes,
    long ArchiveBytes,
    string ArchiveSha256,
    string PackageManifestSha256,
    IReadOnlyList<ProjectPackageVerificationFile> Files,
    IReadOnlyList<string> Errors)
{
    public string Status => IsValid ? "VALID" : "INVALID";
}

/// <summary>
/// Verifies a CraneCAN Project Package directly inside the ZIP. No project
/// payload is extracted or published to the filesystem. The verifier checks
/// safe ZIP paths, exact package shape, .canproject metadata, the internal
/// SHA-256 manifest, and hashes every payload entry while reading the archive.
/// </summary>
public static class CraneProjectPackageVerifier
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxFileEntries = 20_000;
    private const long MaxSingleEntryBytes = 128L * 1024 * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 256L * 1024 * 1024 * 1024;
    private const long MaxMetadataBytes = 16L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<ProjectPackageVerificationResult> VerifyZipAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
            throw new FileNotFoundException("CraneCAN ZIP package не найден.", fullArchivePath);

        var archiveFingerprint = await HashFileAsync(fullArchivePath, cancellationToken)
            .ConfigureAwait(false);

        var errors = new List<string>();
        var verifiedFiles = new List<ProjectPackageVerificationFile>();
        Guid? projectId = null;
        var projectName = string.Empty;
        var resourceCount = 0;
        var fileCount = 0;
        long totalUncompressedBytes = 0;
        var packageManifestSha256 = string.Empty;

        try
        {
            using var archive = ZipFile.OpenRead(fullArchivePath);
            var scan = ScanArchive(archive);
            fileCount = scan.Files.Count;
            totalUncompressedBytes = scan.TotalUncompressedBytes;

            var hashManifestEntry = scan.Files.Single(file => file.IsHashManifest).Entry;
            var hashManifestFingerprint = await HashEntryAsync(
                    hashManifestEntry,
                    cancellationToken)
                .ConfigureAwait(false);
            packageManifestSha256 = hashManifestFingerprint.Sha256;

            CraneProjectPackageHashManifest hashManifest;
            try
            {
                hashManifest = await DeserializeEntryAsync<CraneProjectPackageHashManifest>(
                        hashManifestEntry,
                        MaxMetadataBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                CraneProjectPackageHashManifestCodec.Validate(hashManifest);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add("Внутренний SHA-256 manifest не читается или некорректен: " + exception.Message);
                return Result(false);
            }

            var projectEntry = scan.Files.Single(file => file.IsProjectManifest).Entry;
            CraneProject project;
            try
            {
                project = await DeserializeEntryAsync<CraneProject>(
                        projectEntry,
                        MaxMetadataBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                CraneProjectCodec.Validate(project);
                projectId = project.ProjectId;
                projectName = project.Name;
                resourceCount = project.Resources.Count;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(".canproject не читается или не прошёл validation: " + exception.Message);
                return Result(false);
            }

            var expectedPayload = BuildExpectedPayload(project, projectEntry.FullName);
            var recordedHashes = BuildHashExpectations(hashManifest);
            ValidatePackageShape(scan.Files, project, expectedPayload, recordedHashes, hashManifest, errors);
            if (errors.Count > 0)
                return Result(false);

            foreach (var scanned in scan.Files
                         .Where(file => !file.IsHashManifest)
                         .OrderBy(file => file.NormalizedName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actual = await HashEntryAsync(scanned.Entry, cancellationToken)
                    .ConfigureAwait(false);
                var recorded = recordedHashes[scanned.NormalizedName];
                var matched = actual.Length == recorded.Length &&
                              string.Equals(
                                  actual.Sha256,
                                  recorded.Sha256,
                                  StringComparison.OrdinalIgnoreCase);

                verifiedFiles.Add(new ProjectPackageVerificationFile(
                    scanned.NormalizedName,
                    recorded.Length,
                    actual.Length,
                    recorded.Sha256,
                    actual.Sha256,
                    matched));

                if (!matched)
                {
                    errors.Add(
                        $"SHA-256/size mismatch: {scanned.NormalizedName}. " +
                        $"Recorded {recorded.Length} bytes / {recorded.Sha256}; " +
                        $"actual {actual.Length} bytes / {actual.Sha256}.");
                }
            }

            return Result(errors.Count == 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(exception.Message);
            return Result(false);
        }

        ProjectPackageVerificationResult Result(bool valid) => new(
            fullArchivePath,
            valid,
            projectId,
            projectName,
            resourceCount,
            fileCount,
            totalUncompressedBytes,
            archiveFingerprint.Length,
            archiveFingerprint.Sha256,
            packageManifestSha256,
            verifiedFiles
                .OrderBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            errors.ToArray());
    }

    private static ArchiveScan ScanArchive(ZipArchive archive)
    {
        var files = new List<ScannedEntry>();
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            if (IsUnixSymlink(entry))
                throw new InvalidDataException($"ZIP package содержит symbolic link, что запрещено: {entry.FullName}.");

            var isDirectory = string.IsNullOrEmpty(entry.Name);
            var normalizedName = NormalizeSafeEntryName(entry.FullName, allowDirectory: isDirectory);
            if (isDirectory)
            {
                if (fileNames.Contains(normalizedName))
                    throw new InvalidDataException($"ZIP package использует один путь одновременно как файл и каталог: {normalizedName}.");
                if (!directoryNames.Add(normalizedName))
                    throw new InvalidDataException($"ZIP package содержит повторяющийся каталог: {normalizedName}.");
                continue;
            }

            if (files.Count >= MaxFileEntries)
                throw new InvalidDataException($"ZIP package содержит более {MaxFileEntries} файлов.");
            if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
                throw new InvalidDataException($"Недопустимый размер ZIP entry: {normalizedName}.");
            if ((IsHashManifest(normalizedName) || IsRootProjectManifest(normalizedName)) &&
                entry.Length > MaxMetadataBytes)
            {
                throw new InvalidDataException($"Metadata entry превышает безопасный лимит 16 MiB: {normalizedName}.");
            }

            checked { totalUncompressedBytes += entry.Length; }
            if (totalUncompressedBytes > MaxTotalUncompressedBytes)
                throw new InvalidDataException("Суммарный распакованный объём ZIP package превышает безопасный лимит 256 GiB.");

            if (!fileNames.Add(normalizedName))
                throw new InvalidDataException($"ZIP package содержит повторяющийся файл: {normalizedName}.");
            if (directoryNames.Contains(normalizedName))
                throw new InvalidDataException($"ZIP package использует один путь одновременно как файл и каталог: {normalizedName}.");

            files.Add(new ScannedEntry(
                entry,
                normalizedName,
                IsRootProjectManifest(normalizedName),
                IsHashManifest(normalizedName)));
        }

        if (files.Count == 0)
            throw new InvalidDataException("ZIP package не содержит файлов.");

        ValidateFileDirectoryCollisions(fileNames);

        var allProjects = files
            .Where(file => string.Equals(
                Path.GetExtension(file.NormalizedName),
                ".canproject",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rootProjects = allProjects.Where(file => file.IsProjectManifest).ToArray();
        if (allProjects.Length != 1 || rootProjects.Length != 1)
            throw new InvalidDataException("CraneCAN ZIP package должен содержать ровно один *.canproject в корне архива.");

        var hashManifests = files.Where(file => file.IsHashManifest).ToArray();
        if (hashManifests.Length != 1)
        {
            throw new InvalidDataException(
                $"CraneCAN ZIP package должен содержать ровно один {CraneProjectPackageHashManifestCodec.EntryName} в корне архива. " +
                "Legacy package без SHA-256 manifest нужно заново экспортировать текущей версией CraneCAN.");
        }

        return new ArchiveScan(files, totalUncompressedBytes);
    }

    private static Dictionary<string, CraneProjectResourceKind> BuildExpectedPayload(
        CraneProject project,
        string projectEntryName)
    {
        var projectName = NormalizeSafeEntryName(projectEntryName);
        var expected = new Dictionary<string, CraneProjectResourceKind>(StringComparer.OrdinalIgnoreCase)
        {
            [projectName] = CraneProjectResourceKind.Other
        };

        foreach (var resource in project.Resources)
        {
            var entryName = NormalizeSafeEntryName(resource.RelativePath);
            if (string.Equals(entryName, CraneProjectPackageHashManifestCodec.EntryName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Project resource использует зарезервированное имя {CraneProjectPackageHashManifestCodec.EntryName}.");
            if (!expected.TryAdd(entryName, resource.Kind))
                throw new InvalidDataException($"Manifest повторно использует package path: {entryName}.");
        }

        return expected;
    }

    private static Dictionary<string, ProjectPackageFileFingerprint> BuildHashExpectations(
        CraneProjectPackageHashManifest manifest)
    {
        var expected = new Dictionary<string, ProjectPackageFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalized = CraneProjectPackageHashManifestCodec.NormalizeFingerprint(file);
            var safeName = NormalizeSafeEntryName(normalized.EntryName);
            if (!string.Equals(safeName, normalized.EntryName, StringComparison.Ordinal))
                throw new InvalidDataException($"Package SHA-256 manifest содержит неканонический path: {normalized.EntryName}.");
            if (!expected.TryAdd(safeName, normalized))
                throw new InvalidDataException($"Package SHA-256 manifest повторяет path: {safeName}.");
        }
        return expected;
    }

    private static void ValidatePackageShape(
        IReadOnlyList<ScannedEntry> files,
        CraneProject project,
        IReadOnlyDictionary<string, CraneProjectResourceKind> expectedPayload,
        IReadOnlyDictionary<string, ProjectPackageFileFingerprint> recordedHashes,
        CraneProjectPackageHashManifest hashManifest,
        List<string> errors)
    {
        var projectFileName = expectedPayload.Keys.Single(name =>
            name.EndsWith(".canproject", StringComparison.OrdinalIgnoreCase));

        if (hashManifest.ProjectId != project.ProjectId)
            errors.Add("Project ID во внутреннем SHA-256 manifest не совпадает с .canproject.");
        if (!string.Equals(hashManifest.ProjectFileName, projectFileName, StringComparison.OrdinalIgnoreCase))
            errors.Add("Имя .canproject во внутреннем SHA-256 manifest не совпадает с архивом.");

        var actualPayload = files
            .Where(file => !file.IsHashManifest)
            .Select(file => file.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in expectedPayload.Keys.Where(name => !actualPayload.Contains(name)))
            errors.Add("ZIP package не содержит зарегистрированный payload: " + name + ".");
        foreach (var name in actualPayload.Where(name => !expectedPayload.ContainsKey(name)))
            errors.Add("ZIP package содержит незарегистрированный payload: " + name + ".");
        foreach (var name in expectedPayload.Keys.Where(name => !recordedHashes.ContainsKey(name)))
            errors.Add("Внутренний SHA-256 manifest не содержит payload: " + name + ".");
        foreach (var name in recordedHashes.Keys.Where(name => !expectedPayload.ContainsKey(name)))
            errors.Add("Внутренний SHA-256 manifest содержит лишний payload: " + name + ".");
    }

    private static async Task<T> DeserializeEntryAsync<T>(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"Metadata entry слишком велик: {entry.FullName}.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException($"ZIP entry пуст или повреждён: {entry.FullName}.");
    }

    private static async Task<ComputedFingerprint> HashEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            checked { length += read; }
            if (length > entry.Length || length > MaxSingleEntryBytes)
                throw new InvalidDataException($"ZIP entry распаковал больше данных, чем объявлено: {entry.FullName}.");
            hash.AppendData(buffer, 0, read);
        }
        if (length != entry.Length)
            throw new InvalidDataException($"Размер ZIP entry не совпадает с metadata: {entry.FullName}.");
        return new ComputedFingerprint(
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<ComputedFingerprint> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }
        return new ComputedFingerprint(
            stream.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string NormalizeSafeEntryName(string path, bool allowDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("ZIP package содержит пустой entry path.");

        var value = path.Trim().Replace('\\', '/');
        if (allowDirectory)
            value = value.TrimEnd('/');

        if (value.Length == 0 ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'))
        {
            throw new InvalidDataException($"ZIP entry path должен быть относительным: {path}.");
        }

        var segments = value.Split('/');
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"ZIP entry path содержит пустой сегмент: {path}.");

        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.IndexOf('\0') >= 0 ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.Any(character => character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            {
                throw new InvalidDataException($"ZIP entry path содержит недопустимый Windows-сегмент: {path}.");
            }

            var deviceName = segment.Split('.')[0];
            if (IsReservedWindowsDeviceName(deviceName))
                throw new InvalidDataException($"ZIP entry использует зарезервированное Windows-имя: {path}.");
        }

        return string.Join('/', segments);
    }

    private static bool IsReservedWindowsDeviceName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Length == 4 &&
               (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               value[3] is >= '1' and <= '9';
    }

    private static bool IsRootProjectManifest(string normalizedName) =>
        !normalizedName.Contains('/') &&
        string.Equals(Path.GetExtension(normalizedName), ".canproject", StringComparison.OrdinalIgnoreCase);

    private static bool IsHashManifest(string normalizedName) =>
        !normalizedName.Contains('/') &&
        string.Equals(normalizedName, CraneProjectPackageHashManifestCodec.EntryName, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000;
    }

    private static void ValidateFileDirectoryCollisions(IReadOnlySet<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var slash = fileName.IndexOf('/');
            while (slash >= 0)
            {
                var parent = fileName[..slash];
                if (fileNames.Contains(parent))
                    throw new InvalidDataException($"ZIP package использует путь одновременно как файл и каталог: {parent}.");
                slash = fileName.IndexOf('/', slash + 1);
            }
        }
    }

    private sealed record ComputedFingerprint(long Length, string Sha256);
    private sealed record ScannedEntry(
        ZipArchiveEntry Entry,
        string NormalizedName,
        bool IsProjectManifest,
        bool IsHashManifest);
    private sealed record ArchiveScan(
        IReadOnlyList<ScannedEntry> Files,
        long TotalUncompressedBytes);
}
