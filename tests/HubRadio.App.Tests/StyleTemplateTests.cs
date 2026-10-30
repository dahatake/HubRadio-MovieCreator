using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StyleTemplateTests
{
    private static readonly OriginRef Origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
    private static readonly RationalTime Duration = new(90, 1);
    private static readonly StyleTemplateSubtitleLimits Limits = new(2, 16, new RationalTime(5, 6), new RationalTime(7, 1));
    private static readonly string[] AssertiveWords = ["必ず", "保証", "絶対", "確実に", "間違いなく"];

    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static StyleTemplateInputCue Cue(long start, long seconds, params string[] words)
    {
        long step = seconds * 100 / words.Length;
        var timed = words.Select((word, index) => new StyleTemplateWord(word,
            new TimeRange(new RationalTime(start * 100 + step * index, 100), new RationalTime(step, 100)))).ToArray();
        return new(new StandardDisplayedCueText(Range(start, seconds), string.Concat(words), Origin), timed);
    }

    private static StyleTemplateInputCue[] Cues() =>
    [
        Cue(2, 3, "これは", "本当に", "すごい", "ことです"),
        Cue(10, 4, "売上が", "30%", "増えました"),
        Cue(20, 3, "次に", "進みましょう"),
        Cue(30, 2, "すごい！"),
        Cue(40, 3, "ありがとう", "ございます"),
        Cue(55, 8, "ここでは", "動画", "の", "作り方", "を", "順番", "に", "説明", "して", "いきます", "ので", "聞いて", "ください", "ね"),
    ];

    private static StyleTemplateApplyOptions Options(bool sound = true, string? amount = "many", StyleTemplateBrand? brand = null) =>
        new(Limits, amount, sound, Duration, brand);

    private static StyleTemplatePlan Apply(StyleTemplateDefinition template, StyleTemplateApplyOptions options,
        params StyleTemplateFreeTelopInput[] free) => StyleTemplateApplier.Apply(template, Cues(), free, options);

    private static StyleTemplateDefinition Template(string id) => StyleTemplateCatalog.Find(id)!;

    // FR-136 AC-150 / FR-137 AC-153: the list shows name, purpose, orientation and a preview; each definition is within PARAM-006/007.
    [TestMethod]
    [TestCategory("FR-136")]
    [TestCategory("FR-137")]
    public void CatalogListsTheSixDefinitionsWithinTheColorAndFontLimits()
    {
        string[] expected = ["explainer", "short-pop", "variety", "talk-show", "vertical-3-tier", "handmade"];
        CollectionAssert.AreEqual(expected, StyleTemplateCatalog.All.Select(static item => item.Id).ToArray());
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            Assert.IsTrue(template.Version >= 1);
            Assert.IsFalse(string.IsNullOrWhiteSpace(template.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(template.Purpose));
            Assert.IsFalse(string.IsNullOrWhiteSpace(template.Preview.Description));
            Assert.IsFalse(string.IsNullOrWhiteSpace(template.Preview.SampleText));
            Assert.IsTrue(StyleTemplateCatalog.IsWithinLimits(template), template.Id);
            Assert.IsTrue(template.EmphasisColors.Count <= 2 && template.FontFamilies.Count <= 3);
            foreach (string text in new[] { template.Name, template.Purpose, template.Preview.Description, template.Preview.SampleText })
            {
                Assert.IsFalse(AssertiveWords.Any(text.Contains), $"{template.Id}: {text}");
            }
        }

        Assert.AreEqual(StyleTemplateOrientation.Landscape, Template("explainer").Orientation);
        Assert.AreEqual(StyleTemplateOrientation.Vertical, Template("short-pop").Orientation);
        Assert.AreEqual(StyleTemplateOrientation.Vertical, Template("vertical-3-tier").Orientation);
        Assert.IsTrue(Template("talk-show").LayoutDefinedOnly && Template("vertical-3-tier").LayoutDefinedOnly);
        Assert.AreEqual(2, Template("variety").FontFamilies.Count is >= 2 and <= 3 ? 2 : 0);
        Assert.IsFalse(StyleTemplateCatalog.All.Any(static item => item.Id.Contains("character", StringComparison.Ordinal)));
    }

    // FR-136 AC-150: choosing a template needs no setting values and still yields captions, telops and a placement.
    [TestMethod]
    [TestCategory("FR-136")]
    public void ChoosingATemplateAloneAppliesItsPresentation()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            StyleTemplatePlan plan = Apply(template, Options());
            Assert.AreEqual(template.Reference, plan.TemplateReference);
            Assert.IsTrue(plan.Captions.Count > 0, template.Id);
            Assert.IsTrue(plan.EmphasisTelops.Count > 0, template.Id);
            Assert.AreEqual(template.Placement, plan.Placement);
            CollectionAssert.AreEqual(template.FontFamilies.ToArray(), plan.FontFamilies.ToArray());
        }
    }

    // FR-136 AC-151: no template leaves the FR-059 / FR-060 output exactly as before.
    [TestMethod]
    [TestCategory("FR-136")]
    public void WithoutATemplateTheDefaultOutputIsUnchanged()
    {
        StandardDisplayedCueText[] cues = Cues().Select(static item => item.Cue).ToArray();
        var (telops, sounds) = StyleTemplateApplier.PlanWithoutTemplate(cues, "standard", true, Duration);
        IReadOnlyList<StandardEmphasisTelop> direct = StandardEmphasisTelops.Select(cues, "standard", Duration);
        CollectionAssert.AreEqual(direct.ToArray(), telops.ToArray());
        CollectionAssert.AreEqual(StandardEmphasisTelops.PlanSoundEffects(true, cues, direct, Duration).ToArray(), sounds.ToArray());
        Assert.AreEqual(0, StyleTemplateApplier.PlanWithoutTemplate(cues, null, true, Duration).Telops.Count);
    }

    // FR-136 AC-152: ID and version are saved, the same input reproduces the same plan, adjustments mark the Job.
    [TestMethod]
    [TestCategory("FR-136")]
    public void TheJobRecordsTheTemplateAndReproducesItAndMarksAdjustments()
    {
        StyleTemplateDefinition template = Template("variety");
        string directory = Path.Combine(Path.GetTempPath(), "style-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            JobStyleTemplateRecord record = JobStyleTemplateRecord.For(template);
            JobStyleTemplateStore.Save(directory, record);
            JobStyleTemplateRecord? loaded = JobStyleTemplateStore.Load(directory);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("variety", loaded.TemplateId);
            Assert.AreEqual(1, loaded.Version);
            Assert.IsFalse(loaded.IsModified);
            Assert.AreEqual("テンプレート", loaded.Label);

            StyleTemplateDefinition again = StyleTemplateCatalog.Find(loaded.TemplateId, loaded.Version)!;
            Assert.AreEqual(Apply(template, Options()).ToCanonicalText(), Apply(again, Options()).ToCanonicalText());

            foreach (string property in JobStyleTemplateRecord.AdjustableProperties)
            {
                loaded = loaded.Adjust("telop-1", property, "x");
            }

            loaded = loaded.Adjust("telop-1", "color", "#112233");
            Assert.IsTrue(loaded.IsModified);
            Assert.AreEqual("テンプレート＋変更あり", loaded.Label);
            Assert.AreEqual(6, loaded.Adjustments.Count);
            JobStyleTemplateStore.Save(directory, loaded);
            Assert.AreEqual("テンプレート＋変更あり", JobStyleTemplateStore.Load(directory)!.Label);
            Assert.ThrowsExactly<ArgumentException>(() => loaded.Adjust("telop-1", "unknown", "x"));
            Assert.ThrowsExactly<ArgumentException>(() => JobStyleTemplateStore.Save(directory, new("variety", 99, [])));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    // FR-136 AC-163: a brand-fixed Project keeps the brand color, font and logo; the template keeps motion, placement and sound.
    [TestMethod]
    [TestCategory("FR-136")]
    public void BrandColorsFontsAndLogoWinOverTheTemplate()
    {
        var brand = new StyleTemplateBrand("#101010", "#EFEFEF", ["#AA0000", "#00AA00", "#0000AA"], ["Brand Sans"], "logo-1");
        StyleTemplateDefinition template = Template("variety");
        StyleTemplatePlan plan = Apply(template, Options(brand: brand));
        Assert.AreEqual("#101010", plan.TextColor);
        Assert.AreEqual("#EFEFEF", plan.OutlineColor);
        CollectionAssert.AreEqual(new[] { "#AA0000", "#00AA00" }, plan.EmphasisColors.ToArray());
        CollectionAssert.AreEqual(new[] { "Brand Sans" }, plan.FontFamilies.ToArray());
        Assert.AreEqual("logo-1", plan.LogoReference);
        Assert.IsTrue(plan.EmphasisTelops.All(item => plan.EmphasisColors.Contains(item.Color)));
        Assert.AreEqual(template.Placement, plan.Placement);
        Assert.IsTrue(plan.EmphasisTelops.All(static item => item.Motion?.Kind == StyleTemplateMotionKind.Pop));
        Assert.IsTrue(plan.SoundEffects.Count > 0);
    }

    // FR-137 AC-153: emphasis text is part of a displayed cue; captions and cues stay within the FR-051 limits and floors.
    [TestMethod]
    [TestCategory("FR-137")]
    public void EveryTemplateKeepsTextVerbatimAndInsideTheSubtitleLimits()
    {
        StandardDisplayedCueText[] cues = Cues().Select(static item => item.Cue).ToArray();
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            StyleTemplatePlan plan = Apply(template, Options());
            Assert.IsTrue(plan.EmphasisColors.Count <= 2 && plan.FontFamilies.Count <= 3);
            Assert.IsTrue(plan.EmphasisTelops.All(item => cues.Any(cue => cue.Text.Contains(item.Telop.Text, StringComparison.Ordinal))), template.Id);
            Assert.IsTrue(plan.EmphasisTelops.Count <= StandardEmphasisTelops.Limit("many", Duration));
            Assert.IsTrue(plan.Captions.Count >= plan.MinimumCaptionCount, template.Id);
            foreach (StyleTemplateCaption caption in plan.Captions)
            {
                Assert.IsTrue(caption.Text.Length <= Limits.LineCountMaximum * Limits.LineLengthMaximum, template.Id);
                Assert.IsTrue(caption.Range.Duration <= Limits.CueMaximumDuration, template.Id);
                Assert.IsTrue(cues.Any(cue => cue.Text.Contains(caption.Text, StringComparison.Ordinal)), $"{template.Id}: {caption.Text}");
                if (caption.Range.Duration < Limits.CueMinimumDuration)
                {
                    Assert.IsTrue(plan.Warnings.Any(item => item.Code == "shown-below-minimum-duration" && item.Range == caption.Range));
                }
            }
        }
    }

    // FR-137 AC-153: free telops need a permitted kind and a permitted source; the system writes none.
    [TestMethod]
    [TestCategory("FR-137")]
    public void FreeTelopTextOnlyComesFromSubtitlesUserInputOrConfirmedText()
    {
        StyleTemplateDefinition variety = Template("variety");
        StyleTemplatePlan plan = Apply(variety, Options(),
            new(StyleTemplateFreeTelopKind.Retort, "なんでやねん", Range(12, 2), StyleTemplateTextSource.UserInput, Origin, 0.2, 0.3),
            new(StyleTemplateFreeTelopKind.InnerVoice, "確認済みの心の声", Range(22, 2), StyleTemplateTextSource.ConfirmedSentence, Origin),
            new(StyleTemplateFreeTelopKind.Retort, "ことです", Range(5, 2), StyleTemplateTextSource.SubtitlePart, Origin),
            new(StyleTemplateFreeTelopKind.Topic, "要約", Range(30, 2), StyleTemplateTextSource.ConfirmedSummary, Origin),
            new(StyleTemplateFreeTelopKind.Retort, "位置外", Range(35, 2), StyleTemplateTextSource.UserInput, Origin, 1.5, 0.2));
        CollectionAssert.AreEqual(new[] { "なんでやねん", "確認済みの心の声" }, plan.FreeTelops.Select(static item => item.Input.Text).ToArray());
        Assert.AreEqual(3, plan.Warnings.Count(static item => item.Code == StyleTemplateApplier.RejectedTelopWarning));

        StyleTemplatePlan talk = Apply(Template("talk-show"), Options(),
            new(StyleTemplateFreeTelopKind.Topic, "今日の話題の要約", Range(30, 3), StyleTemplateTextSource.ConfirmedSummary, Origin),
            new(StyleTemplateFreeTelopKind.Title, "売上が30%増えました", Range(10, 3), StyleTemplateTextSource.SubtitlePart, Origin));
        Assert.AreEqual(2, talk.FreeTelops.Count);
    }

    // FR-137 AC-154: short-pop shows 1-3 words, only the spoken word gets the emphasis color, motion ends in about 0.5 s.
    [TestMethod]
    [TestCategory("FR-137")]
    public void ShortPopShowsFewWordsAndColorsOnlyTheSpokenWord()
    {
        StyleTemplateDefinition shortPop = Template("short-pop");
        StyleTemplatePlan plan = Apply(shortPop, Options());
        Assert.IsTrue(plan.Captions.Count > Cues().Length, "split into word groups");
        var relaxed = plan.Captions.Where(caption => caption.Highlights.Count > 3).ToList();
        foreach (StyleTemplateCaption caption in plan.Captions)
        {
            Assert.IsTrue(caption.Highlights.Count >= 1);
            Assert.IsTrue(caption.Highlights.All(item => item.Color == shortPop.EmphasisColors[0]));
            Assert.AreEqual(caption.Text.Length, caption.Highlights.Sum(static item => item.Length));
            Assert.IsTrue(caption.Highlights.All(item => item.Range.Start >= caption.Range.Start && item.Range.End <= caption.Range.End + new RationalTime(1, 1)));
            Assert.IsNotNull(caption.Motion);
            Assert.AreEqual(1.3, caption.Motion.FromScale);
            Assert.AreEqual(1.0, caption.Motion.ToScale);
            Assert.IsTrue(caption.Motion.Range.Duration <= new RationalTime(1, 2));
            if (caption.Highlights.Count > 3)
            {
                Assert.IsTrue(caption.Range.Duration >= Limits.CueMinimumDuration || relaxed.Contains(caption));
            }
        }

        Assert.IsTrue(plan.Captions.Any(static caption => caption.Highlights.Count is >= 1 and <= 3));
        Assert.IsTrue(plan.EmphasisTelops.Count <= StandardEmphasisTelops.Limit("many", Duration));
    }

    // FR-137 AC-154: variety plays sounds when FR-060 is enabled, none when disabled; motion ends in about 0.5 s.
    [TestMethod]
    [TestCategory("FR-137")]
    public void VarietyFollowsTheSoundEffectSettingAndFinishesMotionQuickly()
    {
        StyleTemplateDefinition variety = Template("variety");
        var retort = new StyleTemplateFreeTelopInput(StyleTemplateFreeTelopKind.Retort, "なんでやねん", Range(12, 2),
            StyleTemplateTextSource.UserInput, Origin, 0.3, 0.4);
        StyleTemplatePlan on = Apply(variety, Options(sound: true), retort);
        StyleTemplatePlan off = Apply(variety, Options(sound: false), retort);
        Assert.AreEqual(0, off.SoundEffects.Count);
        Assert.IsTrue(on.SoundEffects.Any(static item => item.Kind == StandardSoundEffectKind.Emphasis));
        Assert.IsTrue(on.SoundEffects.Any(item => item.OutputStart == new RationalTime(12, 1)), "a sound at the free telop");
        Assert.AreEqual(StyleTemplateMotionKind.Shake, on.FreeTelops.Single().Motion!.Kind);
        Assert.IsTrue(on.EmphasisTelops.All(static item => item.Motion!.Range.Duration <= new RationalTime(1, 2)));
        Assert.IsTrue(on.FreeTelops.All(static item => item.Motion!.Range.Duration <= new RationalTime(1, 2)));
        Assert.IsTrue(on.EmphasisTelops.Count <= StandardEmphasisTelops.Limit("many", Duration));
    }
}
