using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

public enum ContainerSignature
{
    IsoBaseMediaFtyp,
    Ebml,
    RiffAvi,
    Flv,
    MpegTransportStream,
    Unknown,
}

public enum MediaStreamKind
{
    Video,
    Audio,
    Other,
}

public sealed class ProbedMediaStream
{
    public ProbedMediaStream(
        int index,
        MediaStreamKind kind,
        string codecName,
        string? profile,
        int? width,
        int? height,
        string? pixelFormat,
        RationalNumber? nominalFrameRate,
        RationalNumber? averageFrameRate,
        RationalNumber timeBase,
        decimal? startSeconds,
        decimal? durationSeconds,
        int? sampleRate,
        int? channels,
        string? channelLayout)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                "A probed stream index must not be negative.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "A probed stream kind must be defined.");
        }

        codecName = RequireTrimmedText(
            codecName,
            nameof(codecName),
            "A probed codec name");
        profile = RequireOptionalTrimmedText(
            profile,
            nameof(profile),
            "A probed codec profile");
        pixelFormat = RequireOptionalTrimmedText(
            pixelFormat,
            nameof(pixelFormat),
            "A probed pixel format");
        channelLayout = RequireOptionalTrimmedText(
            channelLayout,
            nameof(channelLayout),
            "A probed channel layout");

        if (kind == MediaStreamKind.Video)
        {
            if (width is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "A video stream width must be positive.");
            }

            if (height is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(height),
                    "A video stream height must be positive.");
            }

            ValidatePositiveRate(
                nominalFrameRate,
                nameof(nominalFrameRate),
                required: true);
            ValidatePositiveRate(
                averageFrameRate,
                nameof(averageFrameRate),
                required: true);
        }
        else
        {
            if (width is not null || height is not null || pixelFormat is not null)
            {
                throw new ArgumentException(
                    "Non-video streams must not contain video-only metadata.",
                    nameof(width));
            }

            ValidatePositiveRate(
                nominalFrameRate,
                nameof(nominalFrameRate),
                required: false);
            ValidatePositiveRate(
                averageFrameRate,
                nameof(averageFrameRate),
                required: false);
        }

        if (timeBase.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeBase),
                "A probed stream time base must be positive.");
        }

        if (durationSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "A probed stream duration must not be negative.");
        }

        if (kind == MediaStreamKind.Audio)
        {
            if (sampleRate is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleRate),
                    "An audio sample rate must be positive when present.");
            }

            if (channels is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channels),
                    "An audio channel count must be positive when present.");
            }
        }
        else if (sampleRate is not null || channels is not null || channelLayout is not null)
        {
            throw new ArgumentException(
                "Non-audio streams must not contain audio-only metadata.",
                nameof(sampleRate));
        }

        Index = index;
        Kind = kind;
        CodecName = codecName;
        Profile = profile;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        NominalFrameRate = nominalFrameRate;
        AverageFrameRate = averageFrameRate;
        TimeBase = timeBase;
        StartSeconds = startSeconds;
        DurationSeconds = durationSeconds;
        SampleRate = sampleRate;
        Channels = channels;
        ChannelLayout = channelLayout;
    }

    public int Index { get; }

    public MediaStreamKind Kind { get; }

    public string CodecName { get; }

    public string? Profile { get; }

    public int? Width { get; }

    public int? Height { get; }

    public string? PixelFormat { get; }

    public RationalNumber? NominalFrameRate { get; }

    public RationalNumber? AverageFrameRate { get; }

    public RationalNumber TimeBase { get; }

    public decimal? StartSeconds { get; }

    public decimal? DurationSeconds { get; }

    public int? SampleRate { get; }

    public int? Channels { get; }

    public string? ChannelLayout { get; }

    private static string RequireTrimmedText(
        string value,
        string parameterName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must be trimmed and nonblank.",
                parameterName);
        }

        return value;
    }

    private static string? RequireOptionalTrimmedText(
        string? value,
        string parameterName,
        string description) =>
        value is null
            ? null
            : RequireTrimmedText(value, parameterName, description);

    private static void ValidatePositiveRate(
        RationalNumber? rate,
        string parameterName,
        bool required)
    {
        if (rate is null)
        {
            if (required)
            {
                throw new ArgumentException(
                    "A video stream frame rate is required.",
                    parameterName);
            }

            return;
        }

        if (rate.Value.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A probed stream frame rate must be positive when present.");
        }
    }
}

