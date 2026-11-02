using System.Reflection;
using System.Text;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Recovery;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class JobRecoveryTests
{
    private static readonly DateTimeOffset RecoveryAt =
        new(2041, 10, 11, 12, 13, 14, TimeSpan.Zero);

    [TestMethod]
    public async Task RecoverAsyncWithNoRunningJobsReturnsCurrentChecksWithoutTransitions()
    {
        using JobRecoveryFixture fixture = new();
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsTrue(result.BeforeCheck.DatabaseUsable);
        Assert.IsTrue(result.AfterCheck.DatabaseUsable);
        Assert.AreEqual(0, result.RecoveredJobs.Count);
        Assert.AreEqual(0, result.SkippedJobIds.Count);
        Assert.AreEqual(0L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(0L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM job_attempts;"));
    }

    [TestMethod]
    // NFR-REL-001 AC-115: 中断された処理は interrupted として復旧され、記録が残る。
    public async Task RecoverAsyncChangesRunningAttemptToExactInterruptedSnapshot()
    {
        using JobRecoveryFixture fixture = new();
        DateTimeOffset createdAt = RecoveryAt.AddMinutes(-2);
        DateTimeOffset startedAt = RecoveryAt.AddMinutes(-1);
        RunningJobSetup setup = fixture.CreateRunningJob(
            "analysis.transcript",
            createdAt,
            startedAt);
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        RecoveredJob recovered = result.RecoveredJobs.Single();
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        JobAttempt attempt = aggregate.Attempts.Single();
        Assert.IsTrue(result.DatabaseUsable);
        Assert.AreEqual(setup.Job.Id, recovered.JobId);
        Assert.AreEqual(setup.Attempt.Id, recovered.AttemptId);
        Assert.AreEqual(JobState.Running, recovered.PreviousState);
        Assert.AreEqual(JobState.Interrupted, recovered.CurrentState);
        Assert.IsFalse(recovered.AutomaticallyRequeued);
        Assert.AreEqual(2, recovered.FinalRevision);
        Assert.AreEqual(JobState.Interrupted, aggregate.Job.State);
        Assert.AreEqual(RecoveryAt, aggregate.Job.UpdatedAt);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(2, aggregate.Revision);
        Assert.AreEqual(setup.Attempt.Id, attempt.Id);
        Assert.AreEqual(setup.Attempt.JobId, attempt.JobId);
        Assert.AreEqual(setup.Attempt.SequenceNumber, attempt.SequenceNumber);
        Assert.AreEqual(setup.Attempt.QueuedAt, attempt.QueuedAt);
        Assert.AreEqual(setup.Attempt.StartedAt, attempt.StartedAt);
        Assert.AreEqual(JobState.Interrupted, attempt.State);
        Assert.AreEqual(RecoveryAt, attempt.CompletedAt);
        Assert.AreEqual("job.app_restarted", attempt.ErrorCode);
        Assert.AreEqual(0, result.SkippedJobIds.Count);
    }

    [TestMethod]
    public async Task RecoverAsyncProcessesRunningJobsByCreatedAtThenId()
    {
        using JobRecoveryFixture fixture = new();
        DateTimeOffset sameCreatedAt = RecoveryAt.AddHours(-2);
        RunningJobSetup higherId = fixture.CreateRunningJob(
            "order.higher",
            sameCreatedAt,
            sameCreatedAt.AddSeconds(1),
            EntityId<JobIdTag>.From(
                Guid.Parse("22222222-2222-4222-8222-222222222222")));
        RunningJobSetup earliest = fixture.CreateRunningJob(
            "order.earliest",
            sameCreatedAt.AddMinutes(-1),
            sameCreatedAt.AddMinutes(-1).AddSeconds(1),
            EntityId<JobIdTag>.From(
                Guid.Parse("33333333-3333-4333-8333-333333333333")));
        RunningJobSetup lowerId = fixture.CreateRunningJob(
            "order.lower",
            sameCreatedAt,
            sameCreatedAt.AddSeconds(1),
            EntityId<JobIdTag>.From(
                Guid.Parse("11111111-1111-4111-8111-111111111111")));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { earliest.Job.Id, lowerId.Job.Id, higherId.Job.Id },
            result.RecoveredJobs.Select(static item => item.JobId).ToArray());
        Assert.IsTrue(result.RecoveredJobs.All(static item =>
            item.CurrentState == JobState.Interrupted && item.FinalRevision == 2));
    }

    [TestMethod]
    public async Task RecoverAsyncExactAllowlistRequeuesWholeJobWithoutStartingAttempt()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "media.proxy",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));
        IReadOnlySet<string> allowlist = new HashSet<string>(
            ["media.proxy"],
            StringComparer.OrdinalIgnoreCase);

        JobRecoveryResult result = await recovery.RecoverAsync(
            allowlist,
            CancellationToken.None);

        RecoveredJob recovered = result.RecoveredJobs.Single();
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        JobAttempt attempt = aggregate.Attempts.Single();
        Assert.AreEqual(JobState.Queued, recovered.CurrentState);
        Assert.IsTrue(recovered.AutomaticallyRequeued);
        Assert.AreEqual(3, recovered.FinalRevision);
        Assert.AreEqual(JobState.Queued, aggregate.Job.State);
        Assert.AreEqual(3, aggregate.Revision);
        Assert.AreEqual(1, aggregate.Job.CurrentAttemptNumber);
        Assert.AreEqual(1, aggregate.Attempts.Count);
        Assert.AreEqual(setup.Attempt.Id, attempt.Id);
        Assert.AreEqual(JobState.Interrupted, attempt.State);
        Assert.AreEqual(RecoveryAt, attempt.CompletedAt);
        Assert.AreEqual("job.app_restarted", attempt.ErrorCode);
        Assert.AreEqual(0, aggregate.Progress.Count);
        Assert.AreEqual(0, aggregate.Heartbeats.Count);
    }

    [TestMethod]
    public async Task RecoverAsyncDoesNotApplyEmptyPrefixWildcardOrCaseFoldedAllowlists()
    {
        (string JobKind, IReadOnlySet<string> Allowlist)[] scenarios =
        [
            ("media.render", new HashSet<string>(StringComparer.Ordinal)),
            ("media.render", new HashSet<string>(["media"], StringComparer.Ordinal)),
            ("media.render", new HashSet<string>(["media.*"], StringComparer.Ordinal)),
            (
                "media.render",
                new HashSet<string>(["MEDIA.RENDER"], StringComparer.OrdinalIgnoreCase)),
        ];

        foreach ((string jobKind, IReadOnlySet<string> allowlist) in scenarios)
        {
            using JobRecoveryFixture fixture = new();
            RunningJobSetup setup = fixture.CreateRunningJob(
                jobKind,
                RecoveryAt.AddMinutes(-2),
                RecoveryAt.AddMinutes(-1));
            var recovery = new JobRecovery(
                fixture.Store,
                fixture.Recovery,
                new FixedTimeProvider(RecoveryAt));

            JobRecoveryResult result = await recovery.RecoverAsync(
                allowlist,
                CancellationToken.None);

            RecoveredJob recovered = result.RecoveredJobs.Single();
            StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
            Assert.AreEqual(JobState.Interrupted, recovered.CurrentState, jobKind);
            Assert.IsFalse(recovered.AutomaticallyRequeued, jobKind);
            Assert.AreEqual(2, recovered.FinalRevision, jobKind);
            Assert.AreEqual(JobState.Interrupted, aggregate.Job.State, jobKind);
            Assert.AreEqual(2, aggregate.Revision, jobKind);
        }
    }

    [TestMethod]
    public async Task RecoverAsyncUsesLatestProgressOrHeartbeatAsCompletionFloor()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "analysis.future-evidence",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        DateTimeOffset heartbeatAt = RecoveryAt.AddMinutes(1);
        DateTimeOffset progressAt = RecoveryAt.AddMinutes(2);
        fixture.Store.AppendHeartbeat(new JobHeartbeat(
            EntityId<JobHeartbeatIdTag>.New(),
            setup.Job.Id,
            setup.Attempt.Id,
            setup.Attempt.SequenceNumber,
            heartbeatAt));
        fixture.Store.AppendProgress(new JobProgress(
            EntityId<JobProgressIdTag>.New(),
            setup.Job.Id,
            setup.Attempt.Id,
            setup.Attempt.SequenceNumber,
            progressAt,
            completedUnits: 1,
            totalUnits: 2,
            phase: "future-progress",
            message: null));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        _ = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        Assert.AreEqual(progressAt, aggregate.Attempts.Single().CompletedAt);
        Assert.AreEqual(progressAt, aggregate.Job.UpdatedAt);
        Assert.AreEqual(JobState.Interrupted, aggregate.Job.State);
    }

    [TestMethod]
    public async Task RecoverAsyncInterruptsRunningJobsRegardlessOfHeartbeatAgeOrAbsence()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup absent = fixture.CreateRunningJob(
            "heartbeat.absent",
            RecoveryAt.AddDays(-3),
            RecoveryAt.AddDays(-3).AddSeconds(1));
        RunningJobSetup old = fixture.CreateRunningJob(
            "heartbeat.old",
            RecoveryAt.AddDays(-2),
            RecoveryAt.AddDays(-2).AddSeconds(1));
        RunningJobSetup fresh = fixture.CreateRunningJob(
            "heartbeat.fresh",
            RecoveryAt.AddHours(-1),
            RecoveryAt.AddHours(-1).AddSeconds(1));
        fixture.Store.AppendHeartbeat(new JobHeartbeat(
            EntityId<JobHeartbeatIdTag>.New(),
            old.Job.Id,
            old.Attempt.Id,
            old.Attempt.SequenceNumber,
            old.Attempt.StartedAt!.Value.AddSeconds(1)));
        fixture.Store.AppendHeartbeat(new JobHeartbeat(
            EntityId<JobHeartbeatIdTag>.New(),
            fresh.Job.Id,
            fresh.Attempt.Id,
            fresh.Attempt.SequenceNumber,
            RecoveryAt));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.AreEqual(3, result.RecoveredJobs.Count);
        foreach (RunningJobSetup setup in new[] { absent, old, fresh })
        {
            StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
            Assert.AreEqual(JobState.Interrupted, aggregate.Job.State);
            Assert.AreEqual("job.app_restarted", aggregate.Attempts.Single().ErrorCode);
        }
    }

    [TestMethod]
    public async Task RecoverAsyncLeavesQueuedCancelledSucceededAndFailedJobsUntouched()
    {
        using JobRecoveryFixture fixture = new();
        EntityId<JobIdTag>[] jobIds =
        [
            fixture.CreateJobInState(JobState.Queued, "state.queued"),
            fixture.CreateJobInState(JobState.Cancelled, "state.cancelled"),
            fixture.CreateJobInState(JobState.Succeeded, "state.succeeded"),
            fixture.CreateJobInState(JobState.Failed, "state.failed"),
        ];
        (JobState State, int Revision, int AttemptCount)[] before = jobIds
            .Select(jobId => fixture.RequiredAggregate(jobId))
            .Select(static aggregate => (
                aggregate.Job.State,
                aggregate.Revision,
                aggregate.Attempts.Count))
            .ToArray();
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(["state.queued"], StringComparer.Ordinal),
            CancellationToken.None);

        Assert.AreEqual(0, result.RecoveredJobs.Count);
        Assert.AreEqual(0, result.SkippedJobIds.Count);
        for (int index = 0; index < jobIds.Length; index++)
        {
            StoredJobAggregate aggregate = fixture.RequiredAggregate(jobIds[index]);
            Assert.AreEqual(before[index].State, aggregate.Job.State);
            Assert.AreEqual(before[index].Revision, aggregate.Revision);
            Assert.AreEqual(before[index].AttemptCount, aggregate.Attempts.Count);
        }
    }

    [TestMethod]
    public async Task RecoverAsyncDatabaseUnusableReturnsSameCheckWithoutMutation()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "database.invalid",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        fixture.ExecuteNonQuery("DROP INDEX idx_artifacts_content_digest;");
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>([setup.Job.Kind], StringComparer.Ordinal),
            CancellationToken.None);

        Assert.IsFalse(result.DatabaseUsable);
        Assert.AreSame(result.BeforeCheck, result.AfterCheck);
        Assert.IsFalse(result.BeforeCheck.DatabaseUsable);
        Assert.AreEqual(WorkspaceIntegrityIssueKind.DatabaseInvalid,
            result.BeforeCheck.Issues.Single().Kind);
        Assert.AreEqual(0, result.BeforeCheck.StagingCandidates.Count);
        Assert.AreEqual(0, result.RecoveredJobs.Count);
        Assert.AreEqual(0, result.SkippedJobIds.Count);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(JobState.Running, aggregate.Attempts.Single().State);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public async Task RecoverAsyncArtifactMismatchDoesNotBlockJobAndRemainsInAfterCheck()
    {
        using JobRecoveryFixture fixture = new();
        byte[] original = Encoding.UTF8.GetBytes("artifact-one");
        byte[] changed = Encoding.UTF8.GetBytes("artifact-two");
        Assert.AreEqual(original.Length, changed.Length);
        StoredArtifact artifact = await fixture.PromoteArtifactAsync(
            fixture.ProjectId,
            "integrity.bin",
            original);
        string artifactPath = fixture.GetStoragePath(artifact.StorageReference);
        File.WriteAllBytes(artifactPath, changed);
        RunningJobSetup setup = fixture.CreateRunningJob(
            "workspace.issue",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.IsTrue(result.DatabaseUsable);
        AssertDigestMismatch(result.BeforeCheck, artifact.Id);
        AssertDigestMismatch(result.AfterCheck, artifact.Id);
        Assert.AreEqual(JobState.Interrupted,
            fixture.RequiredAggregate(setup.Job.Id).Job.State);
        Assert.IsTrue(File.Exists(artifactPath));
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(artifactPath));
    }

    [TestMethod]
    public async Task RecoverAsyncNeverCleansWorkspaceRecoveryCandidates()
    {
        using JobRecoveryFixture fixture = new();
        EntityId<ProjectIdTag> candidateProject = fixture.CreateProject();
        string candidatePath = fixture.CreateStagingFile(
            candidateProject,
            "nested/must-remain.bin",
            [1, 2, 3]);
        RunningJobSetup setup = fixture.CreateRunningJob(
            "staging.protected",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.IsTrue(result.BeforeCheck.StagingCandidates.Any(candidate =>
            candidate.ProjectId == candidateProject &&
            candidate.RelativePath == "nested/must-remain.bin"));
        Assert.IsTrue(result.AfterCheck.StagingCandidates.Any(candidate =>
            candidate.ProjectId == candidateProject &&
            candidate.RelativePath == "nested/must-remain.bin"));
        Assert.IsTrue(File.Exists(candidatePath));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(candidatePath));
        Assert.AreEqual(JobState.Interrupted,
            fixture.RequiredAggregate(setup.Job.Id).Job.State);
    }

    [TestMethod]
    public async Task RecoverAsyncSecondCallIsIdempotentAndFindsNoRunningJobs()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "idempotent.recovery",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        JobRecoveryResult first = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);
        JobRecoveryResult second = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.AreEqual(1, first.RecoveredJobs.Count);
        Assert.AreEqual(0, second.RecoveredJobs.Count);
        Assert.AreEqual(0, second.SkippedJobIds.Count);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        Assert.AreEqual(JobState.Interrupted, aggregate.Job.State);
        Assert.AreEqual(2, aggregate.Revision);
        Assert.AreEqual(1, aggregate.Attempts.Count);
    }

    [TestMethod]
    public async Task RecoverAsyncPreCancelledLeavesRunningJobUntouched()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "cancel.before",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                recovery.RecoverAsync(
                    new HashSet<string>(StringComparer.Ordinal),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(JobState.Running, aggregate.Attempts.Single().State);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public async Task RecoverAsyncCancellationBetweenJobsKeepsCompletedPrefixOnly()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup first = fixture.CreateRunningJob(
            "cancel.first",
            RecoveryAt.AddMinutes(-3),
            RecoveryAt.AddMinutes(-3).AddSeconds(1));
        RunningJobSetup second = fixture.CreateRunningJob(
            "cancel.second",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-2).AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new CancelOnFirstReadTimeProvider(RecoveryAt, cancellation));

        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                recovery.RecoverAsync(
                    new HashSet<string>(StringComparer.Ordinal),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        StoredJobAggregate firstAggregate = fixture.RequiredAggregate(first.Job.Id);
        StoredJobAggregate secondAggregate = fixture.RequiredAggregate(second.Job.Id);
        Assert.AreEqual(JobState.Interrupted, firstAggregate.Job.State);
        Assert.AreEqual(2, firstAggregate.Revision);
        Assert.AreEqual(JobState.Running, secondAggregate.Job.State);
        Assert.AreEqual(JobState.Running, secondAggregate.Attempts.Single().State);
        Assert.AreEqual(1, secondAggregate.Revision);
    }

    [TestMethod]
    public async Task RecoverAsyncRejectsNullBlankAndUntrimmedAllowlistsWithoutMutation()
    {
        using JobRecoveryFixture fixture = new();
        RunningJobSetup setup = fixture.CreateRunningJob(
            "validation.running",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            recovery.RecoverAsync(null!, CancellationToken.None));
        foreach (IReadOnlySet<string> invalid in new IReadOnlySet<string>[]
        {
            new HashSet<string>([string.Empty], StringComparer.Ordinal),
            new HashSet<string>(["   "], StringComparer.Ordinal),
            new HashSet<string>([" leading"], StringComparer.Ordinal),
            new HashSet<string>(["trailing "], StringComparer.Ordinal),
            new HashSet<string>([null!], StringComparer.Ordinal),
        })
        {
            ArgumentException exception =
                await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                    recovery.RecoverAsync(invalid, CancellationToken.None));
            Assert.AreEqual("automaticallyRestartableKinds", exception.ParamName);
        }

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new JobRecovery(null!, fixture.Recovery));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new JobRecovery(fixture.Store, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new JobRecovery(fixture.Store, fixture.Recovery, null!));
        StoredJobAggregate aggregate = fixture.RequiredAggregate(setup.Job.Id);
        Assert.AreEqual(JobState.Running, aggregate.Job.State);
        Assert.AreEqual(1, aggregate.Revision);
    }

    [TestMethod]
    public async Task PublicSurfaceIsMinimalImmutableReadOnlyAndPathFree()
    {
        using JobRecoveryFixture fixture = new();
        _ = fixture.CreateRunningJob(
            "surface.running",
            RecoveryAt.AddMinutes(-2),
            RecoveryAt.AddMinutes(-1));
        var recovery = new JobRecovery(
            fixture.Store,
            fixture.Recovery,
            new FixedTimeProvider(RecoveryAt));
        JobRecoveryResult result = await recovery.RecoverAsync(
            new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.IsTrue(typeof(JobRecovery).IsPublic);
        Assert.IsTrue(typeof(JobRecovery).IsSealed);
        Assert.AreEqual(0, typeof(JobRecovery).GetInterfaces().Length);
        ConstructorInfo publicConstructor = AssertSinglePublicConstructor(
            typeof(JobRecovery));
        CollectionAssert.AreEqual(
            new[] { typeof(JobStore), typeof(WorkspaceRecovery) },
            publicConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        ConstructorInfo internalConstructor = typeof(JobRecovery)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single();
        CollectionAssert.AreEqual(
            new[] { typeof(JobStore), typeof(WorkspaceRecovery), typeof(TimeProvider) },
            internalConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        MethodInfo recoverMethod = AssertSinglePublicMethod(
            typeof(JobRecovery),
            "RecoverAsync");
        Assert.AreEqual(typeof(Task<JobRecoveryResult>), recoverMethod.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(IReadOnlySet<string>), typeof(CancellationToken) },
            recoverMethod.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        AssertResultType(
            typeof(RecoveredJob),
            "JobId",
            "AttemptId",
            "PreviousState",
            "CurrentState",
            "AutomaticallyRequeued",
            "FinalRevision");
        AssertResultType(
            typeof(JobRecoveryResult),
            "DatabaseUsable",
            "BeforeCheck",
            "AfterCheck",
            "RecoveredJobs",
            "SkippedJobIds");
        Assert.AreEqual(
            typeof(IReadOnlyList<RecoveredJob>),
            typeof(JobRecoveryResult).GetProperty("RecoveredJobs")!.PropertyType);
        Assert.AreEqual(
            typeof(IReadOnlyList<EntityId<JobIdTag>>),
            typeof(JobRecoveryResult).GetProperty("SkippedJobIds")!.PropertyType);
        foreach (Type type in new[] { typeof(RecoveredJob), typeof(JobRecoveryResult) })
        {
            Assert.IsFalse(type.GetProperties().Any(static property =>
                property.Name.Contains("Path", StringComparison.Ordinal) ||
                property.Name.Contains("Error", StringComparison.Ordinal) ||
                property.Name.Contains("Exception", StringComparison.Ordinal) ||
                property.Name.Contains("Message", StringComparison.Ordinal)));
        }

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<RecoveredJob>)result.RecoveredJobs).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EntityId<JobIdTag>>)result.SkippedJobIds).Clear());
    }

    private static void AssertDigestMismatch(
        WorkspaceRecoveryCheckResult check,
        EntityId<ArtifactIdTag> artifactId)
    {
        WorkspaceIntegrityIssue issue = check.Issues.Single(item =>
            item.Kind == WorkspaceIntegrityIssueKind.ArtifactDigestMismatch &&
            item.EntityId == artifactId.ToString());
        Assert.IsNotNull(issue.ContentDigest);
        Assert.IsNotNull(issue.PathFingerprint);
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
        MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(expectedName, methods[0].Name);
        return methods[0];
    }

    private static void AssertResultType(Type type, params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        Assert.AreEqual(
            0,
            type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(static method => !method.IsSpecialName),
            type.Name);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedProperties,
            properties.Select(static property => property.Name).ToArray(),
            type.Name);
        Assert.IsTrue(properties.All(static property => property.SetMethod is null), type.Name);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CancelOnFirstReadTimeProvider(
        DateTimeOffset value,
        CancellationTokenSource cancellation) : TimeProvider
    {
        private int _readCount;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                cancellation.Cancel();
            }

            return value;
        }
    }

    private sealed record RunningJobSetup(
        Job Job,
        JobAttempt Attempt);

    private sealed class JobRecoveryFixture : IDisposable
    {
        private int _jobSequence;

        internal JobRecoveryFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.JobRecoveryTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = CreateProject();
            Store = new JobStore(DatabasePath);
            Recovery = new WorkspaceRecovery(DatabasePath, WorkspacePath);
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal string WorkspacePath { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal JobStore Store { get; }

        internal WorkspaceRecovery Recovery { get; }

        internal EntityId<ProjectIdTag> CreateProject()
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                projectId,
                "Job recovery test project",
                "Exercise restart-safe durable job recovery.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"jobRecovery\":true}",
                RecoveryAt.AddDays(-10));
            return projectId;
        }

        internal RunningJobSetup CreateRunningJob(
            string kind,
            DateTimeOffset createdAt,
            DateTimeOffset startedAt,
            EntityId<JobIdTag>? jobId = null,
            EntityId<ProjectIdTag>? projectId = null)
        {
            var job = new Job(
                jobId ?? EntityId<JobIdTag>.New(),
                projectId ?? ProjectId,
                kind,
                JobState.Queued,
                createdAt,
                createdAt,
                currentAttemptNumber: 0);
            Store.CreateJob(job);
            var attempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                job.Id,
                sequenceNumber: 1,
                JobState.Running,
                createdAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(
                job.Id,
                expectedRevision: 0,
                attempt,
                startedAt);
            return new RunningJobSetup(job, attempt);
        }

        internal EntityId<JobIdTag> CreateJobInState(JobState state, string kind)
        {
            DateTimeOffset createdAt = RecoveryAt
                .AddHours(-6)
                .AddMinutes(Interlocked.Increment(ref _jobSequence));
            var job = new Job(
                EntityId<JobIdTag>.New(),
                ProjectId,
                kind,
                JobState.Queued,
                createdAt,
                createdAt,
                currentAttemptNumber: 0);
            Store.CreateJob(job);
            if (state == JobState.Queued)
            {
                return job.Id;
            }

            if (state == JobState.Cancelled)
            {
                _ = Store.CancelQueued(job.Id, 0, createdAt.AddSeconds(1));
                return job.Id;
            }

            DateTimeOffset startedAt = createdAt.AddSeconds(1);
            var runningAttempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                job.Id,
                sequenceNumber: 1,
                JobState.Running,
                createdAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(job.Id, 0, runningAttempt, startedAt);
            if (state == JobState.Running)
            {
                return job.Id;
            }

            DateTimeOffset completedAt = startedAt.AddSeconds(1);
            var completedAttempt = new JobAttempt(
                runningAttempt.Id,
                runningAttempt.JobId,
                runningAttempt.SequenceNumber,
                state,
                runningAttempt.QueuedAt,
                runningAttempt.StartedAt,
                completedAt,
                state switch
                {
                    JobState.Failed => "test.failed",
                    JobState.Interrupted => "test.interrupted",
                    _ => null,
                });
            _ = Store.CompleteAttempt(job.Id, 1, completedAttempt, completedAt);
            return job.Id;
        }

        internal StoredJobAggregate RequiredAggregate(EntityId<JobIdTag> jobId)
        {
            return Store.GetJobAggregate(jobId)
                ?? throw new AssertFailedException("The expected job aggregate was not stored.");
        }

        internal async Task<StoredArtifact> PromoteArtifactAsync(
            EntityId<ProjectIdTag> projectId,
            string relativePath,
            byte[] content)
        {
            string stagingPath = CreateStagingFile(
                projectId,
                string.Concat("promotion/", Guid.NewGuid().ToString("N"), ".tmp"),
                content);
            var request = new ArtifactPromotionRequest(
                EntityId<ArtifactIdTag>.New(),
                projectId,
                ArtifactKind.Analysis,
                stagingPath,
                relativePath,
                ContentDigest.Compute(content),
                content.LongLength,
                RecoveryAt.AddDays(-1));
            return await new ArtifactStore(DatabasePath, WorkspacePath)
                .PromoteAsync(request);
        }

        internal string CreateStagingFile(
            EntityId<ProjectIdTag> projectId,
            string relativePath,
            byte[] content)
        {
            string path = Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        internal string GetStoragePath(string storageReference) =>
            Path.Combine(
                WorkspacePath,
                storageReference.Replace('/', Path.DirectorySeparatorChar));

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