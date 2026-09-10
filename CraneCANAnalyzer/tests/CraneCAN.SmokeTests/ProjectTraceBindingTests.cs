using System.Runtime.CompilerServices;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class ProjectTraceBindingTests
{
    [ModuleInitializer]
    internal static void Initialize() => Run();

    private static void Run()
    {
        TestBindingRoundTripAndProjectMove();
        TestBindingDisambiguatesDuplicateTraceNames();
        TestMissingBoundTraceDoesNotFallback();
        TestBindingValidationAndCleanup();
    }

    private static void TestBindingRoundTripAndProjectMove()
    {
        var root = TempDirectory("cranecan-binding-portable");
        var moved = root + "-moved";
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "experiments", "boom.canexperiment");
            var tracePath = Path.Combine(root, "traces", "boom.trc");
            File.WriteAllText(experimentPath, "experiment placeholder");
            File.WriteAllText(tracePath, "trace placeholder");

            var project = CraneProjectCodec.Create("binding portable", "machine");
            var experimentUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment,
                setActive: true);
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
            project = CraneProjectCodec.Save(projectPath, project);

            var restored = CraneProjectCodec.Load(projectPath);
            Check(
                restored.TraceBindings.Count == 1 &&
                restored.TraceBindings[0].ExperimentResourceId == experimentUpdate.Resource.ResourceId &&
                restored.TraceBindings[0].TraceResourceId == traceUpdate.Resource.ResourceId,
                "Persistent TRC binding did not survive .canproject round-trip.");

            var legacy = Path.Combine("Z:\\old-pc", "boom.trc");
            var beforeMove = ProjectTraceBindingResolver.Resolve(
                experimentPath,
                1,
                ProjectTraceBindingRole.Reference,
                legacy);
            Check(
                beforeMove.CanLoad &&
                beforeMove.ReboundToProject &&
                PathsEqual(beforeMove.ResolvedPath!, tracePath) &&
                beforeMove.Message.Contains("сохранённая", StringComparison.OrdinalIgnoreCase),
                "Persistent binding was not preferred before project move.");

            Directory.Move(root, moved);
            var movedExperiment = Path.Combine(moved, "experiments", "boom.canexperiment");
            var movedTrace = Path.Combine(moved, "traces", "boom.trc");
            var afterMove = ProjectTraceBindingResolver.Resolve(
                movedExperiment,
                1,
                ProjectTraceBindingRole.Reference,
                legacy);
            Check(
                afterMove.CanLoad && PathsEqual(afterMove.ResolvedPath!, movedTrace),
                "Persistent binding did not follow the moved project folder.");

            var manifest = File.ReadAllText(Path.Combine(moved, "machine.canproject"));
            Check(
                !manifest.Contains("old-pc", StringComparison.OrdinalIgnoreCase) &&
                manifest.Contains("traceBindings", StringComparison.Ordinal),
                ".canproject leaked the legacy path or omitted persistent bindings.");
        }
        finally
        {
            ProjectTraceBindingResolver.ClearExperimentContext();
            DeleteDirectory(root);
            DeleteDirectory(moved);
        }
    }

    private static void TestBindingDisambiguatesDuplicateTraceNames()
    {
        var root = TempDirectory("cranecan-binding-duplicate");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "good"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "fault"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "experiments", "test.canexperiment");
            var goodTrace = Path.Combine(root, "traces", "good", "capture.trc");
            var faultTrace = Path.Combine(root, "traces", "fault", "capture.trc");
            File.WriteAllText(experimentPath, "experiment placeholder");
            File.WriteAllText(goodTrace, "good");
            File.WriteAllText(faultTrace, "fault");

            var project = CraneProjectCodec.Create("binding duplicate", "machine");
            var experimentUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment);
            project = experimentUpdate.Project;
            var goodUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                goodTrace,
                CraneProjectResourceKind.Trace);
            project = goodUpdate.Project;
            var faultUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                faultTrace,
                CraneProjectResourceKind.Trace);
            project = faultUpdate.Project;

            var legacy = Path.Combine("Y:\\legacy", "capture.trc");
            CraneProjectCodec.Save(projectPath, project);
            var automatic = ProjectTraceDependencyResolver.Resolve(experimentPath, legacy);
            Check(
                !automatic.CanLoad &&
                automatic.Kind == ProjectTraceResolutionKind.AmbiguousTrace,
                "Duplicate same-name Trace resources were expected to be ambiguous before binding.");

            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                2,
                ProjectTraceBindingRole.Action,
                faultUpdate.Resource.ResourceId);
            CraneProjectCodec.Save(projectPath, project);

            var bound = ProjectTraceBindingResolver.Resolve(
                experimentPath,
                2,
                ProjectTraceBindingRole.Action,
                legacy);
            Check(
                bound.CanLoad && PathsEqual(bound.ResolvedPath!, faultTrace),
                "Persistent binding did not disambiguate duplicate capture.trc resources.");

            var unrelatedRole = ProjectTraceBindingResolver.Resolve(
                experimentPath,
                2,
                ProjectTraceBindingRole.Reference,
                legacy);
            Check(
                !unrelatedRole.CanLoad &&
                unrelatedRole.Kind == ProjectTraceResolutionKind.AmbiguousTrace,
                "Binding for ACTION incorrectly affected REFERENCE dependency.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestMissingBoundTraceDoesNotFallback()
    {
        var root = TempDirectory("cranecan-binding-missing");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "a"));
            Directory.CreateDirectory(Path.Combine(root, "traces", "b"));
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "experiments", "test.canexperiment");
            var traceA = Path.Combine(root, "traces", "a", "capture.trc");
            var traceB = Path.Combine(root, "traces", "b", "capture.trc");
            File.WriteAllText(experimentPath, "experiment placeholder");
            File.WriteAllText(traceA, "a");
            File.WriteAllText(traceB, "b");

            var project = CraneProjectCodec.Create("binding missing", "machine");
            var experimentUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment);
            project = experimentUpdate.Project;
            var traceAUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                traceA,
                CraneProjectResourceKind.Trace);
            project = traceAUpdate.Project;
            var traceBUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                traceB,
                CraneProjectResourceKind.Trace);
            project = traceBUpdate.Project;
            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                1,
                ProjectTraceBindingRole.Reference,
                traceBUpdate.Resource.ResourceId);
            CraneProjectCodec.Save(projectPath, project);

            File.Delete(traceB);
            var result = ProjectTraceBindingResolver.Resolve(
                experimentPath,
                1,
                ProjectTraceBindingRole.Reference,
                Path.Combine("X:\\legacy", "capture.trc"));
            Check(
                !result.CanLoad &&
                result.Kind == ProjectTraceResolutionKind.ProjectResource &&
                result.ResolvedPath is not null &&
                PathsEqual(result.ResolvedPath, traceB) &&
                result.Message.Contains("привязка", StringComparison.OrdinalIgnoreCase),
                "Missing explicitly bound Trace silently fell back to another same-name file.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void TestBindingValidationAndCleanup()
    {
        var root = TempDirectory("cranecan-binding-validation");
        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var experimentPath = Path.Combine(root, "test.canexperiment");
            var tracePath = Path.Combine(root, "test.trc");
            var profilePath = Path.Combine(root, "test.craneprofile");
            File.WriteAllText(experimentPath, "experiment");
            File.WriteAllText(tracePath, "trace");
            File.WriteAllText(profilePath, "profile");

            var project = CraneProjectCodec.Create("binding validation", "machine");
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
            var profileUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                profilePath,
                CraneProjectResourceKind.MachineProfile);
            project = profileUpdate.Project;

            var wrongKindRejected = false;
            try
            {
                _ = ProjectTraceBindingService.SetBinding(
                    project,
                    experimentUpdate.Resource.ResourceId,
                    1,
                    ProjectTraceBindingRole.Reference,
                    profileUpdate.Resource.ResourceId);
            }
            catch (InvalidOperationException)
            {
                wrongKindRejected = true;
            }
            Check(wrongKindRejected, "Binding accepted a non-Trace target resource.");

            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                1,
                ProjectTraceBindingRole.Reference,
                traceUpdate.Resource.ResourceId);
            project = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                1,
                ProjectTraceBindingRole.Reference,
                traceUpdate.Resource.ResourceId);
            Check(
                project.TraceBindings.Count == 1 &&
                ProjectTraceBindingService.ValidateBindings(project).Count == 0,
                "Setting the same binding twice created duplicates or invalid state.");

            var withoutTrace = CraneProjectCodec.RemoveResource(
                project,
                traceUpdate.Resource.ResourceId);
            Check(
                withoutTrace.TraceBindings.Count == 0 &&
                ProjectTraceBindingService.ValidateBindings(withoutTrace).Count == 0,
                "Removing a Trace resource did not automatically remove dependent bindings.");

            var rebound = ProjectTraceBindingService.SetBinding(
                project,
                experimentUpdate.Resource.ResourceId,
                1,
                ProjectTraceBindingRole.Action,
                traceUpdate.Resource.ResourceId);
            var withoutExperiment = CraneProjectCodec.RemoveResource(
                rebound,
                experimentUpdate.Resource.ResourceId);
            Check(
                withoutExperiment.TraceBindings.Count == 0 &&
                ProjectTraceBindingService.ValidateBindings(withoutExperiment).Count == 0,
                "Removing a GuidedExperiment resource did not automatically remove its bindings.");
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
