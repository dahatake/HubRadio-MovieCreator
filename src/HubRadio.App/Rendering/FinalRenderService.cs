using HubRadio.App.Jobs;
using HubRadio.App.Reviews;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

public enum FinalRenderStage
{
    NotStarted,
    EvidenceValidated,
    GraphCompiled,
    ProcessCompleted,
    OutputVerified,
    ArtifactPromoted,
}

public enum FinalRenderDisposition
{
    Completed,
    Duplicate,
    Cancelled,
    Failed,
    ReconciliationRequired,
}

public enum FinalRenderFailureReason
{
    ApprovalMismatch,
    PolicyDrift,
    GraphDrift,
    ToolDrift,
    InputDrift,
    CompilationFailed,
    ProcessTimedOut,
    ProcessFailed,
    OutputMismatch,
    PromotionFailed,
    ReconciliationRequired,
    ProgressPersistenceFailed,
}

public sealed class FinalRenderPolicySnapshot
{
    public FinalRenderPolicySnapshot(
        ContentDigest digest,
        ContentDigest currentDigest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(currentDigest);

        Digest = digest;
        CurrentDigest = currentDigest;
    }

    public ContentDigest Digest { get; }

    public ContentDigest CurrentDigest { get; }
}

public sealed class FinalRenderRequest
{
    public FinalRenderRequest(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> outputArtifactId,
        CompositionVersion approvedCompositionVersion,
        ContentDigest approvedCompositionDigest,
        IReadOnlyList<StoredApproval> approvalEvidence,
        FinalRenderPolicySnapshot policySnapshot,
        RenderGraph graph,
        RenderGraphValidationContext validationContext,
        FfmpegToolBinding tool,
        IReadOnlyList<FfmpegInputBinding> inputs,
        string stagingOutputPath,
        string outputRelativePath,
        string workingDirectory,
        TimeSpan timeout,
        ContentDigest expectedOutputDigest,
        long expectedOutputByteSize,
        DateTimeOffset outputCreatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(outputArtifactId);
        ArgumentNullException.ThrowIfNull(approvedCompositionVersion);
        ArgumentNullException.ThrowIfNull(approvedCompositionDigest);
        ArgumentNullException.ThrowIfNull(approvalEvidence);
        ArgumentNullException.ThrowIfNull(policySnapshot);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stagingOutputPath);
        ArgumentNullException.ThrowIfNull(outputRelativePath);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedOutputDigest);

        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The final-render timeout must be positive and supported by the runtime timer.");
        }

        if (expectedOutputByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedOutputByteSize),
                "The expected final-render byte size must be positive.");
        }

        _ = new ArtifactPromotionRequest(
            outputArtifactId,
            projectId,
            ArtifactKind.Render,
            stagingOutputPath,
            outputRelativePath,
            expectedOutputDigest,
            expectedOutputByteSize,
            outputCreatedAt);

        StoredApproval[] copiedApprovals = CopyApprovals(approvalEvidence);
        FfmpegInputBinding[] copiedInputs = CopyInputs(inputs);

        JobId = jobId;
        ProjectId = projectId;
        OutputArtifactId = outputArtifactId;
        ApprovedCompositionVersion = approvedCompositionVersion;
        ApprovedCompositionDigest = approvedCompositionDigest;
        ApprovalEvidence = Array.AsReadOnly(copiedApprovals);
        PolicySnapshot = policySnapshot;
        Graph = graph;
        ValidationContext = validationContext;
        Tool = tool;
        Inputs = Array.AsReadOnly(copiedInputs);
        StagingOutputPath = stagingOutputPath;
        OutputRelativePath = outputRelativePath;
        WorkingDirectory = workingDirectory;
        Timeout = timeout;
        ExpectedOutputDigest = expectedOutputDigest;
        ExpectedOutputByteSize = expectedOutputByteSize;
        OutputCreatedAt = outputCreatedAt;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<ArtifactIdTag> OutputArtifactId { get; }

    public CompositionVersion ApprovedCompositionVersion { get; }

    public ContentDigest ApprovedCompositionDigest { get; }

    public IReadOnlyList<StoredApproval> ApprovalEvidence { get; }

    public FinalRenderPolicySnapshot PolicySnapshot { get; }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    internal FfmpegToolBinding Tool { get; }

    internal IReadOnlyList<FfmpegInputBinding> Inputs { get; }

    internal string StagingOutputPath { get; }

    internal string OutputRelativePath { get; }

    internal string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }

    public ContentDigest ExpectedOutputDigest { get; }

    public long ExpectedOutputByteSize { get; }

    public DateTimeOffset OutputCreatedAt { get; }

    private static StoredApproval[] CopyApprovals(
        IReadOnlyList<StoredApproval> approvals)
    {
        var copied = new StoredApproval[approvals.Count];
        var ids = new HashSet<EntityId<ApprovalIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            StoredApproval approval = approvals[index]
                ?? throw new ArgumentException(
                    "Approval evidence must not contain null values.",
                    nameof(approvals));
            if (!ids.Add(approval.Id))
            {
                throw new ArgumentException(
                    "Approval evidence must use unique approval identities.",
                    nameof(approvals));
            }

            copied[index] = approval;
        }

        return copied;
    }

    private static FfmpegInputBinding[] CopyInputs(
        IReadOnlyList<FfmpegInputBinding> inputs)
    {
        var copied = new FfmpegInputBinding[inputs.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            FfmpegInputBinding input = inputs[index]
                ?? throw new ArgumentException(
                    "FFmpeg input bindings must not contain null values.",
                    nameof(inputs));
            if (!ids.Add(input.InputId))
            {
                throw new ArgumentException(
                    "FFmpeg input bindings must use unique input identities.",
                    nameof(inputs));
            }

            copied[index] = input;
        }

        return copied;
    }
}

