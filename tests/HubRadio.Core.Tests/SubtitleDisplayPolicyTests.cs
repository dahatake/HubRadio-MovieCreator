using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class SubtitleDisplayPolicyTests
{
    [TestMethod]
    public void FullPreserveReturnsCanonicalCueOrderAndExactSourceText()
    {
        SubtitleDisplaySourceCue later = Source(2, Range(2, 1), "後です。");
        SubtitleDisplaySourceCue earlier = Source(1, Range(0, 1), "前です、はい。");
        SubtitleDisplayProfile profile = Profile(
            SubtitleCueDisplayMode.Full,
            SubtitlePunctuationDisplay.Preserve);

        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            profile,
            [later, earlier],
            selectedCueIds: []);

        Assert.AreEqual(profile.VersionDigest, result.ProfileVersionDigest);
        Assert.AreEqual(2, result.SourceCueCount);
        CollectionAssert.AreEqual(
            new[] { earlier.Cue.Id, later.Cue.Id },
            result.DisplayedCues.Select(static cue => cue.Cue.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "前です、はい。", "後です。" },
            result.DisplayedCues.Select(static cue => cue.DisplayText).ToArray());
        Assert.IsTrue(result.DisplayedCues.All(static cue => !cue.TextChanged));
        Assert.HasCount(0, result.OmittedCueIds);
    }

    [TestMethod]
    public void HideRemovesOnlyJapaneseCommaPeriodFormsAndPreservesOtherPunctuation()
    {
        SubtitleDisplaySourceCue source = Source(
            1,
            Range(0, 1),
            "「本当？！」、はい。次､です｡");

        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            Profile(
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod),
            [source],
            []);

        SubtitleDisplayedCue displayed = result.DisplayedCues.Single();
        Assert.AreEqual("「本当？！」はい次です", displayed.DisplayText);
        Assert.AreEqual(source.SourceTextDigest, displayed.SourceTextDigest);
        Assert.AreSame(source.Cue, displayed.Cue);
        Assert.IsTrue(displayed.TextChanged);
        Assert.AreEqual("「本当？！」、はい。次､です｡", source.Text);
    }

    [TestMethod]
    public void PunctuationOnlyCueIsOmittedWithIdentityInsteadOfCreatingBlankText()
    {
        SubtitleDisplaySourceCue punctuation = Source(1, Range(0, 1), "、。､｡");
        SubtitleDisplaySourceCue visible = Source(2, Range(1, 1), "表示。");

        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            Profile(
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod),
            [punctuation, visible],
            []);

        Assert.HasCount(1, result.DisplayedCues);
        Assert.AreEqual(visible.Cue.Id, result.DisplayedCues.Single().Cue.Id);
        Assert.AreEqual("表示", result.DisplayedCues.Single().DisplayText);
        CollectionAssert.AreEqual(
            new[] { punctuation.Cue.Id },
            result.OmittedCueIds.ToArray());
    }

    [TestMethod]
    public void SelectedModeReturnsOnlyExplicitKnownCueIdsInTimelineOrder()
    {
        SubtitleDisplaySourceCue first = Source(1, Range(0, 1), "一");
        SubtitleDisplaySourceCue second = Source(2, Range(1, 1), "二");
        SubtitleDisplaySourceCue third = Source(3, Range(2, 1), "三");

        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            Profile(
                SubtitleCueDisplayMode.Selected,
                SubtitlePunctuationDisplay.Preserve),
            [third, first, second],
            [third.Cue.Id, first.Cue.Id]);

        CollectionAssert.AreEqual(
            new[] { first.Cue.Id, third.Cue.Id },
            result.DisplayedCues.Select(static cue => cue.Cue.Id).ToArray());
        Assert.AreEqual(3, result.SourceCueCount);
    }

    [TestMethod]
    public void SelectedModeAllowsAnExplicitEmptySelection()
    {
        SubtitleDisplayResult result = SubtitleDisplayPolicy.Apply(
            Profile(
                SubtitleCueDisplayMode.Selected,
                SubtitlePunctuationDisplay.Preserve),
            [Source(1, Range(0, 1), "一")],
            []);

        Assert.HasCount(0, result.DisplayedCues);
        Assert.HasCount(0, result.OmittedCueIds);
        Assert.AreEqual(1, result.SourceCueCount);
    }

    [TestMethod]
    public void UnknownDuplicateOrAmbiguousSelectionsFailClosed()
    {
        SubtitleDisplaySourceCue source = Source(1, Range(0, 1), "一");
        EntityId<CueIdTag> unknown = CueId(99);

        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Selected,
                    SubtitlePunctuationDisplay.Preserve),
                [source],
                [unknown]),
            SubtitleDisplayFailureReason.UnknownSelectedCue);
        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Selected,
                    SubtitlePunctuationDisplay.Preserve),
                [source],
                [source.Cue.Id, source.Cue.Id]),
            SubtitleDisplayFailureReason.DuplicateSelection);
        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Selected,
                    SubtitlePunctuationDisplay.Preserve),
                [source],
                [null!]),
            SubtitleDisplayFailureReason.InvalidSelection);
        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [source],
                [source.Cue.Id]),
            SubtitleDisplayFailureReason.AmbiguousSelection);
    }

    [TestMethod]
    public void DuplicateSourceCueAndNullItemsFailClosedPrivately()
    {
        SubtitleDisplaySourceCue first = Source(1, Range(0, 1), "一");
        SubtitleDisplaySourceCue duplicate = Source(1, Range(1, 1), "二");

        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [first, duplicate],
                []),
            SubtitleDisplayFailureReason.DuplicateSourceCue);
        AssertFailure(
            () => SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [first, null!],
                []),
            SubtitleDisplayFailureReason.InvalidSourceCue);
    }

    [TestMethod]
    public void SourceCueRejectsGraphicInvalidTextAndUnknownProfileEnums()
    {
        Cue graphic = new(CueId(1), CueKind.Graphic, Range(0, 1), "graphic");
        AssertParamName<ArgumentException>(
            "cue",
            () => new SubtitleDisplaySourceCue(graphic, "text"));
        foreach (string invalid in new[]
        {
            string.Empty,
            " ",
            " leading",
            "escape\u001btext",
            "bad\uD800",
        })
        {
            SubtitleDisplayException exception = Assert.ThrowsExactly<
                SubtitleDisplayException>(() =>
                new SubtitleDisplaySourceCue(SubtitleCue(2, Range(0, 1)), invalid));
            Assert.AreEqual(SubtitleDisplayFailureReason.InvalidText, exception.Reason);
            Assert.IsFalse(exception.ToString().Contains("leading", StringComparison.Ordinal));
        }

        AssertParamName<ArgumentOutOfRangeException>(
            "cueMode",
            () => new SubtitleDisplayProfile(
                Digest('a'),
                (SubtitleCueDisplayMode)int.MaxValue,
                SubtitlePunctuationDisplay.Preserve));
        AssertParamName<ArgumentOutOfRangeException>(
            "punctuationDisplay",
            () => new SubtitleDisplayProfile(
                Digest('a'),
                SubtitleCueDisplayMode.Full,
                (SubtitlePunctuationDisplay)int.MaxValue));
    }

    [TestMethod]
    public void PolicyVersionChangesEvidenceWithoutMutatingDisplayOrSource()
    {
        SubtitleDisplaySourceCue source = Source(1, Range(0, 1), "本文。");
        SubtitleDisplayResult first = SubtitleDisplayPolicy.Apply(
            new SubtitleDisplayProfile(
                Digest('a'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve),
            [source],
            []);
        SubtitleDisplayResult second = SubtitleDisplayPolicy.Apply(
            new SubtitleDisplayProfile(
                Digest('b'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve),
            [source],
            []);

        Assert.AreNotEqual(first.ProfileVersionDigest, second.ProfileVersionDigest);
        Assert.AreEqual(
            first.DisplayedCues.Single().DisplayText,
            second.DisplayedCues.Single().DisplayText);
        Assert.AreEqual("本文。", source.Text);
        Assert.AreEqual(
            ContentDigest.Compute(EncodingUtf8("本文。")),
            source.SourceTextDigest);
    }

    [TestMethod]
    public void ResultsAreDefensiveAndEquivalentInputSetsAreDeterministic()
    {
        SubtitleDisplaySourceCue first = Source(1, Range(0, 1), "一。");
        SubtitleDisplaySourceCue second = Source(2, Range(1, 1), "二。");
        SubtitleDisplayProfile profile = Profile(
            SubtitleCueDisplayMode.Full,
            SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod);

        SubtitleDisplayResult one = SubtitleDisplayPolicy.Apply(
            profile,
            [second, first],
            []);
        SubtitleDisplayResult two = SubtitleDisplayPolicy.Apply(
            profile,
            [first, second],
            []);

        CollectionAssert.AreEqual(
            one.DisplayedCues.Select(static cue => cue.Cue.Id).ToArray(),
            two.DisplayedCues.Select(static cue => cue.Cue.Id).ToArray());
        CollectionAssert.AreEqual(
            one.DisplayedCues.Select(static cue => cue.DisplayText).ToArray(),
            two.DisplayedCues.Select(static cue => cue.DisplayText).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleDisplayedCue>)one.DisplayedCues).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EntityId<CueIdTag>>)one.OmittedCueIds).Clear());
    }

    [TestMethod]
    public void PreCancellationAndPublicSurfaceRemainMinimal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SubtitleDisplayPolicy.Apply(
                Profile(
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [Source(1, Range(0, 1), "一")],
                [],
                cancellation.Token));

        Assert.IsTrue(typeof(SubtitleDisplayPolicy).IsAbstract);
        Assert.IsTrue(typeof(SubtitleDisplayPolicy).IsSealed);
        Assert.AreEqual(
            1,
            typeof(SubtitleDisplayPolicy).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
        Assert.IsFalse(typeof(SubtitleDisplayPolicy).GetMembers()
            .Any(static member =>
                member.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Summary", StringComparison.OrdinalIgnoreCase)));
    }

    private static SubtitleDisplayException AssertFailure(
        Action action,
        SubtitleDisplayFailureReason reason)
    {
        SubtitleDisplayException exception =
            Assert.ThrowsExactly<SubtitleDisplayException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static SubtitleDisplayProfile Profile(
        SubtitleCueDisplayMode cueMode,
        SubtitlePunctuationDisplay punctuation) =>
        new(Digest('a'), cueMode, punctuation);

    private static SubtitleDisplaySourceCue Source(
        int id,
        TimeRange range,
        string text) =>
        new(SubtitleCue(id, range), text);

    private static Cue SubtitleCue(int id, TimeRange range) =>
        new(CueId(id), CueKind.Subtitle, range, "subtitle");

    private static EntityId<CueIdTag> CueId(int value) =>
        EntityId<CueIdTag>.Parse($"76000001-0000-0000-0000-{value:x12}");

    private static ContentDigest Digest(char fill) => new(new string(fill, 64));

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static byte[] EncodingUtf8(string value) =>
        new System.Text.UTF8Encoding(false, true).GetBytes(value);

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}