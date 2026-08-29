using System.Globalization;
using System.Text;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Policy;

public enum PublicationChangeOrigin
{
    Inherited,
    System,
    ExplicitHuman,
}

public enum PublicationScopeRelation
{
    /// <summary>
    /// The two <see cref="PublicationScope"/> labels are identical.
    /// </summary>
    /// <remarks>
    /// This compares only the scope label. In particular, a
    /// <see cref="PublicationScope.SpecificGroupOnly"/> match does not authorize a
    /// change to the group identity or destination identity, which are outside this policy.
    /// </remarks>
    SameScope,
    ProvenRestriction,
    PotentialExpansionOrIncomparable,
}

public enum PublicationPolicyDisposition
{
    NoChange,
    AllowedAutomaticRestriction,
    DeniedAutomaticUnprovenChange,
    RequiresAuthorizedRestriction,
    RequiresVersionedChange,
}

public sealed class PublicationPolicyDecision
{
    internal PublicationPolicyDecision(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        PublicationChangeOrigin origin,
        string? reason,
        PublicationScopeRelation relation,
        PublicationPolicyDisposition disposition,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredActingRole,
        bool createChildCompositionVersion,
        bool invalidateExistingReviewEvidence,
        bool reevaluateRequiredReviews,
        ReviewState? requiredNextState,
        bool auditRequired)
    {
        ValidateInvariant(
            parentScope,
            requestedChildScope,
            origin,
            reason,
            relation,
            disposition,
            requiredAuthorizationOperation,
            requiredActingRole,
            createChildCompositionVersion,
            invalidateExistingReviewEvidence,
            reevaluateRequiredReviews,
            requiredNextState,
            auditRequired);

        ParentScope = parentScope;
        RequestedChildScope = requestedChildScope;
        Origin = origin;
        Reason = reason;
        Relation = relation;
        Disposition = disposition;
        RequiredAuthorizationOperation = requiredAuthorizationOperation;
        RequiredActingRole = requiredActingRole;
        CreateChildCompositionVersion = createChildCompositionVersion;
        InvalidateExistingReviewEvidence = invalidateExistingReviewEvidence;
        ReevaluateRequiredReviews = reevaluateRequiredReviews;
        RequiredNextState = requiredNextState;
        AuditRequired = auditRequired;
    }

    public PublicationScope ParentScope { get; }

    public PublicationScope RequestedChildScope { get; }

    public PublicationChangeOrigin Origin { get; }

    public string? Reason { get; }

    public PublicationScopeRelation Relation { get; }

    public PublicationPolicyDisposition Disposition { get; }

    public AuthorizationOperation? RequiredAuthorizationOperation { get; }

    /// <summary>
    /// Gets the role that an orchestration layer must verify before applying a human change.
    /// </summary>
    public AuthorizationRole? RequiredActingRole { get; }

    public bool CreateChildCompositionVersion { get; }

    public bool InvalidateExistingReviewEvidence { get; }

    public bool ReevaluateRequiredReviews { get; }

    public ReviewState? RequiredNextState { get; }

    public bool AuditRequired { get; }

