using HubRadio.Core.Security;

namespace HubRadio.Core.Reviews;

public enum ReviewTransitionKind
{
    SubmitForEditorReview,
    BlockPublication,
    RequestChanges,
    CompleteEditorReview,
    CompletePerformerReview,
    CompleteContentReview,
    CompleteBrandReview,
    RestartEditing,
    StartFinalRendering,
    RetryFinalRendering,
    CompleteFinalRendering,
    RequestTechnicalRerender,
    RequestContentCorrection,
    Export,
    RecordPublication,
}

public enum PerformerReviewRequirement
{
    NotRequired,
    Required,
}

public enum BrandReviewRequirement
{
    NotRequired,
    Required,
}

public enum ReviewExportDestination
{
    Internal,
    External,
}

[Flags]
public enum ReviewGateStatus
{
    None = 0,
    RequiredApprovalEvidenceRecorded = 1 << 0,
    RightsPolicySatisfied = 1 << 1,
    ApprovedContentDigestMatched = 1 << 2,
    FinalRenderPolicySatisfied = 1 << 3,
    ExportPolicySatisfied = 1 << 4,
}

public enum ReviewTransitionDisposition
{
    Allowed,
    Denied,
    RequiresPrerequisite,
}

/// <summary>
/// Describes one closed review action and only the context that action can use.
/// </summary>
/// <remarks>
/// A request intentionally contains no reason. The caller records the substantive reason,
/// actor, role, and time in a <see cref="ReviewRecord"/> when it applies an allowed decision.
/// Gate status is caller-supplied status, not proof that this pure type verified persisted
/// approval, rights, digest, or policy evidence.
/// </remarks>
public sealed class ReviewTransitionRequest
{
    private ReviewTransitionRequest(
        ReviewTransitionKind kind,
        PerformerReviewRequirement? performerReviewRequirement,
        BrandReviewRequirement? brandReviewRequirement,
        ReviewExportDestination? exportDestination,
        ReviewGateStatus satisfiedGateStatus)
    {
        bool contextIsConsistent = kind switch
        {
            ReviewTransitionKind.SubmitForEditorReview
                or ReviewTransitionKind.BlockPublication
                or ReviewTransitionKind.RequestChanges
                or ReviewTransitionKind.CompletePerformerReview
                or ReviewTransitionKind.RestartEditing
                or ReviewTransitionKind.CompleteFinalRendering
                or ReviewTransitionKind.RequestContentCorrection
                or ReviewTransitionKind.RecordPublication =>
                performerReviewRequirement is null
                && brandReviewRequirement is null
                && exportDestination is null
                && satisfiedGateStatus == ReviewGateStatus.None,

            ReviewTransitionKind.CompleteEditorReview =>
                performerReviewRequirement is not null
                && brandReviewRequirement is null
                && exportDestination is null
                && satisfiedGateStatus == ReviewGateStatus.None,

            ReviewTransitionKind.CompleteContentReview =>
                performerReviewRequirement is null
                && brandReviewRequirement is not null
                && exportDestination is null
                && (brandReviewRequirement != global::HubRadio.Core.Reviews.BrandReviewRequirement.Required
                    || satisfiedGateStatus == ReviewGateStatus.None),

            ReviewTransitionKind.CompleteBrandReview
                or ReviewTransitionKind.StartFinalRendering
                or ReviewTransitionKind.RetryFinalRendering
                or ReviewTransitionKind.RequestTechnicalRerender =>
                performerReviewRequirement is null
                && brandReviewRequirement is null
                && exportDestination is null,

            ReviewTransitionKind.Export =>
                performerReviewRequirement is null
                && brandReviewRequirement is null
                && exportDestination is not null,

            _ => false,
        };

        if (!Enum.IsDefined(kind)
            || performerReviewRequirement is not null
                && !Enum.IsDefined(performerReviewRequirement.Value)
            || brandReviewRequirement is not null
                && !Enum.IsDefined(brandReviewRequirement.Value)
            || exportDestination is not null
                && !Enum.IsDefined(exportDestination.Value)
            || !ReviewTransitionValidation.IsValidGateStatus(satisfiedGateStatus)
            || !ReviewTransitionValidation.IsPermittedGateStatus(
                kind,
                brandReviewRequirement,
                exportDestination,
                satisfiedGateStatus)
            || !contextIsConsistent)
        {
            throw new InvalidOperationException(
                "Review transition request arguments are inconsistent.");
        }

        Kind = kind;
        PerformerReviewRequirement = performerReviewRequirement;
        BrandReviewRequirement = brandReviewRequirement;
        ExportDestination = exportDestination;
        SatisfiedGateStatus = satisfiedGateStatus;
    }

    public ReviewTransitionKind Kind { get; }

    public PerformerReviewRequirement? PerformerReviewRequirement { get; }

    public BrandReviewRequirement? BrandReviewRequirement { get; }

    public ReviewExportDestination? ExportDestination { get; }

    public ReviewGateStatus SatisfiedGateStatus { get; }

    public static ReviewTransitionRequest SubmitForEditorReview() =>
        CreateSimple(ReviewTransitionKind.SubmitForEditorReview);

    public static ReviewTransitionRequest BlockPublication() =>
        CreateSimple(ReviewTransitionKind.BlockPublication);

    public static ReviewTransitionRequest RequestChanges() =>
        CreateSimple(ReviewTransitionKind.RequestChanges);

