using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public sealed class EditorialPhraseCandidate
{
    internal EditorialPhraseCandidate(
        Detection detection,
        Suggestion suggestion,
        SourceSpan sourceSpan,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        string dictionaryVersion,
        string matchedPhrase,
        int normalizedSentenceStart,
        int normalizedMatchLength,
        bool usesUtteranceTimingFallback,
        bool usesPartialTokenCoverage)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        EditorialPhraseContract.ValidateText(
            dictionaryVersion,
            nameof(dictionaryVersion));
        EditorialPhraseContract.ValidateText(matchedPhrase, nameof(matchedPhrase));
        if (normalizedSentenceStart < 0 || normalizedMatchLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizedSentenceStart),
                "An editorial phrase match requires a nonnegative start and positive length.");
        }

        if (sourceSpan.TranscriptRevisionId != sourceRevisionId
            || sourceSpan.TranscriptTokenIds.Count == 0)
        {
            throw new ArgumentException(
                "An editorial phrase source span must bind evidence tokens in its revision.",
                nameof(sourceSpan));
        }

        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        if (detection.Kind != DetectionKind.EditorialInstruction
            || suggestion.Kind != SuggestionKind.DeleteRange
            || suggestion.Status != SuggestionStatus.Proposed
            || detection.ModelRunId != suggestion.ModelRunId
            || detection.Origin != expectedOrigin
            || suggestion.Origin != expectedOrigin
            || detection.Confidence is not null
            || suggestion.Confidence is not null)
        {
            throw new ArgumentException(
                "Editorial phrase detection and suggestion evidence is inconsistent.",
                nameof(suggestion));
        }

        Detection = detection;
        Suggestion = suggestion;
        SourceSpan = sourceSpan;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        DictionaryVersion = dictionaryVersion;
        MatchedPhrase = matchedPhrase;
        NormalizedSentenceStart = normalizedSentenceStart;
        NormalizedMatchLength = normalizedMatchLength;
        UsesUtteranceTimingFallback = usesUtteranceTimingFallback;
        UsesPartialTokenCoverage = usesPartialTokenCoverage;
    }

    public Detection Detection { get; }

    public Suggestion Suggestion { get; }

    public SourceSpan SourceSpan { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public string DictionaryVersion { get; }

    public string MatchedPhrase { get; }

    public int NormalizedSentenceStart { get; }

    public int NormalizedMatchLength { get; }

    public bool UsesUtteranceTimingFallback { get; }

    public bool UsesPartialTokenCoverage { get; }
}

public sealed class EditorialPhraseScanResult
{
    internal EditorialPhraseScanResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<ModelRunIdTag> modelRunId,
        string dictionaryVersion,
        int inspectedTokenCount,
        IReadOnlyList<EditorialPhraseCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(modelRunId);
        EditorialPhraseContract.ValidateText(
            dictionaryVersion,
            nameof(dictionaryVersion));
        ArgumentNullException.ThrowIfNull(candidates);
        if (inspectedTokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inspectedTokenCount),
                inspectedTokenCount,
                "The inspected token count must not be negative.");
        }

        var detectionIds = new HashSet<EntityId<DetectionIdTag>>();
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        EditorialPhraseCandidate[] copiedCandidates =
            new EditorialPhraseCandidate[candidates.Count];
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            EditorialPhraseCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Editorial phrase candidates must not contain null elements.",
                    nameof(candidates));
            if (candidate.SourceRevisionId != sourceRevisionId
                || candidate.SourceRevisionDigest != sourceRevisionDigest
                || candidate.Detection.ModelRunId != modelRunId
                || candidate.Suggestion.ModelRunId != modelRunId
                || !string.Equals(
                    candidate.DictionaryVersion,
                    dictionaryVersion,
                    StringComparison.Ordinal)
                || !detectionIds.Add(candidate.Detection.Id)
                || !suggestionIds.Add(candidate.Suggestion.Id)
                || !sourceSpanIds.Add(candidate.SourceSpan.Id))
            {
                throw new ArgumentException(
                    "An editorial phrase scan contains inconsistent candidates.",
                    nameof(candidates));
            }

            copiedCandidates[index] = candidate;
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        ModelRunId = modelRunId;
        DictionaryVersion = dictionaryVersion;
        InspectedTokenCount = inspectedTokenCount;
        Candidates = Array.AsReadOnly(copiedCandidates);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public string DictionaryVersion { get; }

    public int InspectedTokenCount { get; }

    public IReadOnlyList<EditorialPhraseCandidate> Candidates { get; }
}

public enum EditorialPhraseDetectorFailureReason
{
    SourceSpanBindingMismatch,
    InvalidText,
    ArithmeticOverflow,
}

