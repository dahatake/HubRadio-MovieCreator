using System.Buffers;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Transcripts;

public sealed class TranscriptTokenTextEdit
{
    public TranscriptTokenTextEdit(
        EntityId<TranscriptTokenIdTag> tokenId,
        string expectedText,
        string revisedText)
    {
        ArgumentNullException.ThrowIfNull(tokenId);
        TranscriptEditorContract.ValidateText(expectedText, nameof(expectedText));
        TranscriptEditorContract.ValidateText(revisedText, nameof(revisedText));
        if (string.Equals(expectedText, revisedText, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A transcript text edit must change the token text.",
                nameof(revisedText));
        }

        TokenId = tokenId;
        ExpectedText = expectedText;
        RevisedText = revisedText;
    }

    public EntityId<TranscriptTokenIdTag> TokenId { get; }

    public string ExpectedText { get; }

    public string RevisedText { get; }
}

public enum TranscriptTextChangeKind
{
    Utterance,
    Sentence,
    Token,
}

public sealed class TranscriptTextChange
{
    internal TranscriptTextChange(
        TranscriptTextChangeKind kind,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<TranscriptSentenceIdTag>? sentenceId,
        EntityId<TranscriptTokenIdTag>? tokenId,
        string previousText,
        string currentText)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        TranscriptEditorContract.ValidateText(previousText, nameof(previousText));
        TranscriptEditorContract.ValidateText(currentText, nameof(currentText));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "The transcript text-change kind must be defined.");
        }

        bool validTarget = kind switch
        {
            TranscriptTextChangeKind.Utterance =>
                sentenceId is null && tokenId is null,
            TranscriptTextChangeKind.Sentence =>
                sentenceId is not null && tokenId is null,
            TranscriptTextChangeKind.Token =>
                sentenceId is not null && tokenId is not null,
            _ => false,
        };
        if (!validTarget)
        {
            throw new ArgumentException(
                "The transcript text-change target does not match its kind.",
                nameof(kind));
        }

        if (string.Equals(previousText, currentText, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A transcript text change must contain different values.",
                nameof(currentText));
        }

        Kind = kind;
        UtteranceId = utteranceId;
        SentenceId = sentenceId;
        TokenId = tokenId;
        PreviousText = previousText;
        CurrentText = currentText;
    }

    public TranscriptTextChangeKind Kind { get; }

    public EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    public EntityId<TranscriptSentenceIdTag>? SentenceId { get; }

    public EntityId<TranscriptTokenIdTag>? TokenId { get; }

    public string PreviousText { get; }

    public string CurrentText { get; }
}

public sealed class TranscriptDiff
{
    internal TranscriptDiff(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        EntityId<TranscriptRevisionIdTag> revisionId,
        IReadOnlyList<TranscriptTextChange> changes)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(changes);
        if (sourceRevisionId == revisionId)
        {
            throw new ArgumentException(
                "A transcript diff requires distinct revision IDs.",
                nameof(revisionId));
        }

        if (changes.Count == 0)
        {
            throw new ArgumentException(
                "A transcript diff must contain at least one change.",
                nameof(changes));
        }

        TranscriptTextChange[] copiedChanges = new TranscriptTextChange[changes.Count];
        var targets = new HashSet<TranscriptChangeTarget>();
        for (int index = 0; index < copiedChanges.Length; index++)
        {
            TranscriptTextChange change = changes[index]
                ?? throw new ArgumentException(
                    "Transcript changes must not contain null elements.",
                    nameof(changes));
            var target = new TranscriptChangeTarget(
                change.Kind,
                change.UtteranceId,
                change.SentenceId,
                change.TokenId);
            if (!targets.Add(target))
            {
                throw new ArgumentException(
                    "Transcript changes must not contain duplicate targets.",
                    nameof(changes));
            }

            copiedChanges[index] = change;
        }

        SourceRevisionId = sourceRevisionId;
        RevisionId = revisionId;
        Changes = Array.AsReadOnly(copiedChanges);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public EntityId<TranscriptRevisionIdTag> RevisionId { get; }

    public IReadOnlyList<TranscriptTextChange> Changes { get; }

    private sealed record TranscriptChangeTarget(
        TranscriptTextChangeKind Kind,
        EntityId<TranscriptUtteranceIdTag> UtteranceId,
        EntityId<TranscriptSentenceIdTag>? SentenceId,
        EntityId<TranscriptTokenIdTag>? TokenId);
}

public sealed class TranscriptDownstreamStaleRecord
{
    internal TranscriptDownstreamStaleRecord(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<TranscriptRevisionIdTag> revisionId,
        ContentDigest revisionDigest)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(revisionDigest);
        if (sourceRevisionId == revisionId || sourceRevisionDigest == revisionDigest)
        {
            throw new ArgumentException(
                "A transcript dependency change requires distinct revision evidence.",
                nameof(revisionId));
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        RevisionId = revisionId;
        RevisionDigest = revisionDigest;
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<TranscriptRevisionIdTag> RevisionId { get; }

    public ContentDigest RevisionDigest { get; }

    public bool PreviousRevisionConsumersAreStale => true;
}

public sealed class TranscriptEditRecord
{
    internal TranscriptEditRecord(
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        TranscriptDiff diff,
        TranscriptDownstreamStaleRecord downstreamStale,
        IReadOnlyList<SourceSpan> revisedSourceSpans)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(downstreamStale);
        ArgumentNullException.ThrowIfNull(revisedSourceSpans);
        if (diff.SourceRevisionId != downstreamStale.SourceRevisionId ||
            diff.RevisionId != downstreamStale.RevisionId)
        {
            throw new ArgumentException(
                "The transcript diff and stale record must identify the same revisions.",
                nameof(downstreamStale));
        }

