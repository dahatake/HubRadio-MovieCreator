using System.Numerics;
using System.Reflection;
using System.Text;
using HubRadio.App.Compositions;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SubtitleServiceTests
{
    private const string ActorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-4001";

    private static readonly DateTimeOffset CreatedAt =
        new(2044, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void CreateInitialBindsResolvedLayoutSourceCompositionAndCanonicalContent()
    {
        Fixture fixture = Fixture.Create();

        SubtitleCueHistory history = SubtitleService.CreateInitial(
            fixture.Composition,
            fixture.SourceCandidate,
            fixture.ResolvedLayout,
            CueId(1),
            RevisionId(1),
            Range(10, 2),
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Create the initial subtitle cue.");

        SubtitleCueRevision revision = history.CurrentRevision;
        Assert.AreSame(fixture.SourceCandidate, history.SourceCandidate);
        Assert.AreEqual(fixture.SourceCandidate.Id, history.SourceCandidateId);
        Assert.AreEqual(fixture.Composition.ProjectId, history.ProjectId);
        Assert.AreEqual(CueId(1), history.CueId);
        Assert.HasCount(1, history.Revisions);
        Assert.AreEqual(RevisionId(1), revision.Id);
        Assert.IsNull(revision.ParentRevisionId);
        Assert.AreEqual(fixture.Composition.Id, revision.CompositionVersionId);
        Assert.AreEqual(
            fixture.Composition.ContentDigest,
            revision.CompositionVersionDigest);
        Assert.AreEqual(fixture.SourceCandidate.Id, revision.SourceCandidateId);
        Assert.AreEqual(
            fixture.SourceCandidate.SourceRevisionId,
            revision.SourceRevisionId);
        Assert.AreEqual(
            fixture.SourceCandidate.SourceRevisionDigest,
            revision.SourceRevisionDigest);
        Assert.AreEqual(fixture.SourceCandidate.Origin, revision.SourceOrigin);
        Assert.AreEqual(ActorSid, revision.ActorSid);
        Assert.AreEqual(CreatedAt.AddMinutes(1), revision.CreatedAt);
        Assert.AreEqual("Create the initial subtitle cue.", revision.Reason);
        Assert.AreEqual(SubtitleCueChangeKind.Initial, revision.Change.Kind);
        Assert.IsNull(revision.Change.Previous);
        Assert.AreSame(revision.Snapshot, revision.Change.Current);
        Assert.AreEqual(fixture.SourceCandidate.Origin, revision.Change.Origin);

        SubtitleCueSnapshot snapshot = revision.Snapshot;
        Assert.AreEqual(CueKind.Subtitle, snapshot.Cue.Kind);
        Assert.AreEqual(CueId(1), snapshot.Cue.Id);
        Assert.AreEqual(Range(10, 2), snapshot.Cue.Range);
        Assert.AreEqual("subtitle", snapshot.Cue.RegionName);
        Assert.AreEqual(fixture.SourceCandidate.Text, snapshot.Text);
        Assert.AreEqual(SubtitleCueStyle.Normal, snapshot.Style);
        Assert.AreEqual(fixture.SourceCandidate.Origin, snapshot.TextOrigin);
        Assert.AreSame(fixture.ResolvedLayout, snapshot.LayoutResolution);
        Assert.AreEqual(
            fixture.ResolvedLayout.Plan!.Placement,
            snapshot.ResolvedPlacement);
        Assert.IsFalse(snapshot.RequiresManualLayout);
        Assert.AreEqual(
            SubtitleSnapshotJson.ComputeContentDigest(snapshot),
            revision.ContentDigest);
    }

    [TestMethod]
    public void CreateInitialPreservesNoSolutionForManualNavigationWithoutRelaxingHardFailures()
    {
        Fixture fixture = Fixture.Create();

        SubtitleCueHistory history = SubtitleService.CreateInitial(
            fixture.Composition,
            fixture.SourceCandidate,
            fixture.ManualLayout,
            CueId(1),
            RevisionId(1),
            Range(10, 2),
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Retain the unresolved subtitle layout.");

        SubtitleCueSnapshot snapshot = history.CurrentRevision.Snapshot;
        Assert.IsTrue(snapshot.RequiresManualLayout);
        Assert.IsNull(snapshot.ResolvedPlacement);
        Assert.IsNull(snapshot.Cue.RegionName);
        Assert.AreEqual(
            SubtitleLayoutResolutionStatus.ManualAdjustmentRequired,
            snapshot.LayoutResolution.Status);
        CollectionAssert.Contains(
            snapshot.LayoutResolution.Evaluations.Single().HardConstraints.ToArray(),
            SubtitleHardConstraintKind.ProhibitedRegionOverlap);
    }

    [TestMethod]
    public void TextEditCreatesEditorialChildOnFreshCompositionAndLeavesParentImmutable()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        SubtitleCueRevision parent = initial.CurrentRevision;
        CompositionVersion childComposition = fixture.ChildComposition(
            fixture.Composition,
            2);
        OriginRef editorial = EditorialOrigin(1);

        SubtitleCueHistory edited = SubtitleService.SetText(
            initial,
            childComposition,
            fixture.SourceCandidate,
            RevisionId(2),
            "修正した字幕",
            editorial,
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Correct the subtitle wording.");

        SubtitleCueRevision child = edited.CurrentRevision;
        Assert.HasCount(2, edited.Revisions);
        Assert.AreSame(parent, edited.Revisions[0]);
        Assert.AreEqual(parent.Id, child.ParentRevisionId);
        Assert.AreEqual(childComposition.Id, child.CompositionVersionId);
        Assert.AreEqual(
            fixture.Composition.Id,
            child.ParentCompositionVersionId);
        Assert.AreEqual(SubtitleCueChangeKind.Text, child.Change.Kind);
        Assert.AreSame(parent.Snapshot, child.Change.Previous);
        Assert.AreSame(child.Snapshot, child.Change.Current);
        Assert.AreEqual(editorial, child.Change.Origin);
        Assert.AreEqual("修正した字幕", child.Snapshot.Text);
        Assert.AreEqual(editorial, child.Snapshot.TextOrigin);
        Assert.AreEqual(parent.Snapshot.Cue, child.Snapshot.Cue);
        Assert.AreEqual(parent.Snapshot.Style, child.Snapshot.Style);
        Assert.AreSame(
            parent.Snapshot.LayoutResolution,
            child.Snapshot.LayoutResolution);
        Assert.AreNotEqual(parent.ContentDigest, child.ContentDigest);
        Assert.AreEqual(fixture.SourceCandidate.Text, parent.Snapshot.Text);
        Assert.AreEqual(
            fixture.SourceCandidate.Origin,
            parent.Snapshot.TextOrigin);
        Assert.HasCount(1, initial.Revisions);
    }

    [TestMethod]
    public void TimingEditChangesOnlyOutputRangeAndRetainsSourceTimeEvidence()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        OriginRef editorial = EditorialOrigin(2);

        SubtitleCueHistory edited = SubtitleService.SetTiming(
            initial,
            fixture.ChildComposition(fixture.Composition, 2),
            fixture.SourceCandidate,
            RevisionId(2),
            Range(12, 3),
            editorial,
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Adjust the output subtitle timing.");

        SubtitleCueRevision child = edited.CurrentRevision;
        Assert.AreEqual(SubtitleCueChangeKind.Timing, child.Change.Kind);
        Assert.AreEqual(Range(12, 3), child.Snapshot.Cue.Range);
        Assert.AreEqual(
            initial.CurrentRevision.Snapshot.Cue.Id,
            child.Snapshot.Cue.Id);
        Assert.AreEqual(
            initial.CurrentRevision.Snapshot.Cue.RegionName,
            child.Snapshot.Cue.RegionName);
        Assert.AreEqual(
            initial.CurrentRevision.Snapshot.Text,
            child.Snapshot.Text);
        Assert.AreEqual(
            fixture.SourceCandidate.SourceRange,
            edited.SourceCandidate.SourceRange);
        Assert.AreEqual(editorial, child.Change.Origin);
    }

    [TestMethod]
    public void EverySubtitleStyleCanBeSelectedOnlyAsAnExplicitEditorialChild()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory history = fixture.CreateInitial();
        SubtitleCueStyle[] styles =
        [
            SubtitleCueStyle.Emphasis,
            SubtitleCueStyle.Caution,
            SubtitleCueStyle.Quote,
            SubtitleCueStyle.Normal,
        ];
        for (int index = 0; index < styles.Length; index++)
        {
            CompositionVersion parent = fixture.CompositionFor(history.CurrentRevision);
            CompositionVersion child = fixture.ChildComposition(parent, index + 2);
            history = SubtitleService.SetStyle(
                history,
                child,
                fixture.SourceCandidate,
                RevisionId(index + 2),
                styles[index],
                EditorialOrigin(index + 10),
                ActorSid,
                CreatedAt.AddMinutes(index + 2),
                "Change the subtitle style.");

            Assert.AreEqual(styles[index], history.CurrentRevision.Snapshot.Style);
            Assert.AreEqual(
                SubtitleCueChangeKind.Style,
                history.CurrentRevision.Change.Kind);
            Assert.AreEqual(
                fixture.SourceCandidate.Origin,
                history.CurrentRevision.Snapshot.TextOrigin);
        }

        Assert.HasCount(5, history.Revisions);
        CollectionAssert.AreEqual(
            new[] { "Normal", "Emphasis", "Caution", "Quote" },
            Enum.GetNames<SubtitleCueStyle>());
    }

    [TestMethod]
    public void LayoutEditCanResolveManualResultButCannotEraseItsParentEvidence()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial(fixture.ManualLayout);
        Assert.IsTrue(initial.CurrentRevision.Snapshot.RequiresManualLayout);

        SubtitleCueHistory resolved = SubtitleService.SetLayoutResolution(
            initial,
            fixture.ChildComposition(fixture.Composition, 2),
            fixture.SourceCandidate,
            RevisionId(2),
            fixture.ResolvedLayout,
            EditorialOrigin(20),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Select a manually verified placement.");

        SubtitleCueRevision child = resolved.CurrentRevision;
        Assert.AreEqual(SubtitleCueChangeKind.Layout, child.Change.Kind);
        Assert.IsFalse(child.Snapshot.RequiresManualLayout);
        Assert.AreEqual("subtitle", child.Snapshot.Cue.RegionName);
        Assert.AreEqual(
            fixture.ResolvedLayout.Plan!.Placement,
            child.Snapshot.ResolvedPlacement);
        Assert.IsTrue(
            initial.CurrentRevision.Snapshot.RequiresManualLayout);
        CollectionAssert.Contains(
            initial.CurrentRevision.Snapshot.LayoutResolution
                .Evaluations.Single().HardConstraints.ToArray(),
            SubtitleHardConstraintKind.ProhibitedRegionOverlap);
    }

    [TestMethod]
    public void EveryEditRequiresANewDirectChildCompositionSoOldApprovalCannotApply()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        var approval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Composition,
            ActorSid,
            ReviewActorRole.ContentApprover,
            CreatedAt.AddMinutes(1),
            "Approve only the parent composition version.");
        CompositionVersion childComposition = fixture.ChildComposition(
            fixture.Composition,
            2);

        SubtitleCueHistory edited = SubtitleService.SetStyle(
            initial,
            childComposition,
            fixture.SourceCandidate,
            RevisionId(2),
            SubtitleCueStyle.Emphasis,
            EditorialOrigin(30),
            ActorSid,
            CreatedAt.AddMinutes(2),
            "Emphasize the cue.");

        Assert.AreEqual(
            fixture.Composition.Id,
            approval.CompositionVersionId);
        Assert.AreNotEqual(
            approval.CompositionVersionId,
            edited.CurrentRevision.CompositionVersionId);
        Assert.AreEqual(
            fixture.Composition.Id,
            edited.CurrentRevision.ParentCompositionVersionId);
        Assert.IsFalse(typeof(SubtitleService).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(static method =>
                method.Name.Contains("Approval", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Copy", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void StaleSourceCandidateAndForeignLayoutEvidenceFailClosedPrivately()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        Fixture stale = Fixture.Create(candidateOrdinal: 2);
        CompositionVersion child = fixture.ChildComposition(fixture.Composition, 2);

        SubtitleServiceException staleFailure = AssertFailure(
            () => SubtitleService.SetText(
                initial,
                child,
                stale.SourceCandidate,
                RevisionId(2),
                "private-stale-edit",
                EditorialOrigin(40),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Attempt stale edit."),
            SubtitleServiceFailureReason.SourceCandidateStale);
        SubtitleServiceException layoutFailure = AssertFailure(
            () => SubtitleService.SetLayoutResolution(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                stale.ResolvedLayout,
                EditorialOrigin(41),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Attempt foreign layout."),
            SubtitleServiceFailureReason.LayoutBindingMismatch);

        Assert.IsFalse(staleFailure.ToString().Contains(
            "private-stale-edit",
            StringComparison.Ordinal));
        Assert.IsNull(staleFailure.InnerException);
        Assert.IsNull(layoutFailure.InnerException);
    }

    [TestMethod]
    public void ForgedOrNonChildCompositionAndReusedIdentityFailBeforeRevisionCreation()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        CompositionVersion forged = fixture.ChildComposition(
            fixture.Composition,
            2,
            digest: Digest('f'));
        CompositionVersion unrelated = fixture.CompositionVersion(
            3,
            parentVersionId: null);

        AssertFailure(
            () => fixture.SetText(initial, forged, RevisionId(2), "Forged"),
            SubtitleServiceFailureReason.CompositionVersionConflict);
        AssertFailure(
            () => fixture.SetText(initial, unrelated, RevisionId(2), "Unrelated"),
            SubtitleServiceFailureReason.CompositionVersionConflict);
        AssertFailure(
            () => fixture.SetText(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                initial.CurrentRevision.Id,
                "Duplicate revision"),
            SubtitleServiceFailureReason.RevisionConflict);
        AssertFailure(
            () => fixture.SetText(
                initial,
                fixture.Composition,
                RevisionId(2),
                "Same composition"),
            SubtitleServiceFailureReason.CompositionVersionConflict);
    }

    [TestMethod]
    public void TextTimingStyleAndLayoutNoOpsAreRejected()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();

        AssertFailure(
            () => fixture.SetText(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                RevisionId(2),
                initial.CurrentRevision.Snapshot.Text),
            SubtitleServiceFailureReason.NoChange);
        AssertFailure(
            () => SubtitleService.SetTiming(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                fixture.SourceCandidate,
                RevisionId(2),
                initial.CurrentRevision.Snapshot.Cue.Range,
                EditorialOrigin(51),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "No-op timing."),
            SubtitleServiceFailureReason.NoChange);
        AssertFailure(
            () => SubtitleService.SetStyle(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                fixture.SourceCandidate,
                RevisionId(2),
                initial.CurrentRevision.Snapshot.Style,
                EditorialOrigin(52),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "No-op style."),
            SubtitleServiceFailureReason.NoChange);
        AssertFailure(
            () => SubtitleService.SetLayoutResolution(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                fixture.SourceCandidate,
                RevisionId(2),
                fixture.ResolvedLayout,
                EditorialOrigin(53),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "No-op layout."),
            SubtitleServiceFailureReason.NoChange);
    }

    [TestMethod]
    public void InvalidTextTimingStyleOriginSidAndTimeUseStablePrivateFailures()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        CompositionVersion child = fixture.ChildComposition(fixture.Composition, 2);

        SubtitleServiceException text = AssertFailure(
            () => fixture.SetText(initial, child, RevisionId(2), "private\uD800text"),
            SubtitleServiceFailureReason.InvalidText);
        Assert.IsFalse(text.ToString().Contains("private", StringComparison.Ordinal));
        AssertFailure(
            () => SubtitleService.SetTiming(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                new TimeRange(new RationalTime(-1, 1), new RationalTime(1, 1)),
                EditorialOrigin(61),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Invalid output timing."),
            SubtitleServiceFailureReason.InvalidTiming);
        AssertFailure(
            () => SubtitleService.SetStyle(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                (SubtitleCueStyle)int.MaxValue,
                EditorialOrigin(62),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Invalid style."),
            SubtitleServiceFailureReason.InvalidStyle);
        AssertFailure(
            () => SubtitleService.SetStyle(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                SubtitleCueStyle.Emphasis,
                fixture.SourceCandidate.Origin,
                ActorSid,
                CreatedAt.AddMinutes(2),
                "A manual edit cannot impersonate source evidence."),
            SubtitleServiceFailureReason.InvalidOrigin);
        AssertFailure(
            () => SubtitleService.SetStyle(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                SubtitleCueStyle.Emphasis,
                EditorialOrigin(63),
                "not-a-sid",
                CreatedAt.AddMinutes(2),
                "Invalid actor."),
            SubtitleServiceFailureReason.InvalidActor);
        AssertFailure(
            () => SubtitleService.SetStyle(
                initial,
                child,
                fixture.SourceCandidate,
                RevisionId(2),
                SubtitleCueStyle.Emphasis,
                EditorialOrigin(64),
                ActorSid,
                initial.CurrentRevision.CreatedAt.AddTicks(-1),
                "Backward time."),
            SubtitleServiceFailureReason.RevisionConflict);
    }

    [TestMethod]
    public void HistoriesAndSnapshotsAreDefensiveReadOnlyValues()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();
        SubtitleCueHistory edited = fixture.SetText(
            initial,
            fixture.ChildComposition(fixture.Composition, 2),
            RevisionId(2),
            "Edited value");

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleCueRevision>)edited.Revisions).Clear());
        Assert.AreSame(edited.Revisions[^1], edited.CurrentRevision);
        Assert.AreEqual(initial.CueId, edited.CueId);
        Assert.AreEqual(initial.SourceCandidateId, edited.SourceCandidateId);
        Assert.AreEqual(initial.ProjectId, edited.ProjectId);
        Assert.AreEqual(
            SubtitleSnapshotJson.ComputeContentDigest(
                edited.CurrentRevision.Snapshot),
            edited.CurrentRevision.ContentDigest);

        Type[] immutableTypes =
        [
            typeof(SubtitleCueSnapshot),
            typeof(SubtitleCueChange),
            typeof(SubtitleCueRevision),
            typeof(SubtitleCueHistory),
            typeof(SubtitleServiceException),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }
    }

    [TestMethod]
    public void EquivalentInputProducesEqualContentDigestWithoutReusingRevisionIdentity()
    {
        Fixture fixture = Fixture.Create();

        SubtitleCueHistory first = fixture.CreateInitial(revisionId: RevisionId(1));
        SubtitleCueHistory second = SubtitleService.CreateInitial(
            fixture.Composition,
            fixture.SourceCandidate,
            fixture.ResolvedLayout,
            CueId(1),
            RevisionId(99),
            Range(10, 2),
            ActorSid,
            CreatedAt.AddMinutes(9),
            "Different revision metadata.");

        Assert.AreNotEqual(first.CurrentRevision.Id, second.CurrentRevision.Id);
        Assert.AreNotEqual(first.CurrentRevision.CreatedAt, second.CurrentRevision.CreatedAt);
        Assert.AreEqual(
            first.CurrentRevision.ContentDigest,
            second.CurrentRevision.ContentDigest);
    }

    [TestMethod]
    public void PreCancellationWinsWithoutReturningAPartialHistory()
    {
        Fixture fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SubtitleService.CreateInitial(
                fixture.Composition,
                fixture.SourceCandidate,
                fixture.ResolvedLayout,
                CueId(1),
                RevisionId(1),
                Range(10, 2),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Cancelled operation.",
                cancellation.Token));
    }

    [TestMethod]
    public void LayoutEditRejectsNullCurrentSourceCandidateAtThePublicBoundary()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory initial = fixture.CreateInitial();

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SubtitleService.SetLayoutResolution(
                initial,
                fixture.ChildComposition(fixture.Composition, 2),
                null!,
                RevisionId(2),
                fixture.ResolvedLayout,
                EditorialOrigin(70),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Reject a missing current source candidate."));
    }

    [TestMethod]
    [DataRow(SubtitleCueStyle.Normal)]
    [DataRow(SubtitleCueStyle.Emphasis)]
    [DataRow(SubtitleCueStyle.Caution)]
    [DataRow(SubtitleCueStyle.Quote)]
    public void ExportMapperCopiesTheExactR012SnapshotWithoutLayoutDependency(
        SubtitleCueStyle style)
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory history = fixture.CreateInitial(fixture.ManualLayout);
        if (style != SubtitleCueStyle.Normal)
        {
            history = SubtitleService.SetStyle(
                history,
                fixture.ChildComposition(fixture.Composition, 2),
                fixture.SourceCandidate,
                RevisionId(2),
                style,
                EditorialOrigin(71),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Select a subtitle export style.");
        }

        SubtitleCueSnapshot snapshot = history.CurrentRevision.Snapshot;
        SubtitleFileCue mapped = SubtitleExportMapper.Map(snapshot);

        Assert.AreSame(snapshot.Cue, mapped.Cue);
        Assert.AreEqual(snapshot.Text, mapped.Text);
        Assert.AreEqual(style.ToString(), mapped.Style.ToString());
        Assert.IsTrue(snapshot.RequiresManualLayout);
        Assert.IsNull(snapshot.Cue.RegionName);
    }

    [TestMethod]
    public void DisplayPolicyFeedsPreviewAndExportFromTheSameNonDestructiveResult()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueHistory history = fixture.CreateInitial();
        SubtitleCueSnapshot snapshot = history.CurrentRevision.Snapshot;
        SubtitleDisplaySourceCue source = SubtitleDisplayMapper.MapSource(snapshot);
        var profile = new SubtitleDisplayProfile(
            Digest('d'),
            SubtitleCueDisplayMode.Full,
            SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod);

        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            profile,
            [source],
            selectedCueIds: []);
        SubtitleDisplayedCue preview = result.DisplayedCues.Single();
        SubtitleFileCue export = SubtitleDisplayMapper.MapForExport(snapshot, preview);

        Assert.AreEqual(preview.DisplayText, export.Text);
        Assert.AreEqual(snapshot.Cue.Id, export.Cue.Id);
        Assert.AreEqual(snapshot.Style.ToString(), export.Style.ToString());
        Assert.AreEqual(snapshot.Text, source.Text);
        Assert.AreEqual("字幕1", snapshot.Text);
        Assert.AreEqual(
            history.CurrentRevision.ContentDigest,
            SubtitleSnapshotJson.ComputeContentDigest(snapshot));
    }

    [TestMethod]
    public void DisplayExportMapperRejectsCrossCueAndStaleTextBindings()
    {
        Fixture fixture = Fixture.Create();
        SubtitleCueSnapshot snapshot = fixture.CreateInitial().CurrentRevision.Snapshot;
        SubtitleDisplayResult other = SubtitleDisplayPolicy.Apply(
            new SubtitleDisplayProfile(
                Digest('d'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve),
            [new SubtitleDisplaySourceCue(
                new Cue(CueId(99), CueKind.Subtitle, Range(20, 1), "subtitle"),
                "other")],
            []);

        Assert.ThrowsExactly<ArgumentException>(() =>
            SubtitleDisplayMapper.MapForExport(
                snapshot,
                other.DisplayedCues.Single()));

        SubtitleDisplayResult reusedIdentity = SubtitleDisplayPolicy.Apply(
            new SubtitleDisplayProfile(
                Digest('d'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve),
            [new SubtitleDisplaySourceCue(
                new Cue(
                    snapshot.Cue.Id,
                    CueKind.Subtitle,
                    Range(30, 1),
                    snapshot.Cue.RegionName),
                snapshot.Text)],
            []);
        Assert.ThrowsExactly<ArgumentException>(() =>
            SubtitleDisplayMapper.MapForExport(
                snapshot,
                reusedIdentity.DisplayedCues.Single()));
    }

    [TestMethod]
    public void PublicSurfaceHasOnlyTypedRevisionOperationsAndNoInventedThresholds()
    {
        CollectionAssert.AreEqual(
            new[] { "Normal", "Emphasis", "Caution", "Quote" },
            Enum.GetNames<SubtitleCueStyle>());
        CollectionAssert.AreEqual(
            new[] { "Initial", "Text", "Timing", "Style", "Layout" },
            Enum.GetNames<SubtitleCueChangeKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                "ContextMismatch",
                "SourceCandidateStale",
                "LayoutBindingMismatch",
                "CompositionVersionConflict",
                "RevisionConflict",
                "NoChange",
                "InvalidText",
                "InvalidTiming",
                "InvalidStyle",
                "InvalidOrigin",
                "InvalidActor",
                "InvalidReason",
                "SerializationFailed",
            },
            Enum.GetNames<SubtitleServiceFailureReason>());
        CollectionAssert.AreEqual(
            new[]
            {
                "CreateInitial",
                "SetText",
                "SetTiming",
                "SetStyle",
                "SetLayoutResolution",
            },
            typeof(SubtitleService).GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
                .ToArray());

        MemberInfo[] members = typeof(SubtitleService).Assembly.GetExportedTypes()
            .Where(static type => type.Namespace == "HubRadio.App.Subtitles")
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .ToArray();
        Assert.IsFalse(members.Any(static member =>
            member.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("CharactersPerSecond", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("FramesPerSecond", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Dpi", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(members.OfType<MethodBase>()
            .SelectMany(static method => method.GetParameters())
            .Any(static parameter => parameter.ParameterType == typeof(double)
                || parameter.ParameterType == typeof(float)));
    }

    private static SubtitleServiceException AssertFailure(
        Action action,
        SubtitleServiceFailureReason expectedReason)
    {
        SubtitleServiceException exception =
            Assert.ThrowsExactly<SubtitleServiceException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static EntityId<CueIdTag> CueId(int value) =>
        Id<CueIdTag>(0x73000001, value);

    private static EntityId<SubtitleCueRevisionIdTag> RevisionId(int value) =>
        Id<SubtitleCueRevisionIdTag>(0x73000002, value);

    private static OriginRef EditorialOrigin(int value) =>
        OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(0x73000003, value));

    private static ContentDigest Digest(char fill) =>
        new(new string(fill, 64));

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(Time(start, rate), Time(duration, rate));

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private sealed class Fixture
    {
        private readonly Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>
            _compositions = [];

        private Fixture(
            SubtitleCueCandidate sourceCandidate,
            SubtitleLayoutResolution resolvedLayout,
            SubtitleLayoutResolution manualLayout,
            CompositionVersion composition)
        {
            SourceCandidate = sourceCandidate;
            ResolvedLayout = resolvedLayout;
            ManualLayout = manualLayout;
            Composition = composition;
            _compositions.Add(composition.Id, composition);
        }

        internal SubtitleCueCandidate SourceCandidate { get; }

        internal SubtitleLayoutResolution ResolvedLayout { get; }

        internal SubtitleLayoutResolution ManualLayout { get; }

        internal CompositionVersion Composition { get; }

        internal SubtitleCueHistory CreateInitial(
            SubtitleLayoutResolution? layout = null,
            EntityId<SubtitleCueRevisionIdTag>? revisionId = null) =>
            SubtitleService.CreateInitial(
                Composition,
                SourceCandidate,
                layout ?? ResolvedLayout,
                CueId(1),
                revisionId ?? RevisionId(1),
                Range(10, 2),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Create initial subtitle.");

        internal SubtitleCueHistory SetText(
            SubtitleCueHistory history,
            CompositionVersion child,
            EntityId<SubtitleCueRevisionIdTag> revisionId,
            string text) =>
            SubtitleService.SetText(
                history,
                child,
                SourceCandidate,
                revisionId,
                text,
                EditorialOrigin(100),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Edit subtitle text.");

        internal CompositionVersion CompositionFor(SubtitleCueRevision revision) =>
            _compositions[revision.CompositionVersionId];

        internal CompositionVersion ChildComposition(
            CompositionVersion parent,
            int value,
            ContentDigest? digest = null)
        {
            CompositionVersion child = CompositionVersion(
                value,
                parent.Id,
                digest);
            _compositions[child.Id] = child;
            return child;
        }

        internal CompositionVersion CompositionVersion(
            int value,
            EntityId<CompositionVersionIdTag>? parentVersionId,
            ContentDigest? digest = null)
        {
            return new CompositionVersion(
                Id<CompositionVersionIdTag>(0x73000004, value),
                Id<HubRadio.Core.Projects.ProjectIdTag>(0x73000005, 1),
                parentVersionId,
                ActorSid,
                CreatedAt.AddMinutes(value),
                $"Create composition V{value}.",
                digest ?? EmptyCompositionDigest(),
                elements: []);
        }

        internal static Fixture Create(int candidateOrdinal = 1)
        {
            SubtitleCueCandidate candidate = CreateCandidate(candidateOrdinal);
            LayoutSpec layout = new(
                Id<LayoutSpecIdTag>(0x73000006, candidateOrdinal),
                16,
                9,
                [
                    new LayoutRegion(
                        "subtitle",
                        FullRect(),
                        LayoutRegionRole.Subtitle),
                ],
                [new SafeZone("safe-frame", FullRect())]);
            var profile = new SubtitleLayoutProfile(
                layout.Id,
                Digest('a'),
                ["safe-frame"],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(static kind => new SubtitleSoftConstraintWeight(kind, 0))
                    .ToArray());
            var placement = new SubtitlePlacementCandidate(
                Id<SubtitlePlacementCandidateIdTag>(0x73000007, candidateOrdinal),
                "subtitle",
                new SubtitleLayoutRect(
                    default,
                    default,
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 1)));
            SubtitleLayoutResolution resolved = SubtitleLayoutEngine.Resolve(
                new SubtitleLayoutRequest(
                    candidate,
                    layout,
                    profile,
                    [placement],
                    prohibitedRegions: [],
                    faceRegions: [],
                    previousPlacement: null));
            SubtitleLayoutResolution manual = SubtitleLayoutEngine.Resolve(
                new SubtitleLayoutRequest(
                    candidate,
                    layout,
                    profile,
                    [placement],
                    [new SubtitleProhibitedRegion("full-mask", FullRect())],
                    faceRegions: [],
                    previousPlacement: null));
            var root = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x73000004, 1),
                Id<HubRadio.Core.Projects.ProjectIdTag>(0x73000005, 1),
                parentVersionId: null,
                ActorSid,
                CreatedAt,
                "Create root composition.",
                EmptyCompositionDigest(),
                elements: []);
            return new Fixture(candidate, resolved, manual, root);
        }

        private static SubtitleCueCandidate CreateCandidate(int ordinal)
        {
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(0x73000008, ordinal);
            EntityId<TranscriptTokenIdTag> tokenId =
                Id<TranscriptTokenIdTag>(0x73000009, ordinal);
            TimeRange sourceRange = Range(ordinal - 1, 2);
            var token = new Token(
                tokenId,
                $"字幕{ordinal}",
                sourceRange,
                recognitionConfidence: null);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(0x7300000a, ordinal),
                token.Text,
                [token]);
            EntityId<SourceSpanIdTag> sourceSpanId =
                Id<SourceSpanIdTag>(0x7300000b, ordinal);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(0x7300000c, ordinal),
                sourceSpanId,
                Id<TranscriptSpeakerIdTag>(0x7300000d, ordinal),
                $"Speaker {ordinal}",
                token.Text,
                sourceRange,
                recognitionConfidence: null,
                [sentence]);
            var revision = new TranscriptRevision(
                revisionId,
                Id<TranscriptRevisionIdTag>(0x7300000e, ordinal),
                TranscriptChangeSource.Human,
                CreatedAt,
                [utterance]);
            var sourceSpan = new SourceSpan(
                sourceSpanId,
                Id<AssetIdTag>(0x7300000f, ordinal),
                "audio:1",
                sourceRange,
                Digest('f'),
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

        private static ContentDigest EmptyCompositionDigest() =>
            ContentDigest.Compute(Encoding.UTF8.GetBytes("{\"elements\":[]}"));

        private static NormalizedRect FullRect() =>
            new(
                default,
                default,
                new RationalNumber(1, 1),
                new RationalNumber(1, 1));
    }
}