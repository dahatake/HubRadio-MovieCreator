using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Audit;

public enum ProvenanceAuditFailureReason
{
    SuggestionDecisionBindingMismatch,
    DetectionBindingMismatch,
    ModelRunBindingMismatch,
    TranscriptEditBindingMismatch,
    DeliveryBindingMismatch,
    SerializationFailed,
}

public sealed class ProvenanceAuditException : Exception
{
    internal ProvenanceAuditException(
        ProvenanceAuditFailureReason reason,
        Exception? innerException = null)
        : base(GetStableMessage(reason), innerException)
    {
        Reason = reason;
    }

    public ProvenanceAuditFailureReason Reason { get; }

    private static string GetStableMessage(ProvenanceAuditFailureReason reason) =>
        reason switch
        {
            ProvenanceAuditFailureReason.SuggestionDecisionBindingMismatch =>
                "The suggestion decision provenance is inconsistent.",
            ProvenanceAuditFailureReason.DetectionBindingMismatch =>
                "The detection provenance is inconsistent.",
            ProvenanceAuditFailureReason.ModelRunBindingMismatch =>
                "The model-run provenance is inconsistent.",
            ProvenanceAuditFailureReason.TranscriptEditBindingMismatch =>
                "The transcript-edit provenance is inconsistent.",
            ProvenanceAuditFailureReason.DeliveryBindingMismatch =>
                "The delivery provenance is inconsistent.",
            ProvenanceAuditFailureReason.SerializationFailed =>
                "The provenance audit record could not be serialized.",
            _ => "The provenance audit record could not be created.",
        };
}

public sealed class ProvenanceAuditService
{
    public const string SchemaVersion = "1.0.0";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ReviewStore _reviewStore;

    public ProvenanceAuditService(ReviewStore reviewStore)
    {
        ArgumentNullException.ThrowIfNull(reviewStore);
        _reviewStore = reviewStore;
    }

    public AuditEvent RecordSuggestionDecision(
        EntityId<AuditEventIdTag> eventId,
        EntityId<ProjectIdTag> projectId,
        SuggestionCandidate candidate,
        Detection? detection,
        ModelRun modelRun,
        SuggestionDecisionRecord decision,
        ReviewActorRole actorRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(modelRun);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateSuggestionDecision(candidate, decision);
        ValidateDetection(candidate, detection);
        ValidateModelRun(candidate, modelRun, decision);

        string envelope = WriteSuggestionDecision(
            candidate,
            detection,
            modelRun,
            decision);
        var auditEvent = new AuditEvent(
            eventId,
            AuditEventKind.SuggestionDecisionRecorded,
            decision.ActorSid,
            actorRole,
            decision.DecidedAt,
            envelope,
            projectId);

        cancellationToken.ThrowIfCancellationRequested();
        _reviewStore.AppendAuditEvent(auditEvent);
        return auditEvent;
    }

    public AuditEvent RecordTranscriptCorrection(
        EntityId<AuditEventIdTag> eventId,
        EntityId<ProjectIdTag> projectId,
        TranscriptEditResult edit,
        ReviewActorRole actorRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateTranscriptEdit(edit);
        string envelope = WriteTranscriptCorrection(edit);
        var auditEvent = new AuditEvent(
            eventId,
            AuditEventKind.TranscriptCorrected,
            edit.EditRecord.ActorSid,
            actorRole,
            edit.EditRecord.CreatedAt,
            envelope,
            projectId);

        cancellationToken.ThrowIfCancellationRequested();
        _reviewStore.AppendAuditEvent(auditEvent);
        return auditEvent;
    }

    public AuditEvent RecordDelivery(
        EntityId<AuditEventIdTag> eventId,
        CompositionVersion compositionVersion,
        DeliveryManifest delivery,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(actorId);
        ArgumentNullException.ThrowIfNull(reason);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateReason(reason);
        ValidateDelivery(compositionVersion, delivery, occurredAt);
        string envelope = WriteDelivery(
            compositionVersion,
            delivery,
            actorId,
            occurredAt,
            reason);
        var auditEvent = new AuditEvent(
            eventId,
            AuditEventKind.CompositionExported,
            actorId,
            actorRole,
            occurredAt,
            envelope,
            compositionVersion.ProjectId,
            compositionVersion);

        cancellationToken.ThrowIfCancellationRequested();
        _reviewStore.AppendAuditEvent(auditEvent);
        return auditEvent;
    }

