using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Jobs;

public sealed class LocalJobRunner
{
    private const string ExecutionFailedErrorCode = "job.execution_failed";
    private const string ExecutionTimedOutErrorCode = "job.execution_timed_out";

    private static readonly SemaphoreSlim HeavyOperationGate = new(1, 1);

    private readonly JobStore _jobStore;
    private readonly TimeProvider _timeProvider;

    public LocalJobRunner(JobStore jobStore)
        : this(jobStore, TimeProvider.System)
    {
    }

    internal LocalJobRunner(JobStore jobStore, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _jobStore = jobStore;
        _timeProvider = timeProvider;
    }

    public async Task<LocalJobRunResult> RunAsync(
        EntityId<JobIdTag> jobId,
        Func<LocalJobExecutionContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(operation);

        await HeavyOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            StoredJob storedJob = _jobStore.GetJob(jobId)
                ?? throw new InvalidOperationException("The job does not exist.");
            if (storedJob.Job.State != JobState.Queued)
            {
                throw new InvalidOperationException("Only a queued job can be run.");
            }

            DateTimeOffset startedAt = _timeProvider.GetUtcNow();
            if (startedAt < storedJob.Job.UpdatedAt)
            {
                throw new InvalidOperationException(
                    "The execution clock cannot precede the queued job update.");
            }

            int sequenceNumber = checked(storedJob.Job.CurrentAttemptNumber + 1);
            var runningAttempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                jobId,
                sequenceNumber,
                JobState.Running,
                storedJob.Job.UpdatedAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            StoredJob runningJob = _jobStore.StartAttempt(
                jobId,
                storedJob.Revision,
                runningAttempt,
                startedAt);
            var context = new LocalJobExecutionContext(
                _jobStore,
                _timeProvider,
                runningAttempt);

            JobState terminalState = JobState.Succeeded;
            string? errorCode = null;
            bool callerCancellation = false;
            try
            {
                Task operationTask = Task.Run<Task>(
                    () => operation(context, cancellationToken),
                    CancellationToken.None).Unwrap();
                await operationTask.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                terminalState = JobState.Failed;
                errorCode = ExecutionTimedOutErrorCode;
            }
            catch (OperationCanceledException exception)
            {
                callerCancellation =
                    cancellationToken.IsCancellationRequested &&
                    exception.CancellationToken == cancellationToken;
                if (callerCancellation)
                {
                    terminalState = JobState.Cancelled;
                }
                else
                {
                    terminalState = JobState.Failed;
                    errorCode = ExecutionFailedErrorCode;
                }
            }
            catch (Exception)
            {
                terminalState = JobState.Failed;
                errorCode = ExecutionFailedErrorCode;
            }

            DateTimeOffset? latestRecordedAt = context.Seal();
            DateTimeOffset completedAt = _timeProvider.GetUtcNow();
            if (latestRecordedAt is DateTimeOffset latest && latest > completedAt)
            {
                completedAt = latest;
            }

            var completedAttempt = new JobAttempt(
                runningAttempt.Id,
                runningAttempt.JobId,
                runningAttempt.SequenceNumber,
                terminalState,
                runningAttempt.QueuedAt,
                runningAttempt.StartedAt,
                completedAt,
                errorCode);
            StoredJob completedJob = _jobStore.CompleteAttempt(
                jobId,
                runningJob.Revision,
                completedAttempt,
                completedAt);

            if (callerCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new LocalJobRunResult(
                jobId,
                runningAttempt.Id,
                sequenceNumber,
                terminalState,
                completedJob.Revision);
        }
        finally
        {
            HeavyOperationGate.Release();
        }
    }
}

public sealed class LocalJobExecutionContext
{
    private readonly object _sync = new();
    private readonly JobStore _jobStore;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _latestRecordedAt;
    private bool _isSealed;

    internal LocalJobExecutionContext(
        JobStore jobStore,
        TimeProvider timeProvider,
        JobAttempt runningAttempt)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(runningAttempt);
        if (runningAttempt.State != JobState.Running)
        {
            throw new ArgumentException(
                "The execution context requires a running attempt.",
                nameof(runningAttempt));
        }

        _jobStore = jobStore;
        _timeProvider = timeProvider;
        JobId = runningAttempt.JobId;
        AttemptId = runningAttempt.Id;
        SequenceNumber = runningAttempt.SequenceNumber;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<JobAttemptIdTag> AttemptId { get; }

    public int SequenceNumber { get; }

    public void ReportProgress(
        long completed,
        long? total = null,
        string? phase = null,
        string? message = null,
        DateTimeOffset? recordedAt = null)
    {
        lock (_sync)
        {
            EnsureOpen();
            DateTimeOffset timestamp = recordedAt ?? _timeProvider.GetUtcNow();
            _jobStore.AppendProgress(
                new JobProgress(
                    EntityId<JobProgressIdTag>.New(),
                    JobId,
                    AttemptId,
                    SequenceNumber,
                    timestamp,
                    completed,
                    total,
                    phase,
                    message));
            TrackLatest(timestamp);
        }
    }

    public void RecordHeartbeat(DateTimeOffset? recordedAt = null)
    {
        lock (_sync)
        {
            EnsureOpen();
            DateTimeOffset timestamp = recordedAt ?? _timeProvider.GetUtcNow();
            _jobStore.AppendHeartbeat(
                new JobHeartbeat(
                    EntityId<JobHeartbeatIdTag>.New(),
                    JobId,
                    AttemptId,
                    SequenceNumber,
                    timestamp));
            TrackLatest(timestamp);
        }
    }

    internal DateTimeOffset GetPersistenceTimestamp(DateTimeOffset minimum)
    {
        lock (_sync)
        {
            EnsureOpen();
            DateTimeOffset timestamp = _timeProvider.GetUtcNow().ToUniversalTime();
            DateTimeOffset floor = minimum.ToUniversalTime();
            if (_latestRecordedAt is DateTimeOffset latest && latest > floor)
            {
                floor = latest;
            }

            return timestamp >= floor ? timestamp : floor;
        }
    }

    internal DateTimeOffset? Seal()
    {
        lock (_sync)
        {
            _isSealed = true;
            return _latestRecordedAt;
        }
    }

    private void EnsureOpen()
    {
        if (_isSealed)
        {
            throw new InvalidOperationException(
                "The local job execution context is closed.");
        }
    }

    private void TrackLatest(DateTimeOffset recordedAt)
    {
        if (_latestRecordedAt is null || recordedAt > _latestRecordedAt.Value)
        {
            _latestRecordedAt = recordedAt;
        }
    }
}

public sealed class LocalJobRunResult
{
    internal LocalJobRunResult(
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        JobState terminalState,
        int finalRevision)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(attemptId);
        if (terminalState is not (
            JobState.Succeeded or
            JobState.Failed or
            JobState.Cancelled or
            JobState.Interrupted))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalState),
                terminalState,
                "A local job run result requires a terminal state.");
        }

        if (sequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "A job-attempt sequence number must be positive.");
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
        SequenceNumber = sequenceNumber;
        TerminalState = terminalState;
        FinalRevision = finalRevision;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<JobAttemptIdTag> AttemptId { get; }

    public int SequenceNumber { get; }

    public JobState TerminalState { get; }

    public int FinalRevision { get; }
}