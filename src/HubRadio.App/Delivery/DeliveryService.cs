using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Audit;
using HubRadio.App.Reviews;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Delivery;

public enum DeliveryFailureReason
{
    ContextMismatch,
    RenderEvidenceMismatch,
    QualityControlMismatch,
    ApprovalEvidenceMissing,
    ApprovalEvidenceInvalid,
    StaleApproval,
    RequiredArtifactMissing,
    ArtifactConflict,
    ArtifactBindingMismatch,
    RegisteredArtifactInvalid,
    ProvenanceInvalid,
    EvidenceDigestFailed,
}

public sealed class DeliveryException : Exception
{
    internal DeliveryException(DeliveryFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public DeliveryFailureReason Reason { get; }

    private static string GetStableMessage(DeliveryFailureReason reason) =>
        reason switch
        {
            DeliveryFailureReason.ContextMismatch =>
                "The delivery context does not identify one exact project and composition version.",
            DeliveryFailureReason.RenderEvidenceMismatch =>
                "The render evidence does not match the delivery context.",
            DeliveryFailureReason.QualityControlMismatch =>
                "The quality-control evidence does not match the rendered output.",
            DeliveryFailureReason.ApprovalEvidenceMissing =>
                "Approval evidence for the delivery version is missing.",
            DeliveryFailureReason.ApprovalEvidenceInvalid =>
                "Approval evidence for the delivery version is invalid.",
            DeliveryFailureReason.StaleApproval =>
                "Approval evidence does not approve the delivery version digest.",
            DeliveryFailureReason.RequiredArtifactMissing =>
                "A declared or rendered delivery artifact is missing.",
            DeliveryFailureReason.ArtifactConflict =>
                "The delivery artifact inventory contains a duplicate or conflict.",
            DeliveryFailureReason.ArtifactBindingMismatch =>
                "A registered artifact does not match its delivery declaration.",
            DeliveryFailureReason.RegisteredArtifactInvalid =>
                "A registered delivery artifact could not be validated.",
            DeliveryFailureReason.ProvenanceInvalid =>
                "The delivery provenance contains a broken origin reference.",
            DeliveryFailureReason.EvidenceDigestFailed =>
                "The delivery evidence digest could not be created.",
            _ => "The delivery could not be created.",
        };
}

public sealed class DeliveryCreationResult
{
    internal DeliveryCreationResult(
        DeliveryManifest manifest,
        IReadOnlyList<SourceSpan> usedSourceSpans,
        ContentDigest usedSourceSpansDigest,
        ContentDigest approvalEvidenceDigest,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(usedSourceSpans);
        ArgumentNullException.ThrowIfNull(usedSourceSpansDigest);
        ArgumentNullException.ThrowIfNull(approvalEvidenceDigest);
        ArgumentNullException.ThrowIfNull(evidenceDigest);

        Manifest = manifest;
        UsedSourceSpans = Array.AsReadOnly(usedSourceSpans.ToArray());
        UsedSourceSpansDigest = usedSourceSpansDigest;
        ApprovalEvidenceDigest = approvalEvidenceDigest;
        EvidenceDigest = evidenceDigest;
    }

    public DeliveryManifest Manifest { get; }

    public IReadOnlyList<SourceSpan> UsedSourceSpans { get; }

    public ContentDigest UsedSourceSpansDigest { get; }

    public ContentDigest ApprovalEvidenceDigest { get; }

    public ContentDigest EvidenceDigest { get; }
}

/// <summary>NFR-BR-005: the description with the destination labels, and the written ai-disclosure.txt (null when nothing must be shown).</summary>
public sealed record DeliveryAiDisclosureResult(string Description, string? ArtifactPath);

/// <summary>
/// Creates one delivery inventory from already-rendered, quality-controlled,
/// approved, and registered local evidence.
/// </summary>
public sealed class DeliveryService
{
    private const string UsedSourceSpansDigestDomain =
        "HubRadio.MovieCreator.UsedSourceSpans/v1";
    private const string ApprovalEvidenceDigestDomain =
        "HubRadio.MovieCreator.DeliveryApprovals/v1";
    private const string DeliveryEvidenceDigestDomain =
        "HubRadio.MovieCreator.DeliveryEvidence/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly EvaluateApprovalsDelegate _evaluateApprovals;
    private readonly GetArtifactDelegate _getArtifact;
    private readonly ValidateArtifactDelegate _validateArtifactAsync;

    public DeliveryService(
        ApprovalService approvalService,
        ArtifactStore artifactStore)
        : this(
            GetEvaluateApprovalsDelegate(approvalService),
            GetArtifactDelegateFor(artifactStore),
            GetValidateArtifactDelegate(artifactStore))
    {
    }

    internal DeliveryService(
        EvaluateApprovalsDelegate evaluateApprovals,
        GetArtifactDelegate getArtifact,
        ValidateArtifactDelegate validateArtifactAsync)
    {
        ArgumentNullException.ThrowIfNull(evaluateApprovals);
        ArgumentNullException.ThrowIfNull(getArtifact);
        ArgumentNullException.ThrowIfNull(validateArtifactAsync);

        _evaluateApprovals = evaluateApprovals;
        _getArtifact = getArtifact;
        _validateArtifactAsync = validateArtifactAsync;
    }

    /// <summary>
    /// NFR-BR-005 / AC-126: reflects the AI-generation disclosure and the labels registered for <paramref name="destination"/>
    /// into the deliverable text and next to the deliverable (<c>ai-disclosure.txt</c>). It changes no manifest or evidence digest.
    /// </summary>
    public static DeliveryAiDisclosureResult ApplyAiDisclosure(
        AiGenerationDisclosureService disclosureService,
        EntityId<ProjectIdTag> projectId,
        string destination,
        string description,
        string deliveryDirectory)
    {
        ArgumentNullException.ThrowIfNull(disclosureService);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(description);
        AiDisclosure disclosure = disclosureService.BuildDisclosure(projectId, destination);
        return new DeliveryAiDisclosureResult(
            disclosure.ApplyToDescription(description),
            disclosure.WriteArtifact(deliveryDirectory));
    }

