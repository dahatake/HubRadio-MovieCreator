using System.Text.Json;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-140 (cue speaker assignment, standing-picture cut-out) and FR-142 (word timing) at integration level:
/// AC-164 AC-166 AC-167 AC-171. The images are synthetic; the repository holds no standing picture.
/// </summary>
[TestClass]
public sealed class CueSpeakerAndWordTimingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly OriginRef Origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "hubradio-cue-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static TimeRange Range(long startMs, long durationMs) => new(new RationalTime(startMs, 1000), new RationalTime(durationMs, 1000));

    private static StandardDisplayedCueText Cue(long startMs, long durationMs, string text) => new(Range(startMs, durationMs), text, Origin);

    /// <summary>A synthetic two-character image: the left half red, the right half blue (a real PNG).</summary>
    private static string TwoCharacterImage(string directory, int width = 40, int height = 20)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                bool left = x < width / 2;
                pixels[at] = left ? (byte)0 : (byte)255;      // B
                pixels[at + 1] = 0;                            // G
                pixels[at + 2] = left ? (byte)255 : (byte)0;   // R
                pixels[at + 3] = 255;
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        byte[] bytes = new byte[stream.Size];
        using (DataReader reader = new(stream))
        {
            _ = reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }

        string path = Path.Combine(directory, "two.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string AnyImage(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        return path;
    }

    private static StyleTemplatePlan ApplyDialogue(IReadOnlyList<StandardDisplayedCueText> cues, CueSpeakerAssignmentSet set,
        IReadOnlyList<CharacterPortrait> characters, StyleTemplateBrand? brand = null)
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
        var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
        return StyleTemplateApplier.Apply(template, cues.Select(static cue => new StyleTemplateInputCue(cue, [])).ToArray(),
            set.Resolve(cues.Count, characters).DialogueLines(cues),
            new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(60, 1), Brand: brand, Characters: characters));
    }

    // ---- FR-140 AC-164 ----

    // FR-140 AC-164: the assignment is saved with the Job and is the same after a rerun; an unassigned cue has no speaker.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC164_TheAssignmentIsSavedWithTheJobAndReproduced()
    {
        string job = TempDirectory();
        try
        {
            CueSpeakerAssignmentSet set = CueSpeakerAssignmentSet.Empty.Assign(0, "char-a").Assign(2, "char-b").Confirm();
            CueSpeakerAssignmentStore.Save(job, set);
            CueSpeakerAssignmentSet loaded = CueSpeakerAssignmentStore.Load(job)!;
            Assert.AreEqual("char-a", loaded.ConfirmedSpeakerOf(0));
            Assert.IsNull(loaded.ConfirmedSpeakerOf(1), "an unassigned cue stays without a speaker");
            Assert.AreEqual("char-b", loaded.ConfirmedSpeakerOf(2));
            Assert.IsTrue(loaded.IsConfirmed);
            CueSpeakerAssignmentStore.Save(job, loaded);
            CollectionAssert.AreEqual(set.Assignments.ToArray(), CueSpeakerAssignmentStore.Load(job)!.Assignments.ToArray());
            Assert.IsNull(CueSpeakerAssignmentStore.Load(Path.Combine(job, "none")));
            File.WriteAllText(Path.Combine(job, CueSpeakerAssignmentStore.FileName), "{ not json");
            Assert.IsNull(CueSpeakerAssignmentStore.Load(job), "a damaged file assigns nobody");
        }
        finally
        {
            Directory.Delete(job, recursive: true);
        }
    }

    // FR-140 AC-164: the picture and the per-character color appear only for the assigned cues; unassigned cues have none.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC164_OnlyAssignedCuesShowAPortraitAndTheirCharactersColor()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            CharacterPortrait a = store.Register("コパ", AnyImage(root, "a.png"), PortraitRightsStatus.Confirmed, Now);
            CharacterPortrait b = store.Register("相方", AnyImage(root, "b.png"), PortraitRightsStatus.Confirmed, Now);
            StandardDisplayedCueText[] cues = [Cue(0, 2000, "こんにちは"), Cue(2000, 2000, "どうも"), Cue(4000, 2000, "また明日")];
            CueSpeakerAssignmentSet set = CueSpeakerAssignmentSet.Empty.Assign(0, a.Id).Assign(2, b.Id).Confirm();

            StyleTemplatePlan plan = ApplyDialogue(cues, set, store.List());

            Assert.AreEqual(2, plan.Portraits.Count);
            CollectionAssert.AreEqual(new[] { a.Id, b.Id }, plan.Portraits.Select(static shot => shot.CharacterId).ToArray());
            Assert.AreEqual(cues[0].OutputRange, plan.Portraits[0].Range);
            Assert.AreEqual(cues[2].OutputRange, plan.Portraits[1].Range);
            Assert.IsFalse(plan.Portraits.Any(shot => shot.Range.Overlaps(cues[1].OutputRange)), "no picture during the unassigned cue");
            Assert.AreEqual(a.SubtitleColor, plan.FreeTelops[0].Color);
            Assert.AreEqual(b.SubtitleColor, plan.FreeTelops[1].Color);
            Assert.AreNotEqual(plan.FreeTelops[0].Color, plan.FreeTelops[1].Color);
            // The lines are the cues' own text: nothing is made up (FR-137).
            CollectionAssert.AreEqual(new[] { "こんにちは", "また明日" }, plan.FreeTelops.Select(static item => item.Input.Text).ToArray());

            // Nobody assigned: no picture at all.
            Assert.AreEqual(0, ApplyDialogue(cues, CueSpeakerAssignmentSet.Empty, store.List()).Portraits.Count);

            // The record states the same: the unassigned cue has no speaker and the default color.
            string json = File.ReadAllText(SaveRecord(root, plan, cues));
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement[] subtitles = document.RootElement.GetProperty("subtitles").EnumerateArray().ToArray();
            Assert.AreEqual(a.Id, subtitles[0].GetProperty("speaker").GetString());
            Assert.AreEqual(JsonValueKind.Null, subtitles[1].GetProperty("speaker").ValueKind);
            Assert.AreEqual(document.RootElement.GetProperty("defaultSubtitleColor").GetString(), subtitles[1].GetProperty("color").GetString());
            Assert.AreEqual(2, document.RootElement.GetProperty("portraits").GetArrayLength());
            Assert.AreEqual("character-dialogue", document.RootElement.GetProperty("templateId").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SaveRecord(string root, StyleTemplatePlan plan, IReadOnlyList<StandardDisplayedCueText> cues)
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
        StyleTemplateRenderRecord record = StyleTemplateRenderRecord.Create(template, plan, 1920, 1080,
            cues.Select(static cue => new StyleTemplateInputCue(cue, [])).ToArray(), true, static _ => "#FF0000");
        StyleTemplateRenderRecord.Save(Path.Combine(root, "job"), record);
        return Path.Combine(root, "job", StyleTemplateRenderRecord.FileName);
    }

    // FR-140 AC-164: in a brand-fixed Project the brand color wins and the characters differ by position.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC164_BrandFixedKeepsTheBrandColorAndSeparatesCharactersByPosition()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            CharacterPortrait a = store.Register("コパ", AnyImage(root, "a.png"), PortraitRightsStatus.Confirmed, Now);
            CharacterPortrait b = store.Register("相方", AnyImage(root, "b.png"), PortraitRightsStatus.Confirmed, Now);
            StandardDisplayedCueText[] cues = [Cue(0, 2000, "こんにちは"), Cue(2000, 2000, "どうも")];
            CueSpeakerAssignmentSet set = CueSpeakerAssignmentSet.Empty.Assign(0, a.Id).Assign(1, b.Id).Confirm();
            var brand = new StyleTemplateBrand("#FFFFFF", "#102030", ["#FFFFFF"], [StyleTemplateFonts.NotoSansJpBlack.AssFamily], null);

            StyleTemplatePlan plan = ApplyDialogue(cues, set, store.List(), brand);

            Assert.IsTrue(plan.FreeTelops.All(static item => item.Color == "#FFFFFF"), "the brand color wins");
            Assert.AreNotEqual(plan.Portraits[0].Left, plan.Portraits[1].Left, "characters are told apart by position");
            CollectionAssert.AreEqual(new[] { "コパ", "相方" }, plan.Portraits.Select(static shot => shot.CharacterName).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- FR-140 AC-166 ----

    // FR-140 AC-166: an estimate is a draft until the user confirms or corrects it; only a confirmed assignment reaches the output.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC166_AnEstimateIsADraftAndOnlyAConfirmedAssignmentReachesTheOutput()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            CharacterPortrait a = store.Register("コパ", AnyImage(root, "a.png"), PortraitRightsStatus.Confirmed, Now);
            CharacterPortrait b = store.Register("相方", AnyImage(root, "b.png"), PortraitRightsStatus.Confirmed, Now);
            StandardDisplayedCueText[] cues = [Cue(0, 1000, "一"), Cue(1000, 1000, "二"), Cue(2000, 1000, "三")];

            CueSpeakerAssignmentSet draft = CueSpeakerAssignmentSet.Estimate(cues.Length, store.List());
            Assert.IsFalse(draft.IsConfirmed);
            Assert.AreEqual(CueSpeakerAssignmentSet.DraftLabel, draft.Label);
            Assert.IsTrue(draft.Estimated);
            Assert.AreEqual(a.Id, draft.SpeakerOf(0));
            Assert.IsNull(draft.ConfirmedSpeakerOf(0), "a draft names no speaker for the output");
            Assert.AreEqual(0, draft.DialogueLines(cues).Count);
            Assert.AreEqual(0, ApplyDialogue(cues, draft, store.List()).Portraits.Count, "a draft shows no standing picture");

            // A correction keeps it a draft; only the explicit confirmation makes it the user's decision.
            CueSpeakerAssignmentSet corrected = draft.Assign(1, a.Id);
            Assert.IsFalse(corrected.IsConfirmed);
            CueSpeakerAssignmentSet confirmed = corrected.Confirm();
            Assert.IsTrue(confirmed.IsConfirmed);
            Assert.AreEqual(CueSpeakerAssignmentSet.ConfirmedLabel, confirmed.Label);
            Assert.IsFalse(confirmed.Estimated);
            CollectionAssert.AreEqual(new[] { a.Id, a.Id, a.Id }, ApplyDialogue(cues, confirmed, store.List()).Portraits.Select(static shot => shot.CharacterId).ToArray());

            // Confirming an estimate made before the cues were known expands it over the cues of the run.
            CueSpeakerAssignmentSet pending = CueSpeakerAssignmentSet.Estimate(0, store.List()).Confirm();
            Assert.IsTrue(pending.IsConfirmed);
            CollectionAssert.AreEqual(new[] { a.Id, b.Id, a.Id },
                ApplyDialogue(cues, pending, store.List()).Portraits.Select(static shot => shot.CharacterId).ToArray());

            // A removed character is dropped and the result is a draft again; nothing is guessed in its place.
            Assert.IsTrue(store.Remove(b.Id));
            CueSpeakerAssignmentSet pruned = CueSpeakerAssignmentSet.Empty.Assign(0, b.Id).Confirm().Prune(store.List(), cues.Length);
            Assert.IsFalse(pruned.IsConfirmed);
            Assert.IsNull(pruned.SpeakerOf(0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- FR-140 AC-167 ----

    // FR-140 AC-167: one character is cut out of a two-character image; the cut-out inherits the rights confirmation and the source
    // file is unchanged.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC167_ACutoutInheritsTheRightsStateAndLeavesTheSourceUnchanged()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            string image = TwoCharacterImage(root);
            CharacterPortrait source = store.Register("並び", image, PortraitRightsStatus.Confirmed, Now, rightsNote: "自作");
            string sourceStored = store.ImagePath(source);
            byte[] before = File.ReadAllBytes(sourceStored);
            DateTimeOffset later = Now.AddHours(3);

            CharacterPortrait left = store.RegisterCutout(source.Id, "左の子", new PortraitCropRect(0, 0, 20, 20), later);
            CharacterPortrait right = store.RegisterCutout(source.Id, "右の子", new PortraitCropRect(20, 0, 20, 20), later);

            Assert.AreEqual(PortraitRightsStatus.Confirmed, left.Rights);
            Assert.AreEqual(source.RightsRecordedAt, left.RightsRecordedAt, "the time of the original confirmation is inherited");
            Assert.AreEqual("自作", left.RightsNote);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(sourceStored), "the original image is not changed");
            Assert.AreEqual((20, 20), PortraitImageCropper.Size(store.ImagePath(left)));
            Assert.AreEqual("#FF0000", PortraitImageCropper.MeanColor(store.ImagePath(left)));
            Assert.AreEqual("#0000FF", PortraitImageCropper.MeanColor(store.ImagePath(right)));
            Assert.AreEqual(3, store.Count);
            Assert.AreNotEqual(left.SubtitleColor, right.SubtitleColor);

            // An unconfirmed source gives an unconfirmed cut-out (the user is not made to appear to have confirmed it).
            CharacterPortrait raw = store.Register("未確認", TwoCharacterImage(root), PortraitRightsStatus.Unconfirmed, Now);
            Assert.AreEqual(PortraitRightsStatus.Unconfirmed,
                store.RegisterCutout(raw.Id, "未確認の子", new PortraitCropRect(0, 0, 20, 20), later).Rights);

            // A range outside the image, an unknown source and a duplicate name are refused.
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.RegisterCutout(source.Id, "外", new PortraitCropRect(30, 0, 20, 20), later));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.RegisterCutout(source.Id, "小", new PortraitCropRect(0, 0, 2, 2), later));
            _ = Assert.ThrowsExactly<KeyNotFoundException>(() => store.RegisterCutout("char-none", "無", new PortraitCropRect(0, 0, 20, 20), later));
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.RegisterCutout(source.Id, "左の子", new PortraitCropRect(0, 0, 20, 20), later));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- FR-142 AC-171 ----

    private static StyleTemplateWord[] Words(long startMs, params (string Text, long Ms)[] parts)
    {
        var list = new List<StyleTemplateWord>();
        long at = startMs;
        foreach ((string text, long ms) in parts)
        {
            list.Add(new(text, Range(at, ms)));
            at += ms;
        }

        return [.. list];
    }

    // FR-142 AC-171: words lie inside the cue and concatenate to its text; otherwise the cue is shown as a whole with a warning.
    [TestMethod]
    [TestCategory("FR-142")]
    public void AC171_WordsLieInsideTheCueAndConcatenateToItsText()
    {
        StandardDisplayedCueText cue = Cue(1000, 1500, "今日は 晴れ");
        StyleTemplateWord[] words = Words(1000, ("今日", 500), ("は ", 400), ("晴れ", 600));

        CueWordTimingResult ok = CueWordTiming.For(cue, words);
        Assert.IsTrue(ok.HasWordTiming);
        Assert.IsNull(ok.Warning);
        Assert.AreEqual(cue.Text, string.Concat(ok.Words.Select(static word => word.Text)));
        Assert.IsTrue(ok.Words.All(word => word.Range.Start >= cue.OutputRange.Start && word.Range.End <= cue.OutputRange.End));

        // A word outside the cue, an overlap, a different text or no words: the cue is shown as a whole with a warning.
        foreach (StyleTemplateWord[] bad in new[]
        {
            Words(1000, ("今日", 500), ("は ", 400), ("晴れ", 900)),
            [new("今日は ", Range(900, 500)), new("晴れ", Range(1400, 500))],
            [new("今日", Range(1000, 800)), new("は ", Range(1500, 300)), new("晴れ", Range(1700, 300))],
            Words(1000, ("今日", 500), ("は ", 400), ("雨", 600)),
            [],
        })
        {
            CueWordTimingResult result = CueWordTiming.For(cue, bad);
            Assert.IsFalse(result.HasWordTiming);
            Assert.AreEqual(StyleTemplateApplier.MissingWordTimingWarning, result.Warning);
            Assert.AreEqual(0, result.Words.Count);
        }

        Assert.IsFalse(CueWordTiming.For(cue, null).HasWordTiming);
    }

    // FR-142 AC-171: with word times the short-pop template highlights the words one at a time; without, it warns and does not divide.
    [TestMethod]
    [TestCategory("FR-142")]
    public void AC171_TheShortPopHighlightsTheSpokenWordAndWarnsWhenTimesAreMissing()
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.ShortPopId)!;
        var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
        var options = new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(60, 1));
        StandardDisplayedCueText cue = Cue(1000, 1200, "今日晴れ");
        StyleTemplateWord[] words = Words(1000, ("今日", 600), ("晴れ", 600));

        StyleTemplatePlan timed = StyleTemplateApplier.Apply(template, [new StyleTemplateInputCue(cue, words)], [], options);
        Assert.AreEqual(0, timed.Warnings.Count(static item => item.Code == StyleTemplateApplier.MissingWordTimingWarning));
        StyleTemplateHighlight[] highlights = timed.Captions.SelectMany(static caption => caption.Highlights).ToArray();
        Assert.AreEqual(2, highlights.Length);
        Assert.AreEqual(words[0].Range, highlights[0].Range);
        Assert.AreEqual(words[1].Range, highlights[1].Range);
        Assert.IsFalse(highlights[0].Range.Overlaps(highlights[1].Range), "one word at a time");

        StyleTemplatePlan untimed = StyleTemplateApplier.Apply(template, [new StyleTemplateInputCue(cue, [])], [], options);
        Assert.AreEqual(1, untimed.Warnings.Count(static item => item.Code == StyleTemplateApplier.MissingWordTimingWarning));
        Assert.AreEqual(0, untimed.Captions.SelectMany(static caption => caption.Highlights).Count(), "no per-word highlight is invented");
    }

    // FR-142 AC-171: after the user corrects the text, the times follow the words that remain; an unmatched word makes it cue-level.
    [TestMethod]
    [TestCategory("FR-142")]
    public void AC171_ACorrectedCueKeepsTheTimesOfTheWordsItStillHas()
    {
        StandardDisplayedCueText cue = Cue(0, 1800, "今日は晴れ");
        StyleTemplateWord[] words = Words(0, ("今日", 600), ("は", 400), ("晴れ", 800));

        CueWordTimingResult same = CueWordTiming.Realign(cue, words, "今日は晴れ");
        Assert.IsTrue(same.HasWordTiming);
        CollectionAssert.AreEqual(words, same.Words.ToArray());

        CueWordTimingResult changed = CueWordTiming.Realign(cue, words, "今日は雨");
        Assert.IsFalse(changed.HasWordTiming, "a changed word cannot be given a time");
        Assert.AreEqual(CueWordTiming.RealignedPartlyWarning, changed.Warning);
        Assert.IsFalse(CueWordTiming.Realign(cue, words, "今日は晴れ！").HasWordTiming, "an added tail is not timed");
        Assert.IsFalse(CueWordTiming.Realign(cue, [], "今日は晴れ").HasWordTiming);
        Assert.IsFalse(CueWordTiming.Realign(cue, words, string.Empty).HasWordTiming);
    }
}
