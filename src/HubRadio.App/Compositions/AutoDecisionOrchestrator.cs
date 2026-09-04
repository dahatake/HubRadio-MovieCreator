using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Compositions;

public enum AutoDecisionFailureReason
{
    ParentMismatch,
    SettingsMismatch,
    TranscriptMismatch,
    EvidenceMissing,
    EvidenceStale,
    UnknownRule,
    AmbiguousEvidence,
    DuplicateConflict,
    RangeInvalid,
    OverlapConflict,
    ModelRunInvalid,
}

public sealed class AutoDecisionException : Exception
{
    internal AutoDecisionException(AutoDecisionFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public AutoDecisionFailureReason Reason { get; }

    private static string GetStableMessage(AutoDecisionFailureReason reason) =>
        reason switch
        {
            AutoDecisionFailureReason.ParentMismatch =>
                "The auto-decision parent composition is not current.",
            AutoDecisionFailureReason.SettingsMismatch =>
                "The auto-decision settings snapshot does not contain the closed rule set.",
            AutoDecisionFailureReason.TranscriptMismatch =>
                "The auto-decision transcript evidence is inconsistent.",
            AutoDecisionFailureReason.EvidenceMissing =>
                "Required auto-decision evidence is missing.",
            AutoDecisionFailureReason.EvidenceStale =>
                "The auto-decision evidence is stale.",
            AutoDecisionFailureReason.UnknownRule =>
                "The candidate is not one of the three closed auto-decision rules.",
            AutoDecisionFailureReason.AmbiguousEvidence =>
                "The candidate evidence requires a non-deterministic judgment.",
            AutoDecisionFailureReason.DuplicateConflict =>
                "The auto-decision input contains duplicate evidence.",
            AutoDecisionFailureReason.RangeInvalid =>
                "The auto-decision range cannot be mapped exactly to the parent composition.",
            AutoDecisionFailureReason.OverlapConflict =>
                "Auto-decision deletion ranges overlap.",
            AutoDecisionFailureReason.ModelRunInvalid =>
                "The candidate model-run evidence is unavailable or unsuccessful.",
            _ => "The auto-decision preparation failed.",
        };
}

public static class AutoDecisionRule
{
    public const string ExplicitCut = "fr005.explicit-cut";
    public const string AdjacentExactRepetition = "fr005.adjacent-exact-repetition";
    public const string HeadTailSilence = "fr005.head-tail-silence";
}

public sealed class AutoDecisionRequest
{
    public AutoDecisionRequest(
        CompositionVersion parent,
        WorkingCopy workingCopy,
        int expectedWorkingCopyRevision,
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<ModelRun> modelRuns,
        IReadOnlyList<SuggestionCandidate> candidates,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        string actorSid,
        DateTimeOffset createdAt,
        string changeReason)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        WorkingCopy = workingCopy ?? throw new ArgumentNullException(nameof(workingCopy));
        SettingsProfile = settingsProfile
            ?? throw new ArgumentNullException(nameof(settingsProfile));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        SourceSpans = Copy(sourceSpans, nameof(sourceSpans));
        ModelRuns = Copy(modelRuns, nameof(modelRuns));
        Candidates = Copy(candidates, nameof(candidates));
        DecisionHistory = Copy(decisionHistory, nameof(decisionHistory));
        ActorSid = actorSid ?? throw new ArgumentNullException(nameof(actorSid));
        ChangeReason = changeReason ?? throw new ArgumentNullException(nameof(changeReason));
        ExpectedWorkingCopyRevision = expectedWorkingCopyRevision;
        CreatedAt = createdAt;
    }

    public CompositionVersion Parent { get; }

    public WorkingCopy WorkingCopy { get; }

    public int ExpectedWorkingCopyRevision { get; }

    public DefaultSettingsProfile SettingsProfile { get; }

    public DefaultSettingsSnapshot Settings { get; }

