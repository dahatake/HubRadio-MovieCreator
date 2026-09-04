using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Compositions;

public sealed class CompositionVersion
{
    public CompositionVersion(
        EntityId<CompositionVersionIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? parentVersionId,
        string createdByActorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest contentDigest,
        IReadOnlyList<CompositionElement> elements)
        : this(
            id,
            projectId,
            parentVersionId,
            createdByActorId,
            createdAt,
            changeReason,
            contentDigest,
            elements,
            evidence: null)
    {
    }

    internal CompositionVersion(
        EntityId<CompositionVersionIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? parentVersionId,
        string createdByActorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest contentDigest,
        IReadOnlyList<CompositionElement> elements,
        CompositionEvidenceEnvelope? evidence)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(contentDigest);

        if (id == parentVersionId)
        {
            throw new ArgumentException(
                "A composition version cannot be its own parent.",
                nameof(parentVersionId));
        }

        CompositionElement[] copiedElements =
            CompositionValidation.CopyElements(elements, nameof(elements));

        Id = id;
        ProjectId = projectId;
        ParentVersionId = parentVersionId;
        CreatedByActorId = CompositionValidation.RequireNonBlank(
            createdByActorId,
            nameof(createdByActorId),
            "A composition-version actor ID");
        CreatedAt = createdAt;
        ChangeReason = CompositionValidation.RequireNonBlank(
            changeReason,
            nameof(changeReason),
            "A composition-version change reason");
        ContentDigest = contentDigest;
        Elements = Array.AsReadOnly(copiedElements);
        Evidence = evidence;
    }

    public EntityId<CompositionVersionIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag>? ParentVersionId { get; }

    public string CreatedByActorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public string ChangeReason { get; }

    public ContentDigest ContentDigest { get; }

    public IReadOnlyList<CompositionElement> Elements { get; }

    public CompositionEvidenceEnvelope? Evidence { get; }
}