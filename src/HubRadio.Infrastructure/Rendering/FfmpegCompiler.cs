using System.Globalization;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Rendering;

public sealed class FfmpegToolBinding
{
    public FfmpegToolBinding(BinaryVerificationRequest verificationRequest)
    {
        ArgumentNullException.ThrowIfNull(verificationRequest);
        VerificationRequest = verificationRequest;
    }

    public BinaryVerificationRequest VerificationRequest { get; }
}

public sealed class FfmpegInputBinding
{
    public FfmpegInputBinding(
        EntityId<RenderInputIdTag> inputId,
        BinaryVerificationRequest verificationRequest)
    {
        ArgumentNullException.ThrowIfNull(inputId);
        ArgumentNullException.ThrowIfNull(verificationRequest);
        InputId = inputId;
        VerificationRequest = verificationRequest;
    }

    public EntityId<RenderInputIdTag> InputId { get; }

    public BinaryVerificationRequest VerificationRequest { get; }

}

public sealed class FfmpegCompileRequest
{
    public FfmpegCompileRequest(
        RenderGraph graph,
        RenderGraphValidationContext validationContext,
        FfmpegToolBinding tool,
        IReadOnlyList<FfmpegInputBinding> inputs,
        string outputPath,
        string workingDirectory,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        FfmpegInputBinding[] copied = new FfmpegInputBinding[inputs.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            FfmpegInputBinding binding = inputs[index]
                ?? throw new ArgumentException(
                    "FFmpeg input bindings must not contain null values.",
                    nameof(inputs));
            if (!ids.Add(binding.InputId))
            {
                throw new ArgumentException(
                    "FFmpeg input bindings must use unique input identities.",
                    nameof(inputs));
            }

            copied[index] = binding;
        }

        Graph = graph;
        ValidationContext = validationContext;
        Tool = tool;
        Inputs = Array.AsReadOnly(copied);
        OutputPath = outputPath;
        WorkingDirectory = workingDirectory;
        Timeout = timeout;
    }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    public FfmpegToolBinding Tool { get; }

    public IReadOnlyList<FfmpegInputBinding> Inputs { get; }

    public string OutputPath { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }
}

public sealed class FfmpegCompilation
{
    internal FfmpegCompilation(
        ProcessRunRequest processRequest,
        string outputPath,
        string filterScript,
        ContentDigest filterScriptDigest)
    {
        ArgumentNullException.ThrowIfNull(processRequest);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(filterScript);
        ArgumentNullException.ThrowIfNull(filterScriptDigest);
        ProcessRequest = processRequest;
        OutputPath = outputPath;
        FilterScript = filterScript;
        FilterScriptDigest = filterScriptDigest;
    }

    public ProcessRunRequest ProcessRequest { get; }

    public string OutputPath { get; }

    public string FilterScript { get; }

    public ContentDigest FilterScriptDigest { get; }
}

public enum FfmpegCompilerFailureReason
{
    GraphInvalid,
    ToolNotVerified,
    InputNotVerified,
    InputBindingMismatch,
    UnsupportedCodec,
    UnsupportedTransition,
    OutputPathInvalid,
    CommandLineTooLong,
    CompilationFailed,
}

public sealed class FfmpegCompilerException : Exception
{
    internal FfmpegCompilerException(FfmpegCompilerFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public FfmpegCompilerFailureReason Reason { get; }

    private static string GetStableMessage(FfmpegCompilerFailureReason reason) =>
        reason switch
        {
            FfmpegCompilerFailureReason.GraphInvalid =>
                "The render graph is not valid for compilation.",
            FfmpegCompilerFailureReason.ToolNotVerified =>
                "The FFmpeg executable has not passed exact verification.",
            FfmpegCompilerFailureReason.InputNotVerified =>
                "A render input has not passed exact verification.",
            FfmpegCompilerFailureReason.InputBindingMismatch =>
                "The verified render-input bindings do not match the graph.",
            FfmpegCompilerFailureReason.UnsupportedCodec =>
                "The render profile contains an unsupported codec.",
            FfmpegCompilerFailureReason.UnsupportedTransition =>
                "The render graph contains a transition without a verified FFmpeg mapping.",
            FfmpegCompilerFailureReason.OutputPathInvalid =>
                "The FFmpeg output staging path is invalid.",
            FfmpegCompilerFailureReason.CommandLineTooLong =>
                "The compiled FFmpeg command exceeds the Windows command-line limit.",
            FfmpegCompilerFailureReason.CompilationFailed =>
                "The render graph could not be compiled safely.",
            _ => "FFmpeg compilation failed.",
        };
}

public static class FfmpegCompiler
{
    public const int MaximumWindowsCommandLineCharacters = 32767;
    public const long FixedFfmpegByteSize = DecodeValidator.CanonicalFfmpegByteSize;
    public const string FixedFfmpegSha256 = DecodeValidator.CanonicalFfmpegSha256;

