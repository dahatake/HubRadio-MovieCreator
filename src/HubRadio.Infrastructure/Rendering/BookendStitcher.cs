using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Rendering;

/// <summary>
/// FR-035: writes a NEW MP4 of Opening → main → Closing (hard cuts) normalized to the
/// main video's canvas, frame rate and channel count, plus a <c>.stitch.json</c>
/// traceability manifest. Inputs are only read; the result is promoted with
/// non-overwriting moves after ffprobe verification. Nothing is registered in a store.
/// </summary>
public sealed class BookendStitcher
{
    public const string ManifestKind = "hubradio.bookendStitch";
    public const string ManifestSchemaVersion = "1.0.0";
    public const decimal DurationTolerance = 0.2m;
    public const int MinimumCanvasSide = 16;
    public const int MaximumCanvasSide = 4096;
    public const int OutputSampleRate = 48000;

    internal static readonly string[] AllowedInputExtensions = [".mp4", ".m4v", ".mov", ".mkv", ".webm"];
    internal static readonly string[] AllowedFormatNames = ["mov", "mp4", "matroska", "webm"];
    private const decimal MaximumFramesPerSecond = 120m;

    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> _run;
    private readonly Func<BinaryVerificationRequest, CancellationToken, Task<bool>> _verifyTool;
    private readonly Func<DateTimeOffset> _utcNow;

    public BookendStitcher()
        : this(new ProcessRunner().RunAsync, VerifyPinnedAsync, static () => DateTimeOffset.UtcNow)
    {
    }

    // Test seam: production always uses ProcessRunner and BinaryVerifier.
    internal BookendStitcher(
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run,
        Func<BinaryVerificationRequest, CancellationToken, Task<bool>> verifyTool,
        Func<DateTimeOffset> utcNow)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _verifyTool = verifyTool ?? throw new ArgumentNullException(nameof(verifyTool));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    internal static string FfprobeSha256 => FfprobeClient.CanonicalFfprobeSha256;

    internal static string FfmpegSha256 => FfmpegCompiler.FixedFfmpegSha256;

    public static string ManifestPathFor(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + ".stitch.json");

    public async Task<BookendStitchResult> StitchAsync(BookendStitchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OpeningPath is null && request.ClosingPath is null)
            throw Fail(BookendStitchFailure.Usage, "At least one of --opening or --closing is required.");
        if (request.ProcessTimeout <= TimeSpan.Zero || request.ProcessTimeout > ProcessRunRequest.MaximumTimeout)
            throw Fail(BookendStitchFailure.Usage, "The process timeout is out of range.");

        string output = ResolveOutput(request.OutputPath);
        string manifest = ManifestPathFor(output);
        string outputDirectory = Path.GetDirectoryName(output)!;

        var inputs = new List<(BookendSegmentRole Role, string Path)>();
        if (request.OpeningPath is not null) inputs.Add((BookendSegmentRole.Opening, ResolveInput(request.OpeningPath, BookendSegmentRole.Opening)));
        inputs.Add((BookendSegmentRole.Main, ResolveInput(request.MainPath, BookendSegmentRole.Main)));
        if (request.ClosingPath is not null) inputs.Add((BookendSegmentRole.Closing, ResolveInput(request.ClosingPath, BookendSegmentRole.Closing)));

        string ffmpeg = await VerifyToolAsync("ffmpeg", request.FfmpegPath, FfmpegSha256, FfmpegCompiler.FixedFfmpegByteSize,
            cancellationToken).ConfigureAwait(false);
        string ffprobe = await VerifyToolAsync("ffprobe", request.FfprobePath, FfprobeSha256, FfprobeClient.CanonicalFfprobeByteSize,
            cancellationToken).ConfigureAwait(false);

