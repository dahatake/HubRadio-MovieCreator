using System.Text.Json;
using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-141 (AC-168/169/170) talk-show input from the Job screen, the side telops and name bands the output shows, and
/// FR-143 (AC-174/175) the placement record and output inspection of a vertical Job.
/// </summary>
[TestClass]
public sealed class TalkShowRenderingTests
{
    private const string Valid = """
        {"topics":[{"id":"topic-1","text":"今日のテーマ","origin":"user","confirmed":false},{"id":"topic-2","text":"要約の下書き","origin":"draft","confirmed":false}],
         "speakers":[{"id":"speaker-1","name":"山田太郎","nameOrigin":"user","nameConfirmed":false,"title":"代表取締役","titleConfirmed":true,"titleConfirmedOn":"2026-10-30"},
                     {"id":"speaker-2","name":"佐藤花子","nameOrigin":"user","nameConfirmed":false,"title":"取締役","titleConfirmed":false,"titleConfirmedOn":""},
                     {"id":"speaker-3","name":"話者3","nameOrigin":"user","nameConfirmed":false,"title":"","titleConfirmed":false,"titleConfirmedOn":""}]}
        """;

    // FR-141 AC-168: the JSON the screen sends is read into the Job's input; a draft stays a draft.
    [TestMethod]
    public void TalkShowInputJsonIsReadWithDraftAndConfirmationState()
    {
        Assert.IsTrue(TalkShowInputJson.TryParse(Valid, out TalkShowInput input));
        Assert.AreEqual(2, input.Topics.Count);
        Assert.AreEqual(TalkShowValueOrigin.SystemDraft, input.Topics[1].Origin);
        Assert.IsFalse(input.Topics[1].Confirmed);
        Assert.AreEqual("2026-10-30", input.Speakers[0].TitleConfirmedOn);
        Assert.IsNull(input.Speakers[2].Title);
    }

    // FR-141 AC-168: the text is never trusted (unknown keys, wrong types, too long, duplicate IDs, control characters).
    [TestMethod]
    public void TalkShowInputJsonRejectsMalformedAndOversizedText()
    {
        foreach (string bad in new[]
        {
            "", "[]", "{}", "{\"topics\":[],\"speakers\":[],\"x\":1}", "{\"topics\":{},\"speakers\":[]}",
            "{\"topics\":[{\"id\":\"a\",\"text\":5}],\"speakers\":[]}",
            "{\"topics\":[{\"id\":\"a\",\"text\":\"x\",\"origin\":\"guess\"}],\"speakers\":[]}",
            "{\"topics\":[{\"id\":\"a\",\"text\":\"x\"},{\"id\":\"a\",\"text\":\"y\"}],\"speakers\":[]}",
            "{\"topics\":[{\"id\":\"a\",\"text\":\"x\\u0007\"}],\"speakers\":[]}",
            "{\"topics\":[{\"id\":\"a\",\"text\":\"" + new string('あ', 201) + "\"}],\"speakers\":[]}",
            "{\"topics\":[],\"speakers\":[]}" + new string(' ', TalkShowInputJson.MaximumLength),
        })
        {
            Assert.IsFalse(TalkShowInputJson.TryParse(bad, out _), bad.Length > 80 ? bad[..80] : bad);
        }
    }

    // FR-141 AC-169: only entered / confirmed values reach the output; an unconfirmed title is dropped with a warning; no anonymous label.
    [TestMethod]
    public void OnlyEnteredOrConfirmedValuesAreShownInTheSideTelopsAndNameBands()
    {
        Assert.IsTrue(TalkShowInputJson.TryParse(Valid, out TalkShowInput input));
        TalkShowRenderPlan plan = TalkShowRenderPlanner.Plan(input, 20, 1920, 1080, "#FFFFFF");
        Assert.AreEqual("今日のテーマ", plan.SideTelops.Single().Text);
        Assert.AreEqual("top-left", plan.SideTelops.Single().Position);
        Assert.IsTrue(plan.SideTelops.Single().Confirmed);
        Assert.AreEqual(0, plan.SideTelops.Single().Start);
        Assert.AreEqual(20, plan.SideTelops.Single().End);
        Assert.AreEqual(2, plan.NameBands.Count);
        TalkShowNameBandRecord first = plan.NameBands.Single(static band => band.SpeakerId == "speaker-1");
        Assert.AreEqual("代表取締役", first.Title);
        Assert.IsTrue(first.TitleConfirmed);
        TalkShowNameBandRecord second = plan.NameBands.Single(static band => band.SpeakerId == "speaker-2");
        Assert.IsNull(second.Title);
        Assert.IsFalse(second.TitleConfirmed);
        Assert.AreEqual("佐藤花子", second.Text);
        Assert.IsFalse(plan.NameBands.Any(static band => band.SpeakerId == "speaker-3"), "an anonymous label never reaches a band");
        Assert.IsTrue(plan.Warnings.Any(static warning => warning.Contains("肩書き") && warning.Contains(TalkShowPlan.TitleUnconfirmedWarning)));
        Assert.IsTrue(plan.Warnings.Any(static warning => warning.Contains(TalkShowPlan.TopicUnconfirmedWarning)));
        Assert.IsTrue(first.End <= second.Start, "the bands follow one another");
    }

