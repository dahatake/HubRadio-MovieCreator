using System.Buffers;
using System.Text;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Media;

public enum IngestWorkflowStage
{
    NotStarted,
    CopiedToStaging,
    Probed,
    SourceIndexed,
    WarningsNormalized,
    DecodeValidated,
    OriginalCommitted,
    ProxyCommitted,
    FrameMapBuilt,
    PreviewAssetsCommitted,
}

public enum IngestWorkflowFailureReason
{
    ImportFailed,
    ProbeFailed,
    SourceIndexFailed,
    WarningNormalizationFailed,
    DecodeValidationFailed,
    OriginalPromotionFailed,
    ProxyGenerationFailed,
    FrameMapGenerationFailed,
    PreviewGenerationFailed,
    ProgressPersistenceFailed,
}

public sealed class IngestWorkflowRequest
{
    public const string CurrentVersion = "media-ingest-workflow-v1";
    internal const long StageCount = 9;

    public IngestWorkflowRequest(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        EntityId<AssetIdTag> assetCandidateId,
        EntityId<ArtifactIdTag> originalArtifactId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        EntityId<ArtifactIdTag> waveformIndexArtifactId,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        EntityId<ArtifactIdTag> spriteIndexArtifactId,
        string mediaType,
        RightsRecord rights,
        IngestValidationRequirements validationRequirements,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(assetCandidateId);
        ArgumentNullException.ThrowIfNull(originalArtifactId);
        ArgumentNullException.ThrowIfNull(proxyArtifactId);
        ArgumentNullException.ThrowIfNull(waveformIndexArtifactId);
        ArgumentNullException.ThrowIfNull(spriteImageArtifactId);
        ArgumentNullException.ThrowIfNull(spriteIndexArtifactId);
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(rights);
        ArgumentNullException.ThrowIfNull(validationRequirements);

        if (string.IsNullOrWhiteSpace(mediaType) ||
            !string.Equals(mediaType, mediaType.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The ingest media type must be trimmed and nonblank.",
                nameof(mediaType));
        }

        if (operationTimeout <= TimeSpan.Zero ||
            operationTimeout > Infrastructure.Processes.ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The ingest operation timeout must be positive and supported by the runtime timer.");
        }

        EntityId<ArtifactIdTag>[] artifactIds =
        [
            originalArtifactId,
            proxyArtifactId,
            waveformIndexArtifactId,
            spriteImageArtifactId,
            spriteIndexArtifactId,
        ];
        if (artifactIds.Distinct().Count() != artifactIds.Length)
        {
            throw new ArgumentException(
                "Every ingest output must have a distinct artifact ID.",
                nameof(originalArtifactId));
        }

        Version = CurrentVersion;
        JobId = jobId;
        ProjectId = projectId;
        AssetCandidateId = assetCandidateId;
        OriginalArtifactId = originalArtifactId;
        ProxyArtifactId = proxyArtifactId;
        WaveformIndexArtifactId = waveformIndexArtifactId;
        SpriteImageArtifactId = spriteImageArtifactId;
        SpriteIndexArtifactId = spriteIndexArtifactId;
        MediaType = mediaType;
        Rights = rights;
        ValidationRequirements = validationRequirements;
        OperationTimeout = operationTimeout;
        RequestDigest = ComputeDigest();
    }

    public string Version { get; }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<AssetIdTag> AssetCandidateId { get; }

    public EntityId<ArtifactIdTag> OriginalArtifactId { get; }

    public EntityId<ArtifactIdTag> ProxyArtifactId { get; }

    public EntityId<ArtifactIdTag> WaveformIndexArtifactId { get; }

    public EntityId<ArtifactIdTag> SpriteImageArtifactId { get; }

    public EntityId<ArtifactIdTag> SpriteIndexArtifactId { get; }

    public string MediaType { get; }

    public RightsRecord Rights { get; }

    public IngestValidationRequirements ValidationRequirements { get; }

    public TimeSpan OperationTimeout { get; }

    public ContentDigest RequestDigest { get; }

    private ContentDigest ComputeDigest()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", Version);
            writer.WriteNumber("stageCount", StageCount);
            writer.WriteString("jobId", JobId.ToString());
            writer.WriteString("projectId", ProjectId.ToString());
            writer.WriteString("assetCandidateId", AssetCandidateId.ToString());
            writer.WriteString("originalArtifactId", OriginalArtifactId.ToString());
            writer.WriteString("proxyArtifactId", ProxyArtifactId.ToString());
            writer.WriteString(
                "waveformIndexArtifactId",
                WaveformIndexArtifactId.ToString());
            writer.WriteString(
                "spriteImageArtifactId",
                SpriteImageArtifactId.ToString());
            writer.WriteString(
                "spriteIndexArtifactId",
                SpriteIndexArtifactId.ToString());
            writer.WriteString("mediaType", MediaType);
            writer.WritePropertyName("rights");
            writer.WriteStartObject();
            writer.WriteString("status", Rights.Status.ToString());
            if (Rights.EvidenceReference is null)
            {
                writer.WriteNull("evidenceReference");
            }
            else
            {
                writer.WriteString(
                    "evidenceReference",
                    Rights.EvidenceReference);
            }

            writer.WriteString("allowedUse", Rights.AllowedUse);
            writer.WriteBoolean(
                "externalPublicationAllowed",
                Rights.ExternalPublicationAllowed);
            writer.WriteEndObject();
            if (ValidationRequirements.MinimumVideoWidth is int minimumWidth)
            {
                writer.WriteNumber("minimumVideoWidth", minimumWidth);
                writer.WriteNumber(
                    "minimumVideoHeight",
                    ValidationRequirements.MinimumVideoHeight!.Value);
            }
            else
            {
                writer.WriteNull("minimumVideoWidth");
                writer.WriteNull("minimumVideoHeight");
            }

