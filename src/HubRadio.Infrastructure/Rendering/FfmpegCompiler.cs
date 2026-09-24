using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
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
        TimeSpan timeout,
        IReadOnlyList<FfmpegVideoGeometryBinding>? videoGeometry = null,
        IReadOnlyList<FfmpegFontBinding>? fonts = null,
        FfmpegEncoderSettings? encoderSettings = null,
        FfmpegGeneratedScriptTarget? generatedScript = null,
        IReadOnlyList<FfmpegAssGraphicBinding>? graphics = null,
        FfmpegFrameExtraction? frameExtraction = null,
        IReadOnlyList<FfmpegMusicBinding>? music = null,
        bool loudnessMeasurement = false)
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
        VideoGeometry = Array.AsReadOnly((videoGeometry ?? []).ToArray());
        Fonts = Array.AsReadOnly((fonts ?? []).ToArray());
        EncoderSettings = encoderSettings;
        GeneratedScript = generatedScript;
        Graphics = Array.AsReadOnly((graphics ?? []).ToArray());
        FrameExtraction = frameExtraction;
        Music = Array.AsReadOnly((music ?? []).ToArray());
        LoudnessMeasurement = loudnessMeasurement;
        if (Music.Count > 16 || Music.Any(static item => item is null))
            throw new ArgumentException("Music bindings must be bounded and non-null.", nameof(music));
        if (VideoGeometry.Any(static item => item is null)
            || VideoGeometry.Select(static item => item.NodeId).Distinct().Count() != VideoGeometry.Count
            || Fonts.Any(static item => item is null)
            || Fonts.Select(static item => item.Evidence.Name).Distinct(StringComparer.Ordinal).Count() != Fonts.Count)
        {
            throw new ArgumentException("Geometry and font bindings must be unique and non-null.");
        }

        if (Graphics.Count > 64 || Graphics.Any(static item => item is null)
            || Graphics.Select(static item => item.InputId).Distinct().Count() != Graphics.Count)
        {
            throw new ArgumentException("Graphic bindings must be bounded, unique and non-null.", nameof(graphics));
        }
    }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    public FfmpegToolBinding Tool { get; }

    public IReadOnlyList<FfmpegInputBinding> Inputs { get; }

    public string OutputPath { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }

    public IReadOnlyList<FfmpegVideoGeometryBinding> VideoGeometry { get; }

    public IReadOnlyList<FfmpegFontBinding> Fonts { get; }

    public FfmpegEncoderSettings? EncoderSettings { get; }
    public FfmpegGeneratedScriptTarget? GeneratedScript { get; }
    public IReadOnlyList<FfmpegAssGraphicBinding> Graphics { get; }
    public FfmpegFrameExtraction? FrameExtraction { get; }
    public IReadOnlyList<FfmpegMusicBinding> Music { get; }
    /// <summary>Audio-only loudnorm analysis to the null muxer; no media file is produced.</summary>
    public bool LoudnessMeasurement { get; }
}

/// <summary>A verified, non-looping file segment. Editorial eligibility belongs to the plan adapter.</summary>
public sealed class FfmpegMusicBinding
{
    public FfmpegMusicBinding(BinaryVerificationRequest verificationRequest, TimeRange outputRange)
    {
        VerificationRequest = verificationRequest ?? throw new ArgumentNullException(nameof(verificationRequest));
        if (outputRange.Start < default(RationalTime) || outputRange.Duration <= default(RationalTime))
            throw new ArgumentOutOfRangeException(nameof(outputRange));
        OutputRange = outputRange;
    }
    public BinaryVerificationRequest VerificationRequest { get; }
    public TimeRange OutputRange { get; }
}

/// <summary>Opt-in delivery encoding. No encoder name or raw option text is accepted.</summary>
public sealed class FfmpegEncoderSettings
{
    public FfmpegEncoderSettings(int videoBitrateBitsPerSecond, int audioBitrateBitsPerSecond,
        int audioChannels, FfmpegLoudnessTarget? loudness = null, FfmpegSourceColor? sourceColor = null)
    {
        if (videoBitrateBitsPerSecond is < 100_000 or > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(videoBitrateBitsPerSecond));
        if (audioBitrateBitsPerSecond is < 32_000 or > 512_000)
            throw new ArgumentOutOfRangeException(nameof(audioBitrateBitsPerSecond));
        if (audioChannels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(audioChannels));
        VideoBitrateBitsPerSecond = videoBitrateBitsPerSecond;
        AudioBitrateBitsPerSecond = audioBitrateBitsPerSecond;
        AudioChannels = audioChannels;
        Loudness = loudness;
        SourceColor = sourceColor;
    }

    public int VideoBitrateBitsPerSecond { get; }
    public int AudioBitrateBitsPerSecond { get; }
    public int AudioChannels { get; }
    public string VideoProfile => "high";
    public string ColorSpace => "bt709";
    public bool Progressive => true;
    public FfmpegLoudnessTarget? Loudness { get; }
    public FfmpegSourceColor? SourceColor { get; }
}

public enum FfmpegSourceColorModel
{
    Bt709Sdr,
    Bt601525Sdr,
    Bt601625Sdr,
    Bt2020Sdr,
    Bt2020Pq,
    Bt2020Hlg,
}

/// <summary>Closed measured source-color interpretation; no raw color/filter names.</summary>
public sealed class FfmpegSourceColor
{
    public FfmpegSourceColor(FfmpegSourceColorModel model, bool fullRange)
    {
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        Model = model;
        FullRange = fullRange;
    }
    public FfmpegSourceColorModel Model { get; }
    public bool FullRange { get; }
    public bool ToneMapping => Model is FfmpegSourceColorModel.Bt2020Pq or FfmpegSourceColorModel.Bt2020Hlg;
}