    // FR-138 AC-157: a topic's side telop lasts until the next topic starts; a vertical output places it inside the margins.
    [TestMethod]
    public void SideTelopsFollowOneAnotherAndStayInsideTheVerticalMargins()
    {
        var input = new TalkShowInput(
            [new("t1", "一つ目", 0, TalkShowValueOrigin.UserEntered, false), new("t2", "二つ目", 1, TalkShowValueOrigin.UserEntered, false)], []);
        TalkShowRenderPlan plan = TalkShowRenderPlanner.Plan(input, 30, 1080, 1920, "#FFFFFF");
        Assert.AreEqual(2, plan.SideTelops.Count);
        Assert.AreEqual(plan.SideTelops[0].End, plan.SideTelops[1].Start);
        Assert.AreEqual(30, plan.SideTelops[1].End);
        StyleTemplateRect safe = StyleTemplateLayout.SafeArea(1080, 1920, StyleTemplateMargins.Vertical);
        Assert.IsTrue(plan.SideTelops.All(telop => safe.Contains(telop.Box.ToRect())));
    }

    // FR-141 AC-170: changing a confirmed value changes the plan; the old value does not remain.
    [TestMethod]
    public void ChangedConfirmedValuesTakeEffectAndTheOldOnesDoNotRemain()
    {
        Assert.IsTrue(TalkShowInputJson.TryParse(Valid, out TalkShowInput input));
        TalkShowInput changed = input.WithSpeaker(input.Speakers[0] with { Name = "鈴木一郎", Title = "会長" });
        TalkShowRenderPlan plan = TalkShowRenderPlanner.Plan(changed, 20, 1920, 1080, "#FFFFFF");
        TalkShowNameBandRecord band = plan.NameBands.Single(static item => item.SpeakerId == "speaker-1");
        Assert.AreEqual("鈴木一郎　会長", band.Text);
        Assert.IsFalse(plan.NameBands.Any(static item => item.Text.Contains("山田") || item.Text.Contains("代表取締役")));
    }

    // FR-141: text that does not fit the frame is held back with a warning instead of failing the Job.
    [TestMethod]
    public void TextTooLongForTheFrameIsHeldBackWithAWarning()
    {
        var input = new TalkShowInput([new("t1", new string('あ', 150), 0, TalkShowValueOrigin.UserEntered, false)],
            [new("s1", new string('名', 100), TalkShowValueOrigin.UserEntered, false, null, false, null)]);
        TalkShowRenderPlan plan = TalkShowRenderPlanner.Plan(input, 20, 1920, 1080, "#FFFFFF");
        Assert.AreEqual(0, plan.SideTelops.Count);
        Assert.AreEqual(0, plan.NameBands.Count);
        Assert.AreEqual(2, plan.Warnings.Count);
    }

    private static StyleTemplateRenderRecord Record(int width, int height) => new("talk-show", 1, width, height, "#FFFFFF", "#FFFF00", "#FFFFFF",
        false, [new RecordedCue(0, 2, "字幕", null, null, "#FFFFFF", null, [])], [], []);

    // FR-143 AC-174 / AC-175: the record states the rule, the region, the margins, the positions and the verdict.
    [TestMethod]
    public void VerticalRecordPassesInsideTheMarginsAndTheRegionWithItsEvidence()
    {
        StyleTemplateRect box = StyleTemplateLayout.AllowedArea(1080, 1920, StyleTemplateMargins.Vertical, new(0, 0, 1080, 1920));
        StyleTemplateRenderRecord record = Record(1080, 1920).WithPlacement(null,
            [new("EmphasisTelop", "強調", 1, 2, new TalkShowBox(200, 700, 600, 300))], TalkShowBox.From(box with { Top = box.Bottom - 100 }),
            StyleTemplateMargins.Vertical, platformUiConfirmed: false);
        Assert.AreEqual("pass", record.OutputInspection!.Verdict);
        Assert.AreEqual("template-margins-and-region", record.OutputInspection.Rule);
        Assert.AreEqual(480, record.Margins!.Bottom);
        Assert.AreEqual(250, record.OutputInspection.Margins!.Top);
        Assert.AreEqual(1080, record.Output!.Width);
        Assert.AreEqual(1920, record.SubtitleArea!.Height);
        Assert.IsTrue(record.OutputInspection.Items.All(static item => item.Ok));
        Assert.IsTrue(record.OutputInspection.Warnings.Contains(VerticalPlacementEvidence.PlatformUiUnconfirmedWarning));
        Assert.IsNotNull(record.Subtitles[0].Box);
    }

