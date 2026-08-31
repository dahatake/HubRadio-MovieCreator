using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Profiles;
using HubRadio.App.Speakers;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class LongFormComposerTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2042, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [TestMethod]
    public void ComposeBuildsCanonicalTemplatePlanAndPreservesEvidence()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest(
            reverseTopicInput: true);
        string[] requestedTopicOrder = request.Topics
            .Select(static topic => topic.TopicMetadata.Metadata.Title)
            .ToArray();

        LongFormCompositionPlan plan = LongFormComposer.Compose(
            request);

        Assert.AreSame(fixture.Project, plan.Project);
        Assert.AreSame(fixture.Provenance, plan.Provenance);
        Assert.AreSame(fixture.Template, plan.Template);
        Assert.AreSame(fixture.Profile, plan.Profile);
        Assert.AreEqual(LongFormDestination.YouTube, plan.Destination);
        Assert.AreSame(fixture.SpeakerAssignments, plan.SpeakerAssignments);
        CollectionAssert.AreEqual(
            requestedTopicOrder,
            request.Topics
                .Select(static topic => topic.TopicMetadata.Metadata.Title)
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                LongFormElementKind.PreRollMargin,
                LongFormElementKind.Opening,
                LongFormElementKind.ProgramName,
                LongFormElementKind.EpisodeNumber,
                LongFormElementKind.EpisodeTitle,
                LongFormElementKind.PerformerIntroduction,
                LongFormElementKind.PublicationScopeIndicator,
                LongFormElementKind.Body,
                LongFormElementKind.Topic,
                LongFormElementKind.Topic,
                LongFormElementKind.Summary,
                LongFormElementKind.CallToAction,
                LongFormElementKind.Ending,
                LongFormElementKind.PostRollMargin,
            },
            plan.Elements.Select(static element => element.Kind).ToArray());
        Assert.IsTrue(plan.Elements.All(
            static element => element.State == LongFormElementState.Included));

        Assert.AreEqual(
            "Hub Radio",
            Element(plan, LongFormElementKind.ProgramName).Text);
        Assert.AreEqual(
            "42",
            Element(plan, LongFormElementKind.EpisodeNumber).Text);
        Assert.AreEqual(
            fixture.Project.ContentName,
            Element(plan, LongFormElementKind.EpisodeTitle).Text);
        Assert.AreEqual(
            fixture.Project.PublicationScope,
            plan.Project.PublicationScope);
        Assert.AreEqual(
            "Episode summary",
            Element(plan, LongFormElementKind.Summary).Text);
        Assert.AreEqual(
            "Listen to the next episode",
            Element(plan, LongFormElementKind.CallToAction).Text);

        LongFormElementPlan[] topics = plan.Elements
            .Where(static element => element.Kind == LongFormElementKind.Topic)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "Architecture", "Delivery" },
            topics.Select(static topic => topic.Text).ToArray());
        CollectionAssert.AreEqual(
            new[] { Range(0, 5), Range(5, 5) },
            topics.Select(static topic => topic.TopicRange!.Range).ToArray());
        CollectionAssert.AreEqual(
            new[] { Range(8, 10), Range(18, 10) },
            topics.Select(static topic => topic.OutputRange!.Value).ToArray());
        Assert.IsTrue(topics[0].Origins.Any(origin =>
            origin.Kind == OriginKind.SourceSpan
            && origin.SourceSpanId == fixture.SourceSpans[0].Id));
        Assert.IsTrue(topics[1].Origins.Any(origin =>
            origin.Kind == OriginKind.SourceSpan
            && origin.SourceSpanId == fixture.SourceSpans[1].Id));
        Assert.AreSame(fixture.Topics[0].Metadata, topics[0].TopicMetadata);
        Assert.AreSame(fixture.Topics[1].Metadata, topics[1].TopicMetadata);
        SpeakerAssignment expectedSpeaker = fixture.SpeakerAssignments
            .CurrentRevision.Assignments.Single();
        SpeakerAssignment actualSpeaker = plan.SpeakerAssignments!
            .CurrentRevision.Assignments.Single();
        Assert.AreSame(expectedSpeaker, actualSpeaker);
        Assert.AreEqual(fixture.Performer.Id, actualSpeaker.PerformerId);
        Assert.AreEqual(fixture.Performer.DisplayName, actualSpeaker.FullName);
        Assert.AreEqual(fixture.Performer.Role, actualSpeaker.Role);
        foreach (LongFormElementPlan topic in topics)
        {
            TopicMainSpeakerSnapshot snapshot = topic.TopicMetadata!
                .Metadata.MainSpeaker!;
            Assert.AreEqual(actualSpeaker.SpeakerId, snapshot.SpeakerId);
            Assert.AreEqual(actualSpeaker.PerformerId, snapshot.PerformerId);
            Assert.AreEqual(actualSpeaker.FullName, snapshot.DisplayText);
            Assert.IsFalse(
                snapshot.TitleOrAffiliationConfirmationRequired);
        }

        LongFormElementPlan opening = Element(
            plan,
            LongFormElementKind.Opening);
        CollectionAssert.AreEqual(
            new[]
            {
                LongFormAssetRole.OpeningMedia,
                LongFormAssetRole.KeyVisual,
                LongFormAssetRole.Logo,
                LongFormAssetRole.Jingle,
            },
            opening.AssetSlots.Select(static slot => slot.Role).ToArray());
        Assert.IsTrue(opening.AssetSlots.All(
            static slot => slot.State == LongFormAssetSlotState.Included));
        AssertSlotPreservesAsset(opening, fixture.OpeningMedia);
        AssertSlotPreservesAsset(opening, fixture.KeyVisual);
        AssertSlotPreservesAsset(opening, fixture.Logo);
        AssertSlotPreservesAsset(opening, fixture.Jingle);
        AssertSlotPreservesAsset(
            Element(plan, LongFormElementKind.Body),
            fixture.BodyMedia);
        AssertSlotPreservesAsset(
            Element(plan, LongFormElementKind.Ending),
            fixture.EndingMedia);

        CollectionAssert.AreEqual(
            new[] { "jingle", "key-visual", "logo" },
            plan.TemplateAssets
                .Select(static asset => asset.ParameterName)
                .ToArray());
        AssertTemplateAssetPreserved(plan, "jingle", fixture.Jingle);
        AssertTemplateAssetPreserved(plan, "key-visual", fixture.KeyVisual);
        AssertTemplateAssetPreserved(plan, "logo", fixture.Logo);
        CollectionAssert.AreEquivalent(
            fixture.Project.Assets
                .Select(static asset => asset.Id.ToString())
                .ToArray(),
            plan.ReferencedAssets
                .Select(static asset => asset.Id.ToString())
                .ToArray());

        Assert.IsTrue(plan.TimingComplete);
        Assert.IsTrue(plan.ExternalRightsReady);
        Assert.IsFalse(plan.ExternalPublicationReady);
        Assert.IsFalse(plan.Warnings.Any(warning => warning.Code is
            LongFormWarningCode.ElementOmitted
            or LongFormWarningCode.TimingUnspecified
            or LongFormWarningCode.OptionalAssetOmitted
            or LongFormWarningCode.AssetRightsUnconfirmed
            or LongFormWarningCode.AssetRightsRejected
            or LongFormWarningCode.AssetExternalPublicationNotAllowed));
        Assert.IsTrue(plan.Warnings.Any(warning =>
            warning.Code == LongFormWarningCode.DevelopmentTemplateNotApproved));
        Assert.IsTrue(plan.Warnings.Any(warning =>
            warning.Code == LongFormWarningCode.PlatformSafeZonesUnmeasured));
        CollectionAssert.AreEqual(
            fixture.Profile.Unmeasured.ToArray(),
            plan.Warnings
                .Where(warning =>
                    warning.Code
                        == LongFormWarningCode.ProfileLimitationUnmeasured)
                .Select(static warning => warning.Limitation)
                .ToArray());
    }

    [TestMethod]
    public void OptionalSlotsCanBeOmittedWithoutInventingTimingOrDisplayText()
    {
        Fixture fixture = new();
        AppliedBrandTemplate templateWithoutAssetParameters =
            fixture.CreateTemplate(includeAssetParameters: false);

        LongFormCompositionPlan plan = LongFormComposer.Compose(
            fixture.CreateRequest(
                includeTimings: false,
                includeOptionalElements: false,
                includeOptionalAssets: false,
                template: templateWithoutAssetParameters));

        foreach (LongFormElementKind kind in new[]
        {
            LongFormElementKind.PreRollMargin,
            LongFormElementKind.ProgramName,
            LongFormElementKind.EpisodeNumber,
            LongFormElementKind.EpisodeTitle,
            LongFormElementKind.PerformerIntroduction,
            LongFormElementKind.PublicationScopeIndicator,
            LongFormElementKind.Summary,
            LongFormElementKind.PostRollMargin,
        })
        {
            LongFormElementPlan omitted = Element(plan, kind);
            Assert.AreEqual(LongFormElementState.Omitted, omitted.State, kind.ToString());
            Assert.IsNull(omitted.Text, kind.ToString());
            Assert.IsNull(omitted.OutputRange, kind.ToString());
            Assert.IsEmpty(omitted.Origins, kind.ToString());
        }

        LongFormElementPlan opening = Element(
            plan,
            LongFormElementKind.Opening);
        Assert.AreEqual(
            LongFormAssetSlotState.Included,
            opening.AssetSlots.Single(slot =>
                slot.Role == LongFormAssetRole.OpeningMedia).State);
        foreach (LongFormAssetRole role in new[]
        {
            LongFormAssetRole.KeyVisual,
            LongFormAssetRole.Logo,
            LongFormAssetRole.Jingle,
        })
        {
            LongFormAssetSlot slot = opening.AssetSlots.Single(candidate =>
                candidate.Role == role);
            Assert.AreEqual(LongFormAssetSlotState.Omitted, slot.State, role.ToString());
            Assert.IsNull(slot.Asset, role.ToString());
            Assert.IsNull(slot.Origin, role.ToString());
            Assert.IsNull(slot.Rights, role.ToString());
        }

        LongFormAssetSlot ending = Element(
            plan,
            LongFormElementKind.Ending).AssetSlots.Single();
        Assert.AreEqual(LongFormAssetSlotState.Omitted, ending.State);
        Assert.IsEmpty(plan.TemplateAssets);
        Assert.AreEqual(
            PublicationScope.ExternalPublicationApproved,
            plan.Project.PublicationScope);
        Assert.IsTrue(plan.Profile.Capabilities.Contains(
            "end-card-margin",
            StringComparer.Ordinal));
        Assert.IsTrue(plan.Elements
            .Where(static element =>
                element.State == LongFormElementState.Included)
            .All(static element => element.OutputRange is null));
        Assert.IsFalse(plan.TimingComplete);
        Assert.IsFalse(plan.ExternalPublicationReady);
        Assert.IsTrue(plan.Warnings.Any(warning =>
            warning.Code == LongFormWarningCode.TimingUnspecified));
        Assert.IsTrue(plan.Warnings.Any(warning =>
            warning.Code == LongFormWarningCode.OptionalAssetOmitted
            && warning.AssetRole == LongFormAssetRole.Jingle));
    }

    [TestMethod]
    public void FixedTemplateTextEpisodeAndAssetsCannotBeOverridden()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();

        var forgedProgramName = new LongFormElementInput(
            request.ProgramName.Id,
            LongFormElementKind.ProgramName,
            enabled: true,
            "Forged Program",
            request.ProgramName.OutputRange,
            request.ProgramName.Origins,
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                programName: forgedProgramName)),
            LongFormComposerFailureReason.TemplateMismatch);

        var forgedEpisodeNumber = new LongFormElementInput(
            request.EpisodeNumber.Id,
            LongFormElementKind.EpisodeNumber,
            enabled: true,
            "0042",
            request.EpisodeNumber.OutputRange,
            request.EpisodeNumber.Origins,
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                episodeNumber: forgedEpisodeNumber)),
            LongFormComposerFailureReason.TemplateMismatch);

        LongFormAssetReference[] swappedLogoReferences = request.Opening.Assets
            .Select(reference => reference.Role == LongFormAssetRole.Logo
                ? new LongFormAssetReference(
                    LongFormAssetRole.Logo,
                    fixture.KeyVisual.Id,
                    fixture.KeyVisual.ContentDigest)
                : reference)
            .ToArray();
        var swappedLogo = new LongFormElementInput(
            request.Opening.Id,
            LongFormElementKind.Opening,
            enabled: true,
            text: null,
            request.Opening.OutputRange,
            request.Opening.Origins,
            swappedLogoReferences);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                opening: swappedLogo)),
            LongFormComposerFailureReason.TemplateMismatch);

        AppliedBrandTemplate wrongKind = fixture.CreateTemplate(
            includeAssetParameters: true,
            programNameValue: TemplateParameterValue.FromInteger(42));
        AssertFailure(
            () => LongFormComposer.Compose(fixture.CreateRequest(
                template: wrongKind)),
            LongFormComposerFailureReason.TemplateMismatch);
    }

    [TestMethod]
    public void DestinationSelectsOnlyItsCallToActionAndCatalogFailsClosed()
    {
        Fixture fixture = new();
        LongFormCompositionRequest youtube = fixture.CreateRequest();
        LongFormCompositionRequest internalDistribution =
            fixture.CreateRequest(
                destination: LongFormDestination.InternalDistribution);

        LongFormCompositionPlan youtubePlan = LongFormComposer.Compose(youtube);
        LongFormCompositionPlan internalPlan = LongFormComposer.Compose(
            internalDistribution);

        Assert.AreEqual(
            "Listen to the next episode",
            Element(youtubePlan, LongFormElementKind.CallToAction).Text);
        Assert.AreEqual(
            "Join the internal community",
            Element(internalPlan, LongFormElementKind.CallToAction).Text);
        Assert.AreNotEqual(youtubePlan.PlanDigest, internalPlan.PlanDigest);

        LongFormCallToActionInput[] missingDestination = youtube.CallToActions
            .Where(callToAction =>
                callToAction.Destination != youtube.Destination)
            .ToArray();
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                youtube,
                callToActions: missingDestination)),
            LongFormComposerFailureReason.InvalidElement);

        LongFormCallToActionInput duplicate = youtube.CallToActions[0];
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                youtube,
                callToActions: [.. youtube.CallToActions, duplicate])),
            LongFormComposerFailureReason.InvalidElement);
    }

    [TestMethod]
    public void UntrustedSourceSpanAndEditorialRecordOriginsAreRejected()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();
        EntityId<SourceSpanIdTag> fabricatedSourceSpanId =
            Id<SourceSpanIdTag>(
                "c1000000-0000-0000-0000-000000000999");
        OriginRef[] fabricatedBodyOrigins = request.Body.Origins
            .Select(origin => origin.SourceSpanId == fixture.SourceSpans[0].Id
                ? OriginRef.FromSourceSpan(fabricatedSourceSpanId)
                : origin)
            .ToArray();
        var fabricatedBody = new LongFormElementInput(
            request.Body.Id,
            LongFormElementKind.Body,
            enabled: true,
            text: null,
            request.Body.OutputRange,
            fabricatedBodyOrigins,
            request.Body.Assets);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                body: fabricatedBody)),
            LongFormComposerFailureReason.OriginEvidenceMissing);

        var fabricatedSummary = new LongFormElementInput(
            request.Summary.Id,
            LongFormElementKind.Summary,
            enabled: true,
            request.Summary.Text,
            request.Summary.OutputRange,
            [OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(
                "c2000000-0000-0000-0000-000000000999"))],
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                summary: fabricatedSummary)),
            LongFormComposerFailureReason.OriginEvidenceMissing);
    }

    [TestMethod]
    public void ProvenanceMustMatchProjectRevisionAndSourceAssetDigest()
    {
        Fixture fixture = new();

        var wrongProject = new LongFormProvenanceSnapshot(
            Id<ProjectIdTag>(
                "a6000000-0000-0000-0000-000000000002"),
            fixture.Provenance.TranscriptRevisionId,
            fixture.Provenance.TranscriptRevisionDigest,
            fixture.SourceSpans,
            fixture.Provenance.TrustedEditorialRecordIds);
        AssertFailure(
            () => LongFormComposer.Compose(fixture.CreateRequest(
                provenance: wrongProject)),
            LongFormComposerFailureReason.ContextMismatch);

        SourceSpan sourceSpan = fixture.SourceSpans[0];
        var wrongSourceDigest = new SourceSpan(
            sourceSpan.Id,
            sourceSpan.AssetId,
            sourceSpan.StreamId,
            sourceSpan.Range,
            ContentDigest.Compute(Encoding.UTF8.GetBytes("forged source")),
            sourceSpan.TranscriptRevisionId,
            sourceSpan.TranscriptTokenIds);
        var digestMismatch = new LongFormProvenanceSnapshot(
            fixture.Project.Id,
            fixture.Provenance.TranscriptRevisionId,
            fixture.Provenance.TranscriptRevisionDigest,
            [wrongSourceDigest, fixture.SourceSpans[1]],
            fixture.Provenance.TrustedEditorialRecordIds);
        AssertFailure(
            () => LongFormComposer.Compose(fixture.CreateRequest(
                provenance: digestMismatch)),
            LongFormComposerFailureReason.ContextMismatch);

        var wrongSourceRevision = new SourceSpan(
            sourceSpan.Id,
            sourceSpan.AssetId,
            sourceSpan.StreamId,
            sourceSpan.Range,
            sourceSpan.SourceDigest,
            Id<TranscriptRevisionIdTag>(
                "ac000000-0000-0000-0000-000000000002"),
            sourceSpan.TranscriptTokenIds);
        var revisionMismatch = new LongFormProvenanceSnapshot(
            fixture.Project.Id,
            fixture.Provenance.TranscriptRevisionId,
            fixture.Provenance.TranscriptRevisionDigest,
            [wrongSourceRevision, fixture.SourceSpans[1]],
            fixture.Provenance.TrustedEditorialRecordIds);
        AssertFailure(
            () => LongFormComposer.Compose(fixture.CreateRequest(
                provenance: revisionMismatch)),
            LongFormComposerFailureReason.ContextMismatch);

        var wrongTranscriptDigest = new LongFormProvenanceSnapshot(
            fixture.Project.Id,
            fixture.Provenance.TranscriptRevisionId,
            ContentDigest.Compute(Encoding.UTF8.GetBytes("wrong transcript")),
            fixture.SourceSpans,
            fixture.Provenance.TrustedEditorialRecordIds);
        AssertFailure(
            () => LongFormComposer.Compose(fixture.CreateRequest(
                provenance: wrongTranscriptDigest)),
            LongFormComposerFailureReason.TopicBindingMismatch);
    }

    [TestMethod]
    public void StaleVisibleTopicSpeakerIsRejectedAfterAssignmentBecomesHidden()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory hidden = SpeakerAssignmentService.SetAssignment(
            fixture.Project,
            fixture.Transcript,
            fixture.SpeakerAssignments,
            Id<SpeakerAssignmentRevisionIdTag>(
                "b2000000-0000-0000-0000-000000000002"),
            fixture.SpeakerId,
            fixture.Performer.Id,
            "Alpha",
            SpeakerDisplayFormat.Hidden,
            ActorSid,
            CreatedAt.AddHours(1),
            "Hide the speaker in composed output.");
        ResolvedSpeakerDisplay display = SpeakerAssignmentService.ResolveDisplay(
            fixture.Project,
            fixture.Transcript,
            hidden,
            fixture.SpeakerId);
        Assert.IsFalse(display.IsVisible);
        Assert.IsNull(display.Text);

        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                fixture.CreateRequest(),
                speakerAssignments: hidden)),
            LongFormComposerFailureReason.SpeakerEvidenceMissing);
    }

    [TestMethod]
    public void UnusedTemplateInventoryAssetDoesNotBlockRightsReadiness()
    {
        Fixture fixture = new(RightsStatus.Rejected);
        AppliedBrandTemplate inventoryOnly = fixture.CreateTemplate(
            includeAssetParameters: false,
            inventoryOnlyAsset: fixture.Logo);

        LongFormCompositionPlan plan = LongFormComposer.Compose(
            fixture.CreateRequest(
                includeOptionalAssets: false,
                template: inventoryOnly));

        Assert.AreSame(
            fixture.Logo,
            plan.TemplateAssets.Single(asset =>
                asset.ParameterName == "inventory-only-asset").Asset);
        Assert.IsFalse(plan.ReferencedAssets.Any(asset =>
            asset.Id == fixture.Logo.Id));
        Assert.IsTrue(plan.ExternalRightsReady);
        Assert.IsFalse(plan.Warnings.Any(warning =>
            warning.AssetId == fixture.Logo.Id
            && warning.Code is LongFormWarningCode.AssetRightsRejected
                or LongFormWarningCode.AssetExternalPublicationNotAllowed));
    }

    [TestMethod]
    public void EqualTimestampInstantsWithDifferentOffsetsDigestIdentically()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();
        DateTimeOffset deadline = fixture.Project.Deadline!.Value;
        Project shiftedOffset = ProjectWithDeadline(
            fixture.Project,
            deadline.ToOffset(TimeSpan.FromHours(9)));
        Project differentInstant = ProjectWithDeadline(
            fixture.Project,
            deadline.AddTicks(1));

        ContentDigest canonical = LongFormComposer.Compose(request).PlanDigest;
        ContentDigest shifted = LongFormComposer.Compose(Replace(
            request,
            project: shiftedOffset)).PlanDigest;
        ContentDigest changed = LongFormComposer.Compose(Replace(
            request,
            project: differentInstant)).PlanDigest;

        Assert.AreEqual(canonical, shifted);
        Assert.AreNotEqual(canonical, changed);
    }

    [TestMethod]
    public void EveryStructuralElementCanBeExplicitlyOmitted()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();
        LongFormTopicInput[] omittedTopics = request.Topics
            .Select(topic => new LongFormTopicInput(
                Omit(topic.Element),
                topic.TopicRange,
                topic.TopicMetadata))
            .ToArray();
        LongFormCallToActionInput[] omittedCallToActions =
            request.CallToActions
                .Select(callToAction => new LongFormCallToActionInput(
                    callToAction.Destination,
                    Omit(callToAction.Element)))
                .ToArray();
        var allOmitted = new LongFormCompositionRequest(
            request.PlanId,
            request.Project,
            request.Provenance,
            request.Template,
            request.Profile,
            request.Destination,
            request.SpeakerAssignments,
            Omit(request.PreRollMargin),
            Omit(request.Opening),
            Omit(request.ProgramName),
            Omit(request.EpisodeNumber),
            Omit(request.EpisodeTitle),
            Omit(request.PerformerIntroduction),
            Omit(request.PublicationScopeIndicator),
            Omit(request.Body),
            omittedTopics,
            Omit(request.Summary),
            omittedCallToActions,
            Omit(request.Ending),
            Omit(request.PostRollMargin));

        LongFormCompositionPlan plan = LongFormComposer.Compose(allOmitted);

        Assert.AreEqual(14, plan.Elements.Count);
        Assert.IsTrue(plan.Elements.All(element =>
            element.State == LongFormElementState.Omitted
            && element.Text is null
            && element.OutputRange is null
            && element.Origins.Count == 0
            && element.AssetSlots.All(slot =>
                slot.State == LongFormAssetSlotState.Omitted)));
        Assert.IsFalse(plan.TimingComplete);
        Assert.IsFalse(plan.ExternalPublicationReady);
    }

    [TestMethod]
    public void UnconfirmedAndRejectedRightsFailClosedAndRemainExactSnapshots()
    {
        foreach ((RightsStatus status, LongFormWarningCode expectedWarning)
            in new[]
            {
                (
                    RightsStatus.Unconfirmed,
                    LongFormWarningCode.AssetRightsUnconfirmed),
                (
                    RightsStatus.Rejected,
                    LongFormWarningCode.AssetRightsRejected),
            })
        {
            Fixture fixture = new(status);

            LongFormCompositionPlan plan = LongFormComposer.Compose(
                fixture.CreateRequest());

            LongFormAssetSlot logoSlot = Element(
                plan,
                LongFormElementKind.Opening).AssetSlots.Single(slot =>
                    slot.Role == LongFormAssetRole.Logo);
            Assert.AreSame(fixture.Logo, logoSlot.Asset);
            Assert.AreSame(fixture.Logo.Rights, logoSlot.Rights);
            Assert.AreSame(
                fixture.Logo.Rights,
                plan.TemplateAssets.Single(asset =>
                    asset.ParameterName == "logo").Rights);
            Assert.AreSame(
                fixture.Logo.Rights,
                plan.ReferencedAssets.Single(asset =>
                    asset.Id == fixture.Logo.Id).Rights);
            Assert.AreEqual(status, logoSlot.Rights!.Status);
            Assert.IsFalse(plan.ExternalRightsReady);
            Assert.IsFalse(plan.ExternalPublicationReady);
            Assert.AreEqual(
                1,
                plan.Warnings.Count(warning =>
                    warning.Code == expectedWarning
                    && warning.AssetId == fixture.Logo.Id));
            Assert.AreEqual(
                1,
                plan.Warnings.Count(warning =>
                    warning.Code
                        == LongFormWarningCode
                            .AssetExternalPublicationNotAllowed
                    && warning.AssetId == fixture.Logo.Id));
        }
    }

    [TestMethod]
    public void CanonicalOrderingMakesEquivalentRequestsDigestIdentically()
    {
        Fixture fixture = new();
        LongFormCompositionRequest canonical = fixture.CreateRequest(
            reverseTopicInput: false,
            reverseOrigins: false);
        LongFormCompositionRequest reordered = fixture.CreateRequest(
            reverseTopicInput: true,
            reverseOrigins: true);

        LongFormCompositionPlan first = LongFormComposer.Compose(canonical);
        LongFormCompositionPlan second = LongFormComposer.Compose(reordered);
        LongFormCompositionPlan repeated = LongFormComposer.Compose(canonical);

        Assert.AreEqual(first.PlanDigest, second.PlanDigest);
        Assert.AreEqual(first.PlanDigest, repeated.PlanDigest);
        CollectionAssert.AreEqual(
            first.Elements.Select(static element => element.Id).ToArray(),
            second.Elements.Select(static element => element.Id).ToArray());
        Assert.AreEqual(64, first.PlanDigest.Hex.Length);

        LongFormElementInput selectedCallToAction = CallToAction(canonical);
        LongFormElementInput changedCallToAction = new(
            selectedCallToAction.Id,
            LongFormElementKind.CallToAction,
            enabled: true,
            "Read the release notes",
            selectedCallToAction.OutputRange,
            selectedCallToAction.Origins,
            assets: []);
        LongFormCallToActionInput[] changedCallToActions =
            canonical.CallToActions
                .Select(callToAction => callToAction.Destination
                    == canonical.Destination
                        ? new LongFormCallToActionInput(
                            callToAction.Destination,
                            changedCallToAction)
                        : callToAction)
                .ToArray();
        LongFormCompositionPlan changed = LongFormComposer.Compose(
            Replace(canonical, callToActions: changedCallToActions));
        Assert.AreNotEqual(first.PlanDigest, changed.PlanDigest);
    }

    [TestMethod]
    public void MissingEvidenceMismatchedAssetsAndConflictingRangesFailClosed()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();

        LongFormElementInput missingOrigin = new(
            request.ProgramName.Id,
            LongFormElementKind.ProgramName,
            enabled: true,
            request.ProgramName.Text,
            request.ProgramName.OutputRange,
            origins: [],
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                programName: missingOrigin)),
            LongFormComposerFailureReason.OriginEvidenceMissing);

        LongFormElementInput duplicateElementId = new(
            request.Opening.Id,
            LongFormElementKind.ProgramName,
            enabled: true,
            request.ProgramName.Text,
            request.ProgramName.OutputRange,
            request.ProgramName.Origins,
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                programName: duplicateElementId)),
            LongFormComposerFailureReason.DuplicateElementId);

        OriginRef programOrigin = request.ProgramName.Origins.Single();
        LongFormElementInput duplicateOrigins = new(
            request.ProgramName.Id,
            LongFormElementKind.ProgramName,
            enabled: true,
            request.ProgramName.Text,
            request.ProgramName.OutputRange,
            [programOrigin, programOrigin],
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                programName: duplicateOrigins)),
            LongFormComposerFailureReason.OriginEvidenceMissing);

        LongFormElementInput wrongBodyDigest = new(
            request.Body.Id,
            LongFormElementKind.Body,
            enabled: true,
            text: null,
            request.Body.OutputRange,
            request.Body.Origins,
            [new LongFormAssetReference(
                LongFormAssetRole.BodyMedia,
                fixture.BodyMedia.Id,
                ContentDigest.Compute(Encoding.UTF8.GetBytes("wrong body")))]);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                body: wrongBodyDigest)),
            LongFormComposerFailureReason.AssetBindingMismatch);

        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                removeSpeakerAssignments: true)),
            LongFormComposerFailureReason.SpeakerEvidenceMissing);

        LongFormElementInput selectedCallToAction = CallToAction(request);
        LongFormElementInput overlappingCallToAction = new(
            selectedCallToAction.Id,
            LongFormElementKind.CallToAction,
            enabled: true,
            selectedCallToAction.Text,
            Range(28, 2),
            selectedCallToAction.Origins,
            assets: []);
        LongFormCallToActionInput[] overlappingCallToActions =
            request.CallToActions
                .Select(callToAction => callToAction.Destination
                    == request.Destination
                        ? new LongFormCallToActionInput(
                            callToAction.Destination,
                            overlappingCallToAction)
                        : callToAction)
                .ToArray();
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                callToActions: overlappingCallToActions)),
            LongFormComposerFailureReason.TimingConflict);

        LongFormCompositionRequest untimed = fixture.CreateRequest(
            includeTimings: false);
        LongFormElementInput negativeOpening = new(
            untimed.Opening.Id,
            LongFormElementKind.Opening,
            enabled: true,
            text: null,
            Range(-1, 1),
            untimed.Opening.Origins,
            untimed.Opening.Assets);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                untimed,
                opening: negativeOpening)),
            LongFormComposerFailureReason.TimingConflict);

        LongFormElementInput zeroDurationProgramName = new(
            request.ProgramName.Id,
            LongFormElementKind.ProgramName,
            enabled: true,
            request.ProgramName.Text,
            Range(3, 0),
            request.ProgramName.Origins,
            assets: []);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                programName: zeroDurationProgramName)),
            LongFormComposerFailureReason.TimingConflict);

        LongFormTopicInput firstTopic = request.Topics.Single(topic =>
            topic.TopicRange.TopicRangeId
                == fixture.Topics[0].Range.TopicRangeId);
        AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                topics: [firstTopic, fixture.CreateOverlappingTopicInput()])),
            LongFormComposerFailureReason.TopicOrderConflict);

        LongFormElementInput invalidText = new(
            request.Summary.Id,
            LongFormElementKind.Summary,
            enabled: true,
            "private\uD800title",
            request.Summary.OutputRange,
            request.Summary.Origins,
            assets: []);
        LongFormComposerException privateFailure = AssertFailure(
            () => LongFormComposer.Compose(Replace(
                request,
                summary: invalidText)),
            LongFormComposerFailureReason.InvalidText);
        Assert.IsFalse(privateFailure.ToString().Contains(
            "private",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void ComposeHonorsPreCancellation()
    {
        Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            LongFormComposer.Compose(
                fixture.CreateRequest(),
                cancellation.Token));
    }

    [TestMethod]
    public void PublicPlanSurfaceIsImmutableAndCollectionsAreDefensive()
    {
        Fixture fixture = new();
        LongFormCompositionRequest request = fixture.CreateRequest();
        LongFormCompositionPlan plan = LongFormComposer.Compose(request);

        foreach (Type type in new[]
        {
            typeof(LongFormAssetReference),
            typeof(LongFormElementInput),
            typeof(LongFormTopicInput),
            typeof(LongFormCallToActionInput),
            typeof(LongFormProvenanceSnapshot),
            typeof(LongFormCompositionRequest),
            typeof(LongFormAssetSlot),
            typeof(LongFormTemplateAssetSnapshot),
            typeof(LongFormElementPlan),
            typeof(LongFormWarning),
            typeof(LongFormCompositionPlan),
            typeof(LongFormComposerException),
        })
        {
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        foreach (Type type in new[]
        {
            typeof(LongFormAssetSlot),
            typeof(LongFormTemplateAssetSnapshot),
            typeof(LongFormElementPlan),
            typeof(LongFormWarning),
            typeof(LongFormCompositionPlan),
            typeof(LongFormComposerException),
        })
        {
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        }

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormElementPlan>)plan.Elements).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormTemplateAssetSnapshot>)plan.TemplateAssets)
                .Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<Asset>)plan.ReferencedAssets).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormWarning>)plan.Warnings).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OriginRef>)plan.Elements[0].Origins).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormAssetSlot>)Element(
                plan,
                LongFormElementKind.Opening).AssetSlots).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormTopicInput>)request.Topics).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormCallToActionInput>)request.CallToActions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SourceSpan>)request.Provenance.SourceSpans).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EntityId<EditorialRecordIdTag>>)request.Provenance
                .TrustedEditorialRecordIds).Clear());
        FieldInfo? evidenceModel = typeof(LongFormProvenanceSnapshot).GetField(
            nameof(LongFormProvenanceSnapshot.EditorialRecordEvidenceModel),
            BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(evidenceModel);
        Assert.IsTrue(evidenceModel.IsLiteral);
        Assert.AreEqual(
            "trusted-id-catalog-only",
            evidenceModel.GetRawConstantValue());

        var mutableOrigins = new List<OriginRef>
        {
            OriginRef.FromAsset(fixture.OpeningMedia.Id),
        };
        var mutableAssets = new List<LongFormAssetReference>
        {
            new(
                LongFormAssetRole.OpeningMedia,
                fixture.OpeningMedia.Id,
                fixture.OpeningMedia.ContentDigest),
        };
        var input = new LongFormElementInput(
            ElementId(99),
            LongFormElementKind.Opening,
            enabled: true,
            text: null,
            outputRange: null,
            mutableOrigins,
            mutableAssets);
        mutableOrigins.Clear();
        mutableAssets.Clear();
        Assert.HasCount(1, input.Origins);
        Assert.HasCount(1, input.Assets);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OriginRef>)input.Origins).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LongFormAssetReference>)input.Assets).Clear());

        Type composer = typeof(LongFormComposer);
        Assert.IsTrue(composer.IsAbstract && composer.IsSealed);
        CollectionAssert.AreEqual(
            new[] { "Compose" },
            composer.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
    }

    private static LongFormElementPlan Element(
        LongFormCompositionPlan plan,
        LongFormElementKind kind) =>
        plan.Elements.Single(element => element.Kind == kind);

    private static Project ProjectWithDeadline(
        Project project,
        DateTimeOffset deadline) =>
        new(
            project.Id,
            project.ContentName,
            project.Purpose,
            project.Audience,
            project.PublicationScope,
            deadline,
            project.Assets,
            project.Performers);

    private static void AssertSlotPreservesAsset(
        LongFormElementPlan element,
        Asset expected)
    {
        LongFormAssetSlot slot = element.AssetSlots.Single(candidate =>
            candidate.Asset?.Id == expected.Id);
        Assert.AreEqual(LongFormAssetSlotState.Included, slot.State);
        Assert.AreSame(expected, slot.Asset);
        Assert.AreSame(expected.Rights, slot.Rights);
        Assert.AreEqual(OriginKind.Asset, slot.Origin!.Kind);
        Assert.AreEqual(expected.Id, slot.Origin.AssetId);
    }

    private static void AssertTemplateAssetPreserved(
        LongFormCompositionPlan plan,
        string parameterName,
        Asset expected)
    {
        LongFormTemplateAssetSnapshot snapshot = plan.TemplateAssets.Single(
            asset => asset.ParameterName == parameterName);
        Assert.AreSame(expected, snapshot.Asset);
        Assert.AreSame(expected.Rights, snapshot.Rights);
        Assert.AreEqual(expected.Id, snapshot.Origin.AssetId);
    }

    private static LongFormComposerException AssertFailure(
        Action action,
        LongFormComposerFailureReason reason)
    {
        LongFormComposerException exception =
            Assert.ThrowsExactly<LongFormComposerException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static LongFormCompositionRequest Replace(
        LongFormCompositionRequest request,
        Project? project = null,
        LongFormProvenanceSnapshot? provenance = null,
        LongFormDestination? destination = null,
        SpeakerAssignmentHistory? speakerAssignments = null,
        LongFormElementInput? opening = null,
        LongFormElementInput? programName = null,
        LongFormElementInput? episodeNumber = null,
        LongFormElementInput? body = null,
        IReadOnlyList<LongFormTopicInput>? topics = null,
        LongFormElementInput? summary = null,
        IReadOnlyList<LongFormCallToActionInput>? callToActions = null,
        bool removeSpeakerAssignments = false) =>
        new(
            request.PlanId,
            project ?? request.Project,
            provenance ?? request.Provenance,
            request.Template,
            request.Profile,
            destination ?? request.Destination,
            removeSpeakerAssignments
                ? null
                : speakerAssignments ?? request.SpeakerAssignments,
            request.PreRollMargin,
            opening ?? request.Opening,
            programName ?? request.ProgramName,
            episodeNumber ?? request.EpisodeNumber,
            request.EpisodeTitle,
            request.PerformerIntroduction,
            request.PublicationScopeIndicator,
            body ?? request.Body,
            topics ?? request.Topics,
            summary ?? request.Summary,
            callToActions ?? request.CallToActions,
            request.Ending,
            request.PostRollMargin);

    private static LongFormElementInput CallToAction(
        LongFormCompositionRequest request) =>
        request.CallToActions.Single(callToAction =>
            callToAction.Destination == request.Destination).Element;

    private static LongFormElementInput Omit(LongFormElementInput input) =>
        new(
            input.Id,
            input.Kind,
            enabled: false,
            text: null,
            outputRange: null,
            origins: [],
            assets: []);

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static EntityId<LongFormElementIdTag> ElementId(int value) =>
        Id<LongFormElementIdTag>(
            $"a2000000-0000-0000-0000-{value:000000000000}");

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed record TopicEvidence(
        int Ordinal,
        TopicRangeRevision Range,
        TopicMetadataRevision Metadata,
        SourceSpan SourceSpan);

    private sealed class Fixture
    {
        private readonly TopicEvidence _overlappingTopic;

        internal Fixture(
            RightsStatus logoRightsStatus = RightsStatus.Approved)
        {
            Profile = BundledRenderProfileLoader.Load(File.ReadAllBytes(
                ResolveRepositoryPath(
                    "src/HubRadio.App/Profiles/16x9/long-form.json")));
            RightsRecord approved = new(
                RightsStatus.Approved,
                "evidence://r015-approved",
                "R-015 composition verification",
                externalPublicationAllowed: true);
            RightsRecord logoRights = logoRightsStatus switch
            {
                RightsStatus.Approved => approved,
                RightsStatus.Unconfirmed => new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal review only",
                    externalPublicationAllowed: false),
                RightsStatus.Rejected => new RightsRecord(
                    RightsStatus.Rejected,
                    "evidence://r015-rejected",
                    "Use rejected",
                    externalPublicationAllowed: false),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(logoRightsStatus)),
            };
            OpeningMedia = CreateAsset(
                1,
                "opening.mp4",
                "video/mp4",
                approved);
            KeyVisual = CreateAsset(
                2,
                "key-visual.png",
                "image/png",
                approved);
            Logo = CreateAsset(3, "logo.svg", "image/svg+xml", logoRights);
            Jingle = CreateAsset(4, "jingle.wav", "audio/wav", approved);
            BodyMedia = CreateAsset(5, "episode.mp4", "video/mp4", approved);
            EndingMedia = CreateAsset(6, "ending.mp4", "video/mp4", approved);
            Performer = new Performer(
                Id<PerformerIdTag>(
                    "a5000000-0000-0000-0000-000000000001"),
                "Host Alpha",
                "Host",
                CreatedAt.AddDays(-1));
            Project = new Project(
                Id<ProjectIdTag>(
                    "a6000000-0000-0000-0000-000000000001"),
                "Episode 42",
                "Verify deterministic long-form composition.",
                "External developers",
                PublicationScope.ExternalPublicationApproved,
                CreatedAt.AddDays(30),
                [BodyMedia, Logo, OpeningMedia, EndingMedia, Jingle, KeyVisual],
                [Performer]);

            SpeakerId = Id<TranscriptSpeakerIdTag>(
                "a7000000-0000-0000-0000-000000000001");
            EntityId<TranscriptRevisionIdTag> transcriptId =
                Id<TranscriptRevisionIdTag>(
                    "ac000000-0000-0000-0000-000000000001");
            (Utterance firstUtterance, SourceSpan firstSpan) =
                CreateUtteranceAndSpan(
                    1,
                    "Architecture statement",
                    Range(0, 5),
                    transcriptId);
            (Utterance secondUtterance, SourceSpan secondSpan) =
                CreateUtteranceAndSpan(
                    2,
                    "Delivery statement",
                    Range(5, 5),
                    transcriptId);
            SourceSpans = Array.AsReadOnly(
                new[] { firstSpan, secondSpan });
            Transcript = new TranscriptRevision(
                transcriptId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [firstUtterance, secondUtterance]);
            SpeakerAssignments = SpeakerAssignmentService.SetAssignment(
                Project,
                Transcript,
                history: null,
                Id<SpeakerAssignmentRevisionIdTag>(
                    "b2000000-0000-0000-0000-000000000001"),
                SpeakerId,
                Performer.Id,
                "Alpha",
                SpeakerDisplayFormat.FullName,
                ActorSid,
                CreatedAt,
                "Confirm the long-form performer.");
            BoundaryScan = TopicBoundaryService.FindCandidates(
                Transcript,
                SourceSpans,
                Id<ModelRunIdTag>(
                    "ad000000-0000-0000-0000-000000000001"));
            Provenance = CreateProvenance();
            Template = CreateTemplate(includeAssetParameters: true);
            Topics = Array.AsReadOnly(new[]
            {
                CreateTopic(1, Range(0, 5), firstSpan, "Architecture"),
                CreateTopic(2, Range(5, 5), secondSpan, "Delivery"),
            });
            _overlappingTopic = CreateTopic(
                3,
                Range(4, 6),
                secondSpan,
                "Overlapping topic");
        }

        internal BundledRenderProfileDefinition Profile { get; }

        internal Asset OpeningMedia { get; }

        internal Asset KeyVisual { get; }

        internal Asset Logo { get; }

        internal Asset Jingle { get; }

        internal Asset BodyMedia { get; }

        internal Asset EndingMedia { get; }

        internal Performer Performer { get; }

        internal Project Project { get; }

        internal EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

        internal TranscriptRevision Transcript { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal LongFormProvenanceSnapshot Provenance { get; }

        internal SpeakerAssignmentHistory SpeakerAssignments { get; }

        internal TopicBoundaryScanResult BoundaryScan { get; }

        internal AppliedBrandTemplate Template { get; }

        internal IReadOnlyList<TopicEvidence> Topics { get; }

        internal AppliedBrandTemplate CreateTemplate(
            bool includeAssetParameters,
            TemplateParameterValue? programNameValue = null,
            TemplateParameterValue? episodeNumberValue = null,
            Asset? inventoryOnlyAsset = null)
        {
            var definitions = new List<TemplateParameterDefinition>
            {
                new(
                    "program-name",
                    programNameValue
                        ?? TemplateParameterValue.FromText("Hub Radio"),
                    editorOverrideAllowed: false),
                new(
                    "episode-number",
                    episodeNumberValue
                        ?? TemplateParameterValue.FromInteger(42),
                    editorOverrideAllowed: false),
            };
            if (includeAssetParameters)
            {
                definitions.Add(new TemplateParameterDefinition(
                    "key-visual",
                    TemplateParameterValue.FromAssetReference(
                        KeyVisual.Id,
                        KeyVisual.ContentDigest),
                    editorOverrideAllowed: false));
                definitions.Add(new TemplateParameterDefinition(
                    "logo",
                    TemplateParameterValue.FromAssetReference(
                        Logo.Id,
                        Logo.ContentDigest),
                    editorOverrideAllowed: false));
                definitions.Add(new TemplateParameterDefinition(
                    "jingle",
                    TemplateParameterValue.FromAssetReference(
                        Jingle.Id,
                        Jingle.ContentDigest),
                    editorOverrideAllowed: false));
            }

            if (inventoryOnlyAsset is not null)
            {
                definitions.Add(new TemplateParameterDefinition(
                    "inventory-only-asset",
                    TemplateParameterValue.FromAssetReference(
                        inventoryOnlyAsset.Id,
                        inventoryOnlyAsset.ContentDigest),
                    editorOverrideAllowed: false));
            }

            var version = new BrandTemplateVersion(
                Id<SeriesIdTag>(
                    "b3000000-0000-0000-0000-000000000001"),
                new TemplateReference(
                    Profile.Profile.TemplateId,
                    "R-015 test template",
                    Profile.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                definitions);
            return version.ApplyEditorOverrides([]);
        }

        internal LongFormCompositionRequest CreateRequest(
            bool includeTimings = true,
            bool includeOptionalElements = true,
            bool includeOptionalAssets = true,
            bool reverseTopicInput = false,
            bool reverseOrigins = false,
            LongFormDestination destination = LongFormDestination.YouTube,
            AppliedBrandTemplate? template = null,
            LongFormProvenanceSnapshot? provenance = null)
        {
            TimeRange? Timing(long start, long duration) =>
                includeTimings ? Range(start, duration) : null;

            LongFormElementInput preRoll = includeOptionalElements
                ? Included(
                    1,
                    LongFormElementKind.PreRollMargin,
                    text: null,
                    Timing(0, 1),
                    [EditorialOrigin(1)],
                    assets: [],
                    reverseOrigins)
                : Omitted(1, LongFormElementKind.PreRollMargin);
            var openingReferences = new List<LongFormAssetReference>
            {
                AssetReference(LongFormAssetRole.OpeningMedia, OpeningMedia),
            };
            if (includeOptionalAssets)
            {
                openingReferences.Add(AssetReference(
                    LongFormAssetRole.KeyVisual,
                    KeyVisual));
                openingReferences.Add(AssetReference(
                    LongFormAssetRole.Logo,
                    Logo));
                openingReferences.Add(AssetReference(
                    LongFormAssetRole.Jingle,
                    Jingle));
            }

            OriginRef[] openingOrigins =
            [
                EditorialOrigin(2),
                .. openingReferences.Select(reference =>
                    OriginRef.FromAsset(reference.AssetId)),
            ];
            LongFormElementInput opening = Included(
                2,
                LongFormElementKind.Opening,
                text: null,
                Timing(1, 2),
                openingOrigins,
                openingReferences,
                reverseOrigins);
            LongFormElementInput programName = includeOptionalElements
                ? Included(
                    3,
                    LongFormElementKind.ProgramName,
                    "Hub Radio",
                    Timing(3, 1),
                    [EditorialOrigin(3)],
                    assets: [],
                    reverseOrigins)
                : Omitted(3, LongFormElementKind.ProgramName);
            LongFormElementInput episodeNumber = includeOptionalElements
                ? Included(
                    4,
                    LongFormElementKind.EpisodeNumber,
                    "42",
                    Timing(4, 1),
                    [EditorialOrigin(4)],
                    assets: [],
                    reverseOrigins)
                : Omitted(4, LongFormElementKind.EpisodeNumber);
            LongFormElementInput episodeTitle = includeOptionalElements
                ? Included(
                    5,
                    LongFormElementKind.EpisodeTitle,
                    Project.ContentName,
                    Timing(5, 1),
                    [EditorialOrigin(5)],
                    assets: [],
                    reverseOrigins)
                : Omitted(5, LongFormElementKind.EpisodeTitle);
            LongFormElementInput performer = includeOptionalElements
                ? Included(
                    6,
                    LongFormElementKind.PerformerIntroduction,
                    text: null,
                    Timing(6, 1),
                    [EditorialOrigin(6)],
                    assets: [],
                    reverseOrigins)
                : Omitted(6, LongFormElementKind.PerformerIntroduction);
            LongFormElementInput publicationScope = includeOptionalElements
                ? Included(
                    7,
                    LongFormElementKind.PublicationScopeIndicator,
                    text: null,
                    Timing(7, 1),
                    [EditorialOrigin(7)],
                    assets: [],
                    reverseOrigins)
                : Omitted(7, LongFormElementKind.PublicationScopeIndicator);
            LongFormElementInput body = Included(
                8,
                LongFormElementKind.Body,
                text: null,
                Timing(8, 20),
                [
                    OriginRef.FromSourceSpan(SourceSpans[0].Id),
                    OriginRef.FromSourceSpan(SourceSpans[1].Id),
                    OriginRef.FromAsset(BodyMedia.Id),
                ],
                [AssetReference(LongFormAssetRole.BodyMedia, BodyMedia)],
                reverseOrigins);
            LongFormTopicInput[] topics = Topics
                .Select(topic => CreateTopicInput(
                    topic,
                    includeTimings,
                    reverseOrigins,
                    outputStart: 8 + ((topic.Ordinal - 1) * 10)))
                .ToArray();
            if (reverseTopicInput)
            {
                Array.Reverse(topics);
            }

            LongFormElementInput summary = includeOptionalElements
                ? Included(
                    10,
                    LongFormElementKind.Summary,
                    "Episode summary",
                    Timing(28, 2),
                    [EditorialOrigin(10)],
                    assets: [],
                    reverseOrigins)
                : Omitted(10, LongFormElementKind.Summary);
            LongFormCallToActionInput[] callToActions =
            [
                new(
                    LongFormDestination.InternalDistribution,
                    Included(
                        14,
                        LongFormElementKind.CallToAction,
                        "Join the internal community",
                        Timing(30, 1),
                        [EditorialOrigin(14)],
                        assets: [],
                        reverseOrigins)),
                new(
                    LongFormDestination.YouTube,
                    Included(
                        11,
                        LongFormElementKind.CallToAction,
                        "Listen to the next episode",
                        Timing(30, 1),
                        [EditorialOrigin(11)],
                        assets: [],
                        reverseOrigins)),
            ];
            IReadOnlyList<LongFormAssetReference> endingAssets =
                includeOptionalAssets
                    ? [AssetReference(
                        LongFormAssetRole.EndingMedia,
                        EndingMedia)]
                    : [];
            OriginRef[] endingOrigins =
            [
                EditorialOrigin(12),
                .. endingAssets.Select(reference =>
                    OriginRef.FromAsset(reference.AssetId)),
            ];
            LongFormElementInput ending = Included(
                12,
                LongFormElementKind.Ending,
                text: null,
                Timing(31, 2),
                endingOrigins,
                endingAssets,
                reverseOrigins);
            LongFormElementInput postRoll = includeOptionalElements
                ? Included(
                    13,
                    LongFormElementKind.PostRollMargin,
                    text: null,
                    Timing(33, 1),
                    [EditorialOrigin(13)],
                    assets: [],
                    reverseOrigins)
                : Omitted(13, LongFormElementKind.PostRollMargin);

            return new LongFormCompositionRequest(
                Id<LongFormPlanIdTag>(
                    "a1000000-0000-0000-0000-000000000001"),
                Project,
                provenance ?? Provenance,
                template ?? Template,
                Profile,
                destination,
                SpeakerAssignments,
                preRoll,
                opening,
                programName,
                episodeNumber,
                episodeTitle,
                performer,
                publicationScope,
                body,
                topics,
                summary,
                callToActions,
                ending,
                postRoll);
        }

        internal LongFormTopicInput CreateOverlappingTopicInput() =>
            CreateTopicInput(
                _overlappingTopic,
                includeTiming: true,
                reverseOrigins: false,
                outputStart: 17);

        private TopicEvidence CreateTopic(
            int ordinal,
            TimeRange range,
            SourceSpan sourceSpan,
            string title)
        {
            TopicRangeRevision topicRange =
                TopicBoundaryService.CreateTopicRange(
                    BoundaryScan,
                    Id<TopicRangeIdTag>(
                        $"ae000000-0000-0000-0000-{ordinal:000000000000}"),
                    Id<TopicRangeRevisionIdTag>(
                        $"af000000-0000-0000-0000-{ordinal:000000000000}"),
                    startBoundarySuggestionId: null,
                    endBoundarySuggestionId: null,
                    range,
                    ActorSid,
                    CreatedAt.AddMinutes(ordinal),
                    "Define an exact long-form topic range.");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(
                Project,
                Transcript,
                SourceSpans,
                topicRange,
                SpeakerAssignments,
                ordinal,
                Id<TopicMetadataIdTag>(
                    $"b0000000-0000-0000-0000-{ordinal:000000000000}"),
                MetadataRevisionId((ordinal * 10) + 1),
                ActorSid,
                CreatedAt.AddMinutes((ordinal * 10) + 1),
                "Create exact long-form topic metadata.");
            metadata = TopicMetadataService.SetTitle(
                Project,
                Transcript,
                SourceSpans,
                topicRange,
                SpeakerAssignments,
                metadata,
                MetadataRevisionId((ordinal * 10) + 2),
                title,
                ActorSid,
                CreatedAt.AddMinutes((ordinal * 10) + 2),
                "Confirm the topic title.");
            metadata = TopicMetadataService.SetImportantStatements(
                Project,
                Transcript,
                SourceSpans,
                topicRange,
                SpeakerAssignments,
                metadata,
                MetadataRevisionId((ordinal * 10) + 3),
                [sourceSpan.Id],
                ActorSid,
                CreatedAt.AddMinutes((ordinal * 10) + 3),
                "Confirm the topic source statement.");
            return new TopicEvidence(ordinal, topicRange, metadata, sourceSpan);
        }

        private LongFormTopicInput CreateTopicInput(
            TopicEvidence topic,
            bool includeTiming,
            bool reverseOrigins,
            long outputStart)
        {
            LongFormElementInput element = Included(
                20 + topic.Ordinal,
                LongFormElementKind.Topic,
                topic.Metadata.Metadata.Title,
                includeTiming ? Range(outputStart, 10) : null,
                [
                    EditorialOrigin(20 + topic.Ordinal),
                    OriginRef.FromSourceSpan(topic.SourceSpan.Id),
                ],
                assets: [],
                reverseOrigins);
            return new LongFormTopicInput(
                element,
                topic.Range,
                topic.Metadata);
        }

        private (Utterance Utterance, SourceSpan SourceSpan)
            CreateUtteranceAndSpan(
                int ordinal,
                string text,
                TimeRange range,
                EntityId<TranscriptRevisionIdTag> transcriptId)
        {
            EntityId<TranscriptTokenIdTag> tokenId =
                Id<TranscriptTokenIdTag>(
                    $"a8000000-0000-0000-0000-{ordinal:000000000000}");
            var token = new Token(
                tokenId,
                text,
                range: null,
                recognitionConfidence: 0.9);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    $"a9000000-0000-0000-0000-{ordinal:000000000000}"),
                text,
                [token]);
            EntityId<SourceSpanIdTag> sourceSpanId = Id<SourceSpanIdTag>(
                $"aa000000-0000-0000-0000-{ordinal:000000000000}");
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    $"ab000000-0000-0000-0000-{ordinal:000000000000}"),
                sourceSpanId,
                SpeakerId,
                "Speaker 1",
                text,
                range,
                recognitionConfidence: 0.9,
                [sentence]);
            var sourceSpan = new SourceSpan(
                sourceSpanId,
                BodyMedia.Id,
                "video:1",
                range,
                BodyMedia.ContentDigest,
                transcriptId,
                [tokenId]);
            return (utterance, sourceSpan);
        }

        private LongFormProvenanceSnapshot CreateProvenance()
        {
            EntityId<EditorialRecordIdTag>[] editorialRecordIds =
                Enumerable.Range(1, 14)
                    .Concat([21, 22])
                    .Select(ordinal => EditorialOrigin(ordinal)
                        .EditorialRecordId!)
                    .ToArray();
            return new LongFormProvenanceSnapshot(
                Project.Id,
                Transcript.Id,
                SpeakerAssignments.TranscriptRevisionDigest,
                SourceSpans,
                editorialRecordIds);
        }

        private static Asset CreateAsset(
            int ordinal,
            string fileName,
            string mediaType,
            RightsRecord rights) =>
            new(
                Id<AssetIdTag>(
                    $"a4000000-0000-0000-0000-{ordinal:000000000000}"),
                fileName,
                mediaType,
                ContentDigest.Compute(Encoding.UTF8.GetBytes(fileName)),
                byteSize: 1000 + ordinal,
                rights);

        private static LongFormAssetReference AssetReference(
            LongFormAssetRole role,
            Asset asset) =>
            new(role, asset.Id, asset.ContentDigest);

        private static LongFormElementInput Included(
            int ordinal,
            LongFormElementKind kind,
            string? text,
            TimeRange? outputRange,
            IReadOnlyList<OriginRef> origins,
            IReadOnlyList<LongFormAssetReference> assets,
            bool reverseOrigins)
        {
            OriginRef[] copiedOrigins = origins.ToArray();
            if (reverseOrigins)
            {
                Array.Reverse(copiedOrigins);
            }

            return new LongFormElementInput(
                ElementId(ordinal),
                kind,
                enabled: true,
                text,
                outputRange,
                copiedOrigins,
                assets);
        }

        private static LongFormElementInput Omitted(
            int ordinal,
            LongFormElementKind kind) =>
            new(
                ElementId(ordinal),
                kind,
                enabled: false,
                text: null,
                outputRange: null,
                origins: [],
                assets: []);

        private static OriginRef EditorialOrigin(int ordinal) =>
            OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(
                $"a3000000-0000-0000-0000-{ordinal:000000000000}"));

        private static EntityId<TopicMetadataRevisionIdTag>
            MetadataRevisionId(int value) =>
            Id<TopicMetadataRevisionIdTag>(
                $"b1000000-0000-0000-0000-{value:000000000000}");

        private static string ResolveRepositoryPath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                {
                    string path = Path.Combine(
                        directory.FullName,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar));
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException(
                            $"Expected repository-relative file '{relativePath}'.",
                            path);
                    }

                    return path;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not resolve repository-relative path '{relativePath}'.");
        }
    }
}
