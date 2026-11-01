using System.Text;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-140 AC-165: the subtitle file a speaker-assigned Job stores must pass the stored-subtitle check that the result
/// validation runs (StandardTemplateSubtitleBytes.Matches) — a Job must not fail after the speakers are assigned.
/// </summary>
[TestClass]
public sealed class CueSpeakerStoredSubtitleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static string Png(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        return path;
    }

    // FR-140 AC-165: an assigned cue's styled file (dialogue line, with or without the name label) equals the plain file except for the allowed override tags.
    [TestMethod]
    [TestCategory("FR-140")]
    public async Task AC165_TheStoredSubtitleOfAnAssignedCuePassesTheResultCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "hubradio-stored-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var store = new CharacterPortraitStore(Path.Combine(root, "store"));
            CharacterPortrait a = store.Register("キャラA", Png(root, "a.png"), PortraitRightsStatus.Confirmed, Now);
            StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
            using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
            SubtitleFileCue cue = fixture.Graph(true).DisplayCues[0];
            var revision = fixture.Subtitles.Revisions.Single(item => item.Snapshot.Cue.Id == cue.Cue.Id);
            byte[] plain = StandardSubtitlePlacement.Write(cue, fixture.Subtitles.AssProfile, revision.Snapshot.LayoutResolution).GetUtf8Bytes();
            var cueText = new StandardDisplayedCueText(cue.Cue.Range, cue.Text, OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()));
            var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
            CueSpeakerAssignmentSet set = CueSpeakerAssignmentSet.Empty.Assign(0, a.Id).Confirm();
            foreach (bool brand in new[] { false, true })
            {
                StyleTemplateBrand? b = brand ? new StyleTemplateBrand("#FFFFFF", "#000000", ["#FFFF00"], [], null) : null;
                StyleTemplatePlan plan = StyleTemplateApplier.Apply(template, [new StyleTemplateInputCue(cueText, [])],
                    set.Resolve(1, store.List()).DialogueLines([cueText]),
                    new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(3600, 1), Brand: b, Characters: store.List()));
                byte[] styled = StandardTemplateSubtitles.Write(cue, fixture.Subtitles.AssProfile, revision.Snapshot.LayoutResolution, plan, vertical: false)
                    .GetUtf8Bytes();
                int width = fixture.Subtitles.AssProfile.PlayResolutionWidth, height = fixture.Subtitles.AssProfile.PlayResolutionHeight;
                Assert.IsTrue(StandardTemplateSubtitleBytes.Matches(styled, plain, width, height),
                    "brand=" + brand + Environment.NewLine + Encoding.UTF8.GetString(styled));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