public sealed class EditorialPhraseDetectorException : Exception
{
    internal EditorialPhraseDetectorException(
        EditorialPhraseDetectorFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public EditorialPhraseDetectorFailureReason Reason { get; }

    private static string GetStableMessage(
        EditorialPhraseDetectorFailureReason reason) =>
        reason switch
        {
            EditorialPhraseDetectorFailureReason.SourceSpanBindingMismatch =>
                "The editorial phrase source-span evidence is inconsistent.",
            EditorialPhraseDetectorFailureReason.InvalidText =>
                "Editorial phrase text cannot be normalized safely.",
            EditorialPhraseDetectorFailureReason.ArithmeticOverflow =>
                "The editorial phrase position exceeds the supported range.",
            _ => "Editorial phrase detection failed.",
        };
}

public static class EditorialPhraseDetector
{
    public const string DictionaryVersion = "fr005-2026-08-31.v1";

    private const string RequiredPhrase = "ここはカット";
    private const string DetectionDescription =
        "A versioned explicit editorial instruction matched the transcript token sequence.";
    private const string SuggestionReason =
        "The matched instruction explicitly requests an editorial cut.";
    private const string ReviewWarning =
        "Review the surrounding speech before adopting this deletion candidate.";
    private const string TimingFallbackContext =
        "One or more evidence tokens have no accepted timing; the candidate uses the utterance range.";
    private const string PartialTokenContext =
        "The phrase covers only part of an evidence token; the candidate range includes the whole token.";

    private static readonly string[] ClosedDictionary = [RequiredPhrase];
    private static readonly IReadOnlyList<string> ReadOnlyDictionary =
        Array.AsReadOnly(ClosedDictionary);
    private static readonly PhrasePattern[] Patterns = ClosedDictionary
        .Select(static phrase => new PhrasePattern(
            phrase,
            NormalizeForMatching(phrase)))
        .OrderByDescending(static pattern => pattern.NormalizedText.Length)
        .ThenBy(static pattern => pattern.NormalizedText, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> Phrases => ReadOnlyDictionary;

    public static EditorialPhraseScanResult FindCandidates(
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
        ContentDigest sourceRevisionDigest = ComputeRevisionDigest(revision);
        var candidates = new List<EditorialPhraseCandidate>();
        int inspectedTokenCount = 0;

        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = revision.Utterances[utteranceIndex];
            SourceSpan utteranceSource = validatedSpans[utteranceIndex];
            foreach (Sentence sentence in utterance.Sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inspectedTokenCount = checked(
                    inspectedTokenCount + sentence.Tokens.Count);
                SentenceTextIndex textIndex = BuildTextIndex(sentence.Tokens);
                foreach (PhrasePattern pattern in Patterns)
                {
                    int searchStart = 0;
                    while (searchStart <=
                        textIndex.NormalizedText.Length - pattern.NormalizedText.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int matchStart = textIndex.NormalizedText.IndexOf(
                            pattern.NormalizedText,
                            searchStart,
                            StringComparison.Ordinal);
                        if (matchStart < 0)
                        {
                            break;
                        }

                        int matchEndExclusive = checked(
                            matchStart + pattern.NormalizedText.Length);
                        SelectedEvidence evidence = SelectEvidenceTokens(
                            textIndex,
                            sentence.Tokens,
                            matchStart,
                            matchEndExclusive);
                        candidates.Add(CreateCandidate(
                            revision,
                            sourceRevisionDigest,
                            utterance,
                            utteranceSource,
                            evidence.Tokens,
                            modelRunId,
                            pattern,
                            matchStart,
                            evidence.UsesPartialTokenCoverage));
                        searchStart = matchEndExclusive;
                    }
                }
            }
        }

        return new EditorialPhraseScanResult(
            revision.Id,
            sourceRevisionDigest,
            modelRunId,
            DictionaryVersion,
            inspectedTokenCount,
            candidates);
    }

