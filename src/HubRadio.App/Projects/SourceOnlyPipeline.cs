using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.App.Models;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using static HubRadio.App.Projects.SourceOnlySourceLocator;
using ProjectJson = HubRadio.Core.Projects.ProjectSnapshotJson;

namespace HubRadio.App.Projects;

/// <summary>
/// Awaitable, path-free scheduler outcome. A persistence failure has no claimed execution
/// state; callers must not translate it into success. Task failures are always observed.
/// </summary>
public sealed record SourceOnlyPipelineRun(
    EntityId<SourceOnlyExecutionIdTag> ExecutionId,
    StoredSourceOnlyExecution? Execution,
    string? InfrastructureFailureCode);

public sealed record SourceOnlyRecoveryResult(
    IReadOnlyList<StoredSourceOnlyExecution> Executions, bool Canceled, string? FailureCode);

/// <summary>Native evidence references suitable for host projection; no selected or artifact file paths.</summary>
public sealed record SourceOnlyPipelineEvidence(
    EntityId<SourceOnlyExecutionIdTag> ExecutionId,
    SourceOnlyResultArtifactReference AnalysisCheckpoint,
    SourceOnlyResultArtifactReference Audio,
    SourceOnlyResultArtifactReference Timeline,
    SourceOnlyResultArtifactReference RawAsr,
    SourceOnlyResultArtifactReference MachineTranscript,
    SourceOnlyResultArtifactReference RightsAttestation,
    SourceOnlyResultArtifactReference? AnalysisEvidence,
    SourceOnlyResultArtifactReference? SubtitleWarnings,
    SourceOnlyResultArtifactReference? GenerationEvidence,
    SourceOnlyResultArtifactReference? RenderPreflight,
    SourceOnlyResultArtifactReference? AutomaticDecisions = null)
{
    public SourceOnlyResultArtifactReference? GeneratedRuntime { get; init; }
    public SourceOnlyPipelineGenerationEvidence? Generation { get; init; }
}

/// <summary>Persisted generator output metadata, not evaluation, delivery or publication authorization.</summary>
public sealed record SourceOnlyPipelineGenerationEvidence(
    SourceOnlyResultArtifactReference Checkpoint,
    StandardArtifactGenerationDisposition Disposition,
    IReadOnlyList<SourceOnlyGeneratedArtifactEvidence> Outputs,
    IReadOnlyList<StandardGenerationOmission> Omissions,
    IReadOnlyList<SourceOnlyResultGenerationWarning> Warnings,
    bool StagingCleanupRequired);

/// <summary>Exact produced variant/version/artifact binding. Contains no storage or selected paths.</summary>
public sealed record SourceOnlyGeneratedArtifactEvidence(
    SourceOnlyResultRole Role,
    EntityId<StandardShortFormPlanIdTag>? RenditionId,
    EntityId<StandardThumbnailPlanIdTag>? ThumbnailPlanId,
    SourceOnlyResultArtifactReference Artifact,
    CompositionVersion Version,
    EntityId<RenderProfileIdTag> ProfileId,
    ContentDigest ProfileDigest,
    ContentDigest GraphDigest,
    ContentDigest ManifestDigest);

internal sealed record SourceOnlyAutomaticSkip(EntityId<SuggestionIdTag>? CandidateId, string Reason);
internal sealed record SourceOnlyAutomaticSelection(IReadOnlyList<SuggestionCandidate> Candidates,
    IReadOnlyList<SourceOnlyAutomaticSkip> Skipped);

/// <summary>
/// Host-owned native source-only pipeline. The host authorizes Read/Start/Retry/Regenerate,
/// owns the workspace ACL, and awaits DisposeAsync before closing its stores. Accept is
/// reached only through ProjectCreationService's authorized Start/rights-attestation action.
/// No constructor migrates a database, starts work, downloads models or reads the repository.
/// </summary>
public sealed partial class SourceOnlyPipeline : IAsyncDisposable, ISourceOnlyProjectCoordinator
{
    private const string AnalysisCheckpointSchema = "source-only.analysis-checkpoint.v1";
    private const int MaximumTranscriptBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object acceptanceGate = new();
    private readonly SourceOnlyExecutionStore executions;
    private readonly ProjectCreationDraftStore drafts;
    private readonly ProjectStore projects;
    private readonly DefaultSettingsStore settings;
    private readonly JobStore jobs;
    private readonly JobCoordinator coordinator;
    private readonly ArtifactStore artifacts;
    private readonly VersionService versions;
    private readonly AssetCandidateRegistry candidates;
    private readonly SourceOnlySourceLocator locator;
    private readonly SourceOnlyTaskOwner taskOwner = new();
    private readonly SourceOnlyRuntimeRegistrationGate runtimeRegistrations = new();
    private readonly TimeProvider clock;
    private readonly string workspace;
    private readonly FileStream workspaceLease;
    private readonly FileStream databaseLease;
    private readonly SourceOnlyRuntimeOptions? options;
    private readonly SourceOnlyRuntimeCallbacks callbacks;
    private readonly Func<StoredSourceOnlyExecution, StandardArtifactGenerationResult,
        DefaultSettingsProfile, DefaultSettingsSnapshot, CancellationToken, Task<SourceOnlyResult>>? evaluate;
    private readonly Func<StoredSourceOnlyExecution, SourceOnlyResult, CancellationToken, Task<SourceOnlyResult>>? deliver;
    private readonly Func<StoredSourceOnlyExecution, CancellationToken, Task>? executeForTesting;
    private CancellationTokenSource? recoveryCancellation;
    private Task<SourceOnlyRecoveryResult>? recoveryTask;
    private Task? disposalTask;
    private bool recovering;
    private bool disposed;

    /// <param name="evaluateAsync">
    /// Bind the real SourceOnlyOutputEvaluation.EvaluateAsync. Required, with no success
    /// fallback. The callback produces actual QC/evaluation results (NOT delivery) and must
    /// propagate cancellation; it must not schedule nested LocalJobRunner work.
    /// </param>
    /// <param name="deliverAsync">
    /// Optional host-owned real output-saving service. Absence is an explicit blocked
    /// dependency, never simulated delivery. Return the same evaluated set with actual
    /// persisted delivery bytes/receipts. SourceOnlyResultService must validate those
    /// receipts; the coordinator never sets Delivered or manufactures authorization.
    /// </param>
    public SourceOnlyPipeline(SourceOnlyRuntimeOptions options, AssetCandidateRegistry candidateRegistry,
        Func<StoredSourceOnlyExecution, StandardArtifactGenerationResult, DefaultSettingsProfile,
            DefaultSettingsSnapshot, CancellationToken, Task<SourceOnlyResult>> evaluateAsync,
        SourceOnlyRuntimeCallbacks? callbacks = null, TimeProvider? timeProvider = null,
        Func<StoredSourceOnlyExecution, SourceOnlyResult, CancellationToken, Task<SourceOnlyResult>>? deliverAsync = null,
        JobStore? jobStore = null, JobCoordinator? jobCoordinator = null)
        : this(RequireOptions(options, evaluateAsync).DatabasePath,
            options.WorkspacePath, candidateRegistry, timeProvider, jobStore, jobCoordinator)
    {
        this.options = options;
        evaluate = evaluateAsync ?? throw new ArgumentNullException(nameof(evaluateAsync));
        deliver = deliverAsync;
        this.callbacks = callbacks ?? new SourceOnlyRuntimeCallbacks();
    }

    /// <summary>Production overload binding the real evaluation service, with no delegate substitution.</summary>
    public SourceOnlyPipeline(SourceOnlyRuntimeOptions options, AssetCandidateRegistry candidateRegistry,
        SourceOnlyOutputEvaluation outputEvaluation, SourceOnlyRuntimeCallbacks? callbacks = null, TimeProvider? timeProvider = null,
        Func<StoredSourceOnlyExecution, SourceOnlyResult, CancellationToken, Task<SourceOnlyResult>>? deliverAsync = null,
        JobStore? jobStore = null, JobCoordinator? jobCoordinator = null)
        : this(options, candidateRegistry,
            (outputEvaluation ?? throw new ArgumentNullException(nameof(outputEvaluation))).EvaluateAsync,
            callbacks, timeProvider, deliverAsync, jobStore, jobCoordinator)
    {
    }