        SourceSpan[] copiedSpans = new SourceSpan[revisedSourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copiedSpans.Length; index++)
        {
            SourceSpan span = revisedSourceSpans[index]
                ?? throw new ArgumentException(
                    "Revised source spans must not contain null elements.",
                    nameof(revisedSourceSpans));
            if (!spanIds.Add(span.Id) || span.TranscriptRevisionId != diff.RevisionId)
            {
                throw new ArgumentException(
                    "Revised source spans must uniquely bind the edited revision.",
                    nameof(revisedSourceSpans));
            }

            copiedSpans[index] = span;
        }

        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        TranscriptEditorContract.ValidateText(reason, nameof(reason));
        CreatedAt = createdAt;
        Reason = reason;
        Diff = diff;
        DownstreamStale = downstreamStale;
        RevisedSourceSpans = Array.AsReadOnly(copiedSpans);
    }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Reason { get; }

    public TranscriptDiff Diff { get; }

    public TranscriptDownstreamStaleRecord DownstreamStale { get; }

    public IReadOnlyList<SourceSpan> RevisedSourceSpans { get; }
}

public sealed class TranscriptEditResult
{
    internal TranscriptEditResult(
        TranscriptRevision sourceRevision,
        TranscriptRevision revision,
        TranscriptEditRecord editRecord,
        string canonicalRevisionJson,
        ContentDigest revisionDigest,
        string canonicalEditRecordJson,
        ContentDigest editRecordDigest)
    {
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(editRecord);
        ArgumentNullException.ThrowIfNull(canonicalRevisionJson);
        ArgumentNullException.ThrowIfNull(revisionDigest);
        ArgumentNullException.ThrowIfNull(canonicalEditRecordJson);
        ArgumentNullException.ThrowIfNull(editRecordDigest);
        if (revision.ParentRevisionId != sourceRevision.Id ||
            revision.ChangeSource != TranscriptChangeSource.Human ||
            editRecord.Diff.SourceRevisionId != sourceRevision.Id ||
            editRecord.Diff.RevisionId != revision.Id ||
            editRecord.CreatedAt != revision.CreatedAt)
        {
            throw new ArgumentException(
                "The transcript edit result has inconsistent revision evidence.",
                nameof(editRecord));
        }

        SourceSpan[] copiedSpans = new SourceSpan[editRecord.RevisedSourceSpans.Count];
        if (copiedSpans.Length != revision.Utterances.Count)
        {
            throw new ArgumentException(
                "Each revised utterance must have one source span.",
            nameof(editRecord));
        }

        for (int index = 0; index < copiedSpans.Length; index++)
        {
            SourceSpan span = editRecord.RevisedSourceSpans[index];
            Utterance utterance = revision.Utterances[index];
            if (span.Id != utterance.SourceSpanId ||
                span.TranscriptRevisionId != revision.Id ||
                span.Range != utterance.Range ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw new ArgumentException(
                    "A revised source span is inconsistent with its utterance.",
                    nameof(editRecord));
            }

            copiedSpans[index] = span;
        }

        SourceRevision = sourceRevision;
        Revision = revision;
        RevisedSourceSpans = Array.AsReadOnly(copiedSpans);
        EditRecord = editRecord;
        CanonicalRevisionJson = canonicalRevisionJson;
        RevisionDigest = revisionDigest;
        CanonicalEditRecordJson = canonicalEditRecordJson;
        EditRecordDigest = editRecordDigest;
    }

    public TranscriptRevision SourceRevision { get; }

    public TranscriptRevision Revision { get; }

    public IReadOnlyList<SourceSpan> RevisedSourceSpans { get; }

    public TranscriptEditRecord EditRecord { get; }

    public TranscriptDownstreamStaleRecord DownstreamStale =>
        EditRecord.DownstreamStale;

    public string CanonicalRevisionJson { get; }

    public ContentDigest RevisionDigest { get; }

    public string CanonicalEditRecordJson { get; }

    public ContentDigest EditRecordDigest { get; }
}

public sealed class TranscriptEditPersistenceRequest
{
    public TranscriptEditPersistenceRequest(
        EntityId<ArtifactIdTag> sourceRevisionArtifactId,
        EntityId<ArtifactIdTag> revisionArtifactId,
        string revisionStagingPath,
        EntityId<ArtifactIdTag> editRecordArtifactId,
        string editRecordStagingPath)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionArtifactId);
        ArgumentNullException.ThrowIfNull(revisionArtifactId);
        ArgumentNullException.ThrowIfNull(revisionStagingPath);
        ArgumentNullException.ThrowIfNull(editRecordArtifactId);
        ArgumentNullException.ThrowIfNull(editRecordStagingPath);
        if (sourceRevisionArtifactId == revisionArtifactId ||
            sourceRevisionArtifactId == editRecordArtifactId ||
            revisionArtifactId == editRecordArtifactId)
        {
            throw new ArgumentException(
                "Transcript source, revision, and edit-record artifact IDs must be distinct.",
                nameof(editRecordArtifactId));
        }

        SourceRevisionArtifactId = sourceRevisionArtifactId;
        RevisionArtifactId = revisionArtifactId;
        RevisionStagingPath = revisionStagingPath;
        EditRecordArtifactId = editRecordArtifactId;
        EditRecordStagingPath = editRecordStagingPath;
    }

    public EntityId<ArtifactIdTag> SourceRevisionArtifactId { get; }

    public EntityId<ArtifactIdTag> RevisionArtifactId { get; }

    public string RevisionStagingPath { get; }

    public EntityId<ArtifactIdTag> EditRecordArtifactId { get; }

    public string EditRecordStagingPath { get; }
}