public sealed class FinalRenderOutput
{
    internal FinalRenderOutput(
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest contentDigest,
        long byteSize)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(contentDigest);
        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteSize));
        }

        ArtifactId = artifactId;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }
}

public sealed class FinalRenderRunResult
{
    internal FinalRenderRunResult(
        JobCoordinatorRunResult job,
        FinalRenderStage lastCompletedStage,
        FinalRenderDisposition disposition,
        FinalRenderFailureReason? failureReason,
        FinalRenderOutput? output)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Enum.IsDefined(lastCompletedStage))
        {
            throw new ArgumentOutOfRangeException(nameof(lastCompletedStage));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (failureReason is not null && !Enum.IsDefined(failureReason.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(failureReason));
        }

        bool valid = disposition switch
        {
            FinalRenderDisposition.Completed or FinalRenderDisposition.Duplicate =>
                job.Snapshot.State == JobState.Succeeded &&
                lastCompletedStage == FinalRenderStage.ArtifactPromoted &&
                failureReason is null &&
                output is not null,
            FinalRenderDisposition.Cancelled =>
                job.Snapshot.State == JobState.Cancelled &&
                failureReason is null &&
                output is null,
            FinalRenderDisposition.Failed =>
                job.Snapshot.State == JobState.Failed &&
                failureReason is not null &&
                output is null,
            FinalRenderDisposition.ReconciliationRequired =>
                job.Snapshot.State == JobState.Failed &&
                failureReason == FinalRenderFailureReason.ReconciliationRequired &&
                output is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The final-render result is inconsistent.",
                nameof(disposition));
        }

        Job = job;
        LastCompletedStage = lastCompletedStage;
        Disposition = disposition;
        FailureReason = failureReason;
        Output = output;
    }

    public JobCoordinatorRunResult Job { get; }

    public FinalRenderStage LastCompletedStage { get; }

    public FinalRenderDisposition Disposition { get; }

    public FinalRenderFailureReason? FailureReason { get; }

    public FinalRenderOutput? Output { get; }

    public bool IsComplete =>
        Disposition is FinalRenderDisposition.Completed or FinalRenderDisposition.Duplicate;

    public bool CanRetry =>
        Disposition == FinalRenderDisposition.Failed &&
        Job.Snapshot.Retryability == JobRetryability.Retryable;
}

