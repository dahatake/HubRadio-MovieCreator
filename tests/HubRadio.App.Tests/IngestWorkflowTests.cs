using System.Reflection;
using System.Text;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
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

        Assert.IsTrue(second.IsComplete);
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
        Assert.IsTrue(second.CanResumeWithThisResult);
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
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
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
            new[] { "RunAsync" },
            typeof(IngestWorkflow)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
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

        private readonly ContentDigest _sourceDigest =
            new(new string('a', 64));

        private readonly ContentDigest _proxyDigest =
            new(new string('b', 64));

        private int _jobSequence;
        private bool _timeoutThrown;

        internal WorkflowFixture()
        {
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
            TimeProvider = new IncrementingTimeProvider(CreatedAt.AddSeconds(1));
            Coordinator = new JobCoordinator(
                Store,
                new LocalJobRunner(Store, TimeProvider),
                TimeProvider);
            Request = CreateEquivalentRequest();
            string stagedPath = Path.Combine(RootPath, "source.ready");
            Staged = CreateStagedMedia(
                Request.AssetCandidateId,
                ProjectId,
                Job.Id,
                "source.mov",
                stagedPath,
                _sourceDigest,
                byteSize: 128,
                CreatedAt);
            Probe = CreateProbe(Staged, ProjectId);
            SourceIndex = CreateSourceIndex(Staged);
            MediaInfo = IngestValidator.Validate(
                Probe,
                Request.ValidationRequirements);
            Decode = CreateDecode(Staged);
            Workflow = CreateWorkflow();
        }

        internal string RootPath { get; }

        internal string DatabasePath { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal JobStore Store { get; }

        internal Job Job { get; }

        internal TimeProvider TimeProvider { get; }

        internal JobCoordinator Coordinator { get; }

        internal IngestWorkflowRequest Request { get; }

        internal StagedMediaImport Staged { get; }

        internal MediaProbeResult Probe { get; }

        internal PtsIndex SourceIndex { get; }

        internal MediaInfo MediaInfo { get; }

        internal DecodeValidationResult Decode { get; }

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

        internal void ResetTimeout(string stage)
        {
            TimeoutOnceAt = stage;
            _timeoutThrown = false;
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

        private IngestWorkflow CreateWorkflow() =>
            new(
                Coordinator,
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
            return Task.FromResult(Staged);
        }

        private Task<MediaProbeResult> ProbeAsync(
            StagedMediaImport stagedMedia,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            OnCall("probe", cancellationToken);
            Assert.AreSame(Staged, stagedMedia);
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
            Assert.AreSame(Staged, stagedMedia);
            Assert.AreSame(Probe, probeResult);
            Assert.AreEqual(Request.OperationTimeout, timeout);
            return Task.FromResult(SourceIndex);
        }

        private MediaInfo Normalize(
            MediaProbeResult probeResult,
            IngestValidationRequirements requirements)
        {
            OnCall("normalize", CancellationToken.None);
            Assert.AreSame(Probe, probeResult);
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
            Assert.AreSame(Staged, stagedMedia);
            Assert.AreSame(Probe, probeResult);
            Assert.AreSame(SourceIndex, ptsIndex);
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

            return Task.FromResult(CreatePreviewAssets(
                waveformIndexArtifactId,
                spriteImageArtifactId,
                spriteIndexArtifactId,
                generatedProxy,
                ReturnMismatchedPreview));
        }

        private void OnCall(string stage, CancellationToken cancellationToken)
        {
            Calls.Add(stage);
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
            ContentDigest spriteImageDigest = new(new string('c', 64));
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
                100,
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