using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Reviews;

public sealed class ReviewService
{
    private readonly ReviewStore _reviewStore;
    private readonly AuthorizationService _authorizationService;

    public ReviewService(
        ReviewStore reviewStore,
        AuthorizationService authorizationService)
    {
        ArgumentNullException.ThrowIfNull(reviewStore);
        ArgumentNullException.ThrowIfNull(authorizationService);

        _reviewStore = reviewStore;
        _authorizationService = authorizationService;
    }

    public ReviewComment AddTimecodeComment(
        EntityId<ReviewCommentIdTag> commentId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset createdAt,
        string text)
    {
        (ReviewComment comment, AuthorizationRole authorizationRole,
            AuthorizationOperation operation) = PrepareTimecodeComment(
            commentId,
            compositionVersion,
            timelineMap,
            outputTime,
            actorSid,
            actorRole,
            createdAt,
            text);

        _reviewStore.AppendAuthorizedReviewComment(
            comment,
            authorizationRole,
            operation);
        return comment;
    }

    internal bool TryAddCurrentTimecodeComment(
        EntityId<ReviewCommentIdTag> commentId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset createdAt,
        string text)
    {
        (ReviewComment comment, AuthorizationRole authorizationRole,
            AuthorizationOperation operation) = PrepareTimecodeComment(
            commentId,
            compositionVersion,
            timelineMap,
            outputTime,
            actorSid,
            actorRole,
            createdAt,
            text);
        return _reviewStore.TryAppendAuthorizedCurrentReviewComment(
            comment,
            authorizationRole,
            operation);
    }

    public ReviewRecord RequestChanges(
        EntityId<ReviewCommentIdTag> commentId,
        EntityId<ReviewRecordIdTag> reviewRecordId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        ReviewState expectedCurrentState,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt,
        string text)
    {
        (ReviewComment comment, ReviewRecord reviewRecord,
            AuthorizationRole authorizationRole,
            AuthorizationOperation operation) = PrepareChangeRequest(
            commentId,
            reviewRecordId,
            compositionVersion,
            timelineMap,
            expectedCurrentState,
            outputTime,
            actorSid,
            actorRole,
            recordedAt,
            text);
        _reviewStore.AppendAuthorizedChangeRequest(
            comment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole,
            operation);
        return reviewRecord;
    }

    internal bool TryRequestChangesOnCurrentVersion(
        EntityId<ReviewCommentIdTag> commentId,
        EntityId<ReviewRecordIdTag> reviewRecordId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        ReviewState expectedCurrentState,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt,
        string text)
    {
        (ReviewComment comment, ReviewRecord reviewRecord,
            AuthorizationRole authorizationRole,
            AuthorizationOperation operation) = PrepareChangeRequest(
            commentId,
            reviewRecordId,
            compositionVersion,
            timelineMap,
            expectedCurrentState,
            outputTime,
            actorSid,
            actorRole,
            recordedAt,
            text);
        return _reviewStore.TryAppendAuthorizedCurrentChangeRequest(
            comment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole,
            operation);
    }

    private (ReviewComment Comment, AuthorizationRole AuthorizationRole,
        AuthorizationOperation Operation) PrepareTimecodeComment(
        EntityId<ReviewCommentIdTag> commentId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset createdAt,
        string text)
    {
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(timelineMap);

        (AuthorizationRole authorizationRole, AuthorizationOperation operation) =
            GetCommentAuthorization(actorRole);
        _authorizationService.DemandAs(actorSid, authorizationRole, operation);

        EntityId<SourceSpanIdTag> sourceSpanId = ResolveSourceSpanId(
            compositionVersion,
            timelineMap,
            outputTime);
        var comment = new ReviewComment(
            commentId,
            compositionVersion,
            outputTime,
            sourceSpanId,
            actorSid,
            actorRole,
            createdAt,
            text);
        return (comment, authorizationRole, operation);
    }

