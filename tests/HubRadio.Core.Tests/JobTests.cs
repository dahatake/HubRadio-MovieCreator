using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class JobTests
{
    private static readonly DateTimeOffset QueuedAt =
        new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset StartedAt = QueuedAt.AddSeconds(1);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(1);

    [TestMethod]
    public void JobStateAndMarkerTypesAreExactlyTheRequiredPublicContract()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                JobState.Queued,
                JobState.Running,
                JobState.Succeeded,
                JobState.Failed,
                JobState.Cancelled,
                JobState.Interrupted,
            },
            Enum.GetValues<JobState>());

        foreach (Type markerType in new[]
        {
            typeof(JobIdTag),
            typeof(JobAttemptIdTag),
            typeof(JobProgressIdTag),
            typeof(JobHeartbeatIdTag),
        })
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }

        Assert.IsTrue(typeof(JobStateTransitions).IsPublic);
        Assert.IsTrue(typeof(JobStateTransitions).IsAbstract);
        Assert.IsTrue(typeof(JobStateTransitions).IsSealed);
        Assert.IsTrue(typeof(JobAttemptStateTransitions).IsPublic);
        Assert.IsTrue(typeof(JobAttemptStateTransitions).IsAbstract);
        Assert.IsTrue(typeof(JobAttemptStateTransitions).IsSealed);
    }

    [TestMethod]
    public void JobContractsAreSealedImmutableTypedAndMinimal()
    {
        AssertPublicGetOnlyProperties(
            typeof(Job),
            "Id",
            "ProjectId",
            "Kind",
            "State",
            "CreatedAt",
            "UpdatedAt",
            "CurrentAttemptNumber");
        AssertPublicGetOnlyProperties(
            typeof(JobAttempt),
            "Id",
            "JobId",
            "SequenceNumber",
            "State",
            "QueuedAt",
            "StartedAt",
            "CompletedAt",
            "ErrorCode");
        AssertPublicGetOnlyProperties(
            typeof(JobProgress),
            "Id",
            "JobId",
            "JobAttemptId",
            "SequenceNumber",
            "RecordedAt",
            "CompletedUnits",
            "TotalUnits",
            "Phase",
            "Message");
        AssertPublicGetOnlyProperties(
            typeof(JobHeartbeat),
            "Id",
            "JobId",
            "JobAttemptId",
            "SequenceNumber",
            "RecordedAt");

        Assert.AreEqual(
            typeof(EntityId<JobIdTag>),
            typeof(Job).GetProperty("Id")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<ProjectIdTag>),
            typeof(Job).GetProperty("ProjectId")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<JobAttemptIdTag>),
            typeof(JobAttempt).GetProperty("Id")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<JobAttemptIdTag>),
            typeof(JobProgress).GetProperty("JobAttemptId")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<JobAttemptIdTag>),
            typeof(JobHeartbeat).GetProperty("JobAttemptId")!.PropertyType);

        string[] forbiddenProperties =
        [
            "Payload",
            "InputDigest",
            "Output",
            "OutputCacheKey",
            "Priority",
            "Percentage",
            "EstimatedTimeRemaining",
            "Timeout",
            "ExitCode",
            "StandardOutput",
            "StandardError",
        ];
        foreach (Type type in new[] { typeof(Job), typeof(JobAttempt), typeof(JobProgress), typeof(JobHeartbeat) })
        {
            foreach (string propertyName in forbiddenProperties)
            {
                Assert.IsNull(type.GetProperty(propertyName), $"{type.Name}.{propertyName}");
            }

            MethodInfo[] declaredMethods = type.GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly);
            Assert.IsTrue(declaredMethods.All(static method => method.IsSpecialName));
        }
    }

    [TestMethod]
    public void JobTransitionsAcceptExactlyTheAllowedMatrix()
    {
        HashSet<(JobState From, JobState To)> allowed =
        [
            (JobState.Queued, JobState.Running),
            (JobState.Queued, JobState.Cancelled),
            (JobState.Running, JobState.Succeeded),
            (JobState.Running, JobState.Failed),
            (JobState.Running, JobState.Cancelled),
            (JobState.Running, JobState.Interrupted),
            (JobState.Failed, JobState.Queued),
            (JobState.Interrupted, JobState.Queued),
        ];

        AssertTransitionMatrix(
            allowed,
            JobStateTransitions.CanTransition,
            JobStateTransitions.EnsureCanTransition);
    }

    [TestMethod]
    public void JobAttemptTransitionsAcceptExactlyTheAllowedMatrix()
    {
        HashSet<(JobState From, JobState To)> allowed =
        [
            (JobState.Queued, JobState.Running),
            (JobState.Queued, JobState.Cancelled),
            (JobState.Running, JobState.Succeeded),
            (JobState.Running, JobState.Failed),
            (JobState.Running, JobState.Cancelled),
            (JobState.Running, JobState.Interrupted),
        ];

        AssertTransitionMatrix(
            allowed,
            JobAttemptStateTransitions.CanTransition,
            JobAttemptStateTransitions.EnsureCanTransition);
    }

    [TestMethod]
    public void JobTransitionsPermitFailedAndInterruptedRetryCyclesButAttemptsDoNot()
    {
        JobStateTransitions.EnsureCanTransition(JobState.Queued, JobState.Running);
        JobStateTransitions.EnsureCanTransition(JobState.Running, JobState.Failed);
        JobStateTransitions.EnsureCanTransition(JobState.Failed, JobState.Queued);
        JobStateTransitions.EnsureCanTransition(JobState.Queued, JobState.Running);
        JobStateTransitions.EnsureCanTransition(JobState.Running, JobState.Interrupted);
        JobStateTransitions.EnsureCanTransition(JobState.Interrupted, JobState.Queued);

        Assert.IsFalse(
            JobAttemptStateTransitions.CanTransition(JobState.Failed, JobState.Queued));
        Assert.IsFalse(
            JobAttemptStateTransitions.CanTransition(JobState.Interrupted, JobState.Queued));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => JobAttemptStateTransitions.EnsureCanTransition(
                JobState.Failed,
                JobState.Queued));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => JobAttemptStateTransitions.EnsureCanTransition(
                JobState.Interrupted,
                JobState.Queued));
    }

    [TestMethod]
    public void TransitionContractsFailClosedForUnknownEnumValues()
    {
        JobState unknown = (JobState)int.MaxValue;

        Assert.IsFalse(JobStateTransitions.CanTransition(unknown, JobState.Queued));
        Assert.IsFalse(JobStateTransitions.CanTransition(JobState.Queued, unknown));
        Assert.IsFalse(JobAttemptStateTransitions.CanTransition(unknown, JobState.Queued));
        Assert.IsFalse(JobAttemptStateTransitions.CanTransition(JobState.Queued, unknown));

        AssertParamName<ArgumentOutOfRangeException>(
            "fromState",
            () => JobStateTransitions.EnsureCanTransition(unknown, JobState.Queued));
        AssertParamName<ArgumentOutOfRangeException>(
            "toState",
            () => JobStateTransitions.EnsureCanTransition(JobState.Queued, unknown));
        AssertParamName<ArgumentOutOfRangeException>(
            "fromState",
            () => JobAttemptStateTransitions.EnsureCanTransition(unknown, JobState.Queued));
        AssertParamName<ArgumentOutOfRangeException>(
            "toState",
            () => JobAttemptStateTransitions.EnsureCanTransition(JobState.Queued, unknown));
    }

    [TestMethod]
    public void JobPreservesValidatedPersistedSnapshot()
    {
        EntityId<JobIdTag> id = EntityId<JobIdTag>.New();
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        DateTimeOffset updatedAt = QueuedAt.AddMinutes(1);
        Job job = new(
            id,
            projectId,
            "media-import",
            JobState.Running,
            QueuedAt,
            updatedAt,
            currentAttemptNumber: 2);

        Assert.AreSame(id, job.Id);
        Assert.AreSame(projectId, job.ProjectId);
        Assert.AreEqual("media-import", job.Kind);
        Assert.AreEqual(JobState.Running, job.State);
        Assert.AreEqual(QueuedAt, job.CreatedAt);
        Assert.AreEqual(updatedAt, job.UpdatedAt);
        Assert.AreEqual(2, job.CurrentAttemptNumber);

        Job initial = CreateJob(JobState.Queued, currentAttemptNumber: 0);
        Job queuedRetry = CreateJob(JobState.Queued, currentAttemptNumber: 3);
        Job cancelledBeforeStart = CreateJob(JobState.Cancelled, currentAttemptNumber: 0);
        Assert.AreEqual(0, initial.CurrentAttemptNumber);
        Assert.AreEqual(3, queuedRetry.CurrentAttemptNumber);
        Assert.AreEqual(0, cancelledBeforeStart.CurrentAttemptNumber);
    }

    [TestMethod]
    public void JobRejectsNullUnknownBlankReverseTimeAndInvalidAttemptNumber()
    {
        EntityId<JobIdTag> id = EntityId<JobIdTag>.New();
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();

        AssertParamName<ArgumentNullException>(
            "id",
            () => new Job(null!, projectId, "kind", JobState.Queued, QueuedAt, QueuedAt, 0));
        AssertParamName<ArgumentNullException>(
            "projectId",
            () => new Job(id, null!, "kind", JobState.Queued, QueuedAt, QueuedAt, 0));
        AssertParamName<ArgumentNullException>(
            "kind",
            () => new Job(id, projectId, null!, JobState.Queued, QueuedAt, QueuedAt, 0));

        foreach (string kind in new[] { string.Empty, " ", "\tkind", "kind ", "\r\n" })
        {
            AssertParamName<ArgumentException>(
                "kind",
                () => new Job(id, projectId, kind, JobState.Queued, QueuedAt, QueuedAt, 0));
        }

        AssertParamName<ArgumentOutOfRangeException>(
            "state",
            () => new Job(id, projectId, "kind", (JobState)99, QueuedAt, QueuedAt, 1));
        AssertParamName<ArgumentOutOfRangeException>(
            "updatedAt",
            () => new Job(
                id,
                projectId,
                "kind",
                JobState.Queued,
                QueuedAt,
                QueuedAt.AddTicks(-1),
                0));
        AssertParamName<ArgumentOutOfRangeException>(
            "currentAttemptNumber",
            () => CreateJob(JobState.Queued, currentAttemptNumber: -1));

        foreach (JobState state in new[]
        {
            JobState.Running,
            JobState.Succeeded,
            JobState.Failed,
            JobState.Interrupted,
        })
        {
            AssertParamName<ArgumentOutOfRangeException>(
                "currentAttemptNumber",
                () => CreateJob(state, currentAttemptNumber: 0));
        }
    }

    [TestMethod]
    public void JobAttemptAcceptsEveryValidStateSnapshot()
    {
        foreach (JobState state in Enum.GetValues<JobState>())
        {
            JobAttempt attempt = CreateAttempt(state);

            Assert.AreEqual(state, attempt.State);
            Assert.AreEqual(1, attempt.SequenceNumber);
            Assert.AreEqual(QueuedAt, attempt.QueuedAt);
        }

        JobAttempt cancelledBeforeStart = NewAttempt(
            JobState.Cancelled,
            startedAt: null,
            completedAt: CompletedAt,
            errorCode: null);
        JobAttempt cancelledAfterStart = NewAttempt(
            JobState.Cancelled,
            StartedAt,
            CompletedAt,
            errorCode: null);
        Assert.IsNull(cancelledBeforeStart.StartedAt);
        Assert.AreEqual(StartedAt, cancelledAfterStart.StartedAt);

        JobAttempt failed = CreateAttempt(JobState.Failed);
        JobAttempt interrupted = CreateAttempt(JobState.Interrupted);
        Assert.AreEqual("worker.process_failed", failed.ErrorCode);
        Assert.AreEqual("worker.process_interrupted", interrupted.ErrorCode);
    }

    [TestMethod]
    public void JobAttemptRejectsNullReferencesUnknownStateAndInvalidSequence()
    {
        EntityId<JobAttemptIdTag> id = EntityId<JobAttemptIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        AssertParamName<ArgumentNullException>(
            "id",
            () => new JobAttempt(
                null!, jobId, 1, JobState.Queued, QueuedAt, null, null, null));
        AssertParamName<ArgumentNullException>(
            "jobId",
            () => new JobAttempt(
                id, null!, 1, JobState.Queued, QueuedAt, null, null, null));
        AssertParamName<ArgumentOutOfRangeException>(
            "sequenceNumber",
            () => new JobAttempt(
                id, jobId, 0, JobState.Queued, QueuedAt, null, null, null));
        AssertParamName<ArgumentOutOfRangeException>(
            "state",
            () => new JobAttempt(
                id, jobId, 1, (JobState)99, QueuedAt, null, null, null));
    }

    [TestMethod]
    public void JobAttemptRejectsReverseTimestamps()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "startedAt",
            () => NewAttempt(
                JobState.Running,
                QueuedAt.AddTicks(-1),
                completedAt: null,
                errorCode: null));
        AssertParamName<ArgumentOutOfRangeException>(
            "completedAt",
            () => NewAttempt(
                JobState.Cancelled,
                startedAt: null,
                completedAt: QueuedAt.AddTicks(-1),
                errorCode: null));
        AssertParamName<ArgumentOutOfRangeException>(
            "completedAt",
            () => NewAttempt(
                JobState.Succeeded,
                StartedAt,
                StartedAt.AddTicks(-1),
                errorCode: null));
    }

    [TestMethod]
    public void JobAttemptEnforcesQueuedRunningAndSucceededSnapshots()
    {
        AssertParamName<ArgumentException>(
            "startedAt",
            () => NewAttempt(JobState.Queued, StartedAt, null, null));
        AssertParamName<ArgumentException>(
            "completedAt",
            () => NewAttempt(JobState.Queued, null, CompletedAt, null));
        AssertParamName<ArgumentException>(
            "errorCode",
            () => NewAttempt(JobState.Queued, null, null, "worker.failed"));

        AssertParamName<ArgumentNullException>(
            "startedAt",
            () => NewAttempt(JobState.Running, null, null, null));
        AssertParamName<ArgumentException>(
            "completedAt",
            () => NewAttempt(JobState.Running, StartedAt, CompletedAt, null));
        AssertParamName<ArgumentException>(
            "errorCode",
            () => NewAttempt(JobState.Running, StartedAt, null, "worker.failed"));

        AssertParamName<ArgumentNullException>(
            "startedAt",
            () => NewAttempt(JobState.Succeeded, null, CompletedAt, null));
        AssertParamName<ArgumentNullException>(
            "completedAt",
            () => NewAttempt(JobState.Succeeded, StartedAt, null, null));
        AssertParamName<ArgumentException>(
            "errorCode",
            () => NewAttempt(JobState.Succeeded, StartedAt, CompletedAt, "worker.failed"));
    }

    [TestMethod]
    public void JobAttemptEnforcesFailedCancelledAndInterruptedSnapshots()
    {
        foreach (JobState state in new[] { JobState.Failed, JobState.Interrupted })
        {
            AssertParamName<ArgumentNullException>(
                "startedAt",
                () => NewAttempt(state, null, CompletedAt, "worker.failed"));
            AssertParamName<ArgumentNullException>(
                "completedAt",
                () => NewAttempt(state, StartedAt, null, "worker.failed"));
            AssertParamName<ArgumentNullException>(
                "errorCode",
                () => NewAttempt(state, StartedAt, CompletedAt, null));
        }

        AssertParamName<ArgumentNullException>(
            "completedAt",
            () => NewAttempt(JobState.Cancelled, null, null, null));
        AssertParamName<ArgumentException>(
            "errorCode",
            () => NewAttempt(JobState.Cancelled, null, CompletedAt, "worker.cancelled"));
    }

    [TestMethod]
    public void FailedAndInterruptedAttemptsRequireStableLowercaseErrorCodes()
    {
        foreach (string valid in new[]
        {
            "worker.failed",
            "worker.process_failed",
            "a.b2",
            $"a.{new string('z', 126)}",
        })
        {
            JobAttempt attempt = NewAttempt(
                JobState.Failed,
                StartedAt,
                CompletedAt,
                valid);
            Assert.AreEqual(valid, attempt.ErrorCode);
        }

        foreach (string invalid in new[]
        {
            string.Empty,
            "worker",
            "Worker.failed",
            "1worker.failed",
            "worker..failed",
            "worker.1failed",
            "worker._failed",
            "worker.run__failed",
            "worker.run_.failed",
            "worker.failed_",
            "worker-failed.code",
            "worker.failed ",
            "wørker.failed",
            $"a.{new string('z', 127)}",
        })
        {
            AssertParamName<ArgumentException>(
                "errorCode",
                () => NewAttempt(
                    JobState.Interrupted,
                    StartedAt,
                    CompletedAt,
                    invalid));
        }
    }

    [TestMethod]
    public void JobProgressPreservesDeterminateAndIndeterminateUnitsAndText()
    {
        EntityId<JobProgressIdTag> id = EntityId<JobProgressIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        EntityId<JobAttemptIdTag> attemptId = EntityId<JobAttemptIdTag>.New();
        JobProgress determinate = new(
            id,
            jobId,
            attemptId,
            sequenceNumber: 2,
            CompletedAt,
            completedUnits: 4,
            totalUnits: 10,
            phase: "decode",
            message: "Decoded four units.");
        JobProgress indeterminate = new(
            EntityId<JobProgressIdTag>.New(),
            jobId,
            attemptId,
            sequenceNumber: 2,
            CompletedAt,
            completedUnits: 0,
            totalUnits: null,
            phase: null,
            message: null);

        Assert.AreSame(id, determinate.Id);
        Assert.AreSame(jobId, determinate.JobId);
        Assert.AreSame(attemptId, determinate.JobAttemptId);
        Assert.AreEqual(2, determinate.SequenceNumber);
        Assert.AreEqual(CompletedAt, determinate.RecordedAt);
        Assert.AreEqual(4L, determinate.CompletedUnits);
        Assert.AreEqual(10L, determinate.TotalUnits);
        Assert.AreEqual("decode", determinate.Phase);
        Assert.AreEqual("Decoded four units.", determinate.Message);
        Assert.IsNull(indeterminate.TotalUnits);
        Assert.IsNull(indeterminate.Phase);
        Assert.IsNull(indeterminate.Message);
    }

    [TestMethod]
    public void JobProgressRejectsInvalidReferencesSequenceUnitsDenominatorAndText()
    {
        EntityId<JobProgressIdTag> id = EntityId<JobProgressIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        EntityId<JobAttemptIdTag> attemptId = EntityId<JobAttemptIdTag>.New();

        AssertParamName<ArgumentNullException>(
            "id",
            () => NewProgress(null!, jobId, attemptId));
        AssertParamName<ArgumentNullException>(
            "jobId",
            () => NewProgress(id, null!, attemptId));
        AssertParamName<ArgumentNullException>(
            "jobAttemptId",
            () => NewProgress(id, jobId, null!));
        AssertParamName<ArgumentOutOfRangeException>(
            "sequenceNumber",
            () => NewProgress(id, jobId, attemptId, sequenceNumber: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "completedUnits",
            () => NewProgress(id, jobId, attemptId, completedUnits: -1));

        foreach (long totalUnits in new long[] { -1, 0 })
        {
            AssertParamName<ArgumentOutOfRangeException>(
                "totalUnits",
                () => NewProgress(id, jobId, attemptId, totalUnits: totalUnits));
        }

        AssertParamName<ArgumentOutOfRangeException>(
            "completedUnits",
            () => NewProgress(id, jobId, attemptId, completedUnits: 2, totalUnits: 1));

        foreach (string invalidText in new[] { string.Empty, " ", " phase", "phase ", "\t" })
        {
            AssertParamName<ArgumentException>(
                "phase",
                () => NewProgress(id, jobId, attemptId, phase: invalidText));
            AssertParamName<ArgumentException>(
                "message",
                () => NewProgress(id, jobId, attemptId, message: invalidText));
        }
    }

    [TestMethod]
    public void JobHeartbeatPreservesOnlyAttemptReferenceSequenceAndRecordedTime()
    {
        EntityId<JobHeartbeatIdTag> id = EntityId<JobHeartbeatIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        EntityId<JobAttemptIdTag> attemptId = EntityId<JobAttemptIdTag>.New();
        JobHeartbeat heartbeat = new(id, jobId, attemptId, 3, CompletedAt);

        Assert.AreSame(id, heartbeat.Id);
        Assert.AreSame(jobId, heartbeat.JobId);
        Assert.AreSame(attemptId, heartbeat.JobAttemptId);
        Assert.AreEqual(3, heartbeat.SequenceNumber);
        Assert.AreEqual(CompletedAt, heartbeat.RecordedAt);

        AssertParamName<ArgumentNullException>(
            "id",
            () => new JobHeartbeat(null!, jobId, attemptId, 1, CompletedAt));
        AssertParamName<ArgumentNullException>(
            "jobId",
            () => new JobHeartbeat(id, null!, attemptId, 1, CompletedAt));
        AssertParamName<ArgumentNullException>(
            "jobAttemptId",
            () => new JobHeartbeat(id, jobId, null!, 1, CompletedAt));
        AssertParamName<ArgumentOutOfRangeException>(
            "sequenceNumber",
            () => new JobHeartbeat(id, jobId, attemptId, 0, CompletedAt));
    }

    private static Job CreateJob(JobState state, int currentAttemptNumber = 1) =>
        new(
            EntityId<JobIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            "test-kind",
            state,
            QueuedAt,
            QueuedAt,
            currentAttemptNumber);

    private static JobAttempt CreateAttempt(JobState state) =>
        state switch
        {
            JobState.Queued => NewAttempt(state, null, null, null),
            JobState.Running => NewAttempt(state, StartedAt, null, null),
            JobState.Succeeded => NewAttempt(state, StartedAt, CompletedAt, null),
            JobState.Failed =>
                NewAttempt(state, StartedAt, CompletedAt, "worker.process_failed"),
            JobState.Cancelled => NewAttempt(state, null, CompletedAt, null),
            JobState.Interrupted =>
                NewAttempt(state, StartedAt, CompletedAt, "worker.process_interrupted"),
            _ => throw new AssertFailedException("Unknown test state."),
        };

    private static JobAttempt NewAttempt(
        JobState state,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? errorCode) =>
        new(
            EntityId<JobAttemptIdTag>.New(),
            EntityId<JobIdTag>.New(),
            sequenceNumber: 1,
            state,
            QueuedAt,
            startedAt,
            completedAt,
            errorCode);

    private static JobProgress NewProgress(
        EntityId<JobProgressIdTag> id,
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> jobAttemptId,
        int sequenceNumber = 1,
        long completedUnits = 0,
        long? totalUnits = null,
        string? phase = null,
        string? message = null) =>
        new(
            id,
            jobId,
            jobAttemptId,
            sequenceNumber,
            CompletedAt,
            completedUnits,
            totalUnits,
            phase,
            message);

    private static void AssertTransitionMatrix(
        IReadOnlySet<(JobState From, JobState To)> allowed,
        Func<JobState, JobState, bool> canTransition,
        Action<JobState, JobState> ensureCanTransition)
    {
        foreach (JobState fromState in Enum.GetValues<JobState>())
        {
            foreach (JobState toState in Enum.GetValues<JobState>())
            {
                bool expected = allowed.Contains((fromState, toState));
                Assert.AreEqual(
                    expected,
                    canTransition(fromState, toState),
                    $"{fromState} -> {toState}");

                if (expected)
                {
                    ensureCanTransition(fromState, toState);
                }
                else
                {
                    Assert.ThrowsExactly<InvalidOperationException>(
                        () => ensureCanTransition(fromState, toState),
                        $"{fromState} -> {toState}");
                }
            }
        }
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);

        PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}