using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Jobs;

public enum JobRetryability
{
    NotApplicable,
    Retryable,
    NonRetryable,
}

public enum JobCancellationOutcome
{
    Requested,
    AlreadyRequested,
    NotRunning,
    NotTracked,
}

public enum JobRetryOutcome
{
    Queued,
    StateNotEligible,
    ErrorNotRetryable,
    CoordinatorRunActive,
}

public sealed class JobSnapshot
{
    internal JobSnapshot(
        EntityId<JobIdTag> jobId,
        JobState state,
        EntityId<JobAttemptIdTag>? currentAttemptId,
        int currentAttemptNumber,
        int revision,
        DateTimeOffset updatedAt,
        long? completedUnits,
        long? totalUnits,
        decimal? progressRatio,
        DateTimeOffset? lastProgressAt,
        DateTimeOffset? lastHeartbeatAt,
        string? errorCode,
        JobRetryability retryability,
        bool cancellationRequested)
    {
        JobId = jobId;
        State = state;
        CurrentAttemptId = currentAttemptId;
        CurrentAttemptNumber = currentAttemptNumber;
        Revision = revision;
        UpdatedAt = updatedAt;
        CompletedUnits = completedUnits;
        TotalUnits = totalUnits;
        ProgressRatio = progressRatio;
        LastProgressAt = lastProgressAt;
        LastHeartbeatAt = lastHeartbeatAt;
        ErrorCode = errorCode;
        Retryability = retryability;
        CancellationRequested = cancellationRequested;
    }

    public EntityId<JobIdTag> JobId { get; }

    public JobState State { get; }

    public EntityId<JobAttemptIdTag>? CurrentAttemptId { get; }

    public int CurrentAttemptNumber { get; }

    public int Revision { get; }

    public DateTimeOffset UpdatedAt { get; }

    public long? CompletedUnits { get; }

    public long? TotalUnits { get; }

    public decimal? ProgressRatio { get; }

    public DateTimeOffset? LastProgressAt { get; }

    public DateTimeOffset? LastHeartbeatAt { get; }

    public string? ErrorCode { get; }

    public JobRetryability Retryability { get; }

    internal bool CancellationRequested { get; }
}

public sealed class JobCoordinatorRunResult
{
    internal JobCoordinatorRunResult(
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        JobSnapshot snapshot)
    {
        AttemptId = attemptId;
        SequenceNumber = sequenceNumber;
        Snapshot = snapshot;
    }

    public EntityId<JobAttemptIdTag> AttemptId { get; }

    public int SequenceNumber { get; }

    public JobSnapshot Snapshot { get; }
}

public sealed class JobCancellationResult
{
    internal JobCancellationResult(
        JobCancellationOutcome outcome,
        JobSnapshot snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public JobCancellationOutcome Outcome { get; }

    public JobSnapshot Snapshot { get; }
}

public sealed class JobRetryResult
{
    internal JobRetryResult(
        JobRetryOutcome outcome,
        JobSnapshot snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public JobRetryOutcome Outcome { get; }

    public JobSnapshot Snapshot { get; }
}

internal sealed class JobRevisionConflictException : InvalidOperationException
{
    internal JobRevisionConflictException()
        : base("The job revision changed before the requested operation.")
    {
    }
}

public sealed class JobCoordinator
{
    private const string ExecutionTimedOutErrorCode = "job.execution_timed_out";
    private const string AppRestartedErrorCode = "job.app_restarted";

    private static readonly IReadOnlySet<string> RetryableErrorCodes =
        new HashSet<string>(
        [
            ExecutionTimedOutErrorCode,
            AppRestartedErrorCode,
        ],
        StringComparer.Ordinal);

    private static readonly object ActiveRunsSync = new();
    private static readonly Dictionary<EntityId<JobIdTag>, ActiveRun> ActiveRuns = [];

    private readonly JobStore _jobStore;
    private readonly LocalJobRunner _localJobRunner;
    private readonly TimeProvider _timeProvider;

    public JobCoordinator(
        JobStore jobStore,
        LocalJobRunner localJobRunner)
        : this(jobStore, localJobRunner, TimeProvider.System)
    {
    }

