using System.IO.Compression;
using System.Runtime.CompilerServices;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectPackageVerifierTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestValidPackageWithoutExtraction();
        TestPayloadTamperRejected();
        TestLegacyPackageRejected();
        TestProjectIdMismatchRejected();
        TestTraversalEntryRejected();
    }

    private static void TestValidPackageWithoutExtraction()
    {
        var source = TempDirectory("cranecan-verify-source");
        var output = TempDirectory("cranecan-verify-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "portable.zip"));
            var before = Directory.EnumerateFileSystemEntries(output)
                .Select(Path.GetFileName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = CraneProjectPackageVerifier.VerifyZipAsync(setup.ArchivePath)
                .GetAwaiter()
                .GetResult();

            var after = Directory.EnumerateFileSystemEntries(output)
                .Select(Path.GetFileName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Check(result.IsValid && result.Status == "VALID", "Valid package was not reported as VALID.");
            Check(result.ProjectId == setup.Project.ProjectId, "Verifier lost Project ID.");
            Check(result.ProjectName == setup.Project.Name, "Verifier lost project name.");
            Check(result.ResourceCount == setup.Project.Resources.Count, "Verifier resource count is incorrect.");
            Check(result.FileCount == setup.Project.Resources.Count + 2, "Verifier archive file count is incorrect.");
            Check(result.Files.Count == setup.Project.Resources.Count + 1, "Verifier payload fingerprint count is incorrect.");
            Check(result.Files.All(file => file.IsMatch), "Valid payload did not match recorded fingerprints.");
            Check(result.Errors.Count == 0, "Valid package produced verification errors.");
            Check(result.ArchiveSha256.Length == 64 && result.PackageManifestSha256.Length == 64,
                "Verifier did not return SHA-256 fingerprints.");
            Check(before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase),
                "Verify-only operation created or extracted files beside the ZIP.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestPayloadTamperRejected()
    {
        var source = TempDirectory("cranecan-verify-tamper-source");
        var output = TempDirectory("cranecan-verify-tamper-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "tampered.zip"));
            var traceRelative = setup.Project.Resources.Single(resource =>
                resource.Kind == CraneProjectResourceKind.Trace).RelativePath.Replace('\\', '/');

            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.Entries.Single(item => string.Equals(
                    item.FullName.Replace('\\', '/'),
                    traceRelative,
                    StringComparison.OrdinalIgnoreCase));
                byte[] bytes;
                using (var stream = entry.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                Check(bytes.Length > 0, "Test trace unexpectedly empty.");
                bytes[^1] ^= 0x01;
                entry.Delete();
                var replacement = archive.CreateEntry(traceRelative, CompressionLevel.Optimal);
                using var replacementStream = replacement.Open();
                replacementStream.Write(bytes);
            }

            var result = CraneProjectPackageVerifier.VerifyZipAsync(setup.ArchivePath)
                .GetAwaiter()
                .GetResult();
            Check(!result.IsValid && result.Status == "INVALID", "Tampered payload was not reported INVALID.");
            Check(result.Errors.Any(error => error.Contains("mismatch", StringComparison.OrdinalIgnoreCase)),
                "Tampered payload did not produce a hash mismatch error.");
            Check(result.Files.Any(file =>
                    string.Equals(file.EntryName, traceRelative, StringComparison.OrdinalIgnoreCase) && !file.IsMatch),
                "Tampered trace was not identified in the verification file list.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestLegacyPackageRejected()
    {
        var source = TempDirectory("cranecan-verify-legacy-source");
        var output = TempDirectory("cranecan-verify-legacy-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "legacy.zip"));
            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.Entries.Single(item => string.Equals(
                    item.FullName,
                    CraneProjectPackageHashManifestCodec.EntryName,
                    StringComparison.OrdinalIgnoreCase));
                entry.Delete();
            }

            var result = CraneProjectPackageVerifier.VerifyZipAsync(setup.ArchivePath)
                .GetAwaiter()
                .GetResult();
            Check(!result.IsValid, "Legacy package without internal SHA-256 manifest was accepted.");
            Check(result.Errors.Any(error => error.Contains("Legacy", StringComparison.OrdinalIgnoreCase) ||
                                                  error.Contains("SHA-256 manifest", StringComparison.OrdinalIgnoreCase)),
                "Legacy package rejection reason is missing.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestProjectIdMismatchRejected()
    {
        var source = TempDirectory("cranecan-verify-id-source");
        var output = TempDirectory("cranecan-verify-id-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "wrong-id.zip"));
            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.Entries.Single(item => string.Equals(
                    item.FullName,
                    CraneProjectPackageHashManifestCodec.EntryName,
                    StringComparison.OrdinalIgnoreCase));
                string text;
                using (var reader = new StreamReader(entry.Open()))
                    text = reader.ReadToEnd();
                var changed = text.Replace(
                    setup.Project.ProjectId.ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    StringComparison.OrdinalIgnoreCase);
                Check(!string.Equals(text, changed, StringComparison.Ordinal),
                    "Test could not replace Project ID in internal manifest.");
                entry.Delete();
                var replacement = archive.CreateEntry(
                    CraneProjectPackageHashManifestCodec.EntryName,
                    CompressionLevel.Optimal);
                using var writer = new StreamWriter(replacement.Open());
                writer.Write(changed);
            }

            var result = CraneProjectPackageVerifier.VerifyZipAsync(setup.ArchivePath)
                .GetAwaiter()
                .GetResult();
            Check(!result.IsValid, "Package with mismatched Project ID was accepted.");
            Check(result.Errors.Any(error => error.Contains("Project ID", StringComparison.OrdinalIgnoreCase)),
                "Project ID mismatch was not reported.");
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(output);
        }
    }

    private static void TestTraversalEntryRejected()
    {
        var source = TempDirectory("cranecan-verify-traversal-source");
        var output = TempDirectory("cranecan-verify-traversal-output");
        try
        {
            var setup = CreatePackage(source, Path.Combine(output, "traversal.zip"));
            using (var archive = ZipFile.Open(setup.ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry("../outside.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("must never be extracted");
            }

            var outside = Path.Combine(output, "outside.txt");
            var result = CraneProjectPackageVerifier.VerifyZipAsync(setup.ArchivePath)
                .GetAwaiter()
                .GetResult();
            Check(!result.IsValid, "Path traversal entry was accepted by verify-only mode.");
            Check(!File.Exists(outside), "Verify-only mode extracted a traversal entry.");
            Check(result.Errors.Any(error => error.Contains("недопустимый", StringComparison.OrdinalIgnoreCase) ||
                                                  error.Contains("относительным", StringComparison.OrdinalIgnoreCase)),
                "Path traversal rejection reason is missing.");
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
        Directory.CreateDirectory(Path.Combine(sourceRoot, "traces"));

        var projectPath = Path.Combine(sourceRoot, "machine.canproject");
        var profilePath = Path.Combine(sourceRoot, "profiles", "machine.craneprofile");
        var tracePath = Path.Combine(sourceRoot, "traces", "capture.trc");
        File.WriteAllText(profilePath, "profile bytes");
        File.WriteAllText(tracePath, "$FILEVERSION=1.1\n1) 0.000 Rx 123 1 AA");

        var project = CraneProjectCodec.Create(
            "verify test",
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

        _ = CraneProjectPackageExporter.ExportZipAsync(projectPath, project, archivePath)
            .GetAwaiter()
            .GetResult();

        return new PackageSetup(projectPath, archivePath, project);
    }

    private static string TempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
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