    private (ReviewComment Comment, ReviewRecord ReviewRecord,
        AuthorizationRole AuthorizationRole,
        AuthorizationOperation Operation) PrepareChangeRequest(
        EntityId<ReviewCommentIdTag> commentId,
        EntityId<ReviewRecordIdTag> reviewRecordId,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        ReviewState expectedCurrentState,
        RationalTime outputTime,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt,
        string text)
    {
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(reviewRecordId);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(timelineMap);
        ValidateReviewActorRole(actorRole, nameof(actorRole));

        ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
            expectedCurrentState,
            ReviewTransitionRequest.RequestChanges());
        if (decision.Disposition != ReviewTransitionDisposition.Allowed
            || decision.RequiredAuthorizationOperation is not AuthorizationOperation operation
            || decision.RequiredAuthorizationRole is not AuthorizationRole authorizationRole
            || decision.RequiredReviewActorRole is not ReviewActorRole requiredReviewActorRole)
        {
            throw new InvalidOperationException(
                "The persisted review state does not allow a change request.");
        }

        _authorizationService.DemandAs(actorSid, authorizationRole, operation);
        if (actorRole != requiredReviewActorRole)
        {
            throw new AuthorizationException(
                operation,
                ToAuthorizationRole(actorRole));
        }

        EntityId<SourceSpanIdTag> sourceSpanId = ResolveSourceSpanId(
            compositionVersion,
            timelineMap,
            outputTime);
        var comment = new ReviewComment(
            commentId,
            compositionVersion,
            outputTime,
            sourceSpanId,
            actorSid,
            actorRole,
            recordedAt,
            text);
        var reviewRecord = new ReviewRecord(
            reviewRecordId,
            compositionVersion,
            decision.To,
            actorSid,
            actorRole,
            recordedAt,
            text);
        return (comment, reviewRecord, authorizationRole, operation);
    }

    private static EntityId<SourceSpanIdTag> ResolveSourceSpanId(
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        RationalTime outputTime)
    {
        if (timelineMap.CompositionVersionId != compositionVersion.Id)
        {
            throw new ArgumentException(
                "The timeline map must belong to the supplied composition version.",
                nameof(timelineMap));
        }

        if (!timelineMap.TryMapOutputTime(outputTime, out TimelineMapPoint point))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputTime),
                outputTime,
                "The output time is not covered by the composition timeline map.");
        }

        if (point.Origin.Kind != OriginKind.SourceSpan
            || point.Origin.SourceSpanId is not EntityId<SourceSpanIdTag> sourceSpanId)
        {
            throw new InvalidOperationException(
                "A timecode review comment must resolve to a SourceSpan origin.");
        }

        return sourceSpanId;
    }

    private static (
        AuthorizationRole AuthorizationRole,
        AuthorizationOperation Operation) GetCommentAuthorization(
        ReviewActorRole actorRole)
    {
        ValidateReviewActorRole(actorRole, nameof(actorRole));

        return actorRole switch
        {
            ReviewActorRole.Editor =>
                (AuthorizationRole.Editor, AuthorizationOperation.SubmitForReview),
            ReviewActorRole.PerformerReviewer =>
                (AuthorizationRole.PerformerReviewer,
                    AuthorizationOperation.RecordPerformerReview),
            ReviewActorRole.ContentApprover =>
                (AuthorizationRole.ContentApprover,
                    AuthorizationOperation.RecordContentReview),
            ReviewActorRole.BrandApprover =>
                (AuthorizationRole.BrandApprover,
                    AuthorizationOperation.RecordBrandReview),
            ReviewActorRole.RightsApprover =>
                (AuthorizationRole.RightsApprover,
                    AuthorizationOperation.RecordRightsDecision),
            ReviewActorRole.Administrator =>
                throw new ArgumentException(
                    "The Administrator role alone cannot author review comments.",
                    nameof(actorRole)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(actorRole),
                actorRole,
                "Unknown review actor role."),
        };
    }

    private static AuthorizationRole ToAuthorizationRole(ReviewActorRole actorRole) =>
        actorRole switch
        {
            ReviewActorRole.Editor => AuthorizationRole.Editor,
            ReviewActorRole.PerformerReviewer => AuthorizationRole.PerformerReviewer,
            ReviewActorRole.ContentApprover => AuthorizationRole.ContentApprover,
            ReviewActorRole.BrandApprover => AuthorizationRole.BrandApprover,
            ReviewActorRole.RightsApprover => AuthorizationRole.RightsApprover,
            ReviewActorRole.Administrator => AuthorizationRole.Administrator,
            _ => throw new ArgumentOutOfRangeException(
                nameof(actorRole),
                actorRole,
                "Unknown review actor role."),
        };

    private static void ValidateReviewActorRole(
        ReviewActorRole actorRole,
        string parameterName)
    {
        if (!Enum.IsDefined(actorRole))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                actorRole,
                "Unknown review actor role.");
        }
    }
}