public sealed class StoredTranscriptEdit
{
    internal StoredTranscriptEdit(
        TranscriptEditResult edit,
        StoredArtifact sourceRevisionArtifact,
        StoredArtifact revisionArtifact,
        StoredArtifact editRecordArtifact)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(sourceRevisionArtifact);
        ArgumentNullException.ThrowIfNull(revisionArtifact);
        ArgumentNullException.ThrowIfNull(editRecordArtifact);
        byte[] revisionBytes = TranscriptEditorContract.StrictUtf8.GetBytes(
            edit.CanonicalRevisionJson);
        byte[] recordBytes = TranscriptEditorContract.StrictUtf8.GetBytes(
            edit.CanonicalEditRecordJson);
        if (sourceRevisionArtifact.ProjectId != revisionArtifact.ProjectId ||
            revisionArtifact.ProjectId != editRecordArtifact.ProjectId ||
            sourceRevisionArtifact.Kind != ArtifactKind.Analysis ||
            sourceRevisionArtifact.ContentDigest !=
                edit.DownstreamStale.SourceRevisionDigest ||
            revisionArtifact.Kind != ArtifactKind.Analysis ||
            editRecordArtifact.Kind != ArtifactKind.Evidence ||
            revisionArtifact.ContentDigest != edit.RevisionDigest ||
            revisionArtifact.ByteSize != revisionBytes.LongLength ||
            editRecordArtifact.ContentDigest != edit.EditRecordDigest ||
            editRecordArtifact.ByteSize != recordBytes.LongLength ||
            revisionArtifact.CreatedAt != edit.Revision.CreatedAt.ToUniversalTime() ||
            editRecordArtifact.CreatedAt != edit.Revision.CreatedAt.ToUniversalTime())
        {
            throw new ArgumentException(
                "Stored transcript edit evidence is inconsistent.",
                nameof(revisionArtifact));
        }

        Edit = edit;
        SourceRevisionArtifact = sourceRevisionArtifact;
        RevisionArtifact = revisionArtifact;
        EditRecordArtifact = editRecordArtifact;
    }

    public TranscriptEditResult Edit { get; }

    public StoredArtifact SourceRevisionArtifact { get; }

    public StoredArtifact RevisionArtifact { get; }

    public StoredArtifact EditRecordArtifact { get; }
}

public enum TranscriptEditorFailureReason
{
    InvalidActor,
    InvalidReason,
    NoChanges,
    DuplicateEdit,
    TokenNotFound,
    SourceTextChanged,
    SourceRevisionInvalid,
    SourceSpanBindingMismatch,
    TextHierarchyMismatch,
    TimestampPrecedesSource,
    RevisionIdentityConflict,
    SerializationFailed,
    PersistenceFailed,
}

public sealed class TranscriptEditorException : Exception
{
    internal TranscriptEditorException(
        TranscriptEditorFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public TranscriptEditorFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(TranscriptEditorFailureReason reason) =>
        reason switch
        {
            TranscriptEditorFailureReason.InvalidActor =>
                "The transcript editor identity is invalid.",
            TranscriptEditorFailureReason.InvalidReason =>
                "The transcript edit reason is invalid.",
            TranscriptEditorFailureReason.NoChanges =>
                "The transcript edit contains no text changes.",
            TranscriptEditorFailureReason.DuplicateEdit =>
                "The transcript edit contains duplicate targets.",
            TranscriptEditorFailureReason.TokenNotFound =>
                "A transcript edit target does not exist in the source revision.",
            TranscriptEditorFailureReason.SourceTextChanged =>
                "The transcript edit was based on different source text.",
            TranscriptEditorFailureReason.SourceRevisionInvalid =>
                "The source transcript revision is invalid.",
            TranscriptEditorFailureReason.SourceSpanBindingMismatch =>
                "The source transcript provenance is inconsistent.",
            TranscriptEditorFailureReason.TextHierarchyMismatch =>
                "The transcript text hierarchy cannot be updated without ambiguity.",
            TranscriptEditorFailureReason.TimestampPrecedesSource =>
                "The transcript edit timestamp precedes its source revision.",
            TranscriptEditorFailureReason.RevisionIdentityConflict =>
                "The transcript edit revision identity conflicts with its source.",
            TranscriptEditorFailureReason.SerializationFailed =>
                "The transcript edit could not be serialized canonically.",
            TranscriptEditorFailureReason.PersistenceFailed =>
                "The transcript edit could not be saved.",
            _ => "The transcript edit could not be completed.",
        };
}

public static class TranscriptEditor
{
    public static async Task<StoredTranscriptEdit> SaveAsync(
        ArtifactStore artifactStore,
        BinaryVerifier binaryVerifier,
        TranscriptEditResult edit,
        TranscriptEditPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] revisionBytes = TranscriptEditorContract.StrictUtf8.GetBytes(
            edit.CanonicalRevisionJson);
        byte[] recordBytes = TranscriptEditorContract.StrictUtf8.GetBytes(
            edit.CanonicalEditRecordJson);
        byte[] sourceRevisionBytes = TranscriptEditorContract.StrictUtf8.GetBytes(
            TranscriptSnapshotJson.WriteRevision(edit.SourceRevision));
        StoredArtifact sourceRevisionArtifact;
        try
        {
            sourceRevisionArtifact = artifactStore.GetArtifact(
                request.SourceRevisionArtifactId)
                ?? throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
            if (artifactStore.GetArtifact(request.RevisionArtifactId) is not null ||
                artifactStore.GetArtifact(request.EditRecordArtifactId) is not null)
            {
                throw Failure(
                    TranscriptEditorFailureReason.PersistenceFailed,
                    reconciliationRequired: true);
            }
        }
        catch (TranscriptEditorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
        }

