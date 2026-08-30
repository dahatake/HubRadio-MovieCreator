using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public sealed class TopicBoundaryFeatures
{
    internal TopicBoundaryFeatures(
        EntityId<TranscriptUtteranceIdTag> previousUtteranceId,
        EntityId<TranscriptUtteranceIdTag> currentUtteranceId,
        EntityId<SourceSpanIdTag> previousSourceSpanId,
        EntityId<SourceSpanIdTag> currentSourceSpanId,
        EntityId<TranscriptSpeakerIdTag> previousSpeakerId,
        EntityId<TranscriptSpeakerIdTag> currentSpeakerId,
        TimeRange previousRange,
        TimeRange currentRange,
        RationalTime observedGap)
    {
        ArgumentNullException.ThrowIfNull(previousUtteranceId);
        ArgumentNullException.ThrowIfNull(currentUtteranceId);
        ArgumentNullException.ThrowIfNull(previousSourceSpanId);
        ArgumentNullException.ThrowIfNull(currentSourceSpanId);
        ArgumentNullException.ThrowIfNull(previousSpeakerId);
        ArgumentNullException.ThrowIfNull(currentSpeakerId);
        if (previousUtteranceId == currentUtteranceId
            || previousSourceSpanId == currentSourceSpanId
            || previousRange.Duration <= default(RationalTime)
            || currentRange.Duration <= default(RationalTime)
            || observedGap != currentRange.Start - previousRange.End)
        {
            throw new ArgumentException(
                "Topic-boundary features must describe one ordered utterance transition.",
                nameof(currentUtteranceId));
        }

        PreviousUtteranceId = previousUtteranceId;
        CurrentUtteranceId = currentUtteranceId;
        PreviousSourceSpanId = previousSourceSpanId;
        CurrentSourceSpanId = currentSourceSpanId;
        PreviousSpeakerId = previousSpeakerId;
        CurrentSpeakerId = currentSpeakerId;
        PreviousRange = previousRange;
        CurrentRange = currentRange;
        ObservedGap = observedGap;
    }

    public EntityId<TranscriptUtteranceIdTag> PreviousUtteranceId { get; }

    public EntityId<TranscriptUtteranceIdTag> CurrentUtteranceId { get; }

    public EntityId<SourceSpanIdTag> PreviousSourceSpanId { get; }

    public EntityId<SourceSpanIdTag> CurrentSourceSpanId { get; }

    public EntityId<TranscriptSpeakerIdTag> PreviousSpeakerId { get; }

    public EntityId<TranscriptSpeakerIdTag> CurrentSpeakerId { get; }

    public TimeRange PreviousRange { get; }

    public TimeRange CurrentRange { get; }

    public RationalTime ObservedGap { get; }

    public bool SpeakerChanged => PreviousSpeakerId != CurrentSpeakerId;

    public bool StructurallyAdjacent => true;

    public bool SemanticClassificationPerformed => false;

    public bool ApprovedPauseOrSimilarityThresholdApplied => false;

    public bool OptionalShotBoundaryUsed => false;
}

public sealed class TopicBoundaryCandidate
{
    internal TopicBoundaryCandidate(
        Detection detection,
        Suggestion suggestion,
        SourceSpan sourceSpan,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        RationalTime suggestedBoundary,
        string ruleVersion,
        TopicBoundaryFeatures features)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(features);
        TopicBoundaryContract.ValidateText(ruleVersion, nameof(ruleVersion));
        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        if (detection.Kind != DetectionKind.TopicBoundary
            || suggestion.Kind != SuggestionKind.TopicBoundary
            || suggestion.Status != SuggestionStatus.Proposed
            || detection.ModelRunId != suggestion.ModelRunId
            || detection.Origin != expectedOrigin
            || suggestion.Origin != expectedOrigin
            || detection.Confidence is not null
            || suggestion.Confidence is not null
            || sourceSpan.TranscriptRevisionId != sourceRevisionId
            || sourceSpan.Id != features.CurrentSourceSpanId
            || sourceSpan.Range != features.CurrentRange
            || sourceSpan.Id == features.PreviousSourceSpanId
            || suggestedBoundary != features.CurrentRange.Start
            || sourceSpan.TranscriptTokenIds.Count == 0)
        {
            throw new ArgumentException(
                "A topic-boundary candidate contains inconsistent evidence.",
                nameof(suggestion));
        }

        Detection = detection;
        Suggestion = suggestion;
        SourceSpan = sourceSpan;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        SuggestedBoundary = suggestedBoundary;
        RuleVersion = ruleVersion;
        Features = features;
    }

    public Detection Detection { get; }

    public Suggestion Suggestion { get; }

    public SourceSpan SourceSpan { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public RationalTime SuggestedBoundary { get; }

    public string RuleVersion { get; }

    public TopicBoundaryFeatures Features { get; }
}

