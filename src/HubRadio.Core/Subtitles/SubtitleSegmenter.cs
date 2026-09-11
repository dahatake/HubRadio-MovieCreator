using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.Core.Subtitles;

public sealed class SubtitleCueCandidateIdTag
{
}

public enum SubtitleBoundaryReason
{
    JapaneseSentenceEndingPunctuation,
    SpeakerChange,
    PositiveSourceGap,
    EndOfTranscript,
    ExactTokenBoundary,
}

public sealed class SubtitleTimingFallback
{
    internal SubtitleTimingFallback(
        EntityId<TranscriptTokenIdTag> tokenId,
        EntityId<TranscriptUtteranceIdTag> parentUtteranceId,
        TimeRange parentUtteranceRange)
    {
        ArgumentNullException.ThrowIfNull(tokenId);
        ArgumentNullException.ThrowIfNull(parentUtteranceId);
        if (parentUtteranceRange.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentUtteranceRange),
                parentUtteranceRange,
                "A subtitle timing fallback requires a positive parent utterance range.");
        }

        TokenId = tokenId;
        ParentUtteranceId = parentUtteranceId;
        ParentUtteranceRange = parentUtteranceRange;
    }

    public EntityId<TranscriptTokenIdTag> TokenId { get; }

    public EntityId<TranscriptUtteranceIdTag> ParentUtteranceId { get; }

    public TimeRange ParentUtteranceRange { get; }
}

public sealed class SubtitleTimingOverlap
{
    internal SubtitleTimingOverlap(
        EntityId<TranscriptTokenIdTag> previousEvidenceTokenId,
        EntityId<TranscriptTokenIdTag> currentEvidenceTokenId,
        TimeRange sourceRange,
        bool usesParentUtteranceFallback)
    {
        ArgumentNullException.ThrowIfNull(previousEvidenceTokenId);
        ArgumentNullException.ThrowIfNull(currentEvidenceTokenId);
        if (previousEvidenceTokenId == currentEvidenceTokenId)
        {
            throw new ArgumentException(
                "A subtitle timing overlap requires two distinct evidence tokens.",
                nameof(currentEvidenceTokenId));
        }

        if (sourceRange.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceRange),
                sourceRange,
                "A subtitle timing overlap must have a positive duration.");
        }

        PreviousEvidenceTokenId = previousEvidenceTokenId;
        CurrentEvidenceTokenId = currentEvidenceTokenId;
        SourceRange = sourceRange;
        UsesParentUtteranceFallback = usesParentUtteranceFallback;
    }

    public EntityId<TranscriptTokenIdTag> PreviousEvidenceTokenId { get; }

    public EntityId<TranscriptTokenIdTag> CurrentEvidenceTokenId { get; }

    public TimeRange SourceRange { get; }

    public bool UsesParentUtteranceFallback { get; }
}