public sealed class FinalRenderService
{
    public const string JobKind = "render.final.v1";

    private const long TotalStages = (long)FinalRenderStage.ArtifactPromoted;

    private readonly JobCoordinator _jobCoordinator;
    private readonly ApprovalDelegate _evaluateApprovals;
    private readonly ValidateGraphDelegate _validateGraph;
    private readonly CompileDelegate _compileAsync;
    private readonly RunProcessDelegate _runProcessAsync;
    private readonly VerifyOutputDelegate _verifyOutputAsync;
    private readonly GetArtifactDelegate _getArtifact;
    private readonly ValidateArtifactDelegate _validateArtifactAsync;
    private readonly PromoteDelegate _promoteAsync;

    public FinalRenderService(
        JobCoordinator jobCoordinator,
        ApprovalService approvalService,
        ProcessRunner processRunner,
        ArtifactStore artifactStore)
        : this(
            jobCoordinator,
            GetApprovalDelegate(approvalService),
            RenderGraphValidator.Validate,
            FfmpegCompiler.CompileAsync,
            GetProcessDelegate(processRunner),
            VerifyOutputAsync,
            GetArtifactDelegateFor(artifactStore),
            GetValidateArtifactDelegate(artifactStore),
            GetPromoteDelegate(artifactStore))
    {
    }

    internal FinalRenderService(
        JobCoordinator jobCoordinator,
        ApprovalDelegate evaluateApprovals,
        ValidateGraphDelegate validateGraph,
        CompileDelegate compileAsync,
        RunProcessDelegate runProcessAsync,
        VerifyOutputDelegate verifyOutputAsync,
        GetArtifactDelegate getArtifact,
        ValidateArtifactDelegate validateArtifactAsync,
        PromoteDelegate promoteAsync)
    {
        ArgumentNullException.ThrowIfNull(jobCoordinator);
        ArgumentNullException.ThrowIfNull(evaluateApprovals);
        ArgumentNullException.ThrowIfNull(validateGraph);
        ArgumentNullException.ThrowIfNull(compileAsync);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyOutputAsync);
        ArgumentNullException.ThrowIfNull(getArtifact);
        ArgumentNullException.ThrowIfNull(validateArtifactAsync);
        ArgumentNullException.ThrowIfNull(promoteAsync);

