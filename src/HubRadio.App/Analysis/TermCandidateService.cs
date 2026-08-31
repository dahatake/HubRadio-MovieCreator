using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public enum TermCandidateMatchKind
{
    WrittenFormCompatibilityVariant,
    WrittenFormCaseVariant,
    DictionaryReading,
}

public enum TermCandidateDecisionKind
{
    Rejected,
    Adopted,
}

public sealed class TermCandidate
{
    internal TermCandidate(
        Suggestion suggestion,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<SourceSpanIdTag> sourceSpanId,
        EntityId<TranscriptTokenIdTag> tokenId,
        TermScope termScope,
        EntityId<TermEntryIdTag> termEntryId,
        int termEntryVersion,
        string dictionaryReading,
        string observedText,
        string proposedText,
        TermCandidateMatchKind matchKind)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        ArgumentNullException.ThrowIfNull(tokenId);
        ArgumentNullException.ThrowIfNull(termScope);
        ArgumentNullException.ThrowIfNull(termEntryId);
        TermCandidateContract.ValidateText(
            dictionaryReading,
            nameof(dictionaryReading));
        TermCandidateContract.ValidateText(observedText, nameof(observedText));
        TermCandidateContract.ValidateText(proposedText, nameof(proposedText));

        if (termEntryVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(termEntryVersion),
                termEntryVersion,
                "A term candidate version must not be negative.");
        }

        if (!Enum.IsDefined(matchKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchKind),
                matchKind,
                "The term-candidate match kind must be defined.");
        }

        if (suggestion.Kind != SuggestionKind.TermCorrection
            || suggestion.Origin.Kind != OriginKind.SourceSpan
            || suggestion.Origin.SourceSpanId != sourceSpanId)
        {
            throw new ArgumentException(
                "A term candidate requires a term-correction suggestion bound to its source span.",
                nameof(suggestion));
        }

        if (string.Equals(observedText, proposedText, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A term candidate must propose a text change.",
                nameof(proposedText));
        }

        Suggestion = suggestion;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        SourceSpanId = sourceSpanId;
        TokenId = tokenId;
        TermScope = termScope;
        TermEntryId = termEntryId;
        TermEntryVersion = termEntryVersion;
        DictionaryReading = dictionaryReading;
        ObservedText = observedText;
        ProposedText = proposedText;
        MatchKind = matchKind;
    }

    public Suggestion Suggestion { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public EntityId<TranscriptTokenIdTag> TokenId { get; }

    public TermScope TermScope { get; }

    public EntityId<TermEntryIdTag> TermEntryId { get; }

    public int TermEntryVersion { get; }

    public string DictionaryReading { get; }

    public string ObservedText { get; }

    public string ProposedText { get; }

    public TermCandidateMatchKind MatchKind { get; }
}

public sealed class TermCandidateScanResult
{
    internal TermCandidateScanResult(
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<ModelRunIdTag> modelRunId,
        TermScope termScope,
        int inspectedTokenCount,
        IReadOnlyList<TermCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(termScope);
        ArgumentNullException.ThrowIfNull(candidates);
        if (inspectedTokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inspectedTokenCount),
                inspectedTokenCount,
                "The inspected token count must not be negative.");
        }

        var candidateIds = new HashSet<EntityId<SuggestionIdTag>>();
        TermCandidate[] copiedCandidates = new TermCandidate[candidates.Count];
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            TermCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Term candidates must not contain null elements.",
                    nameof(candidates));
            if (candidate.SourceRevisionId != sourceRevisionId
                || candidate.SourceRevisionDigest != sourceRevisionDigest
                || candidate.Suggestion.ModelRunId != modelRunId
                || candidate.TermScope != termScope
                || candidate.Suggestion.Status != SuggestionStatus.Proposed
                || !candidateIds.Add(candidate.Suggestion.Id))
            {
                throw new ArgumentException(
                    "A term-candidate scan result contains inconsistent candidates.",
                    nameof(candidates));
            }

            copiedCandidates[index] = candidate;
        }

        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        ModelRunId = modelRunId;
        TermScope = termScope;
        InspectedTokenCount = inspectedTokenCount;
        Candidates = Array.AsReadOnly(copiedCandidates);
    }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public TermScope TermScope { get; }

    public int InspectedTokenCount { get; }

    public IReadOnlyList<TermCandidate> Candidates { get; }
}