            writer.WriteNumber("operationTimeoutTicks", OperationTimeout.Ticks);
            writer.WriteEndObject();
            writer.Flush();
        }

        return ContentDigest.Compute(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }
}

public sealed class IngestWorkflowRunResult
{
    private readonly WorkflowResumeState _resumeState;

    internal IngestWorkflowRunResult(
        ContentDigest requestDigest,
        JobCoordinatorRunResult job,
        WorkflowResumeState resumeState,
        IngestWorkflowFailureReason? failureReason,
        bool reconciliationRequired)
    {
        ArgumentNullException.ThrowIfNull(requestDigest);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(resumeState);
        if (job.Snapshot.JobId != resumeState.JobId ||
            requestDigest != resumeState.RequestDigest ||
            (job.Snapshot.State == JobState.Succeeded &&
                (resumeState.Stage !=
                    IngestWorkflowStage.PreviewAssetsCommitted ||
                    failureReason is not null)) ||
            (job.Snapshot.State == JobState.Failed && failureReason is null) ||
            job.Snapshot.State == JobState.Cancelled && failureReason is not null)
        {
            throw new ArgumentException(
                "The ingest workflow result is inconsistent.",
                nameof(resumeState));
        }

        RequestDigest = requestDigest;
        Job = job;
        LastCompletedStage = resumeState.Stage;
        FailureReason = failureReason;
        ReconciliationRequired = reconciliationRequired;
        Asset = resumeState.Asset;
        SourcePtsIndex = resumeState.SourcePtsIndex;
        Original = resumeState.Original;
        Proxy = resumeState.Proxy;
        FrameMap = resumeState.FrameMap;
        PreviewAssets = resumeState.PreviewAssets;
        Warnings = Array.AsReadOnly(
            (resumeState.MediaInfo?.Warnings ?? []).ToArray());
        _resumeState = resumeState;
    }

    public ContentDigest RequestDigest { get; }

    public JobCoordinatorRunResult Job { get; }

    public IngestWorkflowStage LastCompletedStage { get; }

    public IngestWorkflowFailureReason? FailureReason { get; }

    public bool ReconciliationRequired { get; }

    public bool IsComplete =>
        Job.Snapshot.State == JobState.Succeeded &&
        LastCompletedStage == IngestWorkflowStage.PreviewAssetsCommitted;

    public bool CanResumeWithThisResult =>
        !IsComplete &&
        !ReconciliationRequired &&
        Job.Snapshot.Retryability == JobRetryability.Retryable;

    public Asset? Asset { get; }

    public PtsIndex? SourcePtsIndex { get; }

    public ImportedOriginal? Original { get; }

    public GeneratedProxy? Proxy { get; }

    public ProxyFrameMap? FrameMap { get; }

    public GeneratedPreviewAssets? PreviewAssets { get; }

    public IReadOnlyList<MediaInfoWarning> Warnings { get; }

    internal WorkflowResumeState CopyResumeState() => _resumeState.Copy();
}

public sealed class IngestWorkflow
{
    public const string JobKind = "media.ingest.v1";

    private const long TotalStages = IngestWorkflowRequest.StageCount;

    private readonly JobCoordinator _jobCoordinator;
    private readonly CopyDelegate _copyAsync;
    private readonly ProbeDelegate _probeAsync;
    private readonly IndexDelegate _indexAsync;
    private readonly NormalizeDelegate _normalize;
    private readonly DecodeDelegate _decodeAsync;
    private readonly PromoteOriginalDelegate _promoteOriginalAsync;
    private readonly GenerateProxyDelegate _generateProxyAsync;
    private readonly GenerateFrameMapDelegate _generateFrameMap;
    private readonly GeneratePreviewDelegate _generatePreviewAsync;

    public IngestWorkflow(
        JobCoordinator jobCoordinator,
        MediaImporter mediaImporter,
        FfprobeClient ffprobeClient,
        PtsIndexer ptsIndexer,
        DecodeValidator decodeValidator,
        IngestService ingestService,
        ProxyGenerator proxyGenerator,
        ProxyFrameMapGenerator proxyFrameMapGenerator,
        PreviewAssetGenerator previewAssetGenerator)
        : this(
            jobCoordinator,
            GetCopyDelegate(mediaImporter),
            GetProbeDelegate(ffprobeClient),
            GetIndexDelegate(ptsIndexer),
            IngestValidator.Validate,
            GetDecodeDelegate(decodeValidator),
            GetPromoteOriginalDelegate(ingestService),
            GetGenerateProxyDelegate(proxyGenerator),
            GetGenerateFrameMapDelegate(proxyFrameMapGenerator),
            GetGeneratePreviewDelegate(previewAssetGenerator))
    {
    }

