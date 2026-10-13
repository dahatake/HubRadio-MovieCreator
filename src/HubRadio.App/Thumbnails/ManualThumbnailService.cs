using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Thumbnails;

public sealed class ManualThumbnailRequestIdTag
{
}

public sealed class ThumbnailTemplateApprovalIdTag
{
}

public sealed class ManualThumbnailFrameSelection
{
    public ManualThumbnailFrameSelection(
        int proxyFrameSequence,
        int sourceFrameSequence,
        RationalTime? sourcePresentationTime,
        string selectedByActorId,
        DateTimeOffset selectedAt)
    {
        ProxyFrameSequence = proxyFrameSequence;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
        SelectedByActorId = ManualThumbnailContract.RequireText(
            selectedByActorId,
            nameof(selectedByActorId),
            "A thumbnail frame-selection actor ID");
        _ = ManualThumbnailContract.GetUtcTicks(
            selectedAt,
            nameof(selectedAt));
        SelectedAt = selectedAt;
    }

    public int ProxyFrameSequence { get; }

    public int SourceFrameSequence { get; }

    public RationalTime? SourcePresentationTime { get; }

    public string SelectedByActorId { get; }

    public DateTimeOffset SelectedAt { get; }
}

/// <summary>
/// Trusted evidence that one exact thumbnail-template version was approved.
/// </summary>
/// <remarks>
/// There is intentionally no public constructor. Application persistence code may
/// create this snapshot only after retaining the corresponding BrandApprover decision.
/// </remarks>
public sealed class ApprovedThumbnailTemplateSnapshot
{
    internal ApprovedThumbnailTemplateSnapshot(
        EntityId<ThumbnailTemplateApprovalIdTag> id,
        BrandTemplateVersion templateVersion,
        string actorId,
        DateTimeOffset approvedAt,
        string reason,
        string evidenceReference)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(templateVersion);
        if (templateVersion.Kind != BrandTemplateKind.Thumbnail)
        {
            throw new ArgumentException(
                "A thumbnail-template approval must bind a thumbnail template.",
                nameof(templateVersion));
        }

        Id = id;
        TemplateVersion = templateVersion;
        ActorId = ManualThumbnailContract.RequireText(
            actorId,
            nameof(actorId),
            "A thumbnail-template approver ID");
        ActorRole = ReviewActorRole.BrandApprover;
        _ = ManualThumbnailContract.GetUtcTicks(
            approvedAt,
            nameof(approvedAt));
        ApprovedAt = approvedAt;
        Reason = ManualThumbnailContract.RequireText(
            reason,
            nameof(reason),
            "A thumbnail-template approval reason");
        EvidenceReference = ManualThumbnailContract.RequireText(
            evidenceReference,
            nameof(evidenceReference),
            "A thumbnail-template approval evidence reference");
        ApprovalDigest = ManualThumbnailHash.ComputeApprovalDigest(this);
    }

    public EntityId<ThumbnailTemplateApprovalIdTag> Id { get; }

    public BrandTemplateVersion TemplateVersion { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset ApprovedAt { get; }

    public string Reason { get; }

    public string EvidenceReference { get; }

    public ContentDigest ApprovalDigest { get; }
}

public sealed class ManualThumbnailSourceBinding
{
    public ManualThumbnailSourceBinding(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> sourceArtifactId,
        Asset sourceAsset,
        ContentDigest sourcePtsIndexDigest,
        ContentDigest proxyFrameMapDigest)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(sourceArtifactId);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourcePtsIndexDigest);
        ArgumentNullException.ThrowIfNull(proxyFrameMapDigest);

        ProjectId = projectId;
        SourceArtifactId = sourceArtifactId;
        SourceAssetId = sourceAsset.Id;
        SourceContentDigest = sourceAsset.ContentDigest;
        SourceByteSize = sourceAsset.ByteSize;
        SourceRightsDigest = ManualThumbnailHash.ComputeRightsDigest(
            sourceAsset.Rights);
        ObservedRightsStatus = sourceAsset.Rights.Status;
        ObservedExternalPublicationAllowed =
            sourceAsset.Rights.ExternalPublicationAllowed;
        SourcePtsIndexDigest = sourcePtsIndexDigest;
        ProxyFrameMapDigest = proxyFrameMapDigest;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<ArtifactIdTag> SourceArtifactId { get; }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public ContentDigest SourceContentDigest { get; }

    public long SourceByteSize { get; }

    public ContentDigest SourceRightsDigest { get; }

    public RightsStatus ObservedRightsStatus { get; }

    public bool ObservedExternalPublicationAllowed { get; }

    public ContentDigest SourcePtsIndexDigest { get; }

    public ContentDigest ProxyFrameMapDigest { get; }
}

public sealed class ManualThumbnailTemplateBinding
{
    public ManualThumbnailTemplateBinding(
        ApprovedThumbnailTemplateSnapshot approval,
        RenderProfile renderProfile,
        ContentDigest layoutSpecDigest)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(renderProfile);
        ArgumentNullException.ThrowIfNull(layoutSpecDigest);
        TemplateReference reference = approval.TemplateVersion.Reference;
        if (renderProfile.TemplateId != reference.Id ||
            renderProfile.TemplateVersionDigest != reference.VersionDigest)
        {
            throw new ArgumentException(
                "The render profile must bind the approved thumbnail-template version.",
                nameof(renderProfile));
        }

        ApprovalId = approval.Id;
        ApprovalDigest = approval.ApprovalDigest;
        SeriesId = approval.TemplateVersion.SeriesId;
        TemplateId = reference.Id;
        TemplateVersionDigest = reference.VersionDigest;
        RenderProfileId = renderProfile.Id;
        RenderProfileDigest = renderProfile.ProfileDigest;
        LayoutSpecId = renderProfile.LayoutSpecId;
        LayoutSpecDigest = layoutSpecDigest;
    }

    public EntityId<ThumbnailTemplateApprovalIdTag> ApprovalId { get; }

    public ContentDigest ApprovalDigest { get; }

    public EntityId<SeriesIdTag> SeriesId { get; }

    public EntityId<TemplateIdTag> TemplateId { get; }

    public ContentDigest TemplateVersionDigest { get; }

    public EntityId<RenderProfileIdTag> RenderProfileId { get; }

    public ContentDigest RenderProfileDigest { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest LayoutSpecDigest { get; }
}

public sealed class ManualThumbnailRequest
{
    public ManualThumbnailRequest(
        EntityId<ManualThumbnailRequestIdTag> id,
        EntityId<ArtifactIdTag> outputArtifactId,
        ManualThumbnailSourceBinding source,
        ManualThumbnailFrameSelection selection,
        ManualThumbnailTemplateBinding template)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(outputArtifactId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(template);
        if (outputArtifactId == source.SourceArtifactId)
        {
            throw new ArgumentException(
                "The source and thumbnail artifact IDs must be distinct.",
                nameof(outputArtifactId));
        }

        Id = id;
        OutputArtifactId = outputArtifactId;
        Source = source;
        Selection = selection;
        Template = template;
    }

    public EntityId<ManualThumbnailRequestIdTag> Id { get; }

    public EntityId<ArtifactIdTag> OutputArtifactId { get; }

    public ManualThumbnailSourceBinding Source { get; }

    public ManualThumbnailFrameSelection Selection { get; }

    public ManualThumbnailTemplateBinding Template { get; }
}

public sealed class ManualThumbnailContext
{
    public ManualThumbnailContext(
        Project project,
        StoredArtifact sourceArtifact,
        PtsIndex sourcePtsIndex,
        ProxyFrameMap proxyFrameMap,
        BrandTemplateVersion templateVersion,
        AppliedBrandTemplate template,
        ApprovedThumbnailTemplateSnapshot? templateApproval,
        RenderProfile renderProfile,
        LayoutSpec layoutSpec,
        ContentDigest layoutSpecDigest)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceArtifact);
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(proxyFrameMap);
        ArgumentNullException.ThrowIfNull(templateVersion);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(renderProfile);
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(layoutSpecDigest);

        Project = project;
        SourceArtifact = sourceArtifact;
        SourcePtsIndex = sourcePtsIndex;
        ProxyFrameMap = proxyFrameMap;
        TemplateVersion = templateVersion;
        Template = template;
        TemplateApproval = templateApproval;
        RenderProfile = renderProfile;
        LayoutSpec = layoutSpec;
        LayoutSpecDigest = layoutSpecDigest;
    }

    public Project Project { get; }

    public StoredArtifact SourceArtifact { get; }

    public PtsIndex SourcePtsIndex { get; }

    public ProxyFrameMap ProxyFrameMap { get; }

    public BrandTemplateVersion TemplateVersion { get; }

    public AppliedBrandTemplate Template { get; }

    public ApprovedThumbnailTemplateSnapshot? TemplateApproval { get; }

    public RenderProfile RenderProfile { get; }

    public LayoutSpec LayoutSpec { get; }

    public ContentDigest LayoutSpecDigest { get; }
}