    internal JobCoordinator(
        JobStore jobStore,
        LocalJobRunner localJobRunner,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(localJobRunner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _jobStore = jobStore;
        _localJobRunner = localJobRunner;
        _timeProvider = timeProvider;
    }

    public async Task<JobCoordinatorRunResult> RunAsync(
        EntityId<JobIdTag> jobId,
        Func<LocalJobExecutionContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        ActiveRun activeRun;
        lock (ActiveRunsSync)
        {
            if (ActiveRuns.ContainsKey(jobId))
            {
                throw new InvalidOperationException(
                    "The job already has an active coordinator run.");
            }

            StoredJobAggregate aggregate = RequireAggregate(jobId);
            if (aggregate.Job.State != JobState.Queued)
            {
                throw new InvalidOperationException("Only a queued job can be run.");
            }

            activeRun = new ActiveRun(cancellationToken);
            ActiveRuns.Add(jobId, activeRun);
        }

        try
        {
            try
            {
                LocalJobRunResult localResult = await _localJobRunner.RunAsync(
                    jobId,
                    operation,
                    activeRun.Token).ConfigureAwait(false);
                StoredJobAggregate aggregate = RequireAggregate(jobId);
                EnsureRunResultMatches(localResult, aggregate);
                return new JobCoordinatorRunResult(
                    localResult.AttemptId,
                    localResult.SequenceNumber,
                    CreateSnapshot(aggregate));
            }
            catch (OperationCanceledException exception)
                when (activeRun.Token.IsCancellationRequested &&
                      exception.CancellationToken == activeRun.Token)
            {
                StoredJobAggregate aggregate = RequireAggregate(jobId);
                if (aggregate.Job.State == JobState.Queued)
                {
                    DateTimeOffset cancelledAt = GetUpdateTime(
                        aggregate.Job.UpdatedAt);
                    try
                    {
                        _ = _jobStore.CancelQueued(
                            jobId,
                            aggregate.Revision,
                            cancelledAt);
                        aggregate = RequireAggregate(jobId);
                    }
                    catch (StoreConcurrencyException)
                    {
                        aggregate = RequireAggregate(jobId);
                        if (aggregate.Job.State != JobState.Cancelled)
                        {
                            throw;
                        }
                    }
                }

                if (aggregate.Job.State != JobState.Cancelled)
                {
                    throw;
                }

                if (aggregate.Attempts.Count == 0)
                {
                    throw;
                }

                return CreateRunResult(aggregate);
            }
        }
        finally
        {
            lock (ActiveRunsSync)
            {
                if (ActiveRuns.TryGetValue(jobId, out ActiveRun? current) &&
                    ReferenceEquals(current, activeRun))
                {
                    ActiveRuns.Remove(jobId);
                }

                activeRun.Dispose();
            }
        }
    }

    public JobSnapshot GetSnapshot(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        lock (ActiveRunsSync)
        {
            StoredJobAggregate aggregate = RequireAggregate(jobId);
            bool cancellationRequested =
                ActiveRuns.TryGetValue(jobId, out ActiveRun? activeRun) &&
                activeRun.CancellationRequested;
            return CreateSnapshot(aggregate, cancellationRequested);
        }
    }

    internal StoredJob? FindStoredJob(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return _jobStore.GetJob(jobId);
    }

    internal StoredJob GetStoredJob(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return _jobStore.GetJob(jobId)
            ?? throw new InvalidOperationException("The job does not exist.");
    }

    public JobCancellationResult RequestCancellation(EntityId<JobIdTag> jobId)
    {
        return RequestCancellationCore(jobId, expectedRevision: null);
    }

    internal JobCancellationResult RequestCancellation(
        EntityId<JobIdTag> jobId,
        int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected revision must not be negative.");
        }

        return RequestCancellationCore(jobId, expectedRevision);
    }

