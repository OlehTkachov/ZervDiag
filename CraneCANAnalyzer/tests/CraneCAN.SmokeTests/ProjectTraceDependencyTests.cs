using System.Runtime.CompilerServices;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectTraceDependencyTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestPortableRebindAfterProjectMove();
        TestAmbiguousTraceIsRejected();
        TestDirectRelativeFallback();
        TestNoContextOriginalPath();
    }

    private static void TestPortableRebindAfterProjectMove()
    {
        var root = TempDirectory("cranecan-project-dependency");
        var moved = root + "-moved";
        var legacy = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-legacy-{Guid.NewGuid():N}",
            "capture.trc");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(
                root,
                "experiments",
                "telescope.canexperiment");
            var tracePath = Path.Combine(root, "traces", "capture.trc");
            File.WriteAllText(experimentPath, "experiment placeholder");
            File.WriteAllText(tracePath, "trace placeholder");

            var project = CraneProjectCodec.Create(
                "portable dependencies",
                "test machine");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment,
                setActive: true).Project;
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                tracePath,
                CraneProjectResourceKind.Trace).Project;
            CraneProjectCodec.Save(projectPath, project);

            ProjectTraceDependencyResolver.SetExperimentContext(experimentPath);
            var first = ProjectTraceDependencyResolver.Resolve(legacy);
            Check(
                first.CanLoad &&
                first.ReboundToProject &&
                first.Kind == ProjectTraceResolutionKind.ProjectResource &&
                PathsEqual(first.ResolvedPath!, tracePath),
                "Missing legacy TRC was not rebound to the unique project Trace resource.");

            Check(
                !File.ReadAllText(projectPath).Contains(
                    Path.GetDirectoryName(legacy)!,
                    StringComparison.OrdinalIgnoreCase),
                ".canproject unexpectedly persisted the legacy absolute TRC path.");

            ProjectTraceDependencyResolver.ClearExperimentContext();
            Directory.Move(root, moved);

            var movedExperiment = Path.Combine(
                moved,
                "experiments",
                "telescope.canexperiment");
            var movedTrace = Path.Combine(moved, "traces", "capture.trc");
            ProjectTraceDependencyResolver.SetExperimentContext(movedExperiment);
            var second = ProjectTraceDependencyResolver.Resolve(legacy);
            Check(
                second.CanLoad &&
                second.ReboundToProject &&
                PathsEqual(second.ResolvedPath!, movedTrace),
                "TRC dependency did not follow the moved .canproject folder.");
        }
        finally
        {
            ProjectTraceDependencyResolver.ClearExperimentContext();
            DeleteDirectory(root);
            DeleteDirectory(moved);
        }
    }

    private static void TestAmbiguousTraceIsRejected()
    {
        var root = TempDirectory("cranecan-project-ambiguous");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "a"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "b"));

            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(
                root,
                "experiments",
                "test.canexperiment");
            var traceA = Path.Combine(root, "traces", "a", "capture.trc");
            var traceB = Path.Combine(root, "traces", "b", "capture.trc");
            File.WriteAllText(experimentPath, "experiment placeholder");
            File.WriteAllText(traceA, "a");
            File.WriteAllText(traceB, "b");

            var project = CraneProjectCodec.Create("ambiguous", "test");
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment).Project;
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                traceA,
                CraneProjectResourceKind.Trace).Project;
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                traceB,
                CraneProjectResourceKind.Trace).Project;
            CraneProjectCodec.Save(projectPath, project);

            var missing = Path.Combine(
                Path.GetTempPath(),
                $"missing-{Guid.NewGuid():N}",
                "capture.trc");
            var result = ProjectTraceDependencyResolver.Resolve(
                experimentPath,
                missing);

            Check(
                !result.CanLoad &&
                result.Kind == ProjectTraceResolutionKind.AmbiguousTrace &&
                result.Candidates?.Count == 2,
                "Ambiguous same-name project traces were guessed instead of rejected.");

            var exact = ProjectTraceDependencyResolver.Resolve(
                experimentPath,
                "traces/a/capture.trc");
            Check(
                exact.CanLoad &&
                exact.ReboundToProject &&
                PathsEqual(exact.ResolvedPath!, traceA),
                "Exact project-relative trace path did not disambiguate duplicate filenames.");
        }
        finally
        {
            ProjectTraceDependencyResolver.ClearExperimentContext();
            DeleteDirectory(root);
        }
    }

    private static void TestDirectRelativeFallback()
    {
        var root = TempDirectory("cranecan-project-relative");
        try
        {
            var experiment = Path.Combine(root, "test.canexperiment");
            var trace = Path.Combine(root, "local.trc");
            File.WriteAllText(experiment, "experiment placeholder");
            File.WriteAllText(trace, "trace placeholder");

            var result = ProjectTraceDependencyResolver.Resolve(
                experiment,
                "local.trc");
            Check(
                result.CanLoad &&
                result.Kind == ProjectTraceResolutionKind.ExperimentRelativePath &&
                PathsEqual(result.ResolvedPath!, trace),
                "Relative TRC next to an experiment was not resolved without a project.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestNoContextOriginalPath()
    {
        var root = TempDirectory("cranecan-project-original");
        try
        {
            var trace = Path.Combine(root, "direct.trc");
            File.WriteAllText(trace, "trace placeholder");
            ProjectTraceDependencyResolver.ClearExperimentContext();

            var result = ProjectTraceDependencyResolver.Resolve(trace);
            Check(
                result.CanLoad &&
                result.Kind == ProjectTraceResolutionKind.OriginalPath &&
                PathsEqual(result.ResolvedPath!, trace),
                "Existing original TRC path stopped working without project context.");
        }
        finally
        {
            ProjectTraceDependencyResolver.ClearExperimentContext();
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

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
