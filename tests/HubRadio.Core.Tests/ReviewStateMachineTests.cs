using System.Reflection;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ReviewStateMachineTests
{
    private const ReviewGateStatus ApprovalGates =
        ReviewGateStatus.RequiredApprovalEvidenceRecorded
        | ReviewGateStatus.RightsPolicySatisfied;

    private const ReviewGateStatus FinalRenderGates =
        ApprovalGates
        | ReviewGateStatus.ApprovedContentDigestMatched
        | ReviewGateStatus.FinalRenderPolicySatisfied;

    private const ReviewGateStatus InternalExportGates =
        ReviewGateStatus.RequiredApprovalEvidenceRecorded
        | ReviewGateStatus.ApprovedContentDigestMatched
        | ReviewGateStatus.ExportPolicySatisfied;

    private static readonly ActionCase[] CanonicalActions =
    [
        new(
            ReviewTransitionKind.SubmitForEditorReview,
            ReviewTransitionRequest.SubmitForEditorReview,
            ReviewState.EditorReview),
        new(
            ReviewTransitionKind.BlockPublication,
            ReviewTransitionRequest.BlockPublication,
            ReviewState.PublicationBlocked),
        new(
            ReviewTransitionKind.RequestChanges,
            ReviewTransitionRequest.RequestChanges,
            ReviewState.ChangesRequested),
        new(
            ReviewTransitionKind.CompleteEditorReview,
            () => ReviewTransitionRequest.CompleteEditorReview(
                PerformerReviewRequirement.Required),
            ReviewState.PerformerReview),
        new(
            ReviewTransitionKind.CompletePerformerReview,
            ReviewTransitionRequest.CompletePerformerReview,
            ReviewState.ContentReview),
        new(
            ReviewTransitionKind.CompleteContentReview,
            () => ReviewTransitionRequest.CompleteContentReview(
                BrandReviewRequirement.Required,
                ReviewGateStatus.None),
            ReviewState.BrandReview),
        new(
            ReviewTransitionKind.CompleteBrandReview,
            () => ReviewTransitionRequest.CompleteBrandReview(ApprovalGates),
            ReviewState.Approved),
        new(
            ReviewTransitionKind.RestartEditing,
            ReviewTransitionRequest.RestartEditing,
            ReviewState.Editing),
        new(
            ReviewTransitionKind.StartFinalRendering,
            () => ReviewTransitionRequest.StartFinalRendering(FinalRenderGates),
            ReviewState.FinalRendering),
        new(
            ReviewTransitionKind.RetryFinalRendering,
            () => ReviewTransitionRequest.RetryFinalRendering(FinalRenderGates),
            ReviewState.FinalRendering),
        new(
            ReviewTransitionKind.CompleteFinalRendering,
            ReviewTransitionRequest.CompleteFinalRendering,
            ReviewState.QualityControl),
        new(
            ReviewTransitionKind.RequestTechnicalRerender,
            () => ReviewTransitionRequest.RequestTechnicalRerender(FinalRenderGates),
            ReviewState.FinalRendering),
        new(
            ReviewTransitionKind.RequestContentCorrection,
            ReviewTransitionRequest.RequestContentCorrection,
            ReviewState.ChangesRequested),
        new(
            ReviewTransitionKind.Export,
            () => ReviewTransitionRequest.Export(
                ReviewExportDestination.Internal,
                InternalExportGates),
            ReviewState.Exported),
        new(
            ReviewTransitionKind.RecordPublication,
            ReviewTransitionRequest.RecordPublication,
            ReviewState.Published),
    ];

    private static readonly IReadOnlyDictionary<(
        ReviewState From,
        ReviewTransitionKind Kind), ExpectedRule> ExpectedRules =
        new Dictionary<(ReviewState, ReviewTransitionKind), ExpectedRule>
        {
            [(ReviewState.Editing, ReviewTransitionKind.SubmitForEditorReview)] =
                Rule(
                    ReviewState.EditorReview,
                    AuthorizationOperation.SubmitForReview,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor),
            [(ReviewState.Editing, ReviewTransitionKind.BlockPublication)] =
                Rule(
                    ReviewState.PublicationBlocked,
                    AuthorizationOperation.ChangePublicationClassification,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover),

            [(ReviewState.EditorReview, ReviewTransitionKind.RequestChanges)] =
                Rule(
                    ReviewState.ChangesRequested,
                    AuthorizationOperation.SubmitForReview,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor),
            [(ReviewState.EditorReview, ReviewTransitionKind.CompleteEditorReview)] =
                Rule(
                    ReviewState.PerformerReview,
                    AuthorizationOperation.SubmitForReview,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor),

            [(ReviewState.PerformerReview, ReviewTransitionKind.RequestChanges)] =
                Rule(
                    ReviewState.ChangesRequested,
                    AuthorizationOperation.RecordPerformerReview,
                    AuthorizationRole.PerformerReviewer,
                    ReviewActorRole.PerformerReviewer),
            [(ReviewState.PerformerReview, ReviewTransitionKind.CompletePerformerReview)] =
                Rule(
                    ReviewState.ContentReview,
                    AuthorizationOperation.RecordPerformerReview,
                    AuthorizationRole.PerformerReviewer,
                    ReviewActorRole.PerformerReviewer),
            [(ReviewState.PerformerReview, ReviewTransitionKind.BlockPublication)] =
                Rule(
                    ReviewState.PublicationBlocked,
                    AuthorizationOperation.RecordPerformerReview,
                    AuthorizationRole.PerformerReviewer,
                    ReviewActorRole.PerformerReviewer),

            [(ReviewState.ContentReview, ReviewTransitionKind.RequestChanges)] =
                Rule(
                    ReviewState.ChangesRequested,
                    AuthorizationOperation.RecordContentReview,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover),
            [(ReviewState.ContentReview, ReviewTransitionKind.CompleteContentReview)] =
                Rule(
                    ReviewState.BrandReview,
                    AuthorizationOperation.RecordContentReview,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover),
            [(ReviewState.ContentReview, ReviewTransitionKind.BlockPublication)] =
                Rule(
                    ReviewState.PublicationBlocked,
                    AuthorizationOperation.RecordContentReview,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover),

            [(ReviewState.BrandReview, ReviewTransitionKind.RequestChanges)] =
                Rule(
                    ReviewState.ChangesRequested,
                    AuthorizationOperation.RecordBrandReview,
                    AuthorizationRole.BrandApprover,
                    ReviewActorRole.BrandApprover),
            [(ReviewState.BrandReview, ReviewTransitionKind.CompleteBrandReview)] =
                Rule(
                    ReviewState.Approved,
                    AuthorizationOperation.RecordBrandReview,
                    AuthorizationRole.BrandApprover,
                    ReviewActorRole.BrandApprover,
                    ApprovalGates),
            [(ReviewState.BrandReview, ReviewTransitionKind.BlockPublication)] =
                Rule(
                    ReviewState.PublicationBlocked,
                    AuthorizationOperation.RecordBrandReview,
                    AuthorizationRole.BrandApprover,
                    ReviewActorRole.BrandApprover),

            [(ReviewState.ChangesRequested, ReviewTransitionKind.RestartEditing)] =
                Rule(
                    ReviewState.Editing,
                    AuthorizationOperation.CreateCompositionVersion,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    createChildCompositionVersion: true,
                    invalidateExistingApprovalEvidence: true),

            [(ReviewState.Approved, ReviewTransitionKind.StartFinalRendering)] =
                Rule(
                    ReviewState.FinalRendering,
                    AuthorizationOperation.StartApprovedRender,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    FinalRenderGates,
                    preserveApprovedContentDigest: true),
            [(ReviewState.Approved, ReviewTransitionKind.BlockPublication)] =
                Rule(
                    ReviewState.PublicationBlocked,
                    AuthorizationOperation.ChangePublicationClassification,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover),

            [(ReviewState.FinalRendering, ReviewTransitionKind.RetryFinalRendering)] =
                Rule(
                    ReviewState.FinalRendering,
                    AuthorizationOperation.StartApprovedRender,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    FinalRenderGates,
                    preserveApprovedContentDigest: true,
                    technicalRetry: true),
            [(ReviewState.FinalRendering, ReviewTransitionKind.CompleteFinalRendering)] =
                Rule(
                    ReviewState.QualityControl,
                    AuthorizationOperation.StartApprovedRender,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    preserveApprovedContentDigest: true),

            [(ReviewState.QualityControl,
                ReviewTransitionKind.RequestTechnicalRerender)] =
                Rule(
                    ReviewState.FinalRendering,
                    AuthorizationOperation.RecordQualityControl,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    FinalRenderGates,
                    preserveApprovedContentDigest: true,
                    technicalRetry: true),
            [(ReviewState.QualityControl,
                ReviewTransitionKind.RequestContentCorrection)] =
                Rule(
                    ReviewState.ChangesRequested,
                    AuthorizationOperation.RecordQualityControl,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor),
            [(ReviewState.QualityControl, ReviewTransitionKind.Export)] =
                Rule(
                    ReviewState.Exported,
                    AuthorizationOperation.ExportComposition,
                    AuthorizationRole.Editor,
                    ReviewActorRole.Editor,
                    InternalExportGates,
                    preserveApprovedContentDigest: true),

            [(ReviewState.Exported, ReviewTransitionKind.RecordPublication)] =
                Rule(
                    ReviewState.Published,
                    AuthorizationOperation.RecordPublication,
                    AuthorizationRole.ContentApprover,
                    ReviewActorRole.ContentApprover,
                    preserveApprovedContentDigest: true,
                    recordPublicationFactOnly: true),
        };

    [TestMethod]
    public void ClosedEnumsDefineExactlyTheRequiredContractValues()
    {
        AssertExactEnum<ReviewTransitionKind>(
            "SubmitForEditorReview",
            "BlockPublication",
            "RequestChanges",
            "CompleteEditorReview",
            "CompletePerformerReview",
            "CompleteContentReview",
            "CompleteBrandReview",
            "RestartEditing",
            "StartFinalRendering",
            "RetryFinalRendering",
            "CompleteFinalRendering",
            "RequestTechnicalRerender",
            "RequestContentCorrection",
            "Export",
            "RecordPublication");
        AssertExactEnum<PerformerReviewRequirement>("NotRequired", "Required");
        AssertExactEnum<BrandReviewRequirement>("NotRequired", "Required");
        AssertExactEnum<ReviewExportDestination>("Internal", "External");
        AssertExactEnum<ReviewTransitionDisposition>(
            "Allowed",
            "Denied",
            "RequiresPrerequisite");

        CollectionAssert.AreEqual(
            new[]
            {
                ReviewGateStatus.None,
                ReviewGateStatus.RequiredApprovalEvidenceRecorded,
                ReviewGateStatus.RightsPolicySatisfied,
                ReviewGateStatus.ApprovedContentDigestMatched,
                ReviewGateStatus.FinalRenderPolicySatisfied,
                ReviewGateStatus.ExportPolicySatisfied,
            },
            Enum.GetValues<ReviewGateStatus>());
        CollectionAssert.DoesNotContain(Enum.GetNames<ReviewState>(), "RightsReview");
    }

    [TestMethod]
    public void EveryStateActionCellMatchesTheIndependentExpectedTable()
    {
        int evaluatedCellCount = 0;
        int allowedCellCount = 0;
        int deniedCellCount = 0;

        foreach (ReviewState from in Enum.GetValues<ReviewState>())
        {
            foreach (ActionCase actionCase in CanonicalActions)
            {
                ReviewTransitionRequest request = actionCase.Create();
                ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
                    from,
                    request);

                Assert.AreEqual(from, decision.From);
                Assert.AreEqual(actionCase.Kind, decision.Kind);
                Assert.AreEqual(request.SatisfiedGateStatus, decision.SatisfiedGateStatus);

                if (ExpectedRules.TryGetValue(
                    (from, actionCase.Kind),
                    out ExpectedRule? expected))
                {
                    Assert.AreEqual(
                        ReviewTransitionDisposition.Allowed,
                        decision.Disposition,
                        $"Expected an allowed transition for {from}/{actionCase.Kind}.");
                    AssertExpectedRule(decision, expected);
                    allowedCellCount++;
                }
                else
                {
                    Assert.AreEqual(
                        ReviewTransitionDisposition.Denied,
                        decision.Disposition,
                        $"Expected a denied transition for {from}/{actionCase.Kind}.");
                    Assert.AreEqual(actionCase.Target, decision.To);
                    AssertDeniedHasNoHandoff(decision);
                    deniedCellCount++;
                }

                evaluatedCellCount++;
            }
        }

        Assert.AreEqual(180, evaluatedCellCount);
        Assert.AreEqual(22, allowedCellCount);
        Assert.AreEqual(158, deniedCellCount);
    }

    [TestMethod]
    public void OptionalPerformerAndBrandRoutesAreExplicitAndTyped()
    {
        ReviewTransitionDecision performerRequired = ReviewStateMachine.Evaluate(
            ReviewState.EditorReview,
            ReviewTransitionRequest.CompleteEditorReview(
                PerformerReviewRequirement.Required));
        ReviewTransitionDecision performerNotRequired = ReviewStateMachine.Evaluate(
            ReviewState.EditorReview,
            ReviewTransitionRequest.CompleteEditorReview(
                PerformerReviewRequirement.NotRequired));

        Assert.AreEqual(ReviewState.PerformerReview, performerRequired.To);
        Assert.AreEqual(ReviewState.ContentReview, performerNotRequired.To);
        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            performerRequired.Disposition);
        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            performerNotRequired.Disposition);
        AssertSameTransitionAuthorization(performerRequired, performerNotRequired);

        ReviewTransitionDecision brandRequired = ReviewStateMachine.Evaluate(
            ReviewState.ContentReview,
            ReviewTransitionRequest.CompleteContentReview(
                BrandReviewRequirement.Required,
                ReviewGateStatus.None));
        ReviewTransitionDecision brandNotRequired = ReviewStateMachine.Evaluate(
            ReviewState.ContentReview,
            ReviewTransitionRequest.CompleteContentReview(
                BrandReviewRequirement.NotRequired,
                ApprovalGates));

        Assert.AreEqual(ReviewState.BrandReview, brandRequired.To);
        Assert.AreEqual(ReviewState.Approved, brandNotRequired.To);
        Assert.AreEqual(ReviewGateStatus.None, brandRequired.RequiredGateStatus);
        Assert.AreEqual(ApprovalGates, brandNotRequired.RequiredGateStatus);
        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            brandRequired.Disposition);
        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            brandNotRequired.Disposition);
        AssertSameTransitionAuthorization(brandRequired, brandNotRequired);
    }

    [TestMethod]
    public void EveryApprovalRenderRetryAndExportGateFailsClosedWhenMissing()
    {
        GateCase[] gateCases =
        [
            new(
                ReviewState.ContentReview,
                static gates => ReviewTransitionRequest.CompleteContentReview(
                    BrandReviewRequirement.NotRequired,
                    gates),
                ApprovalGates),
            new(
                ReviewState.BrandReview,
                ReviewTransitionRequest.CompleteBrandReview,
                ApprovalGates),
            new(
                ReviewState.Approved,
                ReviewTransitionRequest.StartFinalRendering,
                FinalRenderGates),
            new(
                ReviewState.FinalRendering,
                ReviewTransitionRequest.RetryFinalRendering,
                FinalRenderGates),
            new(
                ReviewState.QualityControl,
                ReviewTransitionRequest.RequestTechnicalRerender,
                FinalRenderGates),
            new(
                ReviewState.QualityControl,
                static gates => ReviewTransitionRequest.Export(
                    ReviewExportDestination.Internal,
                    gates),
                InternalExportGates),
            new(
                ReviewState.QualityControl,
                static gates => ReviewTransitionRequest.Export(
                    ReviewExportDestination.External,
                    gates),
                InternalExportGates | ReviewGateStatus.RightsPolicySatisfied),
        ];

        foreach (GateCase gateCase in gateCases)
        {
            ReviewTransitionDecision noneSupplied = ReviewStateMachine.Evaluate(
                gateCase.From,
                gateCase.Create(ReviewGateStatus.None));

            Assert.AreEqual(
                ReviewTransitionDisposition.RequiresPrerequisite,
                noneSupplied.Disposition);
            Assert.AreEqual(gateCase.Required, noneSupplied.RequiredGateStatus);
            Assert.AreEqual(gateCase.Required, noneSupplied.MissingGateStatus);
            Assert.IsNotNull(noneSupplied.RequiredAuthorizationOperation);
            Assert.IsNotNull(noneSupplied.RequiredAuthorizationRole);
            Assert.IsNotNull(noneSupplied.RequiredReviewActorRole);

            foreach (ReviewGateStatus missingGate in IndividualFlags(gateCase.Required))
            {
                ReviewGateStatus supplied = gateCase.Required & ~missingGate;
                ReviewTransitionDecision oneMissing = ReviewStateMachine.Evaluate(
                    gateCase.From,
                    gateCase.Create(supplied));

                Assert.AreEqual(
                    ReviewTransitionDisposition.RequiresPrerequisite,
                    oneMissing.Disposition,
                    $"A missing {missingGate} gate was accepted for {oneMissing.Kind}.");
                Assert.AreEqual(missingGate, oneMissing.MissingGateStatus);
            }

            ReviewTransitionDecision allSupplied = ReviewStateMachine.Evaluate(
                gateCase.From,
                gateCase.Create(gateCase.Required));
            Assert.AreEqual(
                ReviewTransitionDisposition.Allowed,
                allSupplied.Disposition);
            Assert.AreEqual(ReviewGateStatus.None, allSupplied.MissingGateStatus);
        }
    }

    [TestMethod]
    public void ExternalExportRequiresANewRightsGateWhileInternalExportDoesNot()
    {
        ReviewTransitionDecision internalExport = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.Export(
                ReviewExportDestination.Internal,
                InternalExportGates));
        ReviewTransitionDecision externalWithoutRights = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.Export(
                ReviewExportDestination.External,
                InternalExportGates));
        ReviewTransitionDecision externalWithRights = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.Export(
                ReviewExportDestination.External,
                InternalExportGates | ReviewGateStatus.RightsPolicySatisfied));

        Assert.AreEqual(ReviewTransitionDisposition.Allowed, internalExport.Disposition);
        Assert.IsNull(internalExport.RequiredRightsGateAuthorizationOperation);
        Assert.IsNull(internalExport.RequiredRightsGateAuthorizationRole);
        Assert.IsNull(internalExport.RequiredRightsGateReviewActorRole);

        Assert.AreEqual(
            ReviewTransitionDisposition.RequiresPrerequisite,
            externalWithoutRights.Disposition);
        Assert.AreEqual(
            ReviewGateStatus.RightsPolicySatisfied,
            externalWithoutRights.MissingGateStatus);
        Assert.AreEqual(
            AuthorizationOperation.RecordRightsDecision,
            externalWithoutRights.RequiredRightsGateAuthorizationOperation);
        Assert.AreEqual(
            AuthorizationRole.RightsApprover,
            externalWithoutRights.RequiredRightsGateAuthorizationRole);
        Assert.AreEqual(
            ReviewActorRole.RightsApprover,
            externalWithoutRights.RequiredRightsGateReviewActorRole);

        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            externalWithRights.Disposition);
        Assert.AreEqual(
            ReviewGateStatus.None,
            externalWithRights.MissingGateStatus);
        Assert.AreEqual(
            AuthorizationRole.Editor,
            externalWithRights.RequiredAuthorizationRole);
        Assert.AreEqual(
            AuthorizationOperation.ExportComposition,
            externalWithRights.RequiredAuthorizationOperation);
    }

    [TestMethod]
    public void TransitionAuthorizationNeverUsesAdministratorOrRightsApproverAsABypass()
    {
        foreach (((ReviewState from, ReviewTransitionKind kind), ExpectedRule expected) in
            ExpectedRules)
        {
            ActionCase actionCase = CanonicalActions.Single(
                candidate => candidate.Kind == kind);
            ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
                from,
                actionCase.Create());

            Assert.AreEqual(expected.AuthorizationRole, decision.RequiredAuthorizationRole);
            Assert.AreNotEqual(
                AuthorizationRole.Administrator,
                decision.RequiredAuthorizationRole);
            Assert.AreNotEqual(
                AuthorizationRole.RightsApprover,
                decision.RequiredAuthorizationRole);
            Assert.AreNotEqual(
                ReviewActorRole.Administrator,
                decision.RequiredReviewActorRole);
            Assert.AreNotEqual(
                ReviewActorRole.RightsApprover,
                decision.RequiredReviewActorRole);
            Assert.IsTrue(PermissionMatrix.CanPerform(
                decision.RequiredAuthorizationRole!.Value,
                decision.RequiredAuthorizationOperation!.Value));
        }

        Assert.IsFalse(PermissionMatrix.CanPerform(
            AuthorizationRole.Administrator,
            AuthorizationOperation.RecordContentReview));
        Assert.IsFalse(PermissionMatrix.CanPerform(
            AuthorizationRole.Administrator,
            AuthorizationOperation.RecordBrandReview));
        Assert.IsFalse(PermissionMatrix.CanPerform(
            AuthorizationRole.RightsApprover,
            AuthorizationOperation.RecordContentReview));
        Assert.IsFalse(PermissionMatrix.CanPerform(
            AuthorizationRole.RightsApprover,
            AuthorizationOperation.RecordBrandReview));
        Assert.IsTrue(PermissionMatrix.CanPerform(
            AuthorizationRole.RightsApprover,
            AuthorizationOperation.RecordRightsDecision));
    }

    [TestMethod]
    public void TechnicalRetryAndContentCorrectionProduceOppositeTypedHandoffs()
    {
        ReviewTransitionDecision renderRetry = ReviewStateMachine.Evaluate(
            ReviewState.FinalRendering,
            ReviewTransitionRequest.RetryFinalRendering(FinalRenderGates));
        ReviewTransitionDecision technicalRerender = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.RequestTechnicalRerender(FinalRenderGates));
        ReviewTransitionDecision contentCorrection = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.RequestContentCorrection());

        foreach (ReviewTransitionDecision technical in new[]
        {
            renderRetry,
            technicalRerender,
        })
        {
            Assert.AreEqual(
                ReviewTransitionDisposition.Allowed,
                technical.Disposition);
            Assert.AreEqual(ReviewState.FinalRendering, technical.To);
            Assert.IsTrue(technical.TechnicalRetry);
            Assert.IsTrue(technical.PreserveApprovedContentDigest);
            Assert.IsFalse(technical.CreateChildCompositionVersion);
            Assert.IsFalse(technical.InvalidateExistingApprovalEvidence);
        }

        Assert.AreEqual(
            ReviewTransitionDisposition.Allowed,
            contentCorrection.Disposition);
        Assert.AreEqual(ReviewState.ChangesRequested, contentCorrection.To);
        Assert.IsFalse(contentCorrection.TechnicalRetry);
        Assert.IsFalse(contentCorrection.PreserveApprovedContentDigest);
        Assert.IsFalse(contentCorrection.CreateChildCompositionVersion);
        Assert.IsFalse(contentCorrection.InvalidateExistingApprovalEvidence);

        Assert.AreEqual(
            ReviewTransitionDisposition.Denied,
            ReviewStateMachine.Evaluate(
                ReviewState.QualityControl,
                ReviewTransitionRequest.RetryFinalRendering(FinalRenderGates))
                .Disposition);
        Assert.AreEqual(
            ReviewTransitionDisposition.Denied,
            ReviewStateMachine.Evaluate(
                ReviewState.FinalRendering,
                ReviewTransitionRequest.RequestTechnicalRerender(FinalRenderGates))
                .Disposition);
        Assert.AreEqual(
            ReviewTransitionDisposition.Denied,
            ReviewStateMachine.Evaluate(
                ReviewState.QualityControl,
                ReviewTransitionRequest.RequestChanges())
                .Disposition);
    }

    [TestMethod]
    public void RestartEditingRequiresAChildVersionAndInvalidatesAllPriorApprovalEvidence()
    {
        ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
            ReviewState.ChangesRequested,
            ReviewTransitionRequest.RestartEditing());

        Assert.AreEqual(ReviewTransitionDisposition.Allowed, decision.Disposition);
        Assert.AreEqual(ReviewState.Editing, decision.To);
        Assert.AreEqual(
            AuthorizationOperation.CreateCompositionVersion,
            decision.RequiredAuthorizationOperation);
        Assert.AreEqual(AuthorizationRole.Editor, decision.RequiredAuthorizationRole);
        Assert.AreEqual(ReviewActorRole.Editor, decision.RequiredReviewActorRole);
        Assert.IsTrue(decision.CreateChildCompositionVersion);
        Assert.IsTrue(decision.InvalidateExistingApprovalEvidence);
        Assert.IsFalse(decision.PreserveApprovedContentDigest);
        Assert.IsFalse(decision.TechnicalRetry);
    }

    [TestMethod]
    public void QcContentCorrectionCreatesOneChildOnlyWhenEditingRestarts()
    {
        ReviewTransitionDecision requestCorrection = ReviewStateMachine.Evaluate(
            ReviewState.QualityControl,
            ReviewTransitionRequest.RequestContentCorrection());
        ReviewTransitionDecision restartEditing = ReviewStateMachine.Evaluate(
            requestCorrection.To,
            ReviewTransitionRequest.RestartEditing());

        Assert.AreEqual(ReviewState.ChangesRequested, requestCorrection.To);
        Assert.IsFalse(requestCorrection.CreateChildCompositionVersion);
        Assert.IsFalse(requestCorrection.InvalidateExistingApprovalEvidence);
        Assert.AreEqual(ReviewState.Editing, restartEditing.To);
        Assert.IsTrue(restartEditing.CreateChildCompositionVersion);
        Assert.IsTrue(restartEditing.InvalidateExistingApprovalEvidence);
        Assert.AreEqual(
            1,
            new[] { requestCorrection, restartEditing }.Count(
                static decision => decision.CreateChildCompositionVersion));
    }

    [TestMethod]
    public void PublicationIsFactRecordingOnlyAndPublishedIsTerminal()
    {
        ReviewTransitionDecision publication = ReviewStateMachine.Evaluate(
            ReviewState.Exported,
            ReviewTransitionRequest.RecordPublication());

        Assert.AreEqual(ReviewTransitionDisposition.Allowed, publication.Disposition);
        Assert.AreEqual(ReviewState.Published, publication.To);
        Assert.AreEqual(
            AuthorizationOperation.RecordPublication,
            publication.RequiredAuthorizationOperation);
        Assert.AreEqual(
            AuthorizationRole.ContentApprover,
            publication.RequiredAuthorizationRole);
        Assert.AreEqual(
            ReviewActorRole.ContentApprover,
            publication.RequiredReviewActorRole);
        Assert.IsTrue(publication.RecordPublicationFactOnly);
        Assert.IsTrue(publication.PreserveApprovedContentDigest);
        Assert.IsFalse(publication.TechnicalRetry);
        Assert.IsFalse(publication.CreateChildCompositionVersion);

        foreach (ActionCase actionCase in CanonicalActions)
        {
            ReviewTransitionDecision denied = ReviewStateMachine.Evaluate(
                ReviewState.Published,
                actionCase.Create());
            Assert.AreEqual(
                ReviewTransitionDisposition.Denied,
                denied.Disposition,
                $"Published unexpectedly accepted {actionCase.Kind}.");
        }
    }

    [TestMethod]
    public void PublicationBlockedHasNoInventedRecoveryTransition()
    {
        foreach (ActionCase actionCase in CanonicalActions)
        {
            ReviewTransitionDecision denied = ReviewStateMachine.Evaluate(
                ReviewState.PublicationBlocked,
                actionCase.Create());
            Assert.AreEqual(
                ReviewTransitionDisposition.Denied,
                denied.Disposition,
                $"PublicationBlocked unexpectedly accepted {actionCase.Kind}.");
        }
    }

    [TestMethod]
    public void UndefinedEnumsNullRequestsAndContradictoryContextFailClosed()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "from",
            () => ReviewStateMachine.Evaluate(
                (ReviewState)int.MaxValue,
                ReviewTransitionRequest.SubmitForEditorReview()));
        AssertParamName<ArgumentNullException>(
            "request",
            () => ReviewStateMachine.Evaluate(ReviewState.Editing, null!));
        AssertParamName<ArgumentOutOfRangeException>(
            "performerReviewRequirement",
            () => ReviewTransitionRequest.CompleteEditorReview(
                (PerformerReviewRequirement)int.MaxValue));
        AssertParamName<ArgumentOutOfRangeException>(
            "brandReviewRequirement",
            () => ReviewTransitionRequest.CompleteContentReview(
                (BrandReviewRequirement)int.MaxValue,
                ReviewGateStatus.None));
        AssertParamName<ArgumentOutOfRangeException>(
            "exportDestination",
            () => ReviewTransitionRequest.Export(
                (ReviewExportDestination)int.MaxValue,
                ReviewGateStatus.None));

        ReviewGateStatus undefinedGate = (ReviewGateStatus)(1 << 20);
        AssertParamName<ArgumentOutOfRangeException>(
            "satisfiedGateStatus",
            () => ReviewTransitionRequest.CompleteBrandReview(undefinedGate));
        AssertParamName<ArgumentOutOfRangeException>(
            "satisfiedGateStatus",
            () => ReviewTransitionRequest.StartFinalRendering(undefinedGate));
        AssertParamName<ArgumentOutOfRangeException>(
            "satisfiedGateStatus",
            () => ReviewTransitionRequest.Export(
                ReviewExportDestination.Internal,
                undefinedGate));
        AssertParamName<ArgumentException>(
            "satisfiedGateStatus",
            () => ReviewTransitionRequest.CompleteContentReview(
                BrandReviewRequirement.Required,
                ReviewGateStatus.RequiredApprovalEvidenceRecorded));
    }

    [TestMethod]
    public void RequestFactoriesRejectGateFlagsThatTheirActionsCannotConsume()
    {
        Action[] invalidRequests =
        [
            () => ReviewTransitionRequest.CompleteContentReview(
                BrandReviewRequirement.NotRequired,
                ApprovalGates | ReviewGateStatus.ApprovedContentDigestMatched),
            () => ReviewTransitionRequest.CompleteBrandReview(
                ApprovalGates | ReviewGateStatus.ApprovedContentDigestMatched),
            () => ReviewTransitionRequest.StartFinalRendering(
                FinalRenderGates | ReviewGateStatus.ExportPolicySatisfied),
            () => ReviewTransitionRequest.RetryFinalRendering(
                FinalRenderGates | ReviewGateStatus.ExportPolicySatisfied),
            () => ReviewTransitionRequest.RequestTechnicalRerender(
                FinalRenderGates | ReviewGateStatus.ExportPolicySatisfied),
            () => ReviewTransitionRequest.Export(
                ReviewExportDestination.Internal,
                InternalExportGates | ReviewGateStatus.RightsPolicySatisfied),
            () => ReviewTransitionRequest.Export(
                ReviewExportDestination.External,
                InternalExportGates
                    | ReviewGateStatus.RightsPolicySatisfied
                    | ReviewGateStatus.FinalRenderPolicySatisfied),
        ];

        foreach (Action invalidRequest in invalidRequests)
        {
            AssertParamName<ArgumentException>(
                "satisfiedGateStatus",
                invalidRequest);
        }
    }

    [TestMethod]
    public void InternalConstructorsRejectFabricatedContradictoryCombinations()
    {
        ConstructorInfo requestConstructor = typeof(ReviewTransitionRequest)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        TargetInvocationException requestException =
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                requestConstructor.Invoke(
                [
                    ReviewTransitionKind.SubmitForEditorReview,
                    PerformerReviewRequirement.Required,
                    null,
                    null,
                    ReviewGateStatus.None,
                ]));
        Assert.IsInstanceOfType<InvalidOperationException>(
            requestException.InnerException);

        AssertDecisionConstructorRejects(
            ReviewState.QualityControl,
            ReviewState.FinalRendering,
            ReviewTransitionKind.RequestTechnicalRerender,
            AuthorizationOperation.RecordQualityControl,
            AuthorizationRole.Administrator,
            ReviewActorRole.Administrator,
            FinalRenderGates,
            FinalRenderGates,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: true,
            technicalRetry: true,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.Editing,
            ReviewState.EditorReview,
            ReviewTransitionKind.SubmitForEditorReview,
            AuthorizationOperation.ExportComposition,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.Published,
            ReviewState.EditorReview,
            ReviewTransitionKind.SubmitForEditorReview,
            AuthorizationOperation.SubmitForReview,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.ChangesRequested,
            ReviewState.Editing,
            ReviewTransitionKind.RestartEditing,
            AuthorizationOperation.CreateCompositionVersion,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.QualityControl,
            ReviewState.FinalRendering,
            ReviewTransitionKind.RequestTechnicalRerender,
            AuthorizationOperation.RecordQualityControl,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            FinalRenderGates,
            FinalRenderGates,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.FinalRendering,
            ReviewState.QualityControl,
            ReviewTransitionKind.CompleteFinalRendering,
            AuthorizationOperation.StartApprovedRender,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.FinalRendering,
            ReviewState.QualityControl,
            ReviewTransitionKind.CompleteFinalRendering,
            AuthorizationOperation.StartApprovedRender,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            FinalRenderGates,
            FinalRenderGates,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: true,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.Approved,
            ReviewState.FinalRendering,
            ReviewTransitionKind.StartFinalRendering,
            AuthorizationOperation.StartApprovedRender,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ApprovalGates,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: true,
            technicalRetry: false,
            recordPublicationFactOnly: false,
            ReviewTransitionDisposition.RequiresPrerequisite);

        AssertDecisionConstructorRejects(
            ReviewState.Exported,
            ReviewState.Published,
            ReviewTransitionKind.RecordPublication,
            AuthorizationOperation.RecordPublication,
            AuthorizationRole.ContentApprover,
            ReviewActorRole.ContentApprover,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: false,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.Approved,
            ReviewState.FinalRendering,
            ReviewTransitionKind.StartFinalRendering,
            AuthorizationOperation.StartApprovedRender,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: true,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDecisionConstructorRejects(
            ReviewState.QualityControl,
            ReviewState.Exported,
            ReviewTransitionKind.Export,
            AuthorizationOperation.ExportComposition,
            AuthorizationRole.Editor,
            ReviewActorRole.Editor,
            ReviewGateStatus.None,
            ReviewGateStatus.None,
            createChildCompositionVersion: false,
            invalidateExistingApprovalEvidence: false,
            preserveApprovedContentDigest: true,
            technicalRetry: false,
            recordPublicationFactOnly: false);

        AssertDeniedDecisionConstructorRejects(
            ReviewState.Editing,
            ReviewState.EditorReview,
            ReviewTransitionKind.SubmitForEditorReview,
            ReviewGateStatus.None);
        AssertDeniedDecisionConstructorRejects(
            ReviewState.Published,
            ReviewState.EditorReview,
            ReviewTransitionKind.SubmitForEditorReview,
            ReviewGateStatus.ExportPolicySatisfied);
    }

    [TestMethod]
    public void PublicSurfaceIsClosedImmutableMinimalAndReasonFree()
    {
        Assert.IsTrue(typeof(ReviewStateMachine).IsPublic);
        Assert.IsTrue(typeof(ReviewStateMachine).IsAbstract);
        Assert.IsTrue(typeof(ReviewStateMachine).IsSealed);
        MethodInfo[] machineMethods = typeof(ReviewStateMachine).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, machineMethods.Length);
        Assert.AreEqual(nameof(ReviewStateMachine.Evaluate), machineMethods[0].Name);
        Assert.AreEqual(typeof(ReviewTransitionDecision), machineMethods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(ReviewState), typeof(ReviewTransitionRequest) },
            machineMethods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "from", "request" },
            machineMethods[0].GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());

        AssertClosedImmutableType(
            typeof(ReviewTransitionRequest),
            "Kind",
            "PerformerReviewRequirement",
            "BrandReviewRequirement",
            "ExportDestination",
            "SatisfiedGateStatus");
        AssertClosedImmutableType(
            typeof(ReviewTransitionDecision),
            "From",
            "To",
            "Kind",
            "Disposition",
            "RequiredAuthorizationOperation",
            "RequiredAuthorizationRole",
            "RequiredReviewActorRole",
            "SatisfiedGateStatus",
            "RequiredGateStatus",
            "MissingGateStatus",
            "CreateChildCompositionVersion",
            "InvalidateExistingApprovalEvidence",
            "PreserveApprovedContentDigest",
            "TechnicalRetry",
            "RecordPublicationFactOnly",
            "RequiredRightsGateAuthorizationOperation",
            "RequiredRightsGateAuthorizationRole",
            "RequiredRightsGateReviewActorRole");

        string[] expectedFactories =
        [
            "BlockPublication",
            "CompleteBrandReview",
            "CompleteContentReview",
            "CompleteEditorReview",
            "CompleteFinalRendering",
            "CompletePerformerReview",
            "Export",
            "RecordPublication",
            "RequestChanges",
            "RequestContentCorrection",
            "RequestTechnicalRerender",
            "RestartEditing",
            "RetryFinalRendering",
            "StartFinalRendering",
            "SubmitForEditorReview",
        ];
        MethodInfo[] requestFactories = typeof(ReviewTransitionRequest)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedFactories,
            requestFactories.Select(static method => method.Name).ToArray());
        Assert.IsTrue(requestFactories.All(
            static method => method.ReturnType == typeof(ReviewTransitionRequest)));
        Assert.IsFalse(requestFactories.Any(
            static method => method.GetParameters().Any(
                parameter => parameter.ParameterType == typeof(bool)
                    || parameter.ParameterType == typeof(string)
                    || parameter.ParameterType == typeof(ReviewState))));
        Assert.IsFalse(typeof(ReviewTransitionRequest).GetProperties().Any(
            static property => property.PropertyType == typeof(string)));
        Assert.IsFalse(typeof(ReviewTransitionDecision).GetProperties().Any(
            static property => property.PropertyType == typeof(string)));
        Assert.IsNotNull(typeof(ReviewRecord).GetProperty(nameof(ReviewRecord.Reason)));
    }

    private static ExpectedRule Rule(
        ReviewState to,
        AuthorizationOperation operation,
        AuthorizationRole authorizationRole,
        ReviewActorRole reviewActorRole,
        ReviewGateStatus requiredGateStatus = ReviewGateStatus.None,
        bool createChildCompositionVersion = false,
        bool invalidateExistingApprovalEvidence = false,
        bool preserveApprovedContentDigest = false,
        bool technicalRetry = false,
        bool recordPublicationFactOnly = false) =>
        new(
            to,
            operation,
            authorizationRole,
            reviewActorRole,
            requiredGateStatus,
            createChildCompositionVersion,
            invalidateExistingApprovalEvidence,
            preserveApprovedContentDigest,
            technicalRetry,
            recordPublicationFactOnly);

    private static void AssertExpectedRule(
        ReviewTransitionDecision decision,
        ExpectedRule expected)
    {
        Assert.AreEqual(expected.To, decision.To);
        Assert.AreEqual(expected.Operation, decision.RequiredAuthorizationOperation);
        Assert.AreEqual(expected.AuthorizationRole, decision.RequiredAuthorizationRole);
        Assert.AreEqual(expected.ReviewActorRole, decision.RequiredReviewActorRole);
        Assert.AreEqual(expected.RequiredGateStatus, decision.RequiredGateStatus);
        Assert.AreEqual(ReviewGateStatus.None, decision.MissingGateStatus);
        Assert.AreEqual(
            expected.CreateChildCompositionVersion,
            decision.CreateChildCompositionVersion);
        Assert.AreEqual(
            expected.InvalidateExistingApprovalEvidence,
            decision.InvalidateExistingApprovalEvidence);
        Assert.AreEqual(
            expected.PreserveApprovedContentDigest,
            decision.PreserveApprovedContentDigest);
        Assert.AreEqual(expected.TechnicalRetry, decision.TechnicalRetry);
        Assert.AreEqual(
            expected.RecordPublicationFactOnly,
            decision.RecordPublicationFactOnly);
    }

    private static void AssertDeniedHasNoHandoff(
        ReviewTransitionDecision decision)
    {
        Assert.IsNull(decision.RequiredAuthorizationOperation);
        Assert.IsNull(decision.RequiredAuthorizationRole);
        Assert.IsNull(decision.RequiredReviewActorRole);
        Assert.AreEqual(ReviewGateStatus.None, decision.RequiredGateStatus);
        Assert.AreEqual(ReviewGateStatus.None, decision.MissingGateStatus);
        Assert.IsFalse(decision.CreateChildCompositionVersion);
        Assert.IsFalse(decision.InvalidateExistingApprovalEvidence);
        Assert.IsFalse(decision.PreserveApprovedContentDigest);
        Assert.IsFalse(decision.TechnicalRetry);
        Assert.IsFalse(decision.RecordPublicationFactOnly);
        Assert.IsNull(decision.RequiredRightsGateAuthorizationOperation);
        Assert.IsNull(decision.RequiredRightsGateAuthorizationRole);
        Assert.IsNull(decision.RequiredRightsGateReviewActorRole);
    }

    private static void AssertSameTransitionAuthorization(
        ReviewTransitionDecision first,
        ReviewTransitionDecision second)
    {
        Assert.AreEqual(
            first.RequiredAuthorizationOperation,
            second.RequiredAuthorizationOperation);
        Assert.AreEqual(
            first.RequiredAuthorizationRole,
            second.RequiredAuthorizationRole);
        Assert.AreEqual(
            first.RequiredReviewActorRole,
            second.RequiredReviewActorRole);
    }

    private static IEnumerable<ReviewGateStatus> IndividualFlags(
        ReviewGateStatus status)
    {
        foreach (ReviewGateStatus candidate in Enum.GetValues<ReviewGateStatus>())
        {
            if (candidate != ReviewGateStatus.None
                && (status & candidate) == candidate)
            {
                yield return candidate;
            }
        }
    }

    private static void AssertExactEnum<TEnum>(params string[] expectedNames)
        where TEnum : struct, Enum
    {
        CollectionAssert.AreEqual(expectedNames, Enum.GetNames<TEnum>());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, expectedNames.Length).ToArray(),
            Enum.GetValues<TEnum>().Select(static value => Convert.ToInt32(value)).ToArray());
    }

    private static void AssertClosedImmutableType(
        Type type,
        params string[] expectedPropertyNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);
        Assert.IsFalse(type.IsAbstract);
        Assert.AreEqual(0, type.GetConstructors().Length);

        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedPropertyNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(
            static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.AreEqual(
            0,
            type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Count(static method => !method.IsSpecialName));
    }

    private static void AssertParamName<TException>(
        string expected,
        Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private static void AssertDecisionConstructorRejects(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        AuthorizationOperation operation,
        AuthorizationRole authorizationRole,
        ReviewActorRole reviewActorRole,
        ReviewGateStatus satisfiedGateStatus,
        ReviewGateStatus requiredGateStatus,
        bool createChildCompositionVersion,
        bool invalidateExistingApprovalEvidence,
        bool preserveApprovedContentDigest,
        bool technicalRetry,
        bool recordPublicationFactOnly,
        ReviewTransitionDisposition disposition =
            ReviewTransitionDisposition.Allowed)
    {
        ConstructorInfo constructor = typeof(ReviewTransitionDecision)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        TargetInvocationException exception =
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                constructor.Invoke(
                [
                    from,
                    to,
                    kind,
                    disposition,
                    operation,
                    authorizationRole,
                    reviewActorRole,
                    satisfiedGateStatus,
                    requiredGateStatus,
                    requiredGateStatus & ~satisfiedGateStatus,
                    createChildCompositionVersion,
                    invalidateExistingApprovalEvidence,
                    preserveApprovedContentDigest,
                    technicalRetry,
                    recordPublicationFactOnly,
                ]));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    private static void AssertDeniedDecisionConstructorRejects(
        ReviewState from,
        ReviewState to,
        ReviewTransitionKind kind,
        ReviewGateStatus satisfiedGateStatus)
    {
        ConstructorInfo constructor = typeof(ReviewTransitionDecision)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        TargetInvocationException exception =
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                constructor.Invoke(
                [
                    from,
                    to,
                    kind,
                    ReviewTransitionDisposition.Denied,
                    null,
                    null,
                    null,
                    satisfiedGateStatus,
                    ReviewGateStatus.None,
                    ReviewGateStatus.None,
                    false,
                    false,
                    false,
                    false,
                    false,
                ]));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    private sealed record ActionCase(
        ReviewTransitionKind Kind,
        Func<ReviewTransitionRequest> Create,
        ReviewState Target);

    private sealed record GateCase(
        ReviewState From,
        Func<ReviewGateStatus, ReviewTransitionRequest> Create,
        ReviewGateStatus Required);

    private sealed record ExpectedRule(
        ReviewState To,
        AuthorizationOperation Operation,
        AuthorizationRole AuthorizationRole,
        ReviewActorRole ReviewActorRole,
        ReviewGateStatus RequiredGateStatus,
        bool CreateChildCompositionVersion,
        bool InvalidateExistingApprovalEvidence,
        bool PreserveApprovedContentDigest,
        bool TechnicalRetry,
        bool RecordPublicationFactOnly);
}