public sealed class ManualThumbnailResult
{
    internal ManualThumbnailResult(
        ManualThumbnailRequest request,
        StoredArtifact artifact,
        Asset sourceAsset,
        int sourceVideoStreamIndex,
        ApprovedThumbnailTemplateSnapshot approval,
        OriginRef origin,
        ContentDigest bindingDigest,
        int outputWidth,
        int outputHeight,
        bool wasAlreadyRegistered)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(bindingDigest);

        RequestId = request.Id;
        ArtifactId = artifact.Id;
        ProjectId = artifact.ProjectId;
        Source = request.Source;
        Selection = request.Selection;
        Template = request.Template;
        SourceVideoStreamIndex = sourceVideoStreamIndex;
        SourceRightsStatus = sourceAsset.Rights.Status;
        SourceExternalPublicationAllowed =
            sourceAsset.Rights.ExternalPublicationAllowed;
        TemplateApprovalActorId = approval.ActorId;
        TemplateApprovalRole = approval.ActorRole;
        TemplateApprovedAt = approval.ApprovedAt;
        Origin = origin;
        BindingDigest = bindingDigest;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        OutputContentDigest = artifact.ContentDigest;
        OutputByteSize = artifact.ByteSize;
        CreatedAt = artifact.CreatedAt;
        WasAlreadyRegistered = wasAlreadyRegistered;
    }

    public EntityId<ManualThumbnailRequestIdTag> RequestId { get; }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ManualThumbnailSourceBinding Source { get; }

    public ManualThumbnailFrameSelection Selection { get; }

    public ManualThumbnailTemplateBinding Template { get; }

    public int SourceVideoStreamIndex { get; }

    public RightsStatus SourceRightsStatus { get; }

    public bool SourceExternalPublicationAllowed { get; }

    public string TemplateApprovalActorId { get; }

    public ReviewActorRole TemplateApprovalRole { get; }

    public DateTimeOffset TemplateApprovedAt { get; }

    public OriginRef Origin { get; }

    public ContentDigest BindingDigest { get; }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public ContentDigest OutputContentDigest { get; }

    public long OutputByteSize { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool WasAlreadyRegistered { get; }
}

public enum ManualThumbnailFailureReason
{
    InputBindingMismatch,
    SourceBindingStale,
    SourceRightsNotApproved,
    SelectedFrameInvalid,
    ProxyMappingStale,
    TemplateNotApproved,
    TemplateBindingStale,
    TemplateUnsupported,
    ProfileBindingStale,
    LayoutBindingStale,
    RegisteredSourceInvalid,
    ToolVerificationFailed,
    DuplicateRequest,
    StagingConflict,
    StagingUnavailable,
    ProcessTimedOut,
    ProcessFailed,
    OutputInvalid,
    PromotionFailed,
    RegisteredOutputInvalid,
    CleanupFailed,
}

public sealed class ManualThumbnailException : Exception
{
    internal ManualThumbnailException(
        ManualThumbnailFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public ManualThumbnailFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(ManualThumbnailFailureReason reason) =>
        reason switch
        {
            ManualThumbnailFailureReason.InputBindingMismatch =>
                "The thumbnail request does not identify one project output.",
            ManualThumbnailFailureReason.SourceBindingStale =>
                "The thumbnail source snapshot is stale.",
            ManualThumbnailFailureReason.SourceRightsNotApproved =>
                "The thumbnail source is not approved for external use.",
            ManualThumbnailFailureReason.SelectedFrameInvalid =>
                "The thumbnail source frame was not explicitly selected by exact PTS.",
            ManualThumbnailFailureReason.ProxyMappingStale =>
                "The thumbnail proxy-to-source frame mapping is stale.",
            ManualThumbnailFailureReason.TemplateNotApproved =>
                "The thumbnail template does not have trusted approval evidence.",
            ManualThumbnailFailureReason.TemplateBindingStale =>
                "The approved thumbnail-template binding is stale.",
            ManualThumbnailFailureReason.TemplateUnsupported =>
                "The thumbnail template uses unsupported composition features.",
            ManualThumbnailFailureReason.ProfileBindingStale =>
                "The thumbnail render-profile binding is stale.",
            ManualThumbnailFailureReason.LayoutBindingStale =>
                "The thumbnail layout binding is stale.",
            ManualThumbnailFailureReason.RegisteredSourceInvalid =>
                "The registered thumbnail source could not be verified.",
            ManualThumbnailFailureReason.ToolVerificationFailed =>
                "The thumbnail rendering tool could not be verified.",
            ManualThumbnailFailureReason.DuplicateRequest =>
                "The thumbnail request identity is already bound to different output.",
            ManualThumbnailFailureReason.StagingConflict =>
                "The thumbnail staging destination already exists.",
            ManualThumbnailFailureReason.StagingUnavailable =>
                "The thumbnail staging destination is unavailable.",
            ManualThumbnailFailureReason.ProcessTimedOut =>
                "The thumbnail rendering process exceeded its timeout.",
            ManualThumbnailFailureReason.ProcessFailed =>
                "The thumbnail rendering process failed.",
            ManualThumbnailFailureReason.OutputInvalid =>
                "The rendered thumbnail is not a valid exact PNG output.",
            ManualThumbnailFailureReason.PromotionFailed =>
                "The rendered thumbnail could not be promoted.",
            ManualThumbnailFailureReason.RegisteredOutputInvalid =>
                "The registered thumbnail output could not be verified.",
            ManualThumbnailFailureReason.CleanupFailed =>
                "Thumbnail staging cleanup requires reconciliation.",
            _ => "Manual thumbnail generation failed.",
        };
}

public sealed class ManualThumbnailService
{
    private const int FileBufferSize = 64 * 1024;

    private static readonly ContentDigest FixedFfmpegDigest =
        new(FfmpegCompiler.FixedFfmpegSha256);

    private readonly ArtifactStore _artifactStore;
    private readonly Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> _runProcessAsync;
    private readonly Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyBinaryAsync;
    private readonly Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyRegisteredAsync;
    private readonly Func<
        ArtifactPromotionRequest,
        CancellationToken,
        Task<StoredArtifact>> _promoteAsync;
    private readonly string _workspacePath;
    private readonly string _ffmpegPath;
    private readonly TimeProvider _timeProvider;

    public ManualThumbnailService(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string workspacePath,
        string ffmpegPath)
        : this(
            artifactStore,
            GetProcessDelegate(processRunner),
            GetBinaryVerificationDelegate(binaryVerifier),
            GetRegisteredVerificationDelegate(artifactStore, binaryVerifier),
            GetPromotionDelegate(artifactStore),
            workspacePath,
            ffmpegPath,
            TimeProvider.System)
    {
    }

    internal ManualThumbnailService(
        ArtifactStore artifactStore,
        Func<
            ProcessRunRequest,
            CancellationToken,
            Task<ProcessRunResult>> runProcessAsync,
        Func<
            BinaryVerificationRequest,
            CancellationToken,
            Task<BinaryVerificationResult>> verifyBinaryAsync,
        Func<
            EntityId<ArtifactIdTag>,
            CancellationToken,
            Task<BinaryVerificationResult>> verifyRegisteredAsync,
        Func<
            ArtifactPromotionRequest,
            CancellationToken,
            Task<StoredArtifact>> promoteAsync,
        string workspacePath,
        string ffmpegPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);
        ArgumentNullException.ThrowIfNull(promoteAsync);

        _artifactStore = artifactStore;
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
        _verifyRegisteredAsync = verifyRegisteredAsync;
        _promoteAsync = promoteAsync;
        _workspacePath = ManualThumbnailPath.NormalizeExistingWorkspace(
            workspacePath,
            nameof(workspacePath));
        _ffmpegPath = ManualThumbnailPath.NormalizeLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ManualThumbnailResult> GenerateAsync(
        ManualThumbnailRequest request,
        ManualThumbnailContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);

