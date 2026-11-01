using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Tests;

/// <summary>FR-138 (5) / AC-158: the bottom tier shows 15-20 characters per screen; the hook opens the output or a warning is recorded.</summary>
[TestClass]
public sealed class VerticalThreeBandTests
{
    private static RecordedCue Cue(double start, double end, string text) =>
        new(start, end, text, null, null, "#FFFFFF", null, []);

    // FR-138 AC-158
    [TestMethod]
    public void BottomScreens_AreFifteenToTwentyCharacters_AndKeepTheText()
    {
        RecordedCue[] cues = [Cue(0, 4, "あいうえおかきくけこ"), Cue(4, 8, "さしすせそたちつてとなにぬねの"), Cue(8, 12, "はひふへほまみむめもやゆよ")];
        string all = string.Concat(cues.Select(static cue => cue.Text));
        IReadOnlyList<RecordedCue> screens = StyleTemplateLayout.BottomScreens(cues);
        Assert.AreEqual(all, string.Concat(screens.Select(static item => item.Text)));
        Assert.IsTrue(screens.All(static item => item.Text.Length is >= 15 and <= 20), string.Join("|", screens.Select(static item => item.Text)));
        Assert.AreEqual(0, screens[0].Start, 1e-9);
        Assert.AreEqual(12, screens[^1].End, 1e-9);
        for (int i = 1; i < screens.Count; i++)
        {
            Assert.IsTrue(screens[i].Start >= screens[i - 1].End - 1e-9);
        }
    }

    // FR-138 AC-158: a text shorter than the minimum cannot be made longer; it is kept.
    [TestMethod]
    public void BottomScreens_ShortText_StaysOneScreen()
    {
        IReadOnlyList<RecordedCue> screens = StyleTemplateLayout.BottomScreens([Cue(0, 2, "短い")]);
        Assert.AreEqual(1, screens.Count);
        Assert.AreEqual("短い", screens[0].Text);
    }

    private static StyleTemplateRenderRecord Record() => new(StyleTemplateCatalog.Vertical3TierId, 1, 1080, 1920, "#FFFFFF", "#FFFF00", "#FFFFFF",
        false, [Cue(0, 6, "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめも")], [], []);

    private static RecordedTelop Title() => new("title", "題", 0, 6, new(160, 250, 760, 100));

    // FR-138 AC-158: the hook selected by FR-011 opens the timeline.
    [TestMethod]
    public void WithPlacement_SelectedHook_StartsTheTimeline()
    {
        StyleTemplateRenderRecord placed = Record().WithPlacement(null, [Title()], new(160, 1130, 760, 170), StyleTemplateMargins.Vertical, false,
            hookSegmentId: "span-1", hookEnd: 3);
        Assert.AreEqual("span-1", placed.Hook!.SegmentId);
        Assert.IsTrue(placed.Hook.Selected);
        Assert.AreEqual("hook", placed.Timeline[0].Source);
        Assert.AreEqual("span-1", placed.Timeline[0].HookSegmentId);
        Assert.IsFalse(placed.Warnings.Contains(StyleTemplateRenderRecord.HookNotSelectedWarning));
        Assert.IsTrue(placed.Layout!.Bottom.Subtitles.All(static item => item.Text.Length is >= 15 and <= 20));
    }

    // FR-138 AC-158: no hook selected -> nothing is added at the start and a warning is recorded.
    [TestMethod]
    public void WithPlacement_NoHook_AddsNothingAndWarns()
    {
        StyleTemplateRenderRecord placed = Record().WithPlacement(null, [Title()], new(160, 1130, 760, 170), StyleTemplateMargins.Vertical, false);
        Assert.IsNull(placed.Hook!.SegmentId);
        Assert.IsFalse(placed.Hook.Selected);
        Assert.AreEqual("program", placed.Timeline[0].Source);
        Assert.IsTrue(placed.Warnings.Contains(StyleTemplateRenderRecord.HookNotSelectedWarning));
    }

    // FR-138 AC-156: the three tiers lie inside PARAM-005 margins, in top/middle/bottom order.
    [TestMethod]
    public void Frame_TiersAreOrderedAndInsideMargins()
    {
        StyleTemplateVerticalFrame frame = StyleTemplateLayout.VerticalFrame();
        Assert.IsTrue(frame.TitleTier.Top >= 250 && frame.TitleTier.Top < frame.VideoTier.Top && frame.VideoTier.Top < frame.CaptionTier.Top);
        Assert.IsTrue(frame.CaptionTier.Bottom <= 1920 - 480);
        Assert.AreEqual(0, frame.VideoTier.Top % 2);
        Assert.AreEqual(0, frame.VideoTier.Height % 2);
    }
}
