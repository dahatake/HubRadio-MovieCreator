using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

public enum PreviewRenderFailureReason
{
    InvalidGraph,
    InvalidSelection,
    UnsupportedSelectionBoundary,
    InputBindingMismatch,
    ProxyMappingMismatch,
    CacheStateInvalid,
    DuplicateJob,
    JobCancelled,
    JobFailed,
    StagingConflict,
    StagingUnavailable,
    CompilationFailed,
    ProcessTimedOut,
    RenderFailed,
    OutputInvalid,
    PromotionFailed,
    RegisteredPreviewInvalid,
    CleanupFailed,
}

public sealed class PreviewRenderException : Exception
{
    internal PreviewRenderException(
        PreviewRenderFailureReason reason,
        bool reconciliationRequired)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public PreviewRenderFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(PreviewRenderFailureReason reason) =>
        reason switch
        {
            PreviewRenderFailureReason.InvalidGraph =>
                "The render graph is not valid for interval preview.",
            PreviewRenderFailureReason.InvalidSelection =>
                "The requested preview interval is not valid.",
            PreviewRenderFailureReason.UnsupportedSelectionBoundary =>
                "The requested interval crosses a render boundary that cannot be preserved exactly.",
            PreviewRenderFailureReason.InputBindingMismatch =>
                "The preview input bindings do not match the render graph.",
            PreviewRenderFailureReason.ProxyMappingMismatch =>
                "The proxy mapping evidence does not match the selected render inputs.",
            PreviewRenderFailureReason.CacheStateInvalid =>
                "The preview cache state could not be validated.",
            PreviewRenderFailureReason.DuplicateJob =>
                "The preview job cannot be started in its current state.",
            PreviewRenderFailureReason.JobCancelled =>
                "The preview job was cancelled.",
            PreviewRenderFailureReason.JobFailed =>
                "The preview job did not complete successfully.",
            PreviewRenderFailureReason.StagingConflict =>
                "The preview staging destination is already owned by another operation.",
            PreviewRenderFailureReason.StagingUnavailable =>
                "The preview staging destination is unavailable.",
            PreviewRenderFailureReason.CompilationFailed =>
                "The selected interval could not be compiled safely.",
            PreviewRenderFailureReason.ProcessTimedOut =>
                "The preview render process exceeded its timeout.",
            PreviewRenderFailureReason.RenderFailed =>
                "The preview render process failed.",
            PreviewRenderFailureReason.OutputInvalid =>
                "The preview render output is invalid.",
            PreviewRenderFailureReason.PromotionFailed =>
                "The preview output could not be promoted.",
            PreviewRenderFailureReason.RegisteredPreviewInvalid =>
                "The registered preview output could not be verified.",
            PreviewRenderFailureReason.CleanupFailed =>
                "Preview staging cleanup requires reconciliation.",
            _ => "Preview rendering failed.",
        };
}

public enum PreviewFrameBoundaryKind
{
    InclusiveStart,
    ExclusiveEnd,
}

public sealed class PreviewProxyBinding
{
    public PreviewProxyBinding(
        EntityId<RenderInputIdTag> inputId,
        ProxyFrameMap frameMap)
    {
        ArgumentNullException.ThrowIfNull(inputId);
        ArgumentNullException.ThrowIfNull(frameMap);
        InputId = inputId;
        FrameMap = frameMap;
    }

    public EntityId<RenderInputIdTag> InputId { get; }

    public ProxyFrameMap FrameMap { get; }
}

public sealed class PreviewRenderRequest
{
    public PreviewRenderRequest(
        EntityId<JobIdTag> jobId,
        EntityId<ArtifactIdTag> previewArtifactId,
        RenderGraph graph,
        RenderGraphValidationContext validationContext,
        TimeRange selectedOutputRange,
        FfmpegToolBinding tool,
        IReadOnlyList<FfmpegInputBinding> inputs,
        IReadOnlyList<PreviewProxyBinding> proxyBindings,
        IReadOnlyList<ArtifactCacheCandidate> cacheCandidates,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(previewArtifactId);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(proxyBindings);
        ArgumentNullException.ThrowIfNull(cacheCandidates);
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The preview timeout must be positive and supported by the runtime timer.");
        }

        JobId = jobId;
        PreviewArtifactId = previewArtifactId;
        Graph = graph;
        ValidationContext = validationContext;
        SelectedOutputRange = selectedOutputRange;
        Tool = tool;
        Inputs = Array.AsReadOnly(CopyInputs(inputs));
        ProxyBindings = Array.AsReadOnly(CopyProxyBindings(proxyBindings));
        CacheCandidates = Array.AsReadOnly(CopyCandidates(cacheCandidates));
        Timeout = timeout;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<ArtifactIdTag> PreviewArtifactId { get; }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    public TimeRange SelectedOutputRange { get; }

    public FfmpegToolBinding Tool { get; }

    public IReadOnlyList<FfmpegInputBinding> Inputs { get; }

    public IReadOnlyList<PreviewProxyBinding> ProxyBindings { get; }

    public IReadOnlyList<ArtifactCacheCandidate> CacheCandidates { get; }

    public TimeSpan Timeout { get; }

