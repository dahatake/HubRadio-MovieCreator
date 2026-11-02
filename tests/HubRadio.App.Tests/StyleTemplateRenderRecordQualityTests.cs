using System.Text.Json;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-139 AC-159 at integration level: the render record of every template states the colors the render used (letters and the
/// background behind them, 4.5:1), the user-editable draft state of automatic captions and the source text of each cue.
/// </summary>
[TestClass]
public sealed class StyleTemplateRenderRecordQualityTests
{
    private static double Luminance(string hex)
    {
        (byte r, byte g, byte b) = StyleTemplateColor.Parse(hex);
        static double Lin(byte v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    private static double Contrast(string a, string b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static TimeRange Range(long startMs, long ms) => new(new RationalTime(startMs, 1000), new RationalTime(ms, 1000));

    // FR-139 AC-159: for each template the record carries letter color, background, draft state and the source text, as used.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC159_EveryTemplateRecordStatesColorsDraftAndSourceText()
    {
        OriginRef automatic = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        StandardDisplayedCueText[] cues =
        [
            new(Range(0, 3000), "これは正しくない", automatic),
            new(Range(3000, 3000), "もし雨ならば中止", automatic),
        ];
        Assert.AreEqual(7, StyleTemplateCatalog.All.Select(static t => t.Id).Distinct().Count());
        foreach (StyleTemplateDefinition template in StyleTemplateCatalog.All)
        {
            var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
            StyleTemplateInputCue[] inputs = [.. cues.Select(static cue => new StyleTemplateInputCue(cue, []))];
            StyleTemplatePlan plan = StyleTemplateApplier.Apply(template, inputs, [],
                new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(60, 1)));
            StyleTemplateRenderRecord record = StyleTemplateRenderRecord.Create(template, plan, 1920, 1080, inputs, false, static _ => null);

            string dir = Path.Combine(Path.GetTempPath(), "hr-fr139-" + Guid.NewGuid().ToString("N"));
            try
            {
                StyleTemplateRenderRecord.Save(dir, record);
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, StyleTemplateRenderRecord.FileName)));
                JsonElement root = json.RootElement;
                Assert.IsTrue(root.GetProperty("captionsDraft").GetBoolean(), template.Id + ": automatic captions are marked as draft");
                string[] source = [.. root.GetProperty("sourceTranscript").EnumerateArray().Select(static e => e.GetProperty("text").GetString()!)];
                CollectionAssert.AreEqual(cues.Select(static c => c.Text).ToArray(), source);
                foreach (JsonElement cue in root.GetProperty("subtitles").EnumerateArray())
                {
                    string color = cue.GetProperty("color").GetString()!;
                    string background = cue.GetProperty("background").GetString()!;
                    Assert.IsTrue(Contrast(color, background) >= 4.5, $"{template.Id}: {color} on {background}");
                    Assert.IsTrue(cue.GetProperty("draft").GetBoolean());
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    // FR-139 AC-159: a cue whose text the user edited (editorial origin) is not a draft.
    [TestMethod]
    [TestCategory("FR-139")]
    public void AC159_AnEditedCueIsNotMarkedDraft()
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.ExplainerId)!;
        OriginRef edited = OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New());
        StyleTemplateInputCue[] inputs = [new(new StandardDisplayedCueText(Range(0, 3000), "確認済み", edited), [])];
        StyleTemplatePlan plan = StyleTemplateApplier.Apply(template, inputs, [],
            new StyleTemplateApplyOptions(new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1)),
                "standard", false, new RationalTime(60, 1)));
        StyleTemplateRenderRecord record = StyleTemplateRenderRecord.Create(template, plan, 1920, 1080, inputs, false, static _ => null);
        Assert.IsFalse(record.CaptionsDraft);
        Assert.IsFalse(record.Subtitles[0].Draft);
    }
}
