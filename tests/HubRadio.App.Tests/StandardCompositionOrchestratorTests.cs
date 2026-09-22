using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Policy;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardCompositionOrchestratorTests
{
    [TestMethod]
    public void PrepareReexecutesComposerAndCreatesDeterministicSameContentChild()
    {
        Fixture fixture = new();
        string parentBefore = CompositionSnapshotJson.WriteVersion(fixture.Parent);

        StandardCompositionPreparation first =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        StandardCompositionPreparation second =
            StandardCompositionOrchestrator.Prepare(fixture.Request());

        Assert.AreEqual(fixture.Parent.Id, first.Child.ParentVersionId);
        Assert.AreEqual(fixture.Parent.ContentDigest, first.Child.ContentDigest);
        CollectionAssert.AreEqual(
            fixture.Parent.Elements.ToArray(),
            first.Child.Elements.ToArray());
        Assert.AreEqual(first.Child.Id, first.ChildTimelineMap.CompositionVersionId);
        Assert.AreEqual(first.Child.Id, second.Child.Id);
        Assert.AreEqual(first.Plan.Id, second.Plan.Id);
        Assert.AreEqual(first.Plan.PlanDigest, second.Plan.PlanDigest);
        Assert.AreEqual(
            CompositionSnapshotJson.WriteVersion(first.Child),
            CompositionSnapshotJson.WriteVersion(second.Child));
        Assert.AreEqual(
            parentBefore,
            CompositionSnapshotJson.WriteVersion(fixture.Parent));
        Assert.IsEmpty(first.Plan.ShortForms);
        Assert.IsEmpty(first.Plan.Thumbnails);
        Assert.AreEqual(
            CompositionEvidenceEnvelope.StandardCompositionPlanKind,
            first.Child.Evidence!.Kind);
        _ = StandardCompositionEvidenceCodec.ValidateLoaded(
            first.Child,
            fixture.Parent);
    }

    [TestMethod]
    public void PrepareAppliesSourceOnlyLongFormSafeDefaults()
    {
        Fixture fixture = new();

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request());

        Dictionary<LongFormElementKind, LongFormElementPlan> elements =
            preparation.Plan.LongForm.Elements.ToDictionary(
                static element => element.Kind);
        Assert.AreEqual(
            LongFormElementState.Omitted,
            elements[LongFormElementKind.Opening].State);
        LongFormElementPlan title = elements[LongFormElementKind.EpisodeTitle];
        Assert.AreEqual(LongFormElementState.Included, title.State);
        Assert.AreEqual(fixture.Project.ContentName, title.Text);
        Assert.HasCount(1, title.Origins);
        Assert.AreEqual(OriginKind.Asset, title.Origins[0].Kind);
        Assert.AreEqual(fixture.Asset.Id, title.Origins[0].AssetId);

        LongFormElementPlan body = elements[LongFormElementKind.Body];
        Assert.AreEqual(LongFormElementState.Included, body.State);
        Assert.AreEqual(fixture.Parent.Elements[0].OutputRange, body.OutputRange);
        LongFormAssetSlot bodyMedia = body.AssetSlots.Single(static slot =>
            slot.Role == LongFormAssetRole.BodyMedia);
        Assert.AreEqual(LongFormAssetSlotState.Included, bodyMedia.State);
        Assert.AreEqual(fixture.Asset.Id, bodyMedia.Asset!.Id);

        foreach (LongFormElementKind omitted in new[]
        {
            LongFormElementKind.ProgramName,
            LongFormElementKind.PerformerIntroduction,
            LongFormElementKind.Summary,
            LongFormElementKind.CallToAction,
            LongFormElementKind.Ending,
        })
        {
            Assert.AreEqual(LongFormElementState.Omitted, elements[omitted].State);
        }
    }

    [TestMethod]
    public void PrepareOmitsFreeTextThatOnlyClaimsSourceSpanGrounding()
    {
        Fixture fixture = new();
        LongFormCompositionRequest baseline = fixture.LongFormRequest;
        OriginRef sourceOrigin = OriginRef.FromSourceSpan(fixture.SourceSpan.Id);
        LongFormCallToActionInput baselineCallToAction =
            baseline.CallToActions.Single();
        LongFormCompositionRequest withUngroundedText = ReplaceLongFormRequest(
            baseline,
            programName: IncludeText(
                baseline.ProgramName,
                "Invented program name",
                sourceOrigin),
            episodeNumber: IncludeText(
                baseline.EpisodeNumber,
                "999",
                sourceOrigin),
            summary: IncludeText(
                baseline.Summary,
                "Invented summary",
                sourceOrigin),
            callToActions:
            [
                new LongFormCallToActionInput(
                    baselineCallToAction.Destination,
                    IncludeText(
                        baselineCallToAction.Element,
                        "Invented call to action",
                        sourceOrigin)),
            ]);

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                longFormRequest: withUngroundedText));

        Dictionary<LongFormElementKind, LongFormElementPlan> elements =
            preparation.Plan.LongForm.Elements.ToDictionary(
                static element => element.Kind);
        foreach (LongFormElementKind kind in new[]
        {
            LongFormElementKind.ProgramName,
            LongFormElementKind.EpisodeNumber,
            LongFormElementKind.Summary,
            LongFormElementKind.CallToAction,
        })
        {
            Assert.AreEqual(LongFormElementState.Omitted, elements[kind].State);
            Assert.IsNull(elements[kind].Text);
            Assert.IsEmpty(elements[kind].Origins);
        }
    }

    [TestMethod]
    public void PrepareKeepsClosedEditorialMetadataAndReplaysItExactly()
    {
        Fixture fixture = new();
        LongFormCompositionRequest baseline = fixture.LongFormRequest;
        EntityId<EditorialRecordIdTag> editorialRecordId =
            Id<EditorialRecordIdTag>(
                "57000000-0000-0000-0000-000000000001");
        OriginRef editorialOrigin = OriginRef.FromEditorialRecord(
            editorialRecordId);
        var provenance = new LongFormProvenanceSnapshot(
            baseline.Provenance.ProjectId,
            baseline.Provenance.TranscriptRevisionId,
            baseline.Provenance.TranscriptRevisionDigest,
            baseline.Provenance.SourceSpans,
            [editorialRecordId]);
        LongFormCallToActionInput baselineCallToAction =
            baseline.CallToActions.Single();
        LongFormCompositionRequest withEditorialText = ReplaceLongFormRequest(
            baseline,
            provenance,
            programName: IncludeText(
                baseline.ProgramName,
                "Confirmed program name",
                editorialOrigin),
            summary: IncludeText(
                baseline.Summary,
                "Confirmed summary",
                editorialOrigin),
            callToActions:
            [
                new LongFormCallToActionInput(
                    baselineCallToAction.Destination,
                    IncludeText(
                        baselineCallToAction.Element,
                        "Confirmed call to action",
                        editorialOrigin)),
            ]);

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                longFormRequest: withEditorialText));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        Dictionary<LongFormElementKind, LongFormElementPlan> elements =
            preparation.Plan.LongForm.Elements.ToDictionary(
                static element => element.Kind);
        Assert.AreEqual(
            "Confirmed program name",
            elements[LongFormElementKind.ProgramName].Text);
        Assert.AreEqual(
            "Confirmed summary",
            elements[LongFormElementKind.Summary].Text);
        Assert.AreEqual(
            "Confirmed call to action",
            elements[LongFormElementKind.CallToAction].Text);
        foreach (LongFormElementKind kind in new[]
        {
            LongFormElementKind.ProgramName,
            LongFormElementKind.Summary,
            LongFormElementKind.CallToAction,
        })
        {
            Assert.AreEqual(LongFormElementState.Included, elements[kind].State);
            Assert.AreEqual(editorialRecordId, elements[kind].Origins.Single()
                .EditorialRecordId);
        }
    }

    [TestMethod]
    public void PrepareRemovesSourceAttributionFromEditorialMetadata()
    {
        Fixture fixture = new();
        LongFormCompositionRequest baseline = fixture.LongFormRequest;
        EntityId<EditorialRecordIdTag> editorialRecordId =
            Id<EditorialRecordIdTag>(
                "57000000-0000-0000-0000-000000000002");
        var provenance = new LongFormProvenanceSnapshot(
            baseline.Provenance.ProjectId,
            baseline.Provenance.TranscriptRevisionId,
            baseline.Provenance.TranscriptRevisionDigest,
            baseline.Provenance.SourceSpans,
            [editorialRecordId]);
        var summary = new LongFormElementInput(
            baseline.Summary.Id,
            LongFormElementKind.Summary,
            enabled: true,
            "Confirmed editorial summary",
            outputRange: null,
            [
                OriginRef.FromSourceSpan(fixture.SourceSpan.Id),
                OriginRef.FromEditorialRecord(editorialRecordId),
            ],
            assets: []);
        LongFormCompositionRequest request = ReplaceLongFormRequest(
            baseline,
            provenance,
            summary: summary);

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                longFormRequest: request));

        LongFormElementPlan planned = preparation.Plan.LongForm.Elements.Single(
            static element => element.Kind == LongFormElementKind.Summary);
        Assert.AreEqual(LongFormElementState.Included, planned.State);
        Assert.HasCount(1, planned.Origins);
        Assert.AreEqual(OriginKind.EditorialRecord, planned.Origins[0].Kind);
        Assert.AreEqual(editorialRecordId, planned.Origins[0].EditorialRecordId);
    }

    [TestMethod]
    public void PrepareCanReplanFromPriorStandardCompositionEvidence()
    {
        Fixture fixture = new();
        StandardCompositionPreparation first =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        var nextWorkingCopy = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.Project.Id,
            first.Child.Id,
            fixture.WorkingCopy.CreatedAt,
            first.Child.CreatedAt,
            first.Child.Elements);

        StandardCompositionPreparation second =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                expectedWorkingCopyRevision: 1,
                changeReason: "Regenerate the grounded standard plan.",
                parent: first.Child,
                workingCopy: nextWorkingCopy));

        Assert.AreEqual(first.Child.Id, second.Child.ParentVersionId);
        Assert.AreNotEqual(first.Child.Id, second.Child.Id);
        Assert.AreEqual(first.Child.ContentDigest, second.Child.ContentDigest);
        Assert.AreEqual(
            first.Plan.InputDigests.Single(static input =>
                input.Kind == StandardPlanInputKind.AutoDecision).ContentDigest,
            second.Plan.InputDigests.Single(static input =>
                input.Kind == StandardPlanInputKind.AutoDecision).ContentDigest);
        Assert.AreNotEqual(
            first.Child.Evidence!.ContentDigest,
            second.Plan.InputDigests.Single(static input =>
                input.Kind == StandardPlanInputKind.AutoDecision).ContentDigest);
        _ = StandardCompositionEvidenceCodec.ValidateLoaded(
            second.Child,
            first.Child);
    }

    [TestMethod]
    public void ApplyCanCheckpointAndRestartASecondStandardPlan()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        StandardCompositionApplication first =
            StandardCompositionOrchestrator.Apply(
                new VersionService(store),
                fixture.Request());
        StandardCompositionOrchestrationRequest secondRequest = fixture.Request(
            expectedWorkingCopyRevision: 1,
            changeReason: "Regenerate the grounded standard plan.",
            parent: first.Checkpoint.Version,
            workingCopy: first.Checkpoint.WorkingCopy);

        StandardCompositionApplication second =
            StandardCompositionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                secondRequest);
        StandardCompositionPreparation restarted = new VersionService(
            new ProjectStore(database.DatabasePath)).GetStandardComposition(
                second.Checkpoint.Version.Id)!;

        Assert.AreEqual(2, second.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(
            first.Checkpoint.Version.Id,
            second.Checkpoint.Version.ParentVersionId);
        Assert.AreEqual(3L, database.CountCompositionVersions());
        Assert.AreEqual(
            CompositionSnapshotJson.WriteVersion(second.Checkpoint.Version),
            CompositionSnapshotJson.WriteVersion(restarted.Child));
    }

    [TestMethod]
    public void AdoptedHighlightRoundTripsThroughCandidateAndDecisionReplay()
    {
        Fixture fixture = new();
        (TopicMetadataRevision topic, EmphasisSuggestionCandidate candidate,
            HighlightSuggestionAdoptionResult adoption) =
            fixture.CreateAdoptedHighlight();

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(
                fixture.Request([topic], [candidate], [adoption]));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        Assert.HasCount(1, preparation.Plan.ShortForms);
        Assert.AreEqual(
            candidate.Id,
            preparation.Plan.ShortForms[0].SelectedHighlight.SuggestionId);
        Assert.AreEqual(
            candidate.Payload.SourceSpan.Id,
            preparation.Plan.ShortForms[0].SourceSpans[0].Id);
    }

    [TestMethod]
    public void GroundedProposedHighlightRoundTripsWithoutInventingAdoption()
    {
        Fixture fixture = new();
        (TopicMetadataRevision topic, EmphasisSuggestionCandidate candidate,
            _) = fixture.CreateAdoptedHighlight();

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(
                fixture.Request([topic], [candidate], adoptions: []));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        Assert.HasCount(1, preparation.Plan.ShortForms);
        Assert.AreEqual(
            StandardShortSelectionKind.GroundedCandidate,
            preparation.Plan.ShortForms[0].SelectionKind);
        Assert.AreEqual(
            candidate.Id,
            preparation.Plan.ShortForms[0].SelectedHighlight.SuggestionId);
        Assert.AreEqual(
            candidate.Suggestion.Reason,
            replayed.Preparation.Plan.ShortForms[0].SelectionReason);
        Assert.AreEqual(
            candidate.Suggestion.MissingContext,
            replayed.Preparation.Plan.ShortForms[0].MissingContext);
        Assert.AreEqual(
            candidate.Suggestion.Warning,
            replayed.Preparation.Plan.ShortForms[0].SelectionWarning);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
    }

    [TestMethod]
    public void MusicAssignmentRoundTripsThroughStandardEvidence()
    {
        Fixture fixture = new();
        MusicPlanningResult musicPlanning = fixture.CreateAssignedMusicPlanning();

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                musicPlanning: musicPlanning));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        MusicCueAssignment assignment = preparation.Plan.MusicPlanning
            .Assignments.Single();
        Assert.AreEqual(Digest("title-card-cue"), assignment.CueDigest);
        Assert.AreEqual(Digest("music-track"), assignment.TrackDigest);
        Assert.AreEqual(MusicAssignmentReason.Assigned, assignment.Reason);
    }

    [TestMethod]
    public void SourceFrameRateRoundTripsThroughBothStandardProfiles()
    {
        Fixture fixture = new();
        var sourceFrameRate = new RationalNumber(25, 1);
        BundledRenderProfileDefinition measuredLandscape =
            BundledRenderProfileLoader.Load(fixture.LandscapeJson);
        BundledRenderProfileDefinition landscape =
            StandardRenderProfileFactory.CreateLandscape(
                fixture.SettingsProfile,
                fixture.Settings,
                measuredLandscape,
                sourceFrameRate);
        LongFormCompositionRequest longFormRequest = ReplaceLongFormRequest(
            fixture.LongFormRequest,
            profile: landscape);

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                longFormRequest: longFormRequest,
                sourceFrameRate: sourceFrameRate));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        Assert.AreEqual(
            sourceFrameRate,
            preparation.Plan.LongForm.Profile.Profile.FrameRate);
        Assert.AreEqual(
            sourceFrameRate,
            preparation.Plan.ShortFormProfile.Profile.FrameRate);
    }

    [TestMethod]
    public void ThumbnailPreviewEvidenceRoundTripsAsThreeActualFrames()
    {
        Fixture fixture = new();
        StandardThumbnailEvidence thumbnailEvidence =
            fixture.CreateThumbnailEvidence(frameCount: 4);

        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                thumbnailEvidence: thumbnailEvidence));
        ValidatedStandardCompositionEvidence replayed =
            StandardCompositionEvidenceCodec.ValidateLoaded(
                preparation.Child,
                fixture.Parent);

        Assert.IsNotNull(replayed);
        Assert.HasCount(3, preparation.Plan.Thumbnails);
        CollectionAssert.AreEqual(
            new[] { 0, 1, 3 },
            preparation.Plan.Thumbnails.Select(static item =>
                item.SourceFrameSequence).ToArray());
        Assert.IsFalse(preparation.Plan.Omissions.Any(static omission =>
            omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail));
    }

    [TestMethod]
    [DataRow("proxyByteSize")]
    [DataRow("artifactIdentityCollision")]
    public void RehashedThumbnailArtifactReceiptFailsSemanticReplay(
        string mutation)
    {
        Fixture fixture = new();
        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                thumbnailEvidence: fixture.CreateThumbnailEvidence(
                    frameCount: 4)));
        CompositionVersion tampered = MutateEvidence(
            preparation.Child,
            root =>
            {
                JsonObject thumbnail = root["input"]!["thumbnailEvidence"]!
                    .AsObject();
                switch (mutation)
                {
                    case "proxyByteSize":
                        thumbnail["proxyByteSize"] = 201;
                        break;
                    case "artifactIdentityCollision":
                        thumbnail["sourceArtifactId"] =
                            thumbnail["proxyArtifactId"]!.DeepClone();
                        break;
                    default:
                        Assert.Fail($"Unknown mutation '{mutation}'.");
                        break;
                }
            });

        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionEvidenceCodec.ValidateLoaded(
                tampered,
                fixture.Parent));
    }

    [TestMethod]
    public void RehashedInvalidLongFormElementFailsSemanticComposerReplay()
    {
        Fixture fixture = new();
        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        JsonObject root = JsonNode.Parse(
            CompositionSnapshotJson.WriteVersion(preparation.Child))!
            .AsObject();
        JsonObject envelope = root["evidence"]!.AsObject();
        string evidenceJson = envelope["canonicalJson"]!.GetValue<string>();
        JsonObject evidence = JsonNode.Parse(evidenceJson)!.AsObject();
        evidence["input"]!["longFormRequest"]!["preRollMargin"]!["text"] =
            "This text is invalid because the element is disabled.";
        string tamperedEvidence = CanonicalJson.Canonicalize(
            evidence.ToJsonString());
        envelope["canonicalJson"] = tamperedEvidence;
        envelope["contentDigest"] = ContentDigest.Compute(
            Encoding.UTF8.GetBytes(tamperedEvidence)).Hex;
        CompositionVersion tampered = CompositionSnapshotJson.ReadVersion(
            CanonicalJson.Canonicalize(root.ToJsonString()));

        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionEvidenceCodec.ValidateLoaded(
                tampered,
                fixture.Parent));
    }

    [TestMethod]
    public void OversizedEvidenceTextAndAggregateFailBeforeCheckpoint()
    {
        Fixture fixture = new();
        string overSingleFieldLimit = new(
            'x',
            CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes + 1);
        ArgumentException fieldFailure =
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Request(
                changeReason: overSingleFieldLimit));
        Assert.AreEqual("changeReason", fieldFailure.ParamName);

        string fillsEvidenceEnvelope = new(
            'x',
            CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes);
        StandardCompositionOrchestrationRequest request = fixture.Request(
            changeReason: fillsEvidenceEnvelope);
        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionOrchestrator.Prepare(request));
    }

    [TestMethod]
    public void OversizedMeasuredProfileIsRejectedByRequestPreflight()
    {
        Fixture fixture = new();
        byte[] oversized = new byte[
            BundledRenderProfileLoader.MaximumUtf8JsonBytes + 1];

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => fixture.Request(
                measuredLandscapeProfileJson: oversized));

        Assert.AreEqual("measuredLandscapeProfileJson", exception.ParamName);
    }

    [TestMethod]
    public void MalformedPriorStandardEvidenceMapsToParentMismatch()
    {
        Fixture fixture = new();
        CompositionVersion parent = WithEvidence(
            fixture.Parent,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                "[]"));

        StandardCompositionPlanningException exception =
            Assert.ThrowsExactly<StandardCompositionPlanningException>(() =>
                StandardCompositionOrchestrator.Prepare(fixture.Request(
                    parent: parent)));

        Assert.AreEqual(
            StandardCompositionPlanningFailureReason.ParentMismatch,
            exception.Reason);
    }

    [TestMethod]
    public void PriorStandardEvidenceCannotBeReboundToAnotherVersion()
    {
        Fixture fixture = new();
        StandardCompositionPreparation first =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        CompositionVersion rebound = WithEvidence(new CompositionVersion(
            Id<CompositionVersionIdTag>(
                "58000000-0000-0000-0000-000000000001"),
            first.Child.ProjectId,
            first.Child.ParentVersionId,
            first.Child.CreatedByActorId,
            first.Child.CreatedAt,
            first.Child.ChangeReason,
            first.Child.ContentDigest,
            first.Child.Elements),
            first.Child.Evidence!);
        var workingCopy = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.Project.Id,
            rebound.Id,
            fixture.WorkingCopy.CreatedAt,
            rebound.CreatedAt,
            rebound.Elements);

        StandardCompositionPlanningException exception =
            Assert.ThrowsExactly<StandardCompositionPlanningException>(() =>
                StandardCompositionOrchestrator.Prepare(fixture.Request(
                    expectedWorkingCopyRevision: 1,
                    parent: rebound,
                    workingCopy: workingCopy)));

        Assert.AreEqual(
            StandardCompositionPlanningFailureReason.ParentMismatch,
            exception.Reason);
    }

    [TestMethod]
    [DataRow("unknownRootProperty")]
    [DataRow("unknownNestedProperty")]
    [DataRow("missingRequiredProperty")]
    [DataRow("wrongSchemaVersion")]
    [DataRow("wrongPropertyType")]
    public void RehashedMalformedEvidenceFailsItsClosedParsingPath(
        string mutation)
    {
        Fixture fixture = new();
        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        CompositionVersion tampered = MutateEvidence(
            preparation.Child,
            root =>
            {
                switch (mutation)
                {
                    case "unknownRootProperty":
                        root["unexpected"] = true;
                        break;
                    case "unknownNestedProperty":
                        root["input"]!["workingCopy"]!["unexpected"] = true;
                        break;
                    case "missingRequiredProperty":
                        Assert.IsTrue(
                            root["input"]!.AsObject().Remove("changeReason"));
                        break;
                    case "wrongSchemaVersion":
                        root["schemaVersion"] =
                            "hubradio.standard-composition-evidence.v1";
                        break;
                    case "wrongPropertyType":
                        root["input"]!["sourceDisplayGeometry"]!["width"] =
                            "1920";
                        break;
                    default:
                        Assert.Fail($"Unknown mutation '{mutation}'.");
                        break;
                }
            });

        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionEvidenceCodec.ValidateLoaded(
                tampered,
                fixture.Parent));
    }

    [TestMethod]
    public void DuplicateMalformedAndWrongKnownKindEvidenceFailClosed()
    {
        Fixture fixture = new();
        StandardCompositionPreparation preparation =
            StandardCompositionOrchestrator.Prepare(fixture.Request());
        string canonical = preparation.Child.Evidence!.CanonicalJson;
        string duplicate = string.Concat(
            canonical.AsSpan(0, canonical.Length - 1),
            ",\"schemaVersion\":\"hubradio.standard-composition-evidence.v2\"}");

        Assert.ThrowsExactly<JsonException>(() =>
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                duplicate));

        CompositionVersion malformed = WithEvidence(
            preparation.Child,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                "[]"));
        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionEvidenceCodec.ValidateLoaded(
                malformed,
                fixture.Parent));

        CompositionVersion wrongKnownKind = WithEvidence(
            preparation.Child,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.AutoDecisionKind,
                canonical));
        Assert.ThrowsExactly<FormatException>(() =>
            StandardCompositionEvidenceCodec.ValidateLoaded(
                wrongKnownKind,
                fixture.Parent));
    }

    [TestMethod]
    public void ApplyIsAtomicRestartSafeCancelableAndExactlyIdempotent()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        var service = new VersionService(store);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            _ = Assert.ThrowsExactly<OperationCanceledException>(() =>
                StandardCompositionOrchestrator.Apply(
                    service,
                    request,
                    cancellation.Token));
        }

        Assert.IsNull(store.GetCompositionVersion(expected.Child.Id));
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);

        StandardCompositionApplication applied =
            StandardCompositionOrchestrator.Apply(service, request);
        Assert.AreEqual(1, applied.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(
            expected.Child.Id,
            applied.Checkpoint.Version.Id);
        Assert.AreEqual(2L, database.CountCompositionVersions());

        CompositionVersion restarted = new VersionService(
            new ProjectStore(database.DatabasePath)).GetVersion(
                expected.Child.Id)!;
        Assert.AreEqual(
            CompositionSnapshotJson.WriteVersion(expected.Child),
            CompositionSnapshotJson.WriteVersion(restarted));

        StandardCompositionPreparation restartedPreparation =
            new VersionService(new ProjectStore(database.DatabasePath))
                .GetStandardComposition(expected.Child.Id)!;
        Assert.AreEqual(
            expected.ApplicationDigest,
            restartedPreparation.ApplicationDigest);
        Assert.AreEqual(
            expected.Plan.PlanDigest,
            restartedPreparation.Plan.PlanDigest);
        Assert.AreEqual(
            expected.ParentTimelineMap.Id,
            restartedPreparation.ParentTimelineMap.Id);
        Assert.IsTrue(expected.ParentTimelineMap.Entries.SequenceEqual(
            restartedPreparation.ParentTimelineMap.Entries));
        Assert.AreEqual(
            expected.ChildTimelineMap.Id,
            restartedPreparation.ChildTimelineMap.Id);
        Assert.IsTrue(expected.ChildTimelineMap.Entries.SequenceEqual(
            restartedPreparation.ChildTimelineMap.Entries));
        Assert.AreEqual(
            CompositionSnapshotJson.WriteVersion(expected.Child),
            CompositionSnapshotJson.WriteVersion(
                restartedPreparation.Child));

        StandardCompositionApplication duplicate =
            StandardCompositionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request);
        Assert.AreEqual(
            applied.Checkpoint.CanonicalVersionJson,
            duplicate.Checkpoint.CanonicalVersionJson);
        Assert.AreEqual(1, duplicate.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(2L, database.CountCompositionVersions());

        StandardCompositionPreparation different =
            StandardCompositionOrchestrator.Prepare(fixture.Request(
                changeReason: "Create a semantically different standard plan."));
        Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            StandardCompositionOrchestrator.Apply(
                service,
                fixture.Request(
                    changeReason:
                        "Create a semantically different standard plan.")));
        Assert.IsNull(store.GetCompositionVersion(different.Child.Id));

        StandardCompositionOrchestrationRequest staleBase = fixture.Request(
            expectedWorkingCopyRevision: 1);
        Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            StandardCompositionOrchestrator.Apply(service, staleBase));
        Assert.AreEqual(2L, database.CountCompositionVersions());
        Assert.AreEqual(1, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void RestoredPlanMatchesTheCheckpointedPlanWhenProvenanceArrivesUnordered()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        var service = new VersionService(store);
        LongFormCompositionRequest baseline = fixture.LongFormRequest;
        SourceSpan enclosing = baseline.Provenance.SourceSpans.Single();
        var transcribed = new SourceSpan(
            Id<SourceSpanIdTag>("45000000-0000-0000-0000-000000000002"),
            enclosing.AssetId,
            enclosing.StreamId,
            Range(10, 20),
            enclosing.SourceDigest,
            enclosing.TranscriptRevisionId,
            [enclosing.TranscriptTokenIds[0]]);

        // The pipeline collects transcript spans first and appends the
        // whole-source span, which is not the order the evidence is stored in.
        var provenance = new LongFormProvenanceSnapshot(
            baseline.Provenance.ProjectId,
            baseline.Provenance.TranscriptRevisionId,
            baseline.Provenance.TranscriptRevisionDigest,
            [transcribed, enclosing],
            baseline.Provenance.TrustedEditorialRecordIds);
        StandardCompositionOrchestrationRequest request = fixture.Request(
            longFormRequest: ReplaceLongFormRequest(baseline, provenance));
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);
        _ = StandardCompositionOrchestrator.Apply(service, request);

        StandardCompositionPreparation restored = new VersionService(
            new ProjectStore(database.DatabasePath)).GetStandardComposition(
                expected.Child.Id)!;

        // StandardArtifactGenerator.ValidateCheckpoint compares the whole plan
        // object, which is order sensitive, not only the order-free plan digest.
        Assert.AreEqual(
            StandardRenderGraphFactory.Digest(expected.Plan),
            StandardRenderGraphFactory.Digest(restored.Plan));
        CollectionAssert.AreEqual(
            expected.Plan.LongForm.Provenance.SourceSpans
                .Select(static span => span.Id)
                .ToArray(),
            restored.Plan.LongForm.Provenance.SourceSpans
                .Select(static span => span.Id)
                .ToArray());
    }

    [TestMethod]
    public async Task ConcurrentIdenticalApplyConvergesOnOneExactCheckpoint()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore observer = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        using var release = new ManualResetEventSlim(initialState: false);

        Task<StandardCompositionApplication> first = Task.Run(() =>
        {
            release.Wait();
            return StandardCompositionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request);
        });
        Task<StandardCompositionApplication> second = Task.Run(() =>
        {
            release.Wait();
            return StandardCompositionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request);
        });
        release.Set();
        StandardCompositionApplication[] results = await Task.WhenAll(
            first,
            second);

        Assert.AreEqual(
            results[0].Checkpoint.Version.Id,
            results[1].Checkpoint.Version.Id);
        Assert.AreEqual(
            results[0].Checkpoint.CanonicalVersionJson,
            results[1].Checkpoint.CanonicalVersionJson);
        Assert.AreEqual(2L, database.CountCompositionVersions());
        Assert.AreEqual(1, observer.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void DuplicateApplyRejectsStoredColumnAndPayloadMismatch()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        StandardCompositionApplication first =
            StandardCompositionOrchestrator.Apply(
                new VersionService(store),
                request);
        database.RewriteCompositionCreatedAt(
            first.Checkpoint.Version.Id,
            first.Checkpoint.Version.CreatedAt.AddMinutes(1));

        Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            StandardCompositionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request));
        Assert.AreEqual(2L, database.CountCompositionVersions());
        Assert.AreEqual(1, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void ApplyRejectsWorkingCopyUpdatedAtColumnPayloadMismatch()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);
        database.RewriteWorkingCopyUpdatedAt(
            fixture.WorkingCopy.Id,
            fixture.WorkingCopy.UpdatedAt.AddMinutes(-1));

        StoreConcurrencyException exception =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(store),
                    request));

        Assert.AreEqual("working copy", exception.EntityKind);
        Assert.IsNull(store.GetCompositionVersion(expected.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void OrchestrationRequestRejectsOversizedCollectionCountBeforeCopy()
    {
        Fixture fixture = new();
        var oversized = new OversizedCountList<EmphasisSuggestionCandidate>();

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => fixture.Request(candidates: oversized));

        Assert.AreEqual("candidates", exception.ParamName);
        Assert.IsFalse(oversized.IndexerRead);
    }

    [TestMethod]
    public void OrchestrationRequestReadsCollectionCountOnlyOnce()
    {
        Fixture fixture = new();
        var changing = new ChangingCountList<EmphasisSuggestionCandidate>();

        StandardCompositionOrchestrationRequest request = fixture.Request(
            candidates: changing);

        Assert.IsEmpty(request.Candidates);
        Assert.AreEqual(1, changing.CountReadCount);
    }

    [TestMethod]
    [DataRow("scope")]
    [DataRow("payload")]
    public void ApplyRejectsStoredProjectStateChangedAfterPreparation(
        string mutation)
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);
        switch (mutation)
        {
            case "scope":
                database.RewriteProjectScope(
                    fixture.Project.Id,
                    PublicationScope.PublicationForbidden);
                break;
            case "payload":
                database.RewriteProjectPayload(
                    fixture.Project.Id,
                    "{\"tampered\":true}");
                break;
            default:
                Assert.Fail($"Unknown mutation '{mutation}'.");
                break;
        }

        StoreConcurrencyException exception =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(store),
                    request));

        Assert.AreEqual("project", exception.EntityKind);
        Assert.IsNull(store.GetCompositionVersion(expected.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void DuplicateApplyDoesNotHideProjectStateTampering()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest request = fixture.Request();
        StandardCompositionApplication first =
            StandardCompositionOrchestrator.Apply(
                new VersionService(store),
                request);
        database.RewriteProjectPayload(
            fixture.Project.Id,
            "{\"tampered\":true}");

        StoreConcurrencyException exception =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(new ProjectStore(database.DatabasePath)),
                    request));

        Assert.AreEqual("project", exception.EntityKind);
        Assert.AreEqual(2L, database.CountCompositionVersions());
        Assert.AreEqual(
            first.Checkpoint.Version.Id,
            store.GetWorkingCopy(fixture.Project.Id)!.BaseVersionId);
        Assert.AreEqual(1, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void ApplyRejectsRequestProjectSnapshotDifferentFromStoredPayload()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        Project project = fixture.Project;
        var changedProject = new Project(
            project.Id,
            project.ContentName,
            "Different unstored purpose",
            project.Audience,
            project.PublicationScope,
            project.Deadline,
            project.Assets,
            project.Performers);
        LongFormCompositionRequest longFormRequest = ReplaceLongFormRequest(
            fixture.LongFormRequest,
            project: changedProject);
        StandardCompositionOrchestrationRequest request = fixture.Request(
            longFormRequest: longFormRequest);
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);

        StoreConcurrencyException exception =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(store),
                    request));

        Assert.AreEqual("project", exception.EntityKind);
        Assert.IsNull(store.GetCompositionVersion(expected.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(project.Id)!.Revision);
    }

    [TestMethod]
    public async Task ConcurrentDifferentApplyAllowsOneWinnerAndNoOrphan()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore observer = fixture.CreateStored(database.DatabasePath);
        StandardCompositionOrchestrationRequest firstRequest = fixture.Request(
            changeReason: "Create standard plan A.");
        StandardCompositionOrchestrationRequest secondRequest = fixture.Request(
            changeReason: "Create standard plan B.");
        StandardCompositionPreparation firstExpected =
            StandardCompositionOrchestrator.Prepare(firstRequest);
        StandardCompositionPreparation secondExpected =
            StandardCompositionOrchestrator.Prepare(secondRequest);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<ApplicationAttempt> first = Task.Run(() => TryApply(
            database.DatabasePath,
            firstRequest,
            release));
        Task<ApplicationAttempt> second = Task.Run(() => TryApply(
            database.DatabasePath,
            secondRequest,
            release));
        release.Set();
        ApplicationAttempt[] attempts = await Task.WhenAll(first, second);

        ApplicationAttempt winner = attempts.Single(static item =>
            item.Application is not null);
        ApplicationAttempt loser = attempts.Single(static item =>
            item.Error is not null);
        Assert.IsNotNull(winner.Application);
        Assert.AreEqual(typeof(StoreConcurrencyException), loser.Error!.GetType());
        Assert.AreNotEqual(firstExpected.Child.Id, secondExpected.Child.Id);
        EntityId<CompositionVersionIdTag> losingId =
            winner.Application.Checkpoint.Version.Id == firstExpected.Child.Id
                ? secondExpected.Child.Id
                : firstExpected.Child.Id;
        Assert.IsNull(observer.GetCompositionVersion(losingId));
        Assert.AreEqual(2L, database.CountCompositionVersions());
        StoredWorkingCopy stored = observer.GetWorkingCopy(fixture.Project.Id)!;
        Assert.AreEqual(1, stored.Revision);
        Assert.AreEqual(winner.Application.Checkpoint.Version.Id, stored.BaseVersionId);
    }

    [TestMethod]
    public void ApplyRejectsForgedSameIdentityDraftBeforeAnyMutation()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        CompositionElement original = fixture.Parent.Elements.Single();
        RationalTime forgedDuration = original.OutputRange.Duration
            - new RationalTime(1, 1);
        var forgedElement = new CompositionElement(
            original.Id,
            new TimeRange(original.OutputRange.Start, forgedDuration),
            original.Origin,
            original.AdoptedSuggestion,
            new TimeRange(
                new RationalTime(1, 1),
                forgedDuration));
        var forgedParent = new CompositionVersion(
            fixture.Parent.Id,
            fixture.Parent.ProjectId,
            fixture.Parent.ParentVersionId,
            fixture.Parent.CreatedByActorId,
            fixture.Parent.CreatedAt,
            fixture.Parent.ChangeReason,
            ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8(
                    [forgedElement])),
            [forgedElement]);
        var forgedWorkingCopy = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.WorkingCopy.ProjectId,
            fixture.WorkingCopy.BaseVersionId,
            fixture.WorkingCopy.CreatedAt,
            fixture.WorkingCopy.UpdatedAt,
            [forgedElement]);
        StandardCompositionOrchestrationRequest request = fixture.Request(
            parent: forgedParent,
            workingCopy: forgedWorkingCopy);
        StandardCompositionPreparation forged =
            StandardCompositionOrchestrator.Prepare(request);

        StoreConcurrencyException conflict =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(store),
                    request));

        Assert.AreEqual(0, conflict.ExpectedRevision);
        Assert.AreEqual(0, conflict.ActualRevision);
        Assert.IsNull(store.GetCompositionVersion(forged.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void ApplyRejectsParentSnapshotThatDiffersFromStoredParent()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        CompositionVersion forgedParent = WithEvidence(
            fixture.Parent,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.AutoDecisionKind,
                "{}"));
        StandardCompositionOrchestrationRequest request = fixture.Request(
            parent: forgedParent);
        StandardCompositionPreparation forged =
            StandardCompositionOrchestrator.Prepare(request);

        StoreConcurrencyException exception =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                StandardCompositionOrchestrator.Apply(
                    new VersionService(store),
                    request));

        Assert.AreEqual("composition parent", exception.EntityKind);
        Assert.IsNull(store.GetCompositionVersion(forged.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void ApplyRejectsStoredSemanticallyInvalidEvidenceParent()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        ProjectStore store = fixture.CreateStored(database.DatabasePath);
        CompositionVersion invalidParent = WithEvidence(
            fixture.Parent,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.AutoDecisionKind,
                "{}"));
        database.RewriteCompositionPayload(
            invalidParent.Id,
            CompositionSnapshotJson.WriteVersion(invalidParent));
        StandardCompositionOrchestrationRequest request = fixture.Request(
            parent: invalidParent);
        StandardCompositionPreparation expected =
            StandardCompositionOrchestrator.Prepare(request);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            StandardCompositionOrchestrator.Apply(
                new VersionService(store),
                request));

        Assert.IsNull(store.GetCompositionVersion(expected.Child.Id));
        Assert.AreEqual(1L, database.CountCompositionVersions());
        Assert.AreEqual(0, store.GetWorkingCopy(fixture.Project.Id)!.Revision);
    }

    [TestMethod]
    public void PrepareRejectsExternalProjectScope()
    {
        Fixture fixture = new(PublicationScope.ExternalCandidate);

        Assert.ThrowsExactly<ArgumentException>(() =>
            StandardCompositionOrchestrator.Prepare(fixture.Request()));
    }

    [TestMethod]
    public void PrepareRejectsProjectTitleNotDerivedFromSourceFile()
    {
        Fixture fixture = new();
        Project project = fixture.Project;
        var mismatchedProject = new Project(
            project.Id,
            "Invented title",
            project.Purpose,
            project.Audience,
            project.PublicationScope,
            project.Deadline,
            project.Assets,
            project.Performers);
        LongFormCompositionRequest longFormRequest = ReplaceLongFormRequest(
            fixture.LongFormRequest,
            project: mismatchedProject);

        StandardCompositionOrchestrationException exception =
            Assert.ThrowsExactly<StandardCompositionOrchestrationException>(
                () => StandardCompositionOrchestrator.Prepare(fixture.Request(
                    longFormRequest: longFormRequest)));

        Assert.AreEqual(
            StandardCompositionOrchestrationFailureReason.SourceEvidenceMismatch,
            exception.Reason);
    }

    [TestMethod]
    public void PrepareRejectsParentSourceRangeOutsideProvenanceSpan()
    {
        Fixture fixture = new();
        CompositionElement original = fixture.Parent.Elements.Single();
        var outside = new CompositionElement(
            original.Id,
            original.OutputRange,
            original.Origin,
            original.AdoptedSuggestion,
            Range(200, 100));
        var parent = new CompositionVersion(
            fixture.Parent.Id,
            fixture.Parent.ProjectId,
            fixture.Parent.ParentVersionId,
            fixture.Parent.CreatedByActorId,
            fixture.Parent.CreatedAt,
            fixture.Parent.ChangeReason,
            ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8([outside])),
            [outside]);
        var workingCopy = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.WorkingCopy.ProjectId,
            parent.Id,
            fixture.WorkingCopy.CreatedAt,
            fixture.WorkingCopy.UpdatedAt,
            [outside]);

        StandardCompositionOrchestrationException exception =
            Assert.ThrowsExactly<StandardCompositionOrchestrationException>(
                () => StandardCompositionOrchestrator.Prepare(fixture.Request(
                    parent: parent,
                    workingCopy: workingCopy)));

        Assert.AreEqual(
            StandardCompositionOrchestrationFailureReason.SourceEvidenceMismatch,
            exception.Reason);
    }

    private static ApplicationAttempt TryApply(
        string databasePath,
        StandardCompositionOrchestrationRequest request,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            StandardCompositionApplication application =
                StandardCompositionOrchestrator.Apply(
                    new VersionService(new ProjectStore(databasePath)),
                    request);
            return new ApplicationAttempt(application, Error: null);
        }
        catch (Exception exception)
        {
            return new ApplicationAttempt(Application: null, exception);
        }
    }

    private sealed record ApplicationAttempt(
        StandardCompositionApplication? Application,
        Exception? Error);

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

    private static LongFormElementInput IncludeText(
        LongFormElementInput baseline,
        string text,
        OriginRef origin) =>
        new(
            baseline.Id,
            baseline.Kind,
            enabled: true,
            text,
            outputRange: null,
            [origin],
            assets: []);

    private static LongFormCompositionRequest ReplaceLongFormRequest(
        LongFormCompositionRequest request,
        LongFormProvenanceSnapshot? provenance = null,
        Project? project = null,
        LongFormElementInput? programName = null,
        LongFormElementInput? episodeNumber = null,
        LongFormElementInput? summary = null,
        IReadOnlyList<LongFormCallToActionInput>? callToActions = null,
        BundledRenderProfileDefinition? profile = null) =>
        new(
            request.PlanId,
            project ?? request.Project,
            provenance ?? request.Provenance,
            request.Template,
            profile ?? request.Profile,
            request.Destination,
            request.SpeakerAssignments,
            request.PreRollMargin,
            request.Opening,
            programName ?? request.ProgramName,
            episodeNumber ?? request.EpisodeNumber,
            request.EpisodeTitle,
            request.PerformerIntroduction,
            request.PublicationScopeIndicator,
            request.Body,
            request.Topics,
            summary ?? request.Summary,
            callToActions ?? request.CallToActions,
            request.Ending,
            request.PostRollMargin);

    private sealed class Fixture
    {
        private const string ActorSid = "S-1-5-21-100-200-300-1001";
        private static readonly DateTimeOffset CreatedAt = new(
            2044,
            6,
            7,
            8,
            9,
            10,
            TimeSpan.Zero);

        internal Fixture(
            PublicationScope publicationScope = PublicationScope.InternalOnly)
        {
            Asset = new Asset(
                Id<AssetIdTag>("41000000-0000-0000-0000-000000000001"),
                "source.mp4",
                "video/mp4",
                Digest("standard-source"),
                byteSize: 4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "evidence://internal-only-source",
                    "Internal use only",
                    externalPublicationAllowed: false));
            Project = new Project(
                Id<ProjectIdTag>("42000000-0000-0000-0000-000000000001"),
                "source",
                "Create grounded standard outputs",
                "Internal viewers",
                publicationScope,
                deadline: null,
                [Asset],
                performers: []);
            ProjectPayloadJson = ProjectSnapshotJson.WriteProject(Project);
            ProjectStoreState = new StandardProjectStoreState(
                revision: 0,
                ContentDigest.Compute(Encoding.UTF8.GetBytes(ProjectPayloadJson)),
                CreatedAt.AddMinutes(-4));
            TranscriptRevisionId = Id<TranscriptRevisionIdTag>(
                "43000000-0000-0000-0000-000000000001");
            TranscriptDigest = Digest("standard-transcript");
            TokenIds = Enumerable.Range(1, 10)
                .Select(ordinal => Id<TranscriptTokenIdTag>(
                    $"44000000-0000-0000-0000-{ordinal:000000000000}"))
                .ToArray();
            SourceSpan = new SourceSpan(
                Id<SourceSpanIdTag>(
                    "45000000-0000-0000-0000-000000000001"),
                Asset.Id,
                "video:0",
                Range(0, 100),
                Asset.ContentDigest,
                TranscriptRevisionId,
                TokenIds);
            CompositionElement element = new(
                Id<CompositionElementIdTag>(
                    "46000000-0000-0000-0000-000000000001"),
                Range(0, 100),
                OriginRef.FromSourceSpan(SourceSpan.Id),
                adoptedSuggestion: null,
                SourceSpan.Range);
            Parent = new CompositionVersion(
                Id<CompositionVersionIdTag>(
                    "47000000-0000-0000-0000-000000000001"),
                Project.Id,
                parentVersionId: null,
                ActorSid,
                CreatedAt.AddMinutes(-2),
                "Create the exact source composition.",
                ContentDigest.Compute(
                    CompositionSnapshotJson.WriteElementsObjectToUtf8([element])),
                [element]);
            WorkingCopy = new WorkingCopy(
                Id<WorkingCopyIdTag>(
                    "48000000-0000-0000-0000-000000000001"),
                Project.Id,
                Parent.Id,
                CreatedAt.AddMinutes(-3),
                CreatedAt.AddMinutes(-1),
                Parent.Elements);
            SettingsProfile = BundledDefaultSettingsProfileLoader.Load(
                File.ReadAllBytes(ResolveRepositoryPath(
                    "src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = SettingsProfile.CreateSnapshot();
            LandscapeJson = File.ReadAllBytes(ResolveRepositoryPath(
                "src/HubRadio.App/Profiles/16x9/long-form.json"));
            VerticalJson = File.ReadAllBytes(ResolveRepositoryPath(
                "src/HubRadio.App/Profiles/9x16/vertical-short.json"));
            MusicPlanning = new MusicPlanningService().Plan(
                MusicCatalog.CreateUnavailable(
                    MusicLibraryState.Absent,
                    MusicCatalogWarning.LibraryAbsent),
                cues: [],
                MusicPlanningOptions.Default);
            BundledRenderProfileDefinition measuredLandscape =
                BundledRenderProfileLoader.Load(LandscapeJson);
            BundledRenderProfileDefinition landscape =
                StandardRenderProfileFactory.CreateLandscape(
                    SettingsProfile,
                    Settings,
                    measuredLandscape,
                    measuredLandscape.Profile.FrameRate);
            AppliedBrandTemplate template = new BrandTemplateVersion(
                Id<SeriesIdTag>(
                    "49000000-0000-0000-0000-000000000001"),
                new TemplateReference(
                    landscape.Profile.TemplateId,
                    "Standard neutral test template",
                    landscape.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                parameterDefinitions: []).ApplyEditorOverrides([]);
            var provenance = new LongFormProvenanceSnapshot(
                Project.Id,
                TranscriptRevisionId,
                TranscriptDigest,
                [SourceSpan],
                trustedEditorialRecordIds: []);
            LongFormRequest = new LongFormCompositionRequest(
                Id<LongFormPlanIdTag>(
                    "4a000000-0000-0000-0000-000000000001"),
                Project,
                provenance,
                template,
                landscape,
                LongFormDestination.InternalDistribution,
                speakerAssignments: null,
                Omitted(1, LongFormElementKind.PreRollMargin),
                Omitted(2, LongFormElementKind.Opening),
                Omitted(3, LongFormElementKind.ProgramName),
                Omitted(4, LongFormElementKind.EpisodeNumber),
                Omitted(5, LongFormElementKind.EpisodeTitle),
                Omitted(6, LongFormElementKind.PerformerIntroduction),
                Omitted(7, LongFormElementKind.PublicationScopeIndicator),
                Omitted(8, LongFormElementKind.Body),
                topics: [],
                Omitted(9, LongFormElementKind.Summary),
                [new LongFormCallToActionInput(
                    LongFormDestination.InternalDistribution,
                    Omitted(10, LongFormElementKind.CallToAction))],
                Omitted(11, LongFormElementKind.Ending),
                Omitted(12, LongFormElementKind.PostRollMargin));
        }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal string ProjectPayloadJson { get; }

        internal StandardProjectStoreState ProjectStoreState { get; }

        internal EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

        internal ContentDigest TranscriptDigest { get; }

        internal IReadOnlyList<EntityId<TranscriptTokenIdTag>> TokenIds { get; }

        internal SourceSpan SourceSpan { get; }

        internal CompositionVersion Parent { get; }

        internal WorkingCopy WorkingCopy { get; }

        internal DefaultSettingsProfile SettingsProfile { get; }

        internal DefaultSettingsSnapshot Settings { get; }

        internal byte[] LandscapeJson { get; }

        internal byte[] VerticalJson { get; }

        internal MusicPlanningResult MusicPlanning { get; }

        internal LongFormCompositionRequest LongFormRequest { get; }

        internal StandardCompositionOrchestrationRequest Request(
            IReadOnlyList<TopicMetadataRevision>? topics = null,
            IReadOnlyList<EmphasisSuggestionCandidate>? candidates = null,
            IReadOnlyList<HighlightSuggestionAdoptionResult>? adoptions = null,
            int expectedWorkingCopyRevision = 0,
            string? changeReason = null,
            CompositionVersion? parent = null,
            WorkingCopy? workingCopy = null,
            LongFormCompositionRequest? longFormRequest = null,
            MusicPlanningResult? musicPlanning = null,
            RationalNumber? sourceFrameRate = null,
            StandardProjectStoreState? projectStoreState = null,
            byte[]? measuredLandscapeProfileJson = null,
            StandardThumbnailEvidence? thumbnailEvidence = null) =>
            new(
                parent ?? Parent,
                workingCopy ?? WorkingCopy,
                expectedWorkingCopyRevision,
                projectStoreState ?? ProjectStoreState,
                SettingsProfile,
                Settings,
                measuredLandscapeProfileJson ?? LandscapeJson,
                VerticalJson,
                longFormRequest ?? LongFormRequest,
                Id<TimelineMapIdTag>(
                    "4b000000-0000-0000-0000-000000000001"),
                new SourceDisplayGeometry(1920, 1080),
                sourceFrameRate ?? new RationalNumber(30000, 1001),
                Digest("toolchain"),
                Digest("model-set"),
                musicPlanning ?? MusicPlanning,
                topics ?? [],
                candidates ?? [],
                adoptions ?? [],
                ActorSid,
                CreatedAt,
                changeReason
                    ?? "Create the grounded standard composition plan.",
                thumbnailEvidence);

        internal MusicPlanningResult CreateAssignedMusicPlanning()
        {
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
            return new MusicPlanningService().Plan(
                catalog,
                [new MusicCue(
                    Digest("title-card-cue"),
                    MusicCueKind.TitleCard,
                    durationSeconds: 5m,
                    hasForegroundSpeech: false)],
                MusicPlanningOptions.Default);
        }

        internal StandardThumbnailEvidence CreateThumbnailEvidence(
            int frameCount)
        {
            PtsIndexEntry[] entries = Enumerable.Range(0, frameCount)
                .Select(index => new PtsIndexEntry(
                    index,
                    new RationalTime(index * 20, 1),
                    new RationalTime(20, 1),
                    keyFrame: true))
                .ToArray();
            var sourcePtsIndex = new PtsIndex(
                Asset.Id,
                Project.Id,
                Id<JobIdTag>(
                    "59000000-0000-0000-0000-000000000001"),
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
                    entry.PresentationTime,
                    entry.Duration,
                    index,
                    row: 0)).ToArray();
            var spriteIndex = new ThumbnailSpriteIndex(
                Asset.Id,
                Project.Id,
                Id<ArtifactIdTag>(
                    "5a000000-0000-0000-0000-000000000001"),
                proxyDigest,
                proxyPtsDigest,
                Id<ArtifactIdTag>(
                    "5b000000-0000-0000-0000-000000000001"),
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
                    "5c000000-0000-0000-0000-000000000001"),
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
                    "5d000000-0000-0000-0000-000000000001"),
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

        internal (
            TopicMetadataRevision Topic,
            EmphasisSuggestionCandidate Candidate,
            HighlightSuggestionAdoptionResult Adoption)
            CreateAdoptedHighlight()
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
                Project.Audience,
                TopicMetadataValueOrigin.ProjectSnapshot,
                publicationCautions: [],
                TopicMetadataValueOrigin.Unset);
            var topic = new TopicMetadataRevision(
                Id<TopicMetadataIdTag>(
                    "4c000000-0000-0000-0000-000000000001"),
                Id<TopicMetadataRevisionIdTag>(
                    "4d000000-0000-0000-0000-000000000001"),
                parentRevisionId: null,
                Project.Id,
                Id<TopicRangeIdTag>(
                    "4e000000-0000-0000-0000-000000000001"),
                Id<TopicRangeRevisionIdTag>(
                    "4f000000-0000-0000-0000-000000000001"),
                SourceSpan.Range,
                TranscriptRevisionId,
                TranscriptDigest,
                metadata,
                TopicMetadataField.Initial,
                ActorSid,
                CreatedAt.AddMinutes(-1),
                "Create deterministic topic metadata.");
            var candidateSpan = new SourceSpan(
                Id<SourceSpanIdTag>(
                    "51000000-0000-0000-0000-000000000001"),
                Asset.Id,
                SourceSpan.StreamId,
                Range(5, 30),
                Asset.ContentDigest,
                TranscriptRevisionId,
                [TokenIds[0]]);
            var suggestion = new Suggestion(
                Id<SuggestionIdTag>(
                    "52000000-0000-0000-0000-000000000001"),
                Id<ModelRunIdTag>(
                    "53000000-0000-0000-0000-000000000001"),
                SuggestionKind.Highlight,
                SuggestionStatus.Proposed,
                OriginRef.FromSourceSpan(candidateSpan.Id),
                "Explicit source-grounded statement selected for review.",
                confidence: null,
                "No semantic quality score is available.",
                "Review timing and layout before use.");
            var payload = new EmphasisCandidate(
                suggestion,
                candidateSpan,
                TranscriptRevisionId,
                TranscriptDigest,
                topic.Id,
                EmphasisCandidateService.RuleVersion,
                "Exact source excerpt",
                EmphasisTextSourceKind.OriginalTranscript,
                [EmphasisReasonKind.ImportantStatement],
                termEvidence: []);
            EmphasisSuggestionCandidate candidate =
                SuggestionService.Register(payload, []);
            HighlightSuggestionAdoptionResult adoption =
                SuggestionService.AdoptHighlight(
                    candidate,
                    candidate.SourceDigest,
                    Id<CueIdTag>(
                        "54000000-0000-0000-0000-000000000001"),
                    candidateSpan.Range,
                    "graphic-main",
                    decisionHistory: [],
                    Id<SuggestionDecisionIdTag>(
                        "55000000-0000-0000-0000-000000000001"),
                    ActorSid,
                    CreatedAt,
                    "Adopt the reviewed source-grounded highlight.");
            return (topic, candidate, adoption);
        }

        internal ProjectStore CreateStored(string databasePath)
        {
            var store = new ProjectStore(databasePath);
            store.CreateProject(
                Project,
                ProjectPayloadJson,
                CreatedAt.AddMinutes(-4));
            store.AppendCompositionVersion(
                Parent,
                CompositionSnapshotJson.WriteVersion(Parent));
            store.CreateWorkingCopy(
                WorkingCopy,
                CompositionSnapshotJson.WriteWorkingCopy(WorkingCopy));
            return store;
        }

        private static LongFormElementInput Omitted(
            int ordinal,
            LongFormElementKind kind) =>
            new(
                Id<LongFormElementIdTag>(
                    $"56000000-0000-0000-0000-{ordinal:000000000000}"),
                kind,
                enabled: false,
                text: null,
                outputRange: null,
                origins: [],
                assets: []);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        internal TemporaryDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.StandardCompositionOrchestratorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal long CountCompositionVersions()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM composition_versions;";
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException(
                    "Composition-version count was null."));
        }

        internal void RewriteCompositionCreatedAt(
            EntityId<CompositionVersionIdTag> versionId,
            DateTimeOffset createdAt)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE composition_versions
                SET created_at = $created_at
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue(
                "$created_at",
                createdAt.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", versionId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        internal void RewriteCompositionPayload(
            EntityId<CompositionVersionIdTag> versionId,
            string canonicalJson)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE composition_versions
                SET payload = $payload
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue(
                "$payload",
                Encoding.UTF8.GetBytes(canonicalJson));
            command.Parameters.AddWithValue("$id", versionId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        internal void RewriteProjectScope(
            EntityId<ProjectIdTag> projectId,
            PublicationScope scope) =>
            ExecuteProjectMutation(
                "UPDATE projects SET publication_scope = $value WHERE id = $id;",
                projectId,
                scope.ToString());

        internal void RewriteProjectPayload(
            EntityId<ProjectIdTag> projectId,
            string canonicalJson) =>
            ExecuteProjectMutation(
                "UPDATE projects SET payload = $value WHERE id = $id;",
                projectId,
                Encoding.UTF8.GetBytes(canonicalJson));

        internal void RewriteWorkingCopyUpdatedAt(
            EntityId<WorkingCopyIdTag> workingCopyId,
            DateTimeOffset updatedAt)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE working_copies
                SET updated_at = $updated_at
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue(
                "$updated_at",
                updatedAt.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", workingCopyId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        private void ExecuteProjectMutation(
            string sql,
            EntityId<ProjectIdTag> projectId,
            object value)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", projectId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private static CompositionVersion MutateEvidence(
        CompositionVersion version,
        Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(
            version.Evidence!.CanonicalJson)!.AsObject();
        mutation(root);
        string canonical = CanonicalJson.Canonicalize(root.ToJsonString());
        Assert.AreNotEqual(version.Evidence.CanonicalJson, canonical);
        return WithEvidence(
            version,
            CompositionEvidenceEnvelope.Create(
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                canonical));
    }

    private static CompositionVersion WithEvidence(
        CompositionVersion version,
        CompositionEvidenceEnvelope evidence)
    {
        JsonObject root = JsonNode.Parse(
            CompositionSnapshotJson.WriteVersion(version))!.AsObject();
        root["evidence"] = new JsonObject
        {
            ["canonicalJson"] = evidence.CanonicalJson,
            ["contentDigest"] = evidence.ContentDigest.Hex,
            ["kind"] = evidence.Kind,
        };
        return CompositionSnapshotJson.ReadVersion(
            CanonicalJson.Canonicalize(root.ToJsonString()));
    }

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(value));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

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
