using System.Reflection;
using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class FfmpegCompilerTests
{
    [TestMethod]
    public async Task ValidGraphCompilesToAtomicArgumentListAndAllowlistedFilterScript()
    {
        using Fixture fixture = Fixture.Create();

        FfmpegCompilation compilation = await CompileAsync(fixture, fixture.Request());

        Assert.AreEqual(fixture.ToolRequest.FilePath, compilation.ProcessRequest.Executable);
        Assert.AreEqual(fixture.RootPath, compilation.ProcessRequest.WorkingDirectory);
        Assert.AreEqual(fixture.OutputPath, compilation.OutputPath);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(compilation.FilterScript)),
            compilation.FilterScriptDigest);
        CollectionAssert.AreEqual(
            new[] { fixture.VideoPath, fixture.GraphicPath, fixture.AudioPath },
            InputPathTokens(compilation.ProcessRequest.ArgumentList));
        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "-filter_complex");
        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            compilation.FilterScript);
        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "h264_mf");
        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "aac");
        string[] arguments = compilation.ProcessRequest.ArgumentList.ToArray();
        Assert.AreEqual("48000", arguments[Array.IndexOf(arguments, "-ar") + 1]);
        Assert.AreEqual("aac_low", arguments[Array.IndexOf(arguments, "-profile:a") + 1]);
        CollectionAssert.AreEqual(
            new[] { "[v2]", "[a3]" },
            MapTokens(compilation.ProcessRequest.ArgumentList));
        StringAssert.Contains(
            compilation.FilterScript,
            "scale=1280:720:flags=bilinear,fps=30/1,format=yuv420p[v0]");
        StringAssert.Contains(
            compilation.FilterScript,
            "overlay=x=0:y=0:enable='between(t,2,3)':eof_action=repeat[v1]");
        StringAssert.Contains(
            compilation.FilterScript,
            "[v1]subtitles=filename=");
        StringAssert.Contains(compilation.FilterScript, "[v2]");
        Assert.IsFalse(compilation.ProcessRequest.ArgumentList.Any(static argument =>
            argument.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || argument.Contains("powershell", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.Contains(compilation.ProcessRequest.ArgumentList.ToArray(), "-n");
        CollectionAssert.DoesNotContain(compilation.ProcessRequest.ArgumentList.ToArray(), "-y");
        CollectionAssert.DoesNotContain(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "-shortest");
    }

    [TestMethod]
    public async Task TypedEncoderSettingsAreOptInAndDoNotChangeLegacyDefaults()
    {
        using Fixture fixture = Fixture.Create();
        FfmpegCompilation legacy = await CompileAsync(fixture, fixture.Request());
        var settings = new FfmpegEncoderSettings(8_000_000, 384_000, 2, new FfmpegLoudnessTarget(-16m, -1m));
        FfmpegCompilation standard = await CompileAsync(fixture, fixture.Request(encoderSettings: settings));
        string[] oldArgs = legacy.ProcessRequest.ArgumentList.ToArray();
        string[] args = standard.ProcessRequest.ArgumentList.ToArray();
        CollectionAssert.DoesNotContain(oldArgs, "-profile:v");
        CollectionAssert.DoesNotContain(oldArgs, "-b:v");
        CollectionAssert.DoesNotContain(oldArgs, "-b:a");
        Assert.IsFalse(legacy.FilterScript.Contains("loudnorm", StringComparison.Ordinal));
        Assert.AreEqual("100", args[Array.IndexOf(args, "-profile:v") + 1]);
        Assert.AreEqual("8000000", args[Array.IndexOf(args, "-b:v") + 1]);
        Assert.AreEqual("384000", args[Array.IndexOf(args, "-b:a") + 1]);
        Assert.AreEqual("48000", args[Array.IndexOf(args, "-ar") + 1]);
        Assert.AreEqual("aac_low", args[Array.IndexOf(args, "-profile:a") + 1]);
        Assert.AreEqual("bt709", args[Array.IndexOf(args, "-colorspace") + 1]);
        Assert.AreEqual("h264_metadata=video_full_range_flag=0:colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1",
            args[Array.IndexOf(args, "-bsf:v") + 1]);
        Assert.AreEqual("30/1", args[Array.IndexOf(args, "-r") + 1]);
        StringAssert.Contains(standard.FilterScript, "aformat=channel_layouts=stereo,loudnorm=I=-16:TP=-1:LRA=11:linear=false:print_format=json");
        StringAssert.Contains(standard.FilterScript, "yadif=mode=send_frame:parity=auto:deint=interlaced,setfield=prog");
        StringAssert.Contains(standard.FilterScript, "setparams=field_mode=prog:color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=limited");
        CollectionAssert.Contains(args, "-nostats");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegEncoderSettings(0, 384_000, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegEncoderSettings(8_000_000, -1, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegEncoderSettings(8_000_000, 384_000, 6));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegLoudnessTarget(-71m, -1m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegLoudnessTarget(-16m, 1m));
    }

    [TestMethod]
    public async Task GeneratedScriptIsExactNewLocalFileKeptReadOnlyThroughExecution()
    {
        using Fixture fixture = Fixture.Create();
        string path = Path.Combine(fixture.RootPath, "graph';[literal].ffscript");
        using FfmpegCompilation compiled = await CompileAsync(fixture,
            fixture.Request(generatedScript: new FfmpegGeneratedScriptTarget(path)));
        Assert.AreEqual(path, compiled.GeneratedScriptPath);
        Assert.AreEqual(compiled.FilterScript, await File.ReadAllTextAsync(path));
        Assert.AreEqual(compiled.FilterScriptDigest, ContentDigest.Compute(await File.ReadAllBytesAsync(path)));
        string[] args = compiled.ProcessRequest.ArgumentList.ToArray();
        Assert.AreEqual(path, args[Array.IndexOf(args, "-/filter_complex") + 1]);
        CollectionAssert.DoesNotContain(args, compiled.FilterScript);
        CollectionAssert.DoesNotContain(args, "-filter_complex");
        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var ignored = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
        }
        Assert.IsFalse(File.Exists(compiled.OutputPath));
    }

    [TestMethod]
    public async Task GeneratedScriptRejectsExistingTraversalAndControlCharacterTargetsWithoutOverwrite()
    {
        using Fixture fixture = Fixture.Create();
        string existing = Path.Combine(fixture.RootPath, "prior.ffscript");
        byte[] original = Encoding.UTF8.GetBytes("prior artifact must survive");
        await File.WriteAllBytesAsync(existing, original);
        foreach (string path in new[]
        {
            existing,
            Path.Combine(fixture.RootPath, "..", "outside.ffscript"),
            Path.Combine(fixture.RootPath, "private\n-filter_complex.ffscript"),
            Path.Combine(fixture.RootPath, "private\0.ffscript"),
            Path.Combine(fixture.RootPath, "private\ud800.ffscript"),
            Path.Combine(fixture.RootPath, "prior:alternate.ffscript"),
            Path.Combine(fixture.RootPath, "CON.ffscript"),
            fixture.OutputPath,
        })
        {
            await AssertFailureBeforeVerificationAsync(fixture.Request(generatedScript: new FfmpegGeneratedScriptTarget(path)),
                FfmpegCompilerFailureReason.OutputPathInvalid);
        }
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(existing));
        Assert.HasCount(1, Directory.GetFiles(fixture.RootPath));
        Assert.ThrowsExactly<ArgumentNullException>(() => new FfmpegGeneratedScriptTarget(null!));
    }

    [TestMethod]
    public async Task LargeSubtitleChainUsesGeneratedFileRatherThanExceedingWindowsArgumentLimit()
    {
        using Fixture fixture = Fixture.Create();
        var inputs = fixture.Graph.Inputs.ToList();
        var nodes = fixture.Graph.Nodes.Where(node => node.Id != fixture.Graph.OutputNodeId).ToList();
        var bindings = fixture.Bindings.ToList();
        RenderNode prior = nodes.Single(static node => node.Kind == RenderNodeKind.Subtitle);
        for (int i = 0; i < 128; i++)
        {
            var input = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Subtitle, Digest('8'), 80, prior.Origin!);
            RenderNode cue = RenderNode.Subtitle(EntityId<RenderNodeIdTag>.New(), input.Id, prior.Id,
                new Cue(EntityId<CueIdTag>.New(), CueKind.Subtitle, Range(1, 2), "subtitle"), input.Origin);
            inputs.Add(input);
            nodes.Add(cue);
            bindings.Add(fixture.Binding(input.Id, "subtitle-" + i + new string('x', 240) + ".ass", input.ContentDigest, input.ByteSize));
            prior = cue;
        }
        RenderNode audio = nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
        RenderNode output = RenderNode.Output(fixture.Graph.OutputNodeId, [prior.Id, audio.Id], Range(0, 4));
        nodes.Add(output);
        var graph = new RenderGraph(fixture.Graph.Id, fixture.Graph.CompositionVersionId, fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId, fixture.Graph.TimelineMapDigest, fixture.Graph.ProfileId, fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId, fixture.Graph.LayoutSpecDigest, fixture.Graph.Template, inputs, fixture.Graph.Fonts, nodes, output.Id);
        RenderGraphValidationContext context = ContextForInputs(fixture, inputs);
        await AssertFailureAsync(() => CompileAsync(fixture, fixture.Request(graph: graph, context: context, bindings: bindings)),
            FfmpegCompilerFailureReason.CommandLineTooLong);
        using FfmpegCompilation compiled = await CompileAsync(fixture, fixture.Request(graph: graph, context: context, bindings: bindings,
            generatedScript: new FfmpegGeneratedScriptTarget(Path.Combine(fixture.RootPath, "large.ffscript"))));
        Assert.IsTrue(compiled.FilterScript.Length > FfmpegCompiler.MaximumWindowsCommandLineCharacters);
        Assert.IsTrue(compiled.FilterScript.Length < FfmpegCompiler.MaximumGeneratedScriptBytes);
        Assert.IsTrue(compiled.ProcessRequest.ArgumentList.Sum(static value => value.Length + 1) < 4096);
        Assert.AreEqual(compiled.FilterScriptDigest, ContentDigest.Compute(await File.ReadAllBytesAsync(compiled.GeneratedScriptPath!)));
    }

    [TestMethod]
    public async Task MetadataGraphicsAreVerifiedSeparateFromSpeechSubtitlesAndCannotInjectFilters()
    {
        using Fixture fixture = Fixture.Create();
        RenderNode target = fixture.Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Graphic);
        var input = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Graphic, Digest('8'), 100, target.Origin!);
        RenderGraphInput[] inputs = [.. fixture.Graph.Inputs, input];
        var graph = new RenderGraph(fixture.Graph.Id, fixture.Graph.CompositionVersionId, fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId, fixture.Graph.TimelineMapDigest, fixture.Graph.ProfileId, fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId, fixture.Graph.LayoutSpecDigest, fixture.Graph.Template, inputs, fixture.Graph.Fonts,
            fixture.Graph.Nodes, fixture.Graph.OutputNodeId);
        var font = new FfmpegFontBinding(fixture.Graph.Fonts[0], new BinaryVerificationRequest("font", Path.Combine(fixture.RootPath, "font.ttf"),
            fixture.Graph.Fonts[0].FileDigest, 10));
        FfmpegInputBinding binding = fixture.Binding(input.Id, "title';[in],literal.ass", input.ContentDigest, input.ByteSize);
        FfmpegCompileRequest Request(FfmpegAssGraphicBinding graphic) => new(graph, ContextForInputs(fixture, inputs), fixture.Tool,
            [.. fixture.Bindings, binding], fixture.OutputPath, fixture.RootPath, TimeSpan.FromMinutes(1), fonts: [font], graphics: [graphic]);
        FfmpegCompilation compiled = await CompileAsync(fixture, Request(new FfmpegAssGraphicBinding(input.Id, target.Id, Range(0, 3))));
        StringAssert.Contains(compiled.FilterScript, "ass=filename=");
        StringAssert.Contains(compiled.FilterScript, "title\\\\\\'\\;\\[in\\]\\,literal.ass");
        Assert.IsFalse(compiled.FilterScript.Contains("ass=filename=" + binding.VerificationRequest.FilePath, StringComparison.Ordinal));
        Assert.IsFalse(compiled.FilterScript.Contains(":enable='gte(t", StringComparison.Ordinal));
        Assert.AreEqual(1, compiled.FilterScript.Split("subtitles=filename=", StringSplitOptions.None).Length - 1);
        await AssertFailureBeforeVerificationAsync(Request(new FfmpegAssGraphicBinding(input.Id, target.Id, Range(3, 2))),
            FfmpegCompilerFailureReason.InputBindingMismatch);
        await AssertFailureBeforeVerificationAsync(Request(new FfmpegAssGraphicBinding(input.Id, graph.OutputNodeId, Range(0, 3))),
            FfmpegCompilerFailureReason.InputBindingMismatch);
        await AssertFailureBeforeVerificationAsync(new FfmpegCompileRequest(graph, ContextForInputs(fixture, inputs), fixture.Tool,
            [.. fixture.Bindings, binding], fixture.OutputPath, fixture.RootPath, TimeSpan.FromMinutes(1), fonts: [font]),
            FfmpegCompilerFailureReason.InputBindingMismatch);
    }

    [TestMethod]
    public async Task CardMusicPathIsAtomicReverifiedAndNoImplicitLoopOrStretchIsCompiled()
    {
        using Fixture fixture = Fixture.Create();
        var track = new BinaryVerificationRequest("card-music", Path.Combine(fixture.RootPath, "music';[x].mp3"), Digest('8'), 100);
        var encoder = new FfmpegEncoderSettings(8_000_000, 192_000, 1, new FfmpegLoudnessTarget(-16m, -1m));
        FfmpegCompileRequest Request(TimeRange range) => new(fixture.Graph, fixture.Context, fixture.Tool, fixture.Bindings,
            fixture.OutputPath, fixture.RootPath, TimeSpan.FromMinutes(1), encoderSettings: encoder,
            music: [new FfmpegMusicBinding(track, range)]);
        var verified = new List<BinaryVerificationRequest>();
        FfmpegCompilation compiled = await FfmpegCompiler.CompileAsync(Request(Range(2, 1)), (binding, _) =>
        {
            verified.Add(binding);
            return Task.FromResult(BinaryVerificationResult.Verified(binding, binding.ExpectedByteSize, binding.ExpectedDigest));
        });
        Assert.AreSame(track, verified[^1]);
        CollectionAssert.Contains(InputPathTokens(compiled.ProcessRequest.ArgumentList), track.FilePath);
        Assert.IsFalse(compiled.FilterScript.Contains(track.FilePath, StringComparison.Ordinal));
        StringAssert.Contains(compiled.FilterScript, "aformat=channel_layouts=mono,atrim=duration=1,asetpts=PTS-STARTPTS,adelay=96000S:all=1");
        StringAssert.Contains(compiled.FilterScript, "amix=inputs=2:duration=first:dropout_transition=0:normalize=0");
        Assert.IsFalse(compiled.FilterScript.Contains("atempo", StringComparison.Ordinal));
        CollectionAssert.DoesNotContain(compiled.ProcessRequest.ArgumentList.ToArray(), "-stream_loop");
        await AssertFailureAsync(() => FfmpegCompiler.CompileAsync(Request(Range(2, 1)), (binding, _) => Task.FromResult(
            binding == track ? BinaryVerificationResult.Failed(binding, BinaryVerificationFailureReason.DigestMismatch,
                binding.ExpectedByteSize, Digest('9')) : BinaryVerificationResult.Verified(binding, binding.ExpectedByteSize, binding.ExpectedDigest))),
            FfmpegCompilerFailureReason.InputNotVerified);
        await AssertFailureBeforeVerificationAsync(Request(Range(3, 2)), FfmpegCompilerFailureReason.InputBindingMismatch);
        await AssertFailureBeforeVerificationAsync(Request(new TimeRange(new RationalTime(101, 100), new RationalTime(1, 1))),
            FfmpegCompilerFailureReason.InputBindingMismatch);
    }

    private static RenderGraphValidationContext ContextForInputs(Fixture fixture, IReadOnlyList<RenderGraphInput> inputs) => new(
        fixture.Context.Project, fixture.Context.CompositionVersion, fixture.Context.TimelineMap, fixture.Context.TimelineMapDigest,
        fixture.Context.Profile, fixture.Context.LayoutSpec, fixture.Context.LayoutSpecDigest, fixture.Context.Template,
        fixture.Context.SourceSpans, fixture.Context.EditorialRecordIds,
        inputs.Select(static input => new RenderArtifactEvidence(input.Id, input.ContentDigest, input.ByteSize)).ToArray(), fixture.Context.Fonts);

    [TestMethod]
    public async Task ShellLikeInputPathsRemainSingleTokensAndSubtitlePathIsFilterEscaped()
    {
        using Fixture fixture = Fixture.Create(
            videoFileName: "video & $(literal).mp4",
            subtitleFileName: "sub;title's [x].ass");

        FfmpegCompilation compilation = await CompileAsync(fixture, fixture.Request());

        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            fixture.VideoPath);
        Assert.AreEqual(
            1,
            compilation.ProcessRequest.ArgumentList.Count(argument =>
                string.Equals(argument, fixture.VideoPath, StringComparison.Ordinal)));
        Assert.IsFalse(compilation.FilterScript.Contains(
            fixture.SubtitlePath,
            StringComparison.Ordinal));
        StringAssert.Contains(compilation.FilterScript, "C\\\\:/");
        StringAssert.Contains(compilation.FilterScript, "\\;title\\\\\\'s \\[x\\]");
        Assert.IsFalse(compilation.ProcessRequest.ArgumentList.Any(static argument =>
            string.Equals(argument, "&", StringComparison.Ordinal)
            || string.Equals(argument, ";", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task EquivalentCompilationIsDeterministicAcrossBindingOrder()
    {
        using Fixture fixture = Fixture.Create();
        FfmpegCompileRequest firstRequest = fixture.Request();
        FfmpegCompileRequest secondRequest = fixture.Request(
            bindings: firstRequest.Inputs.Reverse().ToArray());

        FfmpegCompilation first = await CompileAsync(fixture, firstRequest);
        FfmpegCompilation second = await CompileAsync(fixture, secondRequest);

        Assert.AreEqual(first.FilterScript, second.FilterScript);
        Assert.AreEqual(first.FilterScriptDigest, second.FilterScriptDigest);
        CollectionAssert.AreEqual(
            first.ProcessRequest.ArgumentList.ToArray(),
            second.ProcessRequest.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task UnverifiedToolOrInputAndBindingMismatchFailClosedPrivately()
    {
        using Fixture fixture = Fixture.Create();
        BinaryVerificationResult failedTool = BinaryVerificationResult.Failed(
            fixture.ToolRequest,
            BinaryVerificationFailureReason.DigestMismatch,
            fixture.ToolRequest.ExpectedByteSize,
            Digest('f'));
        await AssertFailureAsync(
            () => FfmpegCompiler.CompileAsync(
                fixture.Request(),
                (request, _) => Task.FromResult(
                    request.ComponentIdentity == "ffmpeg"
                        ? failedTool
                        : BinaryVerificationResult.Verified(
                            request,
                            request.ExpectedByteSize,
                            request.ExpectedDigest))),
            FfmpegCompilerFailureReason.ToolNotVerified);

        FfmpegInputBinding first = fixture.Bindings[0];
        BinaryVerificationResult failedInput = BinaryVerificationResult.Failed(
            first.VerificationRequest,
            BinaryVerificationFailureReason.SizeMismatch,
            first.VerificationRequest.ExpectedByteSize + 1);
        await AssertFailureAsync(
            () => FfmpegCompiler.CompileAsync(
                fixture.Request(),
                (request, _) => Task.FromResult(
                    request.ComponentIdentity == first.VerificationRequest.ComponentIdentity
                        ? failedInput
                        : BinaryVerificationResult.Verified(
                            request,
                            request.ExpectedByteSize,
                            request.ExpectedDigest))),
            FfmpegCompilerFailureReason.InputNotVerified);

        FfmpegInputBinding wrongId = new(
            Id<RenderInputIdTag>(0x78000030, 99),
            first.VerificationRequest);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(bindings: [wrongId, .. fixture.Bindings.Skip(1)])),
            FfmpegCompilerFailureReason.InputBindingMismatch);

        BinaryVerificationRequest arbitraryToolRequest = new(
            "ffmpeg",
            fixture.ToolRequest.FilePath,
            Digest('0'),
            10);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(tool: new FfmpegToolBinding(arbitraryToolRequest))),
            FfmpegCompilerFailureReason.ToolNotVerified);
    }

    [TestMethod]
    public async Task MissingDuplicateAndExtraBindingsAreRejected()
    {
        using Fixture fixture = Fixture.Create();

        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(bindings: fixture.Bindings.Skip(1).ToArray())),
            FfmpegCompilerFailureReason.InputBindingMismatch);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Request(
            bindings: [fixture.Bindings[0], fixture.Bindings[0], .. fixture.Bindings.Skip(1)]));
        FfmpegInputBinding extra = fixture.Binding(
            Id<RenderInputIdTag>(0x78000031, 99),
            "extra.bin",
            Digest('9'),
            1);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(bindings: [.. fixture.Bindings, extra])),
            FfmpegCompilerFailureReason.InputBindingMismatch);
    }

    [TestMethod]
    public async Task CompilerRevalidatesGraphAndRejectsUnsupportedCodec()
    {
        using Fixture fixture = Fixture.Create();
        RenderGraph invalidGraph = fixture.GraphWithOutputRange(Range(0, 3));
        await AssertFailureAsync(
            () => CompileAsync(fixture, fixture.Request(graph: invalidGraph)),
            FfmpegCompilerFailureReason.GraphInvalid);

        RenderProfile unsupported = new(
            fixture.Profile.Id,
            fixture.Profile.Name,
            fixture.Profile.ProfileDigest,
            fixture.Profile.Width,
            fixture.Profile.Height,
            fixture.Profile.FrameRateNumerator,
            fixture.Profile.FrameRateDenominator,
            fixture.Profile.LayoutSpecId,
            fixture.Profile.TemplateId,
            fixture.Profile.TemplateVersionDigest,
            "invented-codec",
            fixture.Profile.AudioCodec,
            fixture.Profile.SubtitleBurnIn);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(context: fixture.ContextWith(profile: unsupported))),
            FfmpegCompilerFailureReason.UnsupportedCodec);
    }

    [TestMethod]
    public async Task OutputMustBeManagedDistinctMp4AndCommandLengthIsBounded()
    {
        using Fixture fixture = Fixture.Create();
        await AssertFailureAsync(
            () => CompileAsync(fixture, fixture.Request(outputPath: fixture.VideoPath)),
            FfmpegCompilerFailureReason.OutputPathInvalid);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(outputPath: Path.Combine(Path.GetTempPath(), "outside.mp4"))),
            FfmpegCompilerFailureReason.OutputPathInvalid);
        await AssertFailureAsync(
            () => CompileAsync(
                fixture,
                fixture.Request(outputPath: Path.Combine(fixture.RootPath, "output.mkv"))),
            FfmpegCompilerFailureReason.OutputPathInvalid);
        string tooLong = Path.Combine(
            fixture.RootPath,
            new string('x', FfmpegCompiler.MaximumWindowsCommandLineCharacters) + ".mp4");
        await AssertFailureAsync(
            () => CompileAsync(fixture, fixture.Request(outputPath: tooLong)),
            FfmpegCompilerFailureReason.CommandLineTooLong);

        using Fixture aggregate = Fixture.Create(
            videoFileName: string.Concat(new string('v', 17000), ".mp4"),
            subtitleFileName: string.Concat(new string('s', 17000), ".ass"));
        await AssertFailureAsync(
            () => CompileAsync(aggregate, aggregate.Request()),
            FfmpegCompilerFailureReason.CommandLineTooLong);
    }

    [TestMethod]
    public async Task SidecarProfileMapsSubtitleStreamWithoutSubtitlesFilter()
    {
        using Fixture fixture = Fixture.Create(subtitleBurnIn: false);

        FfmpegCompilation compilation = await CompileAsync(fixture, fixture.Request());

        CollectionAssert.Contains(
            InputPathTokens(compilation.ProcessRequest.ArgumentList),
            fixture.SubtitlePath);
        Assert.IsFalse(compilation.FilterScript.Contains(
            "subtitles=",
            StringComparison.Ordinal));
        CollectionAssert.Contains(
            MapTokens(compilation.ProcessRequest.ArgumentList),
            "3:s:0");
        CollectionAssert.Contains(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "mov_text");
        CollectionAssert.DoesNotContain(
            compilation.ProcessRequest.ArgumentList.ToArray(),
            "-shortest");
        int outputDurationIndex = compilation.ProcessRequest.ArgumentList
            .Select((value, index) => (value, index))
            .Last(item => item.value == "-t")
            .index;
        Assert.AreEqual(
            "4",
            compilation.ProcessRequest.ArgumentList[outputDurationIndex + 1]);
    }

    [TestMethod]
    public async Task BurnedInCueChainCompilesEveryVerifiedSubtitleInputOnce()
    {
        using Fixture fixture = Fixture.Create();
        RenderGraphInput firstInput = fixture.Graph.Inputs.Single(static input =>
            input.Kind == RenderInputKind.Subtitle);
        RenderNode firstSubtitle = fixture.Graph.Nodes.Single(static node =>
            node.Kind == RenderNodeKind.Subtitle);
        RenderNode audio = fixture.Graph.Nodes.Single(static node =>
            node.Kind == RenderNodeKind.Audio);
        RenderNode originalOutput = fixture.Graph.Nodes.Single(node =>
            node.Id == fixture.Graph.OutputNodeId);
        var secondInput = new RenderGraphInput(
            Id<RenderInputIdTag>(0x78000020, 9),
            RenderInputKind.Subtitle,
            Digest('8'),
            81,
            firstInput.Origin);
        RenderNode secondSubtitle = RenderNode.Subtitle(
            Id<RenderNodeIdTag>(0x78000021, 9),
            secondInput.Id,
            firstSubtitle.Id,
            new Cue(
                Id<CueIdTag>(0x78000019, 9),
                CueKind.Subtitle,
                Range(3, 1),
                firstSubtitle.Cue!.RegionName),
            firstInput.Origin);
        RenderNode output = RenderNode.Output(
            originalOutput.Id,
            [secondSubtitle.Id, audio.Id],
            originalOutput.OutputRange);
        var graph = new RenderGraph(
            fixture.Graph.Id,
            fixture.Graph.CompositionVersionId,
            fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId,
            fixture.Graph.TimelineMapDigest,
            fixture.Graph.ProfileId,
            fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId,
            fixture.Graph.LayoutSpecDigest,
            fixture.Graph.Template,
            [.. fixture.Graph.Inputs, secondInput],
            fixture.Graph.Fonts,
            [
                .. fixture.Graph.Nodes.Where(node =>
                    node.Id != fixture.Graph.OutputNodeId),
                secondSubtitle,
                output,
            ],
            output.Id);
        var context = new RenderGraphValidationContext(
            fixture.Context.Project,
            fixture.Context.CompositionVersion,
            fixture.Context.TimelineMap,
            fixture.Context.TimelineMapDigest,
            fixture.Context.Profile,
            fixture.Context.LayoutSpec,
            fixture.Context.LayoutSpecDigest,
            fixture.Context.Template,
            fixture.Context.SourceSpans,
            fixture.Context.EditorialRecordIds,
            [
                .. fixture.Context.Artifacts,
                new RenderArtifactEvidence(
                    secondInput.Id,
                    secondInput.ContentDigest,
                    secondInput.ByteSize),
            ],
            fixture.Context.Fonts);
        FfmpegInputBinding secondBinding = fixture.Binding(
            secondInput.Id,
            "subtitle-2.ass",
            secondInput.ContentDigest,
            secondInput.ByteSize);

        FfmpegCompilation compilation = await CompileAsync(
            fixture,
            fixture.Request(
                graph: graph,
                context: context,
                bindings: [.. fixture.Bindings, secondBinding]));

        Assert.AreEqual(
            2,
            compilation.FilterScript.Split(
                "subtitles=filename=",
                StringSplitOptions.None).Length - 1);
        Assert.AreEqual(
            1,
            compilation.FilterScript.Split(
                Path.GetFileName(fixture.SubtitlePath),
                StringSplitOptions.None).Length - 1);
        Assert.AreEqual(
            1,
            compilation.FilterScript.Split(
                "subtitle-2.ass",
                StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public async Task EverySupportedTransitionUsesOnlyClosedFilterNames()
    {
        using Fixture fixture = Fixture.Create(subtitleBurnIn: false);
        (Transition Transition, string ExpectedFilter)[] cases =
        [
            (Transition.Cut(), "concat=n=2:v=1:a=0"),
            (Transition.Crossfade(new RationalTime(1, 1)), "xfade=transition=fade"),
            (Transition.Fade(new RationalTime(1, 1)), "fade=t=out"),
            (Transition.Wipe(new RationalTime(1, 1), TransitionDirection.Left), "xfade=transition=wipeleft"),
            (Transition.Slide(new RationalTime(1, 1), TransitionDirection.Right), "xfade=transition=slideright"),
        ];

        foreach ((Transition transition, string expectedFilter) in cases)
        {
            (RenderGraph graph, RenderGraphValidationContext context) =
                fixture.TransitionGraph(transition);
            FfmpegInputBinding[] bindings = fixture.Bindings
                .Where(binding => graph.Inputs.Any(input => input.Id == binding.InputId))
                .ToArray();
            FfmpegCompilation compilation = await CompileAsync(
                fixture,
                fixture.Request(graph: graph, context: context, bindings: bindings));
            StringAssert.Contains(compilation.FilterScript, expectedFilter);
        }

        foreach (Transition unsupported in new[]
        {
            Transition.TitleCard(new RationalTime(1, 1)),
            Transition.Zoom(new RationalTime(1, 1)),
        })
        {
            (RenderGraph graph, RenderGraphValidationContext context) =
                fixture.TransitionGraph(unsupported);
            FfmpegInputBinding[] bindings = fixture.Bindings
                .Where(binding => graph.Inputs.Any(input => input.Id == binding.InputId))
                .ToArray();
            await AssertFailureAsync(
                () => CompileAsync(
                    fixture,
                    fixture.Request(graph: graph, context: context, bindings: bindings)),
                FfmpegCompilerFailureReason.UnsupportedTransition);
        }
    }

    [TestMethod]
    public async Task PreCancellationAndPublicSurfaceExposeNoShellOrRawFilterInput()
    {
        using Fixture fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            CompileAsync(fixture, fixture.Request(), cancellation.Token));

        Assert.IsTrue(typeof(FfmpegCompiler).IsAbstract);
        Assert.IsTrue(typeof(FfmpegCompiler).IsSealed);
        MethodInfo method = typeof(FfmpegCompiler).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single();
        CollectionAssert.AreEqual(
            new[] { typeof(FfmpegCompileRequest), typeof(CancellationToken) },
            method.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
        Assert.AreEqual(typeof(Task<FfmpegCompilation>), method.ReturnType);
        Assert.IsFalse(typeof(FfmpegCompileRequest).GetProperties().Any(static property =>
            property.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Shell", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ToolAndEveryGraphInputAreReverifiedForEveryCompilation()
    {
        using Fixture fixture = Fixture.Create();
        var observed = new List<BinaryVerificationRequest>();

        Task<BinaryVerificationResult> VerifyAsync(
            BinaryVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed.Add(request);
            return Task.FromResult(BinaryVerificationResult.Verified(
                request,
                request.ExpectedByteSize,
                request.ExpectedDigest));
        }

        await FfmpegCompiler.CompileAsync(fixture.Request(), VerifyAsync);
        await FfmpegCompiler.CompileAsync(fixture.Request(), VerifyAsync);

        BinaryVerificationRequest[] expectedPass =
        [
            fixture.ToolRequest,
            .. fixture.Graph.Inputs.Select(input => fixture.Bindings
                .Single(binding => binding.InputId == input.Id)
                .VerificationRequest),
        ];
        CollectionAssert.AreEqual(
            expectedPass.Concat(expectedPass).ToArray(),
            observed.ToArray());
    }

    [TestMethod]
    public async Task ControlCharactersAndInvalidUnicodeInProcessPathsFailPrivately()
    {
        using Fixture fixture = Fixture.Create();
        string[] markers =
        [
            "sensitive\r\n-filter_complex.mp4",
            "sensitive\0.mp4",
            "sensitive\ud800.mp4",
        ];

        foreach (string marker in markers)
        {
            FfmpegCompilerException exception = await AssertFailureAsync(
                () => CompileAsync(
                    fixture,
                    fixture.Request(outputPath: Path.Combine(fixture.RootPath, marker))),
                FfmpegCompilerFailureReason.OutputPathInvalid);
            Assert.IsFalse(exception.Message.Contains(marker, StringComparison.Ordinal));
        }

        using Fixture inputFixture = Fixture.Create(
            videoFileName: "sensitive\n-v error.mp4");
        FfmpegCompilerException inputException = await AssertFailureAsync(
            () => CompileAsync(inputFixture, inputFixture.Request()),
            FfmpegCompilerFailureReason.InputBindingMismatch);
        Assert.IsFalse(inputException.Message.Contains(
            "sensitive",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AudioThatEndsBeforeVideoIsRejectedRatherThanSilentlyTruncated()
    {
        using Fixture fixture = Fixture.Create();
        RenderNode original = fixture.Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
        RenderNode shortAudio = RenderNode.Audio(original.Id, original.ArtifactInputId!, Range(0, 2), original.Origin!, null);
        var graph = new RenderGraph(fixture.Graph.Id, fixture.Graph.CompositionVersionId, fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId, fixture.Graph.TimelineMapDigest, fixture.Graph.ProfileId, fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId, fixture.Graph.LayoutSpecDigest, fixture.Graph.Template, fixture.Graph.Inputs,
            fixture.Graph.Fonts, fixture.Graph.Nodes.Select(node => node.Id == original.Id ? shortAudio : node).ToArray(), fixture.Graph.OutputNodeId);

        await AssertFailureAsync(() => CompileAsync(fixture, fixture.Request(graph: graph)),
            FfmpegCompilerFailureReason.UnsupportedAudioShape);
    }

    [TestMethod]
    public async Task ContiguousAudioTerminalsAreSortedAndConcatenatedNotMixed()
    {
        using Fixture fixture = Fixture.Create();
        RenderNode original = fixture.Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
        RenderNode first = RenderNode.Audio(original.Id, original.ArtifactInputId!, Range(0, 2), original.Origin!, null);
        RenderNode second = RenderNode.Audio(EntityId<RenderNodeIdTag>.New(), original.ArtifactInputId!, Range(2, 2), original.Origin!, null);
        RenderNode originalOutput = fixture.Graph.Nodes.Single(node => node.Id == fixture.Graph.OutputNodeId);
        RenderNode output = RenderNode.Output(originalOutput.Id,
            [.. originalOutput.InputNodeIds.Where(id => id != original.Id), second.Id, first.Id], originalOutput.OutputRange);
        var graph = new RenderGraph(fixture.Graph.Id, fixture.Graph.CompositionVersionId, fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId, fixture.Graph.TimelineMapDigest, fixture.Graph.ProfileId, fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId, fixture.Graph.LayoutSpecDigest, fixture.Graph.Template, fixture.Graph.Inputs,
            fixture.Graph.Fonts, [.. fixture.Graph.Nodes.Where(node => node.Id != original.Id && node.Id != originalOutput.Id), first, second, output], output.Id);
        FfmpegCompilation compiled = await CompileAsync(fixture, fixture.Request(graph: graph));
        StringAssert.Contains(compiled.FilterScript, "concat=n=2:v=0:a=1[audio_cuts]");
        CollectionAssert.Contains(MapTokens(compiled.ProcessRequest.ArgumentList), "[audio_cuts]");
        Assert.IsFalse(compiled.FilterScript.Contains("amix", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NonFrameAlignedAudioCutsAreRejectedBeforeAnyVerification()
    {
        using Fixture fixture = Fixture.Create();
        RenderNode original = fixture.Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
        RationalTime boundary = new(101, 100);
        RenderNode first = RenderNode.Audio(original.Id, original.ArtifactInputId!, new TimeRange(default, boundary), original.Origin!, null);
        RenderNode second = RenderNode.Audio(EntityId<RenderNodeIdTag>.New(), original.ArtifactInputId!,
            new TimeRange(boundary, new RationalTime(4, 1) - boundary), original.Origin!, null);
        RenderNode originalOutput = fixture.Graph.Nodes.Single(node => node.Id == fixture.Graph.OutputNodeId);
        RenderNode output = RenderNode.Output(originalOutput.Id,
            [.. originalOutput.InputNodeIds.Where(id => id != original.Id), first.Id, second.Id], originalOutput.OutputRange);
        var graph = new RenderGraph(fixture.Graph.Id, fixture.Graph.CompositionVersionId, fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId, fixture.Graph.TimelineMapDigest, fixture.Graph.ProfileId, fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId, fixture.Graph.LayoutSpecDigest, fixture.Graph.Template, fixture.Graph.Inputs,
            fixture.Graph.Fonts, [.. fixture.Graph.Nodes.Where(node => node.Id != original.Id && node.Id != originalOutput.Id), first, second, output], output.Id);
        int verified = 0;
        await AssertFailureAsync(() => FfmpegCompiler.CompileAsync(fixture.Request(graph: graph), (request, _) =>
        {
            verified++;
            return Task.FromResult(BinaryVerificationResult.Verified(request, request.ExpectedByteSize, request.ExpectedDigest));
        }), FfmpegCompilerFailureReason.UnsupportedTimingGrid);
        Assert.AreEqual(0, verified);
    }

    [TestMethod]
    public void ExactGridRequiresBothFramesAndSamplesWithoutFloatingPointTolerance()
    {
        using Fixture fixture = Fixture.Create();
        Assert.IsTrue(FfmpegCompiler.IsOnOutputGrid(new RationalTime(1, 30), fixture.Profile));
        Assert.IsFalse(FfmpegCompiler.IsOnOutputGrid(new RationalTime(101, 100), fixture.Profile));
        var ntsc = new RenderProfile(fixture.Profile.Id, fixture.Profile.Name, fixture.Profile.ProfileDigest,
            fixture.Profile.Width, fixture.Profile.Height, 30000, 1001, fixture.Profile.LayoutSpecId,
            fixture.Profile.TemplateId, fixture.Profile.TemplateVersionDigest, "h264", "aac", true);
        Assert.IsFalse(FfmpegCompiler.IsOnOutputGrid(new RationalTime(1001, 30000), ntsc)); // one frame, 1601.6 samples
        Assert.IsTrue(FfmpegCompiler.IsOnOutputGrid(new RationalTime(1001, 6000), ntsc)); // five frames, 8008 samples
        Assert.IsFalse(FfmpegCompiler.IsOnSampleGrid(new RationalTime(1, 48000), 44100));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredFullSourceBlurCompilesFractionalDestinationsWithoutRoundingLayout(bool tallSource)
    {
        using Fixture fixture = Fixture.Create();
        SourceDisplayGeometry geometry = tallSource ? new(720, 2000) : new(1920, 1080);
        NormalizedRect destination = tallSource
            ? Rect(9, 50, 0, 1, 16, 25, 1, 1)
            : Rect(0, 1, 175, 512, 1, 1, 81, 256);
        ManualLayoutPlan layout = fixture.ManualPlan(destination: destination, geometry: geometry);
        FfmpegCompileRequest request = fixture.ManualRequest(layout);
        Assert.IsTrue(RenderGraphValidator.Validate(request.Graph, request.ValidationContext).IsValid);
        Assert.ThrowsExactly<InvalidOperationException>(() => LayoutResolver.Resolve(layout.DerivedLayoutSpec, 1080, 1920));
        var observed = new List<BinaryVerificationRequest>();
        Task<BinaryVerificationResult> VerifyAsync(BinaryVerificationRequest binding, CancellationToken token)
        {
            observed.Add(binding);
            return fixture.VerifyAsync(binding, token); // Synthetic verifier only; no encoder is executed.
        }

        FfmpegCompilation first = await FfmpegCompiler.CompileAsync(request, VerifyAsync);
        FfmpegCompilation second = await FfmpegCompiler.CompileAsync(request, VerifyAsync);

        string crop = tallSource ? "crop=720:1280:0:360" : "crop=603:1072:658:4";
        StringAssert.Contains(first.FilterScript,
            $",split=2[v0b][v0f];[v0b]{crop}:exact=1,scale=1080:1920:flags=bilinear,setsar=1,gblur=sigma=20[v0bg];");
        StringAssert.Contains(first.FilterScript,
            $"[v0f]crop={geometry.Width}:{geometry.Height}:0:0:exact=1,scale=1080:1920:flags=bilinear:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1[v0fg];");
        StringAssert.Contains(first.FilterScript,
            "[v0bg][v0fg]overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2:shortest=1,fps=30/1,format=yuv420p[v0]");
        CollectionAssert.AreEqual(new[] { "[v0]", "[a1]" }, MapTokens(first.ProcessRequest.ArgumentList));
        Assert.AreEqual(first.FilterScriptDigest, second.FilterScriptDigest);
        CollectionAssert.AreEqual(first.ProcessRequest.ArgumentList.ToArray(), second.ProcessRequest.ArgumentList.ToArray());
        BinaryVerificationRequest[] expectedPass = [fixture.ToolRequest, .. request.Inputs.Select(static input => input.VerificationRequest)];
        CollectionAssert.AreEqual(expectedPass.Concat(expectedPass).ToArray(), observed.ToArray());
        Assert.AreEqual(destination, layout.SourcePlacements[1].Destination.Region.Rect);
        Assert.ThrowsExactly<InvalidOperationException>(() => LayoutResolver.Resolve(layout.DerivedLayoutSpec, 1080, 1920));
        Assert.IsFalse(File.Exists(first.OutputPath));
    }

    [TestMethod]
    [DataRow(ManualLayoutMode.SingleSubjectCrop)]
    [DataRow(ManualLayoutMode.ImportantRegionZoom)]
    [DataRow(ManualLayoutMode.FixedTemplatePlacement)]
    [DataRow(ManualLayoutMode.BlurredSourceBackground)]
    public async Task PixelAlignedManualCropDestinationsStillUseExactMapping(ManualLayoutMode mode)
    {
        using Fixture fixture = Fixture.Create();
        ManualLayoutPlan layout = fixture.ManualPlan(mode,
            Rect(1, 6, 1, 8, 2, 3, 3, 4), Rect(23, 64, 0, 1, 9, 32, 1, 1));

        FfmpegCompilation compiled = await CompileAsync(fixture, fixture.ManualRequest(layout));

        StringAssert.Contains(compiled.FilterScript, "crop=540:1080:690:0:exact=1,scale=720:1440:flags=bilinear,setsar=1");
        StringAssert.Contains(compiled.FilterScript, mode == ManualLayoutMode.BlurredSourceBackground
            ? "overlay=x=180:y=240:shortest=1"
            : "pad=1080:1920:180:240:color=black");
        Assert.IsFalse(compiled.FilterScript.Contains("force_original_aspect_ratio", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task NonCenteredCroppedOrSmallerFractionalBlurPlacementsRemainRejected(int variant)
    {
        using Fixture fixture = Fixture.Create(videoFileName: "sensitive-source.mp4");
        NormalizedRect destination = variant switch
        {
            0 => Rect(0, 1, 1, 4, 1, 1, 81, 256), // Full source, but not centered.
            1 => Rect(0, 1, 175, 512, 1, 1, 81, 256), // Same aspect, but a cropped source.
            _ => Rect(1, 4, 431, 1024, 1, 2, 81, 512), // Centered, but not the full contain fit.
        };
        NormalizedRect? crop = variant == 1 ? Rect(1, 6, 1, 6, 2, 3, 2, 3) : null;
        ManualLayoutPlan layout = fixture.ManualPlan(destination: destination, foregroundCrop: crop);

        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(layout), FfmpegCompilerFailureReason.CompilationFailed);
    }

    [TestMethod]
    public async Task CenteredFitDoesNotBypassOtherFractionalLayoutRegions()
    {
        using Fixture fixture = Fixture.Create();
        ManualLayoutPlan layout = fixture.ManualPlan(retainedRegion: new LayoutRegion(
            "fractional-graphic", Rect(0, 1, 0, 1, 1, 7, 1, 1), LayoutRegionRole.Graphic));

        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(layout), FfmpegCompilerFailureReason.CompilationFailed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SameLayoutIdCannotSubstituteDifferentRegionsOrSafeZones(bool changeSafeZone)
    {
        using Fixture fixture = Fixture.Create();
        ManualLayoutPlan layout = fixture.ManualPlan();
        LayoutSpec original = layout.DerivedLayoutSpec;
        LayoutRegion foreground = layout.SourcePlacements[1].Destination.Region;
        var different = new LayoutSpec(original.Id, original.OutputAspectWidth, original.OutputAspectHeight,
            changeSafeZone ? original.Regions : original.Regions.Select(region => region.Equals(foreground)
                ? new LayoutRegion(region.Name, Rect(0, 1, 0, 1, 1, 1, 1, 1), region.Role) : region).ToArray(),
            changeSafeZone ? [new SafeZone("output-frame-bounds", Rect(0, 1, 0, 1, 1, 2, 1, 1))] : original.SafeZones);

        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(layout, layoutSpec: different),
            FfmpegCompilerFailureReason.UnsupportedGeometry);
    }

    [TestMethod]
    public async Task FractionalLayoutWithoutBlurBindingOrInAnotherManualModeRemainsRejected()
    {
        using Fixture fixture = Fixture.Create();
        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(fixture.ManualPlan(), bindGeometry: false),
            FfmpegCompilerFailureReason.CompilationFailed);
        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(fixture.ManualPlan(ManualLayoutMode.SingleSubjectCrop)),
            FfmpegCompilerFailureReason.CompilationFailed);
        await AssertFailureBeforeVerificationAsync(fixture.ManualRequest(fixture.ManualPlan(), geometry: new SourceDisplayGeometry(1280, 720)),
            FfmpegCompilerFailureReason.UnsupportedGeometry);
    }

    [TestMethod]
    public async Task CenteredFitRequiresMatchingGeometryForEveryVideoCut()
    {
        using Fixture fixture = Fixture.Create();
        ManualLayoutPlan layout = fixture.ManualPlan();
        FfmpegCompileRequest original = fixture.ManualRequest(layout);
        RenderGraphInput input = original.Graph.Inputs.Single(static item => item.Kind == RenderInputKind.Video);
        CompositionElement[] elements =
        [
            new(Id<CompositionElementIdTag>(0x78000013, 1), Range(0, 2), input.Origin),
            new(Id<CompositionElementIdTag>(0x78000013, 2), Range(2, 2), input.Origin),
        ];
        var composition = new CompositionVersion(original.Graph.CompositionVersionId, fixture.Context.Project.Id,
            null, "compiler-test", DateTimeOffset.UnixEpoch, "Two manual source cuts.",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
        var timeline = new TimelineMap(original.Graph.TimelineMapId, composition.Id,
            [new TimelineMapEntry(Range(0, 2), input.Origin, null), new TimelineMapEntry(Range(2, 2), input.Origin, null)]);
        ContentDigest timelineDigest = RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
        string region = layout.SourcePlacements[0].Destination.Region.Name;
        RenderNode first = RenderNode.Video(original.VideoGeometry[0].NodeId, input.Id, Range(0, 2), input.Origin, null, region);
        RenderNode second = RenderNode.Video(Id<RenderNodeIdTag>(0x78000021, 7), input.Id, Range(2, 2), input.Origin, null, region);
        RenderNode cut = RenderNode.TransitionEffect(Id<RenderNodeIdTag>(0x78000021, 8), [first.Id, second.Id],
            new TimeRange(new RationalTime(2, 1), default), input.Origin, Transition.Cut());
        RenderNode audio = original.Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
        RenderNode output = RenderNode.Output(original.Graph.OutputNodeId, [cut.Id, audio.Id], Range(0, 4));
        var graph = new RenderGraph(original.Graph.Id, composition.Id, composition.ContentDigest, timeline.Id, timelineDigest,
            original.Graph.ProfileId, original.Graph.ProfileDigest, original.Graph.LayoutSpecId, original.Graph.LayoutSpecDigest,
            original.Graph.Template, original.Graph.Inputs, [], [first, second, cut, audio, output], output.Id);
        RenderGraphValidationContext prior = original.ValidationContext;
        var context = new RenderGraphValidationContext(prior.Project, composition, timeline, timelineDigest, prior.Profile,
            prior.LayoutSpec, prior.LayoutSpecDigest, prior.Template, prior.SourceSpans, prior.EditorialRecordIds, prior.Artifacts, []);
        FfmpegCompileRequest WithGeometry(IReadOnlyList<FfmpegVideoGeometryBinding> geometry) => new(graph, context,
            original.Tool, original.Inputs, original.OutputPath, original.WorkingDirectory, original.Timeout, geometry);
        Assert.IsTrue(RenderGraphValidator.Validate(graph, context).IsValid);

        await AssertFailureBeforeVerificationAsync(WithGeometry(original.VideoGeometry), FfmpegCompilerFailureReason.UnsupportedGeometry);
        FfmpegCompilation compiled = await CompileAsync(fixture, WithGeometry(
            [.. original.VideoGeometry, new FfmpegVideoGeometryBinding(second.Id, original.VideoGeometry[0].Geometry, layout)]));

        StringAssert.Contains(compiled.FilterScript, "concat=n=2:v=1:a=0");
        Assert.AreEqual(2, compiled.FilterScript.Split("gblur=sigma=20", StringSplitOptions.None).Length - 1);
    }

    private static async Task AssertFailureBeforeVerificationAsync(FfmpegCompileRequest request, FfmpegCompilerFailureReason reason)
    {
        int verified = 0;
        FfmpegCompilerException exception = await AssertFailureAsync(() => FfmpegCompiler.CompileAsync(request, (binding, _) =>
        {
            verified++;
            return Task.FromResult(BinaryVerificationResult.Verified(binding, binding.ExpectedByteSize, binding.ExpectedDigest));
        }), reason);
        Assert.AreEqual(0, verified);
        Assert.IsFalse(exception.Message.Contains(request.WorkingDirectory, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("sensitive-source", StringComparison.Ordinal));
    }

    private static async Task<FfmpegCompilerException> AssertFailureAsync(
        Func<Task<FfmpegCompilation>> action,
        FfmpegCompilerFailureReason reason)
    {
        FfmpegCompilerException exception =
            await Assert.ThrowsExactlyAsync<FfmpegCompilerException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static Task<FfmpegCompilation> CompileAsync(
        Fixture fixture,
        FfmpegCompileRequest request,
        CancellationToken cancellationToken = default) =>
        FfmpegCompiler.CompileAsync(
            request,
            fixture.VerifyAsync,
            cancellationToken);

    private static string[] InputPathTokens(IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == "-i")
            {
                result.Add(arguments[index + 1]);
            }
        }

        return result.ToArray();
    }

    private static string[] MapTokens(IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == "-map")
            {
                result.Add(arguments[index + 1]);
            }
        }

        return result.ToArray();
    }

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private static ContentDigest Digest(char fill) => new(new string(fill, 64));

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static NormalizedRect Rect(
        long xN, long xD, long yN, long yD,
        long wN, long wD, long hN, long hD) =>
        new(
            new RationalNumber(xN, xD),
            new RationalNumber(yN, yD),
            new RationalNumber(wN, wD),
            new RationalNumber(hN, hD));

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string rootPath,
            RenderGraph graph,
            RenderGraphValidationContext context,
            RenderProfile profile,
            BinaryVerificationRequest toolRequest,
            FfmpegToolBinding tool,
            IReadOnlyList<FfmpegInputBinding> bindings,
            string videoPath,
            string audioPath,
            string subtitlePath,
            string graphicPath,
            string outputPath)
        {
            RootPath = rootPath;
            Graph = graph;
            Context = context;
            Profile = profile;
            ToolRequest = toolRequest;
            Tool = tool;
            Bindings = bindings;
            VideoPath = videoPath;
            AudioPath = audioPath;
            SubtitlePath = subtitlePath;
            GraphicPath = graphicPath;
            OutputPath = outputPath;
        }

        internal string RootPath { get; }
        internal RenderGraph Graph { get; }
        internal RenderGraphValidationContext Context { get; }
        internal RenderProfile Profile { get; }
        internal BinaryVerificationRequest ToolRequest { get; }
        internal FfmpegToolBinding Tool { get; }
        internal IReadOnlyList<FfmpegInputBinding> Bindings { get; }
        internal string VideoPath { get; }
        internal string AudioPath { get; }
        internal string SubtitlePath { get; }
        internal string GraphicPath { get; }
        internal string OutputPath { get; }

        internal static Fixture Create(
            bool subtitleBurnIn = true,
            string videoFileName = "video.mp4",
            string subtitleFileName = "subtitle.ass")
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.FfmpegCompilerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string toolPath = Path.Combine(root, "ffmpeg.exe");
            string videoPath = Path.Combine(root, videoFileName);
            string audioPath = Path.Combine(root, "audio.wav");
            string subtitlePath = Path.Combine(root, subtitleFileName);
            string graphicPath = Path.Combine(root, "graphic.png");
            string outputPath = Path.Combine(root, "output.partial.mp4");

            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(0x78000010, 1);
            RightsRecord rights = new(
                RightsStatus.Approved,
                "rights:evidence",
                "Approved",
                externalPublicationAllowed: true);
            Asset videoAsset = new(
                Id<AssetIdTag>(0x78000011, 1),
                "video.mp4",
                "video/mp4",
                Digest('a'),
                100,
                rights);
            Asset graphicAsset = new(
                Id<AssetIdTag>(0x78000011, 2),
                "graphic.png",
                "image/png",
                Digest('b'),
                50,
                rights);
            Project project = new(
                projectId,
                "FFmpeg compiler",
                "Compile typed graph.",
                "Reviewers",
                PublicationScope.InternalOnly,
                null,
                [videoAsset, graphicAsset],
                []);
            OriginRef videoOrigin = OriginRef.FromAsset(videoAsset.Id);
            OriginRef graphicOrigin = OriginRef.FromAsset(graphicAsset.Id);
            EntityId<EditorialRecordIdTag> editorialId =
                Id<EditorialRecordIdTag>(0x78000012, 1);
            OriginRef editorialOrigin = OriginRef.FromEditorialRecord(editorialId);
            CompositionElement element = new(
                Id<CompositionElementIdTag>(0x78000013, 1),
                Range(0, 4),
                videoOrigin);
            CompositionVersion composition = new(
                Id<CompositionVersionIdTag>(0x78000014, 1),
                projectId,
                null,
                "compiler-test",
                DateTimeOffset.UnixEpoch,
                "Create compiler graph.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            TimelineMap timeline = new(
                Id<TimelineMapIdTag>(0x78000015, 1),
                composition.Id,
                [new TimelineMapEntry(Range(0, 4), videoOrigin, null)]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            NormalizedRect full = Rect(0, 1, 0, 1, 1, 1, 1, 1);
            NormalizedRect top = Rect(0, 1, 0, 1, 1, 1, 1, 2);
            NormalizedRect bottom = Rect(0, 1, 1, 2, 1, 1, 1, 2);
            LayoutSpec layout = new(
                Id<LayoutSpecIdTag>(0x78000016, 1),
                16,
                9,
                [
                    new LayoutRegion("video", full, LayoutRegionRole.Video),
                    new LayoutRegion("graphic", top, LayoutRegionRole.Graphic),
                    new LayoutRegion("subtitle", bottom, LayoutRegionRole.Subtitle),
                ],
                [new SafeZone("frame", full)]);
            ContentDigest layoutDigest = Digest('c');
            TemplateReference template = new(
                Id<TemplateIdTag>(0x78000017, 1),
                "compiler-template",
                Digest('d'));
            RenderProfile profile = new(
                Id<RenderProfileIdTag>(0x78000018, 1),
                "compiler-profile",
                Digest('e'),
                1280,
                720,
                30,
                1,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn);
            RenderGraphInput videoInput = Input(1, RenderInputKind.Video, '1', 1000, videoOrigin);
            RenderGraphInput audioInput = Input(2, RenderInputKind.Audio, '2', 200, videoOrigin);
            RenderGraphInput subtitleInput = Input(3, RenderInputKind.Subtitle, '3', 80, editorialOrigin);
            RenderGraphInput graphicInput = Input(4, RenderInputKind.Graphic, '4', 50, graphicOrigin);
            RenderNode video = RenderNode.Video(Node(1), videoInput.Id, Range(0, 4), videoOrigin, null, "video");
            RenderNode audio = RenderNode.Audio(Node(2), audioInput.Id, Range(0, 4), videoOrigin, null);
            RenderNode graphic = RenderNode.Graphic(
                Node(3),
                graphicInput.Id,
                video.Id,
                new Cue(Id<CueIdTag>(0x78000019, 1), CueKind.Graphic, Range(2, 1), "graphic"),
                graphicOrigin);
            RenderNode subtitle = RenderNode.Subtitle(
                Node(4),
                subtitleInput.Id,
                graphic.Id,
                new Cue(Id<CueIdTag>(0x78000019, 2), CueKind.Subtitle, Range(1, 2), "subtitle"),
                editorialOrigin);
            RenderNode output = RenderNode.Output(
                Node(5),
                subtitleBurnIn
                    ? [subtitle.Id, audio.Id]
                    : [graphic.Id, audio.Id, subtitle.Id],
                Range(0, 4));
            RenderGraph graph = CreateGraph(
                composition,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                [videoInput, audioInput, subtitleInput, graphicInput],
                [video, audio, graphic, subtitle, output],
                output.Id);
            RenderFontEvidence font = new("Test Sans", Digest('f'));
            graph = CopyGraph(graph, fonts: [font]);
            RenderArtifactEvidence[] artifacts = graph.Inputs
                .Select(static input => new RenderArtifactEvidence(
                    input.Id,
                    input.ContentDigest,
                    input.ByteSize))
                .ToArray();
            RenderGraphValidationContext context = new(
                project,
                composition,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                [],
                [editorialId],
                artifacts,
                [font]);
            BinaryVerificationRequest toolRequest = new(
                "ffmpeg",
                toolPath,
                new ContentDigest(FfmpegCompiler.FixedFfmpegSha256),
                FfmpegCompiler.FixedFfmpegByteSize);
            FfmpegToolBinding tool = new(toolRequest);
            var provisional = new Fixture(
                root,
                graph,
                context,
                profile,
                toolRequest,
                tool,
                [],
                videoPath,
                audioPath,
                subtitlePath,
                graphicPath,
                outputPath);
            FfmpegInputBinding[] bindings =
            [
                provisional.Binding(videoInput.Id, videoFileName, videoInput.ContentDigest, videoInput.ByteSize),
                provisional.Binding(audioInput.Id, "audio.wav", audioInput.ContentDigest, audioInput.ByteSize),
                provisional.Binding(subtitleInput.Id, subtitleFileName, subtitleInput.ContentDigest, subtitleInput.ByteSize),
                provisional.Binding(graphicInput.Id, "graphic.png", graphicInput.ContentDigest, graphicInput.ByteSize),
            ];
            return new Fixture(
                root,
                graph,
                context,
                profile,
                toolRequest,
                tool,
                bindings,
                videoPath,
                audioPath,
                subtitlePath,
                graphicPath,
                outputPath);
        }

        internal FfmpegInputBinding Binding(
            EntityId<RenderInputIdTag> id,
            string fileName,
            ContentDigest digest,
            long size)
        {
            var request = new BinaryVerificationRequest(
                $"input-{id.Value:N}",
                Path.Combine(RootPath, fileName),
                digest,
                size);
            return new FfmpegInputBinding(id, request);
        }

        internal Task<BinaryVerificationResult> VerifyAsync(
            BinaryVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BinaryVerificationResult.Verified(
                request,
                request.ExpectedByteSize,
                request.ExpectedDigest));
        }

        internal FfmpegCompileRequest Request(
            RenderGraph? graph = null,
            RenderGraphValidationContext? context = null,
            FfmpegToolBinding? tool = null,
            IReadOnlyList<FfmpegInputBinding>? bindings = null,
            string? outputPath = null,
            FfmpegEncoderSettings? encoderSettings = null,
            FfmpegGeneratedScriptTarget? generatedScript = null) =>
            new(
                graph ?? Graph,
                context ?? Context,
                tool ?? Tool,
                bindings ?? Bindings,
                outputPath ?? OutputPath,
                RootPath,
                TimeSpan.FromMinutes(5), encoderSettings: encoderSettings, generatedScript: generatedScript);

        internal ManualLayoutPlan ManualPlan(
            ManualLayoutMode mode = ManualLayoutMode.BlurredSourceBackground,
            NormalizedRect? destination = null,
            NormalizedRect? foregroundCrop = null,
            SourceDisplayGeometry? geometry = null,
            LayoutRegion? retainedRegion = null)
        {
            SourceDisplayGeometry display = geometry ?? new SourceDisplayGeometry(1920, 1080);
            NormalizedRect full = Rect(0, 1, 0, 1, 1, 1, 1, 1);
            var envelope = new LayoutSpec(Id<LayoutSpecIdTag>(0x78000016, 2), 9, 16,
                retainedRegion is null ? [] : [retainedRegion], [new SafeZone("output-frame-bounds", full)]);
            EntityId<LayoutSpecIdTag> derivedId = Id<LayoutSpecIdTag>(0x78000016, 3);
            OriginRef origin = Graph.Inputs[0].Origin;
            ManualLayoutSource Source(NormalizedRect crop) => new(origin.AssetId!, origin, display,
                new ManualSourceCrop(crop), new ManualSubjectRegion(crop));
            ManualLayoutSource foreground = Source(foregroundCrop ?? full);
            var front = new ManualDestinationGeometry(destination ?? Rect(0, 1, 175, 512, 1, 1, 81, 256));
            int unit = Math.Min(display.Width / 9, display.Height / 16);
            ManualLayoutSource background = Source(Rect((display.Width - 9 * unit) / 2, display.Width,
                (display.Height - 16 * unit) / 2, display.Height, 9 * unit, display.Width, 16 * unit, display.Height));
            ManualLayoutRequest request = mode switch
            {
                ManualLayoutMode.BlurredSourceBackground => ManualLayoutRequest.BlurredSourceBackground(derivedId, envelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient, background, new ManualDestinationGeometry(full), foreground, front),
                ManualLayoutMode.SingleSubjectCrop => ManualLayoutRequest.SingleSubjectCrop(derivedId, envelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient, foreground, front),
                ManualLayoutMode.ImportantRegionZoom => ManualLayoutRequest.ImportantRegionZoom(derivedId, envelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient, foreground, front),
                ManualLayoutMode.FixedTemplatePlacement => ManualLayoutRequest.FixedTemplatePlacement(derivedId, envelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient, foreground, front, Context.Template),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            return ManualLayoutResolver.Resolve(request).Plan!;
        }

        internal FfmpegCompileRequest ManualRequest(
            ManualLayoutPlan plan,
            SourceDisplayGeometry? geometry = null,
            LayoutSpec? layoutSpec = null,
            bool bindGeometry = true)
        {
            LayoutSpec layout = layoutSpec ?? plan.DerivedLayoutSpec;
            var profile = new RenderProfile(Profile.Id, Profile.Name, Profile.ProfileDigest, 1080, 1920, 30, 1,
                layout.Id, Profile.TemplateId, Profile.TemplateVersionDigest, "h264", "aac", false);
            RenderGraphInput[] inputs = Graph.Inputs.Where(static input => input.Kind is RenderInputKind.Video or RenderInputKind.Audio).ToArray();
            RenderNode originalVideo = Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Video);
            RenderNode audio = Graph.Nodes.Single(static node => node.Kind == RenderNodeKind.Audio);
            RenderNode video = RenderNode.Video(originalVideo.Id, originalVideo.ArtifactInputId!, originalVideo.OutputRange,
                originalVideo.Origin!, originalVideo.SourceRange, plan.SourcePlacements[0].Destination.Region.Name);
            RenderNode output = RenderNode.Output(Graph.OutputNodeId, [video.Id, audio.Id], Range(0, 4));
            RenderGraph graph = CreateGraph(Context.CompositionVersion, Context.TimelineMap, Context.TimelineMapDigest,
                profile, layout, Context.LayoutSpecDigest, Context.Template, inputs, [video, audio, output], output.Id);
            var context = new RenderGraphValidationContext(Context.Project, Context.CompositionVersion, Context.TimelineMap,
                Context.TimelineMapDigest, profile, layout, Context.LayoutSpecDigest, Context.Template, Context.SourceSpans,
                Context.EditorialRecordIds, inputs.Select(static input => new RenderArtifactEvidence(input.Id, input.ContentDigest, input.ByteSize)).ToArray(), []);
            return new FfmpegCompileRequest(graph, context, Tool,
                Bindings.Where(binding => inputs.Any(input => input.Id == binding.InputId)).ToArray(),
                OutputPath, RootPath, TimeSpan.FromMinutes(1), bindGeometry
                    ? [new FfmpegVideoGeometryBinding(video.Id, geometry ?? plan.SourcePlacements[0].SourceDisplayGeometry, plan)] : []);
        }

        internal RenderGraph GraphWithOutputRange(TimeRange range)
        {
            RenderNode output = RenderNode.Output(
                Graph.OutputNodeId,
                Graph.Nodes.Single(node => node.Id == Graph.OutputNodeId).InputNodeIds,
                range);
            return CopyGraph(
                Graph,
                nodes: Graph.Nodes
                    .Select(node => node.Id == output.Id ? output : node)
                    .ToArray());
        }

        internal RenderGraphValidationContext ContextWith(RenderProfile profile) =>
            new(
                Context.Project,
                Context.CompositionVersion,
                Context.TimelineMap,
                Context.TimelineMapDigest,
                profile,
                Context.LayoutSpec,
                Context.LayoutSpecDigest,
                Context.Template,
                Context.SourceSpans,
                Context.EditorialRecordIds,
                Context.Artifacts,
                Context.Fonts);

        internal (RenderGraph Graph, RenderGraphValidationContext Context)
            TransitionGraph(Transition transition)
        {
            RenderGraphInput firstInput = Input(1, RenderInputKind.Video, '1', 1000, Graph.Inputs[0].Origin);
            RenderGraphInput secondInput = Input(4, RenderInputKind.Graphic, '4', 50, Graph.Inputs[3].Origin);
            RenderNode first = RenderNode.Video(Node(1), firstInput.Id, Range(0, 4), firstInput.Origin, null, "video");
            RenderNode second = RenderNode.Graphic(
                Node(3),
                secondInput.Id,
                first.Id,
                new Cue(Id<CueIdTag>(0x78000019, 3), CueKind.Graphic, Range(2, 1), "graphic"),
                secondInput.Origin);
            IReadOnlyList<EntityId<RenderNodeIdTag>> transitionInputs = transition.Kind is
                TransitionKind.Fade or TransitionKind.TitleCard or TransitionKind.Zoom
                ? [second.Id]
                : [first.Id, second.Id];
            RenderNode effect = RenderNode.TransitionEffect(
                Node(6),
                transitionInputs,
                transition.Kind == TransitionKind.Cut ? new TimeRange(new RationalTime(2, 1), default) : Range(2, 1),
                Graph.Inputs[2].Origin,
                transition);
            RenderNode output = RenderNode.Output(Node(5), [effect.Id], Range(0, 4));
            RenderGraph graph = CreateGraph(
                Context.CompositionVersion,
                Context.TimelineMap,
                Context.TimelineMapDigest,
                Profile,
                Context.LayoutSpec,
                Context.LayoutSpecDigest,
                Context.Template,
                [firstInput, secondInput],
                [first, second, effect, output],
                output.Id);
            RenderGraphValidationContext context = new(
                Context.Project,
                Context.CompositionVersion,
                Context.TimelineMap,
                Context.TimelineMapDigest,
                Profile,
                Context.LayoutSpec,
                Context.LayoutSpecDigest,
                Context.Template,
                [],
                Context.EditorialRecordIds,
                graph.Inputs.Select(static input => new RenderArtifactEvidence(
                    input.Id, input.ContentDigest, input.ByteSize)).ToArray(),
                []);
            return (graph, context);
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }

        private static RenderGraphInput Input(
            int id,
            RenderInputKind kind,
            char digest,
            long size,
            OriginRef origin) =>
            new(Id<RenderInputIdTag>(0x78000020, id), kind, Digest(digest), size, origin);

        private static EntityId<RenderNodeIdTag> Node(int id) =>
            Id<RenderNodeIdTag>(0x78000021, id);

        private static RenderGraph CreateGraph(
            CompositionVersion composition,
            TimelineMap timeline,
            ContentDigest timelineDigest,
            RenderProfile profile,
            LayoutSpec layout,
            ContentDigest layoutDigest,
            TemplateReference template,
            IReadOnlyList<RenderGraphInput> inputs,
            IReadOnlyList<RenderNode> nodes,
            EntityId<RenderNodeIdTag> outputNodeId) =>
            new(
                Id<RenderGraphIdTag>(0x78000022, 1),
                composition.Id,
                composition.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                inputs,
                [],
                nodes,
                outputNodeId);

        private static RenderGraph CopyGraph(
            RenderGraph source,
            IReadOnlyList<RenderFontEvidence>? fonts = null,
            IReadOnlyList<RenderNode>? nodes = null) =>
            new(
                source.Id,
                source.CompositionVersionId,
                source.CompositionVersionDigest,
                source.TimelineMapId,
                source.TimelineMapDigest,
                source.ProfileId,
                source.ProfileDigest,
                source.LayoutSpecId,
                source.LayoutSpecDigest,
                source.Template,
                source.Inputs,
                fonts ?? source.Fonts,
                nodes ?? source.Nodes,
                source.OutputNodeId);
    }
}