public sealed class SubtitleCueCandidate
{
    internal SubtitleCueCandidate(
        EntityId<SubtitleCueCandidateIdTag> id,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        string text,
        SourceSpan sourceSpan,
        OriginRef origin,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> evidenceTokenIds,
        IReadOnlyList<SubtitleTimingFallback> timingFallbacks,
        IReadOnlyList<SubtitleBoundaryReason> boundaryReasons,
        RationalTime? observedPositiveGapAfter,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(evidenceTokenIds);
        ArgumentNullException.ThrowIfNull(timingFallbacks);
        ArgumentNullException.ThrowIfNull(boundaryReasons);
        ArgumentNullException.ThrowIfNull(reason);

        EntityId<TranscriptTokenIdTag>[] copiedEvidence = evidenceTokenIds.ToArray();
        SubtitleTimingFallback[] copiedFallbacks = timingFallbacks.ToArray();
        SubtitleBoundaryReason[] copiedReasons = boundaryReasons.ToArray();
        if (copiedEvidence.Length == 0
            || copiedEvidence.Any(static tokenId => tokenId is null)
            || copiedEvidence.Distinct().Count() != copiedEvidence.Length
            || copiedFallbacks.Any(static fallback => fallback is null)
            || copiedFallbacks.Select(static fallback => fallback.TokenId).Distinct().Count()
                != copiedFallbacks.Length
            || copiedFallbacks.Any(fallback => !copiedEvidence.Contains(fallback.TokenId))
            || copiedReasons.Length == 0
            || copiedReasons.Any(static boundaryReason => !Enum.IsDefined(boundaryReason))
            || copiedReasons.Distinct().Count() != copiedReasons.Length
            || !copiedReasons.SequenceEqual(copiedReasons.Order()))
        {
            throw new ArgumentException(
                "Subtitle cue candidate evidence must be nonempty, unique, and canonical.",
                nameof(evidenceTokenIds));
        }

        bool hasPositiveGap = copiedReasons.Contains(
            SubtitleBoundaryReason.PositiveSourceGap);
        if (hasPositiveGap != (observedPositiveGapAfter is not null)
            || observedPositiveGapAfter is RationalTime gap
                && gap <= default(RationalTime)
            || sourceSpan.TranscriptRevisionId != sourceRevisionId
            || !sourceSpan.TranscriptTokenIds.SequenceEqual(copiedEvidence)
            || origin != OriginRef.FromSourceSpan(sourceSpan.Id)
            || sourceSpan.Range.Duration <= default(RationalTime))
        {
            throw new ArgumentException(
                "A subtitle cue candidate contains inconsistent timing or provenance.",
                nameof(sourceSpan));
        }

        SubtitleSegmenterContract.ValidateText(text);
        SubtitleSegmenterContract.ValidateText(reason);

        Id = id;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        SpeakerId = speakerId;
        Text = text;
        SourceSpan = sourceSpan;
        Origin = origin;
        EvidenceTokenIds = Array.AsReadOnly(copiedEvidence);
        TimingFallbacks = Array.AsReadOnly(copiedFallbacks);
        BoundaryReasons = Array.AsReadOnly(copiedReasons);
        ObservedPositiveGapAfter = observedPositiveGapAfter;
        Reason = reason;
    }

    public EntityId<SubtitleCueCandidateIdTag> Id { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public string Text { get; }

    public TimeRange SourceRange => SourceSpan.Range;

    public SourceSpan SourceSpan { get; }

    public OriginRef Origin { get; }

    public IReadOnlyList<EntityId<TranscriptTokenIdTag>> EvidenceTokenIds { get; }

    public IReadOnlyList<SubtitleTimingFallback> TimingFallbacks { get; }

    public bool ManualTimingAdjustmentRequired => TimingFallbacks.Count > 0;

    public IReadOnlyList<SubtitleBoundaryReason> BoundaryReasons { get; }

    public RationalTime? ObservedPositiveGapAfter { get; }

    public string Reason { get; }
}

public sealed class SubtitleSegmentationResult
{
    internal SubtitleSegmentationResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        string ruleVersion,
        int inspectedTokenCount,
        IReadOnlyList<SubtitleCueCandidate> candidates,
        IReadOnlyList<SubtitleTimingOverlap> timingOverlaps)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(timingOverlaps);
        SubtitleSegmenterContract.ValidateText(ruleVersion);
        if (inspectedTokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inspectedTokenCount));
        }

        SubtitleCueCandidate[] copiedCandidates = candidates.ToArray();
        SubtitleTimingOverlap[] copiedOverlaps = timingOverlaps.ToArray();
        var candidateIds = new HashSet<EntityId<SubtitleCueCandidateIdTag>>();
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        var evidenceTokenIds = new HashSet<EntityId<TranscriptTokenIdTag>>();
        foreach (SubtitleCueCandidate candidate in copiedCandidates)
        {
            if (candidate is null
                || candidate.SourceRevisionId != sourceRevisionId
                || candidate.SourceRevisionDigest != sourceRevisionDigest
                || !candidateIds.Add(candidate.Id)
                || !sourceSpanIds.Add(candidate.SourceSpan.Id)
                || candidate.EvidenceTokenIds.Any(tokenId => !evidenceTokenIds.Add(tokenId)))
            {
                throw new ArgumentException(
                    "A subtitle segmentation result contains inconsistent candidates.",
                    nameof(candidates));
            }
        }

        var overlapPairs = new HashSet<OverlapPair>();
        foreach (SubtitleTimingOverlap overlap in copiedOverlaps)
        {
            if (overlap is null
                || !evidenceTokenIds.Contains(overlap.PreviousEvidenceTokenId)
                || !evidenceTokenIds.Contains(overlap.CurrentEvidenceTokenId)
                || !overlapPairs.Add(new OverlapPair(
                    overlap.PreviousEvidenceTokenId,
                    overlap.CurrentEvidenceTokenId)))
            {
                throw new ArgumentException(
                    "A subtitle segmentation result contains inconsistent overlap evidence.",
                    nameof(timingOverlaps));
            }
        }

        if (evidenceTokenIds.Count != inspectedTokenCount)
        {
            throw new ArgumentException(
                "Every inspected transcript token must belong to exactly one subtitle candidate.",
                nameof(candidates));
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        RuleVersion = ruleVersion;
        InspectedTokenCount = inspectedTokenCount;
        Candidates = Array.AsReadOnly(copiedCandidates);
        TimingOverlaps = Array.AsReadOnly(copiedOverlaps);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public string RuleVersion { get; }

    public int InspectedTokenCount { get; }

    public IReadOnlyList<SubtitleCueCandidate> Candidates { get; }

    public IReadOnlyList<SubtitleTimingOverlap> TimingOverlaps { get; }

    public bool HasTimingOverlaps => TimingOverlaps.Count > 0;

    private sealed record OverlapPair(
        EntityId<TranscriptTokenIdTag> Previous,
        EntityId<TranscriptTokenIdTag> Current);
}