        ValidatedThumbnailBinding binding = ValidateBinding(request, context);
        StoredArtifact registeredSource = GetRegisteredSource(
            context.SourceArtifact);
        string sourcePath = ResolveRegisteredPath(registeredSource);
        await VerifyRegisteredExactAsync(
            registeredSource,
            ManualThumbnailFailureReason.RegisteredSourceInvalid,
            reconciliationRequired: true,
            cancellationToken).ConfigureAwait(false);

        using FileStream heldSource = OpenHeldSource(sourcePath);
        HeldFileSnapshot sourceSnapshot = await CaptureHeldFileAsync(
            heldSource,
            registeredSource.ContentDigest,
            registeredSource.ByteSize,
            cancellationToken).ConfigureAwait(false);

        StoredArtifact? existing = GetIdempotentOutput(
            request,
            binding.ExpectedStorageReference);
        if (existing is not null)
        {
            await VerifyHeldSourceAfterWorkAsync(
                heldSource,
                sourceSnapshot,
                registeredSource,
                cancellationToken).ConfigureAwait(false);
            await VerifyRegisteredOutputAsync(
                existing,
                binding,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                request,
                existing,
                binding,
                wasAlreadyRegistered: true);
        }

        await VerifyFfmpegAsync(cancellationToken).ConfigureAwait(false);
        await VerifyHeldSourceAfterWorkAsync(
            heldSource,
            sourceSnapshot,
            registeredSource,
            cancellationToken).ConfigureAwait(false);