public sealed class TermCandidateDecisionRecord
{
    internal TermCandidateDecisionRecord(
        EntityId<SuggestionIdTag> candidateId,
        TermCandidateDecisionKind decision,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        EntityId<TranscriptTokenIdTag> tokenId,
        EntityId<TermEntryIdTag> termEntryId,
        int termEntryVersion,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        EntityId<TranscriptRevisionIdTag>? resultingRevisionId)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(tokenId);
        ArgumentNullException.ThrowIfNull(termEntryId);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "The term-candidate decision must be defined.");
        }

        if (termEntryVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(termEntryVersion),
                termEntryVersion,
                "A decided term-entry version must not be negative.");
        }

        bool resultBindingIsValid = decision switch
        {
            TermCandidateDecisionKind.Rejected => resultingRevisionId is null,
            TermCandidateDecisionKind.Adopted => resultingRevisionId is not null,
            _ => false,
        };
        if (!resultBindingIsValid)
        {
            throw new ArgumentException(
                "A term-candidate decision has an invalid resulting revision binding.",
                nameof(resultingRevisionId));
        }

        CandidateId = candidateId;
        Decision = decision;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        TokenId = tokenId;
        TermEntryId = termEntryId;
        TermEntryVersion = termEntryVersion;
        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        TranscriptEditorContract.ValidateText(reason, nameof(reason));
        DecidedAt = decidedAt;
        Reason = reason;
        ResultingRevisionId = resultingRevisionId;
    }

    public EntityId<SuggestionIdTag> CandidateId { get; }

    public TermCandidateDecisionKind Decision { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public EntityId<TranscriptTokenIdTag> TokenId { get; }

    public EntityId<TermEntryIdTag> TermEntryId { get; }

    public int TermEntryVersion { get; }

    public string ActorSid { get; }

    public DateTimeOffset DecidedAt { get; }

    public string Reason { get; }

    public EntityId<TranscriptRevisionIdTag>? ResultingRevisionId { get; }
}

public sealed class TermCandidateDecisionResult
{
    internal TermCandidateDecisionResult(
        TermCandidate originalCandidate,
        TermCandidate decidedCandidate,
        TermCandidateDecisionRecord decisionRecord,
        TranscriptEditResult? transcriptEdit)
    {
        ArgumentNullException.ThrowIfNull(originalCandidate);
        ArgumentNullException.ThrowIfNull(decidedCandidate);
        ArgumentNullException.ThrowIfNull(decisionRecord);
        if (originalCandidate.Suggestion.Status is not (
                SuggestionStatus.Proposed or SuggestionStatus.Reviewed)
            || originalCandidate.Suggestion.Id != decidedCandidate.Suggestion.Id
            || originalCandidate.SourceRevisionId != decidedCandidate.SourceRevisionId
            || decisionRecord.CandidateId != originalCandidate.Suggestion.Id
            || decisionRecord.SourceRevisionId != originalCandidate.SourceRevisionId
            || decisionRecord.SourceRevisionDigest !=
                originalCandidate.SourceRevisionDigest
            || decisionRecord.TokenId != originalCandidate.TokenId
            || decisionRecord.TermEntryId != originalCandidate.TermEntryId
            || decisionRecord.TermEntryVersion != originalCandidate.TermEntryVersion)
        {
            throw new ArgumentException(
                "A term-candidate decision result contains inconsistent evidence.",
                nameof(decidedCandidate));
        }

        bool resultIsValid = decisionRecord.Decision switch
        {
            TermCandidateDecisionKind.Rejected =>
                decidedCandidate.Suggestion.Status == SuggestionStatus.Rejected
                && transcriptEdit is null,
            TermCandidateDecisionKind.Adopted =>
                decidedCandidate.Suggestion.Status == SuggestionStatus.Adopted
                && transcriptEdit is not null
                && transcriptEdit.SourceRevision.Id == originalCandidate.SourceRevisionId
                && transcriptEdit.Revision.Id == decisionRecord.ResultingRevisionId,
            _ => false,
        };
        if (!resultIsValid)
        {
            throw new ArgumentException(
                "A term-candidate decision result does not match its disposition.",
                nameof(transcriptEdit));
        }

        OriginalCandidate = originalCandidate;
        DecidedCandidate = decidedCandidate;
        DecisionRecord = decisionRecord;
        TranscriptEdit = transcriptEdit;
    }