    private static readonly ContentDigest FixedFfmpegDigest =
        new(FixedFfmpegSha256);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Task<FfmpegCompilation> CompileAsync(
        FfmpegCompileRequest request,
        CancellationToken cancellationToken = default) =>
        CompileAsync(
            request,
            new BinaryVerifier().VerifyAsync,
            cancellationToken);

    internal static async Task<FfmpegCompilation> CompileAsync(
        FfmpegCompileRequest request,
        Func<
            BinaryVerificationRequest,
            CancellationToken,
            Task<BinaryVerificationResult>> verifyAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifyAsync);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToolBinding(request.Tool);
        ValidateGraph(request.Graph, request.ValidationContext, cancellationToken);
        Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> bindings =
            ValidateBindings(request.Graph, request.Inputs);
        ValidateCodecs(request.ValidationContext.Profile);
        string outputPath = ValidateOutputPath(
            request.OutputPath,
            request.WorkingDirectory,
            bindings.Values);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            CompilationState state = CompileGraph(
                request.Graph,
                request.ValidationContext,
                bindings,
                cancellationToken);
            List<string> arguments = BuildArguments(
                request.ValidationContext.Profile,
                state,
                outputPath);
            string executable = request.Tool.VerificationRequest.FilePath;
            if (MeasureWindowsCommandLine(executable, arguments)
                >= MaximumWindowsCommandLineCharacters)
            {
                throw Failure(FfmpegCompilerFailureReason.CommandLineTooLong);
            }

            var processRequest = new ProcessRunRequest(
                executable,
                arguments,
                request.WorkingDirectory,
                request.Timeout);
            ContentDigest filterDigest = ContentDigest.Compute(
                new UTF8Encoding(false, true).GetBytes(state.FilterScript));
            await VerifyExactAsync(
                request.Tool.VerificationRequest,
                verifyAsync,
                FfmpegCompilerFailureReason.ToolNotVerified,
                cancellationToken).ConfigureAwait(false);
            foreach (RenderGraphInput input in request.Graph.Inputs)
            {
                await VerifyExactAsync(
                    bindings[input.Id].VerificationRequest,
                    verifyAsync,
                    FfmpegCompilerFailureReason.InputNotVerified,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new FfmpegCompilation(
                processRequest,
                outputPath,
                state.FilterScript,
                filterDigest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FfmpegCompilerException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or OverflowException
                or EncoderFallbackException)
        {
            throw Failure(FfmpegCompilerFailureReason.CompilationFailed);
        }
    }

    private static void ValidateToolBinding(FfmpegToolBinding tool)
    {
        if (!string.Equals(
                tool.VerificationRequest.ComponentIdentity,
                "ffmpeg",
                StringComparison.Ordinal)
            || tool.VerificationRequest.ExpectedDigest != FixedFfmpegDigest
            || tool.VerificationRequest.ExpectedByteSize != FixedFfmpegByteSize
            || !IsSafeProcessPathText(tool.VerificationRequest.FilePath))
        {
            throw Failure(FfmpegCompilerFailureReason.ToolNotVerified);
        }
    }

    private static async Task VerifyExactAsync(
        BinaryVerificationRequest request,
        Func<
            BinaryVerificationRequest,
            CancellationToken,
            Task<BinaryVerificationResult>> verifyAsync,
        FfmpegCompilerFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await verifyAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified
                || result.Reason is not null
                || !string.Equals(
                    result.ComponentIdentity,
                    request.ComponentIdentity,
                    StringComparison.Ordinal)
                || result.ExpectedDigest != request.ExpectedDigest
                || result.ExpectedByteSize != request.ExpectedByteSize
                || result.ActualDigest != request.ExpectedDigest
                || result.ActualByteSize != request.ExpectedByteSize)
            {
                throw Failure(failureReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FfmpegCompilerException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason);
        }
    }

