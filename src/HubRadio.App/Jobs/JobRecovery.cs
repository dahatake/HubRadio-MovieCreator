using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Recovery;

namespace HubRadio.App.Jobs;

public sealed class JobRecovery
{
    private const string AppRestartedErrorCode = "job.app_restarted";

    private readonly JobStore _jobStore;
    private readonly WorkspaceRecovery _workspaceRecovery;
    private readonly TimeProvider _timeProvider;

    public JobRecovery(
        JobStore jobStore,
        WorkspaceRecovery workspaceRecovery)
        : this(jobStore, workspaceRecovery, TimeProvider.System)
    {
    }

    internal JobRecovery(
        JobStore jobStore,
        WorkspaceRecovery workspaceRecovery,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(workspaceRecovery);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _jobStore = jobStore;
        _workspaceRecovery = workspaceRecovery;
        _timeProvider = timeProvider;
    }

    public async Task<JobRecoveryResult> RecoverAsync(
        IReadOnlySet<string> automaticallyRestartableKinds,
        CancellationToken cancellationToken)
    {
        HashSet<string> restartableKinds = ValidateRestartableKinds(
            automaticallyRestartableKinds);
        WorkspaceRecoveryCheckResult beforeCheck =
            await _workspaceRecovery.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!beforeCheck.DatabaseUsable)
        {
            return new JobRecoveryResult(beforeCheck, beforeCheck, [], []);
        }

        IReadOnlyList<StoredJob> runningJobs = _jobStore.ListJobs(JobState.Running);
        var recoveredJobs = new List<RecoveredJob>();
        var skippedJobIds = new List<EntityId<JobIdTag>>();
        foreach (StoredJob runningJob in runningJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredJobAggregate aggregate = RequireCurrentRunningAggregate(runningJob);
            JobAttempt runningAttempt = aggregate.Attempts[^1];
            DateTimeOffset completedAt = GetCompletionTime(aggregate, runningAttempt);
            var interruptedAttempt = new JobAttempt(
                runningAttempt.Id,
                runningAttempt.JobId,
                runningAttempt.SequenceNumber,
                JobState.Interrupted,
                runningAttempt.QueuedAt,
                runningAttempt.StartedAt,
                completedAt,
                AppRestartedErrorCode);

            StoredJob interruptedJob;
            try
            {
                interruptedJob = _jobStore.CompleteAttempt(
                    runningJob.Job.Id,
                    aggregate.Revision,
                    interruptedAttempt,
                    completedAt);
            }
            catch (StoreConcurrencyException)
            {
                StoredJob current = _jobStore.GetJob(runningJob.Job.Id)
                    ?? throw RecoveryFailure();
                if (current.Job.State != JobState.Running)
                {
                    skippedJobIds.Add(runningJob.Job.Id);
                    continue;
                }

                throw;
            }

            bool automaticallyRequeued = restartableKinds.Contains(runningJob.Job.Kind);
            JobState currentState = JobState.Interrupted;
            int finalRevision = interruptedJob.Revision;
            if (automaticallyRequeued)
            {
                DateTimeOffset queuedAt = GetUtcNow();
                if (queuedAt < completedAt)
                {
                    queuedAt = completedAt;
                }

                finalRevision = _jobStore.QueueRetry(
                    runningJob.Job.Id,
                    interruptedJob.Revision,
                    queuedAt);
                currentState = JobState.Queued;
            }

            recoveredJobs.Add(new RecoveredJob(
                runningJob.Job.Id,
                runningAttempt.Id,
                JobState.Running,
                currentState,
                automaticallyRequeued,
                finalRevision));
        }

