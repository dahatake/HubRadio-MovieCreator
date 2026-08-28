using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Reviews;

public sealed class ReviewRecord
{
    public ReviewRecord(
        EntityId<ReviewRecordIdTag> id,
        CompositionVersion compositionVersion,
        ReviewState state,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersion);

        ReviewValidation.ValidateDefined(state, nameof(state), "review state");
        ReviewValidation.ValidateDefined(actorRole, nameof(actorRole), "review actor role");

        Id = id;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        State = state;
        ActorId = ReviewValidation.RequireNonBlank(
            actorId,
            nameof(actorId),
            "A review actor ID");
        ActorRole = actorRole;
        RecordedAt = recordedAt;
        Reason = ReviewValidation.RequireNonBlank(
            reason,
            nameof(reason),
            "A review reason");
    }

    public EntityId<ReviewRecordIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public ReviewState State { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset RecordedAt { get; }

    public string Reason { get; }
}