using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;

namespace HubRadio.Core.Audit;

public sealed class AuditEventIdTag
{
}

public enum AuditEventKind
{
    ProjectRegistered,
    TranscriptCorrected,
    PublicationClassificationChanged,
    ReviewStateRecorded,
    CommentAdded,
    ApprovalGranted,
    CompositionExported,
    PublicationRecorded,
    SuggestionDecisionRecorded,
    RightsDecisionRecorded,
    AiGeneratedElementRecorded,
    AiDisclosureLabelAdded,
}

public sealed class AuditEvent
{
    public AuditEvent(
        EntityId<AuditEventIdTag> id,
        AuditEventKind kind,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt,
        string reason,
        EntityId<ProjectIdTag> projectId,
        CompositionVersion? compositionVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);

        ReviewValidation.ValidateDefined(kind, nameof(kind), "audit-event kind");
        ReviewValidation.ValidateDefined(actorRole, nameof(actorRole), "review actor role");

        if (compositionVersion is not null
            && compositionVersion.ProjectId != projectId)
        {
            throw new ArgumentException(
                "An audit-event composition version must belong to the recorded project.",
                nameof(compositionVersion));
        }

        Id = id;
        Kind = kind;
        ActorId = ReviewValidation.RequireNonBlank(
            actorId,
            nameof(actorId),
            "An audit-event actor ID");
        ActorRole = actorRole;
        OccurredAt = occurredAt;
        Reason = ReviewValidation.RequireNonBlank(
            reason,
            nameof(reason),
            "An audit-event reason");
        ProjectId = projectId;
        CompositionVersionId = compositionVersion?.Id;
        CompositionVersionDigest = compositionVersion?.ContentDigest;
    }

    public EntityId<AuditEventIdTag> Id { get; }

    public AuditEventKind Kind { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset OccurredAt { get; }

    public string Reason { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag>? CompositionVersionId { get; }

    public ContentDigest? CompositionVersionDigest { get; }
}