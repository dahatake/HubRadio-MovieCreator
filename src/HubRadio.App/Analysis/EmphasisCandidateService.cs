using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public enum EmphasisReasonKind
{
    ImportantStatement,
    Number,
    ProductName,
    PublicationCaution,
    Keyword,
    AnswerAfterExplicitQuestion,
    TopicTransitionSummary,
}

public enum EmphasisTextSourceKind
{
    OriginalTranscript,
    ManualSummary,
}

public sealed class EmphasisTermEvidence
{
    internal EmphasisTermEvidence(
        EntityId<TermEntryIdTag> termEntryId,
        int termEntryVersion,
        string writtenForm)
    {
        ArgumentNullException.ThrowIfNull(termEntryId);
        EmphasisCandidateContract.ValidateText(writtenForm, nameof(writtenForm));
        if (termEntryVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(termEntryVersion));
        }

        TermEntryId = termEntryId;
        TermEntryVersion = termEntryVersion;
        WrittenForm = writtenForm;
    }

    public EntityId<TermEntryIdTag> TermEntryId { get; }

    public int TermEntryVersion { get; }

    public string WrittenForm { get; }
}

public sealed class EmphasisCandidate
{
    internal EmphasisCandidate(
        Suggestion suggestion,
        SourceSpan sourceSpan,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<TopicMetadataRevisionIdTag> topicMetadataRevisionId,
        string ruleVersion,
        string text,
        EmphasisTextSourceKind textSource,
        IReadOnlyList<EmphasisReasonKind> reasons,
        IReadOnlyList<EmphasisTermEvidence> termEvidence)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(topicMetadataRevisionId);
        EmphasisCandidateContract.ValidateText(ruleVersion, nameof(ruleVersion));
        EmphasisCandidateContract.ValidateText(text, nameof(text));
        if (!Enum.IsDefined(textSource))
        {
            throw new ArgumentOutOfRangeException(nameof(textSource));
        }

        EmphasisReasonKind[] copiedReasons = reasons.ToArray();
        EmphasisTermEvidence[] copiedTerms = termEvidence.ToArray();
        if (copiedReasons.Length == 0
            || copiedReasons.Any(static reason => !Enum.IsDefined(reason))
            || copiedReasons.Distinct().Count() != copiedReasons.Length
            || !copiedReasons.SequenceEqual(copiedReasons.Order())
            || copiedTerms.Any(static term => term is null)
            || copiedTerms.Select(static term => term.TermEntryId).Distinct().Count()
                != copiedTerms.Length
            || !copiedTerms.Select(static term => term.TermEntryId.ToString())
                .SequenceEqual(copiedTerms
                    .Select(static term => term.TermEntryId.ToString())
                    .Order(StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Emphasis candidate evidence must be unique and canonical.",
                nameof(reasons));
        }

        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        bool hasManualMetadataReason = copiedReasons.Any(static reason =>
            reason is EmphasisReasonKind.PublicationCaution
                or EmphasisReasonKind.Keyword
                or EmphasisReasonKind.TopicTransitionSummary);
        bool hasProductReason = copiedReasons.Contains(
            EmphasisReasonKind.ProductName);
        if (suggestion.Kind != SuggestionKind.Highlight
            || suggestion.Status != SuggestionStatus.Proposed
            || suggestion.Origin != expectedOrigin
            || suggestion.Confidence is not null
            || sourceSpan.TranscriptRevisionId != sourceRevisionId
            || sourceSpan.TranscriptTokenIds.Count == 0
            || (textSource == EmphasisTextSourceKind.ManualSummary
                && !hasManualMetadataReason)
            || hasProductReason != (copiedTerms.Length > 0))
        {
            throw new ArgumentException(
                "An emphasis candidate contains inconsistent suggestion evidence.",
                nameof(suggestion));
        }

        Suggestion = suggestion;
        SourceSpan = sourceSpan;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        TopicMetadataRevisionId = topicMetadataRevisionId;
        RuleVersion = ruleVersion;
        Text = text;
        TextSource = textSource;
        Reasons = Array.AsReadOnly(copiedReasons);
        TermEvidence = Array.AsReadOnly(copiedTerms);
    }

    public Suggestion Suggestion { get; }