    public TranscriptRevision Transcript { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public IReadOnlyList<ModelRun> ModelRuns { get; }

    public IReadOnlyList<SuggestionCandidate> Candidates { get; }

    public IReadOnlyList<SuggestionDecisionRecord> DecisionHistory { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string ChangeReason { get; }

    private static IReadOnlyList<T> Copy<T>(
        IReadOnlyList<T> values,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = new T[values.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            copied[index] = values[index] ?? throw new ArgumentException(
                "Auto-decision inputs must not contain null elements.",
                parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}

public sealed class AutoDecisionEvidence
{
    internal AutoDecisionEvidence(
        string ruleId,
        string ruleVersion,
        ContentDigest ruleDigest,
        ContentDigest decisionDigest,
        EntityId<SuggestionIdTag> candidateId,
        EntityId<ModelRunIdTag> modelRunId,
        ContentDigest modelRunDigest,
        SourceSpan sourceSpan,
        EntityId<SourceSpanIdTag>? comparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> comparisonTokenIds,
        TimeRange deletedSourceRange,
        EntityId<CompositionVersionIdTag> parentVersionId,
        ContentDigest parentCompositionDigest,
        EntityId<DefaultSettingsProfileIdTag> settingsProfileId,
        SettingsProfileVersion settingsProfileVersion,
        ContentDigest settingsProfileDigest,
        ContentDigest settingsSnapshotDigest,
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
        ContentDigest transcriptDigest,
        string actorSid,
        DateTimeOffset decidedAt)
    {
        RuleId = ruleId;
        RuleVersion = ruleVersion;
        RuleDigest = ruleDigest;
        DecisionDigest = decisionDigest;
        CandidateId = candidateId;
        ModelRunId = modelRunId;
        ModelRunDigest = modelRunDigest;
        SourceSpan = sourceSpan;
        ComparisonSourceSpanId = comparisonSourceSpanId;
        ComparisonTokenIds = Array.AsReadOnly(comparisonTokenIds.ToArray());
        DeletedSourceRange = deletedSourceRange;
        ParentVersionId = parentVersionId;
        ParentCompositionDigest = parentCompositionDigest;
        SettingsProfileId = settingsProfileId;
        SettingsProfileVersion = settingsProfileVersion;
        SettingsProfileDigest = settingsProfileDigest;
        SettingsSnapshotDigest = settingsSnapshotDigest;
        TranscriptRevisionId = transcriptRevisionId;
        TranscriptDigest = transcriptDigest;
        ActorSid = actorSid;
        DecidedAt = decidedAt;
    }

    public string RuleId { get; }

    public string RuleVersion { get; }

    public ContentDigest RuleDigest { get; }

    public ContentDigest DecisionDigest { get; }

    public EntityId<SuggestionIdTag> CandidateId { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public ContentDigest ModelRunDigest { get; }

    public SourceSpan SourceSpan { get; }

    public EntityId<SourceSpanIdTag>? ComparisonSourceSpanId { get; }

    public IReadOnlyList<EntityId<TranscriptTokenIdTag>> ComparisonTokenIds { get; }

    public TimeRange DeletedSourceRange { get; }

    public EntityId<CompositionVersionIdTag> ParentVersionId { get; }

    public ContentDigest ParentCompositionDigest { get; }

    public EntityId<DefaultSettingsProfileIdTag> SettingsProfileId { get; }

    public SettingsProfileVersion SettingsProfileVersion { get; }

    public ContentDigest SettingsProfileDigest { get; }

    public ContentDigest SettingsSnapshotDigest { get; }

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

    public ContentDigest TranscriptDigest { get; }

    public string ActorSid { get; }

    public DateTimeOffset DecidedAt { get; }
}

public sealed class AutoDecisionPreparation
{
    internal AutoDecisionPreparation(
        ContentDigest inputDigest,
        ContentDigest decisionSetDigest,
        CompositionVersion child,
        WorkingCopy preparedWorkingCopy,
        int expectedWorkingCopyRevision,
        TimelineMap timelineMap,
        IReadOnlyList<AutoDecisionEvidence> evidence,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        IReadOnlyList<EditOperation> operations)
    {
        InputDigest = inputDigest;
        DecisionSetDigest = decisionSetDigest;
        Child = child;
        PreparedWorkingCopy = preparedWorkingCopy;
        ExpectedWorkingCopyRevision = expectedWorkingCopyRevision;
        TimelineMap = timelineMap;
        Evidence = Array.AsReadOnly(evidence.ToArray());
        DecisionHistory = Array.AsReadOnly(decisionHistory.ToArray());
        Operations = Array.AsReadOnly(operations.ToArray());
    }

    public ContentDigest InputDigest { get; }

    public ContentDigest DecisionSetDigest { get; }

    public CompositionVersion Child { get; }

    public WorkingCopy PreparedWorkingCopy { get; }

    public int ExpectedWorkingCopyRevision { get; }

    public TimelineMap TimelineMap { get; }

    public IReadOnlyList<AutoDecisionEvidence> Evidence { get; }

    public IReadOnlyList<SuggestionDecisionRecord> DecisionHistory { get; }

    public IReadOnlyList<EditOperation> Operations { get; }
}

public sealed class AutoDecisionApplication
{
    internal AutoDecisionApplication(
        AutoDecisionPreparation preparation,
        CheckpointResult checkpoint)
    {
        Preparation = preparation;
        Checkpoint = checkpoint;
    }

    public AutoDecisionPreparation Preparation { get; }

    public CheckpointResult Checkpoint { get; }
}

public sealed class AutoDecisionRollback
{
    internal AutoDecisionRollback(
        CheckpointResult checkpoint,
        TimelineMap timelineMap)
    {
        Checkpoint = checkpoint;
        TimelineMap = timelineMap;
    }

    public CheckpointResult Checkpoint { get; }

    public TimelineMap TimelineMap { get; }
}

public static class AutoDecisionOrchestrator
{
    private const string DecisionReason =
        "Automatically apply a closed FR-005 deletion rule.";
    private const string ExplicitCutRuleVersion =
        EditorialPhraseDetector.DictionaryVersion;
    private const string RepetitionRuleVersion =
        RepetitionSuggestionPayload.CanonicalRuleVersion;
    private const string SilenceRuleVersion =
        SilenceDetector.ThresholdVersion;
    internal const string EvidenceSchemaVersion = "hubradio.auto-decision-evidence.v2";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly RationalTime SilencePadding = new(3, 10);
    private static readonly RationalTime SilenceMinimumDuration = new(2, 1);

    public static AutoDecisionApplication Apply(
        VersionService versionService,
        AutoDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionService);
        cancellationToken.ThrowIfCancellationRequested();
        AutoDecisionPreparation preparation = Prepare(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        CheckpointResult checkpoint;
        try
        {
            ValidatedAutoDecisionEvidence validatedEvidence =
                AutoDecisionEvidenceCodec.ValidateForCheckpoint(
                    preparation.Child,
                    request.Parent);
            checkpoint = versionService.CheckpointAutoDecision(
                request.WorkingCopy,
                preparation.Child.Elements,
                preparation.ExpectedWorkingCopyRevision,
                preparation.Child.Id,
                request.ActorSid,
                request.CreatedAt,
                request.ChangeReason,
                preparation.Child.ContentDigest,
                validatedEvidence,
                cancellationToken);
        }
        catch (StoreConcurrencyException)
        {
            WorkingCopy expectedWorkingCopy = CreateCheckpointWorkingCopy(
                preparation,
                request.CreatedAt);
            if (!versionService.TryResolveExactCheckpoint(
                    preparation.Child,
                    expectedWorkingCopy,
                    preparation.ExpectedWorkingCopyRevision,
                    out CheckpointResult? existing)
                || existing is null)
            {
                throw;
            }

            checkpoint = existing;
        }
        if (checkpoint.Version.Id != preparation.Child.Id
            || checkpoint.Version.ParentVersionId != request.Parent.Id
            || checkpoint.Version.ContentDigest != preparation.Child.ContentDigest
            || checkpoint.Version.Evidence?.ContentDigest !=
                preparation.Child.Evidence?.ContentDigest
            || !checkpoint.Version.Elements.SequenceEqual(
                preparation.Child.Elements))
        {
            throw new InvalidOperationException(
                "The stored auto-decision child does not match its validated preparation.");
        }

        return new AutoDecisionApplication(preparation, checkpoint);
    }

    public static AutoDecisionRollback RollbackAsChild(
        VersionService versionService,
        AutoDecisionApplication application,
        CompositionVersion targetVersion,
        int expectedWorkingCopyRevision,
        string actorSid,
        DateTimeOffset createdAt,
        string changeReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionService);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(targetVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return RollbackAsChild(
            versionService,
            application.Checkpoint.Version.Id,
            targetVersion.Id,
            expectedWorkingCopyRevision,
            actorSid,
            createdAt,
            changeReason,
            cancellationToken);
    }

    public static AutoDecisionRollback RollbackAsChild(
        VersionService versionService,
        EntityId<CompositionVersionIdTag> appliedChildVersionId,
        EntityId<CompositionVersionIdTag> targetVersionId,
        int expectedWorkingCopyRevision,
        string actorSid,
        DateTimeOffset createdAt,
        string changeReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionService);
        ArgumentNullException.ThrowIfNull(appliedChildVersionId);
        ArgumentNullException.ThrowIfNull(targetVersionId);
        cancellationToken.ThrowIfCancellationRequested();
        CompositionVersion appliedChild = versionService.GetVersion(appliedChildVersionId)
            ?? throw new ArgumentException(
                "The applied auto-decision child is not stored.",
                nameof(appliedChildVersionId));
        if (appliedChild.Evidence is null
            || !string.Equals(
                appliedChild.Evidence.Kind,
                CompositionEvidenceEnvelope.AutoDecisionKind,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The stored child does not contain auto-decision evidence.",
                nameof(appliedChildVersionId));
        }

        CompositionVersion targetVersion = versionService.GetVersion(targetVersionId)
            ?? throw new ArgumentException(
                "The rollback target is not stored.",
                nameof(targetVersionId));
        WorkingCopyState current = versionService.GetCurrentState(appliedChild.ProjectId)
            ?? throw new InvalidOperationException("The working copy does not exist.");
        if (current.Revision != expectedWorkingCopyRevision
            || current.WorkingCopy.BaseVersionId != appliedChild.Id)
        {
            throw new StoreConcurrencyException(
                "working copy",
                current.WorkingCopy.Id.ToString(),
                expectedWorkingCopyRevision,
                current.Revision);
        }

        ContentDigest rollbackInputDigest = ComputeDigest(
            "hubradio.auto-decision-rollback.v2",
            [
                appliedChild.Id.ToString(),
                appliedChild.ContentDigest.Hex,
                appliedChild.Evidence.ContentDigest.Hex,
                targetVersion.Id.ToString(),
                targetVersion.ContentDigest.Hex,
                expectedWorkingCopyRevision.ToString(CultureInfo.InvariantCulture),
                actorSid,
                createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                changeReason,
            ]);
        cancellationToken.ThrowIfCancellationRequested();
        CheckpointResult checkpoint = versionService.RollbackAutoDecisionAsChild(
            current.WorkingCopy,
            targetVersion,
            expectedWorkingCopyRevision,
            DeterministicId<CompositionVersionIdTag>(rollbackInputDigest, "rollback-child"),
            actorSid,
            createdAt,
            changeReason,
            cancellationToken);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            DeterministicId<TimelineMapIdTag>(rollbackInputDigest, "rollback-timeline-map"),
            checkpoint.Version);
        ValidateTimelineRoundTrip(timelineMap);
        return new AutoDecisionRollback(checkpoint, timelineMap);
    }

    public static AutoDecisionPreparation Prepare(
        AutoDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ContentDigest transcriptDigest = ValidateCommonInput(request);
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans =
            ValidateSourceSpans(request, transcriptDigest);
        Dictionary<EntityId<ModelRunIdTag>, ModelRun> modelRuns =
            ValidateModelRuns(request.ModelRuns);
        CandidatePlan[] plans = ValidateCandidates(
            request,
            transcriptDigest,
            spans,
            modelRuns,
            cancellationToken);
        ValidateDeletionConflicts(plans);

        string inputJson = WriteInputIdentity(request);
        ContentDigest inputDigest = ComputeInputDigest(inputJson);
        var preparedWorkingCopy = new WorkingCopy(
            request.WorkingCopy.Id,
            request.WorkingCopy.ProjectId,
            request.Parent.Id,
            request.WorkingCopy.CreatedAt,
            request.WorkingCopy.UpdatedAt,
            request.Parent.Elements);
        var operations = new List<EditOperation>();
        var history = request.DecisionHistory.ToList();
        var evidence = new List<AutoDecisionEvidence>(plans.Length);

        foreach (CandidatePlan plan in plans
            .OrderByDescending(static plan => plan.DeleteRange.Start)
            .ThenBy(static plan => plan.Candidate.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyPlan(
                request,
                preparedWorkingCopy,
                plan,
                inputDigest,
                history,
                operations,
                evidence,
                transcriptDigest);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ContentDigest childContentDigest = ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(
                preparedWorkingCopy.Elements));
        EntityId<CompositionVersionIdTag> childId = DeterministicId<
            CompositionVersionIdTag>(inputDigest, "child");
        var childWithoutEvidence = new CompositionVersion(
            childId,
            request.Parent.ProjectId,
            request.Parent.Id,
            request.ActorSid,
            request.CreatedAt,
            request.ChangeReason,
            childContentDigest,
            preparedWorkingCopy.Elements);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            DeterministicId<TimelineMapIdTag>(inputDigest, "timeline-map"),
            childWithoutEvidence);
        ValidateTimelineRoundTrip(timelineMap);
        ContentDigest decisionSetDigest = ComputeDigest(
            "hubradio.auto-decision-set.v1",
            evidence
                .OrderBy(static item => item.DecisionDigest.Hex, StringComparer.Ordinal)
                .Select(static item => item.DecisionDigest.Hex));
        string evidenceJson = WriteEvidencePayload(
            request,
            inputJson,
            inputDigest,
            decisionSetDigest,
            childWithoutEvidence,
            transcriptDigest,
            evidence,
            history,
            timelineMap);
        CompositionEvidenceEnvelope envelope = CompositionEvidenceEnvelope.Create(
            CompositionEvidenceEnvelope.AutoDecisionKind,
            evidenceJson);
        var child = new CompositionVersion(
            childWithoutEvidence.Id,
            childWithoutEvidence.ProjectId,
            childWithoutEvidence.ParentVersionId,
            childWithoutEvidence.CreatedByActorId,
            childWithoutEvidence.CreatedAt,
            childWithoutEvidence.ChangeReason,
            childWithoutEvidence.ContentDigest,
            childWithoutEvidence.Elements,
            envelope);

        return new AutoDecisionPreparation(
            inputDigest,
            decisionSetDigest,
            child,
            preparedWorkingCopy,
            request.ExpectedWorkingCopyRevision,
            timelineMap,
            evidence.OrderBy(static item => item.CandidateId.ToString(), StringComparer.Ordinal)
                .ToArray(),
            history,
            operations);
    }

    private static ContentDigest ValidateCommonInput(AutoDecisionRequest request)
    {
        if (request.ExpectedWorkingCopyRevision < 0
            || request.WorkingCopy.ProjectId != request.Parent.ProjectId
            || request.WorkingCopy.BaseVersionId != request.Parent.Id
            || request.WorkingCopy.Elements.Count != request.Parent.Elements.Count
            || !request.WorkingCopy.Elements.SequenceEqual(request.Parent.Elements)
            || request.CreatedAt < request.WorkingCopy.UpdatedAt)
        {
            throw Failure(AutoDecisionFailureReason.ParentMismatch);
        }

        ContentDigest actualParentDigest = ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(request.Parent.Elements));
        if (actualParentDigest != request.Parent.ContentDigest)
        {
            throw Failure(AutoDecisionFailureReason.ParentMismatch);
        }

        ValidateSettingsBinding(request);
        ValidateSettings(request.Settings);
        ContentDigest transcriptDigest = ContentDigest.Compute(
            StrictUtf8.GetBytes(TranscriptSnapshotJson.WriteRevision(request.Transcript)));
        if (request.Candidates.Count == 0)
        {
            throw Failure(AutoDecisionFailureReason.EvidenceMissing);
        }

        return transcriptDigest;
    }

    private static void ValidateSettings(DefaultSettingsSnapshot settings)
    {
        var values = settings.Values.ToDictionary(
            static selection => selection.Key,
            static selection => selection.Value,
            StringComparer.Ordinal);
        if (!HasBoolean(values, "editing.explicitCutAutoExclude", expected: true)
            || !HasBoolean(
                values,
                "editing.adjacentExactRepetitionAutoExclude",
                expected: true)
            || !HasText(
                values,
                "editing.repetitionMatchPolicy",
                "adjacentSameSpeakerNfkcExact")
            || !HasInteger(values, "editing.silenceThreshold", -50)
            || !HasInteger(values, "editing.silenceMinimumDuration", 2)
            || !HasRational(values, "editing.silencePadding", 3, 10)
            || !HasBoolean(
                values,
                "editing.contextDeviationAutoRemovalEnabled",
                expected: false))
        {
            throw Failure(AutoDecisionFailureReason.SettingsMismatch);
        }
    }

    private static void ValidateSettingsBinding(AutoDecisionRequest request)
    {
        try
        {
            DefaultSettingsSnapshot restored = DefaultSettingsSnapshot.ParseCanonical(
                request.Settings.CanonicalJson,
                request.SettingsProfile);
            if (!string.Equals(
                    restored.CanonicalJson,
                    request.Settings.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw Failure(AutoDecisionFailureReason.SettingsMismatch);
            }
        }
        catch (JsonException)
        {
            throw Failure(AutoDecisionFailureReason.SettingsMismatch);
        }
    }

    private static Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>
        ValidateSourceSpans(
            AutoDecisionRequest request,
            ContentDigest transcriptDigest)
    {
        var spans = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();
        foreach (SourceSpan span in request.SourceSpans)
        {
            if (!spans.TryAdd(span.Id, span))
            {
                throw Failure(AutoDecisionFailureReason.DuplicateConflict);
            }
        }

        foreach (Utterance utterance in request.Transcript.Utterances)
        {
            if (!spans.TryGetValue(utterance.SourceSpanId, out SourceSpan? span))
            {
                throw Failure(AutoDecisionFailureReason.EvidenceMissing);
            }

            if (span.TranscriptRevisionId != request.Transcript.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(AutoDecisionFailureReason.TranscriptMismatch);
            }
        }

        foreach (CompositionElement element in request.Parent.Elements)
        {
            if (element.Origin.Kind != OriginKind.SourceSpan)
            {
                continue;
            }

            if (element.SourceRange is not TimeRange sourceRange
                || element.Origin.SourceSpanId is null
                || !spans.TryGetValue(element.Origin.SourceSpanId, out SourceSpan? span)
                || sourceRange.Start < span.Range.Start
                || sourceRange.End > span.Range.End)
            {
                throw Failure(AutoDecisionFailureReason.RangeInvalid);
            }
        }

        _ = transcriptDigest;
        return spans;
    }

    private static Dictionary<EntityId<ModelRunIdTag>, ModelRun> ValidateModelRuns(
        IReadOnlyList<ModelRun> modelRuns)
    {
        var indexed = new Dictionary<EntityId<ModelRunIdTag>, ModelRun>();
        foreach (ModelRun modelRun in modelRuns)
        {
            if (!indexed.TryAdd(modelRun.Id, modelRun))
            {
                throw Failure(AutoDecisionFailureReason.DuplicateConflict);
            }
        }

        return indexed;
    }

    private static CandidatePlan[] ValidateCandidates(
        AutoDecisionRequest request,
        ContentDigest transcriptDigest,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        IReadOnlyDictionary<EntityId<ModelRunIdTag>, ModelRun> modelRuns,
        CancellationToken cancellationToken)
    {
        var candidateIds = new HashSet<EntityId<SuggestionIdTag>>();
        var decisionCandidateIds = request.DecisionHistory
            .Select(static record => record.CandidateId)
            .ToHashSet();
        var plans = new CandidatePlan[request.Candidates.Count];
        SuggestionCandidate[] ordered = request.Candidates
            .OrderBy(static candidate => candidate.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuggestionCandidate candidate = ordered[index];
            if (!candidateIds.Add(candidate.Id)
                || decisionCandidateIds.Contains(candidate.Id)
                || candidate.Status != SuggestionStatus.Proposed
                || candidate.ReplacementSuggestionId is not null)
            {
                throw Failure(AutoDecisionFailureReason.DuplicateConflict);
            }

            if (!modelRuns.TryGetValue(
                    candidate.Suggestion.ModelRunId,
                    out ModelRun? modelRun)
                || modelRun.Outcome != ModelRunOutcome.Succeeded
                || modelRun.OutputDigest is null)
            {
                throw Failure(AutoDecisionFailureReason.ModelRunInvalid);
            }

            plans[index] = candidate switch
            {
                EditorialPhraseSuggestionCandidate editorial =>
                    ValidateEditorial(request, editorial, transcriptDigest, spans, modelRun),
                RepetitionSuggestionCandidate repetition =>
                    ValidateRepetition(request, repetition, transcriptDigest, spans, modelRun),
                SilenceSuggestionCandidate silence =>
                    ValidateSilence(request, silence, spans, modelRun),
                _ => throw Failure(AutoDecisionFailureReason.UnknownRule),
            };
        }

        return plans;
    }

    private static CandidatePlan ValidateEditorial(
        AutoDecisionRequest request,
        EditorialPhraseSuggestionCandidate candidate,
        ContentDigest transcriptDigest,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        ModelRun modelRun)
    {
        EditorialPhraseCandidate payload = candidate.Payload;
        if (payload.SourceRevisionId != request.Transcript.Id
            || payload.SourceRevisionDigest != transcriptDigest
            || candidate.SourceDigest != transcriptDigest)
        {
            throw Failure(AutoDecisionFailureReason.EvidenceStale);
        }

        if (!string.Equals(
                payload.DictionaryVersion,
                ExplicitCutRuleVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                payload.MatchedPhrase,
                EditorialPhraseDetector.Phrases.Single(),
                StringComparison.Ordinal)
            || payload.UsesPartialTokenCoverage
            || payload.UsesUtteranceTimingFallback
            || payload.SourceSpan.TranscriptTokenIds.Count == 0)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        Utterance utterance = FindUtteranceForTokens(
            request.Transcript,
            payload.SourceSpan.TranscriptTokenIds);
        Token[] tokens = payload.SourceSpan.TranscriptTokenIds
            .Select(id => utterance.Tokens.SingleOrDefault(token => token.Id == id)
                ?? throw Failure(AutoDecisionFailureReason.TranscriptMismatch))
            .ToArray();
        if (tokens.Any(static token => token.Range is null)
            || payload.SourceSpan.Range.Start != tokens[0].Range!.Value.Start
            || payload.SourceSpan.Range.End != tokens[^1].Range!.Value.End
            || !string.Equals(
                Normalize(string.Concat(tokens.Select(static token => token.Text))),
                payload.MatchedPhrase,
                StringComparison.Ordinal))
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        SourceSpan utteranceSpan = spans[utterance.SourceSpanId];
        ValidateDerivedSpan(payload.SourceSpan, utteranceSpan, request.Transcript.Id);
        return CreatePlan(
            candidate,
            modelRun,
            AutoDecisionRule.ExplicitCut,
            ExplicitCutRuleVersion,
            ComputeDigest(
                "hubradio.auto-rule.explicit-cut.v1",
                [ExplicitCutRuleVersion, payload.MatchedPhrase]),
            payload.SourceSpan,
            comparisonSourceSpanId: null,
            comparisonTokenIds: [],
            payload.SourceSpan.Range,
            request.Parent.Elements,
            spans);
    }

    private static CandidatePlan ValidateRepetition(
        AutoDecisionRequest request,
        RepetitionSuggestionCandidate candidate,
        ContentDigest transcriptDigest,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        ModelRun modelRun)
    {
        RepetitionSuggestionPayload payload = candidate.Payload;
        if (payload.SourceRevisionId != request.Transcript.Id
            || payload.SourceRevisionDigest != transcriptDigest
            || candidate.SourceDigest != transcriptDigest)
        {
            throw Failure(AutoDecisionFailureReason.EvidenceStale);
        }

        RepetitionReasonFeatures features = payload.Features;
        if (!string.Equals(
                payload.RuleVersion,
                RepetitionRuleVersion,
                StringComparison.Ordinal)
            || !features.ExactNfkcMatch
            || !features.StructurallyAdjacent
            || !features.CandidateIsLaterOccurrence)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        if (string.Equals(
                features.Rule,
                RepetitionReasonFeatures.AdjacentExactTokenSequenceRule,
                StringComparison.Ordinal))
        {
            return ValidateTokenSequenceRepetition(
                request,
                candidate,
                modelRun,
                spans);
        }

        if (!string.Equals(
                features.Rule,
                RepetitionReasonFeatures.AdjacentExactUtteranceRule,
                StringComparison.Ordinal)
            || !features.SameSpeakerRequired)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        int laterIndex = request.Transcript.Utterances
            .Select(static utterance => utterance.SourceSpanId)
            .ToList()
            .FindIndex(id => id == payload.SourceSpanId);
        if (laterIndex <= 0)
        {
            throw Failure(AutoDecisionFailureReason.TranscriptMismatch);
        }

        Utterance earlier = request.Transcript.Utterances[laterIndex - 1];
        Utterance later = request.Transcript.Utterances[laterIndex];
        if (earlier.SourceSpanId != features.ComparisonSourceSpanId
            || earlier.SpeakerId != later.SpeakerId
            || !payload.EvidenceTokenIds.SequenceEqual(
                later.Tokens.Select(static token => token.Id))
            || !features.ComparisonTokenIds.SequenceEqual(
                earlier.Tokens.Select(static token => token.Id))
            || !string.Equals(
                Normalize(string.Concat(earlier.Tokens.Select(static token => token.Text))),
                Normalize(string.Concat(later.Tokens.Select(static token => token.Text))),
                StringComparison.Ordinal))
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        SourceSpan laterSpan = spans[later.SourceSpanId];
        if (laterSpan.Range != later.Range)
        {
            throw Failure(AutoDecisionFailureReason.RangeInvalid);
        }

        return CreatePlan(
            candidate,
            modelRun,
            AutoDecisionRule.AdjacentExactRepetition,
            RepetitionRuleVersion,
            ComputeDigest(
                "hubradio.auto-rule.repetition.v1",
                [
                    RepetitionRuleVersion,
                    RepetitionReasonFeatures.AdjacentExactUtteranceRule,
                    "same-speaker",
                    "nfkc-exact",
                    "delete-latter",
                ]),
            laterSpan,
            earlier.SourceSpanId,
            features.ComparisonTokenIds,
            laterSpan.Range,
            request.Parent.Elements,
            spans);
    }

    private static CandidatePlan ValidateTokenSequenceRepetition(
        AutoDecisionRequest request,
        RepetitionSuggestionCandidate candidate,
        ModelRun modelRun,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans)
    {
        RepetitionSuggestionPayload payload = candidate.Payload;
        RepetitionReasonFeatures features = payload.Features;
        if (features.SameSpeakerRequired
            || payload.SourceSpanId != features.ComparisonSourceSpanId
            || payload.EvidenceTokenIds.Count != features.ComparisonTokenIds.Count)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        Utterance utterance = request.Transcript.Utterances.SingleOrDefault(item =>
            item.SourceSpanId == payload.SourceSpanId)
            ?? throw Failure(AutoDecisionFailureReason.TranscriptMismatch);
        Token[] tokens = utterance.Tokens.ToArray();
        int earlierStart = FindContiguousTokenBlock(tokens, features.ComparisonTokenIds);
        int laterStart = FindContiguousTokenBlock(tokens, payload.EvidenceTokenIds);
        int count = payload.EvidenceTokenIds.Count;
        if (earlierStart < 0
            || laterStart != earlierStart + count
            || tokens.Skip(earlierStart).Take(count * 2)
                .Any(static token => token.Range is null))
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        Token[] earlier = tokens.Skip(earlierStart).Take(count).ToArray();
        Token[] later = tokens.Skip(laterStart).Take(count).ToArray();
        if (!string.Equals(
                Normalize(string.Concat(earlier.Select(static token => token.Text))),
                Normalize(string.Concat(later.Select(static token => token.Text))),
                StringComparison.Ordinal))
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        SourceSpan utteranceSpan = spans[utterance.SourceSpanId];
        TimeRange laterRange = new(
            later[0].Range!.Value.Start,
            later[^1].Range!.Value.End - later[0].Range!.Value.Start);
        var derivedLaterSpan = new SourceSpan(
            DeterministicId<SourceSpanIdTag>(payload.CandidateKey, "later-token-block"),
            utteranceSpan.AssetId,
            utteranceSpan.StreamId,
            laterRange,
            utteranceSpan.SourceDigest,
            request.Transcript.Id,
            payload.EvidenceTokenIds);
        ValidateDerivedSpan(derivedLaterSpan, utteranceSpan, request.Transcript.Id);
        return CreatePlan(
            candidate,
            modelRun,
            AutoDecisionRule.AdjacentExactRepetition,
            RepetitionRuleVersion,
            ComputeDigest(
                "hubradio.auto-rule.repetition.v2",
                [
                    RepetitionRuleVersion,
                    RepetitionReasonFeatures.AdjacentExactTokenSequenceRule,
                    "same-utterance",
                    "nfkc-exact",
                    "delete-latter",
                ]),
            derivedLaterSpan,
            utteranceSpan.Id,
            features.ComparisonTokenIds,
            laterRange,
            request.Parent.Elements,
            spans,
            requireRegisteredSourceSpan: false);
    }

    private static int FindContiguousTokenBlock(
        IReadOnlyList<Token> tokens,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> tokenIds)
    {
        for (int start = 0; start + tokenIds.Count <= tokens.Count; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < tokenIds.Count; offset++)
            {
                if (tokens[start + offset].Id != tokenIds[offset])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }

    private static CandidatePlan ValidateSilence(
        AutoDecisionRequest request,
        SilenceSuggestionCandidate candidate,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        ModelRun modelRun)
    {
        SilenceCandidate payload = candidate.Payload;
        if (!string.Equals(
                payload.ThresholdVersion,
                SilenceRuleVersion,
                StringComparison.Ordinal)
            || payload.AnalysisTimeline.ProjectId != request.Parent.ProjectId
            || payload.SourceSpan.Range.Duration < SilenceMinimumDuration
            || payload.SourceSpan.TranscriptRevisionId is not null
            || payload.SourceSpan.TranscriptTokenIds.Count != 0)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        SourceExtent extent = ResolveSourceExtent(
            request.Parent.Elements,
            spans,
            payload.SourceSpan.AssetId,
            payload.SourceSpan.StreamId,
            payload.SourceSpan.SourceDigest);
        bool isHead = payload.SourceSpan.Range.Start == extent.Start;
        bool isTail = payload.SourceSpan.Range.End == extent.End;
        if (isHead == isTail)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }

        TimeRange deleteRange = isHead
            ? new TimeRange(
                payload.SourceSpan.Range.Start,
                payload.SourceSpan.Range.Duration - SilencePadding)
            : new TimeRange(
                payload.SourceSpan.Range.Start + SilencePadding,
                payload.SourceSpan.Range.Duration - SilencePadding);
        if (deleteRange.Duration <= default(RationalTime))
        {
            throw Failure(AutoDecisionFailureReason.RangeInvalid);
        }

        return CreatePlan(
            candidate,
            modelRun,
            AutoDecisionRule.HeadTailSilence,
            SilenceRuleVersion,
            ComputeDigest(
                "hubradio.auto-rule.silence.v1",
                [SilenceRuleVersion, "-50", "2/1", "3/10", "head-or-tail"]),
            payload.SourceSpan,
            comparisonSourceSpanId: null,
            comparisonTokenIds: [],
            deleteRange,
            request.Parent.Elements,
            spans);
    }

    private static CandidatePlan CreatePlan(
        DeleteRangeSuggestionCandidate candidate,
        ModelRun modelRun,
        string ruleId,
        string ruleVersion,
        ContentDigest ruleDigest,
        SourceSpan sourceSpan,
        EntityId<SourceSpanIdTag>? comparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> comparisonTokenIds,
        TimeRange deleteRange,
        IReadOnlyList<CompositionElement> parentElements,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        bool requireRegisteredSourceSpan = true)
    {
        if (requireRegisteredSourceSpan
            && (!spans.TryGetValue(sourceSpan.Id, out SourceSpan? registeredSpan)
                || !SourceSpansEqual(sourceSpan, registeredSpan)))
        {
            throw Failure(AutoDecisionFailureReason.EvidenceMissing);
        }

        CompositionElementMatch match = FindContainingElement(
            deleteRange,
            sourceSpan,
            spans,
            parentElements);
        return new CandidatePlan(
            candidate,
            modelRun,
            ComputeModelRunDigest(modelRun),
            ruleId,
            ruleVersion,
            ruleDigest,
            sourceSpan,
            comparisonSourceSpanId,
            comparisonTokenIds.ToArray(),
            deleteRange,
            match.ElementId);
    }

    private static void ValidateDeletionConflicts(IReadOnlyList<CandidatePlan> plans)
    {
        var candidateKeys = new HashSet<(
            EntityId<AssetIdTag> AssetId,
            string StreamId,
            RationalTime Start,
            RationalTime End)>(DeletionConflictKeyComparer.Instance);
        CandidatePlan[] ordered = plans
            .OrderBy(static plan => plan.SourceSpan.AssetId.ToString(), StringComparer.Ordinal)
            .ThenBy(static plan => plan.SourceSpan.StreamId, StringComparer.Ordinal)
            .ThenBy(static plan => plan.DeleteRange.Start)
            .ThenBy(static plan => plan.DeleteRange.End)
            .ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            CandidatePlan current = ordered[index];
            var key = (
                current.SourceSpan.AssetId,
                current.SourceSpan.StreamId,
                current.DeleteRange.Start,
                current.DeleteRange.End);
            if (!candidateKeys.Add(key))
            {
                throw Failure(AutoDecisionFailureReason.DuplicateConflict);
            }

            if (index > 0)
            {
                CandidatePlan previous = ordered[index - 1];
                if (previous.SourceSpan.AssetId == current.SourceSpan.AssetId
                    && string.Equals(
                        previous.SourceSpan.StreamId,
                        current.SourceSpan.StreamId,
                        StringComparison.Ordinal)
                    && previous.DeleteRange.Overlaps(current.DeleteRange))
                {
                    throw Failure(AutoDecisionFailureReason.OverlapConflict);
                }
            }
        }
    }

    private static void ApplyPlan(
        AutoDecisionRequest request,
        WorkingCopy workingCopy,
        CandidatePlan plan,
        ContentDigest inputDigest,
        List<SuggestionDecisionRecord> history,
        List<EditOperation> operations,
        List<AutoDecisionEvidence> evidence,
        ContentDigest transcriptDigest)
    {
        CompositionElement target = FindCurrentContainingElement(
            workingCopy.Elements,
            request.SourceSpans,
            plan);
        TimeRange targetRange = target.SourceRange!.Value;

        if (plan.DeleteRange.End < targetRange.End)
        {
            EntityId<CompositionElementIdTag> rightId = DeterministicId<
                CompositionElementIdTag>(
                inputDigest,
                string.Concat("split-end:", plan.Candidate.Id));
            EditOperation splitEnd = EditOperation.Split(
                target.Id,
                plan.DeleteRange.End - targetRange.Start,
                rightId);
            CompositionEditor.Apply(workingCopy, splitEnd, request.CreatedAt);
            operations.Add(splitEnd);
            target = workingCopy.Elements.Single(element => element.Id == target.Id);
            targetRange = target.SourceRange!.Value;
        }

        EntityId<CompositionElementIdTag> deleteElementId = target.Id;
        if (plan.DeleteRange.Start > targetRange.Start)
        {
            deleteElementId = DeterministicId<CompositionElementIdTag>(
                inputDigest,
                string.Concat("split-start:", plan.Candidate.Id));
            EditOperation splitStart = EditOperation.Split(
                target.Id,
                plan.DeleteRange.Start - targetRange.Start,
                deleteElementId);
            CompositionEditor.Apply(workingCopy, splitStart, request.CreatedAt);
            operations.Add(splitStart);
        }

        EntityId<SuggestionDecisionIdTag> decisionId = DeterministicId<
            SuggestionDecisionIdTag>(
            inputDigest,
            string.Concat("suggestion-decision:", plan.Candidate.Id));
        EditSuggestionAdoptionResult adoption = SuggestionService.AdoptDeleteRange(
            plan.Candidate,
            plan.Candidate.SourceDigest,
            deleteElementId,
            history,
            decisionId,
            request.ActorSid,
            request.CreatedAt,
            DecisionReason);
        CompositionEditor.Apply(workingCopy, adoption.Operation, request.CreatedAt);
        operations.Add(adoption.Operation);
        history.Clear();
        history.AddRange(adoption.DecisionHistory);

        ContentDigest decisionDigest = ComputeDigest(
            "hubradio.auto-decision.v2",
            [
                plan.RuleId,
                plan.RuleVersion,
                plan.RuleDigest.Hex,
                WriteCanonicalJson(writer => WriteCandidate(writer, plan.Candidate)),
                WriteCanonicalJson(writer => WriteModelRun(writer, plan.ModelRun)),
                plan.ModelRunDigest.Hex,
                WriteCanonicalJson(writer => WriteSourceSpan(writer, plan.SourceSpan)),
                plan.ComparisonSourceSpanId is null
                    ? "null"
                    : WriteCanonicalJson(writer => WriteSourceSpan(
                        writer,
                        request.SourceSpans.Single(span =>
                            span.Id == plan.ComparisonSourceSpanId))),
                WriteCanonicalJson(writer => WriteEntityIds(writer, plan.ComparisonTokenIds)),
                WriteCanonicalJson(writer => WriteTimeRange(writer, plan.DeleteRange)),
                request.Parent.Id.ToString(),
                request.Parent.ContentDigest.Hex,
                request.Settings.ProfileId.ToString(),
                request.Settings.ProfileVersion.ToString(),
                request.Settings.ProfileDigest.Hex,
                request.Settings.ContentDigest.Hex,
                request.Transcript.Id.ToString(),
                transcriptDigest.Hex,
                request.ActorSid,
                request.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ]);
        evidence.Add(new AutoDecisionEvidence(
            plan.RuleId,
            plan.RuleVersion,
            plan.RuleDigest,
            decisionDigest,
            plan.Candidate.Id,
            plan.Candidate.Suggestion.ModelRunId,
            plan.ModelRunDigest,
            plan.SourceSpan,
            plan.ComparisonSourceSpanId,
            plan.ComparisonTokenIds,
            plan.DeleteRange,
            request.Parent.Id,
            request.Parent.ContentDigest,
            request.Settings.ProfileId,
            request.Settings.ProfileVersion,
            request.Settings.ProfileDigest,
            request.Settings.ContentDigest,
            request.Transcript.Id,
            transcriptDigest,
            request.ActorSid,
            request.CreatedAt));
    }

    private static CompositionElement FindCurrentContainingElement(
        IReadOnlyList<CompositionElement> elements,
        IReadOnlyList<SourceSpan> sourceSpans,
        CandidatePlan plan)
    {
        var spans = sourceSpans.ToDictionary(static span => span.Id);
        CompositionElement[] matches = elements.Where(element =>
        {
            if (element.Origin.Kind != OriginKind.SourceSpan
                || element.Origin.SourceSpanId is null
                || element.SourceRange is not TimeRange range
                || !spans.TryGetValue(element.Origin.SourceSpanId, out SourceSpan? span))
            {
                return false;
            }

            return span.AssetId == plan.SourceSpan.AssetId
                && span.SourceDigest == plan.SourceSpan.SourceDigest
                && string.Equals(span.StreamId, plan.SourceSpan.StreamId, StringComparison.Ordinal)
                && plan.DeleteRange.Start >= range.Start
                && plan.DeleteRange.End <= range.End;
        }).ToArray();
        if (matches.Length != 1)
        {
            throw Failure(AutoDecisionFailureReason.RangeInvalid);
        }

        return matches[0];
    }

    private static CompositionElementMatch FindContainingElement(
        TimeRange deleteRange,
        SourceSpan candidateSpan,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        IReadOnlyList<CompositionElement> parentElements)
    {
        CompositionElement[] matches = parentElements.Where(element =>
            element.Origin.Kind == OriginKind.SourceSpan
            && element.Origin.SourceSpanId is not null
            && element.SourceRange is TimeRange range
            && spans.TryGetValue(element.Origin.SourceSpanId, out SourceSpan? span)
            && span.AssetId == candidateSpan.AssetId
            && span.SourceDigest == candidateSpan.SourceDigest
            && string.Equals(span.StreamId, candidateSpan.StreamId, StringComparison.Ordinal)
            && deleteRange.Start >= range.Start
            && deleteRange.End <= range.End)
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(AutoDecisionFailureReason.RangeInvalid);
        }

        return new CompositionElementMatch(matches[0].Id);
    }

    private static SourceExtent ResolveSourceExtent(
        IReadOnlyList<CompositionElement> elements,
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> spans,
        EntityId<AssetIdTag> assetId,
        string streamId,
        ContentDigest sourceDigest)
    {
        TimeRange[] ranges = elements.Where(element =>
            element.Origin.Kind == OriginKind.SourceSpan
            && element.Origin.SourceSpanId is not null
            && element.SourceRange is not null
            && spans.TryGetValue(element.Origin.SourceSpanId, out SourceSpan? span)
            && span.AssetId == assetId
            && span.SourceDigest == sourceDigest
            && string.Equals(span.StreamId, streamId, StringComparison.Ordinal))
            .Select(static element => element.SourceRange!.Value)
            .OrderBy(static range => range.Start)
            .ToArray();
        if (ranges.Length == 0)
        {
            throw Failure(AutoDecisionFailureReason.RangeInvalid);
        }

        return new SourceExtent(
            ranges.Min(static range => range.Start),
            ranges.Max(static range => range.End));
    }

    private static ContentDigest ComputeInputDigest(string canonicalInputJson) =>
        ComputeDigest("hubradio.auto-decision-input.v3", [canonicalInputJson]);

    private static ContentDigest ComputeModelRunDigest(ModelRun modelRun) =>
        ComputeDigest(
            "hubradio.model-run-binding.v1",
            [
                modelRun.Id.ToString(),
                modelRun.Provider,
                modelRun.ModelIdentifier,
                modelRun.ModelVersion,
                modelRun.ModelHash?.Hex ?? string.Empty,
                modelRun.ModelHashAvailability.ToString(),
                modelRun.ConfigurationDigest.Hex,
                modelRun.InputDigest.Hex,
                modelRun.OutputDigest?.Hex ?? string.Empty,
                modelRun.Outcome.ToString(),
                modelRun.ErrorCode ?? string.Empty,
                modelRun.ExecutionLocation.ToString(),
                modelRun.Destination ?? string.Empty,
                modelRun.Region ?? string.Empty,
                modelRun.SentDataClassification ?? string.Empty,
                modelRun.RetentionPolicy ?? string.Empty,
                modelRun.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                modelRun.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ]);

    private static ContentDigest ComputeDigest(
        string domain,
        IEnumerable<string> fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, tag: 1, domain);
        foreach (string field in fields)
        {
            AppendDigestField(hash, tag: 2, field);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static EntityId<TTag> DeterministicId<TTag>(
        ContentDigest seed,
        string label)
    {
        using IncrementalHash incremental = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendDigestField(incremental, tag: 1, "hubradio.auto-decision-id.v2");
        AppendDigestField(incremental, tag: 2, seed.Hex);
        AppendDigestField(incremental, tag: 3, label);
        byte[] hash = incremental.GetHashAndReset();
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return EntityId<TTag>.From(new Guid(guidBytes));
    }

    private static void AppendDigestField(
        IncrementalHash hash,
        byte tag,
        string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        hash.AppendData(header);
        hash.AppendData(bytes);
    }

    private static WorkingCopy CreateCheckpointWorkingCopy(
        AutoDecisionPreparation preparation,
        DateTimeOffset updatedAt) =>
        new(
            preparation.PreparedWorkingCopy.Id,
            preparation.PreparedWorkingCopy.ProjectId,
            preparation.Child.Id,
            preparation.PreparedWorkingCopy.CreatedAt,
            updatedAt,
            preparation.Child.Elements);

    private static string WriteEvidencePayload(
        AutoDecisionRequest request,
        string inputJson,
        ContentDigest inputDigest,
        ContentDigest decisionSetDigest,
        CompositionVersion child,
        ContentDigest transcriptDigest,
        IReadOnlyList<AutoDecisionEvidence> evidence,
        IReadOnlyList<SuggestionDecisionRecord> history,
        TimelineMap timelineMap) =>
        WriteCanonicalJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", EvidenceSchemaVersion);
            writer.WritePropertyName("input");
            writer.WriteRawValue(inputJson, skipInputValidation: false);
            writer.WriteString("inputDigest", inputDigest.Hex);
            writer.WriteString("decisionSetDigest", decisionSetDigest.Hex);
            writer.WritePropertyName("child");
            writer.WriteStartObject();
            writer.WriteString("id", child.Id.ToString());
            writer.WriteString("contentDigest", child.ContentDigest.Hex);
            writer.WriteEndObject();
            writer.WritePropertyName("decisions");
            writer.WriteStartArray();
            foreach (AutoDecisionEvidence item in evidence
                .OrderBy(static value => value.CandidateId.ToString(), StringComparer.Ordinal))
            {
                DeleteRangeSuggestionCandidate candidate =
                    (DeleteRangeSuggestionCandidate)request.Candidates.Single(value =>
                        value.Id == item.CandidateId);
                ModelRun modelRun = request.ModelRuns.Single(value =>
                    value.Id == item.ModelRunId);
                writer.WriteStartObject();
                writer.WriteString("ruleId", item.RuleId);
                writer.WriteString("ruleVersion", item.RuleVersion);
                writer.WriteString("ruleDigest", item.RuleDigest.Hex);
                writer.WriteString("decisionDigest", item.DecisionDigest.Hex);
                writer.WritePropertyName("candidate");
                WriteCandidate(writer, candidate);
                writer.WritePropertyName("modelRun");
                WriteModelRun(writer, modelRun);
                writer.WriteString("modelRunDigest", item.ModelRunDigest.Hex);
                writer.WritePropertyName("sourceSpan");
                WriteSourceSpan(writer, item.SourceSpan);
                writer.WritePropertyName("comparison");
                if (item.ComparisonSourceSpanId is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("sourceSpan");
                    WriteSourceSpan(
                        writer,
                        request.SourceSpans.Single(span =>
                            span.Id == item.ComparisonSourceSpanId));
                    writer.WritePropertyName("tokenIds");
                    WriteEntityIds(writer, item.ComparisonTokenIds);
                    writer.WriteEndObject();
                }

                writer.WritePropertyName("deletedRange");
                WriteTimeRange(writer, item.DeletedSourceRange);
                writer.WriteString("actorSid", item.ActorSid);
                writer.WriteString(
                    "decidedAt",
                    item.DecidedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("decisionHistory");
            writer.WriteStartArray();
            foreach (SuggestionDecisionRecord record in history)
            {
                WriteDecisionRecord(writer, record);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("timelineMap");
            WriteTimelineMap(writer, timelineMap);
            writer.WriteEndObject();
        });

    private static string WriteInputIdentity(AutoDecisionRequest request) =>
        WriteCanonicalJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("workingCopy");
            writer.WriteStartObject();
            writer.WriteString("id", request.WorkingCopy.Id.ToString());
            writer.WriteString("projectId", request.WorkingCopy.ProjectId.ToString());
            WriteNullableString(
                writer,
                "baseVersionId",
                request.WorkingCopy.BaseVersionId?.ToString());
            writer.WriteNumber("revision", request.ExpectedWorkingCopyRevision);
            writer.WriteString(
                "createdAt",
                request.WorkingCopy.CreatedAt.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(
                "updatedAt",
                request.WorkingCopy.UpdatedAt.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WritePropertyName("parent");
            writer.WriteStartObject();
            writer.WriteString("id", request.Parent.Id.ToString());
            writer.WriteString("contentDigest", request.Parent.ContentDigest.Hex);
            writer.WriteEndObject();
            writer.WriteString("actorSid", request.ActorSid);
            writer.WriteString(
                "createdAt",
                request.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("changeReason", request.ChangeReason);
            writer.WritePropertyName("settingsProfile");
            writer.WriteRawValue(
                request.SettingsProfile.CanonicalJson,
                skipInputValidation: false);
            writer.WritePropertyName("settingsSnapshot");
            writer.WriteRawValue(request.Settings.CanonicalJson, skipInputValidation: false);
            writer.WritePropertyName("transcript");
            writer.WriteRawValue(
                TranscriptSnapshotJson.WriteRevision(request.Transcript),
                skipInputValidation: false);
            writer.WritePropertyName("sourceSpans");
            writer.WriteStartArray();
            foreach (SourceSpan span in request.SourceSpans
                .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
            {
                WriteSourceSpan(writer, span);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("modelRuns");
            writer.WriteStartArray();
            foreach (ModelRun modelRun in request.ModelRuns
                .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
            {
                WriteModelRun(writer, modelRun);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("candidates");
            writer.WriteStartArray();
            foreach (SuggestionCandidate candidate in request.Candidates
                .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
            {
                WriteCandidate(writer, candidate);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("priorDecisionHistory");
            writer.WriteStartArray();
            foreach (SuggestionDecisionRecord record in request.DecisionHistory)
            {
                WriteDecisionRecord(writer, record);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string WriteCanonicalJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        return StrictUtf8.GetString(CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    private static void WriteSettings(
        Utf8JsonWriter writer,
        DefaultSettingsSnapshot settings)
    {
        writer.WriteStartObject();
        writer.WriteString("profileId", settings.ProfileId.ToString());
        writer.WriteString("profileVersion", settings.ProfileVersion.ToString());
        writer.WriteString("profileDigest", settings.ProfileDigest.Hex);
        writer.WriteString("snapshotDigest", settings.ContentDigest.Hex);
        writer.WritePropertyName("values");
        writer.WriteStartArray();
        foreach (DefaultSettingSelection selection in settings.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("key", selection.Key);
            writer.WriteString("kind", selection.Value.Kind.ToString());
            writer.WritePropertyName("value");
            WriteSettingValue(writer, selection.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSettingValue(Utf8JsonWriter writer, DefaultSettingValue value)
    {
        switch (value.Kind)
        {
            case DefaultSettingValueKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue!.Value);
                break;
            case DefaultSettingValueKind.Integer:
                writer.WriteNumberValue(value.IntegerValue!.Value);
                break;
            case DefaultSettingValueKind.Rational:
                writer.WriteStartObject();
                writer.WriteNumber("numerator", value.RationalValue!.Value.Numerator);
                writer.WriteNumber("denominator", value.RationalValue.Value.Denominator);
                writer.WriteEndObject();
                break;
            case DefaultSettingValueKind.Text:
                writer.WriteStringValue(value.TextValue);
                break;
            case DefaultSettingValueKind.TextList:
                writer.WriteStartArray();
                foreach (string item in value.TextListValue)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unknown settings value kind.");
        }
    }

    private static void WriteModelRun(Utf8JsonWriter writer, ModelRun modelRun)
    {
        writer.WriteStartObject();
        writer.WriteString("id", modelRun.Id.ToString());
        writer.WriteString("provider", modelRun.Provider);
        writer.WriteString("modelIdentifier", modelRun.ModelIdentifier);
        writer.WriteString("modelVersion", modelRun.ModelVersion);
        WriteNullableString(writer, "modelHash", modelRun.ModelHash?.Hex);
        writer.WriteString("modelHashAvailability", modelRun.ModelHashAvailability.ToString());
        writer.WriteString("configurationDigest", modelRun.ConfigurationDigest.Hex);
        writer.WriteString("inputDigest", modelRun.InputDigest.Hex);
        WriteNullableString(writer, "outputDigest", modelRun.OutputDigest?.Hex);
        writer.WriteString("outcome", modelRun.Outcome.ToString());
        WriteNullableString(writer, "errorCode", modelRun.ErrorCode);
        writer.WriteString("executionLocation", modelRun.ExecutionLocation.ToString());
        WriteNullableString(writer, "destination", modelRun.Destination);
        WriteNullableString(writer, "region", modelRun.Region);
        WriteNullableString(
            writer,
            "sentDataClassification",
            modelRun.SentDataClassification);
        WriteNullableString(writer, "retentionPolicy", modelRun.RetentionPolicy);
        writer.WriteString(
            "startedAt",
            modelRun.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString(
            "completedAt",
            modelRun.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteCandidate(
        Utf8JsonWriter writer,
        SuggestionCandidate candidate)
    {
        writer.WriteStartObject();
        writer.WriteString("id", candidate.Id.ToString());
        writer.WriteString("sourceDigest", candidate.SourceDigest.Hex);
        writer.WriteString("producer", candidate.Producer.ToString());
        WriteNullableString(writer, "parentSuggestionId", candidate.ParentSuggestionId?.ToString());
        WriteNullableString(
            writer,
            "replacementSuggestionId",
            candidate.ReplacementSuggestionId?.ToString());
        writer.WritePropertyName("suggestion");
        WriteSuggestion(writer, candidate.Suggestion);
        writer.WritePropertyName("producerSuggestion");
        WriteSuggestion(writer, candidate.ProducerSuggestion);
        writer.WritePropertyName("payload");
        switch (candidate)
        {
            case EditorialPhraseSuggestionCandidate editorial:
                WriteEditorialPayload(writer, editorial.Payload);
                break;
            case RepetitionSuggestionCandidate repetition:
                WriteRepetitionPayload(writer, repetition.Payload);
                break;
            case SilenceSuggestionCandidate silence:
                WriteSilencePayload(writer, silence.Payload);
                break;
            default:
                throw Failure(AutoDecisionFailureReason.UnknownRule);
        }

        writer.WriteEndObject();
    }

    private static void WriteEditorialPayload(
        Utf8JsonWriter writer,
        EditorialPhraseCandidate payload)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WritePropertyName("sourceSpan");
        WriteSourceSpan(writer, payload.SourceSpan);
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString("sourceRevisionDigest", payload.SourceRevisionDigest.Hex);
        writer.WriteString("dictionaryVersion", payload.DictionaryVersion);
        writer.WriteString("matchedPhrase", payload.MatchedPhrase);
        writer.WriteNumber("normalizedSentenceStart", payload.NormalizedSentenceStart);
        writer.WriteNumber("normalizedMatchLength", payload.NormalizedMatchLength);
        writer.WriteBoolean("usesUtteranceTimingFallback", payload.UsesUtteranceTimingFallback);
        writer.WriteBoolean("usesPartialTokenCoverage", payload.UsesPartialTokenCoverage);
        writer.WriteEndObject();
    }

    private static void WriteRepetitionPayload(
        Utf8JsonWriter writer,
        RepetitionSuggestionPayload payload)
    {
        writer.WriteStartObject();
        writer.WriteString("candidateKey", payload.CandidateKey.Hex);
        writer.WriteString("modelRunId", payload.ModelRunId.ToString());
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString("sourceRevisionDigest", payload.SourceRevisionDigest.Hex);
        writer.WriteString("sourceSpanId", payload.SourceSpanId.ToString());
        writer.WritePropertyName("evidenceTokenIds");
        WriteEntityIds(writer, payload.EvidenceTokenIds);
        writer.WriteString("ruleVersion", payload.RuleVersion);
        writer.WriteString("kind", payload.Kind.ToString());
        writer.WriteString("status", payload.Status.ToString());
        writer.WriteString("reason", payload.Reason);
        WriteNullableNumber(writer, "confidence", payload.Confidence);
        writer.WriteBoolean("automaticDeletionAllowed", payload.AutomaticDeletionAllowed);
        writer.WriteBoolean("humanReviewRequired", payload.HumanReviewRequired);
        writer.WritePropertyName("features");
        writer.WriteStartObject();
        writer.WriteString("rule", payload.Features.Rule);
        writer.WriteString(
            "comparisonSourceSpanId",
            payload.Features.ComparisonSourceSpanId.ToString());
        writer.WritePropertyName("comparisonTokenIds");
        WriteEntityIds(writer, payload.Features.ComparisonTokenIds);
        writer.WriteNumber("matchingTokenCount", payload.Features.MatchingTokenCount);
        writer.WriteBoolean("exactNfkcMatch", payload.Features.ExactNfkcMatch);
        writer.WriteBoolean("structurallyAdjacent", payload.Features.StructurallyAdjacent);
        writer.WriteBoolean("sameSpeakerRequired", payload.Features.SameSpeakerRequired);
        writer.WriteBoolean(
            "candidateIsLaterOccurrence",
            payload.Features.CandidateIsLaterOccurrence);
        writer.WriteBoolean(
            "retentionChoiceRequired",
            payload.Features.RetentionChoiceRequired);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSilencePayload(Utf8JsonWriter writer, SilenceCandidate payload)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("analysisTimeline");
        writer.WriteRawValue(
            payload.AnalysisTimeline.CanonicalJson,
            skipInputValidation: false);
        writer.WritePropertyName("sourceSpan");
        WriteSourceSpan(writer, payload.SourceSpan);
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString("thresholdVersion", payload.ThresholdVersion);
        writer.WriteNumber("analysisStartSample", payload.AnalysisStartSample);
        writer.WriteNumber("analysisEndSample", payload.AnalysisEndSample);
        writer.WriteEndObject();
    }

    private static void WriteDetection(Utf8JsonWriter writer, Detection detection)
    {
        writer.WriteStartObject();
        writer.WriteString("id", detection.Id.ToString());
        writer.WriteString("modelRunId", detection.ModelRunId.ToString());
        writer.WriteString("kind", detection.Kind.ToString());
        writer.WritePropertyName("origin");
        WriteOrigin(writer, detection.Origin);
        writer.WriteString("description", detection.Description);
        WriteNullableNumber(writer, "confidence", detection.Confidence);
        writer.WriteEndObject();
    }

    private static void WriteSuggestion(Utf8JsonWriter writer, Suggestion suggestion)
    {
        writer.WriteStartObject();
        writer.WriteString("id", suggestion.Id.ToString());
        writer.WriteString("modelRunId", suggestion.ModelRunId.ToString());
        writer.WriteString("kind", suggestion.Kind.ToString());
        writer.WriteString("status", suggestion.Status.ToString());
        writer.WritePropertyName("origin");
        WriteOrigin(writer, suggestion.Origin);
        writer.WriteString("reason", suggestion.Reason);
        WriteNullableNumber(writer, "confidence", suggestion.Confidence);
        WriteNullableString(writer, "missingContext", suggestion.MissingContext);
        WriteNullableString(writer, "warning", suggestion.Warning);
        writer.WriteEndObject();
    }

    private static void WriteSourceSpan(Utf8JsonWriter writer, SourceSpan span)
    {
        writer.WriteStartObject();
        writer.WriteString("id", span.Id.ToString());
        writer.WriteString("assetId", span.AssetId.ToString());
        writer.WriteString("streamId", span.StreamId);
        writer.WritePropertyName("range");
        WriteTimeRange(writer, span.Range);
        writer.WriteString("sourceDigest", span.SourceDigest.Hex);
        WriteNullableString(
            writer,
            "transcriptRevisionId",
            span.TranscriptRevisionId?.ToString());
        writer.WritePropertyName("transcriptTokenIds");
        WriteEntityIds(writer, span.TranscriptTokenIds);
        writer.WriteEndObject();
    }

    private static void WriteDecisionRecord(
        Utf8JsonWriter writer,
        SuggestionDecisionRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("id", record.Id.ToString());
        writer.WriteString("candidateId", record.CandidateId.ToString());
        writer.WriteString("modelRunId", record.ModelRunId.ToString());
        writer.WriteString("producer", record.Producer.ToString());
        writer.WriteString("suggestionKind", record.SuggestionKind.ToString());
        writer.WriteString("decision", record.Decision.ToString());
        writer.WriteString("statusAfterDecision", record.StatusAfterDecision.ToString());
        writer.WritePropertyName("origin");
        WriteOrigin(writer, record.Origin);
        writer.WriteString("sourceDigest", record.SourceDigest.Hex);
        writer.WriteString("actorSid", record.ActorSid);
        writer.WriteString(
            "decidedAt",
            record.DecidedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("reason", record.Reason);
        WriteNullableString(
            writer,
            "replacementSuggestionId",
            record.ReplacementSuggestionId?.ToString());
        writer.WritePropertyName("adoptedReference");
        if (record.AdoptedReference is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString(
                "suggestionId",
                record.AdoptedReference.SuggestionId.ToString());
            writer.WritePropertyName("origin");
            WriteOrigin(writer, record.AdoptedReference.Origin);
            writer.WriteEndObject();
        }

        WriteNullableString(
            writer,
            "resultingTranscriptRevisionId",
            record.ResultingTranscriptRevisionId?.ToString());
        writer.WriteEndObject();
    }

    private static void WriteTimelineMap(Utf8JsonWriter writer, TimelineMap timelineMap)
    {
        writer.WriteStartObject();
        writer.WriteString("id", timelineMap.Id.ToString());
        writer.WriteString(
            "compositionVersionId",
            timelineMap.CompositionVersionId.ToString());
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (TimelineMapEntry entry in timelineMap.Entries)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("outputRange");
            WriteTimeRange(writer, entry.OutputRange);
            writer.WritePropertyName("origin");
            WriteOrigin(writer, entry.Origin);
            writer.WritePropertyName("sourceRange");
            if (entry.SourceRange is TimeRange sourceRange)
            {
                WriteTimeRange(writer, sourceRange);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", origin.Kind.ToString());
        WriteNullableString(writer, "sourceSpanId", origin.SourceSpanId?.ToString());
        WriteNullableString(writer, "assetId", origin.AssetId?.ToString());
        WriteNullableString(
            writer,
            "editorialRecordId",
            origin.EditorialRecordId?.ToString());
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

    private static void WriteEntityIds<TTag>(
        Utf8JsonWriter writer,
        IReadOnlyList<EntityId<TTag>> ids)
    {
        writer.WriteStartArray();
        foreach (EntityId<TTag> id in ids)
        {
            writer.WriteStringValue(id.ToString());
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        double? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static void ValidateTimelineRoundTrip(TimelineMap map)
    {
        foreach (TimelineMapEntry entry in map.Entries)
        {
            if (entry.Origin.Kind != OriginKind.SourceSpan
                || entry.SourceRange is not TimeRange sourceRange)
            {
                continue;
            }

            if (!map.TryMapOutputTime(entry.OutputRange.Start, out TimelineMapPoint point)
                || point.Origin != entry.Origin
                || point.SourceTime != sourceRange.Start
                || !map.MapSourceTimeToOutput(
                        entry.Origin.SourceSpanId!,
                        sourceRange.Start)
                    .Contains(entry.OutputRange.Start))
            {
                throw Failure(AutoDecisionFailureReason.RangeInvalid);
            }
        }
    }

    private static Utterance FindUtteranceForTokens(
        TranscriptRevision transcript,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> tokenIds)
    {
        Utterance[] matches = transcript.Utterances.Where(utterance =>
            tokenIds.All(id => utterance.Tokens.Any(token => token.Id == id)))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(AutoDecisionFailureReason.TranscriptMismatch);
        }

        return matches[0];
    }

    private static void ValidateDerivedSpan(
        SourceSpan derived,
        SourceSpan parent,
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId)
    {
        if (derived.AssetId != parent.AssetId
            || derived.SourceDigest != parent.SourceDigest
            || !string.Equals(derived.StreamId, parent.StreamId, StringComparison.Ordinal)
            || derived.TranscriptRevisionId != transcriptRevisionId
            || derived.Range.Start < parent.Range.Start
            || derived.Range.End > parent.Range.End)
        {
            throw Failure(AutoDecisionFailureReason.TranscriptMismatch);
        }
    }

    private static bool SourceSpansEqual(SourceSpan left, SourceSpan right) =>
        left.Id == right.Id
        && left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);

    private static string Normalize(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (Exception exception)
            when (exception is EncoderFallbackException or ArgumentException)
        {
            throw Failure(AutoDecisionFailureReason.AmbiguousEvidence);
        }
    }

    private static bool HasBoolean(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        bool expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Boolean
        && value.BooleanValue == expected;

    private static bool HasInteger(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        long expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Integer
        && value.IntegerValue == expected;

    private static bool HasRational(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        long numerator,
        long denominator) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Rational
        && value.RationalValue == new RationalNumber(numerator, denominator);

    private static bool HasText(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        string expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Text
        && string.Equals(value.TextValue, expected, StringComparison.Ordinal);

    private static AutoDecisionException Failure(AutoDecisionFailureReason reason) =>
        new(reason);

    private sealed record CandidatePlan(
        DeleteRangeSuggestionCandidate Candidate,
        ModelRun ModelRun,
        ContentDigest ModelRunDigest,
        string RuleId,
        string RuleVersion,
        ContentDigest RuleDigest,
        SourceSpan SourceSpan,
        EntityId<SourceSpanIdTag>? ComparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> ComparisonTokenIds,
        TimeRange DeleteRange,
        EntityId<CompositionElementIdTag> ParentElementId);

    private readonly record struct SourceExtent(RationalTime Start, RationalTime End);

    private readonly record struct CompositionElementMatch(
        EntityId<CompositionElementIdTag> ElementId);

    private sealed class DeletionConflictKeyComparer : IEqualityComparer<(
        EntityId<AssetIdTag> AssetId,
        string StreamId,
        RationalTime Start,
        RationalTime End)>
    {
        internal static DeletionConflictKeyComparer Instance { get; } = new();

        public bool Equals(
            (EntityId<AssetIdTag> AssetId, string StreamId, RationalTime Start, RationalTime End) x,
            (EntityId<AssetIdTag> AssetId, string StreamId, RationalTime Start, RationalTime End) y) =>
            x.AssetId == y.AssetId
            && string.Equals(x.StreamId, y.StreamId, StringComparison.Ordinal)
            && x.Start == y.Start
            && x.End == y.End;

        public int GetHashCode(
            (EntityId<AssetIdTag> AssetId, string StreamId, RationalTime Start, RationalTime End) value) =>
            HashCode.Combine(
                value.AssetId,
                StringComparer.Ordinal.GetHashCode(value.StreamId),
                value.Start,
                value.End);
    }
}