    public static ReviewTransitionRequest CompleteEditorReview(
        PerformerReviewRequirement performerReviewRequirement)
    {
        ValidateDefined(
            performerReviewRequirement,
            nameof(performerReviewRequirement),
            "performer-review requirement");

        return new ReviewTransitionRequest(
            ReviewTransitionKind.CompleteEditorReview,
            performerReviewRequirement,
            brandReviewRequirement: null,
            exportDestination: null,
            ReviewGateStatus.None);
    }

    public static ReviewTransitionRequest CompletePerformerReview() =>
        CreateSimple(ReviewTransitionKind.CompletePerformerReview);

    public static ReviewTransitionRequest CompleteContentReview(
        BrandReviewRequirement brandReviewRequirement,
        ReviewGateStatus satisfiedGateStatus)
    {
        ValidateDefined(
            brandReviewRequirement,
            nameof(brandReviewRequirement),
            "brand-review requirement");
        ReviewTransitionValidation.ValidatePermittedGateStatus(
            ReviewTransitionKind.CompleteContentReview,
            brandReviewRequirement,
            exportDestination: null,
            satisfiedGateStatus,
            nameof(satisfiedGateStatus));

        return new ReviewTransitionRequest(
            ReviewTransitionKind.CompleteContentReview,
            performerReviewRequirement: null,
            brandReviewRequirement,
            exportDestination: null,
            satisfiedGateStatus);
    }

    public static ReviewTransitionRequest CompleteBrandReview(
        ReviewGateStatus satisfiedGateStatus) =>
        CreateWithGateStatus(
            ReviewTransitionKind.CompleteBrandReview,
            satisfiedGateStatus);

    public static ReviewTransitionRequest RestartEditing() =>
        CreateSimple(ReviewTransitionKind.RestartEditing);

    public static ReviewTransitionRequest StartFinalRendering(
        ReviewGateStatus satisfiedGateStatus) =>
        CreateWithGateStatus(
            ReviewTransitionKind.StartFinalRendering,
            satisfiedGateStatus);

    public static ReviewTransitionRequest RetryFinalRendering(
        ReviewGateStatus satisfiedGateStatus) =>
        CreateWithGateStatus(
            ReviewTransitionKind.RetryFinalRendering,
            satisfiedGateStatus);

    public static ReviewTransitionRequest CompleteFinalRendering() =>
        CreateSimple(ReviewTransitionKind.CompleteFinalRendering);

    public static ReviewTransitionRequest RequestTechnicalRerender(
        ReviewGateStatus satisfiedGateStatus) =>
        CreateWithGateStatus(
            ReviewTransitionKind.RequestTechnicalRerender,
            satisfiedGateStatus);

    public static ReviewTransitionRequest RequestContentCorrection() =>
        CreateSimple(ReviewTransitionKind.RequestContentCorrection);

    public static ReviewTransitionRequest Export(
        ReviewExportDestination exportDestination,
        ReviewGateStatus satisfiedGateStatus)
    {
        ValidateDefined(
            exportDestination,
            nameof(exportDestination),
            "review export destination");
        ReviewTransitionValidation.ValidatePermittedGateStatus(
            ReviewTransitionKind.Export,
            brandReviewRequirement: null,
            exportDestination,
            satisfiedGateStatus,
            nameof(satisfiedGateStatus));

        return new ReviewTransitionRequest(
            ReviewTransitionKind.Export,
            performerReviewRequirement: null,
            brandReviewRequirement: null,
            exportDestination,
            satisfiedGateStatus);
    }

    public static ReviewTransitionRequest RecordPublication() =>
        CreateSimple(ReviewTransitionKind.RecordPublication);

    private static ReviewTransitionRequest CreateSimple(ReviewTransitionKind kind) =>
        new(
            kind,
            performerReviewRequirement: null,
            brandReviewRequirement: null,
            exportDestination: null,
            ReviewGateStatus.None);