/// <summary>A dynamic normalization pass, or a linear pass when a prior measurement pass is attached; its report is not post-encode QC.</summary>
public sealed class FfmpegLoudnessTarget
{
    public FfmpegLoudnessTarget(decimal integratedLufs, decimal truePeakDbtp, decimal rangeLu = 11m)
    {
        if (integratedLufs is < -70m or > -5m) throw new ArgumentOutOfRangeException(nameof(integratedLufs));
        if (truePeakDbtp is < -9m or > 0m) throw new ArgumentOutOfRangeException(nameof(truePeakDbtp));
        if (rangeLu is < 1m or > 50m) throw new ArgumentOutOfRangeException(nameof(rangeLu));
        IntegratedLufs = integratedLufs;
        TruePeakDbtp = truePeakDbtp;
        RangeLu = rangeLu;
    }

    public decimal IntegratedLufs { get; }
    public decimal TruePeakDbtp { get; }
    public decimal RangeLu { get; }
    public decimal? MeasuredIntegratedLufs { get; private init; }
    public decimal? MeasuredTruePeakDbtp { get; private init; }
    public decimal? MeasuredRangeLu { get; private init; }
    public decimal? MeasuredThresholdLufs { get; private init; }
    public decimal? MeasuredOffsetDb { get; private init; }
    public bool IsMeasured => MeasuredIntegratedLufs is not null;

    /// <summary>Bounds are the pinned loudnorm option ranges (measured_I/TP/LRA/thresh, offset).</summary>
    public FfmpegLoudnessTarget WithMeasurement(decimal integratedLufs, decimal truePeakDbtp, decimal rangeLu,
        decimal thresholdLufs, decimal offsetDb)
    {
        if (integratedLufs is < -99m or > 0m) throw new ArgumentOutOfRangeException(nameof(integratedLufs));
        if (truePeakDbtp is < -99m or > 99m) throw new ArgumentOutOfRangeException(nameof(truePeakDbtp));
        if (rangeLu is < 0m or > 99m) throw new ArgumentOutOfRangeException(nameof(rangeLu));
        if (thresholdLufs is < -99m or > 0m) throw new ArgumentOutOfRangeException(nameof(thresholdLufs));
        if (offsetDb is < -99m or > 99m) throw new ArgumentOutOfRangeException(nameof(offsetDb));
        return new FfmpegLoudnessTarget(IntegratedLufs, TruePeakDbtp, RangeLu)
        {
            MeasuredIntegratedLufs = integratedLufs,
            MeasuredTruePeakDbtp = truePeakDbtp,
            MeasuredRangeLu = rangeLu,
            MeasuredThresholdLufs = thresholdLufs,
            MeasuredOffsetDb = offsetDb,
        };
    }
}

/// <summary>Only a new managed destination, NEVER caller-supplied filter contents.</summary>
public sealed class FfmpegGeneratedScriptTarget
{
    public FfmpegGeneratedScriptTarget(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));
    public string Path { get; }
}

/// <summary>
/// An independently burned-in metadata graphic. Its bytes and provenance are a
/// Graphic input in the graph, not speech subtitles or an unverified filter string.
/// </summary>
public sealed class FfmpegAssGraphicBinding
{
    public FfmpegAssGraphicBinding(EntityId<RenderInputIdTag> inputId,
        EntityId<RenderNodeIdTag> targetVisualNodeId, TimeRange outputRange)
    {
        InputId = inputId ?? throw new ArgumentNullException(nameof(inputId));
        TargetVisualNodeId = targetVisualNodeId ?? throw new ArgumentNullException(nameof(targetVisualNodeId));
        if (outputRange.Start < default(RationalTime) || outputRange.Duration <= default(RationalTime))
            throw new ArgumentOutOfRangeException(nameof(outputRange));
        OutputRange = outputRange;
    }

    public EntityId<RenderInputIdTag> InputId { get; }
    public EntityId<RenderNodeIdTag> TargetVisualNodeId { get; }
    public TimeRange OutputRange { get; }
}

public sealed class FfmpegFrameExtraction
{
    public FfmpegFrameExtraction(EntityId<RenderNodeIdTag> nodeId, int sourceFrameSequence,
        RationalTime sourcePresentationTime, FfmpegSourceColor? sourceColor = null)
    {
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        if (sourceFrameSequence < 0) throw new ArgumentOutOfRangeException(nameof(sourceFrameSequence));
        if (sourcePresentationTime < default(RationalTime)) throw new ArgumentOutOfRangeException(nameof(sourcePresentationTime));
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
        SourceColor = sourceColor;
    }

    public EntityId<RenderNodeIdTag> NodeId { get; }
    public int SourceFrameSequence { get; }
    public RationalTime SourcePresentationTime { get; }
    public FfmpegSourceColor? SourceColor { get; }
}

/// <summary>Display-oriented geometry; a supplied manual plan is rendered, not inferred.</summary>
public sealed class FfmpegVideoGeometryBinding
{
    public FfmpegVideoGeometryBinding(
        EntityId<RenderNodeIdTag> nodeId,
        SourceDisplayGeometry geometry,
        ManualLayoutPlan? layout = null)
    {
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Layout = layout;
    }

    public EntityId<RenderNodeIdTag> NodeId { get; }
    public SourceDisplayGeometry Geometry { get; }
    public ManualLayoutPlan? Layout { get; }
}

public sealed class FfmpegFontBinding
{
    public FfmpegFontBinding(RenderFontEvidence evidence, BinaryVerificationRequest verificationRequest)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        VerificationRequest = verificationRequest ?? throw new ArgumentNullException(nameof(verificationRequest));
        if (evidence.FileDigest != verificationRequest.ExpectedDigest)
        {
            throw new ArgumentException("Font bytes must match their evidence.", nameof(verificationRequest));
        }
    }

    public RenderFontEvidence Evidence { get; }
    public BinaryVerificationRequest VerificationRequest { get; }
}

public sealed class FfmpegCompilation : IDisposable
{
    private readonly FileStream? _scriptLease;

