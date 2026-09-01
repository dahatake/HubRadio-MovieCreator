using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum TranscriptBridgeStatus
{
    Available,
    Forbidden,
    Unavailable,
    StaleBase,
    Conflict,
    NoChange,
    Invalid,
}

internal sealed class TranscriptBridgeSnapshot
{
    private TranscriptBridgeSnapshot(
        Project project,
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTimingEvidence> timingEvidence,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope,
        EntityId<ModelRunIdTag> modelRunId,
        SpeakerAssignmentHistory? speakerHistory,
        IReadOnlyList<TermCandidate> termCandidates,
        IReadOnlyList<TermCandidateDecisionRecord> termDecisions,
        TranscriptDiff? diff,
        TranscriptDownstreamStaleRecord? downstreamStale,
        AnalysisInvalidationPlan? invalidation)
    {
        Project = project;
        Revision = revision;
        RevisionDigest = revisionDigest;
        SourceSpans = sourceSpans;
        TimingEvidence = timingEvidence;
        DictionaryEntries = dictionaryEntries;
        TermScope = termScope;
        ModelRunId = modelRunId;
        SpeakerHistory = speakerHistory;
        TermCandidates = termCandidates;
        TermDecisions = termDecisions;
        Diff = diff;
        DownstreamStale = downstreamStale;
        Invalidation = invalidation;
    }

    internal Project Project { get; }

    internal TranscriptRevision Revision { get; }

    internal ContentDigest RevisionDigest { get; }

    internal IReadOnlyList<SourceSpan> SourceSpans { get; }

    internal IReadOnlyList<TranscriptTimingEvidence> TimingEvidence { get; }

    internal IReadOnlyList<TermEntry> DictionaryEntries { get; }

    internal TermScope TermScope { get; }

    internal EntityId<ModelRunIdTag> ModelRunId { get; }

    internal SpeakerAssignmentHistory? SpeakerHistory { get; }

    internal IReadOnlyList<TermCandidate> TermCandidates { get; }

    internal IReadOnlyList<TermCandidateDecisionRecord> TermDecisions { get; }

    internal TranscriptDiff? Diff { get; }

    internal TranscriptDownstreamStaleRecord? DownstreamStale { get; }

    internal AnalysisInvalidationPlan? Invalidation { get; }

    internal static TranscriptBridgeSnapshot Create(
        Project project,
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTimingEvidence> timingEvidence,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope,
        EntityId<ModelRunIdTag> modelRunId,
        SpeakerAssignmentHistory? speakerHistory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(timingEvidence);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);
        ArgumentNullException.ThrowIfNull(termScope);
        ArgumentNullException.ThrowIfNull(modelRunId);

        ContentDigest digest = ComputeRevisionDigest(revision);
        SourceSpan[] spans = ValidateSourceSpans(revision, sourceSpans);
        TranscriptTimingEvidence[] timing = ValidateTimingEvidence(
            revision,
            spans,
            timingEvidence);
        TermEntry[] dictionary = ValidateDictionary(
            project,
            dictionaryEntries,
            termScope);
        ValidateSpeakerHistory(project, revision, digest, speakerHistory);
        TermCandidateScanResult scan = TermCandidateService.FindCandidates(
            revision,
            spans,
            dictionary,
            termScope,
            modelRunId);