public sealed class TopicBoundaryScanResult
{
    internal TopicBoundaryScanResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<ModelRunIdTag> modelRunId,
        string ruleVersion,
        int inspectedTransitionCount,
        RationalTime? transcriptStart,
        RationalTime? transcriptEnd,
        IReadOnlyList<TopicBoundaryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(candidates);
        TopicBoundaryContract.ValidateText(ruleVersion, nameof(ruleVersion));
        if (inspectedTransitionCount < 0
            || (transcriptStart is null) != (transcriptEnd is null)
            || transcriptStart is RationalTime start
                && transcriptEnd is RationalTime end
                && end <= start)
        {
            throw new ArgumentException(
                "A topic-boundary scan has invalid transcript bounds.",
                nameof(transcriptEnd));
        }

        TopicBoundaryCandidate[] copiedCandidates =
            new TopicBoundaryCandidate[candidates.Count];
        var detectionIds = new HashSet<EntityId<DetectionIdTag>>();
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        RationalTime? previousBoundary = null;
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            TopicBoundaryCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Topic-boundary candidates must not contain null elements.",
                    nameof(candidates));
            if (candidate.SourceRevisionId != sourceRevisionId
                || candidate.SourceRevisionDigest != sourceRevisionDigest
                || candidate.Detection.ModelRunId != modelRunId
                || !string.Equals(
                    candidate.RuleVersion,
                    ruleVersion,
                    StringComparison.Ordinal)
                || !detectionIds.Add(candidate.Detection.Id)
                || !suggestionIds.Add(candidate.Suggestion.Id)
                || previousBoundary is RationalTime prior
                    && candidate.SuggestedBoundary < prior)
            {
                throw new ArgumentException(
                    "A topic-boundary scan contains inconsistent candidates.",
                    nameof(candidates));
            }

            previousBoundary = candidate.SuggestedBoundary;
            copiedCandidates[index] = candidate;
        }

        if (copiedCandidates.Length != inspectedTransitionCount)
        {
            throw new ArgumentException(
                "Every inspected utterance transition must have one review candidate.",
                nameof(candidates));
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        ModelRunId = modelRunId;
        RuleVersion = ruleVersion;
        InspectedTransitionCount = inspectedTransitionCount;
        TranscriptStart = transcriptStart;
        TranscriptEnd = transcriptEnd;
        Candidates = Array.AsReadOnly(copiedCandidates);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public string RuleVersion { get; }

    public int InspectedTransitionCount { get; }

    public RationalTime? TranscriptStart { get; }

    public RationalTime? TranscriptEnd { get; }

    public IReadOnlyList<TopicBoundaryCandidate> Candidates { get; }
}

public sealed class TopicRangeIdTag
{
}

public sealed class TopicRangeRevisionIdTag
{
}

public sealed class TopicRangeRevision
{
    internal TopicRangeRevision(
        EntityId<TopicRangeIdTag> topicRangeId,
        EntityId<TopicRangeRevisionIdTag> id,
        EntityId<TopicRangeRevisionIdTag>? parentRevisionId,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<SuggestionIdTag>? startBoundarySuggestionId,
        EntityId<SuggestionIdTag>? endBoundarySuggestionId,
        TimeRange range,
        TimeRange? previousRange,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(topicRangeId);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        if (id == parentRevisionId
            || range.Duration <= default(RationalTime)
            || (parentRevisionId is null) != (previousRange is null)
            || previousRange is TimeRange previous && previous == range
            || startBoundarySuggestionId is not null
                && startBoundarySuggestionId == endBoundarySuggestionId)
        {
            throw new ArgumentException(
                "A topic-range revision contains inconsistent range history.",
                nameof(range));
        }

        TopicRangeId = topicRangeId;
        Id = id;
        ParentRevisionId = parentRevisionId;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        StartBoundarySuggestionId = startBoundarySuggestionId;
        EndBoundarySuggestionId = endBoundarySuggestionId;
        Range = range;
        PreviousRange = previousRange;
        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        TranscriptEditorContract.ValidateText(reason, nameof(reason));
        CreatedAt = createdAt;
        Reason = reason;
    }

    public EntityId<TopicRangeIdTag> TopicRangeId { get; }

    public EntityId<TopicRangeRevisionIdTag> Id { get; }