    internal FfmpegCompilation(
        ProcessRunRequest processRequest,
        string outputPath,
        string filterScript,
        ContentDigest filterScriptDigest,
        FileStream? scriptLease = null)
    {
        ArgumentNullException.ThrowIfNull(processRequest);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(filterScript);
        ArgumentNullException.ThrowIfNull(filterScriptDigest);
        ProcessRequest = processRequest;
        OutputPath = outputPath;
        FilterScript = filterScript;
        FilterScriptDigest = filterScriptDigest;
        _scriptLease = scriptLease;
        GeneratedScriptPath = scriptLease?.Name;
    }

    public ProcessRunRequest ProcessRequest { get; }

    public string OutputPath { get; }

    public string FilterScript { get; }

    public ContentDigest FilterScriptDigest { get; }

    public string? GeneratedScriptPath { get; }

    // The file remains read-only to other processes until the render finishes.
    // The owner cleans up the staging file, not an arbitrary compiler input.
    public void Dispose() => _scriptLease?.Dispose();
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
    UnsupportedAudioShape,
    UnsupportedGeometry,
    UnsupportedTimingGrid,
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
            FfmpegCompilerFailureReason.UnsupportedAudioShape =>
                "Audio cuts must cover the output without gaps or overlaps; transition audio is unsupported.",
            FfmpegCompilerFailureReason.UnsupportedGeometry =>
                "The source geometry or manual placement has no supported exact mapping.",
            FfmpegCompilerFailureReason.UnsupportedTimingGrid =>
                "Every source and output cut must align exactly to output frames and audio samples.",
            _ => "FFmpeg compilation failed.",
        };
}

public static class FfmpegCompiler
{
    public const int MaximumWindowsCommandLineCharacters = 32767;
    public const long FixedFfmpegByteSize = DecodeValidator.CanonicalFfmpegByteSize;
    public const string FixedFfmpegSha256 = DecodeValidator.CanonicalFfmpegSha256;
    // The fixed output contract also used by StandardRenderProfileFactory.
    public const int OutputAudioSampleRate = 48000;
    public const string OutputAudioProfile = "LC";
    public const int MaximumGeneratedScriptBytes = 1024 * 1024;

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
        ValidateExtensions(request, bindings);
        if (request.FrameExtraction is null)
            ValidateTimingGrid(request.Graph, request.ValidationContext.Profile);
        string outputPath = ValidateOutputPath(
            request.OutputPath,
            request.WorkingDirectory,
            bindings.Values,
            request.FrameExtraction is null ? ".mp4" : ".png");
        if (request.FrameExtraction is not null && WorkspacePathSafety.PathEntryExistsConservatively(outputPath))
            throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
        string? scriptPath = request.GeneratedScript is null ? null : ValidateScriptTarget(request, outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            CompilationState state = CompileGraph(
                request.Graph,
                request.ValidationContext,
                bindings,
                request.VideoGeometry,
                request.Fonts,
                request.EncoderSettings,
                request.Graphics,
                request.FrameExtraction,
                request.Music,
                request.LoudnessMeasurement,
                cancellationToken);
            List<string> arguments = BuildArguments(
                request.ValidationContext.Profile,
                state,
                outputPath,
                request.EncoderSettings,
                scriptPath,
                request.FrameExtraction is not null,
                request.LoudnessMeasurement);
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
            byte[] scriptBytes = StrictUtf8.GetBytes(state.FilterScript);
            if (scriptBytes.Length > MaximumGeneratedScriptBytes)
                throw Failure(FfmpegCompilerFailureReason.CommandLineTooLong);
            ContentDigest filterDigest = ContentDigest.Compute(scriptBytes);
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

            foreach (FfmpegFontBinding font in request.Fonts)
            {
                if (!request.Graph.Fonts.Contains(font.Evidence)
                    || !IsSafeProcessPathText(font.VerificationRequest.FilePath))
                {
                    throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
                }

                await VerifyExactAsync(font.VerificationRequest, verifyAsync,
                    FfmpegCompilerFailureReason.InputNotVerified, cancellationToken).ConfigureAwait(false);
            }

            if (request.Fonts.Count > 0 && request.Fonts.Count != request.Graph.Fonts.Count)
            {
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
            }

            foreach (FfmpegMusicBinding music in request.Music)
                await VerifyExactAsync(music.VerificationRequest, verifyAsync,
                    FfmpegCompilerFailureReason.InputNotVerified, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            FileStream? lease = null;
            if (scriptPath is not null)
            {
                try
                {
                    WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(scriptPath)!);
                    await using (var writer = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await writer.WriteAsync(scriptBytes, cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(scriptPath)!);
                    WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(scriptPath));
                    lease = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (lease.Length != scriptBytes.LongLength
                        || ContentDigest.FromBytes(await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false)) != filterDigest)
                        throw Failure(FfmpegCompilerFailureReason.InputNotVerified);
                    lease.Position = 0;
                }
                catch
                {
                    lease?.Dispose();
                    throw;
                }
            }

