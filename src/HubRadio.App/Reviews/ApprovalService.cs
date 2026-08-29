using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Reviews;

/// <summary>
/// Classifies the supplied persisted approval evidence against one current version.
/// </summary>
/// <remarks>
/// Empty or non-empty lists do not establish that every approval required by policy has
/// been collected. S-011 must separately determine the required roles before setting an
/// approval gate status.
/// </remarks>
public sealed class ApprovalValidityResult
{
    internal ApprovalValidityResult(
        IReadOnlyList<StoredApproval> validApprovals,
        IReadOnlyList<StoredApproval> staleApprovals)
    {
        ValidApprovals = Array.AsReadOnly(validApprovals.ToArray());
        StaleApprovals = Array.AsReadOnly(staleApprovals.ToArray());
    }

    public IReadOnlyList<StoredApproval> ValidApprovals { get; }

    public IReadOnlyList<StoredApproval> StaleApprovals { get; }
}

public sealed class ApprovalService
{
    private readonly ReviewStore _reviewStore;
    private readonly AuthorizationService _authorizationService;

    public ApprovalService(
        ReviewStore reviewStore,
        AuthorizationService authorizationService)
    {
        ArgumentNullException.ThrowIfNull(reviewStore);
        ArgumentNullException.ThrowIfNull(authorizationService);

        _reviewStore = reviewStore;
        _authorizationService = authorizationService;
    }

    public Approval RecordApproval(
        EntityId<ApprovalIdTag> approvalId,
        CompositionVersion compositionVersion,
        string actorSid,
        ReviewActorRole actorRole,
        DateTimeOffset approvedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(approvalId);
        ArgumentNullException.ThrowIfNull(compositionVersion);

        var approval = new Approval(
            approvalId,
            compositionVersion,
            actorSid,
            actorRole,
            approvedAt,
            reason);
        (AuthorizationRole authorizationRole, AuthorizationOperation operation) =
            GetApprovalAuthorization(actorRole);
        _authorizationService.DemandAs(actorSid, authorizationRole, operation);
        _reviewStore.AppendAuthorizedApproval(
            approval,
            authorizationRole,
            operation);
        return approval;
    }

    /// <summary>
    /// Verifies each supplied candidate against storage, then classifies it as valid or
    /// stale for <paramref name="currentVersion"/>.
    /// </summary>
    /// <remarks>
    /// This method does not infer missing required approvals from an omitted candidate.
    /// </remarks>
    public ApprovalValidityResult EvaluateValidity(
        CompositionVersion currentVersion,
        IReadOnlyList<StoredApproval> candidateApprovals)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(candidateApprovals);

        StoredVersionEvidence currentEvidence =
            _reviewStore.GetVersionEvidence(currentVersion.Id)
            ?? throw new InvalidOperationException(
                "The current composition version does not exist.");
        ValidateVersionBinding(currentVersion, currentEvidence);

        StoredApproval[] candidates = candidateApprovals.ToArray();
        var seenApprovalIds = new HashSet<EntityId<ApprovalIdTag>>();
        var evidenceByVersion = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            StoredVersionEvidence>
        {
            [currentVersion.Id] = currentEvidence,
        };
        var validApprovals = new List<StoredApproval>(candidates.Length);
        var staleApprovals = new List<StoredApproval>(candidates.Length);

        foreach (StoredApproval candidate in candidates)
        {
            if (candidate is null)
            {
                throw new ArgumentException(
                    "Candidate approvals must not contain null elements.",
                    nameof(candidateApprovals));
            }

            if (!seenApprovalIds.Add(candidate.Id))
            {
                throw new ArgumentException(
                    "Candidate approvals must not contain duplicate approval IDs.",
                    nameof(candidateApprovals));
            }

            if (candidate.ProjectId != currentVersion.ProjectId)
            {
                throw new InvalidOperationException(
                    "A candidate approval belongs to a different project.");
            }

            if (!evidenceByVersion.TryGetValue(
                    candidate.CompositionVersionId,
                    out StoredVersionEvidence? candidateEvidence))
            {
                candidateEvidence = _reviewStore.GetVersionEvidence(
                    candidate.CompositionVersionId)
                    ?? throw new InvalidOperationException(
                        "A candidate approval composition version does not exist.");
                evidenceByVersion.Add(
                    candidate.CompositionVersionId,
                    candidateEvidence);
            }

            ValidateStoredApproval(candidate, candidateEvidence);
            if (candidate.CompositionVersionId == currentVersion.Id
                && candidate.CompositionVersionDigest == currentVersion.ContentDigest)
            {
                validApprovals.Add(candidate);
            }
            else
            {
                staleApprovals.Add(candidate);
            }
        }

        return new ApprovalValidityResult(validApprovals, staleApprovals);
    }

    private static void ValidateVersionBinding(
        CompositionVersion currentVersion,
        StoredVersionEvidence currentEvidence)
    {
        if (currentEvidence.CompositionVersionId != currentVersion.Id
            || currentEvidence.ProjectId != currentVersion.ProjectId
            || currentEvidence.CompositionVersionDigest != currentVersion.ContentDigest)
        {
            throw new InvalidOperationException(
                "The current composition version does not match persisted storage.");
        }
    }

    private static void ValidateStoredApproval(
        StoredApproval candidate,
        StoredVersionEvidence candidateEvidence)
    {
        if (candidateEvidence.CompositionVersionId != candidate.CompositionVersionId
            || candidateEvidence.ProjectId != candidate.ProjectId
            || candidateEvidence.CompositionVersionDigest
                != candidate.CompositionVersionDigest)
        {
            throw new InvalidOperationException(
                "A candidate approval does not match its persisted composition version.");
        }

        StoredApproval? persisted = candidateEvidence.Approvals.SingleOrDefault(
            approval => approval.Id == candidate.Id);
        if (persisted is null
            || persisted.ProjectId != candidate.ProjectId
            || persisted.CompositionVersionId != candidate.CompositionVersionId
            || persisted.CompositionVersionDigest != candidate.CompositionVersionDigest
            || !string.Equals(
                persisted.ActorId,
                candidate.ActorId,
                StringComparison.Ordinal)
            || persisted.ActorRole != candidate.ActorRole
            || !persisted.ApprovedAt.EqualsExact(candidate.ApprovedAt)
            || !string.Equals(
                persisted.Reason,
                candidate.Reason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A candidate approval does not match persisted approval evidence.");
        }
    }

    private static (
        AuthorizationRole AuthorizationRole,
        AuthorizationOperation Operation) GetApprovalAuthorization(
        ReviewActorRole actorRole)
    {
        return actorRole switch
        {
            ReviewActorRole.ContentApprover =>
                (AuthorizationRole.ContentApprover,
                    AuthorizationOperation.RecordContentReview),
            ReviewActorRole.BrandApprover =>
                (AuthorizationRole.BrandApprover,
                    AuthorizationOperation.RecordBrandReview),
            ReviewActorRole.RightsApprover =>
                (AuthorizationRole.RightsApprover,
                    AuthorizationOperation.RecordRightsDecision),
            _ when Enum.IsDefined(actorRole) =>
                throw new ArgumentOutOfRangeException(
                    nameof(actorRole),
                    actorRole,
                    "An approval actor must use an approver role."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(actorRole),
                actorRole,
                "Unknown review actor role."),
        };
    }
}