        return CreateValidated(
            project,
            revision,
            digest,
            spans,
            timing,
            dictionary,
            termScope,
            modelRunId,
            speakerHistory,
            scan.Candidates,
            termDecisions: [],
            diff: null,
            downstreamStale: null,
            invalidation: null);
    }

    internal static TranscriptBridgeSnapshot FromTranscriptEdit(
        TranscriptBridgeSnapshot source,
        TranscriptEditResult edit,
        IReadOnlyList<TermCandidateDecisionRecord> termDecisions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(termDecisions);
        if (source.Revision.Id != edit.SourceRevision.Id ||
            source.RevisionDigest != edit.DownstreamStale.SourceRevisionDigest)
        {
            throw new ArgumentException(
                "The transcript edit does not apply to the registered snapshot.",
                nameof(edit));
        }

        TranscriptTimingEvidence[] rebasedTiming = RebaseTimingEvidence(
            source,
            edit.Revision,
            edit.RevisedSourceSpans);
        TermCandidateScanResult scan = TermCandidateService.FindCandidates(
            edit.Revision,
            edit.RevisedSourceSpans,
            source.DictionaryEntries,
            source.TermScope,
            source.ModelRunId);
        AnalysisInvalidationPlan invalidation =
            AnalysisDependencyGraph.Default.AnalyzeTranscriptEdit(
                edit.DownstreamStale);
        return CreateValidated(
            source.Project,
            edit.Revision,
            edit.RevisionDigest,
            edit.RevisedSourceSpans,
            rebasedTiming,
            source.DictionaryEntries,
            source.TermScope,
            source.ModelRunId,
            speakerHistory: null,
            scan.Candidates,
            termDecisions,
            edit.EditRecord.Diff,
            edit.DownstreamStale,
            invalidation);
    }

    internal TranscriptBridgeSnapshot WithSpeakerHistory(
        SpeakerAssignmentHistory? speakerHistory)
    {
        ValidateSpeakerHistory(
            Project,
            Revision,
            RevisionDigest,
            speakerHistory);
        return CreateValidated(
            Project,
            Revision,
            RevisionDigest,
            SourceSpans,
            TimingEvidence,
            DictionaryEntries,
            TermScope,
            ModelRunId,
            speakerHistory,
            TermCandidates,
            TermDecisions,
            Diff,
            DownstreamStale,
            Invalidation);
    }

    internal TranscriptBridgeSnapshot WithTermDecision(
        TermCandidateDecisionResult decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.TranscriptEdit is not null)
        {
            return FromTranscriptEdit(
                this,
                decision.TranscriptEdit,
                [.. TermDecisions, decision.DecisionRecord]);
        }

        if (decision.DecisionRecord.SourceRevisionId != Revision.Id ||
            decision.DecisionRecord.SourceRevisionDigest != RevisionDigest)
        {
            throw new ArgumentException(
                "The term decision does not apply to the registered snapshot.",
                nameof(decision));
        }

        TermCandidate[] remaining = TermCandidates
            .Where(candidate =>
                candidate.Suggestion.Id != decision.OriginalCandidate.Suggestion.Id)
            .ToArray();
        if (remaining.Length != TermCandidates.Count - 1)
        {
            throw new ArgumentException(
                "The decided term candidate is not registered.",
                nameof(decision));
        }

        return CreateValidated(
            Project,
            Revision,
            RevisionDigest,
            SourceSpans,
            TimingEvidence,
            DictionaryEntries,
            TermScope,
            ModelRunId,
            SpeakerHistory,
            remaining,
            [.. TermDecisions, decision.DecisionRecord],
            Diff,
            DownstreamStale,
            Invalidation);
    }

    internal TranscriptTimingEvidence GetTimingEvidence(
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<TranscriptTokenIdTag>? tokenId) =>
        TimingEvidence.Single(evidence =>
            evidence.UtteranceId == utteranceId &&
            evidence.TokenId == tokenId);

    private static TranscriptBridgeSnapshot CreateValidated(
        Project project,
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTimingEvidence> timingEvidence,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope,
        EntityId<ModelRunIdTag> modelRunId,
        SpeakerAssignmentHistory? speakerHistory,
        IReadOnlyList<TermCandidate> termCandidates,
        IReadOnlyList<TermCandidateDecisionRecord> termDecisions,
        TranscriptDiff? diff,
        TranscriptDownstreamStaleRecord? downstreamStale,
        AnalysisInvalidationPlan? invalidation)
    {
        ContentDigest actualDigest = ComputeRevisionDigest(revision);
        if (actualDigest != revisionDigest)
        {
            throw new ArgumentException(
                "The transcript revision digest is inconsistent.",
                nameof(revisionDigest));
        }

        SourceSpan[] spans = ValidateSourceSpans(revision, sourceSpans);
        TranscriptTimingEvidence[] timing = ValidateTimingEvidence(
            revision,
            spans,
            timingEvidence);
        TermEntry[] dictionary = ValidateDictionary(
            project,
            dictionaryEntries,
            termScope);
        ValidateSpeakerHistory(project, revision, revisionDigest, speakerHistory);
        TermCandidate[] candidates = ValidateCandidates(
            revision,
            revisionDigest,
            termScope,
            termCandidates);
        TermCandidateDecisionRecord[] decisions = ValidateDecisions(
            project,
            termDecisions);
        ValidateDiffAndInvalidation(
            revision,
            revisionDigest,
            diff,
            downstreamStale,
            invalidation);

        return new TranscriptBridgeSnapshot(
            project,
            revision,
            revisionDigest,
            Array.AsReadOnly(spans),
            Array.AsReadOnly(timing),
            Array.AsReadOnly(dictionary),
            termScope,
            modelRunId,
            speakerHistory,
            Array.AsReadOnly(candidates),
            Array.AsReadOnly(decisions),
            diff,
            downstreamStale,
            invalidation);
    }

    private static SourceSpan[] ValidateSourceSpans(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw new ArgumentException(
                "Each transcript utterance requires one source span.",
                nameof(sourceSpans));
        }

        SourceSpan[] copied = new SourceSpan[sourceSpans.Count];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw new ArgumentException(
                    "Transcript source spans must not contain null elements.",
                    nameof(sourceSpans));
            Utterance utterance = revision.Utterances[index];
            if (!ids.Add(span.Id) ||
                span.Id != utterance.SourceSpanId ||
                span.TranscriptRevisionId != revision.Id ||
                span.Range != utterance.Range ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw new ArgumentException(
                    "Transcript source spans do not match the revision.",
                    nameof(sourceSpans));
            }

            copied[index] = span;
        }

        return copied;
    }

    private static TranscriptTimingEvidence[] ValidateTimingEvidence(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTimingEvidence> timingEvidence)
    {
        int expectedCount = revision.Utterances.Count +
            revision.Utterances.Sum(static utterance => utterance.Tokens.Count);
        if (timingEvidence.Count != expectedCount)
        {
            throw new ArgumentException(
                "Timing evidence must cover each utterance and token exactly once.",
                nameof(timingEvidence));
        }

        var expected = new HashSet<TimingKey>();
        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            Utterance utterance = revision.Utterances[utteranceIndex];
            SourceSpan span = sourceSpans[utteranceIndex];
            _ = expected.Add(new TimingKey(utterance.Id, span.Id, TokenId: null));
            foreach (Token token in utterance.Tokens)
            {
                _ = expected.Add(new TimingKey(utterance.Id, span.Id, token.Id));
            }
        }

        TranscriptTimingEvidence[] copied =
            new TranscriptTimingEvidence[timingEvidence.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            TranscriptTimingEvidence evidence = timingEvidence[index]
                ?? throw new ArgumentException(
                    "Timing evidence must not contain null elements.",
                    nameof(timingEvidence));
            if (!expected.Remove(new TimingKey(
                    evidence.UtteranceId,
                    evidence.SourceSpanId,
                    evidence.TokenId)))
            {
                throw new ArgumentException(
                    "Timing evidence does not match the transcript hierarchy.",
                    nameof(timingEvidence));
            }

            copied[index] = evidence;
        }

        if (expected.Count != 0)
        {
            throw new ArgumentException(
                "Timing evidence does not cover the transcript hierarchy.",
                nameof(timingEvidence));
        }

        return copied;
    }

    private static TermEntry[] ValidateDictionary(
        Project project,
        IReadOnlyList<TermEntry> dictionaryEntries,
        TermScope termScope)
    {
        if (termScope.Kind == TermScopeKind.Project &&
            termScope.ProjectId != project.Id)
        {
            throw new ArgumentException(
                "The term scope belongs to a different project.",
                nameof(termScope));
        }

        var ids = new HashSet<EntityId<TermEntryIdTag>>();
        TermEntry[] copied = new TermEntry[dictionaryEntries.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            TermEntry entry = dictionaryEntries[index]
                ?? throw new ArgumentException(
                    "Dictionary entries must not contain null elements.",
                    nameof(dictionaryEntries));
            if (entry.Scope != termScope || !ids.Add(entry.Id))
            {
                throw new ArgumentException(
                    "Dictionary entries must be unique and use the registered scope.",
                    nameof(dictionaryEntries));
            }

            copied[index] = entry;
        }

        return copied;
    }

    private static TermCandidate[] ValidateCandidates(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        TermScope termScope,
        IReadOnlyList<TermCandidate> termCandidates)
    {
        var ids = new HashSet<EntityId<SuggestionIdTag>>();
        TermCandidate[] copied = new TermCandidate[termCandidates.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            TermCandidate candidate = termCandidates[index]
                ?? throw new ArgumentException(
                    "Term candidates must not contain null elements.",
                    nameof(termCandidates));
            if (!ids.Add(candidate.Suggestion.Id) ||
                candidate.SourceRevisionId != revision.Id ||
                candidate.SourceRevisionDigest != revisionDigest ||
                candidate.TermScope != termScope ||
                candidate.Suggestion.Status != SuggestionStatus.Proposed)
            {
                throw new ArgumentException(
                    "Term candidates do not match the registered snapshot.",
                    nameof(termCandidates));
            }

            copied[index] = candidate;
        }

        return copied;
    }

    private static TermCandidateDecisionRecord[] ValidateDecisions(
        Project project,
        IReadOnlyList<TermCandidateDecisionRecord> termDecisions)
    {
        _ = project;
        var ids = new HashSet<EntityId<SuggestionIdTag>>();
        TermCandidateDecisionRecord[] copied =
            new TermCandidateDecisionRecord[termDecisions.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            TermCandidateDecisionRecord decision = termDecisions[index]
                ?? throw new ArgumentException(
                    "Term decisions must not contain null elements.",
                    nameof(termDecisions));
            if (!ids.Add(decision.CandidateId))
            {
                throw new ArgumentException(
                    "A term candidate must not be decided more than once.",
                    nameof(termDecisions));
            }

            copied[index] = decision;
        }

        return copied;
    }

    private static void ValidateSpeakerHistory(
        Project project,
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        SpeakerAssignmentHistory? speakerHistory)
    {
        if (speakerHistory is not null &&
            (speakerHistory.ProjectId != project.Id ||
                speakerHistory.TranscriptRevisionId != revision.Id ||
                speakerHistory.TranscriptRevisionDigest != revisionDigest))
        {
            throw new ArgumentException(
                "Speaker assignment history does not match the transcript snapshot.",
                nameof(speakerHistory));
        }
    }

    private static void ValidateDiffAndInvalidation(
        TranscriptRevision revision,
        ContentDigest revisionDigest,
        TranscriptDiff? diff,
        TranscriptDownstreamStaleRecord? downstreamStale,
        AnalysisInvalidationPlan? invalidation)
    {
        if ((diff is null) != (downstreamStale is null) ||
            (diff is null) != (invalidation is null))
        {
            throw new ArgumentException(
                "Transcript diff, stale, and invalidation evidence must be paired.",
                nameof(diff));
        }

        if (diff is not null &&
            (diff.RevisionId != revision.Id ||
                diff.SourceRevisionId != revision.ParentRevisionId ||
                downstreamStale!.SourceRevisionId != diff.SourceRevisionId ||
                downstreamStale.RevisionId != diff.RevisionId ||
                downstreamStale.RevisionDigest != revisionDigest ||
                invalidation!.PreviousTranscriptDigest !=
                    downstreamStale.SourceRevisionDigest ||
                invalidation!.CurrentTranscriptDigest != revisionDigest ||
                !invalidation.HasTranscriptChange))
        {
            throw new ArgumentException(
                "Transcript diff and invalidation evidence are inconsistent.",
                nameof(diff));
        }
    }

    private static TranscriptTimingEvidence[] RebaseTimingEvidence(
        TranscriptBridgeSnapshot source,
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        var newSpanByUtterance = new Dictionary<
            EntityId<TranscriptUtteranceIdTag>,
            EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < revision.Utterances.Count; index++)
        {
            newSpanByUtterance.Add(
                revision.Utterances[index].Id,
                sourceSpans[index].Id);
        }

        return source.TimingEvidence
            .Select(evidence => new TranscriptTimingEvidence(
                evidence.UtteranceId,
                newSpanByUtterance[evidence.UtteranceId],
                evidence.TokenId,
                evidence.RawStartSeconds,
                evidence.RawEndSeconds,
                evidence.RoundedStartSample,
                evidence.RoundedEndSample,
                evidence.StartWasRounded,
                evidence.EndWasRounded,
                evidence.ReviewReasons))
            .ToArray();
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision revision) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(
            TranscriptSnapshotJson.WriteRevision(revision)));

    private sealed record TimingKey(
        EntityId<TranscriptUtteranceIdTag> UtteranceId,
        EntityId<SourceSpanIdTag> SourceSpanId,
        EntityId<TranscriptTokenIdTag>? TokenId);
}