    private static ReviewTransitionRequest CreateWithGateStatus(
        ReviewTransitionKind kind,
        ReviewGateStatus satisfiedGateStatus)
    {
        ReviewTransitionValidation.ValidatePermittedGateStatus(
            kind,
            brandReviewRequirement: null,
            exportDestination: null,
            satisfiedGateStatus,
            nameof(satisfiedGateStatus));

        return new ReviewTransitionRequest(
            kind,
            performerReviewRequirement: null,
            brandReviewRequirement: null,
            exportDestination: null,
            satisfiedGateStatus);
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

/// <summary>
/// An immutable policy decision. An allowed disposition is not authorization proof: the
/// caller must verify the exact returned role and operation against current assignments and
/// persist a <see cref="ReviewRecord"/> before applying the state change.
/// </summary>
public sealed class ReviewTransitionDecision
{
    internal ReviewTransitionDecision(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        ReviewTransitionDisposition disposition,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredAuthorizationRole,
        ReviewActorRole? requiredReviewActorRole,
        ReviewGateStatus satisfiedGateStatus,
        ReviewGateStatus requiredGateStatus,
        ReviewGateStatus missingGateStatus,
        bool createChildCompositionVersion,
        bool invalidateExistingApprovalEvidence,
        bool preserveApprovedContentDigest,
        bool technicalRetry,
        bool recordPublicationFactOnly)
    {
        ValidateInvariant(
            from,
            to,
            kind,
            disposition,
            requiredAuthorizationOperation,
            requiredAuthorizationRole,
            requiredReviewActorRole,
            satisfiedGateStatus,
            requiredGateStatus,
            missingGateStatus,
            createChildCompositionVersion,
            invalidateExistingApprovalEvidence,
            preserveApprovedContentDigest,
            technicalRetry,
            recordPublicationFactOnly);

        From = from;
        To = to;
        Kind = kind;
        Disposition = disposition;
        RequiredAuthorizationOperation = requiredAuthorizationOperation;
        RequiredAuthorizationRole = requiredAuthorizationRole;
        RequiredReviewActorRole = requiredReviewActorRole;
        SatisfiedGateStatus = satisfiedGateStatus;
        RequiredGateStatus = requiredGateStatus;
        MissingGateStatus = missingGateStatus;
        CreateChildCompositionVersion = createChildCompositionVersion;
        InvalidateExistingApprovalEvidence = invalidateExistingApprovalEvidence;
        PreserveApprovedContentDigest = preserveApprovedContentDigest;
        TechnicalRetry = technicalRetry;
        RecordPublicationFactOnly = recordPublicationFactOnly;
    }

    public ReviewState From { get; }

    /// <summary>
    /// Gets the requested destination. The caller must apply it only when
    /// <see cref="Disposition"/> is <see cref="ReviewTransitionDisposition.Allowed"/>.
    /// </summary>
    public ReviewState To { get; }

    public ReviewTransitionKind Kind { get; }

    public ReviewTransitionDisposition Disposition { get; }

    public AuthorizationOperation? RequiredAuthorizationOperation { get; }

    public AuthorizationRole? RequiredAuthorizationRole { get; }

    public ReviewActorRole? RequiredReviewActorRole { get; }

    public ReviewGateStatus SatisfiedGateStatus { get; }

    public ReviewGateStatus RequiredGateStatus { get; }

    public ReviewGateStatus MissingGateStatus { get; }

    public bool CreateChildCompositionVersion { get; }

    public bool InvalidateExistingApprovalEvidence { get; }

    public bool PreserveApprovedContentDigest { get; }

    public bool TechnicalRetry { get; }

    public bool RecordPublicationFactOnly { get; }

    /// <summary>
    /// Gets the operation that must have authorized a supplied rights-policy decision.
    /// This is a prerequisite-evidence requirement, not permission to perform this transition.
    /// </summary>
    public AuthorizationOperation? RequiredRightsGateAuthorizationOperation =>
        RequiresRightsGate
            ? AuthorizationOperation.RecordRightsDecision
            : null;

    public AuthorizationRole? RequiredRightsGateAuthorizationRole =>
        RequiresRightsGate
            ? AuthorizationRole.RightsApprover
            : null;

    public ReviewActorRole? RequiredRightsGateReviewActorRole =>
        RequiresRightsGate
            ? ReviewActorRole.RightsApprover
            : null;

    private bool RequiresRightsGate =>
        (RequiredGateStatus & ReviewGateStatus.RightsPolicySatisfied) != 0;

    private static void ValidateInvariant(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        ReviewTransitionDisposition disposition,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredAuthorizationRole,
        ReviewActorRole? requiredReviewActorRole,
        ReviewGateStatus satisfiedGateStatus,
        ReviewGateStatus requiredGateStatus,
        ReviewGateStatus missingGateStatus,
        bool createChildCompositionVersion,
        bool invalidateExistingApprovalEvidence,
        bool preserveApprovedContentDigest,
        bool technicalRetry,
        bool recordPublicationFactOnly)
    {
        bool enumsAndFlagsAreDefined = Enum.IsDefined(from)
            && Enum.IsDefined(to)
            && Enum.IsDefined(kind)
            && Enum.IsDefined(disposition)
            && (requiredAuthorizationOperation is null
                || Enum.IsDefined(requiredAuthorizationOperation.Value))
            && (requiredAuthorizationRole is null
                || Enum.IsDefined(requiredAuthorizationRole.Value))
            && (requiredReviewActorRole is null
                || Enum.IsDefined(requiredReviewActorRole.Value))
            && ReviewTransitionValidation.IsValidGateStatus(satisfiedGateStatus)
            && ReviewTransitionValidation.IsValidGateStatus(requiredGateStatus)
            && ReviewTransitionValidation.IsValidGateStatus(missingGateStatus);
        bool targetMatchesKind = TargetMatchesKind(kind, to);
        bool allAuthorizationValuesAreNull = requiredAuthorizationOperation is null
            && requiredAuthorizationRole is null
            && requiredReviewActorRole is null;
        bool allAuthorizationValuesArePresent = requiredAuthorizationOperation is not null
            && requiredAuthorizationRole is not null
            && requiredReviewActorRole is not null;
        bool authorizationIsValid = allAuthorizationValuesArePresent
            && IsValidAuthorizationMapping(
                requiredAuthorizationOperation!.Value,
                requiredAuthorizationRole!.Value,
                requiredReviewActorRole!.Value);
        ReviewGateStatus calculatedMissing = requiredGateStatus & ~satisfiedGateStatus;
        bool gateStatusIsConsistent = missingGateStatus == calculatedMissing;
        bool versionFlagsAreConsistent =
            createChildCompositionVersion == invalidateExistingApprovalEvidence
            && (!createChildCompositionVersion
                || (from == ReviewState.ChangesRequested
                    && to == ReviewState.Editing
                    && kind == ReviewTransitionKind.RestartEditing));
        bool technicalFlagsAreConsistent = !technicalRetry
            || preserveApprovedContentDigest
                && !createChildCompositionVersion
                && ((from == ReviewState.FinalRendering
                        && to == ReviewState.FinalRendering
                        && kind == ReviewTransitionKind.RetryFinalRendering)
                    || (from == ReviewState.QualityControl
                        && to == ReviewState.FinalRendering
                        && kind == ReviewTransitionKind.RequestTechnicalRerender));
        bool selfLoopIsExactTechnicalRetry = disposition == ReviewTransitionDisposition.Denied
            || from != to
            || from == ReviewState.FinalRendering
                && kind == ReviewTransitionKind.RetryFinalRendering
                && technicalRetry
                && preserveApprovedContentDigest;
        bool publicationFactIsConsistent = !recordPublicationFactOnly
            || from == ReviewState.Exported
                && to == ReviewState.Published
                && kind == ReviewTransitionKind.RecordPublication
                && preserveApprovedContentDigest
                && !technicalRetry
                && !createChildCompositionVersion;
        bool preservationIsConsistent = !preserveApprovedContentDigest
            || IsContentPreservingHandoff(from, to, kind);
        bool dispositionIsConsistent = disposition switch
        {
            ReviewTransitionDisposition.Denied =>
                allAuthorizationValuesAreNull
                && requiredGateStatus == ReviewGateStatus.None
                && missingGateStatus == ReviewGateStatus.None
                && !createChildCompositionVersion
                && !invalidateExistingApprovalEvidence
                && !preserveApprovedContentDigest
                && !technicalRetry
                && !recordPublicationFactOnly,
            ReviewTransitionDisposition.Allowed =>
                authorizationIsValid
                && missingGateStatus == ReviewGateStatus.None,
            ReviewTransitionDisposition.RequiresPrerequisite =>
                authorizationIsValid
                && requiredGateStatus != ReviewGateStatus.None
                && missingGateStatus != ReviewGateStatus.None,
            _ => false,
        };
        bool transitionContractIsCanonical =
            disposition == ReviewTransitionDisposition.Denied
                ? ReviewStateMachine.MatchesCanonicalDeniedTransitionContract(
                    from,
                    to,
                    kind,
                    satisfiedGateStatus)
                : ReviewStateMachine.MatchesCanonicalTransitionContract(
                from,
                to,
                kind,
                requiredAuthorizationOperation,
                requiredAuthorizationRole,
                requiredReviewActorRole,
                satisfiedGateStatus,
                requiredGateStatus,
                createChildCompositionVersion,
                invalidateExistingApprovalEvidence,
                preserveApprovedContentDigest,
                technicalRetry,
                recordPublicationFactOnly);

        if (!enumsAndFlagsAreDefined
            || !targetMatchesKind
            || !gateStatusIsConsistent
            || !versionFlagsAreConsistent
            || !technicalFlagsAreConsistent
            || !selfLoopIsExactTechnicalRetry
            || !publicationFactIsConsistent
            || !preservationIsConsistent
            || !dispositionIsConsistent
            || !transitionContractIsCanonical)
        {
            throw new InvalidOperationException(
                "Review transition decision arguments are inconsistent.");
        }
    }

    private static bool TargetMatchesKind(
        ReviewTransitionKind kind,
        ReviewState to)
    {
        return kind switch
        {
            ReviewTransitionKind.SubmitForEditorReview =>
                to == ReviewState.EditorReview,
            ReviewTransitionKind.BlockPublication =>
                to == ReviewState.PublicationBlocked,
            ReviewTransitionKind.RequestChanges
                or ReviewTransitionKind.RequestContentCorrection =>
                to == ReviewState.ChangesRequested,
            ReviewTransitionKind.CompleteEditorReview =>
                to is ReviewState.PerformerReview or ReviewState.ContentReview,
            ReviewTransitionKind.CompletePerformerReview =>
                to == ReviewState.ContentReview,
            ReviewTransitionKind.CompleteContentReview =>
                to is ReviewState.BrandReview or ReviewState.Approved,
            ReviewTransitionKind.CompleteBrandReview =>
                to == ReviewState.Approved,
            ReviewTransitionKind.RestartEditing =>
                to == ReviewState.Editing,
            ReviewTransitionKind.StartFinalRendering
                or ReviewTransitionKind.RetryFinalRendering
                or ReviewTransitionKind.RequestTechnicalRerender =>
                to == ReviewState.FinalRendering,
            ReviewTransitionKind.CompleteFinalRendering =>
                to == ReviewState.QualityControl,
            ReviewTransitionKind.Export =>
                to == ReviewState.Exported,
            ReviewTransitionKind.RecordPublication =>
                to == ReviewState.Published,
            _ => false,
        };
    }

    private static bool IsValidAuthorizationMapping(
        AuthorizationOperation operation,
        AuthorizationRole authorizationRole,
        ReviewActorRole reviewActorRole)
    {
        bool rolesMatch = (authorizationRole, reviewActorRole) switch
        {
            (AuthorizationRole.Editor, ReviewActorRole.Editor) => true,
            (AuthorizationRole.PerformerReviewer,
                ReviewActorRole.PerformerReviewer) => true,
            (AuthorizationRole.ContentApprover,
                ReviewActorRole.ContentApprover) => true,
            (AuthorizationRole.BrandApprover,
                ReviewActorRole.BrandApprover) => true,
            _ => false,
        };

        return rolesMatch
            && authorizationRole is not AuthorizationRole.Administrator
            && authorizationRole is not AuthorizationRole.RightsApprover
            && PermissionMatrix.CanPerform(authorizationRole, operation);
    }

    private static bool IsContentPreservingHandoff(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind)
    {
        return (from, to, kind) switch
        {
            (ReviewState.Approved,
                ReviewState.FinalRendering,
                ReviewTransitionKind.StartFinalRendering) => true,
            (ReviewState.FinalRendering,
                ReviewState.FinalRendering,
                ReviewTransitionKind.RetryFinalRendering) => true,
            (ReviewState.FinalRendering,
                ReviewState.QualityControl,
                ReviewTransitionKind.CompleteFinalRendering) => true,
            (ReviewState.QualityControl,
                ReviewState.FinalRendering,
                ReviewTransitionKind.RequestTechnicalRerender) => true,
            (ReviewState.QualityControl,
                ReviewState.Exported,
                ReviewTransitionKind.Export) => true,
            (ReviewState.Exported,
                ReviewState.Published,
                ReviewTransitionKind.RecordPublication) => true,
            _ => false,
        };
    }
}

/// <summary>
/// Evaluates the closed review workflow without mutating or verifying external state.
/// </summary>
public static class ReviewStateMachine
{
    private const ReviewGateStatus ApprovalGateStatus =
        ReviewTransitionValidation.ApprovalGateStatus;

    private const ReviewGateStatus FinalRenderGateStatus =
        ReviewTransitionValidation.FinalRenderGateStatus;

    private const ReviewGateStatus InternalExportGateStatus =
        ReviewTransitionValidation.InternalExportGateStatus;

    public static ReviewTransitionDecision Evaluate(
        ReviewState from,
        ReviewTransitionRequest request)
    {
        if (!Enum.IsDefined(from))
        {
            throw new ArgumentOutOfRangeException(
                nameof(from),
                from,
                "Unknown review state.");
        }

        ArgumentNullException.ThrowIfNull(request);

        ReviewState to = GetTarget(request);
        (AuthorizationOperation Operation,
            AuthorizationRole Role,
            ReviewActorRole ReviewActorRole)? authorization =
            GetAuthorization(from, request.Kind);

        if (authorization is null)
        {
            return new ReviewTransitionDecision(
                from,
                to,
                request.Kind,
                ReviewTransitionDisposition.Denied,
                requiredAuthorizationOperation: null,
                requiredAuthorizationRole: null,
                requiredReviewActorRole: null,
                request.SatisfiedGateStatus,
                requiredGateStatus: ReviewGateStatus.None,
                missingGateStatus: ReviewGateStatus.None,
                createChildCompositionVersion: false,
                invalidateExistingApprovalEvidence: false,
                preserveApprovedContentDigest: false,
                technicalRetry: false,
                recordPublicationFactOnly: false);
        }

        ReviewGateStatus requiredGateStatus = GetRequiredGateStatus(from, request);
        ReviewGateStatus missingGateStatus =
            requiredGateStatus & ~request.SatisfiedGateStatus;
        ReviewTransitionDisposition disposition =
            missingGateStatus == ReviewGateStatus.None
                ? ReviewTransitionDisposition.Allowed
                : ReviewTransitionDisposition.RequiresPrerequisite;
        bool createChildCompositionVersion =
            (from == ReviewState.ChangesRequested
                && request.Kind == ReviewTransitionKind.RestartEditing);
        bool preserveApprovedContentDigest =
            (from, request.Kind) switch
            {
                (ReviewState.Approved,
                    ReviewTransitionKind.StartFinalRendering) => true,
                (ReviewState.FinalRendering,
                    ReviewTransitionKind.RetryFinalRendering) => true,
                (ReviewState.FinalRendering,
                    ReviewTransitionKind.CompleteFinalRendering) => true,
                (ReviewState.QualityControl,
                    ReviewTransitionKind.RequestTechnicalRerender) => true,
                (ReviewState.QualityControl,
                    ReviewTransitionKind.Export) => true,
                (ReviewState.Exported,
                    ReviewTransitionKind.RecordPublication) => true,
                _ => false,
            };
        bool technicalRetry =
            (from == ReviewState.FinalRendering
                && request.Kind == ReviewTransitionKind.RetryFinalRendering)
            || (from == ReviewState.QualityControl
                && request.Kind == ReviewTransitionKind.RequestTechnicalRerender);
        bool recordPublicationFactOnly =
            from == ReviewState.Exported
            && request.Kind == ReviewTransitionKind.RecordPublication;

        return new ReviewTransitionDecision(
            from,
            to,
            request.Kind,
            disposition,
            authorization.Value.Operation,
            authorization.Value.Role,
            authorization.Value.ReviewActorRole,
            request.SatisfiedGateStatus,
            requiredGateStatus,
            missingGateStatus,
            createChildCompositionVersion,
            invalidateExistingApprovalEvidence: createChildCompositionVersion,
            preserveApprovedContentDigest,
            technicalRetry,
            recordPublicationFactOnly);
    }

    private static ReviewState GetTarget(ReviewTransitionRequest request)
    {
        return request.Kind switch
        {
            ReviewTransitionKind.SubmitForEditorReview =>
                ReviewState.EditorReview,
            ReviewTransitionKind.BlockPublication =>
                ReviewState.PublicationBlocked,
            ReviewTransitionKind.RequestChanges
                or ReviewTransitionKind.RequestContentCorrection =>
                ReviewState.ChangesRequested,
            ReviewTransitionKind.CompleteEditorReview =>
                request.PerformerReviewRequirement
                    == PerformerReviewRequirement.Required
                        ? ReviewState.PerformerReview
                        : ReviewState.ContentReview,
            ReviewTransitionKind.CompletePerformerReview =>
                ReviewState.ContentReview,
            ReviewTransitionKind.CompleteContentReview =>
                request.BrandReviewRequirement == BrandReviewRequirement.Required
                    ? ReviewState.BrandReview
                    : ReviewState.Approved,
            ReviewTransitionKind.CompleteBrandReview =>
                ReviewState.Approved,
            ReviewTransitionKind.RestartEditing =>
                ReviewState.Editing,
            ReviewTransitionKind.StartFinalRendering
                or ReviewTransitionKind.RetryFinalRendering
                or ReviewTransitionKind.RequestTechnicalRerender =>
                ReviewState.FinalRendering,
            ReviewTransitionKind.CompleteFinalRendering =>
                ReviewState.QualityControl,
            ReviewTransitionKind.Export =>
                ReviewState.Exported,
            ReviewTransitionKind.RecordPublication =>
                ReviewState.Published,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Kind,
                "Unknown review transition kind."),
        };
    }

    private static (
        AuthorizationOperation Operation,
        AuthorizationRole Role,
        ReviewActorRole ReviewActorRole)? GetAuthorization(
        ReviewState from,
        ReviewTransitionKind kind)
    {
        return (from, kind) switch
        {
            (ReviewState.Editing,
                ReviewTransitionKind.SubmitForEditorReview) =>
                Editor(AuthorizationOperation.SubmitForReview),
            (ReviewState.Editing,
                ReviewTransitionKind.BlockPublication) =>
                ContentApprover(
                    AuthorizationOperation.ChangePublicationClassification),

            (ReviewState.EditorReview,
                ReviewTransitionKind.RequestChanges
                    or ReviewTransitionKind.CompleteEditorReview) =>
                Editor(AuthorizationOperation.SubmitForReview),

            (ReviewState.PerformerReview,
                ReviewTransitionKind.RequestChanges
                    or ReviewTransitionKind.CompletePerformerReview
                    or ReviewTransitionKind.BlockPublication) =>
                PerformerReviewer(
                    AuthorizationOperation.RecordPerformerReview),

            (ReviewState.ContentReview,
                ReviewTransitionKind.RequestChanges
                    or ReviewTransitionKind.CompleteContentReview
                    or ReviewTransitionKind.BlockPublication) =>
                ContentApprover(AuthorizationOperation.RecordContentReview),

            (ReviewState.BrandReview,
                ReviewTransitionKind.RequestChanges
                    or ReviewTransitionKind.CompleteBrandReview
                    or ReviewTransitionKind.BlockPublication) =>
                BrandApprover(AuthorizationOperation.RecordBrandReview),

            (ReviewState.ChangesRequested,
                ReviewTransitionKind.RestartEditing) =>
                Editor(AuthorizationOperation.CreateCompositionVersion),

            (ReviewState.Approved,
                ReviewTransitionKind.StartFinalRendering) =>
                Editor(AuthorizationOperation.StartApprovedRender),
            (ReviewState.Approved,
                ReviewTransitionKind.BlockPublication) =>
                ContentApprover(
                    AuthorizationOperation.ChangePublicationClassification),

            (ReviewState.FinalRendering,
                ReviewTransitionKind.RetryFinalRendering
                    or ReviewTransitionKind.CompleteFinalRendering) =>
                Editor(AuthorizationOperation.StartApprovedRender),

            (ReviewState.QualityControl,
                ReviewTransitionKind.RequestTechnicalRerender
                    or ReviewTransitionKind.RequestContentCorrection) =>
                Editor(AuthorizationOperation.RecordQualityControl),
            (ReviewState.QualityControl,
                ReviewTransitionKind.Export) =>
                Editor(AuthorizationOperation.ExportComposition),

            (ReviewState.Exported,
                ReviewTransitionKind.RecordPublication) =>
                ContentApprover(AuthorizationOperation.RecordPublication),

            _ => null,
        };
    }

    private static ReviewGateStatus GetRequiredGateStatus(
        ReviewState from,
        ReviewTransitionRequest request)
    {
        return (from, request.Kind) switch
        {
            (ReviewState.ContentReview,
                ReviewTransitionKind.CompleteContentReview)
                when request.BrandReviewRequirement
                    == BrandReviewRequirement.NotRequired =>
                ApprovalGateStatus,
            (ReviewState.BrandReview,
                ReviewTransitionKind.CompleteBrandReview) =>
                ApprovalGateStatus,
            (ReviewState.Approved,
                ReviewTransitionKind.StartFinalRendering) =>
                FinalRenderGateStatus,
            (ReviewState.FinalRendering,
                ReviewTransitionKind.RetryFinalRendering) =>
                FinalRenderGateStatus,
            (ReviewState.QualityControl,
                ReviewTransitionKind.RequestTechnicalRerender) =>
                FinalRenderGateStatus,
            (ReviewState.QualityControl,
                ReviewTransitionKind.Export)
                when request.ExportDestination == ReviewExportDestination.External =>
                InternalExportGateStatus | ReviewGateStatus.RightsPolicySatisfied,
            (ReviewState.QualityControl,
                ReviewTransitionKind.Export) =>
                InternalExportGateStatus,
            _ => ReviewGateStatus.None,
        };
    }

    internal static bool MatchesCanonicalTransitionContract(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        AuthorizationOperation? authorizationOperation,
        AuthorizationRole? authorizationRole,
        ReviewActorRole? reviewActorRole,
        ReviewGateStatus satisfiedGateStatus,
        ReviewGateStatus requiredGateStatus,
        bool createChildCompositionVersion,
        bool invalidateExistingApprovalEvidence,
        bool preserveApprovedContentDigest,
        bool technicalRetry,
        bool recordPublicationFactOnly)
    {
        (AuthorizationOperation Operation,
            AuthorizationRole Role,
            ReviewActorRole ReviewActorRole)? expectedAuthorization =
            GetAuthorization(from, kind);
        if (expectedAuthorization is null
            || authorizationOperation != expectedAuthorization.Value.Operation
            || authorizationRole != expectedAuthorization.Value.Role
            || reviewActorRole != expectedAuthorization.Value.ReviewActorRole
            || !RequiredGateStatusMatchesCanonicalContract(
                from,
                to,
                kind,
                requiredGateStatus)
            || (satisfiedGateStatus & ~requiredGateStatus)
                != ReviewGateStatus.None)
        {
            return false;
        }

        bool expectedCreateChildCompositionVersion =
            (from == ReviewState.ChangesRequested
                && kind == ReviewTransitionKind.RestartEditing);
        bool expectedPreserveApprovedContentDigest =
            (from, kind) switch
            {
                (ReviewState.Approved,
                    ReviewTransitionKind.StartFinalRendering) => true,
                (ReviewState.FinalRendering,
                    ReviewTransitionKind.RetryFinalRendering) => true,
                (ReviewState.FinalRendering,
                    ReviewTransitionKind.CompleteFinalRendering) => true,
                (ReviewState.QualityControl,
                    ReviewTransitionKind.RequestTechnicalRerender) => true,
                (ReviewState.QualityControl,
                    ReviewTransitionKind.Export) => true,
                (ReviewState.Exported,
                    ReviewTransitionKind.RecordPublication) => true,
                _ => false,
            };
        bool expectedTechnicalRetry =
            (from == ReviewState.FinalRendering
                && kind == ReviewTransitionKind.RetryFinalRendering)
            || (from == ReviewState.QualityControl
                && kind == ReviewTransitionKind.RequestTechnicalRerender);
        bool expectedRecordPublicationFactOnly =
            from == ReviewState.Exported
            && kind == ReviewTransitionKind.RecordPublication;

        return createChildCompositionVersion
                == expectedCreateChildCompositionVersion
            && invalidateExistingApprovalEvidence
                == expectedCreateChildCompositionVersion
            && preserveApprovedContentDigest
                == expectedPreserveApprovedContentDigest
            && technicalRetry == expectedTechnicalRetry
            && recordPublicationFactOnly
                == expectedRecordPublicationFactOnly;
    }

    internal static bool MatchesCanonicalDeniedTransitionContract(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        ReviewGateStatus satisfiedGateStatus)
    {
        if (GetAuthorization(from, kind) is not null)
        {
            return false;
        }

        BrandReviewRequirement? brandReviewRequirement = kind switch
        {
            ReviewTransitionKind.CompleteContentReview
                when to == ReviewState.BrandReview =>
                global::HubRadio.Core.Reviews.BrandReviewRequirement.Required,
            ReviewTransitionKind.CompleteContentReview
                when to == ReviewState.Approved =>
                global::HubRadio.Core.Reviews.BrandReviewRequirement.NotRequired,
            _ => null,
        };
        ReviewExportDestination? exportDestination =
            kind == ReviewTransitionKind.Export
                ? ReviewExportDestination.External
                : null;

        return ReviewTransitionValidation.IsPermittedGateStatus(
            kind,
            brandReviewRequirement,
            exportDestination,
            satisfiedGateStatus);
    }

    private static bool RequiredGateStatusMatchesCanonicalContract(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        ReviewGateStatus requiredGateStatus) =>
        (from, to, kind) switch
        {
            (ReviewState.ContentReview,
                ReviewState.Approved,
                ReviewTransitionKind.CompleteContentReview) =>
                requiredGateStatus == ApprovalGateStatus,
            (ReviewState.BrandReview,
                ReviewState.Approved,
                ReviewTransitionKind.CompleteBrandReview) =>
                requiredGateStatus == ApprovalGateStatus,
            (ReviewState.Approved,
                ReviewState.FinalRendering,
                ReviewTransitionKind.StartFinalRendering) =>
                requiredGateStatus == FinalRenderGateStatus,
            (ReviewState.FinalRendering,
                ReviewState.FinalRendering,
                ReviewTransitionKind.RetryFinalRendering) =>
                requiredGateStatus == FinalRenderGateStatus,
            (ReviewState.QualityControl,
                ReviewState.FinalRendering,
                ReviewTransitionKind.RequestTechnicalRerender) =>
                requiredGateStatus == FinalRenderGateStatus,
            (ReviewState.QualityControl,
                ReviewState.Exported,
                ReviewTransitionKind.Export) =>
                requiredGateStatus == InternalExportGateStatus
                || requiredGateStatus
                    == ReviewTransitionValidation.ExternalExportGateStatus,
            _ => requiredGateStatus == ReviewGateStatus.None,
        };

    private static (
        AuthorizationOperation Operation,
        AuthorizationRole Role,
        ReviewActorRole ReviewActorRole) Editor(
        AuthorizationOperation operation) =>
        (operation, AuthorizationRole.Editor, ReviewActorRole.Editor);

    private static (
        AuthorizationOperation Operation,
        AuthorizationRole Role,
        ReviewActorRole ReviewActorRole) PerformerReviewer(
        AuthorizationOperation operation) =>
        (operation,
            AuthorizationRole.PerformerReviewer,
            ReviewActorRole.PerformerReviewer);

    private static (
        AuthorizationOperation Operation,
        AuthorizationRole Role,
        ReviewActorRole ReviewActorRole) ContentApprover(
        AuthorizationOperation operation) =>
        (operation,
            AuthorizationRole.ContentApprover,
            ReviewActorRole.ContentApprover);

    private static (
        AuthorizationOperation Operation,
        AuthorizationRole Role,
        ReviewActorRole ReviewActorRole) BrandApprover(
        AuthorizationOperation operation) =>
        (operation,
            AuthorizationRole.BrandApprover,
            ReviewActorRole.BrandApprover);
}

internal static class ReviewTransitionValidation
{
    internal const ReviewGateStatus ApprovalGateStatus =
        ReviewGateStatus.RequiredApprovalEvidenceRecorded
        | ReviewGateStatus.RightsPolicySatisfied;