    private static EditorialPhraseCandidate CreateCandidate(
        TranscriptRevision revision,
        ContentDigest sourceRevisionDigest,
        Utterance utterance,
        SourceSpan utteranceSource,
        IReadOnlyList<Token> evidenceTokens,
        EntityId<ModelRunIdTag> modelRunId,
        PhrasePattern pattern,
        int matchStart,
        bool usesPartialTokenCoverage)
    {
        bool usesTimingFallback = evidenceTokens.Any(static token => token.Range is null);
        TimeRange candidateRange;
        if (usesTimingFallback)
        {
            candidateRange = utterance.Range;
        }
        else
        {
            TimeRange first = evidenceTokens[0].Range!.Value;
            TimeRange last = evidenceTokens[^1].Range!.Value;
            candidateRange = new TimeRange(first.Start, last.End - first.Start);
        }

        var sourceSpan = new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            utteranceSource.AssetId,
            utteranceSource.StreamId,
            candidateRange,
            utteranceSource.SourceDigest,
            revision.Id,
            evidenceTokens.Select(static token => token.Id).ToArray());
        OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
        var detection = new Detection(
            EntityId<DetectionIdTag>.New(),
            modelRunId,
            DetectionKind.EditorialInstruction,
            origin,
            DetectionDescription,
            confidence: null);
        var suggestion = new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            modelRunId,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            origin,
            SuggestionReason,
            confidence: null,
            usesTimingFallback
                ? TimingFallbackContext
                : usesPartialTokenCoverage
                    ? PartialTokenContext
                    : null,
            ReviewWarning);
        return new EditorialPhraseCandidate(
            detection,
            suggestion,
            sourceSpan,
            revision.Id,
            sourceRevisionDigest,
            DictionaryVersion,
            pattern.SourceText,
            matchStart,
            pattern.NormalizedText.Length,
            usesTimingFallback,
            usesPartialTokenCoverage);
    }

    private static SentenceTextIndex BuildTextIndex(IReadOnlyList<Token> tokens)
    {
        var builder = new StringBuilder();
        var ranges = new TokenTextRange[tokens.Count];
        for (int index = 0; index < tokens.Count; index++)
        {
            string normalized = NormalizeForMatching(tokens[index].Text);
            if (normalized.Length == 0)
            {
                throw Failure(EditorialPhraseDetectorFailureReason.InvalidText);
            }

            int start = builder.Length;
            builder.Append(normalized);
            ranges[index] = new TokenTextRange(start, builder.Length);
        }

        return new SentenceTextIndex(builder.ToString(), ranges);
    }

    private static SelectedEvidence SelectEvidenceTokens(
        SentenceTextIndex textIndex,
        IReadOnlyList<Token> tokens,
        int matchStart,
        int matchEndExclusive)
    {
        var selected = new List<Token>();
        int firstSelectedIndex = -1;
        int lastSelectedIndex = -1;
        for (int index = 0; index < textIndex.TokenRanges.Count; index++)
        {
            TokenTextRange tokenRange = textIndex.TokenRanges[index];
            if (tokenRange.EndExclusive <= matchStart)
            {
                continue;
            }

            if (tokenRange.Start >= matchEndExclusive)
            {
                break;
            }

            selected.Add(tokens[index]);
            if (firstSelectedIndex < 0)
            {
                firstSelectedIndex = index;
            }

            lastSelectedIndex = index;
        }

        if (selected.Count == 0)
        {
            throw Failure(EditorialPhraseDetectorFailureReason.InvalidText);
        }

        TokenTextRange firstRange = textIndex.TokenRanges[firstSelectedIndex];
        TokenTextRange lastRange = textIndex.TokenRanges[lastSelectedIndex];
        bool usesPartialTokenCoverage =
            matchStart != firstRange.Start
            || matchEndExclusive != lastRange.EndExclusive;
        return new SelectedEvidence(
            selected.ToArray(),
            usesPartialTokenCoverage);
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(
                EditorialPhraseDetectorFailureReason.SourceSpanBindingMismatch);
        }

        var copied = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    EditorialPhraseDetectorFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    EditorialPhraseDetectorFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static ContentDigest ComputeRevisionDigest(TranscriptRevision revision)
    {
        try
        {
            string canonicalJson = TranscriptSnapshotJson.WriteRevision(revision);
            return ContentDigest.Compute(
                EditorialPhraseContract.StrictUtf8.GetBytes(canonicalJson));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(EditorialPhraseDetectorFailureReason.InvalidText);
        }
    }

    private static string NormalizeForMatching(string value)
    {
        try
        {
            _ = EditorialPhraseContract.StrictUtf8.GetByteCount(value);
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (Exception exception)
            when (exception is EncoderFallbackException or ArgumentException)
        {
            throw Failure(EditorialPhraseDetectorFailureReason.InvalidText);
        }
    }

    private static EditorialPhraseDetectorException Failure(
        EditorialPhraseDetectorFailureReason reason) =>
        new(reason);

    private sealed record PhrasePattern(string SourceText, string NormalizedText);

    private sealed record SentenceTextIndex(
        string NormalizedText,
        IReadOnlyList<TokenTextRange> TokenRanges);

    private sealed record SelectedEvidence(
        Token[] Tokens,
        bool UsesPartialTokenCoverage);

    private readonly record struct TokenTextRange(int Start, int EndExclusive);
}

internal static class EditorialPhraseContract
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
                "Editorial phrase text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Editorial phrase text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}