public sealed class MediaProbeResult
{
    internal const string UnrecognizedContainerSignatureWarning =
        "unrecognized-container-signature";
    internal const string SignatureFormatMismatchWarning =
        "signature-format-mismatch";
    internal const string AudioTrackAbsentWarning =
        "audio-track-absent";
    internal const string MultipleVideoStreamsWarning =
        "multiple-video-streams";
    internal const string MultipleAudioStreamsWarning =
        "multiple-audio-streams";
    internal const string VariableRateFieldsDifferWarning =
        "variable-rate-fields-differ";

    private static readonly HashSet<string> KnownWarnings =
    [
        UnrecognizedContainerSignatureWarning,
        SignatureFormatMismatchWarning,
        AudioTrackAbsentWarning,
        MultipleVideoStreamsWarning,
        MultipleAudioStreamsWarning,
        VariableRateFieldsDifferWarning,
    ];

    public MediaProbeResult(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        ContainerSignature containerSignature,
        IReadOnlyList<string> formatNames,
        string? formatLongName,
        decimal? durationSeconds,
        long? declaredByteSize,
        long? bitRate,
        int? probeScore,
        IReadOnlyList<ProbedMediaStream> streams,
        IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(formatNames);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(warnings);

        if (string.IsNullOrWhiteSpace(sourceFileName) ||
            sourceFileName.Contains('/', StringComparison.Ordinal) ||
            sourceFileName.Contains('\\', StringComparison.Ordinal) ||
            sourceFileName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A probed source file name must be a nonblank leaf name without control characters.",
                nameof(sourceFileName));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                "A probed media byte size must be positive.");
        }

        if (!Enum.IsDefined(containerSignature))
        {
            throw new ArgumentOutOfRangeException(
                nameof(containerSignature),
                "A container signature must be defined.");
        }

        if (formatNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one probed format name is required.",
                nameof(formatNames));
        }

        string[] copiedFormatNames = new string[formatNames.Count];
        for (int index = 0; index < copiedFormatNames.Length; index++)
        {
            string? formatName = formatNames[index];
            if (string.IsNullOrWhiteSpace(formatName) ||
                !string.Equals(formatName, formatName.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Probed format names must contain trimmed nonblank tokens.",
                    nameof(formatNames));
            }

            copiedFormatNames[index] = formatName;
        }

        if (formatLongName is not null &&
            (string.IsNullOrWhiteSpace(formatLongName) ||
             !string.Equals(formatLongName, formatLongName.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A probed long format name must be trimmed and nonblank when present.",
                nameof(formatLongName));
        }

        if (durationSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "A probed format duration must not be negative.");
        }

        if (declaredByteSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredByteSize),
                "A declared media byte size must be positive when present.");
        }

        if (declaredByteSize is not null && declaredByteSize != byteSize)
        {
            throw new ArgumentException(
                "A declared media byte size must equal the staged media byte size.",
                nameof(declaredByteSize));
        }

        if (bitRate is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitRate),
                "A probed bit rate must be positive when present.");
        }

        if (probeScore is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeScore),
                "A probe score must be between 0 and 100 when present.");
        }

        if (streams.Count == 0)
        {
            throw new ArgumentException(
                "At least one probed media stream is required.",
                nameof(streams));
        }

        ProbedMediaStream[] copiedStreams = new ProbedMediaStream[streams.Count];
        HashSet<int> streamIndices = [];
        int videoStreamCount = 0;
        for (int index = 0; index < copiedStreams.Length; index++)
        {
            ProbedMediaStream? stream = streams[index];
            if (stream is null || !streamIndices.Add(stream.Index))
            {
                throw new ArgumentException(
                    "Probed media streams must be nonnull and have unique indices.",
                    nameof(streams));
            }

            copiedStreams[index] = stream;
            if (stream.Kind == MediaStreamKind.Video)
            {
                videoStreamCount++;
            }
        }

        if (videoStreamCount == 0)
        {
            throw new ArgumentException(
                "At least one video stream is required.",
                nameof(streams));
        }

        SortedSet<string> normalizedWarnings = new(StringComparer.Ordinal);
        foreach (string? warning in warnings)
        {
            if (warning is null || !KnownWarnings.Contains(warning))
            {
                throw new ArgumentException(
                    "A media probe warning must be a recognized stable warning string.",
                    nameof(warnings));
            }

            normalizedWarnings.Add(warning);
        }

        CandidateId = candidateId;
        ProjectId = projectId;
        JobId = jobId;
        SourceFileName = sourceFileName;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        ContainerSignature = containerSignature;
        FormatNames = Array.AsReadOnly(copiedFormatNames);
        FormatLongName = formatLongName;
        DurationSeconds = durationSeconds;
        DeclaredByteSize = declaredByteSize;
        BitRate = bitRate;
        ProbeScore = probeScore;
        Streams = Array.AsReadOnly(copiedStreams);
        Warnings = Array.AsReadOnly(normalizedWarnings.ToArray());
    }

    public EntityId<AssetIdTag> CandidateId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> JobId { get; }

    public string SourceFileName { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public ContainerSignature ContainerSignature { get; }

    public IReadOnlyList<string> FormatNames { get; }

    public string? FormatLongName { get; }

    public decimal? DurationSeconds { get; }

    public long? DeclaredByteSize { get; }

    public long? BitRate { get; }

    public int? ProbeScore { get; }

    public IReadOnlyList<ProbedMediaStream> Streams { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public enum MediaProbeFailureReason
{
    ToolVerificationFailed,
    StagedFileInvalid,
    ProcessTimedOut,
    ProcessFailed,
    OutputTruncated,
    InvalidJson,
    InvalidMetadata,
    NoVideoStream,
}

public sealed class MediaProbeException : Exception
{
    internal MediaProbeException(MediaProbeFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public MediaProbeFailureReason Reason { get; }

    private static string GetStableMessage(MediaProbeFailureReason reason) =>
        reason switch
        {
            MediaProbeFailureReason.ToolVerificationFailed =>
                "The media probe tool could not be verified.",
            MediaProbeFailureReason.StagedFileInvalid =>
                "The staged media file is invalid.",
            MediaProbeFailureReason.ProcessTimedOut =>
                "The media probe process exceeded its timeout.",
            MediaProbeFailureReason.ProcessFailed =>
                "The media probe process failed.",
            MediaProbeFailureReason.OutputTruncated =>
                "The media probe output exceeded the capture limit.",
            MediaProbeFailureReason.InvalidJson =>
                "The media probe output is not valid JSON.",
            MediaProbeFailureReason.InvalidMetadata =>
                "The media probe metadata is invalid.",
            MediaProbeFailureReason.NoVideoStream =>
                "The media probe found no video stream.",
            _ => "The media probe failed.",
        };
}

public sealed class FfprobeClient
{
    internal const string CanonicalPackageId = "BtbN.FFmpeg.LGPL.8.1";
    internal const string CanonicalPackageVersion = "8.1.2-20260731";
    internal const long CanonicalFfprobeByteSize = 113330688;
    internal const string CanonicalFfprobeSha256 =
        "4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2";

    private const int SignaturePrefixLength = 377;
    private const int FileBufferSize = 64 * 1024;
    private const string ToolComponentIdentity =
        "tool.ffprobe.BtbN.FFmpeg.LGPL.8.1.8.1.2-20260731";
    private const string StagedMediaComponentIdentity = "media.staged-input";
    private const string ShowEntries =
        "format=format_name,format_long_name,duration,size,bit_rate,probe_score:" +
        "stream=index,codec_type,codec_name,profile,width,height,pix_fmt," +
        "r_frame_rate,avg_frame_rate,time_base,start_time,duration," +
        "sample_rate,channels,channel_layout";

    private static readonly ContentDigest CanonicalFfprobeDigest =
        new(CanonicalFfprobeSha256);

    private static readonly HashSet<string> StreamPropertyNames =
    [
        "index",
        "codec_type",
        "codec_name",
        "profile",
        "width",
        "height",
        "pix_fmt",
        "r_frame_rate",
        "avg_frame_rate",
        "time_base",
        "start_time",
        "duration",
        "sample_rate",
        "channels",
        "channel_layout",
    ];

    private static readonly HashSet<string> FormatPropertyNames =
    [
        "format_name",
        "format_long_name",
        "duration",
        "size",
        "bit_rate",
        "probe_score",
    ];

    private readonly ProcessRunner _processRunner;
    private readonly BinaryVerifier _binaryVerifier;
    private readonly string _ffprobePath;

    public FfprobeClient(
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string ffprobePath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(binaryVerifier);

        _ffprobePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        _processRunner = processRunner;
        _binaryVerifier = binaryVerifier;
    }

    public async Task<MediaProbeResult> ProbeAsync(
        StagedMediaImport stagedMedia,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessRunRequest request = CreateProcessRequest(
            _ffprobePath,
            stagedMedia.StagingPath,
            timeout);
        using FileStream stagedStream = OpenStagedMedia(stagedMedia.StagingPath);
        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);

        await VerifyAsync(
            new BinaryVerificationRequest(
                ToolComponentIdentity,
                _ffprobePath,
                CanonicalFfprobeDigest,
                CanonicalFfprobeByteSize),
            MediaProbeFailureReason.ToolVerificationFailed,
            cancellationToken).ConfigureAwait(false);

        await VerifyAsync(
            new BinaryVerificationRequest(
                StagedMediaComponentIdentity,
                stagedMedia.StagingPath,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize),
            MediaProbeFailureReason.StagedFileInvalid,
            cancellationToken).ConfigureAwait(false);

        ContainerSignature signature = await DetectContainerSignatureAsync(
            stagedStream,
            cancellationToken).ConfigureAwait(false);

        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(MediaProbeFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(MediaProbeFailureReason.ProcessFailed);
        }

        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);

        return ParseProcessResult(stagedMedia, signature, processResult);
    }

    internal static MediaProbeResult ParseProcessResult(
        StagedMediaImport stagedMedia,
        ContainerSignature signature,
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(processResult);

        if (processResult.Truncated)
        {
            throw Failure(MediaProbeFailureReason.OutputTruncated);
        }

        if (processResult.ExitCode != 0 ||
            processResult.StandardError.Length != 0 ||
            string.IsNullOrWhiteSpace(processResult.StandardOutput))
        {
            throw Failure(MediaProbeFailureReason.ProcessFailed);
        }

        return ParseProbeOutput(
            stagedMedia,
            signature,
            processResult.StandardOutput);
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffprobePath,
        string stagedPath,
        TimeSpan timeout)
    {
        string normalizedFfprobePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        string normalizedStagedPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            stagedPath,
            nameof(stagedPath));
        string? workingDirectory = Path.GetDirectoryName(normalizedStagedPath);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            throw new ArgumentException(
                "The staged media path must have a parent directory.",
                nameof(stagedPath));
        }

        return new ProcessRunRequest(
            normalizedFfprobePath,
            [
                "-v",
                "error",
                "-show_entries",
                ShowEntries,
                "-of",
                "json",
                normalizedStagedPath,
            ],
            workingDirectory,
            timeout,
            maxCapturedBytes: 1024 * 1024,
            standardInput: null);
    }

    internal static ContainerSignature DetectContainerSignature(
        ReadOnlySpan<byte> prefix,
        long byteSize)
    {
        if (prefix.Length >= 8 && prefix[4..8].SequenceEqual("ftyp"u8))
        {
            return ContainerSignature.IsoBaseMediaFtyp;
        }

        if (prefix.Length >= 4 &&
            prefix[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
        {
            return ContainerSignature.Ebml;
        }

        if (prefix.Length >= 12 &&
            prefix[..4].SequenceEqual("RIFF"u8) &&
            prefix[8..12].SequenceEqual("AVI "u8))
        {
            return ContainerSignature.RiffAvi;
        }

        if (prefix.Length >= 3 && prefix[..3].SequenceEqual("FLV"u8))
        {
            return ContainerSignature.Flv;
        }

        if (byteSize >= SignaturePrefixLength &&
            prefix.Length >= SignaturePrefixLength &&
            prefix[0] == 0x47 &&
            prefix[188] == 0x47 &&
            prefix[376] == 0x47)
        {
            return ContainerSignature.MpegTransportStream;
        }

        return ContainerSignature.Unknown;
    }

    internal static MediaProbeResult ParseProbeOutput(
        StagedMediaImport stagedMedia,
        ContainerSignature signature,
        string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(standardOutput);

        if (standardOutput.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(MediaProbeFailureReason.InvalidJson);
        }

        try
        {
            _ = CanonicalJson.Canonicalize(standardOutput);
        }
        catch (Exception exception)
            when (exception is JsonException or EncoderFallbackException or
                FormatException or OverflowException)
        {
            throw Failure(MediaProbeFailureReason.InvalidJson);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            if (ContainsReplacementCharacter(document.RootElement))
            {
                throw Failure(MediaProbeFailureReason.InvalidJson);
            }

            return ParseDocument(stagedMedia, signature, document.RootElement);
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(MediaProbeFailureReason.InvalidJson);
        }
    }

    private static async Task<ContainerSignature> DetectContainerSignatureAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[SignaturePrefixLength];
        int bytesRead = 0;

        try
        {
            stream.Position = 0;
            while (bytesRead < prefix.Length)
            {
                int read = await stream.ReadAsync(
                    prefix.AsMemory(bytesRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            long byteSize = stream.Length;
            stream.Position = 0;
            return DetectContainerSignature(prefix.AsSpan(0, bytesRead), byteSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(MediaProbeFailureReason.StagedFileInvalid);
        }
    }

    private async Task VerifyAsync(
        BinaryVerificationRequest request,
        MediaProbeFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _binaryVerifier.VerifyAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified)
            {
                throw Failure(failureReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason);
        }
    }

    private static FileStream OpenStagedMedia(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            return new FileStream(path, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException or
                IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or NotSupportedException)
        {
            throw Failure(MediaProbeFailureReason.StagedFileInvalid);
        }
    }

    private static void ValidateHeldStagedMedia(
        FileStream stream,
        long expectedByteSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.Device |
                               FileAttributes.ReparsePoint)) != 0 ||
                !stream.CanRead ||
                !stream.CanSeek ||
                stream.Length <= 0 ||
                stream.Length != expectedByteSize)
            {
                throw Failure(MediaProbeFailureReason.StagedFileInvalid);
            }
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(MediaProbeFailureReason.StagedFileInvalid);
        }
    }

    private static MediaProbeResult ParseDocument(
        StagedMediaImport stagedMedia,
        ContainerSignature signature,
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        JsonElement streamsElement = default;
        JsonElement formatElement = default;
        bool hasStreams = false;
        bool hasFormat = false;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "streams":
                    streamsElement = property.Value;
                    hasStreams = true;
                    break;
                case "format":
                    formatElement = property.Value;
                    hasFormat = true;
                    break;
                case "programs":
                case "stream_groups":
                    // FFprobe 8.1 emits these empty JSON envelopes even though the
                    // exact show_entries expression requests only streams and format.
                    RequireEmptyArray(property.Value);
                    break;
                default:
                    throw Failure(MediaProbeFailureReason.InvalidMetadata);
            }
        }

        if (!hasStreams || !hasFormat ||
            streamsElement.ValueKind != JsonValueKind.Array ||
            formatElement.ValueKind != JsonValueKind.Object)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        List<ProbedMediaStream> streams = [];
        foreach (JsonElement streamElement in streamsElement.EnumerateArray())
        {
            streams.Add(ParseStream(streamElement));
        }

        ParsedFormat format = ParseFormat(formatElement);
        if (!streams.Any(static stream => stream.Kind == MediaStreamKind.Video))
        {
            throw Failure(MediaProbeFailureReason.NoVideoStream);
        }

        IReadOnlyList<string> warnings = BuildWarnings(
            signature,
            format.Names,
            streams);

        try
        {
            return new MediaProbeResult(
                stagedMedia.CandidateId,
                stagedMedia.ProjectId,
                stagedMedia.JobId,
                stagedMedia.SourceFileName,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize,
                signature,
                format.Names,
                format.LongName,
                format.DurationSeconds,
                format.DeclaredByteSize,
                format.BitRate,
                format.ProbeScore,
                streams,
                warnings);
        }
        catch (ArgumentException)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }
    }

    private static ProbedMediaStream ParseStream(JsonElement element)
    {
        RequireObjectWithOnlyProperties(element, StreamPropertyNames);

        int index = ReadRequiredFlexibleInt32(element, "index");
        string codecType = ReadRequiredTrimmedString(element, "codec_type");
        MediaStreamKind kind = codecType switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            _ => MediaStreamKind.Other,
        };
        string codecName = ReadRequiredTrimmedString(element, "codec_name");
        string? profile = ReadOptionalTrimmedString(element, "profile");
        int? width = ReadOptionalFlexibleInt32(element, "width");
        int? height = ReadOptionalFlexibleInt32(element, "height");
        string? pixelFormat = ReadOptionalTrimmedString(element, "pix_fmt");
        RationalNumber? nominalFrameRate = kind == MediaStreamKind.Video
            ? ReadRequiredPositiveRational(element, "r_frame_rate")
            : ReadOptionalNonVideoRate(element, "r_frame_rate");
        RationalNumber? averageFrameRate = kind == MediaStreamKind.Video
            ? ReadRequiredPositiveRational(element, "avg_frame_rate")
            : ReadOptionalNonVideoRate(element, "avg_frame_rate");
        RationalNumber timeBase = ReadRequiredPositiveRational(element, "time_base");
        decimal? startSeconds = ReadOptionalDecimalString(element, "start_time");
        decimal? durationSeconds = ReadOptionalDecimalString(element, "duration");
        int? sampleRate = ReadOptionalStringInt32(element, "sample_rate");
        int? channels = ReadOptionalFlexibleInt32(element, "channels");
        string? channelLayout = ReadOptionalTrimmedString(element, "channel_layout");

        try
        {
            return new ProbedMediaStream(
                index,
                kind,
                codecName,
                profile,
                width,
                height,
                pixelFormat,
                nominalFrameRate,
                averageFrameRate,
                timeBase,
                startSeconds,
                durationSeconds,
                sampleRate,
                channels,
                channelLayout);
        }
        catch (ArgumentException)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }
    }

    private static ParsedFormat ParseFormat(JsonElement element)
    {
        RequireObjectWithOnlyProperties(element, FormatPropertyNames);

        string rawNames = ReadRequiredString(element, "format_name");
        string[] names = rawNames
            .Split(',', StringSplitOptions.None)
            .Select(static name => name.Trim())
            .ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        string? longName = ReadOptionalTrimmedString(element, "format_long_name");
        decimal? durationSeconds = ReadOptionalDecimalString(element, "duration");
        long? declaredByteSize = ReadOptionalStringInt64(element, "size");
        long? bitRate = ReadOptionalStringInt64(element, "bit_rate");
        int? probeScore = ReadOptionalFlexibleInt32(element, "probe_score");

        if (durationSeconds is < 0 ||
            declaredByteSize is <= 0 ||
            bitRate is <= 0 ||
            probeScore is < 0 or > 100)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return new ParsedFormat(
            Array.AsReadOnly(names),
            longName,
            durationSeconds,
            declaredByteSize,
            bitRate,
            probeScore);
    }

    private static IReadOnlyList<string> BuildWarnings(
        ContainerSignature signature,
        IReadOnlyList<string> formatNames,
        IReadOnlyList<ProbedMediaStream> streams)
    {
        SortedSet<string> warnings = new(StringComparer.Ordinal);
        int videoCount = streams.Count(
            static stream => stream.Kind == MediaStreamKind.Video);
        int audioCount = streams.Count(
            static stream => stream.Kind == MediaStreamKind.Audio);

        if (signature == ContainerSignature.Unknown)
        {
            warnings.Add(MediaProbeResult.UnrecognizedContainerSignatureWarning);
        }
        else if (!SignatureMatchesFormat(signature, formatNames))
        {
            warnings.Add(MediaProbeResult.SignatureFormatMismatchWarning);
        }

        if (audioCount == 0)
        {
            warnings.Add(MediaProbeResult.AudioTrackAbsentWarning);
        }

        if (videoCount > 1)
        {
            warnings.Add(MediaProbeResult.MultipleVideoStreamsWarning);
        }

        if (audioCount > 1)
        {
            warnings.Add(MediaProbeResult.MultipleAudioStreamsWarning);
        }

        if (streams.Any(static stream =>
            stream.Kind == MediaStreamKind.Video &&
            stream.NominalFrameRate != stream.AverageFrameRate))
        {
            warnings.Add(MediaProbeResult.VariableRateFieldsDifferWarning);
        }

        return Array.AsReadOnly(warnings.ToArray());
    }

    private static bool SignatureMatchesFormat(
        ContainerSignature signature,
        IReadOnlyList<string> formatNames) =>
        signature switch
        {
            ContainerSignature.IsoBaseMediaFtyp => ContainsAnyFormat(
                formatNames,
                "mov",
                "mp4",
                "m4a",
                "3gp",
                "3g2",
                "mj2"),
            ContainerSignature.Ebml => ContainsAnyFormat(
                formatNames,
                "matroska",
                "webm"),
            ContainerSignature.RiffAvi => ContainsAnyFormat(formatNames, "avi"),
            ContainerSignature.Flv => ContainsAnyFormat(formatNames, "flv"),
            ContainerSignature.MpegTransportStream => ContainsAnyFormat(
                formatNames,
                "mpegts"),
            ContainerSignature.Unknown => true,
            _ => false,
        };

    private static bool ContainsAnyFormat(
        IReadOnlyList<string> formatNames,
        params string[] expectedNames) =>
        formatNames.Any(formatName =>
            expectedNames.Contains(formatName, StringComparer.Ordinal));

    private static void RequireObjectWithOnlyProperties(
        JsonElement element,
        HashSet<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Any(property =>
                !allowedProperties.Contains(property.Name)))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }
    }

    private static void RequireEmptyArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 0)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        string? text = value.GetString();
        if (text is null || text.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return text;
    }

    private static string ReadRequiredTrimmedString(
        JsonElement element,
        string propertyName)
    {
        string text = ReadRequiredString(element, propertyName).Trim();
        if (text.Length == 0)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return text;
    }

    private static string? ReadOptionalTrimmedString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out _))
        {
            return null;
        }

        return ReadRequiredTrimmedString(element, propertyName);
    }

    private static int ReadRequiredFlexibleInt32(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return ParseFlexibleInt32(value);
    }

    private static int? ReadOptionalFlexibleInt32(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            ? ParseFlexibleInt32(value)
            : null;

    private static int ParseFlexibleInt32(JsonElement value)
    {
        string? text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        if (text is null ||
            !int.TryParse(
                text,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return parsed;
    }

    private static int? ReadOptionalStringInt32(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !int.TryParse(
                value.GetString(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return parsed;
    }

    private static long? ReadOptionalStringInt64(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !long.TryParse(
                value.GetString(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long parsed))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return parsed;
    }

    private static decimal? ReadOptionalDecimalString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !decimal.TryParse(
                value.GetString(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal parsed))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return parsed;
    }

    private static RationalNumber ReadRequiredPositiveRational(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return ParsePositiveRational(value);
    }

    private static RationalNumber? ReadOptionalNonVideoRate(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), "0/0", StringComparison.Ordinal))
        {
            return null;
        }

        return ParsePositiveRational(value);
    }

    private static RationalNumber ParsePositiveRational(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        string? text = value.GetString();
        int separator = text?.IndexOf('/') ?? -1;
        if (text is null ||
            separator <= 0 ||
            separator != text.LastIndexOf('/') ||
            !long.TryParse(
                text.AsSpan(0, separator),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long numerator) ||
            !long.TryParse(
                text.AsSpan(separator + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long denominator) ||
            numerator <= 0 ||
            denominator <= 0)
        {
            throw Failure(MediaProbeFailureReason.InvalidMetadata);
        }

        return new RationalNumber(numerator, denominator);
    }

    private static bool ContainsReplacementCharacter(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Contains('\uFFFD', StringComparison.Ordinal) ||
                        ContainsReplacementCharacter(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsReplacementCharacter);
            case JsonValueKind.String:
                return element.GetString()?.Contains(
                    '\uFFFD',
                    StringComparison.Ordinal) == true;
            default:
                return false;
        }
    }

    private static MediaProbeException Failure(MediaProbeFailureReason reason) =>
        new(reason);

    private sealed record ParsedFormat(
        IReadOnlyList<string> Names,
        string? LongName,
        decimal? DurationSeconds,
        long? DeclaredByteSize,
        long? BitRate,
        int? ProbeScore);
}