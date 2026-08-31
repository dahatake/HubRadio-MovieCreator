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
        CollectionAssert.AreEqual(
            new[] { "[v2]", "[a3]" },
            MapTokens(compilation.ProcessRequest.ArgumentList));
        StringAssert.Contains(
            compilation.FilterScript,
            "scale=1280:720:flags=bilinear,fps=30000/1001,format=yuv420p[v0]");
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
                30000,
                1001,
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
            string? outputPath = null) =>
            new(
                graph ?? Graph,
                context ?? Context,
                tool ?? Tool,
                bindings ?? Bindings,
                outputPath ?? OutputPath,
                RootPath,
                TimeSpan.FromMinutes(5));

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

        private static NormalizedRect Rect(
            long xN, long xD, long yN, long yD,
            long wN, long wD, long hN, long hD) =>
            new(
                new RationalNumber(xN, xD),
                new RationalNumber(yN, yD),
                new RationalNumber(wN, wD),
                new RationalNumber(hN, hD));
    }
}