        ThumbnailStaging staging = PrepareStaging(
            request,
            cancellationToken);
        bool promoted = false;
        try
        {
            ProcessRunRequest processRequest = CreateProcessRequest(
                _ffmpegPath,
                sourcePath,
                staging.OutputPath,
                context.SourcePtsIndex.StreamIndex,
                request.Selection.SourceFrameSequence,
                context.RenderProfile.Width,
                context.RenderProfile.Height,
                binding.VideoRegion.Rect,
                timeout);

            ProcessRunResult processResult;
            try
            {
                processResult = await RunProcessAsync(
                    processRequest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await VerifyHeldSourceAfterWorkAsync(
                    heldSource,
                    sourceSnapshot,
                    registeredSource,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await VerifyHeldSourceAfterWorkAsync(
                heldSource,
                sourceSnapshot,
                registeredSource,
                cancellationToken).ConfigureAwait(false);
            ValidateCleanProcessResult(processResult);
            FileSnapshot outputSnapshot = await ValidatePngAsync(
                _workspacePath,
                staging.OutputPath,
                context.RenderProfile.Width,
                context.RenderProfile.Height,
                cancellationToken).ConfigureAwait(false);
            await VerifyHeldSourceAfterWorkAsync(
                heldSource,
                sourceSnapshot,
                registeredSource,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset createdAt = _timeProvider
                .GetUtcNow()
                .ToUniversalTime();
            var promotion = new ArtifactPromotionRequest(
                request.OutputArtifactId,
                request.Source.ProjectId,
                ArtifactKind.Render,
                staging.OutputPath,
                binding.ExpectedRelativePath,
                outputSnapshot.Digest,
                outputSnapshot.ByteSize,
                createdAt);
            StoredArtifact artifact = await PromoteCommittedAsync(promotion)
                .ConfigureAwait(false);
            promoted = true;
            ValidatePromotedMetadata(
                artifact,
                request,
                binding,
                outputSnapshot,
                createdAt);

            if (!TryCleanupOwnedStaging(staging))
            {
                throw Failure(
                    ManualThumbnailFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            await VerifyHeldSourceAfterWorkAsync(
                heldSource,
                sourceSnapshot,
                registeredSource,
                CancellationToken.None).ConfigureAwait(false);
            await VerifyRegisteredOutputAsync(
                artifact,
                binding,
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(
                request,
                artifact,
                binding,
                wasAlreadyRegistered: false);
        }
        catch (Exception exception)
        {
            bool cleaned = TryCleanupOwnedStaging(staging);
            if (!cleaned)
            {
                throw Failure(
                    ManualThumbnailFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is ManualThumbnailException)
            {
                throw;
            }

            if (promoted)
            {
                throw Failure(
                    ManualThumbnailFailureReason.PromotionFailed,
                    reconciliationRequired: true);
            }

            throw Failure(ManualThumbnailFailureReason.StagingUnavailable);
        }
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        int videoStreamIndex,
        int sourceFrameSequence,
        int outputWidth,
        int outputHeight,
        PixelRect videoRegion,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(videoRegion);
        if (videoStreamIndex < 0 || sourceFrameSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceFrameSequence),
                "Thumbnail stream and frame sequences must be nonnegative.");
        }

        if (outputWidth <= 0 ||
            outputHeight <= 0 ||
            videoRegion.X < 0 ||
            videoRegion.Y < 0 ||
            videoRegion.Width <= 0 ||
            videoRegion.Height <= 0 ||
            (long)videoRegion.X + videoRegion.Width > outputWidth ||
            (long)videoRegion.Y + videoRegion.Height > outputHeight)
        {
            throw new ArgumentException(
                "The thumbnail output geometry is invalid.",
                nameof(videoRegion));
        }

        ValidateTimeout(timeout);
        string executable = ManualThumbnailPath.NormalizeLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string source = ManualThumbnailPath.NormalizeLocalAbsolutePath(
            sourcePath,
            nameof(sourcePath));
        string output = ManualThumbnailPath.NormalizeLocalAbsolutePath(
            outputPath,
            nameof(outputPath));
        if (!string.Equals(
                Path.GetExtension(output),
                ".png",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The thumbnail output path must use the PNG extension.",
                nameof(outputPath));
        }

        string workingDirectory = Path.GetDirectoryName(output)
            ?? throw new ArgumentException(
                "The thumbnail output path must have a parent directory.",
                nameof(outputPath));
        string filter = string.Concat(
            "select=eq(n\\,",
            sourceFrameSequence.ToString(CultureInfo.InvariantCulture),
            "),scale=",
            videoRegion.Width.ToString(CultureInfo.InvariantCulture),
            ":",
            videoRegion.Height.ToString(CultureInfo.InvariantCulture),
            ":force_original_aspect_ratio=decrease:flags=bilinear,pad=",
            videoRegion.Width.ToString(CultureInfo.InvariantCulture),
            ":",
            videoRegion.Height.ToString(CultureInfo.InvariantCulture),
            ":(ow-iw)/2:(oh-ih)/2:color=black,pad=",
            outputWidth.ToString(CultureInfo.InvariantCulture),
            ":",
            outputHeight.ToString(CultureInfo.InvariantCulture),
            ":",
            videoRegion.X.ToString(CultureInfo.InvariantCulture),
            ":",
            videoRegion.Y.ToString(CultureInfo.InvariantCulture),
            ":color=black,setsar=1");

        return new ProcessRunRequest(
            executable,
            [
                "-hide_banner",
                "-nostdin",
                "-v",
                "error",
                "-i",
                source,
                "-map",
                string.Concat(
                    "0:",
                    videoStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-vf",
                filter,
                "-frames:v",
                "1",
                "-an",
                "-map_metadata",
                "-1",
                "-c:v",
                "png",
                "-f",
                "image2",
                "-n",
                output,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    private ValidatedThumbnailBinding ValidateBinding(
        ManualThumbnailRequest request,
        ManualThumbnailContext context)
    {
        if (request.Source.ProjectId != context.Project.Id ||
            request.OutputArtifactId == context.SourceArtifact.Id)
        {
            throw Failure(ManualThumbnailFailureReason.InputBindingMismatch);
        }

        Asset? sourceAsset = context.Project.Assets.SingleOrDefault(asset =>
            asset.Id == request.Source.SourceAssetId);
        if (sourceAsset is null ||
            request.Source.SourceArtifactId != context.SourceArtifact.Id ||
            request.Source.SourceContentDigest != sourceAsset.ContentDigest ||
            request.Source.SourceByteSize != sourceAsset.ByteSize ||
            request.Source.SourceRightsDigest !=
                ManualThumbnailHash.ComputeRightsDigest(sourceAsset.Rights) ||
            request.Source.ObservedRightsStatus != sourceAsset.Rights.Status ||
            request.Source.ObservedExternalPublicationAllowed !=
                sourceAsset.Rights.ExternalPublicationAllowed ||
            context.SourceArtifact.ProjectId != context.Project.Id ||
            context.SourceArtifact.Kind != ArtifactKind.Original ||
            context.SourceArtifact.ContentDigest != sourceAsset.ContentDigest ||
            context.SourceArtifact.ByteSize != sourceAsset.ByteSize)
        {
            throw Failure(ManualThumbnailFailureReason.SourceBindingStale);
        }

        if (sourceAsset.Rights.Status != RightsStatus.Approved ||
            !sourceAsset.Rights.ExternalPublicationAllowed)
        {
            throw Failure(
                ManualThumbnailFailureReason.SourceRightsNotApproved);
        }

        ContentDigest sourcePtsIndexDigest = ManualThumbnailHash
            .ComputeCanonicalJsonDigest(context.SourcePtsIndex.CanonicalJson);
        if (request.Source.SourcePtsIndexDigest != sourcePtsIndexDigest ||
            context.SourcePtsIndex.CandidateId != sourceAsset.Id ||
            context.SourcePtsIndex.ProjectId != context.Project.Id ||
            !string.Equals(
                context.SourcePtsIndex.SourceFileName,
                sourceAsset.FileName,
                StringComparison.Ordinal) ||
            context.SourcePtsIndex.ContentDigest != sourceAsset.ContentDigest ||
            context.SourcePtsIndex.ByteSize != sourceAsset.ByteSize)
        {
            throw Failure(ManualThumbnailFailureReason.SourceBindingStale);
        }

        ContentDigest frameMapDigest = ManualThumbnailHash
            .ComputeCanonicalJsonDigest(context.ProxyFrameMap.CanonicalJson);
        if (request.Source.ProxyFrameMapDigest != frameMapDigest ||
            context.ProxyFrameMap.SourceAssetId != sourceAsset.Id ||
            context.ProxyFrameMap.ProjectId != context.Project.Id ||
            context.ProxyFrameMap.SourceContentDigest != sourceAsset.ContentDigest ||
            context.ProxyFrameMap.SourcePtsIndexDigest != sourcePtsIndexDigest ||
            context.ProxyFrameMap.SourceFrameCount !=
                context.SourcePtsIndex.Entries.Count ||
            context.ProxyFrameMap.SourcePresentationStart !=
                context.SourcePtsIndex.PresentationStart ||
            context.ProxyFrameMap.SourcePresentationEnd !=
                context.SourcePtsIndex.PresentationEnd)
        {
            throw Failure(ManualThumbnailFailureReason.ProxyMappingStale);
        }

        RationalTime? selectedPts = request.Selection.SourcePresentationTime;
        if (selectedPts is null ||
            (uint)request.Selection.SourceFrameSequence >=
                (uint)context.SourcePtsIndex.Entries.Count)
        {
            throw Failure(ManualThumbnailFailureReason.SelectedFrameInvalid);
        }

        PtsIndexEntry selectedSource = context.SourcePtsIndex.Entries[
            request.Selection.SourceFrameSequence];
        if (selectedSource.PresentationTime != selectedPts.Value)
        {
            throw Failure(ManualThumbnailFailureReason.SelectedFrameInvalid);
        }

        ProxyFrameMapEntry mapped;
        try
        {
            mapped = context.ProxyFrameMap.MapProxyFrameToSource(
                request.Selection.ProxyFrameSequence);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(ManualThumbnailFailureReason.ProxyMappingStale);
        }

        if (mapped.SourceFrameSequence != request.Selection.SourceFrameSequence ||
            mapped.SourcePresentationTime != selectedPts.Value)
        {
            throw Failure(ManualThumbnailFailureReason.ProxyMappingStale);
        }

        ApprovedThumbnailTemplateSnapshot approval =
            context.TemplateApproval
            ?? throw Failure(ManualThumbnailFailureReason.TemplateNotApproved);
        BrandTemplateVersion version = context.TemplateVersion;
        AppliedBrandTemplate template = context.Template;
        TemplateReference reference = version.Reference;
        if (version.Kind != BrandTemplateKind.Thumbnail ||
            approval.TemplateVersion.Kind != BrandTemplateKind.Thumbnail ||
            approval.TemplateVersion.SeriesId != version.SeriesId ||
            !approval.TemplateVersion.Reference.Equals(reference) ||
            request.Template.ApprovalId != approval.Id ||
            request.Template.ApprovalDigest != approval.ApprovalDigest ||
            request.Template.SeriesId != version.SeriesId ||
            request.Template.TemplateId != reference.Id ||
            request.Template.TemplateVersionDigest != reference.VersionDigest ||
            template.SeriesId != version.SeriesId ||
            template.Kind != BrandTemplateKind.Thumbnail ||
            !template.Reference.Equals(reference) ||
            !IsAppliedFromVersion(version, template))
        {
            throw Failure(ManualThumbnailFailureReason.TemplateBindingStale);
        }

        if (version.ParameterDefinitions.Count != 0 ||
            template.Parameters.Count != 0)
        {
            throw Failure(ManualThumbnailFailureReason.TemplateUnsupported);
        }

        RenderProfile profile = context.RenderProfile;
        if (request.Template.RenderProfileId != profile.Id ||
            request.Template.RenderProfileDigest != profile.ProfileDigest ||
            profile.TemplateId != reference.Id ||
            profile.TemplateVersionDigest != reference.VersionDigest)
        {
            throw Failure(ManualThumbnailFailureReason.ProfileBindingStale);
        }

        if (request.Template.LayoutSpecId != context.LayoutSpec.Id ||
            request.Template.LayoutSpecDigest != context.LayoutSpecDigest ||
            profile.LayoutSpecId != context.LayoutSpec.Id)
        {
            throw Failure(ManualThumbnailFailureReason.LayoutBindingStale);
        }

        ResolvedLayout layout;
        try
        {
            layout = LayoutResolver.Resolve(
                context.LayoutSpec,
                profile.Width,
                profile.Height);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException)
        {
            throw Failure(ManualThumbnailFailureReason.LayoutBindingStale);
        }

        ResolvedLayoutRegion[] videoRegions = layout.Regions
            .Where(static region => region.Role == LayoutRegionRole.Video)
            .ToArray();
        if (videoRegions.Length != 1 || layout.Regions.Count != 1)
        {
            throw Failure(ManualThumbnailFailureReason.TemplateUnsupported);
        }

        ContentDigest bindingDigest = ManualThumbnailHash.ComputeBindingDigest(
            request,
            sourceAsset,
            approval,
            context.SourcePtsIndex.StreamIndex,
            videoRegions[0].Rect,
            profile.Width,
            profile.Height);
        string relativePath = string.Concat(
            "thumbnails/",
            request.Id.ToString(),
            "/",
            bindingDigest.Hex,
            ".png");
        string storageReference = string.Concat(
            "projects/",
            request.Source.ProjectId.ToString(),
            "/renders/",
            relativePath);
        return new ValidatedThumbnailBinding(
            sourceAsset,
            approval,
            videoRegions[0],
            context.SourcePtsIndex.StreamIndex,
            profile.Width,
            profile.Height,
            bindingDigest,
            relativePath,
            storageReference);
    }

    private StoredArtifact GetRegisteredSource(StoredArtifact expected)
    {
        try
        {
            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(
                    ManualThumbnailFailureReason.RegisteredSourceInvalid,
                    reconciliationRequired: true);
            if (!HasSameMetadata(expected, current))
            {
                throw Failure(
                    ManualThumbnailFailureReason.RegisteredSourceInvalid,
                    reconciliationRequired: true);
            }

            return current;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }
    }

    private StoredArtifact? GetIdempotentOutput(
        ManualThumbnailRequest request,
        string expectedStorageReference)
    {
        try
        {
            StoredArtifact? byId = _artifactStore.GetArtifact(
                request.OutputArtifactId);
            if (byId is not null)
            {
                if (byId.ProjectId != request.Source.ProjectId ||
                    byId.Kind != ArtifactKind.Render ||
                    !string.Equals(
                        byId.StorageReference,
                        expectedStorageReference,
                        StringComparison.Ordinal))
                {
                    throw Failure(
                        ManualThumbnailFailureReason.DuplicateRequest);
                }

                return byId;
            }

            StoredArtifact? byPath = _artifactStore
                .ListProjectArtifacts(request.Source.ProjectId)
                .SingleOrDefault(artifact => string.Equals(
                    artifact.StorageReference,
                    expectedStorageReference,
                    StringComparison.Ordinal));
            if (byPath is not null)
            {
                throw Failure(ManualThumbnailFailureReason.DuplicateRequest);
            }

            return null;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private async Task VerifyFfmpegAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new BinaryVerificationRequest(
                "ffmpeg",
                _ffmpegPath,
                FixedFfmpegDigest,
                FfmpegCompiler.FixedFfmpegByteSize);
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!IsExactVerification(result, request))
            {
                throw Failure(
                    ManualThumbnailFailureReason.ToolVerificationFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.ToolVerificationFailed);
        }
    }

    private async Task VerifyRegisteredExactAsync(
        StoredArtifact expected,
        ManualThumbnailFailureReason failureReason,
        bool reconciliationRequired,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyRegisteredAsync(
                expected.Id,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified ||
                result.Reason is not null ||
                result.ExpectedDigest != expected.ContentDigest ||
                result.ActualDigest != expected.ContentDigest ||
                result.ExpectedByteSize != expected.ByteSize ||
                result.ActualByteSize != expected.ByteSize)
            {
                throw Failure(failureReason, reconciliationRequired);
            }

            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(failureReason, reconciliationRequired);
            if (!HasSameMetadata(expected, current))
            {
                throw Failure(failureReason, reconciliationRequired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason, reconciliationRequired);
        }
    }

    private static bool IsExactVerification(
        BinaryVerificationResult result,
        BinaryVerificationRequest request) =>
        result.IsVerified &&
        result.Reason is null &&
        string.Equals(
            result.ComponentIdentity,
            request.ComponentIdentity,
            StringComparison.Ordinal) &&
        result.ExpectedDigest == request.ExpectedDigest &&
        result.ActualDigest == request.ExpectedDigest &&
        result.ExpectedByteSize == request.ExpectedByteSize &&
        result.ActualByteSize == request.ExpectedByteSize;

    private async Task<ProcessRunResult> RunProcessAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runProcessAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(ManualThumbnailFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(ManualThumbnailFailureReason.ProcessFailed);
        }
    }

    private static void ValidateCleanProcessResult(ProcessRunResult result)
    {
        if (result.ExitCode != 0 ||
            result.Truncated ||
            result.StandardOutput is null ||
            result.StandardOutput.Length != 0 ||
            result.StandardError is null ||
            result.StandardError.Length != 0 ||
            result.Duration < TimeSpan.Zero)
        {
            throw Failure(ManualThumbnailFailureReason.ProcessFailed);
        }
    }

    private async Task<StoredArtifact> PromoteCommittedAsync(
        ArtifactPromotionRequest request)
    {
        try
        {
            return await _promoteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private ThumbnailStaging PrepareStaging(
        ManualThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(Path.Combine(
            ProjectFolders.GetPath(_workspacePath, request.Source.ProjectId),
            "staging",
            "manual-thumbnails"));
        try
        {
            ManualThumbnailPath.EnsureDirectoryChain(
                _workspacePath,
                root,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(ManualThumbnailFailureReason.StagingUnavailable);
        }

        string directory = Path.Combine(
            root,
            string.Concat(
                request.Id.ToString(),
                "-",
                request.OutputArtifactId.ToString()));
        if (ManualThumbnailPath.EntryExistsConservatively(directory))
        {
            throw Failure(ManualThumbnailFailureReason.StagingConflict);
        }

        string claim = Path.Combine(
            root,
            string.Concat(
                ".",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ".claim"));
        bool claimCreated = false;
        bool directoryMoved = false;
        try
        {
            _ = Directory.CreateDirectory(claim);
            claimCreated = true;
            ManualThumbnailPath.ValidateExistingPathChain(
                _workspacePath,
                claim,
                targetIsDirectory: true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(claim, directory);
            claimCreated = false;
            directoryMoved = true;
            ManualThumbnailPath.ValidateExistingPathChain(
                _workspacePath,
                directory,
                targetIsDirectory: true);
            return new ThumbnailStaging(
                directory,
                Path.Combine(directory, "thumbnail.partial.png"));
        }
        catch (Exception exception)
        {
            bool cleaned = directoryMoved
                ? ManualThumbnailPath.TryDeleteOwnedEmptyDirectory(
                    _workspacePath,
                    directory)
                : !claimCreated || ManualThumbnailPath.TryDeleteOwnedEmptyDirectory(
                    _workspacePath,
                    claim);
            if (!cleaned)
            {
                throw Failure(
                    ManualThumbnailFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw Failure(
                ManualThumbnailPath.EntryExistsConservatively(directory)
                    ? ManualThumbnailFailureReason.StagingConflict
                    : ManualThumbnailFailureReason.StagingUnavailable);
        }
    }

    private bool TryCleanupOwnedStaging(ThumbnailStaging staging)
    {
        try
        {
            if (ManualThumbnailPath.EntryExistsConservatively(
                    staging.OutputPath))
            {
                ManualThumbnailPath.ValidateExistingPathChain(
                    _workspacePath,
                    staging.OutputPath,
                    targetIsDirectory: false);
                File.Delete(staging.OutputPath);
                if (ManualThumbnailPath.EntryExistsConservatively(
                        staging.OutputPath))
                {
                    return false;
                }
            }

            return ManualThumbnailPath.TryDeleteOwnedEmptyDirectory(
                _workspacePath,
                staging.DirectoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task VerifyRegisteredOutputAsync(
        StoredArtifact artifact,
        ValidatedThumbnailBinding binding,
        CancellationToken cancellationToken)
    {
        try
        {
            if (artifact.Kind != ArtifactKind.Render ||
                !string.Equals(
                    artifact.StorageReference,
                    binding.ExpectedStorageReference,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    ManualThumbnailFailureReason.RegisteredOutputInvalid,
                    reconciliationRequired: true);
            }

            await VerifyRegisteredExactAsync(
                artifact,
                ManualThumbnailFailureReason.RegisteredOutputInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
            string path = ResolveRegisteredPath(artifact);
            FileSnapshot png = await ValidatePngAsync(
                _workspacePath,
                path,
                binding.OutputWidth,
                binding.OutputHeight,
                cancellationToken).ConfigureAwait(false);
            if (png.Digest != artifact.ContentDigest ||
                png.ByteSize != artifact.ByteSize)
            {
                throw Failure(
                    ManualThumbnailFailureReason.RegisteredOutputInvalid,
                    reconciliationRequired: true);
            }

            StoredArtifact current = _artifactStore.GetArtifact(artifact.Id)
                ?? throw Failure(
                    ManualThumbnailFailureReason.RegisteredOutputInvalid,
                    reconciliationRequired: true);
            if (!HasSameMetadata(artifact, current))
            {
                throw Failure(
                    ManualThumbnailFailureReason.RegisteredOutputInvalid,
                    reconciliationRequired: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManualThumbnailException exception)
            when (exception.Reason ==
                ManualThumbnailFailureReason.RegisteredOutputInvalid)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredOutputInvalid,
                reconciliationRequired: true);
        }
    }

    private string ResolveRegisteredPath(StoredArtifact artifact)
    {
        try
        {
            string path = ManualThumbnailPath.ResolvePortableRelativePath(
                _workspacePath,
                artifact.StorageReference);
            ManualThumbnailPath.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            return path;
        }
        catch (Exception)
        {
            throw Failure(
                artifact.Kind == ArtifactKind.Original
                    ? ManualThumbnailFailureReason.RegisteredSourceInvalid
                    : ManualThumbnailFailureReason.RegisteredOutputInvalid,
                reconciliationRequired: true);
        }
    }

    private static FileStream OpenHeldSource(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception)
            when (ManualThumbnailPath.IsFileSystemFailure(exception))
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }
    }

    private async Task VerifyHeldSourceAfterWorkAsync(
        FileStream stream,
        HeldFileSnapshot snapshot,
        StoredArtifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidateHeldFileAsync(
                stream,
                snapshot,
                artifact.ContentDigest,
                artifact.ByteSize,
                cancellationToken).ConfigureAwait(false);
            await VerifyRegisteredExactAsync(
                artifact,
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }
    }

    private static async Task<HeldFileSnapshot> CaptureHeldFileAsync(
        FileStream stream,
        ContentDigest expectedDigest,
        long expectedByteSize,
        CancellationToken cancellationToken)
    {
        try
        {
            FileAttributes attributes = ValidateOpenedRegularFile(stream);
            DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(
                stream.SafeFileHandle);
            var snapshot = new HeldFileSnapshot(
                expectedByteSize,
                attributes,
                lastWriteTimeUtc);
            await ValidateHeldFileAsync(
                stream,
                snapshot,
                expectedDigest,
                expectedByteSize,
                cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception exception)
            when (ManualThumbnailPath.IsFileSystemFailure(exception) ||
                exception is CryptographicException)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }
    }

    private static async Task ValidateHeldFileAsync(
        FileStream stream,
        HeldFileSnapshot snapshot,
        ContentDigest expectedDigest,
        long expectedByteSize,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes = ValidateOpenedRegularFile(stream);
        if (stream.Length != expectedByteSize ||
            snapshot.ByteSize != expectedByteSize ||
            attributes != snapshot.Attributes ||
            File.GetLastWriteTimeUtc(stream.SafeFileHandle) !=
                snapshot.LastWriteTimeUtc)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }

        stream.Position = 0;
        byte[] digest = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        if (ContentDigest.FromBytes(digest) != expectedDigest ||
            stream.Length != expectedByteSize ||
            ValidateOpenedRegularFile(stream) != snapshot.Attributes ||
            File.GetLastWriteTimeUtc(stream.SafeFileHandle) !=
                snapshot.LastWriteTimeUtc)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredSourceInvalid,
                reconciliationRequired: true);
        }
    }

    private static FileAttributes ValidateOpenedRegularFile(FileStream stream)
    {
        FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
        if ((attributes &
                (FileAttributes.Directory |
                    FileAttributes.Device |
                    FileAttributes.ReparsePoint)) != 0 ||
            !stream.CanRead ||
            !stream.CanSeek)
        {
            throw new InvalidDataException("The opened file is not regular.");
        }

        return attributes;
    }

    private static async Task<FileSnapshot> ValidatePngAsync(
        string workspacePath,
        string path,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            ManualThumbnailPath.ValidateExistingPathChain(
                workspacePath,
                path,
                targetIsDirectory: false);
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _ = ValidateOpenedRegularFile(stream);
            if (stream.Length < 57)
            {
                throw Failure(ManualThumbnailFailureReason.OutputInvalid);
            }

            byte[] signatureBytes = new byte[8];
            await stream.ReadExactlyAsync(signatureBytes, cancellationToken)
                .ConfigureAwait(false);
            ReadOnlySpan<byte> signature =
                [137, 80, 78, 71, 13, 10, 26, 10];
            if (!signatureBytes.AsSpan().SequenceEqual(signature))
            {
                throw Failure(ManualThumbnailFailureReason.OutputInvalid);
            }

            bool seenHeader = false;
            bool seenPalette = false;
            bool seenImageData = false;
            bool imageDataEnded = false;
            bool seenEnd = false;
            int decodedChannelCount = 0;
            long expectedDecodedByteCount = 0;
            long maximumCompressedByteCount = 0;
            using var compressedImageData = new MemoryStream();
            byte[] chunkHeader = new byte[8];
            byte[] crcBytes = new byte[4];
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
            try
            {
                while (!seenEnd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await stream.ReadExactlyAsync(
                        chunkHeader,
                        cancellationToken).ConfigureAwait(false);
                    uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(
                        chunkHeader.AsSpan(0, 4));
                    ReadOnlySpan<byte> chunkType = chunkHeader.AsSpan(4, 4);
                    if (!chunkType.ToArray().All(static value =>
                            value is >= (byte)'A' and <= (byte)'Z' or
                                >= (byte)'a' and <= (byte)'z') ||
                        chunkType[2] is not (>= (byte)'A' and <= (byte)'Z') ||
                        (long)chunkLength + sizeof(uint) >
                            stream.Length - stream.Position)
                    {
                        throw Failure(
                            ManualThumbnailFailureReason.OutputInvalid);
                    }

                    bool isHeader = chunkType.SequenceEqual("IHDR"u8);
                    bool isPalette = chunkType.SequenceEqual("PLTE"u8);
                    bool isImageData = chunkType.SequenceEqual("IDAT"u8);
                    bool isEnd = chunkType.SequenceEqual("IEND"u8);
                    if ((!seenHeader && !isHeader) ||
                        (seenHeader && isHeader) ||
                        (isHeader && chunkLength != 13) ||
                        (isPalette &&
                            (seenPalette ||
                                seenImageData ||
                                chunkLength == 0 ||
                                chunkLength > 768 ||
                                chunkLength % 3 != 0)) ||
                        (isEnd && chunkLength != 0) ||
                        (isImageData && (chunkLength == 0 || imageDataEnded)) ||
                        (seenImageData && !isImageData && !isEnd &&
                            chunkType[0] is >= (byte)'A' and <= (byte)'Z') ||
                        (!isHeader && !isImageData && !isEnd &&
                            chunkType[0] is >= (byte)'A' and <= (byte)'Z' &&
                            !chunkType.SequenceEqual("PLTE"u8)))
                    {
                        throw Failure(
                            ManualThumbnailFailureReason.OutputInvalid);
                    }

                    uint crc = 0xffffffffu;
                    crc = UpdateCrc32(crc, chunkType);
                    byte[]? headerData = isHeader ? new byte[13] : null;
                    long remaining = chunkLength;
                    int headerOffset = 0;
                    while (remaining > 0)
                    {
                        int requested = checked((int)Math.Min(
                            buffer.Length,
                            remaining));
                        await stream.ReadExactlyAsync(
                            buffer.AsMemory(0, requested),
                            cancellationToken).ConfigureAwait(false);
                        crc = UpdateCrc32(
                            crc,
                            buffer.AsSpan(0, requested));
                        if (headerData is not null)
                        {
                            buffer.AsSpan(0, requested).CopyTo(
                                headerData.AsSpan(headerOffset));
                            headerOffset += requested;
                        }
                        else if (isImageData)
                        {
                            if (compressedImageData.Length + requested >
                                maximumCompressedByteCount)
                            {
                                throw Failure(
                                    ManualThumbnailFailureReason.OutputInvalid);
                            }

                            compressedImageData.Write(buffer, 0, requested);
                        }

                        remaining -= requested;
                    }

                    await stream.ReadExactlyAsync(
                        crcBytes,
                        cancellationToken).ConfigureAwait(false);
                    uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                        crcBytes);
                    if ((crc ^ 0xffffffffu) != expectedCrc)
                    {
                        throw Failure(
                            ManualThumbnailFailureReason.OutputInvalid);
                    }

                    if (headerData is not null)
                    {
                        if (BinaryPrimitives.ReadUInt32BigEndian(
                                headerData.AsSpan(0, 4)) !=
                                (uint)expectedWidth ||
                            BinaryPrimitives.ReadUInt32BigEndian(
                                headerData.AsSpan(4, 4)) !=
                                (uint)expectedHeight ||
                            headerData[8] != 8 ||
                            headerData[9] is not (2 or 6) ||
                            headerData[10] != 0 ||
                            headerData[11] != 0 ||
                            headerData[12] != 0)
                        {
                            throw Failure(
                                ManualThumbnailFailureReason.OutputInvalid);
                        }

                        decodedChannelCount = headerData[9] == 2 ? 3 : 4;
                        expectedDecodedByteCount = checked(
                            (long)expectedHeight *
                            (1L + checked(
                                (long)expectedWidth *
                                decodedChannelCount)));
                        maximumCompressedByteCount = GetMaximumZlibByteCount(
                            expectedDecodedByteCount);
                        seenHeader = true;
                    }
                    else if (isImageData)
                    {
                        seenImageData = true;
                    }
                    else if (isPalette)
                    {
                        seenPalette = true;
                    }
                    else if (isEnd)
                    {
                        seenEnd = true;
                    }

                    if (seenImageData && !isImageData)
                    {
                        imageDataEnded = true;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (!seenHeader ||
                !seenImageData ||
                !seenEnd ||
                decodedChannelCount == 0 ||
                stream.Position != stream.Length)
            {
                throw Failure(ManualThumbnailFailureReason.OutputInvalid);
            }

            ValidatePngImageData(
                compressedImageData,
                expectedWidth,
                expectedHeight,
                decodedChannelCount,
                expectedDecodedByteCount);
            stream.Position = 0;
            byte[] digestBytes = await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            _ = ValidateOpenedRegularFile(stream);
            return new FileSnapshot(
                stream.Length,
                ContentDigest.FromBytes(digestBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManualThumbnailException)
        {
            throw;
        }
        catch (Exception exception)
            when (ManualThumbnailPath.IsFileSystemFailure(exception) ||
                exception is InvalidDataException or CryptographicException or
                    OverflowException)
        {
            throw Failure(ManualThumbnailFailureReason.OutputInvalid);
        }
    }

    private static void ValidatePngImageData(
        MemoryStream compressedImageData,
        int width,
        int height,
        int channelCount,
        long expectedByteCount)
    {
        compressedImageData.Position = 0;
        using var zlib = new ZLibStream(
            compressedImageData,
            CompressionMode.Decompress,
            leaveOpen: true);
        int rowByteCount = checked(1 + checked(width * channelCount));
        byte[] row = ArrayPool<byte>.Shared.Rent(rowByteCount);
        try
        {
            long decodedByteCount = 0;
            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            {
                zlib.ReadExactly(row.AsSpan(0, rowByteCount));
                if (row[0] > 4)
                {
                    throw Failure(
                        ManualThumbnailFailureReason.OutputInvalid);
                }

                decodedByteCount = checked(decodedByteCount + rowByteCount);
            }

            if (decodedByteCount != expectedByteCount || zlib.ReadByte() != -1)
            {
                throw Failure(ManualThumbnailFailureReason.OutputInvalid);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row, clearArray: true);
        }
    }

    private static long GetMaximumZlibByteCount(long sourceByteCount)
    {
        if (sourceByteCount <= 0)
        {
            throw Failure(ManualThumbnailFailureReason.OutputInvalid);
        }

        return checked(
            sourceByteCount +
            (sourceByteCount >> 12) +
            (sourceByteCount >> 14) +
            (sourceByteCount >> 25) +
            13);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^
                    (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc;
    }

    private static void ValidatePromotedMetadata(
        StoredArtifact artifact,
        ManualThumbnailRequest request,
        ValidatedThumbnailBinding binding,
        FileSnapshot output,
        DateTimeOffset createdAt)
    {
        if (artifact.Id != request.OutputArtifactId ||
            artifact.ProjectId != request.Source.ProjectId ||
            artifact.Kind != ArtifactKind.Render ||
            !string.Equals(
                artifact.StorageReference,
                binding.ExpectedStorageReference,
                StringComparison.Ordinal) ||
            artifact.ContentDigest != output.Digest ||
            artifact.ByteSize != output.ByteSize ||
            artifact.CreatedAt != createdAt)
        {
            throw Failure(
                ManualThumbnailFailureReason.RegisteredOutputInvalid,
                reconciliationRequired: true);
        }
    }

    private static ManualThumbnailResult CreateResult(
        ManualThumbnailRequest request,
        StoredArtifact artifact,
        ValidatedThumbnailBinding binding,
        bool wasAlreadyRegistered) =>
        new(
            request,
            artifact,
            binding.SourceAsset,
            binding.SourceVideoStreamIndex,
            binding.Approval,
            OriginRef.FromAsset(binding.SourceAsset.Id),
            binding.BindingDigest,
            binding.OutputWidth,
            binding.OutputHeight,
            wasAlreadyRegistered);

    private static bool IsAppliedFromVersion(
        BrandTemplateVersion version,
        AppliedBrandTemplate applied)
    {
        if (version.ParameterDefinitions.Count != applied.Parameters.Count)
        {
            return false;
        }

        for (int index = 0; index < version.ParameterDefinitions.Count; index++)
        {
            TemplateParameterDefinition definition =
                version.ParameterDefinitions[index];
            TemplateParameterAssignment assignment = applied.Parameters[index];
            if (!string.Equals(
                    definition.Name,
                    assignment.Name,
                    StringComparison.Ordinal) ||
                assignment.Value.Kind != definition.DefaultValue.Kind)
            {
                return false;
            }

            if (assignment.Value == definition.DefaultValue)
            {
                continue;
            }

            if (!definition.EditorOverrideAllowed ||
                definition.EditorValueAllowlist is not null &&
                !definition.EditorValueAllowlist.Contains(assignment.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameMetadata(
        StoredArtifact expected,
        StoredArtifact actual) =>
        expected.Id == actual.Id &&
        expected.ProjectId == actual.ProjectId &&
        expected.Kind == actual.Kind &&
        string.Equals(
            expected.StorageReference,
            actual.StorageReference,
            StringComparison.Ordinal) &&
        expected.ContentDigest == actual.ContentDigest &&
        expected.ByteSize == actual.ByteSize &&
        expected.CreatedAt == actual.CreatedAt;

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The thumbnail rendering timeout must be positive and supported by the runtime timer.");
        }
    }

    private static Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> GetProcessDelegate(ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> GetBinaryVerificationDelegate(
            BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return binaryVerifier.VerifyAsync;
    }

    private static Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> GetRegisteredVerificationDelegate(
            ArtifactStore artifactStore,
            BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return (artifactId, cancellationToken) =>
            artifactStore.VerifyRegisteredAsync(
                artifactId,
                binaryVerifier,
                cancellationToken);
    }

    private static Func<
        ArtifactPromotionRequest,
        CancellationToken,
        Task<StoredArtifact>> GetPromotionDelegate(ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.PromoteAsync;
    }

    private static ManualThumbnailException Failure(
        ManualThumbnailFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record ValidatedThumbnailBinding(
        Asset SourceAsset,
        ApprovedThumbnailTemplateSnapshot Approval,
        ResolvedLayoutRegion VideoRegion,
        int SourceVideoStreamIndex,
        int OutputWidth,
        int OutputHeight,
        ContentDigest BindingDigest,
        string ExpectedRelativePath,
        string ExpectedStorageReference);

    private sealed record ThumbnailStaging(
        string DirectoryPath,
        string OutputPath);

    private readonly record struct FileSnapshot(
        long ByteSize,
        ContentDigest Digest);

    private readonly record struct HeldFileSnapshot(
        long ByteSize,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);
}

internal static class ManualThumbnailHash
{
    private const string ApprovalDomain =
        "HubRadio.MovieCreator.ThumbnailTemplateApproval/v1";
    private const string BindingDomain =
        "HubRadio.MovieCreator.ManualThumbnailBinding/v1";
    private const string RightsDomain =
        "HubRadio.MovieCreator.ManualThumbnailRights/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static ContentDigest ComputeApprovalDigest(
        ApprovedThumbnailTemplateSnapshot approval)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, ApprovalDomain);
        AppendText(hash, 2, approval.Id.ToString());
        AppendText(hash, 3, approval.TemplateVersion.SeriesId.ToString());
        AppendText(hash, 4, approval.TemplateVersion.Reference.Id.ToString());
        AppendText(hash, 5, approval.TemplateVersion.Reference.VersionDigest.Hex);
        AppendText(hash, 6, approval.ActorId);
        AppendText(hash, 7, approval.ActorRole.ToString());
        AppendInt64(
            hash,
            8,
            ManualThumbnailContract.GetUtcTicks(
                approval.ApprovedAt,
                nameof(approval)));
        AppendText(hash, 9, approval.Reason);
        AppendText(hash, 10, approval.EvidenceReference);
        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static ContentDigest ComputeRightsDigest(RightsRecord rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        string allowedUse = ManualThumbnailContract.RequireText(
            rights.AllowedUse,
            nameof(rights),
            "A thumbnail source rights allowed-use value");
        string? evidenceReference = rights.EvidenceReference is null
            ? null
            : ManualThumbnailContract.RequireText(
                rights.EvidenceReference,
                nameof(rights),
                "A thumbnail source rights evidence reference");
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, RightsDomain);
        AppendText(hash, 2, rights.Status.ToString());
        AppendNullableText(hash, 3, evidenceReference);
        AppendText(hash, 4, allowedUse);
        AppendInt64(
            hash,
            5,
            rights.ExternalPublicationAllowed ? 1 : 0);
        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static ContentDigest ComputeCanonicalJsonDigest(string json)
    {
        byte[] bytes = StrictUtf8.GetBytes(json);
        return ContentDigest.Compute(bytes);
    }

    internal static ContentDigest ComputeBindingDigest(
        ManualThumbnailRequest request,
        Asset sourceAsset,
        ApprovedThumbnailTemplateSnapshot approval,
        int streamIndex,
        PixelRect videoRegion,
        int outputWidth,
        int outputHeight)
    {
        RationalTime sourcePts = request.Selection.SourcePresentationTime
            ?? throw new InvalidOperationException(
                "A validated thumbnail selection requires source PTS.");
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, BindingDomain);
        AppendText(hash, 2, request.Source.ProjectId.ToString());
        AppendText(hash, 3, request.Source.SourceArtifactId.ToString());
        AppendText(hash, 4, request.Source.SourceAssetId.ToString());
        AppendText(hash, 5, sourceAsset.ContentDigest.Hex);
        AppendInt64(hash, 6, sourceAsset.ByteSize);
        AppendText(hash, 7, request.Source.SourceRightsDigest.Hex);
        AppendText(hash, 8, request.Source.SourcePtsIndexDigest.Hex);
        AppendText(hash, 9, request.Source.ProxyFrameMapDigest.Hex);
        AppendInt64(hash, 10, streamIndex);
        AppendInt64(hash, 11, request.Selection.ProxyFrameSequence);
        AppendInt64(hash, 12, request.Selection.SourceFrameSequence);
        AppendInt64(hash, 13, sourcePts.Value);
        AppendInt64(hash, 14, sourcePts.Rate);
        AppendText(hash, 15, request.Selection.SelectedByActorId);
        AppendInt64(
            hash,
            16,
            ManualThumbnailContract.GetUtcTicks(
                request.Selection.SelectedAt,
                nameof(request)));
        AppendText(hash, 17, approval.Id.ToString());
        AppendText(hash, 18, approval.ApprovalDigest.Hex);
        AppendText(hash, 19, request.Template.SeriesId.ToString());
        AppendText(hash, 20, request.Template.TemplateId.ToString());
        AppendText(hash, 21, request.Template.TemplateVersionDigest.Hex);
        AppendText(hash, 22, request.Template.RenderProfileId.ToString());
        AppendText(hash, 23, request.Template.RenderProfileDigest.Hex);
        AppendText(hash, 24, request.Template.LayoutSpecId.ToString());
        AppendText(hash, 25, request.Template.LayoutSpecDigest.Hex);
        AppendInt64(hash, 26, outputWidth);
        AppendInt64(hash, 27, outputHeight);
        AppendInt64(hash, 28, videoRegion.X);
        AppendInt64(hash, 29, videoRegion.Y);
        AppendInt64(hash, 30, videoRegion.Width);
        AppendInt64(hash, 31, videoRegion.Height);
        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static void AppendNullableText(
        IncrementalHash hash,
        byte tag,
        string? value)
    {
        AppendBytes(hash, tag, [value is null ? (byte)0 : (byte)1]);
        if (value is not null)
        {
            AppendText(hash, tag, value);
        }
    }

    private static void AppendText(
        IncrementalHash hash,
        byte tag,
        string value) =>
        AppendBytes(hash, tag, StrictUtf8.GetBytes(value));

    private static void AppendInt64(
        IncrementalHash hash,
        byte tag,
        long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        AppendBytes(hash, tag, bytes);
    }

    private static void AppendBytes(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }
}

internal static class ManualThumbnailContract
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string RequireText(
        string? value,
        string parameterName,
        string description)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{description} must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (!value.EnumerateRunes().Any(static rune =>
                !Rune.IsWhiteSpace(rune) &&
                Rune.GetUnicodeCategory(rune) is not (
                    UnicodeCategory.Control or
                    UnicodeCategory.Format or
                    UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or
                    UnicodeCategory.EnclosingMark or
                    UnicodeCategory.SpaceSeparator or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator)))
        {
            throw new ArgumentException(
                $"{description} must contain substantive text.",
                parameterName);
        }

        return value;
    }

    internal static long GetUtcTicks(
        DateTimeOffset value,
        string parameterName)
    {
        try
        {
            return value.ToUniversalTime().Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The timestamp cannot be represented in UTC.");
        }
    }
}

internal static class ManualThumbnailPath
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static string NormalizeExistingWorkspace(
        string path,
        string parameterName)
    {
        string normalized = Path.TrimEndingDirectorySeparator(
            NormalizeLocalAbsolutePath(path, parameterName));
        try
        {
            ValidateExistingDirectoryAncestorChain(normalized);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw new ArgumentException(
                "The workspace must be an existing local directory without reparse points.",
                parameterName);
        }

        return normalized;
    }

    internal static string NormalizeLocalAbsolutePath(
        string path,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            OperatingSystem.IsWindows() &&
                (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                    path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    path.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The path must be a nonblank absolute local path without surrounding whitespace.",
                parameterName);
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                string root = Path.GetPathRoot(fullPath) ?? string.Empty;
                if (fullPath.AsSpan(root.Length).Contains(':'))
                {
                    throw new ArgumentException(
                        "The path must not use an alternate data stream.",
                        parameterName);
                }
            }

            return fullPath;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            throw new ArgumentException(
                "The path must be a valid absolute local path.",
                parameterName);
        }
    }

    internal static void EnsureDirectoryChain(
        string workspacePath,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!IsStrictDescendant(workspacePath, destination))
        {
            throw new IOException("The directory is outside the workspace.");
        }

        string relative = Path.GetRelativePath(workspacePath, destination);
        string current = workspacePath;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExistingPathChain(
                workspacePath,
                current,
                targetIsDirectory: true);
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out _))
            {
                _ = Directory.CreateDirectory(current);
            }

            ValidateExistingPathChain(
                workspacePath,
                current,
                targetIsDirectory: true);
        }
    }

    internal static string ResolvePortableRelativePath(
        string workspacePath,
        string relativePath)
    {
        if (!IsValidPortableRelativePath(relativePath))
        {
            throw new IOException("The storage reference is invalid.");
        }

        string path = Path.GetFullPath(Path.Combine(
            workspacePath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictDescendant(workspacePath, path) ||
            !HasSameWindowsDriveRoot(workspacePath, path))
        {
            throw new IOException("The storage reference escapes the workspace.");
        }

        return path;
    }

    internal static void ValidateExistingPathChain(
        string workspacePath,
        string targetPath,
        bool targetIsDirectory)
    {
        if ((!string.Equals(
                    workspacePath,
                    targetPath,
                    PathComparison) &&
                !IsStrictDescendant(workspacePath, targetPath)) ||
            !HasSameWindowsDriveRoot(workspacePath, targetPath))
        {
            throw new IOException("The path is outside the workspace.");
        }

        string? current = targetPath;
        bool isTarget = true;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The path contains a reparse point.");
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isDevice = (attributes & FileAttributes.Device) != 0;
            if (isDevice ||
                isTarget && targetIsDirectory != isDirectory ||
                !isTarget && !isDirectory)
            {
                throw new IOException("The path entry has an invalid type.");
            }

            if (string.Equals(current, workspacePath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
            isTarget = false;
        }

        throw new IOException("The workspace path was not reached.");
    }

    internal static bool TryDeleteOwnedEmptyDirectory(
        string workspacePath,
        string path)
    {
        try
        {
            if (!IsStrictDescendant(workspacePath, path))
            {
                return false;
            }

            if (!TryGetAttributes(path, out _))
            {
                return true;
            }

            ValidateExistingPathChain(
                workspacePath,
                path,
                targetIsDirectory: true);
            Directory.Delete(path, recursive: false);
            return !EntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool EntryExistsConservatively(string path)
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

    internal static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            SecurityException or NotSupportedException or ArgumentException;

    private static void ValidateExistingDirectoryAncestorChain(string path)
    {
        string? current = path;
        while (current is not null)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw new IOException("The workspace ancestor is unsafe.");
            }

            current = Directory.GetParent(current)?.FullName;
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

    private static bool IsValidPortableRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = relativePath.Split('/');
        return segments.All(static segment =>
            segment.Length > 0 &&
            segment is not "." and not ".." &&
            segment[^1] is not '.' and not ' ' &&
            !segment.Any(static character =>
                character is '<' or '>' or '"' or '|' or '?' or '*'));
    }

    private static bool IsStrictDescendant(
        string rootPath,
        string candidatePath)
    {
        if (string.Equals(rootPath, candidatePath, PathComparison))
        {
            return false;
        }

        string prefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : string.Concat(rootPath, Path.DirectorySeparatorChar);
        return candidatePath.StartsWith(prefix, PathComparison);
    }

    private static bool HasSameWindowsDriveRoot(
        string firstPath,
        string secondPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string? firstRoot = Path.GetPathRoot(firstPath);
        string? secondRoot = Path.GetPathRoot(secondPath);
        return !string.IsNullOrEmpty(firstRoot) &&
            !string.IsNullOrEmpty(secondRoot) &&
            string.Equals(
                firstRoot,
                secondRoot,
                StringComparison.OrdinalIgnoreCase);
    }
}