    internal IngestWorkflow(
        JobCoordinator jobCoordinator,
        CopyDelegate copyAsync,
        ProbeDelegate probeAsync,
        IndexDelegate indexAsync,
        NormalizeDelegate normalize,
        DecodeDelegate decodeAsync,
        PromoteOriginalDelegate promoteOriginalAsync,
        GenerateProxyDelegate generateProxyAsync,
        GenerateFrameMapDelegate generateFrameMap,
        GeneratePreviewDelegate generatePreviewAsync)
    {
        ArgumentNullException.ThrowIfNull(jobCoordinator);
        ArgumentNullException.ThrowIfNull(copyAsync);
        ArgumentNullException.ThrowIfNull(probeAsync);
        ArgumentNullException.ThrowIfNull(indexAsync);
        ArgumentNullException.ThrowIfNull(normalize);
        ArgumentNullException.ThrowIfNull(decodeAsync);
        ArgumentNullException.ThrowIfNull(promoteOriginalAsync);
        ArgumentNullException.ThrowIfNull(generateProxyAsync);
        ArgumentNullException.ThrowIfNull(generateFrameMap);
        ArgumentNullException.ThrowIfNull(generatePreviewAsync);

        _jobCoordinator = jobCoordinator;
        _copyAsync = copyAsync;
        _probeAsync = probeAsync;
        _indexAsync = indexAsync;
        _normalize = normalize;
        _decodeAsync = decodeAsync;
        _promoteOriginalAsync = promoteOriginalAsync;
        _generateProxyAsync = generateProxyAsync;
        _generateFrameMap = generateFrameMap;
        _generatePreviewAsync = generatePreviewAsync;
    }