internal sealed class TranscriptBridgeOperationResult
{
    private TranscriptBridgeOperationResult(
        TranscriptBridgeStatus status,
        TranscriptBridgeSnapshot? snapshot)
    {
        if ((status == TranscriptBridgeStatus.Available) != (snapshot is not null))
        {
            throw new ArgumentException(
                "The transcript bridge result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
    }

    internal TranscriptBridgeStatus Status { get; }

    internal TranscriptBridgeSnapshot? Snapshot { get; }

    internal static TranscriptBridgeOperationResult Available(
        TranscriptBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(TranscriptBridgeStatus.Available, snapshot);
    }

    internal static TranscriptBridgeOperationResult Forbidden() =>
        new(TranscriptBridgeStatus.Forbidden, snapshot: null);

    internal static TranscriptBridgeOperationResult Unavailable() =>
        new(TranscriptBridgeStatus.Unavailable, snapshot: null);

    internal static TranscriptBridgeOperationResult StaleBase() =>
        new(TranscriptBridgeStatus.StaleBase, snapshot: null);

    internal static TranscriptBridgeOperationResult Conflict() =>
        new(TranscriptBridgeStatus.Conflict, snapshot: null);

    internal static TranscriptBridgeOperationResult NoChange() =>
        new(TranscriptBridgeStatus.NoChange, snapshot: null);

    internal static TranscriptBridgeOperationResult Invalid() =>
        new(TranscriptBridgeStatus.Invalid, snapshot: null);
}

internal interface ITranscriptBridgeRuntime
{
    TranscriptBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken);
}

internal sealed class TranscriptBridgeRuntime : ITranscriptBridgeRuntime
{
    private readonly object gate = new();
    private readonly Dictionary<
        EntityId<ProjectIdTag>,
        TranscriptBridgeSnapshot> snapshots = [];
    private readonly Func<DateTimeOffset> getCurrentTime;
    private readonly Func<Guid> createId;

