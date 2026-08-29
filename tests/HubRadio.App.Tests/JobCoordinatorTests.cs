using System.Reflection;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class JobCoordinatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2042, 11, 12, 13, 14, 15, TimeSpan.Zero);

    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RunAsyncReturnsSafePersistedProgressAndHeartbeatSnapshot()
    {
        const string sensitiveMessage = "SensitiveProgressMessage-14f126ac";
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.success");
        DateTimeOffset progressAt = StartedAt.AddSeconds(1);
        DateTimeOffset heartbeatAt = StartedAt.AddSeconds(2);

        JobCoordinatorRunResult result = await fixture.Coordinator.RunAsync(
            job.Id,
            (context, _) =>
            {
                context.ReportProgress(
                    completed: 3,
                    total: 4,
                    phase: "analysis.decode",
                    message: sensitiveMessage,
                    progressAt);
                context.RecordHeartbeat(heartbeatAt);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        JobSnapshot snapshot = result.Snapshot;
        StoredJobAggregate aggregate = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(job.Id, snapshot.JobId);
        Assert.AreEqual(result.AttemptId, snapshot.CurrentAttemptId);
        Assert.AreEqual(1, result.SequenceNumber);
        Assert.AreEqual(JobState.Succeeded, snapshot.State);
        Assert.AreEqual(1, snapshot.CurrentAttemptNumber);
        Assert.AreEqual(2, snapshot.Revision);
        Assert.AreEqual(heartbeatAt, snapshot.UpdatedAt);
        Assert.AreEqual(3L, snapshot.CompletedUnits);
        Assert.AreEqual(4L, snapshot.TotalUnits);
        Assert.AreEqual(0.75m, snapshot.ProgressRatio);
        Assert.AreEqual(progressAt, snapshot.LastProgressAt);
        Assert.AreEqual(heartbeatAt, snapshot.LastHeartbeatAt);
        Assert.IsNull(snapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.NotApplicable, snapshot.Retryability);
        Assert.AreEqual(sensitiveMessage, aggregate.Progress.Single().Message);
        Assert.IsFalse(
            JsonSerializer.Serialize(result).Contains(
                sensitiveMessage,
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetSnapshotWhileRunningUsesLatestCurrentAttemptEvidence()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.running");
        DateTimeOffset firstProgressAt = StartedAt.AddSeconds(1);
        DateTimeOffset latestProgressAt = StartedAt.AddSeconds(2);
        DateTimeOffset heartbeatAt = StartedAt.AddSeconds(3);
        var evidenceRecorded = NewSignal();
        var releaseOperation = NewSignal();

        Task<JobCoordinatorRunResult> run = fixture.Coordinator.RunAsync(
            job.Id,
            async (context, _) =>
            {
                context.ReportProgress(
                    1,
                    10,
                    "analysis.first",
                    "private first message",
                    firstProgressAt);
                context.ReportProgress(
                    4,
                    10,
                    "analysis.latest",
                    "private latest message",
                    latestProgressAt);
                context.RecordHeartbeat(heartbeatAt);
                evidenceRecorded.TrySetResult(true);
                await releaseOperation.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await evidenceRecorded.Task.WaitAsync(TestTimeout);

        JobSnapshot snapshot = fixture.Coordinator.GetSnapshot(job.Id);

        Assert.AreEqual(JobState.Running, snapshot.State);
        Assert.AreEqual(1, snapshot.Revision);
        Assert.AreEqual(4L, snapshot.CompletedUnits);
        Assert.AreEqual(10L, snapshot.TotalUnits);
        Assert.AreEqual(0.4m, snapshot.ProgressRatio);
        Assert.AreEqual(latestProgressAt, snapshot.LastProgressAt);
        Assert.AreEqual(heartbeatAt, snapshot.LastHeartbeatAt);
        Assert.IsNull(snapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.NotApplicable, snapshot.Retryability);

        releaseOperation.TrySetResult(true);
        JobCoordinatorRunResult completed = await run.WaitAsync(TestTimeout);
        Assert.AreEqual(JobState.Succeeded, completed.Snapshot.State);
    }

    [TestMethod]
    public async Task CancellationBeforeDuringAndAfterRunHasTypedOutcomes()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.cancel");

        JobCancellationResult before = fixture.Coordinator.RequestCancellation(job.Id);
        Assert.AreEqual(JobCancellationOutcome.NotRunning, before.Outcome);
        Assert.AreEqual(JobState.Queued, before.Snapshot.State);

        var operationEntered = NewSignal();
        var cancellationObserved = NewSignal();
        Task<JobCoordinatorRunResult> run = fixture.Coordinator.RunAsync(
            job.Id,
            async (_, cancellationToken) =>
            {
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(
                        () => cancellationObserved.TrySetResult(true));
                operationEntered.TrySetResult(true);
                await cancellationObserved.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            },
            CancellationToken.None);
        await operationEntered.Task.WaitAsync(TestTimeout);

        JobCancellationResult during = fixture.Coordinator.RequestCancellation(job.Id);
        await cancellationObserved.Task.WaitAsync(TestTimeout);
        JobCoordinatorRunResult cancelled = await run.WaitAsync(TestTimeout);
        JobCancellationResult after = fixture.Coordinator.RequestCancellation(job.Id);

        Assert.AreEqual(JobCancellationOutcome.Requested, during.Outcome);
        Assert.AreEqual(JobState.Running, during.Snapshot.State);
        Assert.AreEqual(JobState.Cancelled, cancelled.Snapshot.State);
        Assert.IsNull(cancelled.Snapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.NotApplicable, cancelled.Snapshot.Retryability);
        Assert.AreEqual(JobCancellationOutcome.NotRunning, after.Outcome);
        Assert.AreEqual(JobState.Cancelled, after.Snapshot.State);
        Assert.AreEqual(2, fixture.RequiredAggregate(job.Id).Revision);
    }

    [TestMethod]
    public async Task ConcurrentCancellationHasOneRequesterAndIdempotentFollowers()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.concurrent_cancel");
        var operationEntered = NewSignal();
        var releaseOperation = NewSignal();
        Task<JobCoordinatorRunResult> run = fixture.Coordinator.RunAsync(
            job.Id,
            async (_, cancellationToken) =>
            {
                operationEntered.TrySetResult(true);
                await releaseOperation.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            },
            CancellationToken.None);
        await operationEntered.Task.WaitAsync(TestTimeout);

        Task<JobCancellationResult>[] requests = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => fixture.Coordinator.RequestCancellation(job.Id)))
            .ToArray();
        JobCancellationResult[] results = await Task.WhenAll(requests)
            .WaitAsync(TestTimeout);

        Assert.AreEqual(
            1,
            results.Count(static result =>
                result.Outcome == JobCancellationOutcome.Requested));
        Assert.AreEqual(
            results.Length - 1,
            results.Count(static result =>
                result.Outcome == JobCancellationOutcome.AlreadyRequested));
        Assert.IsTrue(results.All(static result =>
            result.Snapshot.State == JobState.Running));

        releaseOperation.TrySetResult(true);
        JobCoordinatorRunResult cancelled = await run.WaitAsync(TestTimeout);
        Assert.AreEqual(JobState.Cancelled, cancelled.Snapshot.State);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForHeavyGateCancelsQueuedJobBeforeAttempt()
    {
        using JobCoordinatorFixture fixture = new();
        Job gateOwner = fixture.CreateQueuedJob("analysis.gate_owner");
        Job waitingJob = fixture.CreateQueuedJob("analysis.waiting_cancel");
        JobCoordinator waitingCoordinator = fixture.CreateCoordinator();
        var ownerEntered = NewSignal();
        var releaseOwner = NewSignal();
        int waitingInvocationCount = 0;

        Task<JobCoordinatorRunResult> ownerRun = fixture.Coordinator.RunAsync(
            gateOwner.Id,
            async (_, _) =>
            {
                ownerEntered.TrySetResult(true);
                await releaseOwner.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await ownerEntered.Task.WaitAsync(TestTimeout);

        Task<JobCoordinatorRunResult> waitingRun = waitingCoordinator.RunAsync(
            waitingJob.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref waitingInvocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        JobCancellationResult cancellation =
            waitingCoordinator.RequestCancellation(waitingJob.Id);
        releaseOwner.TrySetResult(true);
        _ = await ownerRun.WaitAsync(TestTimeout);
        Exception? waitingFailure = await CaptureExceptionAsync(waitingRun);

        Assert.AreEqual(JobCancellationOutcome.Requested, cancellation.Outcome);
        Assert.AreEqual(JobState.Cancelled, cancellation.Snapshot.State);
        Assert.IsInstanceOfType<OperationCanceledException>(waitingFailure);
        Assert.AreEqual(0, waitingInvocationCount);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(waitingJob.Id);
        Assert.AreEqual(JobState.Cancelled, aggregate.Job.State);
        Assert.AreEqual(0, aggregate.Attempts.Count);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public async Task CallerTokenCancellationWhileWaitingForGatePersistsCancelledState()
    {
        using JobCoordinatorFixture fixture = new();
        Job gateOwner = fixture.CreateQueuedJob("analysis.token_gate_owner");
        Job waitingJob = fixture.CreateQueuedJob("analysis.token_waiting_cancel");
        JobCoordinator waitingCoordinator = fixture.CreateCoordinator();
        var ownerEntered = NewSignal();
        var releaseOwner = NewSignal();
        int waitingInvocationCount = 0;
        using var cancellation = new CancellationTokenSource();

        Task<JobCoordinatorRunResult> ownerRun = fixture.Coordinator.RunAsync(
            gateOwner.Id,
            async (_, _) =>
            {
                ownerEntered.TrySetResult(true);
                await releaseOwner.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await ownerEntered.Task.WaitAsync(TestTimeout);

        Task<JobCoordinatorRunResult> waitingRun = waitingCoordinator.RunAsync(
            waitingJob.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref waitingInvocationCount);
                return Task.CompletedTask;
            },
            cancellation.Token);
        cancellation.Cancel();
        releaseOwner.TrySetResult(true);
        _ = await ownerRun.WaitAsync(TestTimeout);
        Exception? waitingFailure = await CaptureExceptionAsync(waitingRun);

        Assert.IsInstanceOfType<OperationCanceledException>(waitingFailure);
        Assert.AreEqual(0, waitingInvocationCount);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(waitingJob.Id);
        Assert.AreEqual(JobState.Cancelled, aggregate.Job.State);
        Assert.AreEqual(0, aggregate.Attempts.Count);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public void RunningJobWithoutProcessTrackingFailsClosedCancellation()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.untracked");
        JobAttempt attempt = fixture.StartAttempt(job);

        JobCancellationResult result = fixture.Coordinator.RequestCancellation(job.Id);

        Assert.AreEqual(JobCancellationOutcome.NotTracked, result.Outcome);
        Assert.AreEqual(JobState.Running, result.Snapshot.State);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(attempt.Id, aggregate.Attempts.Single().Id);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public async Task TimeoutRetryPreservesAttemptThenSecondRetryIsNoOpAndRunCanBeReused()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.retry_timeout");

        JobCoordinatorRunResult failed = await fixture.Coordinator.RunAsync(
            job.Id,
            static (_, _) => throw new TimeoutException("private timeout detail"),
            CancellationToken.None);
        StoredJobAggregate failedAggregate = fixture.RequiredAggregate(job.Id);
        JobAttempt firstAttempt = failedAggregate.Attempts.Single();

        Assert.AreEqual(JobState.Failed, failed.Snapshot.State);
        Assert.AreEqual("job.execution_timed_out", failed.Snapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.Retryable, failed.Snapshot.Retryability);

        fixture.TimeProvider.SetUtcNow(StartedAt.AddSeconds(10));
        JobRetryResult queued = fixture.Coordinator.QueueRetry(job.Id);
        StoredJobAggregate queuedAggregate = fixture.RequiredAggregate(job.Id);
        JobRetryResult duplicate = fixture.Coordinator.QueueRetry(job.Id);

        Assert.AreEqual(JobRetryOutcome.Queued, queued.Outcome);
        Assert.AreEqual(JobState.Queued, queued.Snapshot.State);
        Assert.AreEqual(JobRetryability.NotApplicable, queued.Snapshot.Retryability);
        Assert.AreEqual("job.execution_timed_out", queued.Snapshot.ErrorCode);
        Assert.AreEqual(3, queued.Snapshot.Revision);
        Assert.AreEqual(1, queuedAggregate.Attempts.Count);
        AssertAttemptEqual(firstAttempt, queuedAggregate.Attempts.Single());
        Assert.AreEqual(JobRetryOutcome.StateNotEligible, duplicate.Outcome);
        Assert.AreEqual(3, fixture.RequiredAggregate(job.Id).Revision);

        fixture.TimeProvider.SetUtcNow(StartedAt.AddSeconds(11));
        int retryInvocationCount = 0;
        JobCoordinatorRunResult succeeded = await fixture.Coordinator.RunAsync(
            job.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref retryInvocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        StoredJobAggregate succeededAggregate = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(JobState.Succeeded, succeeded.Snapshot.State);
        Assert.AreEqual(1, retryInvocationCount);
        Assert.AreEqual(2, succeededAggregate.Attempts.Count);
        AssertAttemptEqual(firstAttempt, succeededAggregate.Attempts[0]);
        Assert.AreEqual(2, succeededAggregate.Attempts[1].SequenceNumber);
    }

    [TestMethod]
    public async Task NonRetryableAndUnknownErrorCodesNeverQueue()
    {
        const string sensitiveDetail = "SensitiveFailureDetail-646f8071";
        using JobCoordinatorFixture fixture = new();
        Job executionFailure = fixture.CreateQueuedJob("analysis.nonretryable");
        JobCoordinatorRunResult failed = await fixture.Coordinator.RunAsync(
            executionFailure.Id,
            (_, _) => throw new InvalidOperationException(sensitiveDetail),
            CancellationToken.None);
        int failureRevision = failed.Snapshot.Revision;

        JobRetryResult executionRetry = fixture.Coordinator.QueueRetry(
            executionFailure.Id);

        Assert.AreEqual(JobState.Failed, failed.Snapshot.State);
        Assert.AreEqual("job.execution_failed", failed.Snapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.NonRetryable, failed.Snapshot.Retryability);
        Assert.AreEqual(JobRetryOutcome.ErrorNotRetryable, executionRetry.Outcome);
        Assert.AreEqual(
            failureRevision,
            fixture.RequiredAggregate(executionFailure.Id).Revision);
        Assert.IsFalse(
            JsonSerializer.Serialize(failed).Contains(
                sensitiveDetail,
                StringComparison.Ordinal));

        Job unknownFailure = fixture.CreateTerminalJob(
            "analysis.unknown",
            JobState.Failed,
            "vendor.unknown_failure");
        StoredJobAggregate unknownBefore = fixture.RequiredAggregate(unknownFailure.Id);

        JobSnapshot unknownSnapshot = fixture.Coordinator.GetSnapshot(unknownFailure.Id);
        JobRetryResult unknownRetry = fixture.Coordinator.QueueRetry(unknownFailure.Id);

        Assert.AreEqual("vendor.unknown_failure", unknownSnapshot.ErrorCode);
        Assert.AreEqual(JobRetryability.NonRetryable, unknownSnapshot.Retryability);
        Assert.AreEqual(JobRetryOutcome.ErrorNotRetryable, unknownRetry.Outcome);
        Assert.AreEqual(
            unknownBefore.Revision,
            fixture.RequiredAggregate(unknownFailure.Id).Revision);
    }

    [TestMethod]
    public void InterruptedAppRestartErrorQueuesRetryWithoutRewritingAttempt()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateTerminalJob(
            "analysis.interrupted",
            JobState.Interrupted,
            "job.app_restarted");
        StoredJobAggregate before = fixture.RequiredAggregate(job.Id);
        JobAttempt interruptedAttempt = before.Attempts.Single();
        fixture.TimeProvider.SetUtcNow(StartedAt.AddSeconds(10));

        JobSnapshot snapshot = fixture.Coordinator.GetSnapshot(job.Id);
        JobRetryResult result = fixture.Coordinator.QueueRetry(job.Id);

        StoredJobAggregate after = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(JobRetryability.Retryable, snapshot.Retryability);
        Assert.AreEqual(JobRetryOutcome.Queued, result.Outcome);
        Assert.AreEqual(JobState.Queued, after.Job.State);
        Assert.AreEqual(before.Revision + 1, after.Revision);
        Assert.AreEqual(1, after.Attempts.Count);
        AssertAttemptEqual(interruptedAttempt, after.Attempts.Single());
    }

    [TestMethod]
    public async Task ConcurrentRunForSameJobRejectsDuplicateOperationAndAttempt()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("render.single_artifact");
        var operationEntered = NewSignal();
        var releaseOperation = NewSignal();
        JobCoordinator secondCoordinator = fixture.CreateCoordinator();
        int firstInvocationCount = 0;
        int duplicateInvocationCount = 0;
        Task<JobCoordinatorRunResult> firstRun = fixture.Coordinator.RunAsync(
            job.Id,
            async (_, _) =>
            {
                _ = Interlocked.Increment(ref firstInvocationCount);
                operationEntered.TrySetResult(true);
                await releaseOperation.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await operationEntered.Task.WaitAsync(TestTimeout);

        Task<JobCoordinatorRunResult> duplicateRun = secondCoordinator.RunAsync(
            job.Id,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref duplicateInvocationCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Exception? duplicateError = await CaptureExceptionAsync(duplicateRun);
        JobRetryResult retryWhileRunning = secondCoordinator.QueueRetry(job.Id);

        Assert.IsInstanceOfType<InvalidOperationException>(duplicateError);
        Assert.AreEqual(1, firstInvocationCount);
        Assert.AreEqual(0, duplicateInvocationCount);
        Assert.AreEqual(JobRetryOutcome.CoordinatorRunActive, retryWhileRunning.Outcome);

        releaseOperation.TrySetResult(true);
        JobCoordinatorRunResult result = await firstRun.WaitAsync(TestTimeout);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(JobState.Succeeded, result.Snapshot.State);
        Assert.AreEqual(1, aggregate.Attempts.Count);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
    }

    [TestMethod]
    public void CorruptStoredAggregateFailsClosedWithoutRetryOrCancellationMutation()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateTerminalJob(
            "analysis.corrupt",
            JobState.Failed,
            "job.execution_timed_out");
        fixture.ExecuteNonQuery(
            "UPDATE job_attempts SET started_at = $timestamp, completed_at = $timestamp WHERE job_id = $job_id;",
            ("$timestamp", "2042-99-99T00:00:00.0000000Z"),
            ("$job_id", job.Id.ToString()));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Coordinator.GetSnapshot(job.Id));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Coordinator.RequestCancellation(job.Id));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.Coordinator.QueueRetry(job.Id));
        Assert.AreEqual(
            2L,
            fixture.ExecuteScalarInt64(
                "SELECT revision FROM jobs WHERE id = $id;",
                ("$id", job.Id.ToString())));
        Assert.AreEqual(
            "failed",
            fixture.ExecuteScalarString(
                "SELECT state FROM jobs WHERE id = $id;",
                ("$id", job.Id.ToString())));
    }

    [TestMethod]
    public async Task PreCancelledRunLeavesQueuedJobAndCoordinatorReusable()
    {
        using JobCoordinatorFixture fixture = new();
        Job job = fixture.CreateQueuedJob("analysis.precancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.Coordinator.RunAsync(
                    job.Id,
                    static (_, _) => Task.CompletedTask,
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(job.Id);
        Assert.AreEqual(JobState.Queued, aggregate.Job.State);
        Assert.AreEqual(0, aggregate.Revision);
        Assert.AreEqual(0, aggregate.Attempts.Count);

        JobCoordinatorRunResult result = await fixture.Coordinator.RunAsync(
            job.Id,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.AreEqual(JobState.Succeeded, result.Snapshot.State);
    }

    [TestMethod]
    public void PublicSurfaceIsClosedImmutableReadOnlyAndMessageFree()
    {
        Assert.IsTrue(typeof(JobCoordinator).IsPublic);
        Assert.IsTrue(typeof(JobCoordinator).IsSealed);
        ConstructorInfo[] constructors = typeof(JobCoordinator).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(1, constructors.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(JobStore), typeof(LocalJobRunner) },
            constructors[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "RunAsync", "GetSnapshot", "RequestCancellation", "QueueRetry" },
            GetDeclaredPublicMethods(typeof(JobCoordinator))
                .Select(static method => method.Name)
                .ToArray());

        AssertResultType(
            typeof(JobSnapshot),
            "JobId",
            "State",
            "CurrentAttemptId",
            "CurrentAttemptNumber",
            "Revision",
            "UpdatedAt",
            "CompletedUnits",
            "TotalUnits",
            "ProgressRatio",
            "LastProgressAt",
            "LastHeartbeatAt",
            "ErrorCode",
            "Retryability");
        AssertResultType(
            typeof(JobCoordinatorRunResult),
            "AttemptId",
            "SequenceNumber",
            "Snapshot");
        AssertResultType(
            typeof(JobCancellationResult),
            "Outcome",
            "Snapshot");
        AssertResultType(
            typeof(JobRetryResult),
            "Outcome",
            "Snapshot");

        PropertyInfo[] snapshotProperties = typeof(JobSnapshot).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.IsFalse(snapshotProperties.Any(static property =>
            property.Name.Contains("Message", StringComparison.Ordinal) ||
            property.Name.Contains("Detail", StringComparison.Ordinal) ||
            property.Name.Contains("Exception", StringComparison.Ordinal) ||
            property.PropertyType == typeof(JobProgress) ||
            property.PropertyType == typeof(JobHeartbeat)));
        Assert.IsTrue(typeof(JobRetryability).IsEnum);
        Assert.IsTrue(typeof(JobCancellationOutcome).IsEnum);
        Assert.IsTrue(typeof(JobRetryOutcome).IsEnum);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static MethodInfo[] GetDeclaredPublicMethods(Type type)
    {
        return type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
    }

    private static void AssertResultType(Type type, params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        Assert.AreEqual(0, GetDeclaredPublicMethods(type).Length, type.Name);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedProperties,
            properties.Select(static property => property.Name).ToArray(),
            type.Name);
        Assert.IsTrue(properties.All(static property => property.SetMethod is null), type.Name);
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

    private sealed class ManualTimeProvider(DateTimeOffset initialValue) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _value = initialValue;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _value;
            }
        }

        internal void SetUtcNow(DateTimeOffset value)
        {
            lock (_sync)
            {
                _value = value;
            }
        }
    }

    private sealed class JobCoordinatorFixture : IDisposable
    {
        private int _jobSequence;

        internal JobCoordinatorFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.JobCoordinatorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                ProjectId,
                "Job coordinator test project",
                "Verify safe job UI coordination.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"jobCoordinator\":true}",
                CreatedAt.AddDays(-1));
            Store = new JobStore(DatabasePath);
            TimeProvider = new ManualTimeProvider(StartedAt);
            var runner = new LocalJobRunner(Store, TimeProvider);
            Coordinator = new JobCoordinator(Store, runner, TimeProvider);
        }

        private string RootPath { get; }

        private string DatabasePath { get; }

        private EntityId<ProjectIdTag> ProjectId { get; }

        internal JobStore Store { get; }

        internal ManualTimeProvider TimeProvider { get; }

        internal JobCoordinator Coordinator { get; }

        internal JobCoordinator CreateCoordinator()
        {
            var runner = new LocalJobRunner(Store, TimeProvider);
            return new JobCoordinator(Store, runner, TimeProvider);
        }

        internal Job CreateQueuedJob(string kind)
        {
            DateTimeOffset createdAt = CreatedAt.AddTicks(
                Interlocked.Increment(ref _jobSequence));
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

        internal JobAttempt StartAttempt(Job job)
        {
            var attempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                job.Id,
                sequenceNumber: 1,
                JobState.Running,
                job.UpdatedAt,
                StartedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(job.Id, 0, attempt, StartedAt);
            return attempt;
        }

        internal Job CreateTerminalJob(
            string kind,
            JobState terminalState,
            string errorCode)
        {
            Job job = CreateQueuedJob(kind);
            JobAttempt runningAttempt = StartAttempt(job);
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
            _ = Store.CompleteAttempt(job.Id, 1, completedAttempt, completedAt);
            return job;
        }

        internal StoredJobAggregate RequiredAggregate(EntityId<JobIdTag> jobId)
        {
            return Store.GetJobAggregate(jobId)
                ?? throw new AssertFailedException(
                    "The expected job aggregate was not stored.");
        }

        internal long ExecuteScalarInt64(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            object value = ExecuteScalar(sql, parameters);
            return value is long result
                ? result
                : throw new InvalidOperationException("The scalar was not an integer.");
        }

        internal string ExecuteScalarString(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            object value = ExecuteScalar(sql, parameters);
            return value as string
                ?? throw new InvalidOperationException("The scalar was not text.");
        }

        internal void ExecuteNonQuery(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            _ = command.ExecuteNonQuery();
        }

        private object ExecuteScalar(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            return command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null.");
        }

        private static void AddParameters(
            SqliteCommand command,
            IEnumerable<(string Name, object Value)> parameters)
        {
            foreach ((string name, object value) in parameters)
            {
                _ = command.Parameters.AddWithValue(name, value);
            }
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