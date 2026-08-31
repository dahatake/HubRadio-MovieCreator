using System.Text;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class JobBridgeTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private const string OtherActorSid = "S-1-5-21-100-200-300-1002";
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";
    private static readonly DateTimeOffset CreatedAt =
        new(2042, 11, 12, 13, 14, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task StatusProjectsOnlyClosedSafeDurableFields()
    {
        const string sensitiveMessage = "<img src=x onerror=alert(1)> C:\\private\\media.mov";
        using var fixture = new JobBridgeFixture();
        Job job = fixture.CreateQueuedJob(IngestWorkflow.JobKind);
        fixture.Details.Value = new JobBridgeDetails(
            revision: 7,
            warnings:
            [
                MediaInfoWarningCode.LowResolution,
                MediaInfoWarningCode.AudioTrackAbsent,
            ],
            JobBridgeReconciliationStatus.Required);
        var entered = NewSignal();
        var release = NewSignal();
        Task<JobCoordinatorRunResult> run = fixture.Coordinator.RunAsync(
            job.Id,
            async (context, _) =>
            {
                context.ReportProgress(
                    1,
                    4,
                    "media.inspect",
                    sensitiveMessage,
                    StartedAt.AddSeconds(1));
                context.RecordHeartbeat(StartedAt.AddSeconds(2));
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeout);

        string response;
        try
        {
            response = fixture.Handler.Handle(CreateRequest(
                BridgeProtocol.JobsGetStatusMethod,
                job.Id));
        }
        finally
        {
            release.TrySetResult(true);
        }

        _ = await run.WaitAsync(TestTimeout);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response));
        Assert.IsFalse(response.Contains(sensitiveMessage, StringComparison.Ordinal));
        Assert.IsFalse(response.Contains(ActorSid, StringComparison.Ordinal));
        Assert.IsFalse(response.Contains(fixture.RootPath, StringComparison.OrdinalIgnoreCase));

        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        JsonElement result = root.GetProperty("result");
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetPropertyNames(root));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "jobId",
                "kind",
                "state",
                "currentAttemptId",
                "currentAttemptNumber",
                "revision",
                "detailsRevision",
                "updatedAt",
                "progressRatio",
                "lastProgressAt",
                "lastHeartbeatAt",
                "failure",
                "retryability",
                "cancellationRequested",
                "warnings",
                "reconciliation",
            },
            GetPropertyNames(result));
        Assert.AreEqual("mediaIngest", result.GetProperty("kind").GetString());
        Assert.AreEqual("running", result.GetProperty("state").GetString());
        Assert.AreEqual(0.25m, result.GetProperty("progressRatio").GetDecimal());
        Assert.AreEqual(7, result.GetProperty("detailsRevision").GetInt32());
        Assert.AreEqual("required", result.GetProperty("reconciliation").GetString());
        CollectionAssert.AreEqual(
            new[] { "audioTrackAbsent", "lowResolution" },
            result.GetProperty("warnings")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        StringAssert.EndsWith(result.GetProperty("updatedAt").GetString()!, "Z");
        StringAssert.EndsWith(result.GetProperty("lastProgressAt").GetString()!, "Z");
        StringAssert.EndsWith(result.GetProperty("lastHeartbeatAt").GetString()!, "Z");
    }

    [TestMethod]
    public void UnknownJobKindAndFailureNeverEchoRawStoredValues()
    {
        const string sensitiveKind = "vendor.<script>C:\\private\\kind";
        const string sensitiveError = "vendor.secret_failure";
        using var fixture = new JobBridgeFixture();
        Job job = fixture.CreateTerminalJob(
            sensitiveKind,
            JobState.Failed,
            sensitiveError);

        string response = fixture.Handler.Handle(CreateRequest(
            BridgeProtocol.JobsGetStatusMethod,
            job.Id));

        Assert.IsFalse(response.Contains(sensitiveKind, StringComparison.Ordinal));
        Assert.IsFalse(response.Contains(sensitiveError, StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual("unknown", result.GetProperty("kind").GetString());
        Assert.AreEqual("other", result.GetProperty("failure").GetString());
        Assert.AreEqual("nonRetryable", result.GetProperty("retryability").GetString());
        Assert.AreEqual("notApplicable", result.GetProperty("reconciliation").GetString());
    }

    [TestMethod]
    public void AuthorizationPrecedesExistenceAndCommandsEnforceExpectedRevision()
    {
        using var fixture = new JobBridgeFixture();
        Job job = fixture.CreateQueuedJob("analysis.authorization");
        EntityId<JobIdTag> missingJobId = EntityId<JobIdTag>.New();
        var unauthorized = new JobBridgeRequestHandler(
            fixture.Coordinator,
            new JobBridgeAuthorization(
                fixture.Authorization,
                () => OtherActorSid),
            fixture.Details);

        AssertErrorCode(
            unauthorized.Handle(CreateRequest(
                BridgeProtocol.JobsGetStatusMethod,
                missingJobId)),
            "forbidden");
        AssertErrorCode(
            fixture.Handler.Handle(CreateRequest(
                BridgeProtocol.JobsGetStatusMethod,
                missingJobId)),
            "notFound");
        AssertErrorCode(
            fixture.Handler.Handle(CreateRequest(
                BridgeProtocol.JobsCancelMethod,
                job.Id,
                expectedRevision: 1)),
            "conflict");

        StoredJob stored = fixture.Store.GetJob(job.Id)!;
        Assert.AreEqual(JobState.Queued, stored.Job.State);
        Assert.AreEqual(0, stored.Revision);
    }

    [TestMethod]
    public void RetryReturnsDurableQueuedSnapshotAndDeniesDuplicateRetry()
    {
        using var fixture = new JobBridgeFixture();
        Job job = fixture.CreateTerminalJob(
            "analysis.retry",
            JobState.Failed,
            "job.execution_timed_out");

        string queuedResponse = fixture.Handler.Handle(CreateRequest(
            BridgeProtocol.JobsRetryMethod,
            job.Id,
            expectedRevision: 2));
        using (JsonDocument queuedDocument = JsonDocument.Parse(queuedResponse))
        {
            JsonElement result = queuedDocument.RootElement.GetProperty("result");
            Assert.AreEqual("queued", result.GetProperty("outcome").GetString());
            Assert.AreEqual("queued", result.GetProperty("job").GetProperty("state").GetString());
            Assert.AreEqual(3, result.GetProperty("job").GetProperty("revision").GetInt32());
        }

        string duplicateResponse = fixture.Handler.Handle(CreateRequest(
            BridgeProtocol.JobsRetryMethod,
            job.Id,
            expectedRevision: 3));
        using JsonDocument duplicateDocument = JsonDocument.Parse(duplicateResponse);
        Assert.AreEqual(
            "stateNotEligible",
            duplicateDocument.RootElement
                .GetProperty("result")
                .GetProperty("outcome")
                .GetString());
        Assert.AreEqual(3, fixture.Store.GetJob(job.Id)!.Revision);
    }

    private static BridgeRequest CreateRequest(
        string method,
        EntityId<JobIdTag> jobId,
        int? expectedRevision = null)
    {
        string parameters = expectedRevision is int revision
            ? $$"""{"jobId":"{{jobId}}","expectedRevision":{{revision}}}"""
            : $$"""{"jobId":"{{jobId}}"}""";
        string json =
            $$"""{"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{{parameters}}}""";
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            json,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection),
            rejection.ToString());
        return request!;
    }

    private static string[] GetPropertyNames(JsonElement value) =>
        value.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

    private static void AssertErrorCode(string response, string expectedCode)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetPropertyNames(root));
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("error").GetProperty("code").GetString());
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FixedDetailsProvider : IJobBridgeDetailsProvider
    {
        internal JobBridgeDetails Value { get; set; } =
            JobBridgeDetails.NotApplicable;

        public JobBridgeDetails GetDetails(
            EntityId<JobIdTag> jobId,
            EntityId<ProjectIdTag> projectId,
            JobBridgeKind kind,
            JobSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(jobId);
            ArgumentNullException.ThrowIfNull(projectId);
            ArgumentNullException.ThrowIfNull(snapshot);
            return kind == JobBridgeKind.MediaIngest
                ? Value
                : JobBridgeDetails.NotApplicable;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialValue) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => initialValue;
    }

    private sealed class JobBridgeFixture : IDisposable
    {
        private int jobSequence;

        internal JobBridgeFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.JobBridgeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            string databasePath = Path.Combine(RootPath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                ProjectId,
                "Job bridge test",
                "Verify a closed native bridge.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(databasePath).CreateProject(
                project,
                "{\"jobBridge\":true}",
                CreatedAt.AddDays(-1));
            Store = new JobStore(databasePath);
            var timeProvider = new ManualTimeProvider(StartedAt);
            var runner = new LocalJobRunner(Store, timeProvider);
            Coordinator = new JobCoordinator(Store, runner, timeProvider);
            Authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(ActorSid));
            Details = new FixedDetailsProvider();
            Handler = new JobBridgeRequestHandler(
                Coordinator,
                new JobBridgeAuthorization(Authorization, () => ActorSid),
                Details);
        }

        internal string RootPath { get; }

        private EntityId<ProjectIdTag> ProjectId { get; }

        internal JobStore Store { get; }

        internal JobCoordinator Coordinator { get; }

        internal AuthorizationService Authorization { get; }

        internal FixedDetailsProvider Details { get; }

        internal JobBridgeRequestHandler Handler { get; }

        internal Job CreateQueuedJob(string kind)
        {
            DateTimeOffset createdAt = CreatedAt.AddTicks(
                Interlocked.Increment(ref jobSequence));
            var job = new Job(
                EntityId<JobIdTag>.New(),
                ProjectId,
                kind,
                JobState.Queued,
                createdAt,
                createdAt,
                currentAttemptNumber: 0);
            Store.CreateJob(job);
            return job;
        }

        internal Job CreateTerminalJob(
            string kind,
            JobState terminalState,
            string errorCode)
        {
            Job job = CreateQueuedJob(kind);
            var runningAttempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                job.Id,
                sequenceNumber: 1,
                JobState.Running,
                job.UpdatedAt,
                StartedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(job.Id, 0, runningAttempt, StartedAt);
            DateTimeOffset completedAt = StartedAt.AddSeconds(1);
            var completedAttempt = new JobAttempt(
                runningAttempt.Id,
                runningAttempt.JobId,
                runningAttempt.SequenceNumber,
                terminalState,
                runningAttempt.QueuedAt,
                runningAttempt.StartedAt,
                completedAt,
                errorCode);
            _ = Store.CompleteAttempt(
                job.Id,
                expectedRevision: 1,
                completedAttempt,
                completedAt);
            return job;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}