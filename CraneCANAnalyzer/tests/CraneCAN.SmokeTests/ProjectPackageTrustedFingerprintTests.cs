using System.Runtime.CompilerServices;
using CraneCAN.Core.Storage;

internal static class ProjectPackageTrustedFingerprintTests
{
    public static void Run()
    {
        TestRoundTripAndTrustedMatch();
        TestArchiveHashMismatch();
        TestProjectIdMismatch();
        TestInternalManifestMismatch();
        TestArchiveRenameDoesNotBreakTrustedMatch();
        TestInvalidShaRejected();
        TestSidecarPath();
    }

    private static void TestRoundTripAndTrustedMatch()
    {
        var root = NewTempDirectory();
        try
        {
            var archivePath = Path.Combine(root, "SOOSAN_package.zip");
            File.WriteAllBytes(archivePath, new byte[] { 1, 2, 3 });
            var projectId = Guid.NewGuid();
            var archiveSha = new string('a', 64);
            var manifestSha = new string('b', 64);

            var fingerprint =
                CraneProjectPackageTrustedFingerprintCodec.Create(
                    archivePath,
                    projectId,
                    "SOOSAN.canproject",
                    123,
                    archiveSha,
                    manifestSha,
                    new DateTimeOffset(2026, 9, 10, 9, 30, 0, TimeSpan.Zero));

            var sidecar =
                CraneProjectPackageTrustedFingerprintCodec.GetSidecarPath(
                    archivePath);
            CraneProjectPackageTrustedFingerprintCodec.Save(
                sidecar,
                fingerprint);
            var loaded =
                CraneProjectPackageTrustedFingerprintCodec.Load(sidecar);

            Check(loaded == fingerprint, "Trusted fingerprint round-trip failed.");

            var verification = Verification(
                archivePath,
                projectId,
                "SOOSAN.canproject",
                123,
                archiveSha,
                manifestSha);

            var comparison =
                CraneProjectPackageTrustedFingerprintCodec.Compare(
                    sidecar,
                    loaded,
                    verification);

            Check(comparison.IsMatch, "Expected external trusted fingerprint MATCH.");
            Check(comparison.Errors.Count == 0, "MATCH must not contain errors.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestArchiveHashMismatch()
    {
        var projectId = Guid.NewGuid();
        var fingerprint =
            CraneProjectPackageTrustedFingerprintCodec.Create(
                "trusted.zip",
                projectId,
                "machine.canproject",
                10,
                new string('1', 64),
                new string('2', 64));

        var verification = Verification(
            "trusted.zip",
            projectId,
            "machine.canproject",
            10,
            new string('3', 64),
            new string('2', 64));

        var comparison =
            CraneProjectPackageTrustedFingerprintCodec.Compare(
                "trusted.zip.cranefingerprint.json",
                fingerprint,
                verification);

        Check(!comparison.IsMatch, "Changed ZIP SHA-256 must be rejected.");
        Check(
            comparison.Errors.Any(error =>
                error.Contains("ZIP SHA-256 mismatch", StringComparison.Ordinal)),
            "ZIP SHA mismatch reason was not reported.");
    }

    private static void TestProjectIdMismatch()
    {
        var fingerprint =
            CraneProjectPackageTrustedFingerprintCodec.Create(
                "trusted.zip",
                Guid.NewGuid(),
                "machine.canproject",
                10,
                new string('1', 64),
                new string('2', 64));

        var verification = Verification(
            "trusted.zip",
            Guid.NewGuid(),
            "machine.canproject",
            10,
            new string('1', 64),
            new string('2', 64));

        var comparison =
            CraneProjectPackageTrustedFingerprintCodec.Compare(
                "trusted.zip.cranefingerprint.json",
                fingerprint,
                verification);

        Check(!comparison.IsMatch, "Changed Project ID must be rejected.");
        Check(
            comparison.Errors.Any(error =>
                error.Contains("Project ID mismatch", StringComparison.Ordinal)),
            "Project ID mismatch reason was not reported.");
    }

    private static void TestInternalManifestMismatch()
    {
        var projectId = Guid.NewGuid();
        var fingerprint =
            CraneProjectPackageTrustedFingerprintCodec.Create(
                "trusted.zip",
                projectId,
                "machine.canproject",
                10,
                new string('1', 64),
                new string('2', 64));

        var verification = Verification(
            "trusted.zip",
            projectId,
            "machine.canproject",
            10,
            new string('1', 64),
            new string('4', 64));

        var comparison =
            CraneProjectPackageTrustedFingerprintCodec.Compare(
                "trusted.zip.cranefingerprint.json",
                fingerprint,
                verification);

        Check(!comparison.IsMatch, "Changed internal manifest SHA-256 must be rejected.");
        Check(
            comparison.Errors.Any(error =>
                error.Contains(
                    "Internal manifest SHA-256 mismatch",
                    StringComparison.Ordinal)),
            "Internal manifest mismatch reason was not reported.");
    }

    private static void TestArchiveRenameDoesNotBreakTrustedMatch()
    {
        var projectId = Guid.NewGuid();
        var fingerprint =
            CraneProjectPackageTrustedFingerprintCodec.Create(
                "original.zip",
                projectId,
                "machine.canproject",
                10,
                new string('1', 64),
                new string('2', 64));

        var verification = Verification(
            "renamed.zip",
            projectId,
            "machine.canproject",
            10,
            new string('1', 64),
            new string('2', 64));

        var comparison =
            CraneProjectPackageTrustedFingerprintCodec.Compare(
                "trusted.cranefingerprint.json",
                fingerprint,
                verification);

        Check(
            comparison.IsMatch,
            "Renaming an otherwise identical ZIP must not invalidate content trust.");
    }

    private static void TestInvalidShaRejected()
    {
        var bad = new CraneProjectPackageTrustedFingerprint
        {
            ProjectId = Guid.NewGuid(),
            ProjectFileName = "machine.canproject",
            ArchiveFileName = "machine.zip",
            ArchiveBytes = 10,
            ArchiveSha256 = "not-a-sha256",
            PackageManifestSha256 = new string('2', 64),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var rejected = false;
        try
        {
            CraneProjectPackageTrustedFingerprintCodec.Validate(bad);
        }
        catch (FormatException)
        {
            rejected = true;
        }

        Check(rejected, "Malformed SHA-256 must be rejected.");
    }

    private static void TestSidecarPath()
    {
        var path =
            CraneProjectPackageTrustedFingerprintCodec.GetSidecarPath(
                Path.Combine(Path.GetTempPath(), "machine.zip"));

        Check(
            path.EndsWith(
                "machine.zip.cranefingerprint.json",
                StringComparison.OrdinalIgnoreCase),
            "Unexpected trusted fingerprint sidecar path.");
    }

    private static ProjectPackageVerificationResult Verification(
        string archivePath,
        Guid projectId,
        string projectFileName,
        long archiveBytes,
        string archiveSha,
        string manifestSha) =>
        new(
            Path.GetFullPath(archivePath),
            true,
            projectId,
            "Machine",
            0,
            2,
            100,
            archiveBytes,
            archiveSha,
            manifestSha,
            new[]
            {
                new ProjectPackageVerificationFile(
                    projectFileName,
                    50,
                    50,
                    new string('f', 64),
                    new string('f', 64),
                    true)
            },
            Array.Empty<string>());

    private static string NewTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CraneCAN-trusted-fingerprint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal static class ProjectPackageTrustedFingerprintTestInitializer
{
    [ModuleInitializer]
    public static void Initialize() =>
        ProjectPackageTrustedFingerprintTests.Run();
}
