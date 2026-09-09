using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

internal static class IncidentSignalBuilderTests
{
    public static async Task RunAsync()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var step = new IncidentEventChainStep(
            1,
            125,
            null,
            0x123,
            false,
            2,
            IncidentTransitionKind.ByteChanged,
            IncidentTransitionPriority.High,
            "0x00",
            "0x80",
            [],
            null,
            false,
            string.Empty,
            "test");

        var definition = new IncidentSignalDefinition
        {
            Name = "Valve enable",
            StartByte = 2,
            StartBit = 0,
            BitLength = 8,
            Scale = 1,
            Offset = 0,
            Unit = string.Empty,
            Notes = "single incident candidate"
        };

        var added = IncidentSignalProfileService.AddOrUpdate(
            new MachineProfile { MachineName = "Test crane" },
            step,
            marker,
            definition);

        Check(!added.ExistingSignalUpdated &&
              added.Signal.Confidence == SignalKnowledgeState.Candidate &&
              added.Profile.ExperimentalSignals.Count == 1 &&
              added.Profile.KnownSignals.Count == 0,
            "Incident Signal Builder did not add a new CANDIDATE.");

        var evidence = added.Signal.Evidence.Single();
        Check(evidence.Kind == EvidenceKind.IncidentCapture &&
              evidence.CaptureOrigin == "incident" &&
              evidence.Description.Contains("одиночное", StringComparison.OrdinalIgnoreCase),
            "Incident-specific evidence was not preserved.");

        var repeatedSameIncident = IncidentSignalProfileService.AddOrUpdate(
            added.Profile,
            step,
            marker,
            definition);
        Check(repeatedSameIncident.ExistingSignalUpdated &&
              repeatedSameIncident.Signal.SignalId == added.Signal.SignalId &&
              repeatedSameIncident.Signal.Evidence.Count == 1,
            "Reopening the same incident duplicated evidence.");

        var secondIncident = IncidentSignalProfileService.AddOrUpdate(
            repeatedSameIncident.Profile,
            step with { ReactionMilliseconds = 210 },
            marker.AddMinutes(1),
            definition);
        Check(secondIncident.Signal.SignalId == added.Signal.SignalId &&
              secondIncident.Signal.Evidence.Count == 2,
            "Independent incident evidence did not merge into the same signal location.");

        var confirmed = added.Signal with
        {
            Confidence = SignalKnowledgeState.Confirmed,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.UserConfirmation,
                    Description = "confirmed"
                }
            ]
        };
        var confirmedProfile = new MachineProfile
        {
            KnownSignals = [confirmed],
            ExperimentalSignals = []
        };
        var confirmedUpdate = IncidentSignalProfileService.AddOrUpdate(
            confirmedProfile,
            step,
            marker.AddHours(1),
            definition);
        Check(confirmedUpdate.Signal.Confidence == SignalKnowledgeState.Confirmed &&
              confirmedUpdate.Profile.KnownSignals.Count == 1 &&
              confirmedUpdate.Profile.ExperimentalSignals.Count == 0,
            "Incident evidence incorrectly downgraded an existing CONFIRMED signal.");

        var invalidRangeRejected = false;
        try
        {
            IncidentSignalProfileService.AddOrUpdate(
                new MachineProfile(),
                step,
                marker,
                definition with { StartByte = 5, BitLength = 8 });
        }
        catch (ArgumentException)
        {
            invalidRangeRejected = true;
        }
        Check(invalidRangeRejected,
            "Signal Builder accepted a field that does not cover the changed DATA byte.");

        var messageStepRejected = false;
        try
        {
            IncidentSignalProfileService.AddOrUpdate(
                new MachineProfile(),
                step with
                {
                    DataIndex = null,
                    Kind = IncidentTransitionKind.IdAppeared
                },
                marker,
                definition);
        }
        catch (InvalidOperationException)
        {
            messageStepRejected = true;
        }
        Check(messageStepRejected,
            "Signal Builder accepted a message-level incident event as a byte signal.");

        var folder = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-incident-signal-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "profile.craneprofile");
        try
        {
            await GuidedJsonCodec.SaveProfileAsync(path, secondIncident.Profile);
            var restored = await GuidedJsonCodec.LoadProfileAsync(path);
            Check(restored.ExperimentalSignals.Single().Evidence.Count == 2 &&
                  restored.ExperimentalSignals.Single().Evidence.All(item =>
                      item.Kind == EvidenceKind.IncidentCapture),
                "IncidentCapture evidence did not survive .craneprofile JSON round-trip.");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
