using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Delivery;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Subtitles;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Rendering;

public enum SubtitleVariantStage
{
    NotStarted,
    BurnedInRendered,
    BurnedInQualityControlled,
    BurnedInDelivered,
    SubtitleFreeRendered,
    SubtitleFreeQualityControlled,
    SubtitleFreeDelivered,
}

public enum SubtitleVariantDisposition
{
    Completed,
    Cancelled,
    Failed,
    ReconciliationRequired,
}

public enum SubtitleVariantFailureReason
{
    ContextMismatch,
    ApprovalMismatch,
    ProfileMismatch,
    GraphMismatch,
    SharedInputMismatch,
    RenderManifestMismatch,
    OutputConflict,
    SubtitleEvidenceMismatch,
    SidecarMismatch,
    BurnedInRenderFailed,
    BurnedInQualityControlFailed,
    BurnedInDeliveryFailed,
    SubtitleFreeRenderFailed,
    SubtitleFreeQualityControlFailed,
    SubtitleFreeDeliveryFailed,
    DeliveryEvidenceMismatch,
    ExecutionConflict,
}

public sealed class SubtitleVariantException : Exception
{
    internal SubtitleVariantException(SubtitleVariantFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SubtitleVariantFailureReason Reason { get; }

    private static string GetStableMessage(
        SubtitleVariantFailureReason reason) =>
        reason switch
        {
            SubtitleVariantFailureReason.ContextMismatch =>
                "The subtitle variants do not identify one exact project and composition version.",
            SubtitleVariantFailureReason.ApprovalMismatch =>
                "The subtitle variants do not share exact approval evidence.",
            SubtitleVariantFailureReason.ProfileMismatch =>
                "The subtitle variant render profiles are not a compatible burned-in and subtitle-free pair.",
            SubtitleVariantFailureReason.GraphMismatch =>
                "A subtitle variant render graph is invalid or has the wrong subtitle polarity.",
            SubtitleVariantFailureReason.SharedInputMismatch =>
                "The subtitle variants do not share exact non-subtitle render inputs.",
            SubtitleVariantFailureReason.RenderManifestMismatch =>
                "A subtitle variant render manifest does not match its exact graph and output.",
            SubtitleVariantFailureReason.OutputConflict =>
                "Subtitle variant output identities or storage references conflict.",
            SubtitleVariantFailureReason.SubtitleEvidenceMismatch =>
                "The subtitle revisions, display policy, layout evidence, and burned-in input are inconsistent.",
            SubtitleVariantFailureReason.SidecarMismatch =>
                "The optional subtitle sidecar does not match the exact displayed subtitle evidence.",
            SubtitleVariantFailureReason.ExecutionConflict =>
                "The subtitle variant request is already executing.",
            _ => "The subtitle variant operation could not be prepared.",
        };
}

public sealed class SubtitleExportSpecification
{
    public SubtitleExportSpecification(
        SubtitleFileFormat format,
        AssSubtitleProfile? assProfile = null)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Unknown subtitle export format.");
        }

        bool requiresAssProfile =
            format == SubtitleFileFormat.AdvancedSubStationAlpha;
        if (requiresAssProfile != (assProfile is not null))
        {
            throw new ArgumentException(
                "An ASS subtitle export requires exactly one ASS profile.",
                nameof(assProfile));
        }

        Format = format;
        AssProfile = assProfile;
    }

    public SubtitleFileFormat Format { get; }

    public AssSubtitleProfile? AssProfile { get; }

    internal SubtitleExportFile Write(
        IReadOnlyList<SubtitleFileCue> cues,
        CancellationToken cancellationToken) =>
        Format switch
        {
            SubtitleFileFormat.WebVtt =>
                WebVttSubtitleWriter.Write(cues, cancellationToken),
            SubtitleFileFormat.SubRip =>
                SubRipSubtitleWriter.Write(cues, cancellationToken),
            SubtitleFileFormat.AdvancedSubStationAlpha =>
                AssSubtitleWriter.Write(
                    cues,
                    AssProfile!,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "Unknown subtitle export format."),
        };
}

public sealed class SubtitleVariantSidecarRequest
{
    public SubtitleVariantSidecarRequest(
        EntityId<ArtifactIdTag> artifactId,
        ManifestArtifact artifact,
        SubtitleExportSpecification export)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(export);

        ArtifactId = artifactId;
        Artifact = artifact;
        Export = export;
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public ManifestArtifact Artifact { get; }

    public SubtitleExportSpecification Export { get; }
}

public sealed class SubtitleVariantWorkItem
{
    public SubtitleVariantWorkItem(
        EntityId<DeliveryManifestIdTag> deliveryManifestId,
        FinalRenderRequest renderRequest,
        OutputQcRequest qualityControlRequest,
        IReadOnlyList<KeyValuePair<
            EntityId<ArtifactIdTag>,
            ManifestArtifact>> artifactEvidence)
    {
        ArgumentNullException.ThrowIfNull(deliveryManifestId);
        ArgumentNullException.ThrowIfNull(renderRequest);
        ArgumentNullException.ThrowIfNull(qualityControlRequest);
        ArgumentNullException.ThrowIfNull(artifactEvidence);

        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>[] copied =
            artifactEvidence.ToArray();
        var ids = new HashSet<EntityId<ArtifactIdTag>>();
        if (copied.Any(static item => item.Key is null || item.Value is null)
            || copied.Any(item => !ids.Add(item.Key)))
        {
            throw new ArgumentException(
                "Variant artifact evidence must contain unique non-null bindings.",
                nameof(artifactEvidence));
        }

        DeliveryManifestId = deliveryManifestId;
        RenderRequest = renderRequest;
        QualityControlRequest = qualityControlRequest;
        ArtifactEvidence = Array.AsReadOnly(copied);
    }

    public EntityId<DeliveryManifestIdTag> DeliveryManifestId { get; }

    public FinalRenderRequest RenderRequest { get; }

    public OutputQcRequest QualityControlRequest { get; }

    public IReadOnlyList<KeyValuePair<
        EntityId<ArtifactIdTag>,
        ManifestArtifact>> ArtifactEvidence
    { get; }
}

public sealed class SubtitleVariantRequest
{
    public SubtitleVariantRequest(
        Project project,
        CompositionVersion compositionVersion,
        IReadOnlyList<SubtitleCueRevision> subtitleRevisions,
        SubtitleDisplayProfile displayProfile,
        IReadOnlyList<EntityId<CueIdTag>> selectedCueIds,
        SubtitleExportSpecification burnedInSubtitleExport,
        SubtitleVariantWorkItem burnedIn,
        SubtitleVariantWorkItem subtitleFree,
        SubtitleVariantSidecarRequest? sidecar = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(subtitleRevisions);
        ArgumentNullException.ThrowIfNull(displayProfile);
        ArgumentNullException.ThrowIfNull(selectedCueIds);
        ArgumentNullException.ThrowIfNull(burnedInSubtitleExport);
        ArgumentNullException.ThrowIfNull(burnedIn);
        ArgumentNullException.ThrowIfNull(subtitleFree);

        SubtitleCueRevision[] revisions = subtitleRevisions.ToArray();
        var revisionIds = new HashSet<EntityId<SubtitleCueRevisionIdTag>>();
        var cueIds = new HashSet<EntityId<CueIdTag>>();
        if (revisions.Any(static revision => revision is null)
            || revisions.Any(revision =>
                !revisionIds.Add(revision.Id)
                || !cueIds.Add(revision.Snapshot.Cue.Id)))
        {
            throw new ArgumentException(
                "Subtitle revisions must contain unique non-null revision and cue identities.",
                nameof(subtitleRevisions));
        }

        EntityId<CueIdTag>[] selection = selectedCueIds.ToArray();
        var selected = new HashSet<EntityId<CueIdTag>>();
        if (selection.Any(static id => id is null)
            || selection.Any(id => !selected.Add(id)))
        {
            throw new ArgumentException(
                "Selected subtitle cue identities must be unique and non-null.",
                nameof(selectedCueIds));
        }

        Project = project;
        CompositionVersion = compositionVersion;
        SubtitleRevisions = Array.AsReadOnly(revisions);
        DisplayProfile = displayProfile;
        SelectedCueIds = Array.AsReadOnly(selection);
        BurnedInSubtitleExport = burnedInSubtitleExport;
        BurnedIn = burnedIn;
        SubtitleFree = subtitleFree;
        Sidecar = sidecar;
    }