    internal TranscriptBridgeRuntime()
        : this(
            static () => DateTimeOffset.UtcNow,
            static () => Guid.NewGuid())
    {
    }

    internal TranscriptBridgeRuntime(
        Func<DateTimeOffset> getCurrentTime,
        Func<Guid> createId)
    {
        ArgumentNullException.ThrowIfNull(getCurrentTime);
        ArgumentNullException.ThrowIfNull(createId);
        this.getCurrentTime = getCurrentTime;
        this.createId = createId;
    }

    internal void Register(TranscriptBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            snapshots[snapshot.Project.Id] = snapshot;
        }
    }

    internal TranscriptBridgeSnapshot GetRequired(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        lock (gate)
        {
            return snapshots.TryGetValue(projectId, out TranscriptBridgeSnapshot? snapshot)
                ? snapshot
                : throw new KeyNotFoundException(
                    "No transcript snapshot is registered for the project.");
        }
    }

    public TranscriptBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            EntityId<ProjectIdTag> projectId = ParseEntity<ProjectIdTag>(
                request.Parameters.GetProperty("projectId"));
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!snapshots.TryGetValue(
                        projectId,
                        out TranscriptBridgeSnapshot? snapshot))
                {
                    return TranscriptBridgeOperationResult.Unavailable();
                }

                TranscriptBridgeOperationResult result = request.Method switch
                {
                    BridgeMethod.TranscriptRead =>
                        TranscriptBridgeOperationResult.Available(snapshot),
                    BridgeMethod.TranscriptEditToken => EditToken(
                        request,
                        snapshot,
                        actorSid,
                        cancellationToken),
                    BridgeMethod.TranscriptSetSpeaker => SetSpeaker(
                        request,
                        snapshot,
                        actorSid),
                    BridgeMethod.TranscriptRemoveSpeaker => RemoveSpeaker(
                        request,
                        snapshot,
                        actorSid),
                    BridgeMethod.TranscriptRejectTerm => DecideTerm(
                        request,
                        snapshot,
                        actorSid,
                        adopt: false),
                    BridgeMethod.TranscriptAdoptTerm => DecideTerm(
                        request,
                        snapshot,
                        actorSid,
                        adopt: true),
                    _ => TranscriptBridgeOperationResult.Invalid(),
                };
                if (result.Snapshot is not null)
                {
                    if (!TranscriptBridgeJson.CanWriteResponse(request, result))
                    {
                        return TranscriptBridgeOperationResult.Unavailable();
                    }

                    snapshots[projectId] = result.Snapshot;
                }

                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TranscriptEditorException exception)
        {
            return exception.Reason switch
            {
                TranscriptEditorFailureReason.SourceTextChanged =>
                    TranscriptBridgeOperationResult.Conflict(),
                TranscriptEditorFailureReason.NoChanges =>
                    TranscriptBridgeOperationResult.NoChange(),
                _ => TranscriptBridgeOperationResult.Invalid(),
            };
        }
        catch (SpeakerAssignmentException exception)
        {
            return exception.Reason switch
            {
                SpeakerAssignmentFailureReason.NoChange =>
                    TranscriptBridgeOperationResult.NoChange(),
                SpeakerAssignmentFailureReason.RevisionConflict or
                SpeakerAssignmentFailureReason.ContextMismatch =>
                    TranscriptBridgeOperationResult.Conflict(),
                _ => TranscriptBridgeOperationResult.Invalid(),
            };
        }
        catch (TermCandidateException exception)
        {
            return exception.Reason switch
            {
                TermCandidateFailureReason.CandidateStale =>
                    TranscriptBridgeOperationResult.StaleBase(),
                TermCandidateFailureReason.CandidateAlreadyDecided or
                TermCandidateFailureReason.TermEntryChanged =>
                    TranscriptBridgeOperationResult.Conflict(),
                _ => TranscriptBridgeOperationResult.Invalid(),
            };
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or
                InvalidOperationException or KeyNotFoundException or
                OverflowException or JsonException)
        {
            return TranscriptBridgeOperationResult.Invalid();
        }
    }

    private TranscriptBridgeOperationResult EditToken(
        BridgeRequest request,
        TranscriptBridgeSnapshot snapshot,
        string actorSid,
        CancellationToken cancellationToken)
    {
        TranscriptBridgeOperationResult? mismatch = CheckTranscriptOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        EntityId<TranscriptTokenIdTag> tokenId =
            ParseEntity<TranscriptTokenIdTag>(
                request.Parameters.GetProperty("tokenId"));
        string expectedText = GetRequiredText(
            request.Parameters,
            "expectedText");
        string revisedText = GetRequiredText(
            request.Parameters,
            "revisedText");
        string reason = GetRequiredText(request.Parameters, "reason");
        Token? token = snapshot.Revision.Utterances
            .SelectMany(static utterance => utterance.Tokens)
            .SingleOrDefault(candidate => candidate.Id == tokenId);
        if (token is null)
        {
            return TranscriptBridgeOperationResult.Invalid();
        }

        if (!string.Equals(token.Text, expectedText, StringComparison.Ordinal))
        {
            return TranscriptBridgeOperationResult.Conflict();
        }

        if (string.Equals(expectedText, revisedText, StringComparison.Ordinal))
        {
            return TranscriptBridgeOperationResult.NoChange();
        }

        cancellationToken.ThrowIfCancellationRequested();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            snapshot.Revision,
            snapshot.SourceSpans,
            [new TranscriptTokenTextEdit(tokenId, expectedText, revisedText)],
            CreateEntityId<TranscriptRevisionIdTag>(),
            actorSid,
            getCurrentTime(),
            reason);
        cancellationToken.ThrowIfCancellationRequested();
        return TranscriptBridgeOperationResult.Available(
            TranscriptBridgeSnapshot.FromTranscriptEdit(
                snapshot,
                edit,
                snapshot.TermDecisions));
    }

    private TranscriptBridgeOperationResult SetSpeaker(
        BridgeRequest request,
        TranscriptBridgeSnapshot snapshot,
        string actorSid)
    {
        TranscriptBridgeOperationResult? mismatch = CheckTranscriptOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        if (!SpeakerRevisionMatches(request.Parameters, snapshot))
        {
            return TranscriptBridgeOperationResult.Conflict();
        }

        SpeakerAssignmentHistory history = SpeakerAssignmentService.SetAssignment(
            snapshot.Project,
            snapshot.Revision,
            snapshot.SpeakerHistory,
            CreateEntityId<SpeakerAssignmentRevisionIdTag>(),
            ParseEntity<TranscriptSpeakerIdTag>(
                request.Parameters.GetProperty("speakerId")),
            ParseEntity<PerformerIdTag>(
                request.Parameters.GetProperty("performerId")),
            GetRequiredText(request.Parameters, "surname"),
            ParseDisplayFormat(GetRequiredText(
                request.Parameters,
                "displayFormat")),
            actorSid,
            getCurrentTime(),
            GetRequiredText(request.Parameters, "reason"));
        return TranscriptBridgeOperationResult.Available(
            snapshot.WithSpeakerHistory(history));
    }

    private TranscriptBridgeOperationResult RemoveSpeaker(
        BridgeRequest request,
        TranscriptBridgeSnapshot snapshot,
        string actorSid)
    {
        TranscriptBridgeOperationResult? mismatch = CheckTranscriptOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        if (!SpeakerRevisionMatches(request.Parameters, snapshot) ||
            snapshot.SpeakerHistory is null)
        {
            return TranscriptBridgeOperationResult.Conflict();
        }

        SpeakerAssignmentHistory history =
            SpeakerAssignmentService.RemoveAssignment(
                snapshot.Project,
                snapshot.Revision,
                snapshot.SpeakerHistory,
                CreateEntityId<SpeakerAssignmentRevisionIdTag>(),
                ParseEntity<TranscriptSpeakerIdTag>(
                    request.Parameters.GetProperty("speakerId")),
                actorSid,
                getCurrentTime(),
                GetRequiredText(request.Parameters, "reason"));
        return TranscriptBridgeOperationResult.Available(
            snapshot.WithSpeakerHistory(history));
    }

    private TranscriptBridgeOperationResult DecideTerm(
        BridgeRequest request,
        TranscriptBridgeSnapshot snapshot,
        string actorSid,
        bool adopt)
    {
        TranscriptBridgeOperationResult? mismatch = CheckTranscriptOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        EntityId<SuggestionIdTag> candidateId = ParseEntity<SuggestionIdTag>(
            request.Parameters.GetProperty("candidateId"));
        EntityId<TermEntryIdTag> termEntryId = ParseEntity<TermEntryIdTag>(
            request.Parameters.GetProperty("termEntryId"));
        int termEntryVersion = ParseNonnegativeInt32(
            request.Parameters.GetProperty("termEntryVersion"));
        TermCandidate? candidate = snapshot.TermCandidates.SingleOrDefault(
            item => item.Suggestion.Id == candidateId);
        if (candidate is null ||
            candidate.TermEntryId != termEntryId ||
            candidate.TermEntryVersion != termEntryVersion)
        {
            return TranscriptBridgeOperationResult.Conflict();
        }

        TermEntry? entry = snapshot.DictionaryEntries.SingleOrDefault(
            item => item.Id == termEntryId &&
                item.Version == termEntryVersion);
        if (entry is null)
        {
            return TranscriptBridgeOperationResult.Conflict();
        }

        DateTimeOffset now = getCurrentTime();
        string reason = GetRequiredText(request.Parameters, "reason");
        TermCandidateDecisionResult decision = adopt
            ? TermCandidateService.AdoptCandidate(
                candidate,
                snapshot.Revision,
                snapshot.SourceSpans,
                entry,
                CreateEntityId<TranscriptRevisionIdTag>(),
                actorSid,
                now,
                reason)
            : TermCandidateService.RejectCandidate(
                candidate,
                snapshot.Revision,
                snapshot.SourceSpans,
                entry,
                actorSid,
                now,
                reason);
        return TranscriptBridgeOperationResult.Available(
            snapshot.WithTermDecision(decision));
    }

    private static TranscriptBridgeOperationResult? CheckTranscriptOcc(
        JsonElement parameters,
        TranscriptBridgeSnapshot snapshot)
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            ParseEntity<TranscriptRevisionIdTag>(
                parameters.GetProperty("revisionId"));
        ContentDigest digest = ParseDigest(parameters.GetProperty("digest"));
        return revisionId != snapshot.Revision.Id ||
            digest != snapshot.RevisionDigest
                ? TranscriptBridgeOperationResult.StaleBase()
                : null;
    }

    private static bool SpeakerRevisionMatches(
        JsonElement parameters,
        TranscriptBridgeSnapshot snapshot)
    {
        JsonElement expected = parameters.GetProperty(
            "expectedSpeakerRevisionId");
        EntityId<SpeakerAssignmentRevisionIdTag>? expectedId =
            expected.ValueKind == JsonValueKind.Null
                ? null
                : ParseEntity<SpeakerAssignmentRevisionIdTag>(expected);
        return expectedId == snapshot.SpeakerHistory?.CurrentRevision.Id;
    }

    private EntityId<TTag> CreateEntityId<TTag>()
    {
        Guid value = createId();
        if (value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The generated bridge entity ID must not be empty.");
        }

        return EntityId<TTag>.Parse(value.ToString("D"));
    }

    private static EntityId<TTag> ParseEntity<TTag>(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !EntityId<TTag>.TryParse(
                value.GetString(),
                out EntityId<TTag>? entityId) ||
            !string.Equals(
                value.GetString(),
                entityId.ToString(),
                StringComparison.Ordinal))
        {
            throw new FormatException("The bridge entity ID is invalid.");
        }

        return entityId;
    }

    private static ContentDigest ParseDigest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !ContentDigest.TryParse(value.GetString(), out ContentDigest? digest) ||
            !string.Equals(
                value.GetString(),
                digest.Hex,
                StringComparison.Ordinal))
        {
            throw new FormatException("The bridge digest is invalid.");
        }

        return digest;
    }

    private static int ParseNonnegativeInt32(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed is < 0 or > int.MaxValue)
        {
            throw new FormatException("The bridge integer is invalid.");
        }

        return decimal.ToInt32(parsed);
    }

    private static string GetRequiredText(
        JsonElement parameters,
        string propertyName)
    {
        JsonElement value = parameters.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text ||
            string.IsNullOrWhiteSpace(text) ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("The bridge text is invalid.");
        }

        return text;
    }

    private static SpeakerDisplayFormat ParseDisplayFormat(string value) =>
        value switch
        {
            "fullName" => SpeakerDisplayFormat.FullName,
            "surname" => SpeakerDisplayFormat.Surname,
            "role" => SpeakerDisplayFormat.Role,
            "hidden" => SpeakerDisplayFormat.Hidden,
            _ => throw new FormatException(
                "The speaker display format is invalid."),
        };
}