    public TermCandidate OriginalCandidate { get; }

    public TermCandidate DecidedCandidate { get; }

    public TermCandidateDecisionRecord DecisionRecord { get; }

    public TranscriptEditResult? TranscriptEdit { get; }
}

public enum TermCandidateFailureReason
{
    SourceSpanBindingMismatch,
    DictionaryScopeMismatch,
    DuplicateDictionaryEntry,
    InvalidText,
    CandidateAlreadyDecided,
    CandidateStale,
    TermEntryChanged,
    InvalidDecision,
    DecisionPrecedesSource,
}

public sealed class TermCandidateException : Exception
{
    internal TermCandidateException(TermCandidateFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public TermCandidateFailureReason Reason { get; }

    private static string GetStableMessage(TermCandidateFailureReason reason) =>
        reason switch
        {
            TermCandidateFailureReason.SourceSpanBindingMismatch =>
                "The transcript source-span evidence is inconsistent.",
            TermCandidateFailureReason.DictionaryScopeMismatch =>
                "A dictionary entry belongs to a different term scope.",
            TermCandidateFailureReason.DuplicateDictionaryEntry =>
                "The dictionary snapshot contains a duplicate term-entry ID.",
            TermCandidateFailureReason.InvalidText =>
                "Term-candidate text cannot be normalized safely.",
            TermCandidateFailureReason.CandidateAlreadyDecided =>
                "The term candidate has already received a final decision.",
            TermCandidateFailureReason.CandidateStale =>
                "The term candidate does not match the current transcript revision.",
            TermCandidateFailureReason.TermEntryChanged =>
                "The term candidate does not match the current dictionary entry.",
            TermCandidateFailureReason.InvalidDecision =>
                "The term-candidate decision identity or reason is invalid.",
            TermCandidateFailureReason.DecisionPrecedesSource =>
                "The term-candidate decision precedes its source revision.",
            _ => "The term candidate could not be processed.",
        };
}

public static class TermCandidateService
{
    private const string HumanReviewWarning =
        "The transcript remains unchanged until a user adopts this candidate.";