    public SourceSpan SourceSpan { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<TopicMetadataRevisionIdTag> TopicMetadataRevisionId { get; }

    public string RuleVersion { get; }

    public string Text { get; }

    public EmphasisTextSourceKind TextSource { get; }

    public bool IsExactOriginalText =>
        TextSource == EmphasisTextSourceKind.OriginalTranscript;

    public IReadOnlyList<EmphasisReasonKind> Reasons { get; }

    public IReadOnlyList<EmphasisTermEvidence> TermEvidence { get; }

    public bool AutomaticAdoptionAllowed => false;
}

public sealed class EmphasisCandidateScanResult
{
    internal EmphasisCandidateScanResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<TopicMetadataRevisionIdTag> topicMetadataRevisionId,
        EntityId<ModelRunIdTag> modelRunId,
        TermScope termScope,
        string ruleVersion,
        int inspectedTokenCount,
        int skippedMetadataWithoutSupportingContextCount,
        IReadOnlyList<EmphasisCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(topicMetadataRevisionId);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(termScope);
        EmphasisCandidateContract.ValidateText(ruleVersion, nameof(ruleVersion));
        ArgumentNullException.ThrowIfNull(candidates);
        if (inspectedTokenCount < 0
            || skippedMetadataWithoutSupportingContextCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inspectedTokenCount));
        }

        EmphasisCandidate[] copied = new EmphasisCandidate[candidates.Count];
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        CandidateOrderKey? previous = null;
        for (int index = 0; index < copied.Length; index++)
        {
            EmphasisCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Emphasis candidates must not contain null elements.",
                    nameof(candidates));
            var key = new CandidateOrderKey(
                candidate.SourceSpan.Range.Start,
                candidate.SourceSpan.Range.End,
                candidate.TextSource,
                candidate.Text,
                string.Join(
                    "\0",
                    candidate.SourceSpan.TranscriptTokenIds.Select(
                        static id => id.ToString())));
            if (candidate.SourceRevisionId != sourceRevisionId
                || candidate.SourceRevisionDigest != sourceRevisionDigest
                || candidate.TopicMetadataRevisionId != topicMetadataRevisionId
                || candidate.Suggestion.ModelRunId != modelRunId
                || !string.Equals(
                    candidate.RuleVersion,
                    ruleVersion,
                    StringComparison.Ordinal)
                || !suggestionIds.Add(candidate.Suggestion.Id)
                || previous is CandidateOrderKey prior
                    && CandidateOrderKey.Compare(prior, key) >= 0)
            {
                throw new ArgumentException(
                    "An emphasis scan contains inconsistent candidates.",
                    nameof(candidates));
            }

            previous = key;
            copied[index] = candidate;
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        TopicMetadataRevisionId = topicMetadataRevisionId;
        ModelRunId = modelRunId;
        TermScope = termScope;
        RuleVersion = ruleVersion;
        InspectedTokenCount = inspectedTokenCount;
        SkippedMetadataWithoutSupportingContextCount =
            skippedMetadataWithoutSupportingContextCount;
        Candidates = Array.AsReadOnly(copied);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<TopicMetadataRevisionIdTag> TopicMetadataRevisionId { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public TermScope TermScope { get; }

    public string RuleVersion { get; }

    public int InspectedTokenCount { get; }

    public int SkippedMetadataWithoutSupportingContextCount { get; }

    public IReadOnlyList<EmphasisCandidate> Candidates { get; }

    private readonly record struct CandidateOrderKey(
        RationalTime Start,
        RationalTime End,
        EmphasisTextSourceKind Source,
        string Text,
        string TokenKey)
    {
        internal static int Compare(CandidateOrderKey left, CandidateOrderKey right)
        {
            int result = left.Start.CompareTo(right.Start);
            if (result == 0)
            {
                result = left.End.CompareTo(right.End);
            }

            if (result == 0)
            {
                result = left.Source.CompareTo(right.Source);
            }

            if (result == 0)
            {
                result = string.CompareOrdinal(left.Text, right.Text);
            }

            return result != 0
                ? result
                : string.CompareOrdinal(left.TokenKey, right.TokenKey);
        }
    }
}