public enum SubtitleSegmentationFailureReason
{
    SourceRevisionNotHumanChild,
    RevisionDigestMismatch,
    SourceSpanBindingMismatch,
    DuplicateIdentifier,
    InvalidText,
    ArithmeticOverflow,
    CandidateBindingMismatch,
    SourceRevisionNotMachine,
}

public sealed class SubtitleSegmentationException : Exception
{
    internal SubtitleSegmentationException(SubtitleSegmentationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SubtitleSegmentationFailureReason Reason { get; }

    private static string GetStableMessage(SubtitleSegmentationFailureReason reason) =>
        reason switch
        {
            SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild =>
                "Subtitle segmentation requires a human child transcript revision.",
            SubtitleSegmentationFailureReason.SourceRevisionNotMachine =>
                "Machine subtitle segmentation requires a machine transcript revision.",
            SubtitleSegmentationFailureReason.RevisionDigestMismatch =>
                "The subtitle source revision digest is inconsistent.",
            SubtitleSegmentationFailureReason.SourceSpanBindingMismatch =>
                "The subtitle source-span evidence is inconsistent.",
            SubtitleSegmentationFailureReason.DuplicateIdentifier =>
                "The subtitle source evidence contains duplicate identifiers.",
            SubtitleSegmentationFailureReason.InvalidText =>
                "Subtitle source text could not be processed safely.",
            SubtitleSegmentationFailureReason.ArithmeticOverflow =>
                "Subtitle source-time arithmetic exceeds the supported range.",
            SubtitleSegmentationFailureReason.CandidateBindingMismatch =>
                "The generated subtitle candidate evidence is inconsistent.",
            _ => "Subtitle segmentation failed.",
        };
}

public static class SubtitleSegmenter
{
    public const string RuleVersion = "fr012-2026-08-31.human-transcript-boundaries.v1";
    public const string MachineRuleVersion = "fr012-2026-09-08.machine-transcript-boundaries.v1";

    public static SubtitleSegmentationResult Segment(
        TranscriptRevision revision,
        ContentDigest sourceRevisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        CancellationToken cancellationToken = default) =>
        SegmentCore(revision, sourceRevisionDigest, sourceSpans,
            TranscriptChangeSource.Human, cancellationToken);

