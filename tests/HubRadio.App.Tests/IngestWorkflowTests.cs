using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Recovery;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class IngestWorkflowTests
{
    [TestMethod]
    public async Task RunAsync_ExecutesNineStagesReturnsWarningsAndPersistsProgressAndHeartbeat()
    {
        using WorkflowFixture fixture = new();

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.IsTrue(result.IsComplete);
        Assert.IsFalse(result.CanResumeWithThisResult);
        Assert.IsFalse(result.ReconciliationRequired);
        Assert.IsNull(result.FailureReason);
        Assert.AreEqual(
            IngestWorkflowStage.PreviewAssetsCommitted,
            result.LastCompletedStage);
        Assert.AreEqual(JobState.Succeeded, result.Job.Snapshot.State);
        Assert.AreEqual(9L, result.Job.Snapshot.CompletedUnits);
        Assert.AreEqual(9L, result.Job.Snapshot.TotalUnits);
        Assert.AreEqual(1m, result.Job.Snapshot.ProgressRatio);
        Assert.AreSame(fixture.Staged.CandidateId, result.Asset!.Id);
        Assert.AreEqual(fixture.Request.OriginalArtifactId, result.Original!.Artifact.Id);
        Assert.AreEqual(fixture.Request.ProxyArtifactId, result.Proxy!.ProxyArtifact.Id);
        Assert.AreEqual(
            fixture.Request.WaveformIndexArtifactId,
            result.PreviewAssets!.WaveformIndexArtifact.Id);
        Assert.AreSame(fixture.SourceIndex, result.SourcePtsIndex);
        Assert.IsNotNull(result.FrameMap);
        CollectionAssert.AreEqual(
            new[]
            {
                MediaInfoWarningCode.AudioTrackAbsent,
                MediaInfoWarningCode.LowResolution,
            },
            result.Warnings.Select(static warning => warning.Code).ToArray());
        CollectionAssert.AreEqual(
            WorkflowFixture.StageNames,
            fixture.Calls.ToArray());

        StoredJobAggregate aggregate = fixture.RequireAggregate();
        Assert.AreEqual(9, aggregate.Progress.Count);
        Assert.AreEqual(9, aggregate.Heartbeats.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 9).Select(static value => (long)value).ToArray(),
            aggregate.Progress.Select(static progress => progress.CompletedUnits).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "ingest.copy",
                "ingest.probe",
                "ingest.source_index",
                "ingest.warnings",
                "ingest.decode",
                "ingest.original",
                "ingest.proxy",
                "ingest.frame_map",
                "ingest.preview_assets",
            },
            aggregate.Progress.Select(static progress => progress.Phase).ToArray());
        Assert.IsTrue(aggregate.Progress.All(static progress =>
            progress.TotalUnits == 9 && progress.Message is null));
    }

    [TestMethod]
    [DataRow("copy", IngestWorkflowFailureReason.ImportFailed, IngestWorkflowStage.NotStarted)]
    [DataRow("probe", IngestWorkflowFailureReason.ProbeFailed, IngestWorkflowStage.CopiedToStaging)]
    [DataRow("index", IngestWorkflowFailureReason.SourceIndexFailed, IngestWorkflowStage.Probed)]
    [DataRow("normalize", IngestWorkflowFailureReason.WarningNormalizationFailed, IngestWorkflowStage.SourceIndexed)]
    [DataRow("decode", IngestWorkflowFailureReason.DecodeValidationFailed, IngestWorkflowStage.WarningsNormalized)]
    [DataRow("original", IngestWorkflowFailureReason.OriginalPromotionFailed, IngestWorkflowStage.DecodeValidated)]
    [DataRow("proxy", IngestWorkflowFailureReason.ProxyGenerationFailed, IngestWorkflowStage.OriginalCommitted)]
    [DataRow("map", IngestWorkflowFailureReason.FrameMapGenerationFailed, IngestWorkflowStage.ProxyCommitted)]
    [DataRow("preview", IngestWorkflowFailureReason.PreviewGenerationFailed, IngestWorkflowStage.FrameMapBuilt)]
    public async Task RunAsync_ClassifiesEveryStageFailureAndStopsTheSuffix(
        string failingStage,
        IngestWorkflowFailureReason expectedReason,
        IngestWorkflowStage expectedCompletedStage)
    {
        using WorkflowFixture fixture = new();
        fixture.FailAt = failingStage;

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.IsFalse(result.IsComplete);
        Assert.IsFalse(result.CanResumeWithThisResult);
        Assert.IsFalse(result.ReconciliationRequired);
        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual("job.execution_failed", result.Job.Snapshot.ErrorCode);
        Assert.AreEqual(expectedReason, result.FailureReason);
        Assert.AreEqual(expectedCompletedStage, result.LastCompletedStage);
        Assert.AreEqual(
            Array.IndexOf(WorkflowFixture.StageNames, failingStage) + 1,
            fixture.Calls.Count);
        StoredJobAggregate aggregate = fixture.RequireAggregate();
        Assert.AreEqual(
            failingStage == "copy" ? 0 : (int)expectedCompletedStage,
            aggregate.Progress.Count);
        Assert.AreEqual(aggregate.Progress.Count, aggregate.Heartbeats.Count);
        Assert.AreEqual(
            expectedCompletedStage >= IngestWorkflowStage.OriginalCommitted,
            result.Original is not null);
        Assert.AreEqual(
            expectedCompletedStage >= IngestWorkflowStage.ProxyCommitted,
            result.Proxy is not null);
        Assert.AreEqual(
            expectedCompletedStage >= IngestWorkflowStage.FrameMapBuilt,
            result.FrameMap is not null);
        Assert.IsNull(result.PreviewAssets);
    }

    [TestMethod]
    public async Task TimeoutRetry_UsesValidatedPriorCheckpointAndSkipsCompletedPrefix()
    {
        using WorkflowFixture fixture = new();
        fixture.TimeoutOnceAt = "index";

        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, first.Job.Snapshot.State);
        Assert.AreEqual("job.execution_timed_out", first.Job.Snapshot.ErrorCode);
        Assert.AreEqual(IngestWorkflowFailureReason.SourceIndexFailed, first.FailureReason);
        Assert.AreEqual(IngestWorkflowStage.Probed, first.LastCompletedStage);
        Assert.IsTrue(first.CanResumeWithThisResult);
        Assert.AreEqual(JobRetryOutcome.Queued,
            fixture.Coordinator.QueueRetry(fixture.Request.JobId).Outcome);
        IngestWorkflowRequest equivalentRequest = fixture.CreateEquivalentRequest();
        Assert.AreEqual(fixture.Request.RequestDigest, equivalentRequest.RequestDigest);

        IngestWorkflowRunResult second = await fixture.Workflow.RunAsync(
            equivalentRequest,
            first,
            CancellationToken.None);

        Assert.IsTrue(
            second.IsComplete,
            $"failure={second.FailureReason}; stage={second.LastCompletedStage}; " +
            $"reconcile={second.ReconciliationRequired}; error={second.Job.Snapshot.ErrorCode}; " +
            $"calls={string.Join(',', fixture.Calls)}");
        Assert.AreEqual(JobState.Succeeded, second.Job.Snapshot.State);
        Assert.AreEqual(2, second.Job.SequenceNumber);
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "copy"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "probe"));
        Assert.AreEqual(2, fixture.Calls.Count(static stage => stage == "index"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "normalize"));
        StoredJobAggregate aggregate = fixture.RequireAggregate();
        Assert.AreEqual(2, aggregate.Attempts.Count);
        Assert.AreEqual(JobState.Failed, aggregate.Attempts[0].State);
        Assert.AreEqual(JobState.Succeeded, aggregate.Attempts[1].State);
        Assert.AreEqual(10, aggregate.Progress.Count);
        JobProgress[] secondProgress = aggregate.Progress
            .Where(progress => progress.JobAttemptId == second.Job.AttemptId)
            .ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(2, 8).Select(static value => (long)value).ToArray(),
            secondProgress.Select(static progress => progress.CompletedUnits).ToArray());
    }

    [TestMethod]
    public async Task ResumeAsync_FreshRuntimeRestoresRequestAssetAndWarnings()
    {
        using WorkflowFixture fixture = new();
        fixture.TimeoutOnceAt = "decode";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(IngestWorkflowStage.WarningsNormalized, first.LastCompletedStage);
        Assert.AreEqual(JobState.Failed, first.Job.Snapshot.State);
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.IsTrue(resumed.IsComplete);
        Assert.AreEqual(2, resumed.Job.SequenceNumber);
        Assert.AreNotSame(first.Asset, resumed.Asset);
        Assert.AreEqual(first.Asset!.Id, resumed.Asset!.Id);
        Assert.AreEqual(first.Asset.ContentDigest, resumed.Asset.ContentDigest);
        CollectionAssert.AreEqual(
            first.Warnings.Select(static warning => warning.Code).ToArray(),
            resumed.Warnings.Select(static warning => warning.Code).ToArray());
        Assert.AreEqual(
            1,
            fixture.Calls.Count(static stage => stage == "copy"));
        Assert.AreEqual(
            1,
            fixture.Calls.Count(static stage => stage == "probe"));
        Assert.AreEqual(
            1,
            fixture.Calls.Count(static stage => stage == "index"));
        Assert.AreEqual(
            1,
            fixture.Calls.Count(static stage => stage == "normalize"));
        Assert.AreEqual(
            2,
            fixture.Calls.Count(static stage => stage == "decode"));
    }

    [TestMethod]
    public async Task JobRecovery_FreshRuntimeResumesDurableWarningCheckpoint()
    {
        using WorkflowFixture fixture = new();
        fixture.SeedAbandonedWarningCheckpoint();
        JobRecovery recovery = fixture.CreateFreshRecovery(hourOffset: 1);

        JobRecoveryResult recovered = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal)
            {
                IngestWorkflow.JobKind,
            },
            CancellationToken.None);

        Assert.IsTrue(recovered.DatabaseUsable);
        Assert.HasCount(1, recovered.RecoveredJobs);
        Assert.IsTrue(recovered.RecoveredJobs[0].AutomaticallyRequeued);
        Assert.AreEqual(JobState.Queued, recovered.RecoveredJobs[0].CurrentState);
        (JobCoordinator _, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 2);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.IsTrue(resumed.IsComplete);
        Assert.AreEqual(2, resumed.Job.SequenceNumber);
        CollectionAssert.AreEqual(
            new[] { "decode", "original", "proxy", "map", "preview" },
            fixture.Calls.ToArray());
        CollectionAssert.AreEqual(
            fixture.MediaInfo.Warnings
                .Select(static warning => warning.Code)
                .ToArray(),
            resumed.Warnings.Select(static warning => warning.Code).ToArray());
    }

    [TestMethod]
    public void DurableRequestRequiresExactClosedCanonicalPayload()
    {
        using WorkflowFixture fixture = new();

        IngestWorkflowRequest restored =
            IngestWorkflowCheckpointCodec.DeserializeRequest(
                fixture.Request.CanonicalJson);

        Assert.AreEqual(fixture.Request.JobId, restored.JobId);
        Assert.AreEqual(fixture.Request.ProjectId, restored.ProjectId);
        Assert.AreEqual(fixture.Request.RequestDigest, restored.RequestDigest);
        Assert.AreEqual(fixture.Request.CanonicalJson, restored.CanonicalJson);

        string unknownProperty = MutateCanonicalJson(
            fixture.Request.CanonicalJson,
            static root => root["unexpected"] = true);
        InvalidOperationException unknown =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                IngestWorkflowCheckpointCodec.DeserializeRequest(
                    unknownProperty));
        Assert.AreEqual(
            "The durable ingest checkpoint is invalid.",
            unknown.Message);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.DeserializeRequest(
                "{ " + fixture.Request.CanonicalJson[1..]));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.DeserializeRequest(
                "{\"version\":\"duplicate\"," +
                fixture.Request.CanonicalJson[1..]));
    }

    [TestMethod]
    public async Task DurableWarningCheckpointRoundTripPreservesAllTypedEvidence()
    {
        using WorkflowFixture fixture = new(allWarnings: true);
        fixture.TimeoutOnceAt = "decode";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        StoredIngestWorkflow stored = fixture.WorkflowStore.Get(
            fixture.Request.JobId)!;

        WorkflowResumeState restored = await fixture.Codec.DeserializeAsync(
            fixture.Request,
            stored,
            CancellationToken.None);

        Assert.AreEqual(
            IngestWorkflowStage.WarningsNormalized,
            restored.Stage);
        Assert.AreEqual(
            stored.CheckpointPayloadJson,
            fixture.Codec.Serialize(restored));
        Assert.AreNotSame(first.Asset, restored.Asset);
        Assert.AreEqual(first.Asset!.ContentDigest, restored.Asset!.ContentDigest);
        Assert.AreEqual(
            fixture.SourceIndex.CanonicalJson,
            restored.SourcePtsIndex!.CanonicalJson);
        MediaInfoWarning[] warnings = restored.MediaInfo!.Warnings.ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                MediaInfoWarningCode.DurationFieldsDiffer,
                MediaInfoWarningCode.LowResolution,
                MediaInfoWarningCode.MultipleAudioStreams,
                MediaInfoWarningCode.MultipleVideoStreams,
                MediaInfoWarningCode.UnrecognizedContainerSignature,
                MediaInfoWarningCode.VariableRateFieldsDiffer,
            },
            warnings.Select(static warning => warning.Code).ToArray());
        CollectionAssert.AreEqual(
            warnings.Select(static warning => warning.Code).ToArray(),
            IngestWorkflowCheckpointCodec.ReadWarningCodes(
                    stored.CheckpointPayloadJson,
                    stored.JobId,
                    stored.ProjectId,
                    stored.RequestDigest,
                    stored.CheckpointStage)
                .ToArray());
        string differentRequestDigest =
            (stored.RequestDigest.Hex[0] == '0' ? "1" : "0") +
            stored.RequestDigest.Hex[1..];
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.ReadWarningCodes(
                stored.CheckpointPayloadJson,
                EntityId<JobIdTag>.New(),
                stored.ProjectId,
                stored.RequestDigest,
                stored.CheckpointStage));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.ReadWarningCodes(
                stored.CheckpointPayloadJson,
                stored.JobId,
                EntityId<ProjectIdTag>.New(),
                stored.RequestDigest,
                stored.CheckpointStage));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.ReadWarningCodes(
                stored.CheckpointPayloadJson,
                stored.JobId,
                stored.ProjectId,
                new ContentDigest(differentRequestDigest),
                stored.CheckpointStage));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            IngestWorkflowCheckpointCodec.ReadWarningCodes(
                stored.CheckpointPayloadJson,
                stored.JobId,
                stored.ProjectId,
                stored.RequestDigest,
                stored.CheckpointStage - 1));
        Assert.IsInstanceOfType<DurationFieldsDifferWarningEvidence>(
            warnings.Single(static warning =>
                warning.Code == MediaInfoWarningCode.DurationFieldsDiffer).Evidence);
        Assert.IsInstanceOfType<LowResolutionWarningEvidence>(
            warnings.Single(static warning =>
                warning.Code == MediaInfoWarningCode.LowResolution).Evidence);
        Assert.IsInstanceOfType<MediaStreamCountWarningEvidence>(
            warnings.Single(static warning =>
                warning.Code == MediaInfoWarningCode.MultipleAudioStreams).Evidence);
        Assert.IsInstanceOfType<MediaStreamCountWarningEvidence>(
            warnings.Single(static warning =>
                warning.Code == MediaInfoWarningCode.MultipleVideoStreams).Evidence);
        Assert.IsInstanceOfType<VariableRateFieldsDifferWarningEvidence>(
            warnings.Single(static warning =>
                warning.Code == MediaInfoWarningCode.VariableRateFieldsDiffer).Evidence);
    }

    [TestMethod]
    [DataRow("unknown-property")]
    [DataRow("warning-evidence")]
    [DataRow("stage-binding")]
    [DataRow("fractional-integer")]
    [DataRow("out-of-range-integer")]
    public async Task DurableCheckpointRejectsCanonicalSchemaAndEvidenceMutations(
        string mutation)
    {
        using WorkflowFixture fixture = new(allWarnings: true);
        fixture.TimeoutOnceAt = "decode";
        _ = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        StoredIngestWorkflow stored = fixture.WorkflowStore.Get(
            fixture.Request.JobId)!;
        string changed = MutateCanonicalJson(
            stored.CheckpointPayloadJson,
            root =>
            {
                switch (mutation)
                {
                    case "unknown-property":
                        root["unexpected"] = true;
                        break;
                    case "warning-evidence":
                        JsonArray warnings = root["mediaInfo"]!["warnings"]!
                            .AsArray();
                        JsonNode warning = warnings.Single(node =>
                            string.Equals(
                                node!["code"]!.GetValue<string>(),
                                nameof(MediaInfoWarningCode.LowResolution),
                                StringComparison.Ordinal))!;
                        warning["lowResolution"]!["observedWidth"] = 641;
                        break;
                    case "stage-binding":
                        root["stage"] = 3;
                        break;
                    case "fractional-integer":
                        root["sourcePtsIndex"]!["entries"]![0]!["sequence"] =
                            0.5m;
                        break;
                    case "out-of-range-integer":
                        root["sourcePtsIndex"]!["entries"]![0]!["sequence"] =
                            2_147_483_648m;
                        break;
                    default:
                        throw new AssertFailedException(
                            "Unknown checkpoint mutation.");
                }
            });
        StoredIngestWorkflow changedStored = WithCheckpointPayload(
            stored,
            changed);

        InvalidOperationException warningReadException =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                IngestWorkflowCheckpointCodec.ReadWarningCodes(
                    changed,
                    stored.JobId,
                    stored.ProjectId,
                    stored.RequestDigest,
                    stored.CheckpointStage));
        Assert.AreEqual(
            "The durable ingest checkpoint is invalid.",
            warningReadException.Message);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                fixture.Codec.DeserializeAsync(
                    fixture.Request,
                    changedStored,
                    CancellationToken.None));

        Assert.AreEqual(
            "The durable ingest checkpoint is invalid.",
            exception.Message);
        Assert.IsFalse(exception.Message.Contains(
            fixture.RootPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CompletedCheckpointRoundTripRevalidatesArtifactsAndDerivedIndices()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        IngestWorkflowRunResult completed = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        StoredIngestWorkflow stored = fixture.WorkflowStore.Get(
            fixture.Request.JobId)!;

        WorkflowResumeState restored = await fixture.Codec.DeserializeAsync(
            fixture.Request,
            stored,
            CancellationToken.None);

        Assert.IsTrue(completed.IsComplete);
        Assert.AreEqual(
            IngestWorkflowStage.PreviewAssetsCommitted,
            restored.Stage);
        Assert.AreEqual(
            stored.CheckpointPayloadJson,
            fixture.Codec.Serialize(restored));
        Assert.AreEqual(
            completed.SourcePtsIndex!.CanonicalJson,
            restored.SourcePtsIndex!.CanonicalJson);
        Assert.AreEqual(
            completed.Proxy!.ProxyPtsIndex.CanonicalJson,
            restored.Proxy!.ProxyPtsIndex.CanonicalJson);
        Assert.AreEqual(
            completed.FrameMap!.CanonicalJson,
            restored.FrameMap!.CanonicalJson);
        Assert.AreEqual(
            completed.PreviewAssets!.WaveformIndex.CanonicalJson,
            restored.PreviewAssets!.WaveformIndex.CanonicalJson);
        Assert.AreEqual(
            completed.PreviewAssets.SpriteIndex.CanonicalJson,
            restored.PreviewAssets.SpriteIndex.CanonicalJson);
    }

    [TestMethod]
    public async Task FreshRuntimeAfterOriginalCommitRestoresMovedSourceAndRunsOnlySuffix()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.TimeoutOnceAt = "proxy";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(
            IngestWorkflowStage.OriginalCommitted,
            first.LastCompletedStage);
        Assert.IsFalse(File.Exists(fixture.Staged.StagingPath));
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.IsTrue(resumed.IsComplete);
        Assert.AreEqual(2, resumed.Job.SequenceNumber);
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "copy"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "original"));
        Assert.AreEqual(2, fixture.Calls.Count(static stage => stage == "proxy"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "map"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "preview"));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("tampered")]
    public async Task CompletedCheckpointRejectsMissingOrTamperedRegisteredArtifact(
        string mutation)
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        IngestWorkflowRunResult completed = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        StoredIngestWorkflow stored = fixture.WorkflowStore.Get(
            fixture.Request.JobId)!;
        string proxyPath = fixture.GetRegisteredArtifactPath(
            completed.Proxy!.ProxyArtifact.Id);
        if (mutation == "missing")
        {
            File.Delete(proxyPath);
        }
        else
        {
            byte[] changed = File.ReadAllBytes(proxyPath);
            changed[^1] ^= 0xff;
            File.WriteAllBytes(proxyPath, changed);
        }

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                fixture.Codec.DeserializeAsync(
                    fixture.Request,
                    stored,
                    CancellationToken.None));

        Assert.AreEqual(
            "The durable ingest checkpoint is invalid.",
            exception.Message);
        Assert.IsFalse(exception.Message.Contains(
            fixture.RootPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RegisteredOriginalAheadOfCheckpointRequiresReconciliation()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.TimeoutOnceAt = "original";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        Assert.AreEqual(
            IngestWorkflowStage.DecodeValidated,
            first.LastCompletedStage);
        _ = await fixture.PromoteOriginalOutsideWorkflowAsync();
        int callsBeforeResume = fixture.Calls.Count;
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, resumed.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointValidationFailed,
            resumed.FailureReason);
        Assert.IsTrue(resumed.ReconciliationRequired);
        Assert.AreEqual(callsBeforeResume, fixture.Calls.Count);
    }

    [TestMethod]
    public async Task MissingOriginalBehindCheckpointRequiresReconciliation()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.TimeoutOnceAt = "proxy";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        Assert.AreEqual(
            IngestWorkflowStage.OriginalCommitted,
            first.LastCompletedStage);
        File.Delete(fixture.GetRegisteredArtifactPath(
            first.Original!.Artifact.Id));
        int callsBeforeResume = fixture.Calls.Count;
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, resumed.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointValidationFailed,
            resumed.FailureReason);
        Assert.IsTrue(resumed.ReconciliationRequired);
        Assert.AreEqual(callsBeforeResume, fixture.Calls.Count);
    }

    [TestMethod]
    [DataRow("map", IngestWorkflowStage.ProxyCommitted)]
    [DataRow("preview", IngestWorkflowStage.FrameMapBuilt)]
    public async Task FreshRuntimeRestoresRegisteredProxyAndRunsOnlyRemainingSuffix(
        string timeoutStage,
        IngestWorkflowStage expectedCheckpoint)
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.TimeoutOnceAt = timeoutStage;
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        Assert.AreEqual(expectedCheckpoint, first.LastCompletedStage);
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.IsTrue(resumed.IsComplete);
        Assert.AreEqual(2, resumed.Job.SequenceNumber);
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "copy"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "original"));
        Assert.AreEqual(1, fixture.Calls.Count(static stage => stage == "proxy"));
        Assert.AreEqual(
            timeoutStage == "map" ? 2 : 1,
            fixture.Calls.Count(static stage => stage == "map"));
        Assert.AreEqual(
            timeoutStage == "preview" ? 2 : 1,
            fixture.Calls.Count(static stage => stage == "preview"));
    }

    [TestMethod]
    public async Task PartialPreviewArtifactAheadOfCheckpointRequiresReconciliation()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.TimeoutOnceAt = "preview";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        Assert.AreEqual(
            IngestWorkflowStage.FrameMapBuilt,
            first.LastCompletedStage);
        _ = await fixture.PromoteUnexpectedPreviewArtifactAsync();
        int callsBeforeResume = fixture.Calls.Count;
        (JobCoordinator coordinator, IngestWorkflow workflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            coordinator.QueueRetry(fixture.Request.JobId).Outcome);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointValidationFailed,
            resumed.FailureReason);
        Assert.IsTrue(resumed.ReconciliationRequired);
        Assert.AreEqual(callsBeforeResume, fixture.Calls.Count);
    }

    [TestMethod]
    [DataRow("ready")]
    [DataRow("partial")]
    public async Task UncheckpointedStagingSideEffectRequiresReconciliation(
        string kind)
    {
        using WorkflowFixture fixture = new();
        fixture.CreateUncheckpointedStaging(kind);

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointValidationFailed,
            result.FailureReason);
        Assert.IsTrue(result.ReconciliationRequired);
        Assert.AreEqual(IngestWorkflowStage.NotStarted, result.LastCompletedStage);
        Assert.IsEmpty(fixture.Calls);
    }

    [TestMethod]
    public async Task RecoveryRequeueCannotRerunUnregisteredOriginalPromotion()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.SeedAbandonedDecodeCheckpoint();
        string unregisteredPath =
            fixture.MoveStagingToUnregisteredOriginalDestination();
        JobRecovery recovery = fixture.CreateFreshRecovery(hourOffset: 1);

        JobRecoveryResult recovered = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal)
            {
                IngestWorkflow.JobKind,
            },
            CancellationToken.None);

        Assert.HasCount(1, recovered.RecoveredJobs);
        Assert.IsTrue(recovered.RecoveredJobs[0].AutomaticallyRequeued);
        Assert.IsTrue(recovered.BeforeCheck.Issues.Any(issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.UnregisteredArtifactFile &&
            issue.EntityId == fixture.ProjectId.ToString()));
        Assert.IsTrue(recovered.AfterCheck.Issues.Any(issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.UnregisteredArtifactFile &&
            issue.EntityId == fixture.ProjectId.ToString()));
        (_, IngestWorkflow workflow) = fixture.CreateFreshRuntime(hourOffset: 2);

        IngestWorkflowRunResult resumed = await workflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, resumed.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointValidationFailed,
            resumed.FailureReason);
        Assert.IsTrue(resumed.ReconciliationRequired);
        Assert.IsFalse(resumed.CanResumeWithThisResult);
        Assert.IsEmpty(fixture.Calls);
        Assert.IsTrue(File.Exists(unregisteredPath));
        Assert.IsNull(fixture.ArtifactStore.GetArtifact(
            fixture.Request.OriginalArtifactId));
    }

    [TestMethod]
    public async Task ConcurrentFreshResumeAllowsExactlyOneAttempt()
    {
        using WorkflowFixture fixture = new();
        fixture.TimeoutOnceAt = "decode";
        _ = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        (JobCoordinator firstCoordinator, IngestWorkflow firstWorkflow) =
            fixture.CreateFreshRuntime(hourOffset: 1);
        (_, IngestWorkflow secondWorkflow) =
            fixture.CreateFreshRuntime(hourOffset: 2);
        Assert.AreEqual(
            JobRetryOutcome.Queued,
            firstCoordinator.QueueRetry(fixture.Request.JobId).Outcome);
        fixture.BlockAt = "decode";
        Task<IngestWorkflowRunResult> firstResume = firstWorkflow.ResumeAsync(
            fixture.Request.JobId,
            CancellationToken.None);
        fixture.WaitForBlockedStage();

        try
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondWorkflow.ResumeAsync(
                    fixture.Request.JobId,
                    CancellationToken.None));
        }
        finally
        {
            fixture.ReleaseBlockedStage();
        }

        IngestWorkflowRunResult completed = await firstResume;
        Assert.IsTrue(completed.IsComplete);
        Assert.AreEqual(2, completed.Job.SequenceNumber);
        Assert.AreEqual(2, fixture.RequireAggregate().Attempts.Count);
    }

    [TestMethod]
    [DataRow("probe", IngestWorkflowFailureReason.ProbeFailed)]
    [DataRow("index", IngestWorkflowFailureReason.SourceIndexFailed)]
    [DataRow("decode", IngestWorkflowFailureReason.DecodeValidationFailed)]
    [DataRow("proxy", IngestWorkflowFailureReason.ProxyGenerationFailed)]
    [DataRow("preview", IngestWorkflowFailureReason.PreviewGenerationFailed)]
    public async Task TypedProductionTimeoutReasonsRemainRetryable(
        string stage,
        IngestWorkflowFailureReason expectedReason)
    {
        using WorkflowFixture fixture = new();
        fixture.ResetTimeout(stage);

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual("job.execution_timed_out", result.Job.Snapshot.ErrorCode);
        Assert.AreEqual(expectedReason, result.FailureReason);
        Assert.IsTrue(result.CanResumeWithThisResult);
        Assert.IsFalse(result.ReconciliationRequired);
    }

    [TestMethod]
    public async Task CancellationAfterCopyReturnsCancelledPartialWithoutFailureOrRetryClaim()
    {
        using WorkflowFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        fixture.CancelAt = "probe";
        fixture.Cancellation = cancellation;

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            cancellation.Token);

        Assert.AreEqual(JobState.Cancelled, result.Job.Snapshot.State);
        Assert.AreEqual(IngestWorkflowStage.CopiedToStaging, result.LastCompletedStage);
        Assert.IsNull(result.FailureReason);
        Assert.IsFalse(result.ReconciliationRequired);
        Assert.IsFalse(result.CanResumeWithThisResult);
        Assert.IsNotNull(result.Asset);
        Assert.IsNull(result.Original);
        CollectionAssert.AreEqual(new[] { "copy", "probe" }, fixture.Calls.ToArray());
        StoredJobAggregate aggregate = fixture.RequireAggregate();
        Assert.AreEqual(1, aggregate.Progress.Count);
        Assert.AreEqual(1, aggregate.Heartbeats.Count);
    }

    [TestMethod]
    public async Task ReconciliationFailureAfterValidPrefixIsNeverAcceptedAsRetryCheckpoint()
    {
        using WorkflowFixture fixture = new();
        fixture.ReconciliationAtPreview = true;

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.PreviewGenerationFailed,
            result.FailureReason);
        Assert.AreEqual(IngestWorkflowStage.FrameMapBuilt, result.LastCompletedStage);
        Assert.IsTrue(result.ReconciliationRequired);
        Assert.IsFalse(result.CanResumeWithThisResult);
        Assert.IsNotNull(result.Original);
        Assert.IsNotNull(result.Proxy);
        Assert.IsNotNull(result.FrameMap);
        Assert.IsNull(result.PreviewAssets);
    }

    [TestMethod]
    public async Task HeartbeatPersistenceFailureAlwaysRequiresReconciliation()
    {
        using WorkflowFixture fixture = new();
        fixture.ExecuteNonQuery("""
            CREATE TRIGGER reject_ingest_heartbeat
            BEFORE INSERT ON job_heartbeats
            BEGIN
                SELECT RAISE(ABORT, 'injected heartbeat failure');
            END;
            """);

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.ProgressPersistenceFailed,
            result.FailureReason);
        Assert.AreEqual(
            IngestWorkflowStage.CopiedToStaging,
            result.LastCompletedStage);
        Assert.IsTrue(result.ReconciliationRequired);
        Assert.IsFalse(result.CanResumeWithThisResult);
        StoredJobAggregate aggregate = fixture.RequireAggregate();
        Assert.AreEqual(1, aggregate.Progress.Count);
        Assert.AreEqual(0, aggregate.Heartbeats.Count);
    }

    [TestMethod]
    public async Task OriginalCheckpointPersistenceFailureRollsBackStateAndRequiresReconciliation()
    {
        using WorkflowFixture fixture = new(useRegisteredArtifacts: true);
        fixture.ExecuteNonQuery("""
            CREATE TRIGGER reject_original_ingest_checkpoint
            BEFORE UPDATE OF checkpoint_stage ON ingest_workflows
            WHEN NEW.checkpoint_stage = 6
            BEGIN
                SELECT RAISE(ABORT, 'injected checkpoint failure');
            END;
            """);

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(
            IngestWorkflowFailureReason.CheckpointPersistenceFailed,
            result.FailureReason);
        Assert.AreEqual(
            IngestWorkflowStage.DecodeValidated,
            result.LastCompletedStage);
        Assert.IsTrue(result.ReconciliationRequired);
        Assert.IsFalse(result.CanResumeWithThisResult);
        Assert.IsNull(result.Original);
        Assert.IsNotNull(fixture.ArtifactStore.GetArtifact(
            fixture.Request.OriginalArtifactId));
        StoredIngestWorkflow stored = fixture.WorkflowStore.Get(
            fixture.Request.JobId)!;
        Assert.AreEqual(
            (int)IngestWorkflowStage.DecodeValidated,
            stored.CheckpointStage);
    }

    [TestMethod]
    public async Task MismatchedStageOutputFailsInsideAttemptInsteadOfProducingSuccess()
    {
        using WorkflowFixture fixture = new();
        fixture.ReturnMismatchedProbe = true;

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(IngestWorkflowFailureReason.ProbeFailed, result.FailureReason);
        Assert.AreEqual(
            IngestWorkflowStage.CopiedToStaging,
            result.LastCompletedStage);
        Assert.IsNull(result.SourcePtsIndex);
        CollectionAssert.AreEqual(new[] { "copy", "probe" }, fixture.Calls.ToArray());
    }

    [TestMethod]
    [DataRow("map", IngestWorkflowFailureReason.FrameMapGenerationFailed, IngestWorkflowStage.ProxyCommitted)]
    [DataRow("preview", IngestWorkflowFailureReason.PreviewGenerationFailed, IngestWorkflowStage.FrameMapBuilt)]
    public async Task CrossBoundOutputMismatchRollsBackOnlyTheInvalidStage(
        string mismatch,
        IngestWorkflowFailureReason expectedReason,
        IngestWorkflowStage expectedStage)
    {
        using WorkflowFixture fixture = new();
        fixture.ReturnMismatchedMap = mismatch == "map";
        fixture.ReturnMismatchedPreview = mismatch == "preview";

        IngestWorkflowRunResult result = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(expectedReason, result.FailureReason);
        Assert.AreEqual(expectedStage, result.LastCompletedStage);
        Assert.IsNotNull(result.Original);
        Assert.IsNotNull(result.Proxy);
        Assert.AreEqual(mismatch == "preview", result.FrameMap is not null);
        Assert.IsNull(result.PreviewAssets);
    }

    [TestMethod]
    public async Task ResumeRejectsCheckpointFromAnOlderAttemptGeneration()
    {
        using WorkflowFixture fixture = new();
        fixture.ResetTimeout("index");
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        _ = fixture.Coordinator.QueueRetry(fixture.Request.JobId);

        fixture.ResetTimeout("decode");
        IngestWorkflowRunResult second = await fixture.Workflow.RunAsync(
            fixture.Request,
            first,
            CancellationToken.None);
        Assert.AreEqual(2, second.Job.SequenceNumber);
        Assert.IsTrue(
            second.CanResumeWithThisResult,
            $"failure={second.FailureReason}; stage={second.LastCompletedStage}; " +
            $"reconcile={second.ReconciliationRequired}; error={second.Job.Snapshot.ErrorCode}; " +
            $"calls={string.Join(',', fixture.Calls)}");
        _ = fixture.Coordinator.QueueRetry(fixture.Request.JobId);
        int callsBefore = fixture.Calls.Count;

        ArgumentException stale =
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                fixture.Workflow.RunAsync(fixture.Request, first));

        Assert.AreEqual("previousRun", stale.ParamName);
        Assert.AreEqual(callsBefore, fixture.Calls.Count);
        Assert.AreEqual(JobState.Queued, fixture.RequireAggregate().Job.State);
    }

    [TestMethod]
    public async Task RunAsync_RejectsWrongMissingOrNonqueuedJobBeforeClaimingCandidate()
    {
        using WorkflowFixture fixture = new();
        IngestWorkflowRequest missing = fixture.CreateEquivalentRequest(
            jobId: EntityId<JobIdTag>.New());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Workflow.RunAsync(missing));

        Job wrongKind = fixture.CreateQueuedJob("media.other.v1");
        IngestWorkflowRequest wrongKindRequest = fixture.CreateEquivalentRequest(
            jobId: wrongKind.Id);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Workflow.RunAsync(wrongKindRequest));

        _ = fixture.Store.CancelQueued(
            fixture.Request.JobId,
            expectedRevision: 0,
            fixture.TimeProvider.GetUtcNow());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Workflow.RunAsync(fixture.Request));

        Assert.IsEmpty(fixture.Calls);
    }

    [TestMethod]
    public async Task PreviousResultRequiresSameDigestRetryableStateAndNoReconciliation()
    {
        using WorkflowFixture fixture = new();
        fixture.TimeoutOnceAt = "index";
        IngestWorkflowRunResult first = await fixture.Workflow.RunAsync(
            fixture.Request,
            previousRun: null,
            CancellationToken.None);
        _ = fixture.Coordinator.QueueRetry(fixture.Request.JobId);
        IngestWorkflowRequest changed = fixture.CreateEquivalentRequest(
            mediaType: "video/x-matroska");

        ArgumentException exception =
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                fixture.Workflow.RunAsync(changed, first));

        Assert.AreEqual("previousRun", exception.ParamName);
        Assert.AreEqual(3, fixture.Calls.Count);
        Assert.AreEqual(JobState.Queued, fixture.RequireAggregate().Job.State);
    }

    [TestMethod]
    public void RequestAndResultSurface_AreImmutableTypedPathFreeAndDeterministic()
    {
        using WorkflowFixture fixture = new();
        IngestWorkflowRequest equivalent = fixture.CreateEquivalentRequest();
        Assert.AreEqual(IngestWorkflowRequest.CurrentVersion, fixture.Request.Version);
        Assert.AreEqual(fixture.Request.RequestDigest, equivalent.RequestDigest);

        foreach (Type type in new[]
        {
            typeof(IngestWorkflowRequest),
            typeof(IngestWorkflowRunResult),
            typeof(IngestWorkflow),
        })
        {
            Assert.IsTrue(type
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null),
                type.Name);
        }

        Assert.IsFalse(typeof(IngestWorkflowRunResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.PropertyType == typeof(StagedMediaImport)));
        Assert.AreEqual(0, typeof(IngestWorkflowRunResult).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[] { "ResumeAsync", "RunAsync" },
            typeof(IngestWorkflow)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());

        EntityId<ArtifactIdTag> duplicate = fixture.Request.OriginalArtifactId;
        Assert.ThrowsExactly<ArgumentException>(() => new IngestWorkflowRequest(
            fixture.Request.JobId,
            fixture.Request.ProjectId,
            fixture.Request.AssetCandidateId,
            duplicate,
            duplicate,
            fixture.Request.WaveformIndexArtifactId,
            fixture.Request.SpriteImageArtifactId,
            fixture.Request.SpriteIndexArtifactId,
            fixture.Request.MediaType,
            fixture.Request.Rights,
            fixture.Request.ValidationRequirements,
            fixture.Request.OperationTimeout));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            fixture.CreateEquivalentRequest(operationTimeout: TimeSpan.Zero));
    }

    private sealed class WorkflowFixture : IDisposable
    {
        internal static readonly string[] StageNames =
        [
            "copy",
            "probe",
            "index",
            "normalize",
            "decode",
            "original",
            "proxy",
            "map",
            "preview",
        ];

        private static readonly DateTimeOffset CreatedAt =
            new(2043, 12, 13, 14, 15, 16, TimeSpan.Zero);

        private static readonly byte[] SourceContent = Enumerable.Range(0, 128)
            .Select(static value => checked((byte)value))
            .ToArray();

        private static readonly byte[] ProxyContent = Enumerable.Range(0, 512)
            .Select(static value => checked((byte)(value % 251)))
            .ToArray();

        private static readonly byte[] SpriteContent = Enumerable.Range(0, 100)
            .Select(static value => checked((byte)(255 - value)))
            .ToArray();

        private readonly ContentDigest _sourceDigest =
            ContentDigest.Compute(SourceContent);

        private readonly ContentDigest _proxyDigest =
            ContentDigest.Compute(ProxyContent);

        private readonly ManualResetEventSlim _blockedStageEntered = new(false);
        private readonly ManualResetEventSlim _releaseBlockedStage = new(false);

        private int _jobSequence;
        private bool _timeoutThrown;
        private readonly bool _useRegisteredArtifacts;

        internal WorkflowFixture(
            bool allWarnings = false,
            bool useRegisteredArtifacts = false)
        {
            _useRegisteredArtifacts = useRegisteredArtifacts;
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.IngestWorkflowTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = EntityId<ProjectIdTag>.New();
            new ProjectStore(DatabasePath).CreateProject(
                new Project(
                    ProjectId,
                    "Ingest workflow test",
                    "Verify end-to-end media orchestration.",
                    "Test suite",
                    PublicationScope.InternalOnly,
                    deadline: null,
                    assets: [],
                    performers: []),
                "{\"ingestWorkflow\":true}",
                CreatedAt.AddDays(-1));
            Store = new JobStore(DatabasePath);
            Job = CreateQueuedJob(IngestWorkflow.JobKind);
            WorkflowStore = new IngestWorkflowStore(DatabasePath);
            ArtifactStore = new ArtifactStore(DatabasePath, RootPath);
            TimeProvider = new IncrementingTimeProvider(CreatedAt.AddSeconds(1));
            Coordinator = new JobCoordinator(
                Store,
                new LocalJobRunner(Store, TimeProvider),
                TimeProvider);
            Request = CreateEquivalentRequest();
            string stagedPath = Path.Combine(
                RootPath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "imports",
                Job.Id.ToString(),
                string.Concat(Request.AssetCandidateId.ToString(), ".ready"));
            _ = Directory.CreateDirectory(
                Path.GetDirectoryName(stagedPath)
                    ?? throw new AssertFailedException(
                        "The staging path has no parent directory."));
            Staged = CreateStagedMedia(
                Request.AssetCandidateId,
                ProjectId,
                Job.Id,
                "source.mov",
                stagedPath,
                _sourceDigest,
                byteSize: 128,
                CreatedAt);
            Probe = allWarnings
                ? CreateAllWarningsProbe(Staged, ProjectId)
                : CreateProbe(Staged, ProjectId);
            SourceIndex = CreateSourceIndex(Staged);
            MediaInfo = IngestValidator.Validate(
                Probe,
                Request.ValidationRequirements);
            Decode = CreateDecode(Staged);
            Codec = new IngestWorkflowCheckpointCodec(
                RootPath,
                ArtifactStore);
            Workflow = CreateWorkflow();
        }

        internal string RootPath { get; }

        internal string DatabasePath { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal JobStore Store { get; }

        internal Job Job { get; }

        internal IngestWorkflowStore WorkflowStore { get; }

        internal ArtifactStore ArtifactStore { get; }

        internal TimeProvider TimeProvider { get; }

        internal JobCoordinator Coordinator { get; }

        internal IngestWorkflowRequest Request { get; }

        internal StagedMediaImport Staged { get; }

        internal MediaProbeResult Probe { get; }

        internal PtsIndex SourceIndex { get; }

        internal MediaInfo MediaInfo { get; }

        internal DecodeValidationResult Decode { get; }

        internal IngestWorkflowCheckpointCodec Codec { get; }

        internal IngestWorkflow Workflow { get; }

        internal List<string> Calls { get; } = [];

        internal string? FailAt { get; set; }

        internal string? TimeoutOnceAt { get; set; }

        internal string? CancelAt { get; set; }

        internal CancellationTokenSource? Cancellation { get; set; }

        internal bool ReconciliationAtPreview { get; set; }

        internal bool ReturnMismatchedProbe { get; set; }

        internal bool ReturnMismatchedMap { get; set; }

        internal bool ReturnMismatchedPreview { get; set; }

        internal string? BlockAt { get; set; }

        internal void ResetTimeout(string stage)
        {
            TimeoutOnceAt = stage;
            _timeoutThrown = false;
        }

        internal void WaitForBlockedStage()
        {
            Assert.IsTrue(
                _blockedStageEntered.Wait(TimeSpan.FromSeconds(10)),
                "The workflow did not reach the blocked stage.");
        }

        internal void ReleaseBlockedStage() => _releaseBlockedStage.Set();

        internal (JobCoordinator Coordinator, IngestWorkflow Workflow)
            CreateFreshRuntime(int hourOffset)
        {
            var timeProvider = new IncrementingTimeProvider(
                CreatedAt.AddHours(hourOffset));
            var jobStore = new JobStore(DatabasePath);
            var coordinator = new JobCoordinator(
                jobStore,
                new LocalJobRunner(jobStore, timeProvider),
                timeProvider);
            var workflowStore = new IngestWorkflowStore(DatabasePath);
            var artifactStore = new ArtifactStore(DatabasePath, RootPath);
            return (
                coordinator,
                CreateWorkflow(coordinator, workflowStore, artifactStore));
        }

        internal JobRecovery CreateFreshRecovery(int hourOffset)
        {
            var timeProvider = new IncrementingTimeProvider(
                CreatedAt.AddHours(hourOffset));
            return new JobRecovery(
                new JobStore(DatabasePath),
                new WorkspaceRecovery(DatabasePath, RootPath, timeProvider),
                timeProvider);
        }

        internal Task<ImportedOriginal> PromoteOriginalOutsideWorkflowAsync()
        {
            var asset = new Asset(
                Request.AssetCandidateId,
                Staged.SourceFileName,
                Request.MediaType,
                Staged.ContentDigest,
                Staged.ByteSize,
                Request.Rights);
            return PromoteRegisteredOriginalAsync(
                Request.OriginalArtifactId,
                asset,
                Staged,
                MediaInfo,
                Decode,
                CancellationToken.None);
        }

        internal Task<StoredArtifact> PromoteUnexpectedPreviewArtifactAsync() =>
            PromoteFixtureBytesAsync(
                Request.WaveformIndexArtifactId,
                ArtifactKind.Preview,
                $"{Request.AssetCandidateId}/unexpected-waveform.json",
                Encoding.UTF8.GetBytes("{}"),
                CreatedAt.AddMinutes(2),
                CancellationToken.None);

        internal void CreateUncheckpointedStaging(string kind)
        {
            string path = kind switch
            {
                "ready" => Staged.StagingPath,
                "partial" => Path.ChangeExtension(
                    Staged.StagingPath,
                    ".partial"),
                _ => throw new AssertFailedException(
                    "Unknown staging crash-window kind."),
            };
            File.WriteAllBytes(path, SourceContent);
        }

        internal string GetRegisteredArtifactPath(
            EntityId<ArtifactIdTag> artifactId)
        {
            StoredArtifact artifact = ArtifactStore.GetArtifact(artifactId)
                ?? throw new AssertFailedException(
                    "The registered artifact is missing from the fixture store.");
            return Path.Combine(
                RootPath,
                artifact.StorageReference.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
        }

        internal void SeedAbandonedWarningCheckpoint() =>
            SeedAbandonedCheckpoint(includeDecode: false);

        internal void SeedAbandonedDecodeCheckpoint() =>
            SeedAbandonedCheckpoint(includeDecode: true);

        internal string MoveStagingToUnregisteredOriginalDestination()
        {
            string destination = Path.Combine(
                RootPath,
                "projects",
                ProjectId.ToString(),
                "original",
                Request.AssetCandidateId.ToString(),
                string.Concat(Request.AssetCandidateId == Staged.CandidateId
                    ? Staged.ContentDigest.Hex
                    : throw new AssertFailedException(
                        "The staged candidate is not request-bound."),
                    ".original"));
            _ = Directory.CreateDirectory(
                Path.GetDirectoryName(destination)
                    ?? throw new AssertFailedException(
                        "The original destination has no parent."));
            File.Move(Staged.StagingPath, destination, overwrite: false);
            return destination;
        }

        private void SeedAbandonedCheckpoint(bool includeDecode)
        {
            EnsureStagedFile();
            WorkflowResumeState state = WorkflowResumeState.Create(Request);
            StoredIngestWorkflow persisted = WorkflowStore.RegisterOrGet(
                Request.JobId,
                Request.ProjectId,
                Request.Version,
                Request.RequestDigest,
                Request.CanonicalJson,
                IngestWorkflowCheckpointCodec.SchemaVersion,
                Codec.Serialize(state));
            StoredJob current = Store.GetJob(Job.Id)
                ?? throw new AssertFailedException("The workflow job is missing.");
            DateTimeOffset startedAt = CreatedAt.AddMinutes(1);
            var attempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                Job.Id,
                sequenceNumber: 1,
                JobState.Running,
                current.Job.UpdatedAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(
                Job.Id,
                current.Revision,
                attempt,
                startedAt);

            void Advance(Action update)
            {
                update();
                state.ValidateFor(Request);
                persisted = WorkflowStore.AdvanceCheckpoint(
                    Request.JobId,
                    Request.RequestDigest,
                    persisted.Revision,
                    (int)state.Stage,
                    attempt.SequenceNumber,
                    IngestWorkflowCheckpointCodec.SchemaVersion,
                    Codec.Serialize(state),
                    startedAt.AddTicks((int)state.Stage));
            }

            var asset = new Asset(
                Request.AssetCandidateId,
                Staged.SourceFileName,
                Request.MediaType,
                Staged.ContentDigest,
                Staged.ByteSize,
                Request.Rights);
            Advance(() => state.SetStaged(Staged, asset));
            Advance(() => state.SetProbe(Probe));
            Advance(() => state.SetSourceIndex(SourceIndex));
            Advance(() => state.SetMediaInfo(MediaInfo));
            if (includeDecode)
            {
                Advance(() => state.SetDecodeValidation(Decode));
            }
        }

        internal void ExecuteNonQuery(string sql)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            _ = command.ExecuteNonQuery();
        }

        internal IngestWorkflowRequest CreateEquivalentRequest(
            EntityId<JobIdTag>? jobId = null,
            string? mediaType = null,
            TimeSpan? operationTimeout = null) =>
            new(
                jobId ?? Job.Id,
                ProjectId,
                EntityId<AssetIdTag>.Parse(
                    "11111111-1111-4111-8111-111111111111"),
                EntityId<ArtifactIdTag>.Parse(
                    "22222222-2222-4222-8222-222222222222"),
                EntityId<ArtifactIdTag>.Parse(
                    "33333333-3333-4333-8333-333333333333"),
                EntityId<ArtifactIdTag>.Parse(
                    "44444444-4444-4444-8444-444444444444"),
                EntityId<ArtifactIdTag>.Parse(
                    "55555555-5555-4555-8555-555555555555"),
                EntityId<ArtifactIdTag>.Parse(
                    "66666666-6666-4666-8666-666666666666"),
                mediaType ?? "video/quicktime",
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal editing",
                    externalPublicationAllowed: false),
                new IngestValidationRequirements(
                    minimumVideoWidth: 1920,
                    minimumVideoHeight: 1080),
                operationTimeout ?? TimeSpan.FromSeconds(5));

        internal Job CreateQueuedJob(string kind)
        {
            DateTimeOffset timestamp = CreatedAt.AddTicks(
                Interlocked.Increment(ref _jobSequence));
            var job = new Job(
                EntityId<JobIdTag>.New(),
                ProjectId,
                kind,
                JobState.Queued,
                timestamp,
                timestamp,
                currentAttemptNumber: 0);
            Store.CreateJob(job);
            return job;
        }

        internal StoredJobAggregate RequireAggregate() =>
            Store.GetJobAggregate(Job.Id)
            ?? throw new AssertFailedException("The workflow job was not stored.");

        private IngestWorkflow CreateWorkflow(
            JobCoordinator? coordinator = null,
            IngestWorkflowStore? workflowStore = null,
            ArtifactStore? artifactStore = null) =>
            new(
                coordinator ?? Coordinator,
                workflowStore ?? WorkflowStore,
                artifactStore is null
                    ? Codec
                    : new IngestWorkflowCheckpointCodec(RootPath, artifactStore),
                artifactStore ?? ArtifactStore,
                CopyAsync,
                ProbeAsync,
                IndexAsync,
                Normalize,
                DecodeAsync,
                PromoteOriginalAsync,
                GenerateProxyAsync,
                GenerateFrameMap,
                GeneratePreviewAsync);

        private Task<StagedMediaImport> CopyAsync(
            EntityId<AssetIdTag> candidateId,
            EntityId<ProjectIdTag> projectId,
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            OnCall("copy", cancellationToken);
            Assert.AreEqual(Request.AssetCandidateId, candidateId);
            Assert.AreEqual(ProjectId, projectId);
            Assert.AreEqual(Job.Id, jobId);
            EnsureStagedFile();
            return Task.FromResult(Staged);
        }

        private void EnsureStagedFile()
        {
            if (!File.Exists(Staged.StagingPath))
            {
                File.WriteAllBytes(Staged.StagingPath, SourceContent);
            }
        }

        private Task<MediaProbeResult> ProbeAsync(
            StagedMediaImport stagedMedia,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("probe", cancellationToken);
            AssertStagedEquivalent(Staged, stagedMedia);
            Assert.AreEqual(Request.OperationTimeout, timeout);
            if (ReturnMismatchedProbe)
            {
                return Task.FromResult(CreateProbe(
                    Staged,
                    EntityId<ProjectIdTag>.New()));
            }

            return Task.FromResult(Probe);
        }

        private Task<PtsIndex> IndexAsync(
            StagedMediaImport stagedMedia,
            MediaProbeResult probeResult,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("index", cancellationToken);
            AssertStagedEquivalent(Staged, stagedMedia);
            AssertProbeEquivalent(Probe, probeResult);
            Assert.AreEqual(Request.OperationTimeout, timeout);
            return Task.FromResult(SourceIndex);
        }

        private MediaInfo Normalize(
            MediaProbeResult probeResult,
            IngestValidationRequirements requirements)
        {
            OnCall("normalize", CancellationToken.None);
            AssertProbeEquivalent(Probe, probeResult);
            return IngestValidator.Validate(probeResult, requirements);
        }

        private Task<DecodeValidationResult> DecodeAsync(
            StagedMediaImport stagedMedia,
            MediaProbeResult probeResult,
            PtsIndex ptsIndex,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("decode", cancellationToken);
            AssertStagedEquivalent(Staged, stagedMedia);
            AssertProbeEquivalent(Probe, probeResult);
            Assert.AreEqual(SourceIndex.CanonicalJson, ptsIndex.CanonicalJson);
            Assert.AreEqual(Request.OperationTimeout, timeout);
            return Task.FromResult(Decode);
        }

        private Task<ImportedOriginal> PromoteOriginalAsync(
            EntityId<ArtifactIdTag> artifactId,
            Asset asset,
            StagedMediaImport stagedMedia,
            MediaProbeResult probeResult,
            MediaInfo mediaInfo,
            DecodeValidationResult decodeValidation,
            CancellationToken cancellationToken)
        {
            OnCall("original", cancellationToken);
            if (_useRegisteredArtifacts)
            {
                return PromoteRegisteredOriginalAsync(
                    artifactId,
                    asset,
                    stagedMedia,
                    mediaInfo,
                    decodeValidation,
                    cancellationToken);
            }

            StoredArtifact artifact = CreateStoredArtifact(
                artifactId,
                ProjectId,
                ArtifactKind.Original,
                $"projects/{ProjectId}/original/{asset.Id}/{asset.ContentDigest}.original",
                asset.ContentDigest,
                asset.ByteSize,
                CreatedAt);
            return Task.FromResult(new ImportedOriginal(
                asset,
                artifact,
                mediaInfo,
                decodeValidation,
                OriginalIntegrityStatus.Verified));
        }

        private Task<GeneratedProxy> GenerateProxyAsync(
            EntityId<ArtifactIdTag> proxyArtifactId,
            Asset sourceAsset,
            StoredArtifact sourceOriginal,
            PtsIndex sourcePtsIndex,
            EntityId<JobIdTag> proxyJobId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("proxy", cancellationToken);
            if (_useRegisteredArtifacts)
            {
                return GenerateRegisteredProxyAsync(
                    proxyArtifactId,
                    sourceAsset,
                    sourceOriginal,
                    proxyJobId,
                    cancellationToken);
            }

            StoredArtifact proxyArtifact = CreateStoredArtifact(
                proxyArtifactId,
                ProjectId,
                ArtifactKind.Proxy,
                $"projects/{ProjectId}/proxy/{sourceAsset.Id}/{proxyArtifactId}.mp4",
                _proxyDigest,
                byteSize: 512,
                CreatedAt.AddMinutes(1));
            PtsIndex proxyIndex = CreateProxyIndex(sourceAsset.Id, proxyJobId);
            MediaProbeResult proxyProbe = CreateProxyProbe(
                sourceAsset.Id,
                proxyJobId,
                proxyArtifact);
            return Task.FromResult(CreateGeneratedProxy(
                sourceAsset,
                sourceOriginal,
                proxyArtifact,
                proxyProbe,
                proxyIndex));
        }

        private ProxyFrameMap GenerateFrameMap(
            PtsIndex sourcePtsIndex,
            GeneratedProxy generatedProxy)
        {
            OnCall("map", CancellationToken.None);
            if (ReturnMismatchedMap)
            {
                return ProxyFrameMap.Create(
                    generatedProxy.SourceAssetId,
                    ProjectId,
                    generatedProxy.SourceContentDigest,
                    new ContentDigest(new string('f', 64)),
                    generatedProxy.ProxyArtifact.ContentDigest,
                    ContentDigest.Compute(Encoding.UTF8.GetBytes(
                        generatedProxy.ProxyPtsIndex.CanonicalJson)),
                    sourcePtsIndex.Entries
                        .Select(static entry => entry.PresentationTime)
                        .ToArray(),
                    sourcePtsIndex.PresentationEnd,
                    generatedProxy.ProxyPtsIndex.Entries
                        .Select(static entry => entry.PresentationTime)
                        .ToArray());
            }

            return new ProxyFrameMapGenerator().Generate(
                sourcePtsIndex,
                generatedProxy);
        }

        private Task<GeneratedPreviewAssets> GeneratePreviewAsync(
            EntityId<ArtifactIdTag> waveformIndexArtifactId,
            EntityId<ArtifactIdTag> spriteImageArtifactId,
            EntityId<ArtifactIdTag> spriteIndexArtifactId,
            GeneratedProxy generatedProxy,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("preview", cancellationToken);
            if (ReconciliationAtPreview)
            {
                throw CreatePreviewException(
                    PreviewAssetGenerationFailureReason.PromotionFailed,
                    reconciliationRequired: true);
            }

            if (_useRegisteredArtifacts)
            {
                return GenerateRegisteredPreviewAsync(
                    waveformIndexArtifactId,
                    spriteImageArtifactId,
                    spriteIndexArtifactId,
                    generatedProxy,
                    cancellationToken);
            }

            return Task.FromResult(CreatePreviewAssets(
                waveformIndexArtifactId,
                spriteImageArtifactId,
                spriteIndexArtifactId,
                generatedProxy,
                ReturnMismatchedPreview));
        }

        private async Task<ImportedOriginal> PromoteRegisteredOriginalAsync(
            EntityId<ArtifactIdTag> artifactId,
            Asset asset,
            StagedMediaImport stagedMedia,
            MediaInfo mediaInfo,
            DecodeValidationResult decodeValidation,
            CancellationToken cancellationToken)
        {
            StoredArtifact artifact = await ArtifactStore.PromoteAsync(
                new ArtifactPromotionRequest(
                    artifactId,
                    ProjectId,
                    ArtifactKind.Original,
                    stagedMedia.StagingPath,
                    $"{asset.Id}/{asset.ContentDigest}.original",
                    asset.ContentDigest,
                    asset.ByteSize,
                    CreatedAt),
                cancellationToken);
            return new ImportedOriginal(
                asset,
                artifact,
                mediaInfo,
                decodeValidation,
                OriginalIntegrityStatus.Verified);
        }

        private async Task<GeneratedProxy> GenerateRegisteredProxyAsync(
            EntityId<ArtifactIdTag> artifactId,
            Asset sourceAsset,
            StoredArtifact sourceOriginal,
            EntityId<JobIdTag> proxyJobId,
            CancellationToken cancellationToken)
        {
            StoredArtifact proxyArtifact = await PromoteFixtureBytesAsync(
                artifactId,
                ArtifactKind.Proxy,
                $"{sourceAsset.Id}/{artifactId}.mp4",
                ProxyContent,
                CreatedAt.AddMinutes(1),
                cancellationToken);
            PtsIndex proxyIndex = CreateProxyIndex(sourceAsset.Id, proxyJobId);
            MediaProbeResult proxyProbe = CreateProxyProbe(
                sourceAsset.Id,
                proxyJobId,
                proxyArtifact);
            return CreateGeneratedProxy(
                sourceAsset,
                sourceOriginal,
                proxyArtifact,
                proxyProbe,
                proxyIndex);
        }

        private async Task<GeneratedPreviewAssets> GenerateRegisteredPreviewAsync(
            EntityId<ArtifactIdTag> waveformIndexArtifactId,
            EntityId<ArtifactIdTag> spriteImageArtifactId,
            EntityId<ArtifactIdTag> spriteIndexArtifactId,
            GeneratedProxy generatedProxy,
            CancellationToken cancellationToken)
        {
            GeneratedPreviewAssets model = CreatePreviewAssets(
                waveformIndexArtifactId,
                spriteImageArtifactId,
                spriteIndexArtifactId,
                generatedProxy,
                mismatchWaveformArtifact: false);
            StoredArtifact waveformArtifact = await PromoteFixtureBytesAsync(
                waveformIndexArtifactId,
                ArtifactKind.Preview,
                $"{generatedProxy.SourceAssetId}/waveform.json",
                Encoding.UTF8.GetBytes(model.WaveformIndex.CanonicalJson),
                CreatedAt.AddMinutes(2),
                cancellationToken);
            StoredArtifact spriteImageArtifact = await PromoteFixtureBytesAsync(
                spriteImageArtifactId,
                ArtifactKind.Preview,
                $"{generatedProxy.SourceAssetId}/sprite.png",
                SpriteContent,
                CreatedAt.AddMinutes(2),
                cancellationToken);
            StoredArtifact spriteIndexArtifact = await PromoteFixtureBytesAsync(
                spriteIndexArtifactId,
                ArtifactKind.Preview,
                $"{generatedProxy.SourceAssetId}/sprite.json",
                Encoding.UTF8.GetBytes(model.SpriteIndex.CanonicalJson),
                CreatedAt.AddMinutes(2),
                cancellationToken);
            return Construct<GeneratedPreviewAssets>(
                waveformArtifact,
                spriteImageArtifact,
                spriteIndexArtifact,
                model.WaveformIndex,
                model.SpriteIndex,
                model.Profile);
        }

        private async Task<StoredArtifact> PromoteFixtureBytesAsync(
            EntityId<ArtifactIdTag> artifactId,
            ArtifactKind kind,
            string relativePath,
            byte[] content,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
        {
            string stagingPath = Path.Combine(
                RootPath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "workflow-fixture",
                string.Concat(artifactId.ToString(), ".ready"));
            _ = Directory.CreateDirectory(
                Path.GetDirectoryName(stagingPath)
                    ?? throw new AssertFailedException(
                        "The artifact staging path has no parent."));
            await File.WriteAllBytesAsync(
                stagingPath,
                content,
                cancellationToken);
            return await ArtifactStore.PromoteAsync(
                new ArtifactPromotionRequest(
                    artifactId,
                    ProjectId,
                    kind,
                    stagingPath,
                    relativePath,
                    ContentDigest.Compute(content),
                    content.LongLength,
                    createdAt),
                cancellationToken);
        }

        private static void AssertStagedEquivalent(
            StagedMediaImport expected,
            StagedMediaImport actual)
        {
            Assert.AreEqual(expected.CandidateId, actual.CandidateId);
            Assert.AreEqual(expected.ProjectId, actual.ProjectId);
            Assert.AreEqual(expected.JobId, actual.JobId);
            Assert.AreEqual(expected.SourceFileName, actual.SourceFileName);
            Assert.AreEqual(expected.StagingPath, actual.StagingPath);
            Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
            Assert.AreEqual(expected.ByteSize, actual.ByteSize);
            Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
        }

        private static void AssertProbeEquivalent(
            MediaProbeResult expected,
            MediaProbeResult actual)
        {
            Assert.AreEqual(expected.CandidateId, actual.CandidateId);
            Assert.AreEqual(expected.ProjectId, actual.ProjectId);
            Assert.AreEqual(expected.JobId, actual.JobId);
            Assert.AreEqual(expected.SourceFileName, actual.SourceFileName);
            Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
            Assert.AreEqual(expected.ByteSize, actual.ByteSize);
            Assert.AreEqual(expected.ContainerSignature, actual.ContainerSignature);
            CollectionAssert.AreEqual(
                expected.FormatNames.ToArray(),
                actual.FormatNames.ToArray());
            Assert.AreEqual(expected.Streams.Count, actual.Streams.Count);
            Assert.AreEqual(expected.Warnings.Count, actual.Warnings.Count);
        }

        private void OnCall(string stage, CancellationToken cancellationToken)
        {
            Calls.Add(stage);
            if (string.Equals(BlockAt, stage, StringComparison.Ordinal))
            {
                _blockedStageEntered.Set();
                _releaseBlockedStage.Wait(cancellationToken);
            }

            if (string.Equals(CancelAt, stage, StringComparison.Ordinal))
            {
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (string.Equals(TimeoutOnceAt, stage, StringComparison.Ordinal) &&
                !_timeoutThrown)
            {
                _timeoutThrown = true;
                throw stage switch
                {
                    "probe" => Construct<MediaProbeException>(
                        MediaProbeFailureReason.ProcessTimedOut),
                    "index" => Construct<PtsIndexException>(
                        PtsIndexFailureReason.ProcessTimedOut),
                    "decode" => Construct<DecodeValidationException>(
                        DecodeValidationFailureReason.ProcessTimedOut),
                    "proxy" => Construct<ProxyGenerationException>(
                        ProxyGenerationFailureReason.ProcessTimedOut,
                        false),
                    "preview" => Construct<PreviewAssetGenerationException>(
                        PreviewAssetGenerationFailureReason.ProcessTimedOut,
                        false),
                    _ => new TimeoutException("private timeout detail"),
                };
            }

            if (string.Equals(FailAt, stage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("private stage failure");
            }
        }

        private PtsIndex CreateProxyIndex(
            EntityId<AssetIdTag> sourceAssetId,
            EntityId<JobIdTag> proxyJobId)
        {
            RationalTime duration = new(1001, 30000);
            return new PtsIndex(
                sourceAssetId,
                ProjectId,
                proxyJobId,
                "proxy.mp4",
                _proxyDigest,
                byteSize: 512,
                streamIndex: 0,
                duration,
                [
                    new PtsIndexEntry(0, default, duration, keyFrame: true),
                    new PtsIndexEntry(1, duration, duration, keyFrame: false),
                    new PtsIndexEntry(
                        2,
                        new RationalTime(1001, 15000),
                        duration,
                        keyFrame: true),
                ]);
        }

        private GeneratedPreviewAssets CreatePreviewAssets(
            EntityId<ArtifactIdTag> waveformIndexArtifactId,
            EntityId<ArtifactIdTag> spriteImageArtifactId,
            EntityId<ArtifactIdTag> spriteIndexArtifactId,
            GeneratedProxy generatedProxy,
            bool mismatchWaveformArtifact)
        {
            ContentDigest ptsDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(
                    generatedProxy.ProxyPtsIndex.CanonicalJson));
            WaveformFrame[] waveformFrames = generatedProxy.ProxyPtsIndex.Entries
                .Select(entry => Construct<WaveformFrame>(
                    entry.Sequence,
                    entry.PresentationTime,
                    entry.Duration,
                    Array.Empty<WaveformChannelExtrema>()))
                .ToArray();
            PreviewWaveformIndex waveform = Construct<PreviewWaveformIndex>(
                generatedProxy.SourceAssetId,
                ProjectId,
                generatedProxy.ProxyArtifact.Id,
                generatedProxy.ProxyArtifact.ContentDigest,
                ptsDigest,
                "proxy-preview-assets-v1",
                0,
                0,
                null,
                waveformFrames);
            ContentDigest spriteImageDigest = ContentDigest.Compute(
                SpriteContent);
            ThumbnailSpriteCell[] cells =
            [
                Construct<ThumbnailSpriteCell>(
                    0,
                    0,
                    generatedProxy.ProxyPtsIndex.Entries[0].PresentationTime,
                    generatedProxy.ProxyPtsIndex.Entries[0].Duration,
                    0,
                    0),
                Construct<ThumbnailSpriteCell>(
                    1,
                    2,
                    generatedProxy.ProxyPtsIndex.Entries[2].PresentationTime,
                    generatedProxy.ProxyPtsIndex.Entries[2].Duration,
                    1,
                    0),
            ];
            ThumbnailSpriteIndex sprite = Construct<ThumbnailSpriteIndex>(
                generatedProxy.SourceAssetId,
                ProjectId,
                generatedProxy.ProxyArtifact.Id,
                generatedProxy.ProxyArtifact.ContentDigest,
                ptsDigest,
                spriteImageArtifactId,
                spriteImageDigest,
                "proxy-preview-assets-v1",
                160,
                90,
                2,
                1,
                cells);
            StoredArtifact spriteImage = CreateStoredArtifact(
                spriteImageArtifactId,
                ProjectId,
                ArtifactKind.Preview,
                $"projects/{ProjectId}/previews/sprite.png",
                spriteImageDigest,
                SpriteContent.LongLength,
                CreatedAt.AddMinutes(2));
            StoredArtifact spriteIndex = CreateStoredArtifact(
                spriteIndexArtifactId,
                ProjectId,
                ArtifactKind.Preview,
                $"projects/{ProjectId}/previews/sprite.json",
                ContentDigest.Compute(Encoding.UTF8.GetBytes(sprite.CanonicalJson)),
                Encoding.UTF8.GetByteCount(sprite.CanonicalJson),
                CreatedAt.AddMinutes(2));
            StoredArtifact waveformIndex = CreateStoredArtifact(
                waveformIndexArtifactId,
                ProjectId,
                ArtifactKind.Preview,
                $"projects/{ProjectId}/previews/waveform.json",
                mismatchWaveformArtifact
                    ? new ContentDigest(new string('f', 64))
                    : ContentDigest.Compute(Encoding.UTF8.GetBytes(
                        waveform.CanonicalJson)),
                Encoding.UTF8.GetByteCount(waveform.CanonicalJson),
                CreatedAt.AddMinutes(2));
            PreviewAssetGenerationProfile profile =
                Construct<PreviewAssetGenerationProfile>();
            return Construct<GeneratedPreviewAssets>(
                waveformIndex,
                spriteImage,
                spriteIndex,
                waveform,
                sprite,
                profile);
        }

        public void Dispose()
        {
            _releaseBlockedStage.Set();
            _blockedStageEntered.Dispose();
            _releaseBlockedStage.Dispose();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private sealed class IncrementingTimeProvider(
            DateTimeOffset initial) : TimeProvider
        {
            private long _ticks = initial.UtcTicks;

            public override DateTimeOffset GetUtcNow() =>
                new(Interlocked.Increment(ref _ticks), TimeSpan.Zero);
        }
    }

    private static MediaProbeResult CreateProbe(
        StagedMediaImport staged,
        EntityId<ProjectIdTag> projectId) =>
        new(
            staged.CandidateId,
            projectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: 0.12m,
            declaredByteSize: staged.ByteSize,
            bitRate: null,
            probeScore: 100,
            streams:
            [
                new ProbedMediaStream(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    1280,
                    720,
                    "yuv420p",
                    new RationalNumber(25, 1),
                    new RationalNumber(25, 1),
                    new RationalNumber(1, 1000),
                    startSeconds: 0m,
                    durationSeconds: 0.12m,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
            ],
            warnings: ["audio-track-absent"]);

    private static MediaProbeResult CreateAllWarningsProbe(
        StagedMediaImport staged,
        EntityId<ProjectIdTag> projectId) =>
        new(
            staged.CandidateId,
            projectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            ContainerSignature.Unknown,
            ["mov"],
            "Synthetic mixed container",
            durationSeconds: 12m,
            declaredByteSize: staged.ByteSize,
            bitRate: 1_000_000,
            probeScore: 100,
            streams:
            [
                new ProbedMediaStream(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    640,
                    360,
                    "yuv420p",
                    new RationalNumber(30, 1),
                    new RationalNumber(30000, 1001),
                    new RationalNumber(1, 1000),
                    startSeconds: 0m,
                    durationSeconds: 10m,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
                new ProbedMediaStream(
                    1,
                    MediaStreamKind.Video,
                    "h264",
                    "Main",
                    1920,
                    1080,
                    "yuv420p",
                    new RationalNumber(25, 1),
                    new RationalNumber(25, 1),
                    new RationalNumber(1, 1000),
                    startSeconds: 0m,
                    durationSeconds: 9m,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
                new ProbedMediaStream(
                    2,
                    MediaStreamKind.Audio,
                    "aac",
                    null,
                    width: null,
                    height: null,
                    pixelFormat: null,
                    nominalFrameRate: null,
                    averageFrameRate: null,
                    new RationalNumber(1, 48000),
                    startSeconds: 0m,
                    durationSeconds: 8m,
                    sampleRate: 48000,
                    channels: 2,
                    channelLayout: "stereo"),
                new ProbedMediaStream(
                    3,
                    MediaStreamKind.Audio,
                    "aac",
                    null,
                    width: null,
                    height: null,
                    pixelFormat: null,
                    nominalFrameRate: null,
                    averageFrameRate: null,
                    new RationalNumber(1, 48000),
                    startSeconds: 1m,
                    durationSeconds: 8m,
                    sampleRate: 48000,
                    channels: 2,
                    channelLayout: "stereo"),
            ],
            warnings:
            [
                "multiple-audio-streams",
                "multiple-video-streams",
                "unrecognized-container-signature",
                "variable-rate-fields-differ",
            ]);

    private static StoredIngestWorkflow WithCheckpointPayload(
        StoredIngestWorkflow stored,
        string checkpointPayload) =>
        new(
            stored.JobId,
            stored.ProjectId,
            stored.RequestSchemaVersion,
            stored.RequestDigest,
            stored.RequestPayloadJson,
            stored.CheckpointStage,
            stored.CheckpointAttemptNumber,
            stored.CheckpointSchemaVersion,
            ContentDigest.Compute(Encoding.UTF8.GetBytes(checkpointPayload)),
            checkpointPayload,
            stored.Revision,
            stored.UpdatedAt);

    private static string MutateCanonicalJson(
        string canonicalJson,
        Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(canonicalJson)?.AsObject()
            ?? throw new AssertFailedException(
                "The checkpoint fixture was not a JSON object.");
        mutation(root);
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(
            Encoding.UTF8.GetBytes(root.ToJsonString()));
        return Encoding.UTF8.GetString(canonical);
    }

    private static MediaProbeResult CreateProxyProbe(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<JobIdTag> jobId,
        StoredArtifact proxyArtifact) =>
        new(
            sourceAssetId,
            proxyArtifact.ProjectId,
            jobId,
            "proxy.mp4",
            proxyArtifact.ContentDigest,
            proxyArtifact.ByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: null,
            declaredByteSize: proxyArtifact.ByteSize,
            bitRate: null,
            probeScore: 100,
            streams:
            [
                new ProbedMediaStream(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    640,
                    360,
                    "yuv420p",
                    new RationalNumber(30000, 1001),
                    new RationalNumber(30000, 1001),
                    new RationalNumber(1001, 30000),
                    startSeconds: 0m,
                    durationSeconds: null,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
            ],
            warnings: ["audio-track-absent"]);

    private static PtsIndex CreateSourceIndex(StagedMediaImport staged) =>
        new(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            streamIndex: 0,
            new RationalTime(1, 1000),
            [
                new PtsIndexEntry(0, default, new RationalTime(1, 25), true),
                new PtsIndexEntry(
                    1,
                    new RationalTime(1, 25),
                    new RationalTime(1, 25),
                    false),
                new PtsIndexEntry(
                    2,
                    new RationalTime(2, 25),
                    new RationalTime(1, 25),
                    false),
            ]);

    private static DecodeValidationResult CreateDecode(StagedMediaImport staged) =>
        Construct<DecodeValidationResult>(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            0,
            null,
            TimeSpan.FromMilliseconds(1),
            0,
            ContentDigest.Compute([]),
            default(RationalTime),
            new RationalTime(3, 25),
            3);

    private static StagedMediaImport CreateStagedMedia(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        string stagingPath,
        ContentDigest digest,
        long byteSize,
        DateTimeOffset createdAt) =>
        Construct<StagedMediaImport>(
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            stagingPath,
            digest,
            byteSize,
            createdAt);

    private static StoredArtifact CreateStoredArtifact(
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string storageReference,
        ContentDigest digest,
        long byteSize,
        DateTimeOffset createdAt) =>
        Construct<StoredArtifact>(
            id,
            projectId,
            kind,
            storageReference,
            digest,
            byteSize,
            createdAt);

    private static GeneratedProxy CreateGeneratedProxy(
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        StoredArtifact proxyArtifact,
        MediaProbeResult proxyProbe,
        PtsIndex proxyIndex) =>
        Construct<GeneratedProxy>(
            sourceAsset.Id,
            sourceAsset.ContentDigest,
            sourceOriginal.Id,
            proxyArtifact,
            proxyProbe,
            proxyIndex,
            Construct<ProxyGenerationProfile>(),
            TimeSpan.FromMilliseconds(1));

    private static PreviewAssetGenerationException CreatePreviewException(
        PreviewAssetGenerationFailureReason reason,
        bool reconciliationRequired) =>
        Construct<PreviewAssetGenerationException>(
            reason,
            reconciliationRequired);

    private static T Construct<T>(params object?[] arguments)
    {
        ConstructorInfo constructor = typeof(T)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == arguments.Length);
        try
        {
            return (T)(constructor.Invoke(arguments)
                ?? throw new InvalidOperationException(
                    $"The {typeof(T).Name} constructor returned null."));
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}