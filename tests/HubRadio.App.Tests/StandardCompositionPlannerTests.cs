using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardCompositionPlannerTests
{
    [TestMethod]
    public void GroundedProposedHighlightBecomesCandidateWithoutBeingAdopted()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate proposed = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest([proposed], adoptedHighlights: []));

        Assert.HasCount(1, plan.ShortForms);
        Assert.IsEmpty(plan.Thumbnails);
        Assert.IsEmpty(plan.ShortCandidateExclusions);
        Assert.AreEqual(
            StandardShortSelectionKind.GroundedCandidate,
            plan.ShortForms[0].SelectionKind);
        Assert.AreEqual(SuggestionStatus.Proposed, proposed.Status);
        Assert.AreEqual(
            proposed.Suggestion.Reason,
            plan.ShortForms[0].SelectionReason);
        Assert.AreEqual(
            proposed.Suggestion.MissingContext,
            plan.ShortForms[0].MissingContext);
        Assert.AreEqual(
            proposed.Suggestion.Warning,
            plan.ShortForms[0].SelectionWarning);
        CollectionAssert.AreEqual(
            new[]
            {
                StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
                StandardCompositionOmissionCode.NoMeasuredSourceFrame,
            },
            plan.Omissions.Select(static omission => omission.Code).ToArray());
        Assert.HasCount(17, plan.InputDigests);
    }

    // FR-010 AC-002 / FR-132 AC-136: a grounded speech-segment candidate (15-60 s) becomes a
    // short-form plan (the clean variant is generated per short plan, see
    // StandardArtifactGeneratorTests); without an explicit question it carries no hook.
    [TestMethod]
    public void SpeechSegmentCandidateBecomesGroundedShortWithoutHook()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate speech = fixture.CreateCandidate(
            ordinal: 1,
            start: 0,
            duration: 28,
            tokenOrdinal: 1,
            reason: EmphasisReasonKind.SpeechSegment);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest([speech], adoptedHighlights: []));

        Assert.HasCount(1, plan.ShortForms);
        Assert.IsEmpty(plan.ShortCandidateExclusions);
        Assert.AreEqual(StandardShortSelectionKind.GroundedCandidate, plan.ShortForms[0].SelectionKind);
        Assert.IsNull(plan.ShortForms[0].Hook);
    }

    [TestMethod]
    public void ExplicitlyAdoptedHighlightProducesOneGroundedShortOnly()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1,
            reason: EmphasisReasonKind.AnswerAfterExplicitQuestion);
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(candidate);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest([candidate], [adoption]));

        Assert.HasCount(1, plan.ShortForms);
        StandardShortFormPlan shortForm = plan.ShortForms[0];
        Assert.AreEqual(
            StandardShortSelectionKind.ExplicitAdoption,
            shortForm.SelectionKind);
        Assert.AreEqual(candidate.Id, shortForm.SelectedHighlight.SuggestionId);
        Assert.AreSame(candidate.Payload.SourceSpan, shortForm.SourceSpans[0]);
        Assert.AreEqual(
            StandardCallToActionKind.ViewLongForm,
            shortForm.CallToAction.Kind);
        Assert.AreEqual(
            fixture.LongForm.PlanId,
            shortForm.CallToAction.TargetLongFormPlanId);
        Assert.HasCount(1, shortForm.SubtitleVariants);
        Assert.AreEqual(
            StandardSubtitleVariantKind.BurnedIn,
            shortForm.SubtitleVariants[0].Kind);
        Assert.IsNotNull(shortForm.Hook);
        Assert.AreEqual(candidate.Id, shortForm.Hook.SelectedHighlight.SuggestionId);
        Assert.AreEqual(1080, plan.ShortFormProfile.Profile.Width);
        Assert.AreEqual(1920, plan.ShortFormProfile.Profile.Height);
        Assert.IsTrue(plan.ShortFormProfile.Profile.SubtitleBurnIn);
        Assert.HasCount(4, plan.LongFormSubtitleVariants);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
            plan.Omissions.Single(omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm).Code);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoMeasuredSourceFrame,
            plan.Omissions.Single(omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail).Code);
    }

    [TestMethod]
    public void AdoptedAndProposedCandidatesRemainDistinctSelections()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate adopted = fixture.CreateCandidate(
            ordinal: 1,
            start: 0,
            duration: 15,
            tokenOrdinal: 1);
        EmphasisSuggestionCandidate proposed = fixture.CreateCandidate(
            ordinal: 2,
            start: 20,
            duration: 15,
            tokenOrdinal: 2);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [adopted, proposed],
                [fixture.Adopt(adopted)]));

        Assert.HasCount(2, plan.ShortForms);
        CollectionAssert.AreEqual(
            new[]
            {
                StandardShortSelectionKind.ExplicitAdoption,
                StandardShortSelectionKind.GroundedCandidate,
            },
            plan.ShortForms.Select(static item => item.SelectionKind).ToArray());
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
            plan.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm).Code);
    }

    [TestMethod]
    public void ImportantStatementAloneDoesNotInventAQuestionOrConclusionHook()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1,
            reason: EmphasisReasonKind.ImportantStatement);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest([candidate], [fixture.Adopt(candidate)]));

        Assert.HasCount(1, plan.ShortForms);
        Assert.IsNull(plan.ShortForms[0].Hook);
    }

    [TestMethod]
    public void AdoptedShortDurationUsesInclusiveProductRangeAndTypedExclusions()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate tooShort =
            fixture.CreateCandidate(1, 0, 14, 1);
        EmphasisSuggestionCandidate minimum =
            fixture.CreateCandidate(2, 15, 15, 2);
        EmphasisSuggestionCandidate maximum =
            fixture.CreateCandidate(3, 35, 60, 3);
        EmphasisSuggestionCandidate tooLong =
            fixture.CreateCandidate(4, 0, 61, 4);
        EmphasisSuggestionCandidate[] candidates =
            [tooShort, minimum, maximum, tooLong];

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                candidates,
                candidates.Select(fixture.Adopt).ToArray()));

        CollectionAssert.AreEqual(
            new[] { minimum.Id, maximum.Id },
            plan.ShortForms.Select(static item =>
                item.SelectedHighlight.SuggestionId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { tooShort.Id, tooLong.Id },
            plan.ShortCandidateExclusions
                .Where(static item => item.Code
                    == StandardShortCandidateExclusionCode.DurationOutsidePolicy)
                .Select(static item => item.CandidateId)
                .ToArray());
    }

    [TestMethod]
    public void CandidateInLaterProvenanceSpanProducesGroundedShort()
    {
        Fixture fixture = new();
        SourceSpan firstEvidence = fixture.CreateEvidenceSpan(
            ordinal: 1,
            start: 0,
            duration: 50,
            tokenStart: 0,
            tokenCount: 10);
        SourceSpan secondEvidence = fixture.CreateEvidenceSpan(
            ordinal: 2,
            start: 50,
            duration: 50,
            tokenStart: 10,
            tokenCount: 10);
        CompositionVersion parent = fixture.CreateParent(
            [firstEvidence, secondEvidence]);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            Id<TimelineMapIdTag>(
                "26000000-0000-0000-0000-000000000099"),
            parent);
        LongFormCompositionPlan longForm = fixture.CreateLongForm(
            [firstEvidence, secondEvidence]);
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 9,
            start: 55,
            duration: 30,
            tokenOrdinal: 11);
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(candidate);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [candidate],
                [adoption],
                parent: parent,
                timelineMap: timelineMap,
                longForm: longForm));

        Assert.HasCount(1, plan.ShortForms);
        Assert.AreEqual(candidate.Id, plan.ShortForms[0].SelectedHighlight.SuggestionId);
        Assert.AreSame(candidate.Payload.SourceSpan, plan.ShortForms[0].SourceSpans[0]);
        Assert.IsFalse(plan.ShortCandidateExclusions.Any(exclusion =>
            exclusion.CandidateId == candidate.Id));
    }

    [TestMethod]
    public void SameSourceSpanIdCannotRedefineRangeOrTokens()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1,
            sourceSpanId: fixture.SourceSpan.Id);

        AssertFailure(
            () => StandardCompositionPlanner.Create(
                fixture.CreateRequest([candidate], [fixture.Adopt(candidate)])),
            StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch);
    }

    [TestMethod]
    public void SameSourceSpanIdCannotBeGroundedByDifferentProvenanceId()
    {
        Fixture fixture = new();
        SourceSpan duplicateEvidence = fixture.CloneEvidenceSpanWithId(
            fixture.SourceSpan,
            Id<SourceSpanIdTag>(
                "25000000-0000-0000-0000-000000000099"));
        CompositionVersion parent = fixture.CreateParent(
            [fixture.SourceSpan, duplicateEvidence]);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            Id<TimelineMapIdTag>(
                "26000000-0000-0000-0000-000000000098"),
            parent);
        LongFormCompositionPlan longForm = fixture.CreateLongForm(
            [fixture.SourceSpan, duplicateEvidence]);
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1,
            sourceSpanId: fixture.SourceSpan.Id);

        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [candidate],
                [fixture.Adopt(candidate)],
                parent: parent,
                timelineMap: timelineMap,
                longForm: longForm)),
            StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch);
    }

    [TestMethod]
    public void UnadoptedCandidateCannotRedefineAuthoritativeSourceSpanId()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(
            ordinal: 1,
            start: 5,
            duration: 30,
            tokenOrdinal: 1,
            sourceSpanId: fixture.SourceSpan.Id);

        AssertFailure(
            () => StandardCompositionPlanner.Create(
                fixture.CreateRequest([candidate], [])),
            StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch);
    }

    [TestMethod]
    public void CandidatesCannotGiveOneSourceSpanIdDifferentContent()
    {
        Fixture fixture = new();
        EntityId<SourceSpanIdTag> sharedId = Id<SourceSpanIdTag>(
            "31000000-0000-0000-0000-000000000099");
        EmphasisSuggestionCandidate first = fixture.CreateCandidate(
            ordinal: 1,
            start: 0,
            duration: 15,
            tokenOrdinal: 1,
            sourceSpanId: sharedId);
        EmphasisSuggestionCandidate second = fixture.CreateCandidate(
            ordinal: 2,
            start: 20,
            duration: 15,
            tokenOrdinal: 2,
            sourceSpanId: sharedId);

        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [first, second],
                [fixture.Adopt(first)])),
            StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch);
    }

    [TestMethod]
    public void PlannerRejectsLongFormSourceDifferentFromParentSource()
    {
        Fixture fixture = new();
        SourceSpan different = fixture.CreateEvidenceSpan(
            ordinal: 9,
            start: 0,
            duration: 100,
            tokenStart: 0,
            tokenCount: fixture.TokenIds.Count);
        LongFormCompositionPlan longForm = fixture.CreateLongForm([different]);

        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [],
                [],
                longForm: longForm)),
            StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
    }

    [TestMethod]
    public void InputPermutationsProduceSameCanonicalPlanIdentityAndOrder()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate first = fixture.CreateCandidate(1, 0, 15, 1);
        EmphasisSuggestionCandidate second = fixture.CreateCandidate(2, 20, 15, 2);
        HighlightSuggestionAdoptionResult firstAdoption = fixture.Adopt(first);
        HighlightSuggestionAdoptionResult secondAdoption = fixture.Adopt(second);

        StandardCompositionPlan forward = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [first, second],
                [firstAdoption, secondAdoption]));
        StandardCompositionPlan reverse = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [second, first],
                [secondAdoption, firstAdoption]));

        Assert.AreEqual(forward.Id, reverse.Id);
        Assert.AreEqual(forward.PlanDigest, reverse.PlanDigest);
        CollectionAssert.AreEqual(
            forward.ShortForms.Select(static item => item.Id).ToArray(),
            reverse.ShortForms.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            forward.ShortForms.Select(static item =>
                item.SelectedHighlight.SuggestionId).ToArray());
        CollectionAssert.AreEqual(
            forward.InputDigests.Select(static item => item.ContentDigest).ToArray(),
            reverse.InputDigests.Select(static item => item.ContentDigest).ToArray());
    }

    [TestMethod]
    public void EvaluationInputDigestBindsCompleteTopicMetadataSemantics()
    {
        Fixture fixture = new();
        TopicMetadataRevision original = fixture.TopicMetadataRevision;
        TopicMetadata metadata = original.Metadata;
        var changedMetadata = new TopicMetadata(
            metadata.Title,
            metadata.TitleOrigin,
            metadata.Summary,
            metadata.SummaryOrigin,
            metadata.MainSpeaker,
            metadata.MainSpeakerOrigin,
            metadata.MainSpeakerTieUnresolved,
            metadata.Keywords,
            metadata.KeywordsOrigin,
            metadata.ImportantStatementSourceSpanIds,
            metadata.ImportantStatementsOrigin,
            metadata.Audience,
            TopicMetadataValueOrigin.ManualEditorial,
            metadata.PublicationCautions,
            metadata.PublicationCautionsOrigin);
        var changed = new TopicMetadataRevision(
            original.TopicMetadataId,
            original.Id,
            original.ParentRevisionId,
            original.ProjectId,
            original.TopicRangeId,
            original.TopicRangeRevisionId,
            original.TopicRange,
            original.SourceRevisionId,
            original.SourceRevisionDigest,
            changedMetadata,
            original.ChangedField,
            original.ActorSid,
            original.CreatedAt,
            original.Reason);

        StandardCompositionPlan baseline = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], []));
        StandardCompositionPlan altered = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                topicMetadataRevisions: [changed]));

        ContentDigest baselineEvaluation = baseline.InputDigests.Single(
            static input => input.Kind
                == StandardPlanInputKind.EvaluationInput).ContentDigest;
        ContentDigest alteredEvaluation = altered.InputDigests.Single(
            static input => input.Kind
                == StandardPlanInputKind.EvaluationInput).ContentDigest;
        Assert.AreNotEqual(baselineEvaluation, alteredEvaluation);
        Assert.AreNotEqual(baseline.Id, altered.Id);
        Assert.AreNotEqual(baseline.PlanDigest, altered.PlanDigest);
    }

    [TestMethod]
    public void ToolAndModelDigestsChangeCanonicalPlanIdentity()
    {
        Fixture fixture = new();

        StandardCompositionPlan baseline = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], []));
        StandardCompositionPlan changedTool = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], [], toolDigest: Digest("other-tool")));
        StandardCompositionPlan changedModel = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], [], modelDigest: Digest("other-model")));

        Assert.AreNotEqual(baseline.Id, changedTool.Id);
        Assert.AreNotEqual(baseline.PlanDigest, changedTool.PlanDigest);
        Assert.AreNotEqual(baseline.Id, changedModel.Id);
        Assert.AreNotEqual(baseline.PlanDigest, changedModel.PlanDigest);
    }

    [TestMethod]
    public void ProjectStoreStateChangesCanonicalPlanIdentity()
    {
        Fixture fixture = new();
        var changedState = new StandardProjectStoreState(
            revision: 1,
            Digest("changed-project-payload"),
            fixture.ProjectStoreState.UpdatedAt.AddMinutes(1));

        StandardCompositionPlan baseline = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], []));
        StandardCompositionPlan changed = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                projectStoreState: changedState));

        Assert.AreNotEqual(baseline.Id, changed.Id);
        Assert.AreNotEqual(baseline.PlanDigest, changed.PlanDigest);
    }

    [TestMethod]
    public void MusicCueAssignmentsAreRetainedAndChangePlanIdentity()
    {
        Fixture fixture = new();
        MusicCatalog catalog = MusicCatalog.CreateAvailable(
        [
            new MusicTrack(
                Digest("music-track"),
                byteSize: 100,
                durationSeconds: 10m,
                codecName: "mp3",
                sampleRate: 48000,
                channels: 2),
        ]);
        MusicPlanningResult assigned = new MusicPlanningService().Plan(
            catalog,
            [new MusicCue(
                Digest("title-card-cue"),
                MusicCueKind.TitleCard,
                durationSeconds: 5m,
                hasForegroundSpeech: false)],
            MusicPlanningOptions.Default);

        StandardCompositionPlan baseline = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], []));
        StandardCompositionPlan withMusic = StandardCompositionPlanner.Create(
            fixture.CreateRequest([], [], musicPlanning: assigned));

        MusicCueAssignment assignment = withMusic.MusicPlanning.Assignments
            .Single();
        Assert.AreEqual(Digest("title-card-cue"), assignment.CueDigest);
        Assert.AreEqual(Digest("music-track"), assignment.TrackDigest);
        Assert.AreEqual(MusicAssignmentReason.Assigned, assignment.Reason);
        Assert.IsFalse(assignment.Loop);
        Assert.IsFalse(assignment.Stretch);
        Assert.AreNotEqual(baseline.Id, withMusic.Id);
        Assert.AreNotEqual(baseline.PlanDigest, withMusic.PlanDigest);
    }

    [TestMethod]
    public void PlannerClassifiesStaleDuplicateOverlapAndLimitWithoutPadding()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate first = fixture.CreateCandidate(1, 0, 15, 1);
        EmphasisSuggestionCandidate second = fixture.CreateCandidate(2, 20, 15, 2);
        EmphasisSuggestionCandidate third = fixture.CreateCandidate(3, 40, 15, 3);
        EmphasisSuggestionCandidate fourth = fixture.CreateCandidate(4, 60, 15, 4);
        EmphasisSuggestionCandidate duplicate = fixture.CreateCandidate(5, 0, 15, 1);
        EmphasisSuggestionCandidate overlap = fixture.CreateCandidate(6, 5, 15, 6);
        EmphasisSuggestionCandidate stale = fixture.CreateCandidate(
            7,
            80,
            15,
            7,
            sourceRevisionDigest: Digest("stale-transcript"));
        EmphasisSuggestionCandidate[] candidates =
        [
            stale,
            overlap,
            fourth,
            third,
            duplicate,
            second,
            first,
        ];
        HighlightSuggestionAdoptionResult[] adoptions = candidates
            .Select(fixture.Adopt)
            .ToArray();

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(candidates, adoptions));

        Assert.HasCount(3, plan.ShortForms);
        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            plan.ShortForms.Select(static item =>
                item.SelectedHighlight.SuggestionId).ToArray());
        Dictionary<EntityId<SuggestionIdTag>, StandardShortCandidateExclusionCode>
            exclusions = plan.ShortCandidateExclusions.ToDictionary(
                static item => item.CandidateId,
                static item => item.Code);
        Assert.AreEqual(
            StandardShortCandidateExclusionCode.CandidateLimitApplied,
            exclusions[fourth.Id]);
        Assert.AreEqual(
            StandardShortCandidateExclusionCode.DuplicateSource,
            exclusions[duplicate.Id]);
        Assert.AreEqual(
            StandardShortCandidateExclusionCode.OverlappingSource,
            exclusions[overlap.Id]);
        Assert.AreEqual(
            StandardShortCandidateExclusionCode.StaleEvidence,
            exclusions[stale.Id]);
        Assert.IsTrue(plan.Warnings.Any(static warning =>
            warning.Code == StandardCompositionWarningCode.CandidateLimitApplied));
        Assert.IsFalse(plan.Omissions.Any(static omission =>
            omission.ArtifactKind == StandardCompositionArtifactKind.ShortForm));
    }

    [TestMethod]
    public void ChosenShortCandidateCountLimitsTheSelectedShorts()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate[] candidates =
        [
            fixture.CreateCandidate(1, 0, 15, 1),
            fixture.CreateCandidate(2, 20, 15, 2),
            fixture.CreateCandidate(3, 40, 15, 3),
        ];
        HighlightSuggestionAdoptionResult[] adoptions = candidates
            .Select(fixture.Adopt)
            .ToArray();

        StandardCompositionPlan two = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                candidates,
                adoptions,
                settings: fixture.WithSetting(
                    "project.verticalShortCandidateCount",
                    DefaultSettingValue.FromInteger(2))));
        StandardCompositionPlan none = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                candidates,
                adoptions,
                settings: fixture.WithSetting(
                    "project.verticalShortCandidateCount",
                    DefaultSettingValue.FromInteger(0))));

        CollectionAssert.AreEqual(
            new[] { candidates[0].Id, candidates[1].Id },
            two.ShortForms.Select(static item =>
                item.SelectedHighlight.SuggestionId).ToArray());
        Assert.AreEqual(
            StandardShortCandidateExclusionCode.CandidateLimitApplied,
            two.ShortCandidateExclusions.Single().Code);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
            two.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm).Code);
        Assert.IsEmpty(none.ShortForms);
        Assert.HasCount(3, none.ShortCandidateExclusions);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoGroundedCandidate,
            none.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm).Code);
    }

    [TestMethod]
    public void ChosenThumbnailCountLimitsTheSelectedFrames()
    {
        Fixture fixture = new();
        StandardThumbnailEvidence evidence = fixture.CreateThumbnailEvidence(4);

        StandardCompositionPlan one = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                settings: fixture.WithSetting(
                    "thumbnails.candidateCount",
                    DefaultSettingValue.FromInteger(1)),
                thumbnailEvidence: evidence));
        StandardCompositionPlan two = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                settings: fixture.WithSetting(
                    "thumbnails.candidateCount",
                    DefaultSettingValue.FromInteger(2)),
                thumbnailEvidence: evidence));

        Assert.HasCount(1, one.Thumbnails);
        Assert.HasCount(2, two.Thumbnails);
        CollectionAssert.AreEqual(
            new[] { 0, 3 },
            two.Thumbnails.Select(static item => item.SourceFrameSequence).ToArray());
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalMeasuredSourceFrame,
            two.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail).Code);
    }

    [TestMethod]
    public void LayoutFallsBackToBlurredOrFixedWithoutClaimingSubjectOrPlatformMeasurement()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(1, 5, 30, 1);
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(candidate);

        StandardCompositionPlan landscape = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [candidate],
                [adoption],
                new SourceDisplayGeometry(1920, 1080)));
        StandardCompositionPlan vertical = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [candidate],
                [adoption],
                new SourceDisplayGeometry(1080, 1920)));

        Assert.AreEqual(
            ManualLayoutMode.BlurredSourceBackground,
            landscape.ShortForms[0].Layout.Mode);
        Assert.AreEqual(
            ManualLayoutMode.FixedTemplatePlacement,
            vertical.ShortForms[0].Layout.Mode);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                StandardCompositionWarningCode.SubjectRegionUnavailable,
                StandardCompositionWarningCode.BlurredBackgroundFallback,
                StandardCompositionWarningCode.PlatformUiUnmeasured,
            },
            landscape.Warnings.Select(static warning => warning.Code).ToArray());
        CollectionAssert.IsSubsetOf(
            new[]
            {
                StandardCompositionWarningCode.SubjectRegionUnavailable,
                StandardCompositionWarningCode.FixedLayoutFallback,
                StandardCompositionWarningCode.PlatformUiUnmeasured,
            },
            vertical.Warnings.Select(static warning => warning.Code).ToArray());
    }

    [TestMethod]
    public void PreviewEvidenceProducesThreeActualFrameCandidatesWithoutQualityClaim()
    {
        Fixture fixture = new();
        StandardThumbnailEvidence evidence = fixture.CreateThumbnailEvidence(4);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                thumbnailEvidence: evidence));

        Assert.HasCount(3, plan.Thumbnails);
        CollectionAssert.AreEqual(
            new[] { 0, 1, 3 },
            plan.Thumbnails.Select(static item =>
                item.SourceFrameSequence).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new RationalTime(0, 1),
                new RationalTime(20, 1),
                new RationalTime(60, 1),
            },
            plan.Thumbnails.Select(static item =>
                item.SourcePresentationTime).ToArray());
        Assert.IsTrue(plan.Thumbnails.All(item =>
            item.SourceAssetId == fixture.Asset.Id));
        Assert.IsFalse(plan.Omissions.Any(static omission =>
            omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail));
        Assert.IsTrue(plan.Warnings.Any(static warning =>
            warning.Code
                == StandardCompositionWarningCode.ThumbnailQualityNotEvaluated));
        Assert.AreEqual(
            evidence.EvidenceDigest,
            plan.InputDigests.Single(static input => input.Kind
                == StandardPlanInputKind.ThumbnailEvidence).ContentDigest);
    }

    [TestMethod]
    public void FewerPreviewFramesProduceOnlyGroundedCandidatesAndTypedShortfall()
    {
        Fixture fixture = new();
        StandardThumbnailEvidence evidence = fixture.CreateThumbnailEvidence(2);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                thumbnailEvidence: evidence));

        Assert.HasCount(2, plan.Thumbnails);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalMeasuredSourceFrame,
            plan.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail).Code);
    }

    [TestMethod]
    public void PreviewEvidenceValidatesCellsAfterTheSelectedCandidateLimit()
    {
        Fixture fixture = new();

        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.CreateThumbnailEvidence(
                frameCount: 4,
                corruptLastSpritePresentationTime: true));
    }

    [TestMethod]
    public void TranscriptAudioEvidenceAllowsVideoFramesOutsideSpeechRanges()
    {
        Fixture fixture = new();
        SourceSpan transcriptEvidence = fixture.CreateEvidenceSpan(
            ordinal: 1,
            start: 20,
            duration: 80,
            tokenStart: 0,
            tokenCount: 10,
            streamId: "audio:1");
        CompositionVersion parent = fixture.CreateParent([transcriptEvidence]);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            Id<TimelineMapIdTag>(
                "26000000-0000-0000-0000-000000000097"),
            parent);
        LongFormCompositionPlan longForm = fixture.CreateLongForm(
            [transcriptEvidence]);

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            fixture.CreateRequest(
                [],
                [],
                parent: parent,
                timelineMap: timelineMap,
                longForm: longForm,
                thumbnailEvidence: fixture.CreateThumbnailEvidence(
                    frameCount: 3,
                    sourceStart: -1)));

        Assert.HasCount(3, plan.Thumbnails);
        Assert.AreEqual(
            new RationalTime(-1, 1),
            plan.Thumbnails[0].SourcePresentationTime);
    }

    [TestMethod]
    public void PlannerRejectsMismatchedTimelineSettingsAndProfileWithTypedReasons()
    {
        Fixture fixture = new();
        EmphasisSuggestionCandidate candidate = fixture.CreateCandidate(1, 5, 30, 1);
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(candidate);
        var wrongTimeline = new TimelineMap(
            fixture.TimelineMap.Id,
            Id<CompositionVersionIdTag>(
                "73000000-0000-0000-0000-000000000099"),
            fixture.TimelineMap.Entries);
        DefaultSettingsSnapshot wrongSettings = fixture.WithSetting(
            "output.shortTargetDuration",
            DefaultSettingValue.FromInteger(31));
        BundledRenderProfileDefinition wrongProfile =
            fixture.CreateInvalidVerticalProfileWithoutBurnIn();

        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [candidate],
                [adoption],
                timelineMap: wrongTimeline)),
            StandardCompositionPlanningFailureReason.TimelineMapMismatch);
        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [candidate],
                [adoption],
                settings: wrongSettings)),
            StandardCompositionPlanningFailureReason.SettingsMismatch);
        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [candidate],
                [adoption],
                shortFormProfile: wrongProfile)),
            StandardCompositionPlanningFailureReason.ProfileMismatch);
    }

    [TestMethod]
    public void PlannerRejectsUnreferencedTopicMetadataFromAnotherProject()
    {
        Fixture fixture = new();
        TopicMetadataRevision original = fixture.TopicMetadataRevision;
        var foreign = new TopicMetadataRevision(
            original.TopicMetadataId,
            original.Id,
            original.ParentRevisionId,
            Id<ProjectIdTag>(
                "22000000-0000-0000-0000-000000000099"),
            original.TopicRangeId,
            original.TopicRangeRevisionId,
            original.TopicRange,
            original.SourceRevisionId,
            original.SourceRevisionDigest,
            original.Metadata,
            original.ChangedField,
            original.ActorSid,
            original.CreatedAt,
            original.Reason);

        AssertFailure(
            () => StandardCompositionPlanner.Create(fixture.CreateRequest(
                [],
                [],
                topicMetadataRevisions: [foreign])),
            StandardCompositionPlanningFailureReason.TopicMetadataMismatch);
    }

    [TestMethod]
    public void PlannerObservesPreCancelledTokenWithoutProducingAPlan()
    {
        Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                StandardCompositionPlanner.Create(
                    fixture.CreateRequest([], []),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public void PlanningRequestRejectsOversizedCollectionCountBeforeCopy()
    {
        Fixture fixture = new();
        var oversized = new OversizedCountList<EmphasisSuggestionCandidate>();

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => fixture.CreateRequest(oversized, []));

        Assert.AreEqual("candidates", exception.ParamName);
        Assert.IsFalse(oversized.IndexerRead);
    }

    [TestMethod]
    public void PlanningRequestReadsCollectionCountOnlyOnce()
    {
        Fixture fixture = new();
        var changing = new ChangingCountList<EmphasisSuggestionCandidate>();

        StandardCompositionPlanningRequest request = fixture.CreateRequest(
            changing,
            []);

        Assert.IsEmpty(request.Candidates);
        Assert.AreEqual(1, changing.CountReadCount);
    }

    private static void AssertFailure(
        Action action,
        StandardCompositionPlanningFailureReason expected)
    {
        StandardCompositionPlanningException exception =
            Assert.ThrowsExactly<StandardCompositionPlanningException>(action);
        Assert.AreEqual(expected, exception.Reason);
        Assert.IsNull(exception.InnerException);
    }

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(value));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed class OversizedCountList<T> : IReadOnlyList<T>
    {
        internal bool IndexerRead { get; private set; }

        public int Count =>
            StandardCompositionResourceLimits.MaximumEvidenceCollectionItems + 1;

        public T this[int index]
        {
            get
            {
                IndexerRead = true;
                throw new InvalidOperationException(
                    "The oversized collection indexer must not be read.");
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException(
                "The oversized collection must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable
            .GetEnumerator() => GetEnumerator();
    }

    private sealed class ChangingCountList<T> : IReadOnlyList<T>
    {
        internal int CountReadCount { get; private set; }

        public int Count
        {
            get
            {
                CountReadCount++;
                return CountReadCount == 1
                    ? 0
                    : StandardCompositionResourceLimits
                        .MaximumEvidenceCollectionItems + 1;
            }
        }

        public T this[int index] => throw new ArgumentOutOfRangeException(
            nameof(index));

        public IEnumerator<T> GetEnumerator() =>
            Enumerable.Empty<T>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable
            .GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        private const string ActorSid = "S-1-5-21-100-200-300-1001";
        private static readonly DateTimeOffset CreatedAt = new(
            2042,
            5,
            6,
            7,
            8,
            9,
            TimeSpan.Zero);

        internal Fixture()
        {
            Asset = new Asset(
                Id<AssetIdTag>("21000000-0000-0000-0000-000000000001"),
                "source.mp4",
                "video/mp4",
                Digest("planner-source"),
                byteSize: 1234,
                new RightsRecord(
                    RightsStatus.Approved,
                    "evidence://internal-source",
                    "Internal use only",
                    externalPublicationAllowed: false));
            Project = new Project(
                Id<ProjectIdTag>("22000000-0000-0000-0000-000000000001"),
                "Source",
                "Internal reuse",
                "Internal viewers",
                PublicationScope.InternalOnly,
                deadline: null,
                [Asset],
                performers: []);
            TranscriptRevisionId = Id<TranscriptRevisionIdTag>(
                "23000000-0000-0000-0000-000000000001");
            TranscriptDigest = Digest("planner-transcript");
            TokenIds = Enumerable.Range(1, 20)
                .Select(ordinal => Id<TranscriptTokenIdTag>(
                    $"24000000-0000-0000-0000-{ordinal:000000000000}"))
                .ToArray();
            SourceSpan = new SourceSpan(
                Id<SourceSpanIdTag>("25000000-0000-0000-0000-000000000001"),
                Asset.Id,
                "video:0",
                Range(0, 100),
                Asset.ContentDigest,
                TranscriptRevisionId,
                TokenIds);
            Parent = CreateParent();
            TimelineMap = TimelineMapBuilder.Build(
                Id<TimelineMapIdTag>(
                    "26000000-0000-0000-0000-000000000001"),
                Parent);
            SettingsProfile = BundledDefaultSettingsProfileLoader.Load(
                File.ReadAllBytes(ResolveRepositoryPath(
                    "src/HubRadio.App/DefaultSettings/Profiles/1.1.0.json")));
            Settings = SettingsProfile.CreateSnapshot();
            ProjectStoreState = new StandardProjectStoreState(
                revision: 0,
                Digest("project-payload"),
                CreatedAt);
            MusicPlanning = new MusicPlanningService().Plan(
                MusicCatalog.CreateUnavailable(
                    MusicLibraryState.Absent,
                    MusicCatalogWarning.LibraryAbsent),
                cues: [],
                MusicPlanningOptions.Default);
            LongForm = CreateLongForm();
            ShortFormProfile = CreateVerticalProfile();
            TopicMetadataRevision = CreateTopicMetadataRevision();
        }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

        internal ContentDigest TranscriptDigest { get; }

        internal IReadOnlyList<EntityId<TranscriptTokenIdTag>> TokenIds { get; }

        internal SourceSpan SourceSpan { get; }

        internal CompositionVersion Parent { get; }

        internal TimelineMap TimelineMap { get; }

        internal DefaultSettingsProfile SettingsProfile { get; }

        internal DefaultSettingsSnapshot Settings { get; }

        internal StandardProjectStoreState ProjectStoreState { get; }

        internal MusicPlanningResult MusicPlanning { get; }

        internal LongFormCompositionPlan LongForm { get; }

        internal BundledRenderProfileDefinition ShortFormProfile { get; }

        internal TopicMetadataRevision TopicMetadataRevision { get; }

        internal EmphasisSuggestionCandidate CreateCandidate(
            int ordinal,
            long start,
            long duration,
            int tokenOrdinal,
            EmphasisReasonKind reason = EmphasisReasonKind.ImportantStatement,
            ContentDigest? sourceRevisionDigest = null,
            EntityId<SourceSpanIdTag>? sourceSpanId = null)
        {
            var span = new SourceSpan(
                sourceSpanId ?? Id<SourceSpanIdTag>(
                    $"31000000-0000-0000-0000-{ordinal:000000000000}"),
                Asset.Id,
                SourceSpan.StreamId,
                Range(start, duration),
                Asset.ContentDigest,
                TranscriptRevisionId,
                [TokenIds[tokenOrdinal - 1]]);
            var suggestion = new Suggestion(
                Id<SuggestionIdTag>(
                    $"32000000-0000-0000-0000-{ordinal:000000000000}"),
                Id<ModelRunIdTag>(
                    $"33000000-0000-0000-0000-{ordinal:000000000000}"),
                SuggestionKind.Highlight,
                SuggestionStatus.Proposed,
                OriginRef.FromSourceSpan(span.Id),
                "Explicit emphasis evidence selected for review.",
                confidence: null,
                "No automatic display priority is established.",
                "Review wording and layout before adoption.");
            var payload = new EmphasisCandidate(
                suggestion,
                span,
                TranscriptRevisionId,
                sourceRevisionDigest ?? TranscriptDigest,
                TopicMetadataRevision.Id,
                EmphasisCandidateService.RuleVersion,
                $"Excerpt {ordinal}",
                EmphasisTextSourceKind.OriginalTranscript,
                [reason],
                termEvidence: []);
            return SuggestionService.Register(payload, []);
        }

        internal HighlightSuggestionAdoptionResult Adopt(
            EmphasisSuggestionCandidate candidate) =>
            SuggestionService.AdoptHighlight(
                candidate,
                candidate.SourceDigest,
                Id<CueIdTag>(
                    $"34000000-0000-0000-0000-{CandidateOrdinal(candidate):000000000000}"),
                candidate.Payload.SourceSpan.Range,
                "graphic-main",
                decisionHistory: [],
                Id<SuggestionDecisionIdTag>(
                    $"35000000-0000-0000-0000-{CandidateOrdinal(candidate):000000000000}"),
                ActorSid,
                CreatedAt,
                "Adopt the reviewed source-grounded highlight.");

        internal StandardCompositionPlanningRequest CreateRequest(
            IReadOnlyList<EmphasisSuggestionCandidate> candidates,
            IReadOnlyList<HighlightSuggestionAdoptionResult> adoptedHighlights,
            SourceDisplayGeometry? sourceDisplayGeometry = null,
            CompositionVersion? parent = null,
            TimelineMap? timelineMap = null,
            LongFormCompositionPlan? longForm = null,
            DefaultSettingsSnapshot? settings = null,
            BundledRenderProfileDefinition? shortFormProfile = null,
            IReadOnlyList<TopicMetadataRevision>? topicMetadataRevisions = null,
            ContentDigest? toolDigest = null,
            ContentDigest? modelDigest = null,
            MusicPlanningResult? musicPlanning = null,
            StandardProjectStoreState? projectStoreState = null,
            StandardThumbnailEvidence? thumbnailEvidence = null) =>
            new(
                parent ?? Parent,
                timelineMap ?? TimelineMap,
                longForm ?? LongForm,
                shortFormProfile ?? ShortFormProfile,
                sourceDisplayGeometry ?? new SourceDisplayGeometry(1920, 1080),
                (longForm ?? LongForm).Profile.Profile.FrameRate,
                projectStoreState ?? ProjectStoreState,
                SettingsProfile,
                settings ?? Settings,
                toolDigest ?? Digest("toolchain"),
                modelDigest ?? Digest("model-set"),
                musicPlanning ?? MusicPlanning,
                topicMetadataRevisions ?? [TopicMetadataRevision],
                candidates,
                adoptedHighlights,
                thumbnailEvidence);

        internal StandardThumbnailEvidence CreateThumbnailEvidence(
            int frameCount,
            bool corruptLastSpritePresentationTime = false,
            long sourceStart = 0)
        {
            PtsIndexEntry[] entries = Enumerable.Range(0, frameCount)
                .Select(index => new PtsIndexEntry(
                    index,
                    new RationalTime(sourceStart + (index * 20), 1),
                    new RationalTime(20, 1),
                    keyFrame: true))
                .ToArray();
            var sourcePtsIndex = new PtsIndex(
                Asset.Id,
                Project.Id,
                Id<JobIdTag>(
                    "36000000-0000-0000-0000-000000000001"),
                Asset.FileName,
                Asset.ContentDigest,
                Asset.ByteSize,
                streamIndex: 0,
                new RationalTime(1, 1),
                entries);
            ContentDigest sourcePtsDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(sourcePtsIndex.CanonicalJson));
            ContentDigest proxyDigest = Digest("thumbnail-proxy");
            ContentDigest proxyPtsDigest = Digest("thumbnail-proxy-pts");
            RationalTime[] presentationTimes = entries
                .Select(static entry => entry.PresentationTime)
                .ToArray();
            ProxyFrameMap frameMap = ProxyFrameMap.Create(
                Asset.Id,
                Project.Id,
                Asset.ContentDigest,
                sourcePtsDigest,
                proxyDigest,
                proxyPtsDigest,
                presentationTimes,
                entries[^1].PresentationTime + entries[^1].Duration,
                presentationTimes);
            ThumbnailSpriteCell[] cells = entries.Select((entry, index) =>
                new ThumbnailSpriteCell(
                    index,
                    index,
                    corruptLastSpritePresentationTime
                            && index == entries.Length - 1
                        ? entry.PresentationTime + new RationalTime(1, 1)
                        : entry.PresentationTime,
                    entry.Duration,
                    index,
                    row: 0)).ToArray();
            var spriteIndex = new ThumbnailSpriteIndex(
                Asset.Id,
                Project.Id,
                Id<ArtifactIdTag>(
                    "37000000-0000-0000-0000-000000000001"),
                proxyDigest,
                proxyPtsDigest,
                Id<ArtifactIdTag>(
                    "38000000-0000-0000-0000-000000000001"),
                Digest("thumbnail-sprite-image"),
                PreviewAssetGenerationProfile.CurrentVersion,
                cellWidth: 160,
                cellHeight: 90,
                columns: frameCount,
                rows: 1,
                cells);
            ContentDigest spriteIndexDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(spriteIndex.CanonicalJson));
            var sourceArtifact = new StoredArtifact(
                Id<ArtifactIdTag>(
                    "39000000-0000-0000-0000-000000000001"),
                Project.Id,
                ArtifactKind.Original,
                "projects/source/original.bin",
                Asset.ContentDigest,
                Asset.ByteSize,
                CreatedAt);
            var proxyArtifact = new StoredArtifact(
                spriteIndex.ProxyArtifactId,
                Project.Id,
                ArtifactKind.Proxy,
                "projects/source/proxy.mp4",
                proxyDigest,
                byteSize: 200,
                CreatedAt);
            var spriteImageArtifact = new StoredArtifact(
                spriteIndex.SpriteImageArtifactId,
                Project.Id,
                ArtifactKind.Preview,
                "projects/source/sprite.png",
                spriteIndex.SpriteImageDigest,
                byteSize: 300,
                CreatedAt);
            var spriteIndexArtifact = new StoredArtifact(
                Id<ArtifactIdTag>(
                    "3a000000-0000-0000-0000-000000000001"),
                Project.Id,
                ArtifactKind.Preview,
                "projects/source/sprite.json",
                spriteIndexDigest,
                Encoding.UTF8.GetByteCount(spriteIndex.CanonicalJson),
                CreatedAt);
            return StandardThumbnailEvidence.FromValidatedPreview(
                sourcePtsIndex,
                frameMap,
                spriteIndex,
                sourceArtifact,
                proxyArtifact,
                spriteImageArtifact,
                spriteIndexArtifact);
        }

        internal SourceSpan CreateEvidenceSpan(
            int ordinal,
            long start,
            long duration,
            int tokenStart,
            int tokenCount,
            string? streamId = null) =>
            new(
                Id<SourceSpanIdTag>(
                    $"25000000-0000-0000-0000-{ordinal:000000000000}"),
                Asset.Id,
                streamId ?? SourceSpan.StreamId,
                Range(start, duration),
                Asset.ContentDigest,
                TranscriptRevisionId,
                TokenIds.Skip(tokenStart).Take(tokenCount).ToArray());

        internal SourceSpan CloneEvidenceSpanWithId(
            SourceSpan source,
            EntityId<SourceSpanIdTag> id) =>
            new(
                id,
                source.AssetId,
                source.StreamId,
                source.Range,
                source.SourceDigest,
                source.TranscriptRevisionId,
                source.TranscriptTokenIds);

        internal DefaultSettingsSnapshot WithSetting(
            string key,
            DefaultSettingValue value) =>
            SettingsProfile.CreateSnapshot(Settings.Values.Select(selection =>
                string.Equals(selection.Key, key, StringComparison.Ordinal)
                    ? new DefaultSettingSelection(key, value)
                    : selection).ToArray());

        internal BundledRenderProfileDefinition
            CreateInvalidVerticalProfileWithoutBurnIn()
        {
            BundledRenderProfileDefinition production =
                CreateVerticalProfile();
            var profile = new RenderProfile(
                production.Profile.Id,
                production.Profile.Name,
                production.Profile.ProfileDigest,
                production.Profile.Width,
                production.Profile.Height,
                production.Profile.FrameRateNumerator,
                production.Profile.FrameRateDenominator,
                production.Profile.LayoutSpecId,
                production.Profile.TemplateId,
                production.Profile.TemplateVersionDigest,
                production.Profile.VideoCodec,
                production.Profile.AudioCodec,
                subtitleBurnIn: false);
            return new BundledRenderProfileDefinition(
                profile,
                production.Layout,
                production.LayoutDigest,
                production.TemplateReference,
                production.PixelFormatEvidence,
                production.Capabilities,
                production.PlatformSafeZonesMeasured,
                production.EvidenceSource,
                production.TemplateStatus,
                production.RequiredExternalBrandAssets,
                production.Unmeasured);
        }

        private BundledRenderProfileDefinition CreateVerticalProfile()
        {
            BundledRenderProfileDefinition measured =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath(
                        "src/HubRadio.App/Profiles/9x16/vertical-short.json")));
            return StandardRenderProfileFactory.CreateVerticalShort(
                SettingsProfile,
                Settings,
                measured,
                measured.Profile.FrameRate);
        }

        internal CompositionVersion CreateParent(
            IReadOnlyList<SourceSpan>? sourceSpans = null)
        {
            SourceSpan[] spans = (sourceSpans ?? [SourceSpan]).ToArray();
            RationalTime outputStart = default;
            CompositionElement[] elements = spans.Select((span, index) =>
            {
                var element = new CompositionElement(
                    Id<CompositionElementIdTag>(
                        $"27000000-0000-0000-0000-{index + 1:000000000000}"),
                    new TimeRange(outputStart, span.Range.Duration),
                    OriginRef.FromSourceSpan(span.Id),
                    sourceRange: span.Range);
                outputStart += span.Range.Duration;
                return element;
            }).ToArray();
            return new CompositionVersion(
                Id<CompositionVersionIdTag>(
                    "28000000-0000-0000-0000-000000000001"),
                Project.Id,
                parentVersionId: null,
                ActorSid,
                CreatedAt,
                "Create source composition.",
                ContentDigest.Compute(
                    CompositionSnapshotJson.WriteElementsObjectToUtf8(elements)),
                elements);
        }

        internal LongFormCompositionPlan CreateLongForm(
            IReadOnlyList<SourceSpan>? sourceSpans = null)
        {
            BundledRenderProfileDefinition measured =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath(
                        "src/HubRadio.App/Profiles/16x9/long-form.json")));
            BundledRenderProfileDefinition profile =
                StandardRenderProfileFactory.CreateLandscape(
                    SettingsProfile,
                    Settings,
                    measured,
                    measured.Profile.FrameRate);
            AppliedBrandTemplate template = new BrandTemplateVersion(
                Id<SeriesIdTag>(
                    "29000000-0000-0000-0000-000000000001"),
                new TemplateReference(
                    profile.Profile.TemplateId,
                    "Planner neutral test template",
                    profile.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                parameterDefinitions: []).ApplyEditorOverrides([]);
            var provenance = new LongFormProvenanceSnapshot(
                Project.Id,
                TranscriptRevisionId,
                TranscriptDigest,
                sourceSpans ?? [SourceSpan],
                trustedEditorialRecordIds: []);
            return new LongFormCompositionPlan(
                Id<LongFormPlanIdTag>(
                    "2a000000-0000-0000-0000-000000000001"),
                Project,
                provenance,
                template,
                profile,
                LongFormDestination.InternalDistribution,
                speakerAssignments: null,
                elements: [],
                templateAssets: [],
                referencedAssets: [Asset],
                warnings: [],
                timingComplete: true,
                externalRightsReady: false,
                externalPublicationReady: false,
                Digest("planner-long-form"));
        }

        private TopicMetadataRevision CreateTopicMetadataRevision()
        {
            var metadata = new TopicMetadata(
                "Topic 1",
                TopicMetadataValueOrigin.DeterministicPlaceholder,
                summary: null,
                TopicMetadataValueOrigin.Unset,
                mainSpeaker: null,
                TopicMetadataValueOrigin.Unset,
                mainSpeakerTieUnresolved: false,
                keywords: [],
                TopicMetadataValueOrigin.Unset,
                importantStatementSourceSpanIds: [],
                TopicMetadataValueOrigin.Unset,
                "Internal viewers",
                TopicMetadataValueOrigin.ProjectSnapshot,
                publicationCautions: [],
                TopicMetadataValueOrigin.Unset);
            return new TopicMetadataRevision(
                Id<TopicMetadataIdTag>(
                    "2b000000-0000-0000-0000-000000000001"),
                Id<TopicMetadataRevisionIdTag>(
                    "2c000000-0000-0000-0000-000000000001"),
                parentRevisionId: null,
                Project.Id,
                Id<TopicRangeIdTag>(
                    "2d000000-0000-0000-0000-000000000001"),
                Id<TopicRangeRevisionIdTag>(
                    "2e000000-0000-0000-0000-000000000001"),
                SourceSpan.Range,
                TranscriptRevisionId,
                TranscriptDigest,
                metadata,
                TopicMetadataField.Initial,
                ActorSid,
                CreatedAt,
                "Create deterministic topic metadata.");
        }

        private static int CandidateOrdinal(
            EmphasisSuggestionCandidate candidate) =>
            int.Parse(candidate.Id.ToString()[^12..]);

        private static TimeRange Range(long start, long duration) =>
            new(new RationalTime(start, 1), new RationalTime(duration, 1));

        private static string ResolveRepositoryPath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                {
                    return Path.Combine(
                        directory.FullName,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not resolve repository-relative path '{relativePath}'.");
        }
    }
}