        _jobCoordinator = jobCoordinator;
        _evaluateApprovals = evaluateApprovals;
        _validateGraph = validateGraph;
        _compileAsync = compileAsync;
        _runProcessAsync = runProcessAsync;
        _verifyOutputAsync = verifyOutputAsync;
        _getArtifact = getArtifact;
        _validateArtifactAsync = validateArtifactAsync;
        _promoteAsync = promoteAsync;
    }

    public async Task<FinalRenderRunResult> RunAsync(
        FinalRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQueuedJob(request);

        FinalRenderStage stage = FinalRenderStage.NotStarted;
        FinalRenderDisposition disposition = FinalRenderDisposition.Failed;
        FinalRenderFailureReason? failureReason = null;
        FinalRenderOutput? output = null;

        JobCoordinatorRunResult job = await _jobCoordinator.RunAsync(
            request.JobId,
            async (context, token) =>
            {
                void Report(FinalRenderStage completedStage)
                {
                    stage = completedStage;
                    try
                    {
                        context.ReportProgress(
                            completed: (long)completedStage,
                            total: TotalStages,
                            phase: GetPhase(completedStage),
                            message: null);
                        context.RecordHeartbeat();
                    }
                    catch
                    {
                        if (completedStage == FinalRenderStage.ArtifactPromoted ||
                            output is not null)
                        {
                            failureReason =
                                FinalRenderFailureReason.ReconciliationRequired;
                            disposition =
                                FinalRenderDisposition.ReconciliationRequired;
                        }
                        else
                        {
                            failureReason =
                                FinalRenderFailureReason.ProgressPersistenceFailed;
                        }

                        throw;
                    }
                }

                try
                {
                    token.ThrowIfCancellationRequested();
                    failureReason = GetEvidenceFailure(request, token);
                    if (failureReason is not null)
                    {
                        throw new InvalidOperationException(
                            "The final-render evidence does not match the approved snapshot.");
                    }

                    Report(FinalRenderStage.EvidenceValidated);

                    FfmpegCompilation compilation;
                    try
                    {
                        string attemptOutputPath = GetAttemptOutputPath(
                            request.StagingOutputPath,
                            context.SequenceNumber);
                        compilation = await _compileAsync(
                            new FfmpegCompileRequest(
                                request.Graph,
                                request.ValidationContext,
                                request.Tool,
                                request.Inputs,
                                attemptOutputPath,
                                request.WorkingDirectory,
                                request.Timeout),
                            token).ConfigureAwait(false);
                    }
                    catch (FfmpegCompilerException exception)
                    {
                        failureReason = ClassifyCompilerFailure(exception.Reason);
                        throw;
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        failureReason = FinalRenderFailureReason.ProcessTimedOut;
                        throw;
                    }
                    catch
                    {
                        failureReason = FinalRenderFailureReason.CompilationFailed;
                        throw;
                    }

                    Report(FinalRenderStage.GraphCompiled);

                    ExistingOutputInspection existing =
                        await InspectExistingOutputAsync(
                            request,
                            token).ConfigureAwait(false);
                    if (existing.State == ExistingOutputState.Conflict)
                    {
                        failureReason =
                            FinalRenderFailureReason.ReconciliationRequired;
                        disposition =
                            FinalRenderDisposition.ReconciliationRequired;
                        throw new InvalidOperationException(
                            "The registered final-render output requires reconciliation.");
                    }

                    if (existing.Output is not null)
                    {
                        output = existing.Output;
                        disposition = FinalRenderDisposition.Duplicate;
                        Report(FinalRenderStage.ArtifactPromoted);
                        return;
                    }

                    ProcessRunResult processResult;
                    try
                    {
                        processResult = await _runProcessAsync(
                            compilation.ProcessRequest,
                            token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        failureReason = FinalRenderFailureReason.ProcessTimedOut;
                        throw;
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch
                    {
                        failureReason = FinalRenderFailureReason.ProcessFailed;
                        throw;
                    }

                    if (processResult.ExitCode != 0)
                    {
                        failureReason = FinalRenderFailureReason.ProcessFailed;
                        throw new InvalidOperationException(
                            "The final-render process returned a nonzero exit code.");
                    }

                    Report(FinalRenderStage.ProcessCompleted);

                    try
                    {
                        await _verifyOutputAsync(
                            compilation.OutputPath,
                            request.ExpectedOutputDigest,
                            request.ExpectedOutputByteSize,
                            token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch
                    {
                        failureReason = FinalRenderFailureReason.OutputMismatch;
                        throw;
                    }

                    Report(FinalRenderStage.OutputVerified);

                    StoredArtifact promoted;
                    try
                    {
                        promoted = await _promoteAsync(
                            new ArtifactPromotionRequest(
                                request.OutputArtifactId,
                                request.ProjectId,
                                ArtifactKind.Render,
                                compilation.OutputPath,
                                request.OutputRelativePath,
                                request.ExpectedOutputDigest,
                                request.ExpectedOutputByteSize,
                                request.OutputCreatedAt),
                            token).ConfigureAwait(false);
                    }
                    catch (ArtifactStoreException exception)
                        when (exception.ReconciliationRequired)
                    {
                        failureReason =
                            FinalRenderFailureReason.ReconciliationRequired;
                        disposition =
                            FinalRenderDisposition.ReconciliationRequired;
                        throw;
                    }
                    catch (ArtifactStoreException exception)
                        when (exception.Reason ==
                            ArtifactStoreFailureReason.DestinationExists)
                    {
                        ExistingOutputInspection duplicate =
                            await InspectExistingOutputAsync(
                                request,
                                token).ConfigureAwait(false);
                        if (duplicate.Output is null)
                        {
                            failureReason =
                                FinalRenderFailureReason.ReconciliationRequired;
                            disposition =
                                FinalRenderDisposition.ReconciliationRequired;
                            throw;
                        }

                        output = duplicate.Output;
                        disposition = FinalRenderDisposition.Duplicate;
                        Report(FinalRenderStage.ArtifactPromoted);
                        return;
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        ExistingOutputInspection committed =
                            await InspectExistingOutputAsync(
                                request,
                                CancellationToken.None).ConfigureAwait(false);
                        if (committed.State != ExistingOutputState.Missing)
                        {
                            failureReason =
                                FinalRenderFailureReason.ReconciliationRequired;
                            disposition =
                                FinalRenderDisposition.ReconciliationRequired;
                            throw new InvalidOperationException(
                                "Cancellation raced with final-render artifact promotion.",
                                exception);
                        }

                        throw;
                    }
                    catch
                    {
                        ExistingOutputInspection committed =
                            await InspectExistingOutputAsync(
                                request,
                                CancellationToken.None).ConfigureAwait(false);
                        if (committed.State == ExistingOutputState.Missing)
                        {
                            failureReason =
                                FinalRenderFailureReason.PromotionFailed;
                        }
                        else
                        {
                            failureReason =
                                FinalRenderFailureReason.ReconciliationRequired;
                            disposition =
                                FinalRenderDisposition.ReconciliationRequired;
                        }

                        throw;
                    }

                    if (!MatchesExactArtifact(request, promoted))
                    {
                        failureReason =
                            FinalRenderFailureReason.ReconciliationRequired;
                        disposition =
                            FinalRenderDisposition.ReconciliationRequired;
                        throw new InvalidOperationException(
                            "The promoted final-render output requires reconciliation.");
                    }

                    output = CreateOutput(promoted);
                    disposition = FinalRenderDisposition.Completed;
                    Report(FinalRenderStage.ArtifactPromoted);
                }
                catch (OperationCanceledException exception)
                    when (token.IsCancellationRequested &&
                        exception.CancellationToken == token)
                {
                    ExistingOutputInspection committed =
                        await InspectExistingOutputAsync(
                            request,
                            CancellationToken.None).ConfigureAwait(false);
                    if (committed.State != ExistingOutputState.Missing)
                    {
                        failureReason =
                            FinalRenderFailureReason.ReconciliationRequired;
                        disposition =
                            FinalRenderDisposition.ReconciliationRequired;
                        throw new InvalidOperationException(
                            "Cancellation raced with a registered final-render artifact.",
                            exception);
                    }

                    throw;
                }
                catch (TimeoutException)
                {
                    failureReason ??=
                        FinalRenderFailureReason.ProcessTimedOut;
                    throw;
                }
                catch
                {
                    if (failureReason is null)
                    {
                        failureReason = FinalRenderFailureReason.ProcessFailed;
                    }

                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (job.Snapshot.State == JobState.Cancelled)
        {
            disposition = FinalRenderDisposition.Cancelled;
            failureReason = null;
            output = null;
        }
        else if (job.Snapshot.State == JobState.Failed)
        {
            output = null;
            if (disposition != FinalRenderDisposition.ReconciliationRequired)
            {
                disposition = FinalRenderDisposition.Failed;
            }
        }

        return new FinalRenderRunResult(
            job,
            stage,
            disposition,
            failureReason,
            output);
    }

    public JobCancellationResult RequestCancellation(
        EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return _jobCoordinator.RequestCancellation(jobId);
    }

    public JobRetryResult QueueRetry(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return _jobCoordinator.QueueRetry(jobId);
    }

    private void ValidateQueuedJob(FinalRenderRequest request)
    {
        StoredJob stored = _jobCoordinator.GetStoredJob(request.JobId);
        if (stored.Job.ProjectId != request.ProjectId ||
            !string.Equals(stored.Job.Kind, JobKind, StringComparison.Ordinal) ||
            stored.Job.State != JobState.Queued)
        {
            throw new InvalidOperationException(
                "The final renderer requires its exact queued job.");
        }
    }

    private FinalRenderFailureReason? GetEvidenceFailure(
        FinalRenderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompositionVersion composition = request.ApprovedCompositionVersion;
        if (composition.ProjectId != request.ProjectId ||
            composition.ContentDigest != request.ApprovedCompositionDigest ||
            request.Graph.CompositionVersionId != composition.Id ||
            request.Graph.CompositionVersionDigest !=
                request.ApprovedCompositionDigest ||
            request.ValidationContext.Project.Id != request.ProjectId ||
            request.ValidationContext.CompositionVersion.Id != composition.Id ||
            request.ValidationContext.CompositionVersion.ContentDigest !=
                request.ApprovedCompositionDigest)
        {
            return FinalRenderFailureReason.ApprovalMismatch;
        }

        ApprovalValidityResult approval;
        if (request.ApprovalEvidence.Count == 0)
        {
            return FinalRenderFailureReason.ApprovalMismatch;
        }

        try
        {
            approval = _evaluateApprovals(
                composition,
                request.ApprovalEvidence);
        }
        catch
        {
            return FinalRenderFailureReason.ApprovalMismatch;
        }

        if (approval.StaleApprovals.Count != 0 ||
            approval.ValidApprovals.Count != request.ApprovalEvidence.Count)
        {
            return FinalRenderFailureReason.ApprovalMismatch;
        }

        if (request.PolicySnapshot.Digest != request.PolicySnapshot.CurrentDigest)
        {
            return FinalRenderFailureReason.PolicyDrift;
        }

        try
        {
            RenderGraphValidationResult validation = _validateGraph(
                request.Graph,
                request.ValidationContext,
                cancellationToken);
            return validation.IsValid
                ? null
                : FinalRenderFailureReason.GraphDrift;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested &&
                exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            return FinalRenderFailureReason.GraphDrift;
        }
    }

    private async Task<ExistingOutputInspection> InspectExistingOutputAsync(
        FinalRenderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            StoredArtifact? registered = _getArtifact(request.OutputArtifactId);
            if (registered is null)
            {
                return ExistingOutputInspection.Missing;
            }

            if (!MatchesExactArtifact(request, registered))
            {
                return ExistingOutputInspection.Conflict;
            }

            StoredArtifact verified = await _validateArtifactAsync(
                request.OutputArtifactId,
                cancellationToken).ConfigureAwait(false);
            return MatchesExactArtifact(request, verified)
                ? ExistingOutputInspection.Exact(CreateOutput(verified))
                : ExistingOutputInspection.Conflict;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested &&
                exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            return ExistingOutputInspection.Conflict;
        }
    }

    private static bool MatchesExactArtifact(
        FinalRenderRequest request,
        StoredArtifact artifact) =>
        artifact.Id == request.OutputArtifactId &&
            artifact.ProjectId == request.ProjectId &&
            artifact.Kind == ArtifactKind.Render &&
            artifact.ContentDigest == request.ExpectedOutputDigest &&
            artifact.ByteSize == request.ExpectedOutputByteSize &&
            artifact.CreatedAt == request.OutputCreatedAt.ToUniversalTime() &&
            string.Equals(
                artifact.StorageReference,
                GetExpectedStorageReference(request),
                StringComparison.Ordinal);

    private static string GetAttemptOutputPath(
        string stagingOutputPath,
        int sequenceNumber)
    {
        if (sequenceNumber <= 1)
        {
            return stagingOutputPath;
        }

        string? directory = Path.GetDirectoryName(stagingOutputPath);
        string fileName = Path.GetFileNameWithoutExtension(stagingOutputPath);
        string extension = Path.GetExtension(stagingOutputPath);
        if (directory is null || fileName.Length == 0 || extension.Length == 0)
        {
            throw new ArgumentException(
                "The staging output path cannot be versioned for a retry.",
                nameof(stagingOutputPath));
        }

        return Path.Combine(
            directory,
            string.Concat(
                fileName,
                ".attempt-",
                sequenceNumber.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                extension));
    }

    private static string GetExpectedStorageReference(FinalRenderRequest request) =>
        string.Concat(
            "projects/",
            request.ProjectId.ToString(),
            "/renders/",
            request.OutputRelativePath);

    private static FinalRenderOutput CreateOutput(StoredArtifact artifact) =>
        new(artifact.Id, artifact.ContentDigest, artifact.ByteSize);

    private static FinalRenderFailureReason ClassifyCompilerFailure(
        FfmpegCompilerFailureReason reason) =>
        reason switch
        {
            FfmpegCompilerFailureReason.GraphInvalid =>
                FinalRenderFailureReason.GraphDrift,
            FfmpegCompilerFailureReason.ToolNotVerified =>
                FinalRenderFailureReason.ToolDrift,
            FfmpegCompilerFailureReason.InputNotVerified or
            FfmpegCompilerFailureReason.InputBindingMismatch =>
                FinalRenderFailureReason.InputDrift,
            _ => FinalRenderFailureReason.CompilationFailed,
        };

    private static string GetPhase(FinalRenderStage stage) =>
        stage switch
        {
            FinalRenderStage.EvidenceValidated => "render.final.evidence",
            FinalRenderStage.GraphCompiled => "render.final.compile",
            FinalRenderStage.ProcessCompleted => "render.final.process",
            FinalRenderStage.OutputVerified => "render.final.verify",
            FinalRenderStage.ArtifactPromoted => "render.final.promote",
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "An unfinished final-render stage cannot be reported."),
        };

    private static async Task VerifyOutputAsync(
        string outputPath,
        ContentDigest expectedDigest,
        long expectedByteSize,
        CancellationToken cancellationToken)
    {
        var verifier = new BinaryVerifier();
        _ = await verifier.VerifyAsync(
            new BinaryVerificationRequest(
                "final-render-output",
                outputPath,
                expectedDigest,
                expectedByteSize),
            cancellationToken).ConfigureAwait(false);
    }

    private static ApprovalDelegate GetApprovalDelegate(
        ApprovalService approvalService)
    {
        ArgumentNullException.ThrowIfNull(approvalService);
        return approvalService.EvaluateValidity;
    }

    private static RunProcessDelegate GetProcessDelegate(
        ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static GetArtifactDelegate GetArtifactDelegateFor(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.GetArtifact;
    }

    private static ValidateArtifactDelegate GetValidateArtifactDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.ValidateRegisteredAsync;
    }

    private static PromoteDelegate GetPromoteDelegate(ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.PromoteAsync;
    }

    internal delegate ApprovalValidityResult ApprovalDelegate(
        CompositionVersion compositionVersion,
        IReadOnlyList<StoredApproval> approvalEvidence);

    internal delegate RenderGraphValidationResult ValidateGraphDelegate(
        RenderGraph graph,
        RenderGraphValidationContext context,
        CancellationToken cancellationToken);

    internal delegate Task<FfmpegCompilation> CompileDelegate(
        FfmpegCompileRequest request,
        CancellationToken cancellationToken);

    internal delegate Task<ProcessRunResult> RunProcessDelegate(
        ProcessRunRequest request,
        CancellationToken cancellationToken);

    internal delegate Task VerifyOutputDelegate(
        string outputPath,
        ContentDigest expectedDigest,
        long expectedByteSize,
        CancellationToken cancellationToken);

    internal delegate StoredArtifact? GetArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId);

    internal delegate Task<StoredArtifact> ValidateArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken);

    internal delegate Task<StoredArtifact> PromoteDelegate(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken);

    private enum ExistingOutputState
    {
        Missing,
        Exact,
        Conflict,
    }

    private sealed record ExistingOutputInspection(
        ExistingOutputState State,
        FinalRenderOutput? Output)
    {
        internal static ExistingOutputInspection Missing { get; } =
            new(ExistingOutputState.Missing, null);

        internal static ExistingOutputInspection Conflict { get; } =
            new(ExistingOutputState.Conflict, null);

        internal static ExistingOutputInspection Exact(FinalRenderOutput output) =>
            new(ExistingOutputState.Exact, output);
    }
}