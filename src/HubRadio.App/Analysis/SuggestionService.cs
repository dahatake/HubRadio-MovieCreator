using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;

namespace HubRadio.App.Analysis;

public sealed class SuggestionDecisionIdTag
{
}

public enum SuggestionProducerKind
{
    TermCorrection,
    Silence,
    EditorialPhrase,
    Repetition,
    TopicBoundary,
    AudioReview,
    ContextDeviation,
    Emphasis,
}

public enum SuggestionCandidateFreshness
{
    Current,
    Stale,
}

public enum SuggestionDecisionKind
{
    Rejected,
    Modified,
    Adopted,
}

public enum SuggestionServiceFailureReason
{
    CandidateBindingMismatch,
    DuplicateCandidate,
    DuplicateRepetitionCandidate,
    CandidateStale,
    CandidateAlreadyDecided,
    DuplicateDecision,
    DecisionHistoryInvalid,
    InvalidDecision,
    ReplacementBindingMismatch,
    ReplacementIdentityConflict,
    TypedTermContextRequired,
    RepetitionPayloadInvalid,
}

public sealed class SuggestionServiceException : Exception
{
    internal SuggestionServiceException(SuggestionServiceFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SuggestionServiceFailureReason Reason { get; }

    private static string GetStableMessage(SuggestionServiceFailureReason reason) =>
        reason switch
        {
            SuggestionServiceFailureReason.CandidateBindingMismatch =>
                "The suggestion candidate evidence is inconsistent.",
            SuggestionServiceFailureReason.DuplicateCandidate =>
                "The suggestion candidate identity is already registered.",
            SuggestionServiceFailureReason.DuplicateRepetitionCandidate =>
                "The repetition candidate evidence is already registered.",
            SuggestionServiceFailureReason.CandidateStale =>
                "The suggestion candidate does not match the current source digest.",
            SuggestionServiceFailureReason.CandidateAlreadyDecided =>
                "The suggestion candidate already has a final decision.",
            SuggestionServiceFailureReason.DuplicateDecision =>
                "The suggestion decision identity is already recorded.",
            SuggestionServiceFailureReason.DecisionHistoryInvalid =>
                "The suggestion decision history is inconsistent.",
            SuggestionServiceFailureReason.InvalidDecision =>
                "The suggestion decision metadata is invalid.",
            SuggestionServiceFailureReason.ReplacementBindingMismatch =>
                "The replacement suggestion does not match its parent candidate.",
            SuggestionServiceFailureReason.ReplacementIdentityConflict =>
                "The replacement suggestion identity is not fresh.",
            SuggestionServiceFailureReason.TypedTermContextRequired =>
                "A term-correction decision requires its current transcript and dictionary context.",
            SuggestionServiceFailureReason.RepetitionPayloadInvalid =>
                "The repetition suggestion payload is invalid.",
            _ => "The suggestion operation failed.",
        };
}

public sealed class RepetitionReasonFeatures
{
    public const string AdjacentExactUtteranceRule =
        "adjacent_exact_utterance";
    public const string AdjacentExactTokenSequenceRule =
        "adjacent_exact_token_sequence";

    public RepetitionReasonFeatures(
        string rule,
        EntityId<SourceSpanIdTag> comparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> comparisonTokenIds,
        int matchingTokenCount,
        bool exactNfkcMatch,
        bool structurallyAdjacent,
        bool sameSpeakerRequired,
        bool candidateIsLaterOccurrence,
        bool retentionChoiceRequired)
    {
        ArgumentNullException.ThrowIfNull(comparisonSourceSpanId);
        ArgumentNullException.ThrowIfNull(comparisonTokenIds);
        if (!string.Equals(
                rule,
                AdjacentExactUtteranceRule,
                StringComparison.Ordinal)
            && !string.Equals(
                rule,
                AdjacentExactTokenSequenceRule,
                StringComparison.Ordinal))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        EntityId<TranscriptTokenIdTag>[] copiedTokenIds =
            SuggestionServiceContract.CopyUniqueTokenIds(comparisonTokenIds);
        bool expectedSameSpeaker = string.Equals(
            rule,
            AdjacentExactUtteranceRule,
            StringComparison.Ordinal);
        if (copiedTokenIds.Length == 0
            || matchingTokenCount != copiedTokenIds.Length
            || !exactNfkcMatch
            || !structurallyAdjacent
            || sameSpeakerRequired != expectedSameSpeaker
            || !candidateIsLaterOccurrence
            || !retentionChoiceRequired)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        Rule = rule;
        ComparisonSourceSpanId = comparisonSourceSpanId;
        ComparisonTokenIds = Array.AsReadOnly(copiedTokenIds);
        MatchingTokenCount = matchingTokenCount;
        ExactNfkcMatch = exactNfkcMatch;
        StructurallyAdjacent = structurallyAdjacent;
        SameSpeakerRequired = sameSpeakerRequired;
        CandidateIsLaterOccurrence = candidateIsLaterOccurrence;
        RetentionChoiceRequired = retentionChoiceRequired;
    }

    public string Rule { get; }

    public EntityId<SourceSpanIdTag> ComparisonSourceSpanId { get; }

    public IReadOnlyList<EntityId<TranscriptTokenIdTag>> ComparisonTokenIds { get; }

    public int MatchingTokenCount { get; }

    public bool ExactNfkcMatch { get; }

    public bool StructurallyAdjacent { get; }

    public bool SameSpeakerRequired { get; }

    public bool CandidateIsLaterOccurrence { get; }

    public bool RetentionChoiceRequired { get; }
}

public sealed class RepetitionSuggestionPayload
{
    public const string CanonicalRuleVersion =
        "fr005-2026-08-31.repetition.v1";

