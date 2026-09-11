using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;
using ProjectSnapshotJson = HubRadio.App.Policy.ProjectSnapshotJson;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardRenderGraphFactoryTests
{
    [TestMethod]
    public async Task FactoryUsesEveryActualCutForVideoAndAudioAndCompilesBothChains()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardRenderGraphOutput output = fixture.Graph(false);
        Assert.IsTrue(RenderGraphValidator.Validate(output.Graph, output.ValidationContext).IsValid);
        RenderNode[] videos = output.Graph.Nodes.Where(static node => node.Kind == RenderNodeKind.Video).ToArray();
        RenderNode[] audio = output.Graph.Nodes.Where(static node => node.Kind == RenderNodeKind.Audio).ToArray();
        Assert.HasCount(2, videos);
        Assert.HasCount(2, audio);
        CollectionAssert.AreEqual(new[] { StandardGenerationFixture.Range(0, 30), StandardGenerationFixture.Range(40, 30) },
            videos.Select(static node => node.SourceRange!.Value).ToArray());
        CollectionAssert.AreEqual(videos.Select(static node => node.OutputRange).ToArray(), audio.Select(static node => node.OutputRange).ToArray());
        Assert.AreEqual(StandardGenerationFixture.Range(0, 60), output.Graph.Nodes.Single(node => node.Id == output.Graph.OutputNodeId).OutputRange);
        FfmpegCompilation compiled = await fixture.CompileGraphAsync(output);
        StringAssert.Contains(compiled.FilterScript, "concat=n=2:v=1:a=0");
        StringAssert.Contains(compiled.FilterScript, "concat=n=2:v=0:a=1[audio_cuts]");
        StringAssert.Contains(compiled.FilterScript, "force_original_aspect_ratio=decrease");
        CollectionAssert.Contains(compiled.ProcessRequest.ArgumentList.ToArray(), "[audio_cuts]");
        CollectionAssert.Contains(compiled.ProcessRequest.ArgumentList.ToArray(), "h264_mf");
        CollectionAssert.Contains(compiled.ProcessRequest.ArgumentList.ToArray(), "aac");
        string[] arguments = compiled.ProcessRequest.ArgumentList.ToArray();
        Assert.AreEqual("48000", arguments[Array.IndexOf(arguments, "-ar") + 1]);
        Assert.AreEqual("aac_low", arguments[Array.IndexOf(arguments, "-profile:a") + 1]);
        StringAssert.Contains(compiled.FilterScript, "aresample=48000,atrim=");
        CollectionAssert.Contains(compiled.ProcessRequest.ArgumentList.ToArray(), "-n");
    }

    [TestMethod]
    public async Task BurnInAndSidecarsUseSameActualDisplayedTextWithoutChangingRevision()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardRenderGraphOutput output = fixture.Graph(true);
        Assert.AreEqual("元の字幕。", fixture.Subtitles.Revisions[0].Snapshot.Text);
        Assert.AreEqual("元の字幕", output.DisplayCues[0].Text);
        string ass = Encoding.UTF8.GetString(output.SubtitleFiles[0].File.GetUtf8Bytes());
        string srt = Encoding.UTF8.GetString(SubRipSubtitleWriter.Write(output.DisplayCues).GetUtf8Bytes());
        string vtt = Encoding.UTF8.GetString(WebVttSubtitleWriter.Write(output.DisplayCues).GetUtf8Bytes());
        StringAssert.Contains(ass, "元の字幕");
        StringAssert.Contains(ass, @"{\an5\pos(960,918)\clip(192,864,1728,972)\q2}");
        Assert.AreEqual(new RationalNumber(4, 5), output.SubtitleEvidence!.Layouts[0].Resolution.Plan!.Placement.Rect.Y);
        StringAssert.Contains(srt, "元の字幕");
        StringAssert.Contains(vtt, "元の字幕");
        Assert.IsFalse(ass.Contains("元の字幕。", StringComparison.Ordinal));
        Assert.IsNotNull(output.SubtitleEvidence);
        Assert.AreEqual(output.GraphDigest, StandardRenderGraphFactory.Digest(output.Graph));
    }

    [TestMethod]
    public async Task DefaultTitleIsActualPlanMetadataBurnedIntoCleanVideoWithoutAnOpener()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceFileName: "episode';[x]{literal}.mp4");
        string original = CompositionSnapshotJson.WriteVersion(fixture.Preparation.Child);
        StandardRenderGraphOutput clean = fixture.Graph(false);
        StandardGraphicRenderFile graphic = clean.GraphicFiles.Single();
        Assert.AreEqual("EpisodeTitle", graphic.Evidence.Role);
        Assert.AreEqual(fixture.Preparation.Plan.LongForm.Elements.Single(item => item.Kind == LongFormElementKind.EpisodeTitle).Text,
            graphic.Evidence.Text);
        Assert.AreEqual(StandardTextProvenanceKind.SourceAssetMetadata, graphic.Evidence.Provenance);
        Assert.AreEqual(OriginRef.FromAsset(fixture.Asset.Id), graphic.Evidence.Origins.Single());
        Assert.AreEqual(StandardGenerationFixture.Range(0, 3), graphic.Evidence.OutputRange);
        Assert.AreEqual(fixture.Settings.ContentDigest, graphic.Evidence.SettingsSnapshotDigest);
        Assert.AreEqual(RenderInputKind.Graphic, graphic.Input.Kind);
        Assert.IsFalse(clean.ValidationContext.Profile.SubtitleBurnIn);
        Assert.IsFalse(clean.Graph.Nodes.Any(static node => node.Kind == RenderNodeKind.Subtitle));
        FfmpegCompilation compilation = await fixture.CompileGraphAsync(clean);
        StringAssert.Contains(compilation.FilterScript, "ass=filename=");
        Assert.IsFalse(compilation.FilterScript.Contains(":enable=", StringComparison.Ordinal));
        Assert.IsFalse(compilation.FilterScript.Contains(graphic.Evidence.Text, StringComparison.Ordinal));
        Assert.IsFalse(compilation.ProcessRequest.ArgumentList.Any(arg => arg.Contains(graphic.Evidence.Text, StringComparison.Ordinal)));
        string ass = Encoding.UTF8.GetString(graphic.File.GetUtf8Bytes());
        StringAssert.Contains(ass, "Dialogue: 0,0:00:00.00,0:00:03.00,");
        StringAssert.Contains(ass, @"\{literal\}");
        Assert.AreEqual(original, CompositionSnapshotJson.WriteVersion(fixture.Preparation.Child));
        Assert.AreEqual(StandardGenerationFixture.Range(0, 60), clean.Graph.Nodes.Single(node => node.Id == clean.Graph.OutputNodeId).OutputRange);
    }

    [TestMethod]
    public async Task ShortCallToActionRetainsLongFormTargetAndDoesNotChangeSourceDuration()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        StandardShortArtifactInput input = fixture.Shorts.Single();
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset,
            fixture.Geometry, true, input.Subtitles, fixture.Font.Evidence, input.ShortFormId, fixture.Request().Configuration);
        StandardGraphicRenderFile cta = output.GraphicFiles.Single(item => item.Evidence.Role == "ShortCallToAction");
        Assert.AreEqual("本編を見る", cta.Evidence.Text);
        Assert.AreEqual(StandardTextProvenanceKind.SettingsCallToAction, cta.Evidence.Provenance);
        Assert.AreEqual(fixture.Preparation.Plan.LongForm.PlanId, cta.Evidence.LongFormPlanId);
        Assert.AreEqual(input.ShortFormId, cta.Evidence.ShortFormId);
        Assert.AreEqual(new RationalTime(15, 1), cta.Evidence.OutputRange.End);
        Assert.AreEqual(new RationalTime(15, 1), output.ValidationContext.TimelineMap.Entries[^1].OutputRange.End);
        Assert.HasCount(2, output.GraphicFiles);
        FfmpegCompilation compiled = await fixture.CompileGraphAsync(output);
        Assert.AreEqual(2, compiled.FilterScript.Split("ass=filename=", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, compiled.FilterScript.Split("subtitles=filename=", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void MetadataAssUsesLiteralEscapingRatherThanOverrideOrDialogueInjection()
    {
        var white = new AssColor(0, 255, 255, 255);
        var black = new AssColor(0, 0, 0, 0);
        var profile = new AssSubtitleProfile(1920, 1080, Enum.GetValues<SubtitleFileCueStyle>().Select(style =>
            new AssStyleDefinition(style, "Fixture Sans", 36, white, white, black, black, false, false, false, false,
                100, 100, 0, 0, AssBorderStyle.OutlineAndShadow, 2, 0, AssAlignment.MiddleCenter,
                new AssMargins(0, 0, 0), 1)).ToArray());
        var cue = new SubtitleFileCue(new Cue(EntityId<CueIdTag>.New(), CueKind.Subtitle,
            StandardGenerationFixture.Range(0, 3), "metadata"), "{\\pos(1,1)};[out]\nDialogue: not an event", SubtitleFileCueStyle.Normal);
        string ass = Encoding.UTF8.GetString(StandardSubtitlePlacement.WriteAtBounds(cue, profile, 192, 108, 1728, 324).GetUtf8Bytes());
        Assert.AreEqual(1, ass.Split("\nDialogue: ", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(ass, @"\{\pos(1,1)\};[out]\NDialogue: not an event");
    }

    [TestMethod]
    [DataRow(@"\N")]
    [DataRow(@"\n")]
    [DataRow(@"\h")]
    [DataRow(@"\\N")]
    [DataRow(@"\\n")]
    [DataRow(@"\\h")]
    public async Task MetadataRejectsLiteralAssControlsBeforeAutomaticWrapping(string sequence)
    {
        // At the default 64px font the first line holds 23 graphemes. This puts
        // the last backslash on that boundary, before the character it controls.
        string text = new string('a', 23 - (sequence.Length - 1)) + sequence + " tail";
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            includeCardMusic: true, topicTitle: text);
        Assert.AreEqual(text, fixture.Preparation.Plan.LongForm.Elements.Single(item => item.Kind == LongFormElementKind.Topic).Text);
        Assert.AreEqual(SubtitleWriterFailureReason.InvalidText,
            Assert.ThrowsExactly<SubtitleWriterException>(() => fixture.Graph(false)).Reason);

        var cue = new SubtitleFileCue(new Cue(EntityId<CueIdTag>.New(), CueKind.Subtitle,
            StandardGenerationFixture.Range(0, 3), "metadata"), text, SubtitleFileCueStyle.Normal);
        Assert.AreEqual(SubtitleWriterFailureReason.InvalidText,
            Assert.ThrowsExactly<SubtitleWriterException>(() => StandardSubtitlePlacement.WriteAtBounds(
                cue, fixture.Subtitles.AssProfile, 192, 108, 1728, 324)).Reason);
    }

    [TestMethod]
    public async Task SubtitleWarningsAreDefensivelyCopiedAndBoundWithoutChangingDisplayCues()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var warning = new StandardSubtitleBuildWarning("subtitles.sourceTiming", "test.source-token",
            "missing-token-timing", true, fixture.Spans[1].Range, fixture.Spans[1].Id);
        var warnings = new List<StandardSubtitleBuildWarning> { warning };
        StandardSubtitleInput input = StandardGenerationFixture.WithWarnings(fixture.Subtitles, warnings);
        warnings.Clear();
        Assert.AreSame(warning, input.Warnings.Single());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StandardSubtitleBuildWarning>)input.Warnings).Clear());
        Assert.IsEmpty(fixture.Subtitles.Warnings);
        Assert.ThrowsExactly<ArgumentException>(() => new StandardSubtitleInput([], input.DisplayProfile, input.AssProfile, [null!]));

        StandardRenderGraphOutput original = fixture.Graph(true);
        StandardRenderGraphOutput withWarning = StandardRenderGraphFactory.Create(fixture.Preparation,
            fixture.Asset, fixture.Geometry, true, input, fixture.Font.Evidence);
        Assert.AreEqual(original.DisplayCues.Single().Text, withWarning.DisplayCues.Single().Text);
        Assert.AreEqual(original.DisplayCues.Single().Cue, withWarning.DisplayCues.Single().Cue);
        Assert.AreEqual(original.SubtitleFiles.Single().File.ContentDigest, withWarning.SubtitleFiles.Single().File.ContentDigest);
        Assert.AreNotEqual(original.SubtitleConfigurationDigest, withWarning.SubtitleConfigurationDigest);
        Assert.AreEqual(fixture.Preparation.Child.Id, withWarning.Graph.CompositionVersionId);
    }

    [TestMethod]
    public async Task PlannedThumbnailCompilesOriginalFrameSequenceAtDeclaredGeometryWithoutMediaExecution()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeThumbnail: true);
        StandardThumbnailPlan plan = fixture.Preparation.Plan.Thumbnails.Single();
        StandardRenderConfiguration configuration = fixture.Request().Configuration;
        foreach (bool vertical in new[] { false, true })
        {
            StandardRenderGraphOutput output = StandardThumbnailGraphFactory.Create(fixture.Preparation, fixture.Asset,
                fixture.Geometry, fixture.Index, plan, configuration, vertical);
            Assert.IsTrue(RenderGraphValidator.Validate(output.Graph, output.ValidationContext).IsValid);
            Assert.AreEqual(plan.SourceFrameSequence, output.ThumbnailEvidence!.SourceFrameSequence);
            Assert.AreEqual(plan.SourcePresentationTime, output.ThumbnailEvidence.SourcePresentationTime);
            Assert.AreEqual(fixture.Geometry, output.ThumbnailEvidence.SourceGeometry);
            Assert.AreEqual(configuration.SettingsSnapshotDigest, fixture.Settings.ContentDigest);
            using FfmpegCompilation compiled = await fixture.CompileGraphAsync(output);
            StringAssert.Contains(compiled.FilterScript, $"select=eq(n\\,{plan.SourceFrameSequence})");
            Assert.IsFalse(compiled.FilterScript.Contains("fps=", StringComparison.Ordinal));
            string[] args = compiled.ProcessRequest.ArgumentList.ToArray();
            Assert.AreEqual("1", args[Array.IndexOf(args, "-frames:v") + 1]);
            Assert.AreEqual("png", args[Array.IndexOf(args, "-c:v") + 1]);
            Assert.AreEqual("rgb24", args[Array.IndexOf(args, "-pix_fmt") + 1]);
            CollectionAssert.Contains(args, fixture.Store.GetValidatedRegisteredArtifactPath(fixture.Original.Artifact.Id));
            CollectionAssert.DoesNotContain(args, "-ss");
            CollectionAssert.DoesNotContain(args, "-c:a");
            CollectionAssert.Contains(args, "-n");
            Assert.IsFalse(File.Exists(compiled.OutputPath)); // Command test, not an actual extraction claim.
        }
    }

    [TestMethod]
    public async Task ActualShortPlanBuildsDeterministicVersionAndBlurredLayoutNotInventedExcerpt()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        StandardShortArtifactInput input = fixture.Shorts.Single();
        CompositionVersion version = StandardRenderGraphFactory.CreateShortVersion(fixture.Preparation, input.ShortFormId);
        Assert.AreEqual(version.Id, StandardRenderGraphFactory.CreateShortVersion(fixture.Preparation, input.ShortFormId).Id);
        Assert.AreEqual(fixture.Preparation.Child.Id, version.ParentVersionId);
        Assert.AreEqual(StandardGenerationFixture.Range(5, 15), version.Elements.Single().SourceRange);
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset,
            fixture.Geometry, true, input.Subtitles, fixture.Font.Evidence, input.ShortFormId);
        Assert.AreEqual(1080, output.ValidationContext.Profile.Width);
        Assert.AreEqual(1920, output.ValidationContext.Profile.Height);
        Assert.AreEqual(fixture.Preparation.Plan.ShortForms[0].Layout.DerivedLayoutSpec.Id, output.Graph.LayoutSpecId);
        ManualLayoutPlan layout = fixture.Preparation.Plan.ShortForms[0].Layout;
        Assert.AreEqual(ManualLayoutMode.BlurredSourceBackground, layout.Mode);
        NormalizedRect foreground = layout.SourcePlacements[1].Destination.Region.Rect;
        Assert.AreEqual(new RationalNumber(175, 512), foreground.Y);
        Assert.AreEqual(new RationalNumber(81, 256), foreground.Height);
        Assert.AreEqual(new RationalNumber(2625, 4), new RationalNumber(foreground.Y.Numerator * 1920, foreground.Y.Denominator));
        Assert.AreEqual(new RationalNumber(1215, 2), new RationalNumber(foreground.Height.Numerator * 1920, foreground.Height.Denominator));
        // The ideal full-source fit is fractional; the general pixel resolver must stay strict.
        Assert.ThrowsExactly<InvalidOperationException>(() => LayoutResolver.Resolve(layout.DerivedLayoutSpec, 1080, 1920));
        ContentDigest layoutBefore = StandardRenderGraphFactory.Digest(layout.DerivedLayoutSpec);
        // This calls the real compiler behind the fixture's BinaryVerifier seam, not FFmpeg.
        FfmpegCompilation compiled = await fixture.CompileGraphAsync(output);
        StringAssert.Contains(compiled.FilterScript, "gblur=sigma=20");
        StringAssert.Contains(compiled.FilterScript, "crop=603:1072:658:4:exact=1,scale=1080:1920:flags=bilinear");
        StringAssert.Contains(compiled.FilterScript, "crop=1920:1080:0:0:exact=1,scale=1080:1920:flags=bilinear:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1");
        StringAssert.Contains(compiled.FilterScript, "overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2:shortest=1");
        StringAssert.Contains(compiled.FilterScript, "fontsdir=");
        Assert.AreEqual(layoutBefore, StandardRenderGraphFactory.Digest(layout.DerivedLayoutSpec));
    }

    [TestMethod]
    public async Task WrongSourceAndSubtitleVersionAreRejectedAndAbsentCuesAreNotBurnIn()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var wrongSource = new Asset(fixture.Asset.Id, fixture.Asset.FileName, fixture.Asset.MediaType,
            StandardGenerationFixture.Hash("different"), fixture.Asset.ByteSize, fixture.Asset.Rights);
        Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardRenderGraphFactory.Create(
                fixture.Preparation, wrongSource, fixture.Geometry, false, fixture.Subtitles, fixture.Font.Evidence)).Reason);
        StandardSubtitleInput stale = fixture.MakeSubtitles(fixture.Parent, fixture.Preparation.Plan.LongForm.Profile.Layout,
            fixture.Spans[0], StandardGenerationFixture.Range(0, 30), 1920, 1080);
        Assert.AreEqual(StandardGenerationFailureReason.SubtitleMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardRenderGraphFactory.Create(
                fixture.Preparation, fixture.Asset, fixture.Geometry, true, stale, fixture.Font.Evidence)).Reason);
        StandardSubtitleInput empty = new([], fixture.Subtitles.DisplayProfile, fixture.Subtitles.AssProfile);
        Assert.AreEqual(StandardGenerationFailureReason.NoDisplayCues,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardRenderGraphFactory.Create(
                fixture.Preparation, fixture.Asset, fixture.Geometry, true, empty, fixture.Font.Evidence)).Reason);
    }

    [TestMethod]
    public async Task FontGeometryThatCannotFitResolutionIsRejected()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardSubtitleInput oversized = fixture.MakeSubtitles(fixture.Preparation.Child,
            fixture.Preparation.Plan.LongForm.Profile.Layout, fixture.Spans[0], StandardGenerationFixture.Range(0, 30),
            1920, 1080, fontSize: 2000);
        Assert.AreEqual(StandardGenerationFailureReason.SubtitleMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardRenderGraphFactory.Create(
                fixture.Preparation, fixture.Asset, fixture.Geometry, true, oversized, fixture.Font.Evidence)).Reason);
    }

    [TestMethod]
    public async Task SourceGeometryUsesSameMediaDimensionsSarAndRotationRatherThanCallerAspect()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        string canonical = CanonicalJson.Canonicalize(
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1"}]}""");
        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        Assert.AreEqual("192e1", stream.GetProperty("width").GetRawText());
        Assert.AreEqual("108e1", stream.GetProperty("height").GetRawText());
        SourceDisplayGeometry nominal = StandardSourceGeometry.Parse(fixture.Original.MediaInfo, canonical);
        Assert.AreEqual(fixture.Geometry, nominal);
        SourceDisplayGeometry rotated = StandardSourceGeometry.Parse(fixture.Original.MediaInfo,
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"4:3","side_data_list":[{"rotation":-90}]}]}""");
        Assert.AreEqual(new SourceDisplayGeometry(1080, 2560), rotated);
        Assert.AreEqual(new SourceDisplayGeometry(1080, 1920), StandardSourceGeometry.Parse(fixture.Original.MediaInfo,
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","side_data_list":[{"rotation":-90}],"tags":{"rotate":"270"}}]}"""));
        foreach (string invalid in new[]
        {
            """{"streams":[{"index":1,"width":1920,"height":1080,"sample_aspect_ratio":"1:1"}]}""",
            """{"streams":[{"index":0,"width":1280,"height":720,"sample_aspect_ratio":"1:1"}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"N/A"}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:7"}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","side_data_list":[{"rotation":45}]}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","side_data_list":[{"rotation":-90}],"tags":{"rotate":"90"}}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","tags":{"rotate":"90.5"}}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","tags":{"rotate":"2147483648"}}]}""",
        })
        {
            Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch,
                Assert.ThrowsExactly<StandardGenerationException>(() => StandardSourceGeometry.Parse(fixture.Original.MediaInfo, invalid)).Reason);
        }
    }

    [TestMethod]
    [DataRow(0, "0", 1920, 1080)]
    [DataRow(-90, "-9e1", 1080, 1920)]
    [DataRow(90, "9e1", 1080, 1920)]
    [DataRow(180, "18e1", 1920, 1080)]
    [DataRow(270, "27e1", 1080, 1920)]
    [DataRow(360, "36e1", 1920, 1080)]
    [DataRow(-450, "-45e1", 1080, 1920)]
    public void SourceGeometryAcceptsCanonicalRotationAndStreamIndex(
        int rotation, string canonicalRotation, int expectedWidth, int expectedHeight)
    {
        string canonical = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            streams = new[] { new { index = 10, width = 1920, height = 1080,
                sample_aspect_ratio = "1:1", side_data_list = new[] { new { rotation } } } },
        }));
        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        Assert.AreEqual("1e1", stream.GetProperty("index").GetRawText());
        Assert.AreEqual(canonicalRotation, stream.GetProperty("side_data_list")[0].GetProperty("rotation").GetRawText());
        Assert.AreEqual(new SourceDisplayGeometry(expectedWidth, expectedHeight),
            StandardSourceGeometry.Parse(CreateGeometryMedia(streamIndex: 10), canonical));
    }

    [TestMethod]
    [DataRow("192e1", 1920)]
    [DataRow("108e1", 1080)]
    [DataRow("1.920e3", 1920)]
    [DataRow("19200e-1", 1920)]
    [DataRow("-9e1", -90)]
    [DataRow("1e1", 10)]
    [DataRow("2147483647", int.MaxValue)]
    [DataRow("-2147483648", int.MinValue)]
    public void MathematicalInt32ReaderAcceptsEquivalentRepresentationsAndBoundaries(string json, int expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(expected, StandardSourceGeometry.ReadMathematicalInt32(document.RootElement));
    }

    [TestMethod]
    [DataRow("width", "1920.5")]
    [DataRow("height", "1080.5")]
    [DataRow("index", "0.5")]
    [DataRow("rotation", "90.5")]
    [DataRow("width", "1920.0000000000000000000000000000001")]
    [DataRow("height", "1080.0000000000000000000000000000001")]
    [DataRow("index", "1e-1000")]
    [DataRow("rotation", "-90.0000000000000000000000000000001")]
    [DataRow("width", "2147483648")]
    [DataRow("height", "2147483648")]
    [DataRow("index", "2147483648")]
    [DataRow("rotation", "2147483648")]
    [DataRow("width", "-2147483649")]
    [DataRow("height", "-2147483649")]
    [DataRow("index", "-2147483649")]
    [DataRow("rotation", "-2147483649")]
    [DataRow("width", "1e1000")]
    [DataRow("height", "1e1000")]
    [DataRow("index", "1e1000")]
    [DataRow("rotation", "1e1000")]
    [DataRow("width", "\"1920\"")]
    [DataRow("height", "null")]
    [DataRow("index", "false")]
    [DataRow("rotation", "\"-90\"")]
    public void SourceGeometryRejectsNonIntegralOutOfRangeOrNonNumericValues(string field, string valueJson)
    {
        JsonNode probe = JsonNode.Parse(
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1","side_data_list":[{"rotation":0}]}]}""")!;
        JsonNode stream = probe["streams"]![0]!;
        JsonNode target = field == "rotation" ? stream["side_data_list"]![0]! : stream;
        target[field] = JsonNode.Parse(valueJson);
        string canonical = CanonicalJson.Canonicalize(probe.ToJsonString());

        Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardSourceGeometry.Parse(CreateGeometryMedia(), canonical)).Reason);
    }

    private static MediaInfo CreateGeometryMedia(int streamIndex = 0) => new(
        EntityId<AssetIdTag>.New(), EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(),
        "geometry.mp4", StandardGenerationFixture.Hash("geometry fixture"), 1,
        MediaContainerClassification.IsoBaseMediaFtyp, ["mp4"], null, null,
        [new MediaStreamInfo(streamIndex, MediaStreamType.Video, "h264", null, 1920, 1080, "yuv420p",
            new RationalNumber(30, 1), new RationalNumber(30, 1), new RationalTime(1, 30),
            null, null, null, null, null)],
        null, null, [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]);
}

