using System.IO.Compression;
using System.Security.Cryptography;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectPackageImportTests
{
    public static void Run()
    {
        TestVerifiedImportRoundTrip();
        TestTraversalEntryRejected();
        TestUnexpectedAndMissingEntriesRejected();
        TestCaseInsensitiveDuplicateRejected();
        TestSymlinkRejected();
        TestExistingDestinationIsNeverOverwritten();
    }

    private static void TestVerifiedImportRoundTrip()
    {
        var source = TempDirectory("cranecan-import-source");
        var output = TempDirectory("cranecan-import-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "portable.zip"));
            var destination = Path.Combine(output, "imported-project");

            var result = CraneProjectPackageImporter.ImportZipAsync(
                    setup.ArchivePath,
                    destination)
                .GetAwaiter()
                .GetResult();

            var expectedArchiveSha = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(setup.ArchivePath)))
                .ToLowerInvariant();
            var imported = CraneProjectCodec.Load(result.ProjectPath);

            Check(
                Directory.Exists(destination) &&
                File.Exists(result.ProjectPath) &&
                imported.ProjectId == setup.Project.ProjectId &&
                result.Project.ProjectId == setup.Project.ProjectId &&
                result.ResourceCount == setup.Project.Resources.Count &&
                result.FileCount == setup.Project.Resources.Count + 1 &&
                result.PackageIntegrity.IsHealthy &&
                result.ArchiveSha256 == expectedArchiveSha &&
                result.ArchiveBytes == new FileInfo(setup.ArchivePath).Length,
                "Verified project package import metadata is incorrect.");

            var expectedFiles = setup.Project.Resources
                .Select(resource => resource.RelativePath.Replace('\\', '/'))
                .Append(Path.GetFileName(setup.ProjectPath))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var importedFiles = Directory.EnumerateFiles(
                    destination,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/'))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Check(
                importedFiles.SequenceEqual(expectedFiles, StringComparer.OrdinalIgnoreCase),
                "Importer did not publish exactly the manifest and registered resources.");

            foreach (var resource in setup.Project.Resources)
            {
                var sourcePath = CraneProjectCodec.ResolveResourcePath(
                    setup.ProjectPath,
                    resource);
                var importedResource = imported.Resources.Single(item =>
                    item.ResourceId == resource.ResourceId);
                var importedPath = CraneProjectCodec.ResolveResourcePath(
                    result.ProjectPath,
                    importedResource);
                Check(
                    File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(importedPath)),
                    $"Imported resource bytes changed: {resource.RelativePath}.");
            }

            Check(
                result.Files.Count == result.FileCount &&
                result.Files.All(file =>
                    file.Sha256.Length == 64 &&
                    file.Sha256.All(character =>
                        char.IsDigit(character) || character is >= 'a' and <= 'f')),
                "Importer did not retain per-entry SHA-256 fingerprints.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestTraversalEntryRejected()
    {
        var source = TempDirectory("cranecan-import-traversal-source");
        var output = TempDirectory("cranecan-import-traversal-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "traversal.zip"));
            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("../escape.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("must not escape");
            }

            var destination = Path.Combine(output, "imported");
            var exception = CaptureException(() =>
                CraneProjectPackageImporter.ImportZipAsync(
                        setup.ArchivePath,
                        destination)
                    .GetAwaiter()
                    .GetResult());

            Check(
                exception is InvalidDataException &&
                !Directory.Exists(destination) &&
                !File.Exists(Path.Combine(output, "escape.txt")),
                "Path-traversal ZIP entry was not rejected before publication.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestUnexpectedAndMissingEntriesRejected()
    {
        var source = TempDirectory("cranecan-import-shape-source");
        var output = TempDirectory("cranecan-import-shape-output");
        try
        {
            var unexpected = CreatePackage(source, Path.Combine(output, "unexpected.zip"));
            using (var archive = ZipFile.Open(unexpected.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("extra-unregistered.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("not in manifest");
            }

            var unexpectedDestination = Path.Combine(output, "unexpected-import");
            var unexpectedException = CaptureException(() =>
                CraneProjectPackageImporter.ImportZipAsync(
                        unexpected.ArchivePath,
                        unexpectedDestination)
                    .GetAwaiter()
                    .GetResult());
            Check(
                unexpectedException is InvalidDataException &&
                unexpectedException.Message.Contains(
                    "не зарегистрированы",
                    StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(unexpectedDestination),
                "Unregistered ZIP entry was not rejected.");

            var missingSource = TempDirectory("cranecan-import-missing-source");
            try
            {
                var missing = CreatePackage(
                    missingSource,
                    Path.Combine(output, "missing.zip"));
                var traceRelative = missing.Project.Resources.Single(resource =>
                    resource.Kind == CraneProjectResourceKind.Trace).RelativePath;
                using (var archive = ZipFile.Open(missing.ArchivePath, ZipArchiveMode.Update))
                {
                    var entry = archive.Entries.Single(item => string.Equals(
                        item.FullName.Replace('\\', '/'),
                        traceRelative.Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase));
                    entry.Delete();
                }

                var missingDestination = Path.Combine(output, "missing-import");
                var missingException = CaptureException(() =>
                    CraneProjectPackageImporter.ImportZipAsync(
                            missing.ArchivePath,
                            missingDestination)
                        .GetAwaiter()
                        .GetResult());
                Check(
                    missingException is InvalidDataException &&
                    missingException.Message.Contains(
                        "не содержит зарегистрированные",
                        StringComparison.OrdinalIgnoreCase) &&
                    !Directory.Exists(missingDestination),
                    "Missing registered ZIP entry was not rejected.");
            }
            finally
            {
                DeleteDirectory(missingSource);
            }
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestCaseInsensitiveDuplicateRejected()
    {
        var source = TempDirectory("cranecan-import-duplicate-source");
        var output = TempDirectory("cranecan-import-duplicate-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "duplicate.zip"));
            var traceRelative = setup.Project.Resources.Single(resource =>
                resource.Kind == CraneProjectResourceKind.Trace).RelativePath.Replace('\\', '/');
            var duplicateName = traceRelative.ToUpperInvariant();
            if (string.Equals(duplicateName, traceRelative, StringComparison.Ordinal))
                duplicateName = traceRelative.Replace("capture.trc", "CAPTURE.TRC", StringComparison.Ordinal);

            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(duplicateName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("duplicate");
            }

            var destination = Path.Combine(output, "duplicate-import");
            var exception = CaptureException(() =>
                CraneProjectPackageImporter.ImportZipAsync(
                        setup.ArchivePath,
                        destination)
                    .GetAwaiter()
                    .GetResult());
            Check(
                exception is InvalidDataException &&
                exception.Message.Contains("повторяющийся", StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(destination),
                "Case-insensitive duplicate ZIP path was not rejected.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestSymlinkRejected()
    {
        var source = TempDirectory("cranecan-import-symlink-source");
        var output = TempDirectory("cranecan-import-symlink-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "symlink.zip"));
            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("link-to-outside");
                entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write("../outside");
            }

            var destination = Path.Combine(output, "symlink-import");
            var exception = CaptureException(() =>
                CraneProjectPackageImporter.ImportZipAsync(
                        setup.ArchivePath,
                        destination)
                    .GetAwaiter()
                    .GetResult());
            Check(
                exception is InvalidDataException &&
                exception.Message.Contains("symbolic link", StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(destination),
                "ZIP symbolic link was not rejected.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestExistingDestinationIsNeverOverwritten()
    {
        var source = TempDirectory("cranecan-import-collision-source");
        var output = TempDirectory("cranecan-import-collision-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "collision.zip"));
            var destination = Path.Combine(output, "existing-project");
            Directory.CreateDirectory(destination);
            var sentinel = Path.Combine(destination, "keep.txt");
            File.WriteAllText(sentinel, "do not overwrite");

            var exception = CaptureException(() =>
                CraneProjectPackageImporter.ImportZipAsync(
                        setup.ArchivePath,
                        destination)
                    .GetAwaiter()
                    .GetResult());
            Check(
                exception is IOException &&
                File.ReadAllText(sentinel) == "do not overwrite" &&
                Directory.EnumerateFiles(destination).Count() == 1,
                "Importer modified an existing destination directory.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static PackageSetup CreatePackage(string sourceRoot, string archivePath)
    {
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "profiles"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "traces", "field"));

        var projectPath = Path.Combine(sourceRoot, "machine.canproject");
        var profilePath = Path.Combine(sourceRoot, "profiles", "machine.craneprofile");
        var tracePath = Path.Combine(sourceRoot, "traces", "field", "capture.trc");
        File.WriteAllText(profilePath, "profile bytes");
        File.WriteAllText(
            tracePath,
            "$FILEVERSION=1.1\n1) 0.000 Rx 123 1 AA");

        var project = CraneProjectCodec.Create(
            "import test",
            "test machine",
            "Test",
            "M1",
            "CAN1",
            250_000);
        project = CraneProjectCodec.AddResource(
            project,
            projectPath,
            profilePath,
            CraneProjectResourceKind.MachineProfile,
            setActive: true).Project;
        project = CraneProjectCodec.AddResource(
            project,
            projectPath,
            tracePath,
            CraneProjectResourceKind.Trace).Project;
        project = CraneProjectCodec.Save(projectPath, project);

        _ = CraneProjectPackageExporter.ExportZipAsync(
                projectPath,
                project,
                archivePath)
            .GetAwaiter()
            .GetResult();

        return new PackageSetup(projectPath, archivePath, project);
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static string TempDirectory(string prefix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record PackageSetup(
        string ProjectPath,
        string ArchivePath,
        CraneProject Project);
}
