using System.Text;
using HubRadio.App.Delivery;
using HubRadio.App.Jobs;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SubtitleVariantServiceTests
{
    [TestMethod]
    public async Task CreateAsync_ProducesTwoExplicitDeliveriesFromSharedEvidence()
    {
        VariantFixture fixture = new();

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Completed,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantStage.SubtitleFreeDelivered,
            result.LastCompletedStage);
        Assert.IsTrue(result.IsComplete);
        Assert.IsFalse(result.RequiresReconciliation);
        Assert.IsNull(result.FailureReason);
        Assert.IsNotNull(result.SidecarFile);
        Assert.AreEqual(
            fixture.Sidecar.Artifact.Sha256,
            result.SidecarFile.ContentDigest);
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(2, fixture.DeliveryCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "render:burnedIn",
                "qc:burnedIn",
                "delivery:BurnedIn",
                "render:subtitleFree",
                "qc:subtitleFree",
                "delivery:SubtitleFree",
            },
            fixture.Operations.ToArray());

        SubtitleDeliveryVariantEvidence burnedIn = result.BurnedInDelivery!
            .Manifest.SubtitleVariant!;
        SubtitleDeliveryVariantEvidence subtitleFree = result
            .SubtitleFreeDelivery!
            .Manifest.SubtitleVariant!;
        Assert.AreEqual(
            SubtitleDeliveryVariantKind.BurnedIn,
            burnedIn.Kind);
        Assert.AreEqual(
            SubtitleDeliveryVariantKind.SubtitleFree,
            subtitleFree.Kind);
        Assert.IsNull(burnedIn.SidecarArtifact);
        Assert.AreEqual(fixture.Sidecar.Artifact, subtitleFree.SidecarArtifact);
        Assert.AreEqual(
            fixture.DisplayProfile.VersionDigest,
            burnedIn.DisplayProfileDigest);
        Assert.AreEqual(
            burnedIn.DisplayProfileDigest,
            subtitleFree.DisplayProfileDigest);
        Assert.AreEqual(
            result.SubtitleEvidenceDigest,
            burnedIn.SubtitleEvidenceDigest);
        Assert.AreEqual(
            burnedIn.SubtitleEvidenceDigest,
            subtitleFree.SubtitleEvidenceDigest);
        Assert.AreNotEqual(
            result.BurnedInRender!.Output!.ArtifactId,
            result.SubtitleFreeRender!.Output!.ArtifactId);
        Assert.AreNotEqual(
            result.BurnedInDelivery.EvidenceDigest,
            result.SubtitleFreeDelivery.EvidenceDigest);
        Assert.AreEqual(
            result.BurnedInDelivery.ApprovalEvidenceDigest,
            result.SubtitleFreeDelivery.ApprovalEvidenceDigest);
    }

    [TestMethod]
    public async Task CreateAsync_ProducesEveryCueInBurnedChainAndSidecar()
    {
        VariantFixture fixture = new(subtitleCueCount: 2);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Completed,
            result.Disposition);
        Assert.AreEqual(2, fixture.SubtitleRevisions.Count);
        Assert.AreEqual(2, result.SidecarFile!.CueCount);
        Assert.AreEqual(
            2,
            fixture.BurnedIn.RenderRequest.Graph.Inputs.Count(static input =>
                input.Kind == RenderInputKind.Subtitle));
        RenderNode[] subtitleNodes = fixture.BurnedIn.RenderRequest.Graph.Nodes
            .Where(static node => node.Kind == RenderNodeKind.Subtitle)
            .ToArray();
        Assert.AreEqual(2, subtitleNodes.Length);
        Assert.AreEqual(subtitleNodes[0].Id, subtitleNodes[1].InputNodeIds[0]);
        CollectionAssert.AreEqual(
            fixture.SubtitleRevisions
                .Select(static revision => revision.Snapshot.Cue.Id)
                .ToArray(),
            subtitleNodes.Select(static node => node.Cue!.Id).ToArray());
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(2, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_AcceptsIndependentDuplicateRenderRecovery()
    {
        VariantFixture fixture = new();
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Duplicate);
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Duplicate);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Completed,
            result.Disposition);
        Assert.AreEqual(
            FinalRenderDisposition.Duplicate,
            result.BurnedInRender!.Disposition);
        Assert.AreEqual(
            FinalRenderDisposition.Duplicate,
            result.SubtitleFreeRender!.Disposition);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(2, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_CompletesWithoutOptionalSidecar()
    {
        VariantFixture fixture = new();
        SubtitleVariantWorkItem subtitleFree =
            fixture.CreateSubtitleFreeWithoutSidecar();
        SubtitleVariantRequest request = fixture.CreateRequest(
            subtitleFree: subtitleFree,
            includeSidecar: false);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Completed,
            result.Disposition);
        Assert.IsNull(result.SidecarFile);
        Assert.IsNull(
            result.SubtitleFreeDelivery!.Manifest.SubtitleVariant!
                .SidecarArtifact);
        Assert.IsFalse(result.SubtitleFreeDelivery.Manifest.Artifacts.Any(
            static artifact => string.Equals(
                artifact.ArtifactType,
                "subtitle",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CreateAsync_RejectsUndifferentiatedDeliveryEvidence()
    {
        VariantFixture fixture = new()
        {
            ForceSharedDeliveryEvidenceDigest = true,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.DeliveryEvidenceMismatch,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.SubtitleFreeDelivered,
            result.LastCompletedStage);
        Assert.IsNotNull(result.BurnedInDelivery);
        Assert.IsNotNull(result.SubtitleFreeDelivery);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsSidecarMismatchBeforeAnySideEffect()
    {
        VariantFixture fixture = new();
        ManifestArtifact wrongArtifact = new(
            "subtitle",
            fixture.Sidecar.Artifact.MediaType,
            fixture.Sidecar.Artifact.Language,
            fixture.Sidecar.Artifact.ByteSize,
            Digest('f'),
            fixture.Sidecar.Artifact.StorageReference);
        var wrongSidecar = new SubtitleVariantSidecarRequest(
            fixture.Sidecar.ArtifactId,
            wrongArtifact,
            fixture.Sidecar.Export);
        SubtitleVariantRequest request = fixture.CreateRequest(
            sidecar: wrongSidecar);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.SidecarMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsStaleSubtitleRevisionBeforeRendering()
    {
        VariantFixture fixture = new();
        SubtitleCueRevision current = fixture.SubtitleRevision;
        var stale = new SubtitleCueRevision(
            EntityId<SubtitleCueRevisionIdTag>.New(),
            parentRevisionId: null,
            current.ProjectId,
            EntityId<CompositionVersionIdTag>.New(),
            parentCompositionVersionId: null,
            current.CompositionVersionDigest,
            current.SourceCandidateId,
            current.SourceRevisionId,
            current.SourceRevisionDigest,
            current.SourceOrigin,
            current.Snapshot,
            current.Change,
            current.ActorSid,
            current.CreatedAt,
            current.Reason,
            current.ContentDigest);
        SubtitleVariantRequest request = fixture.CreateRequest(
            revisions: [stale]);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.SubtitleEvidenceMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsCrossVersionApprovalBeforeRendering()
    {
        VariantFixture fixture = new();
        StoredApproval stale = new(
            EntityId<ApprovalIdTag>.New(),
            fixture.Project.Id,
            EntityId<CompositionVersionIdTag>.New(),
            Digest('0'),
            "S-1-5-21-1000-2000-3000-4001",
            ReviewActorRole.ContentApprover,
            new DateTimeOffset(2046, 6, 7, 8, 11, 10, TimeSpan.Zero),
            "Approval belongs to another composition version.");
        FinalRenderRequest burnedRender = fixture.CloneRenderRequest(
            fixture.BurnedIn.RenderRequest,
            approvals: [stale]);
        FinalRenderRequest freeRender = fixture.CloneRenderRequest(
            fixture.SubtitleFree.RenderRequest,
            approvals: [stale]);
        var burnedIn = new SubtitleVariantWorkItem(
            fixture.BurnedIn.DeliveryManifestId,
            burnedRender,
            fixture.BurnedIn.QualityControlRequest,
            fixture.BurnedIn.ArtifactEvidence);
        var subtitleFree = new SubtitleVariantWorkItem(
            fixture.SubtitleFree.DeliveryManifestId,
            freeRender,
            fixture.SubtitleFree.QualityControlRequest,
            fixture.SubtitleFree.ArtifactEvidence);
        SubtitleVariantRequest request = fixture.CreateRequest(
            burnedIn: burnedIn,
            subtitleFree: subtitleFree);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.ApprovalMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsSharedInputPathDriftBeforeRendering()
    {
        VariantFixture fixture = new();
        FinalRenderRequest original = fixture.SubtitleFree.RenderRequest;
        FfmpegInputBinding videoBinding = original.Inputs.Single();
        var driftedBinding = new FfmpegInputBinding(
            videoBinding.InputId,
            new BinaryVerificationRequest(
                videoBinding.VerificationRequest.ComponentIdentity,
                Path.Combine(fixture.RootPath, "different-video.mp4"),
                videoBinding.VerificationRequest.ExpectedDigest,
                videoBinding.VerificationRequest.ExpectedByteSize));
        FinalRenderRequest driftedRender = fixture.CloneRenderRequest(
            original,
            inputs: [driftedBinding]);
        var driftedWorkItem = new SubtitleVariantWorkItem(
            fixture.SubtitleFree.DeliveryManifestId,
            driftedRender,
            fixture.SubtitleFree.QualityControlRequest,
            fixture.SubtitleFree.ArtifactEvidence);
        SubtitleVariantRequest request = fixture.CreateRequest(
            subtitleFree: driftedWorkItem);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.SharedInputMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsSharedInputVerificationDriftBeforeRendering()
    {
        VariantFixture fixture = new();
        EntityId<RenderInputIdTag> videoInputId = fixture.BurnedIn
            .RenderRequest.Graph.Inputs.Single(static input =>
                input.Kind == RenderInputKind.Video).Id;
        FfmpegInputBinding[] burnedInputs = fixture.BurnedIn.RenderRequest.Inputs
            .Select(binding => binding.InputId == videoInputId
                ? DriftDigest(binding)
                : binding)
            .ToArray();
        FfmpegInputBinding[] freeInputs = fixture.SubtitleFree.RenderRequest.Inputs
            .Select(binding => binding.InputId == videoInputId
                ? DriftDigest(binding)
                : binding)
            .ToArray();
        FinalRenderRequest burnedRender = fixture.CloneRenderRequest(
            fixture.BurnedIn.RenderRequest,
            inputs: burnedInputs);
        FinalRenderRequest freeRender = fixture.CloneRenderRequest(
            fixture.SubtitleFree.RenderRequest,
            inputs: freeInputs);
        var burnedIn = new SubtitleVariantWorkItem(
            fixture.BurnedIn.DeliveryManifestId,
            burnedRender,
            fixture.BurnedIn.QualityControlRequest,
            fixture.BurnedIn.ArtifactEvidence);
        var subtitleFree = new SubtitleVariantWorkItem(
            fixture.SubtitleFree.DeliveryManifestId,
            freeRender,
            fixture.SubtitleFree.QualityControlRequest,
            fixture.SubtitleFree.ArtifactEvidence);
        SubtitleVariantRequest request = fixture.CreateRequest(
            burnedIn: burnedIn,
            subtitleFree: subtitleFree);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.SharedInputMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsExtraInputBindingBeforeRendering()
    {
        VariantFixture fixture = new();
        var extraBinding = new FfmpegInputBinding(
            EntityId<RenderInputIdTag>.New(),
            new BinaryVerificationRequest(
                "input.extra",
                Path.Combine(fixture.RootPath, "extra.bin"),
                Digest('0'),
                expectedByteSize: 1));
        FinalRenderRequest burnedRender = fixture.CloneRenderRequest(
            fixture.BurnedIn.RenderRequest,
            inputs: [.. fixture.BurnedIn.RenderRequest.Inputs, extraBinding]);
        var burnedIn = new SubtitleVariantWorkItem(
            fixture.BurnedIn.DeliveryManifestId,
            burnedRender,
            fixture.BurnedIn.QualityControlRequest,
            fixture.BurnedIn.ArtifactEvidence);
        SubtitleVariantRequest request = fixture.CreateRequest(
            burnedIn: burnedIn);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.SharedInputMismatch,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsOutputIdentityConflictBeforeRendering()
    {
        VariantFixture fixture = new();
        FinalRenderRequest driftedRender = fixture.CloneRenderRequest(
            fixture.SubtitleFree.RenderRequest,
            jobId: fixture.BurnedIn.RenderRequest.JobId);
        var driftedWorkItem = new SubtitleVariantWorkItem(
            fixture.SubtitleFree.DeliveryManifestId,
            driftedRender,
            fixture.SubtitleFree.QualityControlRequest,
            fixture.SubtitleFree.ArtifactEvidence);
        SubtitleVariantRequest request = fixture.CreateRequest(
            subtitleFree: driftedWorkItem);

        SubtitleVariantException exception =
            await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(request));

        Assert.AreEqual(
            SubtitleVariantFailureReason.OutputConflict,
            exception.Reason);
        fixture.AssertNoOperationsAndExpiredPolicies(request);
    }

    [TestMethod]
    public async Task CreateAsync_PreCancelledRequestHasNoSideEffects()
    {
        VariantFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fixture.Service.CreateAsync(
                fixture.Request,
                cancellation.Token));

        Assert.AreEqual(0, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_FirstRenderCancellationReturnsCancelled()
    {
        VariantFixture fixture = new();
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Cancelled);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Cancelled,
            result.Disposition);
        Assert.AreEqual(SubtitleVariantStage.NotStarted, result.LastCompletedStage);
        Assert.IsNull(result.FailureReason);
        Assert.IsFalse(result.RequiresReconciliation);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_FirstRenderReconciliationIsSurfaced()
    {
        VariantFixture fixture = new();
        fixture.RenderDispositions.Enqueue(
            FinalRenderDisposition.ReconciliationRequired);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInRenderFailed,
            result.FailureReason);
        Assert.AreEqual(SubtitleVariantStage.NotStarted, result.LastCompletedStage);
        Assert.IsTrue(result.RequiresReconciliation);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_CancellationAfterBurnedInDeliveryRequiresReconciliation()
    {
        VariantFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        fixture.AfterDelivery = call =>
        {
            if (call == 1)
            {
                cancellation.Cancel();
            }
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request,
            cancellation.Token);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInDelivered,
            result.LastCompletedStage);
        Assert.IsNotNull(result.BurnedInDelivery);
        Assert.IsNull(result.SubtitleFreeRender);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_ConcurrentReuseIsRejectedWithoutInvalidatingOwner()
    {
        VariantFixture fixture = new()
        {
            BlockFirstRender = true,
        };
        Task<SubtitleVariantRunResult> owner = fixture.Service.CreateAsync(
            fixture.Request);
        await fixture.FirstRenderEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        SubtitleVariantException conflict;
        try
        {
            conflict = await Assert.ThrowsExactlyAsync<SubtitleVariantException>(
                () => fixture.Service.CreateAsync(fixture.Request));
            Assert.AreEqual(
                SubtitleVariantFailureReason.ExecutionConflict,
                conflict.Reason);
            Assert.AreEqual(1, fixture.RenderCount);
        }
        finally
        {
            _ = fixture.ReleaseFirstRender.TrySetResult(true);
        }

        SubtitleVariantRunResult ownerResult = await owner.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(
            SubtitleVariantDisposition.Completed,
            ownerResult.Disposition);
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(2, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_FirstRenderFailureStopsWithoutPartialPair()
    {
        VariantFixture fixture = new();
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Failed);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.Failed,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInRenderFailed,
            result.FailureReason);
        Assert.AreEqual(SubtitleVariantStage.NotStarted, result.LastCompletedStage);
        Assert.IsFalse(result.RequiresReconciliation);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fixture.SubtitleFree.RenderRequest.PolicySnapshot.ClaimForRun());
    }

    [TestMethod]
    public async Task CreateAsync_SecondRenderFailureRequiresReconciliation()
    {
        VariantFixture fixture = new();
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Completed);
        fixture.RenderDispositions.Enqueue(FinalRenderDisposition.Failed);

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.SubtitleFreeRenderFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInDelivered,
            result.LastCompletedStage);
        Assert.IsTrue(result.RequiresReconciliation);
        Assert.IsNotNull(result.BurnedInDelivery);
        Assert.IsNull(result.SubtitleFreeQualityControl);
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_QcFailureAfterPromotionRequiresReconciliation()
    {
        VariantFixture fixture = new()
        {
            ThrowOnQualityControlCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInQualityControlFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInRendered,
            result.LastCompletedStage);
        Assert.IsNotNull(result.BurnedInRender);
        Assert.IsNull(result.BurnedInDelivery);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_SecondQcFailurePreservesFirstDelivery()
    {
        VariantFixture fixture = new()
        {
            ThrowOnQualityControlCall = 2,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.SubtitleFreeQualityControlFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.SubtitleFreeRendered,
            result.LastCompletedStage);
        Assert.IsNotNull(result.BurnedInDelivery);
        Assert.IsNotNull(result.SubtitleFreeRender);
        Assert.IsNull(result.SubtitleFreeQualityControl);
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_FirstDeliveryFailureStopsBeforeFreeRender()
    {
        VariantFixture fixture = new()
        {
            ThrowOnDeliveryCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInDeliveryFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInQualityControlled,
            result.LastCompletedStage);
        Assert.IsNull(result.BurnedInDelivery);
        Assert.IsNull(result.SubtitleFreeRender);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_SecondDeliveryFailurePreservesFirstDelivery()
    {
        VariantFixture fixture = new()
        {
            ThrowOnDeliveryCall = 2,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.SubtitleFreeDeliveryFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.SubtitleFreeQualityControlled,
            result.LastCompletedStage);
        Assert.IsNotNull(result.BurnedInDelivery);
        Assert.IsNull(result.SubtitleFreeDelivery);
        Assert.AreEqual(2, fixture.RenderCount);
        Assert.AreEqual(2, fixture.QualityControlCount);
        Assert.AreEqual(2, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsForgedCompletedRenderResult()
    {
        VariantFixture fixture = new()
        {
            ForgeRenderResultOnCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInRenderFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.NotStarted,
            result.LastCompletedStage);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsForgedRenderJobIdentity()
    {
        VariantFixture fixture = new()
        {
            ForgeRenderJobIdentityOnCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInRenderFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.NotStarted,
            result.LastCompletedStage);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(0, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsForgedQualityControlResult()
    {
        VariantFixture fixture = new()
        {
            ForgeQualityControlResultOnCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInQualityControlFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInRendered,
            result.LastCompletedStage);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsForgedDeliveryResult()
    {
        VariantFixture fixture = new()
        {
            ForgeDeliveryResultOnCall = 1,
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            SubtitleVariantFailureReason.BurnedInDeliveryFailed,
            result.FailureReason);
        Assert.AreEqual(
            SubtitleVariantStage.BurnedInQualityControlled,
            result.LastCompletedStage);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(1, fixture.QualityControlCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    [DataRow("render")]
    [DataRow("qualityControl")]
    [DataRow("delivery")]
    public async Task CreateAsync_NullDependencyResultRequiresReconciliation(
        string dependency)
    {
        VariantFixture fixture = dependency switch
        {
            "render" => new VariantFixture
            {
                ReturnNullRenderResultOnCall = 1,
            },
            "qualityControl" => new VariantFixture
            {
                ReturnNullQualityControlResultOnCall = 1,
            },
            "delivery" => new VariantFixture
            {
                ReturnNullDeliveryResultOnCall = 1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(dependency)),
        };

        SubtitleVariantRunResult result = await fixture.Service.CreateAsync(
            fixture.Request);

        Assert.AreEqual(
            SubtitleVariantDisposition.ReconciliationRequired,
            result.Disposition);
        Assert.AreEqual(
            dependency switch
            {
                "render" => SubtitleVariantFailureReason.BurnedInRenderFailed,
                "qualityControl" =>
                    SubtitleVariantFailureReason.BurnedInQualityControlFailed,
                _ => SubtitleVariantFailureReason.BurnedInDeliveryFailed,
            },
            result.FailureReason);
        Assert.AreEqual(
            dependency switch
            {
                "render" => SubtitleVariantStage.NotStarted,
                "qualityControl" => SubtitleVariantStage.BurnedInRendered,
                _ => SubtitleVariantStage.BurnedInQualityControlled,
            },
            result.LastCompletedStage);
        Assert.AreEqual(1, fixture.RenderCount);
        Assert.AreEqual(
            dependency == "render" ? 0 : 1,
            fixture.QualityControlCount);
        Assert.AreEqual(
            dependency == "delivery" ? 1 : 0,
            fixture.DeliveryCount);
    }

    private static ContentDigest Digest(char fill) =>
        new(new string(fill, 64));

    private static FfmpegInputBinding DriftDigest(
        FfmpegInputBinding binding) =>
        new(
            binding.InputId,
            new BinaryVerificationRequest(
                binding.VerificationRequest.ComponentIdentity,
                binding.VerificationRequest.FilePath,
                Digest('0'),
                binding.VerificationRequest.ExpectedByteSize));

    private sealed class VariantFixture
    {
        private static readonly DateTimeOffset CreatedAt =
            new(2046, 6, 7, 8, 9, 10, TimeSpan.Zero);

        internal VariantFixture(int subtitleCueCount = 1)
        {
            if (subtitleCueCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subtitleCueCount));
            }

            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.SubtitleVariantServiceTests",
                Guid.NewGuid().ToString("N"));

            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            ContentDigest assetDigest = Digest('1');
            var asset = new Asset(
                assetId,
                "source.mp4",
                "video/mp4",
                assetDigest,
                100,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal subtitle variant test.",
                    externalPublicationAllowed: false));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Subtitle variant project",
                "Verify exact subtitle variants.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                [asset],
                performers: []);

            var outputRange = new TimeRange(
                default,
                new RationalTime(1, 1));
            OriginRef videoOrigin = OriginRef.FromAsset(assetId);
            var element = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                outputRange,
                videoOrigin);
            CompositionVersion = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                "S-1-5-21-1000-2000-3000-4000",
                CreatedAt,
                "Create subtitle variant composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            Timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                CompositionVersion.Id,
                [new TimelineMapEntry(outputRange, videoOrigin, sourceRange: null)]);
            TimelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(Timeline);
            Template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "Subtitle variant template",
                Digest('2'));
            Layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        FullRect(),
                        LayoutRegionRole.Video),
                    new LayoutRegion(
                        "subtitle",
                        FullRect(),
                        LayoutRegionRole.Subtitle),
                ],
                [new SafeZone("safe-frame", FullRect())]);
            LayoutDigest = Digest('3');

            TimeRange[] cueRanges = Enumerable.Range(0, subtitleCueCount)
                .Select(index => new TimeRange(
                    new RationalTime(index, subtitleCueCount),
                    new RationalTime(1, subtitleCueCount)))
                .ToArray();
            SubtitleCueCandidate[] candidates = cueRanges
                .Select(range => CreateCandidate(
                    assetId,
                    assetDigest,
                    range))
                .ToArray();
            var layoutProfile = new SubtitleLayoutProfile(
                Layout.Id,
                Digest('4'),
                ["safe-frame"],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(static kind =>
                        new SubtitleSoftConstraintWeight(kind, 0))
                    .ToArray());
            var placement = new SubtitlePlacementCandidate(
                EntityId<SubtitlePlacementCandidateIdTag>.New(),
                "subtitle",
                new SubtitleLayoutRect(
                    default,
                    default,
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 1)));
            SubtitleLayoutResolution[] layoutResolutions = candidates
                .Select(candidate => SubtitleLayoutEngine.Resolve(
                    new SubtitleLayoutRequest(
                        candidate,
                        Layout,
                        layoutProfile,
                        [placement],
                        prohibitedRegions: [],
                        faceRegions: [],
                        previousPlacement: null)))
                .ToArray();
            SubtitleCueRevision[] subtitleRevisions = candidates
                .Select((candidate, index) => SubtitleService.CreateInitial(
                    CompositionVersion,
                    candidate,
                    layoutResolutions[index],
                    EntityId<CueIdTag>.New(),
                    EntityId<SubtitleCueRevisionIdTag>.New(),
                    cueRanges[index],
                    "S-1-5-21-1000-2000-3000-4000",
                    CreatedAt.AddMinutes(1),
                    "Create subtitle variant cue.").CurrentRevision)
                .ToArray();
            SubtitleRevisions = Array.AsReadOnly(subtitleRevisions);
            SubtitleRevision = subtitleRevisions[0];
            DisplayProfile = new SubtitleDisplayProfile(
                Digest('5'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve);
            SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(
                DisplayProfile,
                subtitleRevisions.Select(static revision =>
                    SubtitleDisplayMapper.MapSource(revision.Snapshot))
                    .ToArray(),
                selectedCueIds: []);
            SubtitleFileCue[] exportCues = subtitleRevisions.Select(revision =>
            {
                SubtitleDisplayedCue displayed = display.DisplayedCues.Single(
                    candidate => candidate.Cue.Id
                        == revision.Snapshot.Cue.Id);
                return SubtitleDisplayMapper.MapForExport(
                    revision.Snapshot,
                    displayed);
            }).ToArray();
            BurnedInExport = new SubtitleExportSpecification(
                SubtitleFileFormat.WebVtt);
            SubtitleExportFile[] burnedSubtitles = exportCues
                .Select(static cue => WebVttSubtitleWriter.Write([cue]))
                .ToArray();

            BurnedInProfile = CreateProfile(
                "burned-in",
                Digest('6'),
                subtitleBurnIn: true);
            SubtitleFreeProfile = CreateProfile(
                "subtitle-free",
                Digest('7'),
                subtitleBurnIn: false);

            EntityId<RenderInputIdTag> videoInputId =
                EntityId<RenderInputIdTag>.From(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"));
            EntityId<RenderInputIdTag> subtitleInputId =
                EntityId<RenderInputIdTag>.From(
                    Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
            var videoInput = new RenderGraphInput(
                videoInputId,
                RenderInputKind.Video,
                assetDigest,
                asset.ByteSize,
                videoOrigin);
            RenderGraphInput[] subtitleInputs = burnedSubtitles
                .Select((subtitle, index) => new RenderGraphInput(
                    index == 0
                        ? subtitleInputId
                        : EntityId<RenderInputIdTag>.New(),
                    RenderInputKind.Subtitle,
                    subtitle.ContentDigest,
                    subtitle.ByteSize,
                    subtitleRevisions[index].Snapshot.TextOrigin))
                .ToArray();
            var font = new RenderFontEvidence("Test Sans", Digest('8'));
            RenderNode videoNode = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                videoInputId,
                outputRange,
                videoOrigin,
                sourceRange: null,
                "main");
            var subtitleNodes = new List<RenderNode>(subtitleCueCount);
            EntityId<RenderNodeIdTag> subtitleTarget = videoNode.Id;
            for (int index = 0; index < subtitleCueCount; index++)
            {
                SubtitleCueRevision revision = subtitleRevisions[index];
                RenderNode subtitleNode = RenderNode.Subtitle(
                    EntityId<RenderNodeIdTag>.New(),
                    subtitleInputs[index].Id,
                    subtitleTarget,
                    revision.Snapshot.Cue,
                    revision.Snapshot.TextOrigin);
                subtitleNodes.Add(subtitleNode);
                subtitleTarget = subtitleNode.Id;
            }

            RenderNode burnedOutput = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [subtitleNodes[^1].Id],
                outputRange);
            RenderNode freeOutput = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [videoNode.Id],
                outputRange);
            BurnedInGraph = CreateGraph(
                BurnedInProfile,
                [videoInput, .. subtitleInputs],
                [font],
                [videoNode, .. subtitleNodes, burnedOutput],
                burnedOutput.Id);
            SubtitleFreeGraph = CreateGraph(
                SubtitleFreeProfile,
                [videoInput],
                [font],
                [videoNode, freeOutput],
                freeOutput.Id);
            BurnedInContext = CreateContext(
                BurnedInProfile,
                candidates.Select(static candidate => candidate.SourceSpan)
                    .ToArray(),
                [
                    new RenderArtifactEvidence(
                        videoInputId,
                        assetDigest,
                        asset.ByteSize),
                    .. subtitleInputs.Select(static input =>
                        new RenderArtifactEvidence(
                            input.Id,
                            input.ContentDigest,
                            input.ByteSize)),
                ],
                [font]);
            SubtitleFreeContext = CreateContext(
                SubtitleFreeProfile,
                candidates.Select(static candidate => candidate.SourceSpan)
                    .ToArray(),
                [new RenderArtifactEvidence(
                    videoInputId,
                    assetDigest,
                    asset.ByteSize)],
                [font]);

            StoredApproval approval = new(
                EntityId<ApprovalIdTag>.New(),
                Project.Id,
                CompositionVersion.Id,
                CompositionVersion.ContentDigest,
                "S-1-5-21-1000-2000-3000-4001",
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(2),
                "Approve exact subtitle variants.");
            Approvals = [approval];
            var toolVerification = new BinaryVerificationRequest(
                "ffmpeg",
                Path.Combine(RootPath, "ffmpeg.exe"),
                Digest('9'),
                10);
            var tool = new FfmpegToolBinding(toolVerification);
            var videoBinding = new FfmpegInputBinding(
                videoInputId,
                new BinaryVerificationRequest(
                    "input.video",
                    Path.Combine(RootPath, "source.mp4"),
                    assetDigest,
                    asset.ByteSize));
            FfmpegInputBinding[] subtitleBindings = subtitleInputs
                .Select((input, index) => new FfmpegInputBinding(
                    input.Id,
                    new BinaryVerificationRequest(
                        string.Concat("input.subtitle.", index + 1),
                        Path.Combine(
                            RootPath,
                            string.Concat("burn-", index + 1, ".vtt")),
                        input.ContentDigest,
                        input.ByteSize)))
                .ToArray();

            EntityId<ArtifactIdTag> burnedVideoId =
                EntityId<ArtifactIdTag>.New();
            EntityId<ArtifactIdTag> freeVideoId =
                EntityId<ArtifactIdTag>.New();
            ManifestArtifact burnedVideo = CreateVideoArtifact(
                Digest('a'),
                301,
                "burned-in.mp4");
            ManifestArtifact freeVideo = CreateVideoArtifact(
                Digest('b'),
                287,
                "subtitle-free.mp4");
            RenderManifest burnedManifest = CreateManifest(
                BurnedInProfile,
                [
                    new RenderInputEvidence("source-video", assetDigest),
                    .. burnedSubtitles.Select((subtitle, index) =>
                        new RenderInputEvidence(
                            string.Concat("burned-subtitle-", index + 1),
                            subtitle.ContentDigest)),
                ],
                [font],
                burnedVideo);
            RenderManifest freeManifest = CreateManifest(
                SubtitleFreeProfile,
                [new RenderInputEvidence("source-video", assetDigest)],
                [font],
                freeVideo);
            var subtitleEvidence = new OutputQcSubtitleEvidence(
                layoutProfile.VersionDigest,
                DisplayProfile.VersionDigest,
                display,
                subtitleRevisions.Select(static revision =>
                    new OutputQcSubtitleLayoutEvidence(
                        revision.Snapshot.Cue.Id,
                        revision.Snapshot.LayoutResolution))
                    .ToArray());

            FinalRenderRequest burnedRender = CreateRenderRequest(
                EntityId<JobIdTag>.New(),
                burnedVideoId,
                BurnedInGraph,
                BurnedInContext,
                tool,
                [videoBinding, .. subtitleBindings],
                "burned-in.mp4",
                burnedVideo);
            FinalRenderRequest freeRender = CreateRenderRequest(
                EntityId<JobIdTag>.New(),
                freeVideoId,
                SubtitleFreeGraph,
                SubtitleFreeContext,
                tool,
                [videoBinding],
                "subtitle-free.mp4",
                freeVideo);
            var burnedQc = new OutputQcRequest(
                burnedVideoId,
                burnedManifest,
                BurnedInGraph,
                BurnedInContext,
                subtitleEvidence,
                TimeSpan.FromMinutes(1));
            var freeQc = new OutputQcRequest(
                freeVideoId,
                freeManifest,
                SubtitleFreeGraph,
                SubtitleFreeContext,
                subtitleEvidence: null,
                TimeSpan.FromMinutes(1));

            SubtitleExportSpecification sidecarExport = new(
                SubtitleFileFormat.SubRip);
            SubtitleExportFile sidecarFile = SubRipSubtitleWriter.Write(
                exportCues);
            var sidecarArtifact = new ManifestArtifact(
                "subtitle",
                "application/x-subrip",
                "ja",
                sidecarFile.ByteSize,
                sidecarFile.ContentDigest,
                string.Concat(
                    "projects/",
                    Project.Id.ToString(),
                    "/deliveries/subtitle-free.ja.srt"));
            Sidecar = new SubtitleVariantSidecarRequest(
                EntityId<ArtifactIdTag>.New(),
                sidecarArtifact,
                sidecarExport);
            BurnedIn = new SubtitleVariantWorkItem(
                EntityId<DeliveryManifestIdTag>.New(),
                burnedRender,
                burnedQc,
                [new(burnedVideoId, burnedVideo)]);
            SubtitleFree = new SubtitleVariantWorkItem(
                EntityId<DeliveryManifestIdTag>.New(),
                freeRender,
                freeQc,
                [
                    new(freeVideoId, freeVideo),
                    new(Sidecar.ArtifactId, Sidecar.Artifact),
                ]);
            Request = CreateRequest();
            Service = new SubtitleVariantService(
                RunRenderAsync,
                RunQualityControlAsync,
                CreateDeliveryAsync);
        }

        internal string RootPath { get; }

        internal Project Project { get; }

        internal CompositionVersion CompositionVersion { get; }

        internal TimelineMap Timeline { get; }

        internal ContentDigest TimelineDigest { get; }

        internal TemplateReference Template { get; }

        internal LayoutSpec Layout { get; }

        internal ContentDigest LayoutDigest { get; }

        internal SubtitleCueRevision SubtitleRevision { get; }

        internal IReadOnlyList<SubtitleCueRevision> SubtitleRevisions { get; }

        internal SubtitleDisplayProfile DisplayProfile { get; }

        internal SubtitleExportSpecification BurnedInExport { get; }

        internal RenderProfile BurnedInProfile { get; }

        internal RenderProfile SubtitleFreeProfile { get; }

        internal RenderGraph BurnedInGraph { get; }

        internal RenderGraph SubtitleFreeGraph { get; }

        internal RenderGraphValidationContext BurnedInContext { get; }

        internal RenderGraphValidationContext SubtitleFreeContext { get; }

        internal IReadOnlyList<StoredApproval> Approvals { get; }

        internal SubtitleVariantSidecarRequest Sidecar { get; }

        internal SubtitleVariantWorkItem BurnedIn { get; }

        internal SubtitleVariantWorkItem SubtitleFree { get; }

        internal SubtitleVariantRequest Request { get; }

        internal SubtitleVariantService Service { get; }

        internal Queue<FinalRenderDisposition> RenderDispositions { get; } = [];

        internal List<string> Operations { get; } = [];

        internal int ThrowOnQualityControlCall { get; init; }

        internal int ThrowOnDeliveryCall { get; init; }

        internal int ForgeRenderResultOnCall { get; init; }

        internal int ForgeRenderJobIdentityOnCall { get; init; }

        internal int ForgeQualityControlResultOnCall { get; init; }

        internal int ForgeDeliveryResultOnCall { get; init; }

        internal int ReturnNullRenderResultOnCall { get; init; }

        internal int ReturnNullQualityControlResultOnCall { get; init; }

        internal int ReturnNullDeliveryResultOnCall { get; init; }

        internal bool ForceSharedDeliveryEvidenceDigest { get; init; }

        internal bool BlockFirstRender { get; init; }

        internal Action<int>? AfterDelivery { get; set; }

        internal TaskCompletionSource<bool> FirstRenderEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ReleaseFirstRender { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RenderCount { get; private set; }

        internal int QualityControlCount { get; private set; }

        internal int DeliveryCount { get; private set; }

        internal SubtitleVariantRequest CreateRequest(
            IReadOnlyList<SubtitleCueRevision>? revisions = null,
            SubtitleVariantWorkItem? burnedIn = null,
            SubtitleVariantWorkItem? subtitleFree = null,
            SubtitleVariantSidecarRequest? sidecar = null,
            bool includeSidecar = true) =>
            new(
                Project,
                CompositionVersion,
                revisions ?? SubtitleRevisions,
                DisplayProfile,
                selectedCueIds: [],
                BurnedInExport,
                burnedIn ?? BurnedIn,
                subtitleFree ?? SubtitleFree,
                includeSidecar ? sidecar ?? Sidecar : null);

        internal SubtitleVariantWorkItem CreateSubtitleFreeWithoutSidecar() =>
            new(
                SubtitleFree.DeliveryManifestId,
                SubtitleFree.RenderRequest,
                SubtitleFree.QualityControlRequest,
                SubtitleFree.ArtifactEvidence
                    .Where(binding => binding.Key != Sidecar.ArtifactId)
                    .ToArray());

        internal FinalRenderRequest CloneRenderRequest(
            FinalRenderRequest source,
            EntityId<JobIdTag>? jobId = null,
            IReadOnlyList<FfmpegInputBinding>? inputs = null,
            IReadOnlyList<StoredApproval>? approvals = null)
        {
            var policy = new FinalRenderPolicySnapshot(
                source.PolicySnapshot.Digest,
                source.PolicySnapshot.CurrentDigest,
                source.ProjectId,
                source.ApprovedCompositionVersion,
                source.Graph,
                source.ValidationContext);
            return new FinalRenderRequest(
                jobId ?? source.JobId,
                source.ProjectId,
                source.OutputArtifactId,
                source.ApprovedCompositionVersion,
                source.ApprovedCompositionDigest,
                approvals ?? source.ApprovalEvidence,
                policy,
                source.Graph,
                source.ValidationContext,
                source.Tool,
                inputs ?? source.Inputs,
                source.StagingOutputPath,
                source.OutputRelativePath,
                source.WorkingDirectory,
                source.Timeout,
                source.ExpectedOutputDigest,
                source.ExpectedOutputByteSize,
                source.OutputCreatedAt);
        }

        internal void AssertNoOperationsAndExpiredPolicies(
            SubtitleVariantRequest request)
        {
            Assert.AreEqual(0, RenderCount);
            Assert.AreEqual(0, QualityControlCount);
            Assert.AreEqual(0, DeliveryCount);
            Assert.AreEqual(0, Operations.Count);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => request.BurnedIn.RenderRequest.PolicySnapshot.ClaimForRun());
            Assert.ThrowsExactly<InvalidOperationException>(
                () => request.SubtitleFree.RenderRequest.PolicySnapshot.ClaimForRun());
        }

        private async Task<FinalRenderRunResult> RunRenderAsync(
            FinalRenderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.PolicySnapshot.ClaimForRun();
            RenderCount++;
            bool burnedIn = ReferenceEquals(
                request.Graph,
                BurnedInGraph);
            Operations.Add(burnedIn
                ? "render:burnedIn"
                : "render:subtitleFree");
            if (BlockFirstRender && RenderCount == 1)
            {
                _ = FirstRenderEntered.TrySetResult(true);
                await ReleaseFirstRender.Task.WaitAsync(cancellationToken);
            }

            FinalRenderDisposition disposition = RenderDispositions.Count == 0
                ? FinalRenderDisposition.Completed
                : RenderDispositions.Dequeue();
            if (ReturnNullRenderResultOnCall == RenderCount)
            {
                return null!;
            }

            FinalRenderRequest resultRequest =
                ForgeRenderResultOnCall == RenderCount
                    ? burnedIn
                        ? SubtitleFree.RenderRequest
                        : BurnedIn.RenderRequest
                    : request;
            return CreateRenderResult(
                resultRequest,
                disposition,
                ForgeRenderJobIdentityOnCall == RenderCount
                    ? EntityId<JobIdTag>.New()
                    : null);
        }

        private Task<OutputQcResult> RunQualityControlAsync(
            OutputQcRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QualityControlCount++;
            bool burnedIn = ReferenceEquals(request.Graph, BurnedInGraph);
            Operations.Add(burnedIn ? "qc:burnedIn" : "qc:subtitleFree");
            if (ThrowOnQualityControlCall == QualityControlCount)
            {
                throw new InvalidOperationException("Synthetic QC failure.");
            }

            if (ReturnNullQualityControlResultOnCall == QualityControlCount)
            {
                return Task.FromResult<OutputQcResult>(null!);
            }

            OutputQcRequest resultRequest =
                ForgeQualityControlResultOnCall == QualityControlCount
                    ? burnedIn
                        ? SubtitleFree.QualityControlRequest
                        : BurnedIn.QualityControlRequest
                    : request;
            return Task.FromResult(CreateQualityControlResult(resultRequest));
        }

        private Task<DeliveryCreationResult> CreateDeliveryAsync(
            SubtitleVariantWorkItem item,
            Project project,
            CompositionVersion compositionVersion,
            OutputQcResult qualityControl,
            SubtitleDeliveryVariantEvidence variant,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreSame(Project, project);
            Assert.AreSame(CompositionVersion, compositionVersion);
            Assert.AreEqual(item.RenderRequest.OutputArtifactId, qualityControl.ArtifactId);
            DeliveryCount++;
            Operations.Add(string.Concat("delivery:", variant.Kind.ToString()));
            if (ThrowOnDeliveryCall == DeliveryCount)
            {
                throw new InvalidOperationException(
                    "Synthetic delivery failure.");
            }

            if (ReturnNullDeliveryResultOnCall == DeliveryCount)
            {
                return Task.FromResult<DeliveryCreationResult>(null!);
            }

            var manifest = new DeliveryManifest(
                ForgeDeliveryResultOnCall == DeliveryCount
                    ? EntityId<DeliveryManifestIdTag>.New()
                    : item.DeliveryManifestId,
                item.QualityControlRequest.RenderManifest,
                item.ArtifactEvidence.Select(static binding => binding.Value)
                    .ToArray(),
                variant);
            var result = new DeliveryCreationResult(
                manifest,
                usedSourceSpans: [],
                Digest('c'),
                Digest('d'),
                ForceSharedDeliveryEvidenceDigest
                    ? Digest('e')
                    : variant.Kind == SubtitleDeliveryVariantKind.BurnedIn
                    ? Digest('e')
                    : Digest('f'));
            AfterDelivery?.Invoke(DeliveryCount);
            return Task.FromResult(result);
        }

        private RenderProfile CreateProfile(
            string name,
            ContentDigest digest,
            bool subtitleBurnIn) =>
            new(
                EntityId<RenderProfileIdTag>.New(),
                name,
                digest,
                1920,
                1080,
                30,
                1,
                Layout.Id,
                Template.Id,
                Template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn);

        private RenderGraph CreateGraph(
            RenderProfile profile,
            IReadOnlyList<RenderGraphInput> inputs,
            IReadOnlyList<RenderFontEvidence> fonts,
            IReadOnlyList<RenderNode> nodes,
            EntityId<RenderNodeIdTag> outputNodeId) =>
            new(
                EntityId<RenderGraphIdTag>.New(),
                CompositionVersion.Id,
                CompositionVersion.ContentDigest,
                Timeline.Id,
                TimelineDigest,
                profile.Id,
                profile.ProfileDigest,
                Layout.Id,
                LayoutDigest,
                Template,
                inputs,
                fonts,
                nodes,
                outputNodeId);

        private RenderGraphValidationContext CreateContext(
            RenderProfile profile,
            IReadOnlyList<SourceSpan> sourceSpans,
            IReadOnlyList<RenderArtifactEvidence> artifacts,
            IReadOnlyList<RenderFontEvidence> fonts) =>
            new(
                Project,
                CompositionVersion,
                Timeline,
                TimelineDigest,
                profile,
                Layout,
                LayoutDigest,
                Template,
                sourceSpans,
                editorialRecordIds: [],
                artifacts,
                fonts);

        private ManifestArtifact CreateVideoArtifact(
            ContentDigest digest,
            long byteSize,
            string relativePath) =>
            new(
                "video",
                "video/mp4",
                language: null,
                byteSize,
                digest,
                string.Concat(
                    "projects/",
                    Project.Id.ToString(),
                    "/renders/",
                    relativePath));

        private RenderManifest CreateManifest(
            RenderProfile profile,
            IReadOnlyList<RenderInputEvidence> inputs,
            IReadOnlyList<RenderFontEvidence> fonts,
            ManifestArtifact output) =>
            new(
                EntityId<RenderManifestIdTag>.New(),
                CompositionVersion,
                Timeline,
                TimelineDigest,
                profile,
                Digest('0'),
                "1.0.0",
                Digest('9'),
                inputs,
                [new RenderToolEvidence(
                    "ffmpeg",
                    "8.0",
                    "release",
                    Digest('9'),
                    containerImageDigest: null)],
                models: [],
                fonts,
                [output]);

        private FinalRenderRequest CreateRenderRequest(
            EntityId<JobIdTag> jobId,
            EntityId<ArtifactIdTag> artifactId,
            RenderGraph graph,
            RenderGraphValidationContext context,
            FfmpegToolBinding tool,
            IReadOnlyList<FfmpegInputBinding> inputs,
            string relativePath,
            ManifestArtifact output)
        {
            var policy = new FinalRenderPolicySnapshot(
                ContentDigest.Compute(Encoding.UTF8.GetBytes(graph.Id.ToString())),
                ContentDigest.Compute(Encoding.UTF8.GetBytes(graph.Id.ToString())),
                Project.Id,
                CompositionVersion,
                graph,
                context);
            return new FinalRenderRequest(
                jobId,
                Project.Id,
                artifactId,
                CompositionVersion,
                CompositionVersion.ContentDigest,
                Approvals,
                policy,
                graph,
                context,
                tool,
                inputs,
                Path.Combine(RootPath, relativePath),
                relativePath,
                RootPath,
                TimeSpan.FromMinutes(1),
                output.Sha256,
                output.ByteSize,
                CreatedAt.AddMinutes(3));
        }

        private static FinalRenderRunResult CreateRenderResult(
            FinalRenderRequest request,
            FinalRenderDisposition disposition,
            EntityId<JobIdTag>? resultJobId = null)
        {
            JobState state = disposition switch
            {
                FinalRenderDisposition.Completed or
                FinalRenderDisposition.Duplicate => JobState.Succeeded,
                FinalRenderDisposition.Cancelled => JobState.Cancelled,
                _ => JobState.Failed,
            };
            var attemptId = EntityId<JobAttemptIdTag>.New();
            var snapshot = new JobSnapshot(
                resultJobId ?? request.JobId,
                state,
                attemptId,
                currentAttemptNumber: 1,
                revision: 1,
                CreatedAt.AddMinutes(4),
                completedUnits: disposition is
                    FinalRenderDisposition.Completed or
                    FinalRenderDisposition.Duplicate ? 5 : null,
                totalUnits: 5,
                progressRatio: disposition is
                    FinalRenderDisposition.Completed or
                    FinalRenderDisposition.Duplicate ? 1m : null,
                lastProgressAt: CreatedAt.AddMinutes(4),
                lastHeartbeatAt: CreatedAt.AddMinutes(4),
                errorCode: state == JobState.Failed ? "synthetic" : null,
                retryability: state == JobState.Failed
                    ? JobRetryability.Retryable
                    : JobRetryability.NotApplicable,
                cancellationRequested:
                    disposition == FinalRenderDisposition.Cancelled);
            var job = new JobCoordinatorRunResult(
                attemptId,
                sequenceNumber: 1,
                snapshot);
            FinalRenderFailureReason? failureReason = disposition switch
            {
                FinalRenderDisposition.Failed =>
                    FinalRenderFailureReason.ProcessFailed,
                FinalRenderDisposition.ReconciliationRequired =>
                    FinalRenderFailureReason.ReconciliationRequired,
                _ => null,
            };
            FinalRenderOutput? output = disposition is
                FinalRenderDisposition.Completed or
                FinalRenderDisposition.Duplicate
                ? new FinalRenderOutput(
                    request.OutputArtifactId,
                    request.ExpectedOutputDigest,
                    request.ExpectedOutputByteSize)
                : null;
            return new FinalRenderRunResult(
                job,
                output is null
                    ? FinalRenderStage.NotStarted
                    : FinalRenderStage.ArtifactPromoted,
                disposition,
                failureReason,
                output);
        }

        private static OutputQcResult CreateQualityControlResult(
            OutputQcRequest request)
        {
            ManifestArtifact output = request.RenderManifest.Outputs.Single();
            var artifact = new StoredArtifact(
                request.OutputArtifactId,
                request.RenderManifest.ProjectId,
                ArtifactKind.Render,
                output.StorageReference,
                output.Sha256,
                output.ByteSize,
                CreatedAt.AddMinutes(3));
            var timing = new OutputQcStreamTiming(
                streamIndex: 0,
                [new OutputQcTimingEntry(
                    sequence: 0,
                    presentationTimestampTicks: 0,
                    durationTicks: 1)],
                new RationalTime(1, 1),
                presentationStart: default,
                presentationEnd: new RationalTime(1, 1),
                Digest('1'));
            var decode = new OutputQcDecodeEvidence(TimeSpan.Zero);
            var luma = new OutputQcLumaMeasurements(
                [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                Digest('2'));
            return new OutputQcResult(
                artifact,
                request,
                Digest('3'),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                timing,
                audioTiming: null,
                subtitleTiming: null,
                decode,
                luma,
                audioMeasurements: null);
        }

        private static SubtitleCueCandidate CreateCandidate(
            EntityId<AssetIdTag> assetId,
            ContentDigest assetDigest,
            TimeRange sourceRange)
        {
            EntityId<TranscriptRevisionIdTag> revisionId =
                EntityId<TranscriptRevisionIdTag>.New();
            EntityId<TranscriptTokenIdTag> tokenId =
                EntityId<TranscriptTokenIdTag>.New();
            var token = new Token(
                tokenId,
                "字幕です。",
                sourceRange,
                recognitionConfidence: null);
            var sentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                token.Text,
                [token]);
            EntityId<SourceSpanIdTag> sourceSpanId =
                EntityId<SourceSpanIdTag>.New();
            var utterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                sourceSpanId,
                EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker",
                token.Text,
                sourceRange,
                recognitionConfidence: null,
                [sentence]);
            var revision = new TranscriptRevision(
                revisionId,
                EntityId<TranscriptRevisionIdTag>.New(),
                TranscriptChangeSource.Human,
                CreatedAt,
                [utterance]);
            var sourceSpan = new SourceSpan(
                sourceSpanId,
                assetId,
                "audio:1",
                sourceRange,
                assetDigest,
                revisionId,
                [tokenId]);
            ContentDigest revisionDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(
                    TranscriptSnapshotJson.WriteRevision(revision)));
            return SubtitleSegmenter.Segment(
                    revision,
                    revisionDigest,
                    [sourceSpan])
                .Candidates
                .Single();
        }

        private static NormalizedRect FullRect() =>
            new(
                default,
                default,
                new RationalNumber(1, 1),
                new RationalNumber(1, 1));
    }
}