using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Graphics;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class EmphasisCueServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2044, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [TestMethod]
    public void AdoptedCandidateCreatesGroundedGraphicRevisionWithDecisionEvidence()
    {
        Fixture fixture = Fixture.Create();
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");

        EmphasisCueHistory history = EmphasisCueService.CreateFromAdoption(
            fixture.Composition,
            adoption,
            fixture.TopicMetadata,
            fixture.Context(),
            SubtitleCueStyle.Emphasis,
            RevisionId(1),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Create a reviewed emphasis cue.");

        EmphasisCueRevision revision = history.CurrentRevision;
        Assert.AreEqual(EmphasisCueSourceKind.AdoptedSuggestion, history.SourceKind);
        Assert.AreEqual(fixture.Composition.ProjectId, history.ProjectId);
        Assert.AreEqual(adoption.OriginalCandidate.Id, history.SourceSuggestionId);
        Assert.AreEqual(adoption.Reference, history.AdoptedSuggestion);
        Assert.AreEqual(fixture.ExactCandidate.SourceRevisionDigest, history.SourceRevisionDigest);
        Assert.HasCount(1, history.Revisions);
        Assert.IsNull(revision.ParentRevisionId);
        Assert.AreEqual(fixture.Composition.Id, revision.CompositionVersionId);
        Assert.AreEqual(fixture.TopicMetadata.Id, revision.TopicMetadataRevisionId);
        Assert.AreEqual(fixture.Layout.Id, revision.LayoutSpecId);
        Assert.AreEqual(fixture.Profile.VersionDigest, revision.LayoutProfileDigest);
        Assert.AreEqual(EmphasisCueChangeKind.Initial, revision.Change.Kind);
        Assert.AreEqual(adoption.Reference.Origin, revision.Change.Origin);
        Assert.AreEqual(
            EmphasisCueSnapshotJson.ComputeContentDigest(revision.Snapshot),
            revision.ContentDigest);

        EmphasisCueSnapshot snapshot = revision.Snapshot;
        Assert.AreSame(adoption.Cue, snapshot.Cue);
        Assert.AreEqual(CueKind.Graphic, snapshot.Cue.Kind);
        Assert.AreEqual("重要です", snapshot.Text);
        Assert.AreEqual(SubtitleCueStyle.Emphasis, snapshot.Style);
        Assert.AreEqual(adoption.Reference.Origin, snapshot.TextOrigin);
        Assert.AreEqual(adoption.Reference, snapshot.AdoptedSuggestion);
        Assert.AreEqual(EmphasisTextSourceKind.OriginalTranscript, snapshot.TextSource);
    }

    [TestMethod]
    public void ManualCreationUsesEditorialOriginAndCannotClaimAQuote()
    {
        Fixture fixture = Fixture.Create();
        OriginRef editorial = EditorialOrigin(1);

        EmphasisCueHistory history = EmphasisCueService.CreateManual(
            fixture.Composition,
            CueId(1),
            Range(10, 1),
            "graphic",
            "編集部テロップ",
            SubtitleCueStyle.Caution,
            editorial,
            fixture.TopicMetadata,
            fixture.ManualContext(),
            RevisionId(1),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Create a manual caution cue.");

        Assert.AreEqual(EmphasisCueSourceKind.ManualEditorial, history.SourceKind);
        Assert.IsNull(history.SourceSuggestionId);
        Assert.IsNull(history.AdoptedSuggestion);
        Assert.IsNull(history.SourceRevisionDigest);
        Assert.AreEqual(editorial, history.CurrentRevision.Snapshot.TextOrigin);
        Assert.IsNull(history.CurrentRevision.Snapshot.TextSource);

        AssertFailure(
            () => EmphasisCueService.CreateManual(
                fixture.Composition,
                CueId(2),
                Range(10, 1),
                "graphic",
                "根拠なし引用",
                SubtitleCueStyle.Quote,
                EditorialOrigin(2),
                fixture.TopicMetadata,
                fixture.ManualContext(),
                RevisionId(2),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Do not create an ungrounded quote."),
            EmphasisCueFailureReason.UngroundedQuote);
    }

    [TestMethod]
    public void QuoteRequiresExactOriginalCandidateAndRejectsManualSummary()
    {
        Fixture fixture = Fixture.Create(includeManualSummary: true);
        HighlightSuggestionAdoptionResult exact = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");
        HighlightSuggestionAdoptionResult summary = fixture.Adopt(
            fixture.ManualSummaryCandidate!,
            CueId(2),
            Range(10, 1),
            "graphic");

        EmphasisCueHistory quote = EmphasisCueService.CreateFromAdoption(
            fixture.Composition,
            exact,
            fixture.TopicMetadata,
            fixture.Context(),
            SubtitleCueStyle.Quote,
            RevisionId(1),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Create an exact grounded quote.");

        Assert.AreEqual(SubtitleCueStyle.Quote, quote.CurrentRevision.Snapshot.Style);
        AssertFailure(
            () => EmphasisCueService.CreateFromAdoption(
                fixture.Composition,
                summary,
                fixture.TopicMetadata,
                fixture.Context(),
                SubtitleCueStyle.Quote,
                RevisionId(2),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Do not quote a manual summary as spoken text."),
            EmphasisCueFailureReason.UngroundedQuote);
    }

    [TestMethod]
    public void TimingMustStayInsideMappedEvidenceAndTopicWithoutInventedTolerance()
    {
        Fixture fixture = Fixture.Create();

        foreach (TimeRange invalid in new[]
        {
            Range(9, 2),
            Range(10, 3),
            Range(12, 1),
        })
        {
            HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
                fixture.ExactCandidate,
                CueId(invalid.Start.Value + 20),
                invalid,
                "graphic");
            AssertFailure(
                () => EmphasisCueService.CreateFromAdoption(
                    fixture.Composition,
                    adoption,
                    fixture.TopicMetadata,
                    fixture.Context(),
                    SubtitleCueStyle.Emphasis,
                    RevisionId(invalid.Start.Value + 20),
                    ActorSid,
                    CreatedAt.AddMinutes(2),
                    "Reject timing outside exact mapped evidence."),
                EmphasisCueFailureReason.TimingOutsideEvidence);
        }

        HighlightSuggestionAdoptionResult boundary = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(99),
            Range(10, 2),
            "graphic");
        EmphasisCueHistory accepted = EmphasisCueService.CreateFromAdoption(
            fixture.Composition,
            boundary,
            fixture.TopicMetadata,
            fixture.Context(),
            SubtitleCueStyle.Emphasis,
            RevisionId(99),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Accept exact evidence boundaries.");
        Assert.AreEqual(Range(10, 2), accepted.CurrentRevision.Snapshot.Cue.Range);
    }

    [TestMethod]
    public void ExactVisualOverlapIsRejectedWhileBoundaryTouchIsAllowed()
    {
        Fixture fixture = Fixture.Create();
        Cue overlappingSubtitle = new(
            CueId(50),
            CueKind.Subtitle,
            Range(10, 1),
            "subtitle-overlap");
        Cue touchingSubtitle = new(
            CueId(51),
            CueKind.Subtitle,
            Range(11, 1),
            "subtitle-overlap");
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");

        AssertFailure(
            () => EmphasisCueService.CreateFromAdoption(
                fixture.Composition,
                adoption,
                fixture.TopicMetadata,
                fixture.Context(existingCues: [overlappingSubtitle]),
                SubtitleCueStyle.Emphasis,
                RevisionId(1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Reject exact visual overlap."),
            EmphasisCueFailureReason.VisualConflict);

        EmphasisCueHistory accepted = EmphasisCueService.CreateFromAdoption(
            fixture.Composition,
            adoption,
            fixture.TopicMetadata,
            fixture.Context(existingCues: [touchingSubtitle]),
            SubtitleCueStyle.Emphasis,
            RevisionId(1),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Allow boundary-touching cues.");
        Assert.AreEqual("graphic", accepted.CurrentRevision.Snapshot.Cue.RegionName);
    }

    [TestMethod]
    public void NewCueCannotReuseAnExistingCueIdentityEvenWithoutOverlap()
    {
        Fixture fixture = Fixture.Create();
        Cue existing = new(
            CueId(1),
            CueKind.Graphic,
            Range(11, 1),
            "graphic-alt");
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");

        AssertFailure(
            () => fixture.CreateFromAdoption(
                adoption,
                fixture.Context(existingCues: [existing])),
            EmphasisCueFailureReason.VisualConflict);
    }

    [TestMethod]
    public void LayoutRequiresGraphicRegionSafeZoneAndNoProhibitedOverlap()
    {
        Fixture fixture = Fixture.Create();
        HighlightSuggestionAdoptionResult wrongRole = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "video");
        AssertFailure(
            () => fixture.CreateFromAdoption(wrongRole, fixture.Context()),
            EmphasisCueFailureReason.InvalidGraphicRegion);

        HighlightSuggestionAdoptionResult valid = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(2),
            Range(10, 1),
            "graphic");
        AssertFailure(
            () => fixture.CreateFromAdoption(
                valid,
                fixture.Context(
                    prohibitedRegions:
                    [
                        new SubtitleProhibitedRegion(
                            "platform-controls",
                            fixture.GraphicRect),
                    ])),
            EmphasisCueFailureReason.ProhibitedRegionOverlap);

        SubtitleLayoutProfile missingSafeZone = fixture.CreateProfile(
            ["safe-frame", "missing-platform-zone"]);
        AssertFailure(
            () => fixture.CreateFromAdoption(
                valid,
                fixture.Context(profile: missingSafeZone)),
            EmphasisCueFailureReason.LayoutBindingMismatch);
    }

    [TestMethod]
    public void StaleSourceTopicAndAdoptionBindingsFailClosedWithoutContentDisclosure()
    {
        Fixture fixture = Fixture.Create();
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");
        EmphasisCuePlacementContext staleDigest = fixture.Context(
            currentSourceRevisionDigest: Digest('f'));
        Fixture staleTopic = Fixture.Create(topicOrdinal: 2);

        EmphasisCueException digestFailure = AssertFailure(
            () => fixture.CreateFromAdoption(adoption, staleDigest),
            EmphasisCueFailureReason.SourceCandidateStale);
        AssertFailure(
            () => EmphasisCueService.CreateFromAdoption(
                fixture.Composition,
                adoption,
                staleTopic.TopicMetadata,
                fixture.Context(),
                SubtitleCueStyle.Emphasis,
                RevisionId(1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "private-topic-mismatch"),
            EmphasisCueFailureReason.TopicBindingMismatch);
        Assert.IsFalse(digestFailure.ToString().Contains(
            fixture.ExactCandidate.Text,
            StringComparison.Ordinal));
        Assert.IsNull(digestFailure.InnerException);
    }

    [TestMethod]
    public void CueRevisionCannotPredateItsAdoptionDecision()
    {
        Fixture fixture = Fixture.Create();
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");

        AssertFailure(
            () => EmphasisCueService.CreateFromAdoption(
                fixture.Composition,
                adoption,
                fixture.TopicMetadata,
                fixture.Context(),
                SubtitleCueStyle.Emphasis,
                RevisionId(1),
                ActorSid,
                adoption.DecisionRecord.DecidedAt.AddTicks(-1),
                "Do not create a revision before adoption."),
            EmphasisCueFailureReason.RevisionConflict);
    }

    [TestMethod]
    public void TypedSingleFieldEditsCreateChildrenAndPreserveOriginalHistory()
    {
        Fixture fixture = Fixture.Create();
        EmphasisCueHistory initial = fixture.CreateInitial();
        CompositionVersion composition = fixture.Composition;

        EmphasisCueHistory text = fixture.Revise(
            initial,
            composition = fixture.ChildComposition(composition, 2),
            RevisionId(2),
            EmphasisCueEdit.ChangeText("編集した強調", EditorialOrigin(10)),
            fixture.Context());
        EmphasisCueHistory timing = fixture.Revise(
            text,
            composition = fixture.ChildComposition(composition, 3),
            RevisionId(3),
            EmphasisCueEdit.ChangeTiming(Range(10, 2), EditorialOrigin(11)),
            fixture.Context());
        EmphasisCueHistory style = fixture.Revise(
            timing,
            composition = fixture.ChildComposition(composition, 4),
            RevisionId(4),
            EmphasisCueEdit.ChangeStyle(SubtitleCueStyle.Caution, EditorialOrigin(12)),
            fixture.Context());
        EmphasisCueHistory region = fixture.Revise(
            style,
            composition = fixture.ChildComposition(composition, 5),
            RevisionId(5),
            EmphasisCueEdit.ChangeRegion("graphic-alt", EditorialOrigin(13)),
            fixture.Context());

        Assert.HasCount(5, region.Revisions);
        CollectionAssert.AreEqual(
            new[]
            {
                EmphasisCueChangeKind.Initial,
                EmphasisCueChangeKind.Text,
                EmphasisCueChangeKind.Timing,
                EmphasisCueChangeKind.Style,
                EmphasisCueChangeKind.Region,
            },
            region.Revisions.Select(static revision => revision.Change.Kind).ToArray());
        Assert.AreEqual("編集した強調", region.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(Range(10, 2), region.CurrentRevision.Snapshot.Cue.Range);
        Assert.AreEqual(SubtitleCueStyle.Caution, region.CurrentRevision.Snapshot.Style);
        Assert.AreEqual("graphic-alt", region.CurrentRevision.Snapshot.Cue.RegionName);
        Assert.AreEqual("重要です", initial.CurrentRevision.Snapshot.Text);
        Assert.HasCount(1, initial.Revisions);
        for (int index = 1; index < region.Revisions.Count; index++)
        {
            Assert.AreEqual(
                region.Revisions[index - 1].Id,
                region.Revisions[index].ParentRevisionId);
            Assert.AreEqual(
                region.Revisions[index - 1].CompositionVersionId,
                region.Revisions[index].ParentCompositionVersionId);
        }
    }

    [TestMethod]
    public void QuoteTextCannotBeChangedAndManualTextCannotBeRestyledAsQuote()
    {
        Fixture fixture = Fixture.Create();
        EmphasisCueHistory quote = fixture.CreateInitial(style: SubtitleCueStyle.Quote);
        CompositionVersion quoteChild = fixture.ChildComposition(fixture.Composition, 2);
        AssertFailure(
            () => fixture.Revise(
                quote,
                quoteChild,
                RevisionId(2),
                EmphasisCueEdit.ChangeText("言っていない引用", EditorialOrigin(20)),
                fixture.Context()),
            EmphasisCueFailureReason.UngroundedQuote);

        EmphasisCueHistory manual = EmphasisCueService.CreateManual(
            fixture.Composition,
            CueId(20),
            Range(10, 1),
            "graphic",
            "手動テロップ",
            SubtitleCueStyle.Emphasis,
            EditorialOrigin(21),
            fixture.TopicMetadata,
            fixture.ManualContext(),
            RevisionId(20),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Create manual emphasis.");
        AssertFailure(
            () => fixture.Revise(
                manual,
                fixture.ChildComposition(fixture.Composition, 21),
                RevisionId(21),
                EmphasisCueEdit.ChangeStyle(
                    SubtitleCueStyle.Quote,
                    EditorialOrigin(22)),
                fixture.ManualContext()),
            EmphasisCueFailureReason.UngroundedQuote);
    }

    [TestMethod]
    public void NoOpDuplicateRevisionAndNonChildCompositionFailClosed()
    {
        Fixture fixture = Fixture.Create();
        EmphasisCueHistory initial = fixture.CreateInitial();
        CompositionVersion child = fixture.ChildComposition(fixture.Composition, 2);

        AssertFailure(
            () => fixture.Revise(
                initial,
                child,
                RevisionId(2),
                EmphasisCueEdit.ChangeStyle(
                    initial.CurrentRevision.Snapshot.Style,
                    EditorialOrigin(30)),
                fixture.Context()),
            EmphasisCueFailureReason.NoChange);
        AssertFailure(
            () => fixture.Revise(
                initial,
                child,
                RevisionId(2),
                EmphasisCueEdit.ChangeText(
                    initial.CurrentRevision.Snapshot.Text,
                    EditorialOrigin(33)),
                fixture.Context()),
            EmphasisCueFailureReason.NoChange);
        AssertFailure(
            () => fixture.Revise(
                initial,
                child,
                initial.CurrentRevision.Id,
                EmphasisCueEdit.ChangeStyle(
                    SubtitleCueStyle.Caution,
                    EditorialOrigin(31)),
                fixture.Context()),
            EmphasisCueFailureReason.RevisionConflict);
        AssertFailure(
            () => fixture.Revise(
                initial,
                fixture.Composition,
                RevisionId(2),
                EmphasisCueEdit.ChangeStyle(
                    SubtitleCueStyle.Caution,
                    EditorialOrigin(32)),
                fixture.Context()),
            EmphasisCueFailureReason.CompositionVersionConflict);
    }

    [TestMethod]
    public void CancellationImmutabilityAndPublicSurfaceRemainTyped()
    {
        Fixture fixture = Fixture.Create();
        HighlightSuggestionAdoptionResult adoption = fixture.Adopt(
            fixture.ExactCandidate,
            CueId(1),
            Range(10, 1),
            "graphic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            EmphasisCueService.CreateFromAdoption(
                fixture.Composition,
                adoption,
                fixture.TopicMetadata,
                fixture.Context(),
                SubtitleCueStyle.Emphasis,
                RevisionId(1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Cancelled cue creation.",
                cancellation.Token));

        EmphasisCueHistory history = fixture.CreateInitial();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EmphasisCueRevision>)history.Revisions).Clear());
        foreach (Type type in new[]
        {
            typeof(EmphasisCueSnapshot),
            typeof(EmphasisCuePlacementContext),
            typeof(EmphasisCueRevision),
            typeof(EmphasisCueHistory),
            typeof(EmphasisCueException),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "CreateFromAdoption",
                "CreateManual",
                "Revise",
            },
            typeof(EmphasisCueService).GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(EmphasisCueService).GetMembers()
            .Any(static member =>
                member.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Maximum", StringComparison.OrdinalIgnoreCase)));
    }

    private static EmphasisCueException AssertFailure(
        Action action,
        EmphasisCueFailureReason expectedReason)
    {
        EmphasisCueException exception = Assert.ThrowsExactly<EmphasisCueException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static EntityId<CueIdTag> CueId(long value) =>
        Id<CueIdTag>(0x75000001, value);

    private static EntityId<EmphasisCueRevisionIdTag> RevisionId(long value) =>
        Id<EmphasisCueRevisionIdTag>(0x75000002, value);

    private static OriginRef EditorialOrigin(long value) =>
        OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(0x75000003, value));

    private static ContentDigest Digest(char fill) => new(new string(fill, 64));

    private static RationalTime Time(long value, long rate = 1) => new(value, rate);

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(Time(start, rate), Time(duration, rate));

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private sealed class Fixture
    {
        private readonly Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>
            _compositions = [];

        private Fixture(
            Project project,
            TranscriptRevision transcript,
            IReadOnlyList<SourceSpan> sourceSpans,
            TopicMetadataRevision topicMetadata,
            EmphasisCandidate exactCandidate,
            EmphasisCandidate? manualSummaryCandidate,
            LayoutSpec layout,
            SubtitleLayoutProfile profile,
            CompositionVersion composition,
            NormalizedRect graphicRect)
        {
            Project = project;
            Transcript = transcript;
            SourceSpans = sourceSpans;
            TopicMetadata = topicMetadata;
            ExactCandidate = exactCandidate;
            ManualSummaryCandidate = manualSummaryCandidate;
            Layout = layout;
            Profile = profile;
            Composition = composition;
            GraphicRect = graphicRect;
            _compositions.Add(composition.Id, composition);
        }

        internal Project Project { get; }
        internal TranscriptRevision Transcript { get; }
        internal IReadOnlyList<SourceSpan> SourceSpans { get; }
        internal TopicMetadataRevision TopicMetadata { get; }
        internal EmphasisCandidate ExactCandidate { get; }
        internal EmphasisCandidate? ManualSummaryCandidate { get; }
        internal LayoutSpec Layout { get; }
        internal SubtitleLayoutProfile Profile { get; }
        internal CompositionVersion Composition { get; }
        internal NormalizedRect GraphicRect { get; }

        internal HighlightSuggestionAdoptionResult Adopt(
            EmphasisCandidate candidate,
            EntityId<CueIdTag> cueId,
            TimeRange outputRange,
            string regionName)
        {
            EmphasisSuggestionCandidate registered = SuggestionService.Register(candidate, []);
            return SuggestionService.AdoptHighlight(
                registered,
                registered.SourceDigest,
                cueId,
                outputRange,
                regionName,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Adopt reviewed emphasis candidate.");
        }

        internal EmphasisCueHistory CreateInitial(
            SubtitleCueStyle style = SubtitleCueStyle.Emphasis)
        {
            HighlightSuggestionAdoptionResult adoption = Adopt(
                ExactCandidate,
                CueId(1),
                Range(10, 1),
                "graphic");
            return EmphasisCueService.CreateFromAdoption(
                Composition,
                adoption,
                TopicMetadata,
                Context(),
                style,
                RevisionId(1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Create initial emphasis cue.");
        }

        internal EmphasisCueHistory CreateFromAdoption(
            HighlightSuggestionAdoptionResult adoption,
            EmphasisCuePlacementContext context) =>
            EmphasisCueService.CreateFromAdoption(
                Composition,
                adoption,
                TopicMetadata,
                context,
                SubtitleCueStyle.Emphasis,
                RevisionId(1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Create emphasis cue.");

        internal EmphasisCueHistory Revise(
            EmphasisCueHistory history,
            CompositionVersion child,
            EntityId<EmphasisCueRevisionIdTag> revisionId,
            EmphasisCueEdit edit,
            EmphasisCuePlacementContext context) =>
            EmphasisCueService.Revise(
                history,
                child,
                TopicMetadata,
                context,
                revisionId,
                edit,
                ActorSid,
                child.CreatedAt,
                "Revise emphasis cue.");

        internal EmphasisCuePlacementContext Context(
            IReadOnlyList<Cue>? existingCues = null,
            IReadOnlyList<SubtitleProhibitedRegion>? prohibitedRegions = null,
            SubtitleLayoutProfile? profile = null,
            ContentDigest? currentSourceRevisionDigest = null) =>
            new(
                Layout,
                profile ?? Profile,
                topicOutputRange: Range(10, 2),
                evidenceOutputRange: Range(10, 2),
                currentSourceRevisionDigest ?? ExactCandidate.SourceRevisionDigest,
                existingCues ?? [],
                prohibitedRegions ?? []);

        internal EmphasisCuePlacementContext ManualContext() =>
            new(
                Layout,
                Profile,
                topicOutputRange: Range(10, 2),
                evidenceOutputRange: null,
                currentSourceRevisionDigest: null,
                existingCues: [],
                prohibitedRegions: []);

        internal SubtitleLayoutProfile CreateProfile(
            IReadOnlyList<string> requiredSafeZoneNames) =>
            new(
                Layout.Id,
                Digest('b'),
                requiredSafeZoneNames,
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(static kind => new SubtitleSoftConstraintWeight(kind, 0))
                    .ToArray());

        internal CompositionVersion ChildComposition(
            CompositionVersion parent,
            int value)
        {
            var child = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x75000004, value),
                parent.ProjectId,
                parent.Id,
                ActorSid,
                CreatedAt.AddMinutes(value + 1),
                $"Create child composition {value}.",
                EmptyCompositionDigest(),
                []);
            _compositions.Add(child.Id, child);
            return child;
        }

        internal static Fixture Create(
            bool includeManualSummary = false,
            int topicOrdinal = 1)
        {
            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(0x75000005, topicOrdinal);
            var project = new Project(
                projectId,
                "Emphasis cue test",
                "Verify reviewed emphasis cue placement.",
                "Reviewers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(0x75000006, topicOrdinal);
            EntityId<TranscriptTokenIdTag> tokenId =
                Id<TranscriptTokenIdTag>(0x75000007, topicOrdinal);
            var token = new Token(tokenId, "重要です", Range(0, 2), null);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(0x75000008, topicOrdinal),
                token.Text,
                [token]);
            EntityId<SourceSpanIdTag> spanId =
                Id<SourceSpanIdTag>(0x75000009, topicOrdinal);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(0x7500000a, topicOrdinal),
                spanId,
                Id<TranscriptSpeakerIdTag>(0x7500000b, topicOrdinal),
                "Speaker",
                token.Text,
                Range(0, 2),
                null,
                [sentence]);
            var transcript = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [utterance]);
            var sourceSpan = new SourceSpan(
                spanId,
                Id<AssetIdTag>(0x7500000c, topicOrdinal),
                "audio:1",
                Range(0, 2),
                Digest('a'),
                revisionId,
                [tokenId]);
            EntityId<ModelRunIdTag> modelRunId = Id<ModelRunIdTag>(0x7500000d, topicOrdinal);
            TopicBoundaryScanResult boundary = TopicBoundaryService.FindCandidates(
                transcript,
                [sourceSpan],
                modelRunId);
            TopicRangeRevision topicRange = TopicBoundaryService.CreateTopicRange(
                boundary,
                Id<TopicRangeIdTag>(0x7500000e, topicOrdinal),
                Id<TopicRangeRevisionIdTag>(0x7500000f, topicOrdinal),
                startBoundarySuggestionId: null,
                endBoundarySuggestionId: null,
                Range(0, 2),
                ActorSid,
                CreatedAt,
                "Create exact topic range.");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(
                project,
                transcript,
                [sourceSpan],
                topicRange,
                speakerAssignments: null,
                topicOrdinal,
                Id<TopicMetadataIdTag>(0x75000010, topicOrdinal),
                Id<TopicMetadataRevisionIdTag>(0x75000011, topicOrdinal),
                ActorSid,
                CreatedAt,
                "Create topic metadata.");
            metadata = TopicMetadataService.SetImportantStatements(
                project,
                transcript,
                [sourceSpan],
                topicRange,
                null,
                metadata,
                Id<TopicMetadataRevisionIdTag>(0x75000012, topicOrdinal),
                [sourceSpan.Id],
                ActorSid,
                CreatedAt.AddTicks(1),
                "Select important statement.");
            if (includeManualSummary)
            {
                metadata = TopicMetadataService.SetSummary(
                    project,
                    transcript,
                    [sourceSpan],
                    topicRange,
                    null,
                    metadata,
                    Id<TopicMetadataRevisionIdTag>(0x75000013, topicOrdinal),
                    "人が書いた要約",
                    ActorSid,
                    CreatedAt.AddTicks(2),
                    "Add manual summary.");
            }

            TermScope termScope = TermScope.ForProject(project.Id);
            EmphasisCandidateScanResult scan = EmphasisCandidateService.FindCandidates(
                transcript,
                [sourceSpan],
                metadata,
                dictionaryEntries: [],
                termScope,
                productTermEntryIds: [],
                modelRunId);
            EmphasisCandidate exact = scan.Candidates.Single(candidate =>
                candidate.Reasons.Contains(EmphasisReasonKind.ImportantStatement));
            EmphasisCandidate? summary = scan.Candidates.SingleOrDefault(candidate =>
                candidate.TextSource == EmphasisTextSourceKind.ManualSummary);

            NormalizedRect graphicRect = Rect(0, 1, 1, 2, 1, 1, 1, 2);
            var layout = new LayoutSpec(
                Id<LayoutSpecIdTag>(0x75000014, topicOrdinal),
                16,
                9,
                [
                    new LayoutRegion("video", FullRect(), LayoutRegionRole.Video),
                    new LayoutRegion("graphic", graphicRect, LayoutRegionRole.Graphic),
                    new LayoutRegion("graphic-alt", Rect(0, 1, 0, 1, 1, 2, 1, 2), LayoutRegionRole.Graphic),
                    new LayoutRegion("subtitle-overlap", graphicRect, LayoutRegionRole.Subtitle),
                ],
                [new SafeZone("safe-frame", FullRect())]);
            var profile = new SubtitleLayoutProfile(
                layout.Id,
                Digest('b'),
                ["safe-frame"],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(static kind => new SubtitleSoftConstraintWeight(kind, 0))
                    .ToArray());
            var composition = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x75000004, 1),
                project.Id,
                null,
                ActorSid,
                CreatedAt,
                "Create root composition.",
                EmptyCompositionDigest(),
                []);
            return new Fixture(
                project,
                transcript,
                [sourceSpan],
                metadata,
                exact,
                summary,
                layout,
                profile,
                composition,
                graphicRect);
        }

        private static ContentDigest EmptyCompositionDigest() =>
            ContentDigest.Compute(Encoding.UTF8.GetBytes("{\"elements\":[]}"));

        private static NormalizedRect FullRect() =>
            Rect(0, 1, 0, 1, 1, 1, 1, 1);

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
                new RationalNumber(xNumerator, xDenominator),
                new RationalNumber(yNumerator, yDenominator),
                new RationalNumber(widthNumerator, widthDenominator),
                new RationalNumber(heightNumerator, heightDenominator));
    }
}