    private static void ValidateSuggestionDecision(
        SuggestionCandidate candidate,
        SuggestionDecisionRecord decision)
    {
        if (candidate.Status != SuggestionStatus.Proposed
            || !SuggestionServiceContract.IsProducerKindCompatible(
                candidate.Producer,
                candidate.Kind)
            || decision.CandidateId != candidate.Id
            || decision.ModelRunId != candidate.Suggestion.ModelRunId
            || decision.Producer != candidate.Producer
            || decision.SuggestionKind != candidate.Kind
            || decision.Origin != candidate.Origin
            || decision.SourceDigest != candidate.SourceDigest)
        {
            throw Failure(
                ProvenanceAuditFailureReason.SuggestionDecisionBindingMismatch);
        }
    }

    private static void ValidateDetection(
        SuggestionCandidate candidate,
        Detection? detection)
    {
        DetectionKind? expectedKind = GetExpectedDetectionKind(candidate.Producer);
        if (expectedKind is null)
        {
            if (detection is not null)
            {
                throw Failure(ProvenanceAuditFailureReason.DetectionBindingMismatch);
            }

            return;
        }

        if (detection is null
            || detection.ModelRunId != candidate.Suggestion.ModelRunId
            || detection.Kind != expectedKind
            || detection.Origin != candidate.Origin)
        {
            throw Failure(ProvenanceAuditFailureReason.DetectionBindingMismatch);
        }
    }

    private static DetectionKind? GetExpectedDetectionKind(
        SuggestionProducerKind producer) =>
        producer switch
        {
            SuggestionProducerKind.Silence => DetectionKind.Silence,
            SuggestionProducerKind.EditorialPhrase =>
                DetectionKind.EditorialInstruction,
            SuggestionProducerKind.TopicBoundary => DetectionKind.TopicBoundary,
            SuggestionProducerKind.AudioReview => DetectionKind.AudioEvent,
            SuggestionProducerKind.ContextDeviation =>
                DetectionKind.ContextDeviation,
            SuggestionProducerKind.TermCorrection or
            SuggestionProducerKind.Repetition or
            SuggestionProducerKind.Emphasis => null,
            _ => throw Failure(
                ProvenanceAuditFailureReason.SuggestionDecisionBindingMismatch),
        };

    private static void ValidateModelRun(
        SuggestionCandidate candidate,
        ModelRun modelRun,
        SuggestionDecisionRecord decision)
    {
        if (modelRun.Id != candidate.Suggestion.ModelRunId
            || modelRun.Id != decision.ModelRunId
            || modelRun.Outcome != ModelRunOutcome.Succeeded
            || modelRun.OutputDigest is null
            || modelRun.CompletedAt > decision.DecidedAt)
        {
            throw Failure(ProvenanceAuditFailureReason.ModelRunBindingMismatch);
        }
    }

    private static void ValidateTranscriptEdit(TranscriptEditResult edit)
    {
        string expectedSourceJson = TranscriptSnapshotJson.WriteRevision(
            edit.SourceRevision);
        string expectedRevisionJson = TranscriptSnapshotJson.WriteRevision(
            edit.Revision);
        string expectedEditRecordJson = TranscriptSnapshotJson.WriteEditRecord(
            edit.EditRecord);
        ContentDigest expectedSourceDigest = ContentDigest.Compute(
            StrictUtf8.GetBytes(expectedSourceJson));
        ContentDigest expectedRevisionDigest = ContentDigest.Compute(
            StrictUtf8.GetBytes(expectedRevisionJson));
        ContentDigest expectedEditRecordDigest = ContentDigest.Compute(
            StrictUtf8.GetBytes(expectedEditRecordJson));

        if (edit.DownstreamStale.SourceRevisionId != edit.SourceRevision.Id
            || edit.DownstreamStale.RevisionId != edit.Revision.Id
            || edit.DownstreamStale.SourceRevisionDigest != expectedSourceDigest
            || edit.DownstreamStale.RevisionDigest != expectedRevisionDigest
            || edit.RevisionDigest != expectedRevisionDigest
            || edit.EditRecordDigest != expectedEditRecordDigest
            || !string.Equals(
                edit.CanonicalRevisionJson,
                expectedRevisionJson,
                StringComparison.Ordinal)
            || !string.Equals(
                edit.CanonicalEditRecordJson,
                expectedEditRecordJson,
                StringComparison.Ordinal))
        {
            throw Failure(ProvenanceAuditFailureReason.TranscriptEditBindingMismatch);
        }
    }