        if (sourceRevisionArtifact.Kind != ArtifactKind.Analysis ||
            sourceRevisionArtifact.ContentDigest !=
                edit.DownstreamStale.SourceRevisionDigest ||
            sourceRevisionArtifact.ByteSize != sourceRevisionBytes.LongLength)
        {
            throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
        }

        try
        {
            await VerifySourceRevisionArtifactAsync(
                artifactStore,
                binaryVerifier,
                request.SourceRevisionArtifactId,
                sourceRevisionArtifact,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TranscriptEditorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
        }

        string relativeRoot = string.Concat(edit.Revision.Id.ToString(), "/");
        ArtifactPromotionRequest recordRequest;
        ArtifactPromotionRequest revisionRequest;
        try
        {
            recordRequest = new ArtifactPromotionRequest(
                request.EditRecordArtifactId,
                sourceRevisionArtifact.ProjectId,
                ArtifactKind.Evidence,
                request.EditRecordStagingPath,
                string.Concat(relativeRoot, "transcript-edit.json"),
                edit.EditRecordDigest,
                recordBytes.LongLength,
                edit.Revision.CreatedAt);
            revisionRequest = new ArtifactPromotionRequest(
                request.RevisionArtifactId,
                sourceRevisionArtifact.ProjectId,
                ArtifactKind.Analysis,
                request.RevisionStagingPath,
                string.Concat(relativeRoot, "transcript.json"),
                edit.RevisionDigest,
                revisionBytes.LongLength,
                edit.Revision.CreatedAt);
        }
        catch (ArgumentException)
        {
            throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
        }

        StoredArtifact editRecordArtifact;
        try
        {
            editRecordArtifact = await artifactStore.PromoteAsync(
                recordRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                exception.ReconciliationRequired ||
                    exception.Reason is ArtifactStoreFailureReason.DestinationExists or
                        ArtifactStoreFailureReason.DatabaseRejected);
        }
        catch (Exception)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                reconciliationRequired: true);
        }

