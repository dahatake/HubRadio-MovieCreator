using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Reviews;

public sealed class ReviewComment
{
    public ReviewComment(
        EntityId<ReviewCommentIdTag> id,
        CompositionVersion compositionVersion,
        RationalTime outputTime,
        EntityId<SourceSpanIdTag> sourceSpanId,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset createdAt,
        string text)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(sourceSpanId);

        if (outputTime < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputTime),
                outputTime,
                "A review-comment output time must not be negative.");
        }

        ReviewValidation.ValidateDefined(actorRole, nameof(actorRole), "review actor role");

        Id = id;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        OutputTime = outputTime;
        SourceSpanId = sourceSpanId;
        ActorId = ReviewValidation.RequireNonBlank(
            actorId,
            nameof(actorId),
            "A review-comment actor ID");
        ActorRole = actorRole;
        CreatedAt = createdAt;
        Text = ReviewValidation.RequireNonBlank(
            text,
            nameof(text),
            "Review-comment text");
    }

    public EntityId<ReviewCommentIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public RationalTime OutputTime { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Text { get; }
}