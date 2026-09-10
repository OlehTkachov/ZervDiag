using System.IO.Compression;
using System.Security.Cryptography;
using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public sealed record ProjectPackageImportResult(
    string ArchivePath,
    string DestinationDirectory,
    string ProjectPath,
    CraneProject Project,
    int ResourceCount,
    int FileCount,
    long UncompressedBytes,
    long ArchiveBytes,
    string ArchiveSha256,
    string PackageManifestSha256,
    IReadOnlyList<ProjectPackageFileFingerprint> Files,
    ProjectIntegrityReport PackageIntegrity);

/// <summary>
/// Safely imports a CraneCAN ZIP package into a new directory. The importer
/// rejects path traversal, Windows alternate-data-stream syntax, duplicate
/// normalized paths, symlinks/special entries, unexpected files, incomplete
/// manifests and any destination that already exists. Every payload file is
/// checked against the internal SHA-256 package manifest before publication.
/// Existing user files are never overwritten.
/// </summary>
public static class CraneProjectPackageImporter
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxFileEntries = 20_000;
    private const long MaxSingleEntryBytes = 128L * 1024 * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 256L * 1024 * 1024 * 1024;
    private const long MaxHashManifestBytes = 16L * 1024 * 1024;

    public static async Task<ProjectPackageImportResult> ImportZipAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException(
                "CraneCAN ZIP package не найден.",
                fullArchivePath);
        }

        var finalDestination = Path.GetFullPath(destinationDirectory);
        EnsureDestinationDoesNotExist(finalDestination);

        var parentDirectory = Path.GetDirectoryName(finalDestination)
            ?? throw new InvalidOperationException(
                "Не удалось определить родительский каталог для импорта package.");
        Directory.CreateDirectory(parentDirectory);

        var archiveFingerprint = await HashFileAsync(
                fullArchivePath,
                cancellationToken)
            .ConfigureAwait(false);

        var stagingRoot = Path.Combine(
            parentDirectory,
            $".cranecan-import-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingRoot);

        var published = false;
        try
        {
            using var archive = ZipFile.OpenRead(fullArchivePath);
            var scan = ScanArchive(archive);
            EnsureEnoughFreeSpace(parentDirectory, scan.TotalUncompressedBytes);

            var hashManifestScanned = scan.Files.Single(file => file.IsHashManifest);
            var stagedHashManifestPath = SafeDestinationPath(
                stagingRoot,
                hashManifestScanned.NormalizedName);
            var hashManifestFingerprint = await ExtractAndHashAsync(
                    hashManifestScanned.Entry,
                    stagedHashManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var hashManifest = CraneProjectPackageHashManifestCodec.Load(
                stagedHashManifestPath);
            var hashExpectations = BuildHashExpectations(hashManifest);

            var projectScanned = scan.Files.Single(file => file.IsProjectManifest);
            var stagedProjectPath = SafeDestinationPath(
                stagingRoot,
                projectScanned.NormalizedName);
            var projectFingerprint = await ExtractAndHashAsync(
                    projectScanned.Entry,
                    stagedProjectPath,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifyFingerprint(projectFingerprint, hashExpectations);

            var stagedProject = CraneProjectCodec.Load(stagedProjectPath);
            var expectedEntries = BuildExpectedEntries(
                stagedProject,
                projectFingerprint.EntryName);
            ValidateHashManifestMatchesProject(
                hashManifest,
                stagedProject,
                expectedEntries,
                hashExpectations);
            ValidateArchiveMatchesProject(
                scan.Files,
                expectedEntries);

            var fingerprints = new List<ProjectPackageFileFingerprint>(scan.Files.Count)
            {
                hashManifestFingerprint,
                projectFingerprint
            };

            foreach (var scanned in scan.Files
                         .Where(file => !file.IsProjectManifest && !file.IsHashManifest)
                         .OrderBy(file => file.NormalizedName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = SafeDestinationPath(
                    stagingRoot,
                    scanned.NormalizedName);
                var fingerprint = await ExtractAndHashAsync(
                        scanned.Entry,
                        destinationPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                VerifyFingerprint(fingerprint, hashExpectations);
                fingerprints.Add(fingerprint);
            }

            var extractedBytes = fingerprints.Sum(file => file.Length);
            if (extractedBytes != scan.TotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    "Фактический объём распакованных файлов не совпадает с ZIP directory metadata.");
            }

            var packageIntegrity = await ProjectIntegrityAnalyzer.AnalyzeAsync(
                    stagedProjectPath,
                    stagedProject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!packageIntegrity.IsHealthy)
            {
                throw new ProjectPackageIntegrityException(
                    "Импорт остановлен: распакованный CraneCAN project не прошёл Project Integrity.",
                    packageIntegrity);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationDoesNotExist(finalDestination);
            Directory.Move(stagingRoot, finalDestination);
            published = true;

            var importedProjectPath = Path.Combine(
                finalDestination,
                Path.GetFileName(stagedProjectPath));

            return new ProjectPackageImportResult(
                fullArchivePath,
                finalDestination,
                importedProjectPath,
                stagedProject,
                stagedProject.Resources.Count,
                fingerprints.Count,
                extractedBytes,
                archiveFingerprint.Length,
                archiveFingerprint.Sha256,
                hashManifestFingerprint.Sha256,
                fingerprints
                    .OrderBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                packageIntegrity);
        }
        finally
        {
            if (!published)
                TryDeleteDirectory(stagingRoot);
        }
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
            {
                throw new InvalidDataException(
                    $"ZIP package содержит symbolic link, что запрещено: {entry.FullName}.");
            }

            var isDirectory = string.IsNullOrEmpty(entry.Name);
            var normalizedName = NormalizeSafeEntryName(
                entry.FullName,
                allowDirectory: isDirectory);

            if (isDirectory)
            {
                if (!directoryNames.Add(normalizedName))
                {
                    throw new InvalidDataException(
                        $"ZIP package содержит повторяющийся каталог: {normalizedName}.");
                }

                continue;
            }

            if (files.Count >= MaxFileEntries)
            {
                throw new InvalidDataException(
                    $"ZIP package содержит более {MaxFileEntries} файлов.");
            }

            if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"Недопустимый размер ZIP entry: {normalizedName}.");
            }
            if (IsHashManifest(normalizedName) && entry.Length > MaxHashManifestBytes)
            {
                throw new InvalidDataException(
                    "Внутренний package SHA-256 manifest превышает безопасный лимит 16 MiB.");
            }

            checked
            {
                totalUncompressedBytes += entry.Length;
            }
            if (totalUncompressedBytes > MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    "Суммарный распакованный объём ZIP package превышает безопасный лимит 256 GiB.");
            }

            if (!fileNames.Add(normalizedName))
            {
                throw new InvalidDataException(
                    $"ZIP package содержит повторяющийся файл: {normalizedName}.");
            }
            if (directoryNames.Contains(normalizedName))
            {
                throw new InvalidDataException(
                    $"ZIP package использует один путь одновременно как файл и каталог: {normalizedName}.");
            }

            files.Add(new ScannedEntry(
                entry,
                normalizedName,
                IsRootProjectManifest(normalizedName),
                IsHashManifest(normalizedName)));
        }

        if (files.Count == 0)
            throw new InvalidDataException("ZIP package не содержит файлов.");

        ValidateFileDirectoryCollisions(fileNames);

        var allProjectManifests = files
            .Where(file => string.Equals(
                Path.GetExtension(file.NormalizedName),
                ".canproject",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rootProjectManifests = allProjectManifests
            .Where(file => file.IsProjectManifest)
            .ToArray();
        if (allProjectManifests.Length != 1 || rootProjectManifests.Length != 1)
        {
            throw new InvalidDataException(
                "CraneCAN ZIP package должен содержать ровно один *.canproject в корне архива.");
        }

        var hashManifests = files.Where(file => file.IsHashManifest).ToArray();
        if (hashManifests.Length != 1)
        {
            throw new InvalidDataException(
                $"CraneCAN ZIP package должен содержать ровно один {CraneProjectPackageHashManifestCodec.EntryName} в корне архива. " +
                "Legacy package без SHA-256 manifest нужно заново экспортировать текущей версией CraneCAN.");
        }

        return new ArchiveScan(files, totalUncompressedBytes);
    }

    private static IReadOnlyDictionary<string, CraneProjectResourceKind> BuildExpectedEntries(
        CraneProject project,
        string projectEntryName)
    {
        CraneProjectCodec.Validate(project);
        var expected = new Dictionary<string, CraneProjectResourceKind>(
            StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeSafeEntryName(projectEntryName)] = CraneProjectResourceKind.Other
        };

        foreach (var resource in project.Resources)
        {
            var entryName = NormalizeSafeEntryName(resource.RelativePath);
            if (string.Equals(
                    entryName,
                    CraneProjectPackageHashManifestCodec.EntryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Project resource использует зарезервированное имя {CraneProjectPackageHashManifestCodec.EntryName}.");
            }
            if (expected.ContainsKey(entryName))
            {
                throw new InvalidDataException(
                    $"Manifest повторно использует package path: {entryName}.");
            }

            expected.Add(entryName, resource.Kind);
        }

        return expected;
    }

    private static IReadOnlyDictionary<string, ProjectPackageFileFingerprint> BuildHashExpectations(
        CraneProjectPackageHashManifest manifest)
    {
        CraneProjectPackageHashManifestCodec.Validate(manifest);
        var expected = new Dictionary<string, ProjectPackageFileFingerprint>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            var normalized = CraneProjectPackageHashManifestCodec.NormalizeFingerprint(file);
            var safeName = NormalizeSafeEntryName(normalized.EntryName);
            if (!string.Equals(safeName, normalized.EntryName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package SHA-256 manifest содержит неканонический path: {normalized.EntryName}.");
            }
            if (!expected.TryAdd(safeName, normalized))
            {
                throw new InvalidDataException(
                    $"Package SHA-256 manifest повторяет path: {safeName}.");
            }
        }

        return expected;
    }

    private static void ValidateHashManifestMatchesProject(
        CraneProjectPackageHashManifest hashManifest,
        CraneProject project,
        IReadOnlyDictionary<string, CraneProjectResourceKind> expectedEntries,
        IReadOnlyDictionary<string, ProjectPackageFileFingerprint> hashExpectations)
    {
        if (hashManifest.ProjectId != project.ProjectId)
        {
            throw new InvalidDataException(
                "Project ID в package SHA-256 manifest не совпадает с .canproject.");
        }

        var projectFileName = expectedEntries.Keys.Single(name =>
            name.EndsWith(".canproject", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(
                hashManifest.ProjectFileName,
                projectFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Имя .canproject в package SHA-256 manifest не совпадает с архивом.");
        }

        var missingHashes = expectedEntries.Keys
            .Where(name => !hashExpectations.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var extraHashes = hashExpectations.Keys
            .Where(name => !expectedEntries.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingHashes.Length > 0 || extraHashes.Length > 0)
        {
            throw new InvalidDataException(
                "Package SHA-256 manifest не соответствует resources .canproject. " +
                $"Missing hash: {string.Join(", ", missingHashes)}. " +
                $"Extra hash: {string.Join(", ", extraHashes)}.");
        }
    }

    private static void ValidateArchiveMatchesProject(
        IReadOnlyList<ScannedEntry> files,
        IReadOnlyDictionary<string, CraneProjectResourceKind> expected)
    {
        var actualPayload = files
            .Where(file => !file.IsHashManifest)
            .Select(file => file.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = actualPayload
            .Where(name => !expected.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidDataException(
                "ZIP package содержит файлы, не зарегистрированные в .canproject: " +
                string.Join(", ", unexpected) +
                ".");
        }

        var missing = expected.Keys
            .Where(name => !actualPayload.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "ZIP package не содержит зарегистрированные project resources: " +
                string.Join(", ", missing) +
                ".");
        }
    }

    private static void VerifyFingerprint(
        ProjectPackageFileFingerprint actual,
        IReadOnlyDictionary<string, ProjectPackageFileFingerprint> expected)
    {
        if (!expected.TryGetValue(actual.EntryName, out var recorded))
        {
            throw new InvalidDataException(
                $"Package SHA-256 manifest не содержит fingerprint: {actual.EntryName}.");
        }
        if (actual.Length != recorded.Length)
        {
            throw new InvalidDataException(
                $"Размер package entry не совпадает с SHA-256 manifest: {actual.EntryName}.");
        }
        if (!string.Equals(actual.Sha256, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch: package entry был изменён после экспорта: {actual.EntryName}.");
        }
    }

    private static async Task<ProjectPackageFileFingerprint> ExtractAndHashAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeSafeEntryName(entry.FullName);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                "Не удалось определить каталог для ZIP entry.");
        Directory.CreateDirectory(destinationDirectory);

        await using var input = entry.Open();
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferSize];
        long length = 0;
        while (true)
        {
            var read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            checked
            {
                length += read;
            }
            if (length > entry.Length || length > MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"ZIP entry распаковал больше данных, чем объявлено: {normalizedName}.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (length != entry.Length)
        {
            throw new InvalidDataException(
                $"Размер распакованного ZIP entry не совпадает с metadata: {normalizedName}.");
        }

        return new ProjectPackageFileFingerprint(
            normalizedName,
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<ProjectPackageFileFingerprint> HashFileAsync(
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
            var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return new ProjectPackageFileFingerprint(
            Path.GetFileName(path),
            stream.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string SafeDestinationPath(string stagingRoot, string entryName)
    {
        var normalized = NormalizeSafeEntryName(entryName);
        var fullRoot = Path.GetFullPath(stagingRoot);
        var destination = Path.GetFullPath(Path.Combine(
            fullRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, destination);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ZIP entry пытается выйти за каталог импорта: {entryName}.");
        }

        return destination;
    }

    private static string NormalizeSafeEntryName(
        string path,
        bool allowDirectory = false)
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
            throw new InvalidDataException(
                $"ZIP entry path должен быть относительным: {path}.");
        }

        var segments = value.Split('/');
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"ZIP entry path содержит пустой сегмент: {path}.");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.IndexOf('\0') >= 0 ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.Any(character =>
                    character < 32 ||
                    character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            {
                throw new InvalidDataException(
                    $"ZIP entry path содержит недопустимый Windows-сегмент: {path}.");
            }

            var deviceName = segment.Split('.')[0];
            if (IsReservedWindowsDeviceName(deviceName))
            {
                throw new InvalidDataException(
                    $"ZIP entry использует зарезервированное Windows-имя: {path}.");
            }
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
        {
            return true;
        }

        if (value.Length == 4 &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            value[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static bool IsRootProjectManifest(string normalizedName) =>
        !normalizedName.Contains('/') &&
        string.Equals(
            Path.GetExtension(normalizedName),
            ".canproject",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsHashManifest(string normalizedName) =>
        !normalizedName.Contains('/') &&
        string.Equals(
            normalizedName,
            CraneProjectPackageHashManifestCodec.EntryName,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000;
    }

    private static void ValidateFileDirectoryCollisions(
        IReadOnlySet<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var slash = fileName.IndexOf('/');
            while (slash >= 0)
            {
                var parent = fileName[..slash];
                if (fileNames.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"ZIP package использует путь одновременно как файл и каталог: {parent}.");
                }

                slash = fileName.IndexOf('/', slash + 1);
            }
        }
    }

    private static void EnsureEnoughFreeSpace(
        string parentDirectory,
        long totalUncompressedBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(parentDirectory));
            if (string.IsNullOrWhiteSpace(root))
                return;

            var drive = new DriveInfo(root);
            const long reserveBytes = 64L * 1024 * 1024;
            if (drive.IsReady &&
                drive.AvailableFreeSpace < totalUncompressedBytes + reserveBytes)
            {
                throw new IOException(
                    "Недостаточно свободного места для безопасной распаковки CraneCAN package.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Network/custom filesystems may not expose free-space metadata.
        }
    }

    private static void EnsureDestinationDoesNotExist(string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new IOException(
                "Папка назначения уже существует. Импорт CraneCAN package никогда не перезаписывает существующие материалы: " +
                destinationDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the private staging directory only.
        }
    }

    private sealed record ScannedEntry(
        ZipArchiveEntry Entry,
        string NormalizedName,
        bool IsProjectManifest,
        bool IsHashManifest);

    private sealed record ArchiveScan(
        IReadOnlyList<ScannedEntry> Files,
        long TotalUncompressedBytes);
}
