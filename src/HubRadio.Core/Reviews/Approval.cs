using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;

namespace HubRadio.Core.Reviews;

public sealed class Approval
{
    public Approval(
        EntityId<ApprovalIdTag> id,
        CompositionVersion compositionVersion,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset approvedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersion);

        ReviewValidation.ValidateDefined(actorRole, nameof(actorRole), "review actor role");
        if (actorRole is not ReviewActorRole.ContentApprover
            and not ReviewActorRole.BrandApprover
            and not ReviewActorRole.RightsApprover)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actorRole),
                actorRole,
                "An approval actor must use an approver role.");
        }

        Id = id;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        ActorId = ReviewValidation.RequireNonBlank(
            actorId,
            nameof(actorId),
            "An approval actor ID");
        ActorRole = actorRole;
        ApprovedAt = approvedAt;
        Reason = ReviewValidation.RequireNonBlank(
            reason,
            nameof(reason),
            "An approval reason");
    }

    public EntityId<ApprovalIdTag> Id { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset ApprovedAt { get; }

    public string Reason { get; }
}