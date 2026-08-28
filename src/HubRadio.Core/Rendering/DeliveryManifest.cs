using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Rendering;

public sealed class DeliveryManifest
{
    public DeliveryManifest(
        EntityId<DeliveryManifestIdTag> id,
        RenderManifest renderManifest,
        IReadOnlyList<ManifestArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(renderManifest);

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
    }

    public EntityId<DeliveryManifestIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderManifestIdTag> RenderManifestId { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public IReadOnlyList<ManifestArtifact> Artifacts { get; }
}