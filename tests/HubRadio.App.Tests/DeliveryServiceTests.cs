using System.Reflection;
using HubRadio.App.Delivery;
using HubRadio.App.Reviews;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class DeliveryServiceTests
{
    [TestMethod]
    public async Task CreateAsync_BindsArtifactsApprovalsQcAndUsedSpansToOneVersion()
    {
        DeliveryFixture fixture = new();

        DeliveryCreationResult result = await fixture.CreateAsync();

        Assert.AreEqual(fixture.ManifestId, result.Manifest.Id);
        Assert.AreEqual(fixture.Project.Id, result.Manifest.ProjectId);
        Assert.AreEqual(fixture.Version.Id, result.Manifest.CompositionVersionId);
        Assert.AreEqual(
            fixture.Version.ContentDigest,
            result.Manifest.CompositionVersionDigest);
        Assert.AreEqual(
            fixture.RenderManifest.Id,
            result.Manifest.RenderManifestId);
        CollectionAssert.AreEqual(
            new[] { "subtitle", "video" },
            result.Manifest.Artifacts
                .Select(static artifact => artifact.ArtifactType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { fixture.UsedSourceSpan.Id },
            result.UsedSourceSpans.Select(static span => span.Id).ToArray());
        Assert.AreNotEqual(
            result.UsedSourceSpansDigest,
            result.ApprovalEvidenceDigest);
        Assert.AreNotEqual(result.ApprovalEvidenceDigest, result.EvidenceDigest);
        Assert.AreEqual(1, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(2, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_DoesNotInventOptionalArtifactRequirements()
    {
        DeliveryFixture fixture = new();
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact> video =
            fixture.ArtifactEvidence.Single(pair =>
                pair.Key == fixture.VideoArtifactId);

        DeliveryCreationResult result = await fixture.CreateAsync(
            artifactEvidence: [video]);

        Assert.AreEqual(1, result.Manifest.Artifacts.Count);
        Assert.AreEqual("video", result.Manifest.Artifacts[0].ArtifactType);
        Assert.AreEqual(2, fixture.RegisteredArtifacts.Count);
        Assert.AreEqual(1, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_BindsExplicitSubtitleVariantIntoManifestAndEvidenceDigest()
    {
        DeliveryFixture fixture = new();
        DeliveryCreationResult legacy = await fixture.CreateAsync();
        var variant = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.SubtitleFree,
            fixture.Graph.Id,
            Digest('e'),
            Digest('f'),
            fixture.SubtitleManifestArtifact);

        DeliveryCreationResult explicitResult = await fixture.Service.CreateAsync(
            fixture.ManifestId,
            fixture.Project,
            fixture.Version,
            fixture.RenderManifest,
            fixture.ValidationContext,
            fixture.QualityControl,
            fixture.Approvals,
            fixture.ArtifactEvidence,
            variant);

        Assert.AreSame(variant, explicitResult.Manifest.SubtitleVariant);
        Assert.AreEqual(
            SubtitleDeliveryVariantKind.SubtitleFree,
            explicitResult.Manifest.SubtitleVariant!.Kind);
        Assert.AreEqual(
            fixture.SubtitleManifestArtifact,
            explicitResult.Manifest.SubtitleVariant.SidecarArtifact);
        Assert.AreNotEqual(legacy.EvidenceDigest, explicitResult.EvidenceDigest);
        Assert.AreEqual(
            legacy.ApprovalEvidenceDigest,
            explicitResult.ApprovalEvidenceDigest);
        Assert.AreEqual(
            legacy.UsedSourceSpansDigest,
            explicitResult.UsedSourceSpansDigest);
    }

    [TestMethod]
    public async Task CreateAsync_AcceptsBurnedInVariantWithMatchingQcEvidence()
    {
        DeliveryFixture fixture = new();
        (
            RenderManifest renderManifest,
            RenderGraphValidationContext validationContext,
            OutputQcResult qualityControl,
            SubtitleDeliveryVariantEvidence variant) =
            fixture.CreateBurnedInVariantCase();

        DeliveryCreationResult result = await fixture.Service.CreateAsync(
            EntityId<DeliveryManifestIdTag>.New(),
            fixture.Project,
            fixture.Version,
            renderManifest,
            validationContext,
            qualityControl,
            fixture.Approvals,
            [new(fixture.VideoArtifactId, fixture.VideoManifestArtifact)],
            variant);

        Assert.AreSame(variant, result.Manifest.SubtitleVariant);
        Assert.AreEqual(
            SubtitleDeliveryVariantKind.BurnedIn,
            result.Manifest.SubtitleVariant!.Kind);
        Assert.IsNull(result.Manifest.SubtitleVariant.SidecarArtifact);
        Assert.AreEqual(
            variant.DisplayProfileDigest,
            qualityControl.SubtitleDisplayProfileDigest);
        CollectionAssert.Contains(
            qualityControl.RequiredHumanReviewAreas.ToArray(),
            OutputQcReviewArea.SubtitlePresentation);
        Assert.AreEqual(1, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(1, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsSubtitleVariantGraphAndPolarityDrift()
    {
        DeliveryFixture fixture = new();
        var wrongGraph = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.SubtitleFree,
            EntityId<RenderGraphIdTag>.New(),
            Digest('e'),
            Digest('f'),
            fixture.SubtitleManifestArtifact);

        DeliveryException graphException =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.Service.CreateAsync(
                    EntityId<DeliveryManifestIdTag>.New(),
                    fixture.Project,
                    fixture.Version,
                    fixture.RenderManifest,
                    fixture.ValidationContext,
                    fixture.QualityControl,
                    fixture.Approvals,
                    fixture.ArtifactEvidence,
                    wrongGraph));

        Assert.AreEqual(
            DeliveryFailureReason.RenderEvidenceMismatch,
            graphException.Reason);
        Assert.AreEqual(0, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);

        var wrongPolarity = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.BurnedIn,
            fixture.Graph.Id,
            Digest('e'),
            Digest('f'),
            sidecarArtifact: null);
        DeliveryException polarityException =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.Service.CreateAsync(
                    EntityId<DeliveryManifestIdTag>.New(),
                    fixture.Project,
                    fixture.Version,
                    fixture.RenderManifest,
                    fixture.ValidationContext,
                    fixture.QualityControl,
                    fixture.Approvals,
                    [new(
                        fixture.VideoArtifactId,
                        fixture.VideoManifestArtifact)],
                    wrongPolarity));

        Assert.AreEqual(
            DeliveryFailureReason.RenderEvidenceMismatch,
            polarityException.Reason);
        Assert.AreEqual(0, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_IsDeterministicAcrossCallerCollectionOrder()
    {
        DeliveryFixture fixture = new();
        StoredApproval secondApproval = fixture.CreateApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.CreatedAt.AddMinutes(2),
            "Approve the same immutable delivery version again.");
        StoredApproval[] approvals = [fixture.Approval, secondApproval];

        DeliveryCreationResult forward = await fixture.CreateAsync(
            approvalEvidence: approvals,
            artifactEvidence: fixture.ArtifactEvidence);
        DeliveryCreationResult reverse = await fixture.CreateAsync(
            approvalEvidence: approvals.Reverse().ToArray(),
            artifactEvidence: fixture.ArtifactEvidence.Reverse().ToArray());

        CollectionAssert.AreEqual(
            forward.Manifest.Artifacts.ToArray(),
            reverse.Manifest.Artifacts.ToArray());
        CollectionAssert.AreEqual(
            forward.UsedSourceSpans.ToArray(),
            reverse.UsedSourceSpans.ToArray());
        Assert.AreEqual(
            forward.UsedSourceSpansDigest,
            reverse.UsedSourceSpansDigest);
        Assert.AreEqual(
            forward.ApprovalEvidenceDigest,
            reverse.ApprovalEvidenceDigest);
        Assert.AreEqual(forward.EvidenceDigest, reverse.EvidenceDigest);
    }

    [TestMethod]
    public async Task CreateAsync_IsDeterministicAcrossRenderToolOrder()
    {
        DeliveryFixture fixture = new();
        var firstTool = new RenderToolEvidence(
            "renderer",
            "1.0",
            "release",
            Digest('d'),
            Digest('e'));
        var secondTool = new RenderToolEvidence(
            "renderer",
            "1.0",
            "release",
            Digest('d'),
            Digest('f'));
        EntityId<RenderManifestIdTag> renderManifestId =
            EntityId<RenderManifestIdTag>.New();
        RenderManifest forwardRender = fixture.CreateRenderManifest(
            id: renderManifestId,
            tools: [firstTool, secondTool]);
        RenderManifest reverseRender = fixture.CreateRenderManifest(
            id: renderManifestId,
            tools: [secondTool, firstTool]);
        OutputQcResult forwardQualityControl = fixture.CreateQualityControl(
            fixture.RegisteredArtifacts[fixture.VideoArtifactId],
            forwardRender);
        OutputQcResult reverseQualityControl = fixture.CreateQualityControl(
            fixture.RegisteredArtifacts[fixture.VideoArtifactId],
            reverseRender);

        DeliveryCreationResult forward = await fixture.CreateAsync(
            renderManifest: forwardRender,
            qualityControl: forwardQualityControl);
        DeliveryCreationResult reverse = await fixture.CreateAsync(
            renderManifest: reverseRender,
            qualityControl: reverseQualityControl);

        Assert.AreEqual(forward.EvidenceDigest, reverse.EvidenceDigest);
    }

    [TestMethod]
    public async Task CreateAsync_BindsUsedSpanDigestToCompositionVersion()
    {
        DeliveryFixture fixture = new();
        CompositionVersion otherVersion = fixture.CreateVersion(
            EntityId<CompositionVersionIdTag>.New());
        var otherTimeline = new TimelineMap(
            EntityId<TimelineMapIdTag>.New(),
            otherVersion.Id,
            fixture.TimelineMap.Entries);
        ContentDigest otherTimelineDigest =
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                otherTimeline);
        var otherRender = new RenderManifest(
            EntityId<RenderManifestIdTag>.New(),
            otherVersion,
            otherTimeline,
            otherTimelineDigest,
            fixture.Profile,
            fixture.RenderManifest.ConfigurationDigest,
            fixture.RenderManifest.ApplicationVersion,
            fixture.RenderManifest.ApplicationDigest,
            fixture.RenderManifest.Inputs,
            fixture.RenderManifest.Tools,
            fixture.RenderManifest.Models,
            fixture.RenderManifest.Fonts,
            fixture.RenderManifest.Outputs);
        var otherGraph = new RenderGraph(
            EntityId<RenderGraphIdTag>.New(),
            otherVersion.Id,
            otherVersion.ContentDigest,
            otherTimeline.Id,
            otherTimelineDigest,
            fixture.Graph.ProfileId,
            fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId,
            fixture.Graph.LayoutSpecDigest,
            fixture.Graph.Template,
            fixture.Graph.Inputs,
            fixture.Graph.Fonts,
            fixture.Graph.Nodes,
            fixture.Graph.OutputNodeId);
        var otherContext = new RenderGraphValidationContext(
            fixture.Project,
            otherVersion,
            otherTimeline,
            otherTimelineDigest,
            fixture.Profile,
            fixture.Layout,
            fixture.LayoutDigest,
            fixture.Template,
            [fixture.UnusedSourceSpan, fixture.UsedSourceSpan],
            editorialRecordIds: [],
            [
                new RenderArtifactEvidence(
                    fixture.InputId,
                    fixture.Asset.ContentDigest,
                    fixture.Asset.ByteSize),
            ],
            fonts: []);
        OutputQcResult otherQualityControl = fixture.CreateQualityControl(
            fixture.RegisteredArtifacts[fixture.VideoArtifactId],
            otherRender,
            otherGraph,
            otherContext);
        StoredApproval otherApproval = fixture.CreateApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.CreatedAt.AddMinutes(2),
            "Approve the consistently retargeted composition version.",
            otherVersion);

        DeliveryCreationResult first = await fixture.CreateAsync();
        DeliveryCreationResult second = await fixture.CreateAsync(
            compositionVersion: otherVersion,
            renderManifest: otherRender,
            validationContext: otherContext,
            qualityControl: otherQualityControl,
            approvalEvidence: [otherApproval]);

        CollectionAssert.AreEqual(
            first.UsedSourceSpans.ToArray(),
            second.UsedSourceSpans.ToArray());
        Assert.AreNotEqual(
            first.UsedSourceSpansDigest,
            second.UsedSourceSpansDigest);
        Assert.AreNotEqual(
            first.ApprovalEvidenceDigest,
            second.ApprovalEvidenceDigest);
        Assert.AreNotEqual(first.EvidenceDigest, second.EvidenceDigest);
    }

    [TestMethod]
    public async Task CreateAsync_ApprovalDigestChangesWithExactValidatedEvidence()
    {
        DeliveryFixture fixture = new();
        StoredApproval secondApproval = fixture.CreateApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.CreatedAt.AddMinutes(2),
            "Record additional approval evidence without a role matrix.");

        DeliveryCreationResult first = await fixture.CreateAsync();
        DeliveryCreationResult second = await fixture.CreateAsync(
            approvalEvidence: [fixture.Approval, secondApproval]);

        Assert.AreNotEqual(
            first.ApprovalEvidenceDigest,
            second.ApprovalEvidenceDigest);
        Assert.AreNotEqual(first.EvidenceDigest, second.EvidenceDigest);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsCrossVersionContextAndRenderEvidence()
    {
        DeliveryFixture contextFixture = new();
        CompositionVersion otherVersion = contextFixture.CreateVersion(
            EntityId<CompositionVersionIdTag>.New());

        DeliveryException contextFailure =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                contextFixture.CreateAsync(compositionVersion: otherVersion));
        Assert.AreEqual(
            DeliveryFailureReason.ContextMismatch,
            contextFailure.Reason);
        Assert.AreEqual(0, contextFixture.ValidatedArtifactCount);

        DeliveryFixture renderFixture = new();
        RenderProfile otherProfile = renderFixture.CreateProfile(
            EntityId<RenderProfileIdTag>.New(),
            Digest('e'));
        RenderManifest otherRender = renderFixture.CreateRenderManifest(
            profile: otherProfile);

        DeliveryException renderFailure =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                renderFixture.CreateAsync(renderManifest: otherRender));
        Assert.AreEqual(
            DeliveryFailureReason.RenderEvidenceMismatch,
            renderFailure.Reason);
        Assert.AreEqual(0, renderFixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsProfileBoundToAnotherLayout()
    {
        DeliveryFixture fixture = new();
        RenderProfile mismatchedProfile = fixture.CreateProfile(
            EntityId<RenderProfileIdTag>.New(),
            Digest('e'),
            EntityId<LayoutSpecIdTag>.New());
        RenderManifest render = fixture.CreateRenderManifest(
            profile: mismatchedProfile);
        RenderGraphValidationContext context = fixture.CreateContext(
            [fixture.UnusedSourceSpan, fixture.UsedSourceSpan],
            profile: mismatchedProfile);

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync(
                    renderManifest: render,
                    validationContext: context));

        Assert.AreEqual(
            DeliveryFailureReason.RenderEvidenceMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsMissingApprovalRenderOrRegisteredArtifact()
    {
        DeliveryFixture approvalFixture = new();
        DeliveryException missingApproval =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                approvalFixture.CreateAsync(approvalEvidence: []));
        Assert.AreEqual(
            DeliveryFailureReason.ApprovalEvidenceMissing,
            missingApproval.Reason);

        DeliveryFixture renderFixture = new();
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact> subtitle =
            renderFixture.ArtifactEvidence.Single(pair =>
                pair.Key == renderFixture.SubtitleArtifactId);
        DeliveryException missingRender =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                renderFixture.CreateAsync(artifactEvidence: [subtitle]));
        Assert.AreEqual(
            DeliveryFailureReason.RequiredArtifactMissing,
            missingRender.Reason);

        DeliveryFixture registeredFixture = new();
        registeredFixture.GetArtifactOverride = id =>
            id == registeredFixture.SubtitleArtifactId
                ? null
                : registeredFixture.RegisteredArtifacts.GetValueOrDefault(id);
        DeliveryException missingRegistered =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                registeredFixture.CreateAsync());
        Assert.AreEqual(
            DeliveryFailureReason.RequiredArtifactMissing,
            missingRegistered.Reason);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsStaleApprovalBeforeArtifactReads()
    {
        DeliveryFixture directFixture = new();
        CompositionVersion otherVersion = directFixture.CreateVersion(
            EntityId<CompositionVersionIdTag>.New());
        StoredApproval staleApproval = directFixture.CreateApproval(
            EntityId<ApprovalIdTag>.New(),
            directFixture.CreatedAt.AddMinutes(2),
            "Approval for another version.",
            otherVersion);

        DeliveryException direct =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                directFixture.CreateAsync(approvalEvidence: [staleApproval]));
        Assert.AreEqual(DeliveryFailureReason.StaleApproval, direct.Reason);
        Assert.AreEqual(0, directFixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, directFixture.ValidatedArtifactCount);

        DeliveryFixture evaluatorFixture = new();
        evaluatorFixture.ApprovalResultOverride = new ApprovalValidityResult(
            validApprovals: [],
            staleApprovals: [evaluatorFixture.Approval]);
        DeliveryException evaluated =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                evaluatorFixture.CreateAsync());
        Assert.AreEqual(DeliveryFailureReason.StaleApproval, evaluated.Reason);
        Assert.AreEqual(0, evaluatorFixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsQcFromAnotherRenderOrArtifact()
    {
        DeliveryFixture fixture = new();
        StoredArtifact otherArtifact = fixture.CreateStoredArtifact(
            EntityId<ArtifactIdTag>.New(),
            ArtifactKind.Render,
            fixture.VideoManifestArtifact);
        OutputQcResult otherQc = fixture.CreateQualityControl(
            otherArtifact,
            fixture.RenderManifest);

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync(qualityControl: otherQc));

        Assert.AreEqual(
            DeliveryFailureReason.QualityControlMismatch,
            exception.Reason);
        Assert.AreEqual(0, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    [DataRow("project")]
    [DataRow("digest")]
    [DataRow("size")]
    [DataRow("storage")]
    public async Task CreateAsync_RejectsRegisteredArtifactBindingMismatch(
        string mismatch)
    {
        DeliveryFixture fixture = new();
        StoredArtifact original =
            fixture.RegisteredArtifacts[fixture.SubtitleArtifactId];
        StoredArtifact mismatched = new(
            original.Id,
            mismatch == "project"
                ? EntityId<ProjectIdTag>.New()
                : original.ProjectId,
            original.Kind,
            mismatch == "storage"
                ? string.Concat(original.StorageReference, ".other")
                : original.StorageReference,
            mismatch == "digest" ? Digest('f') : original.ContentDigest,
            mismatch == "size" ? original.ByteSize + 1 : original.ByteSize,
            original.CreatedAt);
        fixture.GetArtifactOverride = id =>
            id == fixture.SubtitleArtifactId
                ? mismatched
                : fixture.RegisteredArtifacts.GetValueOrDefault(id);

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync());

        Assert.AreEqual(
            DeliveryFailureReason.ArtifactBindingMismatch,
            exception.Reason);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsQualityControlledArtifactKindDrift()
    {
        DeliveryFixture fixture = new();
        StoredArtifact original =
            fixture.RegisteredArtifacts[fixture.VideoArtifactId];
        var mismatched = new StoredArtifact(
            original.Id,
            original.ProjectId,
            ArtifactKind.Delivery,
            original.StorageReference,
            original.ContentDigest,
            original.ByteSize,
            original.CreatedAt);
        fixture.GetArtifactOverride = id =>
            id == fixture.VideoArtifactId
                ? mismatched
                : fixture.RegisteredArtifacts.GetValueOrDefault(id);

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync());

        Assert.AreEqual(
            DeliveryFailureReason.ArtifactBindingMismatch,
            exception.Reason);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsArtifactThatChangesDuringValidation()
    {
        DeliveryFixture fixture = new();
        fixture.ValidateArtifactOverride = (id, _) =>
        {
            StoredArtifact original = fixture.RegisteredArtifacts[id];
            return Task.FromResult(new StoredArtifact(
                original.Id,
                original.ProjectId,
                original.Kind,
                original.StorageReference,
                original.ContentDigest,
                original.ByteSize,
                original.CreatedAt.AddTicks(1)));
        };

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync());

        Assert.AreEqual(
            DeliveryFailureReason.RegisteredArtifactInvalid,
            exception.Reason);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsDuplicateIdsArtifactsAndStorageReferences()
    {
        DeliveryFixture idFixture = new();
        ManifestArtifact extra = idFixture.CreateManifestArtifact(
            "extra",
            "application/octet-stream",
            null,
            17,
            Digest('e'),
            idFixture.DeliveryPath("extra.bin"));
        DeliveryException duplicateId =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                idFixture.CreateAsync(artifactEvidence:
                [
                    .. idFixture.ArtifactEvidence,
                    new(idFixture.VideoArtifactId, extra),
                ]));
        Assert.AreEqual(
            DeliveryFailureReason.ArtifactConflict,
            duplicateId.Reason);

        DeliveryFixture artifactFixture = new();
        DeliveryException duplicateArtifact =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                artifactFixture.CreateAsync(artifactEvidence:
                [
                    .. artifactFixture.ArtifactEvidence,
                    new(
                        EntityId<ArtifactIdTag>.New(),
                        artifactFixture.VideoManifestArtifact),
                ]));
        Assert.AreEqual(
            DeliveryFailureReason.ArtifactConflict,
            duplicateArtifact.Reason);

        DeliveryFixture storageFixture = new();
        ManifestArtifact sameStorage = storageFixture.CreateManifestArtifact(
            "extra",
            "application/octet-stream",
            null,
            19,
            Digest('f'),
            storageFixture.VideoManifestArtifact.StorageReference);
        DeliveryException duplicateStorage =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                storageFixture.CreateAsync(artifactEvidence:
                [
                    .. storageFixture.ArtifactEvidence,
                    new(EntityId<ArtifactIdTag>.New(), sameStorage),
                ]));
        Assert.AreEqual(
            DeliveryFailureReason.ArtifactConflict,
            duplicateStorage.Reason);
    }

    [TestMethod]
    public async Task CreateAsync_DoesNotInventRenderArtifactTypeTaxonomy()
    {
        DeliveryFixture fixture = new();
        ManifestArtifact master = fixture.CreateManifestArtifact(
            "master",
            fixture.VideoManifestArtifact.MediaType,
            fixture.VideoManifestArtifact.Language,
            fixture.VideoManifestArtifact.ByteSize,
            fixture.VideoManifestArtifact.Sha256,
            fixture.VideoManifestArtifact.StorageReference);
        RenderManifest render = fixture.CreateRenderManifest(
            outputs: [master]);
        OutputQcResult qualityControl = fixture.CreateQualityControl(
            fixture.RegisteredArtifacts[fixture.VideoArtifactId],
            render);

        DeliveryCreationResult result = await fixture.CreateAsync(
            renderManifest: render,
            qualityControl: qualityControl,
            artifactEvidence: [new(fixture.VideoArtifactId, master)]);

        Assert.AreEqual(1, result.Manifest.Artifacts.Count);
        Assert.AreEqual("master", result.Manifest.Artifacts[0].ArtifactType);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsMissingOrDigestBrokenSourceSpan()
    {
        DeliveryFixture missingFixture = new();
        RenderGraphValidationContext missingContext =
            missingFixture.CreateContext([missingFixture.UnusedSourceSpan]);
        DeliveryException missing =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                missingFixture.CreateAsync(validationContext: missingContext));
        Assert.AreEqual(DeliveryFailureReason.ProvenanceInvalid, missing.Reason);
        Assert.AreEqual(0, missingFixture.ApprovalEvaluationCount);

        DeliveryFixture digestFixture = new();
        SourceSpan original = digestFixture.UsedSourceSpan;
        SourceSpan broken = new(
            original.Id,
            original.AssetId,
            original.StreamId,
            original.Range,
            Digest('f'),
            original.TranscriptRevisionId,
            original.TranscriptTokenIds);
        RenderGraphValidationContext brokenContext =
            digestFixture.CreateContext(
                [broken, digestFixture.UnusedSourceSpan]);
        DeliveryException digest =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                digestFixture.CreateAsync(validationContext: brokenContext));
        Assert.AreEqual(DeliveryFailureReason.ProvenanceInvalid, digest.Reason);
        Assert.AreEqual(0, digestFixture.ApprovalEvaluationCount);
    }

    [TestMethod]
    public async Task CreateAsync_PreCanceledRequestHasNoDependencySideEffects()
    {
        DeliveryFixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.CreateAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(0, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.GetArtifactCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_PreservesCancellationDuringApprovalEvaluation()
    {
        DeliveryFixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        fixture.EvaluateApprovalsOverride = (_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.CreateAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(1, fixture.ApprovalEvaluationCount);
        Assert.AreEqual(0, fixture.GetArtifactCount);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_PreservesCancellationDuringArtifactValidation()
    {
        DeliveryFixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        fixture.ValidateArtifactOverride = (_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(CancellationToken.None);
        };

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.CreateAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(1, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task CreateAsync_NormalizesPrivateDependencyFailures()
    {
        const string privateText =
            "C:\\private\\delivery.db S-1-5-21-private transcript body";
        DeliveryFixture approvalFixture = new();
        approvalFixture.EvaluateApprovalsOverride = static (_, _) =>
            throw new InvalidOperationException(privateText);

        DeliveryException approvalFailure =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                approvalFixture.CreateAsync());
        Assert.AreEqual(
            DeliveryFailureReason.ApprovalEvidenceInvalid,
            approvalFailure.Reason);
        Assert.IsFalse(
            approvalFailure.Message.Contains(privateText, StringComparison.Ordinal));
        Assert.IsNull(approvalFailure.InnerException);

        DeliveryFixture artifactFixture = new();
        artifactFixture.ValidateArtifactOverride = static (_, _) =>
            throw new IOException(privateText);
        DeliveryException artifactFailure =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                artifactFixture.CreateAsync());
        Assert.AreEqual(
            DeliveryFailureReason.RegisteredArtifactInvalid,
            artifactFailure.Reason);
        Assert.IsFalse(
            artifactFailure.Message.Contains(privateText, StringComparison.Ordinal));
        Assert.IsNull(artifactFailure.InnerException);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsApprovalEvaluatorSubstitution()
    {
        DeliveryFixture fixture = new();
        StoredApproval substituted = new(
            fixture.Approval.Id,
            fixture.Approval.ProjectId,
            fixture.Approval.CompositionVersionId,
            fixture.Approval.CompositionVersionDigest,
            "S-1-5-21-1000-2000-3000-9999",
            fixture.Approval.ActorRole,
            fixture.Approval.ApprovedAt,
            fixture.Approval.Reason);
        fixture.ApprovalResultOverride = new ApprovalValidityResult(
            [substituted],
            staleApprovals: []);

        DeliveryException exception =
            await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                fixture.CreateAsync());

        Assert.AreEqual(
            DeliveryFailureReason.ApprovalEvidenceInvalid,
            exception.Reason);
        Assert.AreEqual(0, fixture.ValidatedArtifactCount);
    }

    [TestMethod]
    public async Task ResultCollectionsAreImmutableAndPublicSurfaceIsMinimal()
    {
        DeliveryFixture fixture = new();
        var approvals = fixture.Approvals.ToList();
        var artifacts = fixture.ArtifactEvidence.ToList();
        Task<DeliveryCreationResult> pending = fixture.CreateAsync(
            approvalEvidence: approvals,
            artifactEvidence: artifacts);
        approvals.Clear();
        artifacts.Clear();
        DeliveryCreationResult result = await pending;

        Assert.AreEqual(2, result.Manifest.Artifacts.Count);
        Assert.AreEqual(1, result.UsedSourceSpans.Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ManifestArtifact>)result.Manifest.Artifacts).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SourceSpan>)result.UsedSourceSpans).Clear());

        Assert.IsTrue(typeof(DeliveryService).IsPublic);
        Assert.IsTrue(typeof(DeliveryService).IsSealed);
        ConstructorInfo[] constructors = typeof(DeliveryService).GetConstructors();
        Assert.AreEqual(1, constructors.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(ApprovalService), typeof(ArtifactStore) },
            constructors[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        MethodInfo[] createMethods = typeof(DeliveryService).GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
        Assert.AreEqual(2, createMethods.Length);
        Assert.IsTrue(createMethods.All(static method =>
            method.Name == nameof(DeliveryService.CreateAsync)));
        CollectionAssert.AreEquivalent(
            new[] { 9, 10 },
            createMethods.Select(static method =>
                method.GetParameters().Length).ToArray());
        MethodInfo variantMethod = createMethods.Single(static method =>
            method.GetParameters().Length == 10);
        Assert.AreEqual(
            typeof(SubtitleDeliveryVariantEvidence),
            variantMethod.GetParameters()[8].ParameterType);
        Assert.AreEqual(
            typeof(CancellationToken),
            variantMethod.GetParameters()[^1].ParameterType);
        AssertImmutablePublicType(
            typeof(DeliveryCreationResult),
            "Manifest",
            "UsedSourceSpans",
            "UsedSourceSpansDigest",
            "ApprovalEvidenceDigest",
            "EvidenceDigest");
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

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    internal sealed class DeliveryFixture
    {
        private const string EditorSid =
            "S-1-5-21-1000-2000-3000-5001";
        private const string ApproverSid =
            "S-1-5-21-1000-2000-3000-5002";

        private readonly Dictionary<
            EntityId<ArtifactIdTag>,
            StoredArtifact> _registeredArtifacts = [];

        internal DeliveryFixture()
        {
            CreatedAt = new DateTimeOffset(
                2048,
                7,
                8,
                9,
                10,
                11,
                TimeSpan.Zero);
            Asset = new Asset(
                EntityId<AssetIdTag>.New(),
                "private-source.mov",
                "video/quicktime",
                Digest('1'),
                4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "rights-evidence",
                    "Reviewed production use.",
                    externalPublicationAllowed: true));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "R-022 delivery fixture",
                "Bind exact delivery evidence.",
                "Internal test operators",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                [Asset],
                performers: []);

            TimeRange usedRange = new(
                default,
                new RationalTime(1, 1));
            TimeRange unusedRange = new(
                new RationalTime(2, 1),
                new RationalTime(1, 1));
            UsedSourceSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                Asset.Id,
                "video:0",
                usedRange,
                Asset.ContentDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            UnusedSourceSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                Asset.Id,
                "video:0",
                unusedRange,
                Asset.ContentDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            OriginRef origin = OriginRef.FromSourceSpan(UsedSourceSpan.Id);
            var element = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                usedRange,
                origin,
                sourceRange: usedRange);
            Version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                EditorSid,
                CreatedAt,
                "Create the exact delivery composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            TimelineMap = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                Version.Id,
                [new TimelineMapEntry(usedRange, origin, usedRange)]);
            TimelineMapDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(TimelineMap);
            Template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "Delivery template",
                Digest('2'));
            Layout = new LayoutSpec(
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
            LayoutDigest = Digest('3');
            Profile = CreateProfile(
                EntityId<RenderProfileIdTag>.New(),
                Digest('4'));

            InputId = EntityId<RenderInputIdTag>.New();
            var input = new RenderGraphInput(
                InputId,
                RenderInputKind.Video,
                Asset.ContentDigest,
                Asset.ByteSize,
                origin);
            RenderNode video = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                InputId,
                usedRange,
                origin,
                usedRange,
                "main");
            RenderNode output = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [video.Id],
                usedRange);
            Graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                Version.Id,
                Version.ContentDigest,
                TimelineMap.Id,
                TimelineMapDigest,
                Profile.Id,
                Profile.ProfileDigest,
                Layout.Id,
                LayoutDigest,
                Template,
                [input],
                fonts: [],
                [video, output],
                output.Id);
            ValidationContext = CreateContext(
                [UnusedSourceSpan, UsedSourceSpan]);

            ManifestId = EntityId<DeliveryManifestIdTag>.New();
            VideoManifestArtifact = CreateManifestArtifact(
                "video",
                "video/mp4",
                "ja",
                101,
                Digest('5'),
                RenderPath("final.mp4"));
            SubtitleManifestArtifact = CreateManifestArtifact(
                "subtitle",
                "text/vtt",
                "ja",
                31,
                Digest('6'),
                DeliveryPath("subtitle.vtt"));
            RenderManifest = CreateRenderManifest();

            VideoArtifactId = EntityId<ArtifactIdTag>.New();
            SubtitleArtifactId = EntityId<ArtifactIdTag>.New();
            _registeredArtifacts.Add(
                VideoArtifactId,
                CreateStoredArtifact(
                    VideoArtifactId,
                    ArtifactKind.Render,
                    VideoManifestArtifact));
            _registeredArtifacts.Add(
                SubtitleArtifactId,
                CreateStoredArtifact(
                    SubtitleArtifactId,
                    ArtifactKind.Delivery,
                    SubtitleManifestArtifact));
            ArtifactEvidence =
            [
                new(VideoArtifactId, VideoManifestArtifact),
                new(SubtitleArtifactId, SubtitleManifestArtifact),
            ];
            QualityControl = CreateQualityControl(
                _registeredArtifacts[VideoArtifactId],
                RenderManifest);
            Approval = CreateApproval(
                EntityId<ApprovalIdTag>.New(),
                CreatedAt.AddMinutes(1),
                "Approve this exact composition version digest.");
            Approvals = [Approval];

            Service = new DeliveryService(
                (compositionVersion, approvals) =>
                {
                    ApprovalEvaluationCount++;
                    if (EvaluateApprovalsOverride is not null)
                    {
                        return EvaluateApprovalsOverride(
                            compositionVersion,
                            approvals);
                    }

                    return ApprovalResultOverride
                        ?? new ApprovalValidityResult(
                            approvals.ToArray(),
                            staleApprovals: []);
                },
                artifactId =>
                {
                    GetArtifactCount++;
                    return GetArtifactOverride is null
                        ? _registeredArtifacts.GetValueOrDefault(artifactId)
                        : GetArtifactOverride(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    ValidatedArtifactCount++;
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValidateArtifactOverride is null
                        ? Task.FromResult(_registeredArtifacts[artifactId])
                        : ValidateArtifactOverride(
                            artifactId,
                            cancellationToken);
                });
        }

        internal DateTimeOffset CreatedAt { get; }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal SourceSpan UsedSourceSpan { get; }

        internal SourceSpan UnusedSourceSpan { get; }

        internal CompositionVersion Version { get; }

        internal TimelineMap TimelineMap { get; }

        internal ContentDigest TimelineMapDigest { get; }

        internal TemplateReference Template { get; }

        internal LayoutSpec Layout { get; }

        internal ContentDigest LayoutDigest { get; }

        internal RenderProfile Profile { get; }

        internal EntityId<RenderInputIdTag> InputId { get; }

        internal RenderGraph Graph { get; }

        internal RenderGraphValidationContext ValidationContext { get; }

        internal EntityId<DeliveryManifestIdTag> ManifestId { get; }

        internal ManifestArtifact VideoManifestArtifact { get; }

        internal ManifestArtifact SubtitleManifestArtifact { get; }

        internal RenderManifest RenderManifest { get; }

        internal EntityId<ArtifactIdTag> VideoArtifactId { get; }

        internal EntityId<ArtifactIdTag> SubtitleArtifactId { get; }

        internal IReadOnlyList<
            KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            ArtifactEvidence
        { get; }

        internal OutputQcResult QualityControl { get; }

        internal StoredApproval Approval { get; }

        internal IReadOnlyList<StoredApproval> Approvals { get; }

        internal IReadOnlyDictionary<
            EntityId<ArtifactIdTag>,
            StoredArtifact> RegisteredArtifacts => _registeredArtifacts;

        internal DeliveryService Service { get; }

        internal ApprovalValidityResult? ApprovalResultOverride { get; set; }

        internal Func<
            CompositionVersion,
            IReadOnlyList<StoredApproval>,
            ApprovalValidityResult>? EvaluateApprovalsOverride
        { get; set; }

        internal Func<EntityId<ArtifactIdTag>, StoredArtifact?>?
            GetArtifactOverride
        { get; set; }

        internal Func<
            EntityId<ArtifactIdTag>,
            CancellationToken,
            Task<StoredArtifact>>? ValidateArtifactOverride
        { get; set; }

        internal int ApprovalEvaluationCount { get; private set; }

        internal int GetArtifactCount { get; private set; }

        internal int ValidatedArtifactCount { get; private set; }

        internal Task<DeliveryCreationResult> CreateAsync(
            EntityId<DeliveryManifestIdTag>? manifestId = null,
            Project? project = null,
            CompositionVersion? compositionVersion = null,
            RenderManifest? renderManifest = null,
            RenderGraphValidationContext? validationContext = null,
            OutputQcResult? qualityControl = null,
            IReadOnlyList<StoredApproval>? approvalEvidence = null,
            IReadOnlyList<KeyValuePair<
                EntityId<ArtifactIdTag>,
                ManifestArtifact>>? artifactEvidence = null,
            CancellationToken cancellationToken = default) =>
            Service.CreateAsync(
                manifestId ?? ManifestId,
                project ?? Project,
                compositionVersion ?? Version,
                renderManifest ?? RenderManifest,
                validationContext ?? ValidationContext,
                qualityControl ?? QualityControl,
                approvalEvidence ?? Approvals,
                artifactEvidence ?? ArtifactEvidence,
                cancellationToken);

        internal CompositionVersion CreateVersion(
            EntityId<CompositionVersionIdTag> id) =>
            new(
                id,
                Project.Id,
                Version.ParentVersionId,
                Version.CreatedByActorId,
                Version.CreatedAt,
                Version.ChangeReason,
                Version.ContentDigest,
                Version.Elements);

        internal RenderProfile CreateProfile(
            EntityId<RenderProfileIdTag> id,
            ContentDigest digest,
            EntityId<LayoutSpecIdTag>? layoutSpecId = null,
            bool subtitleBurnIn = false) =>
            new(
                id,
                "delivery-profile",
                digest,
                1920,
                1080,
                30,
                1,
                layoutSpecId
                    ?? Layout?.Id
                    ?? EntityId<LayoutSpecIdTag>.New(),
                Template?.Id ?? EntityId<TemplateIdTag>.New(),
                Template?.VersionDigest ?? Digest('2'),
                "h264",
                "aac",
                subtitleBurnIn);

        internal RenderManifest CreateRenderManifest(
            RenderProfile? profile = null,
            IReadOnlyList<ManifestArtifact>? outputs = null,
            EntityId<RenderManifestIdTag>? id = null,
            IReadOnlyList<RenderToolEvidence>? tools = null) =>
            new(
                id ?? EntityId<RenderManifestIdTag>.New(),
                Version,
                TimelineMap,
                TimelineMapDigest,
                profile ?? Profile,
                Digest('b'),
                "1.0.0",
                Digest('c'),
                [new RenderInputEvidence("source", Asset.ContentDigest)],
                tools ??
                [
                    new RenderToolEvidence(
                        "ffmpeg",
                        "8.0",
                        "release",
                        Digest('d'),
                        containerImageDigest: null),
                ],
                models: [],
                fonts: [],
                outputs ?? [VideoManifestArtifact]);

        internal RenderGraphValidationContext CreateContext(
            IReadOnlyList<SourceSpan> sourceSpans,
            RenderProfile? profile = null) =>
            new(
                Project,
                Version,
                TimelineMap,
                TimelineMapDigest,
                profile ?? Profile,
                Layout,
                LayoutDigest,
                Template,
                sourceSpans,
                editorialRecordIds: [],
                [
                    new RenderArtifactEvidence(
                        InputId,
                        Asset.ContentDigest,
                        Asset.ByteSize),
                ],
                fonts: []);

        internal OutputQcResult CreateQualityControl(
            StoredArtifact artifact,
            RenderManifest renderManifest,
            RenderGraph? graph = null,
            RenderGraphValidationContext? validationContext = null,
            OutputQcSubtitleEvidence? subtitleEvidence = null)
        {
            RenderGraph targetGraph = graph ?? Graph;
            var request = new OutputQcRequest(
                artifact.Id,
                renderManifest,
                targetGraph,
                validationContext ?? ValidationContext,
                subtitleEvidence,
                TimeSpan.FromMinutes(1));
            var timingEntry = new OutputQcTimingEntry(
                sequence: 0,
                presentationTimestampTicks: 0,
                durationTicks: 1);
            var videoTiming = new OutputQcStreamTiming(
                streamIndex: 0,
                [timingEntry],
                new RationalTime(1, 1),
                presentationStart: default,
                presentationEnd: new RationalTime(1, 1),
                Digest('7'));
            var decode = new OutputQcDecodeEvidence(TimeSpan.Zero);
            var luma = new OutputQcLumaMeasurements(
                [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                Digest('8'));
            return new OutputQcResult(
                artifact,
                request,
                Digest('9'),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                videoTiming,
                audioTiming: null,
                subtitleTiming: null,
                decode,
                luma,
                audioMeasurements: null);
        }

        internal (
            RenderManifest RenderManifest,
            RenderGraphValidationContext ValidationContext,
            OutputQcResult QualityControl,
            SubtitleDeliveryVariantEvidence Variant)
            CreateBurnedInVariantCase()
        {
            var fullRect = new NormalizedRect(
                default,
                default,
                new RationalNumber(1, 1),
                new RationalNumber(1, 1));
            var layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        fullRect,
                        LayoutRegionRole.Video),
                    new LayoutRegion(
                        "subtitle",
                        fullRect,
                        LayoutRegionRole.Subtitle),
                ],
                safeZones: []);
            ContentDigest layoutSpecDigest = Digest('a');
            RenderProfile profile = CreateProfile(
                EntityId<RenderProfileIdTag>.New(),
                Digest('e'),
                layout.Id,
                subtitleBurnIn: true);
            EntityId<EditorialRecordIdTag> editorialRecordId =
                EntityId<EditorialRecordIdTag>.New();
            OriginRef subtitleOrigin =
                OriginRef.FromEditorialRecord(editorialRecordId);
            TimeRange outputRange = Version.Elements.Single().OutputRange;
            var cue = new Cue(
                EntityId<CueIdTag>.New(),
                CueKind.Subtitle,
                outputRange,
                "subtitle");
            var subtitleInput = new RenderGraphInput(
                EntityId<RenderInputIdTag>.New(),
                RenderInputKind.Subtitle,
                Digest('f'),
                17,
                subtitleOrigin);
            RenderNode video = Graph.Nodes.Single(static node =>
                node.Kind == RenderNodeKind.Video);
            RenderNode subtitle = RenderNode.Subtitle(
                EntityId<RenderNodeIdTag>.New(),
                subtitleInput.Id,
                video.Id,
                cue,
                subtitleOrigin);
            RenderNode output = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [subtitle.Id],
                outputRange);
            var font = new RenderFontEvidence("Test Sans", Digest('1'));
            var graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                Version.Id,
                Version.ContentDigest,
                TimelineMap.Id,
                TimelineMapDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutSpecDigest,
                Template,
                [.. Graph.Inputs, subtitleInput],
                [font],
                [video, subtitle, output],
                output.Id);
            var validationContext = new RenderGraphValidationContext(
                Project,
                Version,
                TimelineMap,
                TimelineMapDigest,
                profile,
                layout,
                layoutSpecDigest,
                Template,
                [UnusedSourceSpan, UsedSourceSpan],
                [editorialRecordId],
                [
                    new RenderArtifactEvidence(
                        InputId,
                        Asset.ContentDigest,
                        Asset.ByteSize),
                    new RenderArtifactEvidence(
                        subtitleInput.Id,
                        subtitleInput.ContentDigest,
                        subtitleInput.ByteSize),
                ],
                [font]);
            var renderManifest = new RenderManifest(
                EntityId<RenderManifestIdTag>.New(),
                Version,
                TimelineMap,
                TimelineMapDigest,
                profile,
                RenderManifest.ConfigurationDigest,
                RenderManifest.ApplicationVersion,
                RenderManifest.ApplicationDigest,
                [
                    new RenderInputEvidence("source", Asset.ContentDigest),
                    new RenderInputEvidence(
                        "subtitle",
                        subtitleInput.ContentDigest),
                ],
                RenderManifest.Tools,
                RenderManifest.Models,
                [font],
                [VideoManifestArtifact]);
            ContentDigest displayProfileDigest = Digest('6');
            SubtitleDisplayResult displayResult = SubtitleDisplayPolicy.Apply(
                new SubtitleDisplayProfile(
                    displayProfileDigest,
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [new SubtitleDisplaySourceCue(cue, "caption")],
                selectedCueIds: []);
            var subtitleEvidence = new OutputQcSubtitleEvidence(
                Digest('7'),
                displayProfileDigest,
                displayResult,
                layouts: []);
            OutputQcResult qualityControl = CreateQualityControl(
                _registeredArtifacts[VideoArtifactId],
                renderManifest,
                graph,
                validationContext,
                subtitleEvidence);
            var variant = new SubtitleDeliveryVariantEvidence(
                SubtitleDeliveryVariantKind.BurnedIn,
                graph.Id,
                displayProfileDigest,
                Digest('0'),
                sidecarArtifact: null);
            return (
                renderManifest,
                validationContext,
                qualityControl,
                variant);
        }

        internal StoredApproval CreateApproval(
            EntityId<ApprovalIdTag> id,
            DateTimeOffset approvedAt,
            string reason,
            CompositionVersion? version = null)
        {
            CompositionVersion target = version ?? Version;
            return new StoredApproval(
                id,
                target.ProjectId,
                target.Id,
                target.ContentDigest,
                ApproverSid,
                ReviewActorRole.ContentApprover,
                approvedAt,
                reason);
        }

        internal StoredArtifact CreateStoredArtifact(
            EntityId<ArtifactIdTag> id,
            ArtifactKind kind,
            ManifestArtifact artifact) =>
            new(
                id,
                Project.Id,
                kind,
                artifact.StorageReference,
                artifact.Sha256,
                artifact.ByteSize,
                CreatedAt.AddMinutes(3));

        internal ManifestArtifact CreateManifestArtifact(
            string artifactType,
            string mediaType,
            string? language,
            long byteSize,
            ContentDigest digest,
            string storageReference) =>
            new(
                artifactType,
                mediaType,
                language,
                byteSize,
                digest,
                storageReference);

        internal string RenderPath(string fileName) =>
            string.Concat(
                "projects/",
                Project.Id.ToString(),
                "/renders/",
                fileName);

        internal string DeliveryPath(string fileName) =>
            string.Concat(
                "projects/",
                Project.Id.ToString(),
                "/deliveries/",
                fileName);
    }
}
