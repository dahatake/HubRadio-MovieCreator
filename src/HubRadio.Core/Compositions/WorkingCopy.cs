using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Compositions;

public sealed class WorkingCopy
{
    public WorkingCopy(
        EntityId<WorkingCopyIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? baseVersionId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IReadOnlyList<CompositionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);

        if (updatedAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                updatedAt,
                "A working copy cannot be updated before it is created.");
        }

        CompositionElement[] copiedElements =
            CompositionValidation.CopyElements(elements, nameof(elements));

        Id = id;
        ProjectId = projectId;
        BaseVersionId = baseVersionId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Elements = Array.AsReadOnly(copiedElements);
    }

    public EntityId<WorkingCopyIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag>? BaseVersionId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<CompositionElement> Elements { get; private set; }

    public void ReplaceElements(
        IReadOnlyList<CompositionElement> elements,
        DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                updatedAt,
                "A working-copy update timestamp must not move backwards.");
        }

        CompositionElement[] copiedElements =
            CompositionValidation.CopyElements(elements, nameof(elements));

        Elements = Array.AsReadOnly(copiedElements);
        UpdatedAt = updatedAt;
    }
}