// Synthetic media/tool bytes are used only behind the internal compiler/process seam.
// Graph, canonical planner, subtitle writers, hashing and ArtifactStore remain real.
internal sealed class StandardGenerationFixture : IDisposable
{
    internal const string LoudnessReport = """
                [Parsed_loudnorm_99 @ synthetic] 
                {
                    "input_i": "-22.00", "input_tp": "-5.00", "input_lra": "4.00", "input_thresh": "-32.00",
                    "output_i": "-16.00", "output_tp": "-1.00", "output_lra": "4.00", "output_thresh": "-26.00",
                    "normalization_type": "dynamic", "target_offset": "0.00"
                }
                """;
    internal const string Actor = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset Created = new(2042, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.StandardGeneration", Guid.NewGuid().ToString("N"));
    internal string DatabasePath => Path.Combine(Root, "store.db");
    internal ArtifactStore Store { get; private set; } = null!;
    internal VersionService Versions { get; private set; } = null!;
    internal Asset Asset { get; private set; } = null!;
    internal Project Project { get; private set; } = null!;
    internal SourceSpan[] Spans { get; private set; } = [];
    internal TranscriptRevision Transcript { get; private set; } = null!;
    internal ContentDigest TranscriptDigest { get; private set; } = null!;
    private SubtitleCueCandidate[] _subtitleCandidates = [];
    internal CompositionVersion Parent { get; private set; } = null!;
    internal StandardCompositionPreparation Preparation { get; private set; } = null!;
    internal ImportedOriginal Original { get; private set; } = null!;
    internal PtsIndex Index { get; private set; } = null!;
    internal DefaultSettingsProfile SettingsProfile { get; private set; } = null!;
    internal DefaultSettingsSnapshot Settings { get; private set; } = null!;
    internal FfmpegToolBinding Tool { get; private set; } = null!;
    internal RenderToolEvidence ToolEvidence { get; private set; } = null!;
    internal RenderModelEvidence Model { get; } = new("test", "fixture", "1", Hash("model"));
    internal FfmpegFontBinding Font { get; private set; } = null!;
    internal StandardSubtitleInput Subtitles { get; private set; } = null!;
    internal StandardShortArtifactInput[] Shorts { get; private set; } = [];
    internal StandardMusicRenderInput? Music { get; private set; }
    internal SourceDisplayGeometry Geometry { get; } = new(1920, 1080);

    internal static async Task<StandardGenerationFixture> CreateAsync(bool includeShort = false, bool persistPreparation = true,
        RationalTime? firstCutStart = null, RationalTime? firstCutDuration = null,
        RationalTime sourceClockStart = default, MusicPlanningResult? musicPlanning = null,
        int sourceSampleRate = 48000, bool includeUntimedEnding = false,
        bool includeThumbnail = false, string sourceFileName = "source.mp4",
        IReadOnlyList<DefaultSettingSelection>? settingsOverrides = null,
        bool includeCardMusic = false, bool musicSpeechOverlap = false, bool includeTimedEnding = false,
        IReadOnlyList<PtsIndexEntry>? sourceFrames = null, RationalTime? sourceTimeBase = null,
        string fontName = "Fixture Sans", RationalTime? secondTokenDuration = null, string topicTitle = "Topic")
    {
        var fixture = new StandardGenerationFixture();
        Directory.CreateDirectory(fixture.Root);
        _ = DatabaseMigrator.Migrate(fixture.DatabasePath, Path.Combine(fixture.Root, "backups"));
        await fixture.InitializeAsync(includeShort, persistPreparation, firstCutStart, firstCutDuration,
            sourceClockStart, musicPlanning, sourceSampleRate, includeUntimedEnding, includeThumbnail, sourceFileName, settingsOverrides,
            includeCardMusic, musicSpeechOverlap, includeTimedEnding, sourceFrames, sourceTimeBase, fontName, secondTokenDuration, topicTitle);
        return fixture;
    }

    private async Task InitializeAsync(bool includeShort, bool persistPreparation,
        RationalTime? firstCutStart, RationalTime? firstCutDuration, RationalTime sourceClockStart,
        MusicPlanningResult? musicPlanning, int sourceSampleRate, bool includeUntimedEnding,
        bool includeThumbnail, string sourceFileName, IReadOnlyList<DefaultSettingSelection>? settingsOverrides,
        bool includeCardMusic, bool musicSpeechOverlap, bool includeTimedEnding,
        IReadOnlyList<PtsIndexEntry>? sourceFrames, RationalTime? sourceTimeBase,
        string fontName, RationalTime? secondTokenDuration, string topicTitle)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes("actual test source bytes, not an encoded video");
        Asset = new Asset(EntityId<AssetIdTag>.New(), sourceFileName, "video/mp4", ContentDigest.Compute(sourceBytes),
            sourceBytes.Length, new RightsRecord(RightsStatus.Approved, "test:source", "Internal only", false));
        Project = new Project(EntityId<ProjectIdTag>.New(), HubRadio.App.Projects.SourceOnlyProjectCreationContract.DeriveContentName(sourceFileName),
            "test", "test", PublicationScope.InternalOnly, null, [Asset], []);
        var projectStore = new ProjectStore(DatabasePath);
        string projectJson = ProjectSnapshotJson.WriteProject(Project);
        projectStore.CreateProject(Project, projectJson, Created);
        Versions = new VersionService(projectStore);
        Store = new ArtifactStore(DatabasePath, Root);
        string staging = Path.Combine(Root, "projects", Project.Id.ToString(), "staging");
        Directory.CreateDirectory(staging);
        string originalPath = Path.Combine(staging, "source.mp4");
        await File.WriteAllBytesAsync(originalPath, sourceBytes);
        StoredArtifact stored = await Store.PromoteAsync(new ArtifactPromotionRequest(EntityId<ArtifactIdTag>.New(), Project.Id,
            ArtifactKind.Original, originalPath, "source.mp4", Asset.ContentDigest, Asset.ByteSize, Created));
        EntityId<JobIdTag> job = EntityId<JobIdTag>.New();
        Index = new PtsIndex(Asset.Id, Project.Id, job, Asset.FileName, Asset.ContentDigest, Asset.ByteSize,
            0, sourceTimeBase ?? new RationalTime(1, 30), sourceFrames ?? Enumerable.Range(0, 2400).Select(i => new PtsIndexEntry(i,
                sourceClockStart + new RationalTime(i, 30), new RationalTime(1, 30), true)).ToArray());
        // Preserve deliberately different measured frame timing; only normalize
        // the warning evidence required by these synthetic metadata fields.
        RationalTime formatDuration = sourceClockStart + new RationalTime(80, 1);
        MediaDurationFieldEvidence[] durationFields =
        [
            new(MediaDurationFieldKind.FormatDuration, null, formatDuration),
            new(MediaDurationFieldKind.StreamEndOffset, 0, sourceClockStart + Index.PresentationRange.Duration),
            new(MediaDurationFieldKind.StreamEndOffset, 1, sourceClockStart + new RationalTime(80, 1)),
        ];
        MediaInfoWarning[] mediaWarnings = durationFields.Any(field => field.Value != formatDuration)
            ? [new MediaInfoWarning(MediaInfoWarningCode.DurationFieldsDiffer, new DurationFieldsDifferWarningEvidence(durationFields))]
            : [];
        var media = new MediaInfo(Asset.Id, Project.Id, job, Asset.FileName, Asset.ContentDigest, Asset.ByteSize,
            MediaContainerClassification.IsoBaseMediaFtyp, ["mp4"], null, formatDuration,
            [new MediaStreamInfo(0, MediaStreamType.Video, "h264", null, 1920, 1080, "yuv420p",
                new RationalNumber(30, 1), new RationalNumber(30, 1), Index.TimeBase, sourceClockStart, Index.PresentationRange.Duration, null, null, null),
             new MediaStreamInfo(1, MediaStreamType.Audio, "aac", null, null, null, null, null, null,
                new RationalTime(1, sourceSampleRate), sourceClockStart, new RationalTime(80, 1), sourceSampleRate, 2, "stereo")], null, null, mediaWarnings);
        Original = new ImportedOriginal(Asset, stored, media, new DecodeValidationResult(Asset.Id, Project.Id, job,
            Asset.FileName, Asset.ContentDigest, Asset.ByteSize, 0, 1, TimeSpan.Zero, 0, Hash(""), Index.PresentationStart,
            Index.PresentationEnd, Index.Entries.Count), OriginalIntegrityStatus.Verified);
        SettingsProfile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
        Settings = SettingsProfile.CreateSnapshot(SettingsProfile.CreateSnapshot().Values.Select(item =>
            settingsOverrides?.SingleOrDefault(value => value.Key == item.Key) ?? item).ToArray());
        var measuredLong = BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")));
        var landscape = StandardRenderProfileFactory.CreateLandscape(SettingsProfile, Settings, measuredLong, new RationalNumber(30, 1));
        EntityId<TranscriptRevisionIdTag> transcript = EntityId<TranscriptRevisionIdTag>.New();
        Spans = new[] { new TimeRange(firstCutStart ?? default, firstCutDuration ?? new RationalTime(30, 1)), Range(40, 30) }.Select(range => new SourceSpan(EntityId<SourceSpanIdTag>.New(),
            Asset.Id, "audio:1", range, Asset.ContentDigest, transcript, [EntityId<TranscriptTokenIdTag>.New()])).ToArray();
        var utterances = Spans.Select((span, i) =>
        {
            var token = new Token(span.TranscriptTokenIds[0], i == 0 ? "元の字幕。" : "次の字幕。",
                i == 0 ? (span.Range.Duration < new RationalTime(20, 1) ? span.Range : Range(5, 15))
                    : new TimeRange(new RationalTime(42, 1), secondTokenDuration ?? new RationalTime(8, 1)), null);
            return new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), span.Id, EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker", token.Text, includeCardMusic && !musicSpeechOverlap ? token.Range!.Value : span.Range, null,
                [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), token.Text, [token])]);
        }).ToArray();
        var transcriptRevision = new TranscriptRevision(transcript, EntityId<TranscriptRevisionIdTag>.New(),
            TranscriptChangeSource.Human, Created, utterances);
        Transcript = transcriptRevision;
        TranscriptDigest = Hash(TranscriptSnapshotJson.WriteRevision(transcriptRevision));
        SourceSpan[] speechSpans = Spans.Select((span, i) => new SourceSpan(span.Id, span.AssetId, span.StreamId,
            utterances[i].Range, span.SourceDigest, span.TranscriptRevisionId, span.TranscriptTokenIds)).ToArray();
        _subtitleCandidates = SubtitleSegmenter.Segment(transcriptRevision, TranscriptDigest, speechSpans).Candidates.ToArray();
        var elements = Spans.Select((span, i) => new CompositionElement(EntityId<CompositionElementIdTag>.New(),
            new TimeRange(i == 0 ? default : Spans[0].Range.Duration, span.Range.Duration),
            OriginRef.FromSourceSpan(span.Id), sourceRange: span.Range)).ToArray();
        Parent = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), Project.Id, null, Actor, Created, "test parent",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
        TimelineMap parentMap = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), Parent);
        var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), landscape.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
        LongFormElementInput Omitted(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(),
            kind, false, null, null, [], []);
        LongFormTopicInput[] topics = [];
        MusicPlanningResult music = musicPlanning ?? new MusicPlanningService().Plan(
            MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default);
        if (includeCardMusic)
        {
            var topicRange = new TopicRangeRevision(EntityId<TopicRangeIdTag>.New(), EntityId<TopicRangeRevisionIdTag>.New(),
                null, transcript, TranscriptDigest, null, null, Range(20, 5), null, Actor, Created, "Explicit test card");
            var topicMetadata = new TopicMetadataRevision(EntityId<TopicMetadataIdTag>.New(), EntityId<TopicMetadataRevisionIdTag>.New(),
                null, Project.Id, topicRange.TopicRangeId, topicRange.Id, topicRange.Range, transcript, TranscriptDigest,
                new TopicMetadata(topicTitle, TopicMetadataValueOrigin.DeterministicPlaceholder, null, TopicMetadataValueOrigin.Unset,
                    null, TopicMetadataValueOrigin.Unset, false, [], TopicMetadataValueOrigin.Unset, [], TopicMetadataValueOrigin.Unset,
                    "test", TopicMetadataValueOrigin.ProjectSnapshot, [], TopicMetadataValueOrigin.Unset),
                TopicMetadataField.Initial, Actor, Created, "Explicit test card metadata");
            var card = new LongFormElementInput(EntityId<LongFormElementIdTag>.New(), LongFormElementKind.Topic, true,
                topicTitle, Range(20, 5), [OriginRef.FromSourceSpan(Spans[0].Id)], []);
            topics = [new LongFormTopicInput(card, topicRange, topicMetadata)];
            byte[] musicBytes = Encoding.UTF8.GetBytes("synthetic MP3 test bytes");
            ContentDigest musicDigest = ContentDigest.Compute(musicBytes);
            string musicDirectory = Path.Combine(Root, "Music");
            Directory.CreateDirectory(musicDirectory);
            string musicPath = Path.Combine(musicDirectory, "track';[literal].mp3");
            await File.WriteAllBytesAsync(musicPath, musicBytes);
            MusicCatalog catalog = MusicCatalog.CreateAvailable([new MusicTrack(musicDigest, musicBytes.Length, 10m, "mp3", 48000, 2)]);
            MusicCue cue = StandardMusicRenderInput.CreateCue(card.Id, MusicCueKind.TopicCard, card.OutputRange!.Value, false);
            music = new MusicPlanningService().Plan(catalog, [cue], MusicPlanningOptions.Default);
            Music = new StandardMusicRenderInput(catalog, transcriptRevision, [new StandardMusicCardBinding(card.Id, cue)],
                [new BinaryVerificationRequest("test.music", musicPath, musicDigest, musicBytes.Length)]);
        }
        var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), Project,
            new LongFormProvenanceSnapshot(Project.Id, transcript, TranscriptDigest, Spans, []), template, landscape,
            LongFormDestination.InternalDistribution, null,
            Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening),
            Omitted(LongFormElementKind.ProgramName), Omitted(LongFormElementKind.EpisodeNumber),
            Omitted(LongFormElementKind.EpisodeTitle), Omitted(LongFormElementKind.PerformerIntroduction),
            Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), topics,
            Omitted(LongFormElementKind.Summary),
            [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
            includeUntimedEnding || includeTimedEnding ? new LongFormElementInput(EntityId<LongFormElementIdTag>.New(), LongFormElementKind.Ending,
                true, null, includeTimedEnding ? Range(60, 5) : null, [OriginRef.FromSourceSpan(Spans[0].Id)], []) : Omitted(LongFormElementKind.Ending),
            Omitted(LongFormElementKind.PostRollMargin));
        string toolPath = Path.Combine(Root, "ffmpeg.exe");
        await File.WriteAllBytesAsync(toolPath, [1, 2, 3]);
        Tool = new FfmpegToolBinding(new BinaryVerificationRequest("ffmpeg", toolPath, new ContentDigest(FfmpegCompiler.FixedFfmpegSha256), FfmpegCompiler.FixedFfmpegByteSize));
        ToolEvidence = new RenderToolEvidence("ffmpeg", DecodeValidator.CanonicalPackageVersion, "fixture", Tool.VerificationRequest.ExpectedDigest, null);
        string fontPath = Path.Combine(Root, "font.ttf");
        byte[] fontBytes = [10, 20, 30];
        await File.WriteAllBytesAsync(fontPath, fontBytes);
        Font = new FfmpegFontBinding(new RenderFontEvidence(fontName, ContentDigest.Compute(fontBytes)),
            new BinaryVerificationRequest("font", fontPath, ContentDigest.Compute(fontBytes), fontBytes.Length));
        var metadata = new TopicMetadataRevision(EntityId<TopicMetadataIdTag>.New(), EntityId<TopicMetadataRevisionIdTag>.New(), null,
            Project.Id, EntityId<TopicRangeIdTag>.New(), EntityId<TopicRangeRevisionIdTag>.New(), Range(0, 30), transcript, TranscriptDigest,
            new TopicMetadata("Topic", TopicMetadataValueOrigin.DeterministicPlaceholder, null, TopicMetadataValueOrigin.Unset,
                null, TopicMetadataValueOrigin.Unset, false, [], TopicMetadataValueOrigin.Unset, [], TopicMetadataValueOrigin.Unset,
                "test", TopicMetadataValueOrigin.ProjectSnapshot, [], TopicMetadataValueOrigin.Unset),
            TopicMetadataField.Initial, Actor, Created, "test metadata");
        var shortSpan = new SourceSpan(EntityId<SourceSpanIdTag>.New(), Asset.Id, "audio:1", Range(5, 15), Asset.ContentDigest, transcript, Spans[0].TranscriptTokenIds);
        var suggestion = new Suggestion(EntityId<SuggestionIdTag>.New(), EntityId<ModelRunIdTag>.New(), SuggestionKind.Highlight,
            SuggestionStatus.Proposed, OriginRef.FromSourceSpan(shortSpan.Id), "test selection", null, null, null);
        var candidate = SuggestionService.Register(new EmphasisCandidate(suggestion, shortSpan, transcript, TranscriptDigest, metadata.Id,
            EmphasisCandidateService.RuleVersion, "actual excerpt", EmphasisTextSourceKind.OriginalTranscript, [EmphasisReasonKind.ImportantStatement], []), []);
        var workingCopy = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), Project.Id, Parent.Id, Created, Created, Parent.Elements);
        projectStore.AppendCompositionVersion(Parent, CompositionSnapshotJson.WriteVersion(Parent));
        projectStore.CreateWorkingCopy(workingCopy, CompositionSnapshotJson.WriteWorkingCopy(workingCopy));
        // Explicit synthetic preview evidence for planner/command tests, not a real image claim.
        StandardThumbnailEvidence? thumbnailEvidence = includeThumbnail ? new StandardThumbnailEvidence(Project.Id, Asset.Id,
            stored.Id, Asset.FileName, Asset.ContentDigest, Asset.ByteSize, 0, Hash(Index.CanonicalJson), Hash("map"),
            EntityId<ArtifactIdTag>.New(), Hash("proxy"), 1, Hash("proxy-index"), EntityId<ArtifactIdTag>.New(), Hash("sprite"), 1,
            EntityId<ArtifactIdTag>.New(), Hash("sprite-index"), 1, "fixture-1",
            [new StandardThumbnailFrameEvidence(0, 0, default, 450, new RationalTime(15, 1), new RationalTime(1, 30), true)]) : null;
        var orchestration = new StandardCompositionOrchestrationRequest(Parent, workingCopy, 0,
            new StandardProjectStoreState(0, Hash(projectJson), Created), SettingsProfile, Settings,
            File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")),
            File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json")),
            longForm, parentMap.Id, Geometry, new RationalNumber(30, 1), ToolEvidence.ExecutableDigest,
            Model.WeightsDigest, music, includeShort ? [metadata] : [], includeShort ? [candidate] : [], [],
            Actor, Created, "test standard child", thumbnailEvidence);
        Preparation = persistPreparation
            ? StandardCompositionOrchestrator.Apply(Versions, orchestration).Preparation
            : StandardCompositionOrchestrator.Prepare(orchestration);
        Subtitles = MakeSubtitles(Preparation.Child, landscape.Layout, Spans[0], Range(0, 30), 1920, 1080);
        Shorts = Preparation.Plan.ShortForms.Select(item => new StandardShortArtifactInput(item.Id,
            MakeSubtitles(StandardRenderGraphFactory.CreateShortVersion(Preparation, item.Id), item.Layout.DerivedLayoutSpec,
                item.SourceSpans[0], Range(0, 15), 1080, 1920))).ToArray();
    }

    internal StandardSubtitleInput MakeSubtitles(CompositionVersion version, LayoutSpec layout, SourceSpan span, TimeRange range,
        int width, int height, int fontSize = 36)
    {
        SubtitleCueCandidate candidate = _subtitleCandidates.First(item => item.SourceRange.Start >= span.Range.Start
            && item.SourceRange.End <= span.Range.End);
        range = new TimeRange(range.Start + candidate.SourceRange.Start - span.Range.Start, candidate.SourceRange.Duration);
        LayoutRegion region = layout.Regions.First(static item => item.Role == LayoutRegionRole.Subtitle);
        var profile = new SubtitleLayoutProfile(layout.Id, Hash("subtitle-layout"), [layout.SafeZones[0].Name],
            Enum.GetValues<SubtitleSoftConstraintKind>().Select(static kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());
        var placement = new SubtitlePlacementCandidate(EntityId<SubtitlePlacementCandidateIdTag>.New(), region.Name,
            new SubtitleLayoutRect(new RationalNumber(1, 10), new RationalNumber(4, 5),
                new RationalNumber(4, 5), new RationalNumber(1, 10)));
        SubtitleLayoutResolution resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, layout, profile, [placement], [], [], null));
        SubtitleCueHistory history = SubtitleService.CreateInitial(version, candidate, resolution, EntityId<CueIdTag>.New(),
            EntityId<SubtitleCueRevisionIdTag>.New(), range, Actor, Created, "test subtitle");
        var white = new AssColor(0, 255, 255, 255);
        var black = new AssColor(0, 0, 0, 0);
        var ass = new AssSubtitleProfile(width, height, Enum.GetValues<SubtitleFileCueStyle>().Select(style =>
            new AssStyleDefinition(style, Font.Evidence.Name, fontSize, white, white, black, black, false, false, false, false,
                100, 100, 0, 0, AssBorderStyle.OutlineAndShadow, 2, 0, AssAlignment.BottomCenter, new AssMargins(40, 40, 40), 1)).ToArray());
        return new StandardSubtitleInput([history], new SubtitleDisplayProfile(Hash("display"), SubtitleCueDisplayMode.Full,
            SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod), ass);
    }

    internal StandardArtifactGenerationRequest Request(DefaultSettingsSnapshot? settings = null, RenderModelEvidence? model = null,
        StandardSubtitleInput? subtitles = null, SourceDisplayGeometry? geometry = null,
        StandardCompositionPreparation? preparation = null, StandardMusicRenderInput? music = null,
        PtsIndex? index = null, IReadOnlyList<StandardShortArtifactInput>? shorts = null) =>
        new(preparation ?? Preparation, Original, index ?? Index, geometry ?? Geometry, SettingsProfile, settings ?? Settings,
            Tool, ToolEvidence, model ?? Model, Font, subtitles ?? Subtitles, shorts ?? Shorts, "test-1", Hash("application"), TimeSpan.FromMinutes(2), music ?? Music);

    // These test inputs contain initial revisions, so the exact same candidates
    // and revisions can be reused when testing warning-only changes.
    internal static StandardSubtitleInput WithWarnings(StandardSubtitleInput input, IReadOnlyList<StandardSubtitleBuildWarning> warnings) =>
        new(input.Revisions.Select(revision => new SubtitleCueHistory(
            input.Candidates.Single(candidate => candidate.Id == revision.SourceCandidateId), [revision])).ToArray(),
            input.DisplayProfile, input.AssProfile, warnings);

    internal Task<SourceDisplayGeometry> ProbeGeometryAsync(StandardArtifactGenerationRequest request, string sourcePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.AreEqual(Store.GetValidatedRegisteredArtifactPath(Original.Artifact.Id), sourcePath);
        // Synthetic probe only; same parser and MediaInfo identity/dimensions as production.
        return Task.FromResult(StandardSourceGeometry.Parse(request.Original.MediaInfo,
            """{"streams":[{"index":0,"width":1920,"height":1080,"sample_aspect_ratio":"1:1"}]}"""));
    }

    internal StandardRenderGraphOutput Graph(bool burnedIn) => StandardRenderGraphFactory.Create(Preparation, Asset, Geometry, burnedIn, Subtitles, Font.Evidence, music: Music);

    internal async Task<FfmpegCompilation> CompileGraphAsync(StandardRenderGraphOutput output)
    {
        var bindings = new List<FfmpegInputBinding>();
        foreach (RenderGraphInput input in output.Graph.Inputs)
        {
            string path = Store.GetValidatedRegisteredArtifactPath(Original.Artifact.Id);
            if (input.Kind is RenderInputKind.Subtitle or RenderInputKind.Graphic)
            {
                path = Path.Combine(Root, input.Id + ".ass");
                SubtitleExportFile file = input.Kind == RenderInputKind.Subtitle
                    ? output.SubtitleFiles.Single(item => item.Input.Id == input.Id).File
                    : output.GraphicFiles.Single(item => item.Input.Id == input.Id).File;
                await File.WriteAllBytesAsync(path, file.GetUtf8Bytes());
            }

            bindings.Add(new FfmpegInputBinding(input.Id, new BinaryVerificationRequest("input", path, input.ContentDigest, input.ByteSize)));
        }

        return await CompileAsync(new FfmpegCompileRequest(output.Graph, output.ValidationContext, Tool, bindings,
            Path.Combine(Root, output.FrameExtraction is null ? "test-output.mp4" : "test-output.png"), Root, TimeSpan.FromMinutes(1), output.VideoGeometry,
            output.Graph.Fonts.Count == 0 ? [] : [Font], graphics: output.GraphicFiles.Select(static file => file.Binding).ToArray(),
            encoderSettings: output.MusicBindings.Count == 0 ? null : Request().Configuration.Encoder,
            frameExtraction: output.FrameExtraction, music: output.MusicBindings), CancellationToken.None);
    }

    internal static Task<FfmpegCompilation> CompileAsync(FfmpegCompileRequest request, CancellationToken token) =>
        FfmpegCompiler.CompileAsync(request, (binding, _) => Task.FromResult(
            BinaryVerificationResult.Verified(binding, binding.ExpectedByteSize, binding.ExpectedDigest)), token);

    internal static ContentDigest Hash(string text) => ContentDigest.Compute(Encoding.UTF8.GetBytes(text));
    internal static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));
    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) directory = directory.Parent;
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}