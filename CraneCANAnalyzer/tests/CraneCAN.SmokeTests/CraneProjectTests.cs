using System.Runtime.CompilerServices;
using CraneCAN.Core.Projects;
using CraneCAN.Core.Storage;

internal static class CraneProjectTests
{
    [ModuleInitializer]
    internal static void Initialize() =>
        RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-project-tests-{Guid.NewGuid():N}");
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-project-outside-{Guid.NewGuid():N}.trc");
        Directory.CreateDirectory(root);

        try
        {
            var projectPath = Path.Combine(root, "machine.canproject");
            var profilePath = Path.Combine(root, "machine.craneprofile");
            var experimentDirectory = Path.Combine(root, "experiments");
            Directory.CreateDirectory(experimentDirectory);
            var experimentPath = Path.Combine(
                experimentDirectory,
                "telescope.canexperiment");
            var incidentDirectory = Path.Combine(root, "incident_001");
            Directory.CreateDirectory(incidentDirectory);
            var incidentPath = Path.Combine(
                incidentDirectory,
                "incident.canincident");
            var reportPath = Path.Combine(root, "signature.md");

            await File.WriteAllTextAsync(profilePath, "profile");
            await File.WriteAllTextAsync(experimentPath, "experiment");
            await File.WriteAllTextAsync(incidentPath, "incident");
            await File.WriteAllTextAsync(reportPath, "report");
            await File.WriteAllTextAsync(outside, "outside");

            var profileBytes = await File.ReadAllBytesAsync(profilePath);

            var project = CraneProjectCodec.Create(
                "JK1200A diagnostics",
                "SOOSAN JK1200A",
                "SOOSAN",
                "JK1200A",
                "JCH4",
                250_000);

            var profileUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                profilePath,
                CraneProjectResourceKind.MachineProfile,
                "Machine profile",
                "active profile",
                setActive: true);
            project = profileUpdate.Project;