    private JobCancellationResult RequestCancellationCore(
        EntityId<JobIdTag> jobId,
        int? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        lock (ActiveRunsSync)
        {
            StoredJobAggregate aggregate = RequireAggregate(jobId);
            EnsureExpectedRevision(aggregate, expectedRevision);
            if (!ActiveRuns.TryGetValue(jobId, out ActiveRun? activeRun))
            {
                JobSnapshot snapshot = CreateSnapshot(aggregate);
                return new JobCancellationResult(
                    snapshot.State == JobState.Running
                        ? JobCancellationOutcome.NotTracked
                        : JobCancellationOutcome.NotRunning,
                    snapshot);
            }

            if (aggregate.Job.State is not (JobState.Queued or JobState.Running))
            {
                return new JobCancellationResult(
                    JobCancellationOutcome.NotRunning,
                    CreateSnapshot(aggregate));
            }

            bool requested = activeRun.RequestCancellation();
            if (aggregate.Job.State == JobState.Queued)
            {
                DateTimeOffset cancelledAt = GetUpdateTime(aggregate.Job.UpdatedAt);
                try
                {
                    _ = _jobStore.CancelQueued(
                        jobId,
                        aggregate.Revision,
                        cancelledAt);
                }
                catch (StoreConcurrencyException)
                {
                    aggregate = RequireAggregate(jobId);
                    if (aggregate.Job.State is not (JobState.Running or JobState.Cancelled))
                    {
                        throw;
                    }
                }
                catch (InvalidOperationException)
                {
                    aggregate = RequireAggregate(jobId);
                    if (aggregate.Job.State is not (JobState.Running or JobState.Cancelled))
                    {
                        throw;
                    }
                }
            }

            return new JobCancellationResult(
                requested
                    ? JobCancellationOutcome.Requested
                    : JobCancellationOutcome.AlreadyRequested,
                CreateSnapshot(
                    RequireAggregate(jobId),
                    activeRun.CancellationRequested));
        }
    }

    public JobRetryResult QueueRetry(EntityId<JobIdTag> jobId)
    {
        return QueueRetryCore(jobId, expectedRevision: null);
    }

    internal JobRetryResult QueueRetry(
        EntityId<JobIdTag> jobId,
        int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected revision must not be negative.");
        }

        return QueueRetryCore(jobId, expectedRevision);
    }

    private JobRetryResult QueueRetryCore(
        EntityId<JobIdTag> jobId,
        int? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        lock (ActiveRunsSync)
        {
            StoredJobAggregate aggregate = RequireAggregate(jobId);
            EnsureExpectedRevision(aggregate, expectedRevision);
            JobSnapshot snapshot = CreateSnapshot(aggregate);
            if (ActiveRuns.ContainsKey(jobId))
            {
                return new JobRetryResult(
                    JobRetryOutcome.CoordinatorRunActive,
                    snapshot);
            }

            if (snapshot.State is not (JobState.Failed or JobState.Interrupted))
            {
                return new JobRetryResult(
                    JobRetryOutcome.StateNotEligible,
                    snapshot);
            }

            if (snapshot.Retryability != JobRetryability.Retryable)
            {
                return new JobRetryResult(
                    JobRetryOutcome.ErrorNotRetryable,
                    snapshot);
            }

            DateTimeOffset queuedAt = GetUpdateTime(aggregate.Job.UpdatedAt);
            _ = _jobStore.QueueRetry(jobId, aggregate.Revision, queuedAt);
            JobSnapshot queuedSnapshot = CreateSnapshot(RequireAggregate(jobId));
            return new JobRetryResult(JobRetryOutcome.Queued, queuedSnapshot);
        }
    }

    private static void EnsureExpectedRevision(
        StoredJobAggregate aggregate,
        int? expectedRevision)
    {
        if (expectedRevision is int expected && aggregate.Revision != expected)
        {
            throw new JobRevisionConflictException();
        }
    }

    private StoredJobAggregate RequireAggregate(EntityId<JobIdTag> jobId)
    {
        return _jobStore.GetJobAggregate(jobId)
            ?? throw new InvalidOperationException("The job does not exist.");
    }

    private DateTimeOffset GetUpdateTime(DateTimeOffset currentUpdatedAt)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        DateTimeOffset floor = currentUpdatedAt.ToUniversalTime();
        return now >= floor ? now : floor;
    }