    private static SourceOnlyRuntimeOptions RequireOptions(SourceOnlyRuntimeOptions options, Delegate evaluateAsync)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evaluateAsync);
        return options;
    }

    // Acceptance/scheduling tests can block, cancel or fail the operation, but cannot supply
    // a fake production result. No public constructor exposes this override.
    internal SourceOnlyPipeline(string databasePath, string workspacePath,
        AssetCandidateRegistry candidateRegistry,
        Func<StoredSourceOnlyExecution, CancellationToken, Task> executeForTesting,
        TimeProvider? timeProvider = null, SourceOnlyRuntimeCallbacks? callbacks = null,
        JobStore? jobStore = null, JobCoordinator? jobCoordinator = null)
        : this(databasePath, workspacePath, candidateRegistry, timeProvider, jobStore, jobCoordinator)
    {
        this.executeForTesting = executeForTesting ?? throw new ArgumentNullException(nameof(executeForTesting));
        this.callbacks = callbacks ?? new SourceOnlyRuntimeCallbacks();
    }

    private SourceOnlyPipeline(string databasePath, string workspacePath,
        AssetCandidateRegistry candidateRegistry, TimeProvider? timeProvider,
        JobStore? jobStore, JobCoordinator? jobCoordinator)
    {
        if ((jobStore is null) != (jobCoordinator is null))
        {
            throw new ArgumentException("Supply both the shared job store and coordinator, or neither.", nameof(jobCoordinator));
        }
        _ = RequirePath(databasePath, directory: false);
        workspace = RequirePath(workspacePath, directory: true);
        candidates = candidateRegistry ?? throw new ArgumentNullException(nameof(candidateRegistry));
        clock = timeProvider ?? TimeProvider.System;
        callbacks = new SourceOnlyRuntimeCallbacks();
        locator = new SourceOnlySourceLocator(workspace);
        executions = new SourceOnlyExecutionStore(databasePath);
        drafts = new ProjectCreationDraftStore(databasePath);
        projects = new ProjectStore(databasePath);
        settings = new DefaultSettingsStore(databasePath);
        // Neither type exposes its database identity. The trusted host must bind the
        // pair to this database; never create a second coordinator around a supplied store.
        jobs = jobStore ?? new JobStore(databasePath);
        coordinator = jobCoordinator ?? new JobCoordinator(jobs, new LocalJobRunner(jobs, clock), clock);
        artifacts = new ArtifactStore(databasePath, workspace);
        versions = new VersionService(projects);
        string leaseDirectory = Path.Combine(workspace, "source-only");
        CreateDirectory(workspace, leaseDirectory);
        string leasePath = Path.Combine(leaseDirectory, "pipeline.lock");
        workspaceLease = OpenExclusiveLease(leasePath);
        try
        {
            // The workspace lock alone cannot protect one DB supplied with two
            // different workspaces. Acquire both before accepting/scheduling work.
            databaseLease = OpenExclusiveLease(databasePath + ".source-only.lock");
        }
        catch { workspaceLease.Dispose(); throw; }
    }

    internal JobStore Jobs => jobs;
    internal JobCoordinator Coordinator => coordinator;

    Task ISourceOnlyProjectCoordinator.CaptureSelectionAsync(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId, string path, string actorSid, DateTimeOffset selectedAt,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return locator.CaptureAsync(draftId, candidateId, path, actorSid, selectedAt, cancellationToken);
    }

    void ISourceOnlyProjectCoordinator.ForgetSelection(EntityId<ProjectCreationDraftIdTag> draftId) =>
        locator.ForgetUnacceptedSelection(draftId);

    bool ISourceOnlyProjectCoordinator.RestoreSelection(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId) => RestoreSelection(draftId, candidateId);

    private bool RestoreSelection(EntityId<ProjectCreationDraftIdTag> draftId, EntityId<AssetIdTag> candidateId)
    {
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            StoredProjectCreationDraft? current = drafts.GetDraft(draftId);
            if (current is null || current.IsFinalized ||
                !SourceOnlyProjectCreationContract.IsSourceOnlyPayload(current.CanonicalJson)) { return false; }
            using JsonDocument payload = JsonDocument.Parse(current.CanonicalJson);
            if (Text(payload.RootElement, "sourceCandidateId") != candidateId.ToString()) { return false; }
            try
            {
                LocatedSource selected = locator.ReadSelection(draftId, candidateId);
                // LoadDraft is synchronous. The verified read has no captured context;
                // hold the exact source while restoring only its original pending token.
                using FileStream held = locator.OpenVerifiedAsync(selected, CancellationToken.None).GetAwaiter().GetResult();
                return candidates.RestoreVerifiedSelectionForDraft(candidateId, selected.Path);
            }
            catch (SourceOnlySourceException) { return false; }
        }
    }

    StoredProjectCreationDraft ISourceOnlyProjectCoordinator.Accept(ProjectCreationDraft draft,
        DefaultSettingsSnapshot snapshot, string payloadJson, int expectedRevision, string actorSid,
        DateTimeOffset acceptedAt)
    {
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            EnsureNotRecovering();
            _ = TranscriptEditorContract.ValidateCanonicalSid(actorSid, nameof(actorSid));
            StoredProjectCreationDraft current = drafts.GetDraft(draft.Id)
                ?? throw new ProjectCreationException(ProjectCreationFailureReason.DraftNotFound);
            if (current.IsFinalized)
            {
                StoredSourceOnlyExecution original = executions.GetByDraft(draft.Id)
                    ?? throw new InvalidOperationException("The finalized draft has no native execution.");
                // Replay the ORIGINAL revision, not the newly incremented draft revision.
                if (expectedRevision != current.Revision - 1 ||
                    original.Request.ActorSid != actorSid || original.Request.SourceCandidateId != draft.SourceCandidateId ||
                    original.Request.SettingsSnapshot.CanonicalJson != snapshot.CanonicalJson ||
                    current.CanonicalJson != CanonicalJson.Canonicalize(payloadJson))
                {
                    throw new InvalidOperationException("The finalization replay does not match its original acceptance.");
                }
                _ = ReadAcceptedSource(original); // Validates the recorded DPAPI identity, not a guessed path.
                if (!IsTerminal(original.Phase)) { _ = Start(original.Request.ExecutionId); }
                return current;
            }

            if (!draft.IsComplete || draft.SourceCandidateId is null ||
                draft.PublicationScope != PublicationScope.InternalOnly)
            {
                throw new ProjectCreationException(ProjectCreationFailureReason.DraftIncomplete);
            }
            if (current.Revision != expectedRevision)
            {
                throw new StoreConcurrencyException("project creation draft", draft.Id.ToString(), expectedRevision, current.Revision);
            }
            if (!candidates.IsAvailable(draft.SourceCandidateId))
            {
                _ = RestoreSelection(draft.Id, draft.SourceCandidateId);
            }
            if (!candidates.TryReserve(draft.SourceCandidateId, out var reservation))
            {
                throw new ProjectCreationException(ProjectCreationFailureReason.SourceReselectionRequired);
            }
            using (reservation)
            {
                LocatedSource selected = locator.ReadSelection(draft.Id, draft.SourceCandidateId);
                if (selected.Path != reservation.SelectedPath ||
                    SourceOnlyProjectCreationContract.DeriveContentName(selected.FileName) != draft.ContentName)
                {
                    throw new ProjectCreationException(ProjectCreationFailureReason.SourceReselectionRequired);
                }
                // FinalizeDraft is synchronous today; all awaits inside this verified read
                // are ConfigureAwait(false). The handle pins identity through the DB commit.
                using FileStream held = locator.OpenVerifiedAsync(selected, CancellationToken.None).GetAwaiter().GetResult();
                byte[] protectedLocator = locator.Accept(selected, actorSid, acceptedAt,
                    StableId<ArtifactIdTag>(draft.Id.ToString(), "rights-attestation"));
                var project = new Project(EntityId<ProjectIdTag>.New(), draft.ContentName!, draft.Purpose!,
                    draft.Audience!, PublicationScope.InternalOnly, draft.DeadlineSelection!.Deadline, [], []);
                var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), project.Id,
                    draft.Id, draft.SourceCandidateId, EntityId<JobIdTag>.New(), EntityId<JobIdTag>.New(),
                    snapshot, actorSid, acceptedAt);
                StoredSourceOnlyExecution accepted = drafts.FinalizeSourceOnlyWithExecution(draft, project,
                    payloadJson, expectedRevision, acceptedAt, request, protectedLocator);
                reservation.Commit();
                _ = Start(accepted.Request.ExecutionId);
                return drafts.GetDraft(draft.Id)!;
            }
        }
    }

    /// <summary>
    /// Schedule once per execution; duplicate calls while active return the SAME task.
    /// All work and exceptions are owned until completion/disposal. Ingest is awaited
    /// before acquiring the root heavy-job slot: never nest one RunAsync inside another.
    /// A terminal acceptance is returned without rerunning its work.
    /// </summary>
    public Task<SourceOnlyPipelineRun> Start(EntityId<SourceOnlyExecutionIdTag> executionId)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            EnsureNotRecovering();
            StoredSourceOnlyExecution execution = RequireExecution(executionId);
            if (!IsTerminal(execution.Phase) && RequireJob(execution.Request.RootJobId).Job.State != JobState.Queued)
            {
                // A duplicate active call still joins the owned task. Unowned Running or
                // terminal Jobs require explicit startup recovery, not another RunAsync.
                if (!taskOwner.IsExecutionActive(executionId))
                {
                    throw new InvalidOperationException("Recover the native execution before restarting its jobs.");
                }
            }
            return taskOwner.Start(executionId, execution.Request.ProjectId,
                token => ExecuteOwnedAsync(executionId, token));
        }
    }

    /// <summary>Read latest durable execution, not necessarily the draft's original execution. Host authorizes first.</summary>
    public StoredSourceOnlyExecution? Read(EntityId<ProjectIdTag> projectId) => executions.GetLatestForProject(projectId);

    /// <summary>
    /// Explicit regeneration: captures the exact supplied current settings revision and
    /// parent version/digest, creates a NEW queued root and reuses the successful ingest/ASR.
    /// No hidden settings substitution, parent guessing, or in-place result mutation.
    /// </summary>
    public StoredSourceOnlyExecution Regenerate(EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> parentVersionId, ContentDigest parentDigest,
        DefaultSettingsSnapshot snapshot, int expectedSettingsRevision, string actorSid,
        DateTimeOffset createdAt)
    {
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            EnsureNotRecovering();
            StoredSourceOnlyExecution latest = Read(projectId)
                ?? throw new InvalidOperationException("The project has no source-only execution.");
            EnsureNoActiveExecution(latest);
            WorkingCopyState head = versions.GetCurrentState(projectId)
                ?? throw new InvalidOperationException("The parent working copy is unavailable.");
            CompositionVersion parent = versions.GetVersion(parentVersionId)
                ?? throw new InvalidOperationException("The parent composition is unavailable.");
            if (parent.ProjectId != projectId || parent.ContentDigest != parentDigest ||
                head.WorkingCopy.BaseVersionId != parentVersionId ||
                !head.WorkingCopy.Elements.SequenceEqual(parent.Elements))
            {
                throw new InvalidOperationException("The regeneration parent is not the exact current checkpoint.");
            }
            if (createdAt <= latest.Request.CreatedAt)
            {
                throw new ArgumentException("Regeneration time must follow the preceding acceptance.", nameof(createdAt));
            }
            // Capture the actual stored heads once. CreateRegeneration rechecks this
            // exact immutable prefix set in its acceptance transaction.
            IReadOnlyList<SubtitleEditHeadReference> subtitleEdits = executions.CaptureSubtitleEditHeads(projectId, parentVersionId);
            var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), projectId,
                latest.Request.DraftId, latest.Request.SourceCandidateId, EntityId<JobIdTag>.New(),
                latest.Request.IngestJobId, snapshot, actorSid, createdAt, parentVersionId, parentDigest, subtitleEdits);
            StoredSourceOnlyExecution accepted = executions.CreateRegeneration(request, expectedSettingsRevision);
            _ = Start(accepted.Request.ExecutionId);
            return accepted;
        }
    }

    /// <summary>Explicit successor acceptance through CreateRetry; never QueueRetry the root in place.</summary>
    public StoredSourceOnlyExecution Retry(EntityId<SourceOnlyExecutionIdTag> executionId,
        int expectedRevision, string actorSid, DateTimeOffset retriedAt)
    {
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            EnsureNotRecovering();
            StoredSourceOnlyExecution source = RequireExecution(executionId);
            StoredSourceOnlyExecution latest = Read(source.Request.ProjectId)!;
            if (latest.RetryOfExecutionId == executionId)
            {
                // CreateRetry validates actor/time/revision on exact acceptance replay,
                // even while the already owned successor is running.
                return executions.CreateRetry(executionId, expectedRevision, actorSid, retriedAt);
            }
            EnsureNoActiveExecution(source);
            if (latest.Request.ExecutionId != executionId && latest.RetryOfExecutionId != executionId)
            {
                throw new InvalidOperationException("Retry must target the current execution.");
            }
            StoredSourceOnlyExecution accepted = executions.CreateRetry(executionId, expectedRevision, actorSid, retriedAt);
            _ = Start(accepted.Request.ExecutionId);
            return accepted;
        }
    }

    /// <summary>Cooperatively cancel and await the owned process/stage; does not merely change a status label.</summary>
    public Task<SourceOnlyPipelineRun?> CancelAsync(EntityId<SourceOnlyExecutionIdTag> executionId) =>
        taskOwner.CancelAsync(executionId);

    /// <summary>
    /// Startup recovery under the exclusive workspace lease, before scheduling other work.
    /// Only this pipeline's jobs are touched. Unstarted queued work is scheduled; interrupted
    /// attempts are settled without pretending success. Post-render/delivery uncertainty is
    /// reconciliation-required. A succeeded root can finish its exact persisted result receipt.
    /// Returned rows are a recovery snapshot; newly scheduled jobs remain host-owned.
    /// </summary>
    public Task<SourceOnlyRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        lock (acceptanceGate)
        {
            ThrowIfDisposed();
            if (recovering) { return recoveryTask!; }
            if (taskOwner.ActiveCount != 0) { throw new InvalidOperationException("Recover before starting native jobs."); }
            recoveryCancellation?.Dispose();
            recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = recoveryCancellation.Token;
            recovering = true;
            recoveryTask = Task.Run(async () =>
            {
                try { return new SourceOnlyRecoveryResult(await RecoverCoreAsync(token).ConfigureAwait(false), false, null); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return new SourceOnlyRecoveryResult([], true, null);
                }
                catch (PipelineFailure failure) { return new SourceOnlyRecoveryResult([], false, failure.Code); }
                catch { return new SourceOnlyRecoveryResult([], false, "source_only.recovery_failed"); }
                finally { lock (acceptanceGate) { recovering = false; } }
            }, CancellationToken.None);
            return recoveryTask;
        }
    }

    private async Task<IReadOnlyList<StoredSourceOnlyExecution>> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        var recovered = new List<StoredSourceOnlyExecution>();
        var start = new List<EntityId<SourceOnlyExecutionIdTag>>();
        foreach (StoredProjectCreationDraft draft in drafts.ListDrafts().Where(item => item.IsFinalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (executions.GetByDraft(draft.Id) is null) { continue; }
            StoredSourceOnlyExecution current = Read(draft.FinalizedProjectId!)!;
            // A previous terminal execution may still own a Running root after the
            // runner's terminal write failed. Do not skip it, even after regeneration.
            IReadOnlyList<StoredSourceOnlyExecution> history = executions.ListForProject(
                current.Request.ProjectId, SourceOnlyExecutionStore.MaximumListResults);
            if (history.Count == SourceOnlyExecutionStore.MaximumListResults)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.recovery_history_limit");
            }
            foreach (StoredSourceOnlyExecution previous in history.Where(item => IsTerminal(item.Phase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SettleInterrupted(previous, previous.Request.RootJobId);
                CancelQueued(previous.Request.RootJobId);
                if (previous.Phase == SourceOnlyExecutionPhase.Completed &&
                    RequireJob(previous.Request.RootJobId).Job.State != JobState.Succeeded)
                {
                    // Completed is immutable. Expose the inconsistency for explicit
                    // reconciliation rather than silently blessing a failed root.
                    throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired,
                        "source_only.completed_root_reconciliation_required");
                }
            }
            if (IsTerminal(current.Phase))
            {
                SettleInterrupted(current, current.Request.IngestJobId);
                CancelQueued(current.Request.IngestJobId);
                if (options is not null)
                {
                    await RestoreRuntimeAsync(current.Request.ProjectId, cancellationToken).ConfigureAwait(false);
                }
                recovered.Add(current);
                continue;
            }
            JobState rootState = RequireJob(current.Request.RootJobId).Job.State;
            JobState ingestState = RequireJob(current.Request.IngestJobId).Job.State;
            if (rootState == JobState.Succeeded && current.Phase == SourceOnlyExecutionPhase.Delivering)
            {
                current = await ReconcileAndRestoreRuntimeAsync(current, CompleteFromReceiptAsync,
                    RestoreRuntimeAsync, cancellationToken).ConfigureAwait(false);
            }
            else if (rootState == JobState.Queued && (ingestState is JobState.Queued or JobState.Succeeded) &&
                current.Phase <= SourceOnlyExecutionPhase.Analyzing)
            {
                start.Add(current.Request.ExecutionId);
            }
            else
            {
                SettleInterrupted(current, current.Request.RootJobId);
                SettleInterrupted(current, current.Request.IngestJobId);
                CancelQueued(current.Request.RootJobId);
                CancelQueued(current.Request.IngestJobId);
                bool ambiguous = current.Phase >= SourceOnlyExecutionPhase.Rendering ||
                    (ingestState != JobState.Succeeded && IngestHasPromotedOriginal(current));
                current = SetPhase(current.Request.ExecutionId,
                    ambiguous ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Failed,
                    ambiguous ? "source_only.reconciliation_required" : "source_only.app_restarted");
                if (options is not null)
                {
                    await RestoreRuntimeAsync(current.Request.ProjectId, cancellationToken).ConfigureAwait(false);
                }
            }
            recovered.Add(current);
        }
        lock (acceptanceGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            foreach (var id in start)
            {
                StoredSourceOnlyExecution execution = RequireExecution(id);
                _ = taskOwner.Start(id, execution.Request.ProjectId, token => ExecuteOwnedAsync(id, token));
            }
        }
        return recovered.AsReadOnly();
    }

    internal static async Task<StoredSourceOnlyExecution> ReconcileAndRestoreRuntimeAsync(StoredSourceOnlyExecution execution,
        Func<StoredSourceOnlyExecution, CancellationToken, Task<StoredSourceOnlyExecution>> reconcile,
        Func<EntityId<ProjectIdTag>, CancellationToken, Task> restore, CancellationToken token)
    {
        StoredSourceOnlyExecution current = await reconcile(execution, token).ConfigureAwait(false);
        if (IsTerminal(current.Phase))
        {
            await restore(current.Request.ProjectId, token).ConfigureAwait(false);
        }
        return current;
    }

    /// <summary>
    /// Rehydrate available validated stage checkpoints, including failed evaluation.
    /// Missing later stages are not completion. Never reruns ingest/AI/render/evaluation,
    /// advances a working copy, writes an execution state, or clears reconciliation.
    /// Reads during an owned run are permitted; registrations share its evidence gate.
    /// </summary>
    public async Task RestoreRuntimeAsync(EntityId<ProjectIdTag> projectId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        StoredSourceOnlyExecution execution = Read(projectId)
            ?? throw new InvalidOperationException("The project has no source-only execution.");
        if (options is null) { throw new InvalidOperationException("The native runtime is not configured."); }
        if (RequireJob(execution.Request.IngestJobId).Job.State != JobState.Succeeded)
        {
            // The ingest codec validates any existing partial checkpoint without
            // invoking its producers. It cannot register an incomplete ingest as ready.
            StoredIngestWorkflow? partial = new IngestWorkflowStore(options.DatabasePath).Get(execution.Request.IngestJobId);
            if (partial is not null)
            {
                IngestWorkflowRequest request = IngestWorkflowCheckpointCodec.DeserializeRequest(partial.RequestPayloadJson);
                if (request.JobId != execution.Request.IngestJobId || request.ProjectId != projectId ||
                    request.AssetCandidateId != execution.Request.SourceCandidateId || request.RequestDigest != partial.RequestDigest)
                {
                    throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.ingest_checkpoint_invalid");
                }
                _ = await new IngestWorkflowCheckpointCodec(workspace, artifacts)
                    .DeserializeAsync(request, partial, cancellationToken).ConfigureAwait(false);
            }
            var ids = new AnalysisIds(execution.Request.IngestJobId.ToString());
            if (new[] { ids.Audio, ids.Timeline, ids.Raw, ids.Transcript }.Any(id => artifacts.GetArtifact(id) is not null))
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.ingest_checkpoint_incomplete");
            }
            EnsureNoLaterRuntimeCheckpoint(execution);
            return;
        }
        IngestWorkflowRunResult ingest = await CreateIngestWorkflow(new ProcessRunner(), new BinaryVerifier())
            .ReadCompletedAsync(execution.Request.IngestJobId, cancellationToken).ConfigureAwait(false);
        LocatedSource source = ReadAcceptedSource(execution);
        ValidateIngest(execution, source, ingest);
        await ValidateRightsCheckpointAsync(execution, source, cancellationToken).ConfigureAwait(false);
        AnalysisState? analysis = await ReadAnalysisAsync(execution, ingest, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            await ValidatePartialAnalysisAsync(execution, ingest, cancellationToken).ConfigureAwait(false);
            EnsureNoLaterRuntimeCheckpoint(execution);
            if (!runtimeRegistrations.IsInsideCallback)
            {
                await RegisterIngestAsync(execution, ingest, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        await RestoreRuntimeStagesAsync(execution, ReadProject(projectId), analysis.Import, cancellationToken,
            ingest).ConfigureAwait(false);
    }

    // Stage-only test boundary: production reaches this only after the exact ingest,
    // DPAPI attestation and persisted machine import were validated above.
    internal async Task RestoreRuntimeStagesAsync(StoredSourceOnlyExecution execution, Project project,
        TranscriptImportResult import, CancellationToken token, IngestWorkflowRunResult? ingest = null)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (project.Id != execution.Request.ProjectId || import.AudioTimeline.ProjectId != project.Id ||
            import.AudioTimeline.SourceAssetId != execution.Request.SourceCandidateId)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.runtime_checkpoint_invalid");
        }
        if (execution.ResultJson is not null)
        {
            SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest).ValidateBinding(execution);
        }
        SourceOnlyAnalysisEvidence? analysis = HasEvidence(execution, "analysis-evidence")
            ? await ReadAnalysisEvidenceAsync(project, import, execution, token).ConfigureAwait(false) : null;
        StandardCompositionPreparation? preparation = await ReadCompositionCheckpointAsync(execution, token).ConfigureAwait(false);
        if (preparation is not null) { ValidateRuntimeComposition(execution, preparation, import); }
        bool hasGenerated = HasEvidence(execution, "generated-runtime");
        bool hasGeneration = HasEvidence(execution, "generation-evidence");
        if ((analysis is null && (preparation is not null || HasEvidence(execution, "baseline-checkpoint") || HasEvidence(execution, "auto-checkpoint"))) ||
            (preparation is null && (hasGenerated || hasGeneration || HasEvidence(execution, "subtitle-warnings"))))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.runtime_checkpoint_incomplete");
        }
        SourceOnlyGeneratedRuntimeEvidence? generated = preparation is null ? null
            : await ReadGeneratedRuntimeEvidenceAsync(execution, preparation, import, token).ConfigureAwait(false);
        if (hasGeneration)
        {
            _ = await ReadGenerationEvidenceAsync(execution, preparation!, import.AudioTimeline, token, generated).ConfigureAwait(false);
        }
        await ValidateSubtitleWarningsAsync(execution, preparation, generated, token).ConfigureAwait(false);
        // A callback may Read its own evidence while already holding the registration
        // gate. Validate that read, but do not recursively dispatch earlier phases.
        if (runtimeRegistrations.IsInsideCallback) { return; }
        if (ingest is not null) { await RegisterIngestAsync(execution, ingest, token).ConfigureAwait(false); }
        await RegisterTranscriptAsync(execution, project, import, token).ConfigureAwait(false);
        if (analysis is not null) { await RegisterAnalysisAsync(execution, project, import, analysis, token).ConfigureAwait(false); }
        if (preparation is not null) { await RegisterCompositionAsync(execution, preparation, import, token).ConfigureAwait(false); }
        if (generated is not null && callbacks.GeneratedReady is not null)
        {
            await RegisterRuntimeAsync(execution, "generated", ContentDigest.Compute(Utf8.GetBytes(WriteGeneratedRuntimeEvidence(execution, generated))),
                cancellation => callbacks.GeneratedReady(generated, cancellation), token).ConfigureAwait(false);
        }
    }

    private bool HasEvidence(StoredSourceOnlyExecution execution, string stage) =>
        artifacts.GetArtifact(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), stage)) is not null;

    private void EnsureNoLaterRuntimeCheckpoint(StoredSourceOnlyExecution execution)
    {
        if (execution.VersionId is not null || execution.ResultJson is not null ||
            artifacts.GetArtifact(new AnalysisIds(execution.Request.IngestJobId.ToString()).Checkpoint) is not null ||
            new[] { "analysis-evidence", "baseline-checkpoint", "auto-checkpoint", "composition-checkpoint",
                "render-preflight", "automatic-decisions", "silence-detection", "repetition-raw",
                "generated-runtime", "subtitle-warnings", "generation-evidence", "evaluation-receipt", "result-receipt" }
                .Any(stage => HasEvidence(execution, stage)))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.runtime_checkpoint_incomplete");
        }
    }

    internal async Task<StandardCompositionPreparation?> ReadCompositionCheckpointAsync(StoredSourceOnlyExecution execution, CancellationToken token)
    {
        StandardCompositionPreparation? preparation = null;
        try
        {
            foreach (string stage in new[] { "baseline-checkpoint", "auto-checkpoint", "composition-checkpoint" })
            {
                if (!HasEvidence(execution, stage)) { continue; }
                using JsonDocument document = JsonDocument.Parse(await ReadArtifactBytesAsync(
                    StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), stage),
                    execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                RequireShape(root, "schema", "executionId", "stage", "versionId", "digest", "workingCopyRevision", "workingCopy", "settingsDigest");
                CompositionVersion version = versions.GetVersion(EntityId<CompositionVersionIdTag>.Parse(Text(root, "versionId"))) ?? throw new FormatException();
                WorkingCopy copy = CompositionSnapshotJson.ReadWorkingCopy(Text(root, "workingCopy"));
                if (Text(root, "schema") != "source-only.composition-checkpoint.v1" || Text(root, "stage") != stage ||
                    Text(root, "executionId") != execution.Request.ExecutionId.ToString() ||
                    Text(root, "settingsDigest") != execution.Request.SettingsSnapshot.ContentDigest.Hex ||
                    Text(root, "digest") != version.ContentDigest.Hex || version.ProjectId != execution.Request.ProjectId ||
                    version.CreatedByActorId != execution.Request.ActorSid || version.CreatedAt < execution.Request.CreatedAt ||
                    copy.ProjectId != version.ProjectId || copy.BaseVersionId != version.Id || copy.UpdatedAt != version.CreatedAt ||
                    !copy.Elements.SequenceEqual(version.Elements) ||
                    !int.TryParse(Text(root, "workingCopyRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision) || revision < 1)
                {
                    throw new FormatException();
                }
                if (stage != "composition-checkpoint") { continue; }
                preparation = versions.GetStandardComposition(version.Id) ?? throw new FormatException();
                ValidateCheckpointLineage(execution, version);
                var expectedCopy = new WorkingCopy(preparation.PreparedWorkingCopy.Id, version.ProjectId, version.Id,
                    preparation.PreparedWorkingCopy.CreatedAt, version.CreatedAt, version.Elements);
                if (revision != checked(preparation.ExpectedWorkingCopyRevision + 1) ||
                    CompositionSnapshotJson.WriteWorkingCopy(expectedCopy) != Text(root, "workingCopy") ||
                    StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) != execution.Request.SettingsSnapshot.ContentDigest ||
                    (execution.VersionId is not null && (execution.VersionId != version.Id || execution.VersionDigest != version.ContentDigest)))
                {
                    throw new FormatException();
                }
            }
            if (preparation is null && execution.VersionId is not null) { throw new FormatException(); }
            return preparation;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.composition_checkpoint_invalid"); }
    }

    private void ValidateCheckpointLineage(StoredSourceOnlyExecution execution, CompositionVersion version)
    {
        var visited = new HashSet<EntityId<CompositionVersionIdTag>>();
        CompositionVersion current = version;
        while (visited.Count < SourceOnlyExecutionStore.MaximumVersionLineageLength && visited.Add(current.Id))
        {
            if (current.ProjectId != execution.Request.ProjectId) { break; }
            if (current.Id == execution.Request.ParentVersionId)
            {
                if (current.Id == version.Id || current.ContentDigest != execution.Request.ParentDigest ||
                    current.CreatedAt > execution.Request.CreatedAt) { break; }
                return;
            }
            if (current.CreatedAt < execution.Request.CreatedAt) { break; }
            if (current.ParentVersionId is null)
            {
                if (execution.Request.ParentVersionId is null) { return; }
                break;
            }
            CompositionVersion parent = versions.GetVersion(current.ParentVersionId) ?? throw new FormatException();
            if (parent.CreatedAt > current.CreatedAt) { break; }
            current = parent;
        }
        throw new FormatException("The checkpoint is not a descendant of the accepted parent.");
    }

    private async Task ValidateSubtitleWarningsAsync(StoredSourceOnlyExecution execution, StandardCompositionPreparation? preparation,
        SourceOnlyGeneratedRuntimeEvidence? generated, CancellationToken token)
    {
        try
        {
            if (!HasEvidence(execution, "subtitle-warnings"))
            {
                if (generated is not null) { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.subtitle_warnings_missing"); }
                return;
            }
            byte[] bytes = await ReadArtifactBytesAsync(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "subtitle-warnings"),
                execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            RequireShape(root, "schema", "executionId", "mappingRule", "productionVersionId", "settingsDigest", "warnings");
            if (preparation is null || Text(root, "schema") != "source-only.subtitle-warnings.v1" ||
                Text(root, "executionId") != execution.Request.ExecutionId.ToString() || Text(root, "mappingRule") != "source-only.subtitle-projection.v1" ||
                Text(root, "productionVersionId") != preparation.Child.Id.ToString() ||
                Text(root, "settingsDigest") != execution.Request.SettingsSnapshot.ContentDigest.Hex || root.GetProperty("warnings").ValueKind != JsonValueKind.Array ||
                (generated is not null && CanonicalJson.Canonicalize(root.GetProperty("warnings").GetRawText()) !=
                    CanonicalJson.Canonicalize(JsonSerializer.Serialize(generated.Versions.Values.SelectMany(item => item.Build.Warnings).ToArray()))))
            {
                throw new FormatException();
            }
            // Old runs have warnings but no saved subtitle inputs. Validate those
            // records, yet do not invent histories or advertise them as rendered cues.
            foreach (JsonElement warning in root.GetProperty("warnings").EnumerateArray())
            {
                RequireShape(warning, "Key", "Rule", "Reason", "IsUnresolved", "SourceRange", "SourceSpanId");
                if (new[] { "Key", "Rule", "Reason" }.Any(key => string.IsNullOrWhiteSpace(Text(warning, key)) || Text(warning, key).Length > 256) ||
                    !Text(warning, "Rule").StartsWith(StandardSubtitleBuilder.RuleVersion + ".", StringComparison.Ordinal)) { throw new FormatException(); }
                TimeRange? range = warning.GetProperty("SourceRange").ValueKind == JsonValueKind.Null ? null
                    : new TimeRange(SourceOnlyResultInputValidation.ReadTime(warning.GetProperty("SourceRange").GetProperty("Start")),
                        SourceOnlyResultInputValidation.ReadTime(warning.GetProperty("SourceRange").GetProperty("Duration")));
                EntityId<SourceSpanIdTag>? span = warning.GetProperty("SourceSpanId").ValueKind == JsonValueKind.Null ? null
                    : EntityId<SourceSpanIdTag>.Parse(Text(warning.GetProperty("SourceSpanId"), "Value"));
                if (span is not null && !preparation.Plan.LongForm.Provenance.SourceSpans.Any(item => item.Id == span)) { throw new FormatException(); }
                var expected = new StandardSubtitleBuildWarning(Text(warning, "Key"), Text(warning, "Rule"), Text(warning, "Reason"),
                    warning.GetProperty("IsUnresolved").GetBoolean(), range, span);
                if (CanonicalJson.Canonicalize(JsonSerializer.Serialize(expected)) != CanonicalJson.Canonicalize(warning.GetRawText())) { throw new FormatException(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PipelineFailure) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.subtitle_warnings_invalid"); }
    }

    /// <summary>Validated ASR/audio/rights references for the latest execution. Does not rerun any stage.</summary>
    public async Task<SourceOnlyPipelineEvidence> ReadEvidenceAsync(EntityId<ProjectIdTag> projectId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StoredSourceOnlyExecution execution = Read(projectId) ?? throw new InvalidOperationException("The execution is missing.");
        if (options is null) { throw new InvalidOperationException("The native runtime is not configured."); }
        IngestWorkflowRunResult ingest = await CreateIngestWorkflow(new ProcessRunner(), new BinaryVerifier())
            .ReadCompletedAsync(execution.Request.IngestJobId, cancellationToken).ConfigureAwait(false);
        LocatedSource source = ReadAcceptedSource(execution);
        ValidateIngest(execution, source, ingest);
        await ValidateRightsCheckpointAsync(execution, source, cancellationToken).ConfigureAwait(false);
        AnalysisState analysis = await ReadAnalysisAsync(execution, ingest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The analysis checkpoint is missing.");
        StandardCompositionPreparation? preparation = await ReadCompositionCheckpointAsync(execution, cancellationToken).ConfigureAwait(false);
        if (preparation is not null) { ValidateRuntimeComposition(execution, preparation, analysis.Import); }
        if (HasEvidence(execution, "analysis-evidence"))
        {
            _ = await ReadAnalysisEvidenceAsync(ReadProject(projectId), analysis.Import, execution, cancellationToken).ConfigureAwait(false);
        }
        else if (preparation is not null)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_evidence_missing");
        }
        if (preparation is null && HasEvidence(execution, "generated-runtime"))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.composition_checkpoint_missing");
        }
        SourceOnlyGeneratedRuntimeEvidence? generatedRuntime = preparation is null ? null
            : await ReadGeneratedRuntimeEvidenceAsync(execution, preparation, analysis.Import, cancellationToken).ConfigureAwait(false);
        SourceOnlyPipelineGenerationEvidence? generation = HasEvidence(execution, "generation-evidence")
            ? await ReadGenerationEvidenceAsync(execution, preparation ?? throw new PipelineFailure(
                SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.composition_checkpoint_missing"), analysis.Import.AudioTimeline,
                cancellationToken, generatedRuntime).ConfigureAwait(false) : null;
        await ValidateSubtitleWarningsAsync(execution, preparation, generatedRuntime, cancellationToken).ConfigureAwait(false);
        if (execution.ResultJson is not null)
        {
            SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest).ValidateBinding(execution);
        }
        var ids = new AnalysisIds(execution.Request.IngestJobId.ToString());
        SourceOnlyResultArtifactReference Reference(StoredArtifact artifact) =>
            new(artifact.Id, artifact.ContentDigest, artifact.ByteSize, artifact.Kind);
        async Task<SourceOnlyResultArtifactReference?> Optional(EntityId<ArtifactIdTag> id) =>
            artifacts.GetArtifact(id) is null ? null : Reference(await ValidateNativeReferenceAsync(id, projectId, cancellationToken).ConfigureAwait(false));
        return new SourceOnlyPipelineEvidence(execution.Request.ExecutionId,
            Reference(await artifacts.ValidateRegisteredAsync(ids.Checkpoint, cancellationToken).ConfigureAwait(false)),
            Reference(analysis.Audio), Reference(analysis.Timeline), Reference(analysis.Raw), Reference(analysis.Transcript),
            Reference(await artifacts.ValidateRegisteredAsync(source.AttestationArtifactId, cancellationToken).ConfigureAwait(false)),
            await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "analysis-evidence")).ConfigureAwait(false),
            await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "subtitle-warnings")).ConfigureAwait(false),
            await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generation-evidence")).ConfigureAwait(false),
            await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "render-preflight")).ConfigureAwait(false),
            await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "automatic-decisions")).ConfigureAwait(false))
        {
            GeneratedRuntime = await Optional(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generated-runtime")).ConfigureAwait(false),
            Generation = generation,
        };
    }

    private async Task ValidateRightsCheckpointAsync(StoredSourceOnlyExecution execution, LocatedSource source, CancellationToken token)
    {
        byte[] rights = await ReadArtifactBytesAsync(source.AttestationArtifactId, execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
        if (!rights.AsSpan().SequenceEqual(Utf8.GetBytes(CanonicalJson.Canonicalize(source.RightsEvidenceJson))))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.rights_checkpoint_invalid");
        }
    }

    private async Task<StoredArtifact> ValidateNativeReferenceAsync(EntityId<ArtifactIdTag> id, EntityId<ProjectIdTag> projectId, CancellationToken token)
    {
        StoredArtifact artifact = await artifacts.ValidateRegisteredAsync(id, token).ConfigureAwait(false);
        if (artifact.ProjectId != projectId || artifact.Kind != ArtifactKind.Analysis)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.evidence_binding_invalid");
        }
        return artifact;
    }

    public ValueTask DisposeAsync()
    {
        lock (acceptanceGate)
        {
            if (disposalTask is not null) { return new ValueTask(disposalTask); }
            disposed = true;
            disposalTask = DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    internal async Task<SourceOnlyPipelineGenerationEvidence?> ReadGenerationEvidenceAsync(StoredSourceOnlyExecution execution,
        StandardCompositionPreparation preparation, AnalysisAudioTimeline analysisAudio, CancellationToken token,
        SourceOnlyGeneratedRuntimeEvidence? generatedRuntime = null)
    {
        try
        {
            SourceOnlyResultGenerationCheckpoint? checkpoint = await SourceOnlyResultGenerationCheckpoint
                .ReadAsync(artifacts, execution, token).ConfigureAwait(false);
            if (checkpoint is null) { return null; }
            JsonElement payload = checkpoint.Payload;
            bool hasRuntimeBinding = payload.TryGetProperty("generatedRuntime", out JsonElement runtimeBinding);
            string[] keys = ["schema", "executionId", "settingsDigest", "disposition", "outputs", "Omissions", "Warnings", "StagingCleanupRequired"];
            RequireShape(payload, hasRuntimeBinding ? [.. keys, "generatedRuntime"] : keys);
            if (hasRuntimeBinding)
            {
                StoredArtifact savedRuntime = await ValidateReferenceAsync(runtimeBinding,
                    StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generated-runtime"), execution.Request.ProjectId, token).ConfigureAwait(false);
                if (generatedRuntime is null || savedRuntime.ContentDigest != ContentDigest.Compute(Utf8.GetBytes(
                    WriteGeneratedRuntimeEvidence(execution, generatedRuntime)))) { throw new FormatException(); }
            }
            if (!Enum.TryParse(Text(payload, "disposition"), out StandardArtifactGenerationDisposition disposition) ||
                disposition is not (StandardArtifactGenerationDisposition.Completed or StandardArtifactGenerationDisposition.GeneratedWithOmissions) ||
                disposition.ToString() != Text(payload, "disposition") || preparation.Child.ProjectId != execution.Request.ProjectId ||
                analysisAudio.ProjectId != execution.Request.ProjectId || analysisAudio.SourceAssetId != execution.Request.SourceCandidateId ||
                StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) != execution.Request.SettingsSnapshot.ContentDigest)
            {
                throw new FormatException();
            }
            StoredArtifact original = await artifacts.ValidateRegisteredAsync(analysisAudio.SourceOriginalArtifactId, token).ConfigureAwait(false);
            if (original.ProjectId != execution.Request.ProjectId || original.Kind != ArtifactKind.Original ||
                original.ContentDigest != analysisAudio.SourceContentDigest || original.ByteSize != analysisAudio.SourceByteSize)
            {
                throw new FormatException();
            }
            string Id(JsonElement value, string key) => SourceOnlyResultEvidenceStore.NativeId(value, key);
            string Digest(JsonElement value, string key) => SourceOnlyResultEvidenceStore.NativeDigest(value, key);
            long Integer(JsonElement value, string key) => SourceOnlyResultEvidenceStore.Integer(value.GetProperty(key));
            var outputs = new List<SourceOnlyGeneratedArtifactEvidence>();
            var ids = new HashSet<EntityId<ArtifactIdTag>>();
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement output in payload.GetProperty("outputs").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                RequireShape(output, "role", "artifact", "version", "graph", "manifest", "binding");
                string role = Text(output, "role");
                var identity = SourceOnlyOutputEvaluation.ParseRole(role); // Reject unknown variants; do not filter them.
                JsonElement reference = output.GetProperty("artifact");
                RequireShape(reference, "id", "digest", "byteSize");
                var id = EntityId<ArtifactIdTag>.Parse(Text(reference, "id"));
                StoredArtifact artifact = await artifacts.ValidateRegisteredAsync(id, token).ConfigureAwait(false);
                if (!ids.Add(id) || !roles.Add(role) || artifact.Kind != ArtifactKind.Render || artifact.ProjectId != execution.Request.ProjectId ||
                    Text(reference, "id") != id.ToString() || Text(reference, "digest") != artifact.ContentDigest.Hex ||
                    Text(reference, "byteSize") != artifact.ByteSize.ToString(CultureInfo.InvariantCulture))
                {
                    throw new FormatException();
                }
                CompositionVersion version = CompositionSnapshotJson.ReadVersion(Text(output, "version"));
                CompositionVersion storedVersion = versions.GetVersion(version.Id) ?? throw new FormatException();
                if (CompositionSnapshotJson.WriteVersion(storedVersion) != Text(output, "version") ||
                    version.ProjectId != execution.Request.ProjectId || version.CreatedAt != preparation.Child.CreatedAt ||
                    version.CreatedByActorId != preparation.Child.CreatedByActorId)
                {
                    throw new FormatException();
                }
                JsonElement graph = output.GetProperty("graph"), manifest = output.GetProperty("manifest"), binding = output.GetProperty("binding");
                ContentDigest graphDigest = SourceOnlyResultEvidenceStore.PayloadDigest(graph);
                ContentDigest manifestDigest = SourceOnlyResultEvidenceStore.PayloadDigest(manifest);
                var profileId = EntityId<RenderProfileIdTag>.Parse(Id(graph, "ProfileId"));
                var profileDigest = new ContentDigest(Digest(graph, "ProfileDigest"));
                if (Text(binding, "Role") != role || Id(binding, "ArtifactId") != id.ToString() ||
                    Id(binding, "PlanId") != preparation.Plan.Id.ToString() || Digest(binding, "PlanDigest") != preparation.Plan.PlanDigest.Hex ||
                    Id(binding, "VersionId") != version.Id.ToString() || Digest(binding, "VersionDigest") != version.ContentDigest.Hex ||
                    Digest(binding, "SettingsSnapshotDigest") != execution.Request.SettingsSnapshot.ContentDigest.Hex ||
                    Id(binding, "SourceArtifactId") != original.Id.ToString() ||
                    Digest(binding, "SourcePtsIndexDigest") != analysisAudio.SourceVideoPtsIndexDigest.Hex ||
                    Id(binding, "GraphId") != Id(graph, "Id") || Digest(binding, "GraphDigest") != graphDigest.Hex ||
                    Digest(binding, "OutputDigest") != artifact.ContentDigest.Hex || Integer(binding, "OutputByteSize") != artifact.ByteSize ||
                    Id(graph, "CompositionVersionId") != version.Id.ToString() || Digest(graph, "CompositionVersionDigest") != version.ContentDigest.Hex ||
                    Id(manifest, "ProjectId") != execution.Request.ProjectId.ToString() ||
                    Id(manifest, "CompositionVersionId") != version.Id.ToString() || Digest(manifest, "CompositionVersionDigest") != version.ContentDigest.Hex ||
                    Id(manifest, "ProfileId") != profileId.ToString() || Digest(manifest, "ProfileDigest") != profileDigest.Hex ||
                    Digest(manifest, "ConfigurationDigest") != SourceOnlyResultEvidenceStore.PayloadDigest(binding).Hex ||
                    manifest.GetProperty("Outputs").GetArrayLength() != 1)
                {
                    throw new FormatException();
                }
                JsonElement manifestOutput = manifest.GetProperty("Outputs")[0];
                bool image = identity.ThumbnailPlanId is not null;
                string mediaType = identity.Role switch
                {
                    SourceOnlyResultRole.SubRip => "application/x-subrip",
                    SourceOnlyResultRole.WebVtt => "text/vtt",
                    SourceOnlyResultRole.LandscapeThumbnail or SourceOnlyResultRole.VerticalThumbnail => "image/png",
                    _ => "video/mp4",
                };
                if (Digest(manifestOutput, "Sha256") != artifact.ContentDigest.Hex || Integer(manifestOutput, "ByteSize") != artifact.ByteSize ||
                    Text(manifestOutput, "StorageReference") != artifact.StorageReference || Text(manifestOutput, "MediaType") != mediaType ||
                    Text(manifestOutput, "ArtifactType") != (image ? "thumbnail" : identity.Role is SourceOnlyResultRole.SubRip or SourceOnlyResultRole.WebVtt ? "subtitle" : "video"))
                {
                    throw new FormatException();
                }
                if (image)
                {
                    JsonElement frame = binding.GetProperty("Execution").GetProperty("Thumbnail");
                    var frameEvidence = new SourceOnlyResultFrameEvidence(EntityId<AssetIdTag>.Parse(Id(frame, "SourceAssetId")),
                        EntityId<SourceSpanIdTag>.Parse(Id(frame, "SourceSpanId")), new ContentDigest(Digest(frame, "SourceDigest")),
                        new ContentDigest(Digest(frame, "SourcePtsIndexDigest")), checked((int)Integer(frame, "SourceFrameSequence")),
                        SourceOnlyResultInputValidation.ReadTime(frame.GetProperty("SourcePresentationTime")),
                        SourceOnlyResultInputValidation.ReadTime(frame.GetProperty("SourceFrameDuration")),
                        checked((int)Integer(frame.GetProperty("SourceGeometry"), "Width")), checked((int)Integer(frame.GetProperty("SourceGeometry"), "Height")),
                        checked((int)Integer(frame, "Width")), checked((int)Integer(frame, "Height")), Text(frame, "ExtractionRule"));
                    // A descriptor for deterministic identity validation only; never an
                    // evaluated result, fake PNG measurement or approval.
                    var descriptor = new SourceOnlyResultArtifact(identity.Role, null,
                        new(artifact.Id, artifact.ContentDigest, artifact.ByteSize, artifact.Kind), version.Id, version.ContentDigest,
                        new(original.Id, original.ContentDigest, original.ByteSize, original.Kind), graphDigest, manifestDigest,
                        [], null, null, null, thumbnailPlanId: identity.ThumbnailPlanId, frameEvidence: frameEvidence,
                        profileId: profileId, profileDigest: profileDigest);
                    SourceOnlyResultImageExpected expected = SourceOnlyResultImageValidation.Expected(preparation, descriptor,
                        execution.Request.SettingsSnapshot);
                    if (Id(frame, "ThumbnailPlanId") != identity.ThumbnailPlanId!.ToString() ||
                        frameEvidence.SourcePtsIndexDigest != analysisAudio.SourceVideoPtsIndexDigest ||
                        CompositionSnapshotJson.WriteVersion(expected.Version) != Text(output, "version"))
                    {
                        throw new FormatException();
                    }
                }
                else
                {
                    CompositionVersion expected = identity.RenditionId is null ? preparation.Child
                        : StandardRenderGraphFactory.CreateShortVersion(preparation, identity.RenditionId);
                    RenderProfile profile = StandardRenderGraphFactory.GetProfile(preparation.Plan,
                        identity.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn, identity.RenditionId);
                    if (CompositionSnapshotJson.WriteVersion(expected) != Text(output, "version") ||
                        profileId != profile.Id || profileDigest != profile.ProfileDigest)
                    {
                        throw new FormatException();
                    }
                }
                JsonElement[] mediaInputs = graph.GetProperty("Inputs").EnumerateArray().Where(input =>
                    Integer(input, "Kind") == (long)RenderInputKind.Video || Integer(input, "Kind") == (long)RenderInputKind.Audio).ToArray();
                if (mediaInputs.Length == 0 || mediaInputs.Any(input => Digest(input, "ContentDigest") != original.ContentDigest.Hex ||
                    Integer(input, "ByteSize") != original.ByteSize)) { throw new FormatException(); }
                RenderGraph parsedGraph = SourceOnlyResultInputValidation.ReadGraph(graph);
                TimelineMap timeline = TimelineMapBuilder.Build(parsedGraph.TimelineMapId, version);
                if (RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline) != parsedGraph.TimelineMapDigest) { throw new FormatException(); }
                SourceOnlyResultInputValidation.ValidateManifestGraph(manifest, graph);
                if (!image && generatedRuntime is not null)
                {
                    ValidateGeneratedSubtitleBinding(generatedRuntime, identity, version, timeline, parsedGraph, binding);
                }
                outputs.Add(new(identity.Role, identity.RenditionId, identity.ThumbnailPlanId,
                    new(artifact.Id, artifact.ContentDigest, artifact.ByteSize, artifact.Kind), version, profileId, profileDigest, graphDigest, manifestDigest));
            }
            if (outputs.Count is 0 or > SourceOnlyResult.MaximumArtifactCount ||
                outputs.Count(item => item.Role == SourceOnlyResultRole.LandscapeClean) != 1) { throw new FormatException(); }
            StandardGenerationOmission[] omissions = payload.GetProperty("Omissions").EnumerateArray()
                .Select(item => new StandardGenerationOmission(Text(item, "Role"), Text(item, "Reason"))).ToArray();
            SourceOnlyResultGenerationWarning[] warnings = payload.GetProperty("Warnings").EnumerateArray()
                .Select(item => new SourceOnlyResultGenerationWarning(Text(item, "Role"), Text(item, "Code"))).ToArray();
            return new(checkpoint.Artifact, disposition, outputs.AsReadOnly(), Array.AsReadOnly(omissions),
                Array.AsReadOnly(warnings), payload.GetProperty("StagingCleanupRequired").GetBoolean());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.generation_checkpoint_invalid"); }
    }

    private static void ValidateGeneratedSubtitleBinding(SourceOnlyGeneratedRuntimeEvidence runtime,
        SourceOnlyResultRoleIdentity identity, CompositionVersion version, TimelineMap timeline, RenderGraph graph, JsonElement binding)
    {
        StandardSubtitleInput subtitles = identity.RenditionId is null ? runtime.LandscapeSubtitles.Subtitles
            : runtime.ShortForms.Single(item => item.ShortFormId == identity.RenditionId).Subtitles;
        StandardRenderGraphFactory.ValidateSubtitleProjection(runtime.Preparation, subtitles, identity.RenditionId);
        SubtitleEditProjectionEvidence? projection = subtitles.Projection;
        if (projection is not null)
        {
            SourceOnlyGeneratedSubtitleRuntimeEvidence generated = runtime.Versions[version.Id];
            if (!ReferenceEquals(generated.SubtitleProjection, projection)) { throw new FormatException(); }
            projection.ValidateOutput(generated);
        }
        var revisions = subtitles.Revisions.Select(item => new
        {
            item.Id,
            item.ContentDigest,
            item.SourceCandidateId,
            item.SourceRevisionId,
            item.SourceRevisionDigest,
        }).ToArray();
        ContentDigest configuration = projection is null ? StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v2",
            subtitles.DisplayProfile,
            subtitles.AssProfile,
            subtitles.Warnings,
            revisions,
        }) : StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v3",
            subtitles.DisplayProfile,
            subtitles.AssProfile,
            subtitles.Warnings,
            revisions,
            SubtitleEditProjection = projection.CanonicalJson,
            SubtitleEditProjectionDigest = projection.ContentDigest,
        });
        SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(subtitles.DisplayProfile,
            subtitles.Revisions.Select(item => SubtitleDisplayMapper.MapSource(item.Snapshot)).ToArray(), []);
        var cues = display.DisplayedCues.Select(item => SubtitleDisplayMapper.MapForExport(
            subtitles.Revisions.Single(revision => revision.Snapshot.Cue.Id == item.Cue.Id).Snapshot, item)).ToArray();
        if (SourceOnlyResultEvidenceStore.NativeDigest(binding, "SubtitleConfigurationDigest") != configuration.Hex ||
            SourceOnlyResultEvidenceStore.NativeDigest(binding, "DisplayCueDigest") != StandardRenderGraphFactory.Digest(cues).Hex ||
            graph.Fonts.Any(font => font.Name != runtime.Font.Name || font.FileDigest != runtime.Font.FileDigest))
        {
            throw new FormatException();
        }
        StandardCompositionPlan plan = runtime.Preparation.Plan;
        RenderProfile profile = StandardRenderGraphFactory.GetProfile(plan,
            identity.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn, identity.RenditionId);
        LayoutSpec layout = StandardRenderGraphFactory.GetLayout(plan, identity.RenditionId);
        var context = new RenderGraphValidationContext(plan.LongForm.Project, version, timeline,
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline), profile, layout,
            identity.RenditionId is null ? plan.LongForm.Profile.LayoutDigest : StandardRenderGraphFactory.Digest(layout),
            identity.RenditionId is null ? plan.LongForm.Profile.TemplateReference : plan.ShortFormProfile.TemplateReference,
            plan.LongForm.Provenance.SourceSpans.Concat(plan.ShortForms.SelectMany(item => item.SourceSpans))
                .Concat(subtitles.Candidates.Select(item => item.SourceSpan)).DistinctBy(item => item.Id).ToArray(),
            projection is null ? plan.LongForm.Provenance.TrustedEditorialRecordIds
                : plan.LongForm.Provenance.TrustedEditorialRecordIds.Concat(projection.TrustedEditorialRecordIds).Distinct().ToArray(),
            graph.Inputs.Select(input => new RenderArtifactEvidence(input.Id, input.ContentDigest, input.ByteSize)).ToArray(), graph.Fonts);
        if (!RenderGraphValidator.Validate(graph, context).IsValid ||
            (profile.SubtitleBurnIn && !graph.Nodes.Where(item => item.Kind == RenderNodeKind.Subtitle)
                .Select(item => item.Cue!.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal)
                .SequenceEqual(cues.Select(item => item.Cue.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal))))
        {
            throw new FormatException();
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (recoveryCancellation is not null)
            {
                try { await recoveryCancellation.CancelAsync().ConfigureAwait(false); }
                catch (AggregateException) { }
            }
            if (recoveryTask is not null) { _ = await recoveryTask.ConfigureAwait(false); }
            await taskOwner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            recoveryCancellation?.Dispose();
            try { await databaseLease.DisposeAsync().ConfigureAwait(false); }
            finally { await workspaceLease.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task<SourceOnlyPipelineRun> ExecuteOwnedAsync(EntityId<SourceOnlyExecutionIdTag> executionId,
        CancellationToken token)
    {
        try
        {
            StoredSourceOnlyExecution execution = RequireExecution(executionId);
            if (IsTerminal(execution.Phase)) { return new(executionId, execution, null); }
            if (executeForTesting is not null)
            {
                await executeForTesting(execution, token).ConfigureAwait(false);
                throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.no_production_result");
            }
            await RunProductionAsync(execution, token).ConfigureAwait(false);
            return new(executionId, RequireExecution(executionId), null);
        }
        catch (Exception exception)
        {
            try
            {
                StoredSourceOnlyExecution current = RequireExecution(executionId);
                // The owned delegate/coordinator has exited. A failure to persist its
                // terminal attempt must not strand Running behind a terminal execution.
                SettleInterrupted(current, current.Request.RootJobId, "job.terminal_persistence_failed");
                SettleInterrupted(current, current.Request.IngestJobId, "job.terminal_persistence_failed");
                CancelQueued(current.Request.RootJobId);
                CancelQueued(current.Request.IngestJobId);
                if (!IsTerminal(current.Phase))
                {
                    PipelineFailure? classified = exception as PipelineFailure;
                    bool ambiguity = (classified is null && current.Phase >= SourceOnlyExecutionPhase.Rendering) ||
                        exception is AudioExtractionException { ReconciliationRequired: true } or
                            ArtifactStoreException { ReconciliationRequired: true } ||
                        (RequireJob(current.Request.IngestJobId).Job.State != JobState.Succeeded && IngestHasPromotedOriginal(current));
                    SourceOnlyExecutionPhase phase = ambiguity ? SourceOnlyExecutionPhase.ReconciliationRequired
                        : classified is not null ? classified.Phase
                        : exception is SourceOnlySourceException ? SourceOnlyExecutionPhase.ReselectionRequired
                        : exception is OperationCanceledException && token.IsCancellationRequested ? SourceOnlyExecutionPhase.Canceled
                        : SourceOnlyExecutionPhase.Failed;
                    string? code = phase == SourceOnlyExecutionPhase.Canceled ? null
                        : ambiguity ? "source_only.reconciliation_required"
                        : exception is PipelineFailure failure ? failure.Code
                        : exception is SourceOnlySourceException source ? source.Code
                        : exception is LocalAnalysisWorkerException worker ? LocalAnalysisWorker.GetSafeFailureCode(worker)
                        : exception is StandardGenerationException { Reason: StandardGenerationFailureReason.UnsupportedSourceTiming }
                            ? "source_only.unsupported_source_timing"
                        : exception is SubtitleSegmentationException ? "source_only.subtitle_source_mapping_unsupported"
                        : exception is AudioExtractionException { Reason: AudioExtractionFailureReason.NoAudioStream }
                            ? "source_only.audio_stream_absent"
                        : "source_only.execution_failed";
                    SourceOnlyResult? failedResult = classified?.Result;
                    if (failedResult is not null)
                    {
                        failedResult.ValidateBinding(current);
                        current = executions.UpdateState(executionId, current.Revision, phase, failedResult.CanonicalJson,
                            failedResult.Binding.VersionId, failedResult.Binding.VersionDigest, code);
                    }
                    else { current = SetPhase(executionId, phase, code); }
                }
                return new(executionId, current, null);
            }
            catch
            {
                // No raw exception/path leaves a background task, even if the state store
                // itself is unavailable. Host can surface this stable infrastructure code.
                return new(executionId, null, "source_only.state_persistence_failed");
            }
        }
    }

    internal static async Task<T> IngestBeforeRootAsync<T>(Func<CancellationToken, Task> ingest,
        Func<CancellationToken, Task<T>> root, CancellationToken token)
    {
        await ingest(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await root(token).ConfigureAwait(false);
    }

    private async Task RunProductionAsync(StoredSourceOnlyExecution execution, CancellationToken token)
    {
        SourceOnlyRuntimeOptions runtime = options ?? throw new InvalidOperationException("The native runtime is not configured.");
        var processRunner = new ProcessRunner();
        var verifier = new BinaryVerifier();
        using HeldInputs heldTools = await HoldVerifiedInputsAsync(runtime, verifier, token).ConfigureAwait(false);
        IngestWorkflow ingestWorkflow = CreateIngestWorkflow(processRunner, verifier);
        LocatedSource source = ReadAcceptedSource(execution);
        IngestWorkflowRunResult? ingest = null;
        StandardCompositionPreparation? preparation = null;
        SourceOnlyResult? result = null;
        Exception? stageFailure = null;

        JobCoordinatorRunResult root = await IngestBeforeRootAsync(
            async cancellation =>
            {
                if (RequireJob(execution.Request.IngestJobId).Job.State == JobState.Succeeded)
                {
                    ingest = await ingestWorkflow.ReadCompletedAsync(execution.Request.IngestJobId, cancellation).ConfigureAwait(false);
                }
                else
                {
                    if (execution.Request.ParentVersionId is not null ||
                        RequireJob(execution.Request.IngestJobId).Job.State != JobState.Queued)
                    {
                        throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.ingest_not_queued");
                    }
                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Ingesting);
                    using FileStream selected = await locator.OpenVerifiedAsync(source, cancellation).ConfigureAwait(false);
                    if (!candidates.RestoreVerifiedSelectionForIngest(source.CandidateId, source.Path))
                    {
                        throw new SourceOnlySourceException("source_only.candidate_handoff_conflict");
                    }
                    _ = await SaveJsonAsync(execution, source.AttestationArtifactId, source.RightsEvidenceJson,
                        "rights-attestation", cancellation).ConfigureAwait(false);
                    string seed = execution.Request.IngestJobId.ToString();
                    // Original ID MUST equal the selected candidate UUID for QuickStart's existing source binding.
                    var request = new IngestWorkflowRequest(execution.Request.IngestJobId, execution.Request.ProjectId,
                        source.CandidateId, EntityId<ArtifactIdTag>.From(source.CandidateId.Value),
                        StableId<ArtifactIdTag>(seed, "proxy"), StableId<ArtifactIdTag>(seed, "waveform"),
                        StableId<ArtifactIdTag>(seed, "sprite-image"), StableId<ArtifactIdTag>(seed, "sprite-index"),
                        MediaTypeFor(source.FileName), source.Rights, new IngestValidationRequirements(), runtime.OperationTimeout);
                    ingest = await ingestWorkflow.RunAsync(request, cancellationToken: cancellation).ConfigureAwait(false);
                }
                ValidateIngest(execution, source, ingest);
                await RegisterIngestAsync(execution, ingest, cancellation).ConfigureAwait(false);
            },
            cancellation => coordinator.RunAsync(execution.Request.RootJobId, async (context, stageToken) =>
            {
                try
                {
                    IngestWorkflowRunResult completedIngest = ingest!;
                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Analyzing);
                    context.ReportProgress(1, 6, "source_only.analysis");
                    Project project = BindProjectSource(execution, completedIngest.Asset!);
                    AnalysisState analysis = await GetOrCreateAnalysisAsync(execution, completedIngest,
                        processRunner, verifier, stageToken).ConfigureAwait(false);
                    SourceOnlyAnalysisEvidence analysisEvidence = BuildAnalysis(project, analysis.Import, execution,
                        Now(analysis.Import.Revision.CreatedAt), stageToken);
                    _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "analysis-evidence"),
                        CanonicalJson.Canonicalize(JsonSerializer.Serialize(analysisEvidence)), "analysis-evidence", stageToken).ConfigureAwait(false);
                    await RegisterAnalysisAsync(execution, project, analysis.Import, analysisEvidence, stageToken).ConfigureAwait(false);

                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Composing);
                    context.ReportProgress(2, 6, "source_only.composition");
                    string originalPath = artifacts.GetValidatedRegisteredArtifactPath(completedIngest.Original!.Artifact.Id);
                    using FileStream heldOriginal = OpenHeldFile(originalPath);
                    _ = await artifacts.ValidateRegisteredAsync(completedIngest.Original.Artifact.Id, stageToken).ConfigureAwait(false);
                    SourceDisplayGeometry geometry;
                    try
                    {
                        geometry = await StandardSourceGeometry.ProbeAsync(completedIngest.Original.MediaInfo,
                            originalPath, runtime.FfprobePath, processRunner, runtime.OperationTimeout, stageToken).ConfigureAwait(false);
                    }
                    catch (StandardGenerationException)
                    {
                        throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.source_geometry_unavailable");
                    }
                    DefaultSettingsProfile profile = settings.GetProfile(execution.Request.SettingsSnapshot.ProfileId,
                        execution.Request.SettingsSnapshot.ProfileVersion)
                        ?? throw new InvalidOperationException("The accepted settings profile is unavailable.");
                    if (profile.ContentDigest != execution.Request.SettingsSnapshot.ProfileDigest ||
                        profile.ContentDigest != runtime.BundledSettingsProfile.ContentDigest)
                    {
                        throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.settings_profile_mismatch");
                    }
                    CompositionState composition = await ComposeAsync(execution, completedIngest, project, analysis,
                        analysisEvidence, profile, geometry, stageToken).ConfigureAwait(false);
                    preparation = composition.Preparation;
                    await RegisterCompositionAsync(execution, preparation, analysis.Import, stageToken).ConfigureAwait(false);

                    SourceOnlyGeneratedRuntimeEvidence generatedRuntime = BuildGeneratedRuntimeEvidence(preparation,
                        analysis.Import, profile, execution.Request.SettingsSnapshot, runtime.Font.Evidence,
                        execution.Request.ActorSid, Now(preparation.Child.CreatedAt), stageToken);
                    generatedRuntime = await ApplyCapturedSubtitleEditsAsync(execution, generatedRuntime, stageToken).ConfigureAwait(false);
                    StandardSubtitleBuildResult subtitles = generatedRuntime.LandscapeSubtitles;
                    IReadOnlyList<StandardShortArtifactInput> shortInputs = generatedRuntime.ShortForms;
                    await CheckpointGeneratedRuntimeAsync(execution, generatedRuntime, stageToken).ConfigureAwait(false);

                    var tool = new FfmpegToolBinding(new BinaryVerificationRequest("ffmpeg", runtime.FfmpegPath,
                        new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), DecodeValidator.CanonicalFfmpegByteSize));
                    var toolEvidence = new RenderToolEvidence("ffmpeg", DecodeValidator.CanonicalPackageVersion,
                        DecodeValidator.CanonicalPackageId, tool.VerificationRequest.ExpectedDigest, null);
                    var modelEvidence = new RenderModelEvidence("Systran", "faster-whisper-small",
                        "536b0662742c02347bc0e980a01041f333bce120", heldTools.ModelWeightsDigest);
                    var generationRequest = new StandardArtifactGenerationRequest(preparation, completedIngest, geometry,
                        profile, execution.Request.SettingsSnapshot, tool, toolEvidence, modelEvidence, runtime.Font,
                        subtitles.Subtitles, shortInputs, runtime.ApplicationVersion, runtime.ApplicationDigest, runtime.OperationTimeout,
                        music: composition.Music);
                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Rendering);
                    context.ReportProgress(3, 6, "source_only.rendering");
                    var generator = new StandardArtifactGenerator(artifacts, versions, processRunner, workspace, runtime.FfprobePath);
                    StandardArtifactGenerationResult generated = await generator.GenerateAsync(generationRequest, stageToken).ConfigureAwait(false);
                    if (generated.Disposition is not (StandardArtifactGenerationDisposition.Completed or StandardArtifactGenerationDisposition.GeneratedWithOmissions))
                    {
                        result = GenerationFailureResult(execution, preparation, generated);
                        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "result-receipt"),
                            result.CanonicalJson, "result-receipt", CancellationToken.None).ConfigureAwait(false);
                        if (generated.Disposition == StandardArtifactGenerationDisposition.Cancelled && stageToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(stageToken);
                        }
                        throw new PipelineFailure(generated.Disposition == StandardArtifactGenerationDisposition.ReconciliationRequired || generated.StagingCleanupRequired
                            ? SourceOnlyExecutionPhase.ReconciliationRequired
                            : generated.Disposition == StandardArtifactGenerationDisposition.Cancelled
                                ? SourceOnlyExecutionPhase.Canceled : SourceOnlyExecutionPhase.Failed,
                            generated.FailureReason == StandardGenerationFailureReason.UnsupportedSourceTiming
                                ? "source_only.unsupported_source_timing" : "source_only.generation_failed", result);
                    }
                    stageToken.ThrowIfCancellationRequested();
                    foreach (CompositionVersion generatedVersion in generated.CompositionVersions)
                    {
                        if (generatedVersion.ProjectId != execution.Request.ProjectId ||
                            (generatedVersion.Id != preparation.Child.Id && generatedVersion.ParentVersionId != preparation.Child.Id))
                        {
                            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.generated_version_mismatch");
                        }
                        AppendExactVersion(generatedVersion);
                    }
                    _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generation-evidence"),
                        CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
                        {
                            schema = "source-only.generation-evidence.v1",
                            executionId = execution.Request.ExecutionId.ToString(),
                            settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
                            generatedRuntime = ArtifactReference(artifacts.GetArtifact(StableId<ArtifactIdTag>(
                                execution.Request.ExecutionId.ToString(), "generated-runtime"))
                                ?? throw new InvalidOperationException("The generated subtitle checkpoint is missing.")),
                            disposition = generated.Disposition.ToString(),
                            outputs = generated.Artifacts.Select(item => new
                            {
                                role = item.BindingEvidence.Role,
                                artifact = ArtifactReference(item.Artifact),
                                version = CompositionSnapshotJson.WriteVersion(item.ValidationContext.CompositionVersion),
                                graph = item.Graph,
                                manifest = item.Manifest,
                                binding = item.BindingEvidence,
                            }).ToArray(),
                            generated.Omissions,
                            generated.Warnings,
                            generated.StagingCleanupRequired,
                        })), "generation-evidence", stageToken).ConfigureAwait(false);
                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Evaluating);
                    context.ReportProgress(4, 6, "source_only.evaluation");
                    SourceOnlyResult evaluated;
                    try
                    {
                        evaluated = await evaluate!(RequireExecution(execution.Request.ExecutionId), generated,
                            profile, execution.Request.SettingsSnapshot, stageToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stageToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        // Includes an evaluator that has not yet implemented a generator's
                        // new artifact role. Keep EVERY actual artifact reference; do not
                        // silently filter thumbnails/sidecars to manufacture a passing set.
                        result = new SourceOnlyResult(new SourceOnlyResultBinding(execution.Request.ProjectId,
                            execution.Request.ExecutionId, execution.Request.RootJobId,
                            execution.Request.SettingsSnapshot.ContentDigest, preparation.Child.Id, preparation.Child.ContentDigest),
                            generated.Disposition, [], [], [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.BindingMismatch)],
                            generated.Artifacts.Select(item => new SourceOnlyResultArtifactReference(item.Artifact.Id,
                                item.Artifact.ContentDigest, item.Artifact.ByteSize, item.Artifact.Kind)).ToArray(), [], null,
                            generated.StagingCleanupRequired,
                            generated.Warnings.Select(item => new SourceOnlyResultGenerationWarning(item.Role, item.Code)).ToArray());
                        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "result-receipt"),
                            result.CanonicalJson, "result-receipt", CancellationToken.None).ConfigureAwait(false);
                        throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired,
                            "source_only.output_evaluation_contract_failed", result);
                    }
                    evaluated.ValidateBinding(RequireExecution(execution.Request.ExecutionId));
                    if (evaluated.Binding.VersionId != preparation.Child.Id || evaluated.Binding.VersionDigest != preparation.Child.ContentDigest)
                    {
                        throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.result_version_mismatch");
                    }
                    result = BindTranscriptResult(evaluated, new SourceOnlyResultTranscriptReference(analysis.Import.Revision.Id,
                        analysis.Transcript.ContentDigest, completedIngest.Original.Artifact.Id));
                    _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "evaluation-receipt"),
                        result.CanonicalJson, "evaluation-receipt", stageToken).ConfigureAwait(false);
                    if (result.ReconciliationRequired || result.StagingCleanupRequired)
                    {
                        throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.manual_resolution_required", result);
                    }
                    if (result.HasBlockingFailures)
                    {
                        throw new PipelineFailure(result.ReconciliationRequired ? SourceOnlyExecutionPhase.ReconciliationRequired
                            : SourceOnlyExecutionPhase.Failed, "source_only.output_evaluation_failed", result);
                    }
                    // Warning/NotEvaluated is not blocking QC. Preserve the typed
                    // evaluation result and its warnings through actual local delivery.
                    _ = SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.Delivering);
                    context.ReportProgress(5, 6, "source_only.delivering");
                    var resultService = new SourceOnlyResultService(artifacts,
                        new AutomatedEvaluationStore(runtime.DatabasePath), new QuickStartWorkflowStore(runtime.DatabasePath), projects);
                    StoredSourceOnlyExecution delivering = RequireExecution(execution.Request.ExecutionId);
                    result = await DeliverResultAsync(delivering, result, deliver,
                        (value, cancellation) => resultService.ValidateAsync(value, delivering, cancellation), stageToken).ConfigureAwait(false);
                    _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "result-receipt"),
                        result.CanonicalJson, "result-receipt", CancellationToken.None).ConfigureAwait(false);
                    context.ReportProgress(6, 6, "source_only.stages_completed");
                    context.RecordHeartbeat();
                }
                catch (OperationCanceledException exception) when (stageToken.IsCancellationRequested)
                {
                    stageFailure = exception;
                    throw new OperationCanceledException(stageToken);
                }
                catch (Exception exception) { stageFailure = exception; throw; }
            }, cancellation), token).ConfigureAwait(false);

        if (root.Snapshot.State != JobState.Succeeded || preparation is null || result is null)
        {
            if (root.Snapshot.State == JobState.Cancelled)
            {
                throw new PipelineFailure(result is { ReconciliationRequired: true } or { StagingCleanupRequired: true }
                    ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Canceled,
                    "source_only.canceled", result);
            }
            throw stageFailure ?? new PipelineFailure(root.Snapshot.State == JobState.Cancelled
                ? SourceOnlyExecutionPhase.Canceled : SourceOnlyExecutionPhase.Failed, "source_only.root_not_succeeded");
        }
        // Completion receipt is a commit point: a late cancellation cannot relabel finished
        // side effects. Recovery can finish this exact write if the process dies here.
        StoredSourceOnlyExecution current = RequireExecution(execution.Request.ExecutionId);
        _ = executions.UpdateState(current.Request.ExecutionId, current.Revision, SourceOnlyExecutionPhase.Completed,
            result.CanonicalJson, preparation.Child.Id, preparation.Child.ContentDigest);
    }

    internal static SourceOnlyResult BindTranscriptResult(SourceOnlyResult evaluated, SourceOnlyResultTranscriptReference transcript)
    {
        if (evaluated.TranscriptReferences.Any(item => item != transcript))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.result_transcript_mismatch");
        }
        return new SourceOnlyResult(evaluated.Binding, evaluated.GenerationDisposition,
            evaluated.Artifacts, evaluated.Omissions, evaluated.Failures, evaluated.ReconciliationArtifacts,
            [transcript], evaluated.PrimaryWorkflow, evaluated.StagingCleanupRequired,
            evaluated.GenerationWarnings, evaluated.NativeReconciliationAttempts);
    }

    internal static async Task<SourceOnlyResult> DeliverResultAsync(StoredSourceOnlyExecution execution,
        SourceOnlyResult evaluated,
        Func<StoredSourceOnlyExecution, SourceOnlyResult, CancellationToken, Task<SourceOnlyResult>>? delivery,
        Func<SourceOnlyResult, CancellationToken, Task<SourceOnlyResultValidation>> validate,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        evaluated.ValidateBinding(execution);
        if (evaluated.ReconciliationRequired || evaluated.StagingCleanupRequired)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.manual_resolution_required", evaluated);
        }
        if (evaluated.HasBlockingFailures)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.output_evaluation_failed", evaluated);
        }
        if (delivery is null)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.delivery_unavailable", evaluated);
        }
        if (!(await validate(evaluated, token).ConfigureAwait(false)).IsValid)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.result_reconciliation_required", evaluated);
        }
        token.ThrowIfCancellationRequested();
        try
        {
            SourceOnlyResult delivered = await delivery(execution, evaluated, token).ConfigureAwait(false);
            delivered.ValidateBinding(execution);
            // Delivery may append receipts/workflow state, not filter outputs or clear QC.
            if (delivered.Binding != evaluated.Binding || delivered.HasBlockingFailures ||
                delivered.GenerationDisposition != evaluated.GenerationDisposition ||
                delivered.StagingCleanupRequired != evaluated.StagingCleanupRequired ||
                !delivered.GenerationWarnings.SequenceEqual(evaluated.GenerationWarnings) ||
                !delivered.Omissions.SequenceEqual(evaluated.Omissions) ||
                !delivered.TranscriptReferences.SequenceEqual(evaluated.TranscriptReferences) ||
                delivered.Artifacts.Count != evaluated.Artifacts.Count ||
                delivered.Artifacts.Any(item => !item.Delivered || !evaluated.Artifacts.Any(original =>
                    original.Artifact == item.Artifact && original.Role == item.Role && original.RenditionId == item.RenditionId &&
                    original.Source == item.Source && original.VersionId == item.VersionId && original.VersionDigest == item.VersionDigest &&
                    original.GraphDigest == item.GraphDigest && original.ManifestDigest == item.ManifestDigest &&
                    original.ThumbnailPlanId == item.ThumbnailPlanId && original.FrameEvidence == item.FrameEvidence &&
                    original.ProfileId == item.ProfileId && original.ProfileDigest == item.ProfileDigest &&
                    CanonicalJson.Canonicalize(JsonSerializer.Serialize(original.Measurements)) ==
                        CanonicalJson.Canonicalize(JsonSerializer.Serialize(item.Measurements)) &&
                    original.Evaluation?.CanonicalJson == item.Evaluation?.CanonicalJson &&
                    original.Evidence.All(item.Evidence.Contains))) ||
                !(await validate(delivered, CancellationToken.None).ConfigureAwait(false)).IsValid)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.delivery_not_validated", evaluated);
            }
            // A validated delivery is a commit point. Persist its receipt even if the
            // user cancels immediately afterwards; never label saved outputs "unsaved".
            return delivered;
        }
        catch (PipelineFailure) { throw; }
        catch
        {
            // Once the real service is invoked, cancellation cannot prove that no
            // output was saved. Its partial side effects require reconciliation.
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.delivery_reconciliation_required", evaluated);
        }
    }

    private IngestWorkflow CreateIngestWorkflow(ProcessRunner runner, BinaryVerifier verifier)
    {
        SourceOnlyRuntimeOptions runtime = options!;
        var probe = new FfprobeClient(runner, verifier, runtime.FfprobePath);
        var indexer = new PtsIndexer(runner, verifier, runtime.FfprobePath);
        return new IngestWorkflow(coordinator, new IngestWorkflowStore(runtime.DatabasePath), artifacts, workspace,
            new MediaImporter(candidates, workspace), probe, indexer,
            new DecodeValidator(runner, verifier, runtime.FfmpegPath), new IngestService(artifacts, verifier),
            new ProxyGenerator(artifacts, runner, verifier, probe, indexer, workspace, runtime.FfmpegPath),
            new ProxyFrameMapGenerator(), new PreviewAssetGenerator(artifacts, runner, verifier, workspace, runtime.FfmpegPath));
    }

    private static void ValidateIngest(StoredSourceOnlyExecution execution, LocatedSource source, IngestWorkflowRunResult ingest)
    {
        if (!ingest.IsComplete || ingest.ReconciliationRequired || ingest.Asset is null || ingest.Original is null ||
            ingest.SourcePtsIndex is null || ingest.Job.Snapshot.JobId != execution.Request.IngestJobId ||
            ingest.Asset.Id != source.CandidateId || ingest.Asset.FileName != source.FileName ||
            ingest.Asset.ContentDigest != source.Digest || ingest.Asset.ByteSize != source.ByteSize ||
            ingest.Original.Artifact.Id.Value != source.CandidateId.Value ||
            ingest.Asset.Rights.EvidenceReference != source.Rights.EvidenceReference ||
            ingest.Asset.Rights.Status != RightsStatus.Approved || ingest.Asset.Rights.ExternalPublicationAllowed)
        {
            throw new PipelineFailure(ingest.ReconciliationRequired ? SourceOnlyExecutionPhase.ReconciliationRequired
                : ingest.Job.Snapshot.State == JobState.Cancelled ? SourceOnlyExecutionPhase.Canceled
                : SourceOnlyExecutionPhase.Failed, "source_only.ingest_not_complete");
        }
    }

    private Project BindProjectSource(StoredSourceOnlyExecution execution, Asset source)
    {
        Project project = ReadProject(execution.Request.ProjectId);
        if (project.Assets.Count == 1)
        {
            var expected = new Project(project.Id, project.ContentName, project.Purpose, project.Audience,
                project.PublicationScope, project.Deadline, [source], project.Performers);
            if (ProjectJson.WriteProject(expected) != ProjectJson.WriteProject(project))
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.project_source_mismatch");
            }
            return project;
        }
        if (project.Assets.Count != 0 || project.PublicationScope != PublicationScope.InternalOnly)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.project_source_mismatch");
        }
        StoredProject stored = projects.GetProject(project.Id)!;
        var updated = new Project(project.Id, project.ContentName, project.Purpose, project.Audience,
            project.PublicationScope, project.Deadline, [source], project.Performers);
        _ = projects.UpdateProject(updated, ProjectJson.WriteProject(updated), stored.Revision, Now(stored.UpdatedAt));
        return updated;
    }

    private async Task<HeldInputs> HoldVerifiedInputsAsync(SourceOnlyRuntimeOptions runtime, BinaryVerifier verifier,
        CancellationToken token)
    {
        var held = new HeldInputs();
        try
        {
            BinaryVerificationRequest[] bindings =
            [
                new("source-only.ffmpeg", runtime.FfmpegPath, new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), DecodeValidator.CanonicalFfmpegByteSize),
                new("source-only.ffprobe", runtime.FfprobePath, new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize),
                runtime.PythonVerification,
                runtime.Font.VerificationRequest,
            ];
            foreach (BinaryVerificationRequest binding in bindings)
            {
                held.Files.Add(OpenHeldFile(binding.FilePath));
                _ = await verifier.VerifyAsync(binding, token).ConfigureAwait(false);
            }
            string ready = await runtime.ModelManager.RequireOfflinePathAsync(ModelManager.AdoptedAsrModelId, token).ConfigureAwait(false);
            if (!string.Equals(ready, runtime.ModelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.model_directory_mismatch");
            }
            // Closed pinned model bundle only, never recursive source/profile collection.
            foreach (string name in new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" })
            {
                held.Files.Add(OpenHeldFile(Path.Combine(ready, name)));
            }
            _ = await runtime.ModelManager.RequireOfflinePathAsync(ModelManager.AdoptedAsrModelId, token).ConfigureAwait(false);
            var weights = new BinaryVerificationRequest("source-only.model-weights", Path.Combine(ready, "model.bin"),
                new ContentDigest("3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"), 483546902);
            _ = await verifier.VerifyAsync(weights, token).ConfigureAwait(false);
            held.ModelWeightsDigest = weights.ExpectedDigest;
            return held;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { held.Dispose(); throw; }
        catch
        {
            held.Dispose();
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.runtime_verification_failed");
        }
    }

    private async Task<AnalysisState> GetOrCreateAnalysisAsync(StoredSourceOnlyExecution execution,
        IngestWorkflowRunResult ingest, ProcessRunner runner, BinaryVerifier verifier, CancellationToken token)
    {
        AnalysisState? existing = await ReadAnalysisAsync(execution, ingest, token).ConfigureAwait(false);
        if (existing is not null) { return existing; }
        if (execution.Request.ParentVersionId is not null)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_checkpoint_missing");
        }
        string seed = execution.Request.IngestJobId.ToString();
        var ids = new AnalysisIds(seed);
        StoredArtifact? audioArtifact = artifacts.GetArtifact(ids.Audio);
        StoredArtifact? timelineArtifact = artifacts.GetArtifact(ids.Timeline);
        AnalysisAudioTimeline timeline;
        StoredSourceOnlyExecution audioProducer;
        if ((audioArtifact is null) != (timelineArtifact is null))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_checkpoint_incomplete");
        }
        if (audioArtifact is null)
        {
            if (artifacts.GetArtifact(ids.Raw) is not null || artifacts.GetArtifact(ids.Transcript) is not null)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_checkpoint_incomplete");
            }
            var extraction = new AudioExtraction(artifacts, runner, verifier,
                new FfprobeClient(runner, verifier, options!.FfprobePath), workspace, options.FfmpegPath, options.FfprobePath);
            ExtractedAnalysisAudio audio = await extraction.ExtractAsync(ids.Audio, ids.Timeline,
                ingest.Asset!, ingest.Original!.Artifact, ingest.SourcePtsIndex!, execution.Request.RootJobId,
                options.OperationTimeout, token).ConfigureAwait(false);
            audioArtifact = audio.AudioArtifact;
            timelineArtifact = audio.TimelineArtifact;
            timeline = audio.Timeline;
            audioProducer = execution;
        }
        else
        {
            audioArtifact = await artifacts.ValidateRegisteredAsync(ids.Audio, token).ConfigureAwait(false);
            timelineArtifact = await artifacts.ValidateRegisteredAsync(ids.Timeline, token).ConfigureAwait(false);
            timeline = AnalysisAudioTimeline.ParseCanonical(Utf8.GetString(await ReadArtifactBytesAsync(ids.Timeline,
                execution.Request.ProjectId, 64 * 1024, token).ConfigureAwait(false)));
            audioProducer = executions.GetByJob(timeline.AnalysisJobId)
                ?? throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.audio_owner_missing");
            if (audioProducer.Request.RootJobId != timeline.AnalysisJobId ||
                audioProducer.Request.ProjectId != execution.Request.ProjectId || audioProducer.Request.IngestJobId != execution.Request.IngestJobId ||
                timeline.ProjectId != execution.Request.ProjectId || timeline.SourceAssetId != ingest.Asset!.Id ||
                timeline.SourceContentDigest != ingest.Asset.ContentDigest || timeline.SourceByteSize != ingest.Asset.ByteSize ||
                timeline.SourceOriginalArtifactId != ingest.Original!.Artifact.Id ||
                timeline.SourceVideoPtsIndexDigest != ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)) ||
                audioArtifact.Kind != ArtifactKind.Analysis || audioArtifact.ProjectId != execution.Request.ProjectId ||
                timeline.AnalysisAudioArtifactId != ids.Audio || timeline.TimelineArtifactId != ids.Timeline ||
                timeline.AnalysisAudioDigest != audioArtifact.ContentDigest || timeline.AnalysisAudioByteSize != audioArtifact.ByteSize)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.audio_checkpoint_invalid");
            }
        }
        StoredArtifact? rawArtifact = artifacts.GetArtifact(ids.Raw);
        LocalAsrResult raw;
        if (rawArtifact is null)
        {
            if (artifacts.GetArtifact(ids.Transcript) is not null)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.raw_asr_missing");
            }
            string resultDirectory = Path.Combine(workspace, "projects", execution.Request.ProjectId.ToString(), "staging",
                "source-only-asr-" + execution.Request.RootJobId);
            CreateDirectory(workspace, resultDirectory);
            var worker = new LocalAnalysisWorker(runner, options!.PythonExecutable, workspace);
            raw = await worker.TranscribeAsync(artifacts.GetValidatedRegisteredArtifactPath(audioArtifact.Id),
                options.ModelDirectory, resultDirectory, timeline, options.OperationTimeout, token).ConfigureAwait(false);
            // Keep raw worker bytes EXACT, not canonicalized/re-encoded doubles.
            rawArtifact = await SaveBytesAsync(execution, ids.Raw, raw.RawResultBytes.ToArray(), "asr-raw", token).ConfigureAwait(false);
            _ = RequirePath(raw.ArtifactPath, directory: false);
            File.Delete(raw.ArtifactPath);
            Directory.Delete(resultDirectory, recursive: false);
        }
        else
        {
            byte[] bytes = await ReadArtifactBytesAsync(ids.Raw, execution.Request.ProjectId, LocalAnalysisWorker.MaxResultBytes, token).ConfigureAwait(false);
            using JsonDocument saved = JsonDocument.Parse(bytes);
            string policy = saved.RootElement.GetProperty("result").GetProperty("configuration").GetProperty("language").GetString()!;
            raw = LocalAnalysisWorker.ParseResult(bytes, rawArtifact.ContentDigest, artifacts.GetValidatedRegisteredArtifactPath(ids.Raw),
                Text(saved.RootElement, "requestId"), policy, timeline);
        }
        // The durable raw-result receipt fixes creation time even if a process exits
        // between raw promotion, transcript promotion, and the final checkpoint write.
        DateTimeOffset createdAt = rawArtifact.CreatedAt;
        TranscriptImportResult imported = RebindImportedIds(TranscriptImporter.Import(raw.WorkerResult, timeline,
            ids.Revision, ids.Speaker, createdAt));
        string transcriptJson = TranscriptSnapshotJson.WriteRevision(imported.Revision);
        StoredArtifact transcript = await SaveJsonAsync(execution, ids.Transcript, transcriptJson, "machine-transcript", token).ConfigureAwait(false);
        using JsonDocument rawDocument = JsonDocument.Parse(raw.RawResultBytes);
        string checkpoint = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = AnalysisCheckpointSchema,
            executionId = audioProducer.Request.ExecutionId.ToString(),
            projectId = execution.Request.ProjectId.ToString(),
            draftId = execution.Request.DraftId.ToString(),
            ingestJobId = execution.Request.IngestJobId.ToString(),
            sourceCandidateId = execution.Request.SourceCandidateId.ToString(),
            sourceDigest = ingest.Asset!.ContentDigest.Hex,
            originalArtifactId = ingest.Original.Artifact.Id.ToString(),
            ptsDigest = ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)).Hex,
            audio = ArtifactReference(audioArtifact),
            timeline = ArtifactReference(timelineArtifact!),
            raw = ArtifactReference(rawArtifact),
            transcript = ArtifactReference(transcript),
            revisionId = ids.Revision.ToString(),
            speakerId = ids.Speaker.ToString(),
            createdAt = createdAt.ToString("O", CultureInfo.InvariantCulture),
            requestId = rawDocument.RootElement.GetProperty("requestId").GetString(),
            requestedPolicy = raw.LanguageDetection.RequestedPolicy,
            idRule = "source-only.import-ids.v1",
        }));
        _ = await SaveJsonAsync(execution, ids.Checkpoint, checkpoint, "analysis-checkpoint", token).ConfigureAwait(false);
        return new AnalysisState(imported, audioArtifact, timelineArtifact!, rawArtifact, transcript);
    }

    private async Task ValidatePartialAnalysisAsync(StoredSourceOnlyExecution execution,
        IngestWorkflowRunResult ingest, CancellationToken token)
    {
        var ids = new AnalysisIds(execution.Request.IngestJobId.ToString());
        try
        {
            foreach (var id in new[] { ids.Audio, ids.Timeline, ids.Raw, ids.Transcript })
            {
                if (artifacts.GetArtifact(id) is null) { continue; }
                StoredArtifact artifact = await artifacts.ValidateRegisteredAsync(id, token).ConfigureAwait(false);
                if (artifact.ProjectId != execution.Request.ProjectId || artifact.Kind != ArtifactKind.Analysis) { throw new FormatException(); }
            }
            if (artifacts.GetArtifact(ids.Timeline) is null)
            {
                if (artifacts.GetArtifact(ids.Raw) is not null || artifacts.GetArtifact(ids.Transcript) is not null) { throw new FormatException(); }
                return; // An audio-only promotion is not a ready transcript.
            }
            AnalysisAudioTimeline timeline = AnalysisAudioTimeline.ParseCanonical(Utf8.GetString(await ReadArtifactBytesAsync(
                ids.Timeline, execution.Request.ProjectId, 64 * 1024, token).ConfigureAwait(false)));
            StoredArtifact audio = artifacts.GetArtifact(ids.Audio) ?? throw new FormatException();
            StoredSourceOnlyExecution producer = executions.GetByJob(timeline.AnalysisJobId) ?? throw new FormatException();
            if (producer.Request.RootJobId != timeline.AnalysisJobId || producer.Request.ProjectId != execution.Request.ProjectId ||
                producer.Request.IngestJobId != execution.Request.IngestJobId || timeline.ProjectId != execution.Request.ProjectId ||
                timeline.SourceAssetId != execution.Request.SourceCandidateId || timeline.SourceContentDigest != ingest.Asset!.ContentDigest ||
                timeline.SourceByteSize != ingest.Asset.ByteSize || timeline.SourceOriginalArtifactId != ingest.Original!.Artifact.Id ||
                timeline.SourceVideoPtsIndexDigest != ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)) ||
                timeline.AnalysisAudioArtifactId != ids.Audio || timeline.AnalysisAudioDigest != audio.ContentDigest ||
                timeline.AnalysisAudioByteSize != audio.ByteSize || timeline.TimelineArtifactId != ids.Timeline)
            {
                throw new FormatException();
            }
            StoredArtifact? rawArtifact = artifacts.GetArtifact(ids.Raw);
            if (rawArtifact is null)
            {
                if (artifacts.GetArtifact(ids.Transcript) is not null) { throw new FormatException(); }
                return;
            }
            byte[] rawBytes = await ReadArtifactBytesAsync(ids.Raw, execution.Request.ProjectId, LocalAnalysisWorker.MaxResultBytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(rawBytes);
            LocalAsrResult raw = LocalAnalysisWorker.ParseResult(rawBytes, rawArtifact.ContentDigest,
                artifacts.GetValidatedRegisteredArtifactPath(ids.Raw), Text(document.RootElement, "requestId"),
                Text(document.RootElement.GetProperty("result").GetProperty("configuration"), "language"), timeline);
            if (artifacts.GetArtifact(ids.Transcript) is not null)
            {
                TranscriptImportResult imported = RebindImportedIds(TranscriptImporter.Import(raw.WorkerResult, timeline,
                    ids.Revision, ids.Speaker, rawArtifact.CreatedAt));
                byte[] transcript = await ReadArtifactBytesAsync(ids.Transcript, execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
                if (!transcript.AsSpan().SequenceEqual(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(imported.Revision)))) { throw new FormatException(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_checkpoint_invalid"); }
    }

    private async Task<AnalysisState?> ReadAnalysisAsync(StoredSourceOnlyExecution execution,
        IngestWorkflowRunResult ingest, CancellationToken token)
    {
        var ids = new AnalysisIds(execution.Request.IngestJobId.ToString());
        if (artifacts.GetArtifact(ids.Checkpoint) is null) { return null; }
        try
        {
            using JsonDocument checkpoint = JsonDocument.Parse(await ReadArtifactBytesAsync(ids.Checkpoint,
                execution.Request.ProjectId, 64 * 1024, token).ConfigureAwait(false));
            JsonElement root = checkpoint.RootElement;
            RequireShape(root, "schema", "executionId", "projectId", "draftId", "ingestJobId", "sourceCandidateId",
                "sourceDigest", "originalArtifactId", "ptsDigest", "audio", "timeline", "raw", "transcript",
                "revisionId", "speakerId", "createdAt", "requestId", "requestedPolicy", "idRule");
            StoredSourceOnlyExecution producer = RequireExecution(EntityId<SourceOnlyExecutionIdTag>.Parse(Text(root, "executionId")));
            if (Text(root, "schema") != AnalysisCheckpointSchema || Text(root, "idRule") != "source-only.import-ids.v1" ||
                Text(root, "projectId") != execution.Request.ProjectId.ToString() ||
                Text(root, "draftId") != execution.Request.DraftId.ToString() ||
                Text(root, "ingestJobId") != execution.Request.IngestJobId.ToString() ||
                producer.Request.ProjectId != execution.Request.ProjectId || producer.Request.DraftId != execution.Request.DraftId ||
                producer.Request.IngestJobId != execution.Request.IngestJobId ||
                Text(root, "sourceCandidateId") != execution.Request.SourceCandidateId.ToString() ||
                Text(root, "sourceDigest") != ingest.Asset!.ContentDigest.Hex ||
                Text(root, "originalArtifactId") != ingest.Original!.Artifact.Id.ToString() ||
                Text(root, "ptsDigest") != ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)).Hex ||
                Text(root, "revisionId") != ids.Revision.ToString() || Text(root, "speakerId") != ids.Speaker.ToString())
            {
                throw new FormatException();
            }
            StoredArtifact audio = await ValidateReferenceAsync(root.GetProperty("audio"), ids.Audio, execution.Request.ProjectId, token).ConfigureAwait(false);
            StoredArtifact timelineArtifact = await ValidateReferenceAsync(root.GetProperty("timeline"), ids.Timeline, execution.Request.ProjectId, token).ConfigureAwait(false);
            StoredArtifact rawArtifact = await ValidateReferenceAsync(root.GetProperty("raw"), ids.Raw, execution.Request.ProjectId, token).ConfigureAwait(false);
            StoredArtifact transcript = await ValidateReferenceAsync(root.GetProperty("transcript"), ids.Transcript, execution.Request.ProjectId, token).ConfigureAwait(false);
            AnalysisAudioTimeline timeline = AnalysisAudioTimeline.ParseCanonical(Utf8.GetString(
                await ReadArtifactBytesAsync(ids.Timeline, execution.Request.ProjectId, 64 * 1024, token).ConfigureAwait(false)));
            if (timeline.ProjectId != execution.Request.ProjectId || timeline.AnalysisJobId != producer.Request.RootJobId ||
                timeline.SourceAssetId != execution.Request.SourceCandidateId || timeline.SourceContentDigest != ingest.Asset.ContentDigest ||
                timeline.SourceByteSize != ingest.Asset.ByteSize || timeline.SourceOriginalArtifactId != ingest.Original.Artifact.Id ||
                timeline.SourceVideoPtsIndexDigest.Hex != Text(root, "ptsDigest") ||
                timeline.AnalysisAudioArtifactId != ids.Audio || timeline.AnalysisAudioDigest != audio.ContentDigest ||
                timeline.AnalysisAudioByteSize != audio.ByteSize || timeline.TimelineArtifactId != ids.Timeline)
            {
                throw new FormatException();
            }
            byte[] rawBytes = await ReadArtifactBytesAsync(ids.Raw, execution.Request.ProjectId, LocalAnalysisWorker.MaxResultBytes, token).ConfigureAwait(false);
            LocalAsrResult raw = LocalAnalysisWorker.ParseResult(rawBytes, rawArtifact.ContentDigest,
                artifacts.GetValidatedRegisteredArtifactPath(ids.Raw), Text(root, "requestId"), Text(root, "requestedPolicy"), timeline);
            DateTimeOffset createdAt = ParseUtc(Text(root, "createdAt"));
            if (createdAt < producer.Request.CreatedAt) { throw new FormatException(); }
            TranscriptImportResult imported = RebindImportedIds(TranscriptImporter.Import(raw.WorkerResult, timeline,
                ids.Revision, ids.Speaker, createdAt));
            byte[] expectedTranscript = Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(imported.Revision));
            byte[] storedTranscript = await ReadArtifactBytesAsync(ids.Transcript, execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
            if (!expectedTranscript.AsSpan().SequenceEqual(storedTranscript)) { throw new FormatException(); }
            return new AnalysisState(imported, audio, timelineArtifact, rawArtifact, transcript);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_checkpoint_invalid"); }
    }

    /// <summary>Stable IDs are derived from the RECORDED revision and ordinal, never from fresh GUIDs on reload.</summary>
    internal static TranscriptImportResult RebindImportedIds(TranscriptImportResult imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        if (imported.Revision.ChangeSource != TranscriptChangeSource.Machine)
        {
            throw new ArgumentException("Only an actual machine import can be rebound.", nameof(imported));
        }
        string seed = imported.Revision.Id.ToString();
        var utteranceIds = new Dictionary<EntityId<TranscriptUtteranceIdTag>, EntityId<TranscriptUtteranceIdTag>>();
        var spanIds = new Dictionary<EntityId<SourceSpanIdTag>, EntityId<SourceSpanIdTag>>();
        var tokenIds = new Dictionary<EntityId<TranscriptTokenIdTag>, EntityId<TranscriptTokenIdTag>>();
        var utterances = new List<Utterance>();
        var spans = new List<SourceSpan>();
        for (int index = 0; index < imported.Revision.Utterances.Count; index++)
        {
            Utterance previous = imported.Revision.Utterances[index];
            string label = index.ToString(CultureInfo.InvariantCulture);
            var utteranceId = StableId<TranscriptUtteranceIdTag>(seed, "utterance:" + label);
            var spanId = StableId<SourceSpanIdTag>(seed, "utterance-span:" + label);
            utteranceIds.Add(previous.Id, utteranceId);
            spanIds.Add(previous.SourceSpanId, spanId);
            var sentences = new List<Sentence>();
            for (int sentenceIndex = 0; sentenceIndex < previous.Sentences.Count; sentenceIndex++)
            {
                Sentence sentence = previous.Sentences[sentenceIndex];
                string sentenceLabel = label + ":" + sentenceIndex.ToString(CultureInfo.InvariantCulture);
                var tokens = new List<Token>();
                for (int tokenIndex = 0; tokenIndex < sentence.Tokens.Count; tokenIndex++)
                {
                    Token original = sentence.Tokens[tokenIndex];
                    var id = StableId<TranscriptTokenIdTag>(seed, "token:" + sentenceLabel + ":" + tokenIndex.ToString(CultureInfo.InvariantCulture));
                    tokenIds.Add(original.Id, id);
                    tokens.Add(new Token(id, original.Text, original.Range, original.RecognitionConfidence));
                }
                sentences.Add(new Sentence(StableId<TranscriptSentenceIdTag>(seed, "sentence:" + sentenceLabel), sentence.Text, tokens));
            }
            utterances.Add(new Utterance(utteranceId, spanId, previous.SpeakerId, previous.SpeakerLabel,
                previous.Text, previous.Range, previous.RecognitionConfidence, sentences));
            SourceSpan source = imported.SourceSpans[index];
            spans.Add(new SourceSpan(spanId, source.AssetId, source.StreamId, source.Range, source.SourceDigest,
                source.TranscriptRevisionId, source.TranscriptTokenIds.Select(id => tokenIds[id]).ToArray()));
        }
        var revision = new TranscriptRevision(imported.Revision.Id, imported.Revision.ParentRevisionId,
            TranscriptChangeSource.Machine, imported.Revision.CreatedAt, utterances);
        return new TranscriptImportResult(revision, spans, imported.TimingEvidence.Select(item => new TranscriptTimingEvidence(
            utteranceIds[item.UtteranceId], spanIds[item.SourceSpanId], item.TokenId is null ? null : tokenIds[item.TokenId],
            item.RawStartSeconds, item.RawEndSeconds, item.RoundedStartSample, item.RoundedEndSample,
            item.StartWasRounded, item.EndWasRounded, item.ReviewReasons)).ToArray(), imported.RawWorkerResult, imported.AudioTimeline);
    }

    internal SourceOnlyAnalysisEvidence BuildAnalysis(Project project, TranscriptImportResult import,
        StoredSourceOnlyExecution execution, DateTimeOffset at, CancellationToken token = default)
    {
        string seed = execution.Request.ExecutionId.ToString();
        if (at < import.Revision.CreatedAt || at < execution.Request.CreatedAt)
        {
            throw new InvalidOperationException("Analysis evidence predates its inputs.");
        }
        TopicBoundaryScanResult rawBoundaries = TopicBoundaryService.FindCandidates(import.Revision, import.SourceSpans,
            StableId<ModelRunIdTag>(seed, "topic-rule"), token);
        // Services produce actual features; replace only nondeterministic entity IDs so
        // a registration replay does not invent a new candidate identity each time.
        TopicBoundaryCandidate[] boundaryCandidates = rawBoundaries.Candidates.Select((item, index) =>
        {
            string label = index.ToString(CultureInfo.InvariantCulture);
            OriginRef origin = OriginRef.FromSourceSpan(item.SourceSpan.Id);
            return new TopicBoundaryCandidate(new Detection(StableId<DetectionIdTag>(seed, "topic-detection:" + label),
                item.Detection.ModelRunId, item.Detection.Kind, origin, item.Detection.Description, null),
                new Suggestion(StableId<SuggestionIdTag>(seed, "topic-suggestion:" + label), item.Suggestion.ModelRunId,
                    item.Suggestion.Kind, item.Suggestion.Status, origin, item.Suggestion.Reason, null,
                    item.Suggestion.MissingContext, item.Suggestion.Warning), item.SourceSpan, item.SourceRevisionId,
                item.SourceRevisionDigest, item.SuggestedBoundary, item.RuleVersion, item.Features);
        }).ToArray();
        var boundaries = new TopicBoundaryScanResult(rawBoundaries.SourceRevisionId, rawBoundaries.SourceRevisionDigest,
            rawBoundaries.ModelRunId, rawBoundaries.RuleVersion, rawBoundaries.InspectedTransitionCount,
            rawBoundaries.TranscriptStart, rawBoundaries.TranscriptEnd, boundaryCandidates);
        var ranges = new List<TopicRangeRevision>();
        var metadata = new List<TopicMetadataRevision>();
        var emphasis = new List<EmphasisSuggestionCandidate>();
        var warnings = new List<string>
        {
            "source_only.topic_semantics_not_evaluated",
        };
        if (boundaries.TranscriptStart is RationalTime start && boundaries.TranscriptEnd is RationalTime end)
        {
            TopicRangeRevision range = TopicBoundaryService.CreateTopicRange(boundaries,
                StableId<TopicRangeIdTag>(seed, "transcript-topic"), StableId<TopicRangeRevisionIdTag>(seed, "transcript-topic-revision"),
                null, null, new TimeRange(start, end - start), execution.Request.ActorSid, at,
                "Represent the measured transcript extent as one unclassified topic, not a semantic boundary.");
            ranges.Add(range);
            TopicMetadataRevision topic = TopicMetadataService.CreateInitial(project, import.Revision, import.SourceSpans,
                range, null, 1, StableId<TopicMetadataIdTag>(seed, "topic-metadata"),
                StableId<TopicMetadataRevisionIdTag>(seed, "topic-metadata-revision"), execution.Request.ActorSid,
                at, "Create structural metadata; editorial title and importance remain unconfirmed.");
            metadata.Add(topic);
            EmphasisCandidateScanResult scan = EmphasisCandidateService.FindCandidates(import.Revision, import.SourceSpans,
                topic, [], TermScope.ForProject(project.Id), [], StableId<ModelRunIdTag>(seed, "emphasis-rule"), token);
            for (int index = 0; index < scan.Candidates.Count; index++)
            {
                EmphasisCandidate item = scan.Candidates[index];
                string label = index.ToString(CultureInfo.InvariantCulture);
                SourceSpan span = ReidentifySpan(item.SourceSpan, StableId<SourceSpanIdTag>(seed, "emphasis-span:" + label));
                var suggestion = new Suggestion(StableId<SuggestionIdTag>(seed, "emphasis-suggestion:" + label),
                    item.Suggestion.ModelRunId, item.Suggestion.Kind, item.Suggestion.Status, OriginRef.FromSourceSpan(span.Id),
                    item.Suggestion.Reason, item.Suggestion.Confidence, item.Suggestion.MissingContext, item.Suggestion.Warning);
                emphasis.Add(SuggestionService.Register(new EmphasisCandidate(suggestion, span, item.SourceRevisionId,
                    item.SourceRevisionDigest, item.TopicMetadataRevisionId, item.RuleVersion, item.Text, item.TextSource,
                    item.Reasons, item.TermEvidence), []));
            }
        }
        else { warnings.Add("source_only.no_speech_recognized"); }

        EditorialPhraseScanResult editorialScan = EditorialPhraseDetector.FindCandidates(import.Revision, import.SourceSpans,
            StableId<ModelRunIdTag>(seed, "editorial-rule"), token);
        EditorialPhraseSuggestionCandidate[] editorial = editorialScan.Candidates.Select((item, index) =>
        {
            string label = index.ToString(CultureInfo.InvariantCulture);
            SourceSpan span = ReidentifySpan(item.SourceSpan, StableId<SourceSpanIdTag>(seed, "editorial-span:" + label));
            OriginRef origin = OriginRef.FromSourceSpan(span.Id);
            var payload = new EditorialPhraseCandidate(
                new Detection(StableId<DetectionIdTag>(seed, "editorial-detection:" + label), item.Detection.ModelRunId,
                    item.Detection.Kind, origin, item.Detection.Description, null),
                new Suggestion(StableId<SuggestionIdTag>(seed, "editorial-suggestion:" + label), item.Suggestion.ModelRunId,
                    item.Suggestion.Kind, item.Suggestion.Status, origin, item.Suggestion.Reason, null,
                    item.Suggestion.MissingContext, item.Suggestion.Warning), span, item.SourceRevisionId,
                item.SourceRevisionDigest, item.DictionaryVersion, item.MatchedPhrase, item.NormalizedSentenceStart,
                item.NormalizedMatchLength, item.UsesUtteranceTimingFallback, item.UsesPartialTokenCoverage);
            return SuggestionService.Register(payload, []);
        }).ToArray();
        return new SourceOnlyAnalysisEvidence(at, boundaries, ranges.AsReadOnly(), metadata.AsReadOnly(),
            emphasis.AsReadOnly(), Array.AsReadOnly(editorial), warnings.AsReadOnly());
    }

    private async Task<SourceOnlyAnalysisEvidence> ReadAnalysisEvidenceAsync(Project project, TranscriptImportResult import,
        StoredSourceOnlyExecution execution, CancellationToken token)
    {
        try
        {
            byte[] stored = await ReadArtifactBytesAsync(StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "analysis-evidence"),
                execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(stored);
            SourceOnlyAnalysisEvidence replay = BuildAnalysis(project, import, execution,
                document.RootElement.GetProperty("CreatedAt").GetDateTimeOffset(), token);
            if (!CanonicalJson.CanonicalizeToUtf8(JsonSerializer.SerializeToUtf8Bytes(replay)).AsSpan().SequenceEqual(stored))
            {
                // Earlier native executions recorded these two legacy warnings. Match
                // their COMPLETE original bytes; do not accept arbitrary warning edits
                // or relabel a historical run as having performed the new auto rules.
                SourceOnlyAnalysisEvidence legacy = replay with
                {
                    Warnings = replay.Warnings.Take(1)
                        .Append("source_only.repetition_and_silence_auto_removal_not_connected")
                        .Concat(replay.Warnings.Skip(1).Select(item => item == "source_only.no_speech_recognized"
                            ? "source_only.no_speech_detected" : item)).ToArray(),
                };
                if (!CanonicalJson.CanonicalizeToUtf8(JsonSerializer.SerializeToUtf8Bytes(legacy)).AsSpan().SequenceEqual(stored))
                {
                    throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_evidence_invalid");
                }
                return legacy;
            }
            return replay;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PipelineFailure) { throw; }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.analysis_evidence_invalid"); }
    }

    private static SourceOnlyResult GenerationFailureResult(StoredSourceOnlyExecution execution,
        StandardCompositionPreparation preparation, StandardArtifactGenerationResult generated) => new(
        new SourceOnlyResultBinding(execution.Request.ProjectId, execution.Request.ExecutionId, execution.Request.RootJobId,
            execution.Request.SettingsSnapshot.ContentDigest, preparation.Child.Id, preparation.Child.ContentDigest),
        generated.Disposition, [], [],
        [new SourceOnlyResultFailure(generated.Disposition switch
        {
            StandardArtifactGenerationDisposition.Cancelled => SourceOnlyResultFailureCode.GenerationCanceled,
            StandardArtifactGenerationDisposition.ReconciliationRequired => SourceOnlyResultFailureCode.GenerationReconciliationRequired,
            _ => SourceOnlyResultFailureCode.GenerationFailed,
        })], generated.ReconciliationArtifacts.Select(item => new SourceOnlyResultArtifactReference(item.ArtifactId,
            item.ContentDigest, item.ByteSize, ArtifactKind.Render)).ToArray(), [], null, generated.StagingCleanupRequired,
            generated.Warnings.Select(item => new SourceOnlyResultGenerationWarning(item.Role, item.Code)).ToArray());

    private Task RegisterRuntimeAsync(StoredSourceOnlyExecution execution, string stage, ContentDigest digest,
        Func<CancellationToken, Task> register, CancellationToken token) =>
        runtimeRegistrations.RegisterAsync(execution.Request.ProjectId + ":" + stage,
            StandardRenderGraphFactory.Digest(new { execution.Request.ExecutionId, digest }), async cancellation =>
            {
                ThrowIfDisposed();
                if (Read(execution.Request.ProjectId)?.Request.ExecutionId != execution.Request.ExecutionId)
                {
                    throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.runtime_checkpoint_changed");
                }
                await register(cancellation).ConfigureAwait(false);
            }, token);

    private Task RegisterIngestAsync(StoredSourceOnlyExecution execution, IngestWorkflowRunResult ingest, CancellationToken token) =>
        callbacks.IngestReady is null ? Task.CompletedTask : RegisterRuntimeAsync(execution, "ingest",
            StandardRenderGraphFactory.Digest(new { ingest.Asset, ingest.Original!.Artifact, pts = ingest.SourcePtsIndex!.CanonicalJson }),
            cancellation => callbacks.IngestReady(ingest, cancellation), token);

    private Task RegisterTranscriptAsync(StoredSourceOnlyExecution execution, Project project,
        TranscriptImportResult import, CancellationToken token) =>
        callbacks.TranscriptReady is null ? Task.CompletedTask : RegisterRuntimeAsync(execution, "transcript",
            StandardRenderGraphFactory.Digest(new
            {
                project = ProjectJson.WriteProject(project),
                revision = TranscriptSnapshotJson.WriteRevision(import.Revision),
                import.SourceSpans,
                import.TimingEvidence
            }),
            cancellation => callbacks.TranscriptReady(project, import, cancellation), token);

    private Task RegisterCompositionAsync(StoredSourceOnlyExecution execution, StandardCompositionPreparation preparation,
        TranscriptImportResult import, CancellationToken token) =>
        callbacks.CompositionReady is null ? Task.CompletedTask : RegisterRuntimeAsync(execution, "composition",
            StandardRenderGraphFactory.Digest(new
            {
                version = CompositionSnapshotJson.WriteVersion(preparation.Child),
                preparation.ApplicationDigest,
                revision = TranscriptSnapshotJson.WriteRevision(import.Revision)
            }),
            cancellation => callbacks.CompositionReady(preparation, import, cancellation), token);

    private async Task RegisterAnalysisAsync(StoredSourceOnlyExecution execution, Project project, TranscriptImportResult import,
        SourceOnlyAnalysisEvidence evidence, CancellationToken token)
    {
        await RegisterTranscriptAsync(execution, project, import, token).ConfigureAwait(false);
        if (callbacks.AnalysisReady is not null)
        {
            await RegisterRuntimeAsync(execution, "analysis", StandardRenderGraphFactory.Digest(evidence),
                cancellation => callbacks.AnalysisReady(project, import, evidence, cancellation), token).ConfigureAwait(false);
        }
    }

    internal static SourceOnlyGeneratedRuntimeEvidence BuildGeneratedRuntimeEvidence(StandardCompositionPreparation preparation,
        TranscriptImportResult import, DefaultSettingsProfile profile, DefaultSettingsSnapshot snapshot,
        RenderFontEvidence font, string actorSid, DateTimeOffset createdAt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ContentDigest transcriptDigest = ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(import.Revision)));
        if (import.Revision.ChangeSource != TranscriptChangeSource.Machine || preparation.Child.ProjectId != import.AudioTimeline.ProjectId ||
            preparation.Plan.LongForm.Provenance.TranscriptRevisionId != import.Revision.Id ||
            preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest != transcriptDigest ||
            StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) != snapshot.ContentDigest ||
            StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsProfile) != profile.ContentDigest)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.generated_runtime_binding_invalid");
        }
        var byVersion = new Dictionary<EntityId<CompositionVersionIdTag>, SourceOnlyGeneratedSubtitleRuntimeEvidence>();
        StandardSubtitleBuildResult Build(CompositionVersion version, TimelineMap timeline,
            EntityId<StandardShortFormPlanIdTag>? shortId, IReadOnlyList<SourceSpan> spans)
        {
            LayoutSpec layout = StandardRenderGraphFactory.GetLayout(preparation.Plan, shortId);
            RenderProfile render = StandardRenderGraphFactory.GetProfile(preparation.Plan, shortId is not null, shortId);
            StandardSubtitleBuildResult result = BuildProductionSubtitles(version, timeline, layout, render, import,
                transcriptDigest, spans, profile, snapshot, font, actorSid, createdAt, token);
            // StandardSubtitleInput retains the exact initial revisions/candidates but
            // not their histories. Rewrap those SAME objects; create no new cue/revision.
            SubtitleCueHistory[] histories = result.Subtitles.Revisions.Select(revision => new SubtitleCueHistory(
                result.Subtitles.Candidates.Single(candidate => candidate.Id == revision.SourceCandidateId), [revision])).ToArray();
            byVersion.Add(version.Id, new(version, timeline, layout, render, result, Array.AsReadOnly(histories)));
            return result;
        }
        StandardSubtitleBuildResult landscape = Build(preparation.Child, preparation.ChildTimelineMap,
            null, preparation.Plan.LongForm.Provenance.SourceSpans);
        var shorts = new List<StandardShortArtifactInput>();
        foreach (StandardShortFormPlan shortForm in preparation.Plan.ShortForms)
        {
            token.ThrowIfCancellationRequested();
            CompositionVersion version = StandardRenderGraphFactory.CreateShortVersion(preparation, shortForm.Id);
            TimelineMap timeline = TimelineMapBuilder.Build(StableId<TimelineMapIdTag>(version.Id.ToString(), "subtitle-timeline"), version);
            StandardSubtitleBuildResult result = Build(version, timeline, shortForm.Id,
                import.SourceSpans.Concat(shortForm.SourceSpans).DistinctBy(span => span.Id).ToArray());
            shorts.Add(new(shortForm.Id, result.Subtitles));
        }
        return new(preparation, import, landscape, shorts.AsReadOnly(),
            new ReadOnlyDictionary<EntityId<CompositionVersionIdTag>, SourceOnlyGeneratedSubtitleRuntimeEvidence>(byVersion),
            createdAt, font);
    }

    internal static string WriteGeneratedRuntimeEvidence(StoredSourceOnlyExecution execution, SourceOnlyGeneratedRuntimeEvidence evidence)
    {
        object[] projections = WriteSubtitleProjections(execution, evidence);
        // An empty projection set uses the original v1 fields and values EXACTLY.
        // Canonical property ordering is independent of the dictionary insertion order.
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = projections.Length == 0 ? "source-only.generated-runtime.v1" : "source-only.generated-runtime.v2",
            ["executionId"] = execution.Request.ExecutionId.ToString(),
            ["projectId"] = execution.Request.ProjectId.ToString(),
            ["settingsDigest"] = execution.Request.SettingsSnapshot.ContentDigest.Hex,
            ["productionVersionId"] = evidence.Preparation.Child.Id.ToString(),
            ["productionVersionDigest"] = evidence.Preparation.Child.ContentDigest.Hex,
            ["planDigest"] = evidence.Preparation.Plan.PlanDigest.Hex,
            ["transcriptRevisionId"] = evidence.Import.Revision.Id.ToString(),
            ["transcriptDigest"] = ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(evidence.Import.Revision))).Hex,
            ["createdAt"] = evidence.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["font"] = evidence.Font,
            ["builderRule"] = StandardSubtitleBuilder.RuleVersion,
            ["projectionRule"] = "source-only.subtitle-projection.v1",
            ["versions"] = evidence.Versions.Values.OrderBy(item => item.Version.Id.ToString(), StringComparer.Ordinal).Select(item => new
            {
                versionId = item.Version.Id.ToString(),
                versionDigest = item.Version.ContentDigest.Hex,
                versionSnapshotDigest = ContentDigest.Compute(Utf8.GetBytes(CompositionSnapshotJson.WriteVersion(item.Version))).Hex,
                timeline = item.Timeline,
                layout = item.Layout,
                profile = item.Profile,
                build = item.Build,
            }).ToArray(),
            ["shortForms"] = evidence.ShortForms.Select(item => item.ShortFormId.ToString()).ToArray(),
        };
        if (projections.Length != 0) { payload["projections"] = projections; }
        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(payload));
    }

    internal async Task CheckpointGeneratedRuntimeAsync(StoredSourceOnlyExecution execution,
        SourceOnlyGeneratedRuntimeEvidence evidence, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        ValidateRuntimeComposition(execution, evidence.Preparation, evidence.Import);
        string generatedJson = WriteGeneratedRuntimeEvidence(execution, evidence);
        foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in evidence.Versions.Values)
        {
            token.ThrowIfCancellationRequested();
            AppendExactVersion(item.Version);
        }
        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "subtitle-warnings"),
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "source-only.subtitle-warnings.v1",
                executionId = execution.Request.ExecutionId.ToString(),
                mappingRule = "source-only.subtitle-projection.v1",
                productionVersionId = evidence.Preparation.Child.Id.ToString(),
                settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
                warnings = evidence.Versions.Values.SelectMany(item => item.Build.Warnings).ToArray(),
            })), "subtitle-warnings", token).ConfigureAwait(false);
        StoredArtifact checkpoint = await SaveJsonAsync(execution,
            StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generated-runtime"),
            generatedJson, "generated-runtime", token).ConfigureAwait(false);
        if (callbacks.GeneratedReady is not null)
        {
            await RegisterRuntimeAsync(execution, "generated", checkpoint.ContentDigest,
                cancellation => callbacks.GeneratedReady(evidence, cancellation), token).ConfigureAwait(false);
        }
    }

    internal async Task<SourceOnlyGeneratedRuntimeEvidence?> ReadGeneratedRuntimeEvidenceAsync(StoredSourceOnlyExecution execution,
        StandardCompositionPreparation preparation, TranscriptImportResult import, CancellationToken token, int depth = 0)
    {
        ThrowIfDisposed();
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateSubtitleProjectionDepth(depth);
            var id = StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generated-runtime");
            if (artifacts.GetArtifact(id) is null) { return null; }
            byte[] bytes = await ReadArtifactBytesAsync(id, execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string schema = Text(root, "schema");
            string[] keys = ["schema", "executionId", "projectId", "settingsDigest", "productionVersionId", "productionVersionDigest",
                "planDigest", "transcriptRevisionId", "transcriptDigest", "createdAt", "font", "builderRule", "projectionRule", "versions", "shortForms"];
            if (schema is not ("source-only.generated-runtime.v1" or "source-only.generated-runtime.v2")) { throw new FormatException(); }
            RequireShape(root, schema == "source-only.generated-runtime.v2" ? [.. keys, "projections"] : keys);
            DefaultSettingsProfile profile = settings.GetProfile(execution.Request.SettingsSnapshot.ProfileId,
                execution.Request.SettingsSnapshot.ProfileVersion) ?? throw new FormatException();
            var font = new RenderFontEvidence(Text(root.GetProperty("font"), "Name"),
                new ContentDigest(Text(root.GetProperty("font").GetProperty("FileDigest"), "Hex")));
            ValidateRuntimeComposition(execution, preparation, import);
            SourceOnlyGeneratedRuntimeEvidence replay = BuildGeneratedRuntimeEvidence(preparation, import, profile,
                execution.Request.SettingsSnapshot, font, execution.Request.ActorSid, ParseUtc(Text(root, "createdAt")), token);
            // Never deserialize a projection capability or use saved projection fields
            // as input. Rebuild from the immutable accepted prefix, including prior proofs.
            replay = await ApplyCapturedSubtitleEditsAsync(execution, replay, token, depth).ConfigureAwait(false);
            if (!Utf8.GetBytes(WriteGeneratedRuntimeEvidence(execution, replay)).AsSpan().SequenceEqual(bytes)) { throw new FormatException(); }
            foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in replay.Versions.Values)
            {
                // Restore only: never append a derived version or advance a working copy.
                CompositionVersion stored = versions.GetVersion(item.Version.Id) ?? throw new FormatException();
                if (CompositionSnapshotJson.WriteVersion(stored) != CompositionSnapshotJson.WriteVersion(item.Version)) { throw new FormatException(); }
            }
            return replay;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PipelineFailure failure) when (failure.Code == SubtitleProjectionConflictCode)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, SubtitleProjectionConflictCode);
        }
        catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.generated_runtime_checkpoint_invalid"); }
    }

    private static void ValidateRuntimeComposition(StoredSourceOnlyExecution execution,
        StandardCompositionPreparation preparation, TranscriptImportResult import)
    {
        if (preparation.Child.ProjectId != execution.Request.ProjectId || import.AudioTimeline.ProjectId != execution.Request.ProjectId ||
            import.AudioTimeline.SourceAssetId != execution.Request.SourceCandidateId ||
            preparation.Child.CreatedByActorId != execution.Request.ActorSid || preparation.Child.CreatedAt < execution.Request.CreatedAt ||
            preparation.Plan.LongForm.Provenance.TranscriptRevisionId != import.Revision.Id ||
            preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest != ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(import.Revision))) ||
            StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) != execution.Request.SettingsSnapshot.ContentDigest ||
            StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsProfile) != execution.Request.SettingsSnapshot.ProfileDigest ||
            !preparation.Plan.LongForm.Project.Assets.Any(asset => asset.Id == import.AudioTimeline.SourceAssetId &&
                asset.ContentDigest == import.AudioTimeline.SourceContentDigest && asset.ByteSize == import.AudioTimeline.SourceByteSize) ||
            (execution.VersionId is not null && (execution.VersionId != preparation.Child.Id || execution.VersionDigest != preparation.Child.ContentDigest)))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.composition_checkpoint_invalid");
        }
    }

    private async Task<CompositionState> ComposeAsync(StoredSourceOnlyExecution execution,
        IngestWorkflowRunResult ingest, Project project, AnalysisState analysis, SourceOnlyAnalysisEvidence evidence,
        DefaultSettingsProfile profile, SourceDisplayGeometry geometry, CancellationToken token)
    {
        SourceOnlyRuntimeOptions runtime = options!;
        string seed = execution.Request.ExecutionId.ToString();
        SourceSpan fullSource = CreateFullSourceSpan(ingest.SourcePtsIndex!, analysis.Import);
        CompositionVersion parent;
        WorkingCopyState? state = versions.GetCurrentState(project.Id);
        DateTimeOffset at = Now(execution.Request.CreatedAt);
        if (execution.Request.ParentVersionId is null)
        {
            // No speech-span concatenation: includes silence and every original video PTS.
            if (state is not null)
            {
                await VerifyRetryWorkingCopyAsync(execution, state, token).ConfigureAwait(false);
            }
            var element = new CompositionElement(StableId<CompositionElementIdTag>(seed, "full-source-element"),
                new TimeRange(default, fullSource.Range.Duration), OriginRef.FromSourceSpan(fullSource.Id),
                sourceRange: fullSource.Range);
            var copy = new WorkingCopy(state?.WorkingCopy.Id ?? StableId<WorkingCopyIdTag>(seed, "working-copy"), project.Id,
                null, state?.WorkingCopy.CreatedAt ?? at, at, [element]);
            int revision = 0;
            if (state is null) { projects.CreateWorkingCopy(copy, CompositionSnapshotJson.WriteWorkingCopy(copy)); }
            else { revision = projects.UpdateWorkingCopy(copy, CompositionSnapshotJson.WriteWorkingCopy(copy), state.Revision); }
            CheckpointResult baseline = versions.Checkpoint(copy, revision, StableId<CompositionVersionIdTag>(seed, "baseline"),
                execution.Request.ActorSid, at, "Create the full original PTS composition before any automatic edits.");
            await SaveCompositionStageAsync(execution, "baseline-checkpoint", baseline, token).ConfigureAwait(false);
            parent = baseline.Version;
            state = new WorkingCopyState(baseline.WorkingCopy, baseline.WorkingCopyRevision);
        }
        else
        {
            parent = versions.GetVersion(execution.Request.ParentVersionId)
                ?? throw new InvalidOperationException("The accepted regeneration parent is missing.");
            if (parent.ContentDigest != execution.Request.ParentDigest || state is null)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.parent_changed");
            }
            if (state.WorkingCopy.BaseVersionId != parent.Id || !state.WorkingCopy.Elements.SequenceEqual(parent.Elements))
            {
                await VerifyRetryWorkingCopyAsync(execution, state, token).ConfigureAwait(false);
                var restored = new WorkingCopy(state.WorkingCopy.Id, project.Id, parent.Id,
                    state.WorkingCopy.CreatedAt, Now(state.WorkingCopy.UpdatedAt), parent.Elements);
                int revision = projects.UpdateWorkingCopy(restored, CompositionSnapshotJson.WriteWorkingCopy(restored), state.Revision);
                state = new WorkingCopyState(restored, revision);
            }
        }

        RationalNumber frameRate = ingest.Original!.MediaInfo.PrimaryVideoStream.NominalFrameRate
            ?? throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.source_frame_rate_unavailable");
        BundledRenderProfileDefinition landscape = StandardRenderProfileFactory.CreateLandscape(profile,
            execution.Request.SettingsSnapshot, BundledRenderProfileLoader.Load(runtime.LandscapeProfileJson.Span), frameRate);
        int? audioSampleRate = ingest.Original.MediaInfo.Streams.Where(item => item.Kind == MediaStreamType.Audio)
            .OrderBy(item => item.Index).FirstOrDefault()?.SampleRate;
        ValidateSourceTiming(ingest.Original.MediaInfo, ingest.SourcePtsIndex!, landscape.Profile, analysis.Import.AudioTimeline);
        bool Representable(TimeRange range) => IsExactSourceRange(range, ingest.SourcePtsIndex!, landscape.Profile, audioSampleRate!.Value);
        EmphasisSuggestionCandidate[] representableCandidates = evidence.EmphasisCandidates
            .Where(item => Representable(item.Payload.SourceSpan.Range)).ToArray();
        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(seed, "render-preflight"),
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "source-only.render-preflight.v1",
                executionId = seed,
                settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
                rule = "retain-exact-frame-and-audio-sample-boundaries-no-retiming",
                excludedHighlightCandidateIds = evidence.EmphasisCandidates
                    .Where(item => !Representable(item.Payload.SourceSpan.Range)).Select(item => item.Id.ToString()).ToArray(),
                reason = "source_only.candidate_timing_requires_review",
            })), "render-preflight", token).ConfigureAwait(false);
        SourceSpan[] spans = analysis.Import.SourceSpans.Append(fullSource)
            .Concat(evidence.EditorialCandidates.Select(item => item.Payload.SourceSpan))
            .Concat(evidence.EmphasisCandidates.Select(item => item.Payload.SourceSpan))
            .DistinctBy(item => item.Id).ToArray();
        AutomaticEvidence measured = await MeasureAutomaticEvidenceAsync(execution, analysis, evidence, token).ConfigureAwait(false);
        SourceSpan[] decisionSpans = spans.Concat(measured.Candidates.OfType<SilenceSuggestionCandidate>()
            .Select(item => item.Payload.SourceSpan)).DistinctBy(item => item.Id).ToArray();
        DateTimeOffset decisionAt = Now(measured.ModelRuns.Select(item => item.CompletedAt).Append(state.WorkingCopy.UpdatedAt).Max());
        AutoDecisionRequest DecisionRequest(IReadOnlyList<SuggestionCandidate> items) => new(parent,
            state.WorkingCopy, state.Revision, profile, execution.Request.SettingsSnapshot, analysis.Import.Revision,
            decisionSpans, measured.ModelRuns, items, [], execution.Request.ActorSid, decisionAt,
            "Apply only measured, approved FR-005 decisions at exact source PTS and sample boundaries.");
        SourceOnlyAutomaticSelection automatic = SelectAutomaticCandidates(DecisionRequest(measured.Candidates),
            analysis.Import, ingest.SourcePtsIndex!, landscape.Profile, audioSampleRate!.Value, token);
        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(seed, "automatic-decisions"),
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "source-only.automatic-decisions.v1",
                executionId = seed,
                settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
                sourcePtsDigest = ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)).Hex,
                transcriptDigest = analysis.Transcript.ContentDigest.Hex,
                audioDigest = analysis.Audio.ContentDigest.Hex,
                selectedCandidateIds = automatic.Candidates.Select(item => item.Id.ToString()).ToArray(),
                skipped = measured.Skipped.Concat(automatic.Skipped).ToArray(),
                modelRuns = measured.ModelRuns,
                rule = "fr005-policy-then-exact-source-output-frame-and-sample-grid-no-retiming",
            })), "automatic-decisions", token).ConfigureAwait(false);
        if (automatic.Candidates.Count > 0)
        {
            AutoDecisionApplication applied = AutoDecisionOrchestrator.Apply(versions, DecisionRequest(automatic.Candidates), token);
            parent = applied.Checkpoint.Version;
            state = new WorkingCopyState(applied.Checkpoint.WorkingCopy, applied.Checkpoint.WorkingCopyRevision);
            await SaveCompositionStageAsync(execution, "auto-checkpoint", applied.Checkpoint, token).ConfigureAwait(false);
        }

        // Bind the already verified profile's neutral template. No made-up brand assets.
        AppliedBrandTemplate template = new BrandTemplateVersion(
            StableId<SeriesIdTag>(profile.Id.ToString(), "neutral-series"), landscape.TemplateReference,
            BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
        var provenance = new LongFormProvenanceSnapshot(project.Id, analysis.Import.Revision.Id,
            analysis.Transcript.ContentDigest, spans, []);
        LongFormElementInput Omitted(LongFormElementKind kind) => new(
            StableId<LongFormElementIdTag>(seed, "long-form:" + kind), kind, false, null, null, [], []);
        var request = new LongFormCompositionRequest(StableId<LongFormPlanIdTag>(seed, "long-form"), project,
            provenance, template, landscape, LongFormDestination.InternalDistribution, null,
            Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening),
            Omitted(LongFormElementKind.ProgramName), Omitted(LongFormElementKind.EpisodeNumber),
            Omitted(LongFormElementKind.EpisodeTitle), Omitted(LongFormElementKind.PerformerIntroduction),
            Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), [],
            Omitted(LongFormElementKind.Summary),
            [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
            Omitted(LongFormElementKind.Ending), Omitted(LongFormElementKind.PostRollMargin));
        StoredProject stored = projects.GetProject(project.Id)!;
        var storedState = new StandardProjectStoreState(stored.Revision,
            ContentDigest.Compute(Utf8.GetBytes(stored.CanonicalJson)), stored.UpdatedAt);
        StandardThumbnailEvidence thumbnails = await StandardThumbnailEvidence.FromCompletedIngestAsync(ingest, artifacts, token).ConfigureAwait(false);
        MusicCatalog catalog = await new MusicCatalogScanner(runtime.MusicDirectory, new ProcessRunner(), new BinaryVerifier(),
            runtime.FfprobePath, runtime.OperationTimeout).ScanAsync(token).ConfigureAwait(false);
        var musicPlanner = new MusicPlanningService();
        DateTimeOffset composedAt = Now(state.WorkingCopy.UpdatedAt);
        StandardCompositionOrchestrationRequest CreateRequest(MusicPlanningResult music) =>
            new(parent, state.WorkingCopy, state.Revision, storedState,
                profile, execution.Request.SettingsSnapshot, runtime.LandscapeProfileJson, runtime.VerticalProfileJson,
                request, StableId<TimelineMapIdTag>(seed, "parent-timeline"), geometry, frameRate,
                new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                new ContentDigest("3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"),
                music, evidence.TopicMetadata, representableCandidates, [], execution.Request.ActorSid,
                composedAt, "Create a standard plan from the exact accepted settings and actual source evidence.", thumbnails);
        // Resolve actual safe-default LongForm elements first. A filename title overlay
        // has no explicit card range and must not invent a non-dialogue music cue.
        StandardCompositionPreparation preview = StandardCompositionOrchestrator.Prepare(CreateRequest(
            musicPlanner.Plan(catalog, [], MusicPlanningOptions.Default, token)), token);
        IReadOnlyList<StandardMusicCardBinding> cards = BuildMusicCards(preview.Plan.LongForm,
            preview.ChildTimelineMap, analysis.Import.Revision, execution.Request.SettingsSnapshot);
        MusicPlanningResult planning = musicPlanner.Plan(catalog, cards.Select(item => item.Cue).ToArray(), MusicPlanningOptions.Default, token);
        StandardCompositionApplication standard = StandardCompositionOrchestrator.Apply(versions, CreateRequest(planning), token);
        StandardMusicCardBinding[] assignedCards = cards.Where(card => planning.Assignments.Any(assignment =>
            assignment.CueDigest == card.Cue.CueDigest && assignment.TrackDigest is not null)).ToArray();
        IReadOnlyList<BinaryVerificationRequest> tracks = await ResolveMusicTracksAsync(runtime.MusicDirectory, catalog,
            planning, token).ConfigureAwait(false);
        var musicInputs = new StandardMusicRenderInput(catalog, analysis.Import.Revision, assignedCards, tracks);
        await SaveCompositionStageAsync(execution, "composition-checkpoint", standard.Checkpoint, token).ConfigureAwait(false);
        return new CompositionState(standard.Preparation, musicInputs);
    }

    private async Task<AutomaticEvidence> MeasureAutomaticEvidenceAsync(StoredSourceOnlyExecution execution,
        AnalysisState analysis, SourceOnlyAnalysisEvidence evidence, CancellationToken token)
    {
        string seed = execution.Request.ExecutionId.ToString();
        var proposed = new List<SuggestionCandidate>(evidence.EditorialCandidates);
        var runs = new List<ModelRun>();
        var skipped = new List<SourceOnlyAutomaticSkip>();
        ContentDigest editorialRule = CanonicalJson.ComputeDigest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            EditorialPhraseDetector.DictionaryVersion,
            EditorialPhraseDetector.Phrases,
        }));
        runs.Add(new ModelRun(StableId<ModelRunIdTag>(seed, "editorial-rule"), "HubRadio.Deterministic",
            "explicit-editorial-phrase-dictionary", EditorialPhraseDetector.DictionaryVersion,
            editorialRule, ModelHashAvailability.Available, editorialRule, analysis.Transcript.ContentDigest,
            CanonicalJson.ComputeDigest(JsonSerializer.SerializeToUtf8Bytes(evidence.EditorialCandidates)),
            ModelRunOutcome.Succeeded, null, ModelExecutionLocation.Local, null, null, null, null,
            evidence.CreatedAt, Now(evidence.CreatedAt)));

        var values = execution.Request.SettingsSnapshot.Values.ToDictionary(item => item.Key, item => item.Value);
        // The detector's fixed candidate threshold is admitted only when it matches
        // the ACTUAL accepted profile. It is not a benchmark-derived approval flag.
        bool silenceApproved = values["editing.silenceThreshold"].IntegerValue == (long)SilenceDetector.NoiseDb &&
            values["editing.silenceMinimumDuration"].IntegerValue == SilenceDetector.MinimumDurationSeconds &&
            values["editing.silencePadding"].RationalValue == new RationalNumber(3, 10);
        if (silenceApproved)
        {
            DateTimeOffset startedAt = Now(evidence.CreatedAt);
            var modelId = StableId<ModelRunIdTag>(seed, "silence-rule");
            // This adapter reopens existing verified audio; it performs no extraction.
            var audio = new ExtractedAnalysisAudio(analysis.Audio, analysis.Timeline, analysis.Import.AudioTimeline, TimeSpan.Zero);
            SilenceDetectionResult detected = await new SilenceDetector(artifacts, new ProcessRunner(), new BinaryVerifier(), options!.FfmpegPath)
                .DetectAsync(audio, modelId, options.OperationTimeout, token).ConfigureAwait(false);
            StoredArtifact observation = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(seed, "silence-detection"),
                CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
                {
                    schema = "source-only.silence-detection.v1",
                    timeline = detected.Timeline.CanonicalJson,
                    detected.Configuration,
                    detected.Candidates,
                    detected.ProcessDuration,
                })), "silence-detection", token).ConfigureAwait(false);
            runs.Add(new ModelRun(modelId, "FFmpeg", "silencedetect", SilenceDetector.ThresholdVersion,
                new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), ModelHashAvailability.Available,
                CanonicalJson.ComputeDigest(JsonSerializer.SerializeToUtf8Bytes(detected.Configuration)), analysis.Audio.ContentDigest,
                observation.ContentDigest, ModelRunOutcome.Succeeded, null, ModelExecutionLocation.Local, null, null, null, null,
                startedAt, Now(startedAt)));
            proposed.AddRange(detected.Candidates.Select(item => SuggestionService.Register(item, [], token)));
        }
        else { skipped.Add(new(null, "source_only.silence_settings_not_approved")); }

        if (values["editing.adjacentExactRepetitionAutoExclude"].BooleanValue == true)
        {
            DateTimeOffset startedAt = Now(evidence.CreatedAt);
            var modelId = StableId<ModelRunIdTag>(seed, "repetition-rule");
            string directory = Path.Combine(workspace, "projects", execution.Request.ProjectId.ToString(), "staging", "source-only-repetition-" + seed);
            CreateDirectory(workspace, directory);
            try
            {
                LocalRepetitionResult result = await new LocalAnalysisWorker(new ProcessRunner(), options!.PythonExecutable, workspace)
                    .FindRepetitionsAsync(analysis.Import.Revision, analysis.Transcript.ContentDigest, modelId,
                        directory, options.OperationTimeout, token).ConfigureAwait(false);
                StoredArtifact observation = await SaveBytesAsync(execution, StableId<ArtifactIdTag>(seed, "repetition-raw"),
                    result.RawResultBytes.ToArray(), "repetition-raw", token).ConfigureAwait(false);
                // The recorded artifact here is the verified interpreter, not a
                // fabricated hash of a Python rule file or a manually approved edit.
                runs.Add(new ModelRun(modelId, "CPython", "hubradio_worker.repetition",
                    RepetitionSuggestionPayload.CanonicalRuleVersion, options.PythonVerification.ExpectedDigest,
                    ModelHashAvailability.Available, CanonicalJson.ComputeDigest(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        method = "repetition",
                        rule = RepetitionSuggestionPayload.CanonicalRuleVersion,
                        executableDigest = options.PythonVerification.ExpectedDigest.Hex,
                        inputShape = "actual-transcript-word-tokens-v1",
                    })), analysis.Transcript.ContentDigest, observation.ContentDigest, ModelRunOutcome.Succeeded, null,
                    ModelExecutionLocation.Local, null, null, null, null, startedAt, Now(startedAt)));
                proposed.AddRange(result.Candidates);
                _ = RequirePath(result.ArtifactPath, directory: false);
                File.Delete(result.ArtifactPath);
            }
            catch (LocalAnalysisWorkerException exception) when (exception.Code is "repetition.input_too_large" or "repetition.invalid_input")
            {
                skipped.Add(new(null, exception.Code == "repetition.input_too_large"
                    ? "source_only.repetition_input_too_large_retained" : "source_only.repetition_input_undecidable_retained"));
            }
            finally
            {
                _ = RequirePath(directory, directory: true);
                // Never sweep a failed worker's residual file and claim cleanup.
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); }
            }
        }
        return new AutomaticEvidence(proposed.AsReadOnly(), runs.AsReadOnly(), skipped.AsReadOnly());
    }

    internal static SourceOnlyAutomaticSelection SelectAutomaticCandidates(AutoDecisionRequest request,
        TranscriptImportResult import, PtsIndex index, RenderProfile renderProfile, int audioSampleRate, CancellationToken token = default)
    {
        if (request.Transcript.Id != import.Revision.Id ||
            TranscriptSnapshotJson.WriteRevision(request.Transcript) != TranscriptSnapshotJson.WriteRevision(import.Revision) ||
            import.AudioTimeline.SourceAssetId != index.CandidateId || import.AudioTimeline.SourceContentDigest != index.ContentDigest)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.auto_input_mismatch");
        }
        var selected = new List<SuggestionCandidate>();
        var skipped = new List<SourceOnlyAutomaticSkip>();
        var deleted = new List<TimeRange>();
        var retainedComparisons = new List<TimeRange>();
        foreach (SuggestionCandidate candidate in request.Candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var proposed = new AutoDecisionRequest(request.Parent, request.WorkingCopy, request.ExpectedWorkingCopyRevision,
                    request.SettingsProfile, request.Settings, request.Transcript, request.SourceSpans, request.ModelRuns,
                    [candidate], request.DecisionHistory, request.ActorSid, request.CreatedAt, request.ChangeReason);
                AutoDecisionPreparation trial = AutoDecisionOrchestrator.Prepare(proposed, token);
                TimeRange cut = trial.Evidence.Single().DeletedSourceRange;
                string? reason = null;
                TimeRange? comparisonRange = null;
                IReadOnlyList<EntityId<TranscriptTokenIdTag>> deletedTokenIds = candidate switch
                {
                    EditorialPhraseSuggestionCandidate phrase => phrase.Payload.SourceSpan.TranscriptTokenIds,
                    RepetitionSuggestionCandidate repeated => repeated.Payload.EvidenceTokenIds,
                    _ => [],
                };
                if (candidate is RepetitionSuggestionCandidate repetition &&
                    repetition.Payload.Features.Rule == RepetitionReasonFeatures.AdjacentExactUtteranceRule)
                {
                    // ASR imports use one anonymous speaker token for all utterances;
                    // equality of that placeholder does not prove the same speaker.
                    reason = "source_only.repetition_speaker_unverified_retained";
                }
                else if (candidate is SilenceSuggestionCandidate && import.Revision.Utterances.Any(item => item.Range.Overlaps(cut)))
                { reason = "source_only.silence_speech_overlap_retained"; }
                else if (candidate is not SilenceSuggestionCandidate && import.Revision.Utterances
                    .Where(item => item.Range.Overlaps(cut)).Any(item => item.Tokens.Any(word => word.Range is null)))
                { reason = "source_only.untimed_words_retained"; }
                else if (candidate is not SilenceSuggestionCandidate && import.TimingEvidence.Any(item =>
                    (item.RequiresReview || item.StartWasRounded || item.EndWasRounded) &&
                    import.Revision.Utterances.Any(utterance => utterance.Id == item.UtteranceId && utterance.Range.Overlaps(cut))))
                { reason = "source_only.inexact_token_timing_retained"; }
                else if (candidate is not SilenceSuggestionCandidate && import.Revision.Utterances.Any(utterance =>
                    utterance.Tokens.Any(word => word.Range is TimeRange range && range.Overlaps(cut) && !deletedTokenIds.Contains(word.Id))))
                { reason = "source_only.other_words_overlap_retained"; }
                else if (candidate is not SilenceSuggestionCandidate && import.Revision.Utterances
                    .Where(utterance => utterance.Range.Overlaps(cut)).Any(utterance => HasUnrepresentedWords(utterance, token)))
                { reason = "source_only.unrepresented_words_retained"; }
                else if (!IsExactSourceRange(cut, index, renderProfile, audioSampleRate) ||
                    trial.Child.Elements.Any(element => element.SourceRange is not TimeRange source ||
                        !IsExactSourceRange(source, index, renderProfile, audioSampleRate) ||
                        !FfmpegCompiler.IsOnOutputGrid(element.OutputRange.Start, renderProfile) ||
                        !FfmpegCompiler.IsOnOutputGrid(element.OutputRange.Duration, renderProfile)))
                { reason = "source_only.auto_cut_not_on_exact_grid_retained"; }
                else if (trial.Child.Elements.Count == 0 || deleted.Any(item => item.Overlaps(cut)) || retainedComparisons.Any(item => item.Overlaps(cut)))
                { reason = "source_only.auto_cut_conflict_retained"; }
                if (reason is null && candidate is RepetitionSuggestionCandidate exact)
                {
                    Token[] compared = import.Revision.Utterances.Single(item => item.SourceSpanId == exact.Payload.Features.ComparisonSourceSpanId)
                        .Tokens.Where(item => exact.Payload.Features.ComparisonTokenIds.Contains(item.Id)).ToArray();
                    if (compared.Length == 0 || compared.Any(item => item.Range is null))
                    { reason = "source_only.untimed_words_retained"; }
                    else
                    {
                        comparisonRange = new TimeRange(compared[0].Range!.Value.Start, compared[^1].Range!.Value.End - compared[0].Range!.Value.Start);
                        if (deleted.Any(item => item.Overlaps(comparisonRange.Value))) { reason = "source_only.auto_cut_conflict_retained"; }
                    }
                }
                if (reason is null)
                {
                    AutoDecisionPreparation combined = AutoDecisionOrchestrator.Prepare(new AutoDecisionRequest(request.Parent,
                        request.WorkingCopy, request.ExpectedWorkingCopyRevision, request.SettingsProfile, request.Settings,
                        request.Transcript, request.SourceSpans, request.ModelRuns, selected.Append(candidate).ToArray(),
                        request.DecisionHistory, request.ActorSid, request.CreatedAt, request.ChangeReason), token);
                    if (combined.Child.Elements.Count is 0 or > StandardRenderGraphFactory.MaximumCuts)
                    { reason = "source_only.auto_cut_conflict_retained"; }
                }
                if (reason is not null) { skipped.Add(new(candidate.Id, reason)); continue; }
                selected.Add(candidate);
                deleted.Add(cut);
                if (comparisonRange is not null) { retainedComparisons.Add(comparisonRange.Value); }
            }
            catch (AutoDecisionException exception)
            {
                skipped.Add(new(candidate.Id, exception.Reason == AutoDecisionFailureReason.SettingsMismatch
                    ? "source_only.auto_settings_not_approved_retained" : "source_only.auto_evidence_undecidable_retained"));
            }
            catch (ArgumentException)
            {
                skipped.Add(new(candidate.Id, "source_only.auto_evidence_undecidable_retained"));
            }
            catch (SubtitleSegmentationException)
            {
                skipped.Add(new(candidate.Id, "source_only.unrepresented_words_retained"));
            }
        }
        return new SourceOnlyAutomaticSelection(selected.AsReadOnly(), skipped.AsReadOnly());
    }

    private static bool HasUnrepresentedWords(Utterance utterance, CancellationToken token)
    {
        int[] starts = SubtitleSegmenter.FindUniqueOrderedTextStarts(utterance.Text,
            utterance.Tokens.Select(word => word.Text).ToArray(), token);
        int cursor = 0;
        for (int i = 0; i < starts.Length; i++)
        {
            if (!utterance.Text[cursor..starts[i]].EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune))) { return true; }
            cursor = starts[i] + utterance.Tokens[i].Text.Length;
        }
        return !utterance.Text[cursor..].EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune));
    }

    internal static bool IsExactSourceRange(TimeRange range, PtsIndex index, RenderProfile profile, int audioSampleRate) =>
        range.Duration > default(RationalTime) && range.Start >= index.PresentationStart && range.End <= index.PresentationEnd &&
        FfmpegCompiler.IsOnOutputGrid(range.Start, profile) && FfmpegCompiler.IsOnOutputGrid(range.Duration, profile) &&
        FfmpegCompiler.IsOnSampleGrid(range.Start, audioSampleRate) && FfmpegCompiler.IsOnSampleGrid(range.Duration, audioSampleRate) &&
        index.Entries.Any(frame => frame.PresentationTime == range.Start) &&
        (range.End == index.PresentationEnd || index.Entries.Any(frame => frame.PresentationTime == range.End));

    internal static void ValidateSourceTiming(MediaInfo media, PtsIndex index, RenderProfile profile, AnalysisAudioTimeline audio)
    {
        MediaStreamInfo[] videoStreams = media.Streams.Where(item => item.Kind == MediaStreamType.Video).ToArray();
        MediaStreamInfo[] audioStreams = media.Streams.Where(item => item.Kind == MediaStreamType.Audio).ToArray();
        if (videoStreams.Length != 1 || audioStreams.Length != 1 || videoStreams[0].Start != default(RationalTime) ||
            audioStreams[0].Start != default(RationalTime) || audioStreams[0].SampleRate is not int sampleRate ||
            audioStreams[0].Duration is not RationalTime duration || duration < index.PresentationEnd ||
            index.PresentationStart != default(RationalTime) || videoStreams[0].NominalFrameRate != profile.FrameRate ||
            audio.FirstDecodedAudioFramePresentationTime != default(RationalTime) ||
            audio.AnalysisDuration < index.PresentationEnd || audio.SourceAssetId != index.CandidateId ||
            audio.SourceContentDigest != index.ContentDigest || index.StreamIndex != videoStreams[0].Index ||
            !IsExactSourceRange(index.PresentationRange, index, profile, sampleRate))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.unsupported_source_timing");
        }
        for (int i = 0; i < index.Entries.Count; i++)
        {
            PtsIndexEntry frame = index.Entries[i];
            if (!FfmpegCompiler.IsOnOutputGrid(frame.PresentationTime, profile) ||
                !FfmpegCompiler.IsOnOutputGrid(frame.Duration, profile) ||
                (i > 0 && index.Entries[i - 1].PresentationTime + index.Entries[i - 1].Duration != frame.PresentationTime))
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.unsupported_source_timing");
            }
        }
    }

    internal static IReadOnlyList<StandardMusicCardBinding> BuildMusicCards(LongFormCompositionPlan plan, TimelineMap timeline,
        TranscriptRevision transcript, DefaultSettingsSnapshot settingsSnapshot)
    {
        if (plan.Provenance.TranscriptRevisionId != transcript.Id ||
            plan.Provenance.TranscriptRevisionDigest != ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(transcript))))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.music_transcript_mismatch");
        }
        IReadOnlyList<string> eligible = settingsSnapshot.Values.Single(item => item.Key == "assets.musicEligibleSections").Value.TextListValue;
        var cards = new List<StandardMusicCardBinding>();
        var ranges = new List<TimeRange>();
        var spans = plan.Provenance.SourceSpans.ToDictionary(item => item.Id);
        foreach (LongFormElementPlan element in plan.Elements)
        {
            (MusicCueKind? kind, string section) = element.Kind switch
            {
                LongFormElementKind.EpisodeTitle => (MusicCueKind.TitleCard, "titleCard"),
                LongFormElementKind.Topic => (MusicCueKind.TopicCard, "topicCard"),
                LongFormElementKind.Ending => (MusicCueKind.EndCard, "endCard"),
                _ => ((MusicCueKind?)null, string.Empty),
            };
            if (kind is null || !eligible.Contains(section, StringComparer.Ordinal) ||
                element.State != LongFormElementState.Included || element.Text is null || element.OutputRange is not TimeRange range ||
                range.Duration <= default(RationalTime) || !FfmpegCompiler.IsOnOutputGrid(range.Start, plan.Profile.Profile) ||
                !FfmpegCompiler.IsOnOutputGrid(range.Duration, plan.Profile.Profile) || ranges.Any(item => item.Overlaps(range))) { continue; }
            RationalTime covered = default;
            bool foreground = false;
            foreach (TimelineMapEntry entry in timeline.Entries.Where(entry => entry.OutputRange.Overlaps(range)))
            {
                if (entry.SourceRange is not TimeRange source || entry.Origin.SourceSpanId is null ||
                    !spans.TryGetValue(entry.Origin.SourceSpanId, out SourceSpan? span) ||
                    source.Duration != entry.OutputRange.Duration || source.Start < span.Range.Start || source.End > span.Range.End)
                { foreground = true; break; }
                RationalTime start = range.Start > entry.OutputRange.Start ? range.Start : entry.OutputRange.Start;
                RationalTime end = range.End < entry.OutputRange.End ? range.End : entry.OutputRange.End;
                var sourceRange = new TimeRange(source.Start + start - entry.OutputRange.Start, end - start);
                covered += end - start;
                if (transcript.Utterances.Any(utterance => utterance.Range.Overlaps(sourceRange))) { foreground = true; break; }
            }
            if (foreground || covered != range.Duration) { continue; }
            cards.Add(new StandardMusicCardBinding(element.Id, StandardMusicRenderInput.CreateCue(element.Id, kind.Value, range, false)));
            ranges.Add(range);
        }
        return cards.AsReadOnly();
    }

    private static async Task<IReadOnlyList<BinaryVerificationRequest>> ResolveMusicTracksAsync(string musicDirectory,
        MusicCatalog catalog, MusicPlanningResult planning, CancellationToken token)
    {
        var remaining = planning.Assignments.Where(item => item.TrackDigest is not null)
            .Select(item => item.TrackDigest!).ToHashSet();
        var tracks = new List<BinaryVerificationRequest>();
        if (remaining.Count == 0) { return tracks.AsReadOnly(); }
        _ = RequirePath(musicDirectory, directory: true);
        foreach (string path in Directory.EnumerateFiles(musicDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            FileStream held;
            try { held = OpenHeldFile(path); }
            catch (SourceOnlySourceException) { continue; } // Scanner may already have excluded this unrelated unsafe entry.
            using (held)
            {
                ContentDigest digest = ContentDigest.FromBytes(await SHA256.HashDataAsync(held, token).ConfigureAwait(false));
                if (!remaining.Contains(digest)) { continue; }
                MusicTrack track = catalog.Tracks.Single(item => item.ContentDigest == digest);
                if (held.Length != track.ByteSize) { throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.music_changed"); }
                tracks.Add(new BinaryVerificationRequest("source-only.music", path, digest, track.ByteSize));
                remaining.Remove(digest);
                if (remaining.Count == 0) { break; }
            }
        }
        if (remaining.Count != 0) { throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.music_changed"); }
        return tracks.AsReadOnly();
    }

    internal async Task SaveCompositionStageAsync(StoredSourceOnlyExecution execution, string stage,
        CheckpointResult checkpoint, CancellationToken token)
    {
        _ = await SaveJsonAsync(execution, StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), stage),
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "source-only.composition-checkpoint.v1",
                executionId = execution.Request.ExecutionId.ToString(),
                stage,
                versionId = checkpoint.Version.Id.ToString(),
                digest = checkpoint.Version.ContentDigest.Hex,
                workingCopyRevision = checkpoint.WorkingCopyRevision.ToString(CultureInfo.InvariantCulture),
                workingCopy = checkpoint.CanonicalWorkingCopyJson,
                settingsDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
            })), stage, token).ConfigureAwait(false);
    }

    private async Task VerifyRetryWorkingCopyAsync(StoredSourceOnlyExecution execution, WorkingCopyState current,
        CancellationToken token)
    {
        EntityId<SourceOnlyExecutionIdTag>? previousId = execution.RetryOfExecutionId;
        string snapshot = CompositionSnapshotJson.WriteWorkingCopy(current.WorkingCopy);
        var seen = new HashSet<EntityId<SourceOnlyExecutionIdTag>>();
        while (previousId is not null && seen.Count < SourceOnlyExecutionStore.MaximumRetryLineageLength && seen.Add(previousId))
        {
            StoredSourceOnlyExecution previous = RequireExecution(previousId);
            if (previous.VersionId is not null || previous.ResultJson is not null ||
                previous.Request.ProjectId != execution.Request.ProjectId ||
                previous.Request.SourceCandidateId != execution.Request.SourceCandidateId)
            {
                break;
            }
            foreach (string stage in new[] { "composition-checkpoint", "auto-checkpoint", "baseline-checkpoint" })
            {
                var id = StableId<ArtifactIdTag>(previousId.ToString(), stage);
                if (artifacts.GetArtifact(id) is null) { continue; }
                using JsonDocument document = JsonDocument.Parse(await ReadArtifactBytesAsync(id,
                    execution.Request.ProjectId, MaximumTranscriptBytes, token).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                RequireShape(root, "schema", "executionId", "stage", "versionId", "digest", "workingCopyRevision", "workingCopy", "settingsDigest");
                if (Text(root, "schema") == "source-only.composition-checkpoint.v1" && Text(root, "stage") == stage &&
                    Text(root, "executionId") == previousId.ToString() && Text(root, "workingCopy") == snapshot &&
                    Text(root, "workingCopyRevision") == current.Revision.ToString(CultureInfo.InvariantCulture) &&
                    Text(root, "versionId") == current.WorkingCopy.BaseVersionId?.ToString() &&
                    Text(root, "settingsDigest") == previous.Request.SettingsSnapshot.ContentDigest.Hex)
                {
                    CompositionVersion version = versions.GetVersion(current.WorkingCopy.BaseVersionId!)
                        ?? throw new InvalidOperationException("The previous native version is missing.");
                    if (version.ContentDigest.Hex == Text(root, "digest") &&
                        version.Elements.SequenceEqual(current.WorkingCopy.Elements) && version.CreatedAt == current.WorkingCopy.UpdatedAt)
                    {
                        return; // Proven native head only; never discard subsequent user edits.
                    }
                }
            }
            previousId = previous.RetryOfExecutionId;
        }
        throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.retry_working_copy_changed");
    }

    internal static SourceSpan CreateFullSourceSpan(PtsIndex index, TranscriptImportResult import)
    {
        if (index.CandidateId != import.AudioTimeline.SourceAssetId || index.ContentDigest != import.AudioTimeline.SourceContentDigest)
        {
            throw new InvalidOperationException("The source PTS and analysis audio do not identify the same media.");
        }
        // Full original VIDEO PTS envelope, represented on the shared media source clock.
        // Token membership only asserts actual recognized evidence INSIDE that envelope;
        // it does not shorten the envelope to speech, or claim speech in its silent gaps.
        var range = new TimeRange(index.PresentationStart, index.PresentationEnd - index.PresentationStart);
        EntityId<TranscriptTokenIdTag>[] tokens = import.Revision.Utterances
            .Where(utterance => utterance.Range.Start >= range.Start && utterance.Range.End <= range.End)
            .SelectMany(utterance => utterance.Tokens).Select(token => token.Id).ToArray();
        return new SourceSpan(StableId<SourceSpanIdTag>(index.JobId.ToString(), "full-source-pts"), index.CandidateId,
            "audio:" + import.AudioTimeline.SourceAudioStreamIndex.ToString(CultureInfo.InvariantCulture),
            range, index.ContentDigest, import.Revision.Id, tokens);
    }

    /// <summary>
    /// Adapter for the utterance-grounded subtitle builder. The temporary projection is
    /// ONLY a source/output mapping view: it is never checkpointed or rendered. It retains
    /// the full output extent (including non-speech gaps), calls the real builder on actual
    /// utterance spans, then creates initial cues against the unchanged production version.
    /// This avoids pretending that a whole-media PTS span is a single ASR utterance.
    /// No text, token time, speech range or production cut is fabricated or retimed.
    /// </summary>
    internal static StandardSubtitleBuildResult BuildProductionSubtitles(CompositionVersion version,
        TimelineMap timeline, LayoutSpec layout, RenderProfile renderProfile, TranscriptImportResult import,
        ContentDigest transcriptDigest, IReadOnlyList<SourceSpan> productionSpans,
        DefaultSettingsProfile settingsProfile, DefaultSettingsSnapshot snapshot, RenderFontEvidence font,
        string actorSid, DateTimeOffset createdAt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (timeline.CompositionVersionId != version.Id ||
            !TimelineMapBuilder.Build(timeline.Id, version).Entries.SequenceEqual(timeline.Entries) ||
            version.ContentDigest != RenderGraphCanonicalEvidence.ComputeCompositionDigest(version.Elements) ||
            transcriptDigest != ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(import.Revision))))
        {
            throw new InvalidOperationException("The subtitle projection inputs are inconsistent.");
        }
        var byId = productionSpans.ToDictionary(item => item.Id);
        var projected = new List<CompositionElement>();
        foreach (TimelineMapEntry entry in timeline.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Origin.SourceSpanId is null || entry.SourceRange is not TimeRange cut ||
                !byId.TryGetValue(entry.Origin.SourceSpanId, out SourceSpan? source) ||
                cut.Start < source.Range.Start || cut.End > source.Range.End || entry.OutputRange.Duration != cut.Duration ||
                source.AssetId != import.AudioTimeline.SourceAssetId || source.SourceDigest != import.AudioTimeline.SourceContentDigest)
            {
                throw new InvalidOperationException("The subtitle projection requires exact source cuts.");
            }
            SourceSpan[] utterances = import.SourceSpans.Where(span => span.AssetId == source.AssetId &&
                span.SourceDigest == source.SourceDigest && span.StreamId == source.StreamId &&
                span.Range.Start < cut.End && span.Range.End > cut.Start).ToArray();
            RationalTime[] boundaries = utterances.SelectMany(span => new[] { span.Range.Start, span.Range.End })
                .Where(time => time > cut.Start && time < cut.End).Append(cut.Start).Append(cut.End).Distinct().Order().ToArray();
            for (int index = 1; index < boundaries.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (projected.Count >= 8192)
                {
                    throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.subtitle_mapping_limit");
                }
                var range = new TimeRange(boundaries[index - 1], boundaries[index] - boundaries[index - 1]);
                SourceSpan? utterance = utterances.FirstOrDefault(span => span.Range.Start <= range.Start && span.Range.End >= range.End);
                var output = new TimeRange(entry.OutputRange.Start + range.Start - cut.Start, range.Duration);
                projected.Add(new CompositionElement(StableId<CompositionElementIdTag>(version.Id.ToString(),
                    "subtitle-view:" + projected.Count.ToString(CultureInfo.InvariantCulture)), output,
                    utterance is null ? OriginRef.FromAsset(source.AssetId) : OriginRef.FromSourceSpan(utterance.Id),
                    sourceRange: utterance is null ? null : range));
            }
        }
        var projection = new CompositionVersion(StableId<CompositionVersionIdTag>(version.Id.ToString(), "subtitle-projection"),
            version.ProjectId, version.Id, version.CreatedByActorId, version.CreatedAt,
            "Temporary subtitle source mapping only; not a rendered or checkpointed composition.",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(projected), projected);
        TimelineMap projectionTimeline = TimelineMapBuilder.Build(
            StableId<TimelineMapIdTag>(projection.Id.ToString(), "timeline"), projection);
        StandardSubtitleBuildResult built = StandardSubtitleBuilder.BuildWithEvidence(projection, projectionTimeline,
            layout, renderProfile, import.Revision, transcriptDigest, import.SourceSpans,
            settingsProfile, snapshot, font, actorSid, createdAt, token);
        var histories = new List<SubtitleCueHistory>();
        foreach (SubtitleCueRevision cue in built.Subtitles.Revisions)
        {
            token.ThrowIfCancellationRequested();
            SubtitleCueCandidate candidate = built.Subtitles.Candidates.Single(item => item.Id == cue.SourceCandidateId);
            // Recheck the final output mapping against the ORIGINAL production timeline,
            // not just the helper view used by the builder.
            bool mapped = timeline.Entries.Any(entry => entry.SourceRange is TimeRange range &&
                entry.Origin.SourceSpanId is not null && byId.TryGetValue(entry.Origin.SourceSpanId, out var source) &&
                source.AssetId == candidate.SourceSpan.AssetId && source.SourceDigest == candidate.SourceSpan.SourceDigest &&
                source.StreamId == candidate.SourceSpan.StreamId && range.Start <= candidate.SourceRange.Start &&
                range.End >= candidate.SourceRange.End && cue.Snapshot.Cue.Range == new TimeRange(
                    entry.OutputRange.Start + candidate.SourceRange.Start - range.Start, candidate.SourceRange.Duration));
            if (!mapped) { throw new InvalidOperationException("The generated subtitle does not map to the production cuts."); }
            histories.Add(SubtitleService.CreateInitial(version, candidate, cue.Snapshot.LayoutResolution,
                StableId<CueIdTag>(version.Id.ToString(), "subtitle-cue:" + candidate.Id),
                StableId<SubtitleCueRevisionIdTag>(version.Id.ToString(), "subtitle-revision:" + candidate.Id),
                cue.Snapshot.Cue.Range, actorSid, createdAt,
                "Bind actual source-token subtitles to the unchanged production composition.", token));
        }
        return new StandardSubtitleBuildResult(new StandardSubtitleInput(histories, built.Subtitles.DisplayProfile,
            built.Subtitles.AssProfile, warnings: built.Warnings), built.Warnings, built.SegmentationRuleVersion, built.SettingsDigest);
    }

    private void AppendExactVersion(CompositionVersion version)
    {
        StoredCompositionVersion? stored = projects.GetCompositionVersion(version.Id);
        string json = CompositionSnapshotJson.WriteVersion(version);
        if (stored is null) { projects.AppendCompositionVersion(version, json); }
        else if (stored.CanonicalJson != json)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.version_conflict");
        }
    }

    private async Task<StoredArtifact> SaveJsonAsync(StoredSourceOnlyExecution execution, EntityId<ArtifactIdTag> id,
        string json, string role, CancellationToken token)
    {
        if (Utf8.GetByteCount(json) > MaximumTranscriptBytes) { throw new InvalidOperationException("Native evidence exceeds its byte limit."); }
        return await SaveBytesAsync(execution, id, Utf8.GetBytes(CanonicalJson.Canonicalize(json)), role, token).ConfigureAwait(false);
    }

    private async Task<StoredArtifact> SaveBytesAsync(StoredSourceOnlyExecution execution, EntityId<ArtifactIdTag> id,
        byte[] bytes, string role, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > MaximumTranscriptBytes) { throw new InvalidOperationException("Native evidence exceeds its byte limit."); }
        ContentDigest digest = ContentDigest.Compute(bytes);
        StoredArtifact? existing = artifacts.GetArtifact(id);
        if (existing is not null)
        {
            existing = await artifacts.ValidateRegisteredAsync(id, token).ConfigureAwait(false);
            if (existing.ProjectId != execution.Request.ProjectId || existing.Kind != ArtifactKind.Analysis ||
                existing.ContentDigest != digest || existing.ByteSize != bytes.LongLength)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.evidence_conflict");
            }
            return existing;
        }
        string staging = Path.Combine(workspace, "projects", execution.Request.ProjectId.ToString(), "staging", "source-only-evidence");
        CreateDirectory(workspace, staging);
        string path = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            StoredArtifact artifact = await artifacts.PromoteAsync(new ArtifactPromotionRequest(id, execution.Request.ProjectId,
                ArtifactKind.Analysis, path, "source-only/" + id + "." + role + ".json", digest, bytes.LongLength,
                Now(execution.Request.CreatedAt)), token).ConfigureAwait(false);
            _ = await artifacts.ValidateRegisteredAsync(id, token).ConfigureAwait(false);
            if (artifact.ContentDigest != digest || artifact.ByteSize != bytes.LongLength)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.evidence_promotion_failed");
            }
            return artifact;
        }
        finally
        {
            // Cleanup only the one file allocated above; never sweep another job's staging.
            if (File.Exists(path))
            {
                try { _ = RequirePath(path, directory: false); File.Delete(path); }
                catch { throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.evidence_cleanup_failed"); }
            }
        }
    }

    private async Task<byte[]> ReadArtifactBytesAsync(EntityId<ArtifactIdTag> id, EntityId<ProjectIdTag> projectId,
        int maximumBytes, CancellationToken token)
    {
        StoredArtifact stored = artifacts.GetArtifact(id) ?? throw new InvalidOperationException("Native evidence is missing.");
        if (stored.Kind != ArtifactKind.Analysis || stored.ProjectId != projectId || stored.ByteSize <= 0 || stored.ByteSize > maximumBytes)
        {
            throw new InvalidOperationException("Native evidence metadata is invalid.");
        }
        await using ValidatedArtifactReadStream stream = await artifacts.OpenValidatedReadAsync(id, token).ConfigureAwait(false);
        if (stream.ContentDigest != stored.ContentDigest || stream.ByteSize != stored.ByteSize ||
            stream.Kind != stored.Kind || stream.ProjectId != stored.ProjectId)
        {
            throw new InvalidOperationException("Native evidence changed.");
        }
        if (stream.Length != stored.ByteSize) { throw new InvalidOperationException("Native evidence changed."); }
        byte[] bytes = new byte[checked((int)stored.ByteSize)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.Length != bytes.Length || ContentDigest.Compute(bytes) != stored.ContentDigest)
        {
            throw new InvalidOperationException("Native evidence changed.");
        }
        return bytes;
    }

    private static object ArtifactReference(StoredArtifact artifact) => new
    {
        id = artifact.Id.ToString(),
        digest = artifact.ContentDigest.Hex,
        byteSize = artifact.ByteSize.ToString(CultureInfo.InvariantCulture),
    };
    private async Task<StoredArtifact> ValidateReferenceAsync(JsonElement reference, EntityId<ArtifactIdTag> expectedId,
        EntityId<ProjectIdTag> projectId, CancellationToken token)
    {
        RequireShape(reference, "id", "digest", "byteSize");
        StoredArtifact artifact = await artifacts.ValidateRegisteredAsync(expectedId, token).ConfigureAwait(false);
        if (Text(reference, "id") != expectedId.ToString() || Text(reference, "digest") != artifact.ContentDigest.Hex ||
            Text(reference, "byteSize") != artifact.ByteSize.ToString(CultureInfo.InvariantCulture) ||
            artifact.ProjectId != projectId || artifact.Kind != ArtifactKind.Analysis)
        {
            throw new FormatException("Native artifact binding is invalid.");
        }
        return artifact;
    }

    private async Task<StoredSourceOnlyExecution> CompleteFromReceiptAsync(StoredSourceOnlyExecution execution, CancellationToken token)
    {
        try
        {
            if (RequireJob(execution.Request.RootJobId).Job.State != JobState.Succeeded)
            {
                throw new InvalidOperationException("Only a successful root has a completable receipt.");
            }
            string json = Utf8.GetString(await ReadArtifactBytesAsync(
                StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "result-receipt"),
                execution.Request.ProjectId, SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes, token).ConfigureAwait(false));
            SourceOnlyResult result = SourceOnlyResult.ParseCanonical(json);
            result.ValidateBinding(execution);
            var resultService = new SourceOnlyResultService(artifacts, new AutomatedEvaluationStore(options!.DatabasePath),
                new QuickStartWorkflowStore(options.DatabasePath), projects);
            SourceOnlyResultValidation validation = await resultService.ValidateAsync(result, execution, token).ConfigureAwait(false);
            if (result.HasBlockingFailures || result.StagingCleanupRequired || !validation.IsValid || result.Artifacts.Any(item => !item.Delivered))
            {
                throw new InvalidOperationException("The delivered result receipt is not valid.");
            }
            return executions.UpdateState(execution.Request.ExecutionId, execution.Revision, SourceOnlyExecutionPhase.Completed,
                result.CanonicalJson, result.Binding.VersionId, result.Binding.VersionDigest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return SetPhase(execution.Request.ExecutionId, SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.result_reconciliation_required"); }
    }

    private static SourceSpan ReidentifySpan(SourceSpan source, EntityId<SourceSpanIdTag> id) =>
        new(id, source.AssetId, source.StreamId, source.Range, source.SourceDigest, source.TranscriptRevisionId, source.TranscriptTokenIds);
    private static string Text(JsonElement root, string key) => root.GetProperty(key).GetString() ?? throw new FormatException();
    private static DateTimeOffset ParseUtc(string text) => DateTimeOffset.TryParseExact(text, "O",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value.Offset == TimeSpan.Zero &&
        value.ToString("O", CultureInfo.InvariantCulture) == text ? value : throw new FormatException();
    private static void RequireShape(JsonElement root, params string[] keys)
    {
        var remaining = keys.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) { throw new FormatException(); }
        }
        if (remaining.Count != 0) { throw new FormatException(); }
    }

    private sealed class AnalysisIds(string seed)
    {
        internal EntityId<ArtifactIdTag> Audio { get; } = StableId<ArtifactIdTag>(seed, "analysis-audio");
        internal EntityId<ArtifactIdTag> Timeline { get; } = StableId<ArtifactIdTag>(seed, "analysis-timeline");
        internal EntityId<ArtifactIdTag> Raw { get; } = StableId<ArtifactIdTag>(seed, "asr-raw");
        internal EntityId<ArtifactIdTag> Transcript { get; } = StableId<ArtifactIdTag>(seed, "machine-transcript");
        internal EntityId<ArtifactIdTag> Checkpoint { get; } = StableId<ArtifactIdTag>(seed, "analysis-checkpoint");
        internal EntityId<TranscriptRevisionIdTag> Revision { get; } = StableId<TranscriptRevisionIdTag>(seed, "machine-revision");
        internal EntityId<TranscriptSpeakerIdTag> Speaker { get; } = StableId<TranscriptSpeakerIdTag>(seed, "anonymous-speaker");
    }
    private sealed record AnalysisState(TranscriptImportResult Import, StoredArtifact Audio,
        StoredArtifact Timeline, StoredArtifact Raw, StoredArtifact Transcript);
    private sealed record CompositionState(StandardCompositionPreparation Preparation, StandardMusicRenderInput Music);
    private sealed record AutomaticEvidence(IReadOnlyList<SuggestionCandidate> Candidates, IReadOnlyList<ModelRun> ModelRuns,
        IReadOnlyList<SourceOnlyAutomaticSkip> Skipped);
    private sealed class HeldInputs : IDisposable
    {
        internal List<FileStream> Files { get; } = [];
        internal ContentDigest ModelWeightsDigest { get; set; } = null!;
        public void Dispose() { for (int i = Files.Count - 1; i >= 0; i--) { Files[i].Dispose(); } }
    }

    private static string MediaTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        _ => throw new PipelineFailure(SourceOnlyExecutionPhase.Failed, "source_only.unsupported_source_type"),
    };

    private StoredSourceOnlyExecution SetPhase(EntityId<SourceOnlyExecutionIdTag> id,
        SourceOnlyExecutionPhase phase, string? failureCode = null)
    {
        StoredSourceOnlyExecution current = RequireExecution(id);
        // A stage label is not permission to clear a bound result/version, including
        // a failed evaluation or a receipt requiring manual reconciliation.
        return executions.UpdateState(id, current.Revision, phase, current.ResultJson,
            current.VersionId, current.VersionDigest, failureCode);
    }

    private void CancelQueued(EntityId<JobIdTag> id)
    {
        StoredJob stored = RequireJob(id);
        if (stored.Job.State == JobState.Queued)
        {
            _ = jobs.CancelQueued(id, stored.Revision, Now(stored.Job.UpdatedAt));
        }
    }

    private void SettleInterrupted(StoredSourceOnlyExecution execution, EntityId<JobIdTag> id,
        string errorCode = "job.app_restarted")
    {
        if (workspaceLease.SafeFileHandle.IsClosed || databaseLease.SafeFileHandle.IsClosed ||
            (id != execution.Request.RootJobId && id != execution.Request.IngestJobId))
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.job_ownership_unavailable");
        }
        StoredJobAggregate aggregate = jobs.GetJobAggregate(id)
            ?? throw new InvalidOperationException("The accepted job is missing.");
        string kind = id == execution.Request.RootJobId ? SourceOnlyExecutionStore.RootJobKind : SourceOnlyExecutionStore.IngestJobKind;
        if (aggregate.Job.ProjectId != execution.Request.ProjectId || aggregate.Job.Kind != kind)
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.job_ownership_unavailable");
        }
        if (aggregate.Job.State != JobState.Running) { return; }
        JobAttempt previous = aggregate.Attempts[^1];
        DateTimeOffset at = Now(aggregate.Job.UpdatedAt);
        foreach (JobProgress progress in aggregate.Progress) { if (progress.RecordedAt > at) { at = progress.RecordedAt; } }
        foreach (JobHeartbeat heartbeat in aggregate.Heartbeats) { if (heartbeat.RecordedAt > at) { at = heartbeat.RecordedAt; } }
        try
        {
            // Same optimistic revision/attempt identity and evidence-time floor as
            // JobRecovery. Never sweep unrelated application jobs under this lease.
            _ = jobs.CompleteAttempt(id, aggregate.Revision, new JobAttempt(previous.Id, id,
                previous.SequenceNumber, JobState.Interrupted, previous.QueuedAt, previous.StartedAt,
                at, errorCode), at);
        }
        catch (StoreConcurrencyException)
        {
            if (RequireJob(id).Job.State == JobState.Running)
            {
                throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.job_ownership_unavailable");
            }
        }
    }

    private bool IngestHasPromotedOriginal(StoredSourceOnlyExecution execution) =>
        artifacts.GetArtifact(EntityId<ArtifactIdTag>.From(execution.Request.SourceCandidateId.Value)) is not null;

    private LocatedSource ReadAcceptedSource(StoredSourceOnlyExecution execution)
    {
        StoredSourceOnlyExecution original = executions.GetByDraft(execution.Request.DraftId)
            ?? throw new SourceOnlySourceException("source_only.locator_unavailable");
        byte[] bytes = executions.GetProtectedSourceLocator(original.Request.ExecutionId)
            ?? throw new SourceOnlySourceException("source_only.locator_unavailable");
        LocatedSource source = locator.ReadAccepted(bytes, execution.Request.DraftId, execution.Request.SourceCandidateId);
        if (source.Document.AcceptedBy != original.Request.ActorSid ||
            source.Document.AcceptedAt != original.Request.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
        {
            throw new SourceOnlySourceException("source_only.locator_binding_invalid");
        }
        return source;
    }

    private Project ReadProject(EntityId<ProjectIdTag> id)
    {
        StoredProject stored = projects.GetProject(id) ?? throw new InvalidOperationException("The project is missing.");
        using JsonDocument document = JsonDocument.Parse(stored.CanonicalJson);
        JsonElement root = document.RootElement;
        Asset[] assets = root.GetProperty("assets").EnumerateArray().Select(item =>
        {
            JsonElement rights = item.GetProperty("rights");
            var status = Text(rights, "status") switch
            {
                "approved" => RightsStatus.Approved,
                "unconfirmed" => RightsStatus.Unconfirmed,
                "rejected" => RightsStatus.Rejected,
                _ => throw new FormatException(),
            };
            return new Asset(EntityId<AssetIdTag>.Parse(Text(item, "id")), Text(item, "fileName"), Text(item, "mediaType"),
                new ContentDigest(Text(item, "contentDigest")), checked((long)item.GetProperty("byteSize").GetDecimal()),
                new RightsRecord(status, rights.TryGetProperty("evidenceReference", out var reference) ? reference.GetString() : null,
                    Text(rights, "allowedUse"), rights.GetProperty("externalPublicationAllowed").GetBoolean()));
        }).ToArray();
        Performer[] performers = root.GetProperty("performers").EnumerateArray().Select(item => new Performer(
            EntityId<PerformerIdTag>.Parse(Text(item, "id")), Text(item, "displayName"), Text(item, "role"),
            item.TryGetProperty("titleConfirmedAt", out var confirmed) ? ParseUtc(confirmed.GetString()!) : null)).ToArray();
        var project = new Project(id, Text(root, "contentName"), Text(root, "purpose"), Text(root, "audience"),
            stored.PublicationScope, stored.Deadline, assets, performers);
        if (Text(root, "id") != id.ToString() || ProjectJson.WriteProject(project) != stored.CanonicalJson)
        {
            throw new InvalidOperationException("The project snapshot is inconsistent.");
        }
        return project;
    }

    private void EnsureNoActiveExecution(StoredSourceOnlyExecution execution)
    {
        StoredSourceOnlyExecution latest = Read(execution.Request.ProjectId)!;
        EnsureManuallyResolved(latest);
        if (!IsTerminal(latest.Phase) || taskOwner.IsProjectActive(execution.Request.ProjectId) ||
            RequireJob(latest.Request.RootJobId).Job.State is JobState.Running or JobState.Queued ||
            RequireJob(latest.Request.IngestJobId).Job.State == JobState.Running)
        {
            throw new InvalidOperationException("An execution for this project is still active.");
        }
    }

    internal static void EnsureManuallyResolved(StoredSourceOnlyExecution execution)
    {
        SourceOnlyResult? result = execution.ResultJson is null ? null
            : SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
        result?.ValidateBinding(execution);
        if (execution.Phase == SourceOnlyExecutionPhase.ReconciliationRequired ||
            result is { ReconciliationRequired: true } or { StagingCleanupRequired: true })
        {
            throw new PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired, "source_only.manual_resolution_required", result);
        }
    }
    private StoredSourceOnlyExecution RequireExecution(EntityId<SourceOnlyExecutionIdTag> id) =>
        executions.Get(id) ?? throw new InvalidOperationException("The source-only execution does not exist.");
    private StoredJob RequireJob(EntityId<JobIdTag> id) => jobs.GetJob(id)
        ?? throw new InvalidOperationException("The accepted job does not exist.");
    private DateTimeOffset Now(DateTimeOffset minimum) => clock.GetUtcNow().ToUniversalTime() is var now && now >= minimum ? now : minimum;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void EnsureNotRecovering()
    {
        if (recovering) { throw new InvalidOperationException("Wait for native startup recovery to finish."); }
    }
    private static bool IsTerminal(SourceOnlyExecutionPhase phase) => phase >= SourceOnlyExecutionPhase.Completed;
    internal static EntityId<T> StableId<T>(string seed, string role)
    {
        byte[] hash = SHA256.HashData(Utf8.GetBytes("source-only.native-id.v1\0" + seed + "\0" + role));
        hash[7] = (byte)((hash[7] & 15) | 0x50);
        hash[8] = (byte)((hash[8] & 63) | 0x80);
        return EntityId<T>.From(new Guid(hash.AsSpan(0, 16)));
    }

    internal sealed class PipelineFailure(SourceOnlyExecutionPhase phase, string code, SourceOnlyResult? result = null)
        : InvalidOperationException("The native source-only stage did not complete.")
    {
        internal SourceOnlyExecutionPhase Phase { get; } = phase;
        internal string Code { get; } = code;
        internal SourceOnlyResult? Result { get; } = result;
    }
}