    private static void ValidateGraph(
        RenderGraph graph,
        RenderGraphValidationContext context,
        CancellationToken cancellationToken)
    {
        RenderGraphValidationResult validation = RenderGraphValidator.Validate(
            graph,
            context,
            cancellationToken);
        if (!validation.IsValid)
        {
            throw Failure(FfmpegCompilerFailureReason.GraphInvalid);
        }
    }

    private static Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding>
        ValidateBindings(
            RenderGraph graph,
            IReadOnlyList<FfmpegInputBinding> supplied)
    {
        if (supplied.Count != graph.Inputs.Count)
        {
            throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        }

        Dictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> bindings =
            supplied.ToDictionary(static binding => binding.InputId);
        foreach (RenderGraphInput input in graph.Inputs)
        {
            if (!bindings.TryGetValue(input.Id, out FfmpegInputBinding? binding))
            {
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
            }

            if (binding.VerificationRequest.ExpectedDigest != input.ContentDigest
                || binding.VerificationRequest.ExpectedByteSize != input.ByteSize
                || !IsSafeProcessPathText(binding.VerificationRequest.FilePath))
            {
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
            }
        }

        return bindings;
    }

    private static void ValidateCodecs(RenderProfile profile)
    {
        if (!string.Equals(profile.VideoCodec, "h264", StringComparison.Ordinal)
            || !string.Equals(profile.AudioCodec, "aac", StringComparison.Ordinal))
        {
            throw Failure(FfmpegCompilerFailureReason.UnsupportedCodec);
        }
    }

    private static string ValidateOutputPath(
        string outputPath,
        string workingDirectory,
        IEnumerable<FfmpegInputBinding> inputs)
    {
        if (outputPath.Length >= MaximumWindowsCommandLineCharacters)
        {
            throw Failure(FfmpegCompilerFailureReason.CommandLineTooLong);
        }

        if (string.IsNullOrWhiteSpace(outputPath)
            || !string.Equals(outputPath, outputPath.Trim(), StringComparison.Ordinal)
            || !IsSafeProcessPathText(outputPath)
            || !IsSafeProcessPathText(workingDirectory)
            || !Path.IsPathFullyQualified(outputPath)
            || !string.Equals(
                Path.GetExtension(outputPath),
                ".mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
        }

        string fullOutput;
        string fullWorking;
        try
        {
            fullOutput = Path.GetFullPath(outputPath);
            fullWorking = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(workingDirectory));
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
        }

        string relative = Path.GetRelativePath(fullWorking, fullOutput);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            || inputs.Any(input => string.Equals(
                input.VerificationRequest.FilePath,
                fullOutput,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)))
        {
            throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
        }

        return fullOutput;
    }