public enum EmphasisCandidateFailureReason
{
    SourceSpanBindingMismatch,
    TopicMetadataBindingMismatch,
    DictionaryScopeMismatch,
    DuplicateDictionaryEntry,
    ProductTermNotFound,
    InvalidText,
    ArithmeticOverflow,
}

public sealed class EmphasisCandidateException : Exception
{
    internal EmphasisCandidateException(EmphasisCandidateFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public EmphasisCandidateFailureReason Reason { get; }

    private static string GetStableMessage(
        EmphasisCandidateFailureReason reason) =>
        reason switch
        {
            EmphasisCandidateFailureReason.SourceSpanBindingMismatch =>
                "The emphasis source-span evidence is inconsistent.",
            EmphasisCandidateFailureReason.TopicMetadataBindingMismatch =>
                "The emphasis topic-metadata evidence is inconsistent.",
            EmphasisCandidateFailureReason.DictionaryScopeMismatch =>
                "The emphasis dictionary scope is inconsistent.",
            EmphasisCandidateFailureReason.DuplicateDictionaryEntry =>
                "The emphasis dictionary contains duplicate entries.",
            EmphasisCandidateFailureReason.ProductTermNotFound =>
                "An explicit product-term selection is missing from the dictionary.",
            EmphasisCandidateFailureReason.InvalidText =>
                "Emphasis text could not be processed safely.",
            EmphasisCandidateFailureReason.ArithmeticOverflow =>
                "Emphasis range arithmetic exceeds the supported range.",
            _ => "Emphasis candidate extraction failed.",
        };
}

public static class EmphasisCandidateService
{
    public const string RuleVersion = "fr013-2026-08-31.explicit-evidence.v1";

    private const string MissingContext =
        "The rule identifies explicit evidence, not editorial importance or display priority.";
    private const string ReviewWarning =
        "Review wording, timing, layout, and concurrent on-screen text before adoption.";

    public static EmphasisCandidateScanResult FindCandidates(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicMetadataRevision topicMetadataRevision,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope,
        IReadOnlyList<EntityId<TermEntryIdTag>> productTermEntryIds,
        EntityId<ModelRunIdTag> modelRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topicMetadataRevision);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        ArgumentNullException.ThrowIfNull(termScope);
        ArgumentNullException.ThrowIfNull(productTermEntryIds);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();
        SourceSpan[] spans = ValidateAndCopySourceSpans(revision, sourceSpans);
        ContentDigest revisionDigest = ComputeRevisionDigest(revision);
        ValidateTopicMetadata(
            revision,
            revisionDigest,
            topicMetadataRevision);
        if (termScope.Kind == TermScopeKind.Project
            && termScope.ProjectId != topicMetadataRevision.ProjectId)
        {
            throw Failure(
                EmphasisCandidateFailureReason.DictionaryScopeMismatch);
        }
        Dictionary<EntityId<TermEntryIdTag>, TermEntry> productTerms =
            ValidateAndSelectProductTerms(
                dictionaryEntries,
                termScope,
                productTermEntryIds);

        Dictionary<EntityId<TranscriptTokenIdTag>, Token> tokenById = revision
            .Utterances
            .SelectMany(static utterance => utterance.Tokens)
            .ToDictionary(static token => token.Id);
        var spanById = spans.ToDictionary(static span => span.Id);
        TimeRange topicRange = topicMetadataRevision.TopicRange;
        SourceSpan[] inTopicSpans = spans
            .Where(span => IsFullyContained(span.Range, topicRange))
            .ToArray();
        SourceSpan? metadataSupport = CreateMetadataSupportSpan(
            revision,
            inTopicSpans);
        var specifications = new List<CandidateSpecification>();
        int skippedMetadata = 0;
        try
        {
            AddImportantStatementCandidates(
                topicMetadataRevision,
                spanById,
                tokenById,
                specifications);
            AddOriginalRuleCandidates(
                revision,
                spans,
                topicRange,
                productTerms,
                specifications,
                cancellationToken);
            skippedMetadata += AddMetadataCandidates(
                topicMetadataRevision.Metadata,
                metadataSupport,
                revision,
                spans,
                topicRange,
                specifications);
        }
        catch (OverflowException)
        {
            throw Failure(EmphasisCandidateFailureReason.ArithmeticOverflow);
        }
        catch (Exception exception)
            when (exception is ArgumentException or EncoderFallbackException)
        {
            throw Failure(EmphasisCandidateFailureReason.InvalidText);
        }

