using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Compositions;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Reviews;

internal enum ReviewWorkspaceFailureReason
{
    NotFound,
    NotAuthorized,
    StaleVersion,
    Conflict,
    InvalidRequest,
    OperationFailed,
}

internal sealed class ReviewWorkspaceException : Exception
{
    internal ReviewWorkspaceException(
        ReviewWorkspaceFailureReason reason,
        Exception? innerException = null)
        : base("The review workspace operation could not be completed.", innerException)
    {
        Reason = reason;
    }

    internal ReviewWorkspaceFailureReason Reason { get; }
}

internal enum ReviewWorkspaceActionKind
{
    AddComment,
    RequestChanges,
    RecordApproval,
}

internal enum ReviewWorkspaceActionStatus
{
    Authorized,
    ActorNotAuthorized,
    AlreadyRecorded,
    StateNotEligible,
    TargetNotCurrent,
    ReviewStateUnrecorded,
    PublicationBlocked,
}

internal sealed record ReviewWorkspaceAction(
    ReviewWorkspaceActionKind Kind,
    ReviewWorkspaceActionStatus Status,
    IReadOnlyList<ReviewActorRole> ActorRoles);

internal sealed record ReviewApprovalView(
    StoredApproval Approval,
    bool IsValid);

internal sealed record ReviewWorkspaceSnapshot(
    CompositionVersion Version,
    bool IsCurrentTarget,
    StoredReviewRecord? CurrentReviewRecord,
    IReadOnlyList<StoredReviewComment> Comments,
    IReadOnlyList<ReviewApprovalView> Approvals,
    CompositionDiff? DirectParentDiff,
    IReadOnlyList<ReviewWorkspaceAction> Actions);

internal sealed class ReviewWorkspaceService
{
    private const int MaximumAncestorCount = 10_000;

    private readonly ProjectStore projectStore;
    private readonly ReviewStore reviewStore;
    private readonly ReviewService reviewService;
    private readonly ApprovalService approvalService;
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;
    private readonly TimeProvider timeProvider;

    internal ReviewWorkspaceService(
        ProjectStore projectStore,
        ReviewStore reviewStore,
        AuthorizationService authorizationService,
        Func<string> getCurrentSid,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(reviewStore);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);

