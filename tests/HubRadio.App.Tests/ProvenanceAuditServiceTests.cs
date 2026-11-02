using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Audit;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ProvenanceAuditServiceTests
{
    private const string ActorSid = "S-1-5-21-1000";
    private const string SuggestionContent = "PRIVATE-SUGGESTION-REASON";
    private const string DetectionContent = "PRIVATE-DETECTION-DESCRIPTION";
    private const string MissingContextContent = "PRIVATE-MISSING-CONTEXT";
    private const string WarningContent = "PRIVATE-WARNING";

    private static readonly DateTimeOffset AuditTime =
        new(2033, 4, 5, 6, 7, 8, TimeSpan.FromHours(9));

    [TestMethod]
    // NFR-AUD-001 AC-118: AI（tool・model）が生成した提案と利用者の決定が区別して記録される。
    public void SuggestionDecisionRecordsAllProducersAndOnlyTheirActualDetectionShape()
    {
        using AuditFixture fixture = new();
        SuggestionProducerKind[] producers = Enum.GetValues<SuggestionProducerKind>();
        var expectedRecords = new List<(
            TestSuggestionCandidate Candidate,
            Detection? Detection,
            ModelRun ModelRun,
            SuggestionDecisionRecord Decision)>();

        foreach ((SuggestionProducerKind producer, int index) in
            producers.Select((producer, index) => (producer, index)))
        {
            ModelRun modelRun = CreateModelRun(
                EntityId<ModelRunIdTag>.New(),
                completedAt: AuditTime.AddMinutes(index - 1));
            TestSuggestionCandidate candidate = CreateCandidate(
                producer,
                modelRun.Id,
                Digest(HexDigit(index + 1)));
            SuggestionDecisionRecord decision = CreateDecision(
                candidate,
                SuggestionDecisionKind.Rejected,
                AuditTime.AddMinutes(index),
                $"Human decision {index}.");
            Detection? detection = CreateDetection(candidate);
            expectedRecords.Add((candidate, detection, modelRun, decision));

            AuditEvent auditEvent = fixture.Service.RecordSuggestionDecision(
                EntityId<AuditEventIdTag>.New(),
                fixture.Project.Id,
                candidate,
                detection,
                modelRun,
                decision,
                ReviewActorRole.Editor);

            Assert.AreEqual(AuditEventKind.SuggestionDecisionRecorded, auditEvent.Kind);
            Assert.AreEqual(ActorSid, auditEvent.ActorId);
            Assert.AreEqual(decision.DecidedAt, auditEvent.OccurredAt);
            Assert.AreEqual(fixture.Project.Id, auditEvent.ProjectId);
            Assert.IsNull(auditEvent.CompositionVersionId);
        }

        IReadOnlyList<StoredAuditEvent> stored =
            fixture.ReviewStore.GetProjectAuditEvents(fixture.Project.Id);
        Assert.AreEqual(producers.Length, stored.Count);
        for (int index = 0; index < stored.Count; index++)
        {
            StoredAuditEvent auditEvent = stored[index];
            SuggestionProducerKind producer = producers[index];
            var expected = expectedRecords[index];
            Assert.AreEqual(
                CanonicalJson.Canonicalize(auditEvent.Reason),
                auditEvent.Reason);
            using JsonDocument document = JsonDocument.Parse(auditEvent.Reason);
            JsonElement root = document.RootElement;
            Assert.AreEqual(
                ProvenanceAuditService.SchemaVersion,
                root.GetProperty("schemaVersion").GetString());
            Assert.AreEqual(
                "suggestionDecision",
                root.GetProperty("recordKind").GetString());
            Assert.AreEqual(ActorSid, root.GetProperty("actorId").GetString());
            Assert.AreEqual(
                FormatTimestamp(expected.Decision.DecidedAt),
                root.GetProperty("occurredAt").GetString());
            JsonElement suggestion = root.GetProperty("suggestion");
            Assert.AreEqual(
                expected.Candidate.Id.ToString(),
                suggestion.GetProperty("id").GetString());
            Assert.AreEqual(
                expected.Candidate.Suggestion.ModelRunId.ToString(),
                suggestion.GetProperty("modelRunId").GetString());
            Assert.AreEqual(
                producer.ToString(),
                suggestion.GetProperty("producer").GetString());
            Assert.AreEqual(
                expected.Candidate.Kind.ToString(),
                suggestion.GetProperty("kind").GetString());
            Assert.AreEqual(
                expected.Candidate.SourceDigest.Hex,
                suggestion.GetProperty("sourceDigest").GetString());
            AssertOrigin(suggestion.GetProperty("origin"), expected.Candidate.Origin);

            JsonElement decision = root.GetProperty("decision");
            Assert.AreEqual(
                expected.Decision.Id.ToString(),
                decision.GetProperty("id").GetString());
            Assert.AreEqual(
                "Rejected",
                decision.GetProperty("kind").GetString());
            Assert.AreEqual(
                "Rejected",
                decision.GetProperty("statusAfterDecision").GetString());
            Assert.AreEqual(
                JsonValueKind.Null,
                decision.GetProperty("replacementSuggestionId").ValueKind);
            Assert.AreEqual(
                JsonValueKind.Null,
                decision.GetProperty("adoptedSuggestionId").ValueKind);
            Assert.AreEqual(
                JsonValueKind.Null,
                decision.GetProperty("resultingTranscriptRevisionId").ValueKind);
            Assert.AreEqual(
                $"Human decision {index}.",
                root.GetProperty("reason").GetString());

            JsonElement detection = root.GetProperty("detection");
            if (ProducerRequiresDetection(producer))
            {
                Assert.AreEqual(JsonValueKind.Object, detection.ValueKind);
                Assert.AreEqual(
                    expected.Detection!.Id.ToString(),
                    detection.GetProperty("id").GetString());
                Assert.AreEqual(
                    expected.ModelRun.Id.ToString(),
                    detection.GetProperty("modelRunId").GetString());
                Assert.AreEqual(
                    GetDetectionKind(producer).ToString(),
                    detection.GetProperty("kind").GetString());
                AssertOrigin(
                    detection.GetProperty("origin"),
                    expected.Detection.Origin);
            }
            else
            {
                Assert.AreEqual(JsonValueKind.Null, detection.ValueKind);
            }

            JsonElement model = root.GetProperty("modelRun");
            Assert.AreEqual(
                expected.ModelRun.Id.ToString(),
                model.GetProperty("id").GetString());
            Assert.AreEqual("local-provider", model.GetProperty("provider").GetString());
            Assert.AreEqual("local-model", model.GetProperty("modelIdentifier").GetString());
            Assert.AreEqual("1.2.3", model.GetProperty("modelVersion").GetString());
            Assert.AreEqual("Available", model.GetProperty("modelHashAvailability").GetString());
            Assert.AreEqual(
                expected.ModelRun.ModelHash!.Hex,
                model.GetProperty("modelHash").GetString());
            Assert.AreEqual(
                expected.ModelRun.ConfigurationDigest.Hex,
                model.GetProperty("configurationDigest").GetString());
            Assert.AreEqual(
                expected.ModelRun.InputDigest.Hex,
                model.GetProperty("inputDigest").GetString());
            Assert.AreEqual(
                expected.ModelRun.OutputDigest!.Hex,
                model.GetProperty("outputDigest").GetString());
            Assert.AreEqual("Local", model.GetProperty("executionLocation").GetString());
            Assert.AreEqual("Succeeded", model.GetProperty("outcome").GetString());
            Assert.AreEqual(
                FormatTimestamp(expected.ModelRun.StartedAt),
                model.GetProperty("startedAt").GetString());
            Assert.AreEqual(
                FormatTimestamp(expected.ModelRun.CompletedAt),
                model.GetProperty("completedAt").GetString());
            Assert.IsFalse(ContainsForbiddenContent(auditEvent.Reason));
        }
    }

    [TestMethod]
    public void SuggestionDecisionRecordsCloudNondisclosureAndEveryDecisionTargetShape()
    {
        using AuditFixture fixture = new();
        var records = new List<(SuggestionDecisionKind Kind, StoredAuditEvent Event)>();

        ModelRun cloudRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            executionLocation: ModelExecutionLocation.Cloud,
            modelHashAvailability: ModelHashAvailability.NotDisclosed,
            modelHash: null,
            completedAt: AuditTime.AddMinutes(-1));
        TestSuggestionCandidate adoptedCandidate = CreateCandidate(
            SuggestionProducerKind.Emphasis,
            cloudRun.Id,
            Digest('1'));
        SuggestionDecisionRecord adopted = CreateDecision(
            adoptedCandidate,
            SuggestionDecisionKind.Adopted,
            AuditTime,
            "Adopt the reviewed highlight.");
        fixture.Service.RecordSuggestionDecision(
            EntityId<AuditEventIdTag>.New(),
            fixture.Project.Id,
            adoptedCandidate,
            detection: null,
            cloudRun,
            adopted,
            ReviewActorRole.Editor);

        ModelRun modifiedRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            completedAt: AuditTime);
        TestSuggestionCandidate modifiedCandidate = CreateCandidate(
            SuggestionProducerKind.Repetition,
            modifiedRun.Id,
            Digest('2'));
        SuggestionDecisionRecord modified = CreateDecision(
            modifiedCandidate,
            SuggestionDecisionKind.Modified,
            AuditTime.AddMinutes(1),
            "Replace with a narrower candidate.");
        fixture.Service.RecordSuggestionDecision(
            EntityId<AuditEventIdTag>.New(),
            fixture.Project.Id,
            modifiedCandidate,
            detection: null,
            modifiedRun,
            modified,
            ReviewActorRole.Editor);

        ModelRun termRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            completedAt: AuditTime.AddMinutes(1));
        TestSuggestionCandidate termCandidate = CreateCandidate(
            SuggestionProducerKind.TermCorrection,
            termRun.Id,
            Digest('3'));
        SuggestionDecisionRecord termAdopted = CreateDecision(
            termCandidate,
            SuggestionDecisionKind.Adopted,
            AuditTime.AddMinutes(2),
            "Adopt the verified dictionary correction.");
        fixture.Service.RecordSuggestionDecision(
            EntityId<AuditEventIdTag>.New(),
            fixture.Project.Id,
            termCandidate,
            detection: null,
            termRun,
            termAdopted,
            ReviewActorRole.Editor);

        foreach (StoredAuditEvent auditEvent in
            fixture.ReviewStore.GetProjectAuditEvents(fixture.Project.Id))
        {
            using JsonDocument document = JsonDocument.Parse(auditEvent.Reason);
            SuggestionDecisionKind kind = Enum.Parse<SuggestionDecisionKind>(
                document.RootElement
                    .GetProperty("decision")
                    .GetProperty("kind")
                    .GetString()!);
            records.Add((kind, auditEvent));
        }

        using (JsonDocument document = JsonDocument.Parse(records[0].Event.Reason))
        {
            JsonElement root = document.RootElement;
            JsonElement model = root.GetProperty("modelRun");
            Assert.AreEqual(
                cloudRun.ConfigurationDigest.Hex,
                model.GetProperty("configurationDigest").GetString());
            Assert.AreEqual(
                cloudRun.InputDigest.Hex,
                model.GetProperty("inputDigest").GetString());
            Assert.AreEqual(
                cloudRun.OutputDigest!.Hex,
                model.GetProperty("outputDigest").GetString());
            Assert.AreEqual("Cloud", model.GetProperty("executionLocation").GetString());
            Assert.AreEqual(
                "NotDisclosed",
                model.GetProperty("modelHashAvailability").GetString());
            Assert.AreEqual(JsonValueKind.Null, model.GetProperty("modelHash").ValueKind);
            Assert.AreEqual("azure-openai", model.GetProperty("destination").GetString());
            Assert.AreEqual("japaneast", model.GetProperty("region").GetString());
            Assert.AreEqual(
                "internal",
                model.GetProperty("sentDataClassification").GetString());
            Assert.AreEqual("zero-retention", model.GetProperty("retentionPolicy").GetString());
            Assert.AreEqual(
                adoptedCandidate.Id.ToString(),
                root.GetProperty("decision").GetProperty("adoptedSuggestionId").GetString());
        }

        using (JsonDocument document = JsonDocument.Parse(records[1].Event.Reason))
        {
            Assert.AreEqual(
                modified.ReplacementSuggestionId!.ToString(),
                document.RootElement
                    .GetProperty("decision")
                    .GetProperty("replacementSuggestionId")
                    .GetString());
        }

        using (JsonDocument document = JsonDocument.Parse(records[2].Event.Reason))
        {
            Assert.AreEqual(
                termAdopted.ResultingTranscriptRevisionId!.ToString(),
                document.RootElement
                    .GetProperty("decision")
                    .GetProperty("resultingTranscriptRevisionId")
                    .GetString());
        }
    }

    [TestMethod]
    [DataRow("candidateId")]
    [DataRow("modelRunId")]
    [DataRow("producer")]
    [DataRow("origin")]
    [DataRow("sourceDigest")]
    public void SuggestionDecisionRejectsMismatchedCandidateEvidenceBeforeAppend(
        string mismatch)
    {
        using AuditFixture fixture = new();
        ModelRun modelRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            completedAt: AuditTime.AddMinutes(-1));
        TestSuggestionCandidate candidate = CreateCandidate(
            SuggestionProducerKind.Silence,
            modelRun.Id,
            Digest('1'));
        SuggestionDecisionRecord decision = CreateDecision(
            candidate,
            SuggestionDecisionKind.Rejected,
            AuditTime,
            "Reject candidate.",
            candidateId: mismatch == "candidateId"
                ? EntityId<SuggestionIdTag>.New()
                : null,
            modelRunId: mismatch == "modelRunId"
                ? EntityId<ModelRunIdTag>.New()
                : null,
            producer: mismatch == "producer"
                ? SuggestionProducerKind.EditorialPhrase
                : null,
            origin: mismatch == "origin"
                ? OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New())
                : null,
            sourceDigest: mismatch == "sourceDigest" ? Digest('f') : null);

        ProvenanceAuditException exception =
            Assert.ThrowsExactly<ProvenanceAuditException>(() =>
                fixture.Service.RecordSuggestionDecision(
                    EntityId<AuditEventIdTag>.New(),
                    fixture.Project.Id,
                    candidate,
                    CreateDetection(candidate),
                    modelRun,
                    decision,
                    ReviewActorRole.Editor));

        Assert.AreEqual(
            ProvenanceAuditFailureReason.SuggestionDecisionBindingMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("unexpected")]
    [DataRow("modelRunId")]
    [DataRow("origin")]
    [DataRow("kind")]
    public void SuggestionDecisionRejectsMismatchedDetectionEvidenceBeforeAppend(
        string mismatch)
    {
        using AuditFixture fixture = new();
        ModelRun modelRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            completedAt: AuditTime.AddMinutes(-1));
        SuggestionProducerKind producer = mismatch == "unexpected"
            ? SuggestionProducerKind.TermCorrection
            : SuggestionProducerKind.Silence;
        TestSuggestionCandidate candidate = CreateCandidate(
            producer,
            modelRun.Id,
            Digest('1'));
        SuggestionDecisionRecord decision = CreateDecision(
            candidate,
            SuggestionDecisionKind.Rejected,
            AuditTime,
            "Reject candidate.");
        Detection? detection = mismatch == "missing"
            ? null
            : new Detection(
                EntityId<DetectionIdTag>.New(),
                mismatch == "modelRunId"
                    ? EntityId<ModelRunIdTag>.New()
                    : modelRun.Id,
                mismatch == "kind"
                    ? DetectionKind.TopicBoundary
                    : DetectionKind.Silence,
                mismatch == "origin"
                    ? OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New())
                    : candidate.Origin,
                DetectionContent,
                confidence: null);

        ProvenanceAuditException exception =
            Assert.ThrowsExactly<ProvenanceAuditException>(() =>
                fixture.Service.RecordSuggestionDecision(
                    EntityId<AuditEventIdTag>.New(),
                    fixture.Project.Id,
                    candidate,
                    detection,
                    modelRun,
                    decision,
                    ReviewActorRole.Editor));

        Assert.AreEqual(
            ProvenanceAuditFailureReason.DetectionBindingMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("outcome")]
    [DataRow("timestamp")]
    public void SuggestionDecisionRejectsMismatchedModelRunBeforeAppend(string mismatch)
    {
        using AuditFixture fixture = new();
        EntityId<ModelRunIdTag> candidateModelRunId = EntityId<ModelRunIdTag>.New();
        TestSuggestionCandidate candidate = CreateCandidate(
            SuggestionProducerKind.Emphasis,
            candidateModelRunId,
            Digest('1'));
        SuggestionDecisionRecord decision = CreateDecision(
            candidate,
            SuggestionDecisionKind.Rejected,
            AuditTime,
            "Reject candidate.");
        ModelRun modelRun = mismatch switch
        {
            "identity" => CreateModelRun(
                EntityId<ModelRunIdTag>.New(),
                completedAt: AuditTime.AddMinutes(-1)),
            "outcome" => CreateModelRun(
                candidateModelRunId,
                outcome: ModelRunOutcome.Failed,
                outputDigest: null,
                errorCode: "model.run_failed",
                completedAt: AuditTime.AddMinutes(-1)),
            "timestamp" => CreateModelRun(
                candidateModelRunId,
                completedAt: AuditTime.AddMinutes(1)),
            _ => throw new AssertFailedException("Unknown mismatch."),
        };

        ProvenanceAuditException exception =
            Assert.ThrowsExactly<ProvenanceAuditException>(() =>
                fixture.Service.RecordSuggestionDecision(
                    EntityId<AuditEventIdTag>.New(),
                    fixture.Project.Id,
                    candidate,
                    detection: null,
                    modelRun,
                    decision,
                    ReviewActorRole.Editor));

        Assert.AreEqual(
            ProvenanceAuditFailureReason.ModelRunBindingMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    public void TranscriptCorrectionRecordsOnlyRevisionAndEditRecordEvidence()
    {
        using AuditFixture fixture = new();
        const string previousText = "PRIVATE-ORIGINAL-TRANSCRIPT";
        const string currentText = "PRIVATE-REVISED-TRANSCRIPT";
        TranscriptEditResult edit = CreateTranscriptEdit(previousText, currentText);

        AuditEvent auditEvent = fixture.Service.RecordTranscriptCorrection(
            EntityId<AuditEventIdTag>.New(),
            fixture.Project.Id,
            edit,
            ReviewActorRole.Editor);

        StoredAuditEvent stored = fixture.GetAuditEvents().Single();
        Assert.AreEqual(auditEvent.Id, stored.Id);
        Assert.AreEqual(AuditEventKind.TranscriptCorrected, stored.Kind);
        Assert.AreEqual(ActorSid, stored.ActorId);
        Assert.AreEqual(edit.EditRecord.CreatedAt, stored.OccurredAt);
        Assert.AreEqual(CanonicalJson.Canonicalize(stored.Reason), stored.Reason);
        using JsonDocument document = JsonDocument.Parse(stored.Reason);
        JsonElement root = document.RootElement;
        Assert.AreEqual(
            ProvenanceAuditService.SchemaVersion,
            root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("transcriptCorrection", root.GetProperty("recordKind").GetString());
        Assert.AreEqual(
            edit.SourceRevision.Id.ToString(),
            root.GetProperty("sourceRevisionId").GetString());
        Assert.AreEqual(
            edit.DownstreamStale.SourceRevisionDigest.Hex,
            root.GetProperty("sourceRevisionDigest").GetString());
        Assert.AreEqual(
            edit.Revision.Id.ToString(),
            root.GetProperty("revisionId").GetString());
        Assert.AreEqual(
            edit.RevisionDigest.Hex,
            root.GetProperty("revisionDigest").GetString());
        Assert.AreEqual(
            edit.EditRecordDigest.Hex,
            root.GetProperty("editRecordDigest").GetString());
        Assert.AreEqual(
            edit.EditRecord.Reason,
            root.GetProperty("reason").GetString());
        Assert.AreEqual(ActorSid, root.GetProperty("actorId").GetString());
        Assert.AreEqual(
            FormatTimestamp(edit.EditRecord.CreatedAt),
            root.GetProperty("occurredAt").GetString());
        Assert.AreEqual(
            edit.SourceRevision.ChangeSource.ToString(),
            root.GetProperty("sourceChangeSource").GetString());
        Assert.AreEqual(
            edit.Revision.ChangeSource.ToString(),
            root.GetProperty("revisionChangeSource").GetString());
        Assert.IsFalse(stored.Reason.Contains(previousText, StringComparison.Ordinal));
        Assert.IsFalse(stored.Reason.Contains(currentText, StringComparison.Ordinal));
        Assert.IsFalse(stored.Reason.Contains("previousText", StringComparison.Ordinal));
        Assert.IsFalse(stored.Reason.Contains("currentText", StringComparison.Ordinal));
        Assert.IsFalse(stored.Reason.Contains("tokenId", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("sourceRevisionDigest")]
    [DataRow("revisionDigest")]
    [DataRow("canonicalRevisionJson")]
    [DataRow("editRecordDigest")]
    [DataRow("canonicalEditRecordJson")]
    public void TranscriptCorrectionRejectsInconsistentEvidenceBeforeAppend(
        string mismatch)
    {
        using AuditFixture fixture = new();
        TranscriptEditResult valid = CreateTranscriptEdit("old", "new");
        TranscriptDownstreamStaleRecord downstreamStale =
            mismatch is "sourceRevisionDigest" or "revisionDigest"
                ? new TranscriptDownstreamStaleRecord(
                    valid.SourceRevision.Id,
                    mismatch == "sourceRevisionDigest"
                        ? Digest('f')
                        : valid.DownstreamStale.SourceRevisionDigest,
                    valid.Revision.Id,
                    mismatch == "revisionDigest"
                        ? Digest('f')
                        : valid.DownstreamStale.RevisionDigest)
                : valid.DownstreamStale;
        TranscriptEditRecord editRecord = ReferenceEquals(
                downstreamStale,
                valid.DownstreamStale)
            ? valid.EditRecord
            : new TranscriptEditRecord(
                valid.EditRecord.ActorSid,
                valid.EditRecord.CreatedAt,
                valid.EditRecord.Reason,
                valid.EditRecord.Diff,
                downstreamStale,
                valid.EditRecord.RevisedSourceSpans);
        var inconsistent = new TranscriptEditResult(
            valid.SourceRevision,
            valid.Revision,
            editRecord,
            mismatch == "canonicalRevisionJson"
                ? "{}"
                : valid.CanonicalRevisionJson,
            mismatch == "revisionDigest"
                ? Digest('f')
                : valid.RevisionDigest,
            mismatch == "canonicalEditRecordJson"
                ? "{}"
                : valid.CanonicalEditRecordJson,
            mismatch == "editRecordDigest"
                ? Digest('f')
                : valid.EditRecordDigest);

        ProvenanceAuditException exception =
            Assert.ThrowsExactly<ProvenanceAuditException>(() =>
                fixture.Service.RecordTranscriptCorrection(
                    EntityId<AuditEventIdTag>.New(),
                    fixture.Project.Id,
                    inconsistent,
                    ReviewActorRole.Editor));

        Assert.AreEqual(
            ProvenanceAuditFailureReason.TranscriptEditBindingMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    // NFR-AUD-001 AC-118: 出力の version・構成・digest が履歴として保持される。
    public void DeliveryRecordsCompositionSuggestionAndOutputDigestsWithoutStorageReferences()
    {
        using AuditFixture fixture = new();
        OriginRef origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        Suggestion adoptedSuggestion = CreateSuggestion(
            EntityId<SuggestionIdTag>.New(),
            EntityId<ModelRunIdTag>.New(),
            SuggestionKind.DeleteRange,
            SuggestionStatus.Adopted,
            origin);
        SuggestionReference adoptedReference =
            SuggestionReference.FromAdoptedSuggestion(adoptedSuggestion);
        TimeRange range = new(default, new RationalTime(1, 1));
        CompositionElement element = new(
            EntityId<CompositionElementIdTag>.New(),
            range,
            origin,
            adoptedReference,
            range);
        CompositionVersion version = new(
            EntityId<CompositionVersionIdTag>.New(),
            fixture.Project.Id,
            parentVersionId: null,
            ActorSid,
            AuditTime.AddMinutes(-2),
            "Create the approved composition.",
            Digest('1'),
            [element]);
        fixture.StoreCompositionVersion(version);
        const string privateStorageReference =
            "PRIVATE-WORKSPACE/projects/project/deliveries/video.mp4";
        DeliveryManifest delivery = CreateDelivery(
            version,
            privateStorageReference);

        AuditEvent auditEvent = fixture.Service.RecordDelivery(
            EntityId<AuditEventIdTag>.New(),
            version,
            delivery,
            ActorSid,
            ReviewActorRole.Editor,
            AuditTime,
            "Export the approved delivery.");

        StoredAuditEvent stored = fixture.GetAuditEvents().Single();
        Assert.AreEqual(AuditEventKind.CompositionExported, stored.Kind);
        Assert.AreEqual(version.Id, stored.CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, stored.CompositionVersionDigest);
        Assert.AreEqual(auditEvent.Id, stored.Id);
        Assert.AreEqual(CanonicalJson.Canonicalize(stored.Reason), stored.Reason);
        using JsonDocument document = JsonDocument.Parse(stored.Reason);
        JsonElement root = document.RootElement;
        Assert.AreEqual(
            ProvenanceAuditService.SchemaVersion,
            root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("delivery", root.GetProperty("recordKind").GetString());
        Assert.AreEqual(ActorSid, root.GetProperty("actorId").GetString());
        Assert.AreEqual(
            FormatTimestamp(AuditTime),
            root.GetProperty("occurredAt").GetString());
        Assert.AreEqual(
            "Export the approved delivery.",
            root.GetProperty("reason").GetString());
        Assert.AreEqual(
            fixture.Project.Id.ToString(),
            root.GetProperty("composition").GetProperty("projectId").GetString());
        Assert.AreEqual(
            version.Id.ToString(),
            root.GetProperty("composition").GetProperty("versionId").GetString());
        Assert.AreEqual(
            version.ContentDigest.Hex,
            root.GetProperty("composition").GetProperty("versionDigest").GetString());
        Assert.AreEqual(
            delivery.Id.ToString(),
            root.GetProperty("delivery").GetProperty("manifestId").GetString());
        Assert.AreEqual(
            delivery.RenderManifestId.ToString(),
            root.GetProperty("delivery").GetProperty("renderManifestId").GetString());
        Assert.AreEqual(
            delivery.ProfileId.ToString(),
            root.GetProperty("delivery").GetProperty("profileId").GetString());
        Assert.AreEqual(
            delivery.ProfileDigest.Hex,
            root.GetProperty("delivery").GetProperty("profileDigest").GetString());
        JsonElement adopted = root
            .GetProperty("composition")
            .GetProperty("adoptedSuggestions")[0];
        Assert.AreEqual(element.Id.ToString(), adopted.GetProperty("elementId").GetString());
        Assert.AreEqual(
            adoptedSuggestion.Id.ToString(),
            adopted.GetProperty("suggestionId").GetString());
        AssertOrigin(adopted.GetProperty("origin"), adoptedSuggestion.Origin);
        string[] artifactDigests = root
            .GetProperty("delivery")
            .GetProperty("artifacts")
            .EnumerateArray()
            .Select(static artifact => artifact.GetProperty("sha256").GetString()!)
            .ToArray();
        CollectionAssert.AreEquivalent(
            delivery.Artifacts.Select(static artifact => artifact.Sha256.Hex).ToArray(),
            artifactDigests);
        Assert.IsFalse(stored.Reason.Contains(privateStorageReference, StringComparison.Ordinal));
        Assert.IsFalse(stored.Reason.Contains("storageReference", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeliveryRejectsMismatchedCompositionBeforeAppend()
    {
        using AuditFixture fixture = new();
        CompositionVersion deliveryVersion = CreateCompositionVersion(fixture.Project.Id);
        DeliveryManifest delivery = CreateDelivery(
            deliveryVersion,
            "objects/private/video.mp4");
        CompositionVersion suppliedVersion = CreateCompositionVersion(fixture.Project.Id);
        fixture.StoreCompositionVersion(suppliedVersion);

        ProvenanceAuditException exception =
            Assert.ThrowsExactly<ProvenanceAuditException>(() =>
                fixture.Service.RecordDelivery(
                    EntityId<AuditEventIdTag>.New(),
                    suppliedVersion,
                    delivery,
                    ActorSid,
                    ReviewActorRole.Editor,
                    AuditTime,
                    "Export delivery."));

        Assert.AreEqual(
            ProvenanceAuditFailureReason.DeliveryBindingMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" Export delivery.")]
    [DataRow("Export delivery. ")]
    public void DeliveryRejectsBlankOrUntrimmedReasonBeforeAppend(string reason)
    {
        using AuditFixture fixture = new();
        CompositionVersion version = CreateCompositionVersion(fixture.Project.Id);
        fixture.StoreCompositionVersion(version);
        DeliveryManifest delivery = CreateDelivery(
            version,
            "objects/private/video.mp4");

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.RecordDelivery(
                EntityId<AuditEventIdTag>.New(),
                version,
                delivery,
                ActorSid,
                ReviewActorRole.Editor,
                AuditTime,
                reason));

        Assert.AreEqual("reason", exception.ParamName);
        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    public void CallerProvidedAuditEventIdMakesRetryFailWithoutDuplicateAppend()
    {
        using AuditFixture fixture = new();
        TranscriptEditResult edit = CreateTranscriptEdit("old text", "new text");
        EntityId<AuditEventIdTag> eventId = EntityId<AuditEventIdTag>.New();

        fixture.Service.RecordTranscriptCorrection(
            eventId,
            fixture.Project.Id,
            edit,
            ReviewActorRole.Editor);

        Assert.ThrowsExactly<SqliteException>(() =>
            fixture.Service.RecordTranscriptCorrection(
                eventId,
                fixture.Project.Id,
                edit,
                ReviewActorRole.Editor));
        IReadOnlyList<StoredAuditEvent> stored = fixture.GetAuditEvents();
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual(eventId, stored[0].Id);
    }

    [TestMethod]
    public void PreCanceledOperationDoesNotAppend()
    {
        using AuditFixture fixture = new();
        ModelRun modelRun = CreateModelRun(
            EntityId<ModelRunIdTag>.New(),
            completedAt: AuditTime.AddMinutes(-1));
        TestSuggestionCandidate candidate = CreateCandidate(
            SuggestionProducerKind.TermCorrection,
            modelRun.Id,
            Digest('1'));
        SuggestionDecisionRecord decision = CreateDecision(
            candidate,
            SuggestionDecisionKind.Rejected,
            AuditTime,
            "Reject candidate.");
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            fixture.Service.RecordSuggestionDecision(
                EntityId<AuditEventIdTag>.New(),
                fixture.Project.Id,
                candidate,
                detection: null,
                modelRun,
                decision,
                ReviewActorRole.Editor,
                source.Token));

        Assert.AreEqual(0, fixture.GetAuditEvents().Count);
    }

    [TestMethod]
    public void PublicApiOnlyAppendsProvenanceRecordsAndExposesNoUpdateOrDelete()
    {
        Assert.IsTrue(typeof(ProvenanceAuditService).IsPublic);
        Assert.IsTrue(typeof(ProvenanceAuditService).IsSealed);
        ConstructorInfo[] constructors = typeof(ProvenanceAuditService).GetConstructors();
        Assert.AreEqual(1, constructors.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(ReviewStore) },
            constructors[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        MethodInfo[] methods = typeof(ProvenanceAuditService).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(ProvenanceAuditService.RecordSuggestionDecision),
                nameof(ProvenanceAuditService.RecordTranscriptCorrection),
                nameof(ProvenanceAuditService.RecordDelivery),
            },
            methods.Select(static method => method.Name).ToArray());
        Assert.IsFalse(methods.Any(static method =>
            method.Name.Contains("Update", StringComparison.Ordinal)
            || method.Name.Contains("Delete", StringComparison.Ordinal)));
        Assert.AreEqual(
            0,
            typeof(ProvenanceAuditService).GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length);
    }

    private static TestSuggestionCandidate CreateCandidate(
        SuggestionProducerKind producer,
        EntityId<ModelRunIdTag> modelRunId,
        ContentDigest sourceDigest)
    {
        SuggestionKind kind = GetSuggestionKind(producer);
        Suggestion suggestion = CreateSuggestion(
            EntityId<SuggestionIdTag>.New(),
            modelRunId,
            kind,
            SuggestionStatus.Proposed,
            OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()));
        return new TestSuggestionCandidate(suggestion, sourceDigest, producer);
    }

    private static Suggestion CreateSuggestion(
        EntityId<SuggestionIdTag> id,
        EntityId<ModelRunIdTag> modelRunId,
        SuggestionKind kind,
        SuggestionStatus status,
        OriginRef origin) =>
        new(
            id,
            modelRunId,
            kind,
            status,
            origin,
            SuggestionContent,
            confidence: null,
            MissingContextContent,
            WarningContent);

    private static SuggestionDecisionRecord CreateDecision(
        SuggestionCandidate candidate,
        SuggestionDecisionKind decision,
        DateTimeOffset decidedAt,
        string reason,
        EntityId<SuggestionIdTag>? candidateId = null,
        EntityId<ModelRunIdTag>? modelRunId = null,
        SuggestionProducerKind? producer = null,
        OriginRef? origin = null,
        ContentDigest? sourceDigest = null)
    {
        EntityId<SuggestionIdTag> recordedCandidateId = candidateId ?? candidate.Id;
        EntityId<ModelRunIdTag> recordedModelRunId =
            modelRunId ?? candidate.Suggestion.ModelRunId;
        SuggestionProducerKind recordedProducer = producer ?? candidate.Producer;
        OriginRef recordedOrigin = origin ?? candidate.Origin;
        EntityId<SuggestionIdTag>? replacementSuggestionId =
            decision == SuggestionDecisionKind.Modified
                ? EntityId<SuggestionIdTag>.New()
                : null;
        EntityId<TranscriptRevisionIdTag>? resultingRevisionId =
            decision == SuggestionDecisionKind.Adopted
                && candidate.Kind == SuggestionKind.TermCorrection
                    ? EntityId<TranscriptRevisionIdTag>.New()
                    : null;
        SuggestionReference? adoptedReference =
            decision == SuggestionDecisionKind.Adopted
                && candidate.Kind != SuggestionKind.TermCorrection
                    ? SuggestionReference.FromAdoptedSuggestion(
                        CreateSuggestion(
                            recordedCandidateId,
                            recordedModelRunId,
                            candidate.Kind,
                            SuggestionStatus.Adopted,
                            recordedOrigin))
                    : null;
        SuggestionStatus status = decision switch
        {
            SuggestionDecisionKind.Rejected => SuggestionStatus.Rejected,
            SuggestionDecisionKind.Modified => SuggestionStatus.Reviewed,
            SuggestionDecisionKind.Adopted => SuggestionStatus.Adopted,
            _ => throw new AssertFailedException("Unknown decision kind."),
        };
        return new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            recordedCandidateId,
            recordedModelRunId,
            recordedProducer,
            candidate.Kind,
            decision,
            status,
            recordedOrigin,
            sourceDigest ?? candidate.SourceDigest,
            ActorSid,
            decidedAt,
            reason,
            replacementSuggestionId,
            adoptedReference,
            resultingRevisionId);
    }

    private static Detection? CreateDetection(SuggestionCandidate candidate)
    {
        return ProducerRequiresDetection(candidate.Producer)
            ? new Detection(
                EntityId<DetectionIdTag>.New(),
                candidate.Suggestion.ModelRunId,
                GetDetectionKind(candidate.Producer),
                candidate.Origin,
                DetectionContent,
                confidence: null)
            : null;
    }

    private static bool ProducerRequiresDetection(SuggestionProducerKind producer) =>
        producer is SuggestionProducerKind.Silence
            or SuggestionProducerKind.EditorialPhrase
            or SuggestionProducerKind.TopicBoundary
            or SuggestionProducerKind.AudioReview
            or SuggestionProducerKind.ContextDeviation;

    private static DetectionKind GetDetectionKind(SuggestionProducerKind producer) =>
        producer switch
        {
            SuggestionProducerKind.Silence => DetectionKind.Silence,
            SuggestionProducerKind.EditorialPhrase =>
                DetectionKind.EditorialInstruction,
            SuggestionProducerKind.TopicBoundary => DetectionKind.TopicBoundary,
            SuggestionProducerKind.AudioReview => DetectionKind.AudioEvent,
            SuggestionProducerKind.ContextDeviation => DetectionKind.ContextDeviation,
            _ => throw new AssertFailedException(
                "The producer does not create Detection evidence."),
        };

    private static SuggestionKind GetSuggestionKind(SuggestionProducerKind producer) =>
        producer switch
        {
            SuggestionProducerKind.TermCorrection => SuggestionKind.TermCorrection,
            SuggestionProducerKind.Silence
                or SuggestionProducerKind.EditorialPhrase
                or SuggestionProducerKind.Repetition
                or SuggestionProducerKind.AudioReview
                or SuggestionProducerKind.ContextDeviation =>
                    SuggestionKind.DeleteRange,
            SuggestionProducerKind.TopicBoundary => SuggestionKind.TopicBoundary,
            SuggestionProducerKind.Emphasis => SuggestionKind.Highlight,
            _ => throw new AssertFailedException("Unknown suggestion producer."),
        };

    private static ModelRun CreateModelRun(
        EntityId<ModelRunIdTag> id,
        ModelExecutionLocation executionLocation = ModelExecutionLocation.Local,
        ModelHashAvailability modelHashAvailability = ModelHashAvailability.Available,
        ContentDigest? modelHash = null,
        ModelRunOutcome outcome = ModelRunOutcome.Succeeded,
        ContentDigest? outputDigest = null,
        string? errorCode = null,
        DateTimeOffset? completedAt = null)
    {
        DateTimeOffset completed = completedAt ?? AuditTime.AddMinutes(-1);
        bool cloud = executionLocation == ModelExecutionLocation.Cloud;
        return new ModelRun(
            id,
            cloud ? "cloud-provider" : "local-provider",
            cloud ? "cloud-model" : "local-model",
            "1.2.3",
            modelHashAvailability == ModelHashAvailability.Available
                ? modelHash ?? Digest('a')
                : null,
            modelHashAvailability,
            Digest('b'),
            Digest('c'),
            outcome == ModelRunOutcome.Succeeded
                ? outputDigest ?? Digest('d')
                : null,
            outcome,
            errorCode,
            executionLocation,
            cloud ? "azure-openai" : null,
            cloud ? "japaneast" : null,
            cloud ? "internal" : null,
            cloud ? "zero-retention" : null,
            completed.AddMinutes(-1),
            completed);
    }

    private static TranscriptEditResult CreateTranscriptEdit(
        string previousText,
        string currentText)
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<TranscriptTokenIdTag> tokenId =
            EntityId<TranscriptTokenIdTag>.New();
        TimeRange range = new(default, new RationalTime(1, 1));
        Token token = new(tokenId, previousText, range, 1d);
        Sentence sentence = new(
            EntityId<TranscriptSentenceIdTag>.New(),
            previousText,
            [token]);
        Utterance utterance = new(
            EntityId<TranscriptUtteranceIdTag>.New(),
            sourceSpanId,
            EntityId<TranscriptSpeakerIdTag>.New(),
            "Speaker",
            previousText,
            range,
            1d,
            [sentence]);
        TranscriptRevision revision = new(
            revisionId,
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            AuditTime.AddMinutes(-2),
            [utterance]);
        SourceSpan sourceSpan = new(
            sourceSpanId,
            EntityId<AssetIdTag>.New(),
            "audio:0",
            range,
            Digest('e'),
            revision.Id,
            [tokenId]);
        return TranscriptEditor.ApplyTokenTextEdits(
            revision,
            [sourceSpan],
            [new TranscriptTokenTextEdit(tokenId, previousText, currentText)],
            EntityId<TranscriptRevisionIdTag>.New(),
            ActorSid,
            AuditTime,
            "Correct the reviewed transcript wording.");
    }

    private static CompositionVersion CreateCompositionVersion(
        EntityId<ProjectIdTag> projectId) =>
        new(
            EntityId<CompositionVersionIdTag>.New(),
            projectId,
            parentVersionId: null,
            ActorSid,
            AuditTime.AddMinutes(-2),
            "Create composition.",
            Digest('1'),
            elements: []);

    private static DeliveryManifest CreateDelivery(
        CompositionVersion version,
        string storageReference)
    {
        TimelineMap map = new(
            EntityId<TimelineMapIdTag>.New(),
            version.Id,
            entries: []);
        RenderProfile profile = new(
            EntityId<RenderProfileIdTag>.New(),
            "landscape",
            Digest('2'),
            1920,
            1080,
            30000,
            1001,
            EntityId<LayoutSpecIdTag>.New(),
            EntityId<TemplateIdTag>.New(),
            Digest('3'),
            "h264",
            "aac",
            subtitleBurnIn: true);
        ManifestArtifact video = new(
            "video",
            "video/mp4",
            "ja",
            1234,
            Digest('4'),
            storageReference);
        ManifestArtifact subtitle = new(
            "subtitle",
            "text/vtt",
            "ja",
            567,
            Digest('5'),
            "PRIVATE-WORKSPACE/projects/project/deliveries/subtitle.vtt");
        RenderManifest render = new(
            EntityId<RenderManifestIdTag>.New(),
            version,
            map,
            Digest('6'),
            profile,
            Digest('7'),
            "1.0.0",
            Digest('8'),
            [new RenderInputEvidence("source", Digest('9'))],
            [new RenderToolEvidence(
                "ffmpeg",
                "8.1.2",
                "release",
                Digest('a'),
                containerImageDigest: null)],
            models: [],
            fonts: [],
            outputs: [video]);
        return new DeliveryManifest(
            EntityId<DeliveryManifestIdTag>.New(),
            render,
            [video, subtitle]);
    }

    private static bool ContainsForbiddenContent(string value) =>
        value.Contains(SuggestionContent, StringComparison.Ordinal)
        || value.Contains(DetectionContent, StringComparison.Ordinal)
        || value.Contains(MissingContextContent, StringComparison.Ordinal)
        || value.Contains(WarningContent, StringComparison.Ordinal)
        || value.Contains("prompt", StringComparison.OrdinalIgnoreCase)
        || value.Contains("response", StringComparison.OrdinalIgnoreCase)
        || value.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static void AssertOrigin(JsonElement actual, OriginRef expected)
    {
        Assert.AreEqual(expected.Kind.ToString(), actual.GetProperty("kind").GetString());
        string expectedId = expected.Kind switch
        {
            OriginKind.SourceSpan => expected.SourceSpanId!.ToString(),
            OriginKind.Asset => expected.AssetId!.ToString(),
            OriginKind.EditorialRecord => expected.EditorialRecordId!.ToString(),
            _ => throw new AssertFailedException("Unknown origin kind."),
        };
        Assert.AreEqual(expectedId, actual.GetProperty("id").GetString());
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static char HexDigit(int value) => "123456789abcdef0"[(value - 1) % 16];

    private static ContentDigest Digest(char digit) => new(new string(digit, 64));

    private sealed class TestSuggestionCandidate : SuggestionCandidate
    {
        internal TestSuggestionCandidate(
            Suggestion suggestion,
            ContentDigest sourceDigest,
            SuggestionProducerKind producer)
            : base(
                suggestion,
                suggestion,
                sourceDigest,
                producer,
                parentSuggestionId: null,
                replacementSuggestionId: null)
        {
        }

        internal override SuggestionCandidate Copy(
            Suggestion suggestion,
            EntityId<SuggestionIdTag>? parentSuggestionId,
            EntityId<SuggestionIdTag>? replacementSuggestionId) =>
            new TestSuggestionCandidate(suggestion, SourceDigest, Producer);
    }

    private sealed class AuditFixture : IDisposable
    {
        private readonly string _rootPath;
        private readonly ProjectStore _projectStore;

        internal AuditFixture()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ProvenanceAuditServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            string databasePath = Path.Combine(_rootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(_rootPath, "backups"));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "S-013 provenance audit test",
                "Verify content-free provenance audit records.",
                "Test runner",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            _projectStore = new ProjectStore(databasePath);
            _projectStore.CreateProject(
                Project,
                "{\"project\":true}",
                AuditTime.AddMinutes(-10));
            ReviewStore = new ReviewStore(databasePath);
            Service = new ProvenanceAuditService(ReviewStore);
        }

        internal Project Project { get; }

        internal ReviewStore ReviewStore { get; }

        internal ProvenanceAuditService Service { get; }

        internal IReadOnlyList<StoredAuditEvent> GetAuditEvents() =>
            ReviewStore.GetProjectAuditEvents(Project.Id);

        internal void StoreCompositionVersion(CompositionVersion version) =>
            _projectStore.AppendCompositionVersion(version, "{\"version\":true}");

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(_rootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }
}