    public EntityId<TopicRangeRevisionIdTag>? ParentRevisionId { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<SuggestionIdTag>? StartBoundarySuggestionId { get; }

    public EntityId<SuggestionIdTag>? EndBoundarySuggestionId { get; }

    public TimeRange Range { get; }

    public TimeRange? PreviousRange { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Reason { get; }
}

public enum TopicBoundaryFailureReason
{
    SourceSpanBindingMismatch,
    ArithmeticOverflow,
    CandidateBindingMismatch,
    RangeOutsideTranscript,
    RevisionConflict,
    NoChange,
    InvalidText,
}

public sealed class TopicBoundaryException : Exception
{
    internal TopicBoundaryException(TopicBoundaryFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public TopicBoundaryFailureReason Reason { get; }

    private static string GetStableMessage(TopicBoundaryFailureReason reason) =>
        reason switch
        {
            TopicBoundaryFailureReason.SourceSpanBindingMismatch =>
                "The topic-boundary source-span evidence is inconsistent.",
            TopicBoundaryFailureReason.ArithmeticOverflow =>
                "Topic-boundary time arithmetic exceeds the supported range.",
            TopicBoundaryFailureReason.CandidateBindingMismatch =>
                "The topic-range boundary references are inconsistent.",
            TopicBoundaryFailureReason.RangeOutsideTranscript =>
                "The requested topic range is outside the transcript extent.",
            TopicBoundaryFailureReason.RevisionConflict =>
                "The requested topic-range revision conflicts with its parent.",
            TopicBoundaryFailureReason.NoChange =>
                "The requested topic range does not change its parent revision.",
            TopicBoundaryFailureReason.InvalidText =>
                "Topic-boundary text could not be processed safely.",
            _ => "The topic-boundary operation failed.",
        };
}

public static class TopicBoundaryService
{
    public const string RuleVersion = "fr006-2026-08-31.utterance-transition.v1";

    private const string DetectionDescription =
        "A structural transition between consecutive transcript utterances was observed.";
    private const string SameSpeakerReason =
        "A consecutive utterance transition is available for manual topic-boundary review.";
    private const string SpeakerChangeReason =
        "A consecutive utterance transition with a speaker change is available for manual topic-boundary review.";
    private const string MissingContext =
        "No semantic classifier or approved pause, similarity, or shot threshold was applied.";
    private const string ReviewWarning =
        "A structural utterance transition is not a confirmed semantic topic boundary.";

    public static TopicBoundaryScanResult FindCandidates(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        EntityId<ModelRunIdTag> modelRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();

        SourceSpan[] validatedSpans = ValidateAndCopySourceSpans(
            revision,
            sourceSpans);
        ContentDigest revisionDigest = ComputeRevisionDigest(revision);
        var candidates = new List<TopicBoundaryCandidate>(
            Math.Max(0, revision.Utterances.Count - 1));
        try
        {
            for (int index = 1; index < revision.Utterances.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Utterance previous = revision.Utterances[index - 1];
                Utterance current = revision.Utterances[index];
                SourceSpan previousSpan = validatedSpans[index - 1];
                SourceSpan currentSpan = validatedSpans[index];
                RationalTime observedGap = current.Range.Start - previous.Range.End;
                var features = new TopicBoundaryFeatures(
                    previous.Id,
                    current.Id,
                    previousSpan.Id,
                    currentSpan.Id,
                    previous.SpeakerId,
                    current.SpeakerId,
                    previous.Range,
                    current.Range,
                    observedGap);
                OriginRef origin = OriginRef.FromSourceSpan(currentSpan.Id);
                var detection = new Detection(
                    EntityId<DetectionIdTag>.New(),
                    modelRunId,
                    DetectionKind.TopicBoundary,
                    origin,
                    DetectionDescription,
                    confidence: null);
                var suggestion = new Suggestion(
                    EntityId<SuggestionIdTag>.New(),
                    modelRunId,
                    SuggestionKind.TopicBoundary,
                    SuggestionStatus.Proposed,
                    origin,
                    features.SpeakerChanged
                        ? SpeakerChangeReason
                        : SameSpeakerReason,
                    confidence: null,
                    MissingContext,
                    ReviewWarning);
                candidates.Add(new TopicBoundaryCandidate(
                    detection,
                    suggestion,
                    currentSpan,
                    revision.Id,
                    revisionDigest,
                    current.Range.Start,
                    RuleVersion,
                    features));
            }

            RationalTime? transcriptStart = revision.Utterances.Count == 0
                ? null
                : revision.Utterances[0].Range.Start;
            RationalTime? transcriptEnd = revision.Utterances.Count == 0
                ? null
                : revision.Utterances.Max(static utterance => utterance.Range.End);
            return new TopicBoundaryScanResult(
                revision.Id,
                revisionDigest,
                modelRunId,
                RuleVersion,
                Math.Max(0, revision.Utterances.Count - 1),
                transcriptStart,
                transcriptEnd,
                candidates);
        }
        catch (OverflowException)
        {
            throw Failure(TopicBoundaryFailureReason.ArithmeticOverflow);
        }
    }

    public static TopicRangeRevision CreateTopicRange(
        TopicBoundaryScanResult scan,
        EntityId<TopicRangeIdTag> topicRangeId,
        EntityId<TopicRangeRevisionIdTag> revisionId,
        EntityId<SuggestionIdTag>? startBoundarySuggestionId,
        EntityId<SuggestionIdTag>? endBoundarySuggestionId,
        TimeRange range,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(topicRangeId);
        ArgumentNullException.ThrowIfNull(revisionId);
        ValidateRangeAndBoundaryReferences(
            scan,
            startBoundarySuggestionId,
            endBoundarySuggestionId,
            range);
        try
        {
            return new TopicRangeRevision(
                topicRangeId,
                revisionId,
                parentRevisionId: null,
                scan.SourceRevisionId,
                scan.SourceRevisionDigest,
                startBoundarySuggestionId,
                endBoundarySuggestionId,
                range,
                previousRange: null,
                actorSid,
                createdAt,
                reason);
        }
        catch (ArgumentException)
        {
            throw Failure(TopicBoundaryFailureReason.InvalidText);
        }
    }

    public static TopicRangeRevision ReviseTopicRange(
        TopicBoundaryScanResult scan,
        TopicRangeRevision current,
        EntityId<TopicRangeRevisionIdTag> newRevisionId,
        TimeRange revisedRange,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        if (current.SourceRevisionId != scan.SourceRevisionId
            || current.SourceRevisionDigest != scan.SourceRevisionDigest
            || current.Id == newRevisionId
            || createdAt < current.CreatedAt)
        {
            throw Failure(TopicBoundaryFailureReason.RevisionConflict);
        }

        if (current.Range == revisedRange)
        {
            throw Failure(TopicBoundaryFailureReason.NoChange);
        }

        ValidateRangeAndBoundaryReferences(
            scan,
            current.StartBoundarySuggestionId,
            current.EndBoundarySuggestionId,
            revisedRange);
        try
        {
            return new TopicRangeRevision(
                current.TopicRangeId,
                newRevisionId,
                current.Id,
                current.SourceRevisionId,
                current.SourceRevisionDigest,
                current.StartBoundarySuggestionId,
                current.EndBoundarySuggestionId,
                revisedRange,
                current.Range,
                actorSid,
                createdAt,
                reason);
        }
        catch (ArgumentException)
        {
            throw Failure(TopicBoundaryFailureReason.InvalidText);
        }
    }

    private static void ValidateRangeAndBoundaryReferences(
        TopicBoundaryScanResult scan,
        EntityId<SuggestionIdTag>? startBoundarySuggestionId,
        EntityId<SuggestionIdTag>? endBoundarySuggestionId,
        TimeRange range)
    {
        if (scan.TranscriptStart is not RationalTime transcriptStart
            || scan.TranscriptEnd is not RationalTime transcriptEnd
            || range.Duration <= default(RationalTime)
            || range.Start < transcriptStart
            || range.End > transcriptEnd)
        {
            throw Failure(TopicBoundaryFailureReason.RangeOutsideTranscript);
        }

        TopicBoundaryCandidate? startCandidate = startBoundarySuggestionId is null
            ? null
            : scan.Candidates.SingleOrDefault(
                candidate => candidate.Suggestion.Id == startBoundarySuggestionId);
        TopicBoundaryCandidate? endCandidate = endBoundarySuggestionId is null
            ? null
            : scan.Candidates.SingleOrDefault(
                candidate => candidate.Suggestion.Id == endBoundarySuggestionId);
        if (startBoundarySuggestionId is not null && startCandidate is null
            || endBoundarySuggestionId is not null && endCandidate is null
            || startCandidate is not null
                && endCandidate is not null
                && startCandidate.SuggestedBoundary >= endCandidate.SuggestedBoundary)
        {
            throw Failure(TopicBoundaryFailureReason.CandidateBindingMismatch);
        }
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(TopicBoundaryFailureReason.SourceSpanBindingMismatch);
        }

        var copied = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    TopicBoundaryFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    TopicBoundaryFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision revision)
    {
        try
        {
            string canonicalJson = TranscriptSnapshotJson.WriteRevision(revision);
            return ContentDigest.Compute(
                TopicBoundaryContract.StrictUtf8.GetBytes(canonicalJson));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(TopicBoundaryFailureReason.InvalidText);
        }
    }

    private static TopicBoundaryException Failure(
        TopicBoundaryFailureReason reason) =>
        new(reason);
}

internal static class TopicBoundaryContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Topic-boundary text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Topic-boundary text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}