    public Project Project { get; }

    public CompositionVersion CompositionVersion { get; }

    public IReadOnlyList<SubtitleCueRevision> SubtitleRevisions { get; }

    public SubtitleDisplayProfile DisplayProfile { get; }

    public IReadOnlyList<EntityId<CueIdTag>> SelectedCueIds { get; }

    public SubtitleExportSpecification BurnedInSubtitleExport { get; }

    public SubtitleVariantWorkItem BurnedIn { get; }

    public SubtitleVariantWorkItem SubtitleFree { get; }

    public SubtitleVariantSidecarRequest? Sidecar { get; }
}

public sealed class SubtitleVariantRunResult
{
    internal SubtitleVariantRunResult(
        SubtitleVariantStage lastCompletedStage,
        SubtitleVariantDisposition disposition,
        SubtitleVariantFailureReason? failureReason,
        ContentDigest subtitleEvidenceDigest,
        SubtitleExportFile? sidecarFile,
        FinalRenderRunResult? burnedInRender,
        OutputQcResult? burnedInQualityControl,
        DeliveryCreationResult? burnedInDelivery,
        FinalRenderRunResult? subtitleFreeRender,
        OutputQcResult? subtitleFreeQualityControl,
        DeliveryCreationResult? subtitleFreeDelivery)
    {
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

        ArgumentNullException.ThrowIfNull(subtitleEvidenceDigest);
        bool complete = disposition == SubtitleVariantDisposition.Completed;
        bool valid = complete
            ? lastCompletedStage == SubtitleVariantStage.SubtitleFreeDelivered
                && failureReason is null
                && burnedInRender?.IsComplete == true
                && burnedInQualityControl is not null
                && burnedInDelivery is not null
                && subtitleFreeRender?.IsComplete == true
                && subtitleFreeQualityControl is not null
                && subtitleFreeDelivery is not null
            : disposition == SubtitleVariantDisposition.Cancelled
                ? failureReason is null
                : failureReason is not null;
        if (!valid)
        {
            throw new ArgumentException(
                "The subtitle variant result is inconsistent.",
                nameof(disposition));
        }

        LastCompletedStage = lastCompletedStage;
        Disposition = disposition;
        FailureReason = failureReason;
        SubtitleEvidenceDigest = subtitleEvidenceDigest;
        SidecarFile = sidecarFile;
        BurnedInRender = burnedInRender;
        BurnedInQualityControl = burnedInQualityControl;
        BurnedInDelivery = burnedInDelivery;
        SubtitleFreeRender = subtitleFreeRender;
        SubtitleFreeQualityControl = subtitleFreeQualityControl;
        SubtitleFreeDelivery = subtitleFreeDelivery;
    }

    public SubtitleVariantStage LastCompletedStage { get; }

    public SubtitleVariantDisposition Disposition { get; }

    public SubtitleVariantFailureReason? FailureReason { get; }

    public ContentDigest SubtitleEvidenceDigest { get; }

    public SubtitleExportFile? SidecarFile { get; }

    public FinalRenderRunResult? BurnedInRender { get; }

    public OutputQcResult? BurnedInQualityControl { get; }

    public DeliveryCreationResult? BurnedInDelivery { get; }

    public FinalRenderRunResult? SubtitleFreeRender { get; }

    public OutputQcResult? SubtitleFreeQualityControl { get; }

    public DeliveryCreationResult? SubtitleFreeDelivery { get; }

    public bool IsComplete =>
        Disposition == SubtitleVariantDisposition.Completed;

    public bool RequiresReconciliation =>
        Disposition == SubtitleVariantDisposition.ReconciliationRequired;
}

public sealed class SubtitleVariantService
{
    private const string SubtitleEvidenceDigestDomain =
        "HubRadio.MovieCreator.SubtitleVariantEvidence/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly RunRenderDelegate _runRenderAsync;
    private readonly RunQualityControlDelegate _runQualityControlAsync;
    private readonly CreateDeliveryDelegate _createDeliveryAsync;

    public SubtitleVariantService(
        FinalRenderService finalRenderService,
        OutputQc outputQualityControl,
        DeliveryService deliveryService)
        : this(
            GetRunRenderDelegate(finalRenderService),
            GetRunQualityControlDelegate(outputQualityControl),
            GetCreateDeliveryDelegate(deliveryService))
    {
    }

    internal SubtitleVariantService(
        RunRenderDelegate runRenderAsync,
        RunQualityControlDelegate runQualityControlAsync,
        CreateDeliveryDelegate createDeliveryAsync)
    {
        ArgumentNullException.ThrowIfNull(runRenderAsync);
        ArgumentNullException.ThrowIfNull(runQualityControlAsync);
        ArgumentNullException.ThrowIfNull(createDeliveryAsync);

        _runRenderAsync = runRenderAsync;
        _runQualityControlAsync = runQualityControlAsync;
        _createDeliveryAsync = createDeliveryAsync;
    }

    public async Task<SubtitleVariantRunResult> CreateAsync(
        SubtitleVariantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using PairExecutionLease executionLease = PairExecutionLease.Enter(
            request.BurnedIn.RenderRequest.PolicySnapshot,
            request.SubtitleFree.RenderRequest.PolicySnapshot);

        PreparedSubtitleEvidence prepared;
        try
        {
            prepared = Prepare(request, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            request.BurnedIn.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            throw;
        }

        SubtitleVariantStage stage = SubtitleVariantStage.NotStarted;
        FinalRenderRunResult? burnedInRender = null;
        OutputQcResult? burnedInQualityControl = null;
        DeliveryCreationResult? burnedInDelivery = null;
        FinalRenderRunResult? subtitleFreeRender = null;
        OutputQcResult? subtitleFreeQualityControl = null;
        DeliveryCreationResult? subtitleFreeDelivery = null;

        try
        {
            burnedInRender = await _runRenderAsync(
                request.BurnedIn.RenderRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (burnedInRender is null)
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (!burnedInRender.IsComplete)
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateRenderStoppedResult(
                stage,
                SubtitleVariantFailureReason.BurnedInRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery,
                partialPairAlreadyCommitted: false);
        }

        if (!RenderResultMatches(
                request.BurnedIn.RenderRequest,
                burnedInRender))
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.BurnedInRendered;
        try
        {
            burnedInQualityControl = await _runQualityControlAsync(
                request.BurnedIn.QualityControlRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInQualityControlFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (burnedInQualityControl is null
            || !QualityControlMatches(
                request.BurnedIn,
                burnedInQualityControl,
                prepared,
                expectsSubtitleEvidence: true))
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInQualityControlFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.BurnedInQualityControlled;
        var burnedInVariant = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.BurnedIn,
            request.BurnedIn.RenderRequest.Graph.Id,
            request.DisplayProfile.VersionDigest,
            prepared.EvidenceDigest,
            sidecarArtifact: null);
        try
        {
            burnedInDelivery = await _createDeliveryAsync(
                request.BurnedIn,
                request.Project,
                request.CompositionVersion,
                burnedInQualityControl,
                burnedInVariant,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInDeliveryFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (burnedInDelivery is null
            || !DeliveryMatches(
                request.BurnedIn,
                burnedInDelivery,
                burnedInVariant))
        {
            request.SubtitleFree.RenderRequest.PolicySnapshot.ExpireIfUnclaimed();
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.BurnedInDeliveryFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.BurnedInDelivered;
        try
        {
            subtitleFreeRender = await _runRenderAsync(
                request.SubtitleFree.RenderRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (subtitleFreeRender is null)
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (!subtitleFreeRender.IsComplete)
        {
            return CreateRenderStoppedResult(
                stage,
                SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery,
                partialPairAlreadyCommitted: true);
        }

        if (!RenderResultMatches(
                request.SubtitleFree.RenderRequest,
                subtitleFreeRender))
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.SubtitleFreeRendered;
        try
        {
            subtitleFreeQualityControl = await _runQualityControlAsync(
                request.SubtitleFree.QualityControlRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeQualityControlFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (subtitleFreeQualityControl is null
            || !QualityControlMatches(
                request.SubtitleFree,
                subtitleFreeQualityControl,
                prepared,
                expectsSubtitleEvidence: false))
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeQualityControlFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.SubtitleFreeQualityControlled;
        var subtitleFreeVariant = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.SubtitleFree,
            request.SubtitleFree.RenderRequest.Graph.Id,
            request.DisplayProfile.VersionDigest,
            prepared.EvidenceDigest,
            request.Sidecar?.Artifact);
        try
        {
            subtitleFreeDelivery = await _createDeliveryAsync(
                request.SubtitleFree,
                request.Project,
                request.CompositionVersion,
                subtitleFreeQualityControl,
                subtitleFreeVariant,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeDeliveryFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        if (subtitleFreeDelivery is null
            || !DeliveryMatches(
                request.SubtitleFree,
                subtitleFreeDelivery,
                subtitleFreeVariant))
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.SubtitleFreeDeliveryFailed,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        stage = SubtitleVariantStage.SubtitleFreeDelivered;
        if (!DeliveryPairMatches(burnedInDelivery, subtitleFreeDelivery))
        {
            return CreateStoppedResult(
                stage,
                SubtitleVariantDisposition.ReconciliationRequired,
                SubtitleVariantFailureReason.DeliveryEvidenceMismatch,
                prepared,
                burnedInRender,
                burnedInQualityControl,
                burnedInDelivery,
                subtitleFreeRender,
                subtitleFreeQualityControl,
                subtitleFreeDelivery);
        }

        return new SubtitleVariantRunResult(
            stage,
            SubtitleVariantDisposition.Completed,
            failureReason: null,
            prepared.EvidenceDigest,
            prepared.SidecarFile,
            burnedInRender,
            burnedInQualityControl,
            burnedInDelivery,
            subtitleFreeRender,
            subtitleFreeQualityControl,
            subtitleFreeDelivery);
    }

    private static PreparedSubtitleEvidence Prepare(
        SubtitleVariantRequest request,
        CancellationToken cancellationToken)
    {
        ValidateContext(request);
        ValidateDistinctOutputs(request);
        ValidateApprovals(request);
        ValidateWorkItem(
            request,
            request.BurnedIn,
            SubtitleDeliveryVariantKind.BurnedIn,
            cancellationToken);
        ValidateWorkItem(
            request,
            request.SubtitleFree,
            SubtitleDeliveryVariantKind.SubtitleFree,
            cancellationToken);
        ValidateCompatibleProfiles(request);
        ValidateSharedRenderEvidence(request);
        return PrepareSubtitleEvidence(request, cancellationToken);
    }

    private static void ValidateContext(SubtitleVariantRequest request)
    {
        CompositionVersion composition = request.CompositionVersion;
        bool matches = composition.ProjectId == request.Project.Id
            && ReferenceEquals(
                request.BurnedIn.RenderRequest.ApprovedCompositionVersion,
                composition)
            && ReferenceEquals(
                request.SubtitleFree.RenderRequest.ApprovedCompositionVersion,
                composition)
            && ReferenceEquals(
                request.BurnedIn.RenderRequest.ValidationContext.Project,
                request.Project)
            && ReferenceEquals(
                request.SubtitleFree.RenderRequest.ValidationContext.Project,
                request.Project)
            && ReferenceEquals(
                request.BurnedIn.RenderRequest.ValidationContext
                    .CompositionVersion,
                composition)
            && ReferenceEquals(
                request.SubtitleFree.RenderRequest.ValidationContext
                    .CompositionVersion,
                composition)
            && request.BurnedIn.RenderRequest.ProjectId == request.Project.Id
            && request.SubtitleFree.RenderRequest.ProjectId == request.Project.Id
            && request.BurnedIn.RenderRequest.ApprovedCompositionDigest
                == composition.ContentDigest
            && request.SubtitleFree.RenderRequest.ApprovedCompositionDigest
                == composition.ContentDigest;
        try
        {
            matches = matches
                && RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    composition.Elements) == composition.ContentDigest;
        }
        catch
        {
            matches = false;
        }

        if (!matches)
        {
            throw Failure(SubtitleVariantFailureReason.ContextMismatch);
        }
    }

    private static void ValidateDistinctOutputs(SubtitleVariantRequest request)
    {
        FinalRenderRequest burnedIn = request.BurnedIn.RenderRequest;
        FinalRenderRequest subtitleFree = request.SubtitleFree.RenderRequest;
        bool distinct = !ReferenceEquals(burnedIn, subtitleFree)
            && burnedIn.JobId != subtitleFree.JobId
            && burnedIn.OutputArtifactId != subtitleFree.OutputArtifactId
            && burnedIn.Graph.Id != subtitleFree.Graph.Id
            && request.BurnedIn.QualityControlRequest.RenderManifest.Id
                != request.SubtitleFree.QualityControlRequest.RenderManifest.Id
            && request.BurnedIn.DeliveryManifestId
                != request.SubtitleFree.DeliveryManifestId
            && burnedIn.ExpectedOutputDigest
                != subtitleFree.ExpectedOutputDigest
            && !string.Equals(
                burnedIn.StagingOutputPath,
                subtitleFree.StagingOutputPath,
                PathComparison)
            && !string.Equals(
                burnedIn.OutputRelativePath,
                subtitleFree.OutputRelativePath,
                StringComparison.Ordinal)
            && !ReferenceEquals(
                burnedIn.PolicySnapshot,
                subtitleFree.PolicySnapshot);
        if (request.Sidecar is not null)
        {
            distinct = distinct
                && request.Sidecar.ArtifactId != burnedIn.OutputArtifactId
                && request.Sidecar.ArtifactId != subtitleFree.OutputArtifactId
                && !string.Equals(
                    request.Sidecar.Artifact.StorageReference,
                    ExpectedStorageReference(burnedIn),
                    StringComparison.Ordinal)
                && !string.Equals(
                    request.Sidecar.Artifact.StorageReference,
                    ExpectedStorageReference(subtitleFree),
                    StringComparison.Ordinal);
        }

        if (!distinct)
        {
            throw Failure(SubtitleVariantFailureReason.OutputConflict);
        }
    }

    private static void ValidateApprovals(SubtitleVariantRequest request)
    {
        IReadOnlyList<StoredApproval> first =
            request.BurnedIn.RenderRequest.ApprovalEvidence;
        IReadOnlyList<StoredApproval> second =
            request.SubtitleFree.RenderRequest.ApprovalEvidence;
        bool matches = first.Count > 0
            && HaveSameApprovals(first, second)
            && first.All(approval =>
                approval.ProjectId == request.Project.Id
                && approval.CompositionVersionId
                    == request.CompositionVersion.Id
                && approval.CompositionVersionDigest
                    == request.CompositionVersion.ContentDigest);
        if (!matches)
        {
            throw Failure(SubtitleVariantFailureReason.ApprovalMismatch);
        }
    }

    private static void ValidateWorkItem(
        SubtitleVariantRequest pair,
        SubtitleVariantWorkItem item,
        SubtitleDeliveryVariantKind kind,
        CancellationToken cancellationToken)
    {
        FinalRenderRequest render = item.RenderRequest;
        OutputQcRequest qualityControl = item.QualityControlRequest;
        RenderGraph graph = render.Graph;
        RenderGraphValidationContext context = render.ValidationContext;
        RenderManifest manifest = qualityControl.RenderManifest;
        bool contextMatches = ReferenceEquals(qualityControl.Graph, graph)
            && ReferenceEquals(qualityControl.ValidationContext, context)
            && qualityControl.OutputArtifactId == render.OutputArtifactId
            && render.PolicySnapshot.Matches(render)
            && render.PolicySnapshot.Digest
                == render.PolicySnapshot.CurrentDigest
            && graph.CompositionVersionId == pair.CompositionVersion.Id
            && graph.CompositionVersionDigest
                == pair.CompositionVersion.ContentDigest
            && graph.TimelineMapId == context.TimelineMap.Id
            && graph.TimelineMapDigest == context.TimelineMapDigest
            && graph.ProfileId == context.Profile.Id
            && graph.ProfileDigest == context.Profile.ProfileDigest
            && graph.LayoutSpecId == context.LayoutSpec.Id
            && graph.LayoutSpecDigest == context.LayoutSpecDigest
            && graph.Template.Equals(context.Template);
        if (!contextMatches)
        {
            throw Failure(SubtitleVariantFailureReason.ContextMismatch);
        }

        RenderGraphValidationResult validation;
        try
        {
            validation = RenderGraphValidator.Validate(
                graph,
                context,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            throw Failure(SubtitleVariantFailureReason.GraphMismatch);
        }

        bool expectsBurnIn = kind == SubtitleDeliveryVariantKind.BurnedIn;
        if (!validation.IsValid
            || context.Profile.SubtitleBurnIn != expectsBurnIn)
        {
            throw Failure(SubtitleVariantFailureReason.GraphMismatch);
        }

        ManifestArtifact[] videos = manifest.Outputs
            .Where(static artifact => string.Equals(
                artifact.ArtifactType,
                "video",
                StringComparison.Ordinal))
            .ToArray();
        bool manifestMatches = manifest.ProjectId == pair.Project.Id
            && manifest.CompositionVersionId == pair.CompositionVersion.Id
            && manifest.CompositionVersionDigest
                == pair.CompositionVersion.ContentDigest
            && manifest.TimelineMapId == graph.TimelineMapId
            && manifest.TimelineMapDigest == graph.TimelineMapDigest
            && manifest.ProfileId == graph.ProfileId
            && manifest.ProfileDigest == graph.ProfileDigest
            && manifest.TemplateId == graph.Template.Id
            && manifest.TemplateVersionDigest == graph.Template.VersionDigest
            && HaveSameInputDigests(manifest.Inputs, graph.Inputs)
            && HaveSameFonts(manifest.Fonts, graph.Fonts)
            && manifest.Outputs.Count == 1
            && videos.Length == 1
            && string.Equals(
                videos[0].MediaType,
                "video/mp4",
                StringComparison.Ordinal)
            && videos[0].Sha256 == render.ExpectedOutputDigest
            && videos[0].ByteSize == render.ExpectedOutputByteSize
            && string.Equals(
                videos[0].StorageReference,
                ExpectedStorageReference(render),
                StringComparison.Ordinal)
            && item.ArtifactEvidence.Count(binding =>
                binding.Key == render.OutputArtifactId
                && binding.Value.Equals(videos[0])) == 1;
        if (!manifestMatches)
        {
            throw Failure(
                SubtitleVariantFailureReason.RenderManifestMismatch);
        }

        int expectedArtifactCount = kind == SubtitleDeliveryVariantKind.BurnedIn
            ? 1
            : pair.Sidecar is null ? 1 : 2;
        if (item.ArtifactEvidence.Count != expectedArtifactCount
            || item.ArtifactEvidence
                .Select(static binding => binding.Value.StorageReference)
                .Distinct(StringComparer.Ordinal)
                .Count() != item.ArtifactEvidence.Count)
        {
            throw Failure(SubtitleVariantFailureReason.OutputConflict);
        }
    }

    private static void ValidateCompatibleProfiles(
        SubtitleVariantRequest request)
    {
        RenderProfile burnedIn =
            request.BurnedIn.RenderRequest.ValidationContext.Profile;
        RenderProfile subtitleFree =
            request.SubtitleFree.RenderRequest.ValidationContext.Profile;
        bool matches = burnedIn.SubtitleBurnIn
            && !subtitleFree.SubtitleBurnIn
            && burnedIn.Id != subtitleFree.Id
            && burnedIn.ProfileDigest != subtitleFree.ProfileDigest
            && burnedIn.Width == subtitleFree.Width
            && burnedIn.Height == subtitleFree.Height
            && burnedIn.FrameRate == subtitleFree.FrameRate
            && burnedIn.LayoutSpecId == subtitleFree.LayoutSpecId
            && burnedIn.TemplateId == subtitleFree.TemplateId
            && burnedIn.TemplateVersionDigest
                == subtitleFree.TemplateVersionDigest
            && string.Equals(
                burnedIn.VideoCodec,
                subtitleFree.VideoCodec,
                StringComparison.Ordinal)
            && string.Equals(
                burnedIn.AudioCodec,
                subtitleFree.AudioCodec,
                StringComparison.Ordinal);
        if (!matches)
        {
            throw Failure(SubtitleVariantFailureReason.ProfileMismatch);
        }
    }

    private static void ValidateSharedRenderEvidence(
        SubtitleVariantRequest request)
    {
        FinalRenderRequest burnedIn = request.BurnedIn.RenderRequest;
        FinalRenderRequest subtitleFree = request.SubtitleFree.RenderRequest;
        RenderGraph firstGraph = burnedIn.Graph;
        RenderGraph secondGraph = subtitleFree.Graph;
        RenderGraphValidationContext firstContext = burnedIn.ValidationContext;
        RenderGraphValidationContext secondContext =
            subtitleFree.ValidationContext;

        bool bindingsMatch = firstGraph.CompositionVersionId
                == secondGraph.CompositionVersionId
            && firstGraph.CompositionVersionDigest
                == secondGraph.CompositionVersionDigest
            && firstGraph.TimelineMapId == secondGraph.TimelineMapId
            && firstGraph.TimelineMapDigest == secondGraph.TimelineMapDigest
            && firstGraph.LayoutSpecId == secondGraph.LayoutSpecId
            && firstGraph.LayoutSpecDigest == secondGraph.LayoutSpecDigest
            && firstGraph.Template.Equals(secondGraph.Template)
            && ReferenceEquals(firstContext.TimelineMap, secondContext.TimelineMap)
            && ReferenceEquals(firstContext.LayoutSpec, secondContext.LayoutSpec)
            && firstContext.Template.Equals(secondContext.Template)
            && HaveSameSourceSpans(
                firstContext.SourceSpans,
                secondContext.SourceSpans)
            && firstContext.EditorialRecordIds.Count
                == secondContext.EditorialRecordIds.Count
            && firstContext.EditorialRecordIds.All(
                secondContext.EditorialRecordIds.Contains)
            && HaveSameFonts(firstGraph.Fonts, secondGraph.Fonts)
            && HaveSameFonts(firstContext.Fonts, secondContext.Fonts)
            && HaveSameToolBinding(burnedIn.Tool, subtitleFree.Tool)
            && HaveSameTools(
                request.BurnedIn.QualityControlRequest.RenderManifest.Tools,
                request.SubtitleFree.QualityControlRequest.RenderManifest.Tools)
            && HaveSameModels(
                request.BurnedIn.QualityControlRequest.RenderManifest.Models,
                request.SubtitleFree.QualityControlRequest.RenderManifest.Models)
            && string.Equals(
                request.BurnedIn.QualityControlRequest.RenderManifest
                    .ApplicationVersion,
                request.SubtitleFree.QualityControlRequest.RenderManifest
                    .ApplicationVersion,
                StringComparison.Ordinal)
            && request.BurnedIn.QualityControlRequest.RenderManifest
                    .ApplicationDigest
                == request.SubtitleFree.QualityControlRequest.RenderManifest
                    .ApplicationDigest;
        if (!bindingsMatch)
        {
            throw Failure(SubtitleVariantFailureReason.SharedInputMismatch);
        }

        RenderGraphInput[] firstInputs = firstGraph.Inputs
            .Where(static input => input.Kind != RenderInputKind.Subtitle)
            .OrderBy(static input => input.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        RenderGraphInput[] secondInputs = secondGraph.Inputs
            .Where(static input => input.Kind != RenderInputKind.Subtitle)
            .OrderBy(static input => input.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        int firstSubtitleInputCount = firstGraph.Inputs.Count(static input =>
            input.Kind == RenderInputKind.Subtitle);
        bool inputsMatch = burnedIn.Inputs.Count == firstGraph.Inputs.Count
            && subtitleFree.Inputs.Count == secondGraph.Inputs.Count
            && firstInputs.Length == secondInputs.Length
            && firstInputs.Any(static input => input.Kind == RenderInputKind.Video)
            && firstInputs.Zip(secondInputs).All(pair =>
                RenderInputsMatch(pair.First, pair.Second))
            && firstSubtitleInputCount > 0
            && firstGraph.Inputs.Count
                == secondGraph.Inputs.Count + firstSubtitleInputCount
            && secondGraph.Inputs.All(static input =>
                input.Kind != RenderInputKind.Subtitle)
            && HaveExactContextArtifacts(firstGraph, firstContext)
            && HaveExactContextArtifacts(secondGraph, secondContext);
        if (!inputsMatch)
        {
            throw Failure(SubtitleVariantFailureReason.SharedInputMismatch);
        }

        foreach (RenderGraphInput input in firstInputs)
        {
            FfmpegInputBinding? firstBinding = burnedIn.Inputs.SingleOrDefault(
                binding => binding.InputId == input.Id);
            FfmpegInputBinding? secondBinding =
                subtitleFree.Inputs.SingleOrDefault(
                    binding => binding.InputId == input.Id);
            if (firstBinding is null
                || secondBinding is null
                || !InputBindingMatches(input, firstBinding)
                || !InputBindingMatches(input, secondBinding)
                || !HaveSameVerification(
                    firstBinding.VerificationRequest,
                    secondBinding.VerificationRequest))
            {
                throw Failure(
                    SubtitleVariantFailureReason.SharedInputMismatch);
            }
        }

        RenderNode[] firstBaseNodes = firstGraph.Nodes
            .Where(static node => node.Kind is not (
                RenderNodeKind.Subtitle or RenderNodeKind.Output))
            .OrderBy(static node => node.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        RenderNode[] secondBaseNodes = secondGraph.Nodes
            .Where(static node => node.Kind is not (
                RenderNodeKind.Subtitle or RenderNodeKind.Output))
            .OrderBy(static node => node.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (firstBaseNodes.Length != secondBaseNodes.Length
            || !firstBaseNodes.Zip(secondBaseNodes).All(pair =>
                RenderNodesMatch(pair.First, pair.Second)))
        {
            throw Failure(SubtitleVariantFailureReason.SharedInputMismatch);
        }

        RenderNode firstOutput = firstGraph.Nodes.Single(node =>
            node.Id == firstGraph.OutputNodeId);
        RenderNode secondOutput = secondGraph.Nodes.Single(node =>
            node.Id == secondGraph.OutputNodeId);
        RenderNode[]? subtitleChain = GetBurnedInSubtitleChain(firstGraph);
        EntityId<RenderNodeIdTag>[] firstAudio = firstOutput.InputNodeIds
            .Where(id => firstGraph.Nodes.Single(node => node.Id == id).Kind
                == RenderNodeKind.Audio)
            .OrderBy(static id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        EntityId<RenderNodeIdTag>[] secondAudio = secondOutput.InputNodeIds
            .Where(id => secondGraph.Nodes.Single(node => node.Id == id).Kind
                == RenderNodeKind.Audio)
            .OrderBy(static id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        bool outputsMatch = subtitleChain is { Length: > 0 }
            && firstOutput.OutputRange == secondOutput.OutputRange
            && firstOutput.InputNodeIds.Contains(subtitleChain[^1].Id)
            && secondOutput.InputNodeIds.Contains(
                subtitleChain[0].InputNodeIds[0])
            && firstAudio.SequenceEqual(secondAudio)
            && firstOutput.InputNodeIds.Count == firstAudio.Length + 1
            && secondOutput.InputNodeIds.Count == secondAudio.Length + 1;
        if (!outputsMatch)
        {
            throw Failure(SubtitleVariantFailureReason.SharedInputMismatch);
        }
    }

    private static RenderNode[]? GetBurnedInSubtitleChain(
        RenderGraph graph)
    {
        Dictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes =
            graph.Nodes.ToDictionary(static node => node.Id);
        if (!nodes.TryGetValue(graph.OutputNodeId, out RenderNode? output))
        {
            return null;
        }

        RenderNode[] terminals = output.InputNodeIds
            .Where(nodes.ContainsKey)
            .Select(id => nodes[id])
            .Where(static node => node.Kind == RenderNodeKind.Subtitle)
            .ToArray();
        if (terminals.Length != 1)
        {
            return null;
        }

        var reversed = new List<RenderNode>();
        var visited = new HashSet<EntityId<RenderNodeIdTag>>();
        RenderNode current = terminals[0];
        while (current.Kind == RenderNodeKind.Subtitle)
        {
            if (!visited.Add(current.Id)
                || current.InputNodeIds.Count != 1
                || !nodes.TryGetValue(
                    current.InputNodeIds[0],
                    out RenderNode? dependency))
            {
                return null;
            }

            reversed.Add(current);
            current = dependency;
        }

        if (current.Kind is not (
                RenderNodeKind.Video
                or RenderNodeKind.Graphic
                or RenderNodeKind.Transition)
            || reversed.Count != graph.Nodes.Count(static node =>
                node.Kind == RenderNodeKind.Subtitle))
        {
            return null;
        }

        reversed.Reverse();
        return reversed.ToArray();
    }

    private static PreparedSubtitleEvidence PrepareSubtitleEvidence(
        SubtitleVariantRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenderGraph burnedGraph = request.BurnedIn.RenderRequest.Graph;
        RenderGraph freeGraph = request.SubtitleFree.RenderRequest.Graph;
        RenderNode[]? subtitleNodes = GetBurnedInSubtitleChain(burnedGraph);
        RenderGraphInput[] subtitleInputs = burnedGraph.Inputs
            .Where(static input => input.Kind == RenderInputKind.Subtitle)
            .ToArray();
        if (subtitleNodes is null
            || subtitleNodes.Length == 0
            || subtitleInputs.Length != subtitleNodes.Length
            || subtitleNodes
                .Select(static node => node.ArtifactInputId)
                .Distinct()
                .Count() != subtitleNodes.Length
            || subtitleInputs.Any(input => !subtitleNodes.Any(node =>
                node.ArtifactInputId == input.Id))
            || freeGraph.Nodes.Any(static node =>
                node.Kind == RenderNodeKind.Subtitle)
            || freeGraph.Inputs.Any(static input =>
                input.Kind == RenderInputKind.Subtitle)
            || request.SubtitleRevisions.Count != subtitleNodes.Length)
        {
            throw Failure(SubtitleVariantFailureReason.GraphMismatch);
        }

        SubtitleCueRevision[] revisions = request.SubtitleRevisions
            .OrderBy(static revision => revision.Snapshot.Cue.Range.Start)
            .ThenBy(static revision => revision.Snapshot.Cue.Range.End)
            .ThenBy(
                static revision => revision.Snapshot.Cue.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < revisions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleCueRevision revision = revisions[index];
            RenderNode node = subtitleNodes[index];
            RenderGraphInput subtitleInput = subtitleInputs.Single(input =>
                input.Id == node.ArtifactInputId);
            bool matches = revision.ProjectId == request.Project.Id
                && revision.CompositionVersionId
                    == request.CompositionVersion.Id
                && revision.CompositionVersionDigest
                    == request.CompositionVersion.ContentDigest
                && revision.ContentDigest
                    == SubtitleSnapshotJson.ComputeContentDigest(
                        revision.Snapshot)
                && revision.Snapshot.LayoutResolution.Status
                    == SubtitleLayoutResolutionStatus.Resolved
                && revision.Snapshot.LayoutResolution.Plan is not null
                && revision.Snapshot.LayoutResolution.LayoutSpecId
                    == burnedGraph.LayoutSpecId
                && node.Cue!.Equals(revision.Snapshot.Cue)
                && node.Origin == revision.Snapshot.TextOrigin
                && subtitleInput.Origin == revision.Snapshot.TextOrigin;
            if (!matches)
            {
                throw Failure(
                    SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
            }
        }

        ContentDigest[] layoutProfileDigests = revisions
            .Select(static revision => revision.Snapshot.LayoutResolution
                .ProfileVersionDigest)
            .Distinct()
            .ToArray();
        if (layoutProfileDigests.Length != 1)
        {
            throw Failure(
                SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
        }

        SubtitleDisplayResult display;
        try
        {
            display = SubtitleDisplayPolicy.Apply(
                request.DisplayProfile,
                revisions.Select(static revision =>
                    SubtitleDisplayMapper.MapSource(revision.Snapshot))
                    .ToArray(),
                request.SelectedCueIds,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            throw Failure(
                SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
        }

        if (display.DisplayedCues.Count != revisions.Length
            || display.SourceCueCount != revisions.Length
            || display.OmittedCueIds.Count != 0)
        {
            throw Failure(
                SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
        }

        OutputQcSubtitleEvidence? qualityEvidence =
            request.BurnedIn.QualityControlRequest.SubtitleEvidence;
        if (qualityEvidence is null
            || qualityEvidence.LayoutProfileDigest != layoutProfileDigests[0]
            || qualityEvidence.DisplayProfileDigest
                != request.DisplayProfile.VersionDigest
            || !DisplayResultsMatch(qualityEvidence.DisplayResult, display)
            || !LayoutEvidenceMatches(qualityEvidence.Layouts, revisions)
            || request.SubtitleFree.QualityControlRequest.SubtitleEvidence
                is not null)
        {
            throw Failure(
                SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
        }

        SubtitleFileCue[] cues = revisions.Select(revision =>
        {
            SubtitleDisplayedCue displayed = display.DisplayedCues.Single(
                candidate => candidate.Cue.Id == revision.Snapshot.Cue.Id);
            return SubtitleDisplayMapper.MapForExport(
                revision.Snapshot,
                displayed);
        }).ToArray();

        SubtitleExportFile[] burnedInFiles;
        try
        {
            SubtitleExportFile aggregate =
                request.BurnedInSubtitleExport.Write(
                cues,
                cancellationToken);
            if (aggregate.CueCount != revisions.Length)
            {
                throw Failure(
                    SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
            }

            burnedInFiles = new SubtitleExportFile[cues.Length];
            for (int index = 0; index < cues.Length; index++)
            {
                burnedInFiles[index] = cues.Length == 1
                    ? aggregate
                    : request.BurnedInSubtitleExport.Write(
                        [cues[index]],
                        cancellationToken);
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            throw Failure(
                SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
        }

        for (int index = 0; index < subtitleNodes.Length; index++)
        {
            RenderGraphInput subtitleInput = subtitleInputs.Single(input =>
                input.Id == subtitleNodes[index].ArtifactInputId);
            SubtitleExportFile burnedInFile = burnedInFiles[index];
            FfmpegInputBinding? subtitleBinding =
                request.BurnedIn.RenderRequest.Inputs.SingleOrDefault(
                    binding => binding.InputId == subtitleInput.Id);
            RenderArtifactEvidence? subtitleArtifact =
                request.BurnedIn.RenderRequest.ValidationContext.Artifacts
                    .SingleOrDefault(artifact =>
                        artifact.Id == subtitleInput.Id);
            bool burnedInputMatches = burnedInFile.CueCount == 1
                && burnedInFile.ContentDigest == subtitleInput.ContentDigest
                && burnedInFile.ByteSize == subtitleInput.ByteSize
                && subtitleBinding is not null
                && subtitleBinding.VerificationRequest.ExpectedDigest
                    == burnedInFile.ContentDigest
                && subtitleBinding.VerificationRequest.ExpectedByteSize
                    == burnedInFile.ByteSize
                && subtitleArtifact is not null
                && subtitleArtifact.ContentDigest
                    == burnedInFile.ContentDigest
                && subtitleArtifact.ByteSize == burnedInFile.ByteSize;
            if (!burnedInputMatches)
            {
                throw Failure(
                    SubtitleVariantFailureReason.SubtitleEvidenceMismatch);
            }
        }

        ContentDigest evidenceDigest = ComputeSubtitleEvidenceDigest(
            request,
            revisions,
            display);
        SubtitleExportFile? sidecarFile = ValidateSidecar(
            request,
            cues,
            cancellationToken);
        return new PreparedSubtitleEvidence(
            evidenceDigest,
            layoutProfileDigests[0],
            sidecarFile);
    }

    private static SubtitleExportFile? ValidateSidecar(
        SubtitleVariantRequest request,
        IReadOnlyList<SubtitleFileCue> cues,
        CancellationToken cancellationToken)
    {
        ManifestArtifact[] burnedSubtitleArtifacts =
            request.BurnedIn.ArtifactEvidence
                .Select(static binding => binding.Value)
                .Where(static artifact => string.Equals(
                    artifact.ArtifactType,
                    "subtitle",
                    StringComparison.Ordinal))
                .ToArray();
        ManifestArtifact[] freeSubtitleArtifacts =
            request.SubtitleFree.ArtifactEvidence
                .Select(static binding => binding.Value)
                .Where(static artifact => string.Equals(
                    artifact.ArtifactType,
                    "subtitle",
                    StringComparison.Ordinal))
                .ToArray();
        if (burnedSubtitleArtifacts.Length != 0)
        {
            throw Failure(SubtitleVariantFailureReason.SidecarMismatch);
        }

        if (request.Sidecar is null)
        {
            if (freeSubtitleArtifacts.Length != 0)
            {
                throw Failure(SubtitleVariantFailureReason.SidecarMismatch);
            }

            return null;
        }

        SubtitleVariantSidecarRequest sidecar = request.Sidecar;
        if (!string.Equals(
                sidecar.Artifact.ArtifactType,
                "subtitle",
                StringComparison.Ordinal)
            || sidecar.Artifact.Language is null
            || freeSubtitleArtifacts.Length != 1
            || !freeSubtitleArtifacts[0].Equals(sidecar.Artifact)
            || request.SubtitleFree.ArtifactEvidence.Count(binding =>
                binding.Key == sidecar.ArtifactId
                && binding.Value.Equals(sidecar.Artifact)) != 1)
        {
            throw Failure(SubtitleVariantFailureReason.SidecarMismatch);
        }

        SubtitleExportFile generated;
        try
        {
            generated = sidecar.Export.Write(cues, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            throw Failure(SubtitleVariantFailureReason.SidecarMismatch);
        }

        if (generated.ContentDigest != sidecar.Artifact.Sha256
            || generated.ByteSize != sidecar.Artifact.ByteSize
            || generated.CueCount != cues.Count)
        {
            throw Failure(SubtitleVariantFailureReason.SidecarMismatch);
        }

        return generated;
    }

    private static ContentDigest ComputeSubtitleEvidenceDigest(
        SubtitleVariantRequest request,
        IReadOnlyList<SubtitleCueRevision> revisions,
        SubtitleDisplayResult display)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, SubtitleEvidenceDigestDomain);
        AppendText(hash, request.Project.Id.ToString());
        AppendText(hash, request.CompositionVersion.Id.ToString());
        AppendDigest(hash, request.CompositionVersion.ContentDigest);
        AppendDigest(hash, request.DisplayProfile.VersionDigest);
        AppendText(hash, request.DisplayProfile.CueMode.ToString());
        AppendText(hash, request.DisplayProfile.PunctuationDisplay.ToString());
        EntityId<CueIdTag>[] selection = request.SelectedCueIds
            .OrderBy(static id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, selection.Length);
        foreach (EntityId<CueIdTag> id in selection)
        {
            AppendText(hash, id.ToString());
        }

        AppendInt32(hash, revisions.Count);
        foreach (SubtitleCueRevision revision in revisions)
        {
            SubtitleDisplayedCue displayed = display.DisplayedCues.Single(
                candidate => candidate.Cue.Id == revision.Snapshot.Cue.Id);
            AppendText(hash, revision.Id.ToString());
            AppendText(hash, revision.Snapshot.Cue.Id.ToString());
            AppendText(hash, revision.SourceCandidateId.ToString());
            AppendText(hash, revision.SourceRevisionId.ToString());
            AppendDigest(hash, revision.SourceRevisionDigest);
            AppendDigest(hash, revision.ContentDigest);
            AppendDigest(
                hash,
                revision.Snapshot.LayoutResolution.ProfileVersionDigest);
            AppendDigest(hash, displayed.SourceTextDigest);
            AppendText(hash, displayed.DisplayText);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static bool RenderResultMatches(
        FinalRenderRequest request,
        FinalRenderRunResult result) =>
        result.IsComplete
        && result.Job.Snapshot.JobId == request.JobId
        && result.Output is not null
        && result.Output.ArtifactId == request.OutputArtifactId
        && result.Output.ContentDigest == request.ExpectedOutputDigest
        && result.Output.ByteSize == request.ExpectedOutputByteSize;

    private static bool QualityControlMatches(
        SubtitleVariantWorkItem item,
        OutputQcResult qualityControl,
        PreparedSubtitleEvidence prepared,
        bool expectsSubtitleEvidence)
    {
        FinalRenderRequest render = item.RenderRequest;
        OutputQcRequest expected = item.QualityControlRequest;
        RenderManifest manifest = expected.RenderManifest;
        bool matches = qualityControl.MachineChecksPassed
            && qualityControl.Decode.DecodedSuccessfully
            && qualityControl.ArtifactId == render.OutputArtifactId
            && qualityControl.ProjectId == render.ProjectId
            && qualityControl.ContentDigest == render.ExpectedOutputDigest
            && qualityControl.ByteSize == render.ExpectedOutputByteSize
            && qualityControl.RenderManifestId == manifest.Id
            && qualityControl.RenderGraphId == render.Graph.Id
            && qualityControl.CompositionVersionId
                == render.ApprovedCompositionVersion.Id
            && qualityControl.CompositionVersionDigest
                == render.ApprovedCompositionDigest
            && qualityControl.ProfileId == render.Graph.ProfileId
            && qualityControl.ProfileDigest == render.Graph.ProfileDigest
            && qualityControl.LayoutSpecId == render.Graph.LayoutSpecId
            && qualityControl.LayoutSpecDigest == render.Graph.LayoutSpecDigest
            && qualityControl.TemplateId == render.Graph.Template.Id
            && qualityControl.TemplateVersionDigest
                == render.Graph.Template.VersionDigest
            && qualityControl.SubtitleStreamIndex is null
            && qualityControl.SubtitleTiming is null;
        bool subtitlePresentationReviewRequired =
            qualityControl.RequiredHumanReviewAreas.Contains(
                OutputQcReviewArea.SubtitlePresentation);
        return expectsSubtitleEvidence
            ? matches
                && subtitlePresentationReviewRequired
                && qualityControl.SubtitleLayoutProfileDigest
                    == prepared.LayoutProfileDigest
                && qualityControl.SubtitleDisplayProfileDigest
                    == expected.SubtitleEvidence!.DisplayProfileDigest
            : matches
                && !subtitlePresentationReviewRequired
                && qualityControl.SubtitleLayoutProfileDigest is null
                && qualityControl.SubtitleDisplayProfileDigest is null;
    }

    private static bool DeliveryMatches(
        SubtitleVariantWorkItem item,
        DeliveryCreationResult delivery,
        SubtitleDeliveryVariantEvidence variant)
    {
        DeliveryManifest manifest = delivery.Manifest;
        RenderManifest renderManifest = item.QualityControlRequest.RenderManifest;
        return manifest.Id == item.DeliveryManifestId
            && manifest.ProjectId == renderManifest.ProjectId
            && manifest.CompositionVersionId
                == renderManifest.CompositionVersionId
            && manifest.CompositionVersionDigest
                == renderManifest.CompositionVersionDigest
            && manifest.RenderManifestId == renderManifest.Id
            && manifest.ProfileId == renderManifest.ProfileId
            && manifest.ProfileDigest == renderManifest.ProfileDigest
            && SubtitleVariantsMatch(manifest.SubtitleVariant, variant)
            && manifest.Artifacts.Count == item.ArtifactEvidence.Count
            && manifest.Artifacts.All(artifact =>
                item.ArtifactEvidence.Count(binding =>
                    binding.Value.Equals(artifact)) == 1);
    }

    private static bool DeliveryPairMatches(
        DeliveryCreationResult burnedIn,
        DeliveryCreationResult subtitleFree) =>
        burnedIn.ApprovalEvidenceDigest
            == subtitleFree.ApprovalEvidenceDigest
        && burnedIn.UsedSourceSpansDigest
            == subtitleFree.UsedSourceSpansDigest
        && burnedIn.EvidenceDigest != subtitleFree.EvidenceDigest
        && HaveSameSourceSpans(
            burnedIn.UsedSourceSpans,
            subtitleFree.UsedSourceSpans);

    private static bool SubtitleVariantsMatch(
        SubtitleDeliveryVariantEvidence? actual,
        SubtitleDeliveryVariantEvidence expected) =>
        actual is not null
        && actual.Kind == expected.Kind
        && actual.RenderGraphId == expected.RenderGraphId
        && actual.DisplayProfileDigest == expected.DisplayProfileDigest
        && actual.SubtitleEvidenceDigest == expected.SubtitleEvidenceDigest
        && Equals(actual.SidecarArtifact, expected.SidecarArtifact);

    private static SubtitleVariantRunResult CreateRenderStoppedResult(
        SubtitleVariantStage stage,
        SubtitleVariantFailureReason reason,
        PreparedSubtitleEvidence prepared,
        FinalRenderRunResult? burnedInRender,
        OutputQcResult? burnedInQualityControl,
        DeliveryCreationResult? burnedInDelivery,
        FinalRenderRunResult? subtitleFreeRender,
        OutputQcResult? subtitleFreeQualityControl,
        DeliveryCreationResult? subtitleFreeDelivery,
        bool partialPairAlreadyCommitted)
    {
        FinalRenderRunResult result = subtitleFreeRender ?? burnedInRender
            ?? throw new InvalidOperationException(
                "A stopped render result is missing.");
        SubtitleVariantDisposition disposition = partialPairAlreadyCommitted
                || result.Disposition
                    == FinalRenderDisposition.ReconciliationRequired
            ? SubtitleVariantDisposition.ReconciliationRequired
            : result.Disposition == FinalRenderDisposition.Cancelled
                ? SubtitleVariantDisposition.Cancelled
                : SubtitleVariantDisposition.Failed;
        SubtitleVariantFailureReason? failureReason =
            disposition == SubtitleVariantDisposition.Cancelled
                ? null
                : reason;
        return new SubtitleVariantRunResult(
            stage,
            disposition,
            failureReason,
            prepared.EvidenceDigest,
            prepared.SidecarFile,
            burnedInRender,
            burnedInQualityControl,
            burnedInDelivery,
            subtitleFreeRender,
            subtitleFreeQualityControl,
            subtitleFreeDelivery);
    }

    private static SubtitleVariantRunResult CreateStoppedResult(
        SubtitleVariantStage stage,
        SubtitleVariantDisposition disposition,
        SubtitleVariantFailureReason reason,
        PreparedSubtitleEvidence prepared,
        FinalRenderRunResult? burnedInRender,
        OutputQcResult? burnedInQualityControl,
        DeliveryCreationResult? burnedInDelivery,
        FinalRenderRunResult? subtitleFreeRender,
        OutputQcResult? subtitleFreeQualityControl,
        DeliveryCreationResult? subtitleFreeDelivery) =>
        new(
            stage,
            disposition,
            reason,
            prepared.EvidenceDigest,
            prepared.SidecarFile,
            burnedInRender,
            burnedInQualityControl,
            burnedInDelivery,
            subtitleFreeRender,
            subtitleFreeQualityControl,
            subtitleFreeDelivery);

    private static bool HaveSameApprovals(
        IReadOnlyList<StoredApproval> first,
        IReadOnlyList<StoredApproval> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        Dictionary<EntityId<HubRadio.Core.Reviews.ApprovalIdTag>, StoredApproval>
            expected = second.ToDictionary(static approval => approval.Id);
        return first.Count == expected.Count
            && first.All(approval =>
                expected.TryGetValue(approval.Id, out StoredApproval? other)
                && StoredApprovalsMatch(approval, other));
    }

    private static bool StoredApprovalsMatch(
        StoredApproval first,
        StoredApproval second) =>
        first.Id == second.Id
        && first.ProjectId == second.ProjectId
        && first.CompositionVersionId == second.CompositionVersionId
        && first.CompositionVersionDigest == second.CompositionVersionDigest
        && string.Equals(first.ActorId, second.ActorId, StringComparison.Ordinal)
        && first.ActorRole == second.ActorRole
        && first.ApprovedAt.EqualsExact(second.ApprovedAt)
        && string.Equals(first.Reason, second.Reason, StringComparison.Ordinal);

    private static bool HaveSameInputDigests(
        IReadOnlyList<RenderInputEvidence> manifestInputs,
        IReadOnlyList<RenderGraphInput> graphInputs) =>
        manifestInputs.Count == graphInputs.Count
        && manifestInputs
            .Select(static input => input.ContentDigest.Hex)
            .OrderBy(static digest => digest, StringComparer.Ordinal)
            .SequenceEqual(
                graphInputs
                    .Select(static input => input.ContentDigest.Hex)
                    .OrderBy(static digest => digest, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static bool HaveSameFonts(
        IReadOnlyList<RenderFontEvidence> first,
        IReadOnlyList<RenderFontEvidence> second) =>
        first.Count == second.Count
        && first.All(font => second.Count(candidate =>
            candidate.Equals(font)) == 1);

    private static bool HaveSameTools(
        IReadOnlyList<RenderToolEvidence> first,
        IReadOnlyList<RenderToolEvidence> second) =>
        first.Count == second.Count
        && first.All(tool => second.Count(candidate =>
            candidate.Equals(tool)) == 1);

    private static bool HaveSameModels(
        IReadOnlyList<RenderModelEvidence> first,
        IReadOnlyList<RenderModelEvidence> second) =>
        first.Count == second.Count
        && first.All(model => second.Count(candidate =>
            candidate.Equals(model)) == 1);

    private static bool HaveSameSourceSpans(
        IReadOnlyList<SourceSpan> first,
        IReadOnlyList<SourceSpan> second) =>
        first.Count == second.Count
        && first.All(span => second.Count(candidate =>
            SourceSpansMatch(span, candidate)) == 1);

    private static bool SourceSpansMatch(SourceSpan first, SourceSpan second) =>
        first.Id == second.Id
        && first.AssetId == second.AssetId
        && string.Equals(first.StreamId, second.StreamId, StringComparison.Ordinal)
        && first.Range == second.Range
        && first.SourceDigest == second.SourceDigest
        && first.TranscriptRevisionId == second.TranscriptRevisionId
        && first.TranscriptTokenIds.SequenceEqual(second.TranscriptTokenIds);

    private static bool RenderInputsMatch(
        RenderGraphInput first,
        RenderGraphInput second) =>
        first.Id == second.Id
        && first.Kind == second.Kind
        && first.ContentDigest == second.ContentDigest
        && first.ByteSize == second.ByteSize
        && first.Origin == second.Origin;

    private static bool InputBindingMatches(
        RenderGraphInput input,
        FfmpegInputBinding binding) =>
        binding.InputId == input.Id
        && binding.VerificationRequest.ExpectedDigest == input.ContentDigest
        && binding.VerificationRequest.ExpectedByteSize == input.ByteSize;

    private static bool RenderNodesMatch(
        RenderNode first,
        RenderNode second) =>
        first.Id == second.Id
        && first.Kind == second.Kind
        && first.InputNodeIds.SequenceEqual(second.InputNodeIds)
        && first.ArtifactInputId == second.ArtifactInputId
        && first.OutputRange == second.OutputRange
        && first.SourceRange == second.SourceRange
        && first.Origin == second.Origin
        && string.Equals(
            first.RegionName,
            second.RegionName,
            StringComparison.Ordinal)
        && Equals(first.Cue, second.Cue)
        && Equals(first.Transition, second.Transition);

    private static bool HaveExactContextArtifacts(
        RenderGraph graph,
        RenderGraphValidationContext context) =>
        graph.Inputs.Count == context.Artifacts.Count
        && graph.Inputs.All(input => context.Artifacts.Count(artifact =>
            artifact.Id == input.Id
            && artifact.ContentDigest == input.ContentDigest
            && artifact.ByteSize == input.ByteSize) == 1);

    private static bool HaveSameToolBinding(
        FfmpegToolBinding first,
        FfmpegToolBinding second) =>
        HaveSameVerification(
            first.VerificationRequest,
            second.VerificationRequest);

    private static bool HaveSameVerification(
        BinaryVerificationRequest first,
        BinaryVerificationRequest second) =>
        string.Equals(
            first.ComponentIdentity,
            second.ComponentIdentity,
            StringComparison.Ordinal)
        && string.Equals(first.FilePath, second.FilePath, PathComparison)
        && first.ExpectedDigest == second.ExpectedDigest
        && first.ExpectedByteSize == second.ExpectedByteSize;

    private static bool DisplayResultsMatch(
        SubtitleDisplayResult first,
        SubtitleDisplayResult second)
    {
        if (first.ProfileVersionDigest != second.ProfileVersionDigest
            || first.SourceCueCount != second.SourceCueCount
            || !first.OmittedCueIds.SequenceEqual(second.OmittedCueIds)
            || first.DisplayedCues.Count != second.DisplayedCues.Count)
        {
            return false;
        }

        for (int index = 0; index < first.DisplayedCues.Count; index++)
        {
            SubtitleDisplayedCue left = first.DisplayedCues[index];
            SubtitleDisplayedCue right = second.DisplayedCues[index];
            if (!left.Cue.Equals(right.Cue)
                || left.SourceTextDigest != right.SourceTextDigest
                || !string.Equals(
                    left.DisplayText,
                    right.DisplayText,
                    StringComparison.Ordinal)
                || left.TextChanged != right.TextChanged)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LayoutEvidenceMatches(
        IReadOnlyList<OutputQcSubtitleLayoutEvidence> layouts,
        IReadOnlyList<SubtitleCueRevision> revisions) =>
        layouts.Count == revisions.Count
        && revisions.All(revision => layouts.Count(layout =>
            layout.CueId == revision.Snapshot.Cue.Id
            && SubtitleServiceContract.LayoutEquals(
                layout.Resolution,
                revision.Snapshot.LayoutResolution)) == 1);

    private static string ExpectedStorageReference(
        FinalRenderRequest request) =>
        string.Concat(
            "projects/",
            request.ProjectId.ToString(),
            "/renders/",
            request.OutputRelativePath);

    private static void AppendText(IncrementalHash hash, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendDigest(
        IncrementalHash hash,
        ContentDigest digest) =>
        AppendText(hash, digest.Hex);

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static RunRenderDelegate GetRunRenderDelegate(
        FinalRenderService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RunAsync;
    }

    private static RunQualityControlDelegate GetRunQualityControlDelegate(
        OutputQc qualityControl)
    {
        ArgumentNullException.ThrowIfNull(qualityControl);
        return qualityControl.RunAsync;
    }

    private static CreateDeliveryDelegate GetCreateDeliveryDelegate(
        DeliveryService deliveryService)
    {
        ArgumentNullException.ThrowIfNull(deliveryService);
        return (item, project, composition, qualityControl, variant, token) =>
            deliveryService.CreateAsync(
                item.DeliveryManifestId,
                project,
                composition,
                item.QualityControlRequest.RenderManifest,
                item.QualityControlRequest.ValidationContext,
                qualityControl,
                item.RenderRequest.ApprovalEvidence,
                item.ArtifactEvidence,
                variant,
                token);
    }

    private static SubtitleVariantException Failure(
        SubtitleVariantFailureReason reason) =>
        new(reason);

    private sealed class PairExecutionLease : IDisposable
    {
        private static readonly ConcurrentDictionary<PairExecutionKey, byte>
            ActivePairs = new();

        private PairExecutionKey? _key;

        private PairExecutionLease(PairExecutionKey key)
        {
            _key = key;
        }

        internal static PairExecutionLease Enter(
            FinalRenderPolicySnapshot first,
            FinalRenderPolicySnapshot second)
        {
            var key = new PairExecutionKey(first, second);
            if (!ActivePairs.TryAdd(key, 0))
            {
                throw Failure(
                    SubtitleVariantFailureReason.ExecutionConflict);
            }

            return new PairExecutionLease(key);
        }

        public void Dispose()
        {
            PairExecutionKey? key = Interlocked.Exchange(ref _key, null);
            if (key is not null)
            {
                _ = ActivePairs.TryRemove(key, out _);
            }
        }
    }

    private sealed class PairExecutionKey : IEquatable<PairExecutionKey>
    {
        internal PairExecutionKey(
            FinalRenderPolicySnapshot first,
            FinalRenderPolicySnapshot second)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            First = first;
            Second = second;
        }

        private FinalRenderPolicySnapshot First { get; }

        private FinalRenderPolicySnapshot Second { get; }

        public bool Equals(PairExecutionKey? other) =>
            other is not null
            && (ReferenceEquals(First, other.First)
                    && ReferenceEquals(Second, other.Second)
                || ReferenceEquals(First, other.Second)
                    && ReferenceEquals(Second, other.First));

        public override bool Equals(object? obj) =>
            obj is PairExecutionKey other && Equals(other);

        public override int GetHashCode() =>
            RuntimeHelpers.GetHashCode(First)
            ^ RuntimeHelpers.GetHashCode(Second);
    }

    internal delegate Task<FinalRenderRunResult> RunRenderDelegate(
        FinalRenderRequest request,
        CancellationToken cancellationToken);

    internal delegate Task<OutputQcResult> RunQualityControlDelegate(
        OutputQcRequest request,
        CancellationToken cancellationToken);

    internal delegate Task<DeliveryCreationResult> CreateDeliveryDelegate(
        SubtitleVariantWorkItem item,
        Project project,
        CompositionVersion compositionVersion,
        OutputQcResult qualityControl,
        SubtitleDeliveryVariantEvidence variant,
        CancellationToken cancellationToken);

    private sealed record PreparedSubtitleEvidence(
        ContentDigest EvidenceDigest,
        ContentDigest LayoutProfileDigest,
        SubtitleExportFile? SidecarFile);
}