        CandidateSpecification[] merged = specifications
            .GroupBy(static specification => CandidateIdentity.From(specification))
            .Select(static group => new CandidateSpecification(
                group.First().SourceSpan,
                group.Key.Text,
                group.Key.TextSource,
                group.SelectMany(static item => item.Reasons)
                    .Distinct()
                    .Order()
                    .ToArray(),
                group.SelectMany(static item => item.TermEvidence)
                    .GroupBy(static term => term.TermEntryId)
                    .Select(static terms => terms.First())
                    .OrderBy(static term => term.TermEntryId.ToString(), StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(static specification => specification.SourceSpan.Range.Start)
            .ThenBy(static specification => specification.SourceSpan.Range.End)
            .ThenBy(static specification => specification.TextSource)
            .ThenBy(static specification => specification.Text, StringComparer.Ordinal)
            .ThenBy(
                static specification => string.Join(
                    "\0",
                    specification.SourceSpan.TranscriptTokenIds.Select(
                        static id => id.ToString())),
                StringComparer.Ordinal)
            .ToArray();
        var candidates = new List<EmphasisCandidate>(merged.Length);
        foreach (CandidateSpecification specification in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(CreateCandidate(
                revision,
                revisionDigest,
                topicMetadataRevision,
                modelRunId,
                specification));
        }

        int inspectedTokenCount = revision.Utterances
            .Where(utterance => IsFullyContained(utterance.Range, topicRange))
            .Sum(static utterance => utterance.Tokens.Count);
        return new EmphasisCandidateScanResult(
            revision.Id,
            revisionDigest,
            topicMetadataRevision.Id,
            modelRunId,
            termScope,
            RuleVersion,
            inspectedTokenCount,
            skippedMetadata,
            candidates);
    }

    private static void AddImportantStatementCandidates(
        TopicMetadataRevision metadataRevision,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spanById,
        IReadOnlyDictionary<EntityId<TranscriptTokenIdTag>, Token> tokenById,
        List<CandidateSpecification> specifications)
    {
        foreach (EntityId<SourceSpanIdTag> sourceSpanId in
            metadataRevision.Metadata.ImportantStatementSourceSpanIds)
        {
            if (!spanById.TryGetValue(sourceSpanId, out SourceSpan? sourceSpan)
                || !IsFullyContained(sourceSpan.Range, metadataRevision.TopicRange))
            {
                throw Failure(
                    EmphasisCandidateFailureReason.TopicMetadataBindingMismatch);
            }

            string text = string.Concat(sourceSpan.TranscriptTokenIds.Select(tokenId =>
                tokenById.TryGetValue(tokenId, out Token? token)
                    ? token.Text
                    : throw Failure(
                        EmphasisCandidateFailureReason.SourceSpanBindingMismatch)));
            specifications.Add(new CandidateSpecification(
                sourceSpan,
                text,
                EmphasisTextSourceKind.OriginalTranscript,
                [EmphasisReasonKind.ImportantStatement],
                []));
        }
    }

    private static void AddOriginalRuleCandidates(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> spans,
        TimeRange topicRange,
        IReadOnlyDictionary<EntityId<TermEntryIdTag>, TermEntry> productTerms,
        List<CandidateSpecification> specifications,
        CancellationToken cancellationToken)
    {
        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = revision.Utterances[utteranceIndex];
            if (!IsFullyContained(utterance.Range, topicRange))
            {
                continue;
            }

            SourceSpan utteranceSpan = spans[utteranceIndex];
            foreach (Token token in utterance.Tokens)
            {
                string normalized = Normalize(token.Text);
                if (normalized.Any(static character => character is >= '0' and <= '9'))
                {
                    specifications.Add(new CandidateSpecification(
                        CreateTokenSpan(utteranceSpan, utterance, [token]),
                        token.Text,
                        EmphasisTextSourceKind.OriginalTranscript,
                        [EmphasisReasonKind.Number],
                        []));
                }
            }

            foreach (TermEntry term in productTerms.Values)
            {
                AddTermMatches(
                    utteranceSpan,
                    utterance,
                    term,
                    specifications);
            }

            if (utteranceIndex > 0
                && IsFullyContained(
                    revision.Utterances[utteranceIndex - 1].Range,
                    topicRange)
                && Normalize(revision.Utterances[utteranceIndex - 1].Text)
                    .EndsWith('?'))
            {
                specifications.Add(new CandidateSpecification(
                    utteranceSpan,
                    utterance.Text,
                    EmphasisTextSourceKind.OriginalTranscript,
                    [EmphasisReasonKind.AnswerAfterExplicitQuestion],
                    []));
            }
        }
    }

    private static int AddMetadataCandidates(
        TopicMetadata metadata,
        SourceSpan? supportingSpan,
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        TimeRange topicRange,
        List<CandidateSpecification> specifications)
    {
        var values = new List<(string Text, EmphasisReasonKind Reason)>();
        if (metadata.Summary is not null)
        {
            values.Add((
                metadata.Summary,
                EmphasisReasonKind.TopicTransitionSummary));
        }

        values.AddRange(metadata.Keywords.Select(static text =>
            (text, EmphasisReasonKind.Keyword)));
        values.AddRange(metadata.PublicationCautions.Select(static text =>
            (text, EmphasisReasonKind.PublicationCaution)));
        int skipped = 0;
        foreach ((string text, EmphasisReasonKind reason) in values)
        {
            OriginalTextMatch[] exactMatches = FindExactOriginalMatches(
                revision,
                sourceSpans,
                topicRange,
                text)
                .ToArray();
            if (exactMatches.Length > 0)
            {
                foreach (OriginalTextMatch exactMatch in exactMatches)
                {
                    specifications.Add(new CandidateSpecification(
                        exactMatch.SourceSpan,
                        exactMatch.OriginalText,
                        EmphasisTextSourceKind.OriginalTranscript,
                        [reason],
                        []));
                }
            }
            else if (supportingSpan is not null)
            {
                specifications.Add(new CandidateSpecification(
                    supportingSpan,
                    text,
                    EmphasisTextSourceKind.ManualSummary,
                    [reason],
                    []));
            }
            else
            {
                skipped++;
            }
        }

        return skipped;
    }

    private static void AddTermMatches(
        SourceSpan utteranceSpan,
        Utterance utterance,
        TermEntry term,
        List<CandidateSpecification> specifications)
    {
        string target = Normalize(term.WrittenForm);
        foreach (Sentence sentence in utterance.Sentences)
        {
            for (int start = 0; start < sentence.Tokens.Count; start++)
            {
                var builder = new StringBuilder();
                for (int end = start; end < sentence.Tokens.Count; end++)
                {
                    builder.Append(Normalize(sentence.Tokens[end].Text));
                    int comparison = builder.Length.CompareTo(target.Length);
                    if (comparison > 0)
                    {
                        break;
                    }

                    if (comparison == 0
                        && string.Equals(
                            builder.ToString(),
                            target,
                            StringComparison.Ordinal))
                    {
                        Token[] matched = sentence.Tokens
                            .Skip(start)
                            .Take(end - start + 1)
                            .ToArray();
                        specifications.Add(new CandidateSpecification(
                            CreateTokenSpan(utteranceSpan, utterance, matched),
                            string.Concat(matched.Select(static token => token.Text)),
                            EmphasisTextSourceKind.OriginalTranscript,
                            [EmphasisReasonKind.ProductName],
                            [new EmphasisTermEvidence(
                                term.Id,
                                term.Version,
                                term.WrittenForm)]));
                        break;
                    }
                }
            }
        }
    }

    private static SourceSpan CreateTokenSpan(
        SourceSpan utteranceSpan,
        Utterance utterance,
        IReadOnlyList<Token> tokens)
    {
        bool usesFallback = tokens.Any(static token => token.Range is null);
        TimeRange range = usesFallback
            ? utterance.Range
            : new TimeRange(
                tokens[0].Range!.Value.Start,
                tokens[^1].Range!.Value.End - tokens[0].Range!.Value.Start);
        return new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            utteranceSpan.AssetId,
            utteranceSpan.StreamId,
            range,
            utteranceSpan.SourceDigest,
            utteranceSpan.TranscriptRevisionId,
            tokens.Select(static token => token.Id).ToArray());
    }

