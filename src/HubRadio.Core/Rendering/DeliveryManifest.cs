using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Rendering;

public enum SubtitleDeliveryVariantKind
{
    BurnedIn,
    SubtitleFree,
}

public sealed class SubtitleDeliveryVariantEvidence
{
    public SubtitleDeliveryVariantEvidence(
        SubtitleDeliveryVariantKind kind,
        EntityId<RenderGraphIdTag> renderGraphId,
        ContentDigest displayProfileDigest,
        ContentDigest subtitleEvidenceDigest,
        ManifestArtifact? sidecarArtifact)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown subtitle delivery variant.");
        }

        ArgumentNullException.ThrowIfNull(renderGraphId);
        ArgumentNullException.ThrowIfNull(displayProfileDigest);
        ArgumentNullException.ThrowIfNull(subtitleEvidenceDigest);
        if (sidecarArtifact is not null
            && (kind != SubtitleDeliveryVariantKind.SubtitleFree
                || !string.Equals(
                    sidecarArtifact.ArtifactType,
                    "subtitle",
                    StringComparison.Ordinal)
                || sidecarArtifact.Language is null))
        {
            throw new ArgumentException(
                "Only a subtitle-free variant may bind one explicitly localized subtitle sidecar.",
                nameof(sidecarArtifact));
        }

        Kind = kind;
        RenderGraphId = renderGraphId;
        DisplayProfileDigest = displayProfileDigest;
        SubtitleEvidenceDigest = subtitleEvidenceDigest;
        SidecarArtifact = sidecarArtifact;
    }

    public SubtitleDeliveryVariantKind Kind { get; }

    public EntityId<RenderGraphIdTag> RenderGraphId { get; }

    public ContentDigest DisplayProfileDigest { get; }

    public ContentDigest SubtitleEvidenceDigest { get; }

    public ManifestArtifact? SidecarArtifact { get; }
}

public sealed class DeliveryManifest
{
    public DeliveryManifest(
        EntityId<DeliveryManifestIdTag> id,
        RenderManifest renderManifest,
        IReadOnlyList<ManifestArtifact> artifacts)
        : this(
            id,
            renderManifest,
            artifacts,
            subtitleVariant: null,
            explicitVariant: false)
    {
    }

    public DeliveryManifest(
        EntityId<DeliveryManifestIdTag> id,
        RenderManifest renderManifest,
        IReadOnlyList<ManifestArtifact> artifacts,
        SubtitleDeliveryVariantEvidence subtitleVariant)
        : this(
            id,
            renderManifest,
            artifacts,
            subtitleVariant
                ?? throw new ArgumentNullException(nameof(subtitleVariant)),
            explicitVariant: true)
    {
    }

    private DeliveryManifest(
        EntityId<DeliveryManifestIdTag> id,
        RenderManifest renderManifest,
        IReadOnlyList<ManifestArtifact> artifacts,
        SubtitleDeliveryVariantEvidence? subtitleVariant,
        bool explicitVariant)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(renderManifest);
        if (explicitVariant != (subtitleVariant is not null))
        {
            throw new ArgumentException(
                "The explicit subtitle variant marker is inconsistent.",
                nameof(explicitVariant));
        }

        Id = id;
        ProjectId = renderManifest.ProjectId;
        CompositionVersionId = renderManifest.CompositionVersionId;
        CompositionVersionDigest = renderManifest.CompositionVersionDigest;
        RenderManifestId = renderManifest.Id;
        ProfileId = renderManifest.ProfileId;
        ProfileDigest = renderManifest.ProfileDigest;
        Artifacts = ManifestCollection.CopyDistinct(
            artifacts,
            nameof(artifacts),
            requireAtLeastOne: true);
        ValidateSubtitleVariant(subtitleVariant, Artifacts);
        SubtitleVariant = subtitleVariant;
    }

    public EntityId<DeliveryManifestIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderManifestIdTag> RenderManifestId { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public IReadOnlyList<ManifestArtifact> Artifacts { get; }

    public SubtitleDeliveryVariantEvidence? SubtitleVariant { get; }

    private static void ValidateSubtitleVariant(
        SubtitleDeliveryVariantEvidence? subtitleVariant,
        IReadOnlyList<ManifestArtifact> artifacts)
    {
        if (subtitleVariant is null)
        {
            return;
        }

        ManifestArtifact[] subtitleArtifacts = artifacts
            .Where(static artifact => string.Equals(
                artifact.ArtifactType,
                "subtitle",
                StringComparison.Ordinal))
            .ToArray();
        bool matches = subtitleVariant.SidecarArtifact is null
            ? subtitleArtifacts.Length == 0
            : subtitleArtifacts.Length == 1
                && subtitleArtifacts[0].Equals(
                    subtitleVariant.SidecarArtifact);
        if (!matches)
        {
            throw new ArgumentException(
                "The explicit subtitle variant must match its subtitle sidecar inventory exactly.",
                nameof(subtitleVariant));
        }
    }
}
