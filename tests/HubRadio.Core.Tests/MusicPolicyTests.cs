using System.Reflection;
using HubRadio.Core.Media;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class MusicPolicyTests
{
    [TestMethod]
    public void SpeechFreeCardsRequireOnlyCueLengthAndNeverAnArbitraryMinimum()
    {
        MusicPlanningOptions options = MusicPlanningOptions.Default;

        foreach (MusicCueKind kind in new[]
        {
            MusicCueKind.TitleCard,
            MusicCueKind.TopicCard,
            MusicCueKind.EndCard,
        })
        {
            Assert.IsTrue(MusicPolicy.Evaluate(kind, false, 0.01m, 0.01m, options).IsEligible);
            MusicCueEligibility shortTrack = MusicPolicy.Evaluate(
                kind,
                false,
                1.01m,
                1m,
                options);
            Assert.IsFalse(shortTrack.IsEligible);
            Assert.AreEqual(MusicCueBlockReason.TrackTooShort, shortTrack.BlockReason);
        }
    }

    [TestMethod]
    public void SpeechOverlapAndUnsupportedKindsAreBlocked()
    {
        MusicCueEligibility speech = MusicPolicy.Evaluate(
            MusicCueKind.TitleCard,
            true,
            1m,
            2m,
            MusicPlanningOptions.Default);
        MusicCueEligibility other = MusicPolicy.Evaluate(
            MusicCueKind.Other,
            false,
            1m,
            2m,
            MusicPlanningOptions.Default);

        Assert.AreEqual(MusicCueBlockReason.SpeechOverlap, speech.BlockReason);
        Assert.AreEqual(MusicCueBlockReason.UnsupportedCueKind, other.BlockReason);
    }

    [TestMethod]
    public void DialogueDefaultsOffAndExplicitEnableRecordsNegativeTwentyDbGain()
    {
        MusicCueEligibility disabled = MusicPolicy.Evaluate(
            MusicCueKind.Dialogue,
            true,
            1m,
            2m,
            MusicPlanningOptions.Default);
        MusicPlanningOptions enabled = new(
            MusicUsageScope.InternalOnly,
            usesExternalProfile: false,
            dialogueBackgroundMusicEnabled: true,
            dialogueDuckAmountDecibels: 20);

        Assert.AreEqual(MusicCueBlockReason.DialogueMusicDisabled, disabled.BlockReason);
        Assert.IsNull(MusicPlanningOptions.Default.AppliedDialogueRelativeGainDecibels);
        Assert.AreEqual(-20, enabled.AppliedDialogueRelativeGainDecibels);
        Assert.IsTrue(MusicPolicy.Evaluate(
            MusicCueKind.Dialogue,
            true,
            1m,
            2m,
            enabled).IsEligible);
    }

    [TestMethod]
    public void ExternalScopeProfileAndDifferentDuckAmountFailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MusicPlanningOptions(
            MusicUsageScope.External,
            false,
            false,
            20));
        Assert.ThrowsExactly<ArgumentException>(() => new MusicPlanningOptions(
            MusicUsageScope.InternalOnly,
            true,
            false,
            20));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MusicPlanningOptions(
            MusicUsageScope.InternalOnly,
            false,
            true,
            -20));
    }

    [TestMethod]
    public void MusicTrackRejectsNonMp3TechnicalMetadata()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MusicTrack(
            new HubRadio.Core.Identity.ContentDigest(new string('a', 64)),
            100,
            1m,
            "aac",
            48000,
            2));
    }

    [TestMethod]
    public void PerceptualFitAndRightsDomainsAreAlwaysNotEvaluated()
    {
        CollectionAssert.AreEqual(
            Enum.GetValues<MusicPolicyDomain>(),
            MusicPolicy.NotEvaluatedDomains.Select(static item => item.Domain).ToArray());
        Assert.IsTrue(MusicPolicy.NotEvaluatedDomains.All(static item =>
            item.Status == MusicPolicyEvaluationStatus.NotEvaluated));
        CollectionAssert.AreEqual(
            new[] { MusicPolicyEvaluationStatus.NotEvaluated },
            Enum.GetValues<MusicPolicyEvaluationStatus>());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<MusicPolicyEvaluation>)MusicPolicy.NotEvaluatedDomains).Clear());
    }

    [TestMethod]
    public void PublicPolicySurfaceContainsNoLoopStretchFitOrRightsApproval()
    {
        string[] publicNames = typeof(MusicPolicy).GetMembers(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(static member => member.Name)
            .ToArray();

        Assert.IsFalse(publicNames.Any(static name =>
            name.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Stretch", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)));
    }
}