    private static void ValidateInvariant(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        PublicationChangeOrigin origin,
        string? reason,
        PublicationScopeRelation relation,
        PublicationPolicyDisposition disposition,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredActingRole,
        bool createChildCompositionVersion,
        bool invalidateExistingReviewEvidence,
        bool reevaluateRequiredReviews,
        ReviewState? requiredNextState,
        bool auditRequired)
    {
        bool allEnumsAreDefined = Enum.IsDefined(parentScope)
            && Enum.IsDefined(requestedChildScope)
            && Enum.IsDefined(origin)
            && Enum.IsDefined(relation)
            && Enum.IsDefined(disposition)
            && (requiredAuthorizationOperation is null
                || Enum.IsDefined(requiredAuthorizationOperation.Value))
            && (requiredActingRole is null || Enum.IsDefined(requiredActingRole.Value))
            && (requiredNextState is null || Enum.IsDefined(requiredNextState.Value));
        bool hasNoWorkflowHandoff = requiredAuthorizationOperation is null
            && requiredActingRole is null
            && !createChildCompositionVersion
            && !invalidateExistingReviewEvidence
            && !reevaluateRequiredReviews
            && requiredNextState is null;
        bool hasPublicationAuthorizationRequirement =
            requiredAuthorizationOperation
                == AuthorizationOperation.ChangePublicationClassification
            && requiredActingRole == AuthorizationRole.ContentApprover;
        bool isAutomatic = origin is PublicationChangeOrigin.Inherited
            or PublicationChangeOrigin.System;
        PublicationScopeRelation classifiedRelation = PublicationPolicy.ClassifyRelation(
            parentScope,
            requestedChildScope);
        bool relationMatchesScopes = relation == classifiedRelation;
        bool reasonIsValid = allEnumsAreDefined
            && IsReasonValid(origin, classifiedRelation, reason);

        bool isConsistent = allEnumsAreDefined
            && relationMatchesScopes
            && reasonIsValid
            && disposition switch
            {
                PublicationPolicyDisposition.NoChange =>
                    parentScope == requestedChildScope
                    && relation == PublicationScopeRelation.SameScope
                    && reason is null
                    && hasNoWorkflowHandoff
                    && !auditRequired,
                PublicationPolicyDisposition.AllowedAutomaticRestriction =>
                    parentScope != requestedChildScope
                    && isAutomatic
                    && relation == PublicationScopeRelation.ProvenRestriction
                    && reason is null
                    && hasNoWorkflowHandoff
                    && !auditRequired,
                PublicationPolicyDisposition.DeniedAutomaticUnprovenChange =>
                    parentScope != requestedChildScope
                    && isAutomatic
                    && relation
                        == PublicationScopeRelation.PotentialExpansionOrIncomparable
                    && reason is null
                    && hasNoWorkflowHandoff
                    && !auditRequired,
                PublicationPolicyDisposition.RequiresAuthorizedRestriction =>
                    parentScope != requestedChildScope
                    && origin == PublicationChangeOrigin.ExplicitHuman
                    && relation == PublicationScopeRelation.ProvenRestriction
                    && reason is not null
                    && hasPublicationAuthorizationRequirement
                    && !createChildCompositionVersion
                    && !invalidateExistingReviewEvidence
                    && !reevaluateRequiredReviews
                    && requiredNextState is null
                    && auditRequired,
                PublicationPolicyDisposition.RequiresVersionedChange =>
                    parentScope != requestedChildScope
                    && origin == PublicationChangeOrigin.ExplicitHuman
                    && relation
                        == PublicationScopeRelation.PotentialExpansionOrIncomparable
                    && reason is not null
                    && hasPublicationAuthorizationRequirement
                    && createChildCompositionVersion
                    && invalidateExistingReviewEvidence
                    && reevaluateRequiredReviews
                    && requiredNextState == ReviewState.Editing
                    && auditRequired,
                _ => false,
            };

        if (!isConsistent)
        {
            throw new InvalidOperationException(
                "Publication policy decision arguments are inconsistent.");
        }
    }