    /// <summary>Segments real ASR evidence, including a root revision, without creating a human edit.</summary>
    public static SubtitleSegmentationResult SegmentMachine(
        TranscriptRevision revision,
        ContentDigest sourceRevisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        CancellationToken cancellationToken = default) =>
        SegmentCore(revision, sourceRevisionDigest, sourceSpans,
            TranscriptChangeSource.Machine, cancellationToken);

    private static SubtitleSegmentationResult SegmentCore(
        TranscriptRevision revision,
        ContentDigest sourceRevisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        TranscriptChangeSource expectedSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        cancellationToken.ThrowIfCancellationRequested();

        if (revision.ChangeSource != expectedSource
            || expectedSource == TranscriptChangeSource.Human && revision.ParentRevisionId is null)
        {
            throw Failure(expectedSource == TranscriptChangeSource.Human
                ? SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild
                : SubtitleSegmentationFailureReason.SourceRevisionNotMachine);
        }

        ContentDigest actualRevisionDigest = ComputeRevisionDigest(
            revision,
            cancellationToken);
        if (actualRevisionDigest != sourceRevisionDigest)
        {
            throw Failure(
                SubtitleSegmentationFailureReason.RevisionDigestMismatch);
        }

        SourceSpan[] validatedSpans = ValidateAndCopySourceSpans(
            revision,
            sourceSpans,
            cancellationToken);
        TokenContext[] tokens = FlattenTokens(
            revision,
            validatedSpans,
            cancellationToken);

        try
        {
            SubtitleTimingOverlap[] overlaps = FindTimingOverlaps(
                tokens,
                cancellationToken);
            var candidates = new List<SubtitleCueCandidate>();
            var currentTokens = new List<TokenContext>();
            for (int index = 0; index < tokens.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenContext current = tokens[index];
                currentTokens.Add(current);
                TokenContext? next = index + 1 < tokens.Length
                    ? tokens[index + 1]
                    : null;
                var boundaryReasons = new List<SubtitleBoundaryReason>(4);
                RationalTime? observedPositiveGap = null;

                if (HasJapaneseSentenceEndingPunctuation(string.Concat(
                    current.Token.Text,
                    current.TextAfter)))
                {
                    boundaryReasons.Add(
                        SubtitleBoundaryReason.JapaneseSentenceEndingPunctuation);
                }

                if (next is null)
                {
                    boundaryReasons.Add(SubtitleBoundaryReason.EndOfTranscript);
                }
                else
                {
                    if (current.Utterance.SpeakerId != next.Utterance.SpeakerId)
                    {
                        boundaryReasons.Add(SubtitleBoundaryReason.SpeakerChange);
                    }

                    if (next.EffectiveRange.Start > current.EffectiveRange.End)
                    {
                        observedPositiveGap =
                            next.EffectiveRange.Start - current.EffectiveRange.End;
                        boundaryReasons.Add(
                            SubtitleBoundaryReason.PositiveSourceGap);
                    }
                }

                if (boundaryReasons.Count == 0)
                {
                    continue;
                }

                candidates.Add(CreateCandidate(
                    revision,
                    sourceRevisionDigest,
                    currentTokens,
                    boundaryReasons,
                    observedPositiveGap,
                    cancellationToken));
                currentTokens.Clear();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SubtitleSegmentationResult(
                revision.Id,
                sourceRevisionDigest,
                expectedSource == TranscriptChangeSource.Machine ? MachineRuleVersion : RuleVersion,
                tokens.Length,
                candidates,
                overlaps);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SubtitleSegmentationException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure(
                SubtitleSegmentationFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentException)
        {
            throw Failure(
                SubtitleSegmentationFailureReason.CandidateBindingMismatch);
        }
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        CancellationToken cancellationToken)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(
                SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
        }

        var utteranceIds = new HashSet<EntityId<TranscriptUtteranceIdTag>>();
        var sentenceIds = new HashSet<EntityId<TranscriptSentenceIdTag>>();
        var tokenIds = new HashSet<EntityId<TranscriptTokenIdTag>>();
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        SourceSpan[] copied = new SourceSpan[sourceSpans.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = revision.Utterances[index];
            if (!utteranceIds.Add(utterance.Id))
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.DuplicateIdentifier);
            }

            foreach (Sentence sentence in utterance.Sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sentenceIds.Add(sentence.Id))
                {
                    throw Failure(
                        SubtitleSegmentationFailureReason.DuplicateIdentifier);
                }

                foreach (Token token in sentence.Tokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!tokenIds.Add(token.Id))
                    {
                        throw Failure(
                            SubtitleSegmentationFailureReason.DuplicateIdentifier);
                    }
                }
            }

            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
            if (!sourceSpanIds.Add(span.Id))
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.DuplicateIdentifier);
            }

            ValidatePrivateText(span.StreamId);
            if (span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static TokenContext[] FlattenTokens(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        CancellationToken cancellationToken)
    {
        var flattened = new List<TokenContext>();
        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = revision.Utterances[utteranceIndex];
            SourceSpan sourceSpan = sourceSpans[utteranceIndex];
            TokenTextFragment[] textFragments = ResolveTokenTextFragments(
                utterance,
                cancellationToken);
            int tokenIndex = 0;
            foreach (Sentence sentence in utterance.Sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (Token token in sentence.Tokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TokenTextFragment textFragment = textFragments[tokenIndex++];
                    flattened.Add(new TokenContext(
                        token,
                        utterance,
                        sourceSpan,
                        token.Range ?? utterance.Range,
                        token.Range is null,
                        textFragment.Prefix,
                        textFragment.After,
                        textFragment.IsLastTokenInUtterance));
                }
            }

            if (tokenIndex != textFragments.Length)
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.CandidateBindingMismatch);
            }
        }

        return flattened.ToArray();
    }

    private static TokenTextFragment[] ResolveTokenTextFragments(
        Utterance utterance,
        CancellationToken cancellationToken)
    {
        string[] tokenTexts = utterance.Tokens
            .Select(static token => token.Text)
            .ToArray();
        int[] tokenStarts = FindUniqueOrderedTextStarts(
            utterance.Text,
            tokenTexts,
            cancellationToken);
        var fragments = new TokenTextFragment[tokenTexts.Length];
        for (int index = 0; index < fragments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tokenEnd = checked(tokenStarts[index] + tokenTexts[index].Length);
            int nextTokenStart = index + 1 < tokenStarts.Length
                ? tokenStarts[index + 1]
                : utterance.Text.Length;
            string prefix = index == 0
                ? utterance.Text[..tokenStarts[index]]
                : string.Empty;
            string after = utterance.Text[tokenEnd..nextTokenStart];
            fragments[index] = new TokenTextFragment(
                prefix,
                after,
                index == fragments.Length - 1);
        }

        return fragments;
    }

    internal static int[] FindUniqueOrderedTextStarts(
        string source,
        IReadOnlyList<string> orderedParts,
        CancellationToken cancellationToken)
    {
        var earliestStarts = new int[orderedParts.Count];
        int searchStart = 0;
        for (int index = 0; index < orderedParts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string part = orderedParts[index];
            int start = source.IndexOf(
                part,
                searchStart,
                StringComparison.Ordinal);
            if (start < 0)
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.CandidateBindingMismatch);
            }

            earliestStarts[index] = start;
            searchStart = checked(start + part.Length);
        }

        var latestStarts = new int[orderedParts.Count];
        int exclusiveEnd = source.Length;
        for (int index = orderedParts.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string part = orderedParts[index];
            int start = FindLastTextStartEndingBefore(
                source,
                part,
                exclusiveEnd,
                cancellationToken);
            if (start < 0)
            {
                throw Failure(
                    SubtitleSegmentationFailureReason.CandidateBindingMismatch);
            }

            latestStarts[index] = start;
            exclusiveEnd = start;
        }

        if (!earliestStarts.SequenceEqual(latestStarts))
        {
            throw Failure(
                SubtitleSegmentationFailureReason.CandidateBindingMismatch);
        }

        return earliestStarts;
    }

    private static int FindLastTextStartEndingBefore(
        string source,
        string value,
        int exclusiveEnd,
        CancellationToken cancellationToken)
    {
        for (int candidate = exclusiveEnd - value.Length;
            candidate >= 0;
            candidate--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.AsSpan(candidate, value.Length).SequenceEqual(value.AsSpan()))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static SubtitleTimingOverlap[] FindTimingOverlaps(
        IReadOnlyList<TokenContext> tokens,
        CancellationToken cancellationToken)
    {
        IndexedTokenContext[] ordered = tokens
            .Select(static (context, index) => new IndexedTokenContext(index, context))
            .Where(static item => item.Context.Token.Range is not null)
            .OrderBy(static item => item.Context.EffectiveRange.Start)
            .ThenBy(static item => item.Context.EffectiveRange.End)
            .ThenBy(static item => item.Index)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var active = new List<IndexedTokenContext>();
        var overlaps = new List<IndexedTimingOverlap>();
        foreach (IndexedTokenContext current in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int activeIndex = active.Count - 1; activeIndex >= 0; activeIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (active[activeIndex].Context.EffectiveRange.End
                    <= current.Context.EffectiveRange.Start)
                {
                    active.RemoveAt(activeIndex);
                }
            }

            foreach (IndexedTokenContext other in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedTokenContext previous = other.Index < current.Index
                    ? other
                    : current;
                IndexedTokenContext next = other.Index < current.Index
                    ? current
                    : other;
                RationalTime overlapStart = other.Context.EffectiveRange.Start
                    >= current.Context.EffectiveRange.Start
                    ? other.Context.EffectiveRange.Start
                    : current.Context.EffectiveRange.Start;
                RationalTime overlapEnd = other.Context.EffectiveRange.End
                    <= current.Context.EffectiveRange.End
                    ? other.Context.EffectiveRange.End
                    : current.Context.EffectiveRange.End;
                overlaps.Add(new IndexedTimingOverlap(
                    previous.Index,
                    next.Index,
                    new SubtitleTimingOverlap(
                        previous.Context.Token.Id,
                        next.Context.Token.Id,
                        new TimeRange(overlapStart, overlapEnd - overlapStart),
                        previous.Context.UsesParentUtteranceFallback
                            || next.Context.UsesParentUtteranceFallback)));
            }

            active.Add(current);
        }

        return overlaps
            .OrderBy(static overlap => overlap.PreviousIndex)
            .ThenBy(static overlap => overlap.CurrentIndex)
            .Select(static overlap => overlap.Overlap)
            .ToArray();
    }

    private static SubtitleCueCandidate CreateCandidate(
        TranscriptRevision revision,
        ContentDigest sourceRevisionDigest,
        IReadOnlyList<TokenContext> tokenContexts,
        IReadOnlyList<SubtitleBoundaryReason> boundaryReasons,
        RationalTime? observedPositiveGap,
        CancellationToken cancellationToken)
    {
        if (tokenContexts.Count == 0)
        {
            throw Failure(
                SubtitleSegmentationFailureReason.CandidateBindingMismatch);
        }

        TokenContext first = tokenContexts[0];
        if (tokenContexts.Any(context =>
            context.Utterance.SpeakerId != first.Utterance.SpeakerId))
        {
            throw Failure(
                SubtitleSegmentationFailureReason.CandidateBindingMismatch);
        }

        if (tokenContexts.Any(context =>
            context.SourceSpan.AssetId != first.SourceSpan.AssetId
            || !string.Equals(
                context.SourceSpan.StreamId,
                first.SourceSpan.StreamId,
                StringComparison.Ordinal)
            || context.SourceSpan.SourceDigest != first.SourceSpan.SourceDigest))
        {
            throw Failure(
                SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
        }

        RationalTime start = tokenContexts.Min(
            static context => context.EffectiveRange.Start);
        RationalTime end = tokenContexts.Max(
            static context => context.EffectiveRange.End);
        TimeRange sourceRange = new(start, end - start);
        EntityId<TranscriptTokenIdTag>[] evidenceTokenIds = tokenContexts
            .Select(static context => context.Token.Id)
            .ToArray();
        SubtitleTimingFallback[] timingFallbacks = tokenContexts
            .Where(static context => context.UsesParentUtteranceFallback)
            .Select(static context => new SubtitleTimingFallback(
                context.Token.Id,
                context.Utterance.Id,
                context.Utterance.Range))
            .ToArray();
        var text = new StringBuilder();
        for (int index = 0; index < tokenContexts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenContext context = tokenContexts[index];
            _ = text.Append(context.TextPrefix);
            _ = text.Append(context.Token.Text);
            bool nextTokenIsInSameUtterance = index + 1 < tokenContexts.Count
                && tokenContexts[index + 1].Utterance.Id == context.Utterance.Id;
            bool tokenEndsCandidate = index == tokenContexts.Count - 1;
            if (nextTokenIsInSameUtterance
                || context.IsLastTokenInUtterance
                || tokenEndsCandidate)
            {
                _ = text.Append(context.TextAfter);
            }
        }

        string candidateText = text.ToString().Trim();
        var sourceSpan = new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            first.SourceSpan.AssetId,
            first.SourceSpan.StreamId,
            sourceRange,
            first.SourceSpan.SourceDigest,
            revision.Id,
            evidenceTokenIds);
        OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
        return new SubtitleCueCandidate(
            EntityId<SubtitleCueCandidateIdTag>.New(),
            revision.Id,
            sourceRevisionDigest,
            first.Utterance.SpeakerId,
            candidateText,
            sourceSpan,
            origin,
            evidenceTokenIds,
            timingFallbacks,
            boundaryReasons,
            observedPositiveGap,
            BuildReason(boundaryReasons));
    }

    private static string BuildReason(
        IReadOnlyList<SubtitleBoundaryReason> boundaryReasons)
    {
        IEnumerable<string> descriptions = boundaryReasons.Select(
            static boundaryReason => boundaryReason switch
            {
                SubtitleBoundaryReason.JapaneseSentenceEndingPunctuation =>
                    "Japanese sentence-ending punctuation",
                SubtitleBoundaryReason.SpeakerChange => "speaker change",
                SubtitleBoundaryReason.PositiveSourceGap =>
                    "positive source-timing gap",
                SubtitleBoundaryReason.EndOfTranscript => "end of transcript",
                SubtitleBoundaryReason.ExactTokenBoundary => "existing transcript token boundary",
                _ => throw new ArgumentOutOfRangeException(nameof(boundaryReasons)),
            });
        return string.Concat(
            "Subtitle cue candidate boundary: ",
            string.Join(", ", descriptions),
            ".");
    }

    private static bool HasJapaneseSentenceEndingPunctuation(string value)
    {
        Rune[] runes = value.EnumerateRunes().ToArray();
        for (int index = runes.Length - 1; index >= 0; index--)
        {
            Rune rune = runes[index];
            if (rune.Value is 0x3002 or 0xFF01 or 0xFF1F or 0xFF61
                or 0x21 or 0x3F or 0x203C or 0x2047 or 0x2048 or 0x2049)
            {
                return true;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.ClosePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                || Rune.IsWhiteSpace(rune)
                || rune.Value is >= 0xFE00 and <= 0xFE0F
                || rune.Value is >= 0xE0100 and <= 0xE01EF)
            {
                continue;
            }

            return false;
        }

        return false;
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var utterances = new RevisionUtteranceSnapshot[revision.Utterances.Count];
            for (int utteranceIndex = 0;
                utteranceIndex < utterances.Length;
                utteranceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Utterance utterance = revision.Utterances[utteranceIndex];
                ValidatePrivateText(utterance.SpeakerLabel);
                ValidatePrivateText(utterance.Text);
                var sentences = new RevisionSentenceSnapshot[utterance.Sentences.Count];
                for (int sentenceIndex = 0;
                    sentenceIndex < sentences.Length;
                    sentenceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Sentence sentence = utterance.Sentences[sentenceIndex];
                    ValidatePrivateText(sentence.Text);
                    var tokens = new RevisionTokenSnapshot[sentence.Tokens.Count];
                    for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Token token = sentence.Tokens[tokenIndex];
                        ValidatePrivateText(token.Text);
                        tokens[tokenIndex] = new RevisionTokenSnapshot(
                            token.Id.ToString(),
                            token.Text,
                            token.Range is TimeRange tokenRange
                                ? Snapshot(tokenRange)
                                : null,
                            token.RecognitionConfidence,
                            token.ManualTimingAdjustmentRequired);
                    }

                    sentences[sentenceIndex] = new RevisionSentenceSnapshot(
                        sentence.Id.ToString(),
                        sentence.Text,
                        tokens);
                }

                utterances[utteranceIndex] = new RevisionUtteranceSnapshot(
                    utterance.Id.ToString(),
                    utterance.SourceSpanId.ToString(),
                    utterance.SpeakerId.ToString(),
                    utterance.SpeakerLabel,
                    utterance.Text,
                    Snapshot(utterance.Range),
                    utterance.RecognitionConfidence,
                    sentences);
            }

            var snapshot = new RevisionSnapshot(
                revision.Id.ToString(),
                revision.ParentRevisionId?.ToString(),
                revision.ChangeSource switch
                {
                    TranscriptChangeSource.Machine => "machine",
                    TranscriptChangeSource.Human => "human",
                    _ => throw new ArgumentException(
                        "Unknown transcript change source.",
                        nameof(revision)),
                },
                revision.CreatedAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                utterances);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                SubtitleSegmenterContract.SnapshotJsonOptions);
            cancellationToken.ThrowIfCancellationRequested();
            return CanonicalJson.ComputeDigest(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SubtitleSegmentationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or EncoderFallbackException
                or JsonException
                or InvalidOperationException)
        {
            throw Failure(SubtitleSegmentationFailureReason.InvalidText);
        }
    }

    private static void ValidatePrivateText(string value)
    {
        try
        {
            SubtitleSegmenterContract.ValidateText(value);
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleSegmentationFailureReason.InvalidText);
        }
    }

    private static RevisionTimeRangeSnapshot Snapshot(TimeRange range) =>
        new(
            new RevisionRationalTimeSnapshot(range.Start.Value, range.Start.Rate),
            new RevisionRationalTimeSnapshot(
                range.Duration.Value,
                range.Duration.Rate));

    private static SubtitleSegmentationException Failure(
        SubtitleSegmentationFailureReason reason) =>
        new(reason);

    private sealed record TokenContext(
        Token Token,
        Utterance Utterance,
        SourceSpan SourceSpan,
        TimeRange EffectiveRange,
        bool UsesParentUtteranceFallback,
        string TextPrefix,
        string TextAfter,
        bool IsLastTokenInUtterance);

    private sealed record TokenTextFragment(
        string Prefix,
        string After,
        bool IsLastTokenInUtterance);

    private sealed record IndexedTokenContext(int Index, TokenContext Context);

    private sealed record IndexedTimingOverlap(
        int PreviousIndex,
        int CurrentIndex,
        SubtitleTimingOverlap Overlap);

    private sealed record RevisionSnapshot(
        string Id,
        string? ParentRevisionId,
        string ChangeSource,
        string CreatedAt,
        RevisionUtteranceSnapshot[] Utterances);

    private sealed record RevisionUtteranceSnapshot(
        string Id,
        string SourceSpanId,
        string SpeakerId,
        string SpeakerLabel,
        string Text,
        RevisionTimeRangeSnapshot Range,
        double? RecognitionConfidence,
        RevisionSentenceSnapshot[] Sentences);

    private sealed record RevisionSentenceSnapshot(
        string Id,
        string Text,
        RevisionTokenSnapshot[] Tokens);

    private sealed record RevisionTokenSnapshot(
        string Id,
        string Text,
        RevisionTimeRangeSnapshot? Range,
        double? RecognitionConfidence,
        bool ManualTimingAdjustmentRequired);

    private sealed record RevisionTimeRangeSnapshot(
        RevisionRationalTimeSnapshot Start,
        RevisionRationalTimeSnapshot Duration);

    private sealed record RevisionRationalTimeSnapshot(long Value, long Rate);
}

internal static class SubtitleSegmenterContract
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static JsonSerializerOptions SnapshotJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static void ValidateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Subtitle text must be nonblank and trimmed.");
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Subtitle text must contain valid Unicode.",
                exception);
        }
    }
}