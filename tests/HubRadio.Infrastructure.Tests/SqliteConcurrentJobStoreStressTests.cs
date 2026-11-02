using System.Collections.Concurrent;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

/// <summary>
/// Regression for the intermittent Ingesting-stage SqliteException (E2E-006): the media.ingest job and the
/// source-only.pipeline job of ONE project write heartbeats/progress/attempt state concurrently while the UI polls.
/// Every real store opens its own private connection, so writers must queue on the busy timeout and never surface
/// SQLITE_BUSY/LOCKED to the caller.
/// </summary>
[TestClass]
public sealed class SqliteConcurrentJobStoreStressTests
{
    private static readonly DateTimeOffset Base = new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    // FR-132 / AC-136 / AC-137 (E2E-006 intermittent Ingesting failure): concurrent rounds yield no SqliteException.
    [TestMethod]
    public void ConcurrentIngestAndPipelineJobWritesAndUiPollingNeverSurfaceSqliteErrors()
    {
        using StoreTestDatabase database = new();
        var projectStore = new ProjectStore(database.DatabasePath);
        var jobStore = new JobStore(database.DatabasePath);
        var errors = new ConcurrentBag<string>();
        int rounds = 15; // 100 rounds were run once at fix time (about 5 min, 0 SqliteExceptions); kept short for the suite.
        for (int round = 0; round < rounds; round++)
        {
            Project project = new(
                EntityId<ProjectIdTag>.New(), "Stress", "Concurrent jobs.", "Test",
                PublicationScope.InternalOnly, deadline: null, assets: [], performers: []);
            projectStore.CreateProject(project, "{\"project\":true}", Base);
            (Job ingest, JobAttempt ingestAttempt) = Start(jobStore, project.Id, "media.ingest.v1");
            (Job root, JobAttempt rootAttempt) = Start(jobStore, project.Id, "source-only.pipeline.v1");
            using var stop = new CancellationTokenSource();

            void Guard(Action action)
            {
                try { action(); }
                catch (Exception exception)
                {
                    errors.Add(exception is SqliteException s
                        ? $"SqliteErrorCode={s.SqliteErrorCode}/{s.SqliteExtendedErrorCode}: {s.Message}"
                        : exception.GetType().Name + ": " + exception.Message);
                }
            }

            Task[] pollers =
            [
                Task.Run(() => { while (!stop.IsCancellationRequested) { Guard(() => { _ = jobStore.GetJobAggregate(ingest.Id); _ = jobStore.GetJobAggregate(root.Id); }); } }),
                Task.Run(() => { while (!stop.IsCancellationRequested) { Guard(() => _ = jobStore.ListJobs(JobState.Running)); } }),
            ];
            Task[] writers =
            [
                Task.Run(() => Guard(() => { for (int i = 1; i <= 12; i++) { jobStore.AppendHeartbeat(Heartbeat(ingest, ingestAttempt, i)); } })),
                Task.Run(() => Guard(() => { for (int i = 1; i <= 12; i++) { jobStore.AppendProgress(Progress(root, rootAttempt, i)); } })),
                Task.Run(() => Guard(() => { for (int i = 1; i <= 12; i++) { jobStore.AppendHeartbeat(Heartbeat(root, rootAttempt, i)); } })),
                Task.Run(() => Guard(() => { for (int i = 1; i <= 12; i++) { jobStore.AppendProgress(Progress(ingest, ingestAttempt, i)); } })),
            ];
            Task.WaitAll(writers);
            Guard(() => _ = jobStore.CompleteAttempt(ingest.Id, 1, Complete(ingestAttempt), Base.AddSeconds(30)));
            stop.Cancel();
            Task.WaitAll(pollers);
        }

        Assert.AreEqual(0, errors.Count, "SqliteExceptions: " + string.Join(" | ", errors.Distinct().Take(10)));
    }

    private static (Job Job, JobAttempt Attempt) Start(JobStore store, EntityId<ProjectIdTag> projectId, string kind)
    {
        var job = new Job(EntityId<JobIdTag>.New(), projectId, kind, JobState.Queued, Base, Base, currentAttemptNumber: 0);
        store.CreateJob(job);
        var attempt = new JobAttempt(EntityId<JobAttemptIdTag>.New(), job.Id, 1, JobState.Running, Base,
            Base.AddSeconds(1), completedAt: null, errorCode: null);
        _ = store.StartAttempt(job.Id, 0, attempt, Base.AddSeconds(1));
        return (job, attempt);
    }

    private static JobHeartbeat Heartbeat(Job job, JobAttempt attempt, int sequence) =>
        new(EntityId<JobHeartbeatIdTag>.New(), job.Id, attempt.Id, attempt.SequenceNumber, Base.AddSeconds(2).AddMilliseconds(sequence));

    private static JobProgress Progress(Job job, JobAttempt attempt, int sequence) =>
        new(EntityId<JobProgressIdTag>.New(), job.Id, attempt.Id, attempt.SequenceNumber, Base.AddSeconds(2).AddMilliseconds(sequence),
            sequence, 12, "stress", "p");

    private static JobAttempt Complete(JobAttempt running) =>
        new(running.Id, running.JobId, running.SequenceNumber, JobState.Succeeded, running.QueuedAt, running.StartedAt,
            Base.AddSeconds(20), errorCode: null);
}
