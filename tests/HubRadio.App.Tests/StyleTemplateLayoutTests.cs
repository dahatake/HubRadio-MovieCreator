using System.Text;
using System.Text.RegularExpressions;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Compositions;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

/// <summary>FR-136 wiring (AC-150/151/152) and FR-138 / FR-139 (AC-156/157/158/159/161) for the production templates.</summary>
[TestClass]
public sealed class StyleTemplateLayoutTests
{
    private static readonly OriginRef Origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));

    // ---- FR-136 (AC-151, AC-152): the burned-in output follows the template, and no template keeps the default ----

    private static async Task<(string Plain, string Templated)> BurnedInAssAsync(string templateId, bool vertical)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: vertical);
        StandardRenderConfiguration configuration = fixture.Request().Configuration;
        StandardRenderGraphOutput plain;
        StandardRenderGraphOutput templated;
        if (vertical)
        {
            StandardShortArtifactInput input = fixture.Shorts.Single();
            plain = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
                input.Subtitles, fixture.Font.Evidence, input.ShortFormId, configuration);
            templated = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
                input.Subtitles, fixture.Font.Evidence, input.ShortFormId,
                configuration.WithStyleTemplate(StyleTemplateCatalog.Find(templateId)));
        }
        else
        {
            plain = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
                fixture.Subtitles, fixture.Font.Evidence, configuration: configuration);
            templated = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
                fixture.Subtitles, fixture.Font.Evidence, configuration: configuration.WithStyleTemplate(StyleTemplateCatalog.Find(templateId)));
        }

        return (Ass(plain), Ass(templated));
    }

    private static string Ass(StandardRenderGraphOutput output) =>
        string.Concat(output.SubtitleFiles.Select(static file => Encoding.UTF8.GetString(file.File.GetUtf8Bytes())))
        + string.Concat(output.GraphicFiles.Select(static file => Encoding.UTF8.GetString(file.File.GetUtf8Bytes())));

    // FR-136 AC-151: no template keeps the default subtitle, telop and sound output (byte for byte).
    [TestMethod]
    [TestCategory("FR-136")]
    public async Task WithoutATemplateTheBurnedInOutputIsTheDefaultOne()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardRenderGraphOutput baseline = fixture.Graph(true);
        StandardRenderGraphOutput withConfiguration = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset,
            fixture.Geometry, true, fixture.Subtitles, fixture.Font.Evidence, configuration: fixture.Request().Configuration);
        CollectionAssert.AreEqual(baseline.SubtitleFiles.Select(static item => item.File.ContentDigest.Hex).ToArray(),
            withConfiguration.SubtitleFiles.Select(static item => item.File.ContentDigest.Hex).ToArray());
        Assert.IsNull(fixture.Request().Configuration.StyleTemplate);
        Assert.IsFalse(Ass(baseline).Contains(@"\1c&H", StringComparison.Ordinal));
    }

    // FR-136 AC-150 / FR-137 AC-153: the selected template reaches the burned-in subtitle (color) without any setting.
    [TestMethod]
    [TestCategory("FR-136")]
    public async Task ASelectedTemplateColorsTheBurnedInSubtitle()
    {
        (string plain, string templated) = await BurnedInAssAsync(StyleTemplateCatalog.TalkShowId, vertical: false);
        Assert.IsFalse(plain.Contains(@"\1c&H", StringComparison.Ordinal));
        StringAssert.Contains(templated, @"\1c&HFFFFFF&");
        StringAssert.Contains(templated, @"\3c&H1A1A1A&");
    }

    // FR-137 / FR-138 AC-156 / AC-158: the short-pop template burns 1-3 word captions that shrink from 130% to 100%,
    // and every line of a vertical output stays inside the PARAM-005 margins.
    [TestMethod]
    [TestCategory("FR-137")]
    [TestCategory("FR-138")]
    public async Task VerticalShortPopBurnsShrinkingWordCaptionsInsideTheMargins()
    {
        (_, string templated) = await BurnedInAssAsync(StyleTemplateCatalog.ShortPopId, vertical: true);
        StringAssert.Contains(templated, @"\fscx130\fscy130");
        StringAssert.Contains(templated, @"\fscx100\fscy100");
        MatchCollection clips = Regex.Matches(templated, @"\\clip\((\d+),(\d+),(\d+),(\d+)\)");
        Assert.IsGreaterThan(0, clips.Count);
        foreach (Match clip in clips)
        {
            int left = int.Parse(clip.Groups[1].Value), top = int.Parse(clip.Groups[2].Value);
            int right = int.Parse(clip.Groups[3].Value), bottom = int.Parse(clip.Groups[4].Value);
            Assert.IsTrue(left >= 160 && top >= 250 && right <= 1080 - 160 && bottom <= 1920 - 480, clip.Value);
        }
    }

    [TestMethod]
    [TestCategory("FR-138")]
    public async Task VerticalGraphicsAndTelopsStayInsideTheMarginsForEveryVerticalTemplate()
    {
        foreach (string id in new[] { StyleTemplateCatalog.ShortPopId, StyleTemplateCatalog.Vertical3TierId })
        {
            using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
            StandardShortArtifactInput input = fixture.Shorts.Single();
            StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
                input.Subtitles, fixture.Font.Evidence, input.ShortFormId,
                fixture.Request().Configuration.WithStyleTemplate(StyleTemplateCatalog.Find(id)));
            Assert.AreEqual(1080, output.ValidationContext.Profile.Width);
            Assert.AreEqual(1920, output.ValidationContext.Profile.Height);
            foreach (StandardGraphicRenderFile graphic in output.GraphicFiles)
            {
                StandardTextOverlayEvidence e = graphic.Evidence;
                Assert.IsTrue(e.Left >= 160 && e.Top >= 250 && e.Right <= 920 && e.Bottom <= 1440, $"{id} {e.Role}");
            }
        }
    }

    // FR-137: the telop color is the template's emphasis color and reads on its outline at 4.5:1.
    [TestMethod]
    [TestCategory("FR-139")]
    public void EveryTemplateTextReadsOnItsBackgroundAtFourPointFiveToOne()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            string[] foregrounds = [template.TextColor, .. template.EmphasisColors];
            string background = StyleTemplateColor.EnsureBackground(foregrounds, template.OutlineColor);
            foreach (string foreground in foregrounds)
            {
                Assert.IsGreaterThanOrEqualTo(StyleTemplateColor.MinimumContrast, StyleTemplateColor.Contrast(foreground, background),
                    $"{template.Id} {foreground} on {background}");
            }
        }
    }

    // FR-139 AC-159: the emphasis is also told by weight and size (bold, larger than the subtitle), never by color alone.
    [TestMethod]
    [TestCategory("FR-139")]
    public async Task EmphasisIsBoldAndLargerThanTheSubtitleAndMovesWithTheTemplate()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, false,
            fixture.Subtitles, fixture.Font.Evidence,
            configuration: fixture.Request().Configuration.WithStyleTemplate(StyleTemplateCatalog.Find(StyleTemplateCatalog.ShortPopId)));
        StandardGraphicRenderFile[] emphasis = output.GraphicFiles.Where(static item => item.Evidence.Role == "EmphasisTelop").ToArray();
        foreach (StandardGraphicRenderFile telop in emphasis)
        {
            string ass = Encoding.UTF8.GetString(telop.File.GetUtf8Bytes());
            StringAssert.Contains(ass, ",-1,0,0,0,100,100,"); // bold
            Assert.IsGreaterThan(64, telop.Evidence.FontSize);  // larger than the 64 px subtitle band text
            StringAssert.Contains(ass, @"\fscx130");           // and it moves
        }
    }

    // ---- FR-136 AC-152: the Job records the template ID and version, and an adjusted Job shows 「テンプレート＋変更あり」 ----

    [TestMethod]
    [TestCategory("FR-136")]
    public void TheJobRecordKeepsTheTemplateAndShowsTheAdjustedLabel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "style-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.TalkShowId)!;
            JobStyleTemplateStore.Save(directory, JobStyleTemplateRecord.For(template));
            JobStyleTemplateRecord loaded = JobStyleTemplateStore.Load(directory)!;
            Assert.AreEqual(template.Reference, $"{loaded.TemplateId}@{loaded.Version}");
            Assert.AreEqual(JobStyleTemplateRecord.AppliedLabel, loaded.Label);
            JobStyleTemplateStore.Save(directory, loaded.Adjust("telop-1", "color", "#00FF00"));
            Assert.AreEqual(JobStyleTemplateRecord.ModifiedLabel, JobStyleTemplateStore.Load(directory)!.Label);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- FR-138 AC-157 (4): topic side telop and the name band ----

    [TestMethod]
    [TestCategory("FR-138")]
    public void ATopicSideTelopLastsUntilTheTopicChangesAndTheOldOneIsGone()
    {
        StyleTemplateTopic[] topics =
        [
            new("料金の話", new RationalTime(0, 1), StyleTemplateTextSource.UserInput, Origin),
            new("導入の流れ", new RationalTime(30, 1), StyleTemplateTextSource.UserInput, Origin),
        ];
        IReadOnlyList<StyleTemplateFreeTelopInput> telops = StyleTemplateLayout.TopicTelops(topics, new RationalTime(60, 1));
        Assert.HasCount(2, telops);
        foreach (long second in new long[] { 0, 10, 29 })
        {
            Assert.AreEqual("料金の話", StyleTemplateLayout.ShownAt(telops, static item => item.Range, new RationalTime(second, 1)).Single().Text);
        }

        foreach (long second in new long[] { 30, 45, 59 })
        {
            Assert.AreEqual("導入の流れ", StyleTemplateLayout.ShownAt(telops, static item => item.Range, new RationalTime(second, 1)).Single().Text);
        }

        Assert.IsEmpty(StyleTemplateLayout.ShownAt(telops, static item => item.Range, new RationalTime(60, 1)));
        // The template accepts both (the talk-show template has the topic telop and the band).
        StyleTemplatePlan plan = StyleTemplateApplier.Apply(StyleTemplateCatalog.Find(StyleTemplateCatalog.TalkShowId)!,
            [new(new StandardDisplayedCueText(Range(2, 3), "こんにちは", Origin), [])], telops,
            new(new(2, 16, new RationalTime(5, 6), new RationalTime(7, 1)), "standard", true, new RationalTime(60, 1)));
        Assert.AreEqual(2, plan.FreeTelops.Count(static item => item.Input.Kind == StyleTemplateFreeTelopKind.Topic));
    }

    [TestMethod]
    [TestCategory("FR-138")]
    public void TheNameBandShowsOnlyEnteredOrConfirmedNamesAndNeverGuessesOrAnonymousLabels()
    {
        Assert.AreEqual("山田 太郎　営業部長", StyleTemplateLayout.BandText(new("山田 太郎", StyleTemplatePersonSource.UserInput,
            "営業部長", StyleTemplatePersonSource.Confirmed)));
        // The title is unconfirmed: only the name is shown.
        Assert.AreEqual("山田 太郎", StyleTemplateLayout.BandText(new("山田 太郎", StyleTemplatePersonSource.UserInput,
            "営業部長", StyleTemplatePersonSource.Unconfirmed)));
        // No input / a system guess / an anonymous label: no band.
        Assert.IsNull(StyleTemplateLayout.BandText(new(null, StyleTemplatePersonSource.UserInput, "部長", StyleTemplatePersonSource.UserInput)));
        Assert.IsNull(StyleTemplateLayout.BandText(new("佐藤", StyleTemplatePersonSource.Unconfirmed, null, StyleTemplatePersonSource.UserInput)));
        Assert.IsNull(StyleTemplateLayout.BandText(new("話者1", StyleTemplatePersonSource.UserInput, null, StyleTemplatePersonSource.UserInput)));
        Assert.IsNull(StyleTemplateLayout.BandText(new("Speaker 2", StyleTemplatePersonSource.Confirmed, null, StyleTemplatePersonSource.UserInput)));

        StyleTemplateSpeakerTurn[] turns =
        [
            new(new("山田 太郎", StyleTemplatePersonSource.UserInput, null, StyleTemplatePersonSource.UserInput), Range(0, 10), Origin),
            new(new(null, StyleTemplatePersonSource.UserInput, null, StyleTemplatePersonSource.UserInput), Range(10, 10), Origin),
            new(new("話者2", StyleTemplatePersonSource.UserInput, null, StyleTemplatePersonSource.UserInput), Range(20, 10), Origin),
        ];
        IReadOnlyList<StyleTemplateFreeTelopInput> bands = StyleTemplateLayout.BandTelops(turns);
        Assert.AreEqual("山田 太郎", bands.Single().Text);
        Assert.AreEqual(StyleTemplateFreeTelopKind.Band, bands.Single().Kind);
    }

    // ---- FR-138 AC-156 / AC-158 (5): vertical three tiers, margins, hook first, 15-20 characters ----

    [TestMethod]
    [TestCategory("FR-138")]
    public void TheVerticalFrameHasTitleOnTopVideoInTheMiddleAndTheSubtitleBelowInsideTheMargins()
    {
        StyleTemplateVerticalFrame frame = StyleTemplateLayout.VerticalFrame();
        StyleTemplateRect allowed = StyleTemplateLayout.AllowedArea(1080, 1920, StyleTemplateMargins.Vertical, null);
        Assert.AreEqual(new StyleTemplateRect(160, 250, 920, 1440), allowed);
        Assert.IsGreaterThanOrEqualTo(250, frame.TitleTier.Top);
        Assert.IsLessThanOrEqualTo(frame.VideoTier.Top, frame.TitleTier.Bottom);
        Assert.IsLessThanOrEqualTo(frame.CaptionTier.Top, frame.VideoTier.Bottom);
        Assert.IsTrue(allowed.Contains(frame.TitleTier) && allowed.Contains(frame.CaptionTier) && allowed.Contains(frame.BandRow));

        // Every placement the template makes is inside the margins and inside the FR-014 subtitle region.
        StyleTemplateRect region = new(100, 200, 980, 1500);
        var placed = new List<StyleTemplatePlacedTelop>();
        foreach (StyleTemplateFreeTelopKind kind in new[] { StyleTemplateFreeTelopKind.Title, StyleTemplateFreeTelopKind.Hook,
            StyleTemplateFreeTelopKind.Topic, StyleTemplateFreeTelopKind.Band })
        {
            placed.Add(StyleTemplateLayout.Place(new(kind, "x", Range(0, 3), StyleTemplateTextSource.UserInput, Origin), 1080, 1920, vertical: true));
        }

        Assert.IsEmpty(StyleTemplateLayout.OutsideAllowedArea(placed, 1080, 1920, StyleTemplateMargins.Vertical, region));
        Assert.IsNotEmpty(StyleTemplateLayout.OutsideAllowedArea(
            [new(StyleTemplateFreeTelopKind.Title, "x", Range(0, 3), new StyleTemplateRect(100, 100, 900, 300))],
            1080, 1920, StyleTemplateMargins.Vertical, region));
        Assert.AreEqual(250, StyleTemplateLayout.Rect(StyleTemplateFreeTelopKind.Hook, 1080, 1920, vertical: true).Top);
    }

    [TestMethod]
    [TestCategory("FR-138")]
    public void AVerticalSubtitleScreenHoldsAtMostTwentyCharactersAndKeepsTheText()
    {
        const string text = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもや";
        IReadOnlyList<string> screens = StyleTemplateLayout.VerticalScreens(text);
        Assert.IsGreaterThan(1, screens.Count);
        Assert.AreEqual(text, string.Concat(screens));
        Assert.IsTrue(screens.All(static item => item.Length <= StyleTemplateLayout.VerticalCaptionMaximumCharacters));
        Assert.IsTrue(screens.Take(screens.Count - 1).All(static item => item.Length >= 15));
        Assert.HasCount(1, StyleTemplateLayout.VerticalScreens("短い字幕"));
    }

    [TestMethod]
    [TestCategory("FR-138")]
    public async Task TheVerticalThreeTierOutputIs1080By1920WithTheFifteenToTwentyCharacterSubtitle()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        StandardShortArtifactInput input = fixture.Shorts.Single();
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
            input.Subtitles, fixture.Font.Evidence, input.ShortFormId,
            fixture.Request().Configuration.WithStyleTemplate(StyleTemplateCatalog.Find(StyleTemplateCatalog.Vertical3TierId)));
        Assert.AreEqual(1080, output.ValidationContext.Profile.Width);
        Assert.AreEqual(1920, output.ValidationContext.Profile.Height);
        string ass = Ass(output);
        foreach (Match line in Regex.Matches(ass, @"Dialogue: 0,[^,]*,[^,]*,Normal,,0,0,0,,\{[^}]*\}([^\r\n]*)"))
        {
            string visible = Regex.Replace(line.Groups[1].Value, @"\{[^}]*\}", "").Replace(@"\N", "");
            Assert.IsLessThanOrEqualTo(StyleTemplateLayout.VerticalCaptionMaximumCharacters, visible.Length, visible);
        }
    }

    [TestMethod]
    [TestCategory("FR-138")]
    public void TheOutputOpensWithTheHookTheShortChose()
    {
        TimeRange hook = Range(40, 5);
        IReadOnlyList<TimeRange> ranges = StyleTemplateLayout.HookFirst(hook, [Range(0, 20), Range(20, 20)]);
        Assert.IsTrue(StyleTemplateLayout.StartsWithHook(ranges, hook));
        Assert.IsFalse(StyleTemplateLayout.StartsWithHook([Range(0, 20), hook], hook));
    }

    // ---- FR-139 AC-159 / AC-161 ----

    [TestMethod]
    [TestCategory("FR-139")]
    public void AutomaticSubtitlesKeepNegationsAndConditionsAndTheTextIsNotRewritten()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            string[] words = ["これは", "安全では", "ありません", "もし", "雨なら", "中止です"];
            long step = 50;
            var timed = words.Select((word, index) => new StyleTemplateWord(word,
                new TimeRange(new RationalTime(step * index, 100), new RationalTime(step, 100)))).ToArray();
            StyleTemplatePlan plan = StyleTemplateApplier.Apply(template,
                [new(new StandardDisplayedCueText(Range(0, 3), string.Concat(words), Origin), timed)], [],
                new(new(2, 16, new RationalTime(5, 6), new RationalTime(7, 1)), "many", true, new RationalTime(30, 1)));
            Assert.AreEqual(string.Concat(words), string.Concat(plan.Captions.Select(static item => item.Text)), template.Id);
            StringAssert.Contains(string.Concat(plan.Captions.Select(static item => item.Text)), "ありません");
        }
    }

    [TestMethod]
    [TestCategory("FR-139")]
    public void AFontIsRecordedWithItsVersionAndLicenseAndOneThatMayNotBeBurnedInIsRefused()
    {
        JobStyleTemplateRecord record = JobStyleTemplateRecord.For(StyleTemplateCatalog.Find(StyleTemplateCatalog.ExplainerId)!);
        JobStyleTemplateRecord withFont = record.WithFonts([new("Example Sans", "1.002", "SIL OFL 1.1", true)]);
        Assert.AreEqual("SIL OFL 1.1", withFont.Fonts!.Single().License);
        Assert.ThrowsExactly<ArgumentException>(() => record.WithFonts([new("Example Sans", "1.002", "SIL OFL 1.1", false)]));
        Assert.ThrowsExactly<ArgumentException>(() => record.WithFonts([new("Example Sans", "", "SIL OFL 1.1", true)]));
    }

    [TestMethod]
    [TestCategory("FR-139")]
    public void SoundEffectsAreOnlyTheTwoSynthesizedOnesAndOnlyWhenTheSettingIsOn()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            StyleTemplateInputCue[] cues = [new(new StandardDisplayedCueText(Range(2, 3), "ここが大事なポイントです", Origin), [])];
            StyleTemplateApplyOptions on = new(new(2, 16, new RationalTime(5, 6), new RationalTime(7, 1)), "many", true, new RationalTime(30, 1));
            StyleTemplatePlan enabled = StyleTemplateApplier.Apply(template, cues, [], on);
            Assert.IsTrue(enabled.SoundEffects.All(static item => item.Kind is StandardSoundEffectKind.Transition or StandardSoundEffectKind.Emphasis), template.Id);
            Assert.IsEmpty(StyleTemplateApplier.Apply(template, cues, [], on with { SoundEffectsEnabled = false }).SoundEffects, template.Id);
        }
    }

    // AC-161: the list, descriptions and users-guide text make no effect guarantee and no imitation claim.
    [TestMethod]
    [TestCategory("FR-139")]
    public void TemplateTextsMakeNoEffectGuaranteeAndNoImitationClaim()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            foreach (string text in StyleTemplateClaims.VisibleTexts(template))
            {
                Assert.IsEmpty(StyleTemplateClaims.Problems(text), $"{template.Id}: {text}");
            }
        }

        string[] bad =
        [
            "このテンプレートで視聴維持率が必ず上がります", "再生数を保証します", "視聴維持率を伸ばす", "人気チャンネルそっくりの見た目", "あの人の真似ができます",
        ];
        foreach (string text in bad)
        {
            Assert.IsNotEmpty(StyleTemplateClaims.Problems(text), text);
        }

        Assert.IsEmpty(StyleTemplateClaims.Problems("視聴維持率や再生数の向上を約束するものではありません。"));
        // The same check over the picker text the web list shows.
        string picker = File.ReadAllText(FindFile("src/HubRadio.Web/src/features/jobs/StyleTemplatePicker.tsx"));
        foreach (Match literal in Regex.Matches(picker, @">([^<>{}]*[ぁ-んァ-ン一-龥][^<>{}]*)<|'([^']*[ぁ-んァ-ン一-龥][^']*)'"))
        {
            Assert.IsEmpty(StyleTemplateClaims.Problems(literal.Value), literal.Value);
        }
    }

    // ---- FR-136 AC-150: the Job-creation request carries the selected template ID through the bridge ----

    [TestMethod]
    [TestCategory("FR-136")]
    public void TheRegenerateBridgeRequestAcceptsADefinedTemplateAndNoneAndRejectsAnUnknownOne()
    {
        string Json(string? template) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["projectId"] = EntityId<ProjectIdTag>.New().ToString(),
            ["expectedVersionId"] = EntityId<CompositionVersionIdTag>.New().ToString(),
            ["expectedVersionDigest"] = new string('a', 64),
            ["expectedSettingsRevision"] = 3,
            ["expectedSettingsSnapshotDigest"] = new string('b', 64),
        }.Concat(template is null ? [] : [new KeyValuePair<string, object?>("styleTemplateId", template)])
            .ToDictionary(static item => item.Key, static item => item.Value));
        bool Valid(string? template) => HubRadio.App.Web.SourceOnlyBridgeProtocol.HasValidParameters(
            HubRadio.App.Web.BridgeMethod.PipelineRegenerate, System.Text.Json.JsonDocument.Parse(Json(template)).RootElement);
        Assert.IsTrue(Valid(null));
        Assert.IsTrue(Valid(StyleTemplateCatalog.ShortPopId));
        Assert.IsFalse(Valid("no-such-template"));
    }

    private static string FindFile(string relative)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}
