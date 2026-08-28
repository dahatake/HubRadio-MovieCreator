using System.Reflection;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class LocalJobRunnerTests
{
    private static readonly DateTimeOffset QueuedAt =
        new(2038, 9, 10, 11, 12, 13, TimeSpan.Zero);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RunAsyncCompletesQueuedJobAndReturnsFinalSnapshot()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        DateTimeOffset runAt = QueuedAt.AddSeconds(1);
        var runner = new LocalJobRunner(store, new FixedTimeProvider(runAt));
        LocalJobExecutionContext? observedContext = null;
        CancellationToken observedToken = default;
        using var callerCancellation = new CancellationTokenSource();

        LocalJobRunResult result = await runner.RunAsync(
            job.Id,
            (context, cancellationToken) =>
            {
                observedContext = context;
                observedToken = cancellationToken;
                return Task.CompletedTask;
            },
            callerCancellation.Token);

        LocalJobExecutionContext context = observedContext
            ?? throw new AssertFailedException("The operation did not receive its context.");
        StoredJobAggregate aggregate = RequiredAggregate(store, job.Id);
        JobAttempt attempt = AssertSingleAttempt(aggregate);
        Assert.AreEqual(job.Id, context.JobId);
        Assert.AreEqual(result.AttemptId, context.AttemptId);
        Assert.AreEqual(1, context.SequenceNumber);
        Assert.AreEqual(callerCancellation.Token, observedToken);
        Assert.AreEqual(job.Id, result.JobId);
        Assert.AreEqual(attempt.Id, result.AttemptId);
        Assert.AreEqual(1, result.SequenceNumber);
        Assert.AreEqual(JobState.Succeeded, result.TerminalState);
        Assert.AreEqual(2, result.FinalRevision);
        Assert.AreEqual(JobState.Succeeded, aggregate.Job.State);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(runAt, aggregate.Job.UpdatedAt);
        Assert.AreEqual(2, aggregate.Revision);
        Assert.AreEqual(JobState.Succeeded, attempt.State);
        Assert.AreEqual(QueuedAt, attempt.QueuedAt);
        Assert.AreEqual(runAt, attempt.StartedAt);
        Assert.AreEqual(runAt, attempt.CompletedAt);
        Assert.IsNull(attempt.ErrorCode);
        Assert.AreEqual(0, aggregate.Progress.Count);
        Assert.AreEqual(0, aggregate.Heartbeats.Count);
    }

    [TestMethod]
    public async Task RunAsyncOffloadsSynchronousOperationPrefixAndAvoidsContextCapture()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        using var operationEntered = new ManualResetEventSlim(initialState: false);
        using var runReturned = new ManualResetEventSlim(initialState: false);
        using var releaseOperation = new ManualResetEventSlim(initialState: false);
        var synchronizationContext = new RecordingSynchronizationContext();
        Task<LocalJobRunResult>? pendingRun = null;
        Exception? invocationError = null;
        int operationObservedBlockedCaller = 0;
        var callerThread = new Thread(() =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            try
            {
                pendingRun = runner.RunAsync(
                    job.Id,
                    (_, _) =>
                    {
                        operationEntered.Set();
                        if (!runReturned.Wait(TestTimeout))
                        {
                            Volatile.Write(ref operationObservedBlockedCaller, 1);
                        }

                        _ = releaseOperation.Wait(TestTimeout);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                invocationError = exception;
            }
            finally
            {
                runReturned.Set();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        })
        {
            IsBackground = true,
            Name = "LocalJobRunnerTests.CallerThread",
        };

        callerThread.Start();
        bool operationStarted = operationEntered.Wait(TestTimeout);
        bool returnedBeforeRelease = runReturned.Wait(TestTimeout);
        Task<LocalJobRunResult>? taskBeforeRelease = pendingRun;
        bool returnedIncomplete = taskBeforeRelease is { IsCompleted: false };
        releaseOperation.Set();
        bool callerThreadExited = callerThread.Join(TestTimeout);

        Assert.IsTrue(operationStarted, "The operation did not start within the test timeout.");
        Assert.IsTrue(
            returnedBeforeRelease,
            "RunAsync blocked its caller on the operation's synchronous prefix.");
        Assert.IsTrue(returnedIncomplete, "RunAsync did not return an incomplete task.");
        Assert.IsTrue(callerThreadExited, "The caller thread did not exit.");
        Assert.IsNull(invocationError);
        Assert.AreEqual(0, Volatile.Read(ref operationObservedBlockedCaller));
        Task<LocalJobRunResult> run = pendingRun
            ?? throw new AssertFailedException("RunAsync did not return a task.");
        LocalJobRunResult result = await run.WaitAsync(TestTimeout);
        Assert.AreEqual(JobState.Succeeded, result.TerminalState);
        Assert.AreEqual(0, synchronizationContext.PostCount);
    }

    [TestMethod]
    public async Task RunnerInstancesSerializeHeavyOperationsAcrossJobs()
    {
        using LocalJobTestDatabase database = new();
        var setupStore = new JobStore(database.DatabasePath);
        Job firstJob = CreateQueuedJob(database, setupStore);
        Job secondJob = CreateQueuedJob(database, setupStore);
        var firstRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var secondRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;
        int maximumInFlight = 0;

        Task<LocalJobRunResult> firstRun = firstRunner.RunAsync(
            firstJob.Id,
            async (_, _) =>
            {
                int current = Interlocked.Increment(ref inFlight);
                UpdateMaximum(ref maximumInFlight, current);
                firstEntered.TrySetResult(true);
                try
                {
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref inFlight);
                }
            },
            CancellationToken.None);
        Task<LocalJobRunResult>? secondRun = null;
        StoredJob? secondWhileWaiting = null;
        StoredJobAggregate? secondAggregateWhileWaiting = null;
        bool secondOperationWasWaiting = false;
        try
        {
            await firstEntered.Task.WaitAsync(TestTimeout);
            secondRun = secondRunner.RunAsync(
                secondJob.Id,
                async (_, _) =>
                {
                    int current = Interlocked.Increment(ref inFlight);
                    UpdateMaximum(ref maximumInFlight, current);
                    secondEntered.TrySetResult(true);
                    try
                    {
                        await Task.Yield();
                    }
                    finally
                    {
                        _ = Interlocked.Decrement(ref inFlight);
                    }
                },
                CancellationToken.None);
            secondOperationWasWaiting = !secondEntered.Task.IsCompleted;
            secondWhileWaiting = setupStore.GetJob(secondJob.Id);
            secondAggregateWhileWaiting = setupStore.GetJobAggregate(secondJob.Id);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        LocalJobRunResult[] results = await Task.WhenAll(
            firstRun,
            secondRun ?? throw new AssertFailedException("The second run was not started."));
        Assert.IsTrue(secondOperationWasWaiting);
        Assert.IsNotNull(secondWhileWaiting);
        Assert.AreEqual(JobState.Queued, secondWhileWaiting.Job.State);
        Assert.AreEqual(0, secondWhileWaiting.Job.CurrentAttemptNumber);
        Assert.AreEqual(0, secondWhileWaiting.Revision);
        Assert.IsNotNull(secondAggregateWhileWaiting);
        Assert.AreEqual(0, secondAggregateWhileWaiting.Attempts.Count);
        Assert.AreEqual(1, maximumInFlight);
        Assert.AreEqual(0, inFlight);
        Assert.IsTrue(results.All(static result => result.TerminalState == JobState.Succeeded));
        Assert.AreEqual(JobState.Succeeded, RequiredAggregate(setupStore, firstJob.Id).Job.State);
        Assert.AreEqual(JobState.Succeeded, RequiredAggregate(setupStore, secondJob.Id).Job.State);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingLeavesQueuedJobUntouched()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job firstJob = CreateQueuedJob(database, store);
        Job waitingJob = CreateQueuedJob(database, store);
        var firstRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var waitingRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        Task<LocalJobRunResult> firstRun = firstRunner.RunAsync(
            firstJob.Id,
            async (_, _) =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TestTimeout);
        Task<LocalJobRunResult> waitingRun = waitingRunner.RunAsync(
            waitingJob.Id,
            static (_, _) => Task.CompletedTask,
            cancellation.Token);
        cancellation.Cancel();
        Exception? waitingError = await CaptureExceptionAsync(waitingRun);
        StoredJobAggregate waitingAggregate = RequiredAggregate(store, waitingJob.Id);
        releaseFirst.TrySetResult(true);
        LocalJobRunResult firstResult = await firstRun.WaitAsync(TestTimeout);

        Assert.IsInstanceOfType<OperationCanceledException>(waitingError);
        var cancellationError = (OperationCanceledException)waitingError;
        Assert.AreEqual(cancellation.Token, cancellationError.CancellationToken);
        Assert.AreEqual(JobState.Queued, waitingAggregate.Job.State);
        Assert.AreEqual(0, waitingAggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(0, waitingAggregate.Revision);
        Assert.AreEqual(0, waitingAggregate.Attempts.Count);
        Assert.AreEqual(JobState.Succeeded, firstResult.TerminalState);
    }

    [TestMethod]
    public async Task CallerCancellationPersistsCancelledThenRethrowsExactTokenAndReleasesGate()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job cancelledJob = CreateQueuedJob(database, store);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var operationEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        Task<LocalJobRunResult> cancelledRun = runner.RunAsync(
            cancelledJob.Id,
            async (_, cancellationToken) =>
            {
                operationEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellation.Token);
        await operationEntered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Exception? cancellationError = await CaptureExceptionAsync(cancelledRun);

        Assert.IsInstanceOfType<OperationCanceledException>(cancellationError);
        var operationCancelled = (OperationCanceledException)cancellationError;
        Assert.AreEqual(cancellation.Token, operationCancelled.CancellationToken);
        StoredJobAggregate aggregate = RequiredAggregate(store, cancelledJob.Id);
        JobAttempt attempt = AssertSingleAttempt(aggregate);
        Assert.AreEqual(JobState.Cancelled, aggregate.Job.State);
        Assert.AreEqual(JobState.Cancelled, attempt.State);
        Assert.IsNull(attempt.ErrorCode);
        Assert.AreEqual(2, aggregate.Revision);

        Job reusableJob = CreateQueuedJob(database, store);
        LocalJobRunResult reusableResult = await runner.RunAsync(
            reusableJob.Id,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.AreEqual(JobState.Succeeded, reusableResult.TerminalState);
    }

    [TestMethod]
    public async Task OperationFailuresReturnStableCodesWithoutPersistingDetails()
    {
        const string sensitiveDetails = "SensitiveOperationDetails-2f8e83d8";
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        Job failedJob = CreateQueuedJob(database, store);
        Job timedOutJob = CreateQueuedJob(database, store);
        Job unexpectedCancellationJob = CreateQueuedJob(database, store);
        using var unrelatedCancellation = new CancellationTokenSource();
        unrelatedCancellation.Cancel();

        LocalJobRunResult failed = await runner.RunAsync(
            failedJob.Id,
            (_, _) => throw new InvalidOperationException(sensitiveDetails),
            CancellationToken.None);
        LocalJobRunResult timedOut = await runner.RunAsync(
            timedOutJob.Id,
            static (_, _) => throw new TimeoutException("Private timeout details."),
            CancellationToken.None);
        LocalJobRunResult unexpectedCancellation = await runner.RunAsync(
            unexpectedCancellationJob.Id,
            (_, _) => Task.FromCanceled(unrelatedCancellation.Token),
            CancellationToken.None);

        AssertFailedResult(store, failedJob.Id, failed, "job.execution_failed");
        AssertFailedResult(store, timedOutJob.Id, timedOut, "job.execution_timed_out");
        AssertFailedResult(
            store,
            unexpectedCancellationJob.Id,
            unexpectedCancellation,
            "job.execution_failed");
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            """
            SELECT COUNT(*)
            FROM job_attempts
            WHERE instr(COALESCE(error_code, ''), 'SensitiveOperationDetails-2f8e83d8') > 0
               OR instr(COALESCE(error_code, ''), 'Private timeout details') > 0;
            """));
    }

    [TestMethod]
    public async Task ConcurrentEvidencePersistsInOrderUsesIndependentIdsAndSealsContext()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        DateTimeOffset startedAt = QueuedAt.AddSeconds(1);
        DateTimeOffset[] progressTimes =
        [
            QueuedAt.AddSeconds(2),
            QueuedAt.AddSeconds(4),
            QueuedAt.AddSeconds(6),
        ];
        DateTimeOffset[] heartbeatTimes =
        [
            QueuedAt.AddSeconds(3),
            QueuedAt.AddSeconds(5),
            QueuedAt.AddSeconds(7),
        ];
        var runner = new LocalJobRunner(store, new FixedTimeProvider(startedAt));
        LocalJobExecutionContext? observedContext = null;

        LocalJobRunResult result = await runner.RunAsync(
            job.Id,
            (context, _) =>
            {
                observedContext = context;
                IEnumerable<Task> progressWrites = progressTimes.Select(
                    (recordedAt, index) => Task.Run(() => context.ReportProgress(
                        completed: index + 1,
                        total: progressTimes.Length,
                        phase: $"phase-{index + 1}",
                        message: $"message-{index + 1}",
                        recordedAt)));
                IEnumerable<Task> heartbeatWrites = heartbeatTimes.Select(
                    recordedAt => Task.Run(() => context.RecordHeartbeat(recordedAt)));
                return Task.WhenAll(progressWrites.Concat(heartbeatWrites));
            },
            CancellationToken.None);

        LocalJobExecutionContext context = observedContext
            ?? throw new AssertFailedException("The operation did not receive its context.");
        StoredJobAggregate aggregate = RequiredAggregate(store, job.Id);
        JobAttempt attempt = AssertSingleAttempt(aggregate);
        Assert.AreEqual(JobState.Succeeded, result.TerminalState);
        Assert.AreEqual(heartbeatTimes[^1], attempt.CompletedAt);
        Assert.AreEqual(heartbeatTimes[^1], aggregate.Job.UpdatedAt);
        CollectionAssert.AreEqual(
            progressTimes,
            aggregate.Progress.Select(static item => item.RecordedAt).ToArray());
        CollectionAssert.AreEqual(
            heartbeatTimes,
            aggregate.Heartbeats.Select(static item => item.RecordedAt).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1L, 2L, 3L },
            aggregate.Progress.Select(static item => item.CompletedUnits).ToArray());
        Assert.IsTrue(aggregate.Progress.All(item =>
            item.JobId == job.Id &&
            item.JobAttemptId == result.AttemptId &&
            item.SequenceNumber == result.SequenceNumber));
        Assert.IsTrue(aggregate.Heartbeats.All(item =>
            item.JobId == job.Id &&
            item.JobAttemptId == result.AttemptId &&
            item.SequenceNumber == result.SequenceNumber));
        string[] evidenceIds = aggregate.Progress
            .Select(static item => item.Id.ToString())
            .Concat(aggregate.Heartbeats.Select(static item => item.Id.ToString()))
            .ToArray();
        Assert.AreEqual(evidenceIds.Length, evidenceIds.Distinct(StringComparer.Ordinal).Count());

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.ReportProgress(3, 3, recordedAt: QueuedAt.AddSeconds(8)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.RecordHeartbeat(QueuedAt.AddSeconds(8)));
        StoredJobAggregate afterClosedCalls = RequiredAggregate(store, job.Id);
        Assert.AreEqual(progressTimes.Length, afterClosedCalls.Progress.Count);
        Assert.AreEqual(heartbeatTimes.Length, afterClosedCalls.Heartbeats.Count);
    }

    [TestMethod]
    public async Task MissingAndNonQueuedJobsFailBeforeInvokingOperation()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(2)));
        EntityId<JobIdTag> missingJobId = EntityId<JobIdTag>.New();
        Job cancelledJob = CreateQueuedJob(database, store);
        _ = store.CancelQueued(cancelledJob.Id, 0, QueuedAt.AddSeconds(1));
        int invocationCount = 0;

        Exception? missingError = await CaptureExceptionAsync(runner.RunAsync(
            missingJobId,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref invocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None));
        Exception? nonQueuedError = await CaptureExceptionAsync(runner.RunAsync(
            cancelledJob.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref invocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.IsInstanceOfType<InvalidOperationException>(missingError);
        Assert.IsInstanceOfType<InvalidOperationException>(nonQueuedError);
        Assert.AreEqual(0, invocationCount);
        Assert.IsNull(store.GetJob(missingJobId));
        StoredJobAggregate aggregate = RequiredAggregate(store, cancelledJob.Id);
        Assert.AreEqual(JobState.Cancelled, aggregate.Job.State);
        Assert.AreEqual(0, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(1, aggregate.Revision);
        Assert.AreEqual(0, aggregate.Attempts.Count);
    }

    [TestMethod]
    public async Task ConcurrentRunsForSameJobCreateOnlyOneAttempt()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        var firstRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        var secondRunner = new LocalJobRunner(
            new JobStore(database.DatabasePath),
            new FixedTimeProvider(QueuedAt.AddSeconds(2)));
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int secondInvocationCount = 0;

        Task<LocalJobRunResult> firstRun = firstRunner.RunAsync(
            job.Id,
            async (_, _) =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TestTimeout);
        Task<LocalJobRunResult> secondRun = secondRunner.RunAsync(
            job.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref secondInvocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        releaseFirst.TrySetResult(true);
        LocalJobRunResult firstResult = await firstRun.WaitAsync(TestTimeout);
        Exception? secondError = await CaptureExceptionAsync(secondRun);

        Assert.AreEqual(JobState.Succeeded, firstResult.TerminalState);
        Assert.IsInstanceOfType<InvalidOperationException>(secondError);
        Assert.AreEqual(0, secondInvocationCount);
        StoredJobAggregate aggregate = RequiredAggregate(store, job.Id);
        Assert.AreEqual(JobState.Succeeded, aggregate.Job.State);
        Assert.AreEqual(2, aggregate.Revision);
        Assert.AreEqual(1, aggregate.Attempts.Count);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
    }

    [TestMethod]
    public async Task ClockRegressionFailsBeforeStateMutation()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddTicks(-1)));
        int invocationCount = 0;

        Exception? error = await CaptureExceptionAsync(runner.RunAsync(
            job.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref invocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.IsInstanceOfType<InvalidOperationException>(error);
        Assert.AreEqual(0, invocationCount);
        StoredJobAggregate aggregate = RequiredAggregate(store, job.Id);
        Assert.AreEqual(JobState.Queued, aggregate.Job.State);
        Assert.AreEqual(QueuedAt, aggregate.Job.UpdatedAt);
        Assert.AreEqual(0, aggregate.Revision);
        Assert.AreEqual(0, aggregate.Attempts.Count);
    }

    [TestMethod]
    public async Task TerminalPersistenceFailurePropagatesRollsBackAndReleasesGate()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job affectedJob = CreateQueuedJob(database, store);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));
        LocalJobExecutionContext? observedContext = null;
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_job_attempt_terminal_update
            BEFORE UPDATE OF state ON job_attempts
            WHEN OLD.state = 'running' AND NEW.state <> 'running'
            BEGIN
                SELECT RAISE(ABORT, 'forced terminal persistence failure');
            END;
            """);

        Exception? persistenceError = await CaptureExceptionAsync(runner.RunAsync(
            affectedJob.Id,
            (context, _) =>
            {
                observedContext = context;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.IsInstanceOfType<SqliteException>(persistenceError);
        StoredJobAggregate aggregate = RequiredAggregate(store, affectedJob.Id);
        JobAttempt attempt = AssertSingleAttempt(aggregate);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(JobState.Running, attempt.State);
        Assert.AreEqual(1, aggregate.Revision);
        Assert.IsNull(attempt.CompletedAt);
        LocalJobExecutionContext context = observedContext
            ?? throw new AssertFailedException("The operation did not receive its context.");
        Assert.ThrowsExactly<InvalidOperationException>(() => context.RecordHeartbeat());

        database.ExecuteNonQuery("DROP TRIGGER fail_job_attempt_terminal_update;");
        Job reusableJob = CreateQueuedJob(database, store);
        LocalJobRunResult reusableResult = await runner.RunAsync(
            reusableJob.Id,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.AreEqual(JobState.Succeeded, reusableResult.TerminalState);
    }

    [TestMethod]
    public async Task NullArgumentsAreRejectedWithoutMutatingTheJob()
    {
        using LocalJobTestDatabase database = new();
        var store = new JobStore(database.DatabasePath);
        Job job = CreateQueuedJob(database, store);
        var runner = new LocalJobRunner(
            store,
            new FixedTimeProvider(QueuedAt.AddSeconds(1)));

        Assert.ThrowsExactly<ArgumentNullException>(() => new LocalJobRunner(null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new LocalJobRunner(store, null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => runner.RunAsync(
            null!,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => runner.RunAsync(
            job.Id,
            null!,
            CancellationToken.None));

        StoredJobAggregate aggregate = RequiredAggregate(store, job.Id);
        Assert.AreEqual(JobState.Queued, aggregate.Job.State);
        Assert.AreEqual(0, aggregate.Revision);
        Assert.AreEqual(0, aggregate.Attempts.Count);
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalImmutableAndStoreFree()
    {
        Assert.IsTrue(typeof(LocalJobRunner).IsPublic);
        Assert.IsTrue(typeof(LocalJobRunner).IsSealed);
        Assert.AreEqual(0, typeof(LocalJobRunner).GetInterfaces().Length);
        ConstructorInfo publicRunnerConstructor = AssertSinglePublicConstructor(
            typeof(LocalJobRunner));
        CollectionAssert.AreEqual(
            new[] { typeof(JobStore) },
            publicRunnerConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        MethodInfo runMethod = AssertSinglePublicMethod(typeof(LocalJobRunner), "RunAsync");
        Assert.AreEqual(typeof(Task<LocalJobRunResult>), runMethod.ReturnType);
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(EntityId<JobIdTag>),
                typeof(Func<LocalJobExecutionContext, CancellationToken, Task>),
                typeof(CancellationToken),
            },
            runMethod.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        Assert.IsTrue(typeof(LocalJobExecutionContext).IsPublic);
        Assert.IsTrue(typeof(LocalJobExecutionContext).IsSealed);
        Assert.AreEqual(0, typeof(LocalJobExecutionContext).GetConstructors().Length);
        AssertGetOnlyProperties(
            typeof(LocalJobExecutionContext),
            "JobId",
            "AttemptId",
            "SequenceNumber");
        CollectionAssert.AreEquivalent(
            new[] { "ReportProgress", "RecordHeartbeat" },
            GetDeclaredPublicMethods(typeof(LocalJobExecutionContext))
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(LocalJobExecutionContext).GetProperties().Any(
            static property =>
                property.PropertyType == typeof(JobStore) ||
                property.Name.Contains("Store", StringComparison.Ordinal) ||
                property.Name.Contains("Path", StringComparison.Ordinal)));

        Assert.IsTrue(typeof(LocalJobRunResult).IsPublic);
        Assert.IsTrue(typeof(LocalJobRunResult).IsSealed);
        Assert.AreEqual(0, typeof(LocalJobRunResult).GetConstructors().Length);
        Assert.AreEqual(0, GetDeclaredPublicMethods(typeof(LocalJobRunResult)).Length);
        AssertGetOnlyProperties(
            typeof(LocalJobRunResult),
            "JobId",
            "AttemptId",
            "SequenceNumber",
            "TerminalState",
            "FinalRevision");
        Assert.IsFalse(typeof(LocalJobRunResult).GetProperties().Any(static property =>
            property.Name.Contains("Error", StringComparison.Ordinal) ||
            property.Name.Contains("Exception", StringComparison.Ordinal) ||
            property.Name.Contains("Message", StringComparison.Ordinal)));
    }

    private static Job CreateQueuedJob(
        LocalJobTestDatabase database,
        JobStore store)
    {
        var job = new Job(
            EntityId<JobIdTag>.New(),
            database.ProjectId,
            "test.operation",
            JobState.Queued,
            QueuedAt,
            QueuedAt,
            currentAttemptNumber: 0);
        store.CreateJob(job);
        return job;
    }

    private static StoredJobAggregate RequiredAggregate(
        JobStore store,
        EntityId<JobIdTag> jobId)
    {
        return store.GetJobAggregate(jobId)
            ?? throw new AssertFailedException("The expected job aggregate was not stored.");
    }

    private static JobAttempt AssertSingleAttempt(StoredJobAggregate aggregate)
    {
        Assert.AreEqual(1, aggregate.Attempts.Count);
        return aggregate.Attempts[0];
    }

    private static void AssertFailedResult(
        JobStore store,
        EntityId<JobIdTag> jobId,
        LocalJobRunResult result,
        string expectedErrorCode)
    {
        StoredJobAggregate aggregate = RequiredAggregate(store, jobId);
        JobAttempt attempt = AssertSingleAttempt(aggregate);
        Assert.AreEqual(jobId, result.JobId);
        Assert.AreEqual(attempt.Id, result.AttemptId);
        Assert.AreEqual(1, result.SequenceNumber);
        Assert.AreEqual(JobState.Failed, result.TerminalState);
        Assert.AreEqual(2, result.FinalRevision);
        Assert.AreEqual(JobState.Failed, aggregate.Job.State);
        Assert.AreEqual(JobState.Failed, attempt.State);
        Assert.AreEqual(expectedErrorCode, attempt.ErrorCode);
        Assert.AreEqual(2, aggregate.Revision);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TestTimeout);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            int prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private static ConstructorInfo AssertSinglePublicConstructor(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(1, constructors.Length);
        return constructors[0];
    }

    private static MethodInfo AssertSinglePublicMethod(Type type, string expectedName)
    {
        MethodInfo[] methods = GetDeclaredPublicMethods(type);
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(expectedName, methods[0].Name);
        return methods[0];
    }

    private static MethodInfo[] GetDeclaredPublicMethods(Type type)
    {
        return type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
    }

    private static void AssertGetOnlyProperties(Type type, params string[] expectedNames)
    {
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        internal int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _ = Interlocked.Increment(ref _postCount);
            _ = ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class LocalJobTestDatabase : IDisposable
    {
        internal LocalJobTestDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.LocalJobRunnerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                ProjectId,
                "Local job runner test project",
                "Verify durable local job execution.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"project\":true}",
                QueuedAt);
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal long ExecuteScalarInt64(string sql)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null."));
        }

        internal void ExecuteNonQuery(string sql)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            _ = command.ExecuteNonQuery();
        }

        private SqliteConnection OpenConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}