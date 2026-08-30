using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public enum ContextDeviationPosition
{
    LeadingBeforeFirstTopic,
    BetweenTopicRanges,
    TrailingAfterLastTopic,
}

public sealed class ContextDeviationFeatures
{
    internal ContextDeviationFeatures(
        ContextDeviationPosition position,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        TimeRange utteranceRange,
        EntityId<TopicRangeIdTag>? previousTopicRangeId,
        EntityId<TopicRangeIdTag>? nextTopicRangeId,
        RationalTime distanceToNearestTopicRange)
    {
        if (!Enum.IsDefined(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        ArgumentNullException.ThrowIfNull(speakerId);
        if (utteranceRange.Duration <= default(RationalTime)
            || distanceToNearestTopicRange < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(distanceToNearestTopicRange));
        }

        bool referencesAreValid = position switch
        {
            ContextDeviationPosition.LeadingBeforeFirstTopic =>
                previousTopicRangeId is null && nextTopicRangeId is not null,
            ContextDeviationPosition.BetweenTopicRanges =>
                previousTopicRangeId is not null
                    && nextTopicRangeId is not null
                    && previousTopicRangeId != nextTopicRangeId,
            ContextDeviationPosition.TrailingAfterLastTopic =>
                previousTopicRangeId is not null && nextTopicRangeId is null,
            _ => false,
        };
        if (!referencesAreValid)
        {
            throw new ArgumentException(
                "Context-deviation position and topic references are inconsistent.",
                nameof(position));
        }

        Position = position;
        UtteranceId = utteranceId;
        SourceSpanId = sourceSpanId;
        SpeakerId = speakerId;
        UtteranceRange = utteranceRange;
        PreviousTopicRangeId = previousTopicRangeId;
        NextTopicRangeId = nextTopicRangeId;
        DistanceToNearestTopicRange = distanceToNearestTopicRange;
    }

    public ContextDeviationPosition Position { get; }

    public EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public TimeRange UtteranceRange { get; }

    public EntityId<TopicRangeIdTag>? PreviousTopicRangeId { get; }

    public EntityId<TopicRangeIdTag>? NextTopicRangeId { get; }

    public RationalTime DistanceToNearestTopicRange { get; }

    public bool FullyOutsideExplicitTopicRanges => true;

    public bool SemanticDistanceComputed => false;

    public bool TimeThresholdApplied => false;

    public bool AutomaticDeletionAllowed => false;
}

public sealed class ContextDeviationCandidate
{
    internal ContextDeviationCandidate(
        Detection detection,
        Suggestion suggestion,
        SourceSpan sourceSpan,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        string ruleVersion,
        ContextDeviationFeatures features)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(features);
        ContextDeviationContract.ValidateText(ruleVersion, nameof(ruleVersion));
        DetectionKind expectedKind = features.Position ==
            ContextDeviationPosition.BetweenTopicRanges
                ? DetectionKind.ContextDeviation
                : DetectionKind.PreparationConversation;
        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        if (detection.Kind != expectedKind
            || suggestion.Kind != SuggestionKind.DeleteRange
            || suggestion.Status != SuggestionStatus.Proposed
            || detection.ModelRunId != suggestion.ModelRunId
            || detection.Origin != expectedOrigin
            || suggestion.Origin != expectedOrigin
            || detection.Confidence is not null
            || suggestion.Confidence is not null
            || sourceSpan.TranscriptRevisionId != sourceRevisionId
            || sourceSpan.Id != features.SourceSpanId
            || sourceSpan.Range != features.UtteranceRange
            || sourceSpan.TranscriptTokenIds.Count == 0)
        {
            throw new ArgumentException(
                "A context-deviation candidate contains inconsistent evidence.",
                nameof(suggestion));
        }

        Detection = detection;
        Suggestion = suggestion;
        SourceSpan = sourceSpan;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        RuleVersion = ruleVersion;
        Features = features;
    }

    public Detection Detection { get; }

    public Suggestion Suggestion { get; }

