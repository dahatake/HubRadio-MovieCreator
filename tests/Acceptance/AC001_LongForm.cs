using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Delivery;
using HubRadio.App.Profiles;
using HubRadio.App.Reviews;
using HubRadio.App.Speakers;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AC001_LongForm
{
    private const string ResultPathEnvironmentVariable = "HUBRADIO_Q001_RESULT_PATH";
    private const string SyntheticActorSid = "S-1-5-21-1000-2000-3000-5001";
    private const string MachineTopicStatementText = "synthetic machine topic statement";
    private const string ReviewedTopicStatementText = "synthetic reviewed topic statement";
    private const string DeliveryStatementText = "synthetic delivery statement";
    private const string PreludeText = "ここはカット synthetic prelude";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [TestMethod]
    public async Task AutomatableLongFormContractsPassWhileHumanAcceptanceRemainsPending()
    {
        using var fixture = new AcceptanceFixture();

        AcceptanceEvidence result = await fixture.RunAsync();

        Assert.AreEqual("1.0.0", result.SchemaVersion);
        Assert.AreEqual("Q-001", result.Requirement);
        Assert.IsTrue(result.AutomatedPassed);
        Assert.AreEqual("PENDING", result.HumanDecision);
        Assert.IsFalse(result.Q001Complete);
        Assert.IsFalse(result.Scope.ActualMediaUsed);
        Assert.IsFalse(result.Scope.ActualAsrExecuted);
        Assert.IsFalse(result.Scope.ActualModelInferenceUsed);
        Assert.IsFalse(result.Scope.ActualExportPolicyAuthorized);
        Assert.IsFalse(result.Scope.ActualFinalRenderExecuted);
        Assert.IsFalse(result.Scope.ActualFfmpegExecuted);
        Assert.IsFalse(result.Scope.ActualMediaQcExecuted);
        Assert.IsFalse(result.Scope.ActualThumbnailGenerated);
        Assert.IsTrue(result.Scope.SyntheticApprovalFixtureUsed);
        Assert.IsTrue(result.Scope.SyntheticDecisionRecordUsed);
        Assert.IsTrue(result.Scope.SyntheticDownstreamFixtureUsed);
        Assert.IsFalse(result.Scope.HumanCandidateDispositionClaimed);
        Assert.IsFalse(result.Scope.HumanReleaseApprovalClaimed);
        Assert.IsFalse(result.Scope.HumanAcceptanceClaimed);
        Assert.IsTrue(result.Contracts.DeleteRangeCandidatesAvailable);
        Assert.IsTrue(result.Contracts.ContextDeviationSuggestionDecisionRecorded);
        Assert.IsTrue(result.Contracts.LongFormOpeningBodyEndingIncluded);
        Assert.IsTrue(result.Contracts.PlanMappedToComposition);
        Assert.IsTrue(result.Contracts.SubtitleCorrectionReflected);
        Assert.IsTrue(result.Contracts.TopicHeadingsConfigured);
        Assert.IsTrue(result.Contracts.SourceTraceabilityClosed);
        Assert.IsTrue(result.Contracts.SyntheticDeliveryBindingsValidated);
        Assert.AreEqual(2, result.LongForm.TopicElementCount);
        Assert.IsFalse(result.LongForm.ExternalPublicationReady);
        Assert.AreEqual(2, result.Traceability.UsedSourceSpanCount);
        Assert.AreEqual(0, result.Traceability.UnresolvedOriginCount);
        Assert.AreEqual(0, result.Traceability.UnresolvedOutputTimeCount);
        Assert.AreEqual(
            result.Traceability.OutputProbeCount,
            result.Traceability.ResolvedOutputProbeCount);
        Assert.AreEqual(1, result.SubtitleEvidence.ChangedTokenCount);
        Assert.IsTrue(result.SubtitleEvidence.RevisedCandidateBoundToCorrection);
        Assert.IsTrue(result.SubtitleEvidence.RevisedCandidateTextDigestValid);
        Assert.IsTrue(result.SubtitleEvidence.RevisedCandidateSourceRevisionDigestMatches);
        Assert.IsTrue(result.SubtitleEvidence.WebVttDigestValid);
        Assert.AreEqual(1, result.SubtitleEvidence.WebVttCueCount);
        Assert.IsTrue(result.QualityControl.StaticEvidenceValidated);
        Assert.AreEqual("synthetic-static-contract", result.QualityControl.EvidenceLevel);
        Assert.IsFalse(result.QualityControl.ActualMediaQcExecuted);
        Assert.IsTrue(result.QualityControl.SyntheticDownstreamFixtureUsed);
        Assert.IsFalse(result.QualityControl.ActualNumericMeasurementsDisclosed);
        Assert.AreEqual("synthetic-contract-only", result.Delivery.ApprovalEvidenceLevel);
        Assert.IsFalse(result.Delivery.HumanApprovalClaimed);
        Assert.IsTrue(result.Delivery.ApprovalEvidenceDigestValid);
        Assert.IsTrue(result.Delivery.DeliveryEvidenceDigestValid);
        Assert.IsTrue(result.Delivery.DigestStabilityValidated);
        Assert.IsTrue(result.NegativeControls.BrokenProvenance.Detected);
        Assert.IsTrue(result.NegativeControls.StaleApproval.Detected);
        Assert.IsTrue(result.NegativeControls.UnconfirmedRights.Detected);
        Assert.AreEqual(0, result.NegativeControls.BrokenProvenance.DependencySideEffectCount);
        Assert.AreEqual(0, result.NegativeControls.StaleApproval.DependencySideEffectCount);
        Assert.AreEqual(0, result.NegativeControls.UnconfirmedRights.DependencySideEffectCount);
        Assert.AreEqual(result.HumanChecklist.RequiredCount, result.HumanChecklist.Items.Count);
        Assert.IsTrue(result.HumanChecklist.Items.All(static item =>
            string.Equals(item.Status, "PENDING", StringComparison.Ordinal)));
        Assert.IsTrue(result.AutomatedCoverage.EnumeratedAutomatedContractsOnly);
        Assert.IsTrue(result.AutomatedCoverage.KnownLimitations.Count >= 4);

        WriteResultIfRequested(result);
    }

    private static void WriteResultIfRequested(AcceptanceEvidence result)
    {
        string? requestedPath = Environment.GetEnvironmentVariable(
            ResultPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            });
        string canonical = CanonicalJson.Canonicalize(json);
        string[] forbiddenValues =
        [
            SyntheticActorSid,
            MachineTopicStatementText,
            ReviewedTopicStatementText,
            DeliveryStatementText,
            PreludeText,
            "opening.mp4",
            "key-visual.png",
            "logo.svg",
            "jingle.wav",
            "synthetic-source.mov",
            "ending.mp4",
            "projects/q001/renders/final.mp4",
        ];
        bool exposesForbiddenValue = forbiddenValues.Any(value =>
            canonical.Contains(value, StringComparison.Ordinal));
        bool exposesAbsolutePath = Regex.IsMatch(
            canonical,
            "(?:[A-Za-z]:[\\\\/]|/mnt/[A-Za-z]/)",
            RegexOptions.CultureInvariant);
        bool exposesUncPath = canonical.Contains(@"\\\\", StringComparison.Ordinal);
        bool exposesSid = Regex.IsMatch(
            canonical,
            "S-[0-9]+(?:-[0-9]+){2,}",
            RegexOptions.CultureInvariant);
        if (exposesForbiddenValue
            || exposesAbsolutePath
            || exposesUncPath
            || exposesSid)
        {
            Assert.Fail("The Q-001 acceptance result contains private evidence.");
        }

        string fullPath = Path.GetFullPath(requestedPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Fail("The Q-001 acceptance result path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string partialPath = string.Concat(fullPath, ".partial");
        try
        {
            File.WriteAllText(partialPath, canonical, StrictUtf8);
            File.Move(partialPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static EntityId<TTag> Id<TTag>(int value)
    {
        string suffix = value.ToString("D12", CultureInfo.InvariantCulture);
        return EntityId<TTag>.Parse(
            string.Concat("73000000-0000-0000-0000-", suffix));
    }

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(value));

    private sealed class AcceptanceFixture : IDisposable
    {
        private static readonly DateTimeOffset Baseline = new(
            2051,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero);

        private readonly string _rootPath;

        internal AcceptanceFixture()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.Q001.Acceptance",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
        }

        internal async Task<AcceptanceEvidence> RunAsync()
        {
            Asset opening = CreateAsset(1, "opening.mp4");
            Asset keyVisual = CreateAsset(2, "key-visual.png");
            Asset logo = CreateAsset(3, "logo.svg");
            Asset jingle = CreateAsset(4, "jingle.wav");
            Asset body = CreateAsset(5, "synthetic-source.mov");
            Asset ending = CreateAsset(6, "ending.mp4");
            var performer = new Performer(
                Id<PerformerIdTag>(7),
                "Synthetic Host",
                "Host",
                Baseline.AddDays(-1));
            var project = new Project(
                Id<ProjectIdTag>(8),
                "Q-001 synthetic long-form fixture",
                "Automatable AC-001 contract fixture.",
                "Internal reviewers",
                PublicationScope.ExternalPublicationApproved,
                Baseline.AddDays(14),
                [body, logo, opening, ending, jingle, keyVisual],
                [performer]);

            EntityId<TranscriptSpeakerIdTag> speakerId = Id<TranscriptSpeakerIdTag>(9);
            EntityId<TranscriptRevisionIdTag> machineRevisionId =
                Id<TranscriptRevisionIdTag>(10);

            (Utterance preludeUtterance, SourceSpan preludeSpan) = CreateUtterance(
                11,
                speakerId,
                PreludeText,
                Range(0, 2),
                body,
                machineRevisionId);
            (Utterance topicOneUtterance, SourceSpan topicOneSpan) = CreateUtterance(
                12,
                speakerId,
                MachineTopicStatementText,
                Range(2, 4),
                body,
                machineRevisionId);
            (Utterance topicTwoUtterance, SourceSpan topicTwoSpan) = CreateUtterance(
                13,
                speakerId,
                DeliveryStatementText,
                Range(6, 4),
                body,
                machineRevisionId);
            var machineRevision = new TranscriptRevision(
                machineRevisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                Baseline,
                [preludeUtterance, topicOneUtterance, topicTwoUtterance]);
            SourceSpan[] machineSourceSpans = [preludeSpan, topicOneSpan, topicTwoSpan];

            Token topicOneToken = topicOneUtterance.Tokens.Single();
            TranscriptEditResult transcriptEdit = TranscriptEditor.ApplyTokenTextEdits(
                machineRevision,
                machineSourceSpans,
                [
                    new TranscriptTokenTextEdit(
                        topicOneToken.Id,
                        MachineTopicStatementText,
                        ReviewedTopicStatementText),
                ],
                Id<TranscriptRevisionIdTag>(14),
                SyntheticActorSid,
                Baseline.AddMinutes(10),
                "Record deterministic subtitle correction evidence.");
            TranscriptRevision revision = transcriptEdit.Revision;
            IReadOnlyList<SourceSpan> revisedSourceSpans = transcriptEdit.RevisedSourceSpans;

            TopicBoundaryScanResult boundaryScan = TopicBoundaryService.FindCandidates(
                revision,
                revisedSourceSpans,
                Id<ModelRunIdTag>(15));
            TopicRangeRevision topicOneRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                Id<TopicRangeIdTag>(16),
                Id<TopicRangeRevisionIdTag>(17),
                startBoundarySuggestionId: null,
                endBoundarySuggestionId: null,
                Range(2, 4),
                SyntheticActorSid,
                Baseline.AddMinutes(20),
                "Define deterministic topic one range.");
            TopicRangeRevision topicTwoRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                Id<TopicRangeIdTag>(18),
                Id<TopicRangeRevisionIdTag>(19),
                startBoundarySuggestionId: null,
                endBoundarySuggestionId: null,
                Range(6, 4),
                SyntheticActorSid,
                Baseline.AddMinutes(21),
                "Define deterministic topic two range.");

            SpeakerAssignmentHistory assignments = SpeakerAssignmentService.SetAssignment(
                project,
                revision,
                history: null,
                Id<SpeakerAssignmentRevisionIdTag>(20),
                speakerId,
                performer.Id,
                "Host",
                SpeakerDisplayFormat.FullName,
                SyntheticActorSid,
                Baseline.AddMinutes(30),
                "Confirm deterministic performer evidence.");

            TopicMetadataRevision topicOneMetadata = CreateTopicMetadata(
                project,
                revision,
                revisedSourceSpans,
                topicOneRange,
                assignments,
                ordinal: 1,
                title: "Architecture",
                importantSourceSpan: revisedSourceSpans[1],
                idOffset: 100);
            TopicMetadataRevision topicTwoMetadata = CreateTopicMetadata(
                project,
                revision,
                revisedSourceSpans,
                topicTwoRange,
                assignments,
                ordinal: 2,
                title: "Delivery",
                importantSourceSpan: revisedSourceSpans[2],
                idOffset: 200);

            ContextDeviationScanResult deletionScan =
                ContextDeviationDetector.FindCandidates(
                    revision,
                    revisedSourceSpans,
                    [topicOneRange, topicTwoRange],
                    Id<ModelRunIdTag>(21));
            ContentDigest transcriptDigest = transcriptEdit.RevisionDigest;
            ContextDeviationSuggestionCandidate deletionCandidate =
                SuggestionService.Register(
                    deletionScan.Candidates.Single(candidate =>
                        candidate.Features.Position
                        == ContextDeviationPosition.LeadingBeforeFirstTopic),
                    registeredCandidates: []);
            SuggestionRejectionResult deletionDecision = SuggestionService.Reject(
                deletionCandidate,
                transcriptDigest,
                decisionHistory: [],
                Id<SuggestionDecisionIdTag>(220),
                SyntheticActorSid,
                Baseline.AddMinutes(22),
                "Synthetic deterministic rejection record for Q-001 coverage.");
            bool contextDeviationDecisionRecorded =
                deletionDecision.OriginalCandidate.Id == deletionCandidate.Id
                && deletionDecision.DecidedCandidate.Status == SuggestionStatus.Rejected
                && deletionDecision.DecisionRecord.Decision == SuggestionDecisionKind.Rejected
                && deletionDecision.DecisionRecord.StatusAfterDecision == SuggestionStatus.Rejected
                && deletionDecision.DecisionRecord.SourceDigest == transcriptDigest
                && string.Equals(
                    deletionDecision.DecisionRecord.ActorSid,
                    SyntheticActorSid,
                    StringComparison.Ordinal);
            var provenance = new LongFormProvenanceSnapshot(
                project.Id,
                revision.Id,
                transcriptDigest,
                revisedSourceSpans,
                CreateEditorialRecordCatalog());
            BundledRenderProfileDefinition bundledProfile =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")));
            AppliedBrandTemplate template = CreateTemplate(
                bundledProfile,
                keyVisual,
                logo,
                jingle);

            LongFormCompositionRequest request = CreateLongFormRequest(
                project,
                provenance,
                template,
                bundledProfile,
                assignments,
                topicOneRange,
                topicOneMetadata,
                topicTwoRange,
                topicTwoMetadata,
                revisedSourceSpans,
                opening,
                keyVisual,
                logo,
                jingle,
                body,
                ending);
            LongFormCompositionPlan plan = LongFormComposer.Compose(request);

            LongFormElementPlan openingElement = plan.Elements.Single(
                static element => element.Kind == LongFormElementKind.Opening);
            LongFormElementPlan bodyElement = plan.Elements.Single(
                static element => element.Kind == LongFormElementKind.Body);
            LongFormElementPlan endingElement = plan.Elements.Single(
                static element => element.Kind == LongFormElementKind.Ending);
            LongFormElementPlan[] topicElements = plan.Elements
                .Where(static element => element.Kind == LongFormElementKind.Topic)
                .ToArray();

            bool bodyPartitionedByTopics = bodyElement.OutputRange is TimeRange bodyRange
                && topicElements.Length == 2
                && topicElements.All(static topic =>
                    topic.State == LongFormElementState.Included
                    && topic.OutputRange is not null
                    && topic.TopicRange is not null)
                && topicElements[0].OutputRange!.Value.Start == bodyRange.Start
                && topicElements[0].OutputRange!.Value.End
                    == topicElements[1].OutputRange!.Value.Start
                && topicElements[1].OutputRange!.Value.End == bodyRange.End;

            SubtitleSegmentationResult segmentation = SubtitleSegmenter.Segment(
                revision,
                transcriptDigest,
                revisedSourceSpans);
            EntityId<TranscriptTokenIdTag> correctedTokenId =
                revisedSourceSpans[1].TranscriptTokenIds.Single();
            SubtitleCueCandidate correctedCandidate = segmentation.Candidates.Single(
                candidate =>
                    candidate.EvidenceTokenIds.Contains(correctedTokenId)
                    && candidate.SourceRevisionDigest == transcriptDigest);

            LayoutRegion subtitleRegion = bundledProfile.Layout.Regions.Single(
                static region => region.Role == LayoutRegionRole.Subtitle);
            var subtitleLayoutProfile = new SubtitleLayoutProfile(
                bundledProfile.Layout.Id,
                Digest("q001.synthetic.subtitle-layout-profile.v1"),
                [bundledProfile.Layout.SafeZones[0].Name],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(kind => new SubtitleSoftConstraintWeight(kind, 1L))
                    .ToArray());
            var subtitlePlacement = new SubtitlePlacementCandidate(
                Id<SubtitlePlacementCandidateIdTag>(401),
                subtitleRegion.Name,
                new SubtitleLayoutRect(
                    subtitleRegion.Rect.X,
                    subtitleRegion.Rect.Y,
                    subtitleRegion.Rect.Width,
                    subtitleRegion.Rect.Height));
            SubtitleLayoutResolution layoutResolution = SubtitleLayoutEngine.Resolve(
                new SubtitleLayoutRequest(
                    correctedCandidate,
                    bundledProfile.Layout,
                    subtitleLayoutProfile,
                    [subtitlePlacement],
                    prohibitedRegions: [],
                    faceRegions: [],
                    previousPlacement: null));
            Assert.AreEqual(
                SubtitleLayoutResolutionStatus.Resolved,
                layoutResolution.Status);

            bool revisedCandidateContainsCorrection =
                transcriptEdit.EditRecord.Diff.Changes
                .Count(static change => change.Kind == TranscriptTextChangeKind.Token) == 1
                && correctedCandidate.SourceRevisionDigest == transcriptDigest
                && correctedCandidate.EvidenceTokenIds.Contains(correctedTokenId)
                && correctedCandidate.Text.Contains(
                    ReviewedTopicStatementText,
                    StringComparison.Ordinal);

            LongFormElementPlan[] includedNonBody = plan.Elements
                .Where(static element =>
                    element.State == LongFormElementState.Included
                    && element.Kind != LongFormElementKind.Body)
                .ToArray();
            var mappedElements = new List<CompositionElement>(includedNonBody.Length);
            var planElementByCompositionId = new Dictionary<
                EntityId<CompositionElementIdTag>,
                LongFormElementPlan>();
            int elementOrdinal = 0;
            foreach (LongFormElementPlan element in includedNonBody)
            {
                OriginRef selectedOrigin = element.Kind switch
                {
                    LongFormElementKind.Opening or LongFormElementKind.Ending =>
                        element.Origins.First(static origin =>
                            origin.Kind == OriginKind.Asset),
                    LongFormElementKind.Topic =>
                        element.Origins.Single(static origin =>
                            origin.Kind == OriginKind.SourceSpan),
                    _ =>
                        element.Origins.Single(static origin =>
                            origin.Kind == OriginKind.EditorialRecord),
                };
                TimeRange? sourceRange = element.Kind == LongFormElementKind.Topic
                    ? element.TopicRange!.Range
                    : null;
                var mapped = new CompositionElement(
                    Id<CompositionElementIdTag>(500 + elementOrdinal),
                    element.OutputRange!.Value,
                    selectedOrigin,
                    adoptedSuggestion: null,
                    sourceRange: sourceRange);
                mappedElements.Add(mapped);
                planElementByCompositionId.Add(mapped.Id, element);
                elementOrdinal++;
            }

            bool planMappedToComposition = bodyPartitionedByTopics
                && mappedElements.Count == includedNonBody.Length
                && mappedElements.All(mapped =>
                {
                    LongFormElementPlan source = planElementByCompositionId[mapped.Id];
                    bool rangeMatches = mapped.OutputRange == source.OutputRange!.Value;
                    bool originMatches = source.Kind switch
                    {
                        LongFormElementKind.Opening or LongFormElementKind.Ending =>
                            mapped.Origin.Kind == OriginKind.Asset
                            && source.Origins.Contains(mapped.Origin),
                        LongFormElementKind.Topic =>
                            mapped.Origin.Kind == OriginKind.SourceSpan
                            && source.Origins.Contains(mapped.Origin)
                            && mapped.SourceRange == source.TopicRange!.Range,
                        _ => mapped.Origin.Kind == OriginKind.EditorialRecord
                            && source.Origins.Contains(mapped.Origin),
                    };
                    return rangeMatches && originMatches;
                });
            CompositionElement[] traceElements = mappedElements.ToArray();
            var composition = new CompositionVersion(
                Id<CompositionVersionIdTag>(202),
                project.Id,
                parentVersionId: null,
                SyntheticActorSid,
                Baseline.AddMinutes(40),
                "Create deterministic Q-001 trace composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(traceElements),
                traceElements);
            TimelineMap timelineMap = TimelineMapBuilder.Build(
                Id<TimelineMapIdTag>(203),
                composition);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timelineMap);

            int unresolvedOriginCount = timelineMap.Entries.Count(entry =>
                entry.Origin.Kind switch
                {
                    OriginKind.SourceSpan =>
                        !revisedSourceSpans.Any(span => span.Id == entry.Origin.SourceSpanId),
                    OriginKind.Asset =>
                        !project.Assets.Any(asset => asset.Id == entry.Origin.AssetId),
                    OriginKind.EditorialRecord =>
                        !provenance.TrustedEditorialRecordIds.Contains(entry.Origin.EditorialRecordId!),
                    _ => true,
                });
            int outputProbeCount = timelineMap.Entries.Count;
            int resolvedOutputProbeCount = 0;
            int unresolvedOutputTimeCount = 0;
            int reverseCount = 0;
            foreach (TimelineMapEntry entry in timelineMap.Entries)
            {
                RationalTime probe = entry.OutputRange.Start;
                if (!timelineMap.TryMapOutputTime(probe, out TimelineMapPoint point))
                {
                    unresolvedOutputTimeCount++;
                    continue;
                }

                resolvedOutputProbeCount++;
                if (point.Origin.SourceSpanId is EntityId<SourceSpanIdTag> sourceSpanId
                    && point.SourceTime is RationalTime sourceTime)
                {
                    reverseCount += timelineMap.MapSourceTimeToOutput(
                        sourceSpanId,
                        sourceTime).Count(mapped => mapped == probe);
                }
            }

            var renderProfile = new RenderProfile(
                Id<RenderProfileIdTag>(204),
                "q001-synthetic-profile",
                Digest("q001.synthetic.profile.v1"),
                1920,
                1080,
                30,
                1,
                bundledProfile.Layout.Id,
                bundledProfile.Profile.TemplateId,
                bundledProfile.Profile.TemplateVersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            var graphInputs = new List<RenderGraphInput>();
            var graphNodes = new List<RenderNode>();
            EntityId<RenderNodeIdTag>? currentVisualNodeId = null;
            int graphOrdinal = 0;
            int nextRenderInputId = 205;
            int nextRenderNodeId = 300;
            foreach (CompositionElement element in traceElements)
            {
                var visualInput = new RenderGraphInput(
                    Id<RenderInputIdTag>(nextRenderInputId++),
                    RenderInputKind.Video,
                    Digest($"q001.synthetic.visual-input.{graphOrdinal}.v1"),
                    4096 + graphOrdinal,
                    element.Origin);
                graphInputs.Add(visualInput);
                RenderNode visual = RenderNode.Video(
                    Id<RenderNodeIdTag>(nextRenderNodeId++),
                    visualInput.Id,
                    element.OutputRange,
                    element.Origin,
                    element.SourceRange,
                    "primary-video");
                graphNodes.Add(visual);
                if (currentVisualNodeId is null)
                {
                    currentVisualNodeId = visual.Id;
                }
                else
                {
                    RenderNode cut = RenderNode.TransitionEffect(
                        Id<RenderNodeIdTag>(nextRenderNodeId++),
                        [currentVisualNodeId, visual.Id],
                        new TimeRange(visual.OutputRange.Start, default),
                        visual.Origin!,
                        Transition.Cut());
                    graphNodes.Add(cut);
                    currentVisualNodeId = cut.Id;
                }

                graphOrdinal++;
            }

            var subtitleHistory = SubtitleService.CreateInitial(
                composition,
                correctedCandidate,
                layoutResolution,
                Id<CueIdTag>(900),
                Id<SubtitleCueRevisionIdTag>(901),
                topicElements.Single(element =>
                    element.TopicRange!.TopicRangeId ==
                        topicOneRange.TopicRangeId).OutputRange!.Value,
                SyntheticActorSid,
                Baseline.AddMinutes(41),
                "Create initial subtitle cue from corrected human transcript.");
            SubtitleCueSnapshot subtitleSnapshot = subtitleHistory.CurrentRevision.Snapshot;
            SubtitleDisplaySourceCue subtitleSourceCue = SubtitleDisplayMapper.MapSource(
                subtitleSnapshot);
            var subtitleDisplayProfile = new SubtitleDisplayProfile(
                Digest("q001.synthetic.subtitle-display-profile.v1"),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve);
            SubtitleDisplayResult subtitleDisplayResult = SubtitleDisplayPolicy.Apply(
                subtitleDisplayProfile,
                [subtitleSourceCue],
                selectedCueIds: []);
            SubtitleFileCue subtitleFileCue = SubtitleDisplayMapper.MapForExport(
                subtitleSnapshot,
                subtitleDisplayResult.DisplayedCues.Single());
            SubtitleExportFile webVtt = WebVttSubtitleWriter.Write([subtitleFileCue]);
            bool subtitleCorrectionReflected =
                revisedCandidateContainsCorrection
                && string.Equals(
                    subtitleSnapshot.Text,
                    correctedCandidate.Text,
                    StringComparison.Ordinal)
                && StrictUtf8.GetString(webVtt.GetUtf8Bytes()).Contains(
                    correctedCandidate.Text,
                    StringComparison.Ordinal);

            Assert.IsNotNull(currentVisualNodeId);
            RenderGraphInput subtitleInput = new(
                Id<RenderInputIdTag>(nextRenderInputId++),
                RenderInputKind.Subtitle,
                webVtt.ContentDigest,
                webVtt.ByteSize,
                correctedCandidate.Origin);
            graphInputs.Add(subtitleInput);
            RenderNode subtitleNode = RenderNode.Subtitle(
                Id<RenderNodeIdTag>(nextRenderNodeId++),
                subtitleInput.Id,
                currentVisualNodeId!,
                subtitleSnapshot.Cue,
                correctedCandidate.Origin);
            graphNodes.Add(subtitleNode);

            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(nextRenderNodeId++),
                [currentVisualNodeId!, subtitleNode.Id],
                new TimeRange(default, timelineMap.Entries[^1].OutputRange.End));
            graphNodes.Add(output);
            SourceSpan[] renderValidationSourceSpans =
            [
                .. revisedSourceSpans,
                correctedCandidate.SourceSpan,
            ];
            RenderFontEvidence[] fontEvidence =
            [
                new RenderFontEvidence(
                    "synthetic-subtitle-font",
                    Digest("q001.synthetic.font.subtitle.v1")),
            ];
            var graph = new RenderGraph(
                Id<RenderGraphIdTag>(211),
                composition.Id,
                composition.ContentDigest,
                timelineMap.Id,
                timelineDigest,
                renderProfile.Id,
                renderProfile.ProfileDigest,
                bundledProfile.Layout.Id,
                Digest("q001.synthetic.layout.v1"),
                template.Reference,
                graphInputs,
                fontEvidence,
                graphNodes,
                output.Id);
            var validationContext = new RenderGraphValidationContext(
                project,
                composition,
                timelineMap,
                timelineDigest,
                renderProfile,
                bundledProfile.Layout,
                Digest("q001.synthetic.layout.v1"),
                template.Reference,
                renderValidationSourceSpans,
                provenance.TrustedEditorialRecordIds,
                graphInputs.Select(input =>
                    new RenderArtifactEvidence(
                        input.Id,
                        input.ContentDigest,
                        input.ByteSize)).ToArray(),
                fontEvidence);
            RenderGraphValidationResult graphValidation = RenderGraphValidator.Validate(
                graph,
                validationContext);
            Assert.IsTrue(
                graphValidation.IsValid,
                string.Join(",", graphValidation.Issues.Select(issue => issue.Kind.ToString())));

            var outputArtifact = new ManifestArtifact(
                "video",
                "video/mp4",
                "ja",
                512,
                Digest("q001.synthetic.render-output.v1"),
                "projects/q001/renders/final.mp4");
            var renderManifest = new RenderManifest(
                Id<RenderManifestIdTag>(212),
                composition,
                timelineMap,
                timelineDigest,
                renderProfile,
                Digest("q001.synthetic.render-config.v1"),
                "1.0.0-q001-synthetic",
                Digest("q001.synthetic.application.v1"),
                graphInputs.Select((input, index) =>
                    new RenderInputEvidence(
                        $"source-{index + 1}",
                        input.ContentDigest)).ToArray(),
                [
                    new RenderToolEvidence(
                        "ffmpeg",
                        DecodeValidator.CanonicalPackageVersion,
                        "synthetic-contract-no-execution",
                        new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                        containerImageDigest: null),
                ],
                models: [],
                fontEvidence,
                [outputArtifact]);

            EntityId<ArtifactIdTag> outputArtifactId = Id<ArtifactIdTag>(213);
            var storedArtifact = new StoredArtifact(
                outputArtifactId,
                project.Id,
                ArtifactKind.Render,
                outputArtifact.StorageReference,
                outputArtifact.Sha256,
                outputArtifact.ByteSize,
                Baseline.AddMinutes(50));
            var qcRequest = new OutputQcRequest(
                outputArtifactId,
                renderManifest,
                graph,
                validationContext,
                new OutputQcSubtitleEvidence(
                    subtitleLayoutProfile.VersionDigest,
                    subtitleDisplayProfile.VersionDigest,
                    subtitleDisplayResult,
                    [
                        new OutputQcSubtitleLayoutEvidence(
                            subtitleSnapshot.Cue.Id,
                            layoutResolution),
                    ]),
                TimeSpan.FromMinutes(1));
            OutputQc.ValidateEvidence(qcRequest, CancellationToken.None);
            RationalTime timelineEnd = timelineMap.Entries[^1].OutputRange.End;
            var timing = new OutputQcStreamTiming(
                streamIndex: 0,
                [new OutputQcTimingEntry(0, 0, timelineEnd.Value)],
                new RationalTime(1, 1),
                presentationStart: default,
                presentationEnd: timelineEnd,
                Digest("q001.synthetic.video-timing.v1"));
            var qualityControl = new OutputQcResult(
                storedArtifact,
                qcRequest,
                Digest("q001.synthetic.probe.v1"),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                timing,
                audioTiming: null,
                subtitleTiming: null,
                new OutputQcDecodeEvidence(TimeSpan.Zero),
                new OutputQcLumaMeasurements(
                    [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                    Digest("q001.synthetic.luma.v1")),
                audioMeasurements: null);

            var approval = new StoredApproval(
                Id<ApprovalIdTag>(214),
                project.Id,
                composition.Id,
                composition.ContentDigest,
                SyntheticActorSid,
                ReviewActorRole.ContentApprover,
                Baseline.AddMinutes(51),
                "Synthetic contract fixture; not a human decision.");
            var artifacts = new Dictionary<EntityId<ArtifactIdTag>, StoredArtifact>
            {
                [outputArtifactId] = storedArtifact,
            };
            int approvalEvaluationCount = 0;
            int artifactReadCount = 0;
            int artifactValidationCount = 0;
            var deliveryService = new DeliveryService(
                (_, approvals) =>
                {
                    approvalEvaluationCount++;
                    return new ApprovalValidityResult(
                        approvals.ToArray(),
                        staleApprovals: []);
                },
                artifactId =>
                {
                    artifactReadCount++;
                    return artifacts.GetValueOrDefault(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    artifactValidationCount++;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(artifacts[artifactId]);
                });
            IReadOnlyList<KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
                artifactEvidence =
                [
                    new(outputArtifactId, outputArtifact),
                ];
            DeliveryCreationResult delivery = await deliveryService.CreateAsync(
                Id<DeliveryManifestIdTag>(215),
                project,
                composition,
                renderManifest,
                validationContext,
                qualityControl,
                [approval],
                artifactEvidence);
            Assert.AreEqual(1, approvalEvaluationCount);
            Assert.AreEqual(1, artifactReadCount);
            Assert.AreEqual(1, artifactValidationCount);

            DeliveryCreationResult stableDelivery = await new DeliveryService(
                (_, approvals) => new ApprovalValidityResult(
                    approvals.ToArray(),
                    staleApprovals: []),
                artifactId => artifacts[artifactId],
                (artifactId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(artifacts[artifactId]);
                }).CreateAsync(
                    Id<DeliveryManifestIdTag>(215),
                    project,
                    composition,
                    renderManifest,
                    validationContext,
                    qualityControl,
                    [approval],
                    [new(outputArtifactId, outputArtifact)]);
            bool digestStabilityValidated =
                stableDelivery.ApprovalEvidenceDigest == delivery.ApprovalEvidenceDigest
                && stableDelivery.EvidenceDigest == delivery.EvidenceDigest;

            int brokenDependencySideEffects = 0;
            DeliveryException brokenException =
                await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                    CreateSideEffectCountingService(
                            artifacts,
                            () => brokenDependencySideEffects++)
                        .CreateAsync(
                            Id<DeliveryManifestIdTag>(216),
                            project,
                            composition,
                            renderManifest,
                            CreateDigestMismatchContext(
                                project,
                                composition,
                                timelineMap,
                                timelineDigest,
                                renderProfile,
                                bundledProfile.Layout,
                                validationContext,
                                renderValidationSourceSpans,
                                revisedSourceSpans[1].Id),
                            qualityControl,
                            [approval],
                            artifactEvidence));
            Assert.AreEqual(DeliveryFailureReason.ProvenanceInvalid, brokenException.Reason);
            Assert.AreEqual(0, brokenDependencySideEffects);

            int staleDependencySideEffects = 0;
            var staleApproval = new StoredApproval(
                Id<ApprovalIdTag>(217),
                project.Id,
                Id<CompositionVersionIdTag>(218),
                Digest("q001.synthetic.stale-composition.v1"),
                SyntheticActorSid,
                ReviewActorRole.ContentApprover,
                Baseline.AddMinutes(52),
                "Synthetic stale approval negative control.");
            DeliveryException staleException =
                await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                    CreateSideEffectCountingService(
                            artifacts,
                            () => staleDependencySideEffects++)
                        .CreateAsync(
                            Id<DeliveryManifestIdTag>(219),
                            project,
                            composition,
                            renderManifest,
                            validationContext,
                            qualityControl,
                            [staleApproval],
                            artifactEvidence));
            Assert.AreEqual(DeliveryFailureReason.StaleApproval, staleException.Reason);
            Assert.AreEqual(0, staleDependencySideEffects);

            Asset unconfirmedBody = new(
                body.Id,
                body.FileName,
                body.MediaType,
                body.ContentDigest,
                body.ByteSize,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    body.Rights.AllowedUse,
                    externalPublicationAllowed: false));
            var projectWithUnconfirmedRights = new Project(
                project.Id,
                project.ContentName,
                project.Purpose,
                project.Audience,
                project.PublicationScope,
                project.Deadline,
                project.Assets.Select(asset =>
                    asset.Id == unconfirmedBody.Id
                        ? unconfirmedBody
                        : asset).ToArray(),
                project.Performers);
            var unconfirmedValidationContext = new RenderGraphValidationContext(
                projectWithUnconfirmedRights,
                composition,
                timelineMap,
                timelineDigest,
                renderProfile,
                bundledProfile.Layout,
                Digest("q001.synthetic.layout.v1"),
                template.Reference,
                renderValidationSourceSpans,
                provenance.TrustedEditorialRecordIds,
                graphInputs.Select(input =>
                    new RenderArtifactEvidence(
                        input.Id,
                        input.ContentDigest,
                        input.ByteSize)).ToArray(),
                fontEvidence);
            RenderGraphValidationResult unconfirmedRightsResult =
                RenderGraphValidator.Validate(
                    graph,
                    unconfirmedValidationContext);
            Assert.IsTrue(unconfirmedRightsResult.Issues.Any(static issue =>
                issue.Kind == RenderGraphValidationIssueKind.ExternalRightsNotApproved));
            int unconfirmedRightsDependencySideEffects = 0;
            if (unconfirmedRightsResult.IsValid)
            {
                _ = await CreateSideEffectCountingService(
                        artifacts,
                        () => unconfirmedRightsDependencySideEffects++)
                    .CreateAsync(
                        Id<DeliveryManifestIdTag>(240),
                        projectWithUnconfirmedRights,
                        composition,
                        renderManifest,
                        unconfirmedValidationContext,
                        qualityControl,
                        [approval],
                        artifactEvidence);
            }

            Assert.AreEqual(0, unconfirmedRightsDependencySideEffects);

            bool sourceTraceabilityClosed = unresolvedOriginCount == 0
                && unresolvedOutputTimeCount == 0
                && resolvedOutputProbeCount == outputProbeCount
                && delivery.UsedSourceSpans.Count == 2
                && reverseCount == delivery.UsedSourceSpans.Count;
            bool syntheticDeliveryBindingsValidated =
                delivery.Manifest.CompositionVersionId == composition.Id
                && delivery.Manifest.CompositionVersionDigest == composition.ContentDigest
                && delivery.Manifest.Artifacts.Count == 1;

            HumanChecklistItem[] checklistItems =
            [
                new("rights-cleared-actual-media-ingest-and-asr", "PENDING"),
                new("human-transcript-correction", "PENDING"),
                new("human-candidate-disposition", "PENDING"),
                new("human-speaker-and-topic-review", "PENDING"),
                new("long-form-subtitle-emphasis-thumbnail-visual-review", "PENDING"),
                new("exact-ffmpeg-render-and-actual-qc", "PENDING"),
                new("trace-review", "PENDING"),
                new("authorized-approval-and-export", "PENDING"),
            ];

            return new AcceptanceEvidence(
                "1.0.0",
                "Q-001",
                new ScopeEvidence(
                    "deterministic-rights-safe-synthetic-contract",
                    ActualMediaUsed: false,
                    ActualAsrExecuted: false,
                    ActualModelInferenceUsed: false,
                    ActualExportPolicyAuthorized: false,
                    ActualFinalRenderExecuted: false,
                    ActualFfmpegExecuted: false,
                    ActualMediaQcExecuted: false,
                    ActualThumbnailGenerated: false,
                    SyntheticApprovalFixtureUsed: true,
                    SyntheticDecisionRecordUsed: true,
                    SyntheticDownstreamFixtureUsed: true,
                    HumanCandidateDispositionClaimed: false,
                    HumanReleaseApprovalClaimed: false,
                    HumanAcceptanceClaimed: false),
                new ContractEvidence(
                    DeleteRangeCandidatesAvailable:
                        deletionScan.Candidates.Count > 0
                        && deletionCandidate.Kind == SuggestionKind.DeleteRange
                        && deletionCandidate.Status == SuggestionStatus.Proposed
                        && deletionDecision.OriginalCandidate.Status
                            == SuggestionStatus.Proposed,
                    ContextDeviationSuggestionDecisionRecorded:
                        contextDeviationDecisionRecorded,
                    LongFormOpeningBodyEndingIncluded:
                        openingElement.State == LongFormElementState.Included
                        && bodyElement.State == LongFormElementState.Included
                        && endingElement.State == LongFormElementState.Included,
                    PlanMappedToComposition: planMappedToComposition,
                    SubtitleCorrectionReflected: subtitleCorrectionReflected,
                    TopicHeadingsConfigured:
                        topicElements.Length == 2
                        && topicElements.All(topic =>
                            topic.State == LongFormElementState.Included
                            && !string.IsNullOrWhiteSpace(topic.Text)),
                    SourceTraceabilityClosed: sourceTraceabilityClosed,
                    SyntheticDeliveryBindingsValidated:
                        syntheticDeliveryBindingsValidated),
                new LongFormEvidence(
                    plan.Elements.Count,
                    topicElements.Length,
                    plan.TimingComplete,
                    plan.ExternalRightsReady,
                    plan.ExternalPublicationReady,
                    plan.Warnings.Count),
                new DeleteCandidateEvidence(
                    deletionScan.Candidates.Count,
                    deletionScan.Candidates.Count(candidate =>
                        candidate.Features.Position == ContextDeviationPosition.LeadingBeforeFirstTopic),
                    deletionCandidate.Payload.Features.Position.ToString(),
                    deletionCandidate.Payload.RuleVersion,
                    deletionCandidate.Payload.Suggestion.Reason,
                    CandidateProposed: deletionCandidate.Status == SuggestionStatus.Proposed,
                    CandidateRejected:
                        deletionDecision.DecidedCandidate.Status == SuggestionStatus.Rejected,
                    DecisionKind: deletionDecision.DecisionRecord.Decision.ToString(),
                    StatusAfterDecision:
                        deletionDecision.DecisionRecord.StatusAfterDecision.ToString(),
                    DecisionSourceDigestMatches:
                        deletionDecision.DecisionRecord.SourceDigest == transcriptDigest,
                    DecisionActorBound: string.Equals(
                        deletionDecision.DecisionRecord.ActorSid,
                        SyntheticActorSid,
                        StringComparison.Ordinal)),
                new SubtitleEvidence(
                    transcriptEdit.EditRecord.Diff.Changes.Count(static change =>
                        change.Kind == TranscriptTextChangeKind.Token),
                    segmentation.Candidates.Count,
                    transcriptEdit.DownstreamStale.SourceRevisionDigest ==
                        ContentDigest.Compute(StrictUtf8.GetBytes(
                            TranscriptSnapshotJson.WriteRevision(
                                transcriptEdit.SourceRevision))),
                    transcriptEdit.RevisionDigest == ContentDigest.Compute(
                        StrictUtf8.GetBytes(
                            TranscriptSnapshotJson.WriteRevision(revision))),
                    RevisedCandidateBoundToCorrection:
                        correctedCandidate.EvidenceTokenIds.Contains(correctedTokenId)
                        && correctedCandidate.Text.Contains(
                            ReviewedTopicStatementText,
                            StringComparison.Ordinal),
                    RevisedCandidateTextDigestValid:
                        ContentDigest.Compute(
                            StrictUtf8.GetBytes(correctedCandidate.Text)).Hex.Length == 64,
                    RevisedCandidateSourceRevisionDigestMatches:
                        correctedCandidate.SourceRevisionDigest == transcriptDigest,
                    WebVttDigestValid:
                        webVtt.ContentDigest == ContentDigest.Compute(
                            webVtt.GetUtf8Bytes()),
                    WebVttByteSize: webVtt.ByteSize,
                    WebVttCueCount: webVtt.CueCount),
                new TraceabilityEvidence(
                    outputProbeCount,
                    resolvedOutputProbeCount,
                    reverseCount,
                    delivery.UsedSourceSpans.Count,
                    unresolvedOriginCount,
                    unresolvedOutputTimeCount),
                new DeliveryEvidence(
                    syntheticDeliveryBindingsValidated,
                    delivery.Manifest.Artifacts.Count,
                    ApprovalEvidenceDigestValid:
                        delivery.ApprovalEvidenceDigest.Hex.Length == 64,
                    DeliveryEvidenceDigestValid:
                        delivery.EvidenceDigest.Hex.Length == 64,
                    ApprovalEvidenceLevel: "synthetic-contract-only",
                    HumanApprovalClaimed: false,
                    DigestStabilityValidated: digestStabilityValidated),
                new QualityControlEvidence(
                    EvidenceLevel: "synthetic-static-contract",
                    StaticEvidenceValidated: true,
                    ActualMediaQcExecuted: false,
                    SyntheticDownstreamFixtureUsed: true,
                    ActualNumericMeasurementsDisclosed: false),
                new NegativeControlsEvidence(
                    new NegativeControlEvidence(
                        "source-span-digest-mismatch",
                        "delivery-create",
                        brokenException.Reason.ToString(),
                        brokenDependencySideEffects,
                        Detected: true),
                    new NegativeControlEvidence(
                        "stale-approval",
                        "delivery-create",
                        staleException.Reason.ToString(),
                        staleDependencySideEffects,
                        Detected: true),
                    new NegativeControlEvidence(
                        "unconfirmed-rights",
                        "render-graph-precompile",
                        RenderGraphValidationIssueKind.ExternalRightsNotApproved
                            .ToString(),
                        unconfirmedRightsDependencySideEffects,
                        Detected: true)),
                new HumanChecklistEvidence(
                    checklistItems.Length,
                    CompletedCount: 0,
                    checklistItems,
                    "PENDING"),
                new AutomatedCoverageEvidence(
                    EnumeratedAutomatedContractsOnly: true,
                    KnownLimitations:
                    [
                        "Actual ExportPolicy S-011 authorization is not executed in this synthetic contract fixture.",
                        "Actual FFmpeg final render is not executed in this synthetic contract fixture.",
                        "Actual media quality control measurement is not executed in this synthetic contract fixture.",
                        "Actual emphasis and thumbnail visual verification remains human-only and pending.",
                        "Stale transcript, render-output mismatch, cancellation/restart, and cross-version artifact controls are not exercised by this aggregate fixture.",
                    ]),
                AutomatedPassed: true,
                HumanDecision: "PENDING",
                Q001Complete: false);
        }

        private static SourceSpan CreateDigestMismatchSpan(SourceSpan sourceSpan)
        {
            return new SourceSpan(
                sourceSpan.Id,
                sourceSpan.AssetId,
                sourceSpan.StreamId,
                sourceSpan.Range,
                Digest("q001.synthetic.broken-source-digest.v1"),
                sourceSpan.TranscriptRevisionId,
                sourceSpan.TranscriptTokenIds);
        }

        private static RenderGraphValidationContext CreateDigestMismatchContext(
            Project project,
            CompositionVersion composition,
            TimelineMap timelineMap,
            ContentDigest timelineDigest,
            RenderProfile profile,
            LayoutSpec layout,
            RenderGraphValidationContext sourceContext,
            IReadOnlyList<SourceSpan> sourceSpans,
            EntityId<SourceSpanIdTag> brokenSourceSpanId)
        {
            return new RenderGraphValidationContext(
                project,
                composition,
                timelineMap,
                timelineDigest,
                profile,
                layout,
                Digest("q001.synthetic.layout.v1"),
                sourceContext.Template,
                sourceSpans.Select(span => span.Id == brokenSourceSpanId
                    ? CreateDigestMismatchSpan(span)
                    : span).ToArray(),
                sourceContext.EditorialRecordIds,
                sourceContext.Artifacts,
                sourceContext.Fonts);
        }

        private static DeliveryService CreateSideEffectCountingService(
            IReadOnlyDictionary<EntityId<ArtifactIdTag>, StoredArtifact> artifacts,
            Action recordSideEffect) =>
            new(
                (_, approvals) =>
                {
                    recordSideEffect();
                    return new ApprovalValidityResult(
                        approvals.ToArray(),
                        staleApprovals: []);
                },
                artifactId =>
                {
                    recordSideEffect();
                    return artifacts.GetValueOrDefault(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    recordSideEffect();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(artifacts[artifactId]);
                });

        private static TopicMetadataRevision CreateTopicMetadata(
            Project project,
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            TopicRangeRevision range,
            SpeakerAssignmentHistory assignments,
            int ordinal,
            string title,
            SourceSpan importantSourceSpan,
            int idOffset)
        {
            TopicMetadataRevision created = TopicMetadataService.CreateInitial(
                project,
                revision,
                sourceSpans,
                range,
                assignments,
                ordinal,
                Id<TopicMetadataIdTag>(idOffset + 1),
                Id<TopicMetadataRevisionIdTag>(idOffset + 2),
                SyntheticActorSid,
                Baseline.AddMinutes(idOffset + 2),
                "Create deterministic topic metadata.");
            TopicMetadataRevision titled = TopicMetadataService.SetTitle(
                project,
                revision,
                sourceSpans,
                range,
                assignments,
                created,
                Id<TopicMetadataRevisionIdTag>(idOffset + 3),
                title,
                SyntheticActorSid,
                Baseline.AddMinutes(idOffset + 3),
                "Confirm deterministic topic title.");
            return TopicMetadataService.SetImportantStatements(
                project,
                revision,
                sourceSpans,
                range,
                assignments,
                titled,
                Id<TopicMetadataRevisionIdTag>(idOffset + 4),
                [importantSourceSpan.Id],
                SyntheticActorSid,
                Baseline.AddMinutes(idOffset + 4),
                "Bind deterministic important statement evidence.");
        }

        private static AppliedBrandTemplate CreateTemplate(
            BundledRenderProfileDefinition profile,
            Asset keyVisual,
            Asset logo,
            Asset jingle)
        {
            var version = new BrandTemplateVersion(
                Id<SeriesIdTag>(300),
                new TemplateReference(
                    profile.Profile.TemplateId,
                    "development-neutral-long-form",
                    profile.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                [
                    new TemplateParameterDefinition(
                        "program-name",
                        TemplateParameterValue.FromText("Hub Radio"),
                        editorOverrideAllowed: false),
                    new TemplateParameterDefinition(
                        "episode-number",
                        TemplateParameterValue.FromInteger(42),
                        editorOverrideAllowed: false),
                    new TemplateParameterDefinition(
                        "key-visual",
                        TemplateParameterValue.FromAssetReference(
                            keyVisual.Id,
                            keyVisual.ContentDigest),
                        editorOverrideAllowed: false),
                    new TemplateParameterDefinition(
                        "logo",
                        TemplateParameterValue.FromAssetReference(
                            logo.Id,
                            logo.ContentDigest),
                        editorOverrideAllowed: false),
                    new TemplateParameterDefinition(
                        "jingle",
                        TemplateParameterValue.FromAssetReference(
                            jingle.Id,
                            jingle.ContentDigest),
                        editorOverrideAllowed: false),
                ]);
            return version.ApplyEditorOverrides([]);
        }

        private static LongFormCompositionRequest CreateLongFormRequest(
            Project project,
            LongFormProvenanceSnapshot provenance,
            AppliedBrandTemplate template,
            BundledRenderProfileDefinition profile,
            SpeakerAssignmentHistory assignments,
            TopicRangeRevision topicOneRange,
            TopicMetadataRevision topicOneMetadata,
            TopicRangeRevision topicTwoRange,
            TopicMetadataRevision topicTwoMetadata,
            IReadOnlyList<SourceSpan> sourceSpans,
            Asset opening,
            Asset keyVisual,
            Asset logo,
            Asset jingle,
            Asset body,
            Asset ending)
        {
            LongFormElementInput preRoll = Included(
                1,
                LongFormElementKind.PreRollMargin,
                text: null,
                Range(0, 1),
                [EditorialOrigin(1)],
                assets: []);
            LongFormAssetReference[] openingAssets =
            [
                AssetReference(LongFormAssetRole.OpeningMedia, opening),
                AssetReference(LongFormAssetRole.KeyVisual, keyVisual),
                AssetReference(LongFormAssetRole.Logo, logo),
                AssetReference(LongFormAssetRole.Jingle, jingle),
            ];
            OriginRef[] openingOrigins =
            [
                EditorialOrigin(2),
                .. openingAssets.Select(reference => OriginRef.FromAsset(reference.AssetId)),
            ];
            LongFormElementInput openingElement = Included(
                2,
                LongFormElementKind.Opening,
                text: null,
                Range(1, 2),
                openingOrigins,
                openingAssets);
            LongFormElementInput programName = Included(
                3,
                LongFormElementKind.ProgramName,
                "Hub Radio",
                Range(3, 1),
                [EditorialOrigin(3)],
                assets: []);
            LongFormElementInput episodeNumber = Included(
                4,
                LongFormElementKind.EpisodeNumber,
                "42",
                Range(4, 1),
                [EditorialOrigin(4)],
                assets: []);
            LongFormElementInput episodeTitle = Included(
                5,
                LongFormElementKind.EpisodeTitle,
                project.ContentName,
                Range(5, 1),
                [EditorialOrigin(5)],
                assets: []);
            LongFormElementInput performer = Included(
                6,
                LongFormElementKind.PerformerIntroduction,
                text: null,
                Range(6, 1),
                [EditorialOrigin(6)],
                assets: []);
            LongFormElementInput publication = Included(
                7,
                LongFormElementKind.PublicationScopeIndicator,
                text: null,
                Range(7, 1),
                [EditorialOrigin(7)],
                assets: []);
            LongFormElementInput bodyElement = Included(
                8,
                LongFormElementKind.Body,
                text: null,
                Range(8, 8),
                [
                    EditorialOrigin(8),
                    OriginRef.FromSourceSpan(sourceSpans[1].Id),
                    OriginRef.FromSourceSpan(sourceSpans[2].Id),
                    OriginRef.FromAsset(body.Id),
                ],
                [AssetReference(LongFormAssetRole.BodyMedia, body)]);
            LongFormTopicInput topicOne = new(
                Included(
                    21,
                    LongFormElementKind.Topic,
                    topicOneMetadata.Metadata.Title,
                    Range(8, 4),
                    [EditorialOrigin(21), OriginRef.FromSourceSpan(sourceSpans[1].Id)],
                    assets: []),
                topicOneRange,
                topicOneMetadata);
            LongFormTopicInput topicTwo = new(
                Included(
                    22,
                    LongFormElementKind.Topic,
                    topicTwoMetadata.Metadata.Title,
                    Range(12, 4),
                    [EditorialOrigin(22), OriginRef.FromSourceSpan(sourceSpans[2].Id)],
                    assets: []),
                topicTwoRange,
                topicTwoMetadata);
            LongFormElementInput summary = Included(
                9,
                LongFormElementKind.Summary,
                "Episode summary",
                Range(16, 2),
                [EditorialOrigin(9)],
                assets: []);
            LongFormCallToActionInput[] callToActions =
            [
                new(
                    LongFormDestination.InternalDistribution,
                    Included(
                        13,
                        LongFormElementKind.CallToAction,
                        "Join the internal community",
                        Range(18, 2),
                        [EditorialOrigin(13)],
                        assets: [])),
                new(
                    LongFormDestination.YouTube,
                    Included(
                        10,
                        LongFormElementKind.CallToAction,
                        "Listen to the next episode",
                        Range(18, 2),
                        [EditorialOrigin(10)],
                        assets: [])),
            ];
            LongFormElementInput endingElement = Included(
                11,
                LongFormElementKind.Ending,
                text: null,
                Range(20, 1),
                [EditorialOrigin(11), OriginRef.FromAsset(ending.Id)],
                [AssetReference(LongFormAssetRole.EndingMedia, ending)]);
            LongFormElementInput postRoll = Included(
                12,
                LongFormElementKind.PostRollMargin,
                text: null,
                Range(21, 1),
                [EditorialOrigin(12)],
                assets: []);

            return new LongFormCompositionRequest(
                Id<LongFormPlanIdTag>(301),
                project,
                provenance,
                template,
                profile,
                LongFormDestination.YouTube,
                assignments,
                preRoll,
                openingElement,
                programName,
                episodeNumber,
                episodeTitle,
                performer,
                publication,
                bodyElement,
                [topicOne, topicTwo],
                summary,
                callToActions,
                endingElement,
                postRoll);
        }

        private static LongFormElementInput Included(
            int id,
            LongFormElementKind kind,
            string? text,
            TimeRange? range,
            IReadOnlyList<OriginRef> origins,
            IReadOnlyList<LongFormAssetReference> assets) =>
            new(
                Id<LongFormElementIdTag>(id),
                kind,
                enabled: true,
                text,
                range,
                origins,
                assets);

        private static LongFormAssetReference AssetReference(
            LongFormAssetRole role,
            Asset asset) =>
            new(role, asset.Id, asset.ContentDigest);

        private static OriginRef EditorialOrigin(int value) =>
            OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(value));

        private static IReadOnlyList<EntityId<EditorialRecordIdTag>>
            CreateEditorialRecordCatalog() =>
            [
                Id<EditorialRecordIdTag>(1),
                Id<EditorialRecordIdTag>(2),
                Id<EditorialRecordIdTag>(3),
                Id<EditorialRecordIdTag>(4),
                Id<EditorialRecordIdTag>(5),
                Id<EditorialRecordIdTag>(6),
                Id<EditorialRecordIdTag>(7),
                Id<EditorialRecordIdTag>(8),
                Id<EditorialRecordIdTag>(9),
                Id<EditorialRecordIdTag>(10),
                Id<EditorialRecordIdTag>(11),
                Id<EditorialRecordIdTag>(12),
                Id<EditorialRecordIdTag>(13),
                Id<EditorialRecordIdTag>(21),
                Id<EditorialRecordIdTag>(22),
            ];

        private static Asset CreateAsset(int id, string name) =>
            new(
                Id<AssetIdTag>(id),
                name,
                name.EndsWith(".png", StringComparison.Ordinal)
                    ? "image/png"
                    : name.EndsWith(".svg", StringComparison.Ordinal)
                        ? "image/svg+xml"
                        : name.EndsWith(".wav", StringComparison.Ordinal)
                            ? "audio/wav"
                            : "video/mp4",
                Digest($"q001.synthetic.asset.{id}.v1"),
                2048 + id,
                new RightsRecord(
                    RightsStatus.Approved,
                    $"q001.synthetic.rights.{id}.v1",
                    "Deterministic acceptance fixture only.",
                    externalPublicationAllowed: true));

        private static (Utterance Utterance, SourceSpan SourceSpan) CreateUtterance(
            int ordinal,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            string text,
            TimeRange range,
            Asset body,
            EntityId<TranscriptRevisionIdTag> revisionId)
        {
            var token = new Token(
                Id<TranscriptTokenIdTag>(ordinal * 10 + 1),
                text,
                range,
                recognitionConfidence: 1d);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(ordinal * 10 + 2),
                text,
                [token]);
            EntityId<SourceSpanIdTag> spanId = Id<SourceSpanIdTag>(ordinal * 10 + 3);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(ordinal * 10 + 4),
                spanId,
                speakerId,
                "Synthetic speaker",
                text,
                range,
                recognitionConfidence: 1d,
                [sentence]);
            var span = new SourceSpan(
                spanId,
                body.Id,
                "video:0",
                range,
                body.ContentDigest,
                revisionId,
                [token.Id]);
            return (utterance, span);
        }

        private static string ResolveRepositoryPath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                {
                    string path = Path.Combine(
                        directory.FullName,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
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

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed record AcceptanceEvidence(
        string SchemaVersion,
        string Requirement,
        ScopeEvidence Scope,
        ContractEvidence Contracts,
        LongFormEvidence LongForm,
        DeleteCandidateEvidence DeleteCandidates,
        SubtitleEvidence SubtitleEvidence,
        TraceabilityEvidence Traceability,
        DeliveryEvidence Delivery,
        QualityControlEvidence QualityControl,
        NegativeControlsEvidence NegativeControls,
        HumanChecklistEvidence HumanChecklist,
        AutomatedCoverageEvidence AutomatedCoverage,
        bool AutomatedPassed,
        string HumanDecision,
        bool Q001Complete);

    private sealed record ScopeEvidence(
        string FixtureKind,
        bool ActualMediaUsed,
        bool ActualAsrExecuted,
        bool ActualModelInferenceUsed,
        bool ActualExportPolicyAuthorized,
        bool ActualFinalRenderExecuted,
        bool ActualFfmpegExecuted,
        bool ActualMediaQcExecuted,
        bool ActualThumbnailGenerated,
        bool SyntheticApprovalFixtureUsed,
        bool SyntheticDecisionRecordUsed,
        bool SyntheticDownstreamFixtureUsed,
        bool HumanCandidateDispositionClaimed,
        bool HumanReleaseApprovalClaimed,
        bool HumanAcceptanceClaimed);

    private sealed record ContractEvidence(
        bool DeleteRangeCandidatesAvailable,
        bool ContextDeviationSuggestionDecisionRecorded,
        bool LongFormOpeningBodyEndingIncluded,
        bool PlanMappedToComposition,
        bool SubtitleCorrectionReflected,
        bool TopicHeadingsConfigured,
        bool SourceTraceabilityClosed,
        bool SyntheticDeliveryBindingsValidated);

    private sealed record LongFormEvidence(
        int ElementCount,
        int TopicElementCount,
        bool TimingComplete,
        bool ExternalRightsReady,
        bool ExternalPublicationReady,
        int WarningCount);

    private sealed record DeleteCandidateEvidence(
        int CandidateCount,
        int LeadingCandidateCount,
        string RepresentativePosition,
        string RuleVersion,
        string Rule,
        bool CandidateProposed,
        bool CandidateRejected,
        string DecisionKind,
        string StatusAfterDecision,
        bool DecisionSourceDigestMatches,
        bool DecisionActorBound);

    private sealed record SubtitleEvidence(
        int ChangedTokenCount,
        int SegmentationCandidateCount,
        bool SourceRevisionDigestMatches,
        bool RevisedRevisionDigestMatches,
        bool RevisedCandidateBoundToCorrection,
        bool RevisedCandidateTextDigestValid,
        bool RevisedCandidateSourceRevisionDigestMatches,
        bool WebVttDigestValid,
        long WebVttByteSize,
        int WebVttCueCount);

    private sealed record TraceabilityEvidence(
        int OutputProbeCount,
        int ResolvedOutputProbeCount,
        int ReverseRoundTripCount,
        int UsedSourceSpanCount,
        int UnresolvedOriginCount,
        int UnresolvedOutputTimeCount);

    private sealed record DeliveryEvidence(
        bool SyntheticDeliveryBindingsValidated,
        int ArtifactCount,
        bool ApprovalEvidenceDigestValid,
        bool DeliveryEvidenceDigestValid,
        string ApprovalEvidenceLevel,
        bool HumanApprovalClaimed,
        bool DigestStabilityValidated);

    private sealed record QualityControlEvidence(
        string EvidenceLevel,
        bool StaticEvidenceValidated,
        bool ActualMediaQcExecuted,
        bool SyntheticDownstreamFixtureUsed,
        bool ActualNumericMeasurementsDisclosed);

    private sealed record NegativeControlsEvidence(
        NegativeControlEvidence BrokenProvenance,
        NegativeControlEvidence StaleApproval,
        NegativeControlEvidence UnconfirmedRights);

    private sealed record NegativeControlEvidence(
        string Scenario,
        string Boundary,
        string ExpectedDeliveryFailure,
        int DependencySideEffectCount,
        bool Detected);

    private sealed record HumanChecklistEvidence(
        int RequiredCount,
        int CompletedCount,
        IReadOnlyList<HumanChecklistItem> Items,
        string Decision);

    private sealed record AutomatedCoverageEvidence(
        bool EnumeratedAutomatedContractsOnly,
        IReadOnlyList<string> KnownLimitations);

    private sealed record HumanChecklistItem(
        string Code,
        string Status);
}