    // FR-143 AC-174 / AC-175: a subtitle inside the margin fails the inspection and the record says so.
    [TestMethod]
    public void VerticalRecordFailsWhenTheSubtitleIsInsideTheMargin()
    {
        StyleTemplateRenderRecord record = Record(1080, 1920).WithPlacement(null, [],
            TalkShowBox.From(new(160, 1480, 920, 1600)), StyleTemplateMargins.Vertical, platformUiConfirmed: true);
        Assert.AreEqual("fail", record.OutputInspection!.Verdict);
        Assert.IsFalse(record.OutputInspection.Items.Single().InsideMargins);
        Assert.IsTrue(record.OutputInspection.Items.Single().InsideRegion);
        Assert.AreEqual(0, record.OutputInspection.Warnings.Count);
        Assert.IsTrue(record.Warnings.Any(static warning => warning.Contains("不合格")));
        string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        StringAssert.Contains(json, "\"verdict\":\"fail\"");
        StringAssert.Contains(json, "\"subtitleArea\"");
    }

    // FR-143: the user's move of the subtitle into the margin is an explicit, recorded adjustment (never a default).
    [TestMethod]
    public void SubtitleIntoMarginIsAnExplicitRecordedAdjustment()
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.Vertical3TierId)!;
        JobStyleTemplateRecord plain = JobStyleTemplateRecord.For(template);
        Assert.IsFalse(plain.SubtitleIntoMargin);
        JobStyleTemplateRecord moved = plain.Adjust(JobStyleTemplateRecord.SubtitleTarget, "position", JobStyleTemplateRecord.MarginPosition);
        Assert.IsTrue(moved.SubtitleIntoMargin);
        Assert.IsTrue(moved.IsModified);
        Assert.AreEqual(JobStyleTemplateRecord.ModifiedLabel, moved.Label);
    }

    // FR-136 / FR-138: the stored subtitle file of a template Job may differ from the plain file only in the closed set of template tags.
    [TestMethod]
    public void TemplateSubtitleBytesDifferFromThePlainFileOnlyInTheTemplateTags()
    {
        const string head = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
        static byte[] Bytes(string dialogue) => System.Text.Encoding.UTF8.GetBytes(head + dialogue + "\n");
        byte[] plain = Bytes("Dialogue: 0,0:00:01.00,0:00:03.00,Normal,,0,0,0,,{\\an5\\pos(960,945)\\clip(240,864,1680,1026)\\q2}字幕");
        byte[] styled = Bytes("Dialogue: 0,0:00:01.00,0:00:03.00,Normal,,0,0,0,,{\\an5\\pos(540,1500)\\clip(160,1440,920,1560)\\q2\\fnRounded Mplus 1c\\1c&HFFFFFF&\\3c&H1A1A1A&\\fad(250,0)}字幕");
        Assert.IsTrue(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(plain, plain, 1920, 1080));
        Assert.IsTrue(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(styled, plain, 1080, 1920));
        // another text, another time, a clip outside the frame, an unknown tag or a second Dialogue line is not accepted
        Assert.IsFalse(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(Bytes(System.Text.Encoding.UTF8.GetString(styled).Replace("}字幕", "}別の字幕").TrimEnd('\n')), plain, 1080, 1920));
        Assert.IsFalse(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(Bytes(System.Text.Encoding.UTF8.GetString(styled).Replace("0:00:03.00", "0:00:04.00").TrimEnd('\n')), plain, 1080, 1920));
        Assert.IsFalse(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(styled, plain, 900, 1920));
        Assert.IsFalse(HubRadio.App.Rendering.StandardTemplateSubtitleBytes.Matches(
            Bytes(System.Text.Encoding.UTF8.GetString(styled).Replace("\\fad(250,0)", "\\move(1,2,3,4)").TrimEnd('\n')), plain, 1080, 1920));
    }

    // FR-143 AC-173/175: the default vertical subtitle box is inside the margins; the into-margin box is not.
    [TestMethod]
    public void VerticalSubtitleBoxIsInsideTheMarginsUnlessMovedIntoThem()
    {
        StyleTemplateRect safe = StyleTemplateLayout.SafeArea(1080, 1920, StyleTemplateMargins.Vertical);
        Assert.IsTrue(safe.Contains(HubRadio.App.Rendering.StandardTemplateSubtitles.VerticalBox(1080, 1920, null, false)));
        Assert.IsFalse(safe.Contains(HubRadio.App.Rendering.StandardTemplateSubtitles.VerticalBox(1080, 1920, null, true)));
    }

    // FR-138 AC-158: a vertical 3-tier record states the three tiers top to bottom, the title tier from the top margin, the source
    // in the middle, and (no hook is selected here) adds nothing at the start and records a warning.
    [TestMethod]
    public void VerticalThreeTierRecordStatesTheTiersAndWarnsWithoutAHook()
    {
        StyleTemplateRect caption = StyleTemplateLayout.VerticalFrame().CaptionTier;
        var record = new StyleTemplateRenderRecord("vertical-3-tier", 1, 1080, 1920, "#FFFFFF", "#FFFF00", "#FFFFFF", false,
            [new RecordedCue(0, 2, "字幕", null, null, "#FFFFFF", null, [])], [], [])
            .WithPlacement(null, [new("Title", "題名", 0, 2, TalkShowBox.From(StyleTemplateLayout.Rect(StyleTemplateFreeTelopKind.Title, 1080, 1920, true)))],
                TalkShowBox.From(caption), StyleTemplateMargins.Vertical, platformUiConfirmed: false);
        RecordedVerticalLayout layout = record.Layout!;
        Assert.IsTrue(layout.Top.Y >= 250 && layout.Top.Y < layout.Middle.Y && layout.Middle.Y < layout.Bottom.Y);
        Assert.IsNotNull(layout.Top.Title);
        Assert.AreEqual(1080, layout.Middle.Source.Width);
        Assert.AreEqual("字幕", layout.Bottom.Subtitles.Single().Text);
        Assert.IsFalse(record.Hook!.Selected);
        Assert.IsNull(record.Hook.SegmentId);
        Assert.AreEqual("program", record.Timeline[0].Source);
        Assert.IsTrue(record.Warnings.Contains(StyleTemplateRenderRecord.HookNotSelectedWarning));
        Assert.AreEqual("pass", record.OutputInspection!.Verdict);

        // Another template (or a landscape output) has no tiers and no hook warning.
        StyleTemplateRenderRecord plain = Record(1920, 1080).WithPlacement(null, [], null, null, platformUiConfirmed: false);
        Assert.IsNull(plain.Layout);
        Assert.AreEqual(0, plain.Timeline.Count);
        Assert.IsFalse(plain.Warnings.Contains(StyleTemplateRenderRecord.HookNotSelectedWarning));
    }

    // FR-143 (c) AC-175: OutputQc carries the inspection evidence; a position outside the rule fails the inspection.
    [TestMethod]
    public void OutputQcPlacementEvidenceCarriesTheRuleRegionMarginsPositionsAndVerdict()
    {
        StyleTemplateRenderRecord passed = Record(1080, 1920).WithPlacement(null, [],
            TalkShowBox.From(StyleTemplateLayout.VerticalFrame().CaptionTier), StyleTemplateMargins.Vertical, platformUiConfirmed: true);
        HubRadio.Infrastructure.Rendering.OutputQcPlacementEvidence ok = HubRadio.App.Rendering.StandardRenderGraphFactory.OutputQcPlacement(passed.OutputInspection!);
        Assert.IsTrue(ok.Passed);
        Assert.AreEqual("pass", ok.Verdict);
        Assert.AreEqual("template-margins-and-region", ok.Rule);
        Assert.AreEqual(250, ok.Margins!.Top);
        Assert.AreEqual(480, ok.Margins.Bottom);
        Assert.AreEqual(1920, ok.Region.Height);
        Assert.AreEqual("subtitle", ok.Items.Single().Id);

        StyleTemplateRenderRecord failed = Record(1080, 1920).WithPlacement(null, [],
            TalkShowBox.From(new(160, 1480, 920, 1600)), StyleTemplateMargins.Vertical, platformUiConfirmed: true);
        HubRadio.Infrastructure.Rendering.OutputQcPlacementEvidence bad = HubRadio.App.Rendering.StandardRenderGraphFactory.OutputQcPlacement(failed.OutputInspection!);
        Assert.IsFalse(bad.Passed);
        Assert.AreEqual("fail", bad.Verdict);
        Assert.IsFalse(bad.Items.Single().InsideMargins);
    }
}