            var experimentUpdate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                experimentPath,
                CraneProjectResourceKind.GuidedExperiment,
                "TELESCOPE_OUT",
                "guided experiment",
                setActive: true);
            project = experimentUpdate.Project;

            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                incidentPath,
                role: "fault incident").Project;
            project = CraneProjectCodec.AddResource(
                project,
                projectPath,
                reportPath).Project;

            var duplicate = CraneProjectCodec.AddResource(
                project,
                projectPath,
                profilePath,
                CraneProjectResourceKind.MachineProfile,
                "Updated label",
                "active profile",
                setActive: true);
            project = duplicate.Project;

            Check(
                duplicate.ExistingResourceUpdated &&
                duplicate.Resource.ResourceId ==
                    profileUpdate.Resource.ResourceId &&
                project.Resources.Count == 4,
                "Duplicate project resource was not de-duplicated.");

            project = await CraneProjectCodec.SaveAsync(
                projectPath,
                project);
            var restored = await CraneProjectCodec.LoadAsync(projectPath);

            Check(
                restored.ProjectId == project.ProjectId &&
                restored.SchemaVersion ==
                    CraneProject.CurrentSchemaVersion &&
                restored.ProgramVersion == "0.7.0" &&
                restored.Name == "JK1200A diagnostics" &&
                restored.MachineName == "SOOSAN JK1200A" &&
                restored.Bitrate == 250_000 &&
                restored.Resources.Count == 4,
                "Crane project JSON round-trip failed.");

            Check(
                restored.ActiveProfileResourceId ==
                    profileUpdate.Resource.ResourceId &&
                restored.ActiveExperimentResourceId ==
                    experimentUpdate.Resource.ResourceId,
                "Active project resources did not survive round-trip.");

            var restoredProfile = restored.Resources.Single(item =>
                item.Kind ==
                    CraneProjectResourceKind.MachineProfile);
            Check(
                restoredProfile.RelativePath == "machine.craneprofile" &&
                Path.GetFullPath(
                    CraneProjectCodec.ResolveResourcePath(
                        projectPath,
                        restoredProfile)) ==
                Path.GetFullPath(profilePath),
                "Project resource relative path resolution is incorrect.");

            var restoredExperiment = restored.Resources.Single(item =>
                item.Kind ==
                    CraneProjectResourceKind.GuidedExperiment);
            Check(
                restoredExperiment.RelativePath.Replace('\\', '/') ==
                    "experiments/telescope.canexperiment",
                "Nested project resource was not stored portably.");

            Check(
                CraneProjectCodec.InferKind(profilePath) ==
                    CraneProjectResourceKind.MachineProfile &&
                CraneProjectCodec.InferKind(experimentPath) ==
                    CraneProjectResourceKind.GuidedExperiment &&
                CraneProjectCodec.InferKind(incidentPath) ==
                    CraneProjectResourceKind.Incident &&
                CraneProjectCodec.InferKind("capture.trc") ==
                    CraneProjectResourceKind.Trace &&
                CraneProjectCodec.InferKind("notes.pdf") ==
                    CraneProjectResourceKind.Document,
                "Project resource extension classification is incorrect.");

            File.Delete(reportPath);
            var missing = CraneProjectCodec.GetMissingResources(
                projectPath,
                restored);
            Check(
                missing.Count == 1 &&
                missing[0].Kind ==
                    CraneProjectResourceKind.Report,
                "Missing project resource was not reported.");

            Check(
                profileBytes.SequenceEqual(
                    await File.ReadAllBytesAsync(profilePath)),
                "Saving .canproject modified an original resource.");

            var outsideRejected = false;
            try
            {
                _ = CraneProjectCodec.AddResource(
                    restored,
                    projectPath,
                    outside);
            }
            catch (InvalidOperationException)
            {
                outsideRejected = true;
            }
            Check(
                outsideRejected,
                "Resource outside the project folder was accepted.");

            var traversalRejected = false;
            try
            {
                CraneProjectCodec.Validate(restored with
                {
                    Resources =
                    [
                        new CraneProjectResource
                        {
                            Kind = CraneProjectResourceKind.Trace,
                            RelativePath = "../escape.trc"
                        }
                    ],
                    ActiveProfileResourceId = null,
                    ActiveExperimentResourceId = null
                });
            }
            catch (FormatException)
            {
                traversalRejected = true;
            }
            Check(
                traversalRejected,
                "Traversal path in .canproject was accepted.");

            var rootedRejected = false;
            try
            {
                CraneProjectCodec.Validate(restored with
                {
                    Resources =
                    [
                        new CraneProjectResource
                        {
                            Kind = CraneProjectResourceKind.Trace,
                            RelativePath = @"C:\outside\capture.trc"
                        }
                    ],
                    ActiveProfileResourceId = null,
                    ActiveExperimentResourceId = null
                });
            }
            catch (FormatException)
            {
                rootedRejected = true;
            }
            Check(
                rootedRejected,
                "Rooted path in .canproject was accepted.");

            var duplicatePathRejected = false;
            try
            {
                CraneProjectCodec.Validate(restored with
                {
                    Resources =
                    [
                        new CraneProjectResource
                        {
                            Kind =
                                CraneProjectResourceKind.MachineProfile,
                            RelativePath = "machine.craneprofile"
                        },
                        new CraneProjectResource
                        {
                            Kind = CraneProjectResourceKind.Other,
                            RelativePath = "MACHINE.CRANEPROFILE"
                        }
                    ],
                    ActiveProfileResourceId = null,
                    ActiveExperimentResourceId = null
                });
            }
            catch (FormatException)
            {
                duplicatePathRejected = true;
            }
            Check(
                duplicatePathRejected,
                "Case-insensitive duplicate resource path was accepted.");

            var invalidSchemaPath = Path.Combine(
                root,
                "future.canproject");
            await File.WriteAllTextAsync(
                invalidSchemaPath,
                """
                {
                  "projectId": "11111111-1111-1111-1111-111111111111",
                  "schemaVersion": 99,
                  "name": "future",
                  "resources": []
                }
                """);

            var schemaRejected = false;
            try
            {
                _ = await CraneProjectCodec.LoadAsync(
                    invalidSchemaPath);
            }
            catch (NotSupportedException)
            {
                schemaRejected = true;
            }
            Check(
                schemaRejected,
                "Unsupported .canproject schema was accepted.");

            var removed = CraneProjectCodec.RemoveResource(
                restored,
                profileUpdate.Resource.ResourceId);
            Check(
                removed.Resources.Count == 3 &&
                removed.ActiveProfileResourceId is null &&
                removed.ActiveExperimentResourceId ==
                    experimentUpdate.Resource.ResourceId,
                "Removing a project resource did not clear only its active reference.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (File.Exists(outside))
                File.Delete(outside);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