    private static JobCoordinatorRunResult CreateRunResult(
        StoredJobAggregate aggregate)
    {
        if (aggregate.Job.State is not (
                JobState.Succeeded or
                JobState.Failed or
                JobState.Cancelled or
                JobState.Interrupted) ||
            aggregate.Attempts.Count == 0)
        {
            throw new InvalidOperationException(
                "The coordinator run did not produce a terminal attempt.");
        }

        JobAttempt attempt = aggregate.Attempts[^1];
        if (attempt.State != aggregate.Job.State)
        {
            throw new InvalidOperationException(
                "The coordinator run result is inconsistent with the stored job.");
        }

        return new JobCoordinatorRunResult(
            attempt.Id,
            attempt.SequenceNumber,
            CreateSnapshot(aggregate));
    }

    private static void EnsureRunResultMatches(
        LocalJobRunResult localResult,
        StoredJobAggregate aggregate)
    {
        JobAttempt? completedAttempt = aggregate.Attempts.SingleOrDefault(attempt =>
            attempt.SequenceNumber == localResult.SequenceNumber);
        if (localResult.JobId != aggregate.Job.Id ||
            completedAttempt is null ||
            localResult.AttemptId != completedAttempt.Id ||
            localResult.TerminalState != completedAttempt.State ||
            localResult.FinalRevision > aggregate.Revision)
        {
            throw new InvalidOperationException(
                "The local runner result is inconsistent with the stored job.");
        }
    }

    internal static JobSnapshot CreateSnapshot(
        StoredJobAggregate aggregate,
        bool cancellationRequested = false)
    {
        JobAttempt? currentAttempt = aggregate.Attempts.Count == 0
            ? null
            : aggregate.Attempts[^1];
        JobProgress? latestProgress = currentAttempt is null
            ? null
            : aggregate.Progress.LastOrDefault(progress =>
                progress.JobAttemptId == currentAttempt.Id);
        JobHeartbeat? latestHeartbeat = currentAttempt is null
            ? null
            : aggregate.Heartbeats.LastOrDefault(heartbeat =>
                heartbeat.JobAttemptId == currentAttempt.Id);
        decimal? progressRatio = latestProgress?.TotalUnits is long totalUnits
            ? (decimal)latestProgress.CompletedUnits / totalUnits
            : null;
        string? errorCode = currentAttempt?.ErrorCode;
        JobRetryability retryability = aggregate.Job.State is
            JobState.Failed or JobState.Interrupted
                ? ClassifyRetryability(errorCode)
                : JobRetryability.NotApplicable;

        return new JobSnapshot(
            aggregate.Job.Id,
            aggregate.Job.State,
            currentAttempt?.Id,
            aggregate.Job.CurrentAttemptNumber,
            aggregate.Revision,
            aggregate.Job.UpdatedAt,
            latestProgress?.CompletedUnits,
            latestProgress?.TotalUnits,
            progressRatio,
            latestProgress?.RecordedAt,
            latestHeartbeat?.RecordedAt,
            errorCode,
            retryability,
            cancellationRequested && aggregate.Job.State == JobState.Running);
    }

    private static JobRetryability ClassifyRetryability(string? errorCode)
    {
        return errorCode is not null && RetryableErrorCodes.Contains(errorCode)
            ? JobRetryability.Retryable
            : JobRetryability.NonRetryable;
    }

    private sealed class ActiveRun : IDisposable
    {
        private readonly CancellationTokenSource _cancellationSource;
        private bool _cancellationRequested;

        internal ActiveRun(CancellationToken callerCancellationToken)
        {
            _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken);
        }

        internal CancellationToken Token => _cancellationSource.Token;

        internal bool CancellationRequested =>
            _cancellationRequested || _cancellationSource.IsCancellationRequested;

        internal bool RequestCancellation()
        {
            if (_cancellationRequested || _cancellationSource.IsCancellationRequested)
            {
                return false;
            }

            _cancellationRequested = true;
            _ = _cancellationSource.CancelAsync();
            return true;
        }

        public void Dispose()
        {
            _cancellationSource.Dispose();
        }
    }
}