    internal const ReviewGateStatus FinalRenderGateStatus =
        ApprovalGateStatus
        | ReviewGateStatus.ApprovedContentDigestMatched
        | ReviewGateStatus.FinalRenderPolicySatisfied;

    internal const ReviewGateStatus InternalExportGateStatus =
        ReviewGateStatus.RequiredApprovalEvidenceRecorded
        | ReviewGateStatus.ApprovedContentDigestMatched
        | ReviewGateStatus.ExportPolicySatisfied;

    internal const ReviewGateStatus ExternalExportGateStatus =
        InternalExportGateStatus | ReviewGateStatus.RightsPolicySatisfied;

    private const ReviewGateStatus AllGateStatuses =
        ReviewGateStatus.RequiredApprovalEvidenceRecorded
        | ReviewGateStatus.RightsPolicySatisfied
        | ReviewGateStatus.ApprovedContentDigestMatched
        | ReviewGateStatus.FinalRenderPolicySatisfied
        | ReviewGateStatus.ExportPolicySatisfied;

    internal static bool IsValidGateStatus(ReviewGateStatus gateStatus) =>
        (gateStatus & ~AllGateStatuses) == ReviewGateStatus.None;

    internal static bool IsPermittedGateStatus(
        ReviewTransitionKind kind,
        BrandReviewRequirement? brandReviewRequirement,
        ReviewExportDestination? exportDestination,
        ReviewGateStatus gateStatus) =>
        (gateStatus
            & ~GetPermittedGateStatus(
                kind,
                brandReviewRequirement,
                exportDestination)) == ReviewGateStatus.None;