        var handles = new List<FileStream>();
        var owned = new List<string>();
        bool succeeded = false;
        try
        {
            var segments = new List<(BookendSegmentRole Role, string Path, string Sha256, long Size, ProbedMedia Media, decimal Duration)>();
            foreach ((BookendSegmentRole role, string path) in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream handle = OpenInput(path, role);
                handles.Add(handle);
                (string sha256, long size) = await HashAsync(handle, cancellationToken).ConfigureAwait(false);
                ProbedMedia media = await ProbeAsync(ffprobe, path, outputDirectory, request.ProcessTimeout,
                    BookendStitchFailure.InputUnsupported, cancellationToken).ConfigureAwait(false);
                if (!media.FormatNames.Any(name => AllowedFormatNames.Contains(name, StringComparer.Ordinal)) || media.Video is null)
                    throw Fail(BookendStitchFailure.InputUnsupported, $"The {Role(role)} input is not a supported video container.");
                decimal duration = media.Video.Duration ?? media.FormatDuration
                    ?? throw Fail(BookendStitchFailure.InputInvalidDuration, $"The {Role(role)} input has no positive duration.");
                segments.Add((role, path, sha256, size, media, duration));
            }

            ProbedMedia main = segments.Single(item => item.Role == BookendSegmentRole.Main).Media;
            (int width, int height) = Canvas(main.Video!);
            (long fpsNumerator, long fpsDenominator) = FrameRate(main.Video!);
            string frameRate = string.Create(CultureInfo.InvariantCulture, $"{fpsNumerator}/{fpsDenominator}");
            int channels = main.Audio?.Channels == 1 ? 1 : 2;

            var planned = new List<BookendStitchSegment>();
            decimal cursor = 0;
            foreach (var item in segments)
            {
                planned.Add(new BookendStitchSegment(item.Role, Path.GetFileName(item.Path), item.Sha256, item.Size,
                    item.Media.Audio is not null, item.Duration, cursor, cursor + item.Duration));
                cursor += item.Duration;
            }

            string token = Guid.NewGuid().ToString("N");
            string temporaryOutput = Path.Combine(outputDirectory, $".{Path.GetFileName(output)}.{token}.partial");
            string temporaryManifest = Path.Combine(outputDirectory, $".{Path.GetFileName(manifest)}.{token}.partial");
            owned.Add(temporaryOutput);
            owned.Add(temporaryManifest);

            IReadOnlyList<string> arguments = BuildFfmpegArguments(segments.Select(static item => item.Path).ToArray(),
                planned.Select(static item => item.SourceDurationSeconds).ToArray(),
                segments.Select(static item => item.Media.Audio is not null).ToArray(),
                width, height, frameRate, (decimal)fpsNumerator / fpsDenominator, channels, temporaryOutput);
            ProcessRunResult encoded = await RunAsync(new ProcessRunRequest(ffmpeg, arguments, outputDirectory,
                request.ProcessTimeout, maxCapturedBytes: 1024 * 1024), cancellationToken).ConfigureAwait(false);
            if (encoded.ExitCode != 0 || encoded.Truncated || !File.Exists(temporaryOutput))
                throw Fail(BookendStitchFailure.EncodeFailed, "ffmpeg failed to encode the stitched video.");

            ProbedMedia measured = await ProbeAsync(ffprobe, temporaryOutput, outputDirectory, request.ProcessTimeout,
                BookendStitchFailure.OutputVerificationFailed, cancellationToken).ConfigureAwait(false);
            decimal measuredDuration = measured.FormatDuration ?? 0;
            if (measured.Video is not { CodecName: "h264" } video || video.Width != width || video.Height != height
                || measured.Audio is not { CodecName: "aac" } audio || audio.SampleRate != OutputSampleRate || audio.Channels != channels
                || Math.Abs(measuredDuration - cursor) > DurationTolerance)
                throw Fail(BookendStitchFailure.OutputVerificationFailed, "The stitched video does not match the planned output.");

            (string outputSha256, long outputSize) = await HashFileAsync(temporaryOutput, cancellationToken).ConfigureAwait(false);
            byte[] manifestBytes = WriteManifest(_utcNow(), Path.GetFileName(output), outputSha256, outputSize, measuredDuration,
                width, height, frameRate, channels, planned);
            await File.WriteAllBytesAsync(temporaryManifest, manifestBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            MoveNew(temporaryOutput, output);
            owned.Add(output);
            MoveNew(temporaryManifest, manifest);
            succeeded = true;
            return new BookendStitchResult(output, manifest, width, height, frameRate, channels, cursor, measuredDuration, planned);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw Fail(BookendStitchFailure.Cancelled, "The operation was cancelled.", exception);
        }
        finally
        {
            foreach (FileStream handle in handles) await handle.DisposeAsync().ConfigureAwait(false);
            if (!succeeded)
            {
                foreach (string path in owned)
                {
                    try { File.Delete(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal static IReadOnlyList<string> BuildFfmpegArguments(IReadOnlyList<string> inputPaths, IReadOnlyList<decimal> durations,
        IReadOnlyList<bool> hasAudio, int width, int height, string frameRate, decimal framesPerSecond, int channels, string outputPath)
    {
        string layout = channels == 1 ? "mono" : "stereo";
        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();
        for (int index = 0; index < inputPaths.Count; index++)
        {
            string duration = Seconds(durations[index]);
            graph.Append(CultureInfo.InvariantCulture,
                $"[{index}:v:0]setpts=PTS-STARTPTS,scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1,")
                .Append(CultureInfo.InvariantCulture,
                $"scale=w={width}:h={height}:force_original_aspect_ratio=decrease:force_divisible_by=2:out_color_matrix=bt709:out_range=tv,")
                .Append(CultureInfo.InvariantCulture, $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,")
                .Append(CultureInfo.InvariantCulture, $"fps={frameRate},format=yuv420p,tpad=stop_mode=clone:stop_duration={duration},")
                .Append(CultureInfo.InvariantCulture, $"trim=duration={duration},setpts=PTS-STARTPTS[v{index}];");
            graph.Append(hasAudio[index]
                ? string.Create(CultureInfo.InvariantCulture,
                    $"[{index}:a:0]aresample={OutputSampleRate},aformat=sample_fmts=fltp:sample_rates={OutputSampleRate}:channel_layouts={layout},asetpts=PTS-STARTPTS,apad,")
                : string.Create(CultureInfo.InvariantCulture, $"anullsrc=channel_layout={layout}:sample_rate={OutputSampleRate},"))
                .Append(CultureInfo.InvariantCulture, $"atrim=duration={duration},asetpts=PTS-STARTPTS[a{index}];");
            concatInputs.Append(CultureInfo.InvariantCulture, $"[v{index}][a{index}]");
        }

        graph.Append(concatInputs).Append(CultureInfo.InvariantCulture, $"concat=n={inputPaths.Count}:v=1:a=1[vc][aout];")
            .Append("[vc]setparams=field_mode=prog:color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=limited[vout]");

        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        foreach (string path in inputPaths) arguments.AddRange(["-protocol_whitelist", "file", "-i", path]);
        arguments.AddRange([
            "-filter_complex", graph.ToString(), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "h264_mf", "-profile:v", "100", "-b:v", framesPerSecond > 30 ? "12000000" : "8000000",
            "-pix_fmt", "yuv420p", "-r", frameRate,
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
            "-bsf:v", "h264_metadata=video_full_range_flag=0:colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1",
            "-c:a", "aac", "-profile:a", "aac_low", "-ar", OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-b:a", channels == 1 ? "128000" : "384000",
            "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", "-f", "mp4", "-n", outputPath,
        ]);
        return arguments;
    }

    internal static (int Width, int Height) Canvas(ProbedVideoStream video)
    {
        long displayWidth = video.SampleAspectRatio is (long numerator, long denominator)
            ? (long)Math.Floor((decimal)video.Width * numerator / denominator) : video.Width;
        long width = displayWidth - displayWidth % 2;
        long height = video.Height - video.Height % 2;
        if (width < MinimumCanvasSide || width > MaximumCanvasSide || height < MinimumCanvasSide || height > MaximumCanvasSide)
            throw Fail(BookendStitchFailure.UnsupportedGeometry, "The main video size is outside 16-4096 px.");
        return ((int)width, (int)height);
    }

    internal static (long Numerator, long Denominator) FrameRate(ProbedVideoStream video)
    {
        (long Numerator, long Denominator) rate = video.AverageFrameRate ?? video.RealFrameRate
            ?? throw Fail(BookendStitchFailure.UnsupportedFrameRate, "The main video frame rate is unknown.");
        if ((decimal)rate.Numerator / rate.Denominator > MaximumFramesPerSecond)
            throw Fail(BookendStitchFailure.UnsupportedFrameRate, "The main video frame rate exceeds 120 fps.");
        return rate;
    }

    internal static decimal RoundSeconds(decimal value) =>
        decimal.Parse(Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

    private static string Seconds(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static byte[] WriteManifest(DateTimeOffset createdAt, string outputName, string outputSha256, long outputSize,
        decimal outputDuration, int width, int height, string frameRate, int channels, IReadOnlyList<BookendStitchSegment> segments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", ManifestSchemaVersion);
            writer.WriteString("kind", ManifestKind);
            writer.WriteString("createdAtUtc", createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("publicationScope", "internalOnly");
            writer.WriteStartObject("tool");
            writer.WriteString("ffmpegSha256", FfmpegSha256);
            writer.WriteString("ffprobeSha256", FfprobeSha256);
            writer.WriteEndObject();
            writer.WriteStartObject("output");
            writer.WriteString("fileName", outputName);
            writer.WriteString("sha256", outputSha256);
            writer.WriteNumber("byteSize", outputSize);
            writer.WriteNumber("durationSeconds", RoundSeconds(outputDuration));
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteString("frameRate", frameRate);
            writer.WriteNumber("audioChannels", channels);
            writer.WriteNumber("audioSampleRate", OutputSampleRate);
            writer.WriteEndObject();
            writer.WriteStartArray("segments");
            foreach (BookendStitchSegment segment in segments)
            {
                writer.WriteStartObject();
                writer.WriteString("role", segment.Role.ToString().ToLowerInvariant());
                writer.WriteString("fileName", segment.FileName);
                writer.WriteString("sha256", segment.Sha256);
                writer.WriteNumber("byteSize", segment.ByteSize);
                writer.WriteBoolean("hadAudio", segment.HadAudio);
                writer.WriteNumber("sourceDurationSeconds", RoundSeconds(segment.SourceDurationSeconds));
                writer.WriteNumber("outputStartSeconds", RoundSeconds(segment.OutputStartSeconds));
                writer.WriteNumber("outputEndSeconds", RoundSeconds(segment.OutputEndSeconds));
                writer.WriteString("rightsStatus", "unconfirmed");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private async Task<ProbedMedia> ProbeAsync(string ffprobe, string path, string workingDirectory, TimeSpan timeout,
        BookendStitchFailure failure, CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunAsync(new ProcessRunRequest(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-show_streams", "-show_format", "-of", "json", path],
            workingDirectory, timeout, maxCapturedBytes: 4 * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated)
            throw Fail(failure, failure == BookendStitchFailure.InputUnsupported
                ? "ffprobe could not read an input video." : "ffprobe could not read the stitched video.");
        return ProbedMedia.Parse(result.StandardOutput)
            ?? throw Fail(failure, "ffprobe returned malformed output.");
    }

    private async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _run(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw Fail(BookendStitchFailure.Timeout, "A media process exceeded the timeout.", exception);
        }
        catch (ProcessStartException exception)
        {
            throw Fail(BookendStitchFailure.EncodeFailed, "A media process could not be started.", exception);
        }
    }

    private async Task<string> VerifyToolAsync(string name, string rawPath, string sha256, long byteSize, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = Path.GetFullPath(rawPath);
            if (IsUnc(rawPath) || IsUnc(path) || !File.Exists(path))
                throw Fail(BookendStitchFailure.ToolUnverified, $"{name} was not found.");
            if (await _verifyTool(new BinaryVerificationRequest(name, path, new ContentDigest(sha256), byteSize), cancellationToken)
                    .ConfigureAwait(false))
                return path;
        }
        catch (Exception exception) when (exception is not (BookendStitchException or OperationCanceledException))
        {
            throw Fail(BookendStitchFailure.ToolUnverified, $"{name} could not be verified.", exception);
        }

        throw Fail(BookendStitchFailure.ToolUnverified, $"{name} does not match the pinned build.");
    }

    private static async Task<bool> VerifyPinnedAsync(BinaryVerificationRequest request, CancellationToken cancellationToken) =>
        (await new BinaryVerifier().VerifyAsync(request, cancellationToken).ConfigureAwait(false)).IsVerified;

    private static string ResolveOutput(string raw)
    {
        string path = Resolve(raw, BookendStitchFailure.OutputPathInvalid, "output");
        if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw Fail(BookendStitchFailure.OutputPathInvalid, "The output must be an .mp4 file.");
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            throw Fail(BookendStitchFailure.OutputDirectoryMissing, "The output directory does not exist.");
        if (File.Exists(path) || Directory.Exists(path) || File.Exists(ManifestPathFor(path)) || Directory.Exists(ManifestPathFor(path)))
            throw Fail(BookendStitchFailure.OutputExists, "The output or its manifest already exists.");
        return path;
    }

    private static string ResolveInput(string raw, BookendSegmentRole role)
    {
        string path = Resolve(raw, BookendStitchFailure.InputPathInvalid, Role(role));
        if (!File.Exists(path))
            throw Fail(BookendStitchFailure.InputNotFound, $"The {Role(role)} input was not found.");
        if (!AllowedInputExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw Fail(BookendStitchFailure.InputUnsupported, $"The {Role(role)} input extension is not supported.");
        return path;
    }

    private static string Resolve(string raw, BookendStitchFailure failure, string role)
    {
        if (string.IsNullOrWhiteSpace(raw) || IsUnc(raw))
            throw Fail(failure, $"The {role} path must be a local path.");
        string path;
        try { path = Path.GetFullPath(raw); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Fail(failure, $"The {role} path is invalid.", exception);
        }

        if (IsUnc(path) || !Path.IsPathFullyQualified(path))
            throw Fail(failure, $"The {role} path must be a local path.");
        return path;
    }

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static FileStream OpenInput(string path, BookendSegmentRole role)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Fail(BookendStitchFailure.InputNotFound, $"The {Role(role)} input was not found.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Fail(BookendStitchFailure.InputUnsupported, $"The {Role(role)} input cannot be read.", exception);
        }
    }

    private static async Task<(string Sha256, long Size)> HashAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (Convert.ToHexStringLower(hash), stream.Length);
    }

    private static async Task<(string Sha256, long Size)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await HashAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static void MoveNew(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException exception) when (File.Exists(destination) || Directory.Exists(destination))
        {
            throw Fail(BookendStitchFailure.OutputExists, "The output or its manifest appeared during processing.", exception);
        }
    }

    private static string Role(BookendSegmentRole role) => role.ToString().ToLowerInvariant();

    private static BookendStitchException Fail(BookendStitchFailure failure, string message, Exception? inner = null) =>
        new(failure, message, inner);
}
