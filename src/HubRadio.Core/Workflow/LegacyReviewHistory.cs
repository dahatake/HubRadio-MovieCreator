using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;

namespace HubRadio.Core.Workflow;

public enum LegacyReviewReferenceKind
{
    ReviewState,
    Comment,
    Approval,
}

public sealed class LegacyReviewReference
{
    public LegacyReviewReference(
        string sourceId,
        QuickStartWorkflowBinding workflowBinding,
        LegacyReviewReferenceKind kind,
        ReviewState? reviewState,
        DateTimeOffset occurredAt,
        string actorId,
        ReviewActorRole actorRole)
    {
        ArgumentNullException.ThrowIfNull(workflowBinding);
        QuickStartWorkflowContract.ValidateEnum(kind, nameof(kind));
        QuickStartWorkflowContract.ValidateEnum(actorRole, nameof(actorRole));
        if (reviewState is not null)
        {
            QuickStartWorkflowContract.ValidateEnum(reviewState.Value, nameof(reviewState));
        }

        if ((kind == LegacyReviewReferenceKind.ReviewState) !=
            (reviewState is not null))
        {
            throw new ArgumentException(
                "Only a legacy review-state reference may contain a review state.",
                nameof(reviewState));
        }

        if (!Guid.TryParseExact(sourceId, "D", out Guid parsedSourceId) ||
            parsedSourceId == Guid.Empty ||
            !string.Equals(
                sourceId,
                parsedSourceId.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A legacy source ID must be a lowercase nonempty UUID in D format.",
                nameof(sourceId));
        }

        SourceId = sourceId;
        WorkflowBinding = workflowBinding;
        Kind = kind;
        ReviewState = reviewState;
        OccurredAt = occurredAt;
        ArgumentNullException.ThrowIfNull(actorId);
        if (string.IsNullOrWhiteSpace(actorId) ||
            !string.Equals(actorId, actorId.Trim(), StringComparison.Ordinal) ||
            actorId.Contains('\0'))
        {
            throw new ArgumentException(
                "A legacy actor ID must preserve the old nonblank text contract.",
                nameof(actorId));
        }

        try
        {
            _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetByteCount(actorId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A legacy actor ID must contain valid Unicode.",
                nameof(actorId),
                exception);
        }

        ActorId = actorId;
        ActorRole = actorRole;
    }

    public string SourceId { get; }

    public QuickStartWorkflowBinding WorkflowBinding { get; }

    public LegacyReviewReferenceKind Kind { get; }

    public ReviewState? ReviewState { get; }

    public DateTimeOffset OccurredAt { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public bool CanAuthorizeQuickStartExport => false;
}