    public static TermCandidateScanResult FindCandidates(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope,
        EntityId<ModelRunIdTag> modelRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        ArgumentNullException.ThrowIfNull(termScope);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();

        SourceSpan[] validatedSpans = ValidateAndCopySourceSpans(
            revision,
            sourceSpans);
        ContentDigest sourceRevisionDigest = ComputeRevisionDigest(revision);
        IndexedTermEntry[] entries = ValidateAndIndexDictionary(
            dictionaryEntries,
            termScope);
        var candidates = new List<TermCandidate>();
        int inspectedTokenCount = 0;

        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Utterance utterance = revision.Utterances[utteranceIndex];
            SourceSpan sourceSpan = validatedSpans[utteranceIndex];
            foreach (Token token in utterance.Tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inspectedTokenCount = checked(inspectedTokenCount + 1);
                string normalizedObserved = NormalizeForMatching(token.Text);
                if (entries.Any(entry => string.Equals(
                        token.Text,
                        entry.Entry.WrittenForm,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                CandidateMatch[] matches = entries
                    .Select(entry => ClassifyMatch(normalizedObserved, entry))
                    .Where(static match => match is not null)
                    .Select(static match => match!.Value)
                    .ToArray();
                string? missingContext = matches.Length > 1
                    ? "Multiple dictionary entries match the same transcript token."
                    : null;
                foreach (CandidateMatch match in matches)
                {
                    var suggestion = new Suggestion(
                        EntityId<SuggestionIdTag>.New(),
                        modelRunId,
                        SuggestionKind.TermCorrection,
                        SuggestionStatus.Proposed,
                        OriginRef.FromSourceSpan(sourceSpan.Id),
                        GetReason(match.Kind),
                        confidence: null,
                        missingContext,
                        HumanReviewWarning);
                    candidates.Add(new TermCandidate(
                        suggestion,
                        revision.Id,
                        sourceRevisionDigest,
                        sourceSpan.Id,
                        token.Id,
                        termScope,
                        match.Entry.Entry.Id,
                        match.Entry.Entry.Version,
                        match.Entry.Entry.Reading,
                        token.Text,
                        match.Entry.Entry.WrittenForm,
                        match.Kind));
                }
            }
        }

        return new TermCandidateScanResult(
            revision.Id,
            sourceRevisionDigest,
            modelRunId,
            termScope,
            inspectedTokenCount,
            candidates);
    }

    public static TermCandidateDecisionResult RejectCandidate(
        TermCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason)
    {
        ValidateDecision(
            candidate,
            currentRevision,
            currentSourceSpans,
            currentTermEntry,
            actorSid,
            decidedAt,
            reason);
        TermCandidate rejected = WithStatus(candidate, SuggestionStatus.Rejected);
        var record = new TermCandidateDecisionRecord(
            candidate.Suggestion.Id,
            TermCandidateDecisionKind.Rejected,
            candidate.SourceRevisionId,
            candidate.SourceRevisionDigest,
            candidate.TokenId,
            candidate.TermEntryId,
            candidate.TermEntryVersion,
            actorSid,
            decidedAt,
            reason,
            resultingRevisionId: null);
        return new TermCandidateDecisionResult(
            candidate,
            rejected,
            record,
            transcriptEdit: null);
    }

    public static TermCandidateDecisionResult AdoptCandidate(
        TermCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        EntityId<TranscriptRevisionIdTag> newRevisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(newRevisionId);
        ValidateDecision(
            candidate,
            currentRevision,
            currentSourceSpans,
            currentTermEntry,
            actorSid,
            decidedAt,
            reason);

        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            currentRevision,
            currentSourceSpans,
            [new TranscriptTokenTextEdit(
                candidate.TokenId,
                candidate.ObservedText,
                candidate.ProposedText)],
            newRevisionId,
            actorSid,
            decidedAt,
            reason);
        TermCandidate adopted = WithStatus(candidate, SuggestionStatus.Adopted);
        var record = new TermCandidateDecisionRecord(
            candidate.Suggestion.Id,
            TermCandidateDecisionKind.Adopted,
            candidate.SourceRevisionId,
            candidate.SourceRevisionDigest,
            candidate.TokenId,
            candidate.TermEntryId,
            candidate.TermEntryVersion,
            actorSid,
            decidedAt,
            reason,
            edit.Revision.Id);
        return new TermCandidateDecisionResult(candidate, adopted, record, edit);
    }

    internal static bool MatchesCurrentSnapshot(
        TermCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        out ContentDigest currentRevisionDigest)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        SourceSpan[] spans = ValidateAndCopySourceSpans(
            currentRevision,
            currentSourceSpans);
        currentRevisionDigest = ComputeRevisionDigest(currentRevision);
        return GetSnapshotFailure(
            candidate,
            currentRevision,
            spans,
            currentTermEntry,
            currentRevisionDigest) is null;
    }

