using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Delivery;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Web;

internal enum DeliveryBridgeStatus
{
    Available,
    Forbidden,
    Unavailable,
    Incomplete,
    Mismatched,
    QualityControlFailed,
}

internal enum DeliveryTraceStatus
{
    Resolved,
    NotFound,
}

internal enum DeliveryEvidenceProviderStatus
{
    Available,
    Unavailable,
    QualityControlFailed,
}

internal enum DeliveryEvidenceIssue
{
    Incomplete,
    Mismatched,
}

internal sealed class DeliveryEvidenceContext
{
    internal DeliveryEvidenceContext(
        DeliveryCreationResult delivery,
        RenderManifest renderManifest,
        RenderGraph renderGraph,
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        StoredVersionEvidence reviewEvidence,
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            artifactEvidence,
        IReadOnlyList<StoredArtifact> registeredArtifacts)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(renderManifest);
        ArgumentNullException.ThrowIfNull(renderGraph);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(reviewEvidence);
        ArgumentNullException.ThrowIfNull(artifactEvidence);
        ArgumentNullException.ThrowIfNull(registeredArtifacts);

        Delivery = delivery;
        RenderManifest = renderManifest;
        RenderGraph = renderGraph;
        ValidationContext = validationContext;
        QualityControl = qualityControl;
        ReviewEvidence = reviewEvidence;
        ArtifactEvidence = Array.AsReadOnly(artifactEvidence.ToArray());
        RegisteredArtifacts = Array.AsReadOnly(registeredArtifacts.ToArray());
    }

    internal DeliveryCreationResult Delivery { get; }

    internal RenderManifest RenderManifest { get; }

    internal RenderGraph RenderGraph { get; }

    internal RenderGraphValidationContext ValidationContext { get; }

    internal OutputQcResult QualityControl { get; }

    internal StoredVersionEvidence ReviewEvidence { get; }

    internal IReadOnlyList<
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>> ArtifactEvidence
    {
        get;
    }

    internal IReadOnlyList<StoredArtifact> RegisteredArtifacts { get; }
}

internal sealed class DeliveryEvidenceProviderResult
{
    private DeliveryEvidenceProviderResult(
        DeliveryEvidenceProviderStatus status,
        DeliveryEvidenceContext? context)
    {
        if ((status == DeliveryEvidenceProviderStatus.Available) !=
            (context is not null))
        {
            throw new ArgumentException(
                "Only available delivery evidence can include a context.",
                nameof(context));
        }

        Status = status;
        Context = context;
    }

    internal DeliveryEvidenceProviderStatus Status { get; }

    internal DeliveryEvidenceContext? Context { get; }

    internal static DeliveryEvidenceProviderResult Available(
        DeliveryEvidenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DeliveryEvidenceProviderResult(
            DeliveryEvidenceProviderStatus.Available,
            context);
    }

    internal static DeliveryEvidenceProviderResult Unavailable() =>
        new(DeliveryEvidenceProviderStatus.Unavailable, context: null);

    internal static DeliveryEvidenceProviderResult QualityControlFailed() =>
        new(DeliveryEvidenceProviderStatus.QualityControlFailed, context: null);
}

internal interface IDeliveryEvidenceProvider
{
    ValueTask<DeliveryEvidenceProviderResult> ReadAsync(
        EntityId<CompositionVersionIdTag> versionId,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableDeliveryEvidenceProvider :
    IDeliveryEvidenceProvider
{
    internal static UnavailableDeliveryEvidenceProvider Instance { get; } = new();

    private UnavailableDeliveryEvidenceProvider()
    {
    }

    public ValueTask<DeliveryEvidenceProviderResult> ReadAsync(
        EntityId<CompositionVersionIdTag> versionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            DeliveryEvidenceProviderResult.Unavailable());
    }
}

internal interface IDeliveryAccessAuthorization
{
    void DemandRead();
}

internal sealed class DeliveryAccessAuthorization :
    IDeliveryAccessAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal DeliveryAccessAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorProvider(actorProvider))
    {
    }

    internal DeliveryAccessAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public void DemandRead()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.ViewProject);
    }

    private static Func<string> GetActorProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class DeniedDeliveryAccessAuthorization :
    IDeliveryAccessAuthorization
{
    internal static DeniedDeliveryAccessAuthorization Instance { get; } = new();

    private DeniedDeliveryAccessAuthorization()
    {
    }

    public void DemandRead() => throw new AuthorizationException(
        AuthorizationOperation.ViewProject,
        actingRole: null);
}

internal sealed record DeliveryArtifactEvidence(
    string ArtifactId,
    string ArtifactType,
    string MediaType,
    string? Language,
    long ByteSize,
    string Sha256);

internal sealed record DeliveryReviewRecordEvidence(
    string ReviewRecordId,
    ReviewState State,
    ReviewActorRole ActorRole,
    DateTimeOffset RecordedAt);

internal sealed record DeliveryApprovalEvidence(
    string ApprovalId,
    ReviewActorRole ActorRole,
    DateTimeOffset ApprovedAt);

internal sealed record DeliveryRightsEvidence(
    string AssetId,
    string SourceDigest,
    RightsStatus Status,
    bool EvidenceRecorded,
    string AllowedUse,
    bool ExternalPublicationAllowed);

internal sealed record DeliveryQcStreamEvidence(
    int StreamIndex,
    int ObservedCount,
    RationalTime PresentationStart,
    RationalTime PresentationEnd,
    RationalTime ObservedDuration,
    string EvidenceDigest);

internal sealed record DeliveryQcDurationEvidence(
    RationalTime ExpectedDuration,
    RationalTime VideoDurationDelta,
    RationalTime? AudioDurationDelta,
    RationalTime? AudioVideoStartOffset,
    RationalTime? AudioVideoEndOffset);

internal sealed record DeliveryQcLumaEvidence(
    int MeasuredFrameCount,
    decimal MinimumFrameAverage,
    decimal MaximumFrameAverage,
    decimal MeanFrameAverage,
    string EvidenceDigest);

internal sealed record DeliveryQcMeasuredValueEvidence(
    OutputQcMeasuredValueKind Kind,
    decimal? Value);

internal sealed record DeliveryQcAudioEvidence(
    DeliveryQcMeasuredValueEvidence IntegratedLoudnessLufs,
    DeliveryQcMeasuredValueEvidence TruePeakDbfs,
    string EvidenceDigest);

internal sealed record DeliveryQcToolEvidence(
    string Name,
    string Version,
    string ExecutableDigest,
    long ExecutableByteSize);