    public async Task<IngestWorkflowRunResult> RunAsync(
        IngestWorkflowRequest request,
        IngestWorkflowRunResult? previousRun = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQueuedJob(request);

        WorkflowResumeState state = previousRun is null
            ? WorkflowResumeState.Create(request)
            : ValidateAndCopyPreviousRun(request, previousRun);
        IngestWorkflowFailureReason? failureReason = null;
        bool reconciliationRequired = false;

        JobCoordinatorRunResult jobResult = await _jobCoordinator.RunAsync(
            request.JobId,
            async (context, token) =>
            {
                async Task<T> ExecuteAsync<T>(
                    IngestWorkflowFailureReason reason,
                    Func<Task<T>> operation)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        return await operation().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failureReason = reason;
                        reconciliationRequired |= RequiresReconciliation(exception);
                        if (IsRetryableTimeout(exception))
                        {
                            throw new TimeoutException(
                                "The ingest workflow stage exceeded its timeout.");
                        }

                        throw;
                    }
                }

                void ReportStage(IngestWorkflowStage stage)
                {
                    try
                    {
                        context.ReportProgress(
                            completed: (long)stage,
                            total: TotalStages,
                            phase: GetPhase(stage),
                            message: null);
                        context.RecordHeartbeat();
                    }
                    catch
                    {
                        failureReason =
                            IngestWorkflowFailureReason.ProgressPersistenceFailed;
                        reconciliationRequired = true;
                        throw;
                    }
                }

                void CompleteStage(
                    IngestWorkflowFailureReason reason,
                    Action update)
                {
                    WorkflowResumeState before = state.Copy();
                    try
                    {
                        update();
                        state.ValidateFor(request);
                    }
                    catch
                    {
                        state = before;
                        failureReason = reason;
                        throw;
                    }

                    ReportStage(state.Stage);
                }

                if (state.Stage != IngestWorkflowStage.NotStarted)
                {
                    ReportStage(state.Stage);
                }

                if (state.Stage < IngestWorkflowStage.CopiedToStaging)
                {
                    StagedMediaImport staged = await ExecuteAsync(
                        IngestWorkflowFailureReason.ImportFailed,
                        () => _copyAsync(
                            request.AssetCandidateId,
                            request.ProjectId,
                            request.JobId,
                            token)).ConfigureAwait(false);
                    Asset asset;
                    try
                    {
                        asset = new Asset(
                            request.AssetCandidateId,
                            staged.SourceFileName,
                            request.MediaType,
                            staged.ContentDigest,
                            staged.ByteSize,
                            request.Rights);
                    }
                    catch
                    {
                        failureReason = IngestWorkflowFailureReason.ImportFailed;
                        throw;
                    }

                    CompleteStage(
                        IngestWorkflowFailureReason.ImportFailed,
                        () => state.SetStaged(staged, asset));
                }

                if (state.Stage < IngestWorkflowStage.Probed)
                {
                    MediaProbeResult probe = await ExecuteAsync(
                        IngestWorkflowFailureReason.ProbeFailed,
                        () => _probeAsync(
                            state.StagedMedia!,
                            request.OperationTimeout,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.ProbeFailed,
                        () => state.SetProbe(probe));
                }

                if (state.Stage < IngestWorkflowStage.SourceIndexed)
                {
                    PtsIndex sourceIndex = await ExecuteAsync(
                        IngestWorkflowFailureReason.SourceIndexFailed,
                        () => _indexAsync(
                            state.StagedMedia!,
                            state.ProbeResult!,
                            request.OperationTimeout,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.SourceIndexFailed,
                        () => state.SetSourceIndex(sourceIndex));
                }

                if (state.Stage < IngestWorkflowStage.WarningsNormalized)
                {
                    MediaInfo mediaInfo;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        mediaInfo = _normalize(
                            state.ProbeResult!,
                            request.ValidationRequirements);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch
                    {
                        failureReason =
                            IngestWorkflowFailureReason.WarningNormalizationFailed;
                        throw;
                    }

                    CompleteStage(
                        IngestWorkflowFailureReason.WarningNormalizationFailed,
                        () => state.SetMediaInfo(mediaInfo));
                }

                if (state.Stage < IngestWorkflowStage.DecodeValidated)
                {
                    DecodeValidationResult decode = await ExecuteAsync(
                        IngestWorkflowFailureReason.DecodeValidationFailed,
                        () => _decodeAsync(
                            state.StagedMedia!,
                            state.ProbeResult!,
                            state.SourcePtsIndex!,
                            request.OperationTimeout,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.DecodeValidationFailed,
                        () => state.SetDecodeValidation(decode));
                }

                if (state.Stage < IngestWorkflowStage.OriginalCommitted)
                {
                    ImportedOriginal original = await ExecuteAsync(
                        IngestWorkflowFailureReason.OriginalPromotionFailed,
                        () => _promoteOriginalAsync(
                            request.OriginalArtifactId,
                            state.Asset!,
                            state.StagedMedia!,
                            state.ProbeResult!,
                            state.MediaInfo!,
                            state.DecodeValidation!,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.OriginalPromotionFailed,
                        () => state.SetOriginal(original));
                }

                if (state.Stage < IngestWorkflowStage.ProxyCommitted)
                {
                    GeneratedProxy proxy = await ExecuteAsync(
                        IngestWorkflowFailureReason.ProxyGenerationFailed,
                        () => _generateProxyAsync(
                            request.ProxyArtifactId,
                            state.Asset!,
                            state.Original!.Artifact,
                            state.SourcePtsIndex!,
                            request.JobId,
                            request.OperationTimeout,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.ProxyGenerationFailed,
                        () => state.SetProxy(proxy));
                }

                if (state.Stage < IngestWorkflowStage.FrameMapBuilt)
                {
                    ProxyFrameMap frameMap;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        frameMap = _generateFrameMap(
                            state.SourcePtsIndex!,
                            state.Proxy!);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException exception)
                        when (token.IsCancellationRequested &&
                            exception.CancellationToken == token)
                    {
                        throw;
                    }
                    catch
                    {
                        failureReason =
                            IngestWorkflowFailureReason.FrameMapGenerationFailed;
                        throw;
                    }

                    CompleteStage(
                        IngestWorkflowFailureReason.FrameMapGenerationFailed,
                        () => state.SetFrameMap(frameMap));
                }

                if (state.Stage < IngestWorkflowStage.PreviewAssetsCommitted)
                {
                    GeneratedPreviewAssets preview = await ExecuteAsync(
                        IngestWorkflowFailureReason.PreviewGenerationFailed,
                        () => _generatePreviewAsync(
                            request.WaveformIndexArtifactId,
                            request.SpriteImageArtifactId,
                            request.SpriteIndexArtifactId,
                            state.Proxy!,
                            request.OperationTimeout,
                            token)).ConfigureAwait(false);
                    CompleteStage(
                        IngestWorkflowFailureReason.PreviewGenerationFailed,
                        () => state.SetPreviewAssets(preview));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (jobResult.Snapshot.State == JobState.Succeeded &&
            state.Stage != IngestWorkflowStage.PreviewAssetsCommitted)
        {
            throw new InvalidOperationException(
                "The ingest workflow succeeded without completing every stage.");
        }

        if (jobResult.Snapshot.State == JobState.Failed && failureReason is null)
        {
            throw new InvalidOperationException(
                "The ingest workflow failed without a classified stage.");
        }

        return new IngestWorkflowRunResult(
            request.RequestDigest,
            jobResult,
            state.Copy(),
            failureReason,
            reconciliationRequired);
    }

    private void ValidateQueuedJob(IngestWorkflowRequest request)
    {
        StoredJob stored = _jobCoordinator.GetStoredJob(request.JobId);
        if (stored.Job.ProjectId != request.ProjectId ||
            !string.Equals(stored.Job.Kind, JobKind, StringComparison.Ordinal) ||
            stored.Job.State != JobState.Queued)
        {
            throw new InvalidOperationException(
                "The ingest workflow requires its exact queued job.");
        }
    }

    private WorkflowResumeState ValidateAndCopyPreviousRun(
        IngestWorkflowRequest request,
        IngestWorkflowRunResult previousRun)
    {
        if (previousRun.RequestDigest != request.RequestDigest ||
            previousRun.Job.Snapshot.JobId != request.JobId ||
            previousRun.IsComplete ||
            previousRun.ReconciliationRequired ||
            previousRun.Job.Snapshot.Retryability != JobRetryability.Retryable)
        {
            throw new ArgumentException(
                "The previous ingest result is not a safe retry checkpoint.",
                nameof(previousRun));
        }

        StoredJob current = _jobCoordinator.GetStoredJob(request.JobId);
        if (previousRun.Job.SequenceNumber != current.Job.CurrentAttemptNumber)
        {
            throw new ArgumentException(
                "The previous ingest result is not the current retry checkpoint.",
                nameof(previousRun));
        }

        WorkflowResumeState state = previousRun.CopyResumeState();
        state.ValidateFor(request);
        return state;
    }

    private static string GetPhase(IngestWorkflowStage stage) =>
        stage switch
        {
            IngestWorkflowStage.CopiedToStaging => "ingest.copy",
            IngestWorkflowStage.Probed => "ingest.probe",
            IngestWorkflowStage.SourceIndexed => "ingest.source_index",
            IngestWorkflowStage.WarningsNormalized => "ingest.warnings",
            IngestWorkflowStage.DecodeValidated => "ingest.decode",
            IngestWorkflowStage.OriginalCommitted => "ingest.original",
            IngestWorkflowStage.ProxyCommitted => "ingest.proxy",
            IngestWorkflowStage.FrameMapBuilt => "ingest.frame_map",
            IngestWorkflowStage.PreviewAssetsCommitted => "ingest.preview_assets",
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "An unfinished workflow stage cannot be reported."),
        };

    private static bool RequiresReconciliation(Exception exception) =>
        exception switch
        {
            MediaImportException import => import.RecoveryRequired,
            IngestServiceException ingest => ingest.ReconciliationRequired,
            ProxyGenerationException proxy => proxy.ReconciliationRequired,
            PreviewAssetGenerationException preview =>
                preview.ReconciliationRequired,
            ArtifactStoreException artifact => artifact.ReconciliationRequired,
            _ => false,
        };

    private static bool IsRetryableTimeout(Exception exception) =>
        exception is TimeoutException ||
        exception is MediaProbeException
        {
            Reason: MediaProbeFailureReason.ProcessTimedOut,
        } ||
        exception is PtsIndexException
        {
            Reason: PtsIndexFailureReason.ProcessTimedOut,
        } ||
        exception is DecodeValidationException
        {
            Reason: DecodeValidationFailureReason.ProcessTimedOut,
        } ||
        exception is ProxyGenerationException
        {
            Reason: ProxyGenerationFailureReason.ProcessTimedOut,
        } ||
        exception is PreviewAssetGenerationException
        {
            Reason: PreviewAssetGenerationFailureReason.ProcessTimedOut,
        };

    private static CopyDelegate GetCopyDelegate(MediaImporter mediaImporter)
    {
        ArgumentNullException.ThrowIfNull(mediaImporter);
        return mediaImporter.CopyToStagingAsync;
    }

    private static ProbeDelegate GetProbeDelegate(FfprobeClient ffprobeClient)
    {
        ArgumentNullException.ThrowIfNull(ffprobeClient);
        return ffprobeClient.ProbeAsync;
    }

    private static IndexDelegate GetIndexDelegate(PtsIndexer ptsIndexer)
    {
        ArgumentNullException.ThrowIfNull(ptsIndexer);
        return ptsIndexer.IndexAsync;
    }

    private static DecodeDelegate GetDecodeDelegate(DecodeValidator decodeValidator)
    {
        ArgumentNullException.ThrowIfNull(decodeValidator);
        return decodeValidator.ValidateAsync;
    }

    private static PromoteOriginalDelegate GetPromoteOriginalDelegate(
        IngestService ingestService)
    {
        ArgumentNullException.ThrowIfNull(ingestService);
        return ingestService.PromoteVerifiedOriginalAsync;
    }

    private static GenerateProxyDelegate GetGenerateProxyDelegate(
        ProxyGenerator proxyGenerator)
    {
        ArgumentNullException.ThrowIfNull(proxyGenerator);
        return proxyGenerator.GenerateAsync;
    }

    private static GenerateFrameMapDelegate GetGenerateFrameMapDelegate(
        ProxyFrameMapGenerator proxyFrameMapGenerator)
    {
        ArgumentNullException.ThrowIfNull(proxyFrameMapGenerator);
        return proxyFrameMapGenerator.Generate;
    }

    private static GeneratePreviewDelegate GetGeneratePreviewDelegate(
        PreviewAssetGenerator previewAssetGenerator)
    {
        ArgumentNullException.ThrowIfNull(previewAssetGenerator);
        return previewAssetGenerator.GenerateAsync;
    }

    internal delegate Task<StagedMediaImport> CopyDelegate(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken);

    internal delegate Task<MediaProbeResult> ProbeDelegate(
        StagedMediaImport stagedMedia,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    internal delegate Task<PtsIndex> IndexDelegate(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    internal delegate MediaInfo NormalizeDelegate(
        MediaProbeResult probeResult,
        IngestValidationRequirements requirements);

    internal delegate Task<DecodeValidationResult> DecodeDelegate(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        PtsIndex ptsIndex,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    internal delegate Task<ImportedOriginal> PromoteOriginalDelegate(
        EntityId<ArtifactIdTag> artifactId,
        Asset asset,
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        MediaInfo mediaInfo,
        DecodeValidationResult decodeValidation,
        CancellationToken cancellationToken);

    internal delegate Task<GeneratedProxy> GenerateProxyDelegate(
        EntityId<ArtifactIdTag> proxyArtifactId,
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        PtsIndex sourcePtsIndex,
        EntityId<JobIdTag> proxyJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    internal delegate ProxyFrameMap GenerateFrameMapDelegate(
        PtsIndex sourcePtsIndex,
        GeneratedProxy generatedProxy);

    internal delegate Task<GeneratedPreviewAssets> GeneratePreviewDelegate(
        EntityId<ArtifactIdTag> waveformIndexArtifactId,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        EntityId<ArtifactIdTag> spriteIndexArtifactId,
        GeneratedProxy generatedProxy,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class WorkflowResumeState
{
    private WorkflowResumeState(
        EntityId<JobIdTag> jobId,
        ContentDigest requestDigest)
    {
        JobId = jobId;
        RequestDigest = requestDigest;
    }

    internal EntityId<JobIdTag> JobId { get; }

    internal ContentDigest RequestDigest { get; }

    internal IngestWorkflowStage Stage { get; private set; }

    internal StagedMediaImport? StagedMedia { get; private set; }

    internal Asset? Asset { get; private set; }

    internal MediaProbeResult? ProbeResult { get; private set; }

    internal PtsIndex? SourcePtsIndex { get; private set; }

    internal MediaInfo? MediaInfo { get; private set; }

    internal DecodeValidationResult? DecodeValidation { get; private set; }

    internal ImportedOriginal? Original { get; private set; }

    internal GeneratedProxy? Proxy { get; private set; }

    internal ProxyFrameMap? FrameMap { get; private set; }

    internal GeneratedPreviewAssets? PreviewAssets { get; private set; }

    internal static WorkflowResumeState Create(IngestWorkflowRequest request) =>
        new(request.JobId, request.RequestDigest);

    internal WorkflowResumeState Copy() =>
        new(JobId, RequestDigest)
        {
            Stage = Stage,
            StagedMedia = StagedMedia,
            Asset = Asset,
            ProbeResult = ProbeResult,
            SourcePtsIndex = SourcePtsIndex,
            MediaInfo = MediaInfo,
            DecodeValidation = DecodeValidation,
            Original = Original,
            Proxy = Proxy,
            FrameMap = FrameMap,
            PreviewAssets = PreviewAssets,
        };

    internal void SetStaged(StagedMediaImport stagedMedia, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(asset);
        EnsureNext(IngestWorkflowStage.CopiedToStaging);
        StagedMedia = stagedMedia;
        Asset = asset;
        Stage = IngestWorkflowStage.CopiedToStaging;
    }

    internal void SetProbe(MediaProbeResult probeResult)
    {
        ArgumentNullException.ThrowIfNull(probeResult);
        EnsureNext(IngestWorkflowStage.Probed);
        ProbeResult = probeResult;
        Stage = IngestWorkflowStage.Probed;
    }

    internal void SetSourceIndex(PtsIndex ptsIndex)
    {
        ArgumentNullException.ThrowIfNull(ptsIndex);
        EnsureNext(IngestWorkflowStage.SourceIndexed);
        SourcePtsIndex = ptsIndex;
        Stage = IngestWorkflowStage.SourceIndexed;
    }

    internal void SetMediaInfo(MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);
        EnsureNext(IngestWorkflowStage.WarningsNormalized);
        MediaInfo = mediaInfo;
        Stage = IngestWorkflowStage.WarningsNormalized;
    }

    internal void SetDecodeValidation(DecodeValidationResult decodeValidation)
    {
        ArgumentNullException.ThrowIfNull(decodeValidation);
        EnsureNext(IngestWorkflowStage.DecodeValidated);
        DecodeValidation = decodeValidation;
        Stage = IngestWorkflowStage.DecodeValidated;
    }

    internal void SetOriginal(ImportedOriginal original)
    {
        ArgumentNullException.ThrowIfNull(original);
        EnsureNext(IngestWorkflowStage.OriginalCommitted);
        Original = original;
        Stage = IngestWorkflowStage.OriginalCommitted;
    }

    internal void SetProxy(GeneratedProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        EnsureNext(IngestWorkflowStage.ProxyCommitted);
        Proxy = proxy;
        Stage = IngestWorkflowStage.ProxyCommitted;
    }

    internal void SetFrameMap(ProxyFrameMap frameMap)
    {
        ArgumentNullException.ThrowIfNull(frameMap);
        EnsureNext(IngestWorkflowStage.FrameMapBuilt);
        FrameMap = frameMap;
        Stage = IngestWorkflowStage.FrameMapBuilt;
    }

    internal void SetPreviewAssets(GeneratedPreviewAssets previewAssets)
    {
        ArgumentNullException.ThrowIfNull(previewAssets);
        EnsureNext(IngestWorkflowStage.PreviewAssetsCommitted);
        PreviewAssets = previewAssets;
        Stage = IngestWorkflowStage.PreviewAssetsCommitted;
    }

    internal void ValidateFor(IngestWorkflowRequest request)
    {
        if (JobId != request.JobId || RequestDigest != request.RequestDigest)
        {
            throw InvalidState();
        }

        bool valid = Stage switch
        {
            IngestWorkflowStage.NotStarted =>
                StagedMedia is null && Asset is null && ProbeResult is null &&
                SourcePtsIndex is null && MediaInfo is null &&
                DecodeValidation is null && Original is null && Proxy is null &&
                FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.CopiedToStaging =>
                HasStaged(request) && ProbeResult is null &&
                SourcePtsIndex is null && MediaInfo is null &&
                DecodeValidation is null && Original is null && Proxy is null &&
                FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.Probed =>
                HasStaged(request) && HasProbe(request) &&
                SourcePtsIndex is null && MediaInfo is null &&
                DecodeValidation is null && Original is null && Proxy is null &&
                FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.SourceIndexed =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                MediaInfo is null && DecodeValidation is null && Original is null &&
                Proxy is null && FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.WarningsNormalized =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && DecodeValidation is null &&
                Original is null && Proxy is null && FrameMap is null &&
                PreviewAssets is null,
            IngestWorkflowStage.DecodeValidated =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && HasDecode(request) && Original is null &&
                Proxy is null && FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.OriginalCommitted =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && HasDecode(request) && HasOriginal(request) &&
                Proxy is null && FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.ProxyCommitted =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && HasDecode(request) && HasOriginal(request) &&
                HasProxy(request) && FrameMap is null && PreviewAssets is null,
            IngestWorkflowStage.FrameMapBuilt =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && HasDecode(request) && HasOriginal(request) &&
                HasProxy(request) && HasFrameMap(request) && PreviewAssets is null,
            IngestWorkflowStage.PreviewAssetsCommitted =>
                HasStaged(request) && HasProbe(request) && HasSourceIndex(request) &&
                HasMediaInfo(request) && HasDecode(request) && HasOriginal(request) &&
                HasProxy(request) && HasFrameMap(request) && HasPreview(request),
            _ => false,
        };
        if (!valid)
        {
            throw InvalidState();
        }
    }

    private bool HasStaged(IngestWorkflowRequest request) =>
        StagedMedia is not null &&
        Asset is not null &&
        StagedMedia.CandidateId == request.AssetCandidateId &&
        StagedMedia.ProjectId == request.ProjectId &&
        StagedMedia.JobId == request.JobId &&
        Asset.Id == request.AssetCandidateId &&
        Asset.ContentDigest == StagedMedia.ContentDigest &&
        Asset.ByteSize == StagedMedia.ByteSize &&
        string.Equals(
            Asset.FileName,
            StagedMedia.SourceFileName,
            StringComparison.Ordinal) &&
        string.Equals(Asset.MediaType, request.MediaType, StringComparison.Ordinal) &&
        HasSameRights(Asset.Rights, request.Rights);

    private bool HasProbe(IngestWorkflowRequest request) =>
        ProbeResult is not null &&
        ProbeResult.CandidateId == request.AssetCandidateId &&
        ProbeResult.ProjectId == request.ProjectId &&
        ProbeResult.JobId == request.JobId &&
        ProbeResult.ContentDigest == Asset!.ContentDigest &&
        ProbeResult.ByteSize == Asset.ByteSize &&
        string.Equals(
            ProbeResult.SourceFileName,
            Asset.FileName,
            StringComparison.Ordinal);

    private bool HasSourceIndex(IngestWorkflowRequest request) =>
        SourcePtsIndex is not null &&
        SourcePtsIndex.CandidateId == request.AssetCandidateId &&
        SourcePtsIndex.ProjectId == request.ProjectId &&
        SourcePtsIndex.JobId == request.JobId &&
        SourcePtsIndex.ContentDigest == Asset!.ContentDigest &&
        SourcePtsIndex.ByteSize == Asset.ByteSize &&
        string.Equals(
            SourcePtsIndex.SourceFileName,
            Asset.FileName,
            StringComparison.Ordinal);

    private bool HasMediaInfo(IngestWorkflowRequest request) =>
        MediaInfo is not null &&
        MediaInfo.CandidateId == request.AssetCandidateId &&
        MediaInfo.ProjectId == request.ProjectId &&
        MediaInfo.JobId == request.JobId &&
        MediaInfo.ContentDigest == Asset!.ContentDigest &&
        MediaInfo.ByteSize == Asset.ByteSize &&
        MediaInfo.RequiredMinimumVideoWidth ==
            request.ValidationRequirements.MinimumVideoWidth &&
        MediaInfo.RequiredMinimumVideoHeight ==
            request.ValidationRequirements.MinimumVideoHeight &&
        string.Equals(
            MediaInfo.SourceFileName,
            Asset.FileName,
            StringComparison.Ordinal);

    private bool HasDecode(IngestWorkflowRequest request) =>
        DecodeValidation is not null &&
        DecodeValidation.CandidateId == request.AssetCandidateId &&
        DecodeValidation.ProjectId == request.ProjectId &&
        DecodeValidation.JobId == request.JobId &&
        DecodeValidation.ContentDigest == Asset!.ContentDigest &&
        DecodeValidation.ByteSize == Asset.ByteSize &&
        DecodeValidation.DecodedSuccessfully;

    private bool HasOriginal(IngestWorkflowRequest request) =>
        Original is not null &&
        ReferenceEquals(Original.Asset, Asset) &&
        ReferenceEquals(Original.MediaInfo, MediaInfo) &&
        ReferenceEquals(Original.DecodeValidation, DecodeValidation) &&
        Original.Artifact.Id == request.OriginalArtifactId &&
        Original.Artifact.ProjectId == request.ProjectId &&
        Original.Artifact.Kind == ArtifactKind.Original &&
        Original.Artifact.ContentDigest == Asset!.ContentDigest &&
        Original.Artifact.ByteSize == Asset.ByteSize &&
        Original.IntegrityStatus == OriginalIntegrityStatus.Verified;

    private bool HasProxy(IngestWorkflowRequest request) =>
        Proxy is not null &&
        Proxy.SourceAssetId == request.AssetCandidateId &&
        Proxy.SourceContentDigest == Asset!.ContentDigest &&
        Proxy.SourceOriginalArtifactId == request.OriginalArtifactId &&
        Proxy.ProxyArtifact.Id == request.ProxyArtifactId &&
        Proxy.ProxyArtifact.ProjectId == request.ProjectId &&
        Proxy.ProxyArtifact.Kind == ArtifactKind.Proxy;

    private bool HasFrameMap(IngestWorkflowRequest request) =>
        FrameMap is not null &&
        FrameMap.SourceAssetId == request.AssetCandidateId &&
        FrameMap.ProjectId == request.ProjectId &&
        FrameMap.SourceContentDigest == Asset!.ContentDigest &&
        FrameMap.ProxyContentDigest == Proxy!.ProxyArtifact.ContentDigest &&
        FrameMap.SourcePtsIndexDigest == ComputeIndexDigest(SourcePtsIndex!) &&
        FrameMap.ProxyPtsIndexDigest ==
            ComputeIndexDigest(Proxy.ProxyPtsIndex) &&
        FrameMap.SourceFrameCount == SourcePtsIndex!.Entries.Count &&
        FrameMap.SourcePresentationStart ==
            SourcePtsIndex.Entries[0].PresentationTime &&
        FrameMap.SourcePresentationEnd ==
            SourcePtsIndex.PresentationEnd &&
        FrameMap.Entries.Count == Proxy.ProxyPtsIndex.Entries.Count &&
        FrameMap.Entries.Select((entry, index) =>
            entry.ProxyFrameSequence == index &&
            entry.ProxyPresentationTime ==
                Proxy.ProxyPtsIndex.Entries[index].PresentationTime &&
            entry.SourceFrameSequence >= 0 &&
            entry.SourceFrameSequence < SourcePtsIndex.Entries.Count &&
            entry.SourcePresentationTime ==
                SourcePtsIndex.Entries[entry.SourceFrameSequence]
                    .PresentationTime).All(static valid => valid);

    private bool HasPreview(IngestWorkflowRequest request) =>
        PreviewAssets is not null &&
        PreviewAssets.WaveformIndexArtifact.Id ==
            request.WaveformIndexArtifactId &&
        PreviewAssets.WaveformIndexArtifact.ProjectId == request.ProjectId &&
        PreviewAssets.WaveformIndexArtifact.Kind == ArtifactKind.Preview &&
        PreviewAssets.SpriteImageArtifact.Id == request.SpriteImageArtifactId &&
        PreviewAssets.SpriteImageArtifact.ProjectId == request.ProjectId &&
        PreviewAssets.SpriteImageArtifact.Kind == ArtifactKind.Preview &&
        PreviewAssets.SpriteIndexArtifact.Id == request.SpriteIndexArtifactId &&
        PreviewAssets.SpriteIndexArtifact.ProjectId == request.ProjectId &&
        PreviewAssets.SpriteIndexArtifact.Kind == ArtifactKind.Preview &&
        PreviewAssets.WaveformIndex.SourceAssetId == request.AssetCandidateId &&
        PreviewAssets.WaveformIndex.ProjectId == request.ProjectId &&
        PreviewAssets.WaveformIndex.ProxyArtifactId == request.ProxyArtifactId &&
        PreviewAssets.WaveformIndex.ProxyContentDigest ==
            Proxy!.ProxyArtifact.ContentDigest &&
        PreviewAssets.WaveformIndex.ProxyPtsIndexDigest ==
            ComputeIndexDigest(Proxy.ProxyPtsIndex) &&
        PreviewAssets.WaveformIndex.Frames.Count ==
            Proxy.ProxyPtsIndex.Entries.Count &&
        PreviewAssets.WaveformIndex.Frames.Select((frame, index) =>
            frame.ProxyFrameSequence == index &&
            frame.ProxyPresentationTime ==
                Proxy.ProxyPtsIndex.Entries[index].PresentationTime &&
            frame.ProxyDuration ==
                Proxy.ProxyPtsIndex.Entries[index].Duration).All(static valid => valid) &&
        PreviewAssets.SpriteIndex.SourceAssetId == request.AssetCandidateId &&
        PreviewAssets.SpriteIndex.ProjectId == request.ProjectId &&
        PreviewAssets.SpriteIndex.ProxyArtifactId == request.ProxyArtifactId &&
        PreviewAssets.SpriteIndex.ProxyContentDigest ==
            Proxy.ProxyArtifact.ContentDigest &&
        PreviewAssets.SpriteIndex.ProxyPtsIndexDigest ==
            ComputeIndexDigest(Proxy.ProxyPtsIndex) &&
        PreviewAssets.SpriteIndex.SpriteImageArtifactId ==
            PreviewAssets.SpriteImageArtifact.Id &&
        PreviewAssets.SpriteIndex.SpriteImageDigest ==
            PreviewAssets.SpriteImageArtifact.ContentDigest &&
        PreviewAssets.SpriteIndex.Cells.All(cell =>
            cell.ProxyFrameSequence >= 0 &&
            cell.ProxyFrameSequence < Proxy.ProxyPtsIndex.Entries.Count &&
            Proxy.ProxyPtsIndex.Entries[cell.ProxyFrameSequence].KeyFrame &&
            cell.ProxyPresentationTime ==
                Proxy.ProxyPtsIndex.Entries[cell.ProxyFrameSequence]
                    .PresentationTime &&
            cell.ProxyDuration ==
                Proxy.ProxyPtsIndex.Entries[cell.ProxyFrameSequence].Duration) &&
        MatchesCanonicalIndex(
            PreviewAssets.WaveformIndexArtifact,
            PreviewAssets.WaveformIndex.CanonicalJson) &&
        MatchesCanonicalIndex(
            PreviewAssets.SpriteIndexArtifact,
            PreviewAssets.SpriteIndex.CanonicalJson);

    private void EnsureNext(IngestWorkflowStage next)
    {
        if ((int)next != (int)Stage + 1)
        {
            throw InvalidState();
        }
    }

    private static bool HasSameRights(RightsRecord left, RightsRecord right) =>
        left.Status == right.Status &&
        string.Equals(
            left.EvidenceReference,
            right.EvidenceReference,
            StringComparison.Ordinal) &&
        string.Equals(left.AllowedUse, right.AllowedUse, StringComparison.Ordinal) &&
        left.ExternalPublicationAllowed == right.ExternalPublicationAllowed;

    private static bool MatchesCanonicalIndex(
        StoredArtifact artifact,
        string canonicalJson)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(canonicalJson);
        return artifact.ByteSize == utf8.LongLength &&
            artifact.ContentDigest == ContentDigest.Compute(utf8);
    }

    private static ContentDigest ComputeIndexDigest(PtsIndex index) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(index.CanonicalJson));

    private static InvalidOperationException InvalidState() =>
        new("The ingest workflow resume state is inconsistent.");
}