    private static void ValidateDelivery(
        CompositionVersion compositionVersion,
        DeliveryManifest delivery,
        DateTimeOffset occurredAt)
    {
        if (delivery.ProjectId != compositionVersion.ProjectId
            || delivery.CompositionVersionId != compositionVersion.Id
            || delivery.CompositionVersionDigest != compositionVersion.ContentDigest
            || occurredAt < compositionVersion.CreatedAt)
        {
            throw Failure(ProvenanceAuditFailureReason.DeliveryBindingMismatch);
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An audit-event reason must not be blank or have leading or trailing whitespace.",
                nameof(reason));
        }
    }

    private static string WriteSuggestionDecision(
        SuggestionCandidate candidate,
        Detection? detection,
        ModelRun modelRun,
        SuggestionDecisionRecord decision) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteCommonRecordFields(
                writer,
                "suggestionDecision",
                decision.ActorSid,
                decision.DecidedAt,
                decision.Reason);

            writer.WritePropertyName("suggestion");
            writer.WriteStartObject();
            WriteString(writer, "id", candidate.Id.ToString());
            WriteString(
                writer,
                "modelRunId",
                candidate.Suggestion.ModelRunId.ToString());
            WriteString(writer, "producer", candidate.Producer.ToString());
            WriteString(writer, "kind", candidate.Kind.ToString());
            writer.WritePropertyName("origin");
            WriteOrigin(writer, candidate.Origin);
            WriteString(writer, "sourceDigest", candidate.SourceDigest.Hex);
            writer.WriteEndObject();

            writer.WritePropertyName("detection");
            if (detection is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                WriteString(writer, "id", detection.Id.ToString());
                WriteString(writer, "modelRunId", detection.ModelRunId.ToString());
                WriteString(writer, "kind", detection.Kind.ToString());
                writer.WritePropertyName("origin");
                WriteOrigin(writer, detection.Origin);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("modelRun");
            WriteModelRun(writer, modelRun);

            writer.WritePropertyName("decision");
            writer.WriteStartObject();
            WriteString(writer, "id", decision.Id.ToString());
            WriteString(writer, "kind", decision.Decision.ToString());
            WriteString(
                writer,
                "statusAfterDecision",
                decision.StatusAfterDecision.ToString());
            WriteNullableId(
                writer,
                "replacementSuggestionId",
                decision.ReplacementSuggestionId);
            WriteNullableId(
                writer,
                "adoptedSuggestionId",
                decision.AdoptedReference?.SuggestionId);
            WriteNullableId(
                writer,
                "resultingTranscriptRevisionId",
                decision.ResultingTranscriptRevisionId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static string WriteTranscriptCorrection(TranscriptEditResult edit) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteCommonRecordFields(
                writer,
                "transcriptCorrection",
                edit.EditRecord.ActorSid,
                edit.EditRecord.CreatedAt,
                edit.EditRecord.Reason);
            WriteString(writer, "sourceRevisionId", edit.SourceRevision.Id.ToString());
            WriteString(
                writer,
                "sourceRevisionDigest",
                edit.DownstreamStale.SourceRevisionDigest.Hex);
            WriteString(
                writer,
                "sourceChangeSource",
                edit.SourceRevision.ChangeSource.ToString());
            WriteString(writer, "revisionId", edit.Revision.Id.ToString());
            WriteString(writer, "revisionDigest", edit.RevisionDigest.Hex);
            WriteString(
                writer,
                "revisionChangeSource",
                edit.Revision.ChangeSource.ToString());
            WriteString(writer, "editRecordDigest", edit.EditRecordDigest.Hex);
            writer.WriteEndObject();
        });

    private static string WriteDelivery(
        CompositionVersion compositionVersion,
        DeliveryManifest delivery,
        string actorId,
        DateTimeOffset occurredAt,
        string reason) =>
        WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteCommonRecordFields(
                writer,
                "delivery",
                actorId,
                occurredAt,
                reason);

            writer.WritePropertyName("composition");
            writer.WriteStartObject();
            WriteString(writer, "projectId", compositionVersion.ProjectId.ToString());
            WriteString(writer, "versionId", compositionVersion.Id.ToString());
            WriteString(writer, "versionDigest", compositionVersion.ContentDigest.Hex);
            WriteNullableId(writer, "parentVersionId", compositionVersion.ParentVersionId);
            writer.WritePropertyName("adoptedSuggestions");
            writer.WriteStartArray();
            IEnumerable<CompositionElement> adoptedElements =
                compositionVersion.Elements
                    .Where(static element => element.AdoptedSuggestion is not null)
                    .OrderBy(
                        static element => element.Id.ToString(),
                        StringComparer.Ordinal);
            foreach (CompositionElement element in adoptedElements)
            {
                SuggestionReference reference = element.AdoptedSuggestion!;
                writer.WriteStartObject();
                WriteString(writer, "elementId", element.Id.ToString());
                WriteString(writer, "suggestionId", reference.SuggestionId.ToString());
                writer.WritePropertyName("origin");
                WriteOrigin(writer, reference.Origin);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("delivery");
            writer.WriteStartObject();
            WriteString(writer, "manifestId", delivery.Id.ToString());
            WriteString(writer, "renderManifestId", delivery.RenderManifestId.ToString());
            WriteString(writer, "profileId", delivery.ProfileId.ToString());
            WriteString(writer, "profileDigest", delivery.ProfileDigest.Hex);
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            IEnumerable<ManifestArtifact> artifacts = delivery.Artifacts
                .OrderBy(
                    static artifact => artifact.ArtifactType,
                    StringComparer.Ordinal)
                .ThenBy(
                    static artifact => artifact.MediaType,
                    StringComparer.Ordinal)
                .ThenBy(
                    static artifact => artifact.Language ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(
                    static artifact => artifact.Sha256.Hex,
                    StringComparer.Ordinal)
                .ThenBy(static artifact => artifact.ByteSize);
            foreach (ManifestArtifact artifact in artifacts)
            {
                writer.WriteStartObject();
                WriteString(writer, "artifactType", artifact.ArtifactType);
                WriteString(writer, "mediaType", artifact.MediaType);
                WriteNullableString(writer, "language", artifact.Language);
                writer.WriteNumber("byteSize", artifact.ByteSize);
                WriteString(writer, "sha256", artifact.Sha256.Hex);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static void WriteCommonRecordFields(
        Utf8JsonWriter writer,
        string recordKind,
        string actorId,
        DateTimeOffset occurredAt,
        string reason)
    {
        WriteString(writer, "schemaVersion", SchemaVersion);
        WriteString(writer, "recordKind", recordKind);
        WriteString(writer, "actorId", actorId);
        WriteTimestamp(writer, "occurredAt", occurredAt);
        WriteString(writer, "reason", reason);
    }

    private static void WriteModelRun(Utf8JsonWriter writer, ModelRun modelRun)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", modelRun.Id.ToString());
        WriteString(writer, "provider", modelRun.Provider);
        WriteString(writer, "modelIdentifier", modelRun.ModelIdentifier);
        WriteString(writer, "modelVersion", modelRun.ModelVersion);
        WriteString(
            writer,
            "modelHashAvailability",
            modelRun.ModelHashAvailability.ToString());
        WriteNullableString(writer, "modelHash", modelRun.ModelHash?.Hex);
        WriteString(
            writer,
            "configurationDigest",
            modelRun.ConfigurationDigest.Hex);
        WriteString(writer, "inputDigest", modelRun.InputDigest.Hex);
        WriteNullableString(writer, "outputDigest", modelRun.OutputDigest?.Hex);
        WriteString(writer, "outcome", modelRun.Outcome.ToString());
        WriteNullableString(writer, "errorCode", modelRun.ErrorCode);
        WriteString(
            writer,
            "executionLocation",
            modelRun.ExecutionLocation.ToString());
        WriteNullableString(writer, "destination", modelRun.Destination);
        WriteNullableString(writer, "region", modelRun.Region);
        WriteNullableString(
            writer,
            "sentDataClassification",
            modelRun.SentDataClassification);
        WriteNullableString(writer, "retentionPolicy", modelRun.RetentionPolicy);
        WriteTimestamp(writer, "startedAt", modelRun.StartedAt);
        WriteTimestamp(writer, "completedAt", modelRun.CompletedAt);
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        string id = origin.Kind switch
        {
            OriginKind.SourceSpan => origin.SourceSpanId!.ToString(),
            OriginKind.Asset => origin.AssetId!.ToString(),
            OriginKind.EditorialRecord => origin.EditorialRecordId!.ToString(),
            _ => throw new InvalidOperationException("Unknown provenance origin kind."),
        };

        writer.WriteStartObject();
        WriteString(writer, "kind", origin.Kind.ToString());
        WriteString(writer, "id", id);
        writer.WriteEndObject();
    }

    private static void WriteNullableId<TTag>(
        Utf8JsonWriter writer,
        string propertyName,
        EntityId<TTag>? value)
    {
        WriteNullableString(writer, propertyName, value?.ToString());
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        WriteString(writer, propertyName, value);
    }

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value)
    {
        WriteString(
            writer,
            propertyName,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string value)
    {
        _ = StrictUtf8.GetByteCount(value);
        writer.WriteString(propertyName, value);
    }

    private static string WriteCanonical(Action<Utf8JsonWriter> write)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    Indented = false,
                    SkipValidation = false,
                }))
            {
                write(writer);
                writer.Flush();
            }

            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
            return StrictUtf8.GetString(canonical);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or EncoderFallbackException
                or JsonException)
        {
            throw new ProvenanceAuditException(
                ProvenanceAuditFailureReason.SerializationFailed,
                exception);
        }
    }

    private static ProvenanceAuditException Failure(
        ProvenanceAuditFailureReason reason) =>
        new(reason);
}