internal sealed record DeliveryQcEvidence(
    string ArtifactId,
    string Sha256,
    long ByteSize,
    bool MachineChecksPassed,
    bool HumanReviewRequired,
    bool ApprovedNumericThresholdsApplied,
    bool CanProceedToDelivery,
    bool DecodedSuccessfully,
    string ProbeEvidenceDigest,
    DeliveryQcStreamEvidence VideoTiming,
    DeliveryQcStreamEvidence? AudioTiming,
    int? SubtitleStreamIndex,
    int? ObservedSubtitleCueCount,
    string? SubtitleTimingEvidenceDigest,
    DeliveryQcDurationEvidence Duration,
    DeliveryQcLumaEvidence Luma,
    DeliveryQcAudioEvidence? Audio,
    IReadOnlyList<DeliveryQcToolEvidence> Tools,
    IReadOnlyList<OutputQcReviewArea> RequiredHumanReviewAreas);

internal sealed record DeliverySourceSpanEvidence(
    string SourceSpanId,
    string AssetId,
    string StreamId,
    RationalTime Start,
    RationalTime End,
    string SourceDigest);

internal sealed record DeliveryTraceEvidence(
    DeliveryTraceStatus Status,
    RationalTime OutputTime,
    OriginKind? OriginKind,
    string? OriginId,
    RationalTime? SourceTime,
    DeliverySourceSpanEvidence? SourceSpan);

internal sealed record DeliveryBridgeSnapshot(
    string DeliveryManifestId,
    string ProjectId,
    string VersionId,
    string VersionDigest,
    string RenderManifestId,
    string ProfileId,
    string ProfileDigest,
    string DeliveryEvidenceDigest,
    string UsedSourceSpansDigest,
    string ApprovalEvidenceDigest,
    PublicationScope PublicationScope,
    IReadOnlyList<DeliveryArtifactEvidence> Artifacts,
    IReadOnlyList<DeliveryReviewRecordEvidence> ReviewRecords,
    IReadOnlyList<DeliveryApprovalEvidence> Approvals,
    IReadOnlyList<DeliveryRightsEvidence> Rights,
    DeliveryQcEvidence QualityControl);

internal sealed class DeliveryBridgeReadResult
{
    private DeliveryBridgeReadResult(
        DeliveryBridgeStatus status,
        DeliveryBridgeSnapshot? snapshot,
        DeliveryTraceEvidence? trace)
    {
        if ((status == DeliveryBridgeStatus.Available) !=
            (snapshot is not null))
        {
            throw new ArgumentException(
                "Only an available delivery result can include a snapshot.",
                nameof(snapshot));
        }

        if (snapshot is null && trace is not null)
        {
            throw new ArgumentException(
                "A trace requires an available delivery snapshot.",
                nameof(trace));
        }

        Status = status;
        Snapshot = snapshot;
        Trace = trace;
    }

    internal DeliveryBridgeStatus Status { get; }

    internal DeliveryBridgeSnapshot? Snapshot { get; }

    internal DeliveryTraceEvidence? Trace { get; }

    internal static DeliveryBridgeReadResult Available(
        DeliveryBridgeSnapshot snapshot,
        DeliveryTraceEvidence? trace)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new DeliveryBridgeReadResult(
            DeliveryBridgeStatus.Available,
            snapshot,
            trace);
    }

    internal static DeliveryBridgeReadResult Failure(
        DeliveryBridgeStatus status)
    {
        if (status == DeliveryBridgeStatus.Available)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "An available result requires a snapshot.");
        }

        return new DeliveryBridgeReadResult(status, snapshot: null, trace: null);
    }
}

internal sealed class DeliveryEvidenceValidationException : Exception
{
    internal DeliveryEvidenceValidationException(DeliveryEvidenceIssue issue)
        : base("The delivery evidence is not safe to expose.")
    {
        Issue = issue;
    }

    internal DeliveryEvidenceIssue Issue { get; }
}

internal static class DeliveryEvidenceSnapshotFactory
{
    private const int MaxCollectionCount = 1024;
    private const int MaxTextUtf8Bytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static DeliveryBridgeSnapshot Create(
        EntityId<CompositionVersionIdTag> requestedVersionId,
        DeliveryEvidenceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedVersionId);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RenderGraphValidationContext validation = context.ValidationContext;
        Project project = validation.Project;
        CompositionVersion version = validation.CompositionVersion;
        RenderManifest render = context.RenderManifest;
        DeliveryCreationResult delivery = context.Delivery;
        DeliveryManifest manifest = delivery.Manifest;
        OutputQcResult qualityControl = context.QualityControl;

        Require(
            requestedVersionId == version.Id
            && version.ProjectId == project.Id
            && manifest.ProjectId == project.Id
            && manifest.CompositionVersionId == version.Id
            && manifest.CompositionVersionDigest == version.ContentDigest
            && manifest.RenderManifestId == render.Id
            && manifest.ProfileId == render.ProfileId
            && manifest.ProfileDigest == render.ProfileDigest,
            DeliveryEvidenceIssue.Mismatched);

        Require(
            render.ProjectId == project.Id
            && render.CompositionVersionId == version.Id
            && render.CompositionVersionDigest == version.ContentDigest
            && render.TimelineMapId == validation.TimelineMap.Id
            && render.TimelineMapDigest == validation.TimelineMapDigest
            && render.ProfileId == validation.Profile.Id
            && render.ProfileDigest == validation.Profile.ProfileDigest
            && render.TemplateId == validation.Template.Id
            && render.TemplateVersionDigest == validation.Template.VersionDigest,
            DeliveryEvidenceIssue.Mismatched);

        RenderGraphValidationResult graphValidation =
            RenderGraphValidator.Validate(
                context.RenderGraph,
                validation,
                cancellationToken);
        Require(graphValidation.IsValid, DeliveryEvidenceIssue.Mismatched);
        Require(
            context.RenderGraph.Id == qualityControl.RenderGraphId,
            DeliveryEvidenceIssue.Mismatched);

        IReadOnlyList<
            KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            canonicalArtifacts = ValidateArtifacts(
                context,
                cancellationToken);
        ValidateRegisteredArtifacts(
            context,
            canonicalArtifacts,
            cancellationToken);
        ValidateQualityControl(context);
        SourceSpan[] usedSourceSpans = ResolveUsedSourceSpans(
            validation,
            cancellationToken);
        ValidateUsedSourceSpans(
            delivery,
            project,
            version,
            usedSourceSpans);
        ValidateReviewEvidence(context, project, version);
        ValidateDeliveryEvidenceDigest(context, cancellationToken);

