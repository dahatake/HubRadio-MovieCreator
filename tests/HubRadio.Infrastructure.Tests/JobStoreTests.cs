using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class JobStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(10);

    [TestMethod]
    public void CreateAndGetJobPreservesInitialSnapshotAndRevision()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id, kind: "render-'quoted'");

        store.CreateJob(job);

        StoredJob stored = store.GetJob(job.Id)
            ?? throw new AssertFailedException("The job was not loaded.");
        AssertJobEqual(job, stored.Job);
        Assert.AreEqual(0, stored.Revision);
        Assert.IsNull(store.GetJob(EntityId<JobIdTag>.New()));
    }

    [TestMethod]
    public void CreateJobRejectsNonInitialStateAndForeignKeyOrDuplicateRollsBack()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job initial = CreateJob(project.Id);
        store.CreateJob(initial);

        Assert.ThrowsExactly<ArgumentException>(
            () => store.CreateJob(
                new Job(
                    EntityId<JobIdTag>.New(),
                    project.Id,
                    "running",
                    JobState.Running,
                    CreatedAt,
                    StartedAt,
                    currentAttemptNumber: 1)));
        Assert.ThrowsExactly<ArgumentException>(
            () => store.CreateJob(
                new Job(
                    EntityId<JobIdTag>.New(),
                    project.Id,
                    "retry",
                    JobState.Queued,
                    CreatedAt,
                    CreatedAt,
                    currentAttemptNumber: 1)));

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateJob(CreateJob(project.Id, id: initial.Id)));
        Assert.AreEqual(19, duplicate.SqliteErrorCode);
        SqliteException foreignKey = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateJob(CreateJob(EntityId<ProjectIdTag>.New())));
        Assert.AreEqual(19, foreignKey.SqliteErrorCode);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void StartAttemptAtomicallyInsertsAttemptAndAdvancesJob()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt attempt = CreateRunningAttempt(job.Id, sequenceNumber: 1);

        StoredJob started = store.StartAttempt(
            job.Id,
            expectedRevision: 0,
            attempt,
            StartedAt);

        Assert.AreEqual(1, started.Revision);
        Assert.AreEqual(JobState.Running, started.Job.State);
        Assert.AreEqual(1, started.Job.CurrentAttemptNumber);
        Assert.AreEqual(StartedAt, started.Job.UpdatedAt);
        StoredJobAggregate aggregate = store.GetJobAggregate(job.Id)
            ?? throw new AssertFailedException("The aggregate was not loaded.");
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(StartedAt, aggregate.Job.UpdatedAt);
        Assert.AreEqual(1, aggregate.Revision);
        Assert.AreEqual(1, aggregate.Attempts.Count);
        AssertAttemptEqual(attempt, aggregate.Attempts[0]);
        Assert.AreEqual(0, aggregate.Progress.Count);
        Assert.AreEqual(0, aggregate.Heartbeats.Count);
    }

    [TestMethod]
    public void FailedAttemptInsertRollsBackJobTransitionAtomically()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job firstJob = CreateJob(project.Id);
        Job secondJob = CreateJob(project.Id);
        store.CreateJob(firstJob);
        store.CreateJob(secondJob);
        EntityId<JobAttemptIdTag> duplicateAttemptId = EntityId<JobAttemptIdTag>.New();
        _ = store.StartAttempt(
            firstJob.Id,
            0,
            CreateRunningAttempt(firstJob.Id, 1, duplicateAttemptId),
            StartedAt);

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.StartAttempt(
                secondJob.Id,
                0,
                CreateRunningAttempt(secondJob.Id, 1, duplicateAttemptId),
                StartedAt));
        Assert.AreEqual(19, duplicate.SqliteErrorCode);

        StoredJob secondStored = store.GetJob(secondJob.Id)!;
        Assert.AreEqual(JobState.Queued, secondStored.Job.State);
        Assert.AreEqual(0, secondStored.Job.CurrentAttemptNumber);
        Assert.AreEqual(0, secondStored.Revision);
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM job_attempts WHERE job_id = $job_id;",
                ("$job_id", secondJob.Id.ToString())));
    }

    [TestMethod]
    public async Task ConcurrentStartAcrossStoresAllowsExactlyOneExpectedRevisionWinner()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore firstStore, Project project) = CreateStores(database);
        var secondStore = new JobStore(database.DatabasePath);
        Job job = CreateJob(project.Id);
        firstStore.CreateJob(job);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryStartAttempt(
                firstStore,
                job.Id,
                CreateRunningAttempt(job.Id, 1),
                release));
        Task<bool> second = Task.Run(
            () => TryStartAttempt(
                secondStore,
                job.Id,
                CreateRunningAttempt(job.Id, 1),
                release));
        release.Set();
        bool[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(static won => won));
        StoredJobAggregate aggregate = firstStore.GetJobAggregate(job.Id)!;
        Assert.AreEqual(1, aggregate.Revision);
        Assert.AreEqual(1, aggregate.Attempts.Count);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);

        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => secondStore.StartAttempt(
                job.Id,
                0,
                CreateRunningAttempt(job.Id, 2),
                StartedAt.AddSeconds(1)));
        Assert.AreEqual("job", stale.EntityKind);
        Assert.AreEqual(job.Id.ToString(), stale.EntityId);
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);
    }

    [TestMethod]
    public void CompleteAttemptAcceptsAllRunningTerminalOutcomes()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        foreach (JobState terminalState in new[]
        {
            JobState.Succeeded,
            JobState.Failed,
            JobState.Cancelled,
            JobState.Interrupted,
        })
        {
            Job job = CreateJob(project.Id, kind: terminalState.ToString());
            store.CreateJob(job);
            JobAttempt running = CreateRunningAttempt(job.Id, 1);
            _ = store.StartAttempt(job.Id, 0, running, StartedAt);
            JobAttempt completedAttempt = CreateCompletedAttempt(running, terminalState);

            StoredJob completedJob = store.CompleteAttempt(
                job.Id,
                expectedRevision: 1,
                completedAttempt,
                CompletedAt);

            StoredJobAggregate aggregate = store.GetJobAggregate(job.Id)!;
            Assert.AreEqual(2, completedJob.Revision);
            Assert.AreEqual(terminalState, completedJob.Job.State);
            Assert.AreEqual(2, aggregate.Revision);
            Assert.AreEqual(terminalState, aggregate.Job.State);
            Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
            AssertAttemptEqual(completedAttempt, aggregate.Attempts[0]);
        }
    }

    [TestMethod]
    public void FailedAndInterruptedJobsRetryWithoutChangingAttemptNumber()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        foreach (JobState retryState in new[] { JobState.Failed, JobState.Interrupted })
        {
            Job job = CreateJob(project.Id, kind: retryState.ToString());
            store.CreateJob(job);
            JobAttempt firstAttempt = CreateRunningAttempt(job.Id, 1);
            _ = store.StartAttempt(job.Id, 0, firstAttempt, StartedAt);
            _ = store.CompleteAttempt(
                job.Id,
                1,
                CreateCompletedAttempt(firstAttempt, retryState),
                CompletedAt);

            Assert.AreEqual(
                3,
                store.QueueRetry(job.Id, 2, CompletedAt.AddSeconds(1)));
            StoredJob queued = store.GetJob(job.Id)!;
            Assert.AreEqual(JobState.Queued, queued.Job.State);
            Assert.AreEqual(1, queued.Job.CurrentAttemptNumber);
            Assert.AreEqual(3, queued.Revision);

            JobAttempt retry = CreateRunningAttempt(
                job.Id,
                2,
                queuedAt: CompletedAt.AddSeconds(2),
                startedAt: CompletedAt.AddSeconds(3));
            Assert.AreEqual(
                4,
                store.StartAttempt(job.Id, 3, retry, CompletedAt.AddSeconds(3)).Revision);
            Assert.AreEqual(2, store.GetJobAggregate(job.Id)!.Attempts.Count);
        }
    }

    [TestMethod]
    public void CancelQueuedAllowsBeforeStartAndAfterRetryWithoutInsertingAttempt()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job initial = CreateJob(project.Id, kind: "cancel-initial");
        store.CreateJob(initial);

        Assert.AreEqual(1, store.CancelQueued(initial.Id, 0, CreatedAt.AddSeconds(1)));
        StoredJobAggregate cancelledInitial = store.GetJobAggregate(initial.Id)!;
        Assert.AreEqual(JobState.Cancelled, cancelledInitial.Job.State);
        Assert.AreEqual(0, cancelledInitial.Job.CurrentAttemptNumber);
        Assert.AreEqual(0, cancelledInitial.Attempts.Count);

        Job retryJob = CreateJob(project.Id, kind: "cancel-retry");
        store.CreateJob(retryJob);
        JobAttempt attempt = CreateRunningAttempt(retryJob.Id, 1);
        _ = store.StartAttempt(retryJob.Id, 0, attempt, StartedAt);
        _ = store.CompleteAttempt(
            retryJob.Id,
            1,
            CreateCompletedAttempt(attempt, JobState.Failed),
            CompletedAt);
        _ = store.QueueRetry(retryJob.Id, 2, CompletedAt.AddSeconds(1));

        Assert.AreEqual(
            4,
            store.CancelQueued(retryJob.Id, 3, CompletedAt.AddSeconds(2)));
        StoredJobAggregate cancelledRetry = store.GetJobAggregate(retryJob.Id)!;
        Assert.AreEqual(JobState.Cancelled, cancelledRetry.Job.State);
        Assert.AreEqual(1, cancelledRetry.Job.CurrentAttemptNumber);
        Assert.AreEqual(1, cancelledRetry.Attempts.Count);
        Assert.AreEqual(JobState.Failed, cancelledRetry.Attempts[0].State);
    }

    [TestMethod]
    public void TerminalAndInvalidTransitionsAreRejectedWithoutMutation()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt attempt = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, attempt, StartedAt);
        _ = store.CompleteAttempt(
            job.Id,
            1,
            CreateCompletedAttempt(attempt, JobState.Succeeded),
            CompletedAt);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.QueueRetry(job.Id, 2, CompletedAt.AddSeconds(1)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.CancelQueued(job.Id, 2, CompletedAt.AddSeconds(1)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.StartAttempt(
                job.Id,
                2,
                CreateRunningAttempt(
                    job.Id,
                    2,
                    queuedAt: CompletedAt.AddSeconds(2),
                    startedAt: CompletedAt.AddSeconds(3)),
                CompletedAt.AddSeconds(3)));

        StoredJobAggregate unchanged = store.GetJobAggregate(job.Id)!;
        Assert.AreEqual(JobState.Succeeded, unchanged.Job.State);
        Assert.AreEqual(2, unchanged.Revision);
        Assert.AreEqual(1, unchanged.Attempts.Count);
    }

    [TestMethod]
    public void ImmutableAttemptTimestampMismatchAndReverseJobTimeRollBackCompletion()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt running = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, running, StartedAt);
        JobAttempt mismatched = new(
            running.Id,
            running.JobId,
            running.SequenceNumber,
            JobState.Failed,
            running.QueuedAt.AddTicks(1),
            running.StartedAt,
            CompletedAt,
            "worker.failed");

        Assert.ThrowsExactly<ArgumentException>(
            () => store.CompleteAttempt(job.Id, 1, mismatched, CompletedAt));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.CompleteAttempt(
                job.Id,
                1,
                CreateCompletedAttempt(running, JobState.Failed),
                StartedAt.AddTicks(-1)));

        StoredJobAggregate unchanged = store.GetJobAggregate(job.Id)!;
        Assert.AreEqual(JobState.Running, unchanged.Job.State);
        Assert.AreEqual(1, unchanged.Revision);
        Assert.AreEqual(JobState.Running, unchanged.Attempts[0].State);
        Assert.IsNull(unchanged.Attempts[0].CompletedAt);
    }

    [TestMethod]
    public void ProgressAndHeartbeatRequireExactCurrentRunningAttemptAndTime()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt attempt = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, attempt, StartedAt);
        JobProgress validProgress = CreateProgress(
            job.Id,
            attempt.Id,
            1,
            StartedAt,
            completedUnits: 1);
        JobHeartbeat validHeartbeat = CreateHeartbeat(
            job.Id,
            attempt.Id,
            1,
            StartedAt);
        store.AppendProgress(validProgress);
        store.AppendHeartbeat(validHeartbeat);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.AppendProgress(
                CreateProgress(job.Id, EntityId<JobAttemptIdTag>.New(), 1, StartedAt)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.AppendHeartbeat(
                CreateHeartbeat(job.Id, attempt.Id, 2, StartedAt)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.AppendProgress(
                CreateProgress(job.Id, attempt.Id, 1, StartedAt.AddTicks(-1))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.AppendHeartbeat(
                CreateHeartbeat(job.Id, attempt.Id, 1, StartedAt.AddTicks(-1))));

        _ = store.CompleteAttempt(
            job.Id,
            1,
            CreateCompletedAttempt(attempt, JobState.Succeeded),
            CompletedAt);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.AppendProgress(
                CreateProgress(job.Id, attempt.Id, 1, CompletedAt)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.AppendHeartbeat(
                CreateHeartbeat(job.Id, attempt.Id, 1, CompletedAt)));

        StoredJobAggregate aggregate = store.GetJobAggregate(job.Id)!;
        Assert.AreEqual(1, aggregate.Progress.Count);
        Assert.AreEqual(validProgress.Id, aggregate.Progress[0].Id);
        Assert.AreEqual(1, aggregate.Heartbeats.Count);
        Assert.AreEqual(validHeartbeat.Id, aggregate.Heartbeats[0].Id);
    }

    [TestMethod]
    public void CompletionBeforeLatestProgressOrHeartbeatRollsBack()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt attempt = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, attempt, StartedAt);
        DateTimeOffset progressAt = CompletedAt.AddSeconds(1);
        DateTimeOffset heartbeatAt = progressAt.AddSeconds(1);
        store.AppendProgress(CreateProgress(job.Id, attempt.Id, 1, progressAt));
        store.AppendHeartbeat(CreateHeartbeat(job.Id, attempt.Id, 1, heartbeatAt));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.CompleteAttempt(
                job.Id,
                1,
                CreateCompletedAttempt(attempt, JobState.Succeeded),
                CompletedAt));

        StoredJobAggregate unchanged = store.GetJobAggregate(job.Id)!;
        Assert.AreEqual(JobState.Running, unchanged.Job.State);
        Assert.AreEqual(JobState.Running, unchanged.Attempts.Single().State);
        Assert.AreEqual(1, unchanged.Revision);
    }

    [TestMethod]
    public void AggregateReconstructsAndOrdersAttemptsProgressAndHeartbeats()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt firstAttempt = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, firstAttempt, StartedAt);
        JobProgress laterFirstProgress = CreateProgress(
            job.Id,
            firstAttempt.Id,
            1,
            StartedAt.AddSeconds(2),
            completedUnits: 2);
        JobProgress earlierFirstProgress = CreateProgress(
            job.Id,
            firstAttempt.Id,
            1,
            StartedAt.AddSeconds(1),
            completedUnits: 1);
        JobHeartbeat laterFirstHeartbeat = CreateHeartbeat(
            job.Id,
            firstAttempt.Id,
            1,
            StartedAt.AddSeconds(2));
        JobHeartbeat earlierFirstHeartbeat = CreateHeartbeat(
            job.Id,
            firstAttempt.Id,
            1,
            StartedAt.AddSeconds(1));
        store.AppendProgress(laterFirstProgress);
        store.AppendProgress(earlierFirstProgress);
        store.AppendHeartbeat(laterFirstHeartbeat);
        store.AppendHeartbeat(earlierFirstHeartbeat);
        _ = store.CompleteAttempt(
            job.Id,
            1,
            CreateCompletedAttempt(firstAttempt, JobState.Interrupted),
            CompletedAt);
        _ = store.QueueRetry(job.Id, 2, CompletedAt.AddSeconds(1));
        JobAttempt secondAttempt = CreateRunningAttempt(
            job.Id,
            2,
            queuedAt: CompletedAt.AddSeconds(2),
            startedAt: CompletedAt.AddSeconds(3));
        _ = store.StartAttempt(job.Id, 3, secondAttempt, secondAttempt.StartedAt!.Value);
        JobProgress secondProgress = CreateProgress(
            job.Id,
            secondAttempt.Id,
            2,
            secondAttempt.StartedAt.Value,
            completedUnits: 0);
        JobHeartbeat secondHeartbeat = CreateHeartbeat(
            job.Id,
            secondAttempt.Id,
            2,
            secondAttempt.StartedAt.Value);
        store.AppendProgress(secondProgress);
        store.AppendHeartbeat(secondHeartbeat);

        StoredJobAggregate aggregate = store.GetJobAggregate(job.Id)!;
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(2, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(4, aggregate.Revision);
        CollectionAssert.AreEqual(
            new[] { firstAttempt.Id, secondAttempt.Id },
            aggregate.Attempts.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { earlierFirstProgress.Id, laterFirstProgress.Id, secondProgress.Id },
            aggregate.Progress.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { earlierFirstHeartbeat.Id, laterFirstHeartbeat.Id, secondHeartbeat.Id },
            aggregate.Heartbeats.Select(static item => item.Id).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<JobAttempt>)aggregate.Attempts).Clear());
    }

    [TestMethod]
    public void DuplicateProgressAndHeartbeatRollBackAndCorruptionFailsClosed()
    {
        using StoreTestDatabase database = new();
        (ProjectStore _, JobStore store, Project project) = CreateStores(database);
        Job job = CreateJob(project.Id);
        store.CreateJob(job);
        JobAttempt attempt = CreateRunningAttempt(job.Id, 1);
        _ = store.StartAttempt(job.Id, 0, attempt, StartedAt);
        JobProgress progress = CreateProgress(job.Id, attempt.Id, 1, StartedAt);
        JobHeartbeat heartbeat = CreateHeartbeat(job.Id, attempt.Id, 1, StartedAt);
        store.AppendProgress(progress);
        store.AppendHeartbeat(heartbeat);

        SqliteException duplicateProgress = Assert.ThrowsExactly<SqliteException>(
            () => store.AppendProgress(progress));
        SqliteException duplicateHeartbeat = Assert.ThrowsExactly<SqliteException>(
            () => store.AppendHeartbeat(heartbeat));
        Assert.AreEqual(19, duplicateProgress.SqliteErrorCode);
        Assert.AreEqual(19, duplicateHeartbeat.SqliteErrorCode);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM job_progress;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM job_heartbeats;"));

        database.ExecuteNonQuery(
            "UPDATE job_attempts SET started_at = $started_at WHERE id = $id;",
            ("$started_at", "2031-99-99T00:00:00.0000000Z"),
            ("$id", attempt.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetJobAggregate(job.Id));
    }

    private static (ProjectStore ProjectStore, JobStore JobStore, Project Project) CreateStores(
        StoreTestDatabase database)
    {
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = new(
            EntityId<ProjectIdTag>.New(),
            "Job test project",
            "Exercise durable jobs.",
            "Test suite",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        projectStore.CreateProject(project, "{\"project\":true}", CreatedAt);
        return (projectStore, new JobStore(database.DatabasePath), project);
    }

    private static Job CreateJob(
        EntityId<ProjectIdTag> projectId,
        string kind = "render",
        EntityId<JobIdTag>? id = null)
    {
        return new Job(
            id ?? EntityId<JobIdTag>.New(),
            projectId,
            kind,
            JobState.Queued,
            CreatedAt,
            CreatedAt,
            currentAttemptNumber: 0);
    }

    private static JobAttempt CreateRunningAttempt(
        EntityId<JobIdTag> jobId,
        int sequenceNumber,
        EntityId<JobAttemptIdTag>? id = null,
        DateTimeOffset? queuedAt = null,
        DateTimeOffset? startedAt = null)
    {
        DateTimeOffset actualQueuedAt = queuedAt ?? CreatedAt;
        DateTimeOffset actualStartedAt = startedAt ?? StartedAt;
        return new JobAttempt(
            id ?? EntityId<JobAttemptIdTag>.New(),
            jobId,
            sequenceNumber,
            JobState.Running,
            actualQueuedAt,
            actualStartedAt,
            completedAt: null,
            errorCode: null);
    }

    private static JobAttempt CreateCompletedAttempt(
        JobAttempt running,
        JobState terminalState)
    {
        return new JobAttempt(
            running.Id,
            running.JobId,
            running.SequenceNumber,
            terminalState,
            running.QueuedAt,
            running.StartedAt,
            CompletedAt,
            terminalState switch
            {
                JobState.Failed => "worker.failed",
                JobState.Interrupted => "worker.interrupted",
                _ => null,
            });
    }

    private static JobProgress CreateProgress(
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt,
        long completedUnits = 0)
    {
        return new JobProgress(
            EntityId<JobProgressIdTag>.New(),
            jobId,
            attemptId,
            sequenceNumber,
            recordedAt,
            completedUnits,
            totalUnits: 10,
            phase: "render",
            message: "Rendered 'quoted' units.");
    }

    private static JobHeartbeat CreateHeartbeat(
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt)
    {
        return new JobHeartbeat(
            EntityId<JobHeartbeatIdTag>.New(),
            jobId,
            attemptId,
            sequenceNumber,
            recordedAt);
    }

    private static bool TryStartAttempt(
        JobStore store,
        EntityId<JobIdTag> jobId,
        JobAttempt attempt,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.StartAttempt(jobId, 0, attempt, attempt.StartedAt!.Value);
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static void AssertJobEqual(Job expected, Job actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.State, actual.State);
        Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        Assert.AreEqual(expected.CurrentAttemptNumber, actual.CurrentAttemptNumber);
    }

    private static void AssertAttemptEqual(JobAttempt expected, JobAttempt actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.JobId, actual.JobId);
        Assert.AreEqual(expected.SequenceNumber, actual.SequenceNumber);
        Assert.AreEqual(expected.State, actual.State);
        Assert.AreEqual(expected.QueuedAt, actual.QueuedAt);
        Assert.AreEqual(expected.StartedAt, actual.StartedAt);
        Assert.AreEqual(expected.CompletedAt, actual.CompletedAt);
        Assert.AreEqual(expected.ErrorCode, actual.ErrorCode);
    }
}