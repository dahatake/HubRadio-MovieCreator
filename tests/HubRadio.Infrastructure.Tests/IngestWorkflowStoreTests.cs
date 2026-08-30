using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class IngestWorkflowStoreTests
{
    private const string RequestSchemaVersion = "media-ingest-workflow-v1";
    private const string CheckpointSchemaVersion = "media-ingest-checkpoint-v1";

    private static readonly DateTimeOffset CreatedAt =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);

    [TestMethod]
    public void RegisterOrGetPersistsCanonicalRequestAndInitialCheckpointIdempotently()
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        string nonCanonicalRequest = CreateRequestJson(fixture.Job.Id, indented: true);
        string canonicalRequest = Canonicalize(nonCanonicalRequest);
        ContentDigest requestDigest = Digest(canonicalRequest);
        string initialCheckpoint = CreateCheckpointJson(stage: 0, marker: "initial");

        StoredIngestWorkflow registered = fixture.Store.RegisterOrGet(
            fixture.Job.Id,
            fixture.Project.Id,
            RequestSchemaVersion,
            requestDigest,
            nonCanonicalRequest,
            CheckpointSchemaVersion,
            initialCheckpoint);
        var reopenedStore = new IngestWorkflowStore(database.DatabasePath);
        StoredIngestWorkflow reopened = reopenedStore.RegisterOrGet(
            fixture.Job.Id,
            fixture.Project.Id,
            RequestSchemaVersion,
            requestDigest,
            nonCanonicalRequest,
            CheckpointSchemaVersion,
            CreateCheckpointJson(stage: 0, marker: "ignored-after-registration"));

        Assert.AreEqual(fixture.Job.Id, registered.JobId);
        Assert.AreEqual(fixture.Project.Id, registered.ProjectId);
        Assert.AreEqual(RequestSchemaVersion, registered.RequestSchemaVersion);
        Assert.AreEqual(requestDigest, registered.RequestDigest);
        Assert.AreEqual(canonicalRequest, registered.RequestPayloadJson);
        Assert.AreEqual(0, registered.CheckpointStage);
        Assert.AreEqual(0, registered.CheckpointAttemptNumber);
        Assert.AreEqual(CheckpointSchemaVersion, registered.CheckpointSchemaVersion);
        Assert.AreEqual(Canonicalize(initialCheckpoint), registered.CheckpointPayloadJson);
        Assert.AreEqual(Digest(initialCheckpoint), registered.CheckpointDigest);
        Assert.AreEqual(0, registered.Revision);
        Assert.AreEqual(CreatedAt, registered.UpdatedAt);
        AssertWorkflowEqual(registered, reopened);
        AssertWorkflowEqual(
            registered,
            reopenedStore.Get(fixture.Job.Id)
                ?? throw new AssertFailedException("The workflow was not reopened."));
        Assert.IsNull(reopenedStore.Get(EntityId<JobIdTag>.New()));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM ingest_workflows;"));
    }

    [TestMethod]
    public void RegisterOrGetRejectsDigestSchemaProjectAndRequestMismatches()
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        string requestJson = CreateRequestJson(fixture.Job.Id);
        ContentDigest requestDigest = Digest(requestJson);

        Assert.ThrowsExactly<ArgumentException>(
            () => fixture.Store.RegisterOrGet(
                fixture.Job.Id,
                fixture.Project.Id,
                RequestSchemaVersion,
                Digest("{}"),
                requestJson,
                CheckpointSchemaVersion,
                CreateCheckpointJson(0, "initial")));
        Assert.ThrowsExactly<ArgumentException>(
            () => fixture.Store.RegisterOrGet(
                fixture.Job.Id,
                fixture.Project.Id,
                " request-v1",
                requestDigest,
                requestJson,
                CheckpointSchemaVersion,
                CreateCheckpointJson(0, "initial")));
        Assert.ThrowsExactly<ArgumentException>(
            () => fixture.Store.RegisterOrGet(
                fixture.Job.Id,
                fixture.Project.Id,
                RequestSchemaVersion,
                requestDigest,
                "{duplicate:invalid}",
                CheckpointSchemaVersion,
                CreateCheckpointJson(0, "initial")));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Store.RegisterOrGet(
                fixture.Job.Id,
                EntityId<ProjectIdTag>.New(),
                RequestSchemaVersion,
                requestDigest,
                requestJson,
                CheckpointSchemaVersion,
                CreateCheckpointJson(0, "initial")));

        StoredIngestWorkflow registered = Register(fixture);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Store.RegisterOrGet(
                fixture.Job.Id,
                fixture.Project.Id,
                RequestSchemaVersion,
                Digest("{\"other\":true}"),
                "{\"other\":true}",
                CheckpointSchemaVersion,
                CreateCheckpointJson(0, "initial")));
        Assert.AreEqual(0, registered.Revision);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM ingest_workflows;"));
    }

    [TestMethod]
    public void AdvanceCheckpointRequiresExactRunningAttemptAndPersistsAcrossStores()
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        StoredIngestWorkflow initial = Register(fixture);
        JobAttempt firstAttempt = StartAttempt(fixture.JobStore, fixture.Job, 1, StartedAt);
        string stageOneJson = CreateCheckpointJson(1, "copied");

        StoredIngestWorkflow stageOne = fixture.Store.AdvanceCheckpoint(
            fixture.Job.Id,
            initial.RequestDigest,
            initial.Revision,
            completedStage: 1,
            attemptNumber: 1,
            CheckpointSchemaVersion,
            stageOneJson,
            StartedAt);

        Assert.AreEqual(1, stageOne.CheckpointStage);
        Assert.AreEqual(1, stageOne.CheckpointAttemptNumber);
        Assert.AreEqual(1, stageOne.Revision);
        Assert.AreEqual(Canonicalize(stageOneJson), stageOne.CheckpointPayloadJson);
        Assert.AreEqual(Digest(stageOneJson), stageOne.CheckpointDigest);
        Assert.AreEqual(StartedAt, stageOne.UpdatedAt);
        AssertWorkflowEqual(
            stageOne,
            new IngestWorkflowStore(database.DatabasePath).Get(fixture.Job.Id)
                ?? throw new AssertFailedException("The checkpoint was not reopened."));

        DateTimeOffset failedAt = StartedAt.AddSeconds(1);
        _ = fixture.JobStore.CompleteAttempt(
            fixture.Job.Id,
            expectedRevision: 1,
            new JobAttempt(
                firstAttempt.Id,
                firstAttempt.JobId,
                firstAttempt.SequenceNumber,
                JobState.Failed,
                firstAttempt.QueuedAt,
                firstAttempt.StartedAt,
                failedAt,
                "job.execution_timed_out"),
            failedAt);
        _ = fixture.JobStore.QueueRetry(
            fixture.Job.Id,
            expectedRevision: 2,
            failedAt.AddSeconds(1));
        DateTimeOffset retryStartedAt = failedAt.AddSeconds(2);
        _ = StartAttempt(
            fixture.JobStore,
            fixture.Job,
            sequenceNumber: 2,
            retryStartedAt,
            expectedJobRevision: 3);

        var resumedStore = new IngestWorkflowStore(database.DatabasePath);
        StoredIngestWorkflow stageTwo = resumedStore.AdvanceCheckpoint(
            fixture.Job.Id,
            stageOne.RequestDigest,
            stageOne.Revision,
            completedStage: 2,
            attemptNumber: 2,
            CheckpointSchemaVersion,
            CreateCheckpointJson(2, "probed"),
            retryStartedAt);

        Assert.AreEqual(2, stageTwo.CheckpointStage);
        Assert.AreEqual(2, stageTwo.CheckpointAttemptNumber);
        Assert.AreEqual(2, stageTwo.Revision);
    }

    [TestMethod]
    public void AdvanceCheckpointRejectsSkippedStaleWrongAttemptAndReverseTimeWithoutMutation()
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        StoredIngestWorkflow initial = Register(fixture);
        _ = StartAttempt(fixture.JobStore, fixture.Job, 1, StartedAt);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Store.AdvanceCheckpoint(
                fixture.Job.Id,
                initial.RequestDigest,
                initial.Revision,
                completedStage: 2,
                attemptNumber: 1,
                CheckpointSchemaVersion,
                CreateCheckpointJson(2, "skipped"),
                StartedAt));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Store.AdvanceCheckpoint(
                fixture.Job.Id,
                initial.RequestDigest,
                initial.Revision,
                completedStage: 1,
                attemptNumber: 2,
                CheckpointSchemaVersion,
                CreateCheckpointJson(1, "wrong-attempt"),
                StartedAt));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => fixture.Store.AdvanceCheckpoint(
                fixture.Job.Id,
                initial.RequestDigest,
                initial.Revision,
                completedStage: 1,
                attemptNumber: 1,
                CheckpointSchemaVersion,
                CreateCheckpointJson(1, "reverse-time"),
                CreatedAt.AddTicks(-1)));

        StoredIngestWorkflow stageOne = fixture.Store.AdvanceCheckpoint(
            fixture.Job.Id,
            initial.RequestDigest,
            initial.Revision,
            completedStage: 1,
            attemptNumber: 1,
            CheckpointSchemaVersion,
            CreateCheckpointJson(1, "valid"),
            StartedAt);
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => fixture.Store.AdvanceCheckpoint(
                fixture.Job.Id,
                stageOne.RequestDigest,
                expectedRevision: 0,
                completedStage: 2,
                attemptNumber: 1,
                CheckpointSchemaVersion,
                CreateCheckpointJson(2, "stale"),
                StartedAt.AddTicks(1)));
        Assert.AreEqual("ingest workflow", stale.EntityKind);
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);

        StoredIngestWorkflow unchanged = fixture.Store.Get(fixture.Job.Id)!;
        Assert.AreEqual(1, unchanged.CheckpointStage);
        Assert.AreEqual("valid", ReadMarker(unchanged.CheckpointPayloadJson));
    }

    [TestMethod]
    public async Task ConcurrentAdvanceAcrossStoresAllowsExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        StoredIngestWorkflow initial = Register(fixture);
        _ = StartAttempt(fixture.JobStore, fixture.Job, 1, StartedAt);
        var otherStore = new IngestWorkflowStore(database.DatabasePath);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryAdvance(fixture.Store, fixture.Job.Id, initial, "first", release));
        Task<bool> second = Task.Run(
            () => TryAdvance(otherStore, fixture.Job.Id, initial, "second", release));
        release.Set();
        bool[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(static won => won));
        StoredIngestWorkflow stored = fixture.Store.Get(fixture.Job.Id)!;
        Assert.AreEqual(1, stored.CheckpointStage);
        Assert.AreEqual(1, stored.Revision);
        Assert.IsTrue(ReadMarker(stored.CheckpointPayloadJson) is "first" or "second");
    }

    [TestMethod]
    public void RegisterAfterAttemptStartedAndAdvanceOutsideRunningStateAreRejected()
    {
        using StoreTestDatabase database = new();
        StoreFixture unregistered = CreateFixture(database);
        _ = StartAttempt(unregistered.JobStore, unregistered.Job, 1, StartedAt);
        Assert.ThrowsExactly<InvalidOperationException>(() => Register(unregistered));

        StoreFixture registered = CreateFixture(database);
        StoredIngestWorkflow initial = Register(registered);
        JobAttempt running = StartAttempt(registered.JobStore, registered.Job, 1, StartedAt);
        DateTimeOffset completedAt = StartedAt.AddSeconds(1);
        _ = registered.JobStore.CompleteAttempt(
            registered.Job.Id,
            expectedRevision: 1,
            new JobAttempt(
                running.Id,
                running.JobId,
                running.SequenceNumber,
                JobState.Failed,
                running.QueuedAt,
                running.StartedAt,
                completedAt,
                "job.execution_timed_out"),
            completedAt);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => registered.Store.AdvanceCheckpoint(
                registered.Job.Id,
                initial.RequestDigest,
                initial.Revision,
                completedStage: 1,
                attemptNumber: 1,
                CheckpointSchemaVersion,
                CreateCheckpointJson(1, "late"),
                completedAt));
    }

    [TestMethod]
    [DataRow("request-digest")]
    [DataRow("request-payload")]
    [DataRow("checkpoint-digest")]
    [DataRow("checkpoint-payload")]
    [DataRow("malformed-utf8")]
    [DataRow("timestamp")]
    public void GetFailsClosedForStoredPayloadDigestUtf8AndTimestampCorruption(string mutation)
    {
        using StoreTestDatabase database = new();
        StoreFixture fixture = CreateFixture(database);
        _ = Register(fixture);

        switch (mutation)
        {
            case "request-digest":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET request_digest = $value;",
                    ("$value", new string('a', 64)));
                break;
            case "request-payload":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET request_payload = $value;",
                    ("$value", Encoding.UTF8.GetBytes("{ \"changed\": true }")));
                break;
            case "checkpoint-digest":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET checkpoint_digest = $value;",
                    ("$value", new string('b', 64)));
                break;
            case "checkpoint-payload":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET checkpoint_payload = $value;",
                    ("$value", Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
                break;
            case "malformed-utf8":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET checkpoint_payload = X'80';");
                break;
            case "timestamp":
                database.ExecuteNonQuery(
                    "UPDATE ingest_workflows SET updated_at = 'not-a-timestamp';");
                break;
            default:
                throw new AssertFailedException("Unknown corruption mutation.");
        }

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Store.Get(fixture.Job.Id));
        Assert.AreEqual("Stored ingest workflow data is invalid.", exception.Message);
    }

    private static StoreFixture CreateFixture(StoreTestDatabase database)
    {
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = new(
            EntityId<ProjectIdTag>.New(),
            "Ingest workflow store project",
            "Exercise durable ingest checkpoints.",
            "Test suite",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        projectStore.CreateProject(project, "{\"project\":true}", CreatedAt);
        var jobStore = new JobStore(database.DatabasePath);
        var job = new Job(
            EntityId<JobIdTag>.New(),
            project.Id,
            "media.ingest.v1",
            JobState.Queued,
            CreatedAt,
            CreatedAt,
            currentAttemptNumber: 0);
        jobStore.CreateJob(job);
        return new StoreFixture(
            project,
            job,
            jobStore,
            new IngestWorkflowStore(database.DatabasePath));
    }

    private static StoredIngestWorkflow Register(StoreFixture fixture)
    {
        string requestJson = CreateRequestJson(fixture.Job.Id);
        return fixture.Store.RegisterOrGet(
            fixture.Job.Id,
            fixture.Project.Id,
            RequestSchemaVersion,
            Digest(requestJson),
            requestJson,
            CheckpointSchemaVersion,
            CreateCheckpointJson(0, "initial"));
    }

    private static JobAttempt StartAttempt(
        JobStore jobStore,
        Job job,
        int sequenceNumber,
        DateTimeOffset startedAt,
        int expectedJobRevision = 0)
    {
        StoredJob current = jobStore.GetJob(job.Id)!;
        var attempt = new JobAttempt(
            EntityId<JobAttemptIdTag>.New(),
            job.Id,
            sequenceNumber,
            JobState.Running,
            current.Job.UpdatedAt,
            startedAt,
            completedAt: null,
            errorCode: null);
        _ = jobStore.StartAttempt(
            job.Id,
            expectedJobRevision,
            attempt,
            startedAt);
        return attempt;
    }

    private static bool TryAdvance(
        IngestWorkflowStore store,
        EntityId<JobIdTag> jobId,
        StoredIngestWorkflow initial,
        string marker,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.AdvanceCheckpoint(
                jobId,
                initial.RequestDigest,
                initial.Revision,
                completedStage: 1,
                attemptNumber: 1,
                CheckpointSchemaVersion,
                CreateCheckpointJson(1, marker),
                StartedAt);
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static string CreateRequestJson(
        EntityId<JobIdTag> jobId,
        bool indented = false) =>
        indented
            ? $"{{\n  \"schemaVersion\": \"{RequestSchemaVersion}\",\n  \"jobId\": \"{jobId}\"\n}}"
            : $"{{\"jobId\":\"{jobId}\",\"schemaVersion\":\"{RequestSchemaVersion}\"}}";

    private static string CreateCheckpointJson(int stage, string marker) =>
        $"{{\"marker\":\"{marker}\",\"schemaVersion\":\"{CheckpointSchemaVersion}\",\"stage\":{stage}}}";

    private static string ReadMarker(string checkpointJson)
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(checkpointJson);
        return document.RootElement.GetProperty("marker").GetString()
            ?? throw new AssertFailedException("The checkpoint marker was null.");
    }

    private static string Canonicalize(string json)
    {
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(Encoding.UTF8.GetBytes(json));
        return Encoding.UTF8.GetString(canonical);
    }

    private static ContentDigest Digest(string json) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(Canonicalize(json)));

    private static void AssertWorkflowEqual(
        StoredIngestWorkflow expected,
        StoredIngestWorkflow actual)
    {
        Assert.AreEqual(expected.JobId, actual.JobId);
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.RequestSchemaVersion, actual.RequestSchemaVersion);
        Assert.AreEqual(expected.RequestDigest, actual.RequestDigest);
        Assert.AreEqual(expected.RequestPayloadJson, actual.RequestPayloadJson);
        Assert.AreEqual(expected.CheckpointStage, actual.CheckpointStage);
        Assert.AreEqual(
            expected.CheckpointAttemptNumber,
            actual.CheckpointAttemptNumber);
        Assert.AreEqual(
            expected.CheckpointSchemaVersion,
            actual.CheckpointSchemaVersion);
        Assert.AreEqual(expected.CheckpointDigest, actual.CheckpointDigest);
        Assert.AreEqual(expected.CheckpointPayloadJson, actual.CheckpointPayloadJson);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
    }

    private sealed record StoreFixture(
        Project Project,
        Job Job,
        JobStore JobStore,
        IngestWorkflowStore Store);
}