    private static SourceSpan? CreateMetadataSupportSpan(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> inTopicSpans)
    {
        if (inTopicSpans.Count == 0)
        {
            return null;
        }

        SourceSpan first = inTopicSpans[0];
        if (inTopicSpans.Any(span =>
            span.AssetId != first.AssetId
            || !string.Equals(span.StreamId, first.StreamId, StringComparison.Ordinal)
            || span.SourceDigest != first.SourceDigest))
        {
            return null;
        }

        RationalTime start = inTopicSpans.Min(static span => span.Range.Start);
        RationalTime end = inTopicSpans.Max(static span => span.Range.End);
        EntityId<TranscriptTokenIdTag>[] tokenIds = revision.Utterances
            .Where(utterance => inTopicSpans.Any(
                span => span.Id == utterance.SourceSpanId))
            .SelectMany(static utterance => utterance.Tokens)
            .Select(static token => token.Id)
            .ToArray();
        return tokenIds.Length == 0
            ? null
            : new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                first.AssetId,
                first.StreamId,
                new TimeRange(start, end - start),
                first.SourceDigest,
                first.TranscriptRevisionId,
                tokenIds);
    }

    private static EmphasisCandidate CreateCandidate(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        TopicMetadataRevision topicMetadataRevision,
        EntityId<ModelRunIdTag> modelRunId,
        CandidateSpecification specification)
    {
        OriginRef origin = OriginRef.FromSourceSpan(specification.SourceSpan.Id);
        var suggestion = new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            modelRunId,
            SuggestionKind.Highlight,
            SuggestionStatus.Proposed,
            origin,
            GetReason(specification.Reasons),
            confidence: null,
            MissingContext,
            ReviewWarning);
        return new EmphasisCandidate(
            suggestion,
            specification.SourceSpan,
            revision.Id,
            revisionDigest,
            topicMetadataRevision.Id,
            RuleVersion,
            specification.Text,
            specification.TextSource,
            specification.Reasons,
            specification.TermEvidence);
    }

    private static string GetReason(
        IReadOnlyList<EmphasisReasonKind> reasons) =>
        string.Concat(
            "Explicit emphasis evidence: ",
            string.Join(", ", reasons.Select(static reason => reason switch
            {
                EmphasisReasonKind.ImportantStatement => "manual important statement",
                EmphasisReasonKind.Number => "numeric token",
                EmphasisReasonKind.ProductName => "explicit product dictionary term",
                EmphasisReasonKind.PublicationCaution => "manual publication caution",
                EmphasisReasonKind.Keyword => "manual keyword",
                EmphasisReasonKind.AnswerAfterExplicitQuestion =>
                    "utterance immediately after explicit question punctuation",
                EmphasisReasonKind.TopicTransitionSummary => "manual topic summary",
                _ => throw new ArgumentOutOfRangeException(nameof(reasons)),
            })),
            ".");

    private static Dictionary<EntityId<TermEntryIdTag>, TermEntry>
        ValidateAndSelectProductTerms(
            IReadOnlyList<TermEntry> dictionaryEntries,
            TermScope termScope,
            IReadOnlyList<EntityId<TermEntryIdTag>> productTermEntryIds)
    {
        var entries = new Dictionary<EntityId<TermEntryIdTag>, TermEntry>();
        var writtenForms = new HashSet<string>(StringComparer.Ordinal);
        foreach (TermEntry entry in dictionaryEntries)
        {
            if (entry is null || entry.Scope != termScope)
            {
                throw Failure(
                    EmphasisCandidateFailureReason.DictionaryScopeMismatch);
            }

            if (!entries.TryAdd(entry.Id, entry)
                || !writtenForms.Add(Normalize(entry.WrittenForm)))
            {
                throw Failure(
                    EmphasisCandidateFailureReason.DuplicateDictionaryEntry);
            }
        }

        var selected = new Dictionary<EntityId<TermEntryIdTag>, TermEntry>();
        foreach (EntityId<TermEntryIdTag> selectedId in productTermEntryIds)
        {
            if (selectedId is null
                || !entries.TryGetValue(selectedId, out TermEntry? entry))
            {
                throw Failure(
                    EmphasisCandidateFailureReason.ProductTermNotFound);
            }

            if (!selected.TryAdd(selectedId, entry))
            {
                throw Failure(
                    EmphasisCandidateFailureReason.DuplicateDictionaryEntry);
            }
        }

        return selected;
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(
                EmphasisCandidateFailureReason.SourceSpanBindingMismatch);
        }

        var copied = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    EmphasisCandidateFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    EmphasisCandidateFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static void ValidateTopicMetadata(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        TopicMetadataRevision metadataRevision)
    {
        if (metadataRevision.SourceRevisionId != revision.Id
            || metadataRevision.SourceRevisionDigest != revisionDigest)
        {
            throw Failure(
                EmphasisCandidateFailureReason.TopicMetadataBindingMismatch);
        }
    }

    private static IEnumerable<OriginalTextMatch> FindExactOriginalMatches(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        TimeRange topicRange,
        string text)
    {
        string target = Normalize(text);
        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            Utterance utterance = revision.Utterances[utteranceIndex];
            if (!IsFullyContained(utterance.Range, topicRange))
            {
                continue;
            }

            foreach (Sentence sentence in utterance.Sentences)
            {
                for (int start = 0; start < sentence.Tokens.Count; start++)
                {
                    var builder = new StringBuilder();
                    for (int end = start; end < sentence.Tokens.Count; end++)
                    {
                        builder.Append(Normalize(sentence.Tokens[end].Text));
                        int comparison = builder.Length.CompareTo(target.Length);
                        if (comparison > 0)
                        {
                            break;
                        }

                        if (comparison == 0
                            && string.Equals(
                                builder.ToString(),
                                target,
                                StringComparison.Ordinal))
                        {
                            Token[] matched = sentence.Tokens
                                .Skip(start)
                                .Take(end - start + 1)
                                .ToArray();
                            yield return new OriginalTextMatch(
                                CreateTokenSpan(
                                    sourceSpans[utteranceIndex],
                                    utterance,
                                    matched),
                                string.Concat(matched.Select(
                                    static token => token.Text)));
                            break;
                        }
                    }
                }
            }
        }
    }

    private static bool IsFullyContained(TimeRange value, TimeRange container) =>
        value.Start >= container.Start && value.End <= container.End;

    private static string Normalize(string value)
    {
        try
        {
            _ = EmphasisCandidateContract.StrictUtf8.GetByteCount(value);
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (Exception exception)
            when (exception is EncoderFallbackException or ArgumentException)
        {
            throw Failure(EmphasisCandidateFailureReason.InvalidText);
        }
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision revision)
    {
        try
        {
            string canonical = TranscriptSnapshotJson.WriteRevision(revision);
            return ContentDigest.Compute(
                EmphasisCandidateContract.StrictUtf8.GetBytes(canonical));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(EmphasisCandidateFailureReason.InvalidText);
        }
    }

    private static EmphasisCandidateException Failure(
        EmphasisCandidateFailureReason reason) =>
        new(reason);

    private sealed record CandidateSpecification(
        SourceSpan SourceSpan,
        string Text,
        EmphasisTextSourceKind TextSource,
        IReadOnlyList<EmphasisReasonKind> Reasons,
        IReadOnlyList<EmphasisTermEvidence> TermEvidence);

    private sealed record CandidateIdentity(
        EntityId<AssetIdTag> AssetId,
        string StreamId,
        ContentDigest SourceDigest,
        RationalTime Start,
        RationalTime End,
        string TokenKey,
        EmphasisTextSourceKind TextSource,
        string Text)
    {
        internal static CandidateIdentity From(
            CandidateSpecification specification) =>
            new(
                specification.SourceSpan.AssetId,
                specification.SourceSpan.StreamId,
                specification.SourceSpan.SourceDigest,
                specification.SourceSpan.Range.Start,
                specification.SourceSpan.Range.End,
                string.Join(
                    "\0",
                    specification.SourceSpan.TranscriptTokenIds.Select(
                        static id => id.ToString())),
                specification.TextSource,
                specification.Text);
    }

    private sealed record OriginalTextMatch(
        SourceSpan SourceSpan,
        string OriginalText);
}

internal static class EmphasisCandidateContract
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
                "Emphasis text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Emphasis text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}