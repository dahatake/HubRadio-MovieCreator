using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;

namespace HubRadio.App.Tests;

/// <summary>FR-141 (AC-168/169/170) talk-show input and FR-143 (AC-173/174/175) vertical placement inspection.</summary>
[TestClass]
public sealed class TalkShowAndVerticalPlacementTests
{
    private static OriginRef Origin(string _) => OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

    private static TalkShowSpeakerInput Speaker(string id, string? name, string? title, bool titleConfirmed, string? on = null) =>
        new(id, name, TalkShowValueOrigin.UserEntered, false, title, titleConfirmed, on);

    // FR-141 AC-168: input values are saved to the Job; a draft is told apart from a confirmed value.
    [TestMethod]
    public void TalkShowInputIsSavedAndReloadedWithConfirmationStateAndDate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "talk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new TalkShowInput(
                [new("t1", "今日のテーマ", 0, TalkShowValueOrigin.SystemDraft, false)],
                [Speaker("s1", "山田太郎", "代表取締役", true, "2026-10-30")]);
            TalkShowInputStore.Save(dir, input);
            TalkShowInput loaded = TalkShowInputStore.Load(dir);
            Assert.AreEqual("山田太郎", loaded.Speakers.Single().Name);
            Assert.IsTrue(loaded.Speakers.Single().TitleConfirmed);
            Assert.AreEqual("2026-10-30", loaded.Speakers.Single().TitleConfirmedOn);
            Assert.AreEqual(TalkShowValueOrigin.SystemDraft, loaded.Topics.Single().Origin);
            Assert.IsFalse(loaded.Topics.Single().Confirmed);
            Assert.AreEqual(0, TalkShowInputStore.Load(Path.Combine(dir, "none")).Topics.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    // FR-141 AC-169: only entered/confirmed values appear; unconfirmed title is dropped with a warning; no anonymous label.
    [TestMethod]
    public void OnlyEnteredOrConfirmedValuesAreShownAndAnUnconfirmedTitleWarns()
    {
        var input = new TalkShowInput(
            [new("t1", "第1話題", 0, TalkShowValueOrigin.UserEntered, false),
             new("t2", "要約の下書き", 10, TalkShowValueOrigin.SystemDraft, false),
             new("t3", "確認済みの要約", 20, TalkShowValueOrigin.SystemDraft, true),
             new("t4", "  ", 30, TalkShowValueOrigin.UserEntered, false)],
            [Speaker("s1", "山田太郎", "代表取締役", true, "2026-10-30"),
             Speaker("s2", "佐藤花子", "取締役", false),
             Speaker("s3", "話者3", "部長", true, "2026-10-30")]);
        TalkShowPlan plan = TalkShowPlanner.Plan(input, Origin);
        CollectionAssert.AreEqual(new[] { "第1話題", "確認済みの要約" }, plan.Topics.Select(static t => t.Text).ToArray());
        string?[] bands = plan.Speakers.Select(StyleTemplateLayout.BandText).ToArray();
        CollectionAssert.AreEqual(new string?[] { "山田太郎　代表取締役", "佐藤花子", null }, bands);
        Assert.IsTrue(plan.Warnings.Any(static w => w.Code == TalkShowPlan.TitleUnconfirmedWarning));
        Assert.IsTrue(plan.Warnings.Any(static w => w.Code == TalkShowPlan.TopicUnconfirmedWarning));
    }

    // A title marked confirmed without any date is not shown (NFR-BR-002) and says why.
    [TestMethod]
    public void AConfirmedTitleNeedsItsConfirmationDate()
    {
        TalkShowPlan plan = TalkShowPlanner.Plan(new([], [Speaker("s1", "山田太郎", "代表取締役", true)]), Origin);
        Assert.AreEqual("山田太郎", StyleTemplateLayout.BandText(plan.Speakers.Single()));
        Assert.IsTrue(plan.Warnings.Any(static w => w.Code == TalkShowPlan.TitleDateMissingWarning));
    }

    // FR-141 AC-170: a changed confirmed value replaces the old one in what is built next.
    [TestMethod]
    public void ChangingAConfirmedNameAndTitleLeavesNoPreviousValue()
    {
        var first = new TalkShowInput([], [Speaker("s1", "山田太郎", "代表取締役", true, "2026-10-30")]);
        TalkShowInput changed = first.WithSpeaker(Speaker("s1", "山田次郎", "会長", true, "2026-11-01"));
        Assert.AreEqual(1, changed.Speakers.Count);
        string? band = StyleTemplateLayout.BandText(TalkShowPlanner.Plan(changed, Origin).Speakers.Single());
        Assert.AreEqual("山田次郎　会長", band);
        Assert.IsFalse(band!.Contains("山田太郎", StringComparison.Ordinal) || band.Contains("代表取締役", StringComparison.Ordinal));
    }

    private static readonly StyleTemplateRect Region = new(60, 1300, 1020, 1700);

    // FR-143 AC-173: the default (no template) subtitle is inside the FR-014 region and does not depend on PARAM-005.
    [TestMethod]
    public void TheDefaultVerticalSubtitleIsInsideTheRegionAndIgnoresTheTemplateMargins()
    {
        StyleTemplateRect box = VerticalPlacementInspector.DefaultSubtitleRect(Region, 1920);
        Assert.IsTrue(Region.Contains(box));
        Assert.AreEqual(box, VerticalPlacementInspector.DefaultSubtitleRect(Region, 1920));
        VerticalPlacementEvidence evidence = VerticalPlacementInspector.Inspect(1080, 1920, null, Region, [("default", box)], true);
        Assert.AreEqual(VerticalPlacementRule.RegionOnly, evidence.Rule);
        Assert.IsTrue(evidence.Passed);
    }

    // FR-143 AC-174/175: a template Job passes only inside both the margins and the region; the evidence records rule, values, positions, verdict.
    [TestMethod]
    public void TheTemplateInspectionPassesOnlyInsideBothMarginsAndRegionAndRecordsEvidence()
    {
        StyleTemplateMargins margins = StyleTemplateMargins.Vertical;
        StyleTemplateRect good = VerticalPlacementInspector.TemplateSubtitleRect(1080, 1920, margins, Region);
        VerticalPlacementEvidence ok = VerticalPlacementInspector.Inspect(1080, 1920, margins, Region, [("subtitle", good)], false);
        Assert.AreEqual(VerticalPlacementRule.TemplateMarginsAndRegion, ok.Rule);
        Assert.IsTrue(ok.Passed);
        Assert.AreEqual(margins, ok.Margins);
        Assert.AreEqual(Region, ok.Region);
        CollectionAssert.AreEqual(new[] { VerticalPlacementEvidence.PlatformUiUnconfirmedWarning }, ok.Warnings.ToArray());
        StringAssert.Contains(ok.ToJson(), "TemplateMarginsAndRegion");

        // inside the region but inside the bottom margin (y > 1920-480) -> fail
        var inMargin = new StyleTemplateRect(100, 1500, 900, 1600);
        VerticalPlacementEvidence bad = VerticalPlacementInspector.Inspect(1080, 1920, margins, Region, [("subtitle", good), ("moved", inMargin)], true);
        Assert.IsFalse(bad.Passed);
        Assert.IsFalse(bad.Items.Single(static i => i.Id == "moved").InsideMargins);
        Assert.AreEqual(0, bad.Warnings.Count);
        // inside the margins but outside the region -> fail
        Assert.IsFalse(VerticalPlacementInspector.Inspect(1080, 1920, margins, Region, [("x", new StyleTemplateRect(200, 300, 900, 400))], true).Passed);
    }
}