        WorkspaceRecoveryCheckResult afterCheck =
            await _workspaceRecovery.CheckAsync(cancellationToken).ConfigureAwait(false);
        return new JobRecoveryResult(
            beforeCheck,
            afterCheck,
            recoveredJobs,
            skippedJobIds);
    }

    private StoredJobAggregate RequireCurrentRunningAggregate(StoredJob runningJob)
    {
        StoredJobAggregate aggregate = _jobStore.GetJobAggregate(runningJob.Job.Id)
            ?? throw RecoveryFailure();
        if (aggregate.Job.Id != runningJob.Job.Id ||
            aggregate.Job.State != JobState.Running ||
            aggregate.Revision != runningJob.Revision ||
            aggregate.Job.CurrentAttemptNumber <= 0 ||
            aggregate.Attempts.Count != aggregate.Job.CurrentAttemptNumber)
        {
            throw RecoveryFailure();
        }

        JobAttempt currentAttempt = aggregate.Attempts[^1];
        if (currentAttempt.JobId != aggregate.Job.Id ||
            currentAttempt.SequenceNumber != aggregate.Job.CurrentAttemptNumber ||
            currentAttempt.State != JobState.Running ||
            currentAttempt.StartedAt is null ||
            currentAttempt.CompletedAt is not null ||
            currentAttempt.ErrorCode is not null)
        {
            throw RecoveryFailure();
        }

        return aggregate;
    }

    private DateTimeOffset GetCompletionTime(
        StoredJobAggregate aggregate,
        JobAttempt runningAttempt)
    {
        DateTimeOffset completedAt = GetUtcNow();
        completedAt = LaterOf(
            completedAt,
            runningAttempt.StartedAt!.Value.ToUniversalTime());
        completedAt = LaterOf(completedAt, aggregate.Job.UpdatedAt.ToUniversalTime());
        foreach (JobProgress progress in aggregate.Progress)
        {
            completedAt = LaterOf(completedAt, progress.RecordedAt.ToUniversalTime());
        }

        foreach (JobHeartbeat heartbeat in aggregate.Heartbeats)
        {
            completedAt = LaterOf(completedAt, heartbeat.RecordedAt.ToUniversalTime());
        }

        return completedAt;
    }

    private DateTimeOffset GetUtcNow() =>
        _timeProvider.GetUtcNow().ToUniversalTime();

    private static DateTimeOffset LaterOf(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first >= second ? first : second;

    private static HashSet<string> ValidateRestartableKinds(
        IReadOnlySet<string> automaticallyRestartableKinds)
    {
        ArgumentNullException.ThrowIfNull(automaticallyRestartableKinds);
        var validated = new HashSet<string>(StringComparer.Ordinal);
        foreach (string kind in automaticallyRestartableKinds)
        {
            if (string.IsNullOrWhiteSpace(kind) ||
                !string.Equals(kind, kind.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An automatically restartable job kind must be nonblank without surrounding whitespace.",
                    nameof(automaticallyRestartableKinds));
            }

            _ = validated.Add(kind);
        }

        return validated;
    }

    private static InvalidOperationException RecoveryFailure() =>
        new("The running job could not be recovered safely.");
}

public sealed class RecoveredJob
{
    internal RecoveredJob(
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        JobState previousState,
        JobState currentState,
        bool automaticallyRequeued,
        int finalRevision)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(attemptId);
        if (previousState != JobState.Running)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousState),
                previousState,
                "A recovered job must have previously been running.");
        }

        if (currentState is not (JobState.Interrupted or JobState.Queued))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentState),
                currentState,
                "A recovered job must be interrupted or queued.");
        }

        if (automaticallyRequeued != (currentState == JobState.Queued))
        {
            throw new ArgumentException(
                "The automatic requeue flag must match the recovered job state.",
                nameof(automaticallyRequeued));
        }

        if (finalRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalRevision),
                finalRevision,
                "A final revision must not be negative.");
        }

        JobId = jobId;
        AttemptId = attemptId;
        PreviousState = previousState;
        CurrentState = currentState;
        AutomaticallyRequeued = automaticallyRequeued;
        FinalRevision = finalRevision;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<JobAttemptIdTag> AttemptId { get; }

    public JobState PreviousState { get; }

    public JobState CurrentState { get; }

    public bool AutomaticallyRequeued { get; }

    public int FinalRevision { get; }
}

public sealed class JobRecoveryResult
{
    internal JobRecoveryResult(
        WorkspaceRecoveryCheckResult beforeCheck,
        WorkspaceRecoveryCheckResult afterCheck,
        IReadOnlyList<RecoveredJob> recoveredJobs,
        IReadOnlyList<EntityId<JobIdTag>> skippedJobIds)
    {
        ArgumentNullException.ThrowIfNull(beforeCheck);
        ArgumentNullException.ThrowIfNull(afterCheck);
        ArgumentNullException.ThrowIfNull(recoveredJobs);
        ArgumentNullException.ThrowIfNull(skippedJobIds);

        DatabaseUsable = beforeCheck.DatabaseUsable && afterCheck.DatabaseUsable;
        BeforeCheck = beforeCheck;
        AfterCheck = afterCheck;
        RecoveredJobs = Array.AsReadOnly(recoveredJobs.ToArray());
        SkippedJobIds = Array.AsReadOnly(skippedJobIds.ToArray());
    }

    public bool DatabaseUsable { get; }

    public WorkspaceRecoveryCheckResult BeforeCheck { get; }

    public WorkspaceRecoveryCheckResult AfterCheck { get; }

    public IReadOnlyList<RecoveredJob> RecoveredJobs { get; }

    public IReadOnlyList<EntityId<JobIdTag>> SkippedJobIds { get; }
}