    private static FfmpegInputBinding[] CopyInputs(
        IReadOnlyList<FfmpegInputBinding> values)
    {
        var copied = new FfmpegInputBinding[values.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            FfmpegInputBinding value = values[index]
                ?? throw new ArgumentException(
                    "Preview input bindings must not contain null values.",
                    nameof(values));
            if (!ids.Add(value.InputId))
            {
                throw new ArgumentException(
                    "Preview input bindings must use unique input identities.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static PreviewProxyBinding[] CopyProxyBindings(
        IReadOnlyList<PreviewProxyBinding> values)
    {
        var copied = new PreviewProxyBinding[values.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            PreviewProxyBinding value = values[index]
                ?? throw new ArgumentException(
                    "Preview proxy bindings must not contain null values.",
                    nameof(values));
            if (!ids.Add(value.InputId))
            {
                throw new ArgumentException(
                    "Preview proxy bindings must use unique input identities.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static ArtifactCacheCandidate[] CopyCandidates(
        IReadOnlyList<ArtifactCacheCandidate> values)
    {
        var copied = new ArtifactCacheCandidate[values.Count];
        var ids = new HashSet<EntityId<ArtifactIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            ArtifactCacheCandidate value = values[index]
                ?? throw new ArgumentException(
                    "Preview cache candidates must not contain null values.",
                    nameof(values));
            if (!ids.Add(value.ArtifactId))
            {
                throw new ArgumentException(
                    "Preview cache candidates must use unique artifact identities.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }
}

public sealed class PreviewFrameBoundaryEvidence
{
    internal PreviewFrameBoundaryEvidence(
        EntityId<RenderInputIdTag> inputId,
        PreviewFrameBoundaryKind kind,
        RationalTime sourceBoundaryTime,
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        int sourceFrameSequence,
        RationalTime sourcePresentationTime)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        InputId = inputId;
        Kind = kind;
        SourceBoundaryTime = sourceBoundaryTime;
        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
    }

    public EntityId<RenderInputIdTag> InputId { get; }

    public PreviewFrameBoundaryKind Kind { get; }

    public RationalTime SourceBoundaryTime { get; }

    public int ProxyFrameSequence { get; }

    public RationalTime ProxyPresentationTime { get; }

    public int SourceFrameSequence { get; }

    public RationalTime SourcePresentationTime { get; }
}

public sealed class PreviewRenderMapping
{
    internal PreviewRenderMapping(
        EntityId<CompositionElementIdTag> compositionElementId,
        TimeRange selectedTimelineRange,
        TimeRange previewOutputRange,
        OriginRef origin,
        TimeRange? sourceRange,
        PreviewFrameBoundaryEvidence? proxyStartBoundary,
        PreviewFrameBoundaryEvidence? proxyEndBoundary)
    {
        CompositionElementId = compositionElementId;
        SelectedTimelineRange = selectedTimelineRange;
        PreviewOutputRange = previewOutputRange;
        Origin = origin;
        SourceRange = sourceRange;
        ProxyStartBoundary = proxyStartBoundary;
        ProxyEndBoundary = proxyEndBoundary;
    }

    public EntityId<CompositionElementIdTag> CompositionElementId { get; }

    public TimeRange SelectedTimelineRange { get; }

    public TimeRange PreviewOutputRange { get; }

    public OriginRef Origin { get; }

    public TimeRange? SourceRange { get; }

    public PreviewFrameBoundaryEvidence? ProxyStartBoundary { get; }

    public PreviewFrameBoundaryEvidence? ProxyEndBoundary { get; }
}

public sealed class PreviewRenderResult
{
    internal PreviewRenderResult(
        EntityId<ArtifactIdTag> artifactId,
        EntityId<ProjectIdTag> projectId,
        ContentDigest outputDigest,
        long byteSize,
        DateTimeOffset createdAt,
        ArtifactCacheKey cacheKey,
        ContentDigest configurationDigest,
        ContentDigest renderGraphDigest,
        TimeRange selectedOutputRange,
        TimeRange previewOutputRange,
        ContentDigest compositionVersionDigest,
        ContentDigest timelineMapDigest,
        EntityId<RenderProfileIdTag> profileId,
        ContentDigest profileDigest,
        EntityId<LayoutSpecIdTag> layoutSpecId,
        ContentDigest layoutSpecDigest,
        EntityId<TemplateIdTag> templateId,
        ContentDigest templateVersionDigest,
        IReadOnlyList<PreviewRenderMapping> mappings,
        bool cacheHit,
        TimeSpan? processDuration)
    {
        ArtifactId = artifactId;
        ProjectId = projectId;
        OutputDigest = outputDigest;
        ByteSize = byteSize;
        CreatedAt = createdAt;
        CacheKey = cacheKey;
        ConfigurationDigest = configurationDigest;
        RenderGraphDigest = renderGraphDigest;
        SelectedOutputRange = selectedOutputRange;
        PreviewOutputRange = previewOutputRange;
        CompositionVersionDigest = compositionVersionDigest;
        TimelineMapDigest = timelineMapDigest;
        ProfileId = profileId;
        ProfileDigest = profileDigest;
        LayoutSpecId = layoutSpecId;
        LayoutSpecDigest = layoutSpecDigest;
        TemplateId = templateId;
        TemplateVersionDigest = templateVersionDigest;
        Mappings = Array.AsReadOnly(mappings.ToArray());
        CacheHit = cacheHit;
        ProcessDuration = processDuration;
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest OutputDigest { get; }

    public long ByteSize { get; }

    public DateTimeOffset CreatedAt { get; }

    public ArtifactCacheKey CacheKey { get; }

    public ContentDigest ConfigurationDigest { get; }

    public ContentDigest RenderGraphDigest { get; }

    public TimeRange SelectedOutputRange { get; }

    public TimeRange PreviewOutputRange { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public ContentDigest TimelineMapDigest { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest LayoutSpecDigest { get; }

    public EntityId<TemplateIdTag> TemplateId { get; }

    public ContentDigest TemplateVersionDigest { get; }

    public IReadOnlyList<PreviewRenderMapping> Mappings { get; }

    public bool CacheHit { get; }

    public TimeSpan? ProcessDuration { get; }
}

public sealed class PreviewRenderService
{
    public const string Operation = "render.preview.interval.v1";

    private const string DerivationDomain =
        "HubRadio.MovieCreator.PreviewRenderService/interval-derivation/v1";
    private const int FileBufferSize = 64 * 1024;
    private const int MaximumRememberedCacheEntries = 256;
    private const int Sha256ByteLength = 32;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly JobCoordinator _jobCoordinator;
    private readonly Func<
        FfmpegCompileRequest,
        CancellationToken,
        Task<FfmpegCompilation>> _compileAsync;
    private readonly Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> _runProcessAsync;
    private readonly Func<
        ArtifactPromotionRequest,
        CancellationToken,
        Task<StoredArtifact>> _promoteAsync;
    private readonly Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<StoredArtifact>> _validateRegisteredAsync;
    private readonly Func<EntityId<ArtifactIdTag>, StoredArtifact?> _getArtifact;
    private readonly string _workspacePath;
    private readonly TimeProvider _timeProvider;
    private readonly object _knownCacheSync = new();
    private readonly Dictionary<ArtifactCacheKey, ArtifactCacheCandidate>
        _knownCache = [];
    private readonly ConcurrentDictionary<string, CacheGate> _cacheGates =
        new(StringComparer.Ordinal);

    internal int ActiveCacheGateCount => _cacheGates.Count;

    public PreviewRenderService(
        ArtifactStore artifactStore,
        JobCoordinator jobCoordinator,
        ProcessRunner processRunner,
        string workspacePath)
        : this(
            jobCoordinator,
            FfmpegCompiler.CompileAsync,
            GetProcessDelegate(processRunner),
            GetPromotionDelegate(artifactStore),
            GetValidationDelegate(artifactStore),
            GetArtifactDelegate(artifactStore),
            workspacePath,
            TimeProvider.System)
    {
    }

    internal PreviewRenderService(
        JobCoordinator jobCoordinator,
        Func<FfmpegCompileRequest, CancellationToken, Task<FfmpegCompilation>> compileAsync,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>> promoteAsync,
        Func<EntityId<ArtifactIdTag>, CancellationToken, Task<StoredArtifact>> validateRegisteredAsync,
        Func<EntityId<ArtifactIdTag>, StoredArtifact?> getArtifact,
        string workspacePath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(jobCoordinator);
        ArgumentNullException.ThrowIfNull(compileAsync);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(promoteAsync);
        ArgumentNullException.ThrowIfNull(validateRegisteredAsync);
        ArgumentNullException.ThrowIfNull(getArtifact);

        _jobCoordinator = jobCoordinator;
        _compileAsync = compileAsync;
        _runProcessAsync = runProcessAsync;
        _promoteAsync = promoteAsync;
        _validateRegisteredAsync = validateRegisteredAsync;
        _getArtifact = getArtifact;
        _workspacePath = ValidateWorkspacePath(workspacePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PreviewRenderResult> RenderAsync(
        PreviewRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedPreview prepared = Prepare(request, cancellationToken);
        PreviewRenderResult? completed = null;
        PreviewRenderException? capturedFailure = null;
        bool operationStarted = false;
        JobCoordinatorRunResult jobResult;
        try
        {
            jobResult = await _jobCoordinator.RunAsync(
                request.JobId,
                async (context, jobCancellationToken) =>
                {
                    operationStarted = true;
                    try
                    {
                        context.ReportProgress(0, 5, "preview.cache");
                        completed = await RenderPreparedAsync(
                            prepared,
                            context,
                            jobCancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        capturedFailure = Failure(
                            PreviewRenderFailureReason.ProcessTimedOut);
                        throw;
                    }
                    catch (PreviewRenderException exception)
                    {
                        capturedFailure = exception;
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        capturedFailure = Failure(
                            PreviewRenderFailureReason.RenderFailed);
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (InvalidOperationException) when (!operationStarted)
        {
            throw Failure(PreviewRenderFailureReason.DuplicateJob);
        }
        catch (Exception)
        {
            if (capturedFailure is not null)
            {
                throw capturedFailure;
            }

            throw Failure(
                PreviewRenderFailureReason.JobFailed,
                reconciliationRequired: completed is not null);
        }

        if (completed is not null)
        {
            return completed;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (capturedFailure is not null)
        {
            throw capturedFailure;
        }

        if (jobResult.Snapshot.State == JobState.Cancelled)
        {
            throw Failure(PreviewRenderFailureReason.JobCancelled);
        }

        throw Failure(PreviewRenderFailureReason.JobFailed);
    }

    private async Task<PreviewRenderResult> RenderPreparedAsync(
        PreparedPreview prepared,
        LocalJobExecutionContext progress,
        CancellationToken cancellationToken)
    {
        string gateKey = prepared.CacheKey.Digest.Hex;
        CacheGate gate = AcquireCacheGate(gateKey);
        bool enteredGate = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            PreviewRenderResult? cached = await TryGetCacheHitAsync(
                prepared,
                cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                progress.ReportProgress(5, 5, "preview.complete");
                return cached;
            }

            EnsureArtifactIdAvailable(prepared.Request.PreviewArtifactId);
            PreviewStaging staging = PrepareStaging(
                prepared.Context.Project.Id,
                prepared.Request.JobId,
                prepared.Request.PreviewArtifactId,
                cancellationToken);
            bool promoted = false;

            try
            {
                progress.ReportProgress(1, 5, "preview.compile");
                FfmpegCompilation compilation = await CompileAsync(
                    prepared,
                    staging,
                    cancellationToken).ConfigureAwait(false);

                progress.ReportProgress(2, 5, "preview.render");
                ProcessRunResult processResult = await RunProcessAsync(
                    compilation.ProcessRequest,
                    cancellationToken).ConfigureAwait(false);
                ValidateProcessResult(processResult);

                progress.ReportProgress(3, 5, "preview.verify");
                OutputSnapshot output = await VerifyOutputAsync(
                    staging.OutputPath,
                    cancellationToken).ConfigureAwait(false);
                DateTimeOffset createdAt = _timeProvider
                    .GetUtcNow()
                    .ToUniversalTime();

                progress.ReportProgress(4, 5, "preview.promote");
                StoredArtifact artifact = await PromoteAsync(
                    prepared,
                    staging,
                    output,
                    createdAt,
                    cancellationToken).ConfigureAwait(false);
                promoted = true;

                if (!TryCleanupOwnedStaging(staging))
                {
                    throw Failure(
                        PreviewRenderFailureReason.CleanupFailed,
                        reconciliationRequired: true);
                }

                ValidatePromotedMetadata(
                    artifact,
                    prepared,
                    output,
                    createdAt);
                StoredArtifact verified = await ValidateRegisteredAsync(
                    artifact.Id,
                    cancellationToken).ConfigureAwait(false);
                ValidatePromotedMetadata(
                    verified,
                    prepared,
                    output,
                    createdAt);
                cancellationToken.ThrowIfCancellationRequested();

                RememberCache(prepared.CacheKey, verified);
                progress.ReportProgress(5, 5, "preview.complete");
                return CreateResult(
                    prepared,
                    verified,
                    cacheHit: false,
                    processResult.Duration);
            }
            catch (Exception exception)
            {
                if (!promoted && !TryCleanupOwnedStaging(staging))
                {
                    throw Failure(
                        PreviewRenderFailureReason.CleanupFailed,
                        reconciliationRequired: true);
                }

                if (promoted)
                {
                    if (exception is PreviewRenderException previewException &&
                        previewException.ReconciliationRequired)
                    {
                        throw;
                    }

                    throw Failure(
                        PreviewRenderFailureReason.RegisteredPreviewInvalid,
                        reconciliationRequired: true);
                }

                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (exception is PreviewRenderException or TimeoutException)
                {
                    throw;
                }

                throw Failure(PreviewRenderFailureReason.RenderFailed);
            }
        }
        finally
        {
            if (enteredGate)
            {
                gate.Semaphore.Release();
            }

            ReleaseCacheGate(gateKey, gate);
        }
    }

    private PreparedPreview Prepare(
        PreviewRenderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            RenderGraphValidationResult originalValidation =
                RenderGraphValidator.Validate(
                    request.Graph,
                    request.ValidationContext,
                    cancellationToken);
            if (!originalValidation.IsValid)
            {
                throw Failure(PreviewRenderFailureReason.InvalidGraph);
            }

            Dictionary<EntityId<RenderInputIdTag>, ProxyFrameMap> proxyMaps =
                ValidateProxyBindings(request);
            ValidateSelection(
                request.SelectedOutputRange,
                request.ValidationContext.TimelineMap);

            ContentDigest originalGraphDigest = ComputeGraphDigest(request.Graph);
            ContentDigest derivationSeed = ComputeDerivationSeed(
                request,
                originalGraphDigest);
            DerivedPreview derived = DerivePreview(
                request,
                derivationSeed,
                cancellationToken);
            RenderGraphValidationResult derivedValidation =
                RenderGraphValidator.Validate(
                    derived.Graph,
                    derived.Context,
                    cancellationToken);
            if (!derivedValidation.IsValid)
            {
                throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary);
            }

            Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> inputs =
                ValidateInputBindings(request, derived.Graph.Inputs);

            ContentDigest graphDigest = ComputeGraphDigest(derived.Graph);
            ContentDigest configurationDigest = ComputeConfigurationDigest(
                derivationSeed,
                derived,
                graphDigest,
                request.SelectedOutputRange);
            var inputDigests = new List<ContentDigest>();
            foreach (RenderGraphInput input in derived.Graph.Inputs)
            {
                inputDigests.Add(input.ContentDigest);
                if (proxyMaps.TryGetValue(input.Id, out ProxyFrameMap? frameMap))
                {
                    inputDigests.Add(ComputeProxyMapDigest(frameMap));
                }
            }

            ArtifactCacheKey cacheKey = ArtifactCacheKey.Create(
                Operation,
                configurationDigest,
                inputDigests);
            PreviewRenderMapping[] mappings = BuildMappings(
                request,
                derived.ElementSlices,
                proxyMaps);
            FfmpegInputBinding[] selectedInputs = derived.Graph.Inputs
                .Select(input => inputs[input.Id])
                .ToArray();
            return new PreparedPreview(
                request,
                derived.Graph,
                derived.Context,
                selectedInputs,
                cacheKey,
                configurationDigest,
                graphDigest,
                mappings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewRenderException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException or JsonException or CryptographicException)
        {
            throw Failure(PreviewRenderFailureReason.InvalidGraph);
        }
    }

    private static Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding>
        ValidateInputBindings(
            PreviewRenderRequest request,
            IReadOnlyList<RenderGraphInput> requiredInputs)
    {
        Dictionary<EntityId<RenderInputIdTag>, RenderGraphInput> graphInputs =
            request.Graph.Inputs.ToDictionary(static input => input.Id);
        Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> supplied =
            request.Inputs.ToDictionary(static binding => binding.InputId);
        foreach (FfmpegInputBinding binding in request.Inputs)
        {
            if (!graphInputs.TryGetValue(
                    binding.InputId,
                    out RenderGraphInput? input) ||
                binding.VerificationRequest.ExpectedDigest != input.ContentDigest ||
                binding.VerificationRequest.ExpectedByteSize != input.ByteSize)
            {
                throw Failure(PreviewRenderFailureReason.InputBindingMismatch);
            }
        }

        if (requiredInputs.Any(input => !supplied.ContainsKey(input.Id)))
        {
            throw Failure(PreviewRenderFailureReason.InputBindingMismatch);
        }

        return supplied;
    }

    private static Dictionary<EntityId<RenderInputIdTag>, ProxyFrameMap>
        ValidateProxyBindings(PreviewRenderRequest request)
    {
        Dictionary<EntityId<RenderInputIdTag>, RenderGraphInput> graphInputs =
            request.Graph.Inputs.ToDictionary(static input => input.Id);
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans =
            request.ValidationContext.SourceSpans.ToDictionary(static span => span.Id);
        Dictionary<EntityId<AssetIdTag>, Asset> assets =
            request.ValidationContext.Project.Assets.ToDictionary(static asset => asset.Id);
        var result = new Dictionary<EntityId<RenderInputIdTag>, ProxyFrameMap>();

        foreach (PreviewProxyBinding binding in request.ProxyBindings)
        {
            if (!graphInputs.TryGetValue(binding.InputId, out RenderGraphInput? input) ||
                input.Kind != RenderInputKind.Video ||
                binding.FrameMap.ProjectId != request.ValidationContext.Project.Id ||
                binding.FrameMap.ProxyContentDigest != input.ContentDigest ||
                input.Origin.Kind != OriginKind.SourceSpan ||
                input.Origin.SourceSpanId is not EntityId<SourceSpanIdTag> sourceSpanId ||
                !sourceSpans.TryGetValue(sourceSpanId, out SourceSpan? sourceSpan) ||
                sourceSpan.AssetId != binding.FrameMap.SourceAssetId ||
                sourceSpan.SourceDigest != binding.FrameMap.SourceContentDigest ||
                !assets.TryGetValue(sourceSpan.AssetId, out Asset? sourceAsset) ||
                sourceAsset.ContentDigest != binding.FrameMap.SourceContentDigest)
            {
                throw Failure(PreviewRenderFailureReason.ProxyMappingMismatch);
            }

            result.Add(binding.InputId, binding.FrameMap);
        }

        return result;
    }

    private static void ValidateSelection(
        TimeRange selection,
        TimelineMap timelineMap)
    {
        RationalTime timelineEnd = timelineMap.Entries.Count == 0
            ? default
            : timelineMap.Entries[^1].OutputRange.End;
        if (selection.Start < default(RationalTime) ||
            selection.Duration <= default(RationalTime) ||
            selection.End > timelineEnd)
        {
            throw Failure(PreviewRenderFailureReason.InvalidSelection);
        }
    }

    private static DerivedPreview DerivePreview(
        PreviewRenderRequest request,
        ContentDigest derivationSeed,
        CancellationToken cancellationToken)
    {
        TimeRange selection = request.SelectedOutputRange;
        var slices = new List<ElementSlice>();
        RationalTime cursor = selection.Start;
        foreach (CompositionElement element in request.ValidationContext
            .CompositionVersion.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!element.OutputRange.Overlaps(selection))
            {
                continue;
            }

            TimeRange intersection = Intersect(element.OutputRange, selection);
            if (intersection.Start != cursor)
            {
                throw Failure(PreviewRenderFailureReason.InvalidSelection);
            }

            TimeRange previewRange = Rebase(intersection, selection.Start);
            TimeRange? sourceRange = ClipSourceRange(
                element.OutputRange,
                element.SourceRange,
                intersection);
            EntityId<CompositionElementIdTag> derivedElementId = DerivedId<CompositionElementIdTag>(
                derivationSeed,
                "composition-element",
                element.Id.ToString());
            var derivedElement = new CompositionElement(
                derivedElementId,
                previewRange,
                element.Origin,
                element.AdoptedSuggestion,
                sourceRange);
            slices.Add(new ElementSlice(
                element,
                intersection,
                derivedElement));
            cursor = intersection.End;
        }

        if (slices.Count == 0 || cursor != selection.End)
        {
            throw Failure(PreviewRenderFailureReason.InvalidSelection);
        }

        CompositionVersion sourceComposition =
            request.ValidationContext.CompositionVersion;
        EntityId<CompositionVersionIdTag> compositionId =
            DerivedId<CompositionVersionIdTag>(
                derivationSeed,
                "composition-version",
                sourceComposition.Id.ToString());
        CompositionElement[] elements = slices
            .Select(static slice => slice.DerivedElement)
            .ToArray();
        ContentDigest compositionDigest =
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements);
        var composition = new CompositionVersion(
            compositionId,
            sourceComposition.ProjectId,
            sourceComposition.Id,
            "preview-render-service",
            sourceComposition.CreatedAt,
            "Derive an explicit interval preview.",
            compositionDigest,
            elements);

        EntityId<TimelineMapIdTag> timelineId = DerivedId<TimelineMapIdTag>(
            derivationSeed,
            "timeline-map",
            request.ValidationContext.TimelineMap.Id.ToString());
        TimelineMapEntry[] timelineEntries = elements
            .Select(static element => new TimelineMapEntry(
                element.OutputRange,
                element.Origin,
                element.SourceRange))
            .ToArray();
        var timelineMap = new TimelineMap(
            timelineId,
            composition.Id,
            timelineEntries);
        ContentDigest timelineDigest =
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timelineMap);

        var transformer = new IntervalNodeTransformer(
            request.Graph,
            selection,
            derivationSeed);
        RenderNode sourceOutput = request.Graph.Nodes.Single(node =>
            node.Id == request.Graph.OutputNodeId);
        EntityId<RenderNodeIdTag>[] terminalIds = sourceOutput.InputNodeIds
            .Select(transformer.Transform)
            .Where(static id => id is not null)
            .Cast<EntityId<RenderNodeIdTag>>()
            .Distinct()
            .ToArray();
        if (terminalIds.Length == 0)
        {
            throw Failure(
                PreviewRenderFailureReason.UnsupportedSelectionBoundary);
        }

        EntityId<RenderNodeIdTag> outputId = DerivedId<RenderNodeIdTag>(
            derivationSeed,
            "render-node",
            sourceOutput.Id.ToString());
        RenderNode output = RenderNode.Output(
            outputId,
            terminalIds,
            new TimeRange(default, selection.Duration));
        transformer.AddOutput(output);

        HashSet<EntityId<RenderInputIdTag>> referencedInputIds = transformer.Nodes
            .Where(static node => node.ArtifactInputId is not null)
            .Select(static node => node.ArtifactInputId!)
            .ToHashSet();
        RenderGraphInput[] graphInputs = request.Graph.Inputs
            .Where(input => referencedInputIds.Contains(input.Id))
            .ToArray();
        if (graphInputs.Length == 0)
        {
            throw Failure(
                PreviewRenderFailureReason.UnsupportedSelectionBoundary);
        }

        EntityId<RenderGraphIdTag> graphId = DerivedId<RenderGraphIdTag>(
            derivationSeed,
            "render-graph",
            request.Graph.Id.ToString());
        RenderGraph graph = new(
            graphId,
            composition.Id,
            composition.ContentDigest,
            timelineMap.Id,
            timelineDigest,
            request.Graph.ProfileId,
            request.Graph.ProfileDigest,
            request.Graph.LayoutSpecId,
            request.Graph.LayoutSpecDigest,
            request.Graph.Template,
            graphInputs,
            request.Graph.Fonts,
            transformer.Nodes,
            output.Id);
        HashSet<EntityId<RenderInputIdTag>> graphInputIds = graphInputs
            .Select(static input => input.Id)
            .ToHashSet();
        RenderArtifactEvidence[] artifacts = request.ValidationContext.Artifacts
            .Where(artifact => graphInputIds.Contains(artifact.Id))
            .ToArray();
        RenderGraphValidationContext context = new(
            request.ValidationContext.Project,
            composition,
            timelineMap,
            timelineDigest,
            request.ValidationContext.Profile,
            request.ValidationContext.LayoutSpec,
            request.ValidationContext.LayoutSpecDigest,
            request.ValidationContext.Template,
            request.ValidationContext.SourceSpans,
            request.ValidationContext.EditorialRecordIds,
            artifacts,
            request.ValidationContext.Fonts);
        return new DerivedPreview(
            graph,
            context,
            slices.ToArray());
    }

    private static PreviewRenderMapping[] BuildMappings(
        PreviewRenderRequest request,
        IReadOnlyList<ElementSlice> slices,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, ProxyFrameMap> proxyMaps)
    {
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans =
            request.ValidationContext.SourceSpans.ToDictionary(static span => span.Id);
        var mappings = new PreviewRenderMapping[slices.Count];
        for (int index = 0; index < slices.Count; index++)
        {
            ElementSlice slice = slices[index];
            PreviewFrameBoundaryEvidence? proxyStart = null;
            PreviewFrameBoundaryEvidence? proxyEnd = null;
            if (slice.DerivedElement.SourceRange is TimeRange sourceRange &&
                slice.SourceElement.Origin.SourceSpanId is
                    EntityId<SourceSpanIdTag> sourceSpanId)
            {
                EntityId<RenderInputIdTag>[] mappedInputIds = request.Graph.Nodes
                    .Where(node =>
                        node.Kind == RenderNodeKind.Video &&
                        node.OutputRange == slice.SourceElement.OutputRange &&
                        node.Origin == slice.SourceElement.Origin &&
                        node.SourceRange == slice.SourceElement.SourceRange &&
                        node.ArtifactInputId is not null)
                    .Select(static node => node.ArtifactInputId!)
                    .Distinct()
                    .ToArray();
                if (mappedInputIds.Length > 1)
                {
                    throw Failure(PreviewRenderFailureReason.ProxyMappingMismatch);
                }

                if (mappedInputIds.Length == 1)
                {
                    EntityId<RenderInputIdTag> inputId = mappedInputIds[0];
                    if (!proxyMaps.TryGetValue(
                            inputId,
                            out ProxyFrameMap? frameMap) ||
                        !sourceSpans.TryGetValue(sourceSpanId, out SourceSpan? span) ||
                        span.AssetId != frameMap.SourceAssetId ||
                        sourceRange.Start < frameMap.SourcePresentationStart ||
                        sourceRange.End > frameMap.SourcePresentationEnd ||
                        !frameMap.TryMapSourceTimeToProxyFrame(
                            sourceRange.Start,
                            out ProxyFrameMapEntry? startEntry))
                    {
                        throw Failure(
                            PreviewRenderFailureReason.ProxyMappingMismatch);
                    }

                    ProxyFrameMapEntry endEntry;
                    if (sourceRange.End == frameMap.SourcePresentationEnd)
                    {
                        endEntry = frameMap.Entries[^1];
                    }
                    else if (!frameMap.TryMapSourceTimeToProxyFrame(
                        sourceRange.End,
                        out ProxyFrameMapEntry? mappedEnd))
                    {
                        throw Failure(
                            PreviewRenderFailureReason.ProxyMappingMismatch);
                    }
                    else
                    {
                        endEntry = mappedEnd;
                    }

                    proxyStart = CreateBoundary(
                        inputId,
                        PreviewFrameBoundaryKind.InclusiveStart,
                        sourceRange.Start,
                        startEntry);
                    proxyEnd = CreateBoundary(
                        inputId,
                        PreviewFrameBoundaryKind.ExclusiveEnd,
                        sourceRange.End,
                        endEntry);
                }
            }

            mappings[index] = new PreviewRenderMapping(
                slice.SourceElement.Id,
                slice.SelectedTimelineRange,
                slice.DerivedElement.OutputRange,
                slice.DerivedElement.Origin,
                slice.DerivedElement.SourceRange,
                proxyStart,
                proxyEnd);
        }

        return mappings;
    }

    private static PreviewFrameBoundaryEvidence CreateBoundary(
        EntityId<RenderInputIdTag> inputId,
        PreviewFrameBoundaryKind kind,
        RationalTime sourceBoundaryTime,
        ProxyFrameMapEntry entry) =>
        new(
            inputId,
            kind,
            sourceBoundaryTime,
            entry.ProxyFrameSequence,
            entry.ProxyPresentationTime,
            entry.SourceFrameSequence,
            entry.SourcePresentationTime);

    private async Task<PreviewRenderResult?> TryGetCacheHitAsync(
        PreparedPreview prepared,
        CancellationToken cancellationToken)
    {
        ArtifactCacheCandidate[] candidates = GetCandidates(prepared);
        ArtifactPlan plan;
        try
        {
            plan = ArtifactPlanner.Plan(
                Operation,
                prepared.CacheKey,
                candidates,
                currentAdoption: null);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            throw Failure(PreviewRenderFailureReason.CacheStateInvalid);
        }

        if (plan.CachedArtifact is not ArtifactCacheCandidate candidate)
        {
            return null;
        }

        StoredArtifact artifact;
        try
        {
            artifact = await _validateRegisteredAsync(
                candidate.ArtifactId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ForgetCache(prepared.CacheKey, candidate.ArtifactId);
            return null;
        }

        if (!CacheMetadataMatches(artifact, candidate, prepared))
        {
            ForgetCache(prepared.CacheKey, candidate.ArtifactId);
            return null;
        }

        RememberCache(prepared.CacheKey, artifact);
        return CreateResult(
            prepared,
            artifact,
            cacheHit: true,
            processDuration: null);
    }

    private ArtifactCacheCandidate[] GetCandidates(PreparedPreview prepared)
    {
        var candidates = prepared.Request.CacheCandidates.ToList();
        lock (_knownCacheSync)
        {
            if (_knownCache.TryGetValue(
                    prepared.CacheKey,
                    out ArtifactCacheCandidate? known) &&
                !candidates.Any(candidate =>
                    candidate.ArtifactId == known.ArtifactId) &&
                !candidates.Any(candidate =>
                    candidate.CacheKey == prepared.CacheKey))
            {
                candidates.Add(known);
            }
        }

        return candidates.ToArray();
    }

    private void RememberCache(
        ArtifactCacheKey cacheKey,
        StoredArtifact artifact)
    {
        var candidate = new ArtifactCacheCandidate(
            artifact.Id,
            Operation,
            cacheKey,
            artifact.ContentDigest,
            artifact.CreatedAt);
        lock (_knownCacheSync)
        {
            if (!_knownCache.ContainsKey(cacheKey) &&
                _knownCache.Count >= MaximumRememberedCacheEntries)
            {
                ArtifactCacheKey oldest = _knownCache
                    .OrderBy(static entry => entry.Value.CreatedAt)
                    .ThenBy(
                        static entry => entry.Key.Digest.Hex,
                        StringComparer.Ordinal)
                    .First()
                    .Key;
                _knownCache.Remove(oldest);
            }

            _knownCache[cacheKey] = candidate;
        }
    }

    private void ForgetCache(
        ArtifactCacheKey cacheKey,
        EntityId<ArtifactIdTag> artifactId)
    {
        lock (_knownCacheSync)
        {
            if (_knownCache.TryGetValue(
                    cacheKey,
                    out ArtifactCacheCandidate? candidate) &&
                candidate.ArtifactId == artifactId)
            {
                _knownCache.Remove(cacheKey);
            }
        }
    }

    private static bool CacheMetadataMatches(
        StoredArtifact artifact,
        ArtifactCacheCandidate candidate,
        PreparedPreview prepared) =>
        artifact.Id == candidate.ArtifactId &&
        artifact.ProjectId == prepared.Context.Project.Id &&
        artifact.Kind == ArtifactKind.Preview &&
        artifact.ContentDigest == candidate.OutputDigest &&
        artifact.CreatedAt == candidate.CreatedAt &&
        artifact.ByteSize > 0 &&
        string.Equals(
            artifact.StorageReference,
            BuildStorageReference(
                artifact.ProjectId,
                prepared.CacheKey,
                artifact.Id),
            StringComparison.Ordinal);

    private CacheGate AcquireCacheGate(string key)
    {
        while (true)
        {
            CacheGate gate = _cacheGates.GetOrAdd(
                key,
                static _ => new CacheGate());
            lock (gate.Sync)
            {
                if (!gate.Retired)
                {
                    gate.ReferenceCount = checked(gate.ReferenceCount + 1);
                    return gate;
                }
            }

            Thread.Yield();
        }
    }

    private void ReleaseCacheGate(string key, CacheGate gate)
    {
        bool remove;
        lock (gate.Sync)
        {
            gate.ReferenceCount = checked(gate.ReferenceCount - 1);
            remove = gate.ReferenceCount == 0;
            if (remove)
            {
                gate.Retired = true;
            }
        }

        if (remove)
        {
            _cacheGates.TryRemove(key, out _);
        }
    }

    private void EnsureArtifactIdAvailable(EntityId<ArtifactIdTag> artifactId)
    {
        try
        {
            if (_getArtifact(artifactId) is not null)
            {
                throw Failure(PreviewRenderFailureReason.CacheStateInvalid);
            }
        }
        catch (PreviewRenderException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                PreviewRenderFailureReason.CacheStateInvalid,
                reconciliationRequired: true);
        }
    }

    private async Task<FfmpegCompilation> CompileAsync(
        PreparedPreview prepared,
        PreviewStaging staging,
        CancellationToken cancellationToken)
    {
        var compileRequest = new FfmpegCompileRequest(
            prepared.Graph,
            prepared.Context,
            prepared.Request.Tool,
            prepared.Inputs,
            staging.OutputPath,
            staging.DirectoryPath,
            prepared.Request.Timeout);
        try
        {
            FfmpegCompilation compilation = await _compileAsync(
                compileRequest,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    compilation.OutputPath,
                    staging.OutputPath,
                    PathComparison) ||
                !string.Equals(
                    compilation.ProcessRequest.WorkingDirectory,
                    staging.DirectoryPath,
                    PathComparison) ||
                compilation.ProcessRequest.Timeout != prepared.Request.Timeout)
            {
                throw Failure(PreviewRenderFailureReason.CompilationFailed);
            }

            return compilation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewRenderException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(PreviewRenderFailureReason.CompilationFailed);
        }
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(PreviewRenderFailureReason.RenderFailed);
        }
    }

    internal static void ValidateProcessResult(ProcessRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0 ||
            result.Truncated ||
            result.StandardOutput is null ||
            result.StandardOutput.Length != 0 ||
            result.StandardError is null ||
            result.StandardError.Length != 0 ||
            result.Duration < TimeSpan.Zero)
        {
            throw Failure(PreviewRenderFailureReason.RenderFailed);
        }
    }

    private async Task<StoredArtifact> PromoteAsync(
        PreparedPreview prepared,
        PreviewStaging staging,
        OutputSnapshot output,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var promotion = new ArtifactPromotionRequest(
            prepared.Request.PreviewArtifactId,
            prepared.Context.Project.Id,
            ArtifactKind.Preview,
            staging.OutputPath,
            BuildRelativePath(
                prepared.CacheKey,
                prepared.Request.PreviewArtifactId),
            output.Digest,
            output.ByteSize,
            createdAt);
        try
        {
            return await _promoteAsync(
                promotion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                PreviewRenderFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                PreviewRenderFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private async Task<StoredArtifact> ValidateRegisteredAsync(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _validateRegisteredAsync(
                artifactId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                PreviewRenderFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true);
        }
    }

    private static void ValidatePromotedMetadata(
        StoredArtifact artifact,
        PreparedPreview prepared,
        OutputSnapshot output,
        DateTimeOffset createdAt)
    {
        if (artifact.Id != prepared.Request.PreviewArtifactId ||
            artifact.ProjectId != prepared.Context.Project.Id ||
            artifact.Kind != ArtifactKind.Preview ||
            !string.Equals(
                artifact.StorageReference,
                BuildStorageReference(
                    artifact.ProjectId,
                    prepared.CacheKey,
                    artifact.Id),
                StringComparison.Ordinal) ||
            artifact.ContentDigest != output.Digest ||
            artifact.ByteSize != output.ByteSize ||
            artifact.CreatedAt != createdAt)
        {
            throw Failure(
                PreviewRenderFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true);
        }
    }

    private static PreviewRenderResult CreateResult(
        PreparedPreview prepared,
        StoredArtifact artifact,
        bool cacheHit,
        TimeSpan? processDuration) =>
        new(
            artifact.Id,
            artifact.ProjectId,
            artifact.ContentDigest,
            artifact.ByteSize,
            artifact.CreatedAt,
            prepared.CacheKey,
            prepared.ConfigurationDigest,
            prepared.GraphDigest,
            prepared.Request.SelectedOutputRange,
            new TimeRange(default, prepared.Request.SelectedOutputRange.Duration),
            prepared.Context.CompositionVersion.ContentDigest,
            prepared.Context.TimelineMapDigest,
            prepared.Context.Profile.Id,
            prepared.Context.Profile.ProfileDigest,
            prepared.Context.LayoutSpec.Id,
            prepared.Context.LayoutSpecDigest,
            prepared.Context.Template.Id,
            prepared.Context.Template.VersionDigest,
            prepared.Mappings,
            cacheHit,
            processDuration);

    private PreviewStaging PrepareStaging(
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken)
    {
        string stagingRoot = Path.GetFullPath(Path.Combine(
            _workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "previews"));
        string stagingDirectory = Path.Combine(stagingRoot, jobId.ToString());
        string outputPath = Path.Combine(
            stagingDirectory,
            string.Concat(artifactId.ToString(), ".partial.mp4"));
        if (!IsStrictDescendant(_workspacePath, stagingRoot) ||
            !IsStrictDescendant(stagingRoot, stagingDirectory) ||
            !IsStrictDescendant(stagingDirectory, outputPath) ||
            !HasSameWindowsDriveRoot(_workspacePath, outputPath))
        {
            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }

        EnsureDirectoryChain(stagingRoot, cancellationToken);
        if (PathEntryExistsConservatively(stagingDirectory))
        {
            throw Failure(PreviewRenderFailureReason.StagingConflict);
        }

        string claimDirectory = Path.Combine(
            stagingRoot,
            string.Concat(
                ".",
                jobId.ToString(),
                ".",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ".claim"));
        bool claimOwned = false;
        bool stagingOwned = false;
        var staging = new PreviewStaging(stagingDirectory, outputPath);

        try
        {
            if (PathEntryExistsConservatively(claimDirectory))
            {
                throw Failure(PreviewRenderFailureReason.StagingConflict);
            }

            _ = Directory.CreateDirectory(claimDirectory);
            claimOwned = true;
            ValidateExistingDirectory(claimDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(claimDirectory, stagingDirectory);
                claimOwned = false;
                stagingOwned = true;
            }
            catch (IOException)
            {
                throw Failure(
                    PathEntryExistsConservatively(stagingDirectory)
                        ? PreviewRenderFailureReason.StagingConflict
                        : PreviewRenderFailureReason.StagingUnavailable);
            }

            ValidateExistingDirectory(stagingDirectory);
            if (PathEntryExistsConservatively(outputPath))
            {
                throw Failure(PreviewRenderFailureReason.StagingConflict);
            }

            return staging;
        }
        catch (Exception exception)
        {
            bool cleaned = stagingOwned
                ? TryCleanupOwnedStaging(staging)
                : !claimOwned || TryDeleteOwnedEmptyDirectory(claimDirectory);
            if (!cleaned)
            {
                throw Failure(
                    PreviewRenderFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is PreviewRenderException)
            {
                throw;
            }

            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }
    }

    private void EnsureDirectoryChain(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsStrictDescendant(_workspacePath, destinationDirectory))
        {
            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }

        string relative = Path.GetRelativePath(
            _workspacePath,
            destinationDirectory);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = _workspacePath;
        ValidateWorkspaceChain();
        ValidateExistingDirectory(current);
        foreach (string segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            if (TryGetAttributes(current, out FileAttributes attributes))
            {
                ValidateDirectoryAttributes(attributes);
            }
            else
            {
                try
                {
                    _ = Directory.CreateDirectory(current);
                }
                catch (Exception exception) when (IsFileSystemFailure(exception))
                {
                    throw Failure(PreviewRenderFailureReason.StagingUnavailable);
                }
            }

            ValidateWorkspaceChain();
            ValidateExistingDirectory(current);
        }

        if (!string.Equals(current, destinationDirectory, PathComparison))
        {
            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }
    }

    private async Task<OutputSnapshot> VerifyOutputAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            ValidateExistingFile(path);
            using var stream = new FileStream(path, options);
            ValidateExistingFile(path);
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead || !stream.CanSeek || stream.Length <= 0)
            {
                throw Failure(PreviewRenderFailureReason.OutputInvalid);
            }

            long byteSize = stream.Length;
            DateTime lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            byte[] digestBytes = new byte[Sha256ByteLength];
            try
            {
                int written = await SHA256.HashDataAsync(
                    stream,
                    digestBytes,
                    cancellationToken).ConfigureAwait(false);
                if (written != Sha256ByteLength)
                {
                    throw Failure(PreviewRenderFailureReason.OutputInvalid);
                }

                if (stream.Length != byteSize ||
                    File.GetAttributes(stream.SafeFileHandle) != attributes ||
                    File.GetLastWriteTimeUtc(stream.SafeFileHandle) != lastWrite)
                {
                    throw Failure(PreviewRenderFailureReason.OutputInvalid);
                }

                ValidateExistingFile(path);
                return new OutputSnapshot(
                    ContentDigest.FromBytes(digestBytes),
                    byteSize);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digestBytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewRenderException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception) ||
                exception is CryptographicException)
        {
            throw Failure(PreviewRenderFailureReason.OutputInvalid);
        }
    }

    private bool TryCleanupOwnedStaging(PreviewStaging staging)
    {
        try
        {
            if (!IsStrictDescendant(_workspacePath, staging.DirectoryPath) ||
                !IsStrictDescendant(staging.DirectoryPath, staging.OutputPath))
            {
                return false;
            }

            ValidateWorkspaceChain();
            if (TryGetAttributes(
                    staging.OutputPath,
                    out FileAttributes outputAttributes))
            {
                ValidateRegularFileAttributes(outputAttributes);
                ValidateExistingFile(staging.OutputPath);
                File.Delete(staging.OutputPath);
                if (PathEntryExistsConservatively(staging.OutputPath))
                {
                    return false;
                }
            }

            return TryDeleteOwnedEmptyDirectory(staging.DirectoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryDeleteOwnedEmptyDirectory(string path)
    {
        try
        {
            if (!IsStrictDescendant(_workspacePath, path))
            {
                return false;
            }

            if (!TryGetAttributes(path, out FileAttributes attributes))
            {
                return true;
            }

            ValidateDirectoryAttributes(attributes);
            ValidateExistingDirectory(path);
            Directory.Delete(path, recursive: false);
            return !PathEntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ContentDigest ComputeDerivationSeed(
        PreviewRenderRequest request,
        ContentDigest originalGraphDigest) =>
        ComputeCanonicalDigest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("domain", DerivationDomain);
            writer.WriteString("originalGraphDigest", originalGraphDigest.Hex);
            writer.WritePropertyName("selectedOutputRange");
            WriteRange(writer, request.SelectedOutputRange);
            writer.WriteString(
                "toolDigest",
                request.Tool.VerificationRequest.ExpectedDigest.Hex);
            writer.WriteNumber(
                "toolByteSize",
                request.Tool.VerificationRequest.ExpectedByteSize);
            writer.WriteEndObject();
        });

    private static ContentDigest ComputeConfigurationDigest(
        ContentDigest derivationSeed,
        DerivedPreview derived,
        ContentDigest graphDigest,
        TimeRange selection) =>
        ComputeCanonicalDigest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", Operation);
            writer.WriteString("derivationSeed", derivationSeed.Hex);
            writer.WriteString("renderGraphDigest", graphDigest.Hex);
            writer.WriteString(
                "compositionDigest",
                derived.Context.CompositionVersion.ContentDigest.Hex);
            writer.WriteString(
                "timelineMapDigest",
                derived.Context.TimelineMapDigest.Hex);
            writer.WriteString("profileId", derived.Context.Profile.Id.ToString());
            writer.WriteString(
                "profileDigest",
                derived.Context.Profile.ProfileDigest.Hex);
            writer.WriteString(
                "layoutSpecId",
                derived.Context.LayoutSpec.Id.ToString());
            writer.WriteString(
                "layoutSpecDigest",
                derived.Context.LayoutSpecDigest.Hex);
            writer.WriteString("templateId", derived.Context.Template.Id.ToString());
            writer.WriteString(
                "templateVersionDigest",
                derived.Context.Template.VersionDigest.Hex);
            writer.WritePropertyName("selection");
            WriteRange(writer, selection);
            writer.WriteString(
                "ffmpegExecutableDigest",
                FfmpegCompiler.FixedFfmpegSha256);
            writer.WriteNumber(
                "ffmpegExecutableByteSize",
                FfmpegCompiler.FixedFfmpegByteSize);
            writer.WriteEndObject();
        });

    private static ContentDigest ComputeGraphDigest(RenderGraph graph) =>
        ComputeCanonicalDigest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", graph.Id.ToString());
            writer.WriteString(
                "compositionVersionId",
                graph.CompositionVersionId.ToString());
            writer.WriteString(
                "compositionVersionDigest",
                graph.CompositionVersionDigest.Hex);
            writer.WriteString("timelineMapId", graph.TimelineMapId.ToString());
            writer.WriteString("timelineMapDigest", graph.TimelineMapDigest.Hex);
            writer.WriteString("profileId", graph.ProfileId.ToString());
            writer.WriteString("profileDigest", graph.ProfileDigest.Hex);
            writer.WriteString("layoutSpecId", graph.LayoutSpecId.ToString());
            writer.WriteString("layoutSpecDigest", graph.LayoutSpecDigest.Hex);
            writer.WriteString("templateId", graph.Template.Id.ToString());
            writer.WriteString(
                "templateVersionDigest",
                graph.Template.VersionDigest.Hex);
            writer.WritePropertyName("inputs");
            writer.WriteStartArray();
            foreach (RenderGraphInput input in graph.Inputs)
            {
                writer.WriteStartObject();
                writer.WriteString("id", input.Id.ToString());
                writer.WriteString("kind", input.Kind.ToString());
                writer.WriteString("digest", input.ContentDigest.Hex);
                writer.WriteNumber("byteSize", input.ByteSize);
                writer.WritePropertyName("origin");
                WriteOrigin(writer, input.Origin);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("fonts");
            writer.WriteStartArray();
            foreach (RenderFontEvidence font in graph.Fonts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", font.Name);
                writer.WriteString("digest", font.FileDigest.Hex);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("nodes");
            writer.WriteStartArray();
            foreach (RenderNode node in graph.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id.ToString());
                writer.WriteString("kind", node.Kind.ToString());
                writer.WritePropertyName("inputNodeIds");
                writer.WriteStartArray();
                foreach (EntityId<RenderNodeIdTag> inputId in node.InputNodeIds)
                {
                    writer.WriteStringValue(inputId.ToString());
                }

                writer.WriteEndArray();
                if (node.ArtifactInputId is not null)
                {
                    writer.WriteString(
                        "artifactInputId",
                        node.ArtifactInputId.ToString());
                }

                writer.WritePropertyName("outputRange");
                WriteRange(writer, node.OutputRange);
                if (node.SourceRange is TimeRange sourceRange)
                {
                    writer.WritePropertyName("sourceRange");
                    WriteRange(writer, sourceRange);
                }

                if (node.Origin is not null)
                {
                    writer.WritePropertyName("origin");
                    WriteOrigin(writer, node.Origin);
                }

                if (node.RegionName is not null)
                {
                    writer.WriteString("regionName", node.RegionName);
                }

                if (node.Cue is not null)
                {
                    writer.WritePropertyName("cue");
                    writer.WriteStartObject();
                    writer.WriteString("id", node.Cue.Id.ToString());
                    writer.WriteString("kind", node.Cue.Kind.ToString());
                    writer.WritePropertyName("range");
                    WriteRange(writer, node.Cue.Range);
                    if (node.Cue.RegionName is not null)
                    {
                        writer.WriteString("regionName", node.Cue.RegionName);
                    }

                    writer.WriteEndObject();
                }

                if (node.Transition is not null)
                {
                    writer.WritePropertyName("transition");
                    writer.WriteStartObject();
                    writer.WriteString("kind", node.Transition.Kind.ToString());
                    writer.WritePropertyName("duration");
                    WriteTime(writer, node.Transition.Duration);
                    if (node.Transition.Direction is not null)
                    {
                        writer.WriteString(
                            "direction",
                            node.Transition.Direction.Value.ToString());
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("outputNodeId", graph.OutputNodeId.ToString());
            writer.WriteEndObject();
        });

    private static ContentDigest ComputeProxyMapDigest(ProxyFrameMap frameMap) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(frameMap.CanonicalJson));

    private static ContentDigest ComputeCanonicalDigest(
        Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        return ContentDigest.Compute(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    private static void WriteRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, RationalTime time)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", time.Value);
        writer.WriteNumber("rate", time.Rate);
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", origin.Kind.ToString());
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan:
                writer.WriteString("id", origin.SourceSpanId!.ToString());
                break;
            case OriginKind.Asset:
                writer.WriteString("id", origin.AssetId!.ToString());
                break;
            case OriginKind.EditorialRecord:
                writer.WriteString("id", origin.EditorialRecordId!.ToString());
                break;
            default:
                throw new InvalidOperationException("Unknown render origin kind.");
        }

        writer.WriteEndObject();
    }

    private static EntityId<TTag> DerivedId<TTag>(
        ContentDigest seed,
        string scope,
        string sourceIdentity)
    {
        byte[] material = Encoding.UTF8.GetBytes(string.Concat(
            DerivationDomain,
            "\n",
            seed.Hex,
            "\n",
            scope,
            "\n",
            sourceIdentity));
        byte[] digest = SHA256.HashData(material);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        Guid value = new(digest.AsSpan(0, 16), bigEndian: true);
        CryptographicOperations.ZeroMemory(digest);
        return EntityId<TTag>.From(value);
    }

    private static TimeRange Intersect(TimeRange first, TimeRange second)
    {
        RationalTime start = first.Start > second.Start
            ? first.Start
            : second.Start;
        RationalTime end = first.End < second.End
            ? first.End
            : second.End;
        if (end <= start)
        {
            throw Failure(PreviewRenderFailureReason.InvalidSelection);
        }

        return new TimeRange(start, end - start);
    }

    private static TimeRange Rebase(TimeRange range, RationalTime origin) =>
        new(range.Start - origin, range.Duration);

    private static TimeRange? ClipSourceRange(
        TimeRange outputRange,
        TimeRange? sourceRange,
        TimeRange intersection) =>
        sourceRange is TimeRange source
            ? new TimeRange(
                source.Start + (intersection.Start - outputRange.Start),
                intersection.Duration)
            : null;

    private static string BuildRelativePath(
        ArtifactCacheKey cacheKey,
        EntityId<ArtifactIdTag> artifactId) =>
        string.Concat(
            cacheKey.Digest.Hex,
            "/",
            artifactId.ToString(),
            ".mp4");

    private static string BuildStorageReference(
        EntityId<ProjectIdTag> projectId,
        ArtifactCacheKey cacheKey,
        EntityId<ArtifactIdTag> artifactId) =>
        string.Concat(
            "projects/",
            projectId.ToString(),
            "/previews/",
            BuildRelativePath(cacheKey, artifactId));

    private static string ValidateWorkspacePath(string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            !string.Equals(workspacePath, workspacePath.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(workspacePath))
        {
            throw new ArgumentException(
                "The preview workspace must be an existing absolute directory.",
                nameof(workspacePath));
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(workspacePath));
            ValidateDirectoryAncestorChain(normalized);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException or ArgumentException)
        {
            throw new ArgumentException(
                "The preview workspace must be an existing local directory without reparse points.",
                nameof(workspacePath));
        }

        return normalized;
    }

    private void ValidateWorkspaceChain()
    {
        try
        {
            ValidateDirectoryAncestorChain(_workspacePath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }
    }

    private static void ValidateDirectoryAncestorChain(string path)
    {
        string? current = path;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            ValidateDirectoryAttributes(attributes);
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private void ValidateExistingDirectory(string path)
    {
        ValidateExistingPath(path, targetIsDirectory: true);
    }

    private void ValidateExistingFile(string path)
    {
        ValidateExistingPath(path, targetIsDirectory: false);
    }

    private void ValidateExistingPath(string path, bool targetIsDirectory)
    {
        if (!string.Equals(path, _workspacePath, PathComparison) &&
            !IsStrictDescendant(_workspacePath, path))
        {
            throw Failure(PreviewRenderFailureReason.StagingUnavailable);
        }

        string? current = path;
        bool target = true;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if (target && !targetIsDirectory)
            {
                ValidateRegularFileAttributes(attributes);
            }
            else
            {
                ValidateDirectoryAttributes(attributes);
            }

            if (string.Equals(current, _workspacePath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
            target = false;
        }

        throw Failure(PreviewRenderFailureReason.StagingUnavailable);
    }

    private static void ValidateDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.Device) != 0)
        {
            throw new IOException("The directory path is unsafe.");
        }
    }

    private static void ValidateRegularFileAttributes(FileAttributes attributes)
    {
        if ((attributes &
            (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw Failure(PreviewRenderFailureReason.OutputInvalid);
        }
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool PathEntryExistsConservatively(string path)
    {
        try
        {
            return TryGetAttributes(path, out _);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return false;
        }

        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : string.Concat(root, Path.DirectorySeparatorChar);
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool HasSameWindowsDriveRoot(string first, string second)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return string.Equals(
            Path.GetPathRoot(first),
            Path.GetPathRoot(second),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> GetProcessDelegate(ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static Func<
        ArtifactPromotionRequest,
        CancellationToken,
        Task<StoredArtifact>> GetPromotionDelegate(ArtifactStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.PromoteAsync;
    }

    private static Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<StoredArtifact>> GetValidationDelegate(ArtifactStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ValidateRegisteredAsync;
    }

    private static Func<EntityId<ArtifactIdTag>, StoredArtifact?>
        GetArtifactDelegate(ArtifactStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetArtifact;
    }

    private static PreviewRenderException Failure(
        PreviewRenderFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record PreparedPreview(
        PreviewRenderRequest Request,
        RenderGraph Graph,
        RenderGraphValidationContext Context,
        IReadOnlyList<FfmpegInputBinding> Inputs,
        ArtifactCacheKey CacheKey,
        ContentDigest ConfigurationDigest,
        ContentDigest GraphDigest,
        IReadOnlyList<PreviewRenderMapping> Mappings);

    private sealed record DerivedPreview(
        RenderGraph Graph,
        RenderGraphValidationContext Context,
        IReadOnlyList<ElementSlice> ElementSlices);

    private sealed record ElementSlice(
        CompositionElement SourceElement,
        TimeRange SelectedTimelineRange,
        CompositionElement DerivedElement);

    private sealed record PreviewStaging(
        string DirectoryPath,
        string OutputPath);

    private sealed record OutputSnapshot(
        ContentDigest Digest,
        long ByteSize);

    private sealed class CacheGate
    {
        internal object Sync { get; } = new();

        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int ReferenceCount { get; set; }

        internal bool Retired { get; set; }
    }

    private sealed class IntervalNodeTransformer
    {
        private readonly IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode>
            _sourceNodes;
        private readonly TimeRange _selection;
        private readonly ContentDigest _seed;
        private readonly Dictionary<
            EntityId<RenderNodeIdTag>,
            EntityId<RenderNodeIdTag>?> _transformed = [];
        private readonly List<RenderNode> _nodes = [];

        internal IntervalNodeTransformer(
            RenderGraph source,
            TimeRange selection,
            ContentDigest seed)
        {
            _sourceNodes = source.Nodes.ToDictionary(static node => node.Id);
            _selection = selection;
            _seed = seed;
        }

        internal IReadOnlyList<RenderNode> Nodes => _nodes.AsReadOnly();

        internal EntityId<RenderNodeIdTag>? Transform(
            EntityId<RenderNodeIdTag> sourceId)
        {
            if (_transformed.TryGetValue(
                    sourceId,
                    out EntityId<RenderNodeIdTag>? transformed))
            {
                return transformed;
            }

            RenderNode source = _sourceNodes[sourceId];
            EntityId<RenderNodeIdTag>? result = source.Kind switch
            {
                RenderNodeKind.Video => TransformVideo(source),
                RenderNodeKind.Audio => TransformAudio(source),
                RenderNodeKind.Graphic => TransformGraphic(source),
                RenderNodeKind.Subtitle => TransformSubtitle(source),
                RenderNodeKind.Transition => TransformTransition(source),
                RenderNodeKind.Output => throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary),
                _ => throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary),
            };
            _transformed[sourceId] = result;
            return result;
        }

        internal void AddOutput(RenderNode output)
        {
            _nodes.Add(output);
        }

        private EntityId<RenderNodeIdTag>? TransformVideo(RenderNode source)
        {
            if (!source.OutputRange.Overlaps(_selection))
            {
                return null;
            }

            TimeRange intersection = Intersect(source.OutputRange, _selection);
            EntityId<RenderNodeIdTag> id = NodeId(source);
            RenderNode node = RenderNode.Video(
                id,
                source.ArtifactInputId!,
                Rebase(intersection, _selection.Start),
                source.Origin!,
                ClipSourceRange(
                    source.OutputRange,
                    source.SourceRange,
                    intersection),
                source.RegionName!);
            _nodes.Add(node);
            return id;
        }

        private EntityId<RenderNodeIdTag>? TransformAudio(RenderNode source)
        {
            if (!source.OutputRange.Overlaps(_selection))
            {
                return null;
            }

            TimeRange intersection = Intersect(source.OutputRange, _selection);
            EntityId<RenderNodeIdTag> id = NodeId(source);
            RenderNode node = RenderNode.Audio(
                id,
                source.ArtifactInputId!,
                Rebase(intersection, _selection.Start),
                source.Origin!,
                ClipSourceRange(
                    source.OutputRange,
                    source.SourceRange,
                    intersection));
            _nodes.Add(node);
            return id;
        }

        private EntityId<RenderNodeIdTag>? TransformGraphic(RenderNode source)
        {
            EntityId<RenderNodeIdTag>? dependency = Transform(
                source.InputNodeIds[0]);
            if (!source.OutputRange.Overlaps(_selection))
            {
                return dependency;
            }

            if (dependency is null)
            {
                throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary);
            }

            TimeRange range = Rebase(
                Intersect(source.OutputRange, _selection),
                _selection.Start);
            Cue cue = RebaseCue(source, range);
            EntityId<RenderNodeIdTag> id = NodeId(source);
            _nodes.Add(RenderNode.Graphic(
                id,
                source.ArtifactInputId!,
                dependency,
                cue,
                source.Origin!));
            return id;
        }

        private EntityId<RenderNodeIdTag>? TransformSubtitle(RenderNode source)
        {
            EntityId<RenderNodeIdTag>? dependency = Transform(
                source.InputNodeIds[0]);
            if (!source.OutputRange.Overlaps(_selection))
            {
                return dependency;
            }

            if (dependency is null)
            {
                throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary);
            }

            TimeRange range = Rebase(
                Intersect(source.OutputRange, _selection),
                _selection.Start);
            Cue cue = RebaseCue(source, range);
            EntityId<RenderNodeIdTag> id = NodeId(source);
            _nodes.Add(RenderNode.Subtitle(
                id,
                source.ArtifactInputId!,
                dependency,
                cue,
                source.Origin!));
            return id;
        }

        private EntityId<RenderNodeIdTag>? TransformTransition(RenderNode source)
        {
            Transition transition = source.Transition!;
            if (transition.Kind == TransitionKind.Cut)
            {
                if (source.OutputRange.Start <= _selection.Start)
                {
                    return Transform(source.InputNodeIds[^1]);
                }

                if (source.OutputRange.Start >= _selection.End)
                {
                    return Transform(source.InputNodeIds[0]);
                }

                return PreserveTransition(source);
            }

            if (source.OutputRange.End <= _selection.Start)
            {
                return Transform(source.InputNodeIds[^1]);
            }

            if (source.OutputRange.Start >= _selection.End)
            {
                return Transform(source.InputNodeIds[0]);
            }

            if (_selection.Start > source.OutputRange.Start ||
                _selection.End < source.OutputRange.End)
            {
                throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary);
            }

            return PreserveTransition(source);
        }

        private EntityId<RenderNodeIdTag> PreserveTransition(RenderNode source)
        {
            EntityId<RenderNodeIdTag>[] dependencies = source.InputNodeIds
                .Select(Transform)
                .Where(static id => id is not null)
                .Cast<EntityId<RenderNodeIdTag>>()
                .ToArray();
            if (dependencies.Length != source.InputNodeIds.Count)
            {
                throw Failure(
                    PreviewRenderFailureReason.UnsupportedSelectionBoundary);
            }

            EntityId<RenderNodeIdTag> id = NodeId(source);
            _nodes.Add(RenderNode.TransitionEffect(
                id,
                dependencies,
                Rebase(source.OutputRange, _selection.Start),
                source.Origin!,
                source.Transition!));
            return id;
        }

        private Cue RebaseCue(RenderNode source, TimeRange range)
        {
            Cue cue = source.Cue!;
            EntityId<CueIdTag> cueId = DerivedId<CueIdTag>(
                _seed,
                "cue",
                cue.Id.ToString());
            return new Cue(cueId, cue.Kind, range, cue.RegionName);
        }

        private EntityId<RenderNodeIdTag> NodeId(RenderNode source) =>
            DerivedId<RenderNodeIdTag>(
                _seed,
                "render-node",
                source.Id.ToString());
    }
}