    public RepetitionSuggestionPayload(
        ContentDigest candidateKey,
        EntityId<ModelRunIdTag> modelRunId,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<SourceSpanIdTag> sourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> evidenceTokenIds,
        string ruleVersion,
        SuggestionKind kind,
        SuggestionStatus status,
        string reason,
        double? confidence,
        bool automaticDeletionAllowed,
        bool humanReviewRequired,
        RepetitionReasonFeatures features)
    {
        ArgumentNullException.ThrowIfNull(candidateKey);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        ArgumentNullException.ThrowIfNull(evidenceTokenIds);
        ArgumentNullException.ThrowIfNull(features);
        EntityId<TranscriptTokenIdTag>[] copiedEvidenceTokenIds =
            SuggestionServiceContract.CopyUniqueTokenIds(evidenceTokenIds);
        SuggestionServiceContract.ValidateRepetitionText(reason);

        bool sourceSpanRelationshipIsValid = string.Equals(
                features.Rule,
                RepetitionReasonFeatures.AdjacentExactUtteranceRule,
                StringComparison.Ordinal)
            ? features.ComparisonSourceSpanId != sourceSpanId
            : features.ComparisonSourceSpanId == sourceSpanId;
        if (!string.Equals(
                ruleVersion,
                CanonicalRuleVersion,
                StringComparison.Ordinal)
            || kind != SuggestionKind.DeleteRange
            || status != SuggestionStatus.Proposed
            || confidence is not null
            || automaticDeletionAllowed
            || !humanReviewRequired
            || copiedEvidenceTokenIds.Length == 0
            || copiedEvidenceTokenIds.Length != features.MatchingTokenCount
            || copiedEvidenceTokenIds.Intersect(features.ComparisonTokenIds).Any()
            || !sourceSpanRelationshipIsValid
            || candidateKey != ComputeCandidateKey(
                sourceRevisionId,
                sourceRevisionDigest,
                features.Rule,
                sourceSpanId,
                copiedEvidenceTokenIds,
                features.ComparisonSourceSpanId,
                features.ComparisonTokenIds))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        CandidateKey = candidateKey;
        ModelRunId = modelRunId;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        SourceSpanId = sourceSpanId;
        EvidenceTokenIds = Array.AsReadOnly(copiedEvidenceTokenIds);
        RuleVersion = ruleVersion;
        Kind = kind;
        Status = status;
        Reason = reason;
        Confidence = confidence;
        AutomaticDeletionAllowed = automaticDeletionAllowed;
        HumanReviewRequired = humanReviewRequired;
        Features = features;
    }

    public ContentDigest CandidateKey { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public IReadOnlyList<EntityId<TranscriptTokenIdTag>> EvidenceTokenIds { get; }

    public string RuleVersion { get; }

    public SuggestionKind Kind { get; }

    public SuggestionStatus Status { get; }

    public string Reason { get; }

    public double? Confidence { get; }

    public bool AutomaticDeletionAllowed { get; }

    public bool HumanReviewRequired { get; }

    public RepetitionReasonFeatures Features { get; }

    internal static ContentDigest ComputeCandidateKey(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        string rule,
        EntityId<SourceSpanIdTag> sourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> evidenceTokenIds,
        EntityId<SourceSpanIdTag> comparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> comparisonTokenIds)
    {
        var fields = new List<string>(
            6 + evidenceTokenIds.Count + comparisonTokenIds.Count)
        {
            CanonicalRuleVersion,
            sourceRevisionId.ToString(),
            sourceRevisionDigest.ToString(),
            rule,
            sourceSpanId.ToString(),
        };
        fields.AddRange(evidenceTokenIds.Select(static id => id.ToString()));
        fields.Add(comparisonSourceSpanId.ToString());
        fields.AddRange(comparisonTokenIds.Select(static id => id.ToString()));
        string canonical = string.Concat(string.Join("\0", fields), "\n");
        return ContentDigest.Compute(
            SuggestionServiceContract.StrictUtf8.GetBytes(canonical));
    }
}

public abstract class SuggestionCandidate
{
    internal SuggestionCandidate(
        Suggestion suggestion,
        Suggestion producerSuggestion,
        ContentDigest sourceDigest,
        SuggestionProducerKind producer,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(producerSuggestion);
        ArgumentNullException.ThrowIfNull(sourceDigest);
        if (!Enum.IsDefined(producer)
            || producerSuggestion.Status != SuggestionStatus.Proposed
            || !SuggestionServiceContract.HasSameSuggestionEvidence(
                suggestion,
                producerSuggestion)
            || parentSuggestionId == suggestion.Id
            || replacementSuggestionId == suggestion.Id
            || parentSuggestionId is not null
                && parentSuggestionId == replacementSuggestionId
            || replacementSuggestionId is not null
                && suggestion.Status != SuggestionStatus.Reviewed)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Suggestion = suggestion;
        ProducerSuggestion = producerSuggestion;
        SourceDigest = sourceDigest;
        Producer = producer;
        ParentSuggestionId = parentSuggestionId;
        ReplacementSuggestionId = replacementSuggestionId;
    }

    public Suggestion Suggestion { get; }

    public EntityId<SuggestionIdTag> Id => Suggestion.Id;

    public SuggestionKind Kind => Suggestion.Kind;

    public SuggestionStatus Status => Suggestion.Status;

    public OriginRef Origin => Suggestion.Origin;

    public ContentDigest SourceDigest { get; }

    public SuggestionProducerKind Producer { get; }

    public EntityId<SuggestionIdTag>? ParentSuggestionId { get; }

    public EntityId<SuggestionIdTag>? ReplacementSuggestionId { get; }

    internal Suggestion ProducerSuggestion { get; }

    internal abstract SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId);
}

public abstract class DeleteRangeSuggestionCandidate : SuggestionCandidate
{
    internal DeleteRangeSuggestionCandidate(
        Suggestion suggestion,
        Suggestion producerSuggestion,
        ContentDigest sourceDigest,
        SuggestionProducerKind producer,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            producerSuggestion,
            sourceDigest,
            producer,
            parentSuggestionId,
            replacementSuggestionId)
    {
        if (suggestion.Kind != SuggestionKind.DeleteRange)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }
    }
}

public sealed class TermSuggestionCandidate : SuggestionCandidate
{
    internal TermSuggestionCandidate(
        TermCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceRevisionDigest,
            SuggestionProducerKind.TermCorrection,
            parentSuggestionId,
            replacementSuggestionId)
    {
        if (suggestion.Kind != SuggestionKind.TermCorrection)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Payload = payload;
    }