            return new FfmpegCompilation(
                processRequest,
                outputPath,
                state.FilterScript,
                filterDigest,
                lease);
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
                or EncoderFallbackException
                or IOException
                or UnauthorizedAccessException
                or WorkspacePathSafetyException)
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

    internal static bool IsOnOutputGrid(RationalTime time, RenderProfile profile) =>
        ((BigInteger)time.Value * profile.FrameRateNumerator)
            % ((BigInteger)time.Rate * profile.FrameRateDenominator) == 0
        && IsOnSampleGrid(time, OutputAudioSampleRate);

    internal static bool IsOnSampleGrid(RationalTime time, int sampleRate) =>
        sampleRate > 0 && ((BigInteger)time.Value * sampleRate) % time.Rate == 0;

    private static void ValidateTimingGrid(RenderGraph graph, RenderProfile profile)
    {
        foreach (RenderNode node in graph.Nodes.Where(static node =>
            node.Kind is RenderNodeKind.Video or RenderNodeKind.Audio or RenderNodeKind.Output))
        {
            if (!IsOnOutputGrid(node.OutputRange.Start, profile)
                || !IsOnOutputGrid(node.OutputRange.Duration, profile)
                || node.SourceRange is TimeRange source &&
                    (!IsOnOutputGrid(source.Start, profile)
                        || !IsOnOutputGrid(source.Duration, profile)
                        || source.Duration != node.OutputRange.Duration))
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedTimingGrid);
            }
        }
    }

    private static string ValidateOutputPath(
        string outputPath,
        string workingDirectory,
        IEnumerable<FfmpegInputBinding> inputs,
        string extension = ".mp4")
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
                extension,
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

    private static string ValidateScriptTarget(FfmpegCompileRequest request, string outputPath)
    {
        try
        {
            string path = ValidateOutputPath(request.GeneratedScript!.Path, request.WorkingDirectory,
                request.Inputs, ".ffscript");
            if (path.StartsWith(@"\\", StringComparison.Ordinal)
                || !WorkspacePathSafety.TryGetPortableRelativePath(Path.GetFullPath(request.WorkingDirectory), path, out _)
                || string.Equals(path, outputPath, WorkspacePathSafety.PathComparison)
                || string.Equals(path, request.Tool.VerificationRequest.FilePath, WorkspacePathSafety.PathComparison)
                || request.Fonts.Any(font => string.Equals(path, font.VerificationRequest.FilePath, WorkspacePathSafety.PathComparison))
                || WorkspacePathSafety.PathEntryExistsConservatively(path))
                throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(path)!);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or WorkspacePathSafetyException)
        {
            throw Failure(FfmpegCompilerFailureReason.OutputPathInvalid);
        }
    }

    private static void ValidateExtensions(FfmpegCompileRequest request,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> bindings)
    {
        RenderGraph graph = request.Graph;
        RenderNode output = graph.Nodes.Single(node => node.Id == graph.OutputNodeId);
        if (graph.Inputs.Any(input => input.Kind == RenderInputKind.Graphic
            && string.Equals(Path.GetExtension(bindings[input.Id].VerificationRequest.FilePath), ".ass", StringComparison.OrdinalIgnoreCase)
            && !graph.Nodes.Any(node => node.ArtifactInputId == input.Id)
            && !request.Graphics.Any(graphic => graphic.InputId == input.Id)))
            throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        foreach (FfmpegAssGraphicBinding graphic in request.Graphics)
        {
            RenderGraphInput? input = graph.Inputs.SingleOrDefault(item => item.Id == graphic.InputId);
            RenderNode? target = graph.Nodes.SingleOrDefault(item => item.Id == graphic.TargetVisualNodeId);
            if (input?.Kind != RenderInputKind.Graphic || input.ByteSize > MaximumGeneratedScriptBytes
                || target?.Kind is not (RenderNodeKind.Video or RenderNodeKind.Transition or RenderNodeKind.Graphic)
                || graph.Nodes.Any(node => node.ArtifactInputId == graphic.InputId)
                || graphic.OutputRange.End > output.OutputRange.End
                || request.Fonts.Count == 0
                || !string.Equals(Path.GetExtension(bindings[graphic.InputId].VerificationRequest.FilePath), ".ass", StringComparison.OrdinalIgnoreCase))
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        }

        if (request.FrameExtraction is FfmpegFrameExtraction frame)
        {
            RenderNode[] video = graph.Nodes.Where(static node => node.Kind == RenderNodeKind.Video).ToArray();
            if (video.Length != 1 || video[0].Id != frame.NodeId
                || video[0].SourceRange?.Start != frame.SourcePresentationTime
                || video[0].OutputRange.Start != default(RationalTime)
                || request.VideoGeometry.Count != 1
                || request.VideoGeometry[0].NodeId != frame.NodeId
                || request.VideoGeometry[0].Layout is not null
                || request.EncoderSettings is not null
                || graph.Nodes.Any(static node => node.Kind is not (RenderNodeKind.Video or RenderNodeKind.Output)))
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        }

        if (request.EncoderSettings?.Loudness is not null
            && !graph.Nodes.Any(static node => node.Kind == RenderNodeKind.Audio))
            throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape);
        if (request.LoudnessMeasurement && request.EncoderSettings?.Loudness is not { IsMeasured: false })
            throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);

        foreach (FfmpegMusicBinding music in request.Music)
        {
            if (request.EncoderSettings is null || request.FrameExtraction is not null
                || !graph.Nodes.Any(static node => node.Kind == RenderNodeKind.Audio)
                || music.OutputRange.End > output.OutputRange.End
                || !IsOnOutputGrid(music.OutputRange.Start, request.ValidationContext.Profile)
                || !IsOnOutputGrid(music.OutputRange.Duration, request.ValidationContext.Profile)
                || !IsSafeProcessPathText(music.VerificationRequest.FilePath)
                || !Path.IsPathFullyQualified(music.VerificationRequest.FilePath)
                || !string.Equals(Path.GetExtension(music.VerificationRequest.FilePath), ".mp3", StringComparison.OrdinalIgnoreCase))
                throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        }
        FfmpegMusicBinding[] orderedMusic = request.Music.OrderBy(static item => item.OutputRange.Start).ToArray();
        for (int i = 1; i < orderedMusic.Length; i++)
            if (orderedMusic[i].OutputRange.Start < orderedMusic[i - 1].OutputRange.End)
                throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape);
    }

    private static CompilationState CompileGraph(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, FfmpegInputBinding> bindings,
        IReadOnlyList<FfmpegVideoGeometryBinding> videoGeometry,
        IReadOnlyList<FfmpegFontBinding> fonts,
        FfmpegEncoderSettings? encoderSettings,
        IReadOnlyList<FfmpegAssGraphicBinding> graphics,
        FfmpegFrameExtraction? frameExtraction,
        IReadOnlyList<FfmpegMusicBinding> music,
        bool loudnessMeasurement,
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
        // Every input is still declared so input indices match the final pass.
        var audioFilters = new List<string>();
        void AddAudioFilter(string filter)
        {
            filters.Add(filter);
            audioFilters.Add(filter);
        }
        var streamLabels = new Dictionary<EntityId<RenderNodeIdTag>, string>();
        var centeredFitNodes = new HashSet<EntityId<RenderNodeIdTag>>();
        var centeredFitRegions = new HashSet<LayoutRegion>();
        foreach (FfmpegVideoGeometryBinding binding in videoGeometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(binding.NodeId, out RenderNode? node)
                || node.Kind != RenderNodeKind.Video)
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
            }

            if (binding.Layout is ManualLayoutPlan layout)
            {
                ValidateManualLayout(node, context.LayoutSpec, binding, layout);
                if (RequiresCenteredSourceFit(layout, binding.Geometry, context.Profile)
                    && string.Equals(node.RegionName, layout.SourcePlacements[0].Destination.Region.Name,
                        StringComparison.Ordinal))
                {
                    centeredFitNodes.Add(node.Id);
                    centeredFitRegions.Add(layout.SourcePlacements[1].Destination.Region);
                }
            }
        }

        if (centeredFitRegions.Count > 0 && graph.Nodes.Any(node =>
            node.Kind == RenderNodeKind.Video && !centeredFitNodes.Contains(node.Id)))
        {
            // One bound cut must not excuse an unbound cut in the same layout.
            throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
        }

        // A centered full-source fit may have fractional ideal bounds (1920x1080
        // in 1080x1920 is 1080x607.5 at y=656.25). Only those proven fit regions
        // are rendered by scale/overlay expressions. All other regions and every
        // safe zone still require exact integer pixels; the bound layout is unchanged.
        LayoutSpec pixelLayout = centeredFitRegions.Count == 0 ? context.LayoutSpec : new LayoutSpec(
            context.LayoutSpec.Id, context.LayoutSpec.OutputAspectWidth, context.LayoutSpec.OutputAspectHeight,
            context.LayoutSpec.Regions.Where(region => !centeredFitRegions.Contains(region)).ToArray(),
            context.LayoutSpec.SafeZones);
        ResolvedLayout resolved = LayoutResolver.Resolve(pixelLayout, context.Profile.Width, context.Profile.Height);
        int nextInputIndex = 0;
        string[] fontDirectories = fonts.Select(font =>
            Path.GetDirectoryName(font.VerificationRequest.FilePath)!).Distinct(StringComparer.Ordinal).ToArray();
        if (fontDirectories.Length > 1)
        {
            throw Failure(FfmpegCompilerFailureReason.InputBindingMismatch);
        }

        for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderNode node = ordered[ordinal];
            switch (node.Kind)
            {
                case RenderNodeKind.Video:
                    {
                        int inputIndex = nextInputIndex++;
                        if (frameExtraction is null)
                            AddTimedInput(arguments, node, bindings[node.ArtifactInputId!]);
                        else
                        {
                            // Decode from the beginning: n is the actual source frame sequence,
                            // not a seek approximation or a frame of an already edited output.
                            arguments.Add("-i");
                            arguments.Add(bindings[node.ArtifactInputId!].VerificationRequest.FilePath);
                        }
                        ResolvedLayoutRegion region = ResolveRegion(resolved, node.RegionName!);
                        string label = string.Concat("v", ordinal.ToString(CultureInfo.InvariantCulture));
                        FfmpegVideoGeometryBinding? geometry = videoGeometry.SingleOrDefault(item => item.NodeId == node.Id);
                        filters.Add(geometry is null
                            ? BuildVideoFilter(inputIndex, node, region, context.Profile, label, encoderSettings?.SourceColor)
                            : BuildGeometryFilter(inputIndex, node, region, resolved, context, geometry,
                                centeredFitNodes.Contains(node.Id), label, frameExtraction, encoderSettings?.SourceColor ?? frameExtraction?.SourceColor));
                        streamLabels[node.Id] = label;
                        break;
                    }

                case RenderNodeKind.Audio:
                    {
                        int inputIndex = nextInputIndex++;
                        AddTimedInput(arguments, node, bindings[node.ArtifactInputId!]);
                        string label = string.Concat("a", ordinal.ToString(CultureInfo.InvariantCulture));
                        AddAudioFilter(string.Concat(
                            "[", inputIndex.ToString(CultureInfo.InvariantCulture), ":a:0]",
                            "aresample=", OutputAudioSampleRate.ToString(CultureInfo.InvariantCulture), ",",
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
                                fontDirectories.Length == 0 ? string.Empty
                                    : string.Concat(":fontsdir=", EscapeFilterOptionPath(fontDirectories[0])),
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

            int graphicOrdinal = 0;
            foreach (FfmpegAssGraphicBinding graphic in graphics.Where(item => item.TargetVisualNodeId == node.Id))
            {
                string label = FormattableString.Invariant($"metadata_{ordinal}_{graphicOrdinal++}");
                filters.Add(string.Concat("[", streamLabels[node.Id], "]ass=filename=",
                    EscapeFilterOptionPath(bindings[graphic.InputId].VerificationRequest.FilePath),
                    ":fontsdir=", EscapeFilterOptionPath(fontDirectories.Single()),
                    "[", label, "]"));
                // The pinned ass filter has no timeline-enable flag. Its exact
                // Dialogue timestamps govern visibility; do not append enable=.
                streamLabels[node.Id] = label;
            }
        }

        RenderNode outputNode = nodes[graph.OutputNodeId];
        RenderNode[] terminals = outputNode.InputNodeIds.Select(id => nodes[id]).ToArray();
        RenderNode visual = context.Profile.SubtitleBurnIn
            ? terminals.Single(static node => node.Kind == RenderNodeKind.Subtitle)
            : terminals.Single(static node => node.Kind is
                RenderNodeKind.Video or RenderNodeKind.Graphic or RenderNodeKind.Transition);
        RenderNode[] audio = terminals.Where(static node =>
            node.Kind == RenderNodeKind.Audio).OrderBy(static node => node.OutputRange.Start).ToArray();
        string? audioLabel = null;
        if (audio.Length > 0)
        {
            if (audio.Length > 1 && graph.Nodes.Any(static node =>
                node.Kind == RenderNodeKind.Transition && node.Transition!.Kind != TransitionKind.Cut))
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape);
            }

            RationalTime cursor = default;
            foreach (RenderNode cut in audio)
            {
                if (cut.OutputRange.Start != cursor)
                {
                    throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape);
                }

                cursor = cut.OutputRange.End;
            }

            if (cursor != outputNode.OutputRange.End)
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape);
            }

            audioLabel = streamLabels[audio[0].Id];
            if (audio.Length > 1)
            {
                audioLabel = "audio_cuts";
                AddAudioFilter(string.Concat(
                    string.Concat(audio.Select(cut => string.Concat("[", streamLabels[cut.Id], "]"))),
                    "concat=n=", audio.Length.ToString(CultureInfo.InvariantCulture),
                    ":v=0:a=1[", audioLabel, "]"));
            }

            if (music.Count > 0)
            {
                var musicLabels = new List<string>();
                for (int i = 0; i < music.Count; i++)
                {
                    FfmpegMusicBinding track = music[i];
                    int inputIndex = nextInputIndex++;
                    arguments.AddRange(["-t", FormatTime(track.OutputRange.Duration), "-i", track.VerificationRequest.FilePath]);
                    string label = FormattableString.Invariant($"card_music_{i}");
                    BigInteger delaySamples = (BigInteger)track.OutputRange.Start.Value * OutputAudioSampleRate / track.OutputRange.Start.Rate;
                    AddAudioFilter(FormattableString.Invariant($"[{inputIndex}:a:0]aresample=48000,aformat=channel_layouts={(encoderSettings!.AudioChannels == 1 ? "mono" : "stereo")},atrim=duration={FormatTime(track.OutputRange.Duration)},asetpts=PTS-STARTPTS,adelay={delaySamples}S:all=1,apad=whole_dur={FormatTime(outputNode.OutputRange.Duration)},atrim=duration={FormatTime(outputNode.OutputRange.Duration)}[{label}]"));
                    musicLabels.Add(label);
                }
                AddAudioFilter(string.Concat("[", audioLabel, "]", string.Concat(musicLabels.Select(static label => "[" + label + "]")),
                    "amix=inputs=", (music.Count + 1).ToString(CultureInfo.InvariantCulture),
                    ":duration=first:dropout_transition=0:normalize=0[card_music_mix]"));
                audioLabel = "card_music_mix";
            }

            if (encoderSettings is not null)
            {
                string next = "standard_audio";
                var filter = new StringBuilder().Append('[').Append(audioLabel)
                    .Append("]aformat=channel_layouts=").Append(encoderSettings.AudioChannels == 1 ? "mono" : "stereo");
                if (encoderSettings.Loudness is FfmpegLoudnessTarget loudness)
                {
                    filter.Append(FormattableString.Invariant(
                        $",loudnorm=I={loudness.IntegratedLufs}:TP={loudness.TruePeakDbtp}:LRA={loudness.RangeLu}"));
                    filter.Append(loudness.IsMeasured
                        ? FormattableString.Invariant(
                            $":measured_I={loudness.MeasuredIntegratedLufs}:measured_TP={loudness.MeasuredTruePeakDbtp}:measured_LRA={loudness.MeasuredRangeLu}:measured_thresh={loudness.MeasuredThresholdLufs}:offset={loudness.MeasuredOffsetDb}:linear=true")
                        : ":linear=false");
                    filter.Append(":print_format=json");
                }
                filter.Append(",aresample=48000,atrim=duration=").Append(FormatTime(outputNode.OutputRange.Duration))
                    .Append(",asetpts=PTS-STARTPTS[").Append(next).Append(']');
                AddAudioFilter(filter.ToString());
                audioLabel = next;
            }
        }
        RenderNode? subtitle = context.Profile.SubtitleBurnIn
            ? null
            : terminals.SingleOrDefault(static node => node.Kind == RenderNodeKind.Subtitle);
        string videoLabel = streamLabels[visual.Id];
        if (encoderSettings is not null)
        {
            filters.Add(string.Concat("[", videoLabel,
                "]yadif=mode=send_frame:parity=auto:deint=interlaced,setfield=prog,",
                "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,",
                "setparams=field_mode=prog:color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=limited[standard_video]"));
            videoLabel = "standard_video";
        }
        return new CompilationState(
            arguments,
            string.Join(";", loudnessMeasurement ? audioFilters : filters),
            string.Concat("[", videoLabel, "]"),
            audioLabel is null ? null : string.Concat("[", audioLabel, "]"),
            subtitle is null ? null : streamLabels[subtitle.Id],
            outputNode.OutputRange.Duration);
    }

    private static List<string> BuildArguments(
        RenderProfile profile,
        CompilationState state,
        string outputPath,
        FfmpegEncoderSettings? encoderSettings,
        string? scriptPath,
        bool extractFrame,
        bool loudnessMeasurement)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-v",
            encoderSettings?.Loudness is null ? "error" : "info",
        };
        if (encoderSettings?.Loudness is not null) arguments.Add("-nostats");
        arguments.AddRange(state.InputArguments);
        if (!string.IsNullOrEmpty(state.FilterScript))
        {
            arguments.Add(scriptPath is null ? "-filter_complex" : "-/filter_complex");
            arguments.Add(scriptPath ?? state.FilterScript);
        }

        if (loudnessMeasurement)
        {
            arguments.AddRange(["-map", state.AudioMap ?? throw Failure(FfmpegCompilerFailureReason.UnsupportedAudioShape),
                "-f", "null", "-n", outputPath]);
            return arguments;
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
        arguments.Add(extractFrame ? "png" : "h264_mf");
        arguments.Add("-pix_fmt");
        arguments.Add(extractFrame ? "rgb24" : "yuv420p");
        if (extractFrame)
        {
            arguments.AddRange(["-frames:v", "1", "-fps_mode", "passthrough", "-f", "image2", "-update", "1"]);
        }
        else
        {
            arguments.Add("-r");
            arguments.Add(string.Concat(profile.FrameRateNumerator.ToString(CultureInfo.InvariantCulture),
                "/", profile.FrameRateDenominator.ToString(CultureInfo.InvariantCulture)));
        }
        if (encoderSettings is not null)
        {
            // mfenc consumes AVCodecContext.profile (AV_PROFILE_H264_HIGH=100),
            // not libx264's private string-profile option. Keep the mapping closed.
            arguments.AddRange(["-profile:v", "100", "-b:v", encoderSettings.VideoBitrateBitsPerSecond.ToString(CultureInfo.InvariantCulture),
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
                "-bsf:v", "h264_metadata=video_full_range_flag=0:colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1"]);
        }
        if (state.AudioMap is not null)
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
            arguments.Add("-ar");
            arguments.Add(OutputAudioSampleRate.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-profile:a");
            arguments.Add("aac_low");
            if (encoderSettings is not null)
                arguments.AddRange(["-b:a", encoderSettings.AudioBitrateBitsPerSecond.ToString(CultureInfo.InvariantCulture),
                    "-ac", encoderSettings.AudioChannels.ToString(CultureInfo.InvariantCulture)]);
        }

        if (state.SubtitleMap is not null)
        {
            arguments.Add("-c:s");
            arguments.Add("mov_text");
        }

        arguments.Add("-map_metadata");
        arguments.Add("-1");
        if (encoderSettings is not null || extractFrame)
            arguments.AddRange(["-map_metadata:s", "-1", "-map_chapters", "-1"]);
        if (!extractFrame)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add("-t");
            arguments.Add(FormatTime(state.OutputDuration));
        }
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
        string label,
        FfmpegSourceColor? sourceColor = null)
    {
        var filter = new StringBuilder();
        _ = filter
            .Append('[').Append(inputIndex).Append(":v:0]")
            .Append("trim=duration=").Append(FormatTime(node.OutputRange.Duration))
            .Append(",setpts=PTS-STARTPTS,").Append(SourceColorFilter(sourceColor)).Append("scale=")
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

    private static string BuildGeometryFilter(
        int inputIndex,
        RenderNode node,
        ResolvedLayoutRegion region,
        ResolvedLayout resolved,
        RenderGraphValidationContext context,
        FfmpegVideoGeometryBinding binding,
        bool useCenteredSourceFit,
        string label,
        FfmpegFrameExtraction? frameExtraction = null,
        FfmpegSourceColor? sourceColor = null)
    {
        RenderProfile profile = context.Profile;
        SourceDisplayGeometry geometry = binding.Geometry;
        string selection = frameExtraction is null ? "trim=duration=" + FormatTime(node.OutputRange.Duration)
            : FormattableString.Invariant($"select=eq(n\\,{frameExtraction.SourceFrameSequence})");
        string prefix = FormattableString.Invariant(
            $"[{inputIndex}:v:0]{selection},setpts=PTS-STARTPTS,{SourceColorFilter(sourceColor)}scale={geometry.Width}:{geometry.Height}:flags=bilinear,setsar=1");
        string suffix = frameExtraction is null ? FormattableString.Invariant(
            $",fps={profile.FrameRateNumerator}/{profile.FrameRateDenominator},format=yuv420p[{label}]")
            : $",format=rgb24[{label}]";
        if (binding.Layout is not ManualLayoutPlan layout)
        {
            // Fit rather than stretch; both dimensions are even for yuv420p.
            return string.Concat(prefix, FormattableString.Invariant(
                $",scale={region.Rect.Width}:{region.Rect.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={region.Rect.Width}:{region.Rect.Height}:(ow-iw)/2:(oh-ih)/2:color=black,pad={profile.Width}:{profile.Height}:{region.Rect.X}:{region.Rect.Y}:color=black"), suffix);
        }

        string Placement(ManualSourcePlacement placement)
        {
            NormalizedRect crop = placement.Crop.Rect;
            int Pixel(RationalNumber value, int extent)
            {
                decimal product = (decimal)value.Numerator * extent / value.Denominator;
                if (product != decimal.Truncate(product))
                {
                    throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
                }

                return checked((int)product);
            }

            ResolvedLayoutRegion destination = ResolveRegion(resolved, placement.Destination.Region.Name);
            return FormattableString.Invariant(
                $"crop={Pixel(crop.Width, geometry.Width)}:{Pixel(crop.Height, geometry.Height)}:{Pixel(crop.X, geometry.Width)}:{Pixel(crop.Y, geometry.Height)}:exact=1,scale={destination.Rect.Width}:{destination.Rect.Height}:flags=bilinear,setsar=1");
        }

        if (layout.Mode == ManualLayoutMode.BlurredSourceBackground)
        {
            if (layout.SourcePlacements.Count != 2)
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
            }

            ManualSourcePlacement background = layout.SourcePlacements[0];
            ManualSourcePlacement foreground = layout.SourcePlacements[1];
            ResolvedLayoutRegion back = ResolveRegion(resolved, background.Destination.Region.Name);
            if (back.Rect.X != 0 || back.Rect.Y != 0 || back.Rect.Width != profile.Width
                || back.Rect.Height != profile.Height
                || background.Treatment != ManualSourceTreatment.BlurredBackground
                || foreground.Treatment != ManualSourceTreatment.Standard)
            {
                throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
            }

            string foregroundFilter;
            string overlay;
            if (useCenteredSourceFit)
            {
                // Same bounded, even-dimension fit as the non-cropped geometry path.
                // Do not round a manual rectangle or substitute a different source crop.
                foregroundFilter = FormattableString.Invariant(
                    $"crop={geometry.Width}:{geometry.Height}:0:0:exact=1,scale={profile.Width}:{profile.Height}:flags=bilinear:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1");
                overlay = "overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2:shortest=1";
            }
            else
            {
                ResolvedLayoutRegion front = ResolveRegion(resolved, foreground.Destination.Region.Name);
                foregroundFilter = Placement(foreground);
                overlay = FormattableString.Invariant($"overlay=x={front.Rect.X}:y={front.Rect.Y}:shortest=1");
            }

            return string.Concat(prefix, $",split=2[{label}b][{label}f];",
                $"[{label}b]", Placement(background), $",gblur=sigma=20[{label}bg];",
                $"[{label}f]", foregroundFilter, $"[{label}fg];",
                $"[{label}bg][{label}fg]", overlay, suffix);
        }

        if (layout.SourcePlacements.Count != 1)
        {
            throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
        }

        ManualSourcePlacement single = layout.SourcePlacements[0];
        ResolvedLayoutRegion target = ResolveRegion(resolved, single.Destination.Region.Name);
        return string.Concat(prefix, ",", Placement(single), FormattableString.Invariant(
            $",pad={profile.Width}:{profile.Height}:{target.Rect.X}:{target.Rect.Y}:color=black"), suffix);
    }

    private static void ValidateManualLayout(
        RenderNode node,
        LayoutSpec expected,
        FfmpegVideoGeometryBinding binding,
        ManualLayoutPlan layout)
    {
        LayoutSpec actual = layout.DerivedLayoutSpec;
        if (actual.Id != expected.Id
            || actual.OutputAspectWidth != expected.OutputAspectWidth
            || actual.OutputAspectHeight != expected.OutputAspectHeight
            || !actual.Regions.SequenceEqual(expected.Regions)
            || !actual.SafeZones.SequenceEqual(expected.SafeZones)
            || layout.Mode is not (ManualLayoutMode.FixedTemplatePlacement
                or ManualLayoutMode.SingleSubjectCrop or ManualLayoutMode.ImportantRegionZoom
                or ManualLayoutMode.BlurredSourceBackground)
            || layout.SourcePlacements.Any(placement =>
                !placement.SourceDisplayGeometry.Equals(binding.Geometry)
                || placement.Origin != node.Origin))
        {
            throw Failure(FfmpegCompilerFailureReason.UnsupportedGeometry);
        }
    }

    private static bool RequiresCenteredSourceFit(
        ManualLayoutPlan layout,
        SourceDisplayGeometry geometry,
        RenderProfile profile)
    {
        if (layout.Mode != ManualLayoutMode.BlurredSourceBackground || layout.SourcePlacements.Count != 2)
        {
            return false;
        }

        var one = new RationalNumber(1, 1);
        var full = new NormalizedRect(default, default, one, one);
        ManualSourcePlacement background = layout.SourcePlacements[0];
        ManualSourcePlacement foreground = layout.SourcePlacements[1];
        if (background.Destination.Region.Rect != full || foreground.Crop.Rect != full
            || background.SourceAssetId != foreground.SourceAssetId
            || background.Treatment != ManualSourceTreatment.BlurredBackground
            || foreground.Treatment != ManualSourceTreatment.Standard)
        {
            return false;
        }

        // Compare the declared placement with the exact centered contain rectangle,
        // not a tolerance or a name-based convention. Int32 dimension products and
        // twice those products fit in Int64; no floating-point conversion is needed.
        long widthProduct = (long)geometry.Width * profile.Height;
        long heightProduct = (long)geometry.Height * profile.Width;
        NormalizedRect expected = widthProduct >= heightProduct
            ? new NormalizedRect(default, new RationalNumber(widthProduct - heightProduct, 2 * widthProduct),
                one, new RationalNumber(heightProduct, widthProduct))
            : new NormalizedRect(new RationalNumber(heightProduct - widthProduct, 2 * heightProduct), default,
                new RationalNumber(widthProduct, heightProduct), one);
        NormalizedRect destination = foreground.Destination.Region.Rect;
        return destination == expected
            && (BigInteger)destination.Width.Numerator * profile.Width >= 2 * (BigInteger)destination.Width.Denominator
            && (BigInteger)destination.Height.Numerator * profile.Height >= 2 * (BigInteger)destination.Height.Denominator
            && (!IsIntegralPixelCoordinate(destination.X, profile.Width)
                || !IsIntegralPixelCoordinate(destination.Y, profile.Height)
                || !IsIntegralPixelCoordinate(destination.Width, profile.Width)
                || !IsIntegralPixelCoordinate(destination.Height, profile.Height));
    }

    private static bool IsIntegralPixelCoordinate(RationalNumber value, int extent) =>
        ((BigInteger)value.Numerator * extent) % value.Denominator == 0;

    private static string SourceColorFilter(FfmpegSourceColor? source)
    {
        if (source is null) return string.Empty; // Backwards-compatible legacy compilation.
        string range = source.FullRange ? "pc" : "tv";
        if (source.ToneMapping)
        {
            string transfer = source.Model == FfmpegSourceColorModel.Bt2020Pq ? "smpte2084" : "arib-std-b67";
            // Linearize BEFORE any 8-bit conversion. Peak is taken from frame
            // metadata/the filter's bounded default, never invented as measured.
            return $"zscale=pin=2020:tin={transfer}:min=2020_ncl:rin={(source.FullRange ? "full" : "limited")}:t=linear:npl=100," +
                "format=gbrpf32le,zscale=p=709,tonemap=tonemap=hable:desat=0,zscale=t=709:m=709:r=limited,format=yuv420p,";
        }
        string input = source.Model switch
        {
            FfmpegSourceColorModel.Bt709Sdr => "bt709",
            FfmpegSourceColorModel.Bt601525Sdr => "smpte170m",
            FfmpegSourceColorModel.Bt601625Sdr => "bt470bg",
            FfmpegSourceColorModel.Bt2020Sdr => "bt2020",
            _ => throw Failure(FfmpegCompilerFailureReason.CompilationFailed),
        };
        return $"colorspace=iall={input}:all=bt709:irange={range}:range=tv:format=yuv420p,";
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