    private static bool IsReasonValid(
        PublicationChangeOrigin origin,
        PublicationScopeRelation relation,
        string? reason)
    {
        try
        {
            PublicationPolicy.ValidateReason(origin, relation, reason);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class PublicationPolicy
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Evaluates a requested publication-scope label relative to its parent label.
    /// </summary>
    /// <remarks>
    /// This is a pure policy evaluation. For an actual human change, the returned
    /// authorization operation and role are requirements for the caller to verify; they
    /// are not evidence that authorization has already occurred. Group and destination
    /// identity changes for <see cref="PublicationScope.SpecificGroupOnly"/> are outside
    /// this scope-label policy.
    /// </remarks>
    public static PublicationPolicyDecision Evaluate(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        PublicationChangeOrigin origin,
        string? reason)
    {
        ValidateDefined(parentScope, nameof(parentScope), "parent publication scope");
        ValidateDefined(
            requestedChildScope,
            nameof(requestedChildScope),
            "requested child publication scope");
        ValidateDefined(origin, nameof(origin), "publication change origin");

        PublicationScopeRelation relation = ClassifyRelation(
            parentScope,
            requestedChildScope);
        ValidateReason(origin, relation, reason);

        if (origin is not PublicationChangeOrigin.ExplicitHuman)
        {
            return EvaluateAutomatic(
                parentScope,
                requestedChildScope,
                origin,
                relation);
        }

        return EvaluateHuman(
            parentScope,
            requestedChildScope,
            reason,
            relation);
    }

    private static PublicationPolicyDecision EvaluateAutomatic(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        PublicationChangeOrigin origin,
        PublicationScopeRelation relation)
    {
        PublicationPolicyDisposition disposition = relation switch
        {
            PublicationScopeRelation.SameScope =>
                PublicationPolicyDisposition.NoChange,
            PublicationScopeRelation.ProvenRestriction =>
                PublicationPolicyDisposition.AllowedAutomaticRestriction,
            PublicationScopeRelation.PotentialExpansionOrIncomparable =>
                PublicationPolicyDisposition.DeniedAutomaticUnprovenChange,
            _ => throw new ArgumentOutOfRangeException(
                nameof(relation),
                relation,
                "Unknown publication scope relation."),
        };

        return CreateNoWorkflowHandoffDecision(
            parentScope,
            requestedChildScope,
            origin,
            relation,
            disposition);
    }

    private static PublicationPolicyDecision EvaluateHuman(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        string? reason,
        PublicationScopeRelation relation)
    {
        if (relation == PublicationScopeRelation.SameScope)
        {
            return CreateNoWorkflowHandoffDecision(
                parentScope,
                requestedChildScope,
                PublicationChangeOrigin.ExplicitHuman,
                relation,
                PublicationPolicyDisposition.NoChange);
        }

        const AuthorizationOperation requiredOperation =
            AuthorizationOperation.ChangePublicationClassification;
        const AuthorizationRole requiredRole = AuthorizationRole.ContentApprover;

        if (relation == PublicationScopeRelation.ProvenRestriction)
        {
            return new PublicationPolicyDecision(
                parentScope,
                requestedChildScope,
                PublicationChangeOrigin.ExplicitHuman,
                reason,
                relation,
                PublicationPolicyDisposition.RequiresAuthorizedRestriction,
                requiredOperation,
                requiredRole,
                createChildCompositionVersion: false,
                invalidateExistingReviewEvidence: false,
                reevaluateRequiredReviews: false,
                requiredNextState: null,
                auditRequired: true);
        }

        return new PublicationPolicyDecision(
            parentScope,
            requestedChildScope,
            PublicationChangeOrigin.ExplicitHuman,
            reason,
            relation,
            PublicationPolicyDisposition.RequiresVersionedChange,
            requiredOperation,
            requiredRole,
            createChildCompositionVersion: true,
            invalidateExistingReviewEvidence: true,
            reevaluateRequiredReviews: true,
            requiredNextState: ReviewState.Editing,
            auditRequired: true);
    }

    private static PublicationPolicyDecision CreateNoWorkflowHandoffDecision(
        PublicationScope parentScope,
        PublicationScope requestedChildScope,
        PublicationChangeOrigin origin,
        PublicationScopeRelation relation,
        PublicationPolicyDisposition disposition)
    {
        return new PublicationPolicyDecision(
            parentScope,
            requestedChildScope,
            origin,
            reason: null,
            relation,
            disposition,
            requiredAuthorizationOperation: null,
            requiredActingRole: null,
            createChildCompositionVersion: false,
            invalidateExistingReviewEvidence: false,
            reevaluateRequiredReviews: false,
            requiredNextState: null,
            auditRequired: false);
    }

    internal static PublicationScopeRelation ClassifyRelation(
        PublicationScope parentScope,
        PublicationScope requestedChildScope)
    {
        return (parentScope, requestedChildScope) switch
        {
            (PublicationScope.InternalOnly, PublicationScope.InternalOnly)
                or (PublicationScope.SpecificGroupOnly, PublicationScope.SpecificGroupOnly)
                or (PublicationScope.ExternalCandidate, PublicationScope.ExternalCandidate)
                or (PublicationScope.ExternalPublicationApproved,
                    PublicationScope.ExternalPublicationApproved)
                or (PublicationScope.PublicationForbidden,
                    PublicationScope.PublicationForbidden)
                or (PublicationScope.PendingDecision, PublicationScope.PendingDecision) =>
                PublicationScopeRelation.SameScope,

            (PublicationScope.InternalOnly, PublicationScope.PendingDecision)
                or (PublicationScope.InternalOnly, PublicationScope.PublicationForbidden)
                or (PublicationScope.ExternalCandidate, PublicationScope.InternalOnly)
                or (PublicationScope.ExternalCandidate, PublicationScope.PendingDecision)
                or (PublicationScope.ExternalCandidate,
                    PublicationScope.PublicationForbidden)
                or (PublicationScope.ExternalPublicationApproved,
                    PublicationScope.PendingDecision)
                or (PublicationScope.ExternalPublicationApproved,
                    PublicationScope.PublicationForbidden)
                or (PublicationScope.PendingDecision,
                    PublicationScope.PublicationForbidden) =>
                PublicationScopeRelation.ProvenRestriction,

            _ => PublicationScopeRelation.PotentialExpansionOrIncomparable,
        };
    }

    internal static void ValidateReason(
        PublicationChangeOrigin origin,
        PublicationScopeRelation relation,
        string? reason)
    {
        if (origin is not PublicationChangeOrigin.ExplicitHuman)
        {
            ValidateAutomaticContext(reason);
            return;
        }

        ValidateHumanReason(relation, reason);
    }

    private static void ValidateAutomaticContext(string? reason)
    {
        if (reason is not null)
        {
            throw new ArgumentException(
                "An inherited or system change must not carry a human change reason.",
                nameof(reason));
        }
    }

    private static void ValidateHumanReason(
        PublicationScopeRelation relation,
        string? reason)
    {
        if (relation == PublicationScopeRelation.SameScope)
        {
            if (reason is not null)
            {
                throw new ArgumentException(
                    "A same-scope human evaluation must not carry a change reason.",
                    nameof(reason));
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(reason);
        if (string.IsNullOrWhiteSpace(reason)
            || !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A human publication-scope change reason must not be blank or have leading or trailing whitespace.",
                nameof(reason));
        }

        try
        {
            _ = StrictUtf8.GetByteCount(reason);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A human publication-scope change reason must contain valid Unicode that can be encoded as strict UTF-8.",
                nameof(reason),
                exception);
        }

        if (!ContainsSubstantiveUnicodeScalar(reason))
        {
            throw new ArgumentException(
                "A human publication-scope change reason must contain at least one visible, substantive Unicode scalar.",
                nameof(reason));
        }
    }

    private static bool ContainsSubstantiveUnicodeScalar(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (!Rune.IsWhiteSpace(rune)
                && !IsNonSubstantiveSpecialScalar(rune)
                && category is not (UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonSubstantiveSpecialScalar(Rune rune)
    {
        return rune.Value switch
        {
            0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,
            _ => false,
        };
    }

    private static void ValidateDefined<TEnum>(
        TEnum value,
        string parameterName,
        string description)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Unknown {description}.");
        }
    }
}