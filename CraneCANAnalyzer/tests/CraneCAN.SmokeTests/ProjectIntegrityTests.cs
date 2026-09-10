using System.Runtime.CompilerServices;
using System.Text.Json;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectIntegrityTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestStrictTraceBindingValidationOnSaveAndLoad();
        TestHealthyPortableProject();
        TestMissingBoundTraceAndStaleBinding();
        TestAmbiguousUnboundDependency();
    }

    private static void TestStrictTraceBindingValidationOnSaveAndLoad()
    {
        var root = TempDirectory("cranecan-integrity-strict");
        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var project = CraneProjectCodec.Create("strict", "machine");
            var invalid = project with
            {
                TraceBindings =
                [
                    new CraneProjectTraceBinding
                    {
                        ExperimentResourceId = Guid.NewGuid(),
                        RepeatNumber = 1,
                        Role = ProjectTraceBindingRole.Reference,
                        TraceResourceId = Guid.NewGuid()
                    }
                ]
            };

            var saveRejected = false;
            try
            {
                _ = CraneProjectCodec.Save(projectPath, invalid);
            }
            catch (FormatException)
            {
                saveRejected = true;
            }
            Check(
                saveRejected,
                "Saving .canproject accepted a binding that points to missing resources.");

            File.WriteAllText(
                projectPath,
                JsonSerializer.Serialize(invalid));
            var loadRejected = false;
            try
            {
                _ = CraneProjectCodec.Load(projectPath);
            }
            catch (FormatException)
            {
                loadRejected = true;
            }
            Check(
                loadRejected,
                "Loading .canproject accepted a binding that points to missing resources.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestHealthyPortableProject()
    {
        var root = TempDirectory("cranecan-integrity-healthy");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "experiments", "boom.canexperiment");
            var tracePath = Path.Combine(root, "traces", "boom.trc");
            File.WriteAllText(tracePath, "trace placeholder");

            var experiment = new GuidedExperiment
            {
                Name = "BOOM_UP",
                ActionName = "BOOM_UP",
                Repeats =
                [
                    new GuidedExperimentRepeat
                    {
                        RepeatNumber = 1,
                        ReferenceSource = new ExperimentTraceSource
                        {
                            Path = "../traces/boom.trc",
                            Bus = "CAN1"
                        },
                        ActionSource = new ExperimentTraceSource
                        {
                            Path = "../traces/boom.trc",
                            Bus = "CAN1"
                        }
                    }
                ]
            };
            GuidedJsonCodec.SaveExperimentAsync(experimentPath, experiment)
                .GetAwaiter()
                .GetResult();

            var project = CraneProjectCodec.Create("healthy", "machine");
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
            project = CraneProjectCodec.Save(projectPath, project);

            var report = ProjectIntegrityAnalyzer.AnalyzeAsync(projectPath, project)
                .GetAwaiter()
                .GetResult();
            Check(
                report.IsHealthy &&
                report.ErrorCount == 0 &&
                report.WarningCount == 0 &&
                report.Issues.Any(issue =>
                    issue.Code == ProjectIntegrityCode.Healthy) &&
                report.Issues.Count(issue =>
                    issue.Code == ProjectIntegrityCode.DependencyResolvedAutomatically) == 2,
                "A portable project with unique Trace resolution was not reported healthy.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestMissingBoundTraceAndStaleBinding()
    {
        var root = TempDirectory("cranecan-integrity-bound");
        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "boom.canexperiment");
            var tracePath = Path.Combine(root, "boom.trc");
            File.WriteAllText(tracePath, "trace placeholder");

            var experiment = new GuidedExperiment
            {
                Name = "BOOM_UP",
                Repeats =
                [
                    new GuidedExperimentRepeat
                    {
                        RepeatNumber = 1,
                        ReferenceSource = new ExperimentTraceSource
                        {
                            Path = "boom.trc"
                        },
                        ActionSource = new ExperimentTraceSource
                        {
                            Path = "boom.trc"
                        }
                    }
                ]
            };
            GuidedJsonCodec.SaveExperimentAsync(experimentPath, experiment)
                .GetAwaiter()
                .GetResult();

            var project = CraneProjectCodec.Create("bound", "machine");
            var experimentUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment);
            project = experimentUpdate.Project;
            var traceUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                tracePath,
                CraneProjectResourceKind.Trace);
            project = traceUpdate.Project;

            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                1,
                ProjectTraceBindingRole.Reference,
                traceUpdate.Resource.ResourceId);
            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                2,
                ProjectTraceBindingRole.Action,
                traceUpdate.Resource.ResourceId);
            project = CraneProjectCodec.Save(projectPath, project);

            File.Delete(tracePath);
            var report = ProjectIntegrityAnalyzer.AnalyzeAsync(projectPath, project)
                .GetAwaiter()
                .GetResult();
            Check(
                report.ErrorCount > 0 &&
                report.Issues.Any(issue =>
                    issue.Code == ProjectIntegrityCode.BoundTraceMissing) &&
                report.Issues.Any(issue =>
                    issue.Code == ProjectIntegrityCode.StaleBinding),
                "Integrity audit did not detect a missing bound Trace and stale repeat binding.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestAmbiguousUnboundDependency()
    {
        var root = TempDirectory("cranecan-integrity-ambiguous");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "a"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "b"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "experiments", "test.canexperiment");
            var traceA = Path.Combine(root, "traces", "a", "capture.trc");
            var traceB = Path.Combine(root, "traces", "b", "capture.trc");
            File.WriteAllText(traceA, "a");
            File.WriteAllText(traceB, "b");

            var experiment = new GuidedExperiment
            {
                Name = "TEST",
                Repeats =
                [
                    new GuidedExperimentRepeat
                    {
                        RepeatNumber = 1,
                        ReferenceSource = new ExperimentTraceSource
                        {
                            Path = "capture.trc"
                        },
                        ActionSource = new ExperimentTraceSource
                        {
                            Path = "capture.trc"
                        }
                    }
                ]
            };
            GuidedJsonCodec.SaveExperimentAsync(experimentPath, experiment)
                .GetAwaiter()
                .GetResult();

            var project = CraneProjectCodec.Create("ambiguous", "machine");
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
            project = CraneProjectCodec.Save(projectPath, project);

            var report = ProjectIntegrityAnalyzer.AnalyzeAsync(projectPath, project)
                .GetAwaiter()
                .GetResult();
            Check(
                report.ErrorCount >= 2 &&
                report.Issues.Count(issue =>
                    issue.Code == ProjectIntegrityCode.DependencyAmbiguous) == 2,
                "Ambiguous same-name unbound TRC dependencies were not reported as errors.");
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