    private static CompilationState CompileGraph(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> bindings,
        CancellationToken cancellationToken)
    {
        Dictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes =
            graph.Nodes.ToDictionary(static node => node.Id);
        RenderNode[] ordered = TopologicalOrder(
            graph.OutputNodeId,
            nodes,
            cancellationToken);
        var arguments = new List<string>();
        var filters = new List<string>();
        var streamLabels = new Dictionary<EntityId<RenderNodeIdTag>, string>();
        ResolvedLayout resolved = LayoutResolver.Resolve(
            context.LayoutSpec,
            context.Profile.Width,
            context.Profile.Height);
        int nextInputIndex = 0;

        for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderNode node = ordered[ordinal];
            switch (node.Kind)
            {
                case RenderNodeKind.Video:
                    {
                        int inputIndex = nextInputIndex++;
                        AddTimedInput(arguments, node, bindings[node.ArtifactInputId!]);
                        ResolvedLayoutRegion region = ResolveRegion(resolved, node.RegionName!);
                        string label = string.Concat("v", ordinal.ToString(CultureInfo.InvariantCulture));
                        filters.Add(BuildVideoFilter(inputIndex, node, region, context.Profile, label));
                        streamLabels[node.Id] = label;
                        break;
                    }

                case RenderNodeKind.Audio:
                    {
                        int inputIndex = nextInputIndex++;
                        AddTimedInput(arguments, node, bindings[node.ArtifactInputId!]);
                        string label = string.Concat("a", ordinal.ToString(CultureInfo.InvariantCulture));
                        filters.Add(string.Concat(
                            "[", inputIndex.ToString(CultureInfo.InvariantCulture), ":a:0]",
                            "atrim=duration=", FormatTime(node.OutputRange.Duration),
                            ",asetpts=PTS-STARTPTS[", label, "]"));
                        streamLabels[node.Id] = label;
                        break;
                    }

                case RenderNodeKind.Graphic:
                    {
                        int inputIndex = nextInputIndex++;
                        arguments.Add("-loop");
                        arguments.Add("1");
                        arguments.Add("-i");
                        arguments.Add(bindings[node.ArtifactInputId!].VerificationRequest.FilePath);
                        ResolvedLayoutRegion region = ResolveRegion(resolved, node.RegionName!);
                        string raw = string.Concat("g", ordinal.ToString(CultureInfo.InvariantCulture));
                        string output = string.Concat("v", ordinal.ToString(CultureInfo.InvariantCulture));
                        filters.Add(string.Concat(
                            "[", inputIndex.ToString(CultureInfo.InvariantCulture), ":v:0]",
                            "scale=", region.Rect.Width.ToString(CultureInfo.InvariantCulture),
                            ":", region.Rect.Height.ToString(CultureInfo.InvariantCulture),
                            ":flags=bilinear,format=rgba[", raw, "]"));
                        filters.Add(string.Concat(
                            "[", streamLabels[node.InputNodeIds[0]], "][", raw, "]",
                            "overlay=x=", region.Rect.X.ToString(CultureInfo.InvariantCulture),
                            ":y=", region.Rect.Y.ToString(CultureInfo.InvariantCulture),
                            ":enable='between(t,", FormatTime(node.OutputRange.Start),
                            ",", FormatTime(node.OutputRange.End),
                            ")':eof_action=repeat[", output, "]"));
                        streamLabels[node.Id] = output;
                        break;
                    }

                case RenderNodeKind.Subtitle:
                    {
                        if (context.Profile.SubtitleBurnIn)
                        {
                            string output = string.Concat("v", ordinal.ToString(CultureInfo.InvariantCulture));
                            string escaped = EscapeFilterOptionPath(
                                bindings[node.ArtifactInputId!].VerificationRequest.FilePath);
                            filters.Add(string.Concat(
                                "[", streamLabels[node.InputNodeIds[0]], "]",
                                "subtitles=filename=", escaped,
                                "[", output, "]"));
                            streamLabels[node.Id] = output;
                        }
                        else
                        {
                            int inputIndex = nextInputIndex++;
                            arguments.Add("-i");
                            arguments.Add(bindings[node.ArtifactInputId!].VerificationRequest.FilePath);
                            streamLabels[node.Id] = string.Concat(
                                inputIndex.ToString(CultureInfo.InvariantCulture),
                                ":s:0");
                        }

                        break;
                    }

                case RenderNodeKind.Transition:
                    {
                        string output = string.Concat("v", ordinal.ToString(CultureInfo.InvariantCulture));
                        filters.Add(BuildTransitionFilter(node, streamLabels, output));
                        streamLabels[node.Id] = output;
                        break;
                    }

                case RenderNodeKind.Output:
                    break;
                default:
                    throw new InvalidOperationException("Unknown render node kind.");
            }
        }