internal interface ITranscriptBridgeAuthorization
{
    string DemandRead();

    string DemandMutation();
}

internal sealed class TranscriptBridgeAuthorization :
    ITranscriptBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal TranscriptBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorSidProvider(actorProvider))
    {
    }

    internal TranscriptBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public string DemandRead()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.ViewProject);
        return actorSid;
    }

    public string DemandMutation()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.DecideAnalysisSuggestion);
        return actorSid;
    }

    private static Func<string> GetActorSidProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class TranscriptBridgeRequestHandler
{
    private readonly ITranscriptBridgeRuntime runtime;
    private readonly ITranscriptBridgeAuthorization authorization;

    internal TranscriptBridgeRequestHandler(
        ITranscriptBridgeRuntime runtime,
        ITranscriptBridgeAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(authorization);
        this.runtime = runtime;
        this.authorization = authorization;
    }

    internal Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        TranscriptBridgeOperationResult result;
        try
        {
            string actorSid = request.Method == BridgeMethod.TranscriptRead
                ? authorization.DemandRead()
                : authorization.DemandMutation();
            result = runtime.Execute(request, actorSid, cancellationToken) ??
                TranscriptBridgeOperationResult.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthorizationException)
        {
            result = TranscriptBridgeOperationResult.Forbidden();
        }
        catch (Exception)
        {
            result = TranscriptBridgeOperationResult.Unavailable();
        }

        return Task.FromResult(TranscriptBridgeJson.WriteResponse(
            request,
            result));
    }
}

