using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Diagnostics;

namespace HubRadio.Infrastructure.Tests;

// FR-049 AC-024: job history files under the workspace "job" folder.
[TestClass]
public sealed class JobHistoryLogTests
{
    private static readonly DateTimeOffset CreatedAt = new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);

    // FR-049.2 FR-049.3 AC-024.1
    [TestMethod]
    public void EachCommittedTransitionRewritesTheJobFile()
    {
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, "9.8.7.6"));
        Job job = CreateJob(project.Id, CreatedAt);

        store.CreateJob(job);
        string expectedPath = Path.Combine(historyDirectory, $"job-20310203T040506Z-{job.Id}.json");
        Assert.IsTrue(File.Exists(expectedPath));
        Assert.AreEqual(expectedPath, Path.Combine(historyDirectory, JobHistoryLog.GetFileName(job)));
        using (JsonDocument created = ReadJson(expectedPath))
        {
            Assert.AreEqual("Queued", created.RootElement.GetProperty("job").GetProperty("state").GetString());
            Assert.AreEqual(0, created.RootElement.GetProperty("attempts").GetArrayLength());
        }

        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = store.StartAttempt(job.Id, 0, running, StartedAt);
        AssertJobState(expectedPath, "Running", started.Revision);

        byte[] beforeEvidence = File.ReadAllBytes(expectedPath);
        store.AppendProgress(CreateProgress(job.Id, running.Id, 1, StartedAt.AddSeconds(1), "phase.one", "first"));
        store.AppendHeartbeat(new JobHeartbeat(EntityId<JobHeartbeatIdTag>.New(), job.Id, running.Id, 1, StartedAt.AddSeconds(2)));
        CollectionAssert.AreEqual(beforeEvidence, File.ReadAllBytes(expectedPath));

        DateTimeOffset failedAt = StartedAt.AddSeconds(5);
        StoredJob failed = store.CompleteAttempt(
            job.Id,
            started.Revision,
            Complete(running, JobState.Failed, failedAt, "job.execution_failed"),
            failedAt);
        AssertJobState(expectedPath, "Failed", failed.Revision);
        using (JsonDocument failedDocument = ReadJson(expectedPath))
        {
            JsonElement attempt = failedDocument.RootElement.GetProperty("attempts")[0];
            Assert.AreEqual("Failed", attempt.GetProperty("state").GetString());
            Assert.AreEqual("job.execution_failed", attempt.GetProperty("errorCode").GetString());
            Assert.AreEqual("phase.one", attempt.GetProperty("lastProgress").GetProperty("phase").GetString());
            Assert.AreEqual(1, attempt.GetProperty("heartbeatCount").GetInt32());
        }

        int queuedRevision = store.QueueRetry(job.Id, failed.Revision, failedAt.AddSeconds(1));
        AssertJobState(expectedPath, "Queued", queuedRevision);
        using (JsonDocument queued = ReadJson(expectedPath))
        {
            Assert.AreEqual(1, queued.RootElement.GetProperty("attempts").GetArrayLength());
        }

        int cancelledRevision = store.CancelQueued(job.Id, queuedRevision, failedAt.AddSeconds(2));
        AssertJobState(expectedPath, "Cancelled", cancelledRevision);
        Assert.AreEqual(1, Directory.GetFiles(historyDirectory).Length);
        Assert.AreEqual(cancelledRevision, store.GetJob(job.Id)!.Revision);
    }

    // FR-049.4 AC-024.2
    [TestMethod]
    public void FileUsesTheDocumentedJsonContract()
    {
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, "1.2.3.4"));
        Job job = CreateJob(project.Id, CreatedAt);
        store.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = store.StartAttempt(job.Id, 0, running, StartedAt);
        store.AppendProgress(CreateProgress(job.Id, running.Id, 1, StartedAt.AddSeconds(1), "phase.one", null, completedUnits: 1));
        store.AppendProgress(CreateProgress(job.Id, running.Id, 1, StartedAt.AddSeconds(2), "phase.two", null, completedUnits: 4));
        store.AppendHeartbeat(new JobHeartbeat(EntityId<JobHeartbeatIdTag>.New(), job.Id, running.Id, 1, StartedAt.AddSeconds(3)));
        DateTimeOffset completedAt = StartedAt.AddTicks(123_456_789);
        _ = store.CompleteAttempt(job.Id, started.Revision, Complete(running, JobState.Succeeded, completedAt, null), completedAt);

        string path = Path.Combine(historyDirectory, JobHistoryLog.GetFileName(job));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        string text = Encoding.UTF8.GetString(bytes);
        Assert.IsFalse(text.Contains('\r'), "CR");
        StringAssert.StartsWith(text, "{\n  \"schema\": \"hubradio.job-history.v1\",\n  \"recordedAt\": ");

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(
            new[] { "schema", "recordedAt", "applicationVersion", "job", "attempts" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("1.2.3.4", root.GetProperty("applicationVersion").GetString());
        AssertTimestamp(root.GetProperty("recordedAt"));

        JsonElement jobElement = root.GetProperty("job");
        CollectionAssert.AreEqual(
            new[] { "id", "projectId", "kind", "state", "createdAt", "updatedAt", "revision", "currentAttemptNumber" },
            jobElement.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(job.Id.ToString(), jobElement.GetProperty("id").GetString());
        Assert.AreEqual(project.Id.ToString(), jobElement.GetProperty("projectId").GetString());
        Assert.AreEqual("render", jobElement.GetProperty("kind").GetString());
        Assert.AreEqual("Succeeded", jobElement.GetProperty("state").GetString());
        Assert.AreEqual("2031-02-03T04:05:06.0000000Z", jobElement.GetProperty("createdAt").GetString());
        Assert.AreEqual(1, jobElement.GetProperty("currentAttemptNumber").GetInt32());

        JsonElement attempt = root.GetProperty("attempts")[0];
        CollectionAssert.AreEqual(
            new[]
            {
                "sequenceNumber", "id", "state", "queuedAt", "startedAt", "completedAt", "durationMilliseconds",
                "errorCode", "exceptionType", "lastProgress", "progressCount", "heartbeatCount", "lastHeartbeatAt",
            },
            attempt.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(1, attempt.GetProperty("sequenceNumber").GetInt32());
        Assert.AreEqual(running.Id.ToString(), attempt.GetProperty("id").GetString());
        Assert.AreEqual(12_345, attempt.GetProperty("durationMilliseconds").GetInt64());
        Assert.AreEqual(JsonValueKind.Null, attempt.GetProperty("errorCode").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, attempt.GetProperty("exceptionType").ValueKind);
        AssertTimestamp(attempt.GetProperty("completedAt"));
        JsonElement lastProgress = attempt.GetProperty("lastProgress");
        Assert.AreEqual(4, lastProgress.GetProperty("completedUnits").GetInt64());
        Assert.AreEqual(10, lastProgress.GetProperty("totalUnits").GetInt64());
        Assert.AreEqual("phase.two", lastProgress.GetProperty("phase").GetString());
        Assert.AreEqual("2031-02-03T04:05:09.0000000Z", lastProgress.GetProperty("recordedAt").GetString());
        Assert.AreEqual(2, attempt.GetProperty("progressCount").GetInt32());
        Assert.AreEqual(1, attempt.GetProperty("heartbeatCount").GetInt32());
        Assert.AreEqual("2031-02-03T04:05:10.0000000Z", attempt.GetProperty("lastHeartbeatAt").GetString());
    }

    // FR-049.5 AC-024.3
    [TestMethod]
    public void FileNeverContainsProgressMessagesPathsOrExceptionText()
    {
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, null));
        Job job = CreateJob(project.Id, CreatedAt);
        store.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = store.StartAttempt(job.Id, 0, running, StartedAt);
        store.AppendProgress(CreateProgress(
            job.Id, running.Id, 1, StartedAt.AddSeconds(1), "phase.one", @"C:\Users\someone\secret.mp4"));
        store.History!.NoteExceptionType(running.Id, new IOException(@"C:\Users\someone\secret.mp4 is locked"));
        DateTimeOffset failedAt = StartedAt.AddSeconds(2);
        _ = store.CompleteAttempt(job.Id, started.Revision, Complete(running, JobState.Failed, failedAt, "job.execution_failed"), failedAt);

        string text = File.ReadAllText(Path.Combine(historyDirectory, JobHistoryLog.GetFileName(job)));
        Assert.IsFalse(text.Contains("\"message\"", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("someone", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("locked", StringComparison.OrdinalIgnoreCase));
        using JsonDocument document = JsonDocument.Parse(text);
        Assert.AreEqual("IOException", document.RootElement.GetProperty("attempts")[0].GetProperty("exceptionType").GetString());
        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("applicationVersion").ValueKind);
    }

    // FR-049.5 AC-024.3
    [TestMethod]
    public void ExceptionTypeIsWrittenOnlyForRunnerFailureCodes()
    {
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, null));
        Job job = CreateJob(project.Id, CreatedAt);
        store.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = store.StartAttempt(job.Id, 0, running, StartedAt);
        store.History!.NoteExceptionType(running.Id, new InvalidOperationException("ignored"));
        DateTimeOffset interruptedAt = StartedAt.AddSeconds(2);
        _ = store.CompleteAttempt(
            job.Id, started.Revision, Complete(running, JobState.Interrupted, interruptedAt, "job.app_restarted"), interruptedAt);

        using JsonDocument document = ReadJson(Path.Combine(historyDirectory, JobHistoryLog.GetFileName(job)));
        Assert.AreEqual(
            JsonValueKind.Null,
            document.RootElement.GetProperty("attempts")[0].GetProperty("exceptionType").ValueKind);
    }

    // FR-049.6 AC-024.4
    [TestMethod]
    public void WriteFailuresNeverChangeJobOperations()
    {
        using StoreTestDatabase database = new();
        string historyPath = Path.Combine(database.RootPath, "job");
        File.WriteAllText(historyPath, "not a folder");
        (JobStore blockedStore, Project project) = CreateStores(database, new JobHistoryLog(historyPath, null));
        Job job = CreateJob(project.Id, CreatedAt);

        blockedStore.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = blockedStore.StartAttempt(job.Id, 0, running, StartedAt);
        DateTimeOffset completedAt = StartedAt.AddSeconds(1);
        StoredJob completed = blockedStore.CompleteAttempt(
            job.Id, started.Revision, Complete(running, JobState.Succeeded, completedAt, null), completedAt);
        Assert.AreEqual(JobState.Succeeded, completed.Job.State);
        Assert.AreEqual("not a folder", File.ReadAllText(historyPath));

        string otherDirectory = Path.Combine(database.RootPath, "other", "job");
        var store = new JobStore(database.DatabasePath, new JobHistoryLog(otherDirectory, null));
        Job second = CreateJob(project.Id, CreatedAt.AddMinutes(1));
        store.CreateJob(second);
        _ = Assert.ThrowsExactly<StoreConcurrencyException>(() => store.StartAttempt(
            second.Id,
            expectedRevision: 5,
            CreateRunningAttempt(second.Id, 1, second.CreatedAt, second.CreatedAt.AddSeconds(1)),
            second.CreatedAt.AddSeconds(1)));
        AssertJobState(Path.Combine(otherDirectory, JobHistoryLog.GetFileName(second)), "Queued", 0);
    }

    // FR-049.7 AC-024.5
    [TestMethod]
    public void ConcurrentJobsEachEndWithTheirCommittedStateAndNoTemporaryFiles()
    {
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, null));
        Job[] jobs = Enumerable.Range(0, 8)
            .Select(index => CreateJob(project.Id, CreatedAt.AddSeconds(index)))
            .ToArray();

        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, job =>
        {
            store.CreateJob(job);
            DateTimeOffset startedAt = job.CreatedAt.AddSeconds(1);
            JobAttempt running = CreateRunningAttempt(job.Id, 1, job.CreatedAt, startedAt);
            StoredJob started = store.StartAttempt(job.Id, 0, running, startedAt);
            DateTimeOffset completedAt = startedAt.AddSeconds(1);
            _ = store.CompleteAttempt(job.Id, started.Revision, Complete(running, JobState.Succeeded, completedAt, null), completedAt);
        });

        Assert.AreEqual(0, Directory.GetFiles(historyDirectory, "*.tmp").Length);
        Assert.AreEqual(8, Directory.GetFiles(historyDirectory, "job-*.json").Length);
        foreach (Job job in jobs)
        {
            StoredJob stored = store.GetJob(job.Id)!;
            AssertJobState(Path.Combine(historyDirectory, JobHistoryLog.GetFileName(job)), stored.Job.State.ToString(), stored.Revision);
        }
    }

    // FR-049.8 AC-024.6
    [TestMethod]
    public void RetentionDeletesTheOldestFilesAndKeepsUnrelatedFiles()
    {
        Assert.AreEqual(1000, new JobHistoryLog(Path.Combine(Path.GetTempPath(), "unused-job"), null).MaxFiles);
        using StoreTestDatabase database = new();
        string historyDirectory = Path.Combine(database.RootPath, "job");
        Directory.CreateDirectory(historyDirectory);
        string unrelated = Path.Combine(historyDirectory, "notes.txt");
        File.WriteAllText(unrelated, "keep");
        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(historyDirectory, null, maxFiles: 3));
        Job[] jobs = Enumerable.Range(0, 5)
            .Select(index => CreateJob(project.Id, CreatedAt.AddMinutes(index)))
            .ToArray();

        foreach (Job job in jobs)
        {
            store.CreateJob(job);
        }

        string[] remaining = Directory.GetFiles(historyDirectory, "job-*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            jobs.Skip(2).Select(JobHistoryLog.GetFileName).ToArray(),
            remaining);
        Assert.IsTrue(File.Exists(unrelated));
    }

    // FR-049.1 AC-024.7
    [TestMethod]
    public void HistoryIsNotWrittenThroughALinkedFolder()
    {
        using StoreTestDatabase database = new();
        string target = Path.Combine(database.RootPath, "elsewhere");
        Directory.CreateDirectory(target);
        string link = Path.Combine(database.RootPath, "job");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
            return;
        }

        (JobStore store, Project project) = CreateStores(database, new JobHistoryLog(link, null));
        Job job = CreateJob(project.Id, CreatedAt);
        store.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1, CreatedAt, StartedAt);
        StoredJob started = store.StartAttempt(job.Id, 0, running, StartedAt);
        Assert.AreEqual(JobState.Running, started.Job.State);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length);
    }

    private static void AssertJobState(string path, string state, int revision)
    {
        using JsonDocument document = ReadJson(path);
        JsonElement job = document.RootElement.GetProperty("job");
        Assert.AreEqual(state, job.GetProperty("state").GetString());
        Assert.AreEqual(revision, job.GetProperty("revision").GetInt32());
    }

    private static void AssertTimestamp(JsonElement value)
    {
        StringAssert.Matches(value.GetString(), new System.Text.RegularExpressions.Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$"));
    }

    private static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static (JobStore Store, Project Project) CreateStores(StoreTestDatabase database, JobHistoryLog history)
    {
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = new(
            EntityId<ProjectIdTag>.New(),
            "Job history project",
            "Exercise job history.",
            "Test suite",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        projectStore.CreateProject(project, "{\"project\":true}", CreatedAt);
        return (new JobStore(database.DatabasePath, history), project);
    }

    private static Job CreateJob(EntityId<ProjectIdTag> projectId, DateTimeOffset createdAt) =>
        new(EntityId<JobIdTag>.New(), projectId, "render", JobState.Queued, createdAt, createdAt, currentAttemptNumber: 0);

    private static JobAttempt CreateRunningAttempt(
        EntityId<JobIdTag> jobId,
        int sequenceNumber,
        DateTimeOffset queuedAt,
        DateTimeOffset startedAt) =>
        new(EntityId<JobAttemptIdTag>.New(), jobId, sequenceNumber, JobState.Running, queuedAt, startedAt, null, null);

    private static JobAttempt Complete(
        JobAttempt running,
        JobState state,
        DateTimeOffset completedAt,
        string? errorCode) =>
        new(running.Id, running.JobId, running.SequenceNumber, state, running.QueuedAt, running.StartedAt, completedAt, errorCode);

    private static JobProgress CreateProgress(
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt,
        string phase,
        string? message,
        long completedUnits = 0) =>
        new(EntityId<JobProgressIdTag>.New(), jobId, attemptId, sequenceNumber, recordedAt, completedUnits, 10, phase, message);
}
