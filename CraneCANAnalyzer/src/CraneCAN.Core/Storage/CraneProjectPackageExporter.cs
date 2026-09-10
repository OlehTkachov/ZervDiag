using System.IO.Compression;
using System.Security.Cryptography;
using CraneCAN.Core.Projects;

namespace CraneCAN.Core.Storage;

public sealed record ProjectPackageFileFingerprint(
    string EntryName,
    long Length,
    string Sha256);

public sealed record ProjectPackageExportResult(
    string ArchivePath,
    string ProjectFileName,
    int ResourceCount,
    int FileCount,
    long UncompressedBytes,
    long ArchiveBytes,
    string Sha256,
    ProjectIntegrityReport SourceIntegrity,
    ProjectIntegrityReport PackageIntegrity);

public sealed class ProjectPackageIntegrityException : InvalidOperationException
{
    public ProjectPackageIntegrityException(
        string message,
        ProjectIntegrityReport integrityReport)
        : base(message)
    {
        IntegrityReport = integrityReport;
    }

    public ProjectIntegrityReport IntegrityReport { get; }
}

/// <summary>
/// Creates a self-contained ZIP package from a saved CraneCAN project. Only the
/// .canproject manifest and registered project resources are included. Source
/// files are read-only; the exporter verifies source integrity, staged-copy
/// integrity and every ZIP entry before publishing the archive.
/// </summary>
public static class CraneProjectPackageExporter
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<ProjectPackageExportResult> ExportZipAsync(
        string projectPath,
        CraneProject project,
        string archivePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var sourceProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(sourceProjectPath))
        {
            throw new FileNotFoundException(
                "Исходный файл .canproject не найден.",
                sourceProjectPath);
        }

        var sourceIntegrity = await ProjectIntegrityAnalyzer.AnalyzeAsync(
                sourceProjectPath,
                project,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureHealthy(
            sourceIntegrity,
            "Экспорт остановлен: исходный .canproject содержит ошибки или предупреждения.");

        var finalArchivePath = Path.GetFullPath(archivePath);
        var archiveDirectory = Path.GetDirectoryName(finalArchivePath)
            ?? throw new InvalidOperationException(
                "Не удалось определить каталог ZIP package.");

        EnsureArchiveDoesNotOverwriteProjectData(
            sourceProjectPath,
            project,
            finalArchivePath);

        if (File.Exists(finalArchivePath) && !overwrite)
        {
            throw new IOException(
                $"Файл package уже существует: {finalArchivePath}");
        }

        Directory.CreateDirectory(archiveDirectory);

        var projectFileName = Path.GetFileName(sourceProjectPath);
        if (string.IsNullOrWhiteSpace(projectFileName))
        {
            throw new InvalidOperationException(
                "Не удалось определить имя .canproject.");
        }

        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"CraneCAN-package-{Guid.NewGuid():N}");
        var tempArchivePath = Path.Combine(
            archiveDirectory,
            $".{Path.GetFileName(finalArchivePath)}.{Guid.NewGuid():N}.tmp");

        Directory.CreateDirectory(stagingRoot);
        try
        {
            var expected = new Dictionary<string, ProjectPackageFileFingerprint>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var resource in project.Resources
                         .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = CraneProjectCodec.ResolveResourcePath(
                    sourceProjectPath,
                    resource);
                var entryName = NormalizeEntryName(resource.RelativePath);
                if (string.Equals(
                        entryName,
                        NormalizeEntryName(projectFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Сам .canproject не должен быть зарегистрирован как обычный resource.");
                }

                var destinationPath = Path.Combine(
                    stagingRoot,
                    entryName.Replace('/', Path.DirectorySeparatorChar));
                var fingerprint = await CopyAndHashAsync(
                        sourcePath,
                        destinationPath,
                        entryName,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddExpected(expected, fingerprint);
            }

            var stagedProjectPath = Path.Combine(stagingRoot, projectFileName);
            var stagedProject = CraneProjectCodec.Save(stagedProjectPath, project);
            var manifestFingerprint = await HashFileAsync(
                    stagedProjectPath,
                    NormalizeEntryName(projectFileName),
                    cancellationToken)
                .ConfigureAwait(false);
            AddExpected(expected, manifestFingerprint);

            var packageIntegrity = await ProjectIntegrityAnalyzer.AnalyzeAsync(
                    stagedProjectPath,
                    stagedProject,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureHealthy(
                packageIntegrity,
                "Экспорт остановлен: staged package не прошёл повторную проверку целостности.");

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(tempArchivePath))
                File.Delete(tempArchivePath);

            ZipFile.CreateFromDirectory(
                stagingRoot,
                tempArchivePath,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyArchiveAsync(
                    tempArchivePath,
                    expected,
                    cancellationToken)
                .ConfigureAwait(false);

            var archiveFingerprint = await HashFileAsync(
                    tempArchivePath,
                    Path.GetFileName(finalArchivePath),
                    cancellationToken)
                .ConfigureAwait(false);
            var archiveBytes = new FileInfo(tempArchivePath).Length;

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite)
                File.Move(tempArchivePath, finalArchivePath, overwrite: true);
            else
                File.Move(tempArchivePath, finalArchivePath);

            return new ProjectPackageExportResult(
                finalArchivePath,
                projectFileName,
                project.Resources.Count,
                expected.Count,
                expected.Values.Sum(item => item.Length),
                archiveBytes,
                archiveFingerprint.Sha256,
                sourceIntegrity,
                packageIntegrity);
        }
        finally
        {
            TryDeleteFile(tempArchivePath);
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static void EnsureHealthy(
        ProjectIntegrityReport report,
        string message)
    {
        if (report.IsHealthy)
            return;

        throw new ProjectPackageIntegrityException(message, report);
    }

    private static void EnsureArchiveDoesNotOverwriteProjectData(
        string projectPath,
        CraneProject project,
        string archivePath)
    {
        if (PathsEqual(projectPath, archivePath))
        {
            throw new InvalidOperationException(
                "ZIP package нельзя сохранять поверх исходного .canproject.");
        }

        foreach (var resource in project.Resources)
        {
            var resourcePath = CraneProjectCodec.ResolveResourcePath(
                projectPath,
                resource);
            if (!PathsEqual(resourcePath, archivePath))
                continue;

            throw new InvalidOperationException(
                $"ZIP package нельзя сохранять поверх project resource: {resource.RelativePath}.");
        }
    }

    private static async Task<ProjectPackageFileFingerprint> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        string entryName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Project resource не найден.", sourcePath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                "Не удалось определить каталог staged resource.");
        Directory.CreateDirectory(destinationDirectory);

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
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

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
            length += read;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new ProjectPackageFileFingerprint(
            entryName,
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<ProjectPackageFileFingerprint> HashFileAsync(
        string path,
        string entryName,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = await HashStreamAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectPackageFileFingerprint(
            entryName,
            stream.Length,
            sha256);
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        IReadOnlyDictionary<string, ProjectPackageFileFingerprint> expected,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var entryName = NormalizeEntryName(entry.FullName);
            if (!seen.Add(entryName))
            {
                throw new InvalidDataException(
                    $"ZIP package содержит повторяющийся entry: {entryName}.");
            }

            if (!expected.TryGetValue(entryName, out var fingerprint))
            {
                throw new InvalidDataException(
                    $"ZIP package содержит незарегистрированный файл: {entryName}.");
            }

            if (entry.Length != fingerprint.Length)
            {
                throw new InvalidDataException(
                    $"Размер ZIP entry не совпадает со staged copy: {entryName}.");
            }

            await using var entryStream = entry.Open();
            var sha256 = await HashStreamAsync(
                    entryStream,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    sha256,
                    fingerprint.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 ZIP entry не совпадает со staged copy: {entryName}.");
            }
        }

        var missing = expected.Keys
            .Where(entryName => !seen.Contains(entryName))
            .OrderBy(entryName => entryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "ZIP package не содержит ожидаемые файлы: " +
                string.Join(", ", missing) +
                ".");
        }
    }

    private static async Task<string> HashStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
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

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddExpected(
        IDictionary<string, ProjectPackageFileFingerprint> expected,
        ProjectPackageFileFingerprint fingerprint)
    {
        if (expected.ContainsKey(fingerprint.EntryName))
        {
            throw new InvalidOperationException(
                $"Package entry повторяется: {fingerprint.EntryName}.");
        }

        expected.Add(fingerprint.EntryName, fingerprint);
    }

    private static string NormalizeEntryName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim().Replace('\\', '/').TrimStart('/');
        var segments = value.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException(
                $"Недопустимый package entry path: {path}");
        }

        return string.Join('/', segments);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
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
            // Best effort cleanup only.
        }
    }
}