    private static void ValidateDecision(
        TermCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        if (candidate.Suggestion.Status is not (
                SuggestionStatus.Proposed or SuggestionStatus.Reviewed))
        {
            throw Failure(TermCandidateFailureReason.CandidateAlreadyDecided);
        }

        if (decidedAt < currentRevision.CreatedAt)
        {
            throw Failure(TermCandidateFailureReason.DecisionPrecedesSource);
        }

        try
        {
            _ = TranscriptEditorContract.ValidateCanonicalSid(
                actorSid,
                nameof(actorSid));
            TranscriptEditorContract.ValidateText(reason, nameof(reason));
        }
        catch (ArgumentException)
        {
            throw Failure(TermCandidateFailureReason.InvalidDecision);
        }

        SourceSpan[] spans = ValidateAndCopySourceSpans(
            currentRevision,
            currentSourceSpans);
        if (candidate.SourceRevisionId != currentRevision.Id)
        {
            throw Failure(TermCandidateFailureReason.CandidateStale);
        }

        ContentDigest currentRevisionDigest = ComputeRevisionDigest(
            currentRevision);
        if (GetSnapshotFailure(
                candidate,
                currentRevision,
                spans,
                currentTermEntry,
                currentRevisionDigest) is { } failureReason)
        {
            throw Failure(failureReason);
        }
    }

    private static TermCandidateFailureReason? GetSnapshotFailure(
        TermCandidate candidate,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry,
        ContentDigest currentRevisionDigest)
    {
        if (candidate.SourceRevisionId != currentRevision.Id
            || candidate.SourceRevisionDigest != currentRevisionDigest)
        {
            return TermCandidateFailureReason.CandidateStale;
        }

        // Bind both display and decision paths to the exact OCC snapshot that
        // produced the candidate. A content-equivalent update still requires
        // the user to review a fresh candidate.
        if (candidate.TermEntryId != currentTermEntry.Id
            || candidate.TermEntryVersion != currentTermEntry.Version
            || candidate.TermScope != currentTermEntry.Scope
            || !string.Equals(
                candidate.ProposedText,
                currentTermEntry.WrittenForm,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.DictionaryReading,
                currentTermEntry.Reading,
                StringComparison.Ordinal))
        {
            return TermCandidateFailureReason.TermEntryChanged;
        }

        for (int utteranceIndex = 0;
            utteranceIndex < currentRevision.Utterances.Count;
            utteranceIndex++)
        {
            Utterance utterance = currentRevision.Utterances[utteranceIndex];
            foreach (Token token in utterance.Tokens)
            {
                if (token.Id != candidate.TokenId)
                {
                    continue;
                }

                if (currentSourceSpans[utteranceIndex].Id != candidate.SourceSpanId
                    || !string.Equals(
                        token.Text,
                        candidate.ObservedText,
                        StringComparison.Ordinal))
                {
                    return TermCandidateFailureReason.CandidateStale;
                }

                string normalizedObserved = NormalizeForMatching(token.Text);
                IndexedTermEntry indexedEntry = IndexEntry(currentTermEntry);
                CandidateMatch? match = ClassifyMatch(
                    normalizedObserved,
                    indexedEntry);
                if (match is null || match.Value.Kind != candidate.MatchKind)
                {
                    return TermCandidateFailureReason.TermEntryChanged;
                }

                return null;
            }
        }

        return TermCandidateFailureReason.CandidateStale;
    }

