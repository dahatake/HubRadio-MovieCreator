using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class MusicPlanningServiceTests
{
    [TestMethod]
    public void DigestOrderSelectsFirstTrackThatFitsAndIsInputOrderIndependent()
    {
        MusicTrack longest = Track('c', 20m);
        MusicTrack tooShort = Track('a', 2m);
        MusicTrack firstFit = Track('b', 10m);
        MusicCatalog catalog = MusicCatalog.CreateAvailable(
            [longest, tooShort, firstFit]);
        MusicCue cue = Cue('1', MusicCueKind.TitleCard, 5m, false);
        MusicPlanningService service = new();

        MusicPlanningResult result = service.Plan(
            catalog,
            [cue],
            MusicPlanningOptions.Default);

        MusicCueAssignment assignment = result.Assignments.Single();
        Assert.AreEqual(firstFit.ContentDigest, assignment.TrackDigest);
        Assert.AreEqual(MusicAssignmentReason.Assigned, assignment.Reason);
        Assert.IsFalse(assignment.Loop);
        Assert.IsFalse(assignment.Stretch);
    }

    [TestMethod]
    // FR-019 AC-067: Music/ が無いときは BGM なしで完了し warning が記録される。
    public void AbsentCatalogCompletesWithoutAssignmentAndCarriesWarning()
    {
        MusicCatalog catalog = MusicCatalog.CreateUnavailable(
            MusicLibraryState.Absent,
            MusicCatalogWarning.LibraryAbsent);

        MusicPlanningResult result = new MusicPlanningService().Plan(
            catalog,
            [Cue('1', MusicCueKind.TitleCard, 1m, false)],
            MusicPlanningOptions.Default);

        Assert.AreEqual(
            MusicAssignmentReason.CatalogUnavailable,
            result.Assignments.Single().Reason);
        Assert.IsNull(result.Assignments.Single().TrackDigest);
        CollectionAssert.AreEqual(
            new[] { MusicCatalogWarning.LibraryAbsent },
            result.Warnings.ToArray());
    }

    [TestMethod]
    public void SpeechUnsupportedAndShortTrackReturnStableReasons()
    {
        MusicCatalog catalog = MusicCatalog.CreateAvailable([Track('a', 1m)]);
        MusicPlanningResult result = new MusicPlanningService().Plan(
            catalog,
            [
                Cue('1', MusicCueKind.TitleCard, 1m, true),
                Cue('2', MusicCueKind.Other, 1m, false),
                Cue('3', MusicCueKind.EndCard, 2m, false),
            ],
            MusicPlanningOptions.Default);

        CollectionAssert.AreEqual(
            new[]
            {
                MusicAssignmentReason.SpeechOverlap,
                MusicAssignmentReason.UnsupportedCueKind,
                MusicAssignmentReason.NoTrackLongEnough,
            },
            result.Assignments.Select(static item => item.Reason).ToArray());
    }

    [TestMethod]
    // FR-019 AC-067: 会話中の BGM は明示的に有効化したときだけで、duck gain が記録される。
    public void DialogueRequiresExplicitEnableAndOnlyThenRecordsDuckGain()
    {
        MusicCatalog catalog = MusicCatalog.CreateAvailable([Track('a', 2m)]);
        MusicCue dialogue = Cue('1', MusicCueKind.Dialogue, 1m, true);
        MusicPlanningService service = new();

        MusicCueAssignment disabled = service.Plan(
            catalog,
            [dialogue],
            MusicPlanningOptions.Default).Assignments.Single();
        MusicCueAssignment enabled = service.Plan(
            catalog,
            [dialogue],
            new MusicPlanningOptions(
                MusicUsageScope.InternalOnly,
                false,
                true,
                20)).Assignments.Single();

        Assert.AreEqual(MusicAssignmentReason.DialogueMusicDisabled, disabled.Reason);
        Assert.IsNull(disabled.RelativeGainDecibels);
        Assert.AreEqual(MusicAssignmentReason.Assigned, enabled.Reason);
        Assert.AreEqual(-20, enabled.RelativeGainDecibels);
    }

    [TestMethod]
    public void EquivalentCueSetsAreDeterministicAndResultsAreReadOnly()
    {
        MusicCatalog catalog = MusicCatalog.CreateAvailable([Track('a', 10m)]);
        MusicCue first = Cue('1', MusicCueKind.TitleCard, 1m, false);
        MusicCue second = Cue('2', MusicCueKind.EndCard, 1m, false);
        MusicPlanningService service = new();

        MusicPlanningResult one = service.Plan(
            catalog,
            [second, first],
            MusicPlanningOptions.Default);
        MusicPlanningResult two = service.Plan(
            catalog,
            [first, second],
            MusicPlanningOptions.Default);

        CollectionAssert.AreEqual(
            one.Assignments.Select(static item => item.CueDigest.Hex).ToArray(),
            two.Assignments.Select(static item => item.CueDigest.Hex).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<MusicCueAssignment>)one.Assignments).Clear());
        Assert.IsTrue(one.PolicyEvaluations.All(static item =>
            item.Status == MusicPolicyEvaluationStatus.NotEvaluated));
    }

    [TestMethod]
    public void DuplicateCueAndPreCancellationFailWithoutPartialResult()
    {
        MusicCatalog catalog = MusicCatalog.CreateAvailable([Track('a', 10m)]);
        MusicCue cue = Cue('1', MusicCueKind.TitleCard, 1m, false);
        MusicPlanningService service = new();
        Assert.ThrowsExactly<ArgumentException>(() => service.Plan(
            catalog,
            [cue, cue],
            MusicPlanningOptions.Default));

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => service.Plan(
            catalog,
            [cue],
            MusicPlanningOptions.Default,
            cancellation.Token));
    }

    [TestMethod]
    public void AssignmentRejectsImpossiblePublicResultStates()
    {
        ContentDigest cue = new(new string('1', 64));
        ContentDigest track = new(new string('a', 64));

        Assert.ThrowsExactly<ArgumentException>(() => new MusicCueAssignment(
            cue,
            null,
            MusicAssignmentReason.Assigned,
            null,
            loop: false,
            stretch: false));
        Assert.ThrowsExactly<ArgumentException>(() => new MusicCueAssignment(
            cue,
            track,
            MusicAssignmentReason.Assigned,
            null,
            loop: true,
            stretch: false));
        Assert.AreEqual(
            0,
            typeof(MusicCueAssignment).GetConstructors().Length);
    }

    private static MusicTrack Track(char digest, decimal duration) =>
        new(new ContentDigest(new string(digest, 64)), 100, duration, "mp3", 48000, 2);

    private static MusicCue Cue(
        char digest,
        MusicCueKind kind,
        decimal duration,
        bool speech) =>
        new(new ContentDigest(new string(digest, 64)), kind, duration, speech);
}