        IReadOnlyList<DeliveryArtifactEvidence> artifacts =
            CreateArtifactEvidence(canonicalArtifacts);
        IReadOnlyList<DeliveryReviewRecordEvidence> reviewRecords =
            CreateReviewRecords(context.ReviewEvidence.ReviewRecords);
        IReadOnlyList<DeliveryApprovalEvidence> approvals =
            CreateApprovals(context.ReviewEvidence.Approvals);
        IReadOnlyList<DeliveryRightsEvidence> rights = CreateRightsEvidence(
            project,
            validation,
            cancellationToken);
        DeliveryQcEvidence qc = CreateQualityControlEvidence(qualityControl);

        return new DeliveryBridgeSnapshot(
            manifest.Id.ToString(),
            project.Id.ToString(),
            version.Id.ToString(),
            version.ContentDigest.Hex,
            render.Id.ToString(),
            render.ProfileId.ToString(),
            render.ProfileDigest.Hex,
            delivery.EvidenceDigest.Hex,
            delivery.UsedSourceSpansDigest.Hex,
            delivery.ApprovalEvidenceDigest.Hex,
            project.PublicationScope,
            artifacts,
            reviewRecords,
            approvals,
            rights,
            qc);
    }

    internal static DeliveryTraceEvidence? CreateTrace(
        RationalTime? outputTime,
        DeliveryEvidenceContext context)
    {
        if (outputTime is null)
        {
            return null;
        }

        TimelineMap timelineMap = context.ValidationContext.TimelineMap;
        if (!timelineMap.TryMapOutputTime(
                outputTime.Value,
                out TimelineMapPoint point))
        {
            return new DeliveryTraceEvidence(
                DeliveryTraceStatus.NotFound,
                outputTime.Value,
                OriginKind: null,
                OriginId: null,
                SourceTime: null,
                SourceSpan: null);
        }

        string originId = point.Origin.Kind switch
        {
            OriginKind.SourceSpan => point.Origin.SourceSpanId!.ToString(),
            OriginKind.Asset => point.Origin.AssetId!.ToString(),
            OriginKind.EditorialRecord =>
                point.Origin.EditorialRecordId!.ToString(),
            _ => throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Mismatched),
        };

        DeliverySourceSpanEvidence? sourceSpan = null;
        if (point.Origin.SourceSpanId is EntityId<SourceSpanIdTag> sourceSpanId)
        {
            SourceSpan span = context.Delivery.UsedSourceSpans.Single(
                candidate => candidate.Id == sourceSpanId);
            sourceSpan = new DeliverySourceSpanEvidence(
                span.Id.ToString(),
                span.AssetId.ToString(),
                span.StreamId,
                span.Range.Start,
                span.Range.End,
                span.SourceDigest.Hex);
        }

        return new DeliveryTraceEvidence(
            DeliveryTraceStatus.Resolved,
            outputTime.Value,
            point.Origin.Kind,
            originId,
            point.SourceTime,
            sourceSpan);
    }

    private static IReadOnlyList<
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
        ValidateArtifacts(
        DeliveryEvidenceContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ManifestArtifact> declared =
            context.Delivery.Manifest.Artifacts;
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            supplied = context.ArtifactEvidence;
        RequireCount(declared.Count);
        RequireCount(supplied.Count);

        if (supplied.Count < declared.Count)
        {
            throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Incomplete);
        }

        Require(
            supplied.Count == declared.Count,
            DeliveryEvidenceIssue.Mismatched);
        var artifactIds = new HashSet<EntityId<ArtifactIdTag>>();
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>[] orderedSupplied =
            supplied
                .OrderBy(
                    static binding => binding.Key.ToString(),
                    StringComparer.Ordinal)
                .ToArray();
        foreach (KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact> binding
            in orderedSupplied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Key is null
                || binding.Value is null
                || !artifactIds.Add(binding.Key))
            {
                throw new DeliveryEvidenceValidationException(
                    DeliveryEvidenceIssue.Mismatched);
            }

            RequireBoundedArtifact(binding.Value);
        }

        var matched = new bool[orderedSupplied.Length];
        var canonical = new KeyValuePair<
            EntityId<ArtifactIdTag>, ManifestArtifact>[declared.Count];
        for (int declaredIndex = 0;
            declaredIndex < declared.Count;
            declaredIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int matchIndex = -1;
            for (int suppliedIndex = 0;
                suppliedIndex < orderedSupplied.Length;
                suppliedIndex++)
            {
                if (!matched[suppliedIndex]
                    && declared[declaredIndex].Equals(
                        orderedSupplied[suppliedIndex].Value))
                {
                    matchIndex = suppliedIndex;
                    break;
                }
            }

            Require(matchIndex >= 0, DeliveryEvidenceIssue.Mismatched);
            matched[matchIndex] = true;
            canonical[declaredIndex] = orderedSupplied[matchIndex];
        }

        foreach (ManifestArtifact output in context.RenderManifest.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (supplied.Count(binding => binding.Value.Equals(output)) == 0)
            {
                throw new DeliveryEvidenceValidationException(
                    DeliveryEvidenceIssue.Incomplete);
            }

            Require(
                supplied.Count(binding => binding.Value.Equals(output)) == 1,
                DeliveryEvidenceIssue.Mismatched);
        }

        return Array.AsReadOnly(canonical);
    }

    private static void ValidateRegisteredArtifacts(
        DeliveryEvidenceContext context,
        IReadOnlyList<KeyValuePair<
            EntityId<ArtifactIdTag>,
            ManifestArtifact>> canonicalArtifacts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredArtifact> registered = context.RegisteredArtifacts;
        RequireCount(registered.Count);
        if (registered.Count < canonicalArtifacts.Count)
        {
            throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Incomplete);
        }

        Require(
            registered.Count == canonicalArtifacts.Count,
            DeliveryEvidenceIssue.Mismatched);
        var byId = new Dictionary<EntityId<ArtifactIdTag>, StoredArtifact>();
        foreach (StoredArtifact artifact in registered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(
                artifact is not null
                && Enum.IsDefined(artifact.Kind)
                && byId.TryAdd(artifact.Id, artifact),
                DeliveryEvidenceIssue.Mismatched);
        }

        foreach (KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact> binding
            in canonicalArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(binding.Key, out StoredArtifact? artifact))
            {
                throw new DeliveryEvidenceValidationException(
                    DeliveryEvidenceIssue.Incomplete);
            }

            Require(
                artifact.Id == binding.Key
                && artifact.ProjectId == context.ValidationContext.Project.Id
                && artifact.ContentDigest == binding.Value.Sha256
                && artifact.ByteSize == binding.Value.ByteSize
                && string.Equals(
                    artifact.StorageReference,
                    binding.Value.StorageReference,
                    StringComparison.Ordinal)
                && (binding.Key != context.QualityControl.ArtifactId
                    || artifact.Kind == ArtifactKind.Render),
                DeliveryEvidenceIssue.Mismatched);
        }
    }

    private static void ValidateQualityControl(
        DeliveryEvidenceContext context)
    {
        OutputQcResult qc = context.QualityControl;
        RenderGraphValidationContext validation = context.ValidationContext;
        RenderManifest render = context.RenderManifest;
        Project project = validation.Project;
        CompositionVersion version = validation.CompositionVersion;
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>? binding =
            context.ArtifactEvidence
                .Where(candidate => candidate.Key == qc.ArtifactId)
                .Cast<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>?>()
                .SingleOrDefault();

        if (binding is null)
        {
            throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Incomplete);
        }

        ManifestArtifact artifact = binding.Value.Value;
        Require(
            qc.MachineChecksPassed
            && qc.Decode.DecodedSuccessfully
            && qc.HumanReviewRequired
            && !qc.ApprovedNumericThresholdsApplied
            && !qc.CanProceedToDelivery
            && qc.ProjectId == project.Id
            && qc.RenderManifestId == render.Id
            && qc.RenderGraphId == context.RenderGraph.Id
            && qc.CompositionVersionId == version.Id
            && qc.CompositionVersionDigest == version.ContentDigest
            && qc.ProfileId == render.ProfileId
            && qc.ProfileDigest == render.ProfileDigest
            && qc.LayoutSpecId == validation.LayoutSpec.Id
            && qc.LayoutSpecDigest == validation.LayoutSpecDigest
            && qc.TemplateId == render.TemplateId
            && qc.TemplateVersionDigest == render.TemplateVersionDigest
            && qc.ContentDigest == artifact.Sha256
            && qc.ByteSize == artifact.ByteSize
            && render.Outputs.Count(output => output.Equals(artifact)) == 1,
            DeliveryEvidenceIssue.Mismatched);
    }

    private static SourceSpan[] ResolveUsedSourceSpans(
        RenderGraphValidationContext validation,
        CancellationToken cancellationToken)
    {
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans =
            validation.SourceSpans.ToDictionary(static span => span.Id);
        Dictionary<EntityId<AssetIdTag>, Asset> assets =
            validation.Project.Assets.ToDictionary(static asset => asset.Id);
        HashSet<EntityId<EditorialRecordIdTag>> editorialRecordIds =
            validation.EditorialRecordIds.ToHashSet();
        var used = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();

        foreach (TimelineMapEntry entry in validation.TimelineMap.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (entry.Origin.Kind)
            {
                case OriginKind.SourceSpan
                    when entry.Origin.SourceSpanId is
                        EntityId<SourceSpanIdTag> sourceSpanId
                        && sourceSpans.TryGetValue(sourceSpanId, out SourceSpan? span)
                        && assets.TryGetValue(span.AssetId, out Asset? asset)
                        && asset.ContentDigest == span.SourceDigest
                        && entry.SourceRange is TimeRange sourceRange
                        && sourceRange.Start >= span.Range.Start
                        && sourceRange.End <= span.Range.End:
                    used.TryAdd(span.Id, span);
                    break;
                case OriginKind.Asset
                    when entry.Origin.AssetId is EntityId<AssetIdTag> assetId
                        && assets.ContainsKey(assetId)
                        && entry.SourceRange is null:
                    break;
                case OriginKind.EditorialRecord
                    when entry.Origin.EditorialRecordId is
                        EntityId<EditorialRecordIdTag> editorialRecordId
                        && editorialRecordIds.Contains(editorialRecordId)
                        && entry.SourceRange is null:
                    break;
                default:
                    throw new DeliveryEvidenceValidationException(
                        DeliveryEvidenceIssue.Mismatched);
            }
        }

        return used.Values
            .OrderBy(static span => span.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateUsedSourceSpans(
        DeliveryCreationResult delivery,
        Project project,
        CompositionVersion version,
        IReadOnlyList<SourceSpan> expected)
    {
        RequireCount(delivery.UsedSourceSpans.Count);
        Require(
            delivery.UsedSourceSpans.Count == expected.Count,
            delivery.UsedSourceSpans.Count < expected.Count
                ? DeliveryEvidenceIssue.Incomplete
                : DeliveryEvidenceIssue.Mismatched);

        for (int index = 0; index < expected.Count; index++)
        {
            Require(
                SourceSpansMatch(delivery.UsedSourceSpans[index], expected[index]),
                DeliveryEvidenceIssue.Mismatched);
        }

        ContentDigest digest = DeliveryService.ComputeUsedSourceSpansDigest(
            project.Id,
            version,
            expected);
        Require(
            digest == delivery.UsedSourceSpansDigest,
            DeliveryEvidenceIssue.Mismatched);
    }

    private static void ValidateReviewEvidence(
        DeliveryEvidenceContext context,
        Project project,
        CompositionVersion version)
    {
        StoredVersionEvidence evidence = context.ReviewEvidence;
        Require(
            evidence.ProjectId == project.Id
            && evidence.CompositionVersionId == version.Id
            && evidence.CompositionVersionDigest == version.ContentDigest,
            DeliveryEvidenceIssue.Mismatched);
        RequireCount(evidence.ReviewRecords.Count);
        RequireCount(evidence.ReviewComments.Count);
        RequireCount(evidence.Approvals.Count);
        if (evidence.Approvals.Count == 0)
        {
            throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Incomplete);
        }

        Require(
            evidence.ReviewRecords.All(record =>
                record.ProjectId == project.Id
                && record.CompositionVersionId == version.Id
                && record.CompositionVersionDigest == version.ContentDigest),
            DeliveryEvidenceIssue.Mismatched);
        Require(
            evidence.ReviewComments.All(comment =>
                comment.ProjectId == project.Id
                && comment.CompositionVersionId == version.Id
                && comment.CompositionVersionDigest == version.ContentDigest),
            DeliveryEvidenceIssue.Mismatched);
        Require(
            evidence.Approvals.All(approval =>
                approval.ProjectId == project.Id
                && approval.CompositionVersionId == version.Id
                && approval.CompositionVersionDigest == version.ContentDigest),
            DeliveryEvidenceIssue.Mismatched);
        Require(
            evidence.Approvals.All(static approval =>
                approval.ActorRole is
                    ReviewActorRole.ContentApprover or
                    ReviewActorRole.BrandApprover or
                    ReviewActorRole.RightsApprover),
            DeliveryEvidenceIssue.Mismatched);
        Require(
            evidence.ReviewRecords.Select(static record => record.Id).Distinct()
                .Count() == evidence.ReviewRecords.Count
            && evidence.ReviewComments.Select(static comment => comment.Id).Distinct()
                .Count() == evidence.ReviewComments.Count
            && evidence.Approvals.Select(static approval => approval.Id).Distinct()
                .Count() == evidence.Approvals.Count,
            DeliveryEvidenceIssue.Mismatched);

        ContentDigest digest = DeliveryService.ComputeApprovalEvidenceDigest(
            project.Id,
            version,
            evidence.Approvals);
        Require(
            digest == context.Delivery.ApprovalEvidenceDigest,
            DeliveryEvidenceIssue.Mismatched);
    }

    private static void ValidateDeliveryEvidenceDigest(
        DeliveryEvidenceContext context,
        CancellationToken cancellationToken)
    {
        DeliveryCreationResult delivery = context.Delivery;
        ContentDigest digest = DeliveryService.ComputeDeliveryEvidenceDigest(
            delivery.Manifest,
            context.RenderManifest,
            context.QualityControl,
            delivery.UsedSourceSpansDigest,
            delivery.ApprovalEvidenceDigest,
            context.ArtifactEvidence,
            context.RegisteredArtifacts,
            cancellationToken);
        Require(
            digest == delivery.EvidenceDigest,
            DeliveryEvidenceIssue.Mismatched);
    }

    private static IReadOnlyList<DeliveryArtifactEvidence>
        CreateArtifactEvidence(
            IReadOnlyList<
                KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>> bindings)
    {
        return Array.AsReadOnly(bindings
            .Select(static binding => new DeliveryArtifactEvidence(
                binding.Key.ToString(),
                binding.Value.ArtifactType,
                binding.Value.MediaType,
                binding.Value.Language,
                binding.Value.ByteSize,
                binding.Value.Sha256.Hex))
            .ToArray());
    }

    private static IReadOnlyList<DeliveryReviewRecordEvidence>
        CreateReviewRecords(IReadOnlyList<StoredReviewRecord> records)
    {
        return Array.AsReadOnly(records
            .OrderBy(static record => record.RecordedAt)
            .ThenBy(static record => record.Id.ToString(), StringComparer.Ordinal)
            .Select(static record => new DeliveryReviewRecordEvidence(
                record.Id.ToString(),
                record.State,
                record.ActorRole,
                record.RecordedAt))
            .ToArray());
    }

    private static IReadOnlyList<DeliveryApprovalEvidence> CreateApprovals(
        IReadOnlyList<StoredApproval> approvals)
    {
        return Array.AsReadOnly(approvals
            .OrderBy(static approval => approval.ApprovedAt)
            .ThenBy(static approval => approval.Id.ToString(), StringComparer.Ordinal)
            .Select(static approval => new DeliveryApprovalEvidence(
                approval.Id.ToString(),
                approval.ActorRole,
                approval.ApprovedAt))
            .ToArray());
    }

    private static IReadOnlyList<DeliveryRightsEvidence> CreateRightsEvidence(
        Project project,
        RenderGraphValidationContext validation,
        CancellationToken cancellationToken)
    {
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans =
            validation.SourceSpans.ToDictionary(static span => span.Id);
        Dictionary<EntityId<AssetIdTag>, Asset> assets =
            project.Assets.ToDictionary(static asset => asset.Id);
        var usedAssetIds = new HashSet<EntityId<AssetIdTag>>();
        foreach (TimelineMapEntry entry in validation.TimelineMap.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Origin.AssetId is EntityId<AssetIdTag> assetId)
            {
                usedAssetIds.Add(assetId);
            }
            else if (entry.Origin.SourceSpanId is
                EntityId<SourceSpanIdTag> sourceSpanId)
            {
                usedAssetIds.Add(sourceSpans[sourceSpanId].AssetId);
            }
        }

        RequireCount(usedAssetIds.Count);
        DeliveryRightsEvidence[] rights = usedAssetIds
            .OrderBy(static id => id.ToString(), StringComparer.Ordinal)
            .Select(assetId =>
            {
                Asset asset = assets[assetId];
                RequireBoundedText(asset.Rights.AllowedUse);
                return new DeliveryRightsEvidence(
                    asset.Id.ToString(),
                    asset.ContentDigest.Hex,
                    asset.Rights.Status,
                    asset.Rights.EvidenceReference is not null,
                    asset.Rights.AllowedUse,
                    asset.Rights.ExternalPublicationAllowed);
            })
            .ToArray();
        return Array.AsReadOnly(rights);
    }

    private static DeliveryQcEvidence CreateQualityControlEvidence(
        OutputQcResult qc)
    {
        RequireCount(qc.Tools.Count);
        RequireCount(qc.RequiredHumanReviewAreas.Count);
        DeliveryQcStreamEvidence video = CreateStreamEvidence(qc.VideoTiming);
        DeliveryQcStreamEvidence? audio = qc.AudioTiming is null
            ? null
            : CreateStreamEvidence(qc.AudioTiming);
        DeliveryQcAudioEvidence? audioMeasurements = qc.AudioMeasurements is null
            ? null
            : new DeliveryQcAudioEvidence(
                CreateMeasuredValue(qc.AudioMeasurements.IntegratedLoudnessLufs),
                CreateMeasuredValue(qc.AudioMeasurements.TruePeakDbfs),
                qc.AudioMeasurements.EvidenceDigest.Hex);
        DeliveryQcToolEvidence[] tools = qc.Tools
            .Select(static tool =>
            {
                RequireBoundedText(tool.Name);
                RequireBoundedText(tool.Version);
                return new DeliveryQcToolEvidence(
                    tool.Name,
                    tool.Version,
                    tool.ExecutableDigest.Hex,
                    tool.ExecutableByteSize);
            })
            .ToArray();

        return new DeliveryQcEvidence(
            qc.ArtifactId.ToString(),
            qc.ContentDigest.Hex,
            qc.ByteSize,
            qc.MachineChecksPassed,
            qc.HumanReviewRequired,
            qc.ApprovedNumericThresholdsApplied,
            qc.CanProceedToDelivery,
            qc.Decode.DecodedSuccessfully,
            qc.ProbeEvidenceDigest.Hex,
            video,
            audio,
            qc.SubtitleTiming?.StreamIndex,
            qc.SubtitleTiming?.ObservedCueCount,
            qc.SubtitleTiming?.EvidenceDigest.Hex,
            new DeliveryQcDurationEvidence(
                qc.DurationMeasurements.ExpectedDuration,
                qc.DurationMeasurements.VideoDurationDelta,
                qc.DurationMeasurements.AudioDurationDelta,
                qc.DurationMeasurements.AudioVideoStartOffset,
                qc.DurationMeasurements.AudioVideoEndOffset),
            new DeliveryQcLumaEvidence(
                qc.LumaMeasurements.MeasuredFrameCount,
                qc.LumaMeasurements.MinimumFrameAverage,
                qc.LumaMeasurements.MaximumFrameAverage,
                qc.LumaMeasurements.MeanFrameAverage,
                qc.LumaMeasurements.EvidenceDigest.Hex),
            audioMeasurements,
            Array.AsReadOnly(tools),
            Array.AsReadOnly(qc.RequiredHumanReviewAreas.ToArray()));
    }

    private static DeliveryQcStreamEvidence CreateStreamEvidence(
        OutputQcStreamTiming timing) =>
        new(
            timing.StreamIndex,
            timing.ObservedFrameCount,
            timing.PresentationStart,
            timing.PresentationEnd,
            timing.ObservedDuration,
            timing.EvidenceDigest.Hex);

    private static DeliveryQcMeasuredValueEvidence CreateMeasuredValue(
        OutputQcMeasuredValue value) =>
        new(value.Kind, value.FiniteValue);

    private static bool SourceSpansMatch(SourceSpan first, SourceSpan second) =>
        first.Id == second.Id
        && first.AssetId == second.AssetId
        && string.Equals(first.StreamId, second.StreamId, StringComparison.Ordinal)
        && first.Range == second.Range
        && first.SourceDigest == second.SourceDigest
        && first.TranscriptRevisionId == second.TranscriptRevisionId
        && first.TranscriptTokenIds.SequenceEqual(second.TranscriptTokenIds);

    private static void RequireBoundedArtifact(ManifestArtifact artifact)
    {
        RequireBoundedText(artifact.ArtifactType);
        RequireBoundedText(artifact.MediaType);
        if (artifact.Language is not null)
        {
            RequireBoundedText(artifact.Language);
        }
    }

    private static void RequireBoundedText(string value)
    {
        try
        {
            Require(
                StrictUtf8.GetByteCount(value) <= MaxTextUtf8Bytes,
                DeliveryEvidenceIssue.Mismatched);
        }
        catch (EncoderFallbackException)
        {
            throw new DeliveryEvidenceValidationException(
                DeliveryEvidenceIssue.Mismatched);
        }
    }

    private static void RequireCount(int count)
    {
        Require(
            count is >= 0 and <= MaxCollectionCount,
            DeliveryEvidenceIssue.Mismatched);
    }

    private static void Require(bool condition, DeliveryEvidenceIssue issue)
    {
        if (!condition)
        {
            throw new DeliveryEvidenceValidationException(issue);
        }
    }
}

