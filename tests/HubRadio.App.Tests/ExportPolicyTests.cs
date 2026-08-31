using System.Reflection;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Policy;
using HubRadio.App.Rendering;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Policy;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ExportPolicyTests
{
    [TestMethod]
    public void ValidFinalRenderAuthorizationIsConsumedExactlyOnce()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(request);

        ExportPolicyAuthorization authorization = decision.Authorization
            ?? throw new AssertFailedException("The final-render request was not authorized.");
        Assert.IsTrue(decision.IsAuthorized);
        Assert.AreEqual(ExportPolicyDisposition.Authorized, decision.Disposition);
        Assert.AreEqual(ExportPolicyIssueCode.None, decision.IssueCodes);
        Assert.AreEqual(
            ReviewGateStatus.RequiredApprovalEvidenceRecorded
            | ReviewGateStatus.ApprovedContentDigestMatched
            | ReviewGateStatus.RightsPolicySatisfied
            | ReviewGateStatus.FinalRenderPolicySatisfied,
            decision.SatisfiedGateStatus);
        Assert.AreEqual(0, decision.ResolutionRequirements.Count);

        int callbackCount = 0;
        string result = fixture.Policy.Consume(
            authorization,
            request,
            consumption =>
            {
                callbackCount++;
                Assert.AreEqual(request.OperationId, consumption.OperationId);
                Assert.AreEqual(request.Boundary, consumption.Boundary);
                Assert.AreEqual(
                    request.Destination.Destination,
                    consumption.Destination);
                Assert.AreEqual(decision.EvidenceDigest, consumption.EvidenceDigest);
                Assert.AreEqual(
                    decision.SatisfiedGateStatus,
                    consumption.SatisfiedGateStatus);
                FinalRenderPolicySnapshot snapshot =
                    consumption.CreateFinalRenderPolicySnapshot();
                Assert.AreEqual(decision.EvidenceDigest, snapshot.Digest);
                Assert.AreEqual(decision.EvidenceDigest, snapshot.CurrentDigest);
                return "render-started";
            });

        Assert.AreEqual("render-started", result);
        Assert.AreEqual(1, callbackCount);
        ExportPolicyException duplicate = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));
        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed,
            duplicate.Reason);
        Assert.AreEqual(1, callbackCount);
    }

    [TestMethod]
    public void FinalRenderReceiptAndSnapshotCannotEscapeOrBeReused()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The request was not authorized.");
        ExportPolicyConsumption? capturedConsumption = null;
        FinalRenderPolicySnapshot escapedSnapshot = fixture.Policy.Consume(
            authorization,
            request,
            consumption =>
            {
                capturedConsumption = consumption;
                return consumption.CreateFinalRenderPolicySnapshot();
            });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => capturedConsumption!.CreateFinalRenderPolicySnapshot());
        Assert.ThrowsExactly<InvalidOperationException>(
            escapedSnapshot.ClaimForRun);

        ExportPolicyRequest immediateRequest = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization immediateAuthorization = fixture.Policy
            .EvaluateAndAuthorize(immediateRequest)
            .Authorization
            ?? throw new AssertFailedException("The immediate request was not authorized.");
        FinalRenderPolicySnapshot claimedSnapshot = fixture.Policy.Consume(
            immediateAuthorization,
            immediateRequest,
            consumption =>
            {
                FinalRenderPolicySnapshot snapshot =
                    consumption.CreateFinalRenderPolicySnapshot();
                snapshot.ClaimForRun();
                return snapshot;
            });
        Assert.ThrowsExactly<InvalidOperationException>(
            claimedSnapshot.ClaimForRun);
    }

    [TestMethod]
    public void ValidInternalAndExternalExportsReceiveExactGateStatus()
    {
        foreach (ReviewExportDestination destination in Enum.GetValues<
            ReviewExportDestination>())
        {
            using ExportPolicyFixture fixture = new(ReviewState.QualityControl);
            ExportPolicyRequest request = fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                destination);

            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(request);

            ExportPolicyAuthorization authorization = decision.Authorization
                ?? throw new AssertFailedException(
                    $"The {destination} export request was not authorized.");
            ReviewGateStatus expected =
                ReviewGateStatus.RequiredApprovalEvidenceRecorded
                | ReviewGateStatus.ApprovedContentDigestMatched
                | ReviewGateStatus.ExportPolicySatisfied;
            if (destination == ReviewExportDestination.External)
            {
                expected |= ReviewGateStatus.RightsPolicySatisfied;
            }

            Assert.AreEqual(expected, decision.SatisfiedGateStatus, destination.ToString());
            ReviewGateStatus consumed = fixture.Policy.Consume(
                authorization,
                request,
                static receipt => receipt.SatisfiedGateStatus);
            Assert.AreEqual(expected, consumed, destination.ToString());
        }
    }

    [TestMethod]
    public void DuplicateOperationIdentityIsRejectedWithoutReplacingAuthorization()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The initial request was not authorized.");

        ExportPolicyException duplicate = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.EvaluateAndAuthorize(request));

        Assert.AreEqual(ExportPolicyFailureReason.DuplicateOperationId, duplicate.Reason);
        Assert.AreEqual(
            "original-authorization",
            fixture.Policy.Consume(
                authorization,
                request,
                static _ => "original-authorization"));
    }

    [TestMethod]
    public void AuthorizationIsBoundToTheExactRequestObjectAndMismatchBurnsIt()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        EntityId<ExportOperationIdTag> operationId =
            EntityId<ExportOperationIdTag>.New();
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal,
            operationId);
        ExportPolicyRequest equivalentRequest = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal,
            operationId);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The initial request was not authorized.");
        int callbackCount = 0;

        ExportPolicyException mismatch = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                equivalentRequest,
                _ => ++callbackCount));
        ExportPolicyException consumed = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));

        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationRequestMismatch,
            mismatch.Reason);
        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed,
            consumed.Reason);
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public void RoleRevocationBurnsAuthorizationBeforeCallback()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The initial request was not authorized.");
        fixture.RevokeEditorRole();
        int callbackCount = 0;

        ExportPolicyException revoked = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));
        ExportPolicyException consumed = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));

        Assert.AreEqual(ExportPolicyFailureReason.AuthorizationRevoked, revoked.Reason);
        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed,
            consumed.Reason);
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public void RepositoryRevisionDriftBurnsAuthorizationBeforeCallback()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The initial request was not authorized.");
        fixture.AdvanceStoredProjectRevision();
        int callbackCount = 0;

        ExportPolicyException stale = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));
        ExportPolicyException consumed = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));

        Assert.AreEqual(ExportPolicyFailureReason.AuthorizationStale, stale.Reason);
        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed,
            consumed.Reason);
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public void PersistedProjectPayloadMismatchBlocksAuthorization()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        fixture.ReplaceStoredProjectPayload();

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal));

        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ContextMismatch));
        Assert.IsNull(decision.Authorization);
    }

    [TestMethod]
    public void ValidationContextCompositionMetadataSubstitutionIsRejected()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        var substitutedVersion = new CompositionVersion(
            fixture.Version.Id,
            fixture.Version.ProjectId,
            fixture.Version.ParentVersionId,
            "S-1-5-21-999999999-888888888-777777777-4998",
            fixture.Version.CreatedAt,
            "Substitute metadata while retaining the canonical content digest.",
            fixture.Version.ContentDigest,
            fixture.Version.Elements);
        RenderGraphValidationContext substitutedContext =
            fixture.CreateValidationContext(
                fixture.ValidationContext.SourceSpans,
                substitutedVersion);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal,
                validationContext: substitutedContext));

        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ContextMismatch));
        Assert.IsNull(decision.Authorization);
    }

    [TestMethod]
    public void CancelledConsumptionBurnsAuthorizationBeforeCallback()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        ExportPolicyAuthorization authorization = fixture.Policy
            .EvaluateAndAuthorize(request)
            .Authorization
            ?? throw new AssertFailedException("The initial request was not authorized.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int callbackCount = 0;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount,
                cancellation.Token));
        ExportPolicyException consumed = Assert.ThrowsExactly<ExportPolicyException>(() =>
            fixture.Policy.Consume(
                authorization,
                request,
                _ => ++callbackCount));

        Assert.AreEqual(
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed,
            consumed.Reason);
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public void CancelledEvaluationCreatesNoAuthorizationOrUsedOperationIdentity()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            fixture.Policy.EvaluateAndAuthorize(request, cancellation.Token));

        ExportPolicyDecision retry = fixture.Policy.EvaluateAndAuthorize(request);
        Assert.IsTrue(retry.IsAuthorized);
    }

    [TestMethod]
    public void PublicEvidenceAndFailuresAreImmutableAndPrivacySafe()
    {
        using ExportPolicyFixture fixture = new(ReviewState.Approved);
        const string privateActorMarker =
            "S-1-5-21-999999999-888888888-777777777-4999";
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.FinalRender,
            ReviewExportDestination.Internal,
            actorSid: privateActorMarker);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(request);

        Assert.IsFalse(decision.IsAuthorized);
        Assert.IsTrue(
            decision.IssueCodes.HasFlag(ExportPolicyIssueCode.ActorNotAuthorized));
        Assert.IsFalse(
            JsonSerializer.Serialize(decision).Contains(
                privateActorMarker,
                StringComparison.Ordinal));
        Assert.IsFalse(typeof(ExportPolicyRequest).GetProperties().Any(static property =>
            property.Name.Contains("Actor", StringComparison.Ordinal)
            || property.Name.Contains("Path", StringComparison.Ordinal)
            || property.Name.Contains("Reason", StringComparison.Ordinal)));

        AssertImmutablePublicType(
            typeof(ExportPolicyAuthorization),
            "OperationId",
            "Boundary",
            "Destination",
            "EvidenceDigest");
        AssertImmutablePublicType(
            typeof(ExportPolicyDecision),
            "Disposition",
            "IssueCodes",
            "EvidenceDigest",
            "SatisfiedGateStatus",
            "ResolutionRequirements",
            "Authorization",
            "IsAuthorized");
        AssertImmutablePublicType(
            typeof(ExportPolicyConsumption),
            "OperationId",
            "Boundary",
            "Destination",
            "EvidenceDigest",
            "SatisfiedGateStatus");
        AssertImmutablePublicType(
            typeof(FinalRenderPolicySnapshot),
            "Digest",
            "CurrentDigest");
        Assert.IsTrue(typeof(ExportPolicy).IsPublic);
        Assert.IsTrue(typeof(ExportPolicy).IsSealed);
        CollectionAssert.AreEquivalent(
            new[] { "EvaluateAndAuthorize", "Consume" },
            typeof(ExportPolicy).GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
    }

    [TestMethod]
    public void EvidenceCollectionsSnapshotInputsAndRejectDuplicateIdentities()
    {
        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            transcriptText: "A confirmed presenter statement.",
            includeConfirmedPerformer: true))
        {
            var warning = new ExportPolicyWarningEvidence(
                EntityId<ExportPolicyWarningIdTag>.New(),
                ExportPolicyFixture.Digest('c'),
                ExportPolicyWarningApplicability.AllOutputs);
            var warnings = new List<ExportPolicyWarningEvidence> { warning };
            ExportPolicyRequest request = fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                warnings: warnings,
                performerEvidence:
                [
                    fixture.CreatePerformerEvidence(
                        includeConfirmation: true,
                        makeConfirmationStale: false),
                ]);
            warnings.Clear();

            Assert.AreEqual(1, request.Warnings.Count);
            Assert.ThrowsExactly<NotSupportedException>(
                () => ((IList<ExportPolicyWarningEvidence>)request.Warnings)
                    .Clear());
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    warnings: [warning, warning]));

            ExportPolicyResolutionEvidence resolution =
                fixture.CreateResolution(
                    ExportPolicyResolutionKind.Warning,
                    warning.SubjectDigest);
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    resolutions: [resolution, resolution]));

            ExportTextSensitivityEvidence sensitivity =
                fixture.CreateSensitivityEvidence();
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    sensitivityEvidence: [sensitivity, sensitivity]));

            ExportPerformerEvidence performer = fixture.CreatePerformerEvidence(
                includeConfirmation: true,
                makeConfirmationStale: false);
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    performerEvidence: [performer, performer]));
            ExportPerformerEvidence alternateAssignment =
                fixture.CreatePerformerEvidence(
                    includeConfirmation: true,
                    makeConfirmationStale: false);
            Assert.AreNotEqual(
                performer.AssignmentRevisionId,
                alternateAssignment.AssignmentRevisionId);
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    performerEvidence: [performer, alternateAssignment]));

            TranscriptRevision alternateTranscript =
                fixture.CreateAlternateTranscriptWithSameSpeaker();
            ExportPerformerEvidence sameSpeakerForAlternateTranscript =
                fixture.CreatePerformerEvidence(
                    includeConfirmation: true,
                    makeConfirmationStale: false,
                    alternateTranscript);
            ExportPolicyRequest distinctTranscriptEvidence =
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal,
                    performerEvidence:
                    [
                        performer,
                        sameSpeakerForAlternateTranscript,
                    ]);
            Assert.AreEqual(2, distinctTranscriptEvidence.PerformerEvidence.Count);
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Unconfirmed))
        {
            RecordedRightsException rightsException =
                fixture.CreateRightsException(appendMatchingAuditEvent: true);
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External,
                    rightsExceptions: [rightsException, rightsException]));

            EntityId<AuditEventIdTag> sharedAuditEventId =
                EntityId<AuditEventIdTag>.New();
            RecordedRightsException first = fixture.CreateRightsException(
                appendMatchingAuditEvent: false,
                sharedAuditEventId);
            var secondAsset = new Asset(
                EntityId<AssetIdTag>.New(),
                "second.mp4",
                "video/mp4",
                ExportPolicyFixture.Digest('d'),
                64,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal test use.",
                    externalPublicationAllowed: false));
            RecordedRightsException second = fixture.CreateRightsException(
                appendMatchingAuditEvent: false,
                sharedAuditEventId,
                secondAsset);
            Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External,
                    rightsExceptions: [first, second]));
        }
    }

    [TestMethod]
    public void PersistedReviewStateAndContentApprovalAreMandatory()
    {
        using (ExportPolicyFixture fixture = new(
            ReviewState.Editing,
            includeContentApproval: false))
        {
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal));

            Assert.IsFalse(decision.IsAuthorized);
            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.ReviewStateNotReady));
            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RequiredApprovalMissing));
            Assert.IsNull(decision.Authorization);
            Assert.AreEqual(ReviewGateStatus.None, decision.SatisfiedGateStatus);
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.Approved,
            includeContentApproval: false))
        {
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal));

            Assert.AreEqual(
                ExportPolicyIssueCode.RequiredApprovalMissing,
                decision.IssueCodes);
            Assert.IsNull(decision.Authorization);
        }
    }

    [TestMethod]
    public void PublicationClassificationBlocksForbiddenPendingAndExternalMismatch()
    {
        foreach (PublicationScope scope in new[]
        {
            PublicationScope.PublicationForbidden,
            PublicationScope.PendingDecision,
        })
        {
            using ExportPolicyFixture fixture = new(
                ReviewState.Approved,
                scope);
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal));

            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.PublicationScopeBlocked), scope.ToString());
            Assert.IsNull(decision.Authorization, scope.ToString());
        }

        foreach (PublicationScope scope in new[]
        {
            PublicationScope.InternalOnly,
            PublicationScope.SpecificGroupOnly,
            PublicationScope.ExternalCandidate,
        })
        {
            using ExportPolicyFixture fixture = new(
                ReviewState.QualityControl,
                scope);
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External));

            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.DestinationNotAllowed), scope.ToString());
            Assert.IsNull(decision.Authorization, scope.ToString());
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            PublicationScope.InternalOnly))
        {
            ExportPolicyDecision internalDecision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.Internal));
            Assert.IsTrue(internalDecision.IsAuthorized);
        }
    }

    [TestMethod]
    public void RightsFailuresBlockTheApplicableBoundary()
    {
        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Unconfirmed))
        {
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External));
            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RightsNotSatisfied));
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Approved,
            externalPublicationAllowed: false))
        {
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External));
            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RightsNotSatisfied));
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.Approved,
            rightsStatus: RightsStatus.Rejected))
        {
            ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.FinalRender,
                    ReviewExportDestination.Internal));
            Assert.IsTrue(decision.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RightsNotSatisfied));
        }
    }

    [TestMethod]
    public void ExactAuditedRightsExceptionAllowsOnlyItsExternalSnapshot()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Unconfirmed);
        RecordedRightsException rightsException = fixture.CreateRightsException(
            appendMatchingAuditEvent: true);
        ExportPolicyRequest request = fixture.CreateRequest(
            ExportPolicyBoundary.Export,
            ReviewExportDestination.External,
            rightsExceptions: [rightsException]);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(request);

        Assert.IsTrue(decision.IsAuthorized);
        Assert.IsNotNull(decision.Authorization);
        Assert.IsTrue(decision.SatisfiedGateStatus.HasFlag(
            ReviewGateStatus.RightsPolicySatisfied));

        using ExportPolicyFixture unauditedFixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Unconfirmed);
        RecordedRightsException unaudited = unauditedFixture.CreateRightsException(
            appendMatchingAuditEvent: false);
        ExportPolicyDecision rejected = unauditedFixture.Policy.EvaluateAndAuthorize(
            unauditedFixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.External,
                rightsExceptions: [unaudited]));

        Assert.IsTrue(rejected.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RightsExceptionInvalid));
        Assert.IsTrue(rejected.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RightsNotSatisfied));
        Assert.IsNull(rejected.Authorization);
    }

    [TestMethod]
    public void RightsExceptionIsRejectedForInternalOutput()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            rightsStatus: RightsStatus.Unconfirmed);
        RecordedRightsException rightsException = fixture.CreateRightsException(
            appendMatchingAuditEvent: true);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                rightsExceptions: [rightsException]));

        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RightsExceptionInvalid));
        Assert.IsNull(decision.Authorization);
    }

    [TestMethod]
    public void ApplicableWarningRequiresExactApprovalBackedResolution()
    {
        using ExportPolicyFixture fixture = new(ReviewState.QualityControl);
        var warning = new ExportPolicyWarningEvidence(
            EntityId<ExportPolicyWarningIdTag>.New(),
            ExportPolicyFixture.Digest('8'),
            ExportPolicyWarningApplicability.AllOutputs);
        ExportPolicyDecision blocked = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                warnings: [warning]));

        Assert.IsTrue(blocked.IssueCodes.HasFlag(
            ExportPolicyIssueCode.UnresolvedWarning));
        ExportPolicyResolutionRequirement requirement =
            blocked.ResolutionRequirements.Single();
        Assert.AreEqual(ExportPolicyResolutionKind.Warning, requirement.Kind);
        Assert.AreEqual(warning.SubjectDigest, requirement.SubjectDigest);
        Assert.IsNull(blocked.Authorization);

        ExportPolicyResolutionEvidence resolution = fixture.CreateResolution(
            ExportPolicyResolutionKind.Warning,
            warning.SubjectDigest);
        ExportPolicyDecision resolved = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                warnings: [warning],
                resolutions: [resolution]));

        Assert.IsTrue(resolved.IsAuthorized);
        Assert.AreEqual(0, resolved.ResolutionRequirements.Count);
    }

    [TestMethod]
    public void ExternalOnlyWarningDoesNotBlockInternalOutputAndExtraResolutionFailsClosed()
    {
        using ExportPolicyFixture fixture = new(ReviewState.QualityControl);
        var warning = new ExportPolicyWarningEvidence(
            EntityId<ExportPolicyWarningIdTag>.New(),
            ExportPolicyFixture.Digest('9'),
            ExportPolicyWarningApplicability.ExternalOnly);

        ExportPolicyDecision internalDecision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                warnings: [warning]));
        Assert.IsTrue(internalDecision.IsAuthorized);

        ExportPolicyResolutionEvidence extra = fixture.CreateResolution(
            ExportPolicyResolutionKind.Warning,
            ExportPolicyFixture.Digest('a'));
        ExportPolicyDecision extraResolution = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.External,
                resolutions: [extra]));
        Assert.IsTrue(extraResolution.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ResolutionEvidenceInvalid));
        Assert.IsNull(extraResolution.Authorization);
    }

    [TestMethod]
    public void PerformerEvidenceMustBeCurrentAndConfirmed()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.Approved,
            transcriptText: "A confirmed presenter statement.",
            includeConfirmedPerformer: true);
        ExportPolicyDecision omitted = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal));
        Assert.IsTrue(omitted.IssueCodes.HasFlag(
            ExportPolicyIssueCode.PerformerConfirmationRequired));
        Assert.IsNull(omitted.Authorization);

        ExportPerformerEvidence unconfirmed = fixture.CreatePerformerEvidence(
            includeConfirmation: false,
            makeConfirmationStale: false);

        ExportPolicyDecision blocked = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal,
                performerEvidence: [unconfirmed]));

        Assert.IsTrue(blocked.IssueCodes.HasFlag(
            ExportPolicyIssueCode.PerformerConfirmationRequired));
        Assert.IsNull(blocked.Authorization);

        ExportPerformerEvidence confirmed = fixture.CreatePerformerEvidence(
            includeConfirmation: true,
            makeConfirmationStale: false);
        ExportPolicyDecision allowed = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal,
                performerEvidence: [confirmed]));
        Assert.IsTrue(allowed.IsAuthorized);

        ExportPerformerEvidence stale = fixture.CreatePerformerEvidence(
            includeConfirmation: true,
            makeConfirmationStale: true);
        ExportPolicyDecision staleDecision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal,
                performerEvidence: [stale]));
        Assert.IsTrue(staleDecision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.PerformerConfirmationRequired));
        Assert.IsNull(staleDecision.Authorization);
    }

    [TestMethod]
    public void ExternalTranscriptRequiresCompleteSensitivityEvidence()
    {
        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            transcriptText: "An ordinary public statement."))
        {
            ExportPolicyDecision missing = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External));
            Assert.IsTrue(missing.IssueCodes.HasFlag(
                ExportPolicyIssueCode.SensitivityEvidenceMissing));

            ExportPolicyDecision complete = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External,
                    sensitivityEvidence: [fixture.CreateSensitivityEvidence()]));
            Assert.IsTrue(complete.IsAuthorized);
        }

        using (ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            transcriptText: "invalid\ud800"))
        {
            ExportPolicyDecision incomplete = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.External,
                    sensitivityEvidence: [fixture.CreateSensitivityEvidence()]));
            Assert.IsTrue(incomplete.IssueCodes.HasFlag(
                ExportPolicyIssueCode.SensitivityScanIncomplete));
            Assert.IsNull(incomplete.Authorization);
        }
    }

    [TestMethod]
    public void SensitivityFindingRequiresExactHumanResolutionBeforeExternalExport()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            transcriptText: "Contact user@example.com before publication.");
        ExportTextSensitivityEvidence evidence =
            fixture.CreateSensitivityEvidence();
        ExportPolicyDecision blocked = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.External,
                sensitivityEvidence: [evidence]));

        Assert.IsTrue(blocked.IssueCodes.HasFlag(
            ExportPolicyIssueCode.SensitivityResolutionRequired));
        ExportPolicyResolutionRequirement requirement =
            blocked.ResolutionRequirements.Single();
        Assert.AreEqual(
            ExportPolicyResolutionKind.TextSensitivity,
            requirement.Kind);

        ExportPolicyResolutionEvidence resolution = fixture.CreateResolution(
            requirement.Kind,
            requirement.SubjectDigest);
        ExportPolicyDecision resolved = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.External,
                sensitivityEvidence: [evidence],
                resolutions: [resolution]));

        Assert.IsTrue(resolved.IsAuthorized);
        Assert.AreEqual(0, resolved.ResolutionRequirements.Count);
    }

    [TestMethod]
    public void MissingSourceSpanProvenanceBlocksEvenInternalFinalRender()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.Approved,
            transcriptText: "Trace this statement to the source.");
        RenderGraphValidationContext missingSourceSpan =
            fixture.CreateValidationContext(sourceSpans: []);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.FinalRender,
                ReviewExportDestination.Internal,
                validationContext: missingSourceSpan));

        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ProvenanceInvalid));
        Assert.IsNull(decision.Authorization);
    }

    [TestMethod]
    public void TranscriptTokensMustBelongToTheUsedSourceSpan()
    {
        using ExportPolicyFixture fixture = new(
            ReviewState.QualityControl,
            transcriptText: "Trace this statement to the exact source span.",
            crossBindTranscriptToken: true);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.External,
                sensitivityEvidence: [fixture.CreateSensitivityEvidence()]));

        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ProvenanceInvalid));
        Assert.IsNull(decision.Authorization);
    }

    [TestMethod]
    public void RegisteredRenderQcMustBindTheExactArtifactAndVerifiedDigest()
    {
        using ExportPolicyFixture fixture = new(ReviewState.QualityControl);
        ExportRenderQcBinding binding = fixture.CreateRegisteredRenderQcBinding();
        ExportPolicyDecision exact = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                renderQcBinding: binding));
        Assert.IsTrue(exact.IsAuthorized);

        var mismatch = new ExportRenderQcBinding(
            binding.RenderArtifactId,
            fixture.Version,
            fixture.Graph,
            binding.RenderContentDigest,
            binding.RenderByteSize,
            ExportPolicyFixture.Digest('f'),
            binding.QcEvidenceDigest);
        ExportPolicyDecision rejected = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateRequest(
                ExportPolicyBoundary.Export,
                ReviewExportDestination.Internal,
                renderQcBinding: mismatch));

        Assert.IsTrue(rejected.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RenderQcMismatch));
        Assert.IsNull(rejected.Authorization);
    }

    [TestMethod]
    public void ExportRejectsMissingOrTamperedRegisteredRenderArtifact()
    {
        using (ExportPolicyFixture fixture = new(ReviewState.QualityControl))
        {
            ExportPolicyDecision missing = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.Internal,
                    omitRenderQcBinding: true));

            Assert.IsTrue(missing.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RenderQcMismatch));
            Assert.IsNull(missing.Authorization);
        }

        using (ExportPolicyFixture fixture = new(ReviewState.QualityControl))
        {
            ExportRenderQcBinding binding =
                fixture.CreateRegisteredRenderQcBinding();
            fixture.TamperRegisteredRenderArtifact();
            ExportPolicyDecision tampered = fixture.Policy.EvaluateAndAuthorize(
                fixture.CreateRequest(
                    ExportPolicyBoundary.Export,
                    ReviewExportDestination.Internal,
                    renderQcBinding: binding));

            Assert.IsTrue(tampered.IssueCodes.HasFlag(
                ExportPolicyIssueCode.RenderQcMismatch));
            Assert.IsNull(tampered.Authorization);
        }
    }

    private static void AssertImmutablePublicType(
        Type type,
        params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedProperties,
            properties.Select(static property => property.Name).ToArray(),
            type.Name);
        Assert.IsTrue(
            properties.All(static property => property.SetMethod is null),
            type.Name);
    }

    private sealed class ExportPolicyFixture : IDisposable
    {
        private const string AdministratorSid =
            "S-1-5-21-1000-2000-3000-4100";
        private const string EditorSid =
            "S-1-5-21-1000-2000-3000-4101";
        private const string ApproverSid =
            "S-1-5-21-1000-2000-3000-4102";
        private const string RightsApproverSid =
            "S-1-5-21-1000-2000-3000-4103";
        private const string PerformerReviewerSid =
            "S-1-5-21-1000-2000-3000-4104";

        private static readonly DateTimeOffset CreatedAt =
            new(2046, 6, 7, 8, 9, 10, TimeSpan.Zero);

        private readonly string _rootPath;
        private readonly ProjectStore _projectStore;
        private readonly AuthorizationService _authorization;
        private readonly ArtifactStore _artifactStore;
        private ExportRenderQcBinding? _defaultRenderQcBinding;

        internal ExportPolicyFixture(
            ReviewState reviewState,
            PublicationScope publicationScope =
                PublicationScope.ExternalPublicationApproved,
            RightsStatus rightsStatus = RightsStatus.Approved,
            bool? externalPublicationAllowed = null,
            bool includeContentApproval = true,
            string? transcriptText = null,
            bool includeConfirmedPerformer = false,
            bool crossBindTranscriptToken = false)
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ExportPolicyTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            string databasePath = Path.Combine(_rootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(_rootPath, "backups"));

            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            ContentDigest assetDigest = Digest('1');
            bool allowsExternal = externalPublicationAllowed
                ?? rightsStatus == RightsStatus.Approved;
            Asset = new Asset(
                assetId,
                "input.mp4",
                "video/mp4",
                assetDigest,
                128,
                new RightsRecord(
                    rightsStatus,
                    rightsStatus == RightsStatus.Approved
                        ? "rights-evidence-1"
                        : null,
                    "Approved test use.",
                    allowsExternal));
            Performer = includeConfirmedPerformer
                ? new Performer(
                    EntityId<PerformerIdTag>.New(),
                    "Presenter Alpha",
                    "Presenter",
                    CreatedAt.AddMonths(-1))
                : null;
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Export policy test project",
                "Verify exact final-render and export policy evidence.",
                "Test operators",
                publicationScope,
                deadline: null,
                [Asset],
                Performer is null ? [] : [Performer]);
            _projectStore = new ProjectStore(databasePath);
            _projectStore.CreateProject(
                Project,
                ProjectSnapshotJson.WriteProject(Project),
                CreatedAt);

            var range = new TimeRange(default, new RationalTime(1, 1));
            IReadOnlyList<SourceSpan> sourceSpans;
            OriginRef origin;
            if (transcriptText is null)
            {
                sourceSpans = [];
                origin = OriginRef.FromAsset(assetId);
            }
            else
            {
                EntityId<SourceSpanIdTag> sourceSpanId =
                    EntityId<SourceSpanIdTag>.New();
                SpeakerId = EntityId<TranscriptSpeakerIdTag>.New();
                var token = new Token(
                    EntityId<TranscriptTokenIdTag>.New(),
                    transcriptText,
                    range,
                    recognitionConfidence: 0.9);
                var sentence = new Sentence(
                    EntityId<TranscriptSentenceIdTag>.New(),
                    transcriptText,
                    [token]);
                var utterance = new Utterance(
                    EntityId<TranscriptUtteranceIdTag>.New(),
                    sourceSpanId,
                    SpeakerId,
                    "Speaker 1",
                    transcriptText,
                    range,
                    recognitionConfidence: 0.9,
                    [sentence]);
                var utterances = new List<Utterance> { utterance };
                IReadOnlyList<EntityId<TranscriptTokenIdTag>> sourceSpanTokenIds =
                    [token.Id];
                if (crossBindTranscriptToken)
                {
                    const string secondText = "Separate transcript context.";
                    var secondToken = new Token(
                        EntityId<TranscriptTokenIdTag>.New(),
                        secondText,
                        range,
                        recognitionConfidence: 0.9);
                    var secondSentence = new Sentence(
                        EntityId<TranscriptSentenceIdTag>.New(),
                        secondText,
                        [secondToken]);
                    utterances.Add(new Utterance(
                        EntityId<TranscriptUtteranceIdTag>.New(),
                        EntityId<SourceSpanIdTag>.New(),
                        SpeakerId,
                        "Speaker 1",
                        secondText,
                        range,
                        recognitionConfidence: 0.9,
                        [secondSentence]));
                    sourceSpanTokenIds = [secondToken.Id];
                }

                Transcript = new TranscriptRevision(
                    EntityId<TranscriptRevisionIdTag>.New(),
                    parentRevisionId: null,
                    TranscriptChangeSource.Machine,
                    CreatedAt.AddDays(-1),
                    utterances);
                SourceSpan = new SourceSpan(
                    sourceSpanId,
                    assetId,
                    "video:0",
                    range,
                    assetDigest,
                    Transcript.Id,
                    sourceSpanTokenIds);
                sourceSpans = [SourceSpan];
                origin = OriginRef.FromSourceSpan(sourceSpanId);
            }

            TimeRange? sourceRange = transcriptText is null ? null : range;
            var element = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                range,
                origin,
                sourceRange: sourceRange);
            Version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                EditorSid,
                CreatedAt.AddMinutes(1),
                "Create the export-policy composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            _projectStore.AppendCompositionVersion(
                Version,
                CompositionSnapshotJson.WriteVersion(Version));

            var timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                Version.Id,
                [new TimelineMapEntry(range, origin, sourceRange)]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "Export policy fixture template",
                Digest('2'));
            var layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        new NormalizedRect(
                            default,
                            default,
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            ContentDigest layoutDigest = Digest('3');
            var profile = new RenderProfile(
                EntityId<RenderProfileIdTag>.New(),
                "Export policy fixture profile",
                Digest('4'),
                1920,
                1080,
                30,
                1,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            EntityId<RenderInputIdTag> inputId = EntityId<RenderInputIdTag>.New();
            var graphInput = new RenderGraphInput(
                inputId,
                RenderInputKind.Video,
                assetDigest,
                Asset.ByteSize,
                origin);
            RenderNode videoNode = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                inputId,
                range,
                origin,
                sourceRange,
                "main");
            RenderNode outputNode = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [videoNode.Id],
                range);
            Graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                Version.Id,
                Version.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                [graphInput],
                fonts: [],
                [videoNode, outputNode],
                outputNode.Id);
            Profile = profile;
            ValidationContext = new RenderGraphValidationContext(
                Project,
                Version,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                sourceSpans,
                editorialRecordIds: [],
                [new RenderArtifactEvidence(inputId, assetDigest, Asset.ByteSize)],
                fonts: []);

            ReviewStore = new ReviewStore(databasePath);
            ReviewStore.AppendReviewRecord(new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                Version,
                reviewState,
                EditorSid,
                ReviewActorRole.Editor,
                CreatedAt.AddMinutes(2),
                "Persist the policy boundary review state."));
            ApprovalId = EntityId<ApprovalIdTag>.New();
            if (includeContentApproval)
            {
                ReviewStore.AppendApproval(new Approval(
                    ApprovalId,
                    Version,
                    ApproverSid,
                    ReviewActorRole.ContentApprover,
                    CreatedAt.AddMinutes(3),
                    "Approve the exact composition for policy evaluation."));
            }

            _authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(_authorization.BootstrapInitialRoles(AdministratorSid));
            _authorization.AssignRole(
                EditorSid,
                AuthorizationRole.Editor,
                AdministratorSid,
                "Assign the editor role for export-policy tests.");
            _authorization.AssignRole(
                PerformerReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid,
                "Assign the performer-reviewer role for export-policy tests.");
            var approvalService = new ApprovalService(ReviewStore, _authorization);
            _artifactStore = new ArtifactStore(databasePath, _rootPath);
            Policy = new ExportPolicy(
                _projectStore,
                ReviewStore,
                approvalService,
                _authorization,
                _artifactStore);
        }

        internal Project Project { get; }

        internal Asset Asset { get; }

        internal Performer? Performer { get; }

        internal CompositionVersion Version { get; }

        internal RenderGraph Graph { get; }

        internal RenderProfile Profile { get; }

        internal RenderGraphValidationContext ValidationContext { get; }

        internal TranscriptRevision? Transcript { get; }

        internal SourceSpan? SourceSpan { get; }

        internal EntityId<TranscriptSpeakerIdTag>? SpeakerId { get; }

        internal EntityId<ApprovalIdTag> ApprovalId { get; }

        internal ReviewStore ReviewStore { get; }

        internal ExportPolicy Policy { get; }

        internal ExportPolicyRequest CreateRequest(
            ExportPolicyBoundary boundary,
            ReviewExportDestination destination,
            EntityId<ExportOperationIdTag>? operationId = null,
            string actorSid = EditorSid,
            AuthorizationRole actingRole = AuthorizationRole.Editor,
            IReadOnlyList<RecordedRightsException>? rightsExceptions = null,
            IReadOnlyList<ExportPerformerEvidence>? performerEvidence = null,
            IReadOnlyList<ExportTextSensitivityEvidence>? sensitivityEvidence = null,
            IReadOnlyList<ExportPolicyWarningEvidence>? warnings = null,
            IReadOnlyList<ExportPolicyResolutionEvidence>? resolutions = null,
            ExportRenderQcBinding? renderQcBinding = null,
            RenderGraphValidationContext? validationContext = null,
            bool omitRenderQcBinding = false)
        {
            ExportRenderQcBinding? effectiveRenderQcBinding = renderQcBinding;
            if (boundary == ExportPolicyBoundary.Export
                && effectiveRenderQcBinding is null
                && !omitRenderQcBinding)
            {
                effectiveRenderQcBinding = CreateRegisteredRenderQcBinding();
            }

            return new ExportPolicyRequest(
                operationId ?? EntityId<ExportOperationIdTag>.New(),
                boundary,
                actorSid,
                actingRole,
                Project,
                Version,
                new ExportDestinationBinding(destination, Digest('5')),
                Graph,
                validationContext ?? ValidationContext,
                rightsExceptions ?? [],
                performerEvidence ?? [],
                sensitivityEvidence ?? [],
                warnings ?? [],
                resolutions ?? [],
                effectiveRenderQcBinding);
        }

        internal ExportTextSensitivityEvidence CreateSensitivityEvidence() =>
            new(
                Transcript
                    ?? throw new InvalidOperationException(
                        "The fixture has no transcript."),
                dictionaryEntries: []);

        internal ExportPerformerEvidence CreatePerformerEvidence(
            bool includeConfirmation,
            bool makeConfirmationStale,
            TranscriptRevision? transcriptRevision = null)
        {
            TranscriptRevision transcript = transcriptRevision ?? Transcript
                ?? throw new InvalidOperationException(
                    "The fixture has no transcript.");
            EntityId<TranscriptSpeakerIdTag> speakerId = SpeakerId
                ?? throw new InvalidOperationException(
                    "The fixture has no speaker.");
            Performer performer = Performer
                ?? throw new InvalidOperationException(
                    "The fixture has no performer.");
            SpeakerAssignmentHistory initial = SpeakerAssignmentService.SetAssignment(
                Project,
                transcript,
                history: null,
                EntityId<SpeakerAssignmentRevisionIdTag>.New(),
                speakerId,
                performer.Id,
                "Alpha",
                SpeakerDisplayFormat.FullName,
                PerformerReviewerSid,
                CreatedAt.AddHours(-2),
                "Record the performer assignment for export policy.");
            PerformerConfirmation[] confirmations = includeConfirmation
                ?
                [
                    PerformerPolicy.CaptureConfirmation(
                        _authorization,
                        Project,
                        initial,
                        speakerId,
                        EntityId<PerformerConfirmationIdTag>.New(),
                        CreatedAt,
                        PerformerReviewerSid,
                        AuthorizationRole.PerformerReviewer,
                        CreatedAt.AddHours(1),
                        "Confirm the exact presenter label."),
                ]
                : [];
            SpeakerAssignmentHistory current = initial;
            if (makeConfirmationStale)
            {
                current = SpeakerAssignmentService.SetAssignment(
                    Project,
                    transcript,
                    initial,
                    EntityId<SpeakerAssignmentRevisionIdTag>.New(),
                    speakerId,
                    performer.Id,
                    "Alpha",
                    SpeakerDisplayFormat.Surname,
                    PerformerReviewerSid,
                    CreatedAt.AddHours(-1),
                    "Change the presenter display after confirmation.");
            }

            return new ExportPerformerEvidence(
                current,
                CreatedAt,
                confirmations);
        }

        internal TranscriptRevision CreateAlternateTranscriptWithSameSpeaker()
        {
            EntityId<TranscriptSpeakerIdTag> speakerId = SpeakerId
                ?? throw new InvalidOperationException(
                    "The fixture has no speaker.");
            var range = new TimeRange(default, new RationalTime(1, 1));
            const string text = "The same speaker in another transcript revision.";
            var token = new Token(
                EntityId<TranscriptTokenIdTag>.New(),
                text,
                range,
                recognitionConfidence: 0.9);
            var sentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                text,
                [token]);
            var utterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                EntityId<SourceSpanIdTag>.New(),
                speakerId,
                "Speaker 1",
                text,
                range,
                recognitionConfidence: 0.9,
                [sentence]);
            return new TranscriptRevision(
                EntityId<TranscriptRevisionIdTag>.New(),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt.AddDays(-1),
                [utterance]);
        }

        internal RenderGraphValidationContext CreateValidationContext(
            IReadOnlyList<SourceSpan> sourceSpans,
            CompositionVersion? compositionVersion = null) =>
            new(
                ValidationContext.Project,
                compositionVersion ?? ValidationContext.CompositionVersion,
                ValidationContext.TimelineMap,
                ValidationContext.TimelineMapDigest,
                ValidationContext.Profile,
                ValidationContext.LayoutSpec,
                ValidationContext.LayoutSpecDigest,
                ValidationContext.Template,
                sourceSpans,
                ValidationContext.EditorialRecordIds,
                ValidationContext.Artifacts,
                ValidationContext.Fonts);

        internal ExportRenderQcBinding CreateRegisteredRenderQcBinding()
        {
            if (_defaultRenderQcBinding is not null)
            {
                return _defaultRenderQcBinding;
            }

            byte[] bytes = [1, 3, 5, 7, 9];
            ContentDigest digest = ContentDigest.Compute(bytes);
            string stagingDirectory = Path.Combine(
                _rootPath,
                "projects",
                Project.Id.ToString(),
                "staging");
            Directory.CreateDirectory(stagingDirectory);
            string stagingPath = Path.Combine(stagingDirectory, "render.mp4");
            File.WriteAllBytes(stagingPath, bytes);
            EntityId<ArtifactIdTag> artifactId = EntityId<ArtifactIdTag>.New();
            StoredArtifact stored = _artifactStore.PromoteAsync(
                    new ArtifactPromotionRequest(
                        artifactId,
                        Project.Id,
                        ArtifactKind.Render,
                        stagingPath,
                        "render.mp4",
                        digest,
                        bytes.LongLength,
                        CreatedAt.AddHours(2)))
                .GetAwaiter()
                .GetResult();
            Assert.AreEqual(digest, stored.ContentDigest);
            _defaultRenderQcBinding = new ExportRenderQcBinding(
                artifactId,
                Version,
                Graph,
                digest,
                bytes.LongLength,
                digest,
                Digest('b'));
            return _defaultRenderQcBinding;
        }

        internal void TamperRegisteredRenderArtifact()
        {
            _ = CreateRegisteredRenderQcBinding();
            string renderPath = Path.Combine(
                _rootPath,
                "projects",
                Project.Id.ToString(),
                "renders",
                "render.mp4");
            File.WriteAllBytes(renderPath, [9, 7, 5, 3, 1]);
        }

        internal RecordedRightsException CreateRightsException(
            bool appendMatchingAuditEvent,
            EntityId<AuditEventIdTag>? auditEventId = null,
            Asset? asset = null)
        {
            EntityId<AuditEventIdTag> effectiveAuditEventId =
                auditEventId ?? EntityId<AuditEventIdTag>.New();
            Asset targetAsset = asset ?? Asset;
            DateTimeOffset decidedAt = CreatedAt.AddMinutes(2);
            const string reason =
                "Authorize this exact unconfirmed-rights external snapshot.";
            ConstructorInfo constructor = typeof(RecordedRightsException)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
            var rightsException = (RecordedRightsException)constructor.Invoke(
                [
                    EntityId<RightsExceptionIdTag>.New(),
                    targetAsset,
                    Version,
                    Profile,
                    effectiveAuditEventId,
                    RightsApproverSid,
                    ReviewActorRole.RightsApprover,
                    decidedAt,
                    reason,
                    "rights-exception-evidence-1",
                ]);
            if (appendMatchingAuditEvent)
            {
                ReviewStore.AppendAuditEvent(new AuditEvent(
                    effectiveAuditEventId,
                    AuditEventKind.RightsDecisionRecorded,
                    RightsApproverSid,
                    ReviewActorRole.RightsApprover,
                    decidedAt,
                    reason,
                    Project.Id,
                    Version));
            }

            return rightsException;
        }

        internal ExportPolicyResolutionEvidence CreateResolution(
            ExportPolicyResolutionKind kind,
            ContentDigest subjectDigest) =>
            new(
                EntityId<ExportPolicyResolutionIdTag>.New(),
                kind,
                subjectDigest,
                Project.Id,
                Version.Id,
                Version.ContentDigest,
                ApprovalId,
                CreatedAt.AddMinutes(2));

        internal void RevokeEditorRole() =>
            _authorization.RevokeRole(
                EditorSid,
                AuthorizationRole.Editor,
                AdministratorSid);

        internal void AdvanceStoredProjectRevision()
        {
            int revision = _projectStore.UpdateProject(
                Project,
                "{\"revision\":1}",
                expectedRevision: 0,
                CreatedAt.AddMinutes(4));
            Assert.AreEqual(1, revision);
        }

        internal void ReplaceStoredProjectPayload()
        {
            int revision = _projectStore.UpdateProject(
                Project,
                "{\"tampered\":true}",
                expectedRevision: 0,
                CreatedAt.AddMinutes(4));
            Assert.AreEqual(1, revision);
        }

        internal static ContentDigest Digest(char value) =>
            new(new string(value, 64));

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(_rootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }
}