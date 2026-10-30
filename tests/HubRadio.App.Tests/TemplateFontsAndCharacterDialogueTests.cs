using System.Text;
using System.Text.RegularExpressions;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

/// <summary>FR-139 AC-160 (bundled SIL OFL typefaces recorded and used) and FR-137 AC-155 (character dialogue, user portraits).</summary>
[TestClass]
public sealed class TemplateFontsAndCharacterDialogueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "hubradio-char-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A synthetic PNG stand-in (the app bundles no standing picture; the user's own image is registered).</summary>
    private static string SyntheticImage(string directory, string name = "mine.png")
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        return path;
    }

    // ---- FR-139 AC-160 ----

    // AC-160: the bundled typefaces are exactly the three SIL OFL ones; each file, its license text and its name table agree.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC160_TheBundledTypefacesAreTheThreeOflFilesWithTheirLicenses()
    {
        string[] names = StyleTemplateFonts.All.Select(static font => font.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Noto Sans JP Black", "M PLUS Rounded 1c", "Potta One" }, names);
        foreach (StyleTemplateBundledFont font in StyleTemplateFonts.All)
        {
            string license = File.ReadAllText(Path.Combine(StyleTemplateFonts.DefaultDirectory, font.LicenseFileName));
            StringAssert.Contains(license, "SIL OPEN FONT LICENSE Version 1.1");
            Assert.AreEqual(font.AssFamily, StyleTemplateFonts.ReadFamily(font), font.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(StyleTemplateFonts.ReadVersion(font)));
        }

        Assert.AreEqual("2.004", StyleTemplateFonts.ReadVersion(StyleTemplateFonts.NotoSansJpBlack));
    }

    // AC-160: every template uses only bundled typefaces (never a typeface of the user's PC), within PARAM-007.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC160_EveryTemplateUsesOnlyBundledTypefaces()
    {
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            Assert.IsTrue(template.FontFamilies.All(StyleTemplateFonts.IsBundledFamily), template.Id);
            Assert.IsTrue(StyleTemplateCatalog.IsWithinLimits(template), template.Id);
        }

        CollectionAssert.AreEqual(new[] { StyleTemplateFonts.NotoSansJpBlack.AssFamily },
            StyleTemplateCatalog.Find(StyleTemplateCatalog.HandmadeId)!.FontFamilies.ToArray());
        CollectionAssert.AreEqual(new[] { StyleTemplateFonts.PottaOne.AssFamily },
            StyleTemplateCatalog.Find(StyleTemplateCatalog.ShortPopId)!.FontFamilies.ToArray());
        Assert.IsTrue(StyleTemplateCatalog.Find(StyleTemplateCatalog.TalkShowId)!.FontFamilies.Contains(StyleTemplateFonts.MPlusRounded1c.AssFamily));
        Assert.IsFalse(StyleTemplateFonts.IsBundledFamily("Meiryo UI"));
    }

    // AC-160: the Job record (style-template.json) holds the name, the version read from the file and the license of each font.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC160_TheJobRecordHoldsFontNameVersionAndLicense()
    {
        string job = TempDirectory();
        try
        {
            foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
            {
                JobStyleTemplateStore.Save(job, JobStyleTemplateRecord.For(template).WithFonts(StyleTemplateFonts.RecordsFor(template)));
                JobStyleTemplateRecord loaded = JobStyleTemplateStore.Load(job)!;
                Assert.AreEqual(template.FontFamilies.Count, loaded.Fonts!.Count, template.Id);
                foreach (StyleTemplateFontRecord font in loaded.Fonts)
                {
                    StyleTemplateBundledFont bundled = StyleTemplateFonts.All.Single(item => item.Name == font.Name);
                    Assert.AreEqual(StyleTemplateFonts.ReadVersion(bundled), font.Version);
                    Assert.AreEqual(StyleTemplateFonts.LicenseName, font.License);
                    Assert.IsTrue(font.BurnInPermitted);
                    Assert.IsTrue(JobStyleTemplateRecord.IsUsable(font));
                }
            }

            StringAssert.Contains(File.ReadAllText(Path.Combine(job, JobStyleTemplateStore.FileName)), "SIL Open Font License 1.1");
        }
        finally
        {
            Directory.Delete(job, recursive: true);
        }
    }

    // AC-160: a missing or altered font file fails closed.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC160_AMissingOrAlteredFontFileFailsClosed()
    {
        string empty = TempDirectory();
        string altered = TempDirectory();
        try
        {
            StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.HandmadeId)!;
            _ = Assert.ThrowsExactly<FileNotFoundException>(() => StyleTemplateFonts.RecordsFor(template, empty));
            foreach (string file in Directory.GetFiles(StyleTemplateFonts.DefaultDirectory))
            {
                File.Copy(file, Path.Combine(altered, Path.GetFileName(file)));
            }

            File.AppendAllText(Path.Combine(altered, StyleTemplateFonts.NotoSansJpBlack.FileName), "x");
            _ = Assert.ThrowsExactly<InvalidDataException>(() => StyleTemplateFonts.RecordsFor(template, altered));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            Directory.Delete(altered, recursive: true);
        }
    }

    // AC-160: the burned-in subtitle names the bundled typeface of the template (\fn), and no other family.
    [TestMethod]
    [TestCategory("FR-139")]
    public async Task AC160_TheBurnedInSubtitleNamesTheBundledTypeface()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            string ass = Ass(fixture, template);
            string[] families = Regex.Matches(ass, @"\\fn([^\\}]+)").Select(static match => match.Groups[1].Value).Distinct().ToArray();
            Assert.IsTrue(families.Length > 0, template.Id);
            Assert.IsTrue(families.All(StyleTemplateFonts.IsBundledFamily), template.Id + ": " + string.Join("|", families));
            Assert.AreEqual(template.FontFamilies[0], families[0], template.Id);
        }
    }

    // AC-160: the sound effects stay the two FR-060 synthesized ones.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC160_TemplateSoundEffectsAreOnlyTheTwoSynthesizedKinds()
    {
        Assert.AreEqual(2, Enum.GetValues<StandardSoundEffectKind>().Length);
    }

    // ---- FR-137 AC-155 ----

    // AC-155: with no registered portrait the template cannot be chosen; registering the user's own image with a rights state allows it.
    [TestMethod]
    [TestCategory("FR-137")]
    public void AC155_TheTemplateCannotBeChosenUntilAPortraitIsRegisteredWithItsRightsState()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
            Assert.AreEqual(0, store.Count);
            Assert.IsFalse(store.IsSelectable(template));
            Assert.IsTrue(store.IsSelectable(StyleTemplateCatalog.Find(StyleTemplateCatalog.ExplainerId)!));

            CharacterPortrait portrait = store.Register("コパ", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now, rightsNote: "利用者本人の画像");
            Assert.IsTrue(store.IsSelectable(template));
            Assert.AreEqual(PortraitRightsStatus.Confirmed, portrait.Rights);

            // The state is recorded on disk and survives a reload; the image is copied into the store.
            CharacterPortrait loaded = new CharacterPortraitStore(Path.Combine(root, "store")).Find(portrait.Id)!;
            Assert.AreEqual(PortraitRightsStatus.Confirmed, loaded.Rights);
            Assert.AreEqual("利用者本人の画像", loaded.RightsNote);
            Assert.AreEqual(Now, loaded.RightsRecordedAt);
            Assert.IsTrue(File.Exists(store.ImagePath(loaded)));
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "store", CharacterPortraitStore.FileName)), "Confirmed");

            CharacterPortrait changed = store.SetRights(portrait.Id, PortraitRightsStatus.Unconfirmed, Now.AddDays(1));
            Assert.AreEqual(PortraitRightsStatus.Unconfirmed, changed.Rights);
            Assert.IsTrue(store.Remove(portrait.Id));
            Assert.IsFalse(store.IsSelectable(template));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // AC-155: only real images are registered, names and subtitle colors stay distinct per character.
    [TestMethod]
    [TestCategory("FR-137")]
    public void AC155_RegistrationRefusesNonImagesAndGivesEachCharacterItsOwnColor()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            string text = Path.Combine(root, "fake.png");
            File.WriteAllText(text, "not an image");
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.Register("A", text, PortraitRightsStatus.Confirmed, Now));
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.Register("A", Path.Combine(root, "none.png"), PortraitRightsStatus.Confirmed, Now));
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.Register(" ", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now));
            Assert.AreEqual(0, store.Count);

            CharacterPortrait first = store.Register("A", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now);
            CharacterPortrait second = store.Register("B", SyntheticImage(root), PortraitRightsStatus.Unconfirmed, Now);
            Assert.AreNotEqual(first.SubtitleColor, second.SubtitleColor);
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.Register("A", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now));
            _ = Assert.ThrowsExactly<ArgumentException>(() =>
                store.Register("C", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now, subtitleColor: first.SubtitleColor));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // AC-155: the applied output shows the standing picture and the dialogue subtitle, in a different color for each character,
    // and the app bundles no standing picture.
    [TestMethod]
    [TestCategory("FR-137")]
    public async Task AC155_TheOutputShowsPortraitsAndDialogueInPerCharacterColors()
    {
        string root = TempDirectory();
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            CharacterPortrait a = store.Register("コパ", SyntheticImage(root), PortraitRightsStatus.Confirmed, Now);
            CharacterPortrait b = store.Register("相方", SyntheticImage(root, "other.png"), PortraitRightsStatus.Unconfirmed, Now);
            StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
            using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
            SubtitleFileCue cue = fixture.Graph(true).DisplayCues[0];
            RationalTime half = new(1, 2);
            OriginRef origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
            StyleTemplateFreeTelopInput[] lines =
            [
                new(StyleTemplateFreeTelopKind.Dialogue, "こんにちは", new TimeRange(cue.Cue.Range.Start, half), StyleTemplateTextSource.UserInput, origin, Speaker: a.Id),
                new(StyleTemplateFreeTelopKind.Dialogue, "やあ", new TimeRange(cue.Cue.Range.Start + half, half), StyleTemplateTextSource.ConfirmedSentence, origin, Speaker: b.Id),
                new(StyleTemplateFreeTelopKind.Dialogue, "作り足した台詞", new TimeRange(cue.Cue.Range.Start, half), StyleTemplateTextSource.SubtitlePart, origin, Speaker: a.Id),
                new(StyleTemplateFreeTelopKind.Dialogue, "知らない人", new TimeRange(cue.Cue.Range.Start, half), StyleTemplateTextSource.UserInput, origin, Speaker: "char-unknown"),
            ];
            var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
            StyleTemplatePlan plan = StyleTemplateApplier.Apply(template,
                [new StyleTemplateInputCue(new StandardDisplayedCueText(cue.Cue.Range, cue.Text, origin), [])], lines,
                new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(3600, 1), Characters: store.List()));

            // Only user-input / confirmed lines of registered characters are shown; nothing is made up.
            Assert.AreEqual(2, plan.FreeTelops.Count);
            Assert.AreEqual(2, plan.Warnings.Count(static item => item.Code == StyleTemplateApplier.RejectedTelopWarning));
            Assert.AreNotEqual(plan.FreeTelops[0].Color, plan.FreeTelops[1].Color);
            Assert.AreEqual(a.SubtitleColor, plan.FreeTelops[0].Color);
            Assert.AreEqual(b.SubtitleColor, plan.FreeTelops[1].Color);
            Assert.AreEqual(2, plan.Portraits.Count);
            CollectionAssert.AreEqual(new[] { a.Id, b.Id }, plan.Portraits.Select(static item => item.CharacterId).ToArray());
            Assert.IsTrue(plan.Portraits.All(static item => item.Width > 0 && item.Height > 0));
            Assert.AreEqual(1, plan.Warnings.Count(static item => item.Code == StyleTemplateApplier.PortraitRightsUnconfirmedWarning));

            var revision = fixture.Subtitles.Revisions.Single(item => item.Snapshot.Cue.Id == cue.Cue.Id);
            string ass = Encoding.UTF8.GetString(StandardTemplateSubtitles.Write(cue, fixture.Subtitles.AssProfile,
                revision.Snapshot.LayoutResolution, plan, vertical: false).GetUtf8Bytes());
            string[] dialogueLines = ass.Split('\n').Where(static line => line.Contains("こんにちは") || line.Contains("やあ")).ToArray();
            Assert.AreEqual(2, dialogueLines.Length);
            StringAssert.Contains(dialogueLines[0], @"\1c" + StyleTemplateColor.ToAss(a.SubtitleColor));
            StringAssert.Contains(dialogueLines[1], @"\1c" + StyleTemplateColor.ToAss(b.SubtitleColor));
            Assert.IsFalse(ass.Contains("作り足した台詞", StringComparison.Ordinal));
            Assert.IsTrue(dialogueLines.All(static line => line.Contains(@"\fn" + StyleTemplateFonts.MPlusRounded1c.AssFamily, StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // AC-155: the app bundles no standing picture (no image file next to the fonts or in the product source).
    [TestMethod]
    [TestCategory("FR-137")]
    public void AC155_TheAppBundlesNoStandingPicture()
    {
        string[] images = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
        Assert.IsFalse(Directory.EnumerateFiles(StyleTemplateFonts.DefaultDirectory)
            .Any(file => images.Contains(Path.GetExtension(file).ToLowerInvariant())));
        Assert.IsFalse(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Portraits")));
        Assert.AreEqual(0, new CharacterPortraitStore(Path.Combine(Path.GetTempPath(), "hubradio-none-" + Guid.NewGuid().ToString("N"))).Count);
    }

    private static string Ass(StandardGenerationFixture fixture, StyleTemplateDefinition template)
    {
        StandardRenderConfiguration configuration = fixture.Request().Configuration.WithStyleTemplate(template);
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
            fixture.Subtitles, fixture.Font.Evidence, configuration: configuration);
        return string.Concat(output.SubtitleFiles.Select(static file => Encoding.UTF8.GetString(file.File.GetUtf8Bytes())));
    }
}