    public SourceSpan SourceSpan { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public string RuleVersion { get; }

    public ContextDeviationFeatures Features { get; }

    public bool ExplicitHumanDecisionRequired => true;
}

public sealed class ContextDeviationScanResult
{
    internal ContextDeviationScanResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<ModelRunIdTag> modelRunId,
        string ruleVersion,
        int inspectedUtteranceCount,
        int insideTopicUtteranceCount,
        int partiallyCoveredUtteranceCount,
        int unclassifiedWithoutTopicContextCount,
        bool topicContextAvailable,
        IReadOnlyList<ContextDeviationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(candidates);
        ContextDeviationContract.ValidateText(ruleVersion, nameof(ruleVersion));
        if (inspectedUtteranceCount < 0
            || insideTopicUtteranceCount < 0
            || partiallyCoveredUtteranceCount < 0
            || unclassifiedWithoutTopicContextCount < 0
            || insideTopicUtteranceCount
                + partiallyCoveredUtteranceCount
                + unclassifiedWithoutTopicContextCount
                + candidates.Count != inspectedUtteranceCount
            || topicContextAvailable
                && unclassifiedWithoutTopicContextCount != 0
            || !topicContextAvailable
                && unclassifiedWithoutTopicContextCount != inspectedUtteranceCount)
        {
            throw new ArgumentException(
                "A context-deviation scan contains inconsistent counts.",
                nameof(candidates));
        }

        ContextDeviationCandidate[] copiedCandidates =
            new ContextDeviationCandidate[candidates.Count];
        var detectionIds = new HashSet<EntityId<DetectionIdTag>>();
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        RationalTime? previousStart = null;
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            ContextDeviationCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Context-deviation candidates must not contain null elements.",
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
                || previousStart is RationalTime prior
                    && candidate.SourceSpan.Range.Start < prior)
            {
                throw new ArgumentException(
                    "A context-deviation scan contains inconsistent candidates.",
                    nameof(candidates));
            }

            previousStart = candidate.SourceSpan.Range.Start;
            copiedCandidates[index] = candidate;
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        ModelRunId = modelRunId;
        RuleVersion = ruleVersion;
        InspectedUtteranceCount = inspectedUtteranceCount;
        InsideTopicUtteranceCount = insideTopicUtteranceCount;
        PartiallyCoveredUtteranceCount = partiallyCoveredUtteranceCount;
        UnclassifiedWithoutTopicContextCount =
            unclassifiedWithoutTopicContextCount;
        TopicContextAvailable = topicContextAvailable;
        Candidates = Array.AsReadOnly(copiedCandidates);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public string RuleVersion { get; }

    public int InspectedUtteranceCount { get; }

    public int InsideTopicUtteranceCount { get; }

    public int PartiallyCoveredUtteranceCount { get; }

    public int UnclassifiedWithoutTopicContextCount { get; }

    public bool TopicContextAvailable { get; }

    public IReadOnlyList<ContextDeviationCandidate> Candidates { get; }
}

public enum ContextDeviationFailureReason
{
    SourceSpanBindingMismatch,
    TopicRangeBindingMismatch,
    TopicRangeOverlapOrDisorder,
    ArithmeticOverflow,
    InvalidText,
}

public sealed class ContextDeviationException : Exception
{
    internal ContextDeviationException(ContextDeviationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public ContextDeviationFailureReason Reason { get; }

    private static string GetStableMessage(
        ContextDeviationFailureReason reason) =>
        reason switch
        {
            ContextDeviationFailureReason.SourceSpanBindingMismatch =>
                "The context-deviation source-span evidence is inconsistent.",
            ContextDeviationFailureReason.TopicRangeBindingMismatch =>
                "A topic range does not match the transcript context.",
            ContextDeviationFailureReason.TopicRangeOverlapOrDisorder =>
                "Explicit topic ranges overlap or are not in canonical order.",
            ContextDeviationFailureReason.ArithmeticOverflow =>
                "Context-deviation time arithmetic exceeds the supported range.",
            ContextDeviationFailureReason.InvalidText =>
                "Context-deviation text could not be processed safely.",
            _ => "Context-deviation detection failed.",
        };
}

public static class ContextDeviationDetector
{
    public const string RuleVersion =
        "fr005-2026-08-31.explicit-topic-range-position.v1";

    private const string LeadingDescription =
        "An utterance occurs fully before the first explicit topic range.";
    private const string BetweenDescription =
        "An utterance occurs fully between explicit topic ranges.";
    private const string TrailingDescription =
        "An utterance occurs fully after the last explicit topic range.";
    private const string LeadingReason =
        "Speech before the first explicit topic range may be preparation conversation.";
    private const string BetweenReason =
        "Speech outside and between explicit topic ranges requires context-deviation review.";
    private const string TrailingReason =
        "Speech after the last explicit topic range may be post-topic conversation.";
    private const string MissingContext =
        "No semantic distance, phrase classifier, or time threshold was applied.";
    private const string ReviewWarning =
        "The explicit topic ranges do not prove that this utterance should be deleted.";

