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
    IReadOnlyList<ProjectPackageFileFingerprint> Files,
    ProjectIntegrityReport PackageIntegrity);

/// <summary>
/// Safely imports a CraneCAN ZIP package into a new directory. The importer
/// rejects path traversal, Windows alternate-data-stream syntax, duplicate
/// normalized paths, symlinks/special entries, unexpected files, incomplete
/// manifests and any destination that already exists. Files are first
/// extracted into a sibling staging directory, hashed while extracting and
/// checked with Project Integrity before the staging directory is renamed to
/// the requested destination. Existing user files are never overwritten.
/// </summary>
public static class CraneProjectPackageImporter
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxFileEntries = 20_000;
    private const long MaxSingleEntryBytes = 128L * 1024 * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 256L * 1024 * 1024 * 1024;

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

            var manifestEntry = scan.Files.Single(file => file.IsManifest).Entry;
            var stagedProjectPath = SafeDestinationPath(
                stagingRoot,
                manifestEntry.FullName);
            var manifestFingerprint = await ExtractAndHashAsync(
                    manifestEntry,
                    stagedProjectPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var stagedProject = CraneProjectCodec.Load(stagedProjectPath);
            var expectedEntries = BuildExpectedEntries(
                stagedProject,
                manifestFingerprint.EntryName);
            ValidateArchiveMatchesManifest(scan.Files, expectedEntries);

            var fingerprints = new List<ProjectPackageFileFingerprint>(scan.Files.Count)
            {
                manifestFingerprint
            };

            foreach (var scanned in scan.Files
                         .Where(file => !file.IsManifest)
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
                IsRootManifest(normalizedName)));
        }

        if (files.Count == 0)
            throw new InvalidDataException("ZIP package не содержит файлов.");

        ValidateFileDirectoryCollisions(fileNames);

        var allManifests = files
            .Where(file => string.Equals(
                Path.GetExtension(file.NormalizedName),
                ".canproject",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rootManifests = allManifests.Where(file => file.IsManifest).ToArray();
        if (allManifests.Length != 1 || rootManifests.Length != 1)
        {
            throw new InvalidDataException(
                "CraneCAN ZIP package должен содержать ровно один *.canproject в корне архива.");
        }

        return new ArchiveScan(files, totalUncompressedBytes);
    }

    private static IReadOnlyDictionary<string, CraneProjectResourceKind> BuildExpectedEntries(
        CraneProject project,
        string manifestEntryName)
    {
        CraneProjectCodec.Validate(project);
        var expected = new Dictionary<string, CraneProjectResourceKind>(
            StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeSafeEntryName(manifestEntryName)] = CraneProjectResourceKind.Other
        };

        foreach (var resource in project.Resources)
        {
            var entryName = NormalizeSafeEntryName(resource.RelativePath);
            if (expected.ContainsKey(entryName))
            {
                throw new InvalidDataException(
                    $"Manifest повторно использует package path: {entryName}.");
            }

            expected.Add(entryName, resource.Kind);
        }

        return expected;
    }

    private static void ValidateArchiveMatchesManifest(
        IReadOnlyList<ScannedEntry> files,
        IReadOnlyDictionary<string, CraneProjectResourceKind> expected)
    {
        var actual = files
            .Select(file => file.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = actual
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
            .Where(name => !actual.Contains(name))
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
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
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

    private static bool IsRootManifest(string normalizedName) =>
        !normalizedName.Contains('/') &&
        string.Equals(
            Path.GetExtension(normalizedName),
            ".canproject",
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
        bool IsManifest);

    private sealed record ArchiveScan(
        IReadOnlyList<ScannedEntry> Files,
        long TotalUncompressedBytes);
}