internal static class TranscriptBridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteResponse(
        BridgeRequest request,
        TranscriptBridgeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == TranscriptBridgeStatus.Available)
        {
            return WriteResponseCore(
                request,
                TranscriptBridgeOperationResult.Unavailable());
        }
    }

    internal static bool CanWriteResponse(
        BridgeRequest request,
        TranscriptBridgeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            _ = WriteResponseCore(request, result);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string WriteResponseCore(
        BridgeRequest request,
        TranscriptBridgeOperationResult result)
    {
        var buffer = new PlayerBridgeJson.BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString("method", BridgeProtocol.GetMethodName(request.Method));
        writer.WriteStartObject("result");
        writer.WriteString("status", GetStatusText(result.Status));
        writer.WritePropertyName("snapshot");
        if (result.Snapshot is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteSnapshot(writer, result.Snapshot);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        TranscriptBridgeSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("projectId", snapshot.Project.Id.ToString());
        writer.WriteString("revisionId", snapshot.Revision.Id.ToString());
        writer.WriteString("revisionDigest", snapshot.RevisionDigest.Hex);
        if (snapshot.Revision.ParentRevisionId is null)
        {
            writer.WriteNull("parentRevisionId");
        }
        else
        {
            writer.WriteString(
                "parentRevisionId",
                snapshot.Revision.ParentRevisionId.ToString());
        }

        writer.WriteString(
            "changeSource",
            snapshot.Revision.ChangeSource == TranscriptChangeSource.Machine
                ? "machine"
                : "human");
        writer.WriteString(
            "createdAt",
            snapshot.Revision.CreatedAt.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        if (snapshot.SpeakerHistory is null)
        {
            writer.WriteNull("speakerRevisionId");
        }
        else
        {
            writer.WriteString(
                "speakerRevisionId",
                snapshot.SpeakerHistory.CurrentRevision.Id.ToString());
        }

        WritePerformers(writer, snapshot.Project.Performers);
        writer.WriteStartArray("utterances");
        foreach (Utterance utterance in snapshot.Revision.Utterances)
        {
            WriteUtterance(writer, snapshot, utterance);
        }

        writer.WriteEndArray();
        WriteTermCandidates(writer, snapshot.TermCandidates);
        WriteTermDecisions(writer, snapshot.TermDecisions);
        WriteDiff(writer, snapshot.Diff);
        WriteStaleRange(writer, snapshot.DownstreamStale);
        WriteInvalidation(writer, snapshot.Invalidation);
        writer.WriteEndObject();
    }

    private static void WritePerformers(
        Utf8JsonWriter writer,
        IReadOnlyList<Performer> performers)
    {
        writer.WriteStartArray("performers");
        foreach (Performer performer in performers)
        {
            writer.WriteStartObject();
            writer.WriteString("id", performer.Id.ToString());
            writer.WriteString("displayName", performer.DisplayName);
            writer.WriteString("role", performer.Role);
            WriteNullableTimestamp(
                writer,
                "titleConfirmedAt",
                performer.TitleConfirmedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteUtterance(
        Utf8JsonWriter writer,
        TranscriptBridgeSnapshot snapshot,
        Utterance utterance)
    {
        TranscriptTimingEvidence utteranceTiming = snapshot.GetTimingEvidence(
            utterance.Id,
            tokenId: null);
        ResolvedSpeakerDisplay speaker = SpeakerAssignmentService.ResolveDisplay(
            snapshot.Project,
            snapshot.Revision,
            snapshot.SpeakerHistory,
            utterance.SpeakerId);
        writer.WriteStartObject();
        writer.WriteString("id", utterance.Id.ToString());
        writer.WriteString("sourceSpanId", utterance.SourceSpanId.ToString());
        writer.WriteString("text", utterance.Text);
        writer.WritePropertyName("range");
        WriteTimeRange(writer, utterance.Range);
        WriteNullableNumber(
            writer,
            "recognitionConfidence",
            utterance.RecognitionConfidence);
        writer.WriteBoolean(
            "manualTimingAdjustmentRequired",
            utteranceTiming.RequiresReview);
        WriteTimingReasons(writer, utteranceTiming.ReviewReasons);
        writer.WritePropertyName("speaker");
        WriteSpeaker(writer, speaker);
        writer.WriteStartArray("sentences");
        foreach (Sentence sentence in utterance.Sentences)
        {
            writer.WriteStartObject();
            writer.WriteString("id", sentence.Id.ToString());
            writer.WriteString("text", sentence.Text);
            writer.WriteStartArray("tokens");
            foreach (Token token in sentence.Tokens)
            {
                WriteToken(writer, snapshot, utterance, token);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSpeaker(
        Utf8JsonWriter writer,
        ResolvedSpeakerDisplay speaker)
    {
        writer.WriteStartObject();
        writer.WriteString("speakerId", speaker.SpeakerId.ToString());
        if (speaker.PerformerId is null)
        {
            writer.WriteNull("performerId");
        }
        else
        {
            writer.WriteString("performerId", speaker.PerformerId.ToString());
        }

        if (speaker.Format is null)
        {
            writer.WriteNull("format");
        }
        else
        {
            writer.WriteString("format", GetDisplayFormatText(speaker.Format.Value));
        }

        writer.WriteBoolean("isAssigned", speaker.IsAssigned);
        writer.WriteBoolean("isVisible", speaker.IsVisible);
        if (speaker.Text is null)
        {
            writer.WriteNull("text");
        }
        else
        {
            writer.WriteString("text", speaker.Text);
        }

        WriteNullableTimestamp(
            writer,
            "titleConfirmedAt",
            speaker.TitleConfirmedAt);
        writer.WriteBoolean(
            "titleOrAffiliationConfirmationRequired",
            speaker.TitleOrAffiliationConfirmationRequired);
        writer.WriteEndObject();
    }

    private static void WriteToken(
        Utf8JsonWriter writer,
        TranscriptBridgeSnapshot snapshot,
        Utterance utterance,
        Token token)
    {
        TranscriptTimingEvidence evidence = snapshot.GetTimingEvidence(
            utterance.Id,
            token.Id);
        TimeRange effectiveRange = token.Range ?? utterance.Range;
        writer.WriteStartObject();
        writer.WriteString("id", token.Id.ToString());
        writer.WriteString("text", token.Text);
        writer.WritePropertyName("range");
        if (token.Range is TimeRange range)
        {
            WriteTimeRange(writer, range);
        }
        else
        {
            writer.WriteNullValue();
        }

        WriteNullableNumber(
            writer,
            "recognitionConfidence",
            token.RecognitionConfidence);
        writer.WriteBoolean(
            "manualTimingAdjustmentRequired",
            token.ManualTimingAdjustmentRequired || evidence.RequiresReview);
        WriteTimingReasons(writer, evidence.ReviewReasons);
        writer.WritePropertyName("effectiveSeekSourceTime");
        WriteRationalTime(writer, effectiveRange.Start);
        writer.WriteBoolean(
            "seekUsesUtteranceRange",
            token.Range is null);
        writer.WriteEndObject();
    }

    private static void WriteTermCandidates(
        Utf8JsonWriter writer,
        IReadOnlyList<TermCandidate> candidates)
    {
        writer.WriteStartArray("termCandidates");
        foreach (TermCandidate candidate in candidates)
        {
            writer.WriteStartObject();
            writer.WriteString("candidateId", candidate.Suggestion.Id.ToString());
            writer.WriteString("sourceSpanId", candidate.SourceSpanId.ToString());
            writer.WriteString("tokenId", candidate.TokenId.ToString());
            writer.WriteString("termEntryId", candidate.TermEntryId.ToString());
            writer.WriteNumber("termEntryVersion", candidate.TermEntryVersion);
            writer.WriteString("dictionaryReading", candidate.DictionaryReading);
            writer.WriteString("observedText", candidate.ObservedText);
            writer.WriteString("proposedText", candidate.ProposedText);
            writer.WriteString(
                "matchKind",
                candidate.MatchKind switch
                {
                    TermCandidateMatchKind.WrittenFormCompatibilityVariant =>
                        "writtenFormCompatibilityVariant",
                    TermCandidateMatchKind.WrittenFormCaseVariant =>
                        "writtenFormCaseVariant",
                    TermCandidateMatchKind.DictionaryReading =>
                        "dictionaryReading",
                    _ => throw new InvalidOperationException(
                        "Unknown term-candidate match kind."),
                });
            WriteNullableNumber(
                writer,
                "confidence",
                candidate.Suggestion.Confidence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTermDecisions(
        Utf8JsonWriter writer,
        IReadOnlyList<TermCandidateDecisionRecord> decisions)
    {
        writer.WriteStartArray("termDecisions");
        foreach (TermCandidateDecisionRecord decision in decisions)
        {
            writer.WriteStartObject();
            writer.WriteString("candidateId", decision.CandidateId.ToString());
            writer.WriteString(
                "decision",
                decision.Decision == TermCandidateDecisionKind.Adopted
                    ? "adopted"
                    : "rejected");
            writer.WriteString(
                "sourceRevisionId",
                decision.SourceRevisionId.ToString());
            writer.WriteString(
                "sourceRevisionDigest",
                decision.SourceRevisionDigest.Hex);
            writer.WriteString("tokenId", decision.TokenId.ToString());
            writer.WriteString("termEntryId", decision.TermEntryId.ToString());
            writer.WriteNumber("termEntryVersion", decision.TermEntryVersion);
            writer.WriteString(
                "decidedAt",
                decision.DecidedAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
            writer.WriteString("reason", decision.Reason);
            if (decision.ResultingRevisionId is null)
            {
                writer.WriteNull("resultingRevisionId");
            }
            else
            {
                writer.WriteString(
                    "resultingRevisionId",
                    decision.ResultingRevisionId.ToString());
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDiff(
        Utf8JsonWriter writer,
        TranscriptDiff? diff)
    {
        writer.WriteStartArray("diff");
        if (diff is not null)
        {
            foreach (TranscriptTextChange change in diff.Changes)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "kind",
                    change.Kind switch
                    {
                        TranscriptTextChangeKind.Utterance => "utterance",
                        TranscriptTextChangeKind.Sentence => "sentence",
                        TranscriptTextChangeKind.Token => "token",
                        _ => throw new InvalidOperationException(
                            "Unknown transcript change kind."),
                    });
                writer.WriteString("utteranceId", change.UtteranceId.ToString());
                if (change.SentenceId is null)
                {
                    writer.WriteNull("sentenceId");
                }
                else
                {
                    writer.WriteString("sentenceId", change.SentenceId.ToString());
                }

                if (change.TokenId is null)
                {
                    writer.WriteNull("tokenId");
                }
                else
                {
                    writer.WriteString("tokenId", change.TokenId.ToString());
                }

                writer.WriteString("previousText", change.PreviousText);
                writer.WriteString("currentText", change.CurrentText);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteStaleRange(
        Utf8JsonWriter writer,
        TranscriptDownstreamStaleRecord? stale)
    {
        writer.WritePropertyName("staleRange");
        if (stale is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("sourceRevisionId", stale.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            stale.SourceRevisionDigest.Hex);
        writer.WriteString("revisionId", stale.RevisionId.ToString());
        writer.WriteString("revisionDigest", stale.RevisionDigest.Hex);
        writer.WriteEndObject();
    }

    private static void WriteInvalidation(
        Utf8JsonWriter writer,
        AnalysisInvalidationPlan? invalidation)
    {
        writer.WriteStartObject("invalidation");
        WriteDependencyNodes(
            writer,
            "staleNodes",
            invalidation?.StaleNodes ?? []);
        WriteDependencyNodes(
            writer,
            "recomputationNodes",
            invalidation?.RecomputationNodes ?? []);
        WriteDependencyNodes(
            writer,
            "manualRebindingNodes",
            invalidation?.ManualRebindingNodes ?? []);
        writer.WriteEndObject();
    }

    private static void WriteDependencyNodes(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<AnalysisDependencyNodeKind> nodes)
    {
        writer.WriteStartArray(propertyName);
        foreach (AnalysisDependencyNodeKind node in nodes)
        {
            writer.WriteStringValue(node switch
            {
                AnalysisDependencyNodeKind.TermCandidates => "termCandidates",
                AnalysisDependencyNodeKind.EditorialPhraseCandidates =>
                    "editorialPhraseCandidates",
                AnalysisDependencyNodeKind.RepetitionCandidates =>
                    "repetitionCandidates",
                AnalysisDependencyNodeKind.TopicBoundaryCandidates =>
                    "topicBoundaryCandidates",
                AnalysisDependencyNodeKind.TopicRangeBinding =>
                    "topicRangeBinding",
                AnalysisDependencyNodeKind.ContextDeviationCandidates =>
                    "contextDeviationCandidates",
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding =>
                    "speakerAssignmentBinding",
                AnalysisDependencyNodeKind.TopicMetadata => "topicMetadata",
                AnalysisDependencyNodeKind.EmphasisCandidates =>
                    "emphasisCandidates",
                _ => throw new InvalidOperationException(
                    "Unknown transcript invalidation node."),
            });
        }

        writer.WriteEndArray();
    }

    private static void WriteTimingReasons(
        Utf8JsonWriter writer,
        TranscriptTimingReviewReason reasons)
    {
        writer.WriteStartArray("timingReviewReasons");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.WorkerReported,
            "workerReported");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.MissingRawTiming,
            "missingRawTiming");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.NonPositiveRawDuration,
            "nonPositiveRawDuration");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.OutsideUtterance,
            "outsideUtterance");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.OutsideAnalysisAudio,
            "outsideAnalysisAudio");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.NonPositiveRoundedDuration,
            "nonPositiveRoundedDuration");
        WriteTimingReason(
            writer,
            reasons,
            TranscriptTimingReviewReason.OverlapsPreviousTimedToken,
            "overlapsPreviousTimedToken");
        writer.WriteEndArray();
    }

    private static void WriteTimingReason(
        Utf8JsonWriter writer,
        TranscriptTimingReviewReason allReasons,
        TranscriptTimingReviewReason reason,
        string text)
    {
        if ((allReasons & reason) != 0)
        {
            writer.WriteStringValue(text);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        double? value)
    {
        if (value is double number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            writer.WriteString(
                propertyName,
                timestamp.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteTimeRange(
        Utf8JsonWriter writer,
        TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static string GetDisplayFormatText(SpeakerDisplayFormat format) =>
        format switch
        {
            SpeakerDisplayFormat.FullName => "fullName",
            SpeakerDisplayFormat.Surname => "surname",
            SpeakerDisplayFormat.Role => "role",
            SpeakerDisplayFormat.Hidden => "hidden",
            _ => throw new InvalidOperationException(
                "Unknown speaker display format."),
        };

    private static string GetStatusText(TranscriptBridgeStatus status) =>
        status switch
        {
            TranscriptBridgeStatus.Available => "available",
            TranscriptBridgeStatus.Forbidden => "forbidden",
            TranscriptBridgeStatus.Unavailable => "unavailable",
            TranscriptBridgeStatus.StaleBase => "staleBase",
            TranscriptBridgeStatus.Conflict => "conflict",
            TranscriptBridgeStatus.NoChange => "noChange",
            TranscriptBridgeStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown transcript bridge status."),
        };
}