    public async Task<DeliveryCreationResult> CreateAsync(
        EntityId<DeliveryManifestIdTag> manifestId,
        Project project,
        CompositionVersion compositionVersion,
        RenderManifest renderManifest,
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        IReadOnlyList<StoredApproval> approvalEvidence,
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            artifactEvidence,
        CancellationToken cancellationToken = default)
    {
        return await CreateCoreAsync(
            manifestId,
            project,
            compositionVersion,
            renderManifest,
            validationContext,
            qualityControl,
            approvalEvidence,
            artifactEvidence,
            subtitleVariant: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeliveryCreationResult> CreateAsync(
        EntityId<DeliveryManifestIdTag> manifestId,
        Project project,
        CompositionVersion compositionVersion,
        RenderManifest renderManifest,
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        IReadOnlyList<StoredApproval> approvalEvidence,
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            artifactEvidence,
        SubtitleDeliveryVariantEvidence subtitleVariant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtitleVariant);
        return await CreateCoreAsync(
            manifestId,
            project,
            compositionVersion,
            renderManifest,
            validationContext,
            qualityControl,
            approvalEvidence,
            artifactEvidence,
            subtitleVariant,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeliveryCreationResult> CreateCoreAsync(
        EntityId<DeliveryManifestIdTag> manifestId,
        Project project,
        CompositionVersion compositionVersion,
        RenderManifest renderManifest,
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        IReadOnlyList<StoredApproval> approvalEvidence,
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            artifactEvidence,
        SubtitleDeliveryVariantEvidence? subtitleVariant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(renderManifest);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(approvalEvidence);
        ArgumentNullException.ThrowIfNull(artifactEvidence);
        cancellationToken.ThrowIfCancellationRequested();

        StoredApproval[] approvals = SnapshotApprovals(
            approvalEvidence,
            cancellationToken);
        ArtifactBinding[] bindings = SnapshotArtifacts(
            artifactEvidence,
            cancellationToken);
        ValidateContext(
            project,
            compositionVersion,
            renderManifest,
            validationContext);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRenderedArtifacts(
            renderManifest,
            bindings,
            cancellationToken);
        ValidateQualityControl(
            project,
            compositionVersion,
            renderManifest,
            validationContext,
            qualityControl,
            bindings);
        ValidateSubtitleVariant(
            validationContext,
            qualityControl,
            subtitleVariant);
        SourceSpan[] usedSourceSpans = ResolveUsedSourceSpans(
            project,
            compositionVersion,
            validationContext,
            cancellationToken);
        ValidateApprovals(
            project,
            compositionVersion,
            approvals,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        StoredArtifact[] registeredArtifacts = await ValidateArtifactsAsync(
            project.Id,
            qualityControl.ArtifactId,
            bindings,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        DeliveryManifest manifest;
        try
        {
            ManifestArtifact[] artifacts = bindings
                .Select(static binding => binding.Artifact)
                .ToArray();
            manifest = subtitleVariant is null
                ? new DeliveryManifest(
                    manifestId,
                    renderManifest,
                    artifacts)
                : new DeliveryManifest(
                    manifestId,
                    renderManifest,
                    artifacts,
                    subtitleVariant);
        }
        catch (ArgumentException)
        {
            throw Failure(DeliveryFailureReason.ArtifactConflict);
        }

        try
        {
            ContentDigest usedSourceSpansDigest =
                ComputeUsedSourceSpansDigest(
                    project.Id,
                    compositionVersion,
                    usedSourceSpans);
            ContentDigest approvalEvidenceDigest =
                ComputeApprovalEvidenceDigest(
                    project.Id,
                    compositionVersion,
                    approvals);
            ContentDigest evidenceDigest = ComputeDeliveryEvidenceDigest(
                manifest,
                renderManifest,
                qualityControl,
                usedSourceSpansDigest,
                approvalEvidenceDigest,
                bindings,
                registeredArtifacts);
            cancellationToken.ThrowIfCancellationRequested();
            return new DeliveryCreationResult(
                manifest,
                usedSourceSpans,
                usedSourceSpansDigest,
                approvalEvidenceDigest,
                evidenceDigest);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or EncoderFallbackException
                or CryptographicException
                or InvalidOperationException
                or OverflowException)
        {
            throw Failure(DeliveryFailureReason.EvidenceDigestFailed);
        }
    }

    private static StoredApproval[] SnapshotApprovals(
        IReadOnlyList<StoredApproval> approvalEvidence,
        CancellationToken cancellationToken)
    {
        StoredApproval[] approvals;
        try
        {
            approvals = approvalEvidence.ToArray();
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(DeliveryFailureReason.ApprovalEvidenceInvalid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (approvals.Length == 0)
        {
            throw Failure(DeliveryFailureReason.ApprovalEvidenceMissing);
        }

        var ids = new HashSet<EntityId<ApprovalIdTag>>();
        foreach (StoredApproval approval in approvals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (approval is null || !ids.Add(approval.Id))
            {
                throw Failure(DeliveryFailureReason.ApprovalEvidenceInvalid);
            }
        }

        return approvals;
    }

    private static ArtifactBinding[] SnapshotArtifacts(
        IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            artifactEvidence,
        CancellationToken cancellationToken)
    {
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>[] evidence;
        try
        {
            evidence = artifactEvidence.ToArray();
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(DeliveryFailureReason.ArtifactConflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.Length == 0)
        {
            throw Failure(DeliveryFailureReason.RequiredArtifactMissing);
        }

        var ids = new HashSet<EntityId<ArtifactIdTag>>();
        var artifacts = new HashSet<ManifestArtifact>();
        var storageReferences = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new ArtifactBinding[evidence.Length];
        for (int index = 0; index < evidence.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EntityId<ArtifactIdTag>? artifactId = evidence[index].Key;
            ManifestArtifact? artifact = evidence[index].Value;
            if (artifactId is null
                || artifact is null
                || !ids.Add(artifactId)
                || !artifacts.Add(artifact)
                || !storageReferences.Add(artifact.StorageReference))
            {
                throw Failure(DeliveryFailureReason.ArtifactConflict);
            }

            bindings[index] = new ArtifactBinding(artifactId, artifact);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bindings
            .OrderBy(
                static binding => binding.Artifact.ArtifactType,
                StringComparer.Ordinal)
            .ThenBy(
                static binding => binding.Artifact.MediaType,
                StringComparer.Ordinal)
            .ThenBy(
                static binding => binding.Artifact.Language ?? string.Empty,
                StringComparer.Ordinal)
            .ThenBy(
                static binding => binding.Artifact.Sha256.Hex,
                StringComparer.Ordinal)
            .ThenBy(static binding => binding.Artifact.ByteSize)
            .ThenBy(
                static binding => binding.Artifact.StorageReference,
                StringComparer.Ordinal)
            .ThenBy(
                static binding => binding.ArtifactId.ToString(),
                StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateContext(
        Project project,
        CompositionVersion compositionVersion,
        RenderManifest renderManifest,
        RenderGraphValidationContext validationContext)
    {
        bool contextMatches = compositionVersion.ProjectId == project.Id
            && validationContext.Project.Id == project.Id
            && validationContext.CompositionVersion.ProjectId == project.Id
            && validationContext.CompositionVersion.Id == compositionVersion.Id
            && validationContext.CompositionVersion.ContentDigest
                == compositionVersion.ContentDigest
            && validationContext.TimelineMap.CompositionVersionId
                == compositionVersion.Id;
        if (!contextMatches)
        {
            throw Failure(DeliveryFailureReason.ContextMismatch);
        }

        try
        {
            if (RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    compositionVersion.Elements)
                    != compositionVersion.ContentDigest
                || RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    validationContext.CompositionVersion.Elements)
                    != compositionVersion.ContentDigest)
            {
                throw Failure(DeliveryFailureReason.ContextMismatch);
            }

            if (RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                    validationContext.TimelineMap)
                    != validationContext.TimelineMapDigest
                || !TimelineMatchesComposition(
                    validationContext.TimelineMap,
                    compositionVersion.Elements))
            {
                throw Failure(DeliveryFailureReason.ProvenanceInvalid);
            }
        }
        catch (DeliveryException)
        {
            throw;
        }
        catch
        {
            throw Failure(DeliveryFailureReason.ContextMismatch);
        }

        bool renderMatches = renderManifest.ProjectId == project.Id
            && renderManifest.CompositionVersionId == compositionVersion.Id
            && renderManifest.CompositionVersionDigest
                == compositionVersion.ContentDigest
            && renderManifest.TimelineMapId == validationContext.TimelineMap.Id
            && renderManifest.TimelineMapDigest
                == validationContext.TimelineMapDigest
            && renderManifest.ProfileId == validationContext.Profile.Id
            && renderManifest.ProfileDigest
                == validationContext.Profile.ProfileDigest
            && renderManifest.TemplateId == validationContext.Template.Id
            && renderManifest.TemplateVersionDigest
                == validationContext.Template.VersionDigest
            && validationContext.Profile.TemplateId
                == validationContext.Template.Id
            && validationContext.Profile.TemplateVersionDigest
                == validationContext.Template.VersionDigest
            && validationContext.Profile.LayoutSpecId
                == validationContext.LayoutSpec.Id;
        if (!renderMatches)
        {
            throw Failure(DeliveryFailureReason.RenderEvidenceMismatch);
        }
    }

    private static bool TimelineMatchesComposition(
        TimelineMap timelineMap,
        IReadOnlyList<CompositionElement> elements)
    {
        if (timelineMap.Entries.Count != elements.Count)
        {
            return false;
        }

        for (int index = 0; index < elements.Count; index++)
        {
            CompositionElement element = elements[index];
            TimelineMapEntry entry = timelineMap.Entries[index];
            if (entry.OutputRange != element.OutputRange
                || entry.Origin != element.Origin
                || entry.SourceRange != element.SourceRange)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateRenderedArtifacts(
        RenderManifest renderManifest,
        IReadOnlyList<ArtifactBinding> bindings,
        CancellationToken cancellationToken)
    {
        foreach (ManifestArtifact output in renderManifest.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bindings.Count(binding => binding.Artifact.Equals(output)) != 1)
            {
                throw Failure(DeliveryFailureReason.RequiredArtifactMissing);
            }
        }
    }

    private static void ValidateQualityControl(
        Project project,
        CompositionVersion compositionVersion,
        RenderManifest renderManifest,
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        IReadOnlyList<ArtifactBinding> bindings)
    {
        ArtifactBinding? qualityControlledArtifact = bindings.SingleOrDefault(
            binding => binding.ArtifactId == qualityControl.ArtifactId);
        bool isDeclaredRenderOutput = qualityControlledArtifact is not null
            && renderManifest.Outputs.Count(output =>
                output.Equals(qualityControlledArtifact.Artifact)) == 1;

        bool matches = qualityControl.MachineChecksPassed
            && qualityControl.Decode.DecodedSuccessfully
            && qualityControl.ProjectId == project.Id
            && qualityControl.RenderManifestId == renderManifest.Id
            && qualityControl.CompositionVersionId == compositionVersion.Id
            && qualityControl.CompositionVersionDigest
                == compositionVersion.ContentDigest
            && qualityControl.ProfileId == renderManifest.ProfileId
            && qualityControl.ProfileDigest == renderManifest.ProfileDigest
            && qualityControl.LayoutSpecId == validationContext.LayoutSpec.Id
            && qualityControl.LayoutSpecDigest
                == validationContext.LayoutSpecDigest
            && qualityControl.TemplateId == renderManifest.TemplateId
            && qualityControl.TemplateVersionDigest
                == renderManifest.TemplateVersionDigest
            && qualityControlledArtifact is not null
            && isDeclaredRenderOutput
            && qualityControl.ContentDigest
                == qualityControlledArtifact.Artifact.Sha256
            && qualityControl.ByteSize
                == qualityControlledArtifact.Artifact.ByteSize;
        if (!matches)
        {
            throw Failure(DeliveryFailureReason.QualityControlMismatch);
        }
    }

    private static void ValidateSubtitleVariant(
        RenderGraphValidationContext validationContext,
        OutputQcResult qualityControl,
        SubtitleDeliveryVariantEvidence? subtitleVariant)
    {
        if (subtitleVariant is null)
        {
            return;
        }

        bool hasSubtitleEvidence =
            qualityControl.SubtitleLayoutProfileDigest is not null
            && qualityControl.SubtitleDisplayProfileDigest is not null;
        bool subtitlePresentationReviewRequired =
            qualityControl.RequiredHumanReviewAreas.Contains(
                OutputQcReviewArea.SubtitlePresentation);
        bool matches = subtitleVariant.RenderGraphId
                == qualityControl.RenderGraphId
            && qualityControl.SubtitleStreamIndex is null
            && qualityControl.SubtitleTiming is null
            && subtitleVariant.Kind switch
            {
                SubtitleDeliveryVariantKind.BurnedIn =>
                    validationContext.Profile.SubtitleBurnIn
                    && hasSubtitleEvidence
                    && subtitlePresentationReviewRequired
                    && qualityControl.SubtitleDisplayProfileDigest
                        == subtitleVariant.DisplayProfileDigest,
                SubtitleDeliveryVariantKind.SubtitleFree =>
                    !validationContext.Profile.SubtitleBurnIn
                    && !hasSubtitleEvidence
                    && !subtitlePresentationReviewRequired
                    && qualityControl.SubtitleLayoutProfileDigest is null
                    && qualityControl.SubtitleDisplayProfileDigest is null,
                _ => false,
            };
        if (!matches)
        {
            throw Failure(DeliveryFailureReason.RenderEvidenceMismatch);
        }
    }

    private static SourceSpan[] ResolveUsedSourceSpans(
        Project project,
        CompositionVersion compositionVersion,
        RenderGraphValidationContext validationContext,
        CancellationToken cancellationToken)
    {
        if (!TimelineMatchesComposition(
                validationContext.TimelineMap,
                compositionVersion.Elements))
        {
            throw Failure(DeliveryFailureReason.ProvenanceInvalid);
        }

        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceSpans =
            validationContext.SourceSpans.ToDictionary(static span => span.Id);
        Dictionary<EntityId<AssetIdTag>, Asset> assets =
            project.Assets.ToDictionary(static asset => asset.Id);
        HashSet<EntityId<EditorialRecordIdTag>> editorialRecordIds =
            validationContext.EditorialRecordIds.ToHashSet();
        var used = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();

        foreach (TimelineMapEntry entry in validationContext.TimelineMap.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (entry.Origin.Kind)
            {
                case OriginKind.SourceSpan
                    when entry.Origin.SourceSpanId is
                        EntityId<SourceSpanIdTag> sourceSpanId
                        && sourceSpans.TryGetValue(
                            sourceSpanId,
                            out SourceSpan? sourceSpan)
                        && assets.TryGetValue(sourceSpan.AssetId, out Asset? asset)
                        && asset.ContentDigest == sourceSpan.SourceDigest
                        && entry.SourceRange is TimeRange sourceRange
                        && sourceRange.Start >= sourceSpan.Range.Start
                        && sourceRange.End <= sourceSpan.Range.End:
                    used.TryAdd(sourceSpan.Id, sourceSpan);
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
                    throw Failure(DeliveryFailureReason.ProvenanceInvalid);
            }
        }

        return used.Values
            .OrderBy(static span => span.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private void ValidateApprovals(
        Project project,
        CompositionVersion compositionVersion,
        IReadOnlyList<StoredApproval> approvals,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (approvals.Any(approval => approval.ProjectId != project.Id))
        {
            throw Failure(DeliveryFailureReason.ApprovalEvidenceInvalid);
        }

        if (approvals.Any(approval =>
            approval.CompositionVersionId != compositionVersion.Id
            || approval.CompositionVersionDigest
                != compositionVersion.ContentDigest))
        {
            throw Failure(DeliveryFailureReason.StaleApproval);
        }

        ApprovalValidityResult result;
        try
        {
            result = _evaluateApprovals(compositionVersion, approvals)
                ?? throw Failure(
                    DeliveryFailureReason.ApprovalEvidenceInvalid);
        }
        catch (DeliveryException)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(DeliveryFailureReason.ApprovalEvidenceInvalid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.StaleApprovals.Count != 0)
        {
            throw Failure(DeliveryFailureReason.StaleApproval);
        }

        if (!HaveSameApprovals(result.ValidApprovals, approvals))
        {
            throw Failure(DeliveryFailureReason.ApprovalEvidenceInvalid);
        }
    }

    private async Task<StoredArtifact[]> ValidateArtifactsAsync(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> qualityControlledArtifactId,
        IReadOnlyList<ArtifactBinding> bindings,
        CancellationToken cancellationToken)
    {
        var artifacts = new StoredArtifact[bindings.Count];
        for (int index = 0; index < bindings.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactBinding binding = bindings[index];
            StoredArtifact registered;
            try
            {
                registered = _getArtifact(binding.ArtifactId)
                    ?? throw Failure(
                        DeliveryFailureReason.RequiredArtifactMissing);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (DeliveryException)
            {
                throw;
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure(
                    DeliveryFailureReason.RegisteredArtifactInvalid);
            }

            if (!ArtifactMatches(
                    binding.ArtifactId,
                    projectId,
                    binding.Artifact,
                    registered)
                || binding.ArtifactId == qualityControlledArtifactId
                    && registered.Kind != ArtifactKind.Render)
            {
                throw Failure(
                    DeliveryFailureReason.ArtifactBindingMismatch);
            }

            StoredArtifact verified;
            try
            {
                verified = await _validateArtifactAsync(
                    binding.ArtifactId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested
                    && exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure(
                    DeliveryFailureReason.RegisteredArtifactInvalid);
            }

            if (verified is null
                || !ArtifactMatches(
                    binding.ArtifactId,
                    projectId,
                    binding.Artifact,
                    verified)
                || binding.ArtifactId == qualityControlledArtifactId
                    && verified.Kind != ArtifactKind.Render
                || !StoredArtifactsMatch(registered, verified))
            {
                throw Failure(
                    DeliveryFailureReason.RegisteredArtifactInvalid);
            }

            artifacts[index] = verified;
        }

        return artifacts;
    }

    private static bool HaveSameApprovals(
        IReadOnlyList<StoredApproval> first,
        IReadOnlyList<StoredApproval> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        var expected = new Dictionary<
            EntityId<ApprovalIdTag>,
            StoredApproval>();
        foreach (StoredApproval approval in second)
        {
            if (approval is null || !expected.TryAdd(approval.Id, approval))
            {
                return false;
            }
        }

        var observedIds = new HashSet<EntityId<ApprovalIdTag>>();
        foreach (StoredApproval approval in first)
        {
            if (approval is null
                || !observedIds.Add(approval.Id)
                || !expected.TryGetValue(
                    approval.Id,
                    out StoredApproval? persisted)
                || !StoredApprovalsMatch(approval, persisted))
            {
                return false;
            }
        }

        return true;
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

    private static bool ArtifactMatches(
        EntityId<ArtifactIdTag> artifactId,
        EntityId<ProjectIdTag> projectId,
        ManifestArtifact declared,
        StoredArtifact registered) =>
        registered.Id == artifactId
        && registered.ProjectId == projectId
        && registered.ContentDigest == declared.Sha256
        && registered.ByteSize == declared.ByteSize
        && string.Equals(
            registered.StorageReference,
            declared.StorageReference,
            StringComparison.Ordinal);

    private static bool StoredArtifactsMatch(
        StoredArtifact first,
        StoredArtifact second) =>
        first.Id == second.Id
        && first.ProjectId == second.ProjectId
        && first.Kind == second.Kind
        && string.Equals(
            first.StorageReference,
            second.StorageReference,
            StringComparison.Ordinal)
        && first.ContentDigest == second.ContentDigest
        && first.ByteSize == second.ByteSize
        && first.CreatedAt.EqualsExact(second.CreatedAt);

    internal static ContentDigest ComputeUsedSourceSpansDigest(
        EntityId<ProjectIdTag> projectId,
        CompositionVersion compositionVersion,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, UsedSourceSpansDigestDomain);
        AppendText(hash, projectId.ToString());
        AppendText(hash, compositionVersion.Id.ToString());
        AppendDigest(hash, compositionVersion.ContentDigest);
        AppendInt32(hash, sourceSpans.Count);
        foreach (SourceSpan span in sourceSpans)
        {
            AppendText(hash, span.Id.ToString());
            AppendText(hash, span.AssetId.ToString());
            AppendText(hash, span.StreamId);
            AppendTimeRange(hash, span.Range);
            AppendDigest(hash, span.SourceDigest);
            AppendNullableText(hash, span.TranscriptRevisionId?.ToString());
            AppendInt32(hash, span.TranscriptTokenIds.Count);
            foreach (EntityId<TranscriptTokenIdTag> tokenId in
                span.TranscriptTokenIds)
            {
                AppendText(hash, tokenId.ToString());
            }
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static ContentDigest ComputeApprovalEvidenceDigest(
        EntityId<ProjectIdTag> projectId,
        CompositionVersion compositionVersion,
        IReadOnlyList<StoredApproval> approvals)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, ApprovalEvidenceDigestDomain);
        AppendText(hash, projectId.ToString());
        AppendText(hash, compositionVersion.Id.ToString());
        AppendDigest(hash, compositionVersion.ContentDigest);
        StoredApproval[] ordered = approvals
            .OrderBy(
                static approval => approval.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, ordered.Length);
        foreach (StoredApproval approval in ordered)
        {
            AppendText(hash, approval.Id.ToString());
            AppendText(hash, approval.ProjectId.ToString());
            AppendText(hash, approval.CompositionVersionId.ToString());
            AppendDigest(hash, approval.CompositionVersionDigest);
            AppendText(hash, approval.ActorId);
            AppendText(hash, approval.ActorRole.ToString());
            AppendDateTimeOffset(hash, approval.ApprovedAt);
            AppendText(hash, approval.Reason);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static ContentDigest ComputeDeliveryEvidenceDigest(
        DeliveryManifest manifest,
        RenderManifest renderManifest,
        OutputQcResult qualityControl,
        ContentDigest usedSourceSpansDigest,
        ContentDigest approvalEvidenceDigest,
        IReadOnlyList<ArtifactBinding> bindings,
        IReadOnlyList<StoredArtifact> registeredArtifacts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, DeliveryEvidenceDigestDomain);
        AppendText(hash, manifest.Id.ToString());
        AppendText(hash, manifest.ProjectId.ToString());
        AppendText(hash, manifest.CompositionVersionId.ToString());
        AppendDigest(hash, manifest.CompositionVersionDigest);
        AppendText(hash, manifest.RenderManifestId.ToString());
        AppendText(hash, manifest.ProfileId.ToString());
        AppendDigest(hash, manifest.ProfileDigest);
        AppendSubtitleVariant(hash, manifest.SubtitleVariant);
        AppendRenderManifest(hash, renderManifest);
        AppendQualityControl(hash, qualityControl);
        AppendDigest(hash, usedSourceSpansDigest);
        AppendDigest(hash, approvalEvidenceDigest);
        AppendInt32(hash, bindings.Count);
        for (int index = 0; index < bindings.Count; index++)
        {
            ArtifactBinding binding = bindings[index];
            StoredArtifact registered = registeredArtifacts[index];
            AppendText(hash, binding.ArtifactId.ToString());
            AppendManifestArtifact(hash, binding.Artifact);
            AppendText(hash, registered.Kind.ToString());
            AppendDateTimeOffset(hash, registered.CreatedAt);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static ContentDigest ComputeDeliveryEvidenceDigest(
        DeliveryManifest manifest,
        RenderManifest renderManifest,
        OutputQcResult qualityControl,
        ContentDigest usedSourceSpansDigest,
        ContentDigest approvalEvidenceDigest,
        IReadOnlyList<KeyValuePair<
            EntityId<ArtifactIdTag>,
            ManifestArtifact>> artifactEvidence,
        IReadOnlyList<StoredArtifact> registeredArtifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(renderManifest);
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(usedSourceSpansDigest);
        ArgumentNullException.ThrowIfNull(approvalEvidenceDigest);
        ArgumentNullException.ThrowIfNull(artifactEvidence);
        ArgumentNullException.ThrowIfNull(registeredArtifacts);

        ArtifactBinding[] bindings = SnapshotArtifacts(
            artifactEvidence,
            cancellationToken);
        if (registeredArtifacts.Count != bindings.Length)
        {
            throw new ArgumentException(
                "Registered artifact metadata must cover every delivery binding.",
                nameof(registeredArtifacts));
        }

        var registeredById = new Dictionary<
            EntityId<ArtifactIdTag>,
            StoredArtifact>();
        foreach (StoredArtifact artifact in registeredArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null ||
                !registeredById.TryAdd(artifact.Id, artifact))
            {
                throw new ArgumentException(
                    "Registered artifact metadata must have unique identities.",
                    nameof(registeredArtifacts));
            }
        }

        var orderedRegistered = new StoredArtifact[bindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!registeredById.TryGetValue(
                    bindings[index].ArtifactId,
                    out StoredArtifact? registered))
            {
                throw new ArgumentException(
                    "Registered artifact metadata does not match the delivery bindings.",
                    nameof(registeredArtifacts));
            }

            orderedRegistered[index] = registered;
        }

        return ComputeDeliveryEvidenceDigest(
            manifest,
            renderManifest,
            qualityControl,
            usedSourceSpansDigest,
            approvalEvidenceDigest,
            bindings,
            orderedRegistered);
    }

    private static void AppendSubtitleVariant(
        IncrementalHash hash,
        SubtitleDeliveryVariantEvidence? subtitleVariant)
    {
        if (subtitleVariant is null)
        {
            return;
        }

        AppendText(
            hash,
            "HubRadio.MovieCreator.SubtitleDeliveryVariant/v1");
        AppendText(hash, subtitleVariant.Kind.ToString());
        AppendText(hash, subtitleVariant.RenderGraphId.ToString());
        AppendDigest(hash, subtitleVariant.DisplayProfileDigest);
        AppendDigest(hash, subtitleVariant.SubtitleEvidenceDigest);
        AppendBoolean(hash, subtitleVariant.SidecarArtifact is not null);
        if (subtitleVariant.SidecarArtifact is not null)
        {
            AppendManifestArtifact(hash, subtitleVariant.SidecarArtifact);
        }
    }

    private static void AppendRenderManifest(
        IncrementalHash hash,
        RenderManifest renderManifest)
    {
        AppendText(hash, renderManifest.Id.ToString());
        AppendText(hash, renderManifest.ProjectId.ToString());
        AppendText(hash, renderManifest.CompositionVersionId.ToString());
        AppendDigest(hash, renderManifest.CompositionVersionDigest);
        AppendText(hash, renderManifest.TimelineMapId.ToString());
        AppendDigest(hash, renderManifest.TimelineMapDigest);
        AppendText(hash, renderManifest.ProfileId.ToString());
        AppendDigest(hash, renderManifest.ProfileDigest);
        AppendText(hash, renderManifest.TemplateId.ToString());
        AppendDigest(hash, renderManifest.TemplateVersionDigest);
        AppendDigest(hash, renderManifest.ConfigurationDigest);
        AppendText(hash, renderManifest.ApplicationVersion);
        AppendDigest(hash, renderManifest.ApplicationDigest);

        RenderInputEvidence[] inputs = renderManifest.Inputs
            .OrderBy(static input => input.Name, StringComparer.Ordinal)
            .ThenBy(
                static input => input.ContentDigest.Hex,
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, inputs.Length);
        foreach (RenderInputEvidence input in inputs)
        {
            AppendText(hash, input.Name);
            AppendDigest(hash, input.ContentDigest);
        }

        RenderToolEvidence[] tools = renderManifest.Tools
            .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
            .ThenBy(static tool => tool.Version, StringComparer.Ordinal)
            .ThenBy(
                static tool => tool.BuildConfiguration,
                StringComparer.Ordinal)
            .ThenBy(
                static tool => tool.ExecutableDigest.Hex,
                StringComparer.Ordinal)
            .ThenBy(
                static tool => tool.ContainerImageDigest?.Hex
                    ?? string.Empty,
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, tools.Length);
        foreach (RenderToolEvidence tool in tools)
        {
            AppendText(hash, tool.Name);
            AppendText(hash, tool.Version);
            AppendText(hash, tool.BuildConfiguration);
            AppendDigest(hash, tool.ExecutableDigest);
            AppendNullableDigest(hash, tool.ContainerImageDigest);
        }

        RenderModelEvidence[] models = renderManifest.Models
            .OrderBy(static model => model.Provider, StringComparer.Ordinal)
            .ThenBy(
                static model => model.ModelIdentifier,
                StringComparer.Ordinal)
            .ThenBy(
                static model => model.ModelVersion,
                StringComparer.Ordinal)
            .ThenBy(
                static model => model.WeightsDigest.Hex,
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, models.Length);
        foreach (RenderModelEvidence model in models)
        {
            AppendText(hash, model.Provider);
            AppendText(hash, model.ModelIdentifier);
            AppendText(hash, model.ModelVersion);
            AppendDigest(hash, model.WeightsDigest);
        }

        RenderFontEvidence[] fonts = renderManifest.Fonts
            .OrderBy(static font => font.Name, StringComparer.Ordinal)
            .ThenBy(
                static font => font.FileDigest.Hex,
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, fonts.Length);
        foreach (RenderFontEvidence font in fonts)
        {
            AppendText(hash, font.Name);
            AppendDigest(hash, font.FileDigest);
        }

        ManifestArtifact[] outputs = OrderArtifacts(renderManifest.Outputs);
        AppendInt32(hash, outputs.Length);
        foreach (ManifestArtifact output in outputs)
        {
            AppendManifestArtifact(hash, output);
        }
    }

    private static void AppendQualityControl(
        IncrementalHash hash,
        OutputQcResult qualityControl)
    {
        AppendText(hash, qualityControl.ArtifactId.ToString());
        AppendText(hash, qualityControl.ProjectId.ToString());
        AppendDigest(hash, qualityControl.ContentDigest);
        AppendInt64(hash, qualityControl.ByteSize);
        AppendText(hash, qualityControl.RenderManifestId.ToString());
        AppendText(hash, qualityControl.RenderGraphId.ToString());
        AppendText(hash, qualityControl.CompositionVersionId.ToString());
        AppendDigest(hash, qualityControl.CompositionVersionDigest);
        AppendText(hash, qualityControl.ProfileId.ToString());
        AppendDigest(hash, qualityControl.ProfileDigest);
        AppendText(hash, qualityControl.LayoutSpecId.ToString());
        AppendDigest(hash, qualityControl.LayoutSpecDigest);
        AppendText(hash, qualityControl.TemplateId.ToString());
        AppendDigest(hash, qualityControl.TemplateVersionDigest);
        AppendNullableDigest(hash, qualityControl.SubtitleLayoutProfileDigest);
        AppendNullableDigest(hash, qualityControl.SubtitleDisplayProfileDigest);
        AppendDigest(hash, qualityControl.ProbeEvidenceDigest);
        AppendInt32(hash, qualityControl.VideoStreamIndex);
        AppendNullableInt32(hash, qualityControl.AudioStreamIndex);
        AppendNullableInt32(hash, qualityControl.SubtitleStreamIndex);
        AppendStreamTiming(hash, qualityControl.VideoTiming);
        AppendBoolean(hash, qualityControl.AudioTiming is not null);
        if (qualityControl.AudioTiming is not null)
        {
            AppendStreamTiming(hash, qualityControl.AudioTiming);
        }

        AppendBoolean(hash, qualityControl.SubtitleTiming is not null);
        if (qualityControl.SubtitleTiming is not null)
        {
            AppendSubtitleTiming(hash, qualityControl.SubtitleTiming);
        }

        AppendRationalTime(
            hash,
            qualityControl.DurationMeasurements.ExpectedDuration);
        AppendRationalTime(
            hash,
            qualityControl.DurationMeasurements.VideoDurationDelta);
        AppendNullableRationalTime(
            hash,
            qualityControl.DurationMeasurements.AudioDurationDelta);
        AppendNullableRationalTime(
            hash,
            qualityControl.DurationMeasurements.AudioVideoStartOffset);
        AppendNullableRationalTime(
            hash,
            qualityControl.DurationMeasurements.AudioVideoEndOffset);
        AppendInt64(hash, qualityControl.Decode.Elapsed.Ticks);
        AppendDigest(hash, qualityControl.LumaMeasurements.EvidenceDigest);
        AppendInt32(hash, qualityControl.LumaMeasurements.Frames.Count);
        foreach (OutputQcLumaFrameMeasurement frame in
            qualityControl.LumaMeasurements.Frames)
        {
            AppendInt32(hash, frame.FrameNumber);
            AppendInt64(hash, frame.PresentationTimestampTicks);
            AppendDecimal(hash, frame.FrameAverage);
        }

        AppendBoolean(hash, qualityControl.AudioMeasurements is not null);
        if (qualityControl.AudioMeasurements is not null)
        {
            AppendMeasuredValue(
                hash,
                qualityControl.AudioMeasurements.IntegratedLoudnessLufs);
            AppendMeasuredValue(
                hash,
                qualityControl.AudioMeasurements.TruePeakDbfs);
            AppendDigest(
                hash,
                qualityControl.AudioMeasurements.EvidenceDigest);
        }

        OutputQcToolEvidence[] tools = qualityControl.Tools
            .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
            .ThenBy(static tool => tool.Version, StringComparer.Ordinal)
            .ThenBy(
                static tool => tool.ExecutableDigest.Hex,
                StringComparer.Ordinal)
            .ThenBy(static tool => tool.ExecutableByteSize)
            .ToArray();
        AppendInt32(hash, tools.Length);
        foreach (OutputQcToolEvidence tool in tools)
        {
            AppendText(hash, tool.Name);
            AppendText(hash, tool.Version);
            AppendDigest(hash, tool.ExecutableDigest);
            AppendInt64(hash, tool.ExecutableByteSize);
        }

        OutputQcReviewArea[] reviewAreas = qualityControl.RequiredHumanReviewAreas
            .Order()
            .ToArray();
        AppendInt32(hash, reviewAreas.Length);
        foreach (OutputQcReviewArea reviewArea in reviewAreas)
        {
            AppendText(hash, reviewArea.ToString());
        }

        AppendBoolean(hash, qualityControl.MachineChecksPassed);
        AppendBoolean(hash, qualityControl.HumanReviewRequired);
        AppendBoolean(hash, qualityControl.ApprovedNumericThresholdsApplied);
        AppendBoolean(hash, qualityControl.CanProceedToDelivery);
    }

    private static void AppendStreamTiming(
        IncrementalHash hash,
        OutputQcStreamTiming timing)
    {
        AppendInt32(hash, timing.StreamIndex);
        AppendRationalTime(hash, timing.TimeBase);
        AppendRationalTime(hash, timing.PresentationStart);
        AppendRationalTime(hash, timing.PresentationEnd);
        AppendDigest(hash, timing.EvidenceDigest);
        AppendInt32(hash, timing.Entries.Count);
        foreach (OutputQcTimingEntry entry in timing.Entries)
        {
            AppendInt32(hash, entry.Sequence);
            AppendInt64(hash, entry.PresentationTimestampTicks);
            AppendInt64(hash, entry.DurationTicks);
        }
    }

    private static void AppendSubtitleTiming(
        IncrementalHash hash,
        OutputQcSubtitleStreamTiming timing)
    {
        AppendInt32(hash, timing.StreamIndex);
        AppendDigest(hash, timing.EvidenceDigest);
        AppendInt32(hash, timing.CueRanges.Count);
        foreach (TimeRange range in timing.CueRanges)
        {
            AppendTimeRange(hash, range);
        }
    }

    private static void AppendMeasuredValue(
        IncrementalHash hash,
        OutputQcMeasuredValue value)
    {
        AppendText(hash, value.Kind.ToString());
        AppendBoolean(hash, value.FiniteValue is not null);
        if (value.FiniteValue is decimal finite)
        {
            AppendDecimal(hash, finite);
        }
    }

    private static void AppendManifestArtifact(
        IncrementalHash hash,
        ManifestArtifact artifact)
    {
        AppendText(hash, artifact.ArtifactType);
        AppendText(hash, artifact.MediaType);
        AppendNullableText(hash, artifact.Language);
        AppendInt64(hash, artifact.ByteSize);
        AppendDigest(hash, artifact.Sha256);
        AppendText(hash, artifact.StorageReference);
    }

    private static ManifestArtifact[] OrderArtifacts(
        IReadOnlyList<ManifestArtifact> artifacts) =>
        artifacts
            .OrderBy(
                static artifact => artifact.ArtifactType,
                StringComparer.Ordinal)
            .ThenBy(
                static artifact => artifact.MediaType,
                StringComparer.Ordinal)
            .ThenBy(
                static artifact => artifact.Language ?? string.Empty,
                StringComparer.Ordinal)
            .ThenBy(
                static artifact => artifact.Sha256.Hex,
                StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.ByteSize)
            .ThenBy(
                static artifact => artifact.StorageReference,
                StringComparer.Ordinal)
            .ToArray();

    private static void AppendTimeRange(
        IncrementalHash hash,
        TimeRange range)
    {
        AppendRationalTime(hash, range.Start);
        AppendRationalTime(hash, range.Duration);
    }

    private static void AppendRationalTime(
        IncrementalHash hash,
        RationalTime value)
    {
        AppendInt64(hash, value.Value);
        AppendInt64(hash, value.Rate);
    }

    private static void AppendNullableRationalTime(
        IncrementalHash hash,
        RationalTime? value)
    {
        AppendBoolean(hash, value is not null);
        if (value is RationalTime time)
        {
            AppendRationalTime(hash, time);
        }
    }

    private static void AppendDateTimeOffset(
        IncrementalHash hash,
        DateTimeOffset value)
    {
        AppendInt64(hash, value.Ticks);
        AppendInt64(hash, value.Offset.Ticks);
    }

    private static void AppendNullableInt32(
        IncrementalHash hash,
        int? value)
    {
        AppendBoolean(hash, value is not null);
        if (value is int number)
        {
            AppendInt32(hash, number);
        }
    }

    private static void AppendNullableDigest(
        IncrementalHash hash,
        ContentDigest? value)
    {
        AppendBoolean(hash, value is not null);
        if (value is not null)
        {
            AppendDigest(hash, value);
        }
    }

    private static void AppendNullableText(
        IncrementalHash hash,
        string? value)
    {
        AppendBoolean(hash, value is not null);
        if (value is not null)
        {
            AppendText(hash, value);
        }
    }

    private static void AppendDigest(
        IncrementalHash hash,
        ContentDigest digest) =>
        AppendText(hash, digest.Hex);

    private static void AppendDecimal(
        IncrementalHash hash,
        decimal value)
    {
        foreach (int part in decimal.GetBits(value))
        {
            AppendInt32(hash, part);
        }
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendBoolean(
        IncrementalHash hash,
        bool value) =>
        hash.AppendData([value ? (byte)1 : (byte)0]);

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static EvaluateApprovalsDelegate GetEvaluateApprovalsDelegate(
        ApprovalService approvalService)
    {
        ArgumentNullException.ThrowIfNull(approvalService);
        return approvalService.EvaluateValidity;
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

    private static DeliveryException Failure(DeliveryFailureReason reason) =>
        new(reason);

    internal delegate ApprovalValidityResult EvaluateApprovalsDelegate(
        CompositionVersion compositionVersion,
        IReadOnlyList<StoredApproval> approvals);

    internal delegate StoredArtifact? GetArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId);

    internal delegate Task<StoredArtifact> ValidateArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken);

    private sealed record ArtifactBinding(
        EntityId<ArtifactIdTag> ArtifactId,
        ManifestArtifact Artifact);
}