    public static ContextDeviationScanResult FindCandidates(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TopicRangeRevision> topicRanges,
        EntityId<ModelRunIdTag> modelRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topicRanges);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();
        SourceSpan[] validatedSpans = ValidateAndCopySourceSpans(
            revision,
            sourceSpans,
            cancellationToken);
        ContentDigest revisionDigest = ComputeRevisionDigest(revision);
        TopicRangeRevision[] ranges = ValidateAndCopyTopicRanges(
            revision,
            revisionDigest,
            topicRanges,
            cancellationToken);
        if (ranges.Length == 0)
        {
            return new ContextDeviationScanResult(
                revision.Id,
                revisionDigest,
                modelRunId,
                RuleVersion,
                revision.Utterances.Count,
                insideTopicUtteranceCount: 0,
                partiallyCoveredUtteranceCount: 0,
                unclassifiedWithoutTopicContextCount: revision.Utterances.Count,
                topicContextAvailable: false,
                candidates: []);
        }

        var candidates = new List<ContextDeviationCandidate>();
        int insideCount = 0;
        int partialCount = 0;
        try
        {
            for (int index = 0; index < revision.Utterances.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Utterance utterance = revision.Utterances[index];
                TopicRangeRevision[] overlaps = ranges
                    .Where(range => range.Range.Overlaps(utterance.Range)
                        || range.Range == utterance.Range)
                    .ToArray();
                if (overlaps.Any(range =>
                    utterance.Range.Start >= range.Range.Start
                    && utterance.Range.End <= range.Range.End))
                {
                    insideCount++;
                    continue;
                }

                if (overlaps.Length > 0)
                {
                    partialCount++;
                    continue;
                }

                CandidatePosition position = FindPosition(ranges, utterance.Range);
                candidates.Add(CreateCandidate(
                    revision,
                    revisionDigest,
                    validatedSpans[index],
                    utterance,
                    modelRunId,
                    position));
            }
        }
        catch (OverflowException)
        {
            throw Failure(ContextDeviationFailureReason.ArithmeticOverflow);
        }

        return new ContextDeviationScanResult(
            revision.Id,
            revisionDigest,
            modelRunId,
            RuleVersion,
            revision.Utterances.Count,
            insideCount,
            partialCount,
            unclassifiedWithoutTopicContextCount: 0,
            topicContextAvailable: true,
            candidates);
    }

