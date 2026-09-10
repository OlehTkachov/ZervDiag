using System.Runtime.CompilerServices;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

internal static class MachineSignalConfidenceExplanationTests
{
    [ModuleInitializer]
    internal static void RunAtModuleLoad() => Run();

    private static void Run()
    {
        var emptyCandidate = new MachineSignal
        {
            Name = "Empty candidate",
            Confidence = SignalKnowledgeState.Candidate
        };
        var emptyExplanation = MachineSignalConfidenceExplainer.Explain(emptyCandidate);
        Check(emptyExplanation.EvidenceCount == 0 &&
              emptyExplanation.Cautions.Any(text =>
                  text.Contains("нет сохранённых evidence", StringComparison.OrdinalIgnoreCase)) &&
              emptyExplanation.NextSteps.Any(text =>
                  text.Contains("3 независимых incident", StringComparison.OrdinalIgnoreCase)),
            "Candidate without evidence did not explain the missing provenance/next repeatability step.");

        var replayCandidate = new MachineSignal
        {
            Name = "Replay candidate",
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.RepeatedExperiment,
                    CaptureOrigin = "replay",
                    SourceReference = "candidate:replay",
                    Description = "Synthetic replay evidence"
                },
                new SignalEvidence
                {
                    Kind = EvidenceKind.RepeatedExperiment,
                    CaptureOrigin = "replay",
                    SourceReference = "candidate:replay-2",
                    Description = "Second replay repeat"
                }
            ]
        };
        var replayExplanation = MachineSignalConfidenceExplainer.Explain(replayCandidate);
        var replayGroup = replayExplanation.EvidenceGroups.Single(group =>
            group.Kind == EvidenceKind.RepeatedExperiment);
        Check(replayExplanation.ReplayOnly &&
              replayGroup.Count == 2 &&
              replayExplanation.Cautions.Any(text =>
                  text.Contains("Replay", StringComparison.OrdinalIgnoreCase)),
            "Replay-only evidence was not grouped or cautioned correctly.");

        var probable = new MachineSignal
        {
            Name = "Cross incident signal",
            Confidence = SignalKnowledgeState.Probable,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.CrossIncidentSignature,
                    CaptureOrigin = "incident-series",
                    SourceReference = "incident-signature:test",
                    Description = "3/3 stable transition"
                }
            ]
        };
        var probableExplanation = MachineSignalConfidenceExplainer.Explain(probable);
        Check(probableExplanation.HasCrossIncidentEvidence &&
              !probableExplanation.HasIndependentEvidence &&
              probableExplanation.StatusBasis.Contains("Cross-Incident", StringComparison.Ordinal) &&
              probableExplanation.Cautions.Any(text =>
                  text.Contains("независим", StringComparison.OrdinalIgnoreCase)) &&
              probableExplanation.NextSteps.Any(text =>
                  text.Contains("CONFIRMED", StringComparison.Ordinal)),
            "PROBABLE Cross-Incident evidence did not explain its limit or confirmation step.");

        var confirmed = new MachineSignal
        {
            Name = "Physically confirmed",
            Confidence = SignalKnowledgeState.Confirmed,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.PhysicalOutputCheck,
                    CaptureOrigin = "livePcan",
                    SourceReference = "physical:test",
                    Description = "Physical output followed the command"
                },
                new SignalEvidence
                {
                    Kind = EvidenceKind.UserConfirmation,
                    CaptureOrigin = "unknown",
                    SourceReference = "user:test",
                    Description = "Engineer confirmed"
                }
            ]
        };
        var confirmedExplanation = MachineSignalConfidenceExplainer.Explain(confirmed);
        Check(confirmedExplanation.HasExplicitConfirmation &&
              confirmedExplanation.HasIndependentEvidence &&
              !confirmedExplanation.ReplayOnly &&
              !confirmedExplanation.Cautions.Any(text =>
                  text.Contains("без evidence типа UserConfirmation", StringComparison.OrdinalIgnoreCase)),
            "CONFIRMED signal with explicit/independent evidence was incorrectly cautioned.");

        var legacyConfirmed = new MachineSignal
        {
            Name = "Legacy confirmed",
            Confidence = SignalKnowledgeState.Confirmed,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.IncidentCapture,
                    CaptureOrigin = "livePcan",
                    SourceReference = "incident:test"
                }
            ]
        };
        var legacyExplanation = MachineSignalConfidenceExplainer.Explain(legacyConfirmed);
        Check(!legacyExplanation.HasExplicitConfirmation &&
              legacyExplanation.Cautions.Any(text =>
                  text.Contains("UserConfirmation", StringComparison.Ordinal)) &&
              legacyExplanation.StatusBasis.Contains("требуется проверка", StringComparison.OrdinalIgnoreCase),
            "CONFIRMED without explicit confirmation was not flagged as legacy/traceability risk.");

        var duplicateEvidence = new MachineSignal
        {
            Name = "Duplicate refs",
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.IncidentCapture,
                    SourceReference = "same"
                },
                new SignalEvidence
                {
                    Kind = EvidenceKind.IncidentCapture,
                    SourceReference = "same"
                }
            ]
        };
        var duplicateExplanation = MachineSignalConfidenceExplainer.Explain(duplicateEvidence);
        Check(duplicateExplanation.Cautions.Any(text =>
                text.Contains("Дубликаты", StringComparison.OrdinalIgnoreCase)),
            "Duplicate Kind + SourceReference evidence was not flagged.");

        var allKinds = Enum.GetValues<EvidenceKind>();
        var mappingProbe = new MachineSignal
        {
            Name = "Mapping probe",
            Confidence = SignalKnowledgeState.Candidate,
            Evidence = allKinds
                .Select((kind, index) => new SignalEvidence
                {
                    Kind = kind,
                    SourceReference = $"mapping:{index}"
                })
                .ToList()
        };
        var mappingExplanation = MachineSignalConfidenceExplainer.Explain(mappingProbe);
        Check(mappingExplanation.EvidenceGroups.Count == allKinds.Length &&
              mappingExplanation.EvidenceGroups.All(group =>
                  !string.IsNullOrWhiteSpace(group.Title) &&
                  !string.IsNullOrWhiteSpace(group.Interpretation)),
            "Not every EvidenceKind has a visible confidence explanation.");

        Check(probable.Confidence == SignalKnowledgeState.Probable &&
              confirmed.Confidence == SignalKnowledgeState.Confirmed,
            "Confidence explainer mutated the stored Machine Profile status.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