        StoredArtifact revisionArtifact;
        try
        {
            revisionArtifact = await artifactStore.PromoteAsync(
                revisionRequest,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ArtifactStoreException)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                reconciliationRequired: true);
        }
        catch (Exception)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                reconciliationRequired: true);
        }

        try
        {
            await VerifySourceRevisionArtifactAsync(
                artifactStore,
                binaryVerifier,
                request.SourceRevisionArtifactId,
                sourceRevisionArtifact,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                reconciliationRequired: true);
        }

        try
        {
            return new StoredTranscriptEdit(
                edit,
                sourceRevisionArtifact,
                revisionArtifact,
                editRecordArtifact);
        }
        catch (ArgumentException)
        {
            throw Failure(
                TranscriptEditorFailureReason.PersistenceFailed,
                reconciliationRequired: true);
        }
    }

    public static TranscriptEditResult ApplyTokenTextEdits(
        TranscriptRevision sourceRevision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTokenTextEdit> edits,
        EntityId<TranscriptRevisionIdTag> newRevisionId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(newRevisionId);

        if (newRevisionId == sourceRevision.Id ||
            newRevisionId == sourceRevision.ParentRevisionId)
        {
            throw Failure(TranscriptEditorFailureReason.RevisionIdentityConflict);
        }

        if (createdAt < sourceRevision.CreatedAt)
        {
            throw Failure(TranscriptEditorFailureReason.TimestampPrecedesSource);
        }

        string canonicalActorSid;
        try
        {
            canonicalActorSid = TranscriptEditorContract.ValidateCanonicalSid(
                actorSid,
                nameof(actorSid));
        }
        catch (ArgumentException)
        {
            throw Failure(TranscriptEditorFailureReason.InvalidActor);
        }

        try
        {
            TranscriptEditorContract.ValidateText(reason, nameof(reason));
        }
        catch (ArgumentException)
        {
            throw Failure(TranscriptEditorFailureReason.InvalidReason);
        }

        Dictionary<EntityId<TranscriptTokenIdTag>, TranscriptTokenTextEdit> editsById =
            IndexEdits(edits);
        SourceSpan[] validatedSourceSpans = ValidateAndCopySourceSpans(
            sourceRevision,
            sourceSpans);
        ValidateEditTargets(sourceRevision, editsById);

        try
        {
            var revisedUtterances = new List<Utterance>(sourceRevision.Utterances.Count);
            var revisedSourceSpans = new List<SourceSpan>(sourceRevision.Utterances.Count);

            for (int utteranceIndex = 0;
                utteranceIndex < sourceRevision.Utterances.Count;
                utteranceIndex++)
            {
                Utterance sourceUtterance = sourceRevision.Utterances[utteranceIndex];
                SourceSpan sourceSpan = validatedSourceSpans[utteranceIndex];
                EntityId<SourceSpanIdTag> revisedSourceSpanId =
                    EntityId<SourceSpanIdTag>.New();
                Utterance revisedUtterance = ReviseUtterance(
                    sourceUtterance,
                    revisedSourceSpanId,
                    editsById);
                revisedUtterances.Add(revisedUtterance);
                revisedSourceSpans.Add(CloneSourceSpan(
                    sourceSpan,
                    revisedUtterance,
                    newRevisionId));
            }

            var revision = new TranscriptRevision(
                newRevisionId,
                sourceRevision.Id,
                TranscriptChangeSource.Human,
                createdAt,
                revisedUtterances);
            TranscriptDiff diff = BuildDiff(sourceRevision, revision);

            string canonicalSourceJson = TranscriptSnapshotJson.WriteRevision(sourceRevision);
            string canonicalRevisionJson = TranscriptSnapshotJson.WriteRevision(revision);
            ContentDigest sourceRevisionDigest = ContentDigest.Compute(
                TranscriptEditorContract.StrictUtf8.GetBytes(canonicalSourceJson));
            ContentDigest revisionDigest = ContentDigest.Compute(
                TranscriptEditorContract.StrictUtf8.GetBytes(canonicalRevisionJson));
            var downstreamStale = new TranscriptDownstreamStaleRecord(
                sourceRevision.Id,
                sourceRevisionDigest,
                revision.Id,
                revisionDigest);
            var editRecord = new TranscriptEditRecord(
                canonicalActorSid,
                createdAt,
                reason,
                diff,
                downstreamStale,
                revisedSourceSpans);
            string canonicalEditRecordJson =
                TranscriptSnapshotJson.WriteEditRecord(editRecord);
            ContentDigest editRecordDigest = ContentDigest.Compute(
                TranscriptEditorContract.StrictUtf8.GetBytes(canonicalEditRecordJson));

            return new TranscriptEditResult(
                sourceRevision,
                revision,
                editRecord,
                canonicalRevisionJson,
                revisionDigest,
                canonicalEditRecordJson,
                editRecordDigest);
        }
        catch (TranscriptEditorException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException or EncoderFallbackException)
        {
            throw Failure(TranscriptEditorFailureReason.SerializationFailed);
        }
    }

    private static Dictionary<
        EntityId<TranscriptTokenIdTag>,
        TranscriptTokenTextEdit> IndexEdits(
        IReadOnlyList<TranscriptTokenTextEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw Failure(TranscriptEditorFailureReason.NoChanges);
        }

        var indexed = new Dictionary<
            EntityId<TranscriptTokenIdTag>,
            TranscriptTokenTextEdit>(edits.Count);
        for (int index = 0; index < edits.Count; index++)
        {
            TranscriptTokenTextEdit edit = edits[index]
                ?? throw Failure(TranscriptEditorFailureReason.SourceRevisionInvalid);
            if (!indexed.TryAdd(edit.TokenId, edit))
            {
                throw Failure(TranscriptEditorFailureReason.DuplicateEdit);
            }
        }

        return indexed;
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision sourceRevision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != sourceRevision.Utterances.Count)
        {
            throw Failure(TranscriptEditorFailureReason.SourceSpanBindingMismatch);
        }

        var copied = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    TranscriptEditorFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = sourceRevision.Utterances[index];
            if (!spanIds.Add(span.Id) ||
                span.Id != utterance.SourceSpanId ||
                span.TranscriptRevisionId != sourceRevision.Id ||
                span.Range != utterance.Range ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    TranscriptEditorFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static async Task VerifySourceRevisionArtifactAsync(
        ArtifactStore artifactStore,
        BinaryVerifier binaryVerifier,
        EntityId<ArtifactIdTag> sourceRevisionArtifactId,
        StoredArtifact expectedArtifact,
        CancellationToken cancellationToken)
    {
        BinaryVerificationResult verification =
            await artifactStore.VerifyRegisteredAsync(
                sourceRevisionArtifactId,
                binaryVerifier,
                cancellationToken).ConfigureAwait(false);
        if (!verification.IsVerified ||
            verification.ExpectedDigest != expectedArtifact.ContentDigest ||
            verification.ExpectedByteSize != expectedArtifact.ByteSize ||
            verification.ActualDigest != expectedArtifact.ContentDigest ||
            verification.ActualByteSize != expectedArtifact.ByteSize)
        {
            throw Failure(TranscriptEditorFailureReason.PersistenceFailed);
        }
    }

    private static void ValidateEditTargets(
        TranscriptRevision sourceRevision,
        IReadOnlyDictionary<
            EntityId<TranscriptTokenIdTag>,
            TranscriptTokenTextEdit> editsById)
    {
        var remaining = new HashSet<EntityId<TranscriptTokenIdTag>>(editsById.Keys);
        foreach (Utterance utterance in sourceRevision.Utterances)
        {
            foreach (Token token in utterance.Tokens)
            {
                if (!editsById.TryGetValue(token.Id, out TranscriptTokenTextEdit? edit))
                {
                    continue;
                }

                if (!string.Equals(
                    token.Text,
                    edit.ExpectedText,
                    StringComparison.Ordinal))
                {
                    throw Failure(TranscriptEditorFailureReason.SourceTextChanged);
                }

                _ = remaining.Remove(token.Id);
            }
        }

        if (remaining.Count != 0)
        {
            throw Failure(TranscriptEditorFailureReason.TokenNotFound);
        }
    }

    private static Utterance ReviseUtterance(
        Utterance source,
        EntityId<SourceSpanIdTag> revisedSourceSpanId,
        IReadOnlyDictionary<
            EntityId<TranscriptTokenIdTag>,
            TranscriptTokenTextEdit> editsById)
    {
        var revisedSentences = new List<Sentence>(source.Sentences.Count);
        foreach (Sentence sourceSentence in source.Sentences)
        {
            revisedSentences.Add(ReviseSentence(sourceSentence, editsById));
        }

        var sentenceReplacements = new List<TextReplacement>();
        bool hasSentenceEdit = source.Sentences
            .Where((sentence, index) => !string.Equals(
                sentence.Text,
                revisedSentences[index].Text,
                StringComparison.Ordinal))
            .Any();
        int[] sentenceTextStarts = hasSentenceEdit
            ? FindUniqueOrderedTextStarts(
                source.Text,
                source.Sentences.Select(static sentence => sentence.Text).ToArray())
            : [];
        for (int index = 0; index < source.Sentences.Count; index++)
        {
            Sentence sourceSentence = source.Sentences[index];
            Sentence revisedSentence = revisedSentences[index];
            if (!string.Equals(
                sourceSentence.Text,
                revisedSentence.Text,
                StringComparison.Ordinal))
            {
                sentenceReplacements.Add(new TextReplacement(
                    sentenceTextStarts[index],
                    sourceSentence.Text.Length,
                    revisedSentence.Text));
            }
        }

        string revisedText = ApplyReplacements(source.Text, sentenceReplacements);
        return new Utterance(
            source.Id,
            revisedSourceSpanId,
            source.SpeakerId,
            source.SpeakerLabel,
            revisedText,
            source.Range,
            source.RecognitionConfidence,
            revisedSentences);
    }

    private static Sentence ReviseSentence(
        Sentence source,
        IReadOnlyDictionary<
            EntityId<TranscriptTokenIdTag>,
            TranscriptTokenTextEdit> editsById)
    {
        bool hasEdit = source.Tokens.Any(token => editsById.ContainsKey(token.Id));
        if (!hasEdit)
        {
            return source;
        }

        var revisedTokens = new List<Token>(source.Tokens.Count);
        var tokenReplacements = new List<TextReplacement>();
        int[] tokenTextStarts = FindUniqueOrderedTextStarts(
            source.Text,
            source.Tokens.Select(static token => token.Text).ToArray());

        for (int index = 0; index < source.Tokens.Count; index++)
        {
            Token sourceToken = source.Tokens[index];
            bool changed = editsById.TryGetValue(
                sourceToken.Id,
                out TranscriptTokenTextEdit? edit);
            string revisedTokenText = changed ? edit!.RevisedText : sourceToken.Text;
            revisedTokens.Add(new Token(
                sourceToken.Id,
                revisedTokenText,
                sourceToken.Range,
                sourceToken.RecognitionConfidence));

            if (changed)
            {
                tokenReplacements.Add(new TextReplacement(
                    tokenTextStarts[index],
                    sourceToken.Text.Length,
                    revisedTokenText));
            }
        }

        string revisedSentenceText = ApplyReplacements(
            source.Text,
            tokenReplacements);
        return new Sentence(source.Id, revisedSentenceText, revisedTokens);
    }

    private static int[] FindUniqueOrderedTextStarts(
        string source,
        IReadOnlyList<string> orderedParts)
    {
        var earliestStarts = new int[orderedParts.Count];
        int searchStart = 0;
        for (int index = 0; index < orderedParts.Count; index++)
        {
            string part = orderedParts[index];
            int start = source.IndexOf(
                part,
                searchStart,
                StringComparison.Ordinal);
            if (start < 0)
            {
                throw Failure(TranscriptEditorFailureReason.TextHierarchyMismatch);
            }

            earliestStarts[index] = start;
            searchStart = checked(start + part.Length);
        }

        var latestStarts = new int[orderedParts.Count];
        int exclusiveEnd = source.Length;
        for (int index = orderedParts.Count - 1; index >= 0; index--)
        {
            string part = orderedParts[index];
            int start = FindLastTextStartEndingBefore(source, part, exclusiveEnd);
            if (start < 0)
            {
                throw Failure(TranscriptEditorFailureReason.TextHierarchyMismatch);
            }

            latestStarts[index] = start;
            exclusiveEnd = start;
        }

        if (!earliestStarts.SequenceEqual(latestStarts))
        {
            throw Failure(TranscriptEditorFailureReason.TextHierarchyMismatch);
        }

        return earliestStarts;
    }

    private static int FindLastTextStartEndingBefore(
        string source,
        string value,
        int exclusiveEnd)
    {
        for (int candidate = exclusiveEnd - value.Length;
            candidate >= 0;
            candidate--)
        {
            if (source.AsSpan(candidate, value.Length).SequenceEqual(value.AsSpan()))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static string ApplyReplacements(
        string source,
        IReadOnlyList<TextReplacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return source;
        }

        var builder = new StringBuilder(source);
        for (int index = replacements.Count - 1; index >= 0; index--)
        {
            TextReplacement replacement = replacements[index];
            _ = builder.Remove(replacement.Start, replacement.Length);
            _ = builder.Insert(replacement.Start, replacement.Value);
        }

        return builder.ToString();
    }

    private static SourceSpan CloneSourceSpan(
        SourceSpan source,
        Utterance revisedUtterance,
        EntityId<TranscriptRevisionIdTag> revisionId)
    {
        return new SourceSpan(
            revisedUtterance.SourceSpanId,
            source.AssetId,
            source.StreamId,
            source.Range,
            source.SourceDigest,
            revisionId,
            revisedUtterance.Tokens.Select(static token => token.Id).ToArray());
    }

    private static TranscriptDiff BuildDiff(
        TranscriptRevision source,
        TranscriptRevision revision)
    {
        if (source.Utterances.Count != revision.Utterances.Count)
        {
            throw Failure(TranscriptEditorFailureReason.SourceRevisionInvalid);
        }

        var changes = new List<TranscriptTextChange>();
        for (int utteranceIndex = 0;
            utteranceIndex < source.Utterances.Count;
            utteranceIndex++)
        {
            Utterance previousUtterance = source.Utterances[utteranceIndex];
            Utterance currentUtterance = revision.Utterances[utteranceIndex];
            if (!string.Equals(
                previousUtterance.Text,
                currentUtterance.Text,
                StringComparison.Ordinal))
            {
                changes.Add(new TranscriptTextChange(
                    TranscriptTextChangeKind.Utterance,
                    previousUtterance.Id,
                    sentenceId: null,
                    tokenId: null,
                    previousUtterance.Text,
                    currentUtterance.Text));
            }

            if (previousUtterance.Sentences.Count != currentUtterance.Sentences.Count)
            {
                throw Failure(TranscriptEditorFailureReason.SourceRevisionInvalid);
            }

            for (int sentenceIndex = 0;
                sentenceIndex < previousUtterance.Sentences.Count;
                sentenceIndex++)
            {
                Sentence previousSentence = previousUtterance.Sentences[sentenceIndex];
                Sentence currentSentence = currentUtterance.Sentences[sentenceIndex];
                if (!string.Equals(
                    previousSentence.Text,
                    currentSentence.Text,
                    StringComparison.Ordinal))
                {
                    changes.Add(new TranscriptTextChange(
                        TranscriptTextChangeKind.Sentence,
                        previousUtterance.Id,
                        previousSentence.Id,
                        tokenId: null,
                        previousSentence.Text,
                        currentSentence.Text));
                }

                if (previousSentence.Tokens.Count != currentSentence.Tokens.Count)
                {
                    throw Failure(TranscriptEditorFailureReason.SourceRevisionInvalid);
                }

                for (int tokenIndex = 0;
                    tokenIndex < previousSentence.Tokens.Count;
                    tokenIndex++)
                {
                    Token previousToken = previousSentence.Tokens[tokenIndex];
                    Token currentToken = currentSentence.Tokens[tokenIndex];
                    if (!string.Equals(
                        previousToken.Text,
                        currentToken.Text,
                        StringComparison.Ordinal))
                    {
                        changes.Add(new TranscriptTextChange(
                            TranscriptTextChangeKind.Token,
                            previousUtterance.Id,
                            previousSentence.Id,
                            previousToken.Id,
                            previousToken.Text,
                            currentToken.Text));
                    }
                }
            }
        }

        if (changes.Count == 0)
        {
            throw Failure(TranscriptEditorFailureReason.NoChanges);
        }

        return new TranscriptDiff(source.Id, revision.Id, changes);
    }

    private static TranscriptEditorException Failure(
        TranscriptEditorFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record TextReplacement(int Start, int Length, string Value);
}

internal static class TranscriptEditorContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Transcript edit text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Transcript edit text must be valid Unicode.",
                parameterName,
                exception);
        }
    }

    internal static string ValidateCanonicalSid(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A transcript editor SID must be canonical.",
                parameterName);
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A transcript editor SID must be canonical.",
                parameterName,
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A transcript editor SID must be canonical.",
                parameterName);
        }

        return sid.Value;
    }
}