/// <summary>Serialize native registrations, not heavy jobs. Cache only successful exact-evidence registrations.</summary>
internal sealed class SourceOnlyRuntimeRegistrationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ContentDigest> registered = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Registration?> current = new();
    internal bool IsInsideCallback => current.Value is { Active: true };

    internal async Task RegisterAsync(string key, ContentDigest digest, Func<CancellationToken, Task> callback, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (current.Value is { Active: true } nested)
        {
            if (nested.Key == key && nested.Digest == digest) { return; }
            throw new InvalidOperationException("A native registration cannot recursively register a different checkpoint.");
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (registered.GetValueOrDefault(key) == digest) { return; }
            var registration = new Registration(key, digest);
            current.Value = registration;
            try
            {
                await callback(token).ConfigureAwait(false);
                registered[key] = digest;
            }
            finally { registration.Active = false; current.Value = null; }
        }
        finally { gate.Release(); }
    }

    private sealed class Registration(string key, ContentDigest digest)
    {
        internal string Key { get; } = key;
        internal ContentDigest Digest { get; } = digest;
        internal bool Active { get; set; } = true;
    }
}

/// <summary>Internal scheduler seam: owns every Task/CTS, serializes project runs and observes all errors.</summary>
internal sealed class SourceOnlyTaskOwner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<EntityId<SourceOnlyExecutionIdTag>, OwnedRun> runs = [];
    private bool stopped;
    private Task? disposalTask;
    internal int ActiveCount { get { lock (gate) { return runs.Values.Count(item => !item.Task.IsCompleted); } } }
    internal bool IsProjectActive(EntityId<ProjectIdTag> id)
    {
        lock (gate) { return runs.Values.Any(item => item.ProjectId == id && !item.Task.IsCompleted); }
    }
    internal bool IsExecutionActive(EntityId<SourceOnlyExecutionIdTag> id)
    {
        lock (gate) { return runs.TryGetValue(id, out var run) && !run.Task.IsCompleted; }
    }

    internal Task<SourceOnlyPipelineRun> Start(EntityId<SourceOnlyExecutionIdTag> id,
        EntityId<ProjectIdTag> projectId, Func<CancellationToken, Task<SourceOnlyPipelineRun>> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            foreach (var completedId in runs.Where(pair => pair.Key != id && pair.Value.Task.IsCompleted)
                .Select(pair => pair.Key).ToArray())
            {
                runs[completedId].Cancellation.Dispose();
                runs.Remove(completedId);
            }
            if (runs.TryGetValue(id, out var existing)) { return existing.Task; }
            if (runs.Values.Any(item => item.ProjectId == projectId && !item.Task.IsCompleted))
            {
                throw new InvalidOperationException("Only one native execution per project can be active.");
            }
            var cancellation = new CancellationTokenSource();
            Task<SourceOnlyPipelineRun> task = Task.Run(async () =>
            {
                try { return await operation(cancellation.Token).ConfigureAwait(false); }
                catch { return new SourceOnlyPipelineRun(id, null, "source_only.scheduler_failed"); }
            }, CancellationToken.None);
            runs.Add(id, new OwnedRun(projectId, cancellation, task));
            return task;
        }
    }

    internal async Task<SourceOnlyPipelineRun?> CancelAsync(EntityId<SourceOnlyExecutionIdTag> id)
    {
        OwnedRun? run;
        lock (gate) { runs.TryGetValue(id, out run); }
        if (run is null) { return null; }
        // CancelAsync's callback exceptions are observed too; the operation still settles.
        try { await run.Cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
        return await run.Task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposalTask is not null) { return new ValueTask(disposalTask); }
            stopped = true;
            disposalTask = DisposeCoreAsync(runs.Values.ToArray());
            return new ValueTask(disposalTask);
        }
    }

    private static async Task DisposeCoreAsync(OwnedRun[] owned)
    {
        foreach (OwnedRun run in owned)
        {
            try { await run.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
        await Task.WhenAll(owned.Select(item => item.Task)).ConfigureAwait(false);
        foreach (OwnedRun run in owned) { run.Cancellation.Dispose(); }
    }

    private sealed record OwnedRun(EntityId<ProjectIdTag> ProjectId,
        CancellationTokenSource Cancellation, Task<SourceOnlyPipelineRun> Task);
}