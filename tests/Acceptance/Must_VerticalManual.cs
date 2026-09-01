using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HubRadio.App.Delivery;
using HubRadio.App.Reviews;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class Must_VerticalManual
{
    private const string ResultPathEnvironmentVariable =
        "HUBRADIO_Q000_RESULT_PATH";
    private const string SyntheticActorSid =
        "S-1-5-21-1000-2000-3000-5001";
    private const string SyntheticCaptionText =
        "synthetic vertical acceptance caption";
    private const string SyntheticSourceFileName =
        "synthetic-q000-source.mov";
    private const string SyntheticOutputStorageReference =
        "projects/q000/renders/final.mp4";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [TestMethod]
    public async Task AutomatableContractsPassWhileHumanAcceptanceRemainsPending()
    {
        using var fixture = new AcceptanceFixture();

        AcceptanceEvidence result = await fixture.RunAsync();

        Assert.AreEqual("1.0.0", result.SchemaVersion);
        Assert.AreEqual("Q-000", result.Requirement);
        Assert.IsTrue(result.AutomatedPassed);
        Assert.AreEqual("PENDING", result.HumanDecision);
        Assert.IsFalse(result.Q000Complete);
        Assert.IsFalse(result.Scope.ActualMediaUsed);
        Assert.IsFalse(result.Scope.ActualFfmpegExecuted);
        Assert.IsFalse(result.Scope.ActualMediaQcExecuted);
        Assert.IsFalse(result.Scope.ActualFaceDetectionExecuted);
        Assert.IsFalse(result.Scope.PlatformSpecificSafeZoneCertified);
        Assert.IsFalse(result.Scope.HumanSelectedInterval);
        Assert.IsFalse(result.Scope.HumanApprovalClaimed);
        Assert.AreEqual(1080, result.Layout.OutputPixelWidth);
        Assert.AreEqual(1920, result.Layout.OutputPixelHeight);
        Assert.AreEqual(9, result.Layout.OutputAspectWidth);
        Assert.AreEqual(16, result.Layout.OutputAspectHeight);
        Assert.IsTrue(result.Layout.GenericFullFrameSafeZoneOnly);
        Assert.IsTrue(result.Layout.SubtitleContainedByGenericSafeZone);
        Assert.AreEqual(3, result.Keyframes.Samples.Count);
        Assert.IsTrue(result.Keyframes.CanonicalNormalizedRoundTripValidated);
        Assert.IsFalse(result.Keyframes.BoundToCompiler);
        Assert.IsFalse(result.Keyframes.RenderedEvidenceClaimed);
        Assert.AreEqual(0, result.Subtitles.FaceRegionInputCount);
        Assert.IsFalse(result.Subtitles.ActualFaceAbsenceClaimed);
        Assert.IsFalse(result.Subtitles.ActualFaceAvoidanceClaimed);
        Assert.IsTrue(result.Subtitles.SafeZoneConstraintSatisfied);
        Assert.AreEqual(1, result.Subtitles.AssCueCount);
        Assert.IsTrue(result.Render.GraphValid);
        Assert.IsTrue(result.Render.CompilerContractValidated);
        Assert.IsFalse(result.Render.ProcessStarted);
        Assert.AreEqual("environment-gated", result.Render.FfmpegGate.ExecutionStatus);
        Assert.IsFalse(result.Render.FfmpegGate.LegalApprovalGranted);
        Assert.IsFalse(result.Render.FfmpegGate.ReleaseApprovalGranted);
        Assert.IsTrue(result.QualityControl.StaticEvidenceValidated);
        Assert.IsFalse(result.QualityControl.ActualMediaQcExecuted);
        Assert.IsTrue(result.QualityControl.SyntheticDownstreamFixtureUsed);
        Assert.IsTrue(result.QualityControl.HumanReviewRequired);
        Assert.IsFalse(result.QualityControl.CanProceedToDelivery);
        Assert.IsTrue(result.Delivery.SyntheticApprovalFixtureUsed);
        Assert.IsFalse(result.Delivery.HumanApprovalClaimed);
        Assert.IsFalse(result.Delivery.ReleaseApprovalClaimed);
        Assert.IsTrue(result.Delivery.DigestBindingsValidated);
        Assert.IsTrue(result.NegativeControls.BrokenProvenance.Detected);
        Assert.IsTrue(result.NegativeControls.StaleApproval.Detected);
        Assert.AreEqual(0, result.NegativeControls.BrokenProvenance.DependencySideEffectCount);
        Assert.AreEqual(0, result.NegativeControls.StaleApproval.DependencySideEffectCount);
        Assert.AreEqual(0, result.HumanChecklist.CompletedCount);
        Assert.AreEqual(result.HumanChecklist.RequiredCount, result.HumanChecklist.Items.Count);
        Assert.IsTrue(result.HumanChecklist.Items.All(static item =>
            string.Equals(item.Status, "PENDING", StringComparison.Ordinal)));

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
            SyntheticCaptionText,
            SyntheticSourceFileName,
            SyntheticOutputStorageReference,
        ];
        bool exposesForbiddenValue = forbiddenValues.Any(value =>
            canonical.Contains(value, StringComparison.Ordinal));
        bool exposesAbsolutePath = Regex.IsMatch(
            canonical,
            "(?:[A-Za-z]:[\\\\/]|/mnt/[A-Za-z]/)",
            RegexOptions.CultureInvariant);
        bool exposesUncPath = canonical.Contains(
            @"\\\\",
            StringComparison.Ordinal);
        bool exposesSid = Regex.IsMatch(
            canonical,
            "S-[0-9]+(?:-[0-9]+){2,}",
            RegexOptions.CultureInvariant);
        if (exposesForbiddenValue
            || exposesAbsolutePath
            || exposesUncPath
            || exposesSid)
        {
            Assert.Fail("The Q-000 acceptance result contains private evidence.");
        }

        string fullPath = Path.GetFullPath(requestedPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Fail("The Q-000 acceptance result path has no parent directory.");
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
            string.Concat("71000000-0000-0000-0000-", suffix));
    }

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(value));

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(Time(start, rate), Time(duration, rate));

    private static RationalNumber Rational(long numerator, long denominator = 1) =>
        new(numerator, denominator);

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator) =>
        new(
            Rational(xNumerator, xDenominator),
            Rational(yNumerator, yDenominator),
            Rational(widthNumerator, widthDenominator),
            Rational(heightNumerator, heightDenominator));

    private static NormalizedRect FullRect() =>
        Rect(0, 1, 0, 1, 1, 1, 1, 1);

    private static AssSubtitleProfile CreateAssProfile() =>
        new(1080, 1920, Enum.GetValues<SubtitleFileCueStyle>()
            .Select(CreateAssStyle)
            .ToArray());

    private static AssStyleDefinition CreateAssStyle(
        SubtitleFileCueStyle style) =>
        new(
            style,
            "Synthetic Sans",
            fontSize: 54,
            new AssColor(255, 255, 255, 255),
            new AssColor(255, 255, 255, 255),
            new AssColor(255, 0, 0, 0),
            new AssColor(128, 0, 0, 0),
            bold: style is SubtitleFileCueStyle.Emphasis
                or SubtitleFileCueStyle.Caution,
            italic: style == SubtitleFileCueStyle.Quote,
            underline: false,
            strikeOut: false,
            scaleX: 100,
            scaleY: 100,
            spacing: 0,
            angle: 0,
            AssBorderStyle.OutlineAndShadow,
            outlineWidth: 2,
            shadowDepth: 0,
            AssAlignment.BottomCenter,
            new AssMargins(64, 64, 96),
            encoding: 1);

    private static PixelRectEvidence Snapshot(PixelRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private static bool HasAdjacentArguments(
        IReadOnlyList<string> arguments,
        string name,
        string value)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal)
                && string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class AcceptanceFixture : IDisposable
    {
        private static readonly DateTimeOffset Baseline = new(
            2050,
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
                "HubRadio.Q000.Acceptance",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
        }

        internal async Task<AcceptanceEvidence> RunAsync()
        {
            TimeRange selectedRange = Range(0, 2);
            var asset = new Asset(
                Id<AssetIdTag>(1),
                SyntheticSourceFileName,
                "video/quicktime",
                Digest("q000.synthetic.source.v1"),
                4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "q000.synthetic.rights.v1",
                    "Synthetic acceptance fixture only.",
                    externalPublicationAllowed: true));
            var project = new Project(
                Id<ProjectIdTag>(2),
                "Q-000 synthetic vertical fixture",
                "Automatable contract evidence only.",
                "Automated acceptance only",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                [asset],
                performers: []);

            var token = new Token(
                Id<TranscriptTokenIdTag>(3),
                SyntheticCaptionText,
                selectedRange,
                recognitionConfidence: null);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(4),
                SyntheticCaptionText,
                [token]);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(5),
                Id<SourceSpanIdTag>(6),
                Id<TranscriptSpeakerIdTag>(7),
                "Synthetic speaker",
                SyntheticCaptionText,
                selectedRange,
                recognitionConfidence: null,
                [sentence]);
            var transcriptRevision = new TranscriptRevision(
                Id<TranscriptRevisionIdTag>(8),
                Id<TranscriptRevisionIdTag>(9),
                TranscriptChangeSource.Human,
                Baseline,
                [utterance]);
            ContentDigest transcriptRevisionDigest = ContentDigest.Compute(
                StrictUtf8.GetBytes(
                    TranscriptSnapshotJson.WriteRevision(transcriptRevision)));
            var sourceSpan = new SourceSpan(
                utterance.SourceSpanId,
                asset.Id,
                "video:0",
                selectedRange,
                asset.ContentDigest,
                transcriptRevision.Id,
                [token.Id]);
            SubtitleSegmentationResult segmentation = SubtitleSegmenter.Segment(
                transcriptRevision,
                transcriptRevisionDigest,
                [sourceSpan]);
            SubtitleCueCandidate subtitleCandidate =
                segmentation.Candidates.Single();

            OriginRef sourceOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
            var element = new CompositionElement(
                Id<CompositionElementIdTag>(10),
                selectedRange,
                sourceOrigin,
                adoptedSuggestion: null,
                sourceRange: selectedRange);
            var composition = new CompositionVersion(
                Id<CompositionVersionIdTag>(11),
                project.Id,
                parentVersionId: null,
                SyntheticActorSid,
                Baseline.AddMinutes(1),
                "Create the deterministic Q-000 stand-in composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            var timelineMap = new TimelineMap(
                Id<TimelineMapIdTag>(12),
                composition.Id,
                [new TimelineMapEntry(selectedRange, sourceOrigin, selectedRange)]);
            ContentDigest timelineMapDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timelineMap);

            var envelope = new LayoutSpec(
                Id<LayoutSpecIdTag>(13),
                9,
                16,
                [new LayoutRegion("subtitle", FullRect(), LayoutRegionRole.Subtitle)],
                [new SafeZone("output-frame-bounds", FullRect())]);
            var manualSource = new ManualLayoutSource(
                asset.Id,
                sourceOrigin,
                new SourceDisplayGeometry(1920, 1080),
                new ManualSourceCrop(Rect(0, 1, 0, 1, 9, 32, 8, 9)),
                new ManualSubjectRegion(Rect(0, 1, 0, 1, 9, 32, 8, 9)));
            var manualDestination = new ManualDestinationGeometry(FullRect());
            ManualLayoutResolution manualResolution = ManualLayoutResolver.Resolve(
                ManualLayoutRequest.SingleSubjectCrop(
                    Id<LayoutSpecIdTag>(14),
                    envelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient,
                    manualSource,
                    manualDestination));
            ManualLayoutPlan manualPlan = manualResolution.Plan
                ?? throw new InvalidOperationException(
                    "The static manual layout did not resolve.");
            ManualLayoutResolution keyframeSignal = ManualLayoutResolver.Resolve(
                ManualLayoutRequest.SingleSubjectCrop(
                    Id<LayoutSpecIdTag>(15),
                    envelope,
                    ManualStaticCropDecision.ManualKeyframesRequired,
                    manualSource,
                    manualDestination));
            ResolvedLayout resolvedLayout = LayoutResolver.Resolve(
                manualPlan.DerivedLayoutSpec,
                1080,
                1920);
            ResolvedLayoutRegion resolvedVideoRegion = resolvedLayout.Regions.Single(
                static region => string.Equals(
                    region.Name,
                    "manual-single-subject",
                    StringComparison.Ordinal));
            ResolvedSafeZone resolvedSafeZone = resolvedLayout.SafeZones.Single();
            Assert.AreEqual(ManualLayoutResolutionStatus.Resolved, manualResolution.Status);
            Assert.AreEqual(
                ManualLayoutResolutionStatus.ManualKeyframesRequired,
                keyframeSignal.Status);
            Assert.IsNull(keyframeSignal.Plan);
            Assert.AreEqual(new PixelRectEvidence(0, 0, 1080, 1920),
                Snapshot(resolvedVideoRegion.Rect));
            Assert.AreEqual(new PixelRectEvidence(0, 0, 1080, 1920),
                Snapshot(resolvedSafeZone.Rect));

            var keyframeTrack = new KeyframeTrack(
                asset.Id,
                ManualDestinationSlot.SingleSubject,
                new SourceDisplayGeometry(1920, 1080),
                1080,
                1920,
                [new LayoutKeyframeSegment(
                    selectedRange,
                    [
                        new LayoutKeyframe(
                            Time(0),
                            new LayoutKeyframeValue(
                                Rect(0, 1, 0, 1, 9, 32, 8, 9),
                                Rect(0, 1, 0, 1, 1, 2, 1, 2)),
                            LayoutKeyframeInterpolation.Linear),
                        new LayoutKeyframe(
                            Time(2),
                            new LayoutKeyframeValue(
                                Rect(1, 2, 1, 2, 9, 64, 4, 9),
                                Rect(3, 4, 3, 4, 1, 4, 1, 4)),
                            LayoutKeyframeInterpolation.Hold),
                    ])]);
            LayoutKeyframeEvaluation[] keyframeEvaluations =
            [
                keyframeTrack.Evaluate(Time(0)),
                keyframeTrack.Evaluate(Time(1)),
                keyframeTrack.Evaluate(Time(2)),
            ];
            PixelRectEvidence[] expectedSourceCrops =
            [
                new(0, 0, 540, 960),
                new(480, 270, 405, 720),
                new(960, 540, 270, 480),
            ];
            PixelRectEvidence[] expectedDestinations =
            [
                new(0, 0, 540, 960),
                new(405, 720, 405, 720),
                new(810, 1440, 270, 480),
            ];
            NormalizedRect[] expectedNormalizedSourceCrops =
            [
                Rect(0, 1, 0, 1, 9, 32, 8, 9),
                Rect(1, 4, 1, 4, 27, 128, 2, 3),
                Rect(1, 2, 1, 2, 9, 64, 4, 9),
            ];
            NormalizedRect[] expectedNormalizedDestinations =
            [
                Rect(0, 1, 0, 1, 1, 2, 1, 2),
                Rect(3, 8, 3, 8, 3, 8, 3, 8),
                Rect(3, 4, 3, 4, 1, 4, 1, 4),
            ];
            for (int index = 0; index < keyframeEvaluations.Length; index++)
            {
                Assert.AreEqual(
                    expectedSourceCrops[index],
                    Snapshot(keyframeEvaluations[index].SourceCropPixels));
                Assert.AreEqual(
                    expectedDestinations[index],
                    Snapshot(keyframeEvaluations[index].DestinationPixels));
                Assert.AreEqual(
                    expectedNormalizedSourceCrops[index],
                    keyframeEvaluations[index].Value.SourceCrop);
                Assert.AreEqual(
                    expectedNormalizedDestinations[index],
                    keyframeEvaluations[index].Value.Destination);
            }

            ContentDigest subtitleLayoutProfileDigest =
                Digest("q000.synthetic.subtitle-layout-profile.v1");
            var subtitleLayoutProfile = new SubtitleLayoutProfile(
                manualPlan.DerivedLayoutSpec.Id,
                subtitleLayoutProfileDigest,
                ["output-frame-bounds"],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(static kind => new SubtitleSoftConstraintWeight(
                        kind,
                        weight: 100))
                    .ToArray());
            var subtitlePlacement = new SubtitlePlacementCandidate(
                Id<SubtitlePlacementCandidateIdTag>(16),
                "subtitle",
                new SubtitleLayoutRect(
                    Rational(1, 10),
                    Rational(4, 5),
                    Rational(4, 5),
                    Rational(1, 10)));
            var subtitleLayoutRequest = new SubtitleLayoutRequest(
                subtitleCandidate,
                manualPlan.DerivedLayoutSpec,
                subtitleLayoutProfile,
                [subtitlePlacement],
                prohibitedRegions: [],
                faceRegions: [],
                previousPlacement: null);
            SubtitleLayoutResolution subtitleLayoutResolution =
                SubtitleLayoutEngine.Resolve(subtitleLayoutRequest);
            Assert.AreEqual(
                SubtitleLayoutResolutionStatus.Resolved,
                subtitleLayoutResolution.Status);
            Assert.IsFalse(subtitleLayoutResolution.Evaluations.Single()
                .HardConstraints.Contains(
                    SubtitleHardConstraintKind.OutsideRequiredSafeZone));
            Assert.IsFalse(subtitleLayoutResolution.Evaluations.Single()
                .SoftConstraints.Contains(
                    SubtitleSoftConstraintKind.FaceRegionOverlap));

            SubtitleCueHistory subtitleHistory = SubtitleService.CreateInitial(
                composition,
                subtitleCandidate,
                subtitleLayoutResolution,
                Id<CueIdTag>(17),
                Id<SubtitleCueRevisionIdTag>(18),
                selectedRange,
                SyntheticActorSid,
                Baseline.AddMinutes(2),
                "Create the synthetic Q-000 subtitle cue.");
            SubtitleCueSnapshot subtitleSnapshot =
                subtitleHistory.CurrentRevision.Snapshot;
            ContentDigest displayProfileDigest =
                Digest("q000.synthetic.subtitle-display-profile.v1");
            SubtitleDisplaySourceCue displaySource =
                SubtitleDisplayMapper.MapSource(subtitleSnapshot);
            SubtitleDisplayResult displayResult = SubtitleDisplayPolicy.Apply(
                new SubtitleDisplayProfile(
                    displayProfileDigest,
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [displaySource],
                selectedCueIds: []);
            SubtitleFileCue subtitleFileCue = SubtitleDisplayMapper.MapForExport(
                subtitleSnapshot,
                displayResult.DisplayedCues.Single());
            SubtitleExportFile assFile = AssSubtitleWriter.Write(
                [subtitleFileCue],
                CreateAssProfile());

            var template = new TemplateReference(
                Id<TemplateIdTag>(19),
                "Q-000 synthetic template",
                Digest("q000.synthetic.template.v1"));
            ContentDigest layoutDigest = Digest("q000.synthetic.layout.v1");
            var profile = new RenderProfile(
                Id<RenderProfileIdTag>(20),
                "q000-synthetic-portrait",
                Digest("q000.synthetic.render-profile.v1"),
                1080,
                1920,
                30,
                1,
                manualPlan.DerivedLayoutSpec.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: true);
            var videoInput = new RenderGraphInput(
                Id<RenderInputIdTag>(21),
                RenderInputKind.Video,
                asset.ContentDigest,
                asset.ByteSize,
                sourceOrigin);
            var subtitleInput = new RenderGraphInput(
                Id<RenderInputIdTag>(22),
                RenderInputKind.Subtitle,
                assFile.ContentDigest,
                assFile.ByteSize,
                subtitleCandidate.Origin);
            RenderNode videoNode = RenderNode.Video(
                Id<RenderNodeIdTag>(23),
                videoInput.Id,
                selectedRange,
                sourceOrigin,
                selectedRange,
                manualPlan.SourcePlacements.Single().Destination.Region.Name);
            RenderNode subtitleNode = RenderNode.Subtitle(
                Id<RenderNodeIdTag>(24),
                subtitleInput.Id,
                videoNode.Id,
                subtitleSnapshot.Cue,
                subtitleCandidate.Origin);
            RenderNode outputNode = RenderNode.Output(
                Id<RenderNodeIdTag>(25),
                [subtitleNode.Id],
                selectedRange);
            var font = new RenderFontEvidence(
                "Synthetic Sans",
                Digest("q000.synthetic.font.v1"));
            var graph = new RenderGraph(
                Id<RenderGraphIdTag>(26),
                composition.Id,
                composition.ContentDigest,
                timelineMap.Id,
                timelineMapDigest,
                profile.Id,
                profile.ProfileDigest,
                manualPlan.DerivedLayoutSpec.Id,
                layoutDigest,
                template,
                [videoInput, subtitleInput],
                [font],
                [videoNode, subtitleNode, outputNode],
                outputNode.Id);
            var validationContext = new RenderGraphValidationContext(
                project,
                composition,
                timelineMap,
                timelineMapDigest,
                profile,
                manualPlan.DerivedLayoutSpec,
                layoutDigest,
                template,
                [sourceSpan, subtitleCandidate.SourceSpan],
                editorialRecordIds: [],
                [
                    new RenderArtifactEvidence(
                        videoInput.Id,
                        videoInput.ContentDigest,
                        videoInput.ByteSize),
                    new RenderArtifactEvidence(
                        subtitleInput.Id,
                        subtitleInput.ContentDigest,
                        subtitleInput.ByteSize),
                ],
                [font]);
            RenderGraphValidationResult graphValidation =
                RenderGraphValidator.Validate(graph, validationContext);
            Assert.IsTrue(graphValidation.IsValid);

            var videoManifestArtifact = new ManifestArtifact(
                "video",
                "video/mp4",
                "ja",
                1024,
                Digest("q000.synthetic.render-output.v1"),
                SyntheticOutputStorageReference);
            var renderManifest = new RenderManifest(
                Id<RenderManifestIdTag>(27),
                composition,
                timelineMap,
                timelineMapDigest,
                profile,
                Digest("q000.synthetic.render-configuration.v1"),
                "1.0.0-q000-synthetic",
                Digest("q000.synthetic.application.v1"),
                [
                    new RenderInputEvidence("source", videoInput.ContentDigest),
                    new RenderInputEvidence("subtitle", subtitleInput.ContentDigest),
                ],
                [new RenderToolEvidence(
                    "ffmpeg",
                    DecodeValidator.CanonicalPackageVersion,
                    "contract-only-no-execution",
                    new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                    containerImageDigest: null)],
                models: [],
                [font],
                [videoManifestArtifact]);
            var subtitleQcEvidence = new OutputQcSubtitleEvidence(
                subtitleLayoutProfileDigest,
                displayProfileDigest,
                displayResult,
                [new OutputQcSubtitleLayoutEvidence(
                    subtitleSnapshot.Cue.Id,
                    subtitleLayoutResolution)]);
            EntityId<ArtifactIdTag> outputArtifactId = Id<ArtifactIdTag>(28);
            var qcRequest = new OutputQcRequest(
                outputArtifactId,
                renderManifest,
                graph,
                validationContext,
                subtitleQcEvidence,
                TimeSpan.FromMinutes(1));
            OutputQc.ValidateEvidence(qcRequest, CancellationToken.None);

            int verifierCallCount = 0;
            Task<BinaryVerificationResult> VerifyContractAsync(
                BinaryVerificationRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                verifierCallCount++;
                return Task.FromResult(BinaryVerificationResult.Verified(
                    request,
                    request.ExpectedByteSize,
                    request.ExpectedDigest));
            }

            var toolRequest = new BinaryVerificationRequest(
                "ffmpeg",
                Path.Combine(_rootPath, "ffmpeg.exe"),
                new ContentDigest(FfmpegCompiler.FixedFfmpegSha256),
                FfmpegCompiler.FixedFfmpegByteSize);
            FfmpegInputBinding[] inputBindings =
            [
                new(
                    videoInput.Id,
                    new BinaryVerificationRequest(
                        "q000-source",
                        Path.Combine(_rootPath, "source.mov"),
                        videoInput.ContentDigest,
                        videoInput.ByteSize)),
                new(
                    subtitleInput.Id,
                    new BinaryVerificationRequest(
                        "q000-subtitle",
                        Path.Combine(_rootPath, "subtitle.ass"),
                        subtitleInput.ContentDigest,
                        subtitleInput.ByteSize)),
            ];
            var compileRequest = new FfmpegCompileRequest(
                graph,
                validationContext,
                new FfmpegToolBinding(toolRequest),
                inputBindings,
                Path.Combine(_rootPath, "output.partial.mp4"),
                _rootPath,
                TimeSpan.FromMinutes(1));
            FfmpegCompilation compilation = await FfmpegCompiler.CompileAsync(
                compileRequest,
                VerifyContractAsync);
            IReadOnlyList<string> arguments =
                compilation.ProcessRequest.ArgumentList;
            bool tokenizedArguments =
                compilation.ProcessRequest.StandardInput is null
                && arguments.All(static argument =>
                    !argument.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
                    && !argument.Contains(
                        "powershell",
                        StringComparison.OrdinalIgnoreCase));
            bool metadataStripped = HasAdjacentArguments(
                arguments,
                "-map_metadata",
                "-1");
            bool nonOverwrite = arguments.Contains("-n", StringComparer.Ordinal)
                && !arguments.Contains("-y", StringComparer.Ordinal);
            bool subtitleFilterCompiled = compilation.FilterScript.Contains(
                "subtitles=filename=",
                StringComparison.Ordinal);
            Assert.AreEqual(graph.Inputs.Count + 1, verifierCallCount);
            Assert.IsTrue(tokenizedArguments);
            Assert.IsTrue(metadataStripped);
            Assert.IsTrue(nonOverwrite);
            Assert.IsTrue(subtitleFilterCompiled);

            var storedArtifact = new StoredArtifact(
                outputArtifactId,
                project.Id,
                ArtifactKind.Render,
                videoManifestArtifact.StorageReference,
                videoManifestArtifact.Sha256,
                videoManifestArtifact.ByteSize,
                Baseline.AddMinutes(3));
            var timing = new OutputQcStreamTiming(
                streamIndex: 0,
                [new OutputQcTimingEntry(
                    sequence: 0,
                    presentationTimestampTicks: 0,
                    durationTicks: 2)],
                Time(1),
                presentationStart: Time(0),
                presentationEnd: Time(2),
                Digest("q000.synthetic.timing.v1"));
            var syntheticQualityControl = new OutputQcResult(
                storedArtifact,
                qcRequest,
                Digest("q000.synthetic.probe.v1"),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                timing,
                audioTiming: null,
                subtitleTiming: null,
                new OutputQcDecodeEvidence(TimeSpan.Zero),
                new OutputQcLumaMeasurements(
                    [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                    Digest("q000.synthetic.luma.v1")),
                audioMeasurements: null);
            var subtitleVariant = new SubtitleDeliveryVariantEvidence(
                SubtitleDeliveryVariantKind.BurnedIn,
                graph.Id,
                displayProfileDigest,
                assFile.ContentDigest,
                sidecarArtifact: null);
            var syntheticApproval = new StoredApproval(
                Id<ApprovalIdTag>(29),
                project.Id,
                composition.Id,
                composition.ContentDigest,
                SyntheticActorSid,
                ReviewActorRole.ContentApprover,
                Baseline.AddMinutes(4),
                "Synthetic contract fixture; not a human decision.");
            var registeredArtifacts = new Dictionary<
                EntityId<ArtifactIdTag>,
                StoredArtifact>
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
                    return registeredArtifacts.GetValueOrDefault(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    artifactValidationCount++;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(registeredArtifacts[artifactId]);
                });
            IReadOnlyList<KeyValuePair<
                EntityId<ArtifactIdTag>,
                ManifestArtifact>> artifactEvidence =
            [
                new(outputArtifactId, videoManifestArtifact),
            ];
            DeliveryCreationResult delivery = await deliveryService.CreateAsync(
                Id<DeliveryManifestIdTag>(30),
                project,
                composition,
                renderManifest,
                validationContext,
                syntheticQualityControl,
                [syntheticApproval],
                artifactEvidence,
                subtitleVariant);
            Assert.AreEqual(1, approvalEvaluationCount);
            Assert.AreEqual(1, artifactReadCount);
            Assert.AreEqual(1, artifactValidationCount);
            Assert.AreEqual(
                SubtitleDeliveryVariantKind.BurnedIn,
                delivery.Manifest.SubtitleVariant!.Kind);
            Assert.IsNull(delivery.Manifest.SubtitleVariant.SidecarArtifact);

            var brokenSourceSpan = new SourceSpan(
                sourceSpan.Id,
                sourceSpan.AssetId,
                sourceSpan.StreamId,
                sourceSpan.Range,
                Digest("q000.synthetic.broken-source.v1"),
                sourceSpan.TranscriptRevisionId,
                sourceSpan.TranscriptTokenIds);
            var brokenContext = new RenderGraphValidationContext(
                project,
                composition,
                timelineMap,
                timelineMapDigest,
                profile,
                manualPlan.DerivedLayoutSpec,
                layoutDigest,
                template,
                [brokenSourceSpan, subtitleCandidate.SourceSpan],
                editorialRecordIds: [],
                validationContext.Artifacts,
                validationContext.Fonts);
            RenderGraphValidationResult brokenGraph =
                RenderGraphValidator.Validate(graph, brokenContext);
            bool provenanceIssueDetected = brokenGraph.Issues.Any(static issue =>
                issue.Kind ==
                    RenderGraphValidationIssueKind.OriginDigestMismatch);
            Assert.IsTrue(provenanceIssueDetected);
            int brokenDependencySideEffects = 0;
            var brokenService = CreateSideEffectCountingService(
                registeredArtifacts,
                () => brokenDependencySideEffects++);
            DeliveryException brokenException =
                await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                    brokenService.CreateAsync(
                        Id<DeliveryManifestIdTag>(31),
                        project,
                        composition,
                        renderManifest,
                        brokenContext,
                        syntheticQualityControl,
                        [syntheticApproval],
                        artifactEvidence,
                        subtitleVariant));
            Assert.AreEqual(
                DeliveryFailureReason.ProvenanceInvalid,
                brokenException.Reason);
            Assert.AreEqual(0, brokenDependencySideEffects);

            var staleApproval = new StoredApproval(
                Id<ApprovalIdTag>(32),
                project.Id,
                Id<CompositionVersionIdTag>(33),
                Digest("q000.synthetic.stale-composition.v1"),
                SyntheticActorSid,
                ReviewActorRole.ContentApprover,
                Baseline.AddMinutes(4),
                "Synthetic stale approval negative control.");
            int staleDependencySideEffects = 0;
            var staleService = CreateSideEffectCountingService(
                registeredArtifacts,
                () => staleDependencySideEffects++);
            DeliveryException staleException =
                await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                    staleService.CreateAsync(
                        Id<DeliveryManifestIdTag>(34),
                        project,
                        composition,
                        renderManifest,
                        validationContext,
                        syntheticQualityControl,
                        [staleApproval],
                        artifactEvidence,
                        subtitleVariant));
            Assert.AreEqual(
                DeliveryFailureReason.StaleApproval,
                staleException.Reason);
            Assert.AreEqual(0, staleDependencySideEffects);

            KeyframeSampleEvidence[] keyframeSamples = keyframeEvaluations
                .Select(static evaluation => new KeyframeSampleEvidence(
                    evaluation.RequestedTime.ToString(),
                    Snapshot(evaluation.SourceCropPixels),
                    Snapshot(evaluation.DestinationPixels)))
                .ToArray();
            bool subtitleContainedByGenericSafeZone =
                subtitleLayoutResolution.Status ==
                    SubtitleLayoutResolutionStatus.Resolved
                && subtitleLayoutResolution.Evaluations.Single()
                    .HardConstraints.Count == 0;
            bool deliveryBindingsValidated =
                delivery.Manifest.CompositionVersionId == composition.Id
                && delivery.Manifest.CompositionVersionDigest ==
                    composition.ContentDigest
                && delivery.Manifest.RenderManifestId == renderManifest.Id
                && delivery.Manifest.Artifacts.Count == 1
                && delivery.Manifest.SubtitleVariant == subtitleVariant
                && delivery.UsedSourceSpans.Count == 1
                && delivery.UsedSourceSpans[0].Id == sourceSpan.Id;
            Assert.IsTrue(subtitleContainedByGenericSafeZone);
            Assert.IsTrue(deliveryBindingsValidated);

            HumanChecklistItem[] checklistItems =
            [
                new("human-select-real-interval", "PENDING"),
                new("review-real-manual-crop-and-keyframes", "PENDING"),
                new("review-real-face-presence-and-avoidance", "PENDING"),
                new("review-platform-specific-safe-zones", "PENDING"),
                new("execute-approved-exact-ffmpeg-render", "PENDING"),
                new("run-actual-media-qc-and-visual-review", "PENDING"),
                new("record-human-approval", "PENDING"),
            ];

            return new AcceptanceEvidence(
                "1.0.0",
                "Q-000",
                new ScopeEvidence(
                    "deterministic-rights-safe-synthetic-contract",
                    ActualMediaUsed: false,
                    ActualFfmpegExecuted: false,
                    ActualMediaQcExecuted: false,
                    ActualFaceDetectionExecuted: false,
                    PlatformSpecificSafeZoneCertified: false,
                    HumanSelectedInterval: false,
                    HumanApprovalClaimed: false),
                new IntervalEvidence(
                    "deterministic-stand-in",
                    selectedRange.Start.ToString(),
                    selectedRange.Duration.ToString(),
                    HumanSelected: false,
                    RightsStatus: asset.Rights.Status.ToString(),
                    ExternalPublicationAllowed:
                        asset.Rights.ExternalPublicationAllowed),
                new LayoutEvidence(
                    manualResolution.Mode.ToString(),
                    manualResolution.Status.ToString(),
                    keyframeSignal.Status.ToString(),
                    resolvedLayout.OutputPixelWidth,
                    resolvedLayout.OutputPixelHeight,
                    manualPlan.DerivedLayoutSpec.OutputAspectWidth,
                    manualPlan.DerivedLayoutSpec.OutputAspectHeight,
                    manualPlan.SourcePlacements.Count,
                    GenericFullFrameSafeZoneOnly:
                        resolvedLayout.SafeZones.Count == 1
                        && string.Equals(
                            resolvedSafeZone.Name,
                            "output-frame-bounds",
                            StringComparison.Ordinal)
                        && Snapshot(resolvedSafeZone.Rect) ==
                            new PixelRectEvidence(0, 0, 1080, 1920),
                    subtitleContainedByGenericSafeZone,
                    PlatformSpecificSafeZoneClaimed: false),
                new KeyframeEvidence(
                    "exact-rational-nearest-to-even",
                    keyframeTrack.Segments.Single().Keyframes.Count,
                    keyframeSamples,
                    CanonicalNormalizedRoundTripValidated: true,
                    BoundToCompiler: false,
                    RenderedEvidenceClaimed: false),
                new SubtitleEvidence(
                    "synthetic-human-child-contract",
                    HumanTranscriptWorkClaimed: false,
                    segmentation.Candidates.Count,
                    subtitleLayoutRequest.FaceRegions.Count,
                    ActualFaceAbsenceClaimed: false,
                    ActualFaceAvoidanceClaimed: false,
                    subtitleLayoutResolution.Status.ToString(),
                    SafeZoneConstraintSatisfied:
                        subtitleContainedByGenericSafeZone,
                    displayResult.DisplayedCues.Count,
                    assFile.Format.ToString(),
                    assFile.CueCount,
                    assFile.ByteSize,
                    assFile.ContentDigest.Hex,
                    assFile.TimestampRuleVersion,
                    RawTextExposed: false),
                new RenderEvidence(
                    "compiler-contract-only",
                    graphValidation.IsValid,
                    graphValidation.Issues.Count,
                    CompilerContractValidated:
                        tokenizedArguments
                        && metadataStripped
                        && nonOverwrite
                        && subtitleFilterCompiled,
                    verifierCallCount,
                    graph.Inputs.Count,
                    ProcessRequestGenerated: true,
                    ProcessStarted: false,
                    KeyframesBoundToCompiler: false,
                    tokenizedArguments,
                    metadataStripped,
                    nonOverwrite,
                    new FfmpegGateEvidence(
                        FfmpegCompiler.FixedFfmpegSha256,
                        FfmpegCompiler.FixedFfmpegByteSize,
                        LegalApprovalGranted: false,
                        ReleaseApprovalGranted: false,
                        BinaryDiscoveryAttempted: false,
                        "environment-gated")),
                new QualityControlEvidence(
                    "static-evidence-only",
                    StaticEvidenceValidated: true,
                    ActualMediaQcExecuted: false,
                    SyntheticDownstreamFixtureUsed: true,
                    syntheticQualityControl.HumanReviewRequired,
                    syntheticQualityControl.ApprovedNumericThresholdsApplied,
                    syntheticQualityControl.CanProceedToDelivery,
                    syntheticQualityControl.RequiredHumanReviewAreas.Count),
                new DeliveryEvidence(
                    "synthetic-contract-only",
                    SyntheticApprovalFixtureUsed: true,
                    HumanApprovalClaimed: false,
                    ReleaseApprovalClaimed: false,
                    deliveryBindingsValidated,
                    delivery.Manifest.Artifacts.Count,
                    delivery.UsedSourceSpans.Count,
                    delivery.Manifest.SubtitleVariant.Kind.ToString(),
                    BurnedInSidecarAbsent:
                        delivery.Manifest.SubtitleVariant.SidecarArtifact is null,
                    delivery.ApprovalEvidenceDigest.Hex,
                    delivery.EvidenceDigest.Hex),
                new NegativeControlsEvidence(
                    new NegativeControlEvidence(
                        "source-span-digest-mismatch",
                        provenanceIssueDetected,
                        brokenException.Reason.ToString(),
                        brokenDependencySideEffects,
                        Detected: true),
                    new NegativeControlEvidence(
                        "stale-approval",
                        GraphIssueDetected: false,
                        staleException.Reason.ToString(),
                        staleDependencySideEffects,
                        Detected: true)),
                new HumanChecklistEvidence(
                    checklistItems.Length,
                    CompletedCount: 0,
                    checklistItems,
                    "PENDING"),
                AutomatedPassed: true,
                HumanDecision: "PENDING",
                Q000Complete: false);
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
        IntervalEvidence Interval,
        LayoutEvidence Layout,
        KeyframeEvidence Keyframes,
        SubtitleEvidence Subtitles,
        RenderEvidence Render,
        QualityControlEvidence QualityControl,
        DeliveryEvidence Delivery,
        NegativeControlsEvidence NegativeControls,
        HumanChecklistEvidence HumanChecklist,
        bool AutomatedPassed,
        string HumanDecision,
        bool Q000Complete);

    private sealed record ScopeEvidence(
        string FixtureKind,
        bool ActualMediaUsed,
        bool ActualFfmpegExecuted,
        bool ActualMediaQcExecuted,
        bool ActualFaceDetectionExecuted,
        bool PlatformSpecificSafeZoneCertified,
        bool HumanSelectedInterval,
        bool HumanApprovalClaimed);

    private sealed record IntervalEvidence(
        string SelectionKind,
        string OutputStart,
        string OutputDuration,
        bool HumanSelected,
        string RightsStatus,
        bool ExternalPublicationAllowed);

    private sealed record LayoutEvidence(
        string Mode,
        string StaticResolutionStatus,
        string DynamicResolutionStatus,
        int OutputPixelWidth,
        int OutputPixelHeight,
        int OutputAspectWidth,
        int OutputAspectHeight,
        int SourcePlacementCount,
        bool GenericFullFrameSafeZoneOnly,
        bool SubtitleContainedByGenericSafeZone,
        bool PlatformSpecificSafeZoneClaimed);

    private sealed record KeyframeEvidence(
        string InterpolationRule,
        int KeyframeCount,
        IReadOnlyList<KeyframeSampleEvidence> Samples,
        bool CanonicalNormalizedRoundTripValidated,
        bool BoundToCompiler,
        bool RenderedEvidenceClaimed);

    private sealed record KeyframeSampleEvidence(
        string Time,
        PixelRectEvidence SourceCropPixels,
        PixelRectEvidence DestinationPixels);

    private sealed record PixelRectEvidence(
        int X,
        int Y,
        int Width,
        int Height);

    private sealed record SubtitleEvidence(
        string SourceRevisionContract,
        bool HumanTranscriptWorkClaimed,
        int SegmentationCandidateCount,
        int FaceRegionInputCount,
        bool ActualFaceAbsenceClaimed,
        bool ActualFaceAvoidanceClaimed,
        string LayoutStatus,
        bool SafeZoneConstraintSatisfied,
        int DisplayedCueCount,
        string AssFormat,
        int AssCueCount,
        long AssByteSize,
        string AssContentDigest,
        string TimestampRuleVersion,
        bool RawTextExposed);

    private sealed record RenderEvidence(
        string EvidenceLevel,
        bool GraphValid,
        int GraphIssueCount,
        bool CompilerContractValidated,
        int VerifierCallCount,
        int InputCount,
        bool ProcessRequestGenerated,
        bool ProcessStarted,
        bool KeyframesBoundToCompiler,
        bool TokenizedArguments,
        bool MetadataStripped,
        bool NonOverwrite,
        FfmpegGateEvidence FfmpegGate);

    private sealed record FfmpegGateEvidence(
        string ExpectedSha256,
        long ExpectedByteSize,
        bool LegalApprovalGranted,
        bool ReleaseApprovalGranted,
        bool BinaryDiscoveryAttempted,
        string ExecutionStatus);

    private sealed record QualityControlEvidence(
        string EvidenceLevel,
        bool StaticEvidenceValidated,
        bool ActualMediaQcExecuted,
        bool SyntheticDownstreamFixtureUsed,
        bool HumanReviewRequired,
        bool ApprovedNumericThresholdsApplied,
        bool CanProceedToDelivery,
        int RequiredHumanReviewAreaCount);

    private sealed record DeliveryEvidence(
        string ApprovalEvidenceLevel,
        bool SyntheticApprovalFixtureUsed,
        bool HumanApprovalClaimed,
        bool ReleaseApprovalClaimed,
        bool DigestBindingsValidated,
        int ArtifactCount,
        int UsedSourceSpanCount,
        string SubtitleVariant,
        bool BurnedInSidecarAbsent,
        string ApprovalEvidenceDigest,
        string DeliveryEvidenceDigest);

    private sealed record NegativeControlsEvidence(
        NegativeControlEvidence BrokenProvenance,
        NegativeControlEvidence StaleApproval);

    private sealed record NegativeControlEvidence(
        string Scenario,
        bool GraphIssueDetected,
        string ExpectedDeliveryFailure,
        int DependencySideEffectCount,
        bool Detected);

    private sealed record HumanChecklistEvidence(
        int RequiredCount,
        int CompletedCount,
        IReadOnlyList<HumanChecklistItem> Items,
        string Decision);

    private sealed record HumanChecklistItem(
        string Code,
        string Status);
}