using System.Reflection;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.App.Rendering;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Recovery;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class FinalRenderServiceTests
{
    private const string SensitiveStandardOutput =
        "C:\\private\\render\\stdout-2d71e6a2";
    private const string SensitiveStandardError =
        "secret-render-error-7f14ca81";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task SuccessfulRunUsesExactApprovalReportsBoundedProgressAndPromotesOnce()
    {
        using FinalRenderFixture fixture = new();
        StoredApproval approval = fixture.AppendApproval();
        FinalRenderRequest request = fixture.CreateRequest(
            approvalEvidence: [approval]);
        fixture.ProcessBehavior = static (_, _) => Task.FromResult(
            new ProcessRunResult(
                0,
                SensitiveStandardOutput,
                SensitiveStandardError,
                Truncated: true,
                TimeSpan.FromSeconds(2)));

        FinalRenderRunResult result = await fixture.Service.RunAsync(request);

        FinalRenderOutput output = result.Output
            ?? throw new AssertFailedException("The successful render has no output evidence.");
        Assert.AreEqual(FinalRenderDisposition.Completed, result.Disposition);
        Assert.AreEqual(FinalRenderStage.ArtifactPromoted, result.LastCompletedStage);
        Assert.IsTrue(result.IsComplete);
        Assert.IsFalse(result.CanRetry);
        Assert.IsNull(result.FailureReason);
        Assert.AreEqual(JobState.Succeeded, result.Job.Snapshot.State);
        Assert.AreEqual(request.OutputArtifactId, output.ArtifactId);
        Assert.AreEqual(request.ExpectedOutputDigest, output.ContentDigest);
        Assert.AreEqual(request.ExpectedOutputByteSize, output.ByteSize);
        Assert.AreEqual(1, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(1, fixture.GraphValidationCount);
        Assert.AreEqual(1, fixture.CompileCount);
        Assert.AreEqual(1, fixture.ProcessCount);
        Assert.AreEqual(1, fixture.OutputVerificationCount);
        Assert.AreEqual(1, fixture.PromotionCount);

        StoredJobAggregate aggregate = fixture.RequiredAggregate();
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5 },
            aggregate.Progress.Select(static progress => progress.CompletedUnits).ToArray());
        Assert.IsTrue(aggregate.Progress.All(static progress =>
            progress.TotalUnits == 5 && progress.Message is null));
        CollectionAssert.AreEqual(
            new[]
            {
                "render.final.evidence",
                "render.final.compile",
                "render.final.process",
                "render.final.verify",
                "render.final.promote",
            },
            aggregate.Progress.Select(static progress => progress.Phase).ToArray());
        Assert.AreEqual(aggregate.Progress.Count, aggregate.Heartbeats.Count);
        Assert.AreEqual(1m, result.Job.Snapshot.ProgressRatio);

        string serialized = JsonSerializer.Serialize(result);
        Assert.IsFalse(serialized.Contains(SensitiveStandardOutput, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(SensitiveStandardError, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(fixture.StagingOutputPath, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(fixture.ToolPath, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApprovalPolicyAndGraphDriftFailBeforeCompilation()
    {
        (FinalRenderFailureReason Reason, Func<FinalRenderFixture, FinalRenderRequest> Request)[] cases =
        [
            (
                FinalRenderFailureReason.ApprovalMismatch,
                fixture => fixture.CreateRequest(approvalEvidence: [])),
            (
                FinalRenderFailureReason.ApprovalMismatch,
                fixture => fixture.CreateRequest(
                    approvedDigest: FinalRenderFixture.Digest('f'))),
            (
                FinalRenderFailureReason.PolicyDrift,
                fixture => fixture.CreateRequest(
                    policySnapshot: new FinalRenderPolicySnapshot(
                        fixture.PolicyDigest,
                        FinalRenderFixture.Digest('e')))),
            (
                FinalRenderFailureReason.GraphDrift,
                fixture =>
                {
                    fixture.FailGraphValidation = true;
                    return fixture.CreateRequest();
                }),
        ];

        foreach ((FinalRenderFailureReason expected, Func<FinalRenderFixture, FinalRenderRequest> create) in cases)
        {
            using FinalRenderFixture fixture = new();
            FinalRenderRunResult result = await fixture.Service.RunAsync(create(fixture));

            Assert.AreEqual(FinalRenderDisposition.Failed, result.Disposition, expected.ToString());
            Assert.AreEqual(expected, result.FailureReason, expected.ToString());
            Assert.AreEqual(FinalRenderStage.NotStarted, result.LastCompletedStage, expected.ToString());
            Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State, expected.ToString());
            Assert.AreEqual(JobRetryability.NonRetryable, result.Job.Snapshot.Retryability);
            Assert.AreEqual(0, fixture.CompileCount, expected.ToString());
            Assert.AreEqual(0, fixture.ProcessCount, expected.ToString());
            Assert.AreEqual(0, fixture.PromotionCount, expected.ToString());
        }
    }

    [TestMethod]
    public async Task CompilerClassifiesGraphToolInputAndUnsupportedConfigurationFailures()
    {
        (FfmpegCompilerFailureReason Compiler, FinalRenderFailureReason Expected)[] cases =
        [
            (FfmpegCompilerFailureReason.GraphInvalid, FinalRenderFailureReason.GraphDrift),
            (FfmpegCompilerFailureReason.ToolNotVerified, FinalRenderFailureReason.ToolDrift),
            (FfmpegCompilerFailureReason.InputNotVerified, FinalRenderFailureReason.InputDrift),
            (FfmpegCompilerFailureReason.InputBindingMismatch, FinalRenderFailureReason.InputDrift),
            (FfmpegCompilerFailureReason.UnsupportedCodec, FinalRenderFailureReason.CompilationFailed),
        ];

        foreach ((FfmpegCompilerFailureReason compiler, FinalRenderFailureReason expected) in cases)
        {
            using FinalRenderFixture fixture = new()
            {
                CompileFailureReason = compiler,
            };

            FinalRenderRunResult result = await fixture.Service.RunAsync(
                fixture.CreateRequest());

            Assert.AreEqual(FinalRenderDisposition.Failed, result.Disposition, compiler.ToString());
            Assert.AreEqual(expected, result.FailureReason, compiler.ToString());
            Assert.AreEqual(FinalRenderStage.EvidenceValidated, result.LastCompletedStage);
            Assert.AreEqual(1, fixture.CompileCount);
            Assert.AreEqual(0, fixture.ProcessCount);
            Assert.AreEqual(0, fixture.PromotionCount);
        }
    }

    [TestMethod]
    public async Task NonzeroProcessAndOutputMismatchFailWithoutLeakingDetailsOrPromoting()
    {
        using (FinalRenderFixture fixture = new())
        {
            fixture.ProcessBehavior = static (_, _) => Task.FromResult(
                new ProcessRunResult(
                    23,
                    SensitiveStandardOutput,
                    SensitiveStandardError,
                    Truncated: false,
                    TimeSpan.FromSeconds(1)));

            FinalRenderRunResult result = await fixture.Service.RunAsync(
                fixture.CreateRequest());

            Assert.AreEqual(FinalRenderFailureReason.ProcessFailed, result.FailureReason);
            Assert.AreEqual(FinalRenderStage.GraphCompiled, result.LastCompletedStage);
            Assert.AreEqual(0, fixture.OutputVerificationCount);
            Assert.AreEqual(0, fixture.PromotionCount);
            string serialized = JsonSerializer.Serialize(result);
            Assert.IsFalse(serialized.Contains(SensitiveStandardOutput, StringComparison.Ordinal));
            Assert.IsFalse(serialized.Contains(SensitiveStandardError, StringComparison.Ordinal));
        }

        using (FinalRenderFixture fixture = new())
        {
            fixture.UseRealOutputVerification = true;
            fixture.ProcessBehavior = async (_, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    fixture.StagingOutputPath,
                    new byte[checked((int)fixture.ExpectedOutputByteSize)],
                    cancellationToken).ConfigureAwait(false);
                return FinalRenderFixture.SuccessfulProcessResult();
            };

            FinalRenderRunResult result = await fixture.Service.RunAsync(
                fixture.CreateRequest());

            Assert.AreEqual(FinalRenderFailureReason.OutputMismatch, result.FailureReason);
            Assert.AreEqual(FinalRenderStage.ProcessCompleted, result.LastCompletedStage);
            Assert.AreEqual(1, fixture.OutputVerificationCount);
            Assert.AreEqual(0, fixture.PromotionCount);
            Assert.IsTrue(File.Exists(fixture.StagingOutputPath));
            Assert.IsNull(fixture.ExistingArtifact);
            Assert.IsFalse(
                JsonSerializer.Serialize(result).Contains(
                    fixture.StagingOutputPath,
                    StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task TimeoutIsRetryableAndRetryUsesDistinctAttemptStagingPath()
    {
        using FinalRenderFixture fixture = new();
        int processCalls = 0;
        fixture.ProcessBehavior = (_, _) =>
        {
            if (Interlocked.Increment(ref processCalls) == 1)
            {
                throw new TimeoutException("private timeout detail");
            }

            return Task.FromResult(FinalRenderFixture.SuccessfulProcessResult());
        };
        FinalRenderRequest request = fixture.CreateRequest();

        FinalRenderRunResult first = await fixture.Service.RunAsync(request);

        Assert.AreEqual(FinalRenderDisposition.Failed, first.Disposition);
        Assert.AreEqual(FinalRenderFailureReason.ProcessTimedOut, first.FailureReason);
        Assert.AreEqual(JobRetryability.Retryable, first.Job.Snapshot.Retryability);
        Assert.AreEqual("job.execution_timed_out", first.Job.Snapshot.ErrorCode);
        Assert.IsTrue(first.CanRetry);

        JobRetryResult queued = fixture.Service.QueueRetry(request.JobId);
        FinalRenderRunResult second = await fixture.Service.RunAsync(request);

        Assert.AreEqual(JobRetryOutcome.Queued, queued.Outcome);
        Assert.AreEqual(FinalRenderDisposition.Completed, second.Disposition);
        Assert.AreEqual(2, second.Job.SequenceNumber);
        Assert.AreEqual(2, fixture.CompileRequests.Count);
        Assert.AreEqual(fixture.StagingOutputPath, fixture.CompileRequests[0].OutputPath);
        Assert.AreEqual(
            Path.Combine(fixture.StagingDirectory, "final.attempt-2.mp4"),
            fixture.CompileRequests[1].OutputPath);
        Assert.AreNotEqual(
            fixture.CompileRequests[0].OutputPath,
            fixture.CompileRequests[1].OutputPath);
        Assert.AreEqual(1, fixture.PromotionCount);
        StoredJobAggregate aggregate = fixture.RequiredAggregate();
        Assert.AreEqual(2, aggregate.Attempts.Count);
        Assert.AreEqual(JobState.Failed, aggregate.Attempts[0].State);
        Assert.AreEqual(JobState.Succeeded, aggregate.Attempts[1].State);
    }

    [TestMethod]
    public async Task RequestCancellationStopsRunningProcessAndNeverPromotes()
    {
        using FinalRenderFixture fixture = new();
        TaskCompletionSource<bool> entered = NewSignal();
        fixture.ProcessBehavior = async (_, cancellationToken) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return FinalRenderFixture.SuccessfulProcessResult();
        };
        FinalRenderRequest request = fixture.CreateRequest();

        Task<FinalRenderRunResult> run = fixture.Service.RunAsync(request);
        await entered.Task.WaitAsync(TestTimeout);
        JobCancellationResult cancellation =
            fixture.Service.RequestCancellation(request.JobId);
        FinalRenderRunResult result = await run.WaitAsync(TestTimeout);

        Assert.AreEqual(JobCancellationOutcome.Requested, cancellation.Outcome);
        Assert.AreEqual(FinalRenderDisposition.Cancelled, result.Disposition);
        Assert.AreEqual(JobState.Cancelled, result.Job.Snapshot.State);
        Assert.IsNull(result.FailureReason);
        Assert.IsNull(result.Output);
        Assert.AreEqual(FinalRenderStage.GraphCompiled, result.LastCompletedStage);
        Assert.AreEqual(0, fixture.OutputVerificationCount);
        Assert.AreEqual(0, fixture.PromotionCount);
    }

    [TestMethod]
    public async Task CancellationAfterPromotionCommitFailsClosedForReconciliation()
    {
        using FinalRenderFixture fixture = new();
        TaskCompletionSource<bool> promotionEntered = NewSignal();
        TaskCompletionSource<bool> releasePromotion = NewSignal();
        FinalRenderRequest request = fixture.CreateRequest();
        fixture.PromotionBehavior = async (promotion, cancellationToken) =>
        {
            fixture.ExistingArtifact = fixture.CreateStoredArtifact(promotion);
            promotionEntered.TrySetResult(true);
            await releasePromotion.Task.ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        };

        Task<FinalRenderRunResult> run = fixture.Service.RunAsync(request);
        await promotionEntered.Task.WaitAsync(TestTimeout);
        JobCancellationResult cancellation =
            fixture.Service.RequestCancellation(request.JobId);
        releasePromotion.TrySetResult(true);
        FinalRenderRunResult result = await run.WaitAsync(TestTimeout);

        Assert.AreEqual(JobCancellationOutcome.Requested, cancellation.Outcome);
        Assert.AreEqual(
            FinalRenderDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            FinalRenderFailureReason.ReconciliationRequired,
            result.FailureReason);
        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(FinalRenderStage.OutputVerified, result.LastCompletedStage);
        Assert.IsNull(result.Output);
        Assert.IsNotNull(fixture.ExistingArtifact);
        Assert.AreEqual(1, fixture.RegisteredValidationCount);
    }

    [TestMethod]
    public async Task CancellationDuringDestinationConflictInspectionRequiresReconciliation()
    {
        using FinalRenderFixture fixture = new();
        TaskCompletionSource<bool> promotionEntered = NewSignal();
        TaskCompletionSource<bool> releasePromotion = NewSignal();
        FinalRenderRequest request = fixture.CreateRequest();
        fixture.PromotionBehavior = async (promotion, _) =>
        {
            fixture.ExistingArtifact = fixture.CreateStoredArtifact(promotion);
            promotionEntered.TrySetResult(true);
            await releasePromotion.Task.ConfigureAwait(false);
            throw new ArtifactStoreException(
                ArtifactStoreFailureReason.DestinationExists,
                reconciliationRequired: false);
        };

        Task<FinalRenderRunResult> run = fixture.Service.RunAsync(request);
        await promotionEntered.Task.WaitAsync(TestTimeout);
        JobCancellationResult cancellation =
            fixture.Service.RequestCancellation(request.JobId);
        releasePromotion.TrySetResult(true);
        FinalRenderRunResult result = await run.WaitAsync(TestTimeout);

        Assert.AreEqual(JobCancellationOutcome.Requested, cancellation.Outcome);
        Assert.AreEqual(
            FinalRenderDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            FinalRenderFailureReason.ReconciliationRequired,
            result.FailureReason);
        Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State);
        Assert.AreEqual(FinalRenderStage.OutputVerified, result.LastCompletedStage);
        Assert.IsNull(result.Output);
        Assert.IsNotNull(fixture.ExistingArtifact);
        Assert.AreEqual(1, fixture.RegisteredValidationCount);
    }

    [TestMethod]
    public async Task ExactRegisteredArtifactIsTheOnlyDuplicateProof()
    {
        using FinalRenderFixture fixture = new();
        FinalRenderRequest request = fixture.CreateRequest();
        fixture.ExistingArtifact = fixture.CreateStoredArtifact(request);

        FinalRenderRunResult result = await fixture.Service.RunAsync(request);

        Assert.AreEqual(FinalRenderDisposition.Duplicate, result.Disposition);
        Assert.AreEqual(FinalRenderStage.ArtifactPromoted, result.LastCompletedStage);
        Assert.IsTrue(result.IsComplete);
        Assert.IsNotNull(result.Output);
        Assert.AreEqual(1, fixture.CompileCount);
        Assert.AreEqual(1, fixture.RegisteredValidationCount);
        Assert.AreEqual(0, fixture.ProcessCount);
        Assert.AreEqual(0, fixture.OutputVerificationCount);
        Assert.AreEqual(0, fixture.PromotionCount);
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 5 },
            fixture.RequiredAggregate().Progress
                .Select(static progress => progress.CompletedUnits)
                .ToArray());
    }

    [TestMethod]
    public async Task ConflictingRegisteredArtifactRequiresReconciliationBeforeProcess()
    {
        using FinalRenderFixture fixture = new();
        FinalRenderRequest request = fixture.CreateRequest();
        fixture.ExistingArtifact = fixture.CreateStoredArtifact(
            request,
            contentDigest: FinalRenderFixture.Digest('d'));

        FinalRenderRunResult result = await fixture.Service.RunAsync(request);

        Assert.AreEqual(
            FinalRenderDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            FinalRenderFailureReason.ReconciliationRequired,
            result.FailureReason);
        Assert.AreEqual(FinalRenderStage.GraphCompiled, result.LastCompletedStage);
        Assert.AreEqual(0, fixture.RegisteredValidationCount);
        Assert.AreEqual(0, fixture.ProcessCount);
        Assert.AreEqual(0, fixture.PromotionCount);
        Assert.AreEqual(
            JobRetryOutcome.ErrorNotRetryable,
            fixture.Service.QueueRetry(request.JobId).Outcome);
    }

    [TestMethod]
    public async Task DestinationExistenceWithoutExactRegistrationIsNotADuplicate()
    {
        using FinalRenderFixture fixture = new();
        fixture.PromotionBehavior = static (_, _) => throw new ArtifactStoreException(
            ArtifactStoreFailureReason.DestinationExists,
            reconciliationRequired: false);

        FinalRenderRunResult result = await fixture.Service.RunAsync(
            fixture.CreateRequest());

        Assert.AreEqual(
            FinalRenderDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            FinalRenderFailureReason.ReconciliationRequired,
            result.FailureReason);
        Assert.AreEqual(FinalRenderStage.OutputVerified, result.LastCompletedStage);
        Assert.AreEqual(1, fixture.ProcessCount);
        Assert.AreEqual(1, fixture.PromotionCount);
        Assert.IsNull(fixture.ExistingArtifact);
    }

    [TestMethod]
    public async Task AmbiguousPromotionOutcomesRequireReconciliation()
    {
        using (FinalRenderFixture fixture = new())
        {
            fixture.PromotionBehavior = (promotion, _) => Task.FromResult(
                fixture.CreateStoredArtifact(
                    promotion,
                    contentDigest: FinalRenderFixture.Digest('c')));

            FinalRenderRunResult result = await fixture.Service.RunAsync(
                fixture.CreateRequest());

            Assert.AreEqual(
                FinalRenderDisposition.ReconciliationRequired,
                result.Disposition);
            Assert.AreEqual(
                FinalRenderFailureReason.ReconciliationRequired,
                result.FailureReason);
        }

        using (FinalRenderFixture fixture = new())
        {
            fixture.PromotionBehavior = static (_, _) => throw new ArtifactStoreException(
                ArtifactStoreFailureReason.RollbackFailed,
                reconciliationRequired: true);

            FinalRenderRunResult result = await fixture.Service.RunAsync(
                fixture.CreateRequest());

            Assert.AreEqual(
                FinalRenderDisposition.ReconciliationRequired,
                result.Disposition);
            Assert.AreEqual(
                FinalRenderFailureReason.ReconciliationRequired,
                result.FailureReason);
        }
    }

    [TestMethod]
    public async Task RecoveredRestartedJobRequeuesAndRecognizesCommittedArtifact()
    {
        using FinalRenderFixture fixture = new();
        FinalRenderRequest request = fixture.CreateRequest();
        JobAttempt interruptedAttempt = fixture.StartRunningAttempt();
        fixture.ExistingArtifact = fixture.CreateStoredArtifact(request);
        var recovery = new JobRecovery(
            fixture.Store,
            new WorkspaceRecovery(
                fixture.DatabasePath,
                fixture.RootPath,
                fixture.TimeProvider),
            fixture.TimeProvider);

        JobRecoveryResult recoveryResult = await recovery.RecoverAsync(
            new HashSet<string>([FinalRenderService.JobKind], StringComparer.Ordinal),
            CancellationToken.None);
        FinalRenderRunResult result = await fixture.Service.RunAsync(request);

        RecoveredJob recovered = recoveryResult.RecoveredJobs.Single();
        Assert.AreEqual(request.JobId, recovered.JobId);
        Assert.AreEqual(interruptedAttempt.Id, recovered.AttemptId);
        Assert.IsTrue(recovered.AutomaticallyRequeued);
        Assert.AreEqual(JobState.Queued, recovered.CurrentState);
        Assert.AreEqual(FinalRenderDisposition.Duplicate, result.Disposition);
        Assert.AreEqual(0, fixture.ProcessCount);
        Assert.AreEqual(0, fixture.PromotionCount);
        StoredJobAggregate aggregate = fixture.RequiredAggregate();
        Assert.AreEqual(2, aggregate.Attempts.Count);
        Assert.AreEqual(JobState.Interrupted, aggregate.Attempts[0].State);
        Assert.AreEqual("job.app_restarted", aggregate.Attempts[0].ErrorCode);
        Assert.AreEqual(JobState.Succeeded, aggregate.Attempts[1].State);
    }

    [TestMethod]
    public async Task ConcurrentRunForSameJobCannotStartSecondProcessOrPromotion()
    {
        using FinalRenderFixture fixture = new();
        TaskCompletionSource<bool> processEntered = NewSignal();
        TaskCompletionSource<bool> releaseProcess = NewSignal();
        fixture.ProcessBehavior = async (_, _) =>
        {
            processEntered.TrySetResult(true);
            await releaseProcess.Task.ConfigureAwait(false);
            return FinalRenderFixture.SuccessfulProcessResult();
        };
        FinalRenderRequest request = fixture.CreateRequest();

        Task<FinalRenderRunResult> firstRun = fixture.Service.RunAsync(request);
        await processEntered.Task.WaitAsync(TestTimeout);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fixture.Service.RunAsync(request));
        releaseProcess.TrySetResult(true);
        FinalRenderRunResult completed = await firstRun.WaitAsync(TestTimeout);

        Assert.AreEqual(FinalRenderDisposition.Completed, completed.Disposition);
        Assert.AreEqual(1, fixture.ProcessCount);
        Assert.AreEqual(1, fixture.PromotionCount);
        Assert.AreEqual(1, fixture.RequiredAggregate().Attempts.Count);
    }

    [TestMethod]
    public void RequestSnapshotsCollectionsAndPublicResultsAreImmutableAndPathFree()
    {
        using FinalRenderFixture fixture = new();
        StoredApproval approval = fixture.AppendApproval();
        var approvals = new List<StoredApproval> { approval };
        var inputs = new List<FfmpegInputBinding> { fixture.InputBinding };

        FinalRenderRequest request = fixture.CreateRequest(
            approvalEvidence: approvals,
            inputs: inputs);
        approvals.Clear();
        inputs.Clear();

        Assert.AreEqual(1, request.ApprovalEvidence.Count);
        Assert.AreEqual(1, request.Inputs.Count);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredApproval>)request.ApprovalEvidence).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<FfmpegInputBinding>)request.Inputs).Clear());

        AssertImmutableResultType(
            typeof(FinalRenderOutput),
            "ArtifactId",
            "ContentDigest",
            "ByteSize");
        AssertImmutableResultType(
            typeof(FinalRenderRunResult),
            "Job",
            "LastCompletedStage",
            "Disposition",
            "FailureReason",
            "Output",
            "IsComplete",
            "CanRetry");
        Assert.IsTrue(typeof(FinalRenderService).IsPublic);
        Assert.IsTrue(typeof(FinalRenderService).IsSealed);
        CollectionAssert.AreEquivalent(
            new[] { "RunAsync", "RequestCancellation", "QueueRetry" },
            GetDeclaredPublicMethods(typeof(FinalRenderService))
                .Select(static method => method.Name)
                .ToArray());

        PropertyInfo[] publicRequestProperties = typeof(FinalRenderRequest).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.IsTrue(publicRequestProperties.All(static property =>
            property.SetMethod is null));
        string[] forbiddenNames =
        [
            "Path",
            "Directory",
            "Command",
            "Argument",
            "StandardOutput",
            "StandardError",
            "Exception",
        ];
        Assert.IsFalse(publicRequestProperties.Any(property =>
            forbiddenNames.Any(forbidden =>
                property.Name.Contains(forbidden, StringComparison.Ordinal))));
        Assert.IsFalse(typeof(FinalRenderRunResult).GetProperties().Any(static property =>
            property.PropertyType == typeof(ProcessRunResult) ||
            property.PropertyType == typeof(ProcessRunRequest) ||
            property.PropertyType == typeof(Exception)));
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MethodInfo[] GetDeclaredPublicMethods(Type type) =>
        type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();

    private static void AssertImmutableResultType(
        Type type,
        params string[] expectedProperties)
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
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private sealed class IncrementingTimeProvider(DateTimeOffset initialValue)
        : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _value = initialValue;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                _value = _value.AddTicks(1);
                return _value;
            }
        }
    }

    private sealed class FinalRenderFixture : IDisposable
    {
        private static readonly DateTimeOffset CreatedAt =
            new(2044, 4, 5, 6, 7, 8, TimeSpan.Zero);

        private readonly ApprovalService _approvalService;
        private readonly ReviewStore _reviewStore;
        private StoredApproval? _defaultApproval;
        private int _approvalSequence;
        private int _approvalEvaluationCount;
        private int _graphValidationCount;
        private int _compileCount;
        private int _processCount;
        private int _outputVerificationCount;
        private int _registeredValidationCount;
        private int _promotionCount;

        internal FinalRenderFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.FinalRenderServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));

            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            ContentDigest assetDigest = Digest('1');
            var asset = new Asset(
                assetId,
                "input.mp4",
                "video/mp4",
                assetDigest,
                128,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal test rendering only.",
                    externalPublicationAllowed: false));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Final render test project",
                "Verify exact final rendering.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                [asset],
                performers: []);
            var projectStore = new ProjectStore(DatabasePath);
            projectStore.CreateProject(
                Project,
                "{\"finalRender\":true}",
                CreatedAt);

            var range = new TimeRange(default, new RationalTime(1, 1));
            OriginRef origin = OriginRef.FromAsset(assetId);
            var element = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                range,
                origin);
            ContentDigest compositionDigest =
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]);
            Version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                "S-1-5-21-1000-2000-3000-4000",
                CreatedAt.AddMinutes(1),
                "Create final render fixture version.",
                compositionDigest,
                [element]);
            projectStore.AppendCompositionVersion(
                Version,
                "{\"version\":1}");

            var timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                Version.Id,
                [new TimelineMapEntry(range, origin, sourceRange: null)]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "Fixture template",
                Digest('2'));
            var layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        new NormalizedRect(
                            default,
                            default,
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            ContentDigest layoutDigest = Digest('3');
            var profile = new RenderProfile(
                EntityId<RenderProfileIdTag>.New(),
                "Fixture profile",
                Digest('4'),
                1920,
                1080,
                30,
                1,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            EntityId<RenderInputIdTag> inputId = EntityId<RenderInputIdTag>.New();
            var graphInput = new RenderGraphInput(
                inputId,
                RenderInputKind.Video,
                assetDigest,
                asset.ByteSize,
                origin);
            RenderNode videoNode = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                inputId,
                range,
                origin,
                sourceRange: null,
                "main");
            RenderNode outputNode = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [videoNode.Id],
                range);
            Graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                Version.Id,
                Version.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                [graphInput],
                fonts: [],
                [videoNode, outputNode],
                outputNode.Id);
            ValidationContext = new RenderGraphValidationContext(
                Project,
                Version,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                sourceSpans: [],
                editorialRecordIds: [],
                [new RenderArtifactEvidence(inputId, assetDigest, asset.ByteSize)],
                fonts: []);

            StagingDirectory = Path.Combine(
                RootPath,
                "projects",
                Project.Id.ToString(),
                "staging");
            Directory.CreateDirectory(StagingDirectory);
            StagingOutputPath = Path.Combine(StagingDirectory, "final.mp4");
            ToolPath = Path.Combine(RootPath, "ffmpeg.exe");
            string inputPath = Path.Combine(RootPath, "input.mp4");
            ToolBinding = new FfmpegToolBinding(
                new BinaryVerificationRequest(
                    "ffmpeg",
                    ToolPath,
                    new ContentDigest(FfmpegCompiler.FixedFfmpegSha256),
                    FfmpegCompiler.FixedFfmpegByteSize));
            InputBinding = new FfmpegInputBinding(
                inputId,
                new BinaryVerificationRequest(
                    "input.video",
                    inputPath,
                    assetDigest,
                    asset.ByteSize));

            OutputArtifactId = EntityId<ArtifactIdTag>.New();
            ExpectedOutputDigest = Digest('5');
            ExpectedOutputByteSize = 256;
            OutputCreatedAt = CreatedAt.AddMinutes(2);
            PolicyDigest = Digest('6');

            Store = new JobStore(DatabasePath);
            TimeProvider = new IncrementingTimeProvider(CreatedAt.AddHours(1));
            Job = new Job(
                EntityId<JobIdTag>.New(),
                Project.Id,
                FinalRenderService.JobKind,
                JobState.Queued,
                CreatedAt.AddMinutes(3),
                CreatedAt.AddMinutes(3),
                currentAttemptNumber: 0);
            Store.CreateJob(Job);
            var localRunner = new LocalJobRunner(Store, TimeProvider);
            Coordinator = new JobCoordinator(Store, localRunner, TimeProvider);

            _reviewStore = new ReviewStore(DatabasePath);
            _approvalService = new ApprovalService(
                _reviewStore,
                new AuthorizationService(new RoleAssignmentStore(DatabasePath)));
            Service = new FinalRenderService(
                Coordinator,
                EvaluateApprovals,
                ValidateGraph,
                CompileAsync,
                RunProcessAsync,
                VerifyOutputAsync,
                GetArtifact,
                ValidateArtifactAsync,
                PromoteAsync);
        }

        internal string RootPath { get; }

        internal string DatabasePath { get; }

        internal Project Project { get; }

        internal CompositionVersion Version { get; }

        internal RenderGraph Graph { get; }

        internal RenderGraphValidationContext ValidationContext { get; }

        internal string StagingDirectory { get; }

        internal string StagingOutputPath { get; }

        internal string ToolPath { get; }

        internal FfmpegToolBinding ToolBinding { get; }

        internal FfmpegInputBinding InputBinding { get; }

        internal EntityId<ArtifactIdTag> OutputArtifactId { get; }

        internal ContentDigest ExpectedOutputDigest { get; }

        internal long ExpectedOutputByteSize { get; }

        internal DateTimeOffset OutputCreatedAt { get; }

        internal ContentDigest PolicyDigest { get; }

        internal Job Job { get; }

        internal JobStore Store { get; }

        internal IncrementingTimeProvider TimeProvider { get; }

        internal JobCoordinator Coordinator { get; }

        internal FinalRenderService Service { get; }

        internal bool FailGraphValidation { get; set; }

        internal FfmpegCompilerFailureReason? CompileFailureReason { get; set; }

        internal Exception? OutputVerificationFailure { get; set; }

        internal bool UseRealOutputVerification { get; set; }

        internal Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>?
            ProcessBehavior
        { get; set; }

        internal Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>?
            PromotionBehavior
        { get; set; }

        internal StoredArtifact? ExistingArtifact { get; set; }

        internal List<FfmpegCompileRequest> CompileRequests { get; } = [];

        internal int ApprovalEvaluationCount =>
            Volatile.Read(ref _approvalEvaluationCount);

        internal int GraphValidationCount => Volatile.Read(ref _graphValidationCount);

        internal int CompileCount => Volatile.Read(ref _compileCount);

        internal int ProcessCount => Volatile.Read(ref _processCount);

        internal int OutputVerificationCount =>
            Volatile.Read(ref _outputVerificationCount);

        internal int RegisteredValidationCount =>
            Volatile.Read(ref _registeredValidationCount);

        internal int PromotionCount => Volatile.Read(ref _promotionCount);

        internal FinalRenderRequest CreateRequest(
            ContentDigest? approvedDigest = null,
            IReadOnlyList<StoredApproval>? approvalEvidence = null,
            FinalRenderPolicySnapshot? policySnapshot = null,
            IReadOnlyList<FfmpegInputBinding>? inputs = null) =>
            new(
                Job.Id,
                Project.Id,
                OutputArtifactId,
                Version,
                approvedDigest ?? Version.ContentDigest,
                approvalEvidence ?? [_defaultApproval ??= AppendApproval()],
                policySnapshot ?? new FinalRenderPolicySnapshot(
                    PolicyDigest,
                    PolicyDigest),
                Graph,
                ValidationContext,
                ToolBinding,
                inputs ?? [InputBinding],
                StagingOutputPath,
                "final.mp4",
                RootPath,
                TimeSpan.FromMinutes(5),
                ExpectedOutputDigest,
                ExpectedOutputByteSize,
                OutputCreatedAt);

        internal StoredApproval AppendApproval()
        {
            var approval = new Approval(
                EntityId<ApprovalIdTag>.New(),
                Version,
                "S-1-5-21-1000-2000-3000-4001",
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(10 + Interlocked.Increment(ref _approvalSequence)),
                "Approve the exact final-render composition digest.");
            _reviewStore.AppendApproval(approval);
            return _reviewStore.GetVersionEvidence(Version.Id)?.Approvals
                .Single(candidate => candidate.Id == approval.Id)
                ?? throw new AssertFailedException("Approval evidence was not persisted.");
        }

        internal JobAttempt StartRunningAttempt()
        {
            DateTimeOffset startedAt = TimeProvider.GetUtcNow();
            var attempt = new JobAttempt(
                EntityId<JobAttemptIdTag>.New(),
                Job.Id,
                sequenceNumber: 1,
                JobState.Running,
                Job.UpdatedAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            _ = Store.StartAttempt(Job.Id, 0, attempt, startedAt);
            return attempt;
        }

        internal StoredJobAggregate RequiredAggregate() =>
            Store.GetJobAggregate(Job.Id)
            ?? throw new AssertFailedException("The final-render job was not stored.");

        internal StoredArtifact CreateStoredArtifact(
            FinalRenderRequest request,
            ContentDigest? contentDigest = null) =>
            new(
                request.OutputArtifactId,
                request.ProjectId,
                ArtifactKind.Render,
                string.Concat(
                    "projects/",
                    request.ProjectId.ToString(),
                    "/renders/",
                    request.OutputRelativePath),
                contentDigest ?? request.ExpectedOutputDigest,
                request.ExpectedOutputByteSize,
                request.OutputCreatedAt.ToUniversalTime());

        internal StoredArtifact CreateStoredArtifact(
            ArtifactPromotionRequest request,
            ContentDigest? contentDigest = null) =>
            new(
                request.Id,
                request.ProjectId,
                request.Kind,
                string.Concat(
                    "projects/",
                    request.ProjectId.ToString(),
                    "/renders/",
                    request.RelativePath),
                contentDigest ?? request.ExpectedDigest,
                request.ExpectedByteSize,
                request.CreatedAt.ToUniversalTime());

        internal static ContentDigest Digest(char value) =>
            new(new string(value, 64));

        internal static ProcessRunResult SuccessfulProcessResult() =>
            new(
                0,
                string.Empty,
                string.Empty,
                false,
                TimeSpan.FromSeconds(1));

        private ApprovalValidityResult EvaluateApprovals(
            CompositionVersion compositionVersion,
            IReadOnlyList<StoredApproval> approvalEvidence)
        {
            _ = Interlocked.Increment(ref _approvalEvaluationCount);
            return _approvalService.EvaluateValidity(
                compositionVersion,
                approvalEvidence);
        }

        private RenderGraphValidationResult ValidateGraph(
            RenderGraph graph,
            RenderGraphValidationContext context,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _graphValidationCount);
            if (FailGraphValidation)
            {
                throw new InvalidOperationException("Synthetic graph drift.");
            }

            return RenderGraphValidator.Validate(graph, context, cancellationToken);
        }

        private Task<FfmpegCompilation> CompileAsync(
            FfmpegCompileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _compileCount);
            CompileRequests.Add(request);
            if (CompileFailureReason is FfmpegCompilerFailureReason failure)
            {
                throw new FfmpegCompilerException(failure);
            }

            var processRequest = new ProcessRunRequest(
                request.Tool.VerificationRequest.FilePath,
                argumentList: [],
                request.WorkingDirectory,
                request.Timeout);
            return Task.FromResult(new FfmpegCompilation(
                processRequest,
                request.OutputPath,
                filterScript: string.Empty,
                Digest('7')));
        }

        private Task<ProcessRunResult> RunProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _processCount);
            return ProcessBehavior?.Invoke(request, cancellationToken)
                ?? Task.FromResult(SuccessfulProcessResult());
        }

        private Task VerifyOutputAsync(
            string outputPath,
            ContentDigest expectedDigest,
            long expectedByteSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _outputVerificationCount);
            if (OutputVerificationFailure is not null)
            {
                throw OutputVerificationFailure;
            }

            if (UseRealOutputVerification)
            {
                return new BinaryVerifier().VerifyAsync(
                    new BinaryVerificationRequest(
                        "final-render-test-output",
                        outputPath,
                        expectedDigest,
                        expectedByteSize),
                    cancellationToken);
            }

            Assert.IsTrue(Path.IsPathFullyQualified(outputPath));
            Assert.AreEqual(ExpectedOutputDigest, expectedDigest);
            Assert.AreEqual(ExpectedOutputByteSize, expectedByteSize);
            return Task.CompletedTask;
        }

        private StoredArtifact? GetArtifact(EntityId<ArtifactIdTag> artifactId)
        {
            Assert.AreEqual(OutputArtifactId, artifactId);
            return ExistingArtifact;
        }

        private Task<StoredArtifact> ValidateArtifactAsync(
            EntityId<ArtifactIdTag> artifactId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _registeredValidationCount);
            Assert.AreEqual(OutputArtifactId, artifactId);
            return Task.FromResult(
                ExistingArtifact
                ?? throw new InvalidOperationException(
                    "The fixture has no registered artifact."));
        }

        private async Task<StoredArtifact> PromoteAsync(
            ArtifactPromotionRequest request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _promotionCount);
            if (PromotionBehavior is not null)
            {
                return await PromotionBehavior(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            StoredArtifact artifact = CreateStoredArtifact(request);
            ExistingArtifact = artifact;
            return artifact;
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
