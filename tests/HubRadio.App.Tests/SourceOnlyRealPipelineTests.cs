using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Models;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Security;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Tests;

/// <summary>
/// Opt-in local, real-process integration tests, not native GUI acceptance (UX041/043).
/// No WebView, network acquisition, user media, default application directory or production DB.
/// A passing test establishes these particular synthetic-media executions, not ASR accuracy,
/// human/editorial approval, external publication permission or release approval.
/// Run roots are deliberately retained, including on failure; cleanup must be explicit later.
/// Run ONE LivePipeline method in a dedicated testhost, with an external process-tree watchdog.
/// HUBRADIO_LIVE_PIPELINE_SINGLE_TEST must equal that method's TestContext.TestName.
/// DoNotParallelize alone does not isolate the process-global environment from other classes.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SourceOnlyRealPipelineTests
{
    // Keep 120s per real media/ASR process and the bounded recovery/replay calls below.
    // This is NOT the budget for preparation or for an entire pipeline execution.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(120);
    // One 150s preparation window: dependency verification, model installation and synthetic input.
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(150);
    // Starts only after prep.completed; one 300s window for the WHOLE selected method,
    // including reopen/retry and BOTH executions in regeneration. No per-run renewal.
    private static readonly TimeSpan ScenarioExecutionTimeout = TimeSpan.FromSeconds(300);
    // Absolute 450s outer body budget, including setup and both phases; never renewed by logs.
    // The existing bounded shutdown/resource-release waits remain separate (15s each).
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(450);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly CleanupPolicy HostCleanup = new();
    private static int liveScenarioClaimed;
    private const string ModelId = "asr.faster-whisper-small.cpu-int8";
    private const string ModelManifest = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f";
    private const string FontSha256 = "dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LivePipeline")]
    public Task RealSourceOnlyPipeline_DeliversTheEntireGeneratedSet_AndReopensWithoutRerunning() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            StoredSourceOnlyExecution accepted = await fixture.AcceptAsync(token);
            StoredSourceOnlyExecution completed = await fixture.AwaitExecutionAsync(accepted, token);
            SourceOnlyResult original = await fixture.ValidateCompletedAsync(completed, token);
            fixture.Report("initial.validated");

            ArtifactFact[] files = fixture.Inventory(completed.Request.ProjectId);
            int rootRevision = fixture.Services.Jobs.GetJobAggregate(completed.Request.RootJobId)!.Revision;
            int ingestRevision = fixture.Services.Jobs.GetJobAggregate(completed.Request.IngestJobId)!.Revision;

            // Dispose the owner/leases, then use fresh stores, registrations and a fresh pipeline.
            await fixture.ClosePipelineAsync();
            fixture.OpenPipeline();
            SourceOnlyRecoveryResult recovery = await fixture.Track(fixture.Pipeline.RecoverAsync(token)).WaitAsync(OperationTimeout, token);
            Check(!recovery.Canceled && recovery.FailureCode is null, "live_e2e.recovery_failed");
            Check(recovery.Executions.Count == 1 && recovery.Executions[0].StateDigest == completed.StateDigest,
                "live_e2e.recovery_changed_execution");
            StoredSourceOnlyExecution reopened = Required(fixture.Pipeline.Read(completed.Request.ProjectId), "live_e2e.reopen_missing");
            SourceOnlyResult restored = await fixture.ValidateCompletedAsync(reopened, token);
            Check(restored.CanonicalJson == original.CanonicalJson, "live_e2e.reopen_changed_result");

            // These public operations validate checkpoints/receipts; they must not append new work.
            await fixture.Pipeline.RestoreRuntimeAsync(reopened.Request.ProjectId, token);
            SourceOnlyPipelineRun replay = await fixture.Track(fixture.Pipeline.Start(reopened.Request.ExecutionId)).WaitAsync(OperationTimeout, token);
            Check(replay.InfrastructureFailureCode is null && replay.Execution?.StateDigest == completed.StateDigest,
                "live_e2e.terminal_start_changed_execution");
            SourceOnlyResult repeatedDelivery = await fixture.Delivery.DeliverAsync(reopened, restored, token);
            Check(repeatedDelivery.CanonicalJson == original.CanonicalJson, "live_e2e.receipt_replay_changed_result");
            Check(files.SequenceEqual(fixture.Inventory(reopened.Request.ProjectId)), "live_e2e.reopen_rewrote_artifacts");
            Check(fixture.Services.Jobs.GetJobAggregate(reopened.Request.RootJobId)!.Revision == rootRevision &&
                fixture.Services.Jobs.GetJobAggregate(reopened.Request.IngestJobId)!.Revision == ingestRevision,
                "live_e2e.reopen_reran_jobs");
            fixture.Capture.AssertStageOrder();
            fixture.Report("reopen.validated");
        });

    [TestMethod]
    [TestCategory("LivePipeline")]
    public Task RealSourceOnlyPipeline_RegeneratesANewChildAndDelivery_WithoutReplacingTheOriginal() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            StoredSourceOnlyExecution first = await fixture.AwaitExecutionAsync(await fixture.AcceptAsync(token), token);
            SourceOnlyResult original = await fixture.ValidateCompletedAsync(first, token);
            SourceOnlyPipelineEvidence before = await fixture.Pipeline.ReadEvidenceAsync(first.Request.ProjectId, token);
            ArtifactFact[] oldFiles = fixture.Inventory(first.Request.ProjectId);
            int ingestRevision = fixture.Services.Jobs.GetJobAggregate(first.Request.IngestJobId)!.Revision;
            string parentJson = fixture.Services.Projects.GetCompositionVersion(original.VersionId)!.CanonicalJson;
            fixture.Report("regenerate.original_validated");

            await fixture.ClosePipelineAsync();
            fixture.OpenPipeline();
            SourceOnlyRecoveryResult recovery = await fixture.Track(fixture.Pipeline.RecoverAsync(token)).WaitAsync(OperationTimeout, token);
            Check(!recovery.Canceled && recovery.FailureCode is null, "live_e2e.regenerate_recovery_failed");
            fixture.Capture.AssertStageOrder();
            fixture.Capture.BeginNextExecution();
            StoredProjectSettings current = Required(fixture.Services.Settings.GetProjectSettings(first.Request.ProjectId).Settings,
                "live_e2e.current_settings_missing");
            fixture.DemandMutation();
            token.ThrowIfCancellationRequested();
            StoredSourceOnlyExecution accepted = fixture.Pipeline.Regenerate(first.Request.ProjectId, original.VersionId, original.VersionDigest,
                current.CurrentSnapshot, current.Revision, fixture.ActorSid, DateTimeOffset.UtcNow);
            Check(accepted.Request.ExecutionId != first.Request.ExecutionId && accepted.Request.RootJobId != first.Request.RootJobId &&
                accepted.Request.IngestJobId == first.Request.IngestJobId && accepted.Request.SourceCandidateId == first.Request.SourceCandidateId &&
                accepted.Request.ParentVersionId == original.VersionId && accepted.Request.ParentDigest == original.VersionDigest &&
                accepted.SettingsRevision == current.Revision && accepted.Request.SettingsSnapshot.CanonicalJson == current.CurrentSnapshot.CanonicalJson,
                "live_e2e.regeneration_acceptance_invalid");
            StoredSourceOnlyExecution second = await fixture.AwaitExecutionAsync(accepted, token);
            SourceOnlyResult regenerated = await fixture.ValidateCompletedAsync(second, token);
            Check(regenerated.VersionId != original.VersionId, "live_e2e.regeneration_reused_version_id");
            // Identical content MAY have the same digest. New identity/lineage, not a changed hash, proves regeneration.
            CompositionVersion child = CompositionSnapshotJson.ReadVersion(fixture.Services.Projects.GetCompositionVersion(regenerated.VersionId)!.CanonicalJson);
            var visited = new HashSet<EntityId<CompositionVersionIdTag>>();
            while (child.Id != original.VersionId)
            {
                Check(visited.Count < SourceOnlyExecutionStore.MaximumVersionLineageLength && visited.Add(child.Id) &&
                    child.ProjectId == first.Request.ProjectId && child.ParentVersionId is not null, "live_e2e.regeneration_lineage_invalid");
                child = CompositionSnapshotJson.ReadVersion(Required(fixture.Services.Projects.GetCompositionVersion(child.ParentVersionId),
                    "live_e2e.regeneration_parent_missing").CanonicalJson);
            }
            Check(child.ContentDigest == original.VersionDigest && fixture.Services.Projects.GetCompositionVersion(original.VersionId)!.CanonicalJson == parentJson,
                "live_e2e.regeneration_changed_parent");
            SourceOnlyPipelineEvidence after = await fixture.Pipeline.ReadEvidenceAsync(first.Request.ProjectId, token);
            Check(before.AnalysisCheckpoint == after.AnalysisCheckpoint && before.Audio == after.Audio && before.Timeline == after.Timeline &&
                before.RawAsr == after.RawAsr && before.MachineTranscript == after.MachineTranscript && before.RightsAttestation == after.RightsAttestation,
                "live_e2e.regeneration_replaced_asr");
            Check(fixture.Services.Jobs.GetJobAggregate(first.Request.IngestJobId)!.Revision == ingestRevision,
                "live_e2e.regeneration_reran_ingest");
            Check(!regenerated.Artifacts.Select(item => item.ArtifactId).Intersect(original.Artifacts.Select(item => item.ArtifactId)).Any() &&
                !regenerated.Artifacts.Select(item => item.Delivery!.Artifact.ArtifactId).Intersect(original.Artifacts.Select(item => item.Delivery!.Artifact.ArtifactId)).Any(),
                "live_e2e.regeneration_reused_output_ids");
            ArtifactFact[] allFiles = fixture.Inventory(first.Request.ProjectId);
            Check(oldFiles.All(allFiles.Contains), "live_e2e.regeneration_rewrote_old_artifact");
            Check(fixture.Services.Executions.Get(first.Request.ExecutionId)!.StateDigest == first.StateDigest &&
                fixture.Services.Executions.ListForProject(first.Request.ProjectId).Count == 2, "live_e2e.regeneration_changed_history");
            SourceOnlyResultLoadResult oldResult = await fixture.Services.Results.LoadAsync(first, token);
            Check(oldResult.Validation.IsValid && oldResult.Result.CanonicalJson == original.CanonicalJson,
                "live_e2e.regeneration_invalidated_old_result");
            fixture.Report("regenerate.validated");
        });

    [TestMethod]
    [TestCategory("LivePipeline")]
    public Task RealSourceOnlyPipeline_RetriesAnActualOfflineModelFailure_AfterReopenAndRepair() =>
        RunScenarioAsync(async (fixture, token) =>
        {
            await fixture.ClosePipelineAsync();
            fixture.TakeModelOffline(); // Only this test's installed copy; never alter the pinned source bundle.
            fixture.OpenPipeline();
            StoredSourceOnlyExecution failed = await fixture.AwaitExecutionAsync(await fixture.AcceptAsync(token), token);
            Check(failed.Phase == SourceOnlyExecutionPhase.Failed && failed.FailureCode == "source_only.runtime_verification_failed" &&
                failed.ResultJson is null && failed.VersionId is null, "live_e2e.model_failure_not_retryable");
            fixture.Capture.AssertNoStages();
            StoredJobAggregate oldRoot = fixture.Services.Jobs.GetJobAggregate(failed.Request.RootJobId)!;
            StoredJobAggregate oldIngest = fixture.Services.Jobs.GetJobAggregate(failed.Request.IngestJobId)!;
            Check(oldRoot.Job.State == JobState.Cancelled && oldIngest.Job.State == JobState.Cancelled &&
                oldRoot.Attempts.Count == 0 && oldIngest.Attempts.Count == 0 &&
                fixture.Services.Artifacts.ListProjectArtifacts(failed.Request.ProjectId).Count == 0,
                "live_e2e.dependency_failure_claimed_work");
            fixture.Report("retry.actual_failure_preserved");

            await fixture.ClosePipelineAsync();
            await fixture.RestoreModelAsync(token);
            fixture.OpenPipeline();
            SourceOnlyRecoveryResult recovery = await fixture.Track(fixture.Pipeline.RecoverAsync(token)).WaitAsync(OperationTimeout, token);
            Check(!recovery.Canceled && recovery.FailureCode is null && recovery.Executions.Count == 1 &&
                recovery.Executions[0].StateDigest == failed.StateDigest, "live_e2e.retry_recovery_changed_failure");
            fixture.Capture.AssertNoStages();
            fixture.DemandMutation();
            token.ThrowIfCancellationRequested();
            DateTimeOffset retriedAt = DateTimeOffset.UtcNow;
            StoredSourceOnlyExecution accepted = fixture.Pipeline.Retry(failed.Request.ExecutionId, failed.Revision, fixture.ActorSid, retriedAt);
            Check(accepted.RetryOfExecutionId == failed.Request.ExecutionId && accepted.RetryOfRevision == failed.Revision &&
                accepted.RetryOfStateDigest == failed.StateDigest && accepted.Request.ExecutionId != failed.Request.ExecutionId &&
                accepted.Request.RootJobId != failed.Request.RootJobId && accepted.Request.IngestJobId != failed.Request.IngestJobId &&
                accepted.Request.DraftId == failed.Request.DraftId && accepted.Request.SourceCandidateId == failed.Request.SourceCandidateId &&
                accepted.Request.SettingsSnapshot.CanonicalJson == failed.Request.SettingsSnapshot.CanonicalJson,
                "live_e2e.retry_acceptance_invalid");
            StoredSourceOnlyExecution completed = await fixture.AwaitExecutionAsync(accepted, token);
            _ = await fixture.ValidateCompletedAsync(completed, token);
            ArtifactFact[] files = fixture.Inventory(completed.Request.ProjectId);
            StoredSourceOnlyExecution replay = fixture.Pipeline.Retry(failed.Request.ExecutionId, failed.Revision, fixture.ActorSid, retriedAt);
            Check(replay.StateDigest == completed.StateDigest && files.SequenceEqual(fixture.Inventory(completed.Request.ProjectId)),
                "live_e2e.retry_replay_created_work");
            Check(fixture.Services.Executions.Get(failed.Request.ExecutionId)!.StateDigest == failed.StateDigest &&
                fixture.Services.Jobs.GetJobAggregate(failed.Request.RootJobId)!.Revision == oldRoot.Revision &&
                fixture.Services.Jobs.GetJobAggregate(failed.Request.IngestJobId)!.Revision == oldIngest.Revision &&
                fixture.Services.Executions.ListForProject(completed.Request.ProjectId).Count == 2,
                "live_e2e.retry_rewrote_failed_history");
            fixture.Report("retry.validated");
        });

    private async Task RunScenarioAsync(Func<LiveFixture, CancellationToken, Task> scenario)
    {
        // This guard precedes path resolution, directory/DB creation and any process launch.
        if (Environment.GetEnvironmentVariable("HUBRADIO_RUN_LIVE_PIPELINE_TESTS") != "1")
        {
            Assert.Inconclusive("live_e2e.opt_in_required.HUBRADIO_RUN_LIVE_PIPELINE_TESTS");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("live_e2e.windows_acl_required");
            return;
        }

        // This is a caller attestation, not discovery of the runner's filter. The external
        // runner must select exactly this method, no native app, and kill the host/process
        // tree on cleanupIncomplete. Never reuse this host, even after a successful run.
        Check(!string.IsNullOrWhiteSpace(TestContext.TestName) &&
            Environment.GetEnvironmentVariable("HUBRADIO_LIVE_PIPELINE_SINGLE_TEST") == TestContext.TestName,
            "live_e2e.dedicated_single_test_host_required");
        Check(Interlocked.CompareExchange(ref liveScenarioClaimed, 1, 0) == 0,
            "live_e2e.testhost_already_used");

        LiveFixture? fixture = null;
        ProcessEnvironmentScope? environment = null;
        Task? body = null;
        string? failure = null;
        string? phaseFailure = null;
        // Not a using: cancellation and its registrations must survive an abandoned body.
        // All deadlines share ONE retained cancellation task, including its callbacks.
        // A second CancelAsync call need not wait for callbacks already in flight.
        var deadline = new CancellationTokenSource();
        var cancellation = new Lazy<Task>(() => deadline.CancelAsync(), LazyThreadSafetyMode.ExecutionAndPublication);
        var scenarioElapsed = Stopwatch.StartNew();
        try
        {
            fixture = new LiveFixture(TestContext);
            Dictionary<string, string?> values = IsolatedEnvironment(fixture.Root);
            foreach (string? value in values.Values)
            {
                if (value is not null && Path.IsPathFullyQualified(value)) { Directory.CreateDirectory(value); }
            }
            environment = new ProcessEnvironmentScope(values,
                name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
                (name, value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process));
            environment.Apply(); // Before PrepareAsync and therefore before the first subprocess.
            // Retain the unwrapped body, including any synchronous prefix of a phase.
            body = Task.Run(ExecuteAsync);
            await body.WaitAsync(RemainingTimeout(scenarioElapsed, ScenarioTimeout), deadline.Token);
            _ = RemainingTimeout(scenarioElapsed, ScenarioTimeout);

            async Task ExecuteAsync()
            {
                await RunPhaseAsync("prep", PreparationTimeout, fixture.PrepareAsync);
                await RunPhaseAsync("scenario.execution", ScenarioExecutionTimeout, token => scenario(fixture, token));
            }

            async Task RunPhaseAsync(string stage, TimeSpan timeout, Func<CancellationToken, Task> operation)
            {
                // Each phase owns a linked CTS. The budget begins here, not at a waiting
                // heartbeat, and cancellation still originates from the original deadline.
                using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var phaseElapsed = Stopwatch.StartNew();
                Task? phaseBody = null;
                try
                {
                    phaseDeadline.Token.ThrowIfCancellationRequested();
                    fixture.Report(stage + ".started");
                    phaseBody = Task.Run(() => operation(phaseDeadline.Token), phaseDeadline.Token);
                    await phaseBody.WaitAsync(RemainingTimeout(phaseElapsed, timeout), deadline.Token);
                    phaseDeadline.Token.ThrowIfCancellationRequested();
                    _ = RemainingTimeout(phaseElapsed, timeout);
                    // Only an actually successful preparation can emit prep.completed;
                    // ExecuteAsync cannot start the execution clock until this returns.
                    fixture.Report(stage + ".completed", phaseElapsed.Elapsed);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    string code = exception is TimeoutException && phaseElapsed.Elapsed >= timeout
                        ? "live_e2e." + stage + ".deadline_exceeded" : FailureCode(exception);
                    Interlocked.CompareExchange(ref phaseFailure, code, null);
                    Task canceling = cancellation.Value; // Signals the outer wait AND this phase before reporting/joining.
                    try { fixture.Report(stage + ".failed." + SafeCode(code), phaseElapsed.Elapsed); }
                    finally
                    {
                        try { await canceling; }
                        finally
                        {
                            if (phaseBody is not null)
                            {
                                try { await phaseBody; }
                                catch (Exception) { /* Observe the original operation, not just its timed wait. */ }
                            }
                        }
                    }
                    // The join is inside the tracked body. Outer cancellation enters the
                    // bounded shutdown below; incomplete joins retain the CTS/environment.
                    throw;
                }
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            failure = Volatile.Read(ref phaseFailure) ?? (exception is TimeoutException
                ? "live_e2e.scenario.deadline_exceeded" : FailureCode(exception));
        }
        finally
        {
            // A bounded WAIT is not a stopped task. Keep the actual shutdown task, including
            // cancellation callbacks, body, pipeline disposal and any abandoned inner waits.
            // Final disposal follows the body, so the body cannot reopen a pipeline afterwards.
            Task stopping = Task.Run(async () =>
            {
                Exception? cancellationFailure = null;
                try { await cancellation.Value; }
                catch (Exception exception) { cancellationFailure = exception; }
                if (body is not null)
                {
                    try { await body; }
                    catch (Exception) { /* Scenario failure was captured above; observe the original task. */ }
                }
                if (fixture is not null)
                {
                    await fixture.ClosePipelineUntilStoppedAsync();
                    await fixture.AwaitOwnedOperationsStoppedAsync();
                }
                if (cancellationFailure is not null)
                {
                    throw new InvalidOperationException("live_e2e.cancellation_shutdown_failed", cancellationFailure);
                }
            });
            var ownership = new ScenarioOwnership(fixture, environment, deadline, cancellation, body);
            bool cleaned = await HostCleanup.CompleteAsync(ownership, stopping,
                () => fixture?.ReleaseResourcesAsync() ?? Task.CompletedTask,
                () => { environment?.Dispose(); deadline.Dispose(); }, ShutdownTimeout);
            if (!cleaned)
            {
                // Overrides a scenario error: the external runner must treat this as an
                // infrastructure failure. No FailFast, replacement process or indefinite wait.
                failure = "live_e2e.infrastructure_failure.cleanupIncomplete";
                // Do not contend for the fixture's log gate or copy a still-mutating result
                // file while the abandoned body may own it. The failure is in the test result.
                TestContext.WriteLine(failure);
            }
            else if (fixture is not null)
            {
                fixture.Report(failure is null ? "scenario.completed" : "scenario.failed." + SafeCode(failure));
                TestContext.AddResultFile(fixture.LogPath);
                // No ClearAllPools (other tests may own connections), recursive deletion, or DB repair.
            }
        }
        if (failure is not null) { Assert.Fail(failure); }
    }

    private static TimeSpan RemainingTimeout(Stopwatch elapsed, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero) { throw new TimeoutException(); }
        return remaining; // Count scheduling/synchronous work too; never renew a phase's allowance.
    }

    private static Dictionary<string, string?> IsolatedEnvironment(string root)
    {
        // Environment.GetFolderPath uses Windows known folders, not these variables. All
        // application/DB/model/music paths remain explicit fixture paths; never start App.
        string home = Path.Combine(root, "Home");
        string cache = Path.Combine(root, "Cache");
        string hub = Path.Combine(cache, "huggingface", "hub");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Path.Combine(root, "Temp"),
            ["TMP"] = Path.Combine(root, "Temp"),
            ["TMPDIR"] = Path.Combine(root, "Temp"),
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"),
            ["HF_HOME"] = Path.Combine(cache, "huggingface"),
            ["HF_HUB_CACHE"] = hub,
            ["HUGGINGFACE_HUB_CACHE"] = hub,
            ["HF_ASSETS_CACHE"] = Path.Combine(cache, "huggingface", "assets"),
            ["HF_XET_CACHE"] = Path.Combine(cache, "huggingface", "xet"),
            ["TRANSFORMERS_CACHE"] = hub,
            ["XDG_CACHE_HOME"] = cache,
            ["TORCH_HOME"] = Path.Combine(cache, "torch"),
            ["HF_HUB_OFFLINE"] = "1",
            ["HF_DATASETS_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["FFREPORT"] = null,
        };
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> values;
        private readonly Dictionary<string, string?> originals;
        private readonly Action<string, string?> write;
        private bool disposed;

        internal ProcessEnvironmentScope(Dictionary<string, string?> values, Func<string, string?> read, Action<string, string?> write)
        {
            this.values = new(values, StringComparer.OrdinalIgnoreCase);
            this.write = write;
            originals = values.Keys.ToDictionary(name => name, read, StringComparer.OrdinalIgnoreCase);
        }

        internal void Apply()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach ((string name, string? value) in values) { write(name, value); }
        }

        public void Dispose()
        {
            if (disposed) { return; }
            List<Exception> failures = [];
            foreach ((string name, string? value) in originals)
            {
                try { write(name, value); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count != 0) { throw new AggregateException("live_e2e.environment_restore_failed", failures); }
            disposed = true; // Restore null/empty/value exactly; never wipe directories.
        }
    }

    private sealed record ScenarioOwnership(LiveFixture? Fixture, ProcessEnvironmentScope? Environment,
        CancellationTokenSource Deadline, Lazy<Task> Cancellation, Task? Body);

    private sealed record RetainedCleanup(object Owner, Task Stop, Task? ResourceRelease);

    private sealed class CleanupPolicy
    {
        // TEST ONLY: one strong-rooted slot, never evicted, retried or disposed by a timer or
        // finalizer. HostCleanup is static; the one-scenario guard bounds retention to one
        // fixture. Only the external watchdog's host exit releases an incomplete lease.
        private readonly RetainedCleanup?[] retained = new RetainedCleanup?[1];
        internal RetainedCleanup? Retained => retained[0];

        internal async Task<bool> CompleteAsync(object owner, Task stopped, Func<Task> release, Action restore, TimeSpan timeout)
        {
            Check(retained[0] is null, "live_e2e.retained_host_cannot_be_reused");
            Task? resourceRelease = null;
            try
            {
                await stopped.WaitAsync(timeout); // Must SUCCEED, not merely be faulted/canceled/completed.
                resourceRelease = Task.Run(release);
                await resourceRelease.WaitAsync(timeout);
                restore();
                return true;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                retained[0] = new RetainedCleanup(owner, stopped, resourceRelease);
                return false;
            }
        }
    }

    [TestMethod]
    public void EnvironmentScope_RestoresValuesAbsenceAndEmpty_WithoutTouchingTheHost()
    {
        var ambient = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = "original-temp",
            ["TMP"] = "",
            ["FFREPORT"] = "original-report",
            ["HF_HUB_OFFLINE"] = "0",
        };
        var original = new Dictionary<string, string?>(ambient, StringComparer.OrdinalIgnoreCase);
        string root = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "pure-fixture");
        Dictionary<string, string?> values = IsolatedEnvironment(root);
        using var scope = new ProcessEnvironmentScope(values, name => ambient.GetValueOrDefault(name),
            (name, value) => { if (value is null) { ambient.Remove(name); } else { ambient[name] = value; } });
        scope.Apply();
        Assert.IsFalse(ambient.ContainsKey("FFREPORT"));
        Assert.AreEqual("1", ambient["HF_HUB_OFFLINE"]);
        foreach (string name in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HF_HOME", "HUGGINGFACE_HUB_CACHE", "HF_HUB_CACHE", "XDG_CACHE_HOME" })
        {
            Assert.IsTrue(ambient[name]!.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        scope.Dispose();
        scope.Dispose();
        Assert.AreEqual(original.Count, ambient.Count);
        foreach ((string name, string? value) in original) { Assert.AreEqual(value, ambient[name]); }
    }

    [TestMethod]
    public void EnvironmentScope_PartialApplyCanRestoreAllOriginals()
    {
        var ambient = new Dictionary<string, string?> { ["TEMP"] = "original", ["TMP"] = "original-tmp" };
        bool failOnce = true;
        using var scope = new ProcessEnvironmentScope(new() { ["TEMP"] = "isolated", ["TMP"] = "isolated" },
            name => ambient.GetValueOrDefault(name), (name, value) =>
            {
                if (name == "TMP" && failOnce) { failOnce = false; throw new InvalidOperationException(); }
                ambient[name] = value;
            });
        Assert.ThrowsExactly<InvalidOperationException>(scope.Apply);
        scope.Dispose();
        Assert.AreEqual("original", ambient["TEMP"]);
        Assert.AreEqual("original-tmp", ambient["TMP"]);
    }

    [TestMethod]
    public async Task CleanupPolicy_IncompleteFaultedOrCanceledStopNeverReleasesOrRestores()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (Task stopped in new[] { pending.Task, Task.FromException(new InvalidOperationException()), Task.FromCanceled(new CancellationToken(true)) })
        {
            var policy = new CleanupPolicy();
            var owner = new object();
            bool released = false;
            bool restored = false;
            Assert.IsFalse(await policy.CompleteAsync(owner, stopped,
                () => { released = true; return Task.CompletedTask; }, () => restored = true, TimeSpan.Zero));
            Assert.AreSame(owner, policy.Retained!.Owner);
            Assert.AreSame(stopped, policy.Retained.Stop);
            Assert.IsNull(policy.Retained.ResourceRelease);
            if (ReferenceEquals(stopped, pending.Task)) { pending.SetResult(); }
            // Late completion does not schedule deferred disposal or restore the environment.
            Assert.IsFalse(released);
            Assert.IsFalse(restored);
            await Assert.ThrowsExactlyAsync<AssertFailedException>(async () =>
            {
                _ = await policy.CompleteAsync(new object(), Task.CompletedTask,
                    () => { released = true; return Task.CompletedTask; }, () => restored = true, TimeSpan.Zero);
            });
            Assert.AreSame(owner, policy.Retained.Owner);
            Assert.IsFalse(released);
            Assert.IsFalse(restored);
        }
    }

    [TestMethod]
    public async Task CleanupPolicy_OnlyRestoresAfterSuccessfulResourceRelease()
    {
        var policy = new CleanupPolicy();
        var order = new List<string>();
        Assert.IsTrue(await policy.CompleteAsync(new object(), Task.CompletedTask,
            () => { order.Add("release"); return Task.CompletedTask; }, () => order.Add("restore"), ShutdownTimeout));
        CollectionAssert.AreEqual(new[] { "release", "restore" }, order);
        Assert.IsNull(policy.Retained);

        bool restored = false;
        Assert.IsFalse(await policy.CompleteAsync(new object(), Task.CompletedTask,
            () => Task.FromException(new InvalidOperationException()), () => restored = true, ShutdownTimeout));
        Assert.IsFalse(restored);
        Assert.IsNotNull(policy.Retained!.ResourceRelease);

        var pendingPolicy = new CleanupPolicy();
        var pendingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsFalse(await pendingPolicy.CompleteAsync(new object(), Task.CompletedTask,
            () => pendingRelease.Task, () => restored = true, TimeSpan.Zero));
        Task retainedRelease = pendingPolicy.Retained!.ResourceRelease!;
        Assert.IsFalse(retainedRelease.IsCompleted);
        pendingRelease.SetResult();
        await retainedRelease;
        Assert.IsFalse(restored); // No late restoration after a cleanup deadline has failed.
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        AssertFailedException assertion => SafeCode(assertion.Message),
        TimeoutException => "live_e2e.deadline_exceeded",
        OperationCanceledException => "live_e2e.deadline_canceled",
        SourceOnlyPipeline.PipelineFailure pipeline => SafeCode(pipeline.Code),
        SourceOnlySourceException source => SafeCode(source.Code),
        LocalAnalysisWorkerException worker => SafeCode(worker.Code),
        SourceOnlyResultException result => "live_e2e.result." + result.Code,
        ModelManagerException model => "live_e2e.model." + model.Reason,
        WorkspaceAclException acl => "live_e2e.acl." + acl.Reason,
        SourceOnlyApplicationDependencyException dependency => "live_e2e.dependency." + dependency.Dependency,
        _ => "live_e2e.unexpected_" + SafeCode(exception.GetType().Name),
    };

    private static string SafeCode(string? code) => code is { Length: > 0 and <= 256 } &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? code : "live_e2e.unclassified_failure";

    private static void Check([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) { throw new AssertFailedException(code); }
    }

    private static T Required<T>(T? value, string code) where T : class => value ?? throw new AssertFailedException(code);

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HubRadio.slnx"))) { current = current.Parent; }
        return Required(current, "live_e2e.repository_missing").FullName;
    }

    private static JsonElement FinalizeInput(ProjectCreationDraftView draft) => JsonSerializer.SerializeToElement(new
    {
        sourceCandidateId = draft.SourceCandidateId,
        contentName = draft.ContentName,
        purpose = draft.Purpose,
        audience = draft.Audience,
        publicationScope = draft.PublicationScope,
        outputProfileIds = draft.OutputProfileIds,
        language = draft.Language,
        performerNames = draft.PerformerNames,
        deadlineSelection = new { hasDeadline = draft.HasDeadline, value = draft.Deadline },
        brandTemplateId = draft.BrandTemplateId,
    });

    private static void CheckPathFreeProjection(JsonElement value, string actorSid)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Check(!new[] { "storageReference", "stagingPath", "sourcePath", "actorSid", "rawMetadata" }
                    .Contains(property.Name, StringComparer.OrdinalIgnoreCase), "live_e2e.projection_private_property");
                CheckPathFreeProjection(property.Value, actorSid);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) { CheckPathFreeProjection(item, actorSid); }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            Check(!text.Contains(actorSid, StringComparison.Ordinal) && !Path.IsPathFullyQualified(text) &&
                !text.Contains(":\\", StringComparison.Ordinal) && !text.Contains(":/", StringComparison.Ordinal) &&
                !text.StartsWith(@"\\", StringComparison.Ordinal), "live_e2e.projection_private_value");
        }
    }

    private sealed record ArtifactFact(string Id, string Digest, long Size, ArtifactKind Kind, long LastWriteTicks);

    private sealed class LiveFixture
    {
        private readonly TestContext context;
        private readonly object logGate = new();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private TimeSpan lastMilestoneElapsed;
        private readonly string repository;
        private readonly string database;
        private readonly string profiles;
        private readonly string settingsProfiles;
        private readonly SourceOnlyApplicationResourceLease workerLease = new();
        private readonly List<Task> ownedOperations = [];
        private Task? pipelineCloseTask;
        private SourceOnlyPipeline? pipeline;
        private SourceOnlyRuntimeOptions? options;
        private ProjectCreationService? creation;
        private string? preservedModel;

        internal LiveFixture(TestContext context)
        {
            this.context = context;
            repository = RepositoryRoot();
            string parent = SourceOnlySourceLocator.RequirePath(Path.Combine(repository, "artifacts", "validation"), directory: true);
            Root = Path.Combine(parent, "live-e2e-" + Guid.NewGuid().ToString("N"));
            ActorSid = new WindowsActorProvider().GetCurrentSid();
            WorkspaceAclResult acl = WorkspaceAcl.CreateNew(Root, ActorSid);
            Check(acl.OwnerMatchesCurrentActor && acl.Protected && acl.InheritanceConfigured && acl.ExplicitRuleCount == 3,
                "live_e2e.acl_invalid");
            LogPath = Path.Combine(Root, "watchdog.log");
            context.WriteLine("live_e2e.root=" + Path.GetFileName(Root));
            Report("acl.created");
            database = Path.Combine(Root, "hubradio.db");
            profiles = Path.Combine(repository, "src", "HubRadio.App", "Profiles");
            settingsProfiles = Path.Combine(repository, "src", "HubRadio.App", "DefaultSettings", "Profiles");
            SourcePath = Path.Combine(Root, "synthetic-four-seconds.mp4");
        }

        internal string Root { get; }
        internal string LogPath { get; }
        internal string ActorSid { get; }
        internal string SourcePath { get; }
        internal ContentDigest SourceDigest { get; private set; } = null!;
        internal long SourceSize { get; private set; }
        internal SourceOnlyApplicationServices Services { get; private set; } = null!;
        internal SourceOnlyDeliveryService Delivery { get; private set; } = null!;
        internal StageCapture Capture { get; private set; } = null!;
        internal SourceOnlyPipeline Pipeline => Required(pipeline, "live_e2e.pipeline_missing");

        internal void Report(string code) => Report(code, null);

        internal void Report(string code, TimeSpan? phaseDuration)
        {
            lock (logGate)
            {
                TimeSpan observed = elapsed.Elapsed;
                bool observationOnly = code.StartsWith("waiting.", StringComparison.Ordinal);
                Check(observed >= lastMilestoneElapsed && (phaseDuration is null ||
                    (!observationOnly && phaseDuration.Value >= TimeSpan.Zero && phaseDuration.Value <= observed)),
                    "live_e2e.invalid_measured_duration");
                string line = FormattableString.Invariant($"live_e2e elapsed_ms={(long)observed.TotalMilliseconds} stage={SafeCode(code)}");
                TimeSpan sinceMilestone = observed - lastMilestoneElapsed;
                // Real monotonic time between non-waiting events, NOT ASR-only inference
                // time. Repeated observations cannot shorten this duration or renew a deadline.
                line += observationOnly
                    ? FormattableString.Invariant($" observation_only=true since_progress_ms={sinceMilestone.TotalMilliseconds:F3}")
                    : FormattableString.Invariant($" step_elapsed_ms={sinceMilestone.TotalMilliseconds:F3}");
                if (phaseDuration is not null)
                {
                    line += FormattableString.Invariant($" phase_elapsed_ms={phaseDuration.Value.TotalMilliseconds:F3}");
                }
                // Stable codes only: no process stderr, transcript text, metadata, SID or absolute paths.
                context.WriteLine(line);
                File.AppendAllText(LogPath, line + "\n", Utf8);
                if (!observationOnly) { lastMilestoneElapsed = observed; }
            }
        }

        internal async Task PrepareAsync(CancellationToken token)
        {
            // RunPhaseAsync supplies the 150s preparation token; OperationTimeout below
            // remains the independent 120s limit for each actual process invocation.
            token.ThrowIfCancellationRequested();
            // Verify the controlled process environment immediately before real dependency work.
            foreach ((string name, string? value) in IsolatedEnvironment(Root))
            {
                Check(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) == value,
                    "live_e2e.environment_changed." + name);
            }
            var verifier = new BinaryVerifier();
            string ffmpeg = Path.Combine(repository, "artifacts", "portable", "osl009-payload", "Tools", "ffmpeg.exe");
            string ffprobe = Path.Combine(repository, "artifacts", "portable", "osl009-payload", "Tools", "ffprobe.exe");
            string worker = Path.Combine(repository, "artifacts", "python", "WorkerRuntime-Live-20260909c");
            string fontPath = Path.Combine(repository, "artifacts", "inputs", "fonts", "verified-2.004-7388bda58dd8", "NotoSansJP-Regular.otf");
            var python = new BinaryVerificationRequest("live-e2e.python", Path.Combine(worker, "python.exe"),
                new ContentDigest(SourceOnlyApplicationResourceLease.PythonSha256), SourceOnlyApplicationResourceLease.PythonByteSize);
            var fontRequest = new BinaryVerificationRequest("live-e2e.font", fontPath, new ContentDigest(FontSha256), 4533028);
            foreach (BinaryVerificationRequest request in new[]
            {
                new BinaryVerificationRequest("live-e2e.ffmpeg", ffmpeg, new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), DecodeValidator.CanonicalFfmpegByteSize),
                new BinaryVerificationRequest("live-e2e.ffprobe", ffprobe, new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize),
                python, fontRequest,
            })
            {
                _ = SourceOnlySourceLocator.RequirePath(request.FilePath, directory: false);
                _ = await verifier.VerifyAsync(request, token);
            }
            // Bind the entire already-built worker to this App's embedded packaging contract.
            // -I/-B and offline model use are provided by the REAL LocalAnalysisWorker adapter.
            await workerLease.VerifyWorkerAsync(worker, token);
            Report("dependencies.verified");

            string catalogPath = Path.Combine(repository, "packaging", "models", "catalog.json");
            string bundleRoot = Path.Combine(Root, "ModelCatalog");
            byte[] catalogBytes = await File.ReadAllBytesAsync(catalogPath, token);
            Check(ContentDigest.Compute(catalogBytes).Hex == ModelManager.PinnedCatalogSha256, "live_e2e.catalog_pin_mismatch");
            using (JsonDocument catalog = JsonDocument.Parse(catalogBytes))
            {
                JsonElement policy = catalog.RootElement.GetProperty("policy");
                Check(policy.GetProperty("localProcessingOnly").GetBoolean() && !policy.GetProperty("networkAcquisitionEnabled").GetBoolean() &&
                    !policy.GetProperty("silentUpgradeEnabled").GetBoolean(), "live_e2e.catalog_not_offline");
                JsonElement model = catalog.RootElement.GetProperty("models").EnumerateArray().Single(item => item.GetProperty("modelId").GetString() == ModelId);
                JsonElement bundle = model.GetProperty("bundle");
                const string relative = "Bundles/faster-whisper-small-536b0662742c02347bc0e980a01041f333bce120";
                Check(bundle.GetProperty("relativePath").GetString() == relative && bundle.GetProperty("manifestSha256").GetString() == ModelManifest,
                    "live_e2e.bundle_contract_changed");
                JsonElement[] files = bundle.GetProperty("files").EnumerateArray().ToArray();
                Check(files.Select(file => file.GetProperty("name").GetString()).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" }), "live_e2e.bundle_file_set_changed");
                string destination = Path.Combine(bundleRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(destination);
                // Never recursively copy the source directory (which contains unrelated manifests).
                foreach (JsonElement file in files)
                {
                    string name = file.GetProperty("name").GetString()!;
                    string source = Path.Combine(repository, "artifacts", "inputs", "models", "faster-whisper-small", name);
                    string copy = Path.Combine(destination, name);
                    var digest = new ContentDigest(file.GetProperty("sha256").GetString()!);
                    long size = file.GetProperty("byteSize").GetInt64();
                    _ = SourceOnlySourceLocator.RequirePath(source, directory: false);
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous))
                    await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                    {
                        Check(input.Length == size, "live_e2e.bundle_source_size_mismatch");
                        await input.CopyToAsync(output, token);
                        await output.FlushAsync(token);
                    }
                    _ = await verifier.VerifyAsync(new BinaryVerificationRequest("live-e2e.model-file", copy, digest, size), token);
                    Report("model.copied." + name);
                }
            }
            ModelManager manager = ModelManager.Load(catalogPath, bundleRoot, Root, verifier);
            string installed = await manager.InstallFromBundleAsync(ModelId, token);
            Check(installed == await manager.RequireOfflinePathAsync(ModelId, token), "live_e2e.model_not_ready");
            Check(IsUnderRoot(installed), "live_e2e.model_escaped_root");
            Report("model.installed");

            _ = DatabaseMigrator.Migrate(database, Path.Combine(Root, "backups"));
            var settings = new DefaultSettingsStore(database);
            _ = new BundledDefaultSettingsBootstrap(settings).Initialize(settingsProfiles, DateTimeOffset.UtcNow);
            _ = new AuthorizationService(new RoleAssignmentStore(database)).BootstrapInitialRoles(ActorSid);
            string app = typeof(SourceOnlyPipeline).Assembly.Location;
            ContentDigest appDigest;
            await using (var stream = File.OpenRead(app)) { appDigest = ContentDigest.FromBytes(await SHA256.HashDataAsync(stream, token)); }
            options = new SourceOnlyRuntimeOptions(database, Root, ffmpeg, ffprobe, python.FilePath, installed, profiles,
                Path.Combine(settingsProfiles, "1.0.0.json"), new FfmpegFontBinding(new RenderFontEvidence("Noto Sans JP", fontRequest.ExpectedDigest), fontRequest),
                python, manager, typeof(SourceOnlyPipeline).Assembly.GetName().Version!.ToString(), appDigest,
                OperationTimeout, musicDirectory: Path.Combine(Root, "Music"));

            // Deterministic lavfi inputs only. AAC priming is preserved and measured by the real ingest/QC.
            // No source tags, timestamps from a user's camera, fonts from the system, or shell interpolation.
            Report("source.generating");
            ProcessRunResult generated = await new ProcessRunner().RunAsync(new ProcessRunRequest(ffmpeg,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-n", "-filter_threads", "1",
                "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4",
                "-f", "lavfi", "-i", "anullsrc=sample_rate=48000:channel_layout=mono",
                "-map", "0:v:0", "-map", "1:a:0", "-t", "4",
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-c:v", "h264_mf", "-b:v", "2000000", "-profile:v", "100", "-threads:v", "1",
                "-pix_fmt", "yuv420p", "-r", "30", "-fps_mode", "cfr", "-g", "30", "-bf", "0",
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
                "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "1", "-threads:a", "1",
                "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-movflags", "+faststart", SourcePath,
            ], Root, OperationTimeout, maxCapturedBytes: 64 * 1024), token);
            Check(generated.ExitCode == 0 && !generated.Truncated, "live_e2e.synthetic_source_failed");
            await using (var source = File.OpenRead(SourcePath))
            {
                SourceSize = source.Length;
                SourceDigest = ContentDigest.FromBytes(await SHA256.HashDataAsync(source, token));
            }
            Check(SourceSize > 0, "live_e2e.synthetic_source_empty");
            Report("source.generated");
            OpenPipeline();
        }

        internal void OpenPipeline()
        {
            Check(pipeline is null, "live_e2e.owner_already_open");
            pipelineCloseTask = null;
            _ = WorkspaceAcl.VerifyExisting(Root, ActorSid);
            SourceOnlyRuntimeOptions runtime = Required(options, "live_e2e.options_missing");
            Services = new SourceOnlyApplicationServices(database, Root, new AssetCandidateRegistry(), new DefaultSettingsStore(database));
            var qc = new OutputQc(Services.Artifacts, new ProcessRunner(), new BinaryVerifier(), runtime.FfprobePath, runtime.FfmpegPath);
            var evaluation = new SourceOnlyOutputEvaluation(Services.Artifacts, qc, Services.Evaluations, Services.Workflows, Services.Projects);
            Delivery = new SourceOnlyDeliveryService(database, Root, Services.Artifacts, Services.Results, Services.Workflows);
            Capture = new StageCapture(Services.Registrations.Callbacks, Report);
            pipeline = new SourceOnlyPipeline(runtime, Services.Candidates, evaluation, Capture.Callbacks,
                deliverAsync: Delivery.DeliverAsync, jobStore: Services.Jobs, jobCoordinator: Services.Coordinator);
            creation = new ProjectCreationService(Services.Drafts, new AuthorizationService(new RoleAssignmentStore(database)),
                Services.Candidates, ProjectCreationCatalog.Load(profiles), Services.Settings, () => ActorSid,
                cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return Task.FromResult<EntityId<AssetIdTag>?>(Services.Candidates.RegisterSelection(SourcePath));
                }, sourceOnlyCoordinator: pipeline);
            Report("pipeline.opened");
        }

        internal void DemandMutation()
        {
            Check(new WindowsActorProvider().GetCurrentSid() == ActorSid, "live_e2e.actor_changed");
            var authorization = new AuthorizationService(new RoleAssignmentStore(database));
            authorization.Demand(ActorSid, AuthorizationOperation.ViewProject);
            authorization.DemandAs(ActorSid, AuthorizationRole.Editor, AuthorizationOperation.CreateCompositionVersion);
        }

        internal void TakeModelOffline()
        {
            Check(pipeline is null && preservedModel is null, "live_e2e.model_fault_requires_closed_owner");
            string installed = Required(options, "live_e2e.options_missing").ModelDirectory;
            Check(IsUnderRoot(installed), "live_e2e.model_fault_outside_root");
            preservedModel = Path.Combine(Root, "preserved-ready-model");
            Directory.Move(installed, preservedModel);
            Directory.CreateDirectory(installed);
            Report("fault.isolated_model_files_absent");
        }

        internal async Task RestoreModelAsync(CancellationToken token)
        {
            Check(pipeline is null && preservedModel is not null, "live_e2e.model_repair_requires_closed_owner");
            SourceOnlyRuntimeOptions runtime = Required(options, "live_e2e.options_missing");
            Check(IsUnderRoot(runtime.ModelDirectory) && IsUnderRoot(preservedModel) &&
                !Directory.EnumerateFileSystemEntries(runtime.ModelDirectory).Any(), "live_e2e.model_fault_changed");
            // Preserve even the failed-attempt directory. No deletion/repair of an artifact or database row.
            Directory.Move(runtime.ModelDirectory, Path.Combine(Root, "preserved-missing-model-attempt"));
            Directory.Move(preservedModel, runtime.ModelDirectory);
            preservedModel = null;
            Check(await runtime.ModelManager.RequireOfflinePathAsync(ModelId, token) == runtime.ModelDirectory, "live_e2e.repaired_model_invalid");
            Report("model.restored_and_verified");
        }

        internal async Task<StoredSourceOnlyExecution> AcceptAsync(CancellationToken token)
        {
            ProjectCreationService service = Required(creation, "live_e2e.creation_missing");
            SourceOnlyRecoveryResult empty = await Track(Pipeline.RecoverAsync(token)).WaitAsync(OperationTimeout, token);
            Check(!empty.Canceled && empty.FailureCode is null && empty.Executions.Count == 0, "live_e2e.new_root_not_empty");
            ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync(token);
            Check(!draft.IsFinalized && draft.SourceOnly is not null && draft.SourceStatus == "available" && draft.PublicationScope == "internalOnly",
                "live_e2e.draft_contract_invalid");
            var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
            token.ThrowIfCancellationRequested();
            ProjectCreationDraftView finalized = service.FinalizeDraft(draftId, draft.Revision, FinalizeInput(draft));
            StoredSourceOnlyExecution accepted = Required(Services.Executions.GetByDraft(draftId), "live_e2e.acceptance_missing");
            Check(finalized.IsFinalized && finalized.ProjectId == accepted.Request.ProjectId.ToString() &&
                accepted.Request.SourceCandidateId.ToString() == draft.SourceCandidateId && accepted.Request.DraftId == draftId &&
                accepted.Request.RootJobId != accepted.Request.IngestJobId &&
                accepted.Request.SettingsSnapshot.ContentDigest.Hex == draft.SourceOnly.SettingsSnapshotDigest,
                "live_e2e.acceptance_binding_invalid");
            Check(Services.Executions.GetByJob(accepted.Request.RootJobId)?.Request.ExecutionId == accepted.Request.ExecutionId,
                "live_e2e.root_binding_invalid");
            Report("project.accepted");
            return accepted;
        }

        internal async Task<StoredSourceOnlyExecution> AwaitExecutionAsync(StoredSourceOnlyExecution accepted, CancellationToken token)
        {
            SourceOnlyPipeline owner = Pipeline;
            Task<SourceOnlyPipelineRun> running = Track(owner.Start(accepted.Request.ExecutionId)); // Join the task already started by acceptance.
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task watchdog = WatchAsync();
            try
            {
                // The entire pipeline shares the phase's fixed 300s scenario allowance;
                // each real media/ASR process still has its own unchanged 120s runtime limit.
                SourceOnlyPipelineRun result = await running.WaitAsync(token);
                Check(result.InfrastructureFailureCode is null, SafeCode(result.InfrastructureFailureCode));
                StoredSourceOnlyExecution execution = Required(result.Execution, "live_e2e.execution_missing");
                Check(execution.Request.ExecutionId == accepted.Request.ExecutionId, "live_e2e.execution_identity_changed");
                Report("execution." + execution.Phase);
                if (execution.FailureCode is not null) { Report(execution.FailureCode); }
                return execution;
            }
            catch (Exception)
            {
                Report("execution.cancel_requested");
                _ = await Track(owner.CancelAsync(accepted.Request.ExecutionId)).WaitAsync(ShutdownTimeout);
                throw;
            }
            finally
            {
                await watching.CancelAsync();
                try { await watchdog; }
                catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
            }

            async Task WatchAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                while (await timer.WaitForNextTickAsync(watching.Token))
                {
                    // Read-only durable state, never a completed milestone or deadline reset,
                    // including repeated waiting.Analyzing observations.
                    StoredSourceOnlyExecution current = Required(Services.Executions.Get(accepted.Request.ExecutionId), "live_e2e.watchdog_execution_missing");
                    Report("waiting." + current.Phase);
                }
            }
        }

        internal async Task<SourceOnlyResult> ValidateCompletedAsync(StoredSourceOnlyExecution execution, CancellationToken token)
        {
            Check(execution.Phase == SourceOnlyExecutionPhase.Completed && execution.FailureCode is null,
                execution.FailureCode is null ? "live_e2e.execution_not_completed" : SafeCode(execution.FailureCode));
            Capture.AssertStageOrder();
            IngestWorkflowRunResult ingest = Required(Capture.Ingest, "live_e2e.ingest_callback_missing");
            TranscriptImportResult transcript = Required(Capture.Transcript, "live_e2e.transcript_callback_missing");
            StandardCompositionPreparation composition = Required(Capture.Composition, "live_e2e.composition_callback_missing");
            SourceOnlyGeneratedRuntimeEvidence subtitles = Required(Capture.Generated, "live_e2e.generated_callback_missing");
            Project project = Required(Capture.Project, "live_e2e.project_callback_missing");
            Check(ingest.IsComplete && !ingest.ReconciliationRequired && ingest.Job.Snapshot.JobId == execution.Request.IngestJobId,
                "live_e2e.ingest_not_completed");
            Check(!Services.Candidates.IsAvailable(execution.Request.SourceCandidateId), "live_e2e.picker_token_not_consumed");
            Asset source = Required(ingest.Asset, "live_e2e.ingest_asset_missing");
            var original = Required(ingest.Original, "live_e2e.original_missing");
            PtsIndex pts = Required(ingest.SourcePtsIndex, "live_e2e.pts_missing");
            Check(source.Id == execution.Request.SourceCandidateId && source.ContentDigest == SourceDigest && source.ByteSize == SourceSize &&
                original.Artifact.Id.Value == source.Id.Value && original.Artifact.ContentDigest == SourceDigest &&
                project.Id == execution.Request.ProjectId && project.Assets.Single().Id == source.Id, "live_e2e.source_identity_mismatch");
            Check(project.PublicationScope == PublicationScope.InternalOnly && source.Rights.Status == RightsStatus.Approved &&
                !source.Rights.ExternalPublicationAllowed && !project.Assets.Any(asset => asset.Rights.ExternalPublicationAllowed),
                "live_e2e.external_publication_not_forbidden");
            Check(pts.Entries.Count == 120 && pts.PresentationStart == default(RationalTime) && pts.PresentationEnd == new RationalTime(4, 1),
                "live_e2e.source_timing_mismatch");
            Check(original.MediaInfo.PrimaryVideoStream.Width == 1280 && original.MediaInfo.PrimaryVideoStream.Height == 720 &&
                original.MediaInfo.Streams.Single(stream => stream.Kind == MediaStreamType.Audio).SampleRate == 48000 &&
                original.MediaInfo.Streams.Single(stream => stream.Kind == MediaStreamType.Audio).Channels == 1,
                "live_e2e.source_format_mismatch");

            StoredJobAggregate root = Required(Services.Jobs.GetJobAggregate(execution.Request.RootJobId), "live_e2e.root_missing");
            StoredJobAggregate ingestJob = Required(Services.Jobs.GetJobAggregate(execution.Request.IngestJobId), "live_e2e.ingest_job_missing");
            Check(root.Job.State == JobState.Succeeded && ingestJob.Job.State == JobState.Succeeded &&
                root.Job.ProjectId == project.Id && root.Job.Kind == SourceOnlyExecutionStore.RootJobKind &&
                root.Attempts.Count == 1 && ingestJob.Attempts.Count == 1, "live_e2e.jobs_not_succeeded");
            JobAttempt currentRootAttempt = root.Attempts.Single(item => item.SequenceNumber == root.Job.CurrentAttemptNumber);
            Check(root.Progress.Count == 6 &&
                root.Progress.All(item => item.JobId == root.Job.Id && item.JobAttemptId == currentRootAttempt.Id &&
                    item.SequenceNumber == currentRootAttempt.SequenceNumber && item.TotalUnits == 6 &&
                    item.CompletedUnits is >= 1 and <= 6) &&
                root.Progress.Select(item => item.CompletedUnits).Distinct().Count() == 6,
                "live_e2e.durable_stage_units_invalid");
            // ReadProgress breaks equal RecordedAt ties by random ID; CompletedUnits carries the stage ordinal.
            Check(root.Progress.OrderBy(item => item.CompletedUnits).Select(item => item.Phase).SequenceEqual(new[]
            {
                "source_only.analysis", "source_only.composition", "source_only.rendering", "source_only.evaluation",
                "source_only.delivering", "source_only.stages_completed",
            }), "live_e2e.durable_stages_missing");

            SourceOnlyPipelineEvidence evidence = await Pipeline.ReadEvidenceAsync(project.Id, token);
            Check(evidence.ExecutionId == execution.Request.ExecutionId && evidence.GeneratedRuntime is not null &&
                evidence.AnalysisEvidence is not null && evidence.RenderPreflight is not null && evidence.AutomaticDecisions is not null,
                "live_e2e.stage_evidence_missing");
            Check(transcript.Revision.ChangeSource == TranscriptChangeSource.Machine &&
                transcript.RawWorkerResult.ModelManifestDigest.Hex == ModelManifest &&
                transcript.RawWorkerResult.Configuration.Device == "cpu" && transcript.RawWorkerResult.Configuration.ComputeType == "int8" &&
                transcript.RawWorkerResult.InputAudioDigest == evidence.Audio.ContentDigest &&
                transcript.AudioTimeline.SourceOriginalArtifactId == original.Artifact.Id &&
                evidence.MachineTranscript.ContentDigest == ContentDigest.Compute(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(transcript.Revision))),
                "live_e2e.real_asr_binding_invalid");
            Check(composition.Child.Id == execution.VersionId && composition.Child.ContentDigest == execution.VersionDigest &&
                composition.Child.ContentDigest == RenderGraphCanonicalEvidence.ComputeCompositionDigest(composition.Child.Elements) &&
                composition.Plan.LongForm.Provenance.TranscriptRevisionId == transcript.Revision.Id &&
                composition.Plan.LongForm.Provenance.TranscriptRevisionDigest == evidence.MachineTranscript.ContentDigest,
                "live_e2e.production_version_mismatch");
            if (execution.Request.ParentVersionId is null)
            {
                var checkpointId = SourceOnlyPipeline.StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "baseline-checkpoint");
                await using ValidatedArtifactReadStream stream = await Services.Artifacts.OpenValidatedReadAsync(checkpointId, token);
                using JsonDocument checkpoint = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                var baselineId = EntityId<CompositionVersionIdTag>.Parse(checkpoint.RootElement.GetProperty("versionId").GetString()!);
                StoredCompositionVersion saved = Required(Services.Projects.GetCompositionVersion(baselineId), "live_e2e.baseline_missing");
                CompositionVersion baseline = CompositionSnapshotJson.ReadVersion(saved.CanonicalJson);
                Check(baseline.ParentVersionId is null && baseline.ProjectId == project.Id && baseline.Elements.Count == 1 &&
                    baseline.Elements[0].SourceRange == pts.PresentationRange &&
                    baseline.Elements[0].OutputRange == new TimeRange(default, new RationalTime(4, 1)), "live_e2e.baseline_trimmed_source");
            }

            SourceOnlyResultLoadResult loaded = await Services.Results.LoadAsync(execution, token);
            Check(loaded.Validation.IsValid, "live_e2e.persisted_result_invalid");
            SourceOnlyResult result = loaded.Result;
            result.ValidateBinding(execution);
            Check(!result.HasBlockingFailures && !result.ReconciliationRequired && !result.StagingCleanupRequired &&
                result.Artifacts.Count > 0 && result.Artifacts.All(item => item.Delivered), "live_e2e.full_set_not_delivered");
            Check(result.CanonicalJson == SourceOnlyResult.ParseCanonical(result.CanonicalJson, execution.ResultDigest).CanonicalJson,
                "live_e2e.result_roundtrip_invalid");
            SourceOnlyPipelineGenerationEvidence generation = Required(evidence.Generation, "live_e2e.generation_evidence_missing");
            Check(result.Artifacts.Select(item => item.ArtifactId).OrderBy(id => id.ToString(), StringComparer.Ordinal).SequenceEqual(
                generation.Outputs.Select(item => item.Artifact.ArtifactId).OrderBy(id => id.ToString(), StringComparer.Ordinal)),
                "live_e2e.generated_set_was_filtered");
            Check(result.GenerationWarnings.SequenceEqual(generation.Warnings), "live_e2e.generation_warnings_lost");
            Check(result.TranscriptReferences.Count == 1 && result.TranscriptReferences[0].RevisionId == transcript.Revision.Id &&
                result.TranscriptReferences[0].RevisionDigest == evidence.MachineTranscript.ContentDigest &&
                result.TranscriptReferences[0].SourceArtifactId == original.Artifact.Id, "live_e2e.result_transcript_mismatch");
            Check(result.Artifacts.Select(item => item.Delivery!.Receipt).Distinct().Count() == 1, "live_e2e.delivery_not_one_atomic_set");
            Check(result.Artifacts.Select(item => item.Evaluation?.EvaluationId).Distinct().Count() == result.Artifacts.Count,
                "live_e2e.independent_evaluations_missing");
            foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
            {
                SourceOnlyGeneratedArtifactEvidence produced = generation.Outputs.Single(item => item.Artifact.ArtifactId == artifact.ArtifactId);
                Check(produced.Artifact == artifact.Artifact && produced.Role == artifact.Role && produced.RenditionId == artifact.RenditionId &&
                    produced.ThumbnailPlanId == artifact.ThumbnailPlanId && produced.Version.Id == artifact.VersionId &&
                    produced.Version.ContentDigest == artifact.VersionDigest && produced.ProfileId == artifact.ProfileId &&
                    produced.ProfileDigest == artifact.ProfileDigest && produced.GraphDigest == artifact.GraphDigest && produced.ManifestDigest == artifact.ManifestDigest &&
                    artifact.Source.ArtifactId == original.Artifact.Id && artifact.Source.ContentDigest == SourceDigest,
                    "live_e2e.output_binding_mismatch");
                AutomatedEvaluationRun evaluation = Required(artifact.Evaluation, "live_e2e.evaluation_missing");
                Check(Services.Evaluations.Get(evaluation.EvaluationId)?.CanonicalJson == evaluation.CanonicalJson &&
                    evaluation.ArtifactId?.ToString() == artifact.ArtifactId.ToString() && evaluation.CompositionVersionId == artifact.VersionId,
                    "live_e2e.evaluation_not_persisted");
                Check(evaluation.Rules.Any(rule => rule.Scope == AutomatedEvaluationRuleScope.UserInterface) &&
                    evaluation.Rules.Where(rule => rule.Scope == AutomatedEvaluationRuleScope.UserInterface)
                        .All(rule => rule.Status == AutomatedEvaluationStatus.NotEvaluated) &&
                    evaluation.Limitations.All(rule => rule.Status == AutomatedEvaluationStatus.NotEvaluated),
                    "live_e2e.unmeasured_acceptance_claimed");
                Check(artifact.Evidence.Select(item => item.Kind).ToHashSet().SetEquals(new[]
                {
                    SourceOnlyResultEvidenceKind.Graph, SourceOnlyResultEvidenceKind.Manifest,
                    SourceOnlyResultEvidenceKind.RenderingInputs, SourceOnlyResultEvidenceKind.QualityControl,
                }), "live_e2e.output_proof_missing");
                StoredArtifact delivered = await Services.Artifacts.ValidateRegisteredAsync(artifact.Delivery!.Artifact.ArtifactId, token);
                Check(delivered.Kind == ArtifactKind.Delivery && delivered.Id != artifact.ArtifactId &&
                    delivered.ContentDigest == artifact.ArtifactDigest && delivered.ByteSize == artifact.ByteSize,
                    "live_e2e.delivered_bytes_mismatch");
                if (artifact.IsVideo)
                {
                    SourceOnlyResultMeasurements measured = Required(artifact.Measurements, "live_e2e.qc_measurements_missing");
                    Check(measured.DecodedSuccessfully && measured.VideoTimingContinuous && measured.VideoFrameCadenceExact &&
                        measured.VideoStart == default(RationalTime) && measured.VideoDuration == measured.ExpectedDuration &&
                        measured.VideoFrameCount > 0 && measured.AudioDigest is not null, "live_e2e.physical_qc_invalid");
                    Check(measured.OutputDigitalSilence && evaluation.Rules.Single(rule => rule.RuleId == AutomatedEvaluationRuleId.Loudness).Status == AutomatedEvaluationStatus.Warning,
                        "live_e2e.silence_was_not_honestly_measured");
                    Check(evaluation.Rules.Single(rule => rule.RuleId == AutomatedEvaluationRuleId.FullDecode).Status == AutomatedEvaluationStatus.Pass,
                        "live_e2e.full_decode_not_passed");
                }
                else { Check(artifact.Measurements is null, "live_e2e.nonvideo_borrowed_video_qc"); }
                if (artifact.IsImage)
                {
                    var frame = Required(artifact.FrameEvidence, "live_e2e.thumbnail_frame_missing");
                    Check(frame.SourceAssetId == source.Id && frame.SourceDigest == SourceDigest && frame.SourcePtsIndexDigest ==
                        ContentDigest.Compute(Utf8.GetBytes(pts.CanonicalJson)) && frame.SourceFrameSequence >= 0 && frame.SourceFrameSequence < pts.Entries.Count &&
                        pts.Entries[frame.SourceFrameSequence].PresentationTime == frame.SourcePresentationTime,
                        "live_e2e.thumbnail_frame_binding_invalid");
                }
            }
            SourceOnlyResultArtifact primary = result.Artifacts.Single(item => item.Role == SourceOnlyResultRole.LandscapeClean);
            bool hasDisplayCues = Required(primary.Measurements, "live_e2e.primary_qc_missing").Cues.Count > 0;
            foreach (SourceOnlyResultRole role in new[] { SourceOnlyResultRole.LandscapeBurnIn, SourceOnlyResultRole.SubRip, SourceOnlyResultRole.WebVtt })
            {
                Check(result.Artifacts.Count(item => item.Role == role) == (hasDisplayCues ? 1 : 0), "live_e2e.subtitle_output_set_invalid");
            }
            Check(hasDisplayCues || result.Omissions.Any(item => item.Role == SourceOnlyResultOmissionRole.LandscapeSubtitles &&
                item.Reason == SourceOnlyResultOmissionReason.NoDisplayCues), "live_e2e.subtitle_omission_unexplained");
            if (transcript.Revision.Utterances.Count == 0)
            {
                Check(subtitles.Versions.Values.All(item => item.Histories.Count == 0) && !hasDisplayCues &&
                    subtitles.LandscapeSubtitles.Warnings.Any(item => item.Reason == "no-real-asr-cues"), "live_e2e.empty_asr_invented_cues");
            }
            // Do not assume an empty transcript just because the generated input is silent.
            foreach (StandardShortArtifactInput shortForm in subtitles.ShortForms)
            {
                bool emitted = result.Artifacts.Any(item => item.Role == SourceOnlyResultRole.VerticalBurnIn && item.RenditionId == shortForm.ShortFormId);
                Check(emitted == (shortForm.Subtitles.Revisions.Count > 0), "live_e2e.short_output_set_invalid");
                Check(emitted || result.Omissions.Any(item => item.Role == SourceOnlyResultOmissionRole.VerticalSubtitles &&
                    item.RenditionId == shortForm.ShortFormId && item.Reason == SourceOnlyResultOmissionReason.NoDisplayCues),
                    "live_e2e.short_omission_unexplained");
            }
            Check(result.Artifacts.Count(item => item.IsImage) == composition.Plan.Thumbnails.Count * 2, "live_e2e.thumbnail_output_set_invalid");
            Check(result.PrimaryWorkflow?.State == QuickStartWorkflowState.ReadyWithWarnings && result.PrimaryWorkflow.ArtifactId == primary.ArtifactId,
                "live_e2e.workflow_state_invalid");
            QuickStartWorkflow workflow = Required(Services.Workflows.Get(new QuickStartWorkflowBinding(project.Id, result.VersionId, result.VersionDigest)),
                "live_e2e.workflow_missing");
            Check(workflow.State == QuickStartWorkflowState.ReadyWithWarnings, "live_e2e.workflow_invented_export_or_approval");
            CheckPathFreeProjection(JsonSerializer.SerializeToElement(result.ToWebDto()), ActorSid);

            // Rehash every registered artifact, including all receipts and intermediate evidence.
            foreach (StoredArtifact stored in Services.Artifacts.ListProjectArtifacts(project.Id))
            {
                StoredArtifact verified = await Services.Artifacts.ValidateRegisteredAsync(stored.Id, token);
                Check(verified.ProjectId == project.Id && verified.ContentDigest == stored.ContentDigest && verified.ByteSize == stored.ByteSize &&
                    IsUnderRoot(Services.Artifacts.GetValidatedRegisteredArtifactPath(stored.Id)), "live_e2e.artifact_invalid_or_escaped_root");
            }
            return result;
        }

        internal ArtifactFact[] Inventory(EntityId<ProjectIdTag> projectId) => Services.Artifacts.ListProjectArtifacts(projectId)
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .Select(item => new ArtifactFact(item.Id.ToString(), item.ContentDigest.Hex, item.ByteSize, item.Kind,
                File.GetLastWriteTimeUtc(Services.Artifacts.GetValidatedRegisteredArtifactPath(item.Id)).Ticks)).ToArray();

        private bool IsUnderRoot(string path) => Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        internal TTask Track<TTask>(TTask operation) where TTask : Task
        {
            ownedOperations.Add(operation);
            return operation;
        }

        internal async Task AwaitOwnedOperationsStoppedAsync()
        {
            // Called only after the body is terminal, so it can no longer add operations.
            Task[] operations = ownedOperations.ToArray();
            try { await Task.WhenAll(operations); }
            catch (Exception) when (operations.All(operation => operation.IsCompleted)) { }
        }

        internal Task ClosePipelineAsync() => ClosePipelineUntilStoppedAsync().WaitAsync(ShutdownTimeout);

        internal Task ClosePipelineUntilStoppedAsync() => pipelineCloseTask ??= CloseOwnerAsync();

        private async Task CloseOwnerAsync()
        {
            if (pipeline is null) { return; }
            // Preserve both the owner and its ORIGINAL close task across a timed-out wait.
            await pipeline.DisposeAsync().AsTask();
            pipeline = null;
            creation = null;
            Report("pipeline.closed");
        }

        internal Task ReleaseResourcesAsync() => workerLease.DisposeAsync().AsTask();
    }

    private sealed class StageCapture
    {
        private readonly List<string> stages = [];
        internal StageCapture(SourceOnlyRuntimeCallbacks registrations, Action<string> report)
        {
            // Invoke the actual host registrations. Observers retain producer objects only;
            // none substitutes ingest, ASR, a compiler, QC, evaluation, delivery, or job completion.
            Callbacks = new SourceOnlyRuntimeCallbacks
            {
                IngestReady = async (value, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (registrations.IngestReady is not null) { await registrations.IngestReady(value, token); }
                    Ingest = value; stages.Add("ingest"); report("callback.ingest");
                },
                TranscriptReady = async (project, value, token) =>
                {
                    await registrations.TranscriptReady!(project, value, token);
                    Project = project; Transcript = value; stages.Add("transcript"); report("callback.transcript");
                },
                AnalysisReady = async (project, value, evidence, token) =>
                {
                    await registrations.AnalysisReady!(project, value, evidence, token);
                    Analysis = evidence; stages.Add("analysis"); report("callback.analysis");
                },
                CompositionReady = async (value, import, token) =>
                {
                    await registrations.CompositionReady!(value, import, token);
                    Composition = value; stages.Add("composition"); report("callback.composition");
                },
                GeneratedReady = async (value, token) =>
                {
                    await registrations.GeneratedReady!(value, token);
                    Generated = value; stages.Add("generated"); report("callback.generated");
                },
            };
        }
        internal SourceOnlyRuntimeCallbacks Callbacks { get; }
        internal IngestWorkflowRunResult? Ingest { get; private set; }
        internal Project? Project { get; private set; }
        internal TranscriptImportResult? Transcript { get; private set; }
        internal SourceOnlyAnalysisEvidence? Analysis { get; private set; }
        internal StandardCompositionPreparation? Composition { get; private set; }
        internal SourceOnlyGeneratedRuntimeEvidence? Generated { get; private set; }
        internal void AssertStageOrder() => Check(stages.SequenceEqual(new[] { "ingest", "transcript", "analysis", "composition", "generated" }),
            "live_e2e.callback_order_invalid");
        internal void AssertNoStages() => Check(stages.Count == 0, "live_e2e.failed_dependency_dispatched_stages");
        internal void BeginNextExecution()
        {
            AssertStageOrder();
            stages.Clear();
            Ingest = null; Project = null; Transcript = null; Analysis = null; Composition = null; Generated = null;
        }
    }
}