internal static class TranscriptSnapshotJson
{
    internal static string WriteRevision(TranscriptRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "id", revision.Id.ToString());
            if (revision.ParentRevisionId is null)
            {
                writer.WriteNull("parentRevisionId");
            }
            else
            {
                WriteString(
                    writer,
                    "parentRevisionId",
                    revision.ParentRevisionId.ToString());
            }

            WriteString(
                writer,
                "changeSource",
                revision.ChangeSource switch
                {
                    TranscriptChangeSource.Machine => "machine",
                    TranscriptChangeSource.Human => "human",
                    _ => throw new InvalidOperationException(
                        "Unknown transcript change source."),
                });
            WriteTimestamp(writer, "createdAt", revision.CreatedAt);
            writer.WritePropertyName("utterances");
            writer.WriteStartArray();
            foreach (Utterance utterance in revision.Utterances)
            {
                WriteUtterance(writer, utterance);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    internal static string WriteEditRecord(TranscriptEditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "schemaVersion", "1.0.0");
            WriteString(
                writer,
                "sourceRevisionId",
                record.Diff.SourceRevisionId.ToString());
            WriteString(writer, "revisionId", record.Diff.RevisionId.ToString());
            WriteString(writer, "actorSid", record.ActorSid);
            WriteTimestamp(writer, "createdAt", record.CreatedAt);
            WriteString(writer, "reason", record.Reason);
            WriteString(
                writer,
                "sourceRevisionDigest",
                record.DownstreamStale.SourceRevisionDigest.Hex);
            WriteString(
                writer,
                "revisionDigest",
                record.DownstreamStale.RevisionDigest.Hex);
            writer.WriteBoolean(
                "previousRevisionConsumersAreStale",
                record.DownstreamStale.PreviousRevisionConsumersAreStale);
            writer.WritePropertyName("sourceSpans");
            writer.WriteStartArray();
            foreach (SourceSpan sourceSpan in record.RevisedSourceSpans)
            {
                WriteSourceSpan(writer, sourceSpan);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            foreach (TranscriptTextChange change in record.Diff.Changes)
            {
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "kind",
                    change.Kind switch
                    {
                        TranscriptTextChangeKind.Utterance => "utterance",
                        TranscriptTextChangeKind.Sentence => "sentence",
                        TranscriptTextChangeKind.Token => "token",
                        _ => throw new InvalidOperationException(
                            "Unknown transcript text-change kind."),
                    });
                WriteString(writer, "utteranceId", change.UtteranceId.ToString());
                if (change.SentenceId is not null)
                {
                    WriteString(writer, "sentenceId", change.SentenceId.ToString());
                }

                if (change.TokenId is not null)
                {
                    WriteString(writer, "tokenId", change.TokenId.ToString());
                }

                WriteString(writer, "previousText", change.PreviousText);
                WriteString(writer, "currentText", change.CurrentText);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WriteSourceSpan(Utf8JsonWriter writer, SourceSpan sourceSpan)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", sourceSpan.Id.ToString());
        WriteString(writer, "assetId", sourceSpan.AssetId.ToString());
        WriteString(writer, "streamId", sourceSpan.StreamId);
        writer.WritePropertyName("range");
        WriteTimeRange(writer, sourceSpan.Range);
        WriteString(writer, "sourceDigest", sourceSpan.SourceDigest.Hex);
        if (sourceSpan.TranscriptRevisionId is null)
        {
            writer.WriteNull("transcriptRevisionId");
        }
        else
        {
            WriteString(
                writer,
                "transcriptRevisionId",
                sourceSpan.TranscriptRevisionId.ToString());
        }

        writer.WritePropertyName("transcriptTokenIds");
        writer.WriteStartArray();
        foreach (EntityId<TranscriptTokenIdTag> tokenId in
            sourceSpan.TranscriptTokenIds)
        {
            writer.WriteStringValue(tokenId.ToString());
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteUtterance(Utf8JsonWriter writer, Utterance utterance)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", utterance.Id.ToString());
        WriteString(writer, "sourceSpanId", utterance.SourceSpanId.ToString());
        WriteString(writer, "speakerId", utterance.SpeakerId.ToString());
        WriteString(writer, "speakerLabel", utterance.SpeakerLabel);
        WriteString(writer, "text", utterance.Text);
        writer.WritePropertyName("range");
        WriteTimeRange(writer, utterance.Range);
        if (utterance.RecognitionConfidence is double confidence)
        {
            writer.WriteNumber("recognitionConfidence", confidence);
        }
        else
        {
            writer.WriteNull("recognitionConfidence");
        }

        writer.WritePropertyName("sentences");
        writer.WriteStartArray();
        foreach (Sentence sentence in utterance.Sentences)
        {
            writer.WriteStartObject();
            WriteString(writer, "id", sentence.Id.ToString());
            WriteString(writer, "text", sentence.Text);
            writer.WritePropertyName("tokens");
            writer.WriteStartArray();
            foreach (Token token in sentence.Tokens)
            {
                WriteToken(writer, token);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteToken(Utf8JsonWriter writer, Token token)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", token.Id.ToString());
        WriteString(writer, "text", token.Text);
        if (token.Range is TimeRange range)
        {
            writer.WritePropertyName("range");
            WriteTimeRange(writer, range);
        }
        else
        {
            writer.WriteNull("range");
        }

        if (token.RecognitionConfidence is double confidence)
        {
            writer.WriteNumber("recognitionConfidence", confidence);
        }
        else
        {
            writer.WriteNull("recognitionConfidence");
        }

        writer.WriteBoolean(
            "manualTimingAdjustmentRequired",
            token.ManualTimingAdjustmentRequired);
        writer.WriteEndObject();
    }

    private static void WriteTimeRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(Utf8JsonWriter writer, RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteNumber("rate", value.Rate);
        writer.WriteEndObject();
    }

    private static string WriteCanonical(Action<Utf8JsonWriter> write)
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
        return TranscriptEditorContract.StrictUtf8.GetString(canonical);
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
        _ = TranscriptEditorContract.StrictUtf8.GetByteCount(value);
        writer.WriteString(propertyName, value);
    }
}