        this.projectStore = projectStore;
        this.reviewStore = reviewStore;
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        reviewService = new ReviewService(reviewStore, authorizationService);
        approvalService = new ApprovalService(reviewStore, authorizationService);
    }

    internal ReviewWorkspaceSnapshot GetSnapshot(
        EntityId<CompositionVersionIdTag> versionId)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        string actorSid = GetAuthorizedActor();
        return BuildSnapshot(LoadContext(actorSid, versionId));
    }

    internal ReviewWorkspaceSnapshot AddComment(
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest expectedDigest,
        ReviewActorRole actorRole,
        RationalTime outputTime,
        string text)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(expectedDigest);
        string actorSid = GetAuthorizedActor();
        ReviewOperationContext context = LoadContext(actorSid, versionId);
        EnsureCurrentVersion(context, expectedDigest);
        if (!Enum.IsDefined(actorRole))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.InvalidRequest);
        }

        if (!GetAssignedCommentRoles(context.AuthorizationRoles).Contains(actorRole))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.NotAuthorized);
        }

        TimelineMap timelineMap = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            context.Version);
        bool stored = reviewService.TryAddCurrentTimecodeComment(
            EntityId<ReviewCommentIdTag>.New(),
            context.Version,
            timelineMap,
            outputTime,
            actorSid,
            actorRole,
            GetUtcNow(),
            text);
        EnsureCurrentMutationStored(stored);
        return BuildSnapshot(LoadContext(actorSid, versionId));
    }

    internal ReviewWorkspaceSnapshot RequestChanges(
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest expectedDigest,
        ReviewState expectedState,
        RationalTime outputTime,
        string text)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(expectedDigest);
        if (!Enum.IsDefined(expectedState))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.InvalidRequest);
        }

        string actorSid = GetAuthorizedActor();
        ReviewOperationContext context = LoadContext(actorSid, versionId);
        EnsureCurrentVersion(context, expectedDigest);
        StoredReviewRecord? current = GetCurrentReviewRecord(context.Evidence);
        if (current is null || current.State != expectedState)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.Conflict);
        }

        ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
            current.State,
            ReviewTransitionRequest.RequestChanges());
        if (decision.Disposition != ReviewTransitionDisposition.Allowed ||
            decision.RequiredAuthorizationRole is not AuthorizationRole requiredRole ||
            decision.RequiredReviewActorRole is not ReviewActorRole requiredReviewRole)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.InvalidRequest);
        }

        if (!context.AuthorizationRoles.Contains(requiredRole))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.NotAuthorized);
        }

        TimelineMap timelineMap = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            context.Version);
        bool stored = reviewService.TryRequestChangesOnCurrentVersion(
            EntityId<ReviewCommentIdTag>.New(),
            EntityId<ReviewRecordIdTag>.New(),
            context.Version,
            timelineMap,
            expectedState,
            outputTime,
            actorSid,
            requiredReviewRole,
            GetUtcNow(),
            text);
        EnsureCurrentMutationStored(stored);
        return BuildSnapshot(LoadContext(actorSid, versionId));
    }

    internal ReviewWorkspaceSnapshot RecordApproval(
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest expectedDigest,
        ReviewActorRole actorRole,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(expectedDigest);
        string actorSid = GetAuthorizedActor();
        ReviewOperationContext context = LoadContext(actorSid, versionId);
        EnsureCurrentVersion(context, expectedDigest);
        if (!Enum.IsDefined(actorRole))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.InvalidRequest);
        }

        if (!GetAssignedApprovalRoles(context.AuthorizationRoles).Contains(actorRole))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.NotAuthorized);
        }

        if (context.Evidence.Approvals.Any(approval =>
                approval.ActorRole == actorRole &&
                string.Equals(
                    approval.ActorId,
                    actorSid,
                    StringComparison.Ordinal)))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.Conflict);
        }

        EntityId<ApprovalIdTag> approvalId = CreateApprovalId(
            context.Version.Id,
            actorSid,
            actorRole);
        try
        {
            bool stored = approvalService.TryRecordCurrentApproval(
                approvalId,
                context.Version,
                actorSid,
                actorRole,
                GetUtcNow(),
                reason);
            EnsureCurrentMutationStored(stored);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.Conflict,
                exception);
        }

        return BuildSnapshot(LoadContext(actorSid, versionId));
    }

    private string GetAuthorizedActor()
    {
        string actorSid = getCurrentSid();
        try
        {
            authorizationService.Demand(
                actorSid,
                AuthorizationOperation.ViewProject);
        }
        catch (AuthorizationException exception)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.NotAuthorized,
                exception);
        }

        return actorSid;
    }

    private ReviewOperationContext LoadContext(
        string actorSid,
        EntityId<CompositionVersionIdTag> versionId)
    {
        StoredCompositionVersion storedVersion =
            projectStore.GetCompositionVersion(versionId)
            ?? throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.NotFound);
        CompositionVersion version = ReadAndValidateVersion(storedVersion);
        StoredVersionEvidence evidence =
            reviewStore.GetVersionEvidence(version.Id)
            ?? throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        ValidateEvidenceBinding(version, evidence);
        StoredWorkingCopy? workingCopy = projectStore.GetWorkingCopy(version.ProjectId);
        bool isCurrentTarget = workingCopy?.BaseVersionId == version.Id;
        IReadOnlyList<AuthorizationRole> roles =
            authorizationService.GetRoles(actorSid);
        return new ReviewOperationContext(
            actorSid,
            version,
            evidence,
            isCurrentTarget,
            roles);
    }

    private ReviewWorkspaceSnapshot BuildSnapshot(ReviewOperationContext context)
    {
        StoredReviewRecord? currentReviewRecord =
            GetCurrentReviewRecord(context.Evidence);
        IReadOnlyList<StoredApproval> candidateApprovals =
            ReadCurrentAndAncestorApprovals(context.Version, context.Evidence);
        ApprovalValidityResult validity = approvalService.EvaluateValidity(
            context.Version,
            candidateApprovals);
        var validIds = validity.ValidApprovals
            .Select(static approval => approval.Id)
            .ToHashSet();
        ReviewApprovalView[] approvals = candidateApprovals
            .Select(approval => new ReviewApprovalView(
                approval,
                validIds.Contains(approval.Id)))
            .OrderByDescending(static approval => approval.IsValid)
            .ThenByDescending(static approval => approval.Approval.ApprovedAt)
            .ThenBy(static approval => approval.Approval.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        CompositionDiff? directParentDiff = ReadDirectParentDiff(context.Version);
        ReviewWorkspaceAction[] actions = CreateActions(
            context.ActorSid,
            currentReviewRecord?.State,
            context.IsCurrentTarget,
            context.AuthorizationRoles,
            approvals);

        return new ReviewWorkspaceSnapshot(
            context.Version,
            context.IsCurrentTarget,
            currentReviewRecord,
            context.Evidence.ReviewComments,
            Array.AsReadOnly(approvals),
            directParentDiff,
            Array.AsReadOnly(actions));
    }

    private IReadOnlyList<StoredApproval> ReadCurrentAndAncestorApprovals(
        CompositionVersion currentVersion,
        StoredVersionEvidence currentEvidence)
    {
        var approvals = new List<StoredApproval>(currentEvidence.Approvals);
        var visited = new HashSet<EntityId<CompositionVersionIdTag>>
        {
            currentVersion.Id,
        };
        EntityId<CompositionVersionIdTag>? ancestorId =
            currentVersion.ParentVersionId;
        int ancestorCount = 0;
        while (ancestorId is not null)
        {
            if (!visited.Add(ancestorId) ||
                ++ancestorCount > MaximumAncestorCount)
            {
                throw new ReviewWorkspaceException(
                    ReviewWorkspaceFailureReason.OperationFailed);
            }

            StoredCompositionVersion storedAncestor =
                projectStore.GetCompositionVersion(ancestorId)
                ?? throw new ReviewWorkspaceException(
                    ReviewWorkspaceFailureReason.OperationFailed);
            CompositionVersion ancestor = ReadAndValidateVersion(storedAncestor);
            if (ancestor.ProjectId != currentVersion.ProjectId)
            {
                throw new ReviewWorkspaceException(
                    ReviewWorkspaceFailureReason.OperationFailed);
            }

            StoredVersionEvidence ancestorEvidence =
                reviewStore.GetVersionEvidence(ancestor.Id)
                ?? throw new ReviewWorkspaceException(
                    ReviewWorkspaceFailureReason.OperationFailed);
            ValidateEvidenceBinding(ancestor, ancestorEvidence);
            approvals.AddRange(ancestorEvidence.Approvals);
            ancestorId = ancestor.ParentVersionId;
        }

        return Array.AsReadOnly(approvals.ToArray());
    }

    private CompositionDiff? ReadDirectParentDiff(CompositionVersion version)
    {
        if (version.ParentVersionId is null)
        {
            return null;
        }

        StoredCompositionVersion storedParent =
            projectStore.GetCompositionVersion(version.ParentVersionId)
            ?? throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        CompositionVersion parent = ReadAndValidateVersion(storedParent);
        if (parent.ProjectId != version.ProjectId)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        }

        return VersionService.Compare(parent, version);
    }

    private static CompositionVersion ReadAndValidateVersion(
        StoredCompositionVersion stored)
    {
        CompositionVersion version;
        try
        {
            version = CompositionSnapshotJson.ReadVersion(stored.CanonicalJson);
        }
        catch (FormatException exception)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed,
                exception);
        }

        if (stored.Id != version.Id ||
            stored.ProjectId != version.ProjectId ||
            stored.ParentVersionId != version.ParentVersionId ||
            stored.ContentDigest != version.ContentDigest ||
            !stored.CreatedAt.EqualsExact(version.CreatedAt))
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        }

        return version;
    }

    private static void ValidateEvidenceBinding(
        CompositionVersion version,
        StoredVersionEvidence evidence)
    {
        if (evidence.CompositionVersionId != version.Id ||
            evidence.ProjectId != version.ProjectId ||
            evidence.CompositionVersionDigest != version.ContentDigest)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        }
    }

    private static StoredReviewRecord? GetCurrentReviewRecord(
        StoredVersionEvidence evidence)
    {
        if (evidence.ReviewRecords.Count == 0)
        {
            return null;
        }

        StoredReviewRecord current = evidence.ReviewRecords[^1];
        if (evidence.ReviewRecords.Count > 1 &&
            evidence.ReviewRecords[^2].RecordedAt == current.RecordedAt)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.OperationFailed);
        }

        return current;
    }

    private static ReviewWorkspaceAction[] CreateActions(
        string actorSid,
        ReviewState? state,
        bool isCurrentTarget,
        IReadOnlyList<AuthorizationRole> roles,
        IReadOnlyList<ReviewApprovalView> approvals)
    {
        if (!isCurrentTarget)
        {
            return CreateUniformActions(
                ReviewWorkspaceActionStatus.TargetNotCurrent);
        }

        IReadOnlyList<ReviewActorRole> commentRoles =
            GetAssignedCommentRoles(roles);
        ReviewWorkspaceAction requestChanges =
            CreateRequestChangesAction(state, roles);
        IReadOnlyList<ReviewActorRole> assignedApprovalRoles =
            GetAssignedApprovalRoles(roles);
        ReviewActorRole[] availableApprovalRoles = assignedApprovalRoles
            .Where(role => !approvals.Any(view =>
                view.IsValid &&
                view.Approval.ActorRole == role &&
                string.Equals(
                    view.Approval.ActorId,
                    actorSid,
                    StringComparison.Ordinal)))
            .ToArray();
        ReviewWorkspaceActionStatus approvalStatus = availableApprovalRoles.Length > 0
            ? ReviewWorkspaceActionStatus.Authorized
            : assignedApprovalRoles.Count > 0
                ? ReviewWorkspaceActionStatus.AlreadyRecorded
                : ReviewWorkspaceActionStatus.ActorNotAuthorized;
        return
        [
            new ReviewWorkspaceAction(
                ReviewWorkspaceActionKind.AddComment,
                commentRoles.Count > 0
                    ? ReviewWorkspaceActionStatus.Authorized
                    : ReviewWorkspaceActionStatus.ActorNotAuthorized,
                commentRoles),
            requestChanges,
            new ReviewWorkspaceAction(
                ReviewWorkspaceActionKind.RecordApproval,
                approvalStatus,
                Array.AsReadOnly(availableApprovalRoles)),
        ];
    }

    private static ReviewWorkspaceAction[] CreateUniformActions(
        ReviewWorkspaceActionStatus status) =>
    [
        CreateUnavailableAction(ReviewWorkspaceActionKind.AddComment, status),
        CreateUnavailableAction(ReviewWorkspaceActionKind.RequestChanges, status),
        CreateUnavailableAction(ReviewWorkspaceActionKind.RecordApproval, status),
    ];

    private static ReviewWorkspaceAction CreateRequestChangesAction(
        ReviewState? state,
        IReadOnlyList<AuthorizationRole> roles)
    {
        if (state is null)
        {
            return CreateUnavailableAction(
                ReviewWorkspaceActionKind.RequestChanges,
                ReviewWorkspaceActionStatus.ReviewStateUnrecorded);
        }

        if (state == ReviewState.PublicationBlocked)
        {
            return CreateUnavailableAction(
                ReviewWorkspaceActionKind.RequestChanges,
                ReviewWorkspaceActionStatus.PublicationBlocked);
        }

        ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
            state.Value,
            ReviewTransitionRequest.RequestChanges());
        if (decision.Disposition != ReviewTransitionDisposition.Allowed ||
            decision.RequiredAuthorizationRole is not AuthorizationRole requiredRole ||
            decision.RequiredReviewActorRole is not ReviewActorRole requiredReviewRole)
        {
            return CreateUnavailableAction(
                ReviewWorkspaceActionKind.RequestChanges,
                ReviewWorkspaceActionStatus.StateNotEligible);
        }

        return roles.Contains(requiredRole)
            ? new ReviewWorkspaceAction(
                ReviewWorkspaceActionKind.RequestChanges,
                ReviewWorkspaceActionStatus.Authorized,
                Array.AsReadOnly([requiredReviewRole]))
            : CreateUnavailableAction(
                ReviewWorkspaceActionKind.RequestChanges,
                ReviewWorkspaceActionStatus.ActorNotAuthorized);
    }

    private static IReadOnlyList<ReviewActorRole> GetAssignedCommentRoles(
        IReadOnlyList<AuthorizationRole> roles)
    {
        ReviewActorRole[] assigned =
        [
            .. GetReviewAuthorRoles()
                .Where(role => roles.Contains(ToAuthorizationRole(role))),
        ];
        return Array.AsReadOnly(assigned);
    }

    private static IReadOnlyList<ReviewActorRole> GetAssignedApprovalRoles(
        IReadOnlyList<AuthorizationRole> roles)
    {
        ReviewActorRole[] assigned =
        [
            .. GetApprovalActorRoles()
                .Where(role => roles.Contains(ToAuthorizationRole(role))),
        ];
        return Array.AsReadOnly(assigned);
    }

    private static ReviewActorRole[] GetReviewAuthorRoles() =>
    [
        ReviewActorRole.Editor,
        ReviewActorRole.PerformerReviewer,
        ReviewActorRole.ContentApprover,
        ReviewActorRole.BrandApprover,
        ReviewActorRole.RightsApprover,
    ];

    private static ReviewActorRole[] GetApprovalActorRoles() =>
    [
        ReviewActorRole.ContentApprover,
        ReviewActorRole.BrandApprover,
        ReviewActorRole.RightsApprover,
    ];

    private static AuthorizationRole ToAuthorizationRole(ReviewActorRole role) =>
        role switch
        {
            ReviewActorRole.Editor => AuthorizationRole.Editor,
            ReviewActorRole.PerformerReviewer => AuthorizationRole.PerformerReviewer,
            ReviewActorRole.ContentApprover => AuthorizationRole.ContentApprover,
            ReviewActorRole.BrandApprover => AuthorizationRole.BrandApprover,
            ReviewActorRole.RightsApprover => AuthorizationRole.RightsApprover,
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "The review actor role cannot author review evidence."),
        };

    private static ReviewWorkspaceAction CreateUnavailableAction(
        ReviewWorkspaceActionKind kind,
        ReviewWorkspaceActionStatus status) =>
        new(kind, status, Array.Empty<ReviewActorRole>());

    private static void EnsureCurrentVersion(
        ReviewOperationContext context,
        ContentDigest expectedDigest)
    {
        if (!context.IsCurrentTarget ||
            context.Version.ContentDigest != expectedDigest)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.StaleVersion);
        }
    }

    private static void EnsureCurrentMutationStored(bool stored)
    {
        if (!stored)
        {
            throw new ReviewWorkspaceException(
                ReviewWorkspaceFailureReason.StaleVersion);
        }
    }

    private DateTimeOffset GetUtcNow() =>
        timeProvider.GetUtcNow().ToUniversalTime();

    private static EntityId<ApprovalIdTag> CreateApprovalId(
        EntityId<CompositionVersionIdTag> versionId,
        string actorSid,
        ReviewActorRole actorRole)
    {
        byte[] material = Encoding.UTF8.GetBytes(
            string.Concat(
                "hubradio.review.approval.v1\0",
                versionId,
                "\0",
                actorSid,
                "\0",
                actorRole.ToString()));
        byte[] hash = SHA256.HashData(material);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return EntityId<ApprovalIdTag>.From(new Guid(guidBytes));
    }

    private sealed record ReviewOperationContext(
        string ActorSid,
        CompositionVersion Version,
        StoredVersionEvidence Evidence,
        bool IsCurrentTarget,
        IReadOnlyList<AuthorizationRole> AuthorizationRoles);
}
