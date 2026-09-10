using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

internal static class IncidentSignatureProfileTests
{
    public static async Task RunAsync()
    {
        var ids = new[]
        {
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333")
        };

        var high = Candidate(
            IncidentSignaturePriority.High,
            occurrenceCount: 3,
            incidentCount: 3,
            agreementCount: 3,
            dataIndex: 2);

        var definition = new IncidentSignatureSignalDefinition
        {
            Name = "Valve enable",
            StartByte = 2,
            StartBit = 0,
            BitLength = 8,
            Scale = 1,
            Offset = 0,
            Notes = "3/3 incident signature"
        };

        var added = IncidentSignatureProfileService.AddOrUpdate(
            new MachineProfile { MachineName = "Test crane" },
            high,
            ids,
            definition);

        Check(!added.ExistingSignalUpdated &&
              added.Signal.Confidence == SignalKnowledgeState.Probable &&
              added.ResultingConfidence == SignalKnowledgeState.Probable &&
              added.Profile.ExperimentalSignals.Count == 1 &&
              added.Profile.KnownSignals.Count == 0,
            "HIGH Cross-Incident signature was not added as PROBABLE.");

        var evidence = added.Signal.Evidence.Single();
        Check(evidence.Kind == EvidenceKind.CrossIncidentSignature &&
              evidence.CaptureOrigin == "incident-series" &&
              evidence.Description.Contains("3/3", StringComparison.Ordinal) &&
              evidence.Description.Contains("30", StringComparison.Ordinal) &&
              ids.All(id => evidence.SourceReference!.Contains(
                  id.ToString("N"),
                  StringComparison.Ordinal)),
            "Cross-Incident evidence/provenance is incomplete.");

        var sameSeriesReordered = IncidentSignatureProfileService.AddOrUpdate(
            added.Profile,
            high,
            ids.Reverse().ToArray(),
            definition);
        Check(sameSeriesReordered.ExistingSignalUpdated &&
              sameSeriesReordered.Signal.SignalId == added.Signal.SignalId &&
              sameSeriesReordered.Signal.Evidence.Count == 1,
            "The same incident series duplicated Cross-Incident evidence.");

        var candidateExisting = new MachineSignal
        {
            Name = "Existing engineering name",
            CanId = 0x123,
            IsExtended = false,
            StartByte = 2,
            StartBit = 0,
            BitLength = 8,
            ByteOrder = SignalByteOrder.BigEndian,
            Scale = 2,
            Offset = 5,
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.IncidentCapture,
                    Description = "single incident"
                }
            ]
        };
        var promoted = IncidentSignatureProfileService.AddOrUpdate(
            new MachineProfile
            {
                ExperimentalSignals = [candidateExisting]
            },
            high,
            ids,
            definition with { Name = "Should not overwrite" });

        Check(promoted.ExistingSignalUpdated &&
              promoted.Signal.SignalId == candidateExisting.SignalId &&
              promoted.Signal.Name == "Existing engineering name" &&
              promoted.Signal.ByteOrder == SignalByteOrder.BigEndian &&
              promoted.Signal.Scale == 2 &&
              promoted.Signal.Offset == 5 &&
              promoted.PreviousConfidence == SignalKnowledgeState.Candidate &&
              promoted.ResultingConfidence == SignalKnowledgeState.Probable &&
              promoted.Signal.Confidence == SignalKnowledgeState.Probable,
            "HIGH signature did not safely promote existing CANDIDATE to PROBABLE.");

        var confirmed = candidateExisting with
        {
            Confidence = SignalKnowledgeState.Confirmed
        };
        var confirmedUpdate = IncidentSignatureProfileService.AddOrUpdate(
            new MachineProfile
            {
                KnownSignals = [confirmed]
            },
            high,
            ids,
            definition);
        Check(confirmedUpdate.Signal.Confidence == SignalKnowledgeState.Confirmed &&
              confirmedUpdate.Profile.KnownSignals.Count == 1 &&
              confirmedUpdate.Profile.ExperimentalSignals.Count == 0,
            "Cross-Incident evidence incorrectly downgraded or moved CONFIRMED signal.");

        var medium = Candidate(
            IncidentSignaturePriority.Medium,
            occurrenceCount: 2,
            incidentCount: 3,
            agreementCount: 2,
            dataIndex: 3);
        var mediumAdded = IncidentSignatureProfileService.AddOrUpdate(
            new MachineProfile(),
            medium,
            ids,
            definition with
            {
                Name = "Medium candidate",
                StartByte = 3
            });
        Check(mediumAdded.Signal.Confidence == SignalKnowledgeState.Candidate,
            "MEDIUM Cross-Incident signature was incorrectly promoted to PROBABLE.");

        var infoRejected = false;
        try
        {
            IncidentSignatureProfileService.AddOrUpdate(
                new MachineProfile(),
                Candidate(
                    IncidentSignaturePriority.Info,
                    occurrenceCount: 1,
                    incidentCount: 3,
                    agreementCount: 1,
                    dataIndex: 2),
                ids,
                definition);
        }
        catch (InvalidOperationException)
        {
            infoRejected = true;
        }
        Check(infoRejected,
            "INFO Cross-Incident signature was allowed to pollute Machine Profile.");

        var duplicateIdsRejected = false;
        try
        {
            IncidentSignatureProfileService.AddOrUpdate(
                new MachineProfile(),
                high,
                [ids[0], ids[0], ids[1]],
                definition);
        }
        catch (ArgumentException)
        {
            duplicateIdsRejected = true;
        }
        Check(duplicateIdsRejected,
            "Cross-Incident evidence accepted fewer than three distinct Incident IDs.");

        var mismatchedSeriesRejected = false;
        try
        {
            IncidentSignatureProfileService.AddOrUpdate(
                new MachineProfile(),
                high,
                [ids[0], ids[1], ids[2], Guid.Parse("44444444-4444-4444-4444-444444444444")],
                definition);
        }
        catch (ArgumentException)
        {
            mismatchedSeriesRejected = true;
        }
        Check(mismatchedSeriesRejected,
            "Cross-Incident evidence accepted Incident IDs inconsistent with candidate.IncidentCount.");

        var nonOverlappingFieldRejected = false;
        try
        {
            IncidentSignatureProfileService.AddOrUpdate(
                new MachineProfile(),
                high,
                ids,
                definition with
                {
                    StartByte = 4,
                    BitLength = 8
                });
        }
        catch (ArgumentException)
        {
            nonOverlappingFieldRejected = true;
        }
        Check(nonOverlappingFieldRejected,
            "Cross-Incident profile builder accepted a field outside changed DATA[n].");

        var messageLevelRejected = false;
        try
        {
            IncidentSignatureProfileService.AddOrUpdate(
                new MachineProfile(),
                high with
                {
                    DataIndex = null,
                    Kind = IncidentTransitionKind.IdAppeared
                },
                ids,
                definition);
        }
        catch (InvalidOperationException)
        {
            messageLevelRejected = true;
        }
        Check(messageLevelRejected,
            "Message-level signature was incorrectly converted into a byte signal.");

        var folder = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-signature-profile-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var profilePath = Path.Combine(folder, "profile.craneprofile");
        try
        {
            await GuidedJsonCodec.SaveProfileAsync(
                profilePath,
                promoted.Profile);
            var restored = await GuidedJsonCodec.LoadProfileAsync(profilePath);
            var restoredSignal = restored.ExperimentalSignals.Single();
            Check(restoredSignal.Confidence == SignalKnowledgeState.Probable &&
                  restoredSignal.Evidence.Any(item =>
                      item.Kind == EvidenceKind.CrossIncidentSignature),
                "CrossIncidentSignature evidence/status did not survive .craneprofile round-trip.");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static IncidentSignatureCandidate Candidate(
        IncidentSignaturePriority priority,
        int occurrenceCount,
        int incidentCount,
        int agreementCount,
        int dataIndex) =>
        new(
            0x123,
            false,
            dataIndex,
            IncidentTransitionKind.ByteChanged,
            priority,
            occurrenceCount,
            incidentCount,
            100.0 * occurrenceCount / incidentCount,
            -100,
            -120,
            -90,
            30,
            "0x00",
            "0x01",
            agreementCount,
            100.0 * agreementCount / occurrenceCount,
            0,
            [],
            "test");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
