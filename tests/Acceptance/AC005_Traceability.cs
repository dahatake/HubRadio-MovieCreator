using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HubRadio.App.Analysis;
using HubRadio.App.Audit;
using HubRadio.App.Delivery;
using HubRadio.App.Reviews;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AC005_Traceability
{
    private const string ResultPathEnvironmentVariable =
        "HUBRADIO_Q005_RESULT_PATH";
    private const string SyntheticActorSid =
        "S-1-5-21-1000-2000-3000-5001";
    private const string MachineTokenText = "synthetic-machine-caption";
    private const string UnreviewedTokenText = "synthetic-review-candidate";
    private const string ReviewedTokenText = "synthetic-reviewed-caption";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [TestMethod]
    // FR-032 AC-005: 出力の各シーンから元動画区間・出力区間・編集判断・設定・根拠へ逆引きできる。
    public async Task SourceBackedOutputHasClosedReverseTraceabilityAndRejectsBrokenEvidence()
    {
        using var fixture = new AcceptanceFixture();

        AcceptanceEvidence result = await fixture.RunAsync();

        Assert.AreEqual("1.0.0", result.SchemaVersion);
        Assert.AreEqual("Q-005", result.Requirement);
        Assert.IsTrue(result.Passed);
        Assert.AreEqual("5/1", result.TimelineCoverage.OutputDuration);
        Assert.AreEqual(2, result.TimelineCoverage.EntryCount);
        Assert.IsTrue(result.TimelineCoverage.ContiguousFromZero);
        Assert.IsTrue(result.TimelineCoverage.IntervalPartitionProof);
        Assert.AreEqual(8, result.TimelineCoverage.RepresentativeOutputTimeCount);
        Assert.AreEqual(8, result.TimelineCoverage.ReverseRoundTripCount);
        Assert.IsTrue(result.TimelineCoverage.OutputEndRejected);
        Assert.AreEqual(0, result.TimelineCoverage.UnresolvedOutputTimeCount);
        Assert.AreEqual(2, result.ReverseTraceability.SourceSpanResolutionCount);
        Assert.AreEqual(2, result.ReverseTraceability.UsedSourceSpanCount);
        Assert.AreEqual(0, result.ReverseTraceability.UnresolvedOriginCount);
        Assert.AreEqual(2, result.ReverseTraceability.UsedRangeInventory.Count);
        CollectionAssert.AreEqual(
            new[] { OriginKind.SourceSpan.ToString() },
            result.OriginResolution.TimelineOriginKinds.ToArray());
        Assert.IsTrue(result.OriginResolution.AssetResolvedThroughSourceSpan);
        Assert.IsFalse(result.OriginResolution.DirectAssetTimelineOriginClaimed);
        Assert.IsFalse(result.OriginResolution.EditorialRecordAggregateAvailable);
        Assert.IsFalse(result.OriginResolution.EditorialRecordEvidenceClaimed);
        Assert.AreEqual(1, result.SubtitleProvenance.MachineGeneratedSceneCount);
        Assert.AreEqual(1, result.SubtitleProvenance.HumanModifiedSceneCount);
        Assert.IsTrue(result.SubtitleProvenance.SourceRevisionIsMachine);
        Assert.IsTrue(result.SubtitleProvenance.ChildRevisionIsHuman);
        Assert.IsTrue(result.SubtitleProvenance.ParentRevisionMatches);
        Assert.IsTrue(result.SubtitleProvenance.SourceRevisionDigestMatches);
        Assert.IsTrue(result.SubtitleProvenance.ChildRevisionDigestMatches);
        Assert.IsTrue(result.SubtitleProvenance.EditRecordDigestMatches);
        Assert.AreEqual(1, result.SubtitleProvenance.ChangedTokenCount);
        Assert.AreEqual(1, result.DecisionEvidence.AdoptedSuggestionSceneCount);
        Assert.AreEqual(1, result.DecisionEvidence.TranscriptCorrectionSceneCount);
        Assert.IsTrue(result.DecisionEvidence.SyntheticModelRunContractBound);
        Assert.IsTrue(result.DecisionEvidence.HumanDecisionBound);
        Assert.AreEqual(1, result.Rights.UsedAssetCount);
        Assert.AreEqual(1, result.Rights.ApprovedUsedAssetCount);
        Assert.IsTrue(result.Rights.EvidenceReferencePresent);
        Assert.IsTrue(result.Rights.ExternalPublicationAllowed);
        Assert.AreEqual(0, result.Rights.UnresolvedRightsCount);
        Assert.IsTrue(result.Delivery.ManifestVersionMatches);
        Assert.IsTrue(result.Delivery.CompositionDigestMatches);
        Assert.IsTrue(result.Delivery.TimelineDigestMatches);
        Assert.IsTrue(result.Delivery.UsedSourceSpanDigestStableAcrossInputOrder);
        Assert.IsTrue(result.Delivery.ApprovalEvidenceDigestStableAcrossInputOrder);
        Assert.IsTrue(result.Delivery.DeliveryEvidenceDigestStableAcrossInputOrder);
        Assert.AreEqual(2, result.Delivery.ArtifactCount);
        Assert.AreEqual(3, result.Audit.RecordCount);
        Assert.AreEqual(3, result.Audit.CanonicalRecordCount);
        Assert.AreEqual(0, result.Audit.ContentLeakCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "transcriptCorrection",
                "suggestionDecision",
                "delivery",
            },
            result.Audit.RecordKinds.ToArray());
        Assert.AreEqual(
            "sourceSpanDigestMismatch",
            result.NegativeControl.Scenario);
        Assert.IsTrue(result.NegativeControl.RenderGraphIssueDetected);
        Assert.AreEqual(
            "ProvenanceInvalid",
            result.NegativeControl.ExpectedDeliveryFailure);
        Assert.IsTrue(result.NegativeControl.Detected);
        Assert.AreEqual(0, result.NegativeControl.DependencySideEffectCount);
        Assert.IsFalse(result.Scope.ActualMediaUsed);
        Assert.IsFalse(result.Scope.ActualModelInferenceUsed);
        Assert.IsFalse(result.Scope.HumanAcceptanceClaimed);
        Assert.IsFalse(result.OriginResolution.EditorialRecordEvidenceClaimed);

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
            MachineTokenText,
            UnreviewedTokenText,
            ReviewedTokenText,
            "synthetic-source.mov",
            "projects/q005/renders/final.mp4",
            "projects/q005/deliveries/subtitle.vtt",
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
            Assert.Fail("The Q-005 acceptance result contains private evidence.");
        }

        string fullPath = Path.GetFullPath(requestedPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Fail("The Q-005 acceptance result path has no parent directory.");
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
            string.Concat("00000000-0000-0000-0000-", suffix));
    }

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(value));

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static RationalTime Fraction(
        RationalTime value,
        long numerator,
        long denominator) =>
        new(
            checked(value.Value * numerator),
            checked(value.Rate * denominator));

    private sealed class AcceptanceFixture : IDisposable
    {
        private static readonly DateTimeOffset Baseline = new(
            2049,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero);

        private readonly string _rootPath;
        private readonly ProjectStore _projectStore;
        private readonly ReviewStore _reviewStore;
        private readonly ProvenanceAuditService _auditService;
        private readonly Dictionary<EntityId<ArtifactIdTag>, StoredArtifact>
            _registeredArtifacts = [];
        private readonly IReadOnlyList<
            KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>>
            _artifactEvidence;
        private readonly DeliveryService _deliveryService;
        private int _approvalEvaluationCount;
        private int _artifactReadCount;
        private int _artifactValidationCount;

        internal AcceptanceFixture()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.Q005.Acceptance",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            string databasePath = Path.Combine(_rootPath, "acceptance.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(_rootPath, "backups"));
            _projectStore = new ProjectStore(databasePath);
            _reviewStore = new ReviewStore(databasePath);
            _auditService = new ProvenanceAuditService(_reviewStore);

            Asset = new Asset(
                Id<AssetIdTag>(1),
                "synthetic-source.mov",
                "video/quicktime",
                Digest("q005.synthetic.source.v1"),
                4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "q005.synthetic.rights-evidence.v1",
                    "Deterministic acceptance testing only.",
                    externalPublicationAllowed: true));
            Project = new Project(
                Id<ProjectIdTag>(2),
                "Q-005 synthetic traceability fixture",
                "Verify closed reverse provenance without real media.",
                "Automated acceptance only",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                [Asset],
                performers: []);
            _projectStore.CreateProject(
                Project,
                "{\"fixture\":\"q005-synthetic\"}",
                Baseline.AddMinutes(-1));

            TimeRange firstSourceRange = Range(10, 2);
            TimeRange secondSourceRange = Range(20, 3);
            var firstToken = new Token(
                Id<TranscriptTokenIdTag>(3),
                MachineTokenText,
                firstSourceRange,
                recognitionConfidence: 1d);
            var secondToken = new Token(
                Id<TranscriptTokenIdTag>(4),
                UnreviewedTokenText,
                secondSourceRange,
                recognitionConfidence: 1d);
            var firstUtterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(5),
                Id<SourceSpanIdTag>(6),
                Id<TranscriptSpeakerIdTag>(7),
                "Synthetic speaker",
                MachineTokenText,
                firstSourceRange,
                recognitionConfidence: 1d,
                [
                    new Sentence(
                        Id<TranscriptSentenceIdTag>(8),
                        MachineTokenText,
                        [firstToken]),
                ]);
            var secondUtterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(9),
                Id<SourceSpanIdTag>(10),
                Id<TranscriptSpeakerIdTag>(7),
                "Synthetic speaker",
                UnreviewedTokenText,
                secondSourceRange,
                recognitionConfidence: 1d,
                [
                    new Sentence(
                        Id<TranscriptSentenceIdTag>(11),
                        UnreviewedTokenText,
                        [secondToken]),
                ]);
            MachineRevision = new TranscriptRevision(
                Id<TranscriptRevisionIdTag>(12),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                Baseline,
                [firstUtterance, secondUtterance]);
            MachineSourceSpans =
            [
                new SourceSpan(
                    firstUtterance.SourceSpanId,
                    Asset.Id,
                    "video:0",
                    firstSourceRange,
                    Asset.ContentDigest,
                    MachineRevision.Id,
                    [firstToken.Id]),
                new SourceSpan(
                    secondUtterance.SourceSpanId,
                    Asset.Id,
                    "video:0",
                    secondSourceRange,
                    Asset.ContentDigest,
                    MachineRevision.Id,
                    [secondToken.Id]),
            ];
            TranscriptEdit = TranscriptEditor.ApplyTokenTextEdits(
                MachineRevision,
                MachineSourceSpans,
                [
                    new TranscriptTokenTextEdit(
                        secondToken.Id,
                        UnreviewedTokenText,
                        ReviewedTokenText),
                ],
                Id<TranscriptRevisionIdTag>(13),
                SyntheticActorSid,
                Baseline.AddMinutes(10),
                "Record the deterministic reviewed caption correction.");
            SourceSpans = TranscriptEdit.RevisedSourceSpans;

            SourceSpan firstSpan = SourceSpans[0];
            OriginRef firstOrigin = OriginRef.FromSourceSpan(firstSpan.Id);
            ContentDigest modelOutputDigest = Digest(string.Concat(
                "q005.synthetic.no-inference.output.v1|",
                firstSpan.Id.ToString()));
            ModelRun = new ModelRun(
                Id<ModelRunIdTag>(14),
                "q005-synthetic-control",
                "no-inference-contract-fixture",
                "1.0.0",
                Digest("q005.synthetic.no-inference.model-artifact.v1"),
                ModelHashAvailability.Available,
                Digest("q005.synthetic.no-inference.configuration.v1"),
                TranscriptEdit.RevisionDigest,
                modelOutputDigest,
                ModelRunOutcome.Succeeded,
                errorCode: null,
                ModelExecutionLocation.Local,
                destination: null,
                region: null,
                sentDataClassification: null,
                retentionPolicy: null,
                Baseline.AddMinutes(11),
                Baseline.AddMinutes(12));
            string firstCaption = TranscriptEdit.Revision
                .Utterances[0]
                .Tokens[0]
                .Text;
            var proposedSuggestion = new Suggestion(
                Id<SuggestionIdTag>(15),
                ModelRun.Id,
                SuggestionKind.Highlight,
                SuggestionStatus.Proposed,
                firstOrigin,
                "Synthetic explicit-evidence highlight candidate.",
                confidence: null,
                "No editorial priority is inferred by this synthetic control.",
                "Human adoption is required.");
            var emphasis = new EmphasisCandidate(
                proposedSuggestion,
                firstSpan,
                TranscriptEdit.Revision.Id,
                TranscriptEdit.RevisionDigest,
                Id<TopicMetadataRevisionIdTag>(16),
                EmphasisCandidateService.RuleVersion,
                firstCaption,
                EmphasisTextSourceKind.OriginalTranscript,
                [EmphasisReasonKind.ImportantStatement],
                termEvidence: []);
            Candidate = SuggestionService.Register(emphasis, []);
            Adoption = SuggestionService.AdoptHighlight(
                Candidate,
                Candidate.SourceDigest,
                Id<CueIdTag>(17),
                Range(0, 2),
                "graphic",
                decisionHistory: [],
                Id<SuggestionDecisionIdTag>(18),
                SyntheticActorSid,
                Baseline.AddMinutes(20),
                "Adopt the deterministic synthetic candidate for trace testing.");

            SourceSpan secondSpan = SourceSpans[1];
            OriginRef secondOrigin = OriginRef.FromSourceSpan(secondSpan.Id);
            CompositionElement[] elements =
            [
                new CompositionElement(
                    Id<CompositionElementIdTag>(19),
                    Range(0, 2),
                    firstOrigin,
                    Adoption.Reference,
                    firstSpan.Range),
                new CompositionElement(
                    Id<CompositionElementIdTag>(20),
                    Range(2, 3),
                    secondOrigin,
                    adoptedSuggestion: null,
                    secondSpan.Range),
            ];
            Version = new CompositionVersion(
                Id<CompositionVersionIdTag>(21),
                Project.Id,
                parentVersionId: null,
                SyntheticActorSid,
                Baseline.AddMinutes(30),
                "Create the deterministic Q-005 composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements),
                elements);
            TimelineMap = TimelineMapBuilder.Build(
                Id<TimelineMapIdTag>(22),
                Version);
            TimelineMapDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(TimelineMap);
            _projectStore.AppendCompositionVersion(
                Version,
                "{\"fixture\":\"q005-composition\"}");

            Template = new TemplateReference(
                Id<TemplateIdTag>(23),
                "Q-005 synthetic template",
                Digest("q005.synthetic.template.v1"));
            Layout = new LayoutSpec(
                Id<LayoutSpecIdTag>(24),
                16,
                9,
                [
                    new LayoutRegion(
                        "video",
                        new NormalizedRect(
                            default,
                            default,
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            LayoutDigest = Digest("q005.synthetic.layout.v1");
            Profile = new RenderProfile(
                Id<RenderProfileIdTag>(25),
                "q005-synthetic-profile",
                Digest("q005.synthetic.profile.v1"),
                1920,
                1080,
                30,
                1,
                Layout.Id,
                Template.Id,
                Template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);

            FirstInput = new RenderGraphInput(
                Id<RenderInputIdTag>(26),
                RenderInputKind.Video,
                Asset.ContentDigest,
                Asset.ByteSize,
                firstOrigin);
            SecondInput = new RenderGraphInput(
                Id<RenderInputIdTag>(27),
                RenderInputKind.Video,
                Asset.ContentDigest,
                Asset.ByteSize,
                secondOrigin);
            RenderNode firstVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(28),
                FirstInput.Id,
                elements[0].OutputRange,
                firstOrigin,
                firstSpan.Range,
                "video");
            RenderNode secondVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(29),
                SecondInput.Id,
                elements[1].OutputRange,
                secondOrigin,
                secondSpan.Range,
                "video");
            RenderNode cut = RenderNode.TransitionEffect(
                Id<RenderNodeIdTag>(30),
                [firstVideo.Id, secondVideo.Id],
                new TimeRange(new RationalTime(2, 1), default),
                secondOrigin,
                Transition.Cut());
            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(31),
                [cut.Id],
                Range(0, 5));
            Graph = new RenderGraph(
                Id<RenderGraphIdTag>(32),
                Version.Id,
                Version.ContentDigest,
                TimelineMap.Id,
                TimelineMapDigest,
                Profile.Id,
                Profile.ProfileDigest,
                Layout.Id,
                LayoutDigest,
                Template,
                [FirstInput, SecondInput],
                fonts: [],
                [firstVideo, secondVideo, cut, output],
                output.Id);
            ValidationContext = new RenderGraphValidationContext(
                Project,
                Version,
                TimelineMap,
                TimelineMapDigest,
                Profile,
                Layout,
                LayoutDigest,
                Template,
                SourceSpans,
                editorialRecordIds: [],
                [
                    new RenderArtifactEvidence(
                        FirstInput.Id,
                        FirstInput.ContentDigest,
                        FirstInput.ByteSize),
                    new RenderArtifactEvidence(
                        SecondInput.Id,
                        SecondInput.ContentDigest,
                        SecondInput.ByteSize),
                ],
                fonts: []);

            VideoManifestArtifact = new ManifestArtifact(
                "video",
                "video/mp4",
                "ja",
                512,
                Digest("q005.synthetic.render-output.v1"),
                "projects/q005/renders/final.mp4");
            SubtitleManifestArtifact = new ManifestArtifact(
                "subtitle",
                "text/vtt",
                "ja",
                128,
                Digest("q005.synthetic.subtitle-output.v1"),
                "projects/q005/deliveries/subtitle.vtt");
            RenderManifest = new RenderManifest(
                Id<RenderManifestIdTag>(33),
                Version,
                TimelineMap,
                TimelineMapDigest,
                Profile,
                Digest("q005.synthetic.render-configuration.v1"),
                "1.0.0-q005-synthetic",
                Digest("q005.synthetic.application.v1"),
                [
                    new RenderInputEvidence("source-scene-1", Asset.ContentDigest),
                    new RenderInputEvidence("source-scene-2", Asset.ContentDigest),
                ],
                [
                    new RenderToolEvidence(
                        "ffmpeg",
                        DecodeValidator.CanonicalPackageVersion,
                        "synthetic-contract-no-execution",
                        new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                        containerImageDigest: null),
                ],
                [
                    new RenderModelEvidence(
                        ModelRun.Provider,
                        ModelRun.ModelIdentifier,
                        ModelRun.ModelVersion,
                        ModelRun.ModelHash!),
                ],
                fonts: [],
                [VideoManifestArtifact]);

            VideoArtifactId = Id<ArtifactIdTag>(34);
            SubtitleArtifactId = Id<ArtifactIdTag>(35);
            var videoArtifact = new StoredArtifact(
                VideoArtifactId,
                Project.Id,
                ArtifactKind.Render,
                VideoManifestArtifact.StorageReference,
                VideoManifestArtifact.Sha256,
                VideoManifestArtifact.ByteSize,
                Baseline.AddMinutes(32));
            var subtitleArtifact = new StoredArtifact(
                SubtitleArtifactId,
                Project.Id,
                ArtifactKind.Delivery,
                SubtitleManifestArtifact.StorageReference,
                SubtitleManifestArtifact.Sha256,
                SubtitleManifestArtifact.ByteSize,
                Baseline.AddMinutes(33));
            _registeredArtifacts.Add(VideoArtifactId, videoArtifact);
            _registeredArtifacts.Add(SubtitleArtifactId, subtitleArtifact);
            _artifactEvidence =
            [
                new(VideoArtifactId, VideoManifestArtifact),
                new(SubtitleArtifactId, SubtitleManifestArtifact),
            ];
            Approval = new StoredApproval(
                Id<ApprovalIdTag>(36),
                Project.Id,
                Version.Id,
                Version.ContentDigest,
                SyntheticActorSid,
                ReviewActorRole.ContentApprover,
                Baseline.AddMinutes(35),
                "Approve the exact deterministic Q-005 composition digest.");

            QcRequest = new OutputQcRequest(
                VideoArtifactId,
                RenderManifest,
                Graph,
                ValidationContext,
                subtitleEvidence: null,
                TimeSpan.FromMinutes(1));
            var timing = new OutputQcStreamTiming(
                streamIndex: 0,
                [new OutputQcTimingEntry(0, 0, 5)],
                new RationalTime(1, 1),
                presentationStart: default,
                presentationEnd: new RationalTime(5, 1),
                Digest("q005.synthetic.video-timing.v1"));
            QualityControl = new OutputQcResult(
                videoArtifact,
                QcRequest,
                Digest("q005.synthetic.probe-evidence.v1"),
                videoStreamIndex: 0,
                audioStreamIndex: null,
                subtitleStreamIndex: null,
                timing,
                audioTiming: null,
                subtitleTiming: null,
                new OutputQcDecodeEvidence(TimeSpan.Zero),
                new OutputQcLumaMeasurements(
                    [new OutputQcLumaFrameMeasurement(0, 0, 128m)],
                    Digest("q005.synthetic.luma-evidence.v1")),
                audioMeasurements: null);

            _deliveryService = new DeliveryService(
                (_, approvals) =>
                {
                    _approvalEvaluationCount++;
                    return new ApprovalValidityResult(
                        approvals.ToArray(),
                        staleApprovals: []);
                },
                artifactId =>
                {
                    _artifactReadCount++;
                    return _registeredArtifacts.GetValueOrDefault(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    _artifactValidationCount++;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(_registeredArtifacts[artifactId]);
                });
        }

        private Project Project { get; }

        private Asset Asset { get; }

        private TranscriptRevision MachineRevision { get; }

        private IReadOnlyList<SourceSpan> MachineSourceSpans { get; }

        private TranscriptEditResult TranscriptEdit { get; }

        private IReadOnlyList<SourceSpan> SourceSpans { get; }

        private ModelRun ModelRun { get; }

        private EmphasisSuggestionCandidate Candidate { get; }

        private HighlightSuggestionAdoptionResult Adoption { get; }

        private CompositionVersion Version { get; }

        private TimelineMap TimelineMap { get; }

        private ContentDigest TimelineMapDigest { get; }

        private TemplateReference Template { get; }

        private LayoutSpec Layout { get; }

        private ContentDigest LayoutDigest { get; }

        private RenderProfile Profile { get; }

        private RenderGraphInput FirstInput { get; }

        private RenderGraphInput SecondInput { get; }

        private RenderGraph Graph { get; }

        private RenderGraphValidationContext ValidationContext { get; }

        private ManifestArtifact VideoManifestArtifact { get; }

        private ManifestArtifact SubtitleManifestArtifact { get; }

        private RenderManifest RenderManifest { get; }

        private EntityId<ArtifactIdTag> VideoArtifactId { get; }

        private EntityId<ArtifactIdTag> SubtitleArtifactId { get; }

        private StoredApproval Approval { get; }

        private OutputQcRequest QcRequest { get; }

        private OutputQcResult QualityControl { get; }

        internal async Task<AcceptanceEvidence> RunAsync()
        {
            RenderGraphValidationResult graphValidation =
                RenderGraphValidator.Validate(Graph, ValidationContext);
            Assert.IsTrue(graphValidation.IsValid);
            OutputQc.ValidateEvidence(QcRequest, CancellationToken.None);

            _ = _auditService.RecordTranscriptCorrection(
                Id<AuditEventIdTag>(37),
                Project.Id,
                TranscriptEdit,
                ReviewActorRole.Editor);
            _ = _auditService.RecordSuggestionDecision(
                Id<AuditEventIdTag>(38),
                Project.Id,
                Candidate,
                detection: null,
                ModelRun,
                Adoption.DecisionRecord,
                ReviewActorRole.Editor);

            DeliveryCreationResult firstDelivery =
                await _deliveryService.CreateAsync(
                    Id<DeliveryManifestIdTag>(39),
                    Project,
                    Version,
                    RenderManifest,
                    ValidationContext,
                    QualityControl,
                    [Approval],
                    _artifactEvidence);
            DeliveryCreationResult reorderedDelivery =
                await _deliveryService.CreateAsync(
                    Id<DeliveryManifestIdTag>(39),
                    Project,
                    Version,
                    RenderManifest,
                    ValidationContext,
                    QualityControl,
                    [Approval],
                    _artifactEvidence.Reverse().ToArray());
            _ = _auditService.RecordDelivery(
                Id<AuditEventIdTag>(40),
                Version,
                firstDelivery.Manifest,
                SyntheticActorSid,
                ReviewActorRole.Editor,
                Baseline.AddMinutes(40),
                "Record the deterministic Q-005 delivery evidence.");

            TraceVerification trace = VerifyReverseTraceability(firstDelivery);
            SubtitleEvidence subtitle = VerifySubtitleEvidence(trace.Inventory);
            NegativeControlEvidence negative =
                await VerifyBrokenDigestIsRejectedAsync();
            AuditEvidence audit = VerifyAuditEvidence();

            bool usedSourceSpanDigestStable =
                firstDelivery.UsedSourceSpansDigest ==
                    reorderedDelivery.UsedSourceSpansDigest;
            bool approvalEvidenceDigestStable =
                firstDelivery.ApprovalEvidenceDigest ==
                    reorderedDelivery.ApprovalEvidenceDigest;
            bool deliveryEvidenceDigestStable =
                firstDelivery.EvidenceDigest == reorderedDelivery.EvidenceDigest;
            Assert.IsTrue(usedSourceSpanDigestStable);
            Assert.IsTrue(approvalEvidenceDigestStable);
            Assert.IsTrue(deliveryEvidenceDigestStable);
            Assert.AreEqual(2, _approvalEvaluationCount);
            Assert.AreEqual(4, _artifactReadCount);
            Assert.AreEqual(4, _artifactValidationCount);

            string[] originKinds = trace.Inventory
                .Select(static item => item.OriginKind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            bool usesOnlySourceSpanOrigins = originKinds.SequenceEqual(
                [OriginKind.SourceSpan.ToString()],
                StringComparer.Ordinal);
            var assets = Project.Assets.ToDictionary(static asset => asset.Id);
            EntityId<AssetIdTag>[] usedAssetIds = firstDelivery.UsedSourceSpans
                .Select(static span => span.AssetId)
                .Distinct()
                .ToArray();
            Asset[] resolvedUsedAssets = usedAssetIds
                .Where(assets.ContainsKey)
                .Select(assetId => assets[assetId])
                .ToArray();
            int unresolvedRightsCount = usedAssetIds.Count(assetId =>
                !assets.TryGetValue(assetId, out Asset? asset)
                || asset.Rights.Status != RightsStatus.Approved
                || !asset.Rights.ExternalPublicationAllowed
                || string.IsNullOrWhiteSpace(asset.Rights.EvidenceReference));
            int approvedUsedAssetCount = resolvedUsedAssets.Count(asset =>
                asset.Rights.Status == RightsStatus.Approved);
            bool evidenceReferencePresent =
                resolvedUsedAssets.Length == usedAssetIds.Length
                && resolvedUsedAssets.All(asset =>
                    !string.IsNullOrWhiteSpace(asset.Rights.EvidenceReference));
            bool externalPublicationAllowed =
                resolvedUsedAssets.Length == usedAssetIds.Length
                && resolvedUsedAssets.All(static asset =>
                    asset.Rights.ExternalPublicationAllowed);
            Assert.IsTrue(usesOnlySourceSpanOrigins);
            Assert.AreEqual(1, usedAssetIds.Length);
            Assert.AreEqual(1, approvedUsedAssetCount);
            Assert.IsTrue(evidenceReferencePresent);
            Assert.IsTrue(externalPublicationAllowed);
            Assert.AreEqual(0, unresolvedRightsCount);

            bool syntheticModelRunContractBound =
                ModelRun.Id == Candidate.Suggestion.ModelRunId
                && ModelRun.Id == Adoption.DecisionRecord.ModelRunId
                && ModelRun.InputDigest == TranscriptEdit.RevisionDigest
                && ModelRun.OutputDigest == Digest(string.Concat(
                    "q005.synthetic.no-inference.output.v1|",
                    SourceSpans[0].Id.ToString()))
                && ModelRun.Outcome == ModelRunOutcome.Succeeded
                && Candidate.Payload.SourceSpan.Id == SourceSpans[0].Id
                && Candidate.Payload.SourceRevisionId == TranscriptEdit.Revision.Id
                && Candidate.Payload.SourceRevisionDigest ==
                    TranscriptEdit.RevisionDigest
                && Candidate.SourceDigest == TranscriptEdit.RevisionDigest;
            bool humanDecisionBound =
                Adoption.DecisionRecord.Decision ==
                    SuggestionDecisionKind.Adopted
                && Adoption.DecisionRecord.StatusAfterDecision ==
                    SuggestionStatus.Adopted
                && Adoption.DecisionRecord.Origin == Candidate.Origin
                && Adoption.DecisionRecord.SourceDigest == Candidate.SourceDigest
                && Adoption.DecisionRecord.AdoptedReference ==
                    Adoption.Reference
                && Adoption.Reference == Version.Elements[0].AdoptedSuggestion
                && string.Equals(
                    Adoption.DecisionRecord.ActorSid,
                    SyntheticActorSid,
                    StringComparison.Ordinal);
            Assert.IsTrue(syntheticModelRunContractBound);
            Assert.IsTrue(humanDecisionBound);

            bool manifestVersionMatches =
                firstDelivery.Manifest.CompositionVersionId == Version.Id
                && firstDelivery.Manifest.CompositionVersionDigest ==
                    Version.ContentDigest;
            bool compositionDigestMatches =
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    Version.Elements) == Version.ContentDigest;
            bool timelineDigestMatches =
                TimelineMap.CompositionVersionId == Version.Id
                && RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                    TimelineMap) == TimelineMapDigest;
            Assert.IsTrue(manifestVersionMatches);
            Assert.IsTrue(compositionDigestMatches);
            Assert.IsTrue(timelineDigestMatches);

            return new AcceptanceEvidence(
                "1.0.0",
                "Q-005",
                new ScopeEvidence(
                    "deterministic-rights-safe-synthetic-contract",
                    ActualMediaUsed: false,
                    ActualModelInferenceUsed: false,
                    HumanAcceptanceClaimed: false),
                new OriginResolutionEvidence(
                    originKinds,
                    AssetResolvedThroughSourceSpan:
                        usesOnlySourceSpanOrigins && usedAssetIds.Length > 0,
                    DirectAssetTimelineOriginClaimed: originKinds.Contains(
                        OriginKind.Asset.ToString(),
                        StringComparer.Ordinal),
                    EditorialRecordAggregateAvailable: false,
                    EditorialRecordEvidenceClaimed: originKinds.Contains(
                        OriginKind.EditorialRecord.ToString(),
                        StringComparer.Ordinal)),
                new TimelineCoverageEvidence(
                    OutputDuration: TimelineMap.Entries[^1].OutputRange.End.ToString(),
                    EntryCount: TimelineMap.Entries.Count,
                    ContiguousFromZero: trace.ContiguousFromZero,
                    IntervalPartitionProof: trace.IntervalPartitionProof,
                    RepresentativeOutputTimeCount: trace.ProbeCount,
                    ReverseRoundTripCount: trace.RoundTripCount,
                    OutputEndRejected: trace.OutputEndRejected,
                    UnresolvedOutputTimeCount: trace.UnresolvedOutputTimeCount),
                new ReverseTraceabilityEvidence(
                    SourceSpanResolutionCount: trace.Inventory.Count,
                    UsedSourceSpanCount: firstDelivery.UsedSourceSpans.Count,
                    UnresolvedOriginCount: trace.UnresolvedOriginCount,
                    trace.Inventory),
                subtitle,
                new DecisionEvidence(
                    AdoptedSuggestionSceneCount: trace.Inventory.Count(item =>
                        string.Equals(
                            item.DecisionEvidence,
                            "adoptedSuggestion",
                            StringComparison.Ordinal)),
                    TranscriptCorrectionSceneCount: trace.Inventory.Count(item =>
                        string.Equals(
                            item.DecisionEvidence,
                            "transcriptCorrection",
                            StringComparison.Ordinal)),
                    syntheticModelRunContractBound,
                    humanDecisionBound),
                new RightsEvidence(
                    UsedAssetCount: usedAssetIds.Length,
                    ApprovedUsedAssetCount: approvedUsedAssetCount,
                    EvidenceReferencePresent: evidenceReferencePresent,
                    ExternalPublicationAllowed: externalPublicationAllowed,
                    UnresolvedRightsCount: unresolvedRightsCount),
                new DeliveryEvidence(
                    ManifestVersionMatches: manifestVersionMatches,
                    CompositionDigestMatches: compositionDigestMatches,
                    TimelineDigestMatches: timelineDigestMatches,
                    UsedSourceSpanDigestStableAcrossInputOrder:
                        usedSourceSpanDigestStable,
                    ApprovalEvidenceDigestStableAcrossInputOrder:
                        approvalEvidenceDigestStable,
                    DeliveryEvidenceDigestStableAcrossInputOrder:
                        deliveryEvidenceDigestStable,
                    ArtifactCount: firstDelivery.Manifest.Artifacts.Count),
                audit,
                negative,
                Passed: true);
        }

        private TraceVerification VerifyReverseTraceability(
            DeliveryCreationResult delivery)
        {
            Assert.AreEqual(Version.Elements.Count, TimelineMap.Entries.Count);
            var sourceSpans = SourceSpans.ToDictionary(static span => span.Id);
            var assets = Project.Assets.ToDictionary(static asset => asset.Id);
            var revisionTokens = TranscriptEdit.Revision.Utterances
                .SelectMany(static utterance => utterance.Tokens)
                .ToDictionary(static token => token.Id);
            var machineTokens = MachineRevision.Utterances
                .SelectMany(static utterance => utterance.Tokens)
                .ToDictionary(static token => token.Id);
            HashSet<EntityId<TranscriptTokenIdTag>> editedTokenIds = TranscriptEdit
                .EditRecord
                .Diff
                .Changes
                .Where(static change =>
                    change.Kind == TranscriptTextChangeKind.Token)
                .Select(static change => change.TokenId!)
                .ToHashSet();
            HashSet<EntityId<SourceSpanIdTag>> deliveredSpanIds = delivery
                .UsedSourceSpans
                .Select(static span => span.Id)
                .ToHashSet();
            var inventory = new List<UsedRangeEvidence>(TimelineMap.Entries.Count);
            RationalTime expectedStart = default;
            bool contiguousFromZero = TimelineMap.Entries.Count > 0;
            bool intervalPartitionProof = TimelineMap.Entries.Count > 0;
            int probeCount = 0;
            int roundTripCount = 0;
            int unresolvedOutputTimeCount = 0;
            int unresolvedOriginCount = 0;

            for (int index = 0; index < TimelineMap.Entries.Count; index++)
            {
                TimelineMapEntry entry = TimelineMap.Entries[index];
                CompositionElement element = Version.Elements[index];
                bool entryIsContiguous = entry.OutputRange.Start == expectedStart;
                bool entryHasPositiveDuration =
                    entry.OutputRange.Duration > default(RationalTime);
                contiguousFromZero &= entryIsContiguous;
                intervalPartitionProof &=
                    entryIsContiguous && entryHasPositiveDuration;
                Assert.AreEqual(expectedStart, entry.OutputRange.Start);
                Assert.IsTrue(entry.OutputRange.Duration > default(RationalTime));
                Assert.AreEqual(element.OutputRange, entry.OutputRange);
                Assert.AreEqual(element.Origin, entry.Origin);
                Assert.AreEqual(element.SourceRange, entry.SourceRange);
                bool originResolved =
                    entry.Origin.Kind == OriginKind.SourceSpan
                    && entry.SourceRange is not null
                    && entry.Origin.SourceSpanId is
                        EntityId<SourceSpanIdTag> candidateSpanId
                    && sourceSpans.TryGetValue(
                        candidateSpanId,
                        out SourceSpan? candidateSpan)
                    && assets.ContainsKey(candidateSpan.AssetId);
                if (!originResolved)
                {
                    unresolvedOriginCount++;
                }

                Assert.AreEqual(OriginKind.SourceSpan, entry.Origin.Kind);
                Assert.IsNotNull(entry.SourceRange);
                Assert.IsNotNull(entry.Origin.SourceSpanId);
                Assert.IsTrue(sourceSpans.TryGetValue(
                    entry.Origin.SourceSpanId,
                    out SourceSpan? span));
                Assert.IsNotNull(span);
                Assert.AreEqual(span.Range, entry.SourceRange.Value);
                Assert.IsTrue(deliveredSpanIds.Contains(span.Id));
                Assert.IsTrue(assets.TryGetValue(span.AssetId, out Asset? asset));
                Assert.IsNotNull(asset);
                Assert.AreEqual(asset.ContentDigest, span.SourceDigest);
                Assert.AreEqual(RightsStatus.Approved, asset.Rights.Status);
                Assert.IsTrue(asset.Rights.ExternalPublicationAllowed);
                Assert.AreEqual(TranscriptEdit.Revision.Id, span.TranscriptRevisionId);
                Assert.IsTrue(span.TranscriptTokenIds.All(revisionTokens.ContainsKey));

                RationalTime[] offsets =
                [
                    default,
                    Fraction(entry.OutputRange.Duration, 1, 2),
                    Fraction(entry.OutputRange.Duration, 3, 4),
                    Fraction(entry.OutputRange.Duration, 999, 1000),
                ];
                foreach (RationalTime offset in offsets)
                {
                    RationalTime outputTime = entry.OutputRange.Start + offset;
                    bool outputResolved = TimelineMap.TryMapOutputTime(
                        outputTime,
                        out TimelineMapPoint point);
                    if (!outputResolved)
                    {
                        unresolvedOutputTimeCount++;
                    }

                    Assert.IsTrue(outputResolved);
                    Assert.AreEqual(entry.Origin, point.Origin);
                    Assert.AreEqual(
                        entry.SourceRange!.Value.Start + offset,
                        point.SourceTime);
                    IReadOnlyList<RationalTime> reverse =
                        TimelineMap.MapSourceTimeToOutput(
                            span.Id,
                            point.SourceTime!.Value);
                    Assert.AreEqual(1, reverse.Count);
                    Assert.AreEqual(outputTime, reverse[0]);
                    probeCount++;
                    roundTripCount++;
                }

                IReadOnlyList<TimeRange> originRanges =
                    TimelineMap.MapOriginToOutputRanges(entry.Origin);
                Assert.AreEqual(1, originRanges.Count);
                Assert.AreEqual(entry.OutputRange, originRanges[0]);

                bool humanModified = span.TranscriptTokenIds.Any(
                    editedTokenIds.Contains);
                string subtitleProvenance = humanModified
                    ? "humanModified"
                    : "machineGenerated";
                foreach (EntityId<TranscriptTokenIdTag> tokenId in
                    span.TranscriptTokenIds)
                {
                    Assert.IsTrue(machineTokens.TryGetValue(
                        tokenId,
                        out Token? machineToken));
                    Token currentToken = revisionTokens[tokenId];
                    Assert.IsNotNull(machineToken.Range);
                    Assert.IsNotNull(currentToken.Range);
                    Assert.AreEqual(span.Range, machineToken.Range.Value);
                    Assert.AreEqual(span.Range, currentToken.Range.Value);
                    bool tokenWasModified = editedTokenIds.Contains(tokenId);
                    Assert.AreEqual(
                        tokenWasModified,
                        !string.Equals(
                            machineToken.Text,
                            currentToken.Text,
                            StringComparison.Ordinal));
                }

                string decisionEvidence;
                if (element.AdoptedSuggestion is not null)
                {
                    Assert.AreEqual(
                        Adoption.Reference,
                        element.AdoptedSuggestion);
                    Assert.AreEqual(
                        Adoption.DecisionRecord.CandidateId,
                        element.AdoptedSuggestion.SuggestionId);
                    Assert.AreEqual(
                        Adoption.DecisionRecord.Origin,
                        element.AdoptedSuggestion.Origin);
                    decisionEvidence = "adoptedSuggestion";
                }
                else
                {
                    Assert.IsTrue(humanModified);
                    decisionEvidence = "transcriptCorrection";
                }

                inventory.Add(new UsedRangeEvidence(
                    entry.OutputRange.Start.ToString(),
                    entry.OutputRange.Duration.ToString(),
                    entry.SourceRange.Value.Start.ToString(),
                    entry.SourceRange.Value.Duration.ToString(),
                    entry.Origin.Kind.ToString(),
                    subtitleProvenance,
                    decisionEvidence,
                    asset.Rights.Status.ToString(),
                    asset.Rights.ExternalPublicationAllowed));
                expectedStart = entry.OutputRange.End;
            }

            bool outputEndRejected = !TimelineMap.TryMapOutputTime(
                expectedStart,
                out _);
            Assert.IsTrue(outputEndRejected);
            Assert.IsFalse(TimelineMap.TryMapOutputTime(
                new RationalTime(-1, 1),
                out _));
            Assert.AreEqual(SourceSpans.Count, deliveredSpanIds.Count);
            Assert.IsTrue(SourceSpans.All(span => deliveredSpanIds.Contains(span.Id)));

            return new TraceVerification(
                inventory.AsReadOnly(),
                probeCount,
                roundTripCount,
                outputEndRejected,
                contiguousFromZero,
                intervalPartitionProof,
                unresolvedOutputTimeCount,
                unresolvedOriginCount);
        }

        private SubtitleEvidence VerifySubtitleEvidence(
            IReadOnlyList<UsedRangeEvidence> inventory)
        {
            string sourceJson = TranscriptSnapshotJson.WriteRevision(
                TranscriptEdit.SourceRevision);
            string revisionJson = TranscriptSnapshotJson.WriteRevision(
                TranscriptEdit.Revision);
            string editRecordJson = TranscriptSnapshotJson.WriteEditRecord(
                TranscriptEdit.EditRecord);
            bool sourceDigestMatches = ContentDigest.Compute(
                StrictUtf8.GetBytes(sourceJson)) ==
                TranscriptEdit.DownstreamStale.SourceRevisionDigest;
            bool revisionDigestMatches = ContentDigest.Compute(
                StrictUtf8.GetBytes(revisionJson)) == TranscriptEdit.RevisionDigest
                && string.Equals(
                    revisionJson,
                    TranscriptEdit.CanonicalRevisionJson,
                    StringComparison.Ordinal);
            bool editRecordDigestMatches = ContentDigest.Compute(
                StrictUtf8.GetBytes(editRecordJson)) ==
                TranscriptEdit.EditRecordDigest
                && string.Equals(
                    editRecordJson,
                    TranscriptEdit.CanonicalEditRecordJson,
                    StringComparison.Ordinal);
            int changedTokenCount = TranscriptEdit.EditRecord.Diff.Changes.Count(
                static change => change.Kind == TranscriptTextChangeKind.Token);

            Assert.IsTrue(sourceDigestMatches);
            Assert.IsTrue(revisionDigestMatches);
            Assert.IsTrue(editRecordDigestMatches);
            Assert.AreEqual(1, changedTokenCount);

            return new SubtitleEvidence(
                MachineGeneratedSceneCount: inventory.Count(item =>
                    string.Equals(
                        item.SubtitleProvenance,
                        "machineGenerated",
                        StringComparison.Ordinal)),
                HumanModifiedSceneCount: inventory.Count(item =>
                    string.Equals(
                        item.SubtitleProvenance,
                        "humanModified",
                        StringComparison.Ordinal)),
                SourceRevisionIsMachine:
                    TranscriptEdit.SourceRevision.ChangeSource ==
                        TranscriptChangeSource.Machine,
                ChildRevisionIsHuman:
                    TranscriptEdit.Revision.ChangeSource ==
                        TranscriptChangeSource.Human,
                ParentRevisionMatches:
                    TranscriptEdit.Revision.ParentRevisionId ==
                        TranscriptEdit.SourceRevision.Id,
                SourceRevisionDigestMatches: sourceDigestMatches,
                ChildRevisionDigestMatches: revisionDigestMatches,
                EditRecordDigestMatches: editRecordDigestMatches,
                ChangedTokenCount: changedTokenCount);
        }

        private async Task<NegativeControlEvidence>
            VerifyBrokenDigestIsRejectedAsync()
        {
            SourceSpan original = SourceSpans[0];
            var broken = new SourceSpan(
                original.Id,
                original.AssetId,
                original.StreamId,
                original.Range,
                Digest("q005.synthetic.broken-source-digest.v1"),
                original.TranscriptRevisionId,
                original.TranscriptTokenIds);
            var brokenContext = new RenderGraphValidationContext(
                Project,
                Version,
                TimelineMap,
                TimelineMapDigest,
                Profile,
                Layout,
                LayoutDigest,
                Template,
                [broken, SourceSpans[1]],
                editorialRecordIds: [],
                ValidationContext.Artifacts,
                ValidationContext.Fonts);
            RenderGraphValidationResult graphResult =
                RenderGraphValidator.Validate(Graph, brokenContext);
            bool graphIssueDetected = graphResult.Issues.Any(issue =>
                issue.Kind == RenderGraphValidationIssueKind.OriginDigestMismatch);
            Assert.IsTrue(graphIssueDetected);

            int dependencySideEffectCount = 0;
            var service = new DeliveryService(
                (_, approvals) =>
                {
                    dependencySideEffectCount++;
                    return new ApprovalValidityResult(
                        approvals.ToArray(),
                        staleApprovals: []);
                },
                artifactId =>
                {
                    dependencySideEffectCount++;
                    return _registeredArtifacts.GetValueOrDefault(artifactId);
                },
                (artifactId, cancellationToken) =>
                {
                    dependencySideEffectCount++;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(_registeredArtifacts[artifactId]);
                });

            DeliveryException exception =
                await Assert.ThrowsExactlyAsync<DeliveryException>(() =>
                    service.CreateAsync(
                        Id<DeliveryManifestIdTag>(41),
                        Project,
                        Version,
                        RenderManifest,
                        brokenContext,
                        QualityControl,
                        [Approval],
                        _artifactEvidence));
            Assert.AreEqual(
                DeliveryFailureReason.ProvenanceInvalid,
                exception.Reason);
            Assert.AreEqual(0, dependencySideEffectCount);

            return new NegativeControlEvidence(
                "sourceSpanDigestMismatch",
                graphIssueDetected,
                exception.Reason.ToString(),
                dependencySideEffectCount,
                Detected: true);
        }

        private AuditEvidence VerifyAuditEvidence()
        {
            IReadOnlyList<StoredAuditEvent> records =
                _reviewStore.GetProjectAuditEvents(Project.Id);
            int canonicalRecordCount = 0;
            int contentLeakCount = 0;
            var recordKinds = new List<string>(records.Count);
            string[] forbiddenContent =
            [
                MachineTokenText,
                UnreviewedTokenText,
                ReviewedTokenText,
                Asset.FileName,
                VideoManifestArtifact.StorageReference,
                SubtitleManifestArtifact.StorageReference,
            ];

            foreach (StoredAuditEvent record in records)
            {
                if (string.Equals(
                    record.Reason,
                    CanonicalJson.Canonicalize(record.Reason),
                    StringComparison.Ordinal))
                {
                    canonicalRecordCount++;
                }

                contentLeakCount += forbiddenContent.Count(value =>
                    record.Reason.Contains(value, StringComparison.Ordinal));
                using JsonDocument document = JsonDocument.Parse(record.Reason);
                recordKinds.Add(document.RootElement
                    .GetProperty("recordKind")
                    .GetString()!);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    "transcriptCorrection",
                    "suggestionDecision",
                    "delivery",
                },
                recordKinds.ToArray());
            Assert.AreEqual(3, records.Count);
            Assert.AreEqual(records.Count, canonicalRecordCount);
            Assert.AreEqual(0, contentLeakCount);

            return new AuditEvidence(
                records.Count,
                canonicalRecordCount,
                contentLeakCount,
                recordKinds.AsReadOnly());
        }

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

    private sealed record TraceVerification(
        IReadOnlyList<UsedRangeEvidence> Inventory,
        int ProbeCount,
        int RoundTripCount,
        bool OutputEndRejected,
        bool ContiguousFromZero,
        bool IntervalPartitionProof,
        int UnresolvedOutputTimeCount,
        int UnresolvedOriginCount);

    private sealed record AcceptanceEvidence(
        string SchemaVersion,
        string Requirement,
        ScopeEvidence Scope,
        OriginResolutionEvidence OriginResolution,
        TimelineCoverageEvidence TimelineCoverage,
        ReverseTraceabilityEvidence ReverseTraceability,
        SubtitleEvidence SubtitleProvenance,
        DecisionEvidence DecisionEvidence,
        RightsEvidence Rights,
        DeliveryEvidence Delivery,
        AuditEvidence Audit,
        NegativeControlEvidence NegativeControl,
        bool Passed);

    private sealed record ScopeEvidence(
        string FixtureKind,
        bool ActualMediaUsed,
        bool ActualModelInferenceUsed,
        bool HumanAcceptanceClaimed);

    private sealed record OriginResolutionEvidence(
        IReadOnlyList<string> TimelineOriginKinds,
        bool AssetResolvedThroughSourceSpan,
        bool DirectAssetTimelineOriginClaimed,
        bool EditorialRecordAggregateAvailable,
        bool EditorialRecordEvidenceClaimed);

    private sealed record TimelineCoverageEvidence(
        string OutputDuration,
        int EntryCount,
        bool ContiguousFromZero,
        bool IntervalPartitionProof,
        int RepresentativeOutputTimeCount,
        int ReverseRoundTripCount,
        bool OutputEndRejected,
        int UnresolvedOutputTimeCount);

    private sealed record ReverseTraceabilityEvidence(
        int SourceSpanResolutionCount,
        int UsedSourceSpanCount,
        int UnresolvedOriginCount,
        IReadOnlyList<UsedRangeEvidence> UsedRangeInventory);

    private sealed record UsedRangeEvidence(
        string OutputStart,
        string OutputDuration,
        string SourceStart,
        string SourceDuration,
        string OriginKind,
        string SubtitleProvenance,
        string DecisionEvidence,
        string RightsStatus,
        bool ExternalPublicationAllowed);

    private sealed record SubtitleEvidence(
        int MachineGeneratedSceneCount,
        int HumanModifiedSceneCount,
        bool SourceRevisionIsMachine,
        bool ChildRevisionIsHuman,
        bool ParentRevisionMatches,
        bool SourceRevisionDigestMatches,
        bool ChildRevisionDigestMatches,
        bool EditRecordDigestMatches,
        int ChangedTokenCount);

    private sealed record DecisionEvidence(
        int AdoptedSuggestionSceneCount,
        int TranscriptCorrectionSceneCount,
        bool SyntheticModelRunContractBound,
        bool HumanDecisionBound);

    private sealed record RightsEvidence(
        int UsedAssetCount,
        int ApprovedUsedAssetCount,
        bool EvidenceReferencePresent,
        bool ExternalPublicationAllowed,
        int UnresolvedRightsCount);

    private sealed record DeliveryEvidence(
        bool ManifestVersionMatches,
        bool CompositionDigestMatches,
        bool TimelineDigestMatches,
        bool UsedSourceSpanDigestStableAcrossInputOrder,
        bool ApprovalEvidenceDigestStableAcrossInputOrder,
        bool DeliveryEvidenceDigestStableAcrossInputOrder,
        int ArtifactCount);

    private sealed record AuditEvidence(
        int RecordCount,
        int CanonicalRecordCount,
        int ContentLeakCount,
        IReadOnlyList<string> RecordKinds);

    private sealed record NegativeControlEvidence(
        string Scenario,
        bool RenderGraphIssueDetected,
        string ExpectedDeliveryFailure,
        int DependencySideEffectCount,
        bool Detected);
}