    private static ContextDeviationCandidate CreateCandidate(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        SourceSpan sourceSpan,
        Utterance utterance,
        EntityId<ModelRunIdTag> modelRunId,
        CandidatePosition position)
    {
        var features = new ContextDeviationFeatures(
            position.Position,
            utterance.Id,
            sourceSpan.Id,
            utterance.SpeakerId,
            utterance.Range,
            position.Previous?.TopicRangeId,
            position.Next?.TopicRangeId,
            position.Distance);
        OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
        var detection = new Detection(
            EntityId<DetectionIdTag>.New(),
            modelRunId,
            position.Position == ContextDeviationPosition.BetweenTopicRanges
                ? DetectionKind.ContextDeviation
                : DetectionKind.PreparationConversation,
            origin,
            GetDescription(position.Position),
            confidence: null);
        var suggestion = new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            modelRunId,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            origin,
            GetReason(position.Position),
            confidence: null,
            MissingContext,
            ReviewWarning);
        return new ContextDeviationCandidate(
            detection,
            suggestion,
            sourceSpan,
            revision.Id,
            revisionDigest,
            RuleVersion,
            features);
    }

    private static CandidatePosition FindPosition(
        IReadOnlyList<TopicRangeRevision> ranges,
        TimeRange utteranceRange)
    {
        TopicRangeRevision first = ranges[0];
        TopicRangeRevision last = ranges[^1];
        if (utteranceRange.End <= first.Range.Start)
        {
            return new CandidatePosition(
                ContextDeviationPosition.LeadingBeforeFirstTopic,
                Previous: null,
                Next: first,
                first.Range.Start - utteranceRange.End);
        }

        if (utteranceRange.Start >= last.Range.End)
        {
            return new CandidatePosition(
                ContextDeviationPosition.TrailingAfterLastTopic,
                Previous: last,
                Next: null,
                utteranceRange.Start - last.Range.End);
        }

        for (int index = 1; index < ranges.Count; index++)
        {
            TopicRangeRevision previous = ranges[index - 1];
            TopicRangeRevision next = ranges[index];
            if (utteranceRange.Start >= previous.Range.End
                && utteranceRange.End <= next.Range.Start)
            {
                RationalTime distanceFromPrevious =
                    utteranceRange.Start - previous.Range.End;
                RationalTime distanceToNext =
                    next.Range.Start - utteranceRange.End;
                return new CandidatePosition(
                    ContextDeviationPosition.BetweenTopicRanges,
                    previous,
                    next,
                    distanceFromPrevious <= distanceToNext
                        ? distanceFromPrevious
                        : distanceToNext);
            }
        }

        throw Failure(ContextDeviationFailureReason.TopicRangeBindingMismatch);
    }

    private static string GetDescription(ContextDeviationPosition position) =>
        position switch
        {
            ContextDeviationPosition.LeadingBeforeFirstTopic => LeadingDescription,
            ContextDeviationPosition.BetweenTopicRanges => BetweenDescription,
            ContextDeviationPosition.TrailingAfterLastTopic => TrailingDescription,
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };

    private static string GetReason(ContextDeviationPosition position) =>
        position switch
        {
            ContextDeviationPosition.LeadingBeforeFirstTopic => LeadingReason,
            ContextDeviationPosition.BetweenTopicRanges => BetweenReason,
            ContextDeviationPosition.TrailingAfterLastTopic => TrailingReason,
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        CancellationToken cancellationToken)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(ContextDeviationFailureReason.SourceSpanBindingMismatch);
        }

        var copied = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    ContextDeviationFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    ContextDeviationFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static TopicRangeRevision[] ValidateAndCopyTopicRanges(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        IReadOnlyList<TopicRangeRevision> topicRanges,
        CancellationToken cancellationToken)
    {
        var copied = new TopicRangeRevision[topicRanges.Count];
        var topicIds = new HashSet<EntityId<TopicRangeIdTag>>();
        var revisionIds = new HashSet<EntityId<TopicRangeRevisionIdTag>>();
        TopicRangeRevision? previous = null;
        RationalTime? transcriptStart = revision.Utterances.Count == 0
            ? null
            : revision.Utterances[0].Range.Start;
        RationalTime? transcriptEnd = revision.Utterances.Count == 0
            ? null
            : revision.Utterances.Max(static utterance => utterance.Range.End);
        for (int index = 0; index < copied.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TopicRangeRevision range = topicRanges[index]
                ?? throw Failure(
                    ContextDeviationFailureReason.TopicRangeBindingMismatch);
            if (range.SourceRevisionId != revision.Id
                || range.SourceRevisionDigest != revisionDigest
                || transcriptStart is not RationalTime start
                || transcriptEnd is not RationalTime end
                || range.Range.Start < start
                || range.Range.End > end
                || !topicIds.Add(range.TopicRangeId)
                || !revisionIds.Add(range.Id))
            {
                throw Failure(
                    ContextDeviationFailureReason.TopicRangeBindingMismatch);
            }

            if (previous is not null
                && (range.Range.Start < previous.Range.End
                    || CompareRange(previous, range) >= 0))
            {
                throw Failure(
                    ContextDeviationFailureReason.TopicRangeOverlapOrDisorder);
            }

            copied[index] = range;
            previous = range;
        }

        return copied;
    }

    private static int CompareRange(
        TopicRangeRevision left,
        TopicRangeRevision right)
    {
        int start = left.Range.Start.CompareTo(right.Range.Start);
        if (start != 0)
        {
            return start;
        }

        int end = left.Range.End.CompareTo(right.Range.End);
        return end != 0
            ? end
            : string.CompareOrdinal(
                left.TopicRangeId.ToString(),
                right.TopicRangeId.ToString());
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision revision)
    {
        try
        {
            string canonical = TranscriptSnapshotJson.WriteRevision(revision);
            return ContentDigest.Compute(
                ContextDeviationContract.StrictUtf8.GetBytes(canonical));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(ContextDeviationFailureReason.InvalidText);
        }
    }

    private static ContextDeviationException Failure(
        ContextDeviationFailureReason reason) =>
        new(reason);

    private sealed record CandidatePosition(
        ContextDeviationPosition Position,
        TopicRangeRevision? Previous,
        TopicRangeRevision? Next,
        RationalTime Distance);
}

internal static class ContextDeviationContract
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
                "Context-deviation text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Context-deviation text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}