    public TermCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new TermSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class SilenceSuggestionCandidate : DeleteRangeSuggestionCandidate
{
    internal SilenceSuggestionCandidate(
        SilenceCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceSpan.SourceDigest,
            SuggestionProducerKind.Silence,
            parentSuggestionId,
            replacementSuggestionId)
    {
        Payload = payload;
    }

    public SilenceCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new SilenceSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class EditorialPhraseSuggestionCandidate :
    DeleteRangeSuggestionCandidate
{
    internal EditorialPhraseSuggestionCandidate(
        EditorialPhraseCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceRevisionDigest,
            SuggestionProducerKind.EditorialPhrase,
            parentSuggestionId,
            replacementSuggestionId)
    {
        Payload = payload;
    }

    public EditorialPhraseCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new EditorialPhraseSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class RepetitionSuggestionCandidate :
    DeleteRangeSuggestionCandidate
{
    internal RepetitionSuggestionCandidate(
        RepetitionSuggestionPayload payload,
        Suggestion producerSuggestion,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            producerSuggestion,
            payload?.SourceRevisionDigest
                ?? throw new ArgumentNullException(nameof(payload)),
            SuggestionProducerKind.Repetition,
            parentSuggestionId,
            replacementSuggestionId)
    {
        if (producerSuggestion.ModelRunId != payload.ModelRunId
            || producerSuggestion.Kind != payload.Kind
            || producerSuggestion.Status != payload.Status
            || producerSuggestion.Origin != OriginRef.FromSourceSpan(
                payload.SourceSpanId)
            || !string.Equals(
                producerSuggestion.Reason,
                payload.Reason,
                StringComparison.Ordinal)
            || producerSuggestion.Confidence != payload.Confidence
            || producerSuggestion.MissingContext is not null
            || producerSuggestion.Warning is not null)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Payload = payload;
    }

    public RepetitionSuggestionPayload Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new RepetitionSuggestionCandidate(
            Payload,
            ProducerSuggestion,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class TopicBoundarySuggestionCandidate : SuggestionCandidate
{
    internal TopicBoundarySuggestionCandidate(
        TopicBoundaryCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceRevisionDigest,
            SuggestionProducerKind.TopicBoundary,
            parentSuggestionId,
            replacementSuggestionId)
    {
        if (suggestion.Kind != SuggestionKind.TopicBoundary)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Payload = payload;
    }

    public TopicBoundaryCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new TopicBoundarySuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class AudioReviewSuggestionCandidate :
    DeleteRangeSuggestionCandidate
{
    internal AudioReviewSuggestionCandidate(
        AudioReviewCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceSpan.SourceDigest,
            SuggestionProducerKind.AudioReview,
            parentSuggestionId,
            replacementSuggestionId)
    {
        Payload = payload;
    }

    public AudioReviewCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new AudioReviewSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class ContextDeviationSuggestionCandidate :
    DeleteRangeSuggestionCandidate
{
    internal ContextDeviationSuggestionCandidate(
        ContextDeviationCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceRevisionDigest,
            SuggestionProducerKind.ContextDeviation,
            parentSuggestionId,
            replacementSuggestionId)
    {
        Payload = payload;
    }

    public ContextDeviationCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new ContextDeviationSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class EmphasisSuggestionCandidate : SuggestionCandidate
{
    internal EmphasisSuggestionCandidate(
        EmphasisCandidate payload,
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
        : base(
            suggestion,
            payload?.Suggestion
                ?? throw new ArgumentNullException(nameof(payload)),
            payload.SourceRevisionDigest,
            SuggestionProducerKind.Emphasis,
            parentSuggestionId,
            replacementSuggestionId)
    {
        if (suggestion.Kind != SuggestionKind.Highlight)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Payload = payload;
    }

    public EmphasisCandidate Payload { get; }

    internal override SuggestionCandidate Copy(
        Suggestion suggestion,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId) =>
        new EmphasisSuggestionCandidate(
            Payload,
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
}

public sealed class SuggestionDecisionRecord
{
    internal SuggestionDecisionRecord(
        EntityId<SuggestionDecisionIdTag> id,
        EntityId<SuggestionIdTag> candidateId,
        EntityId<ModelRunIdTag> modelRunId,
        SuggestionProducerKind producer,
        SuggestionKind suggestionKind,
        SuggestionDecisionKind decision,
        SuggestionStatus statusAfterDecision,
        OriginRef origin,
        ContentDigest sourceDigest,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        EntityId<SuggestionIdTag>? replacementSuggestionId,
        SuggestionReference? adoptedReference,
        EntityId<TranscriptRevisionIdTag>? resultingTranscriptRevisionId)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceDigest);
        if (!Enum.IsDefined(producer)
            || !Enum.IsDefined(suggestionKind)
            || !Enum.IsDefined(decision)
            || !Enum.IsDefined(statusAfterDecision)
            || !SuggestionServiceContract.IsProducerKindCompatible(
                producer,
                suggestionKind))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        bool shapeIsValid = decision switch
        {
            SuggestionDecisionKind.Rejected =>
                statusAfterDecision == SuggestionStatus.Rejected
                && replacementSuggestionId is null
                && adoptedReference is null
                && resultingTranscriptRevisionId is null,
            SuggestionDecisionKind.Modified =>
                statusAfterDecision == SuggestionStatus.Reviewed
                && replacementSuggestionId is not null
                && replacementSuggestionId != candidateId
                && adoptedReference is null
                && resultingTranscriptRevisionId is null,
            SuggestionDecisionKind.Adopted
                when suggestionKind == SuggestionKind.TermCorrection =>
                statusAfterDecision == SuggestionStatus.Adopted
                && replacementSuggestionId is null
                && adoptedReference is null
                && resultingTranscriptRevisionId is not null,
            SuggestionDecisionKind.Adopted =>
                statusAfterDecision == SuggestionStatus.Adopted
                && replacementSuggestionId is null
                && adoptedReference is not null
                && adoptedReference.SuggestionId == candidateId
                && adoptedReference.Origin == origin
                && resultingTranscriptRevisionId is null,
            _ => false,
        };
        if (!shapeIsValid)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        SuggestionServiceContract.ValidateDecisionMetadata(actorSid, reason);
        Id = id;
        CandidateId = candidateId;
        ModelRunId = modelRunId;
        Producer = producer;
        SuggestionKind = suggestionKind;
        Decision = decision;
        StatusAfterDecision = statusAfterDecision;
        Origin = origin;
        SourceDigest = sourceDigest;
        ActorSid = actorSid;
        DecidedAt = decidedAt;
        Reason = reason;
        ReplacementSuggestionId = replacementSuggestionId;
        AdoptedReference = adoptedReference;
        ResultingTranscriptRevisionId = resultingTranscriptRevisionId;
    }

    public EntityId<SuggestionDecisionIdTag> Id { get; }

    public EntityId<SuggestionIdTag> CandidateId { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public SuggestionProducerKind Producer { get; }

    public SuggestionKind SuggestionKind { get; }

    public SuggestionDecisionKind Decision { get; }

    public SuggestionStatus StatusAfterDecision { get; }

    public OriginRef Origin { get; }

    public ContentDigest SourceDigest { get; }

    public string ActorSid { get; }

    public DateTimeOffset DecidedAt { get; }

    public string Reason { get; }

    public EntityId<SuggestionIdTag>? ReplacementSuggestionId { get; }

    public SuggestionReference? AdoptedReference { get; }

    public EntityId<TranscriptRevisionIdTag>? ResultingTranscriptRevisionId { get; }
}

public sealed class SuggestionCandidateDisplayState
{
    internal SuggestionCandidateDisplayState(
        SuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        SuggestionCandidateFreshness freshness,
        SuggestionStatus status,
        SuggestionDecisionRecord? decision)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentSourceDigest);
        if (!Enum.IsDefined(freshness) || !Enum.IsDefined(status))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch);
        }

        Candidate = candidate;
        CurrentSourceDigest = currentSourceDigest;
        Freshness = freshness;
        Status = status;
        Decision = decision;
    }

    public SuggestionCandidate Candidate { get; }

    public ContentDigest CurrentSourceDigest { get; }

    public SuggestionCandidateFreshness Freshness { get; }

    public SuggestionStatus Status { get; }

    public SuggestionDecisionRecord? Decision { get; }

    public bool CanDecide =>
        Freshness == SuggestionCandidateFreshness.Current
        && Decision is null
        && Candidate.ReplacementSuggestionId is null
        && Status == SuggestionStatus.Proposed;
}

public abstract class SuggestionDecisionResult
{
    internal SuggestionDecisionResult(
        SuggestionCandidate originalCandidate,
        SuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(originalCandidate);
        ArgumentNullException.ThrowIfNull(decidedCandidate);
        ArgumentNullException.ThrowIfNull(decisionRecord);
        ArgumentNullException.ThrowIfNull(decisionHistory);
        if (originalCandidate.Id != decidedCandidate.Id
            || decisionRecord.CandidateId != originalCandidate.Id
            || decisionRecord.StatusAfterDecision != decidedCandidate.Status
            || decisionHistory.Count == 0
            || !ReferenceEquals(decisionHistory[^1], decisionRecord))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        OriginalCandidate = originalCandidate;
        DecidedCandidate = decidedCandidate;
        DecisionRecord = decisionRecord;
        DecisionHistory = Array.AsReadOnly(decisionHistory.ToArray());
    }

    public SuggestionCandidate OriginalCandidate { get; }

    public SuggestionCandidate DecidedCandidate { get; }

    public SuggestionDecisionRecord DecisionRecord { get; }

    public IReadOnlyList<SuggestionDecisionRecord> DecisionHistory { get; }
}

public sealed class SuggestionRejectionResult : SuggestionDecisionResult
{
    internal SuggestionRejectionResult(
        SuggestionCandidate originalCandidate,
        SuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        TermCandidateDecisionResult? termDecision)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        bool termShapeIsValid = originalCandidate is TermSuggestionCandidate term
            ? termDecision is not null
                && ReferenceEquals(term.Payload, termDecision.OriginalCandidate)
                && termDecision.DecisionRecord.Decision ==
                    TermCandidateDecisionKind.Rejected
            : termDecision is null;
        if (decisionRecord.Decision != SuggestionDecisionKind.Rejected
            || decidedCandidate.Status != SuggestionStatus.Rejected
            || !termShapeIsValid)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        TermDecision = termDecision;
    }

    public TermCandidateDecisionResult? TermDecision { get; }
}

public sealed class SuggestionModificationResult : SuggestionDecisionResult
{
    internal SuggestionModificationResult(
        SuggestionCandidate originalCandidate,
        SuggestionCandidate decidedCandidate,
        SuggestionCandidate replacementCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(replacementCandidate);
        if (decisionRecord.Decision != SuggestionDecisionKind.Modified
            || decidedCandidate.Status != SuggestionStatus.Reviewed
            || decidedCandidate.ReplacementSuggestionId != replacementCandidate.Id
            || replacementCandidate.ParentSuggestionId != originalCandidate.Id
            || replacementCandidate.Status != SuggestionStatus.Proposed)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        ReplacementCandidate = replacementCandidate;
    }

    public SuggestionCandidate ReplacementCandidate { get; }
}

public abstract class SuggestionAdoptionResult : SuggestionDecisionResult
{
    internal SuggestionAdoptionResult(
        SuggestionCandidate originalCandidate,
        SuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        if (decisionRecord.Decision != SuggestionDecisionKind.Adopted
            || decidedCandidate.Status != SuggestionStatus.Adopted)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }
    }
}

public sealed class TermSuggestionAdoptionResult : SuggestionAdoptionResult
{
    internal TermSuggestionAdoptionResult(
        TermSuggestionCandidate originalCandidate,
        TermSuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        TermCandidateDecisionResult termDecision)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(termDecision);
        if (!ReferenceEquals(
                originalCandidate.Payload,
                termDecision.OriginalCandidate)
            || termDecision.DecisionRecord.Decision !=
                TermCandidateDecisionKind.Adopted
            || termDecision.TranscriptEdit is null
            || decisionRecord.ResultingTranscriptRevisionId !=
                termDecision.TranscriptEdit.Revision.Id)
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        TermDecision = termDecision;
    }

    public TermCandidateDecisionResult TermDecision { get; }

    public TranscriptEditResult TranscriptEdit =>
        TermDecision.TranscriptEdit
        ?? throw new InvalidOperationException(
            "An adopted term suggestion must contain a transcript edit.");
}

public sealed class EditSuggestionAdoptionResult : SuggestionAdoptionResult
{
    internal EditSuggestionAdoptionResult(
        SuggestionCandidate originalCandidate,
        SuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EditOperation operation,
        SuggestionReference reference)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(reference);
        bool operationMatches = originalCandidate.Kind switch
        {
            SuggestionKind.DeleteRange =>
                operation.Kind == EditOperationKind.Delete,
            SuggestionKind.TopicBoundary =>
                operation.Kind == EditOperationKind.Split,
            _ => false,
        };
        if (!operationMatches
            || reference.SuggestionId != originalCandidate.Id
            || reference.Origin != originalCandidate.Origin
            || !ReferenceEquals(decisionRecord.AdoptedReference, reference))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        Operation = operation;
        Reference = reference;
    }

    public EditOperation Operation { get; }

    public SuggestionReference Reference { get; }

    public OriginRef Origin => Reference.Origin;
}

public sealed class HighlightSuggestionAdoptionResult : SuggestionAdoptionResult
{
    internal HighlightSuggestionAdoptionResult(
        EmphasisSuggestionCandidate originalCandidate,
        EmphasisSuggestionCandidate decidedCandidate,
        SuggestionDecisionRecord decisionRecord,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        Cue cue,
        SuggestionReference reference)
        : base(
            originalCandidate,
            decidedCandidate,
            decisionRecord,
            decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(reference);
        if (cue.Kind != CueKind.Graphic
            || reference.SuggestionId != originalCandidate.Id
            || reference.Origin != originalCandidate.Origin
            || !ReferenceEquals(decisionRecord.AdoptedReference, reference))
        {
            throw SuggestionServiceContract.Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        Cue = cue;
        Reference = reference;
        Text = originalCandidate.Payload.Text;
        TextSource = originalCandidate.Payload.TextSource;
    }

    public Cue Cue { get; }

    public SuggestionReference Reference { get; }

    public OriginRef Origin => Reference.Origin;

    public string Text { get; }

    public EmphasisTextSourceKind TextSource { get; }
}

public static class SuggestionService
{
    public static TermSuggestionCandidate Register(
        TermCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new TermSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static SilenceSuggestionCandidate Register(
        SilenceCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new SilenceSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static EditorialPhraseSuggestionCandidate Register(
        EditorialPhraseCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new EditorialPhraseSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static RepetitionSuggestionCandidate Register(
        RepetitionSuggestionPayload payload,
        Suggestion suggestion,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(suggestion);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new RepetitionSuggestionCandidate(
            payload,
            suggestion,
            suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static TopicBoundarySuggestionCandidate Register(
        TopicBoundaryCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new TopicBoundarySuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static AudioReviewSuggestionCandidate Register(
        AudioReviewCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new AudioReviewSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static ContextDeviationSuggestionCandidate Register(
        ContextDeviationCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new ContextDeviationSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static EmphasisSuggestionCandidate Register(
        EmphasisCandidate payload,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new EmphasisSuggestionCandidate(
            payload,
            payload.Suggestion,
            parentSuggestionId: null,
            replacementSuggestionId: null);
        ValidateRegistration(candidate, registeredCandidates, cancellationToken);
        return candidate;
    }

    public static SuggestionCandidateDisplayState GetDisplayState(
        SuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentSourceDigest);
        if (candidate is TermSuggestionCandidate)
        {
            throw Failure(
                SuggestionServiceFailureReason.TypedTermContextRequired);
        }

        SuggestionDecisionRecord? decision = ValidateDisplayHistory(
            candidate,
            decisionHistory);
        return CreateDisplayState(
            candidate,
            currentSourceDigest,
            candidate.SourceDigest == currentSourceDigest,
            decision);
    }

    public static SuggestionCandidateDisplayState GetDisplayState(
        TermSuggestionCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        SuggestionDecisionRecord? decision = ValidateDisplayHistory(
            candidate,
            decisionHistory);
        ValidateTermHistoryContext(candidate, currentRevision, decision);
        ContentDigest currentSourceDigest;
        bool isCurrent;
        try
        {
            isCurrent = TermCandidateService.MatchesCurrentSnapshot(
                candidate.Payload,
                currentRevision,
                currentSourceSpans,
                currentTermEntry,
                out currentSourceDigest);
        }
        catch (TermCandidateException exception)
        {
            throw MapTermFailure(exception.Reason);
        }

        return CreateDisplayState(
            candidate,
            currentSourceDigest,
            isCurrent,
            decision);
    }

    private static SuggestionDecisionRecord? ValidateDisplayHistory(
        SuggestionCandidate candidate,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
    {
        SuggestionDecisionRecord[] history = CopyAndValidateHistory(
            decisionHistory);
        return ValidateCandidateHistory(candidate, history);
    }

    private static SuggestionCandidateDisplayState CreateDisplayState(
        SuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        bool isCurrent,
        SuggestionDecisionRecord? decision) =>
        new(
            candidate,
            currentSourceDigest,
            isCurrent
                ? SuggestionCandidateFreshness.Current
                : SuggestionCandidateFreshness.Stale,
            decision?.StatusAfterDecision ?? candidate.Status,
            decision);

    public static SuggestionRejectionResult Reject(
        SuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate is TermSuggestionCandidate)
        {
            throw Failure(
                SuggestionServiceFailureReason.TypedTermContextRequired);
        }

        SuggestionDecisionRecord[] history = PrepareDecision(
            candidate,
            currentSourceDigest,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        SuggestionCandidate rejected = CopyCandidate(
            candidate,
            SuggestionStatus.Rejected,
            candidate.ParentSuggestionId,
            replacementSuggestionId: null);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Rejected,
            SuggestionStatus.Rejected,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new SuggestionRejectionResult(
            candidate,
            rejected,
            record,
            appended,
            termDecision: null);
    }

    public static SuggestionRejectionResult RejectTerm(
        TermSuggestionCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        SuggestionDecisionRecord[] history = PrepareDecisionMetadata(
            candidate,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken,
            currentRevision);
        TermCandidateDecisionResult termDecision;
        try
        {
            termDecision = TermCandidateService.RejectCandidate(
                candidate.Payload,
                currentRevision,
                currentSourceSpans,
                currentTermEntry,
                actorSid,
                decidedAt,
                reason);
        }
        catch (TermCandidateException exception)
        {
            throw MapTermFailure(exception.Reason);
        }

        SuggestionCandidate rejected = CopyCandidate(
            candidate,
            SuggestionStatus.Rejected,
            candidate.ParentSuggestionId,
            replacementSuggestionId: null);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Rejected,
            SuggestionStatus.Rejected,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new SuggestionRejectionResult(
            candidate,
            rejected,
            record,
            appended,
            termDecision);
    }

    public static SuggestionModificationResult Modify(
        SuggestionCandidate candidate,
        SuggestionCandidate replacement,
        ContentDigest currentSourceDigest,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(replacement);
        if (candidate is TermSuggestionCandidate)
        {
            throw Failure(
                SuggestionServiceFailureReason.TypedTermContextRequired);
        }

        SuggestionDecisionRecord[] history = PrepareDecision(
            candidate,
            currentSourceDigest,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        return CreateModificationResult(
            candidate,
            replacement,
            currentSourceDigest,
            history,
            decisionId,
            actorSid,
            decidedAt,
            reason);
    }

    public static SuggestionModificationResult ModifyTerm(
        TermSuggestionCandidate candidate,
        TermSuggestionCandidate replacement,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        TermEntry currentReplacementTermEntry,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        ArgumentNullException.ThrowIfNull(currentReplacementTermEntry);
        SuggestionDecisionRecord[] history = PrepareDecisionMetadata(
            candidate,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken,
            currentRevision);
        ContentDigest currentSourceDigest;
        try
        {
            if (!TermCandidateService.MatchesCurrentSnapshot(
                    candidate.Payload,
                    currentRevision,
                    currentSourceSpans,
                    currentTermEntry,
                    out currentSourceDigest))
            {
                throw Failure(SuggestionServiceFailureReason.CandidateStale);
            }

            if (!TermCandidateService.MatchesCurrentSnapshot(
                    replacement.Payload,
                    currentRevision,
                    currentSourceSpans,
                    currentReplacementTermEntry,
                    out ContentDigest replacementSourceDigest)
                || replacementSourceDigest != currentSourceDigest)
            {
                throw Failure(
                    SuggestionServiceFailureReason.ReplacementBindingMismatch);
            }
        }
        catch (TermCandidateException exception)
        {
            throw MapTermFailure(exception.Reason);
        }

        return CreateModificationResult(
            candidate,
            replacement,
            currentSourceDigest,
            history,
            decisionId,
            actorSid,
            decidedAt,
            reason);
    }

    private static SuggestionModificationResult CreateModificationResult(
        SuggestionCandidate candidate,
        SuggestionCandidate replacement,
        ContentDigest currentSourceDigest,
        SuggestionDecisionRecord[] history,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason)
    {
        SuggestionDecisionRecord? replacementDecision = ValidateCandidateHistory(
            replacement,
            history);
        bool replacementIdentityWasUsed = history.Any(record =>
            record.CandidateId == replacement.Id
            || record.ReplacementSuggestionId == replacement.Id);
        if (candidate.Id == replacement.Id || replacementIdentityWasUsed)
        {
            throw Failure(
                SuggestionServiceFailureReason.ReplacementIdentityConflict);
        }

        if (candidate is RepetitionSuggestionCandidate originalRepetition
            && replacement is RepetitionSuggestionCandidate replacementRepetition
            && originalRepetition.Payload.CandidateKey ==
                replacementRepetition.Payload.CandidateKey)
        {
            throw Failure(
                SuggestionServiceFailureReason.DuplicateRepetitionCandidate);
        }

        if (replacementDecision is not null
            || replacement.Status != SuggestionStatus.Proposed
            || replacement.ParentSuggestionId is not null
            || replacement.ReplacementSuggestionId is not null
            || candidate.Producer != replacement.Producer
            || candidate.Kind != replacement.Kind
            || replacement.SourceDigest != currentSourceDigest)
        {
            throw Failure(
                SuggestionServiceFailureReason.ReplacementBindingMismatch);
        }

        SuggestionCandidate reviewedParent = CopyCandidate(
            candidate,
            SuggestionStatus.Reviewed,
            candidate.ParentSuggestionId,
            replacement.Id);
        SuggestionCandidate replacementChild = CopyCandidate(
            replacement,
            SuggestionStatus.Proposed,
            candidate.Id,
            replacementSuggestionId: null);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Modified,
            SuggestionStatus.Reviewed,
            actorSid,
            decidedAt,
            reason,
            replacement.Id,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new SuggestionModificationResult(
            candidate,
            reviewedParent,
            replacementChild,
            record,
            appended);
    }

    public static TermSuggestionAdoptionResult AdoptTerm(
        TermSuggestionCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        EntityId<TranscriptRevisionIdTag> newRevisionId,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        SuggestionDecisionRecord[] history = PrepareDecisionMetadata(
            candidate,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken,
            currentRevision);
        if (history.Any(record =>
                record.ResultingTranscriptRevisionId == newRevisionId))
        {
            throw Failure(SuggestionServiceFailureReason.InvalidDecision);
        }

        TermCandidateDecisionResult termDecision;
        try
        {
            termDecision = TermCandidateService.AdoptCandidate(
                candidate.Payload,
                currentRevision,
                currentSourceSpans,
                currentTermEntry,
                newRevisionId,
                actorSid,
                decidedAt,
                reason);
        }
        catch (TermCandidateException exception)
        {
            throw MapTermFailure(exception.Reason);
        }
        catch (TranscriptEditorException exception)
        {
            throw MapTranscriptFailure(exception.Reason);
        }

        TermSuggestionCandidate adopted = (TermSuggestionCandidate)CopyCandidate(
            candidate,
            SuggestionStatus.Adopted,
            candidate.ParentSuggestionId,
            replacementSuggestionId: null);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            adoptedReference: null,
            termDecision.TranscriptEdit?.Revision.Id);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new TermSuggestionAdoptionResult(
            candidate,
            adopted,
            record,
            appended,
            termDecision);
    }

    public static EditSuggestionAdoptionResult AdoptDeleteRange(
        DeleteRangeSuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        EntityId<CompositionElementIdTag> elementId,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(elementId);
        SuggestionDecisionRecord[] history = PrepareDecision(
            candidate,
            currentSourceDigest,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        EditOperation operation = EditOperation.Delete(elementId);
        SuggestionCandidate adopted = CopyCandidate(
            candidate,
            SuggestionStatus.Adopted,
            candidate.ParentSuggestionId,
            replacementSuggestionId: null);
        SuggestionReference reference = SuggestionReference.FromAdoptedSuggestion(
            adopted.Suggestion);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            reference,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new EditSuggestionAdoptionResult(
            candidate,
            adopted,
            record,
            appended,
            operation,
            reference);
    }

    public static EditSuggestionAdoptionResult AdoptTopicBoundary(
        TopicBoundarySuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        EntityId<CompositionElementIdTag> elementId,
        RationalTime splitOffset,
        EntityId<CompositionElementIdTag> rightElementId,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(rightElementId);
        SuggestionDecisionRecord[] history = PrepareDecision(
            candidate,
            currentSourceDigest,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        EditOperation operation = EditOperation.Split(
            elementId,
            splitOffset,
            rightElementId);
        SuggestionCandidate adopted = CopyCandidate(
            candidate,
            SuggestionStatus.Adopted,
            candidate.ParentSuggestionId,
            replacementSuggestionId: null);
        SuggestionReference reference = SuggestionReference.FromAdoptedSuggestion(
            adopted.Suggestion);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            reference,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new EditSuggestionAdoptionResult(
            candidate,
            adopted,
            record,
            appended,
            operation,
            reference);
    }

    public static HighlightSuggestionAdoptionResult AdoptHighlight(
        EmphasisSuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string? regionName,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(cueId);
        SuggestionDecisionRecord[] history = PrepareDecision(
            candidate,
            currentSourceDigest,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        var cue = new Cue(cueId, CueKind.Graphic, outputRange, regionName);
        EmphasisSuggestionCandidate adopted =
            (EmphasisSuggestionCandidate)CopyCandidate(
                candidate,
                SuggestionStatus.Adopted,
                candidate.ParentSuggestionId,
                replacementSuggestionId: null);
        SuggestionReference reference = SuggestionReference.FromAdoptedSuggestion(
            adopted.Suggestion);
        SuggestionDecisionRecord record = CreateDecisionRecord(
            candidate,
            decisionId,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId: null,
            reference,
            resultingTranscriptRevisionId: null);
        SuggestionDecisionRecord[] appended = Append(history, record);
        return new HighlightSuggestionAdoptionResult(
            candidate,
            adopted,
            record,
            appended,
            cue,
            reference);
    }

    private static void ValidateRegistration(
        SuggestionCandidate candidate,
        IReadOnlyList<SuggestionCandidate> registeredCandidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registeredCandidates);
        var ids = new HashSet<EntityId<SuggestionIdTag>>();
        var repetitionKeys = new HashSet<ContentDigest>();
        foreach (SuggestionCandidate registered in registeredCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registered is null)
            {
                throw Failure(
                    SuggestionServiceFailureReason.CandidateBindingMismatch);
            }

            if (!ids.Add(registered.Id))
            {
                throw Failure(SuggestionServiceFailureReason.DuplicateCandidate);
            }

            if (registered is RepetitionSuggestionCandidate repetition
                && !repetitionKeys.Add(repetition.Payload.CandidateKey))
            {
                throw Failure(
                    SuggestionServiceFailureReason.DuplicateRepetitionCandidate);
            }
        }

        if (!ids.Add(candidate.Id))
        {
            throw Failure(SuggestionServiceFailureReason.DuplicateCandidate);
        }

        if (candidate is RepetitionSuggestionCandidate newRepetition
            && !repetitionKeys.Add(newRepetition.Payload.CandidateKey))
        {
            throw Failure(
                SuggestionServiceFailureReason.DuplicateRepetitionCandidate);
        }
    }

    private static SuggestionDecisionRecord[] PrepareDecision(
        SuggestionCandidate candidate,
        ContentDigest currentSourceDigest,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSourceDigest);
        SuggestionDecisionRecord[] history = PrepareDecisionMetadata(
            candidate,
            decisionHistory,
            decisionId,
            actorSid,
            decidedAt,
            reason,
            cancellationToken);
        if (candidate.SourceDigest != currentSourceDigest)
        {
            throw Failure(SuggestionServiceFailureReason.CandidateStale);
        }

        return history;
    }

    private static SuggestionDecisionRecord[] PrepareDecisionMetadata(
        SuggestionCandidate candidate,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken,
        TranscriptRevision? currentTermRevision = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(decisionId);
        cancellationToken.ThrowIfCancellationRequested();
        SuggestionDecisionRecord[] history = CopyAndValidateHistory(
            decisionHistory);
        SuggestionDecisionRecord? existing = ValidateCandidateHistory(
            candidate,
            history);
        if (candidate is TermSuggestionCandidate termCandidate
            && currentTermRevision is not null)
        {
            ValidateTermHistoryContext(
                termCandidate,
                currentTermRevision,
                existing);
            if (decidedAt < currentTermRevision.CreatedAt)
            {
                throw Failure(SuggestionServiceFailureReason.InvalidDecision);
            }
        }

        if (existing is not null
            || candidate.Status != SuggestionStatus.Proposed
            || candidate.ReplacementSuggestionId is not null)
        {
            throw Failure(
                SuggestionServiceFailureReason.CandidateAlreadyDecided);
        }

        if (history.Any(record => record.Id == decisionId))
        {
            throw Failure(SuggestionServiceFailureReason.DuplicateDecision);
        }

        SuggestionDecisionRecord? parentModification = history.SingleOrDefault(
            record => record.ReplacementSuggestionId == candidate.Id);
        if (parentModification is not null
            && decidedAt < parentModification.DecidedAt)
        {
            throw Failure(SuggestionServiceFailureReason.InvalidDecision);
        }

        SuggestionServiceContract.ValidateDecisionMetadata(actorSid, reason);
        return history;
    }

    private static SuggestionDecisionRecord[] CopyAndValidateHistory(
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory)
    {
        ArgumentNullException.ThrowIfNull(decisionHistory);
        var ids = new HashSet<EntityId<SuggestionDecisionIdTag>>();
        var candidateIds = new HashSet<EntityId<SuggestionIdTag>>();
        var replacementIds = new HashSet<EntityId<SuggestionIdTag>>();
        var resultingTranscriptRevisionIds = new HashSet<
            EntityId<TranscriptRevisionIdTag>>();
        var replacementParents = new Dictionary<
            EntityId<SuggestionIdTag>,
            SuggestionDecisionRecord>();
        var replacementsByParent = new Dictionary<
            EntityId<SuggestionIdTag>,
            EntityId<SuggestionIdTag>>();
        var copied = new SuggestionDecisionRecord[decisionHistory.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            SuggestionDecisionRecord record = decisionHistory[index]
                ?? throw Failure(
                    SuggestionServiceFailureReason.DecisionHistoryInvalid);
            if (!ids.Add(record.Id))
            {
                throw Failure(SuggestionServiceFailureReason.DuplicateDecision);
            }

            if (!candidateIds.Add(record.CandidateId))
            {
                throw Failure(
                    SuggestionServiceFailureReason.DecisionHistoryInvalid);
            }

            if (record.ResultingTranscriptRevisionId is { } resultingRevisionId
                && !resultingTranscriptRevisionIds.Add(resultingRevisionId))
            {
                throw Failure(
                    SuggestionServiceFailureReason.DecisionHistoryInvalid);
            }

            if (replacementParents.TryGetValue(
                    record.CandidateId,
                    out SuggestionDecisionRecord? parent)
                && (record.DecidedAt < parent.DecidedAt
                    || record.Producer != parent.Producer
                    || record.SuggestionKind != parent.SuggestionKind
                    || record.SourceDigest != parent.SourceDigest))
            {
                throw Failure(
                    SuggestionServiceFailureReason.DecisionHistoryInvalid);
            }

            if (record.ReplacementSuggestionId is { } replacementId)
            {
                if (candidateIds.Contains(replacementId)
                    || !replacementIds.Add(replacementId))
                {
                    throw Failure(
                        SuggestionServiceFailureReason.DecisionHistoryInvalid);
                }

                replacementParents.Add(replacementId, record);
                replacementsByParent.Add(record.CandidateId, replacementId);
            }

            copied[index] = record;
        }

        foreach (EntityId<SuggestionIdTag> start in replacementsByParent.Keys)
        {
            var visited = new HashSet<EntityId<SuggestionIdTag>>();
            EntityId<SuggestionIdTag> current = start;
            while (replacementsByParent.TryGetValue(current, out var next))
            {
                if (!visited.Add(current))
                {
                    throw Failure(
                        SuggestionServiceFailureReason.DecisionHistoryInvalid);
                }

                current = next;
            }
        }

        return copied;
    }

    private static SuggestionDecisionRecord? ValidateCandidateHistory(
        SuggestionCandidate candidate,
        IReadOnlyList<SuggestionDecisionRecord> history)
    {
        SuggestionDecisionRecord? ownDecision = history.SingleOrDefault(
            record => record.CandidateId == candidate.Id);
        if (ownDecision is not null
            && (ownDecision.ModelRunId != candidate.Suggestion.ModelRunId
                || ownDecision.Producer != candidate.Producer
                || ownDecision.SuggestionKind != candidate.Kind
                || ownDecision.Origin != candidate.Origin
                || ownDecision.SourceDigest != candidate.SourceDigest))
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        if (candidate is TermSuggestionCandidate termCandidate
            && ownDecision?.ResultingTranscriptRevisionId ==
                termCandidate.Payload.SourceRevisionId)
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        SuggestionDecisionRecord? replacementParent = history.SingleOrDefault(
            record => record.ReplacementSuggestionId == candidate.Id);
        if (candidate.ParentSuggestionId is null)
        {
            if (replacementParent is not null)
            {
                throw Failure(
                    SuggestionServiceFailureReason.DecisionHistoryInvalid);
            }
        }
        else if (replacementParent is null
            || replacementParent.CandidateId != candidate.ParentSuggestionId
            || replacementParent.Decision != SuggestionDecisionKind.Modified
            || replacementParent.Producer != candidate.Producer
            || replacementParent.SuggestionKind != candidate.Kind
            || replacementParent.SourceDigest != candidate.SourceDigest)
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        if (candidate.ReplacementSuggestionId is not null
            && (ownDecision?.Decision != SuggestionDecisionKind.Modified
                || ownDecision.ReplacementSuggestionId !=
                    candidate.ReplacementSuggestionId))
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        if (candidate.Status != SuggestionStatus.Proposed
            && (ownDecision is null
                || ownDecision.StatusAfterDecision != candidate.Status))
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }

        return ownDecision;
    }

    private static void ValidateTermHistoryContext(
        TermSuggestionCandidate candidate,
        TranscriptRevision currentRevision,
        SuggestionDecisionRecord? decision)
    {
        if (decision is null
            || candidate.Payload.SourceRevisionId != currentRevision.Id)
        {
            return;
        }

        bool resultReusesSourceParent =
            currentRevision.ParentRevisionId is { } parentRevisionId
            && decision.ResultingTranscriptRevisionId == parentRevisionId;
        if (decision.DecidedAt < currentRevision.CreatedAt
            || resultReusesSourceParent)
        {
            throw Failure(
                SuggestionServiceFailureReason.DecisionHistoryInvalid);
        }
    }

    private static SuggestionCandidate CopyCandidate(
        SuggestionCandidate candidate,
        SuggestionStatus status,
        EntityId<SuggestionIdTag>? parentSuggestionId,
        EntityId<SuggestionIdTag>? replacementSuggestionId)
    {
        Suggestion source = candidate.ProducerSuggestion;
        var suggestion = new Suggestion(
            source.Id,
            source.ModelRunId,
            source.Kind,
            status,
            source.Origin,
            source.Reason,
            source.Confidence,
            source.MissingContext,
            source.Warning);
        return candidate.Copy(
            suggestion,
            parentSuggestionId,
            replacementSuggestionId);
    }

    private static SuggestionDecisionRecord CreateDecisionRecord(
        SuggestionCandidate candidate,
        EntityId<SuggestionDecisionIdTag> decisionId,
        SuggestionDecisionKind decision,
        SuggestionStatus statusAfterDecision,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        EntityId<SuggestionIdTag>? replacementSuggestionId,
        SuggestionReference? adoptedReference,
        EntityId<TranscriptRevisionIdTag>? resultingTranscriptRevisionId) =>
        new(
            decisionId,
            candidate.Id,
            candidate.Suggestion.ModelRunId,
            candidate.Producer,
            candidate.Kind,
            decision,
            statusAfterDecision,
            candidate.Origin,
            candidate.SourceDigest,
            actorSid,
            decidedAt,
            reason,
            replacementSuggestionId,
            adoptedReference,
            resultingTranscriptRevisionId);

    private static SuggestionDecisionRecord[] Append(
        IReadOnlyList<SuggestionDecisionRecord> history,
        SuggestionDecisionRecord record)
    {
        var appended = new SuggestionDecisionRecord[checked(history.Count + 1)];
        for (int index = 0; index < history.Count; index++)
        {
            appended[index] = history[index];
        }

        appended[^1] = record;
        return appended;
    }

    private static SuggestionServiceException MapTermFailure(
        TermCandidateFailureReason reason) =>
        reason switch
        {
            TermCandidateFailureReason.CandidateAlreadyDecided =>
                Failure(SuggestionServiceFailureReason.CandidateAlreadyDecided),
            TermCandidateFailureReason.CandidateStale or
            TermCandidateFailureReason.TermEntryChanged =>
                Failure(SuggestionServiceFailureReason.CandidateStale),
            TermCandidateFailureReason.InvalidDecision or
            TermCandidateFailureReason.DecisionPrecedesSource =>
                Failure(SuggestionServiceFailureReason.InvalidDecision),
            _ => Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch),
        };

    private static SuggestionServiceException MapTranscriptFailure(
        TranscriptEditorFailureReason reason) =>
        reason switch
        {
            TranscriptEditorFailureReason.InvalidActor or
            TranscriptEditorFailureReason.InvalidReason or
            TranscriptEditorFailureReason.TimestampPrecedesSource or
            TranscriptEditorFailureReason.RevisionIdentityConflict =>
                Failure(SuggestionServiceFailureReason.InvalidDecision),
            TranscriptEditorFailureReason.TokenNotFound or
            TranscriptEditorFailureReason.SourceTextChanged =>
                Failure(SuggestionServiceFailureReason.CandidateStale),
            _ => Failure(
                SuggestionServiceFailureReason.CandidateBindingMismatch),
        };

    private static SuggestionServiceException Failure(
        SuggestionServiceFailureReason reason) =>
        SuggestionServiceContract.Failure(reason);
}

internal static class SuggestionServiceContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool IsProducerKindCompatible(
        SuggestionProducerKind producer,
        SuggestionKind kind) =>
        producer switch
        {
            SuggestionProducerKind.TermCorrection =>
                kind == SuggestionKind.TermCorrection,
            SuggestionProducerKind.Silence or
            SuggestionProducerKind.EditorialPhrase or
            SuggestionProducerKind.Repetition or
            SuggestionProducerKind.AudioReview or
            SuggestionProducerKind.ContextDeviation =>
                kind == SuggestionKind.DeleteRange,
            SuggestionProducerKind.TopicBoundary =>
                kind == SuggestionKind.TopicBoundary,
            SuggestionProducerKind.Emphasis =>
                kind == SuggestionKind.Highlight,
            _ => false,
        };

    internal static SuggestionServiceException Failure(
        SuggestionServiceFailureReason reason) =>
        new(reason);

    internal static EntityId<TranscriptTokenIdTag>[] CopyUniqueTokenIds(
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> tokenIds)
    {
        var unique = new HashSet<EntityId<TranscriptTokenIdTag>>();
        var copied = new EntityId<TranscriptTokenIdTag>[tokenIds.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            EntityId<TranscriptTokenIdTag> tokenId = tokenIds[index]
                ?? throw Failure(
                    SuggestionServiceFailureReason.RepetitionPayloadInvalid);
            if (!unique.Add(tokenId))
            {
                throw Failure(
                    SuggestionServiceFailureReason.RepetitionPayloadInvalid);
            }

            copied[index] = tokenId;
        }

        return copied;
    }

    internal static void ValidateRepetitionText(string value)
    {
        if (value is null
            || string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        if (bytes.Length > 65_536)
        {
            throw Failure(
                SuggestionServiceFailureReason.RepetitionPayloadInvalid);
        }

        for (int index = 0; index < value.Length; index++)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(
                value,
                index);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Surrogate
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                throw Failure(
                    SuggestionServiceFailureReason.RepetitionPayloadInvalid);
            }

            if (char.IsHighSurrogate(value[index]))
            {
                index++;
            }
        }
    }

    internal static void ValidateDecisionMetadata(
        string actorSid,
        string reason)
    {
        try
        {
            _ = TranscriptEditorContract.ValidateCanonicalSid(
                actorSid,
                nameof(actorSid));
            TranscriptEditorContract.ValidateText(reason, nameof(reason));
        }
        catch (ArgumentException)
        {
            throw Failure(SuggestionServiceFailureReason.InvalidDecision);
        }
    }

    internal static bool HasSameSuggestionEvidence(
        Suggestion left,
        Suggestion right) =>
        left.Id == right.Id
        && left.ModelRunId == right.ModelRunId
        && left.Kind == right.Kind
        && left.Origin == right.Origin
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.Confidence == right.Confidence
        && string.Equals(
            left.MissingContext,
            right.MissingContext,
            StringComparison.Ordinal)
        && string.Equals(left.Warning, right.Warning, StringComparison.Ordinal);
}