    private static SourceSpan[] ValidateAndCopySourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw Failure(TermCandidateFailureReason.SourceSpanBindingMismatch);
        }

        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        var copied = new SourceSpan[sourceSpans.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    TermCandidateFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != revision.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    TermCandidateFailureReason.SourceSpanBindingMismatch);
            }

            copied[index] = span;
        }

        return copied;
    }

    private static IndexedTermEntry[] ValidateAndIndexDictionary(
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope)
    {
        var ids = new HashSet<EntityId<TermEntryIdTag>>();
        var entries = new List<IndexedTermEntry>(dictionaryEntries.Count);
        for (int index = 0; index < dictionaryEntries.Count; index++)
        {
            TermEntry entry = dictionaryEntries[index]
                ?? throw new ArgumentException(
                    "Dictionary entries must not contain null elements.",
                    nameof(dictionaryEntries));
            if (entry.Scope != termScope)
            {
                throw Failure(TermCandidateFailureReason.DictionaryScopeMismatch);
            }

            if (!ids.Add(entry.Id))
            {
                throw Failure(TermCandidateFailureReason.DuplicateDictionaryEntry);
            }

            entries.Add(IndexEntry(entry));
        }

        return entries
            .OrderBy(static entry => entry.NormalizedWrittenForm, StringComparer.Ordinal)
            .ThenBy(static entry => entry.NormalizedReading, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Entry.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static IndexedTermEntry IndexEntry(TermEntry entry)
    {
        return new IndexedTermEntry(
            entry,
            NormalizeForMatching(entry.WrittenForm),
            NormalizeForMatching(entry.Reading));
    }

    private static CandidateMatch? ClassifyMatch(
        string normalizedObserved,
        IndexedTermEntry entry)
    {
        if (string.Equals(
                normalizedObserved,
                entry.NormalizedWrittenForm,
                StringComparison.Ordinal))
        {
            return new CandidateMatch(
                entry,
                TermCandidateMatchKind.WrittenFormCompatibilityVariant);
        }

        if (string.Equals(
                normalizedObserved,
                entry.NormalizedWrittenForm,
                StringComparison.OrdinalIgnoreCase))
        {
            return new CandidateMatch(
                entry,
                TermCandidateMatchKind.WrittenFormCaseVariant);
        }

        if (string.Equals(
                normalizedObserved,
                entry.NormalizedReading,
                StringComparison.Ordinal))
        {
            return new CandidateMatch(
                entry,
                TermCandidateMatchKind.DictionaryReading);
        }

        return null;
    }

    private static string NormalizeForMatching(string value)
    {
        try
        {
            _ = TermCandidateContract.StrictUtf8.GetByteCount(value);
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (Exception exception)
            when (exception is EncoderFallbackException or ArgumentException)
        {
            throw Failure(TermCandidateFailureReason.InvalidText);
        }
    }

    private static ContentDigest ComputeRevisionDigest(TranscriptRevision revision)
    {
        try
        {
            string canonicalJson = TranscriptSnapshotJson.WriteRevision(revision);
            return ContentDigest.Compute(
                TermCandidateContract.StrictUtf8.GetBytes(canonicalJson));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(TermCandidateFailureReason.InvalidText);
        }
    }

    private static string GetReason(TermCandidateMatchKind kind) =>
        kind switch
        {
            TermCandidateMatchKind.WrittenFormCompatibilityVariant =>
                "The token matches the dictionary written form after compatibility normalization.",
            TermCandidateMatchKind.WrittenFormCaseVariant =>
                "The token matches the dictionary written form after case-insensitive comparison.",
            TermCandidateMatchKind.DictionaryReading =>
                "The token exactly matches the dictionary reading.",
            _ => throw Failure(TermCandidateFailureReason.InvalidText),
        };

    private static TermCandidate WithStatus(
        TermCandidate candidate,
        SuggestionStatus status)
    {
        Suggestion original = candidate.Suggestion;
        var suggestion = new Suggestion(
            original.Id,
            original.ModelRunId,
            original.Kind,
            status,
            original.Origin,
            original.Reason,
            original.Confidence,
            original.MissingContext,
            original.Warning);
        return new TermCandidate(
            suggestion,
            candidate.SourceRevisionId,
            candidate.SourceRevisionDigest,
            candidate.SourceSpanId,
            candidate.TokenId,
            candidate.TermScope,
            candidate.TermEntryId,
            candidate.TermEntryVersion,
            candidate.DictionaryReading,
            candidate.ObservedText,
            candidate.ProposedText,
            candidate.MatchKind);
    }

    private static TermCandidateException Failure(
        TermCandidateFailureReason reason) =>
        new(reason);

    private readonly record struct IndexedTermEntry(
        TermEntry Entry,
        string NormalizedWrittenForm,
        string NormalizedReading);

    private readonly record struct CandidateMatch(
        IndexedTermEntry Entry,
        TermCandidateMatchKind Kind);
}

internal static class TermCandidateContract
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
                "Term-candidate text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Term-candidate text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}