    internal static void ValidatePermittedGateStatus(
        ReviewTransitionKind kind,
        BrandReviewRequirement? brandReviewRequirement,
        ReviewExportDestination? exportDestination,
        ReviewGateStatus gateStatus,
        string parameterName)
    {
        ValidateGateStatus(gateStatus, parameterName);

        if (!IsPermittedGateStatus(
                kind,
                brandReviewRequirement,
                exportDestination,
                gateStatus))
        {
            throw new ArgumentException(
                "The review action contains gate status that it cannot consume.",
                parameterName);
        }
    }

    internal static void ValidateGateStatus(
        ReviewGateStatus gateStatus,
        string parameterName)
    {
        if (!IsValidGateStatus(gateStatus))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                gateStatus,
                "Unknown review gate status flags.");
        }
    }

    private static ReviewGateStatus GetPermittedGateStatus(
        ReviewTransitionKind kind,
        BrandReviewRequirement? brandReviewRequirement,
        ReviewExportDestination? exportDestination) =>
        kind switch
        {
            ReviewTransitionKind.CompleteContentReview
                when brandReviewRequirement
                    == global::HubRadio.Core.Reviews.BrandReviewRequirement.NotRequired =>
                ApprovalGateStatus,
            ReviewTransitionKind.CompleteBrandReview =>
                ApprovalGateStatus,
            ReviewTransitionKind.StartFinalRendering
                or ReviewTransitionKind.RetryFinalRendering
                or ReviewTransitionKind.RequestTechnicalRerender =>
                FinalRenderGateStatus,
            ReviewTransitionKind.Export
                when exportDestination == ReviewExportDestination.Internal =>
                InternalExportGateStatus,
            ReviewTransitionKind.Export
                when exportDestination == ReviewExportDestination.External =>
                ExternalExportGateStatus,
            _ => ReviewGateStatus.None,
        };
}
