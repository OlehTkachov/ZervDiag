using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectPackageTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestPortableZipRoundTrip();
        TestIntegrityWarningBlocksExport();
        TestOverwriteProtection();
        TestDestinationCannotOverwriteResource();
    }

    private static void TestPortableZipRoundTrip()
    {
        var root = TempDirectory("cranecan-package-source");
        var output = TempDirectory("cranecan-package-output");
        var extracted = TempDirectory("cranecan-package-extracted");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "profiles"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "field"));
            Directory.CreateDirectory(Path.Combine(root, "reports"));

            var projectPath = Path.Combine(root, "SOOSAN_JK1200A.canproject");
            var profilePath = Path.Combine(root, "profiles", "JK1200A.craneprofile");
            var tracePath = Path.Combine(root, "traces", "field", "capture.trc");
            var reportPath = Path.Combine(root, "reports", "signature.md");
            var unregisteredPath = Path.Combine(root, "private-note.tmp");

            File.WriteAllText(profilePath, "{\"machine\":\"JK1200A\"}");
            File.WriteAllText(
                tracePath,
                "$FILEVERSION=1.1\n;$STARTTIME=0\n1) 0.000 Rx 123 8 00 01 02 03 04 05 06 07");
            File.WriteAllText(reportPath, "# portable report");
            File.WriteAllText(unregisteredPath, "must not be packaged");

            var project = CraneProjectCodec.Create(
                "JK1200A package",
                "SOOSAN JK1200A",
                "SOOSAN",
                "JK1200A",
                "JCH4",
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
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                reportPath,
                CraneProjectResourceKind.Report).Project;
            project = CraneProjectCodec.Save(projectPath, project);

            var sourceProjectBytes = File.ReadAllBytes(projectPath);
            var sourceProfileBytes = File.ReadAllBytes(profilePath);
            var sourceTraceBytes = File.ReadAllBytes(tracePath);
            var sourceReportBytes = File.ReadAllBytes(reportPath);

            var archivePath = Path.Combine(output, "JK1200A-package.zip");
            var result = CraneProjectPackageExporter.ExportZipAsync(
                    projectPath,
                    project,
                    archivePath)
                .GetAwaiter()
                .GetResult();

            Check(File.Exists(archivePath), "Portable ZIP package was not created.");
            var actualArchiveSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(archivePath)))
                .ToLowerInvariant();
            Check(
                result.ResourceCount == 3 &&
                result.FileCount == 4 &&
                result.SourceIntegrity.IsHealthy &&
                result.PackageIntegrity.IsHealthy &&
                result.ArchiveBytes == new FileInfo(archivePath).Length &&
                result.Sha256 == actualArchiveSha256 &&
                result.Sha256.Length == 64 &&
                result.Sha256.All(character =>
                    char.IsDigit(character) ||
                    character is >= 'a' and <= 'f'),
                "Portable ZIP package result metadata is incorrect.");

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var entries = archive.Entries
                    .Where(entry => !string.IsNullOrEmpty(entry.Name))
                    .Select(entry => entry.FullName.Replace('\\', '/'))
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var expected = new[]
                {
                    "profiles/JK1200A.craneprofile",
                    "reports/signature.md",
                    "SOOSAN_JK1200A.canproject",
                    "traces/field/capture.trc"
                }.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                 .ToArray();

                Check(
                    entries.SequenceEqual(
                        expected,
                        StringComparer.OrdinalIgnoreCase),
                    "ZIP package did not contain exactly the registered resources plus .canproject.");
                Check(
                    !entries.Any(entry => entry.Contains(
                        "private-note",
                        StringComparison.OrdinalIgnoreCase)),
                    "ZIP package included an unregistered file.");
            }

            ZipFile.ExtractToDirectory(archivePath, extracted);
            var extractedProjectPath = Path.Combine(
                extracted,
                "SOOSAN_JK1200A.canproject");
            var extractedProject = CraneProjectCodec.Load(extractedProjectPath);
            var extractedIntegrity = ProjectIntegrityAnalyzer.AnalyzeAsync(
                    extractedProjectPath,
                    extractedProject)
                .GetAwaiter()
                .GetResult();

            Check(
                extractedProject.ProjectId == project.ProjectId &&
                extractedProject.Resources.Count == project.Resources.Count &&
                extractedIntegrity.IsHealthy,
                "Extracted project did not preserve IDs/resources or failed Project Integrity.");

            Check(
                sourceProjectBytes.SequenceEqual(File.ReadAllBytes(projectPath)) &&
                sourceProfileBytes.SequenceEqual(File.ReadAllBytes(profilePath)) &&
                sourceTraceBytes.SequenceEqual(File.ReadAllBytes(tracePath)) &&
                sourceReportBytes.SequenceEqual(File.ReadAllBytes(reportPath)),
                "Export modified the original .canproject or one of its resources.");
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(output);
            DeleteDirectory(extracted);
        }
    }

    private static void TestIntegrityWarningBlocksExport()
    {
        var root = TempDirectory("cranecan-package-warning");
        var output = TempDirectory("cranecan-package-warning-output");

        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var presentPath = Path.Combine(root, "present.craneprofile");
            var missingPath = Path.Combine(root, "missing.md");
            File.WriteAllText(presentPath, "profile");

            var project = CraneProjectCodec.Create("warning package", "machine");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                presentPath,
                CraneProjectResourceKind.MachineProfile,
                setActive: true).Project;

            File.WriteAllText(missingPath, "temporary");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                missingPath,
                CraneProjectResourceKind.Report).Project;
            File.Delete(missingPath);
            project = CraneProjectCodec.Save(projectPath, project);

            var archivePath = Path.Combine(output, "blocked.zip");
            ProjectPackageIntegrityException? caught = null;
            try
            {
                _ = CraneProjectPackageExporter.ExportZipAsync(
                        projectPath,
                        project,
                        archivePath)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ProjectPackageIntegrityException exception)
            {
                caught = exception;
            }

            Check(
                caught is not null &&
                caught.IntegrityReport.WarningCount > 0 &&
                caught.IntegrityReport.Issues.Any(issue =>
                    issue.Code == ProjectIntegrityCode.ResourceMissing) &&
                !File.Exists(archivePath),
                "Package export did not block a project with integrity warnings.");
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(output);
        }
    }

    private static void TestOverwriteProtection()
    {
        var root = TempDirectory("cranecan-package-overwrite");
        var output = TempDirectory("cranecan-package-overwrite-output");

        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var tracePath = Path.Combine(root, "trace.trc");
            File.WriteAllText(tracePath, "trace");

            var project = CraneProjectCodec.Create("overwrite", "machine");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                tracePath,
                CraneProjectResourceKind.Trace).Project;
            project = CraneProjectCodec.Save(projectPath, project);

            var archivePath = Path.Combine(output, "package.zip");
            File.WriteAllText(archivePath, "keep me");

            var blocked = false;
            try
            {
                _ = CraneProjectPackageExporter.ExportZipAsync(
                        projectPath,
                        project,
                        archivePath,
                        overwrite: false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (IOException)
            {
                blocked = true;
            }

            Check(
                blocked &&
                File.ReadAllText(archivePath) == "keep me",
                "Existing package was overwritten without explicit permission.");

            var result = CraneProjectPackageExporter.ExportZipAsync(
                    projectPath,
                    project,
                    archivePath,
                    overwrite: true)
                .GetAwaiter()
                .GetResult();
            Check(
                result.ArchiveBytes > 0 &&
                new FileInfo(archivePath).Length == result.ArchiveBytes,
                "Explicit package overwrite did not publish the verified archive.");
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(output);
        }
    }

    private static void TestDestinationCannotOverwriteResource()
    {
        var root = TempDirectory("cranecan-package-collision");

        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var reportPath = Path.Combine(root, "report.zip");
            File.WriteAllText(reportPath, "registered resource");

            var project = CraneProjectCodec.Create("collision", "machine");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                reportPath,
                CraneProjectResourceKind.Report).Project;
            project = CraneProjectCodec.Save(projectPath, project);

            var original = File.ReadAllBytes(reportPath);
            var rejected = false;
            try
            {
                _ = CraneProjectPackageExporter.ExportZipAsync(
                        projectPath,
                        project,
                        reportPath,
                        overwrite: true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Check(
                rejected &&
                original.SequenceEqual(File.ReadAllBytes(reportPath)),
                "Package destination was allowed to overwrite a registered project resource.");
        }
        finally
        {
            DeleteDirectory(root);
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
}