        RenderNode outputNode = nodes[graph.OutputNodeId];
        RenderNode[] terminals = outputNode.InputNodeIds.Select(id => nodes[id]).ToArray();
        RenderNode visual = context.Profile.SubtitleBurnIn
            ? terminals.Single(static node => node.Kind == RenderNodeKind.Subtitle)
            : terminals.Single(static node => node.Kind is
                RenderNodeKind.Video or RenderNodeKind.Graphic or RenderNodeKind.Transition);
        RenderNode? audio = terminals.SingleOrDefault(static node =>
            node.Kind == RenderNodeKind.Audio);
        RenderNode? subtitle = context.Profile.SubtitleBurnIn
            ? null
            : terminals.SingleOrDefault(static node => node.Kind == RenderNodeKind.Subtitle);
        return new CompilationState(
            arguments,
            string.Join(";", filters),
            string.Concat("[", streamLabels[visual.Id], "]"),
            audio is null ? null : string.Concat("[", streamLabels[audio.Id], "]"),
            subtitle is null ? null : streamLabels[subtitle.Id],
            outputNode.OutputRange.Duration);
    }

    private static List<string> BuildArguments(
        RenderProfile profile,
        CompilationState state,
        string outputPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-v",
            "error",
        };
        arguments.AddRange(state.InputArguments);
        if (!string.IsNullOrEmpty(state.FilterScript))
        {
            arguments.Add("-filter_complex");
            arguments.Add(state.FilterScript);
        }

        arguments.Add("-map");
        arguments.Add(state.VideoMap);
        if (state.AudioMap is not null)
        {
            arguments.Add("-map");
            arguments.Add(state.AudioMap);
        }

        if (state.SubtitleMap is not null)
        {
            arguments.Add("-map");
            arguments.Add(state.SubtitleMap);
        }

        arguments.Add("-c:v");
        arguments.Add("h264_mf");
        arguments.Add("-pix_fmt");
        arguments.Add("yuv420p");
        arguments.Add("-r");
        arguments.Add(string.Concat(
            profile.FrameRateNumerator.ToString(CultureInfo.InvariantCulture),
            "/",
            profile.FrameRateDenominator.ToString(CultureInfo.InvariantCulture)));
        if (state.AudioMap is not null)
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
        }

        if (state.SubtitleMap is not null)
        {
            arguments.Add("-c:s");
            arguments.Add("mov_text");
        }

        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-movflags");
        arguments.Add("+faststart");
        arguments.Add("-t");
        arguments.Add(FormatTime(state.OutputDuration));
        arguments.Add("-n");
        arguments.Add(outputPath);
        return arguments;
    }

    private static void AddTimedInput(
        List<string> arguments,
        RenderNode node,
        FfmpegInputBinding binding)
    {
        if (node.SourceRange is TimeRange sourceRange)
        {
            arguments.Add("-ss");
            arguments.Add(FormatTime(sourceRange.Start));
        }

        arguments.Add("-t");
        arguments.Add(FormatTime(node.OutputRange.Duration));
        arguments.Add("-i");
        arguments.Add(binding.VerificationRequest.FilePath);
    }

    private static string BuildVideoFilter(
        int inputIndex,
        RenderNode node,
        ResolvedLayoutRegion region,
        RenderProfile profile,
        string label)
    {
        var filter = new StringBuilder();
        _ = filter
            .Append('[').Append(inputIndex).Append(":v:0]")
            .Append("trim=duration=").Append(FormatTime(node.OutputRange.Duration))
            .Append(",setpts=PTS-STARTPTS,scale=")
            .Append(region.Rect.Width).Append(':').Append(region.Rect.Height)
            .Append(":flags=bilinear");
        if (region.Rect.Width != profile.Width
            || region.Rect.Height != profile.Height
            || region.Rect.X != 0
            || region.Rect.Y != 0)
        {
            _ = filter
                .Append(",pad=").Append(profile.Width).Append(':').Append(profile.Height)
                .Append(':').Append(region.Rect.X).Append(':').Append(region.Rect.Y)
                .Append(":color=black");
        }

        _ = filter
            .Append(",fps=")
            .Append(profile.FrameRateNumerator).Append('/')
            .Append(profile.FrameRateDenominator)
            .Append(",format=yuv420p[").Append(label).Append(']');
        return filter.ToString();
    }

    private static string BuildTransitionFilter(
        RenderNode node,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, string> labels,
        string output)
    {
        Transition transition = node.Transition
            ?? throw new InvalidOperationException("A transition node requires a transition.");
        TransitionRenderInstruction instruction =
            new TransitionEmitter().Emit(transition);
        string[] inputs = node.InputNodeIds
            .Select(id => string.Concat("[", labels[id], "]"))
            .ToArray();
        string duration = FormatTime(instruction.Duration);
        string start = FormatTime(node.OutputRange.Start);
        return instruction.Operation switch
        {
            TransitionRenderOperation.DirectCut when inputs.Length == 2 => string.Concat(
                inputs[0], inputs[1], "concat=n=2:v=1:a=0[", output, "]"),
            TransitionRenderOperation.Crossfade when inputs.Length == 2 => Xfade(
                inputs, "fade", duration, start, output),
            TransitionRenderOperation.Fade when inputs.Length == 1 => string.Concat(
                inputs[0], "fade=t=out:st=", start, ":d=", duration,
                "[", output, "]"),
            TransitionRenderOperation.Wipe when inputs.Length == 2 => Xfade(
                inputs,
                instruction.Direction switch
                {
                    TransitionDirection.Left => "wipeleft",
                    TransitionDirection.Right => "wiperight",
                    TransitionDirection.Up => "wipeup",
                    TransitionDirection.Down => "wipedown",
                    _ => throw new InvalidOperationException(),
                },
                duration,
                start,
                output),
            TransitionRenderOperation.Slide when inputs.Length == 2 => Xfade(
                inputs,
                instruction.Direction switch
                {
                    TransitionDirection.Left => "slideleft",
                    TransitionDirection.Right => "slideright",
                    TransitionDirection.Up => "slideup",
                    TransitionDirection.Down => "slidedown",
                    _ => throw new InvalidOperationException(),
                },
                duration,
                start,
                output),
            TransitionRenderOperation.TitleCard or TransitionRenderOperation.Zoom => throw Failure(
                FfmpegCompilerFailureReason.UnsupportedTransition),
            _ => throw Failure(FfmpegCompilerFailureReason.UnsupportedTransition),
        };
    }

    private static string Xfade(
        IReadOnlyList<string> inputs,
        string transition,
        string duration,
        string offset,
        string output) =>
        string.Concat(
            inputs[0], inputs[1],
            "xfade=transition=", transition,
            ":duration=", duration,
            ":offset=", offset,
            "[", output, "]");

    private static RenderNode[] TopologicalOrder(
        EntityId<RenderNodeIdTag> outputNodeId,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<EntityId<RenderNodeIdTag>>();
        var ordered = new List<RenderNode>();
        Visit(outputNodeId, nodes, visited, ordered, cancellationToken);
        return ordered.ToArray();
    }

    private static void Visit(
        EntityId<RenderNodeIdTag> id,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        HashSet<EntityId<RenderNodeIdTag>> visited,
        List<RenderNode> ordered,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!visited.Add(id))
        {
            return;
        }

        RenderNode node = nodes[id];
        foreach (EntityId<RenderNodeIdTag> dependency in node.InputNodeIds)
        {
            Visit(dependency, nodes, visited, ordered, cancellationToken);
        }

        ordered.Add(node);
    }

    private static ResolvedLayoutRegion ResolveRegion(
        ResolvedLayout layout,
        string name) =>
        layout.Regions.Single(region =>
            string.Equals(region.Name, name, StringComparison.Ordinal));

    private static string FormatTime(RationalTime time)
    {
        decimal value = (decimal)time.Value / time.Rate;
        return value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
    }

    private static string EscapeFilterOptionPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        string optionEscaped = EscapeCharacters(
            normalized,
            static character => character is '\\' or '\'' or ':');
        return EscapeCharacters(
            optionEscaped,
            static character => character is '\\' or '\'' or '[' or ']' or ';' or ',');
    }

    private static string EscapeCharacters(
        string value,
        Func<char, bool> requiresEscape)
    {
        var escaped = new StringBuilder(value.Length * 2);
        foreach (char character in value)
        {
            if (requiresEscape(character))
            {
                _ = escaped.Append('\\');
            }

            _ = escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static bool IsSafeProcessPathText(string value)
    {
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static int MeasureWindowsCommandLine(
        string executable,
        IReadOnlyList<string> arguments)
    {
        long length = QuotedArgumentLength(executable) + 1;
        foreach (string argument in arguments)
        {
            length += QuotedArgumentLength(argument) + 1L;
            if (length >= MaximumWindowsCommandLineCharacters)
            {
                return MaximumWindowsCommandLineCharacters;
            }
        }

        return checked((int)length);
    }

    private static int QuotedArgumentLength(string argument)
    {
        if (argument.Length > MaximumWindowsCommandLineCharacters)
        {
            return MaximumWindowsCommandLineCharacters;
        }

        bool requiresQuotes = argument.Length == 0
            || argument.Any(static value => char.IsWhiteSpace(value) || value == '"');
        if (!requiresQuotes)
        {
            return argument.Length;
        }

        long length = 2;
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                length += (backslashes * 2L) + 2;
            }
            else
            {
                length += backslashes + 1L;
            }

            backslashes = 0;
        }

        length += backslashes * 2L;
        return length >= MaximumWindowsCommandLineCharacters
            ? MaximumWindowsCommandLineCharacters
            : (int)length;
    }

    private static FfmpegCompilerException Failure(
        FfmpegCompilerFailureReason reason) =>
        new(reason);

    private sealed record CompilationState(
        IReadOnlyList<string> InputArguments,
        string FilterScript,
        string VideoMap,
        string? AudioMap,
        string? SubtitleMap,
        RationalTime OutputDuration);
}