internal sealed class DeliveryBridgeRequestHandler
{
    private readonly IDeliveryEvidenceProvider provider;
    private readonly IDeliveryAccessAuthorization authorization;

    internal DeliveryBridgeRequestHandler(
        IDeliveryEvidenceProvider provider,
        IDeliveryAccessAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(authorization);
        this.provider = provider;
        this.authorization = authorization;
    }

    internal async Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != BridgeMethod.DeliveryRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Method,
                "The delivery handler only accepts delivery.read.");
        }

        EntityId<CompositionVersionIdTag> versionId =
            EntityId<CompositionVersionIdTag>.Parse(
                request.Parameters.GetProperty("versionId").GetString()!);
        RationalTime? outputTime = ReadOutputTime(
            request.Parameters.GetProperty("outputTime"));
        DeliveryBridgeReadResult result = await ReadAsync(
            versionId,
            outputTime,
            cancellationToken).ConfigureAwait(false);
        return DeliveryBridgeJson.WriteResponse(request, result);
    }

    internal async Task<DeliveryBridgeReadResult> ReadAsync(
        EntityId<CompositionVersionIdTag> versionId,
        RationalTime? outputTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            authorization.DemandRead();
        }
        catch (AuthorizationException)
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Forbidden);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Unavailable);
        }

        DeliveryEvidenceProviderResult providerResult;
        try
        {
            providerResult = await provider.ReadAsync(
                versionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Unavailable);
        }

        if (providerResult is null ||
            providerResult.Status == DeliveryEvidenceProviderStatus.Unavailable)
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Unavailable);
        }

        if (providerResult.Status ==
            DeliveryEvidenceProviderStatus.QualityControlFailed)
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.QualityControlFailed);
        }

        if (providerResult.Status != DeliveryEvidenceProviderStatus.Available ||
            providerResult.Context is null)
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Mismatched);
        }

        try
        {
            DeliveryBridgeSnapshot snapshot =
                DeliveryEvidenceSnapshotFactory.Create(
                    versionId,
                    providerResult.Context,
                    cancellationToken);
            DeliveryTraceEvidence? trace =
                DeliveryEvidenceSnapshotFactory.CreateTrace(
                    outputTime,
                    providerResult.Context);
            return DeliveryBridgeReadResult.Available(snapshot, trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeliveryEvidenceValidationException exception)
        {
            return DeliveryBridgeReadResult.Failure(
                exception.Issue == DeliveryEvidenceIssue.Incomplete
                    ? DeliveryBridgeStatus.Incomplete
                    : DeliveryBridgeStatus.Mismatched);
        }
        catch
        {
            return DeliveryBridgeReadResult.Failure(
                DeliveryBridgeStatus.Mismatched);
        }
    }

    private static RationalTime? ReadOutputTime(JsonElement outputTime)
    {
        if (outputTime.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        long value = long.Parse(
            outputTime.GetProperty("value").GetString()!,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        long rate = long.Parse(
            outputTime.GetProperty("rate").GetString()!,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        return new RationalTime(value, rate);
    }
}

internal static class DeliveryBridgeJson
{
    internal static string WriteResponse(
        BridgeRequest request,
        DeliveryBridgeReadResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", BridgeProtocol.Version);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteString("method", BridgeProtocol.DeliveryReadMethod);
            writer.WriteStartObject("result");
            writer.WriteString("status", GetStatus(result.Status));
            if (result.Snapshot is null)
            {
                writer.WriteNull("snapshot");
            }
            else
            {
                WriteSnapshot(writer, result.Snapshot);
            }

            if (result.Trace is null)
            {
                writer.WriteNull("trace");
            }
            else
            {
                WriteTrace(writer, result.Trace);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (result.Status == DeliveryBridgeStatus.Available &&
            buffer.WrittenCount >
                BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method))
        {
            return WriteResponse(
                request,
                DeliveryBridgeReadResult.Failure(
                    DeliveryBridgeStatus.Mismatched));
        }

        return BridgeProtocol.GetBoundedResponse(buffer, request.Method);
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        DeliveryBridgeSnapshot snapshot)
    {
        writer.WriteStartObject("snapshot");
        writer.WriteString("deliveryManifestId", snapshot.DeliveryManifestId);
        writer.WriteString("projectId", snapshot.ProjectId);
        writer.WriteString("versionId", snapshot.VersionId);
        writer.WriteString("versionDigest", snapshot.VersionDigest);
        writer.WriteString("renderManifestId", snapshot.RenderManifestId);
        writer.WriteString("profileId", snapshot.ProfileId);
        writer.WriteString("profileDigest", snapshot.ProfileDigest);
        writer.WriteString(
            "deliveryEvidenceDigest",
            snapshot.DeliveryEvidenceDigest);
        writer.WriteString(
            "usedSourceSpansDigest",
            snapshot.UsedSourceSpansDigest);
        writer.WriteString(
            "approvalEvidenceDigest",
            snapshot.ApprovalEvidenceDigest);
        writer.WriteString(
            "publicationScope",
            GetPublicationScope(snapshot.PublicationScope));

        writer.WriteStartArray("artifacts");
        foreach (DeliveryArtifactEvidence artifact in snapshot.Artifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("artifactId", artifact.ArtifactId);
            writer.WriteString("artifactType", artifact.ArtifactType);
            writer.WriteString("mediaType", artifact.MediaType);
            if (artifact.Language is null)
            {
                writer.WriteNull("language");
            }
            else
            {
                writer.WriteString("language", artifact.Language);
            }

            writer.WriteString(
                "byteSize",
                artifact.ByteSize.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("review");
        writer.WriteStartArray("records");
        foreach (DeliveryReviewRecordEvidence record in snapshot.ReviewRecords)
        {
            writer.WriteStartObject();
            writer.WriteString("reviewRecordId", record.ReviewRecordId);
            writer.WriteString("state", GetReviewState(record.State));
            writer.WriteString("actorRole", GetActorRole(record.ActorRole));
            writer.WriteString(
                "recordedAt",
                record.RecordedAt.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("approvals");
        foreach (DeliveryApprovalEvidence approval in snapshot.Approvals)
        {
            writer.WriteStartObject();
            writer.WriteString("approvalId", approval.ApprovalId);
            writer.WriteString("actorRole", GetActorRole(approval.ActorRole));
            writer.WriteString(
                "approvedAt",
                approval.ApprovedAt.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("rights");
        foreach (DeliveryRightsEvidence rights in snapshot.Rights)
        {
            writer.WriteStartObject();
            writer.WriteString("assetId", rights.AssetId);
            writer.WriteString("sourceDigest", rights.SourceDigest);
            writer.WriteString("status", GetRightsStatus(rights.Status));
            writer.WriteBoolean("evidenceRecorded", rights.EvidenceRecorded);
            writer.WriteString("allowedUse", rights.AllowedUse);
            writer.WriteBoolean(
                "externalPublicationAllowed",
                rights.ExternalPublicationAllowed);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteQualityControl(writer, snapshot.QualityControl);
        writer.WriteEndObject();
    }

    private static void WriteQualityControl(
        Utf8JsonWriter writer,
        DeliveryQcEvidence qc)
    {
        writer.WriteStartObject("qualityControl");
        writer.WriteString("artifactId", qc.ArtifactId);
        writer.WriteString("sha256", qc.Sha256);
        writer.WriteString(
            "byteSize",
            qc.ByteSize.ToString(CultureInfo.InvariantCulture));
        writer.WriteBoolean("machineChecksPassed", qc.MachineChecksPassed);
        writer.WriteBoolean("humanReviewRequired", qc.HumanReviewRequired);
        writer.WriteBoolean(
            "approvedNumericThresholdsApplied",
            qc.ApprovedNumericThresholdsApplied);
        writer.WriteBoolean("canProceedToDelivery", qc.CanProceedToDelivery);
        writer.WriteBoolean("decodedSuccessfully", qc.DecodedSuccessfully);
        writer.WriteString("probeEvidenceDigest", qc.ProbeEvidenceDigest);
        WriteStream(writer, "videoTiming", qc.VideoTiming);
        if (qc.AudioTiming is null)
        {
            writer.WriteNull("audioTiming");
        }
        else
        {
            WriteStream(writer, "audioTiming", qc.AudioTiming);
        }

        writer.WriteStartObject("subtitleTiming");
        if (qc.SubtitleStreamIndex is null)
        {
            writer.WriteNull("streamIndex");
            writer.WriteNull("observedCueCount");
            writer.WriteNull("evidenceDigest");
        }
        else
        {
            writer.WriteNumber("streamIndex", qc.SubtitleStreamIndex.Value);
            writer.WriteNumber(
                "observedCueCount",
                qc.ObservedSubtitleCueCount!.Value);
            writer.WriteString(
                "evidenceDigest",
                qc.SubtitleTimingEvidenceDigest);
        }

        writer.WriteEndObject();
        writer.WriteStartObject("duration");
        WriteRational(writer, "expected", qc.Duration.ExpectedDuration);
        WriteRational(writer, "videoDelta", qc.Duration.VideoDurationDelta);
        WriteNullableRational(
            writer,
            "audioDelta",
            qc.Duration.AudioDurationDelta);
        WriteNullableRational(
            writer,
            "audioVideoStartOffset",
            qc.Duration.AudioVideoStartOffset);
        WriteNullableRational(
            writer,
            "audioVideoEndOffset",
            qc.Duration.AudioVideoEndOffset);
        writer.WriteEndObject();

        writer.WriteStartObject("luma");
        writer.WriteNumber("measuredFrameCount", qc.Luma.MeasuredFrameCount);
        writer.WriteString(
            "minimumFrameAverage",
            qc.Luma.MinimumFrameAverage.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "maximumFrameAverage",
            qc.Luma.MaximumFrameAverage.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "meanFrameAverage",
            qc.Luma.MeanFrameAverage.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("evidenceDigest", qc.Luma.EvidenceDigest);
        writer.WriteEndObject();

        if (qc.Audio is null)
        {
            writer.WriteNull("audio");
        }
        else
        {
            writer.WriteStartObject("audio");
            WriteMeasuredValue(
                writer,
                "integratedLoudnessLufs",
                qc.Audio.IntegratedLoudnessLufs);
            WriteMeasuredValue(writer, "truePeakDbfs", qc.Audio.TruePeakDbfs);
            writer.WriteString("evidenceDigest", qc.Audio.EvidenceDigest);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("tools");
        foreach (DeliveryQcToolEvidence tool in qc.Tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("version", tool.Version);
            writer.WriteString("executableDigest", tool.ExecutableDigest);
            writer.WriteString(
                "executableByteSize",
                tool.ExecutableByteSize.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("requiredHumanReviewAreas");
        foreach (OutputQcReviewArea area in qc.RequiredHumanReviewAreas)
        {
            writer.WriteStringValue(GetReviewArea(area));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTrace(
        Utf8JsonWriter writer,
        DeliveryTraceEvidence trace)
    {
        writer.WriteStartObject("trace");
        writer.WriteString("status", trace.Status == DeliveryTraceStatus.Resolved
            ? "resolved"
            : "notFound");
        WriteRational(writer, "outputTime", trace.OutputTime);
        if (trace.OriginKind is null)
        {
            writer.WriteNull("originKind");
            writer.WriteNull("originId");
        }
        else
        {
            writer.WriteString("originKind", GetOriginKind(trace.OriginKind.Value));
            writer.WriteString("originId", trace.OriginId);
        }

        WriteNullableRational(writer, "sourceTime", trace.SourceTime);
        if (trace.SourceSpan is null)
        {
            writer.WriteNull("sourceSpan");
        }
        else
        {
            writer.WriteStartObject("sourceSpan");
            writer.WriteString("sourceSpanId", trace.SourceSpan.SourceSpanId);
            writer.WriteString("assetId", trace.SourceSpan.AssetId);
            writer.WriteString("streamId", trace.SourceSpan.StreamId);
            WriteRational(writer, "start", trace.SourceSpan.Start);
            WriteRational(writer, "end", trace.SourceSpan.End);
            writer.WriteString("sourceDigest", trace.SourceSpan.SourceDigest);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteStream(
        Utf8JsonWriter writer,
        string propertyName,
        DeliveryQcStreamEvidence stream)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("streamIndex", stream.StreamIndex);
        writer.WriteNumber("observedCount", stream.ObservedCount);
        WriteRational(writer, "presentationStart", stream.PresentationStart);
        WriteRational(writer, "presentationEnd", stream.PresentationEnd);
        WriteRational(writer, "observedDuration", stream.ObservedDuration);
        writer.WriteString("evidenceDigest", stream.EvidenceDigest);
        writer.WriteEndObject();
    }

    private static void WriteMeasuredValue(
        Utf8JsonWriter writer,
        string propertyName,
        DeliveryQcMeasuredValueEvidence value)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("kind", value.Kind switch
        {
            OutputQcMeasuredValueKind.Finite => "finite",
            OutputQcMeasuredValueKind.NegativeInfinity => "negativeInfinity",
            OutputQcMeasuredValueKind.PositiveInfinity => "positiveInfinity",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        });
        if (value.Value is null)
        {
            writer.WriteNull("value");
        }
        else
        {
            writer.WriteString(
                "value",
                value.Value.Value.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteEndObject();
    }

    private static void WriteRational(
        Utf8JsonWriter writer,
        string propertyName,
        RationalTime value)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteNullableRational(
        Utf8JsonWriter writer,
        string propertyName,
        RationalTime? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            WriteRational(writer, propertyName, value.Value);
        }
    }

    private static string GetStatus(DeliveryBridgeStatus status) => status switch
    {
        DeliveryBridgeStatus.Available => "available",
        DeliveryBridgeStatus.Forbidden => "forbidden",
        DeliveryBridgeStatus.Unavailable => "unavailable",
        DeliveryBridgeStatus.Incomplete => "incomplete",
        DeliveryBridgeStatus.Mismatched => "mismatched",
        DeliveryBridgeStatus.QualityControlFailed => "qualityControlFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string GetPublicationScope(PublicationScope scope) =>
        scope switch
        {
            PublicationScope.InternalOnly => "internalOnly",
            PublicationScope.SpecificGroupOnly => "specificGroupOnly",
            PublicationScope.ExternalCandidate => "externalCandidate",
            PublicationScope.ExternalPublicationApproved =>
                "externalPublicationApproved",
            PublicationScope.PublicationForbidden => "publicationForbidden",
            PublicationScope.PendingDecision => "pendingDecision",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

    private static string GetRightsStatus(RightsStatus status) => status switch
    {
        RightsStatus.Unconfirmed => "unconfirmed",
        RightsStatus.Approved => "approved",
        RightsStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string GetReviewState(ReviewState state) => state switch
    {
        ReviewState.Editing => "editing",
        ReviewState.EditorReview => "editorReview",
        ReviewState.PerformerReview => "performerReview",
        ReviewState.ContentReview => "contentReview",
        ReviewState.BrandReview => "brandReview",
        ReviewState.ChangesRequested => "changesRequested",
        ReviewState.Approved => "approved",
        ReviewState.PublicationBlocked => "publicationBlocked",
        ReviewState.FinalRendering => "finalRendering",
        ReviewState.QualityControl => "qualityControl",
        ReviewState.Exported => "exported",
        ReviewState.Published => "published",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string GetActorRole(ReviewActorRole role) => role switch
    {
        ReviewActorRole.Editor => "editor",
        ReviewActorRole.PerformerReviewer => "performerReviewer",
        ReviewActorRole.ContentApprover => "contentApprover",
        ReviewActorRole.BrandApprover => "brandApprover",
        ReviewActorRole.RightsApprover => "rightsApprover",
        ReviewActorRole.Administrator => "administrator",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string GetReviewArea(OutputQcReviewArea area) => area switch
    {
        OutputQcReviewArea.Duration => "duration",
        OutputQcReviewArea.AudioVideoSynchronization =>
            "audioVideoSynchronization",
        OutputQcReviewArea.SubtitlePresentation => "subtitlePresentation",
        OutputQcReviewArea.ScreenLuma => "screenLuma",
        OutputQcReviewArea.ProgramLoudness => "programLoudness",
        OutputQcReviewArea.TruePeak => "truePeak",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    private static string GetOriginKind(OriginKind kind) => kind switch
    {
        OriginKind.SourceSpan => "sourceSpan",
        OriginKind.Asset => "asset",
        OriginKind.EditorialRecord => "editorialRecord",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
