using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

public sealed class PtsIndexEntry
{
    public PtsIndexEntry(
        int sequence,
        RationalTime presentationTime,
        RationalTime duration,
        bool keyFrame)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                "A PTS index sequence must not be negative.");
        }

        if (duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "A PTS index entry duration must be positive.");
        }

        _ = presentationTime + duration;

        Sequence = sequence;
        PresentationTime = presentationTime;
        Duration = duration;
        KeyFrame = keyFrame;
    }

    public int Sequence { get; }

    public RationalTime PresentationTime { get; }

    public RationalTime Duration { get; }

    public bool KeyFrame { get; }
}

public sealed class PtsIndex
{
    public const string CurrentSchemaVersion = "1.0.0";

    public PtsIndex(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        int streamIndex,
        RationalTime timeBase,
        IReadOnlyList<PtsIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsLeafFileName(sourceFileName))
        {
            throw new ArgumentException(
                "A PTS index source file name must be a nonblank leaf name without control characters.",
                nameof(sourceFileName));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                "A PTS index source byte size must be positive.");
        }

        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                "A PTS index stream index must not be negative.");
        }

        if (timeBase <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeBase),
                "A PTS index time base must be positive.");
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "A PTS index must contain at least one entry.",
                nameof(entries));
        }

        PtsIndexEntry[] copiedEntries = new PtsIndexEntry[entries.Count];
        PtsIndexEntry? previous = null;
        bool hasKeyFrame = false;
        for (int index = 0; index < copiedEntries.Length; index++)
        {
            PtsIndexEntry entry = entries[index]
                ?? throw new ArgumentException(
                    "PTS index entries must not contain null values.",
                    nameof(entries));
            if (entry.Sequence != index)
            {
                throw new ArgumentException(
                    "PTS index sequences must be contiguous and zero-based.",
                    nameof(entries));
            }

            if (entry.Duration <= default(RationalTime))
            {
                throw new ArgumentException(
                    "PTS index entry durations must be positive.",
                    nameof(entries));
            }

            if (!IsIntegralTimeBaseMultiple(entry.PresentationTime, timeBase) ||
                !IsIntegralTimeBaseMultiple(entry.Duration, timeBase))
            {
                throw new ArgumentException(
                    "PTS index entry times must be integral stream-time-base multiples.",
                    nameof(entries));
            }

            _ = entry.PresentationTime + entry.Duration;
            if (previous is not null &&
                entry.PresentationTime <= previous.PresentationTime)
            {
                throw new ArgumentException(
                    "PTS index presentation times must be strictly increasing.",
                    nameof(entries));
            }

            copiedEntries[index] = entry;
            previous = entry;
            hasKeyFrame |= entry.KeyFrame;
        }

        if (!hasKeyFrame)
        {
            throw new ArgumentException(
                "A PTS index must contain at least one key frame.",
                nameof(entries));
        }

        RationalTime lastEnd = copiedEntries[^1].PresentationTime +
            copiedEntries[^1].Duration;
        RationalTime rangeDuration = lastEnd - copiedEntries[0].PresentationTime;
        if (rangeDuration <= default(RationalTime))
        {
            throw new ArgumentException(
                "A PTS index presentation range must be positive.",
                nameof(entries));
        }

        CandidateId = candidateId;
        ProjectId = projectId;
        JobId = jobId;
        SourceFileName = sourceFileName;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        StreamIndex = streamIndex;
        TimeBase = timeBase;
        Entries = Array.AsReadOnly(copiedEntries);
        PresentationRange = new TimeRange(
            copiedEntries[0].PresentationTime,
            rangeDuration);
        CanonicalJson = BuildCanonicalJson();
    }

    public string SchemaVersion => CurrentSchemaVersion;

    public EntityId<AssetIdTag> CandidateId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> JobId { get; }

    public string SourceFileName { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public int StreamIndex { get; }

    public RationalTime TimeBase { get; }

    public IReadOnlyList<PtsIndexEntry> Entries { get; }

    public TimeRange PresentationRange { get; }

    public RationalTime PresentationStart => PresentationRange.Start;

    public RationalTime PresentationEnd => PresentationRange.End;

    public string CanonicalJson { get; }

    private static bool IsLeafFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsIntegralTimeBaseMultiple(
        RationalTime value,
        RationalTime timeBase)
    {
        BigInteger numerator = (BigInteger)value.Value * timeBase.Rate;
        BigInteger denominator = (BigInteger)value.Rate * timeBase.Value;
        return BigInteger.Remainder(numerator, denominator).IsZero;
    }

    private string BuildCanonicalJson()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("candidateId", CandidateId.ToString());
            writer.WriteString("projectId", ProjectId.ToString());
            writer.WriteString("jobId", JobId.ToString());
            writer.WriteString("sourceFileName", SourceFileName);
            writer.WriteString("contentDigest", ContentDigest.Hex);
            writer.WriteNumber("byteSize", ByteSize);
            writer.WriteNumber("streamIndex", StreamIndex);
            writer.WritePropertyName("timeBase");
            WriteRationalTime(writer, TimeBase);
            writer.WritePropertyName("presentationRange");
            writer.WriteStartObject();
            writer.WritePropertyName("start");
            WriteRationalTime(writer, PresentationRange.Start);
            writer.WritePropertyName("duration");
            WriteRationalTime(writer, PresentationRange.Duration);
            writer.WriteEndObject();
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (PtsIndexEntry entry in Entries)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", entry.Sequence);
                writer.WritePropertyName("presentationTime");
                WriteRationalTime(writer, entry.PresentationTime);
                writer.WritePropertyName("duration");
                WriteRationalTime(writer, entry.Duration);
                writer.WriteBoolean("keyFrame", entry.KeyFrame);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] canonical = HubRadio.Core.Serialization.CanonicalJson.CanonicalizeToUtf8(
            buffer.WrittenSpan);
        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(canonical);
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteNumber("rate", value.Rate);
        writer.WriteEndObject();
    }
}

public enum PtsIndexFailureReason
{
    InputBindingMismatch,
    ToolVerificationFailed,
    StagedFileInvalid,
    ProcessTimedOut,
    ProcessFailed,
    OutputTruncated,
    StandardErrorPresent,
    InvalidEncoding,
    EmptyOutput,
    EmptyLine,
    UnexpectedField,
    MalformedOutput,
    MissingStreamIndex,
    InvalidStreamIndex,
    StreamIndexMismatch,
    MissingKeyFrameFlag,
    InvalidKeyFrameFlag,
    MissingPresentationTimestamp,
    InvalidPresentationTimestamp,
    MissingDuration,
    InvalidDuration,
    ArithmeticOverflow,
    NonMonotonicPresentationTime,
    KeyFrameAbsent,
}

public sealed class PtsIndexException : Exception
{
    internal PtsIndexException(PtsIndexFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public PtsIndexFailureReason Reason { get; }

    private static string GetStableMessage(PtsIndexFailureReason reason) =>
        reason switch
        {
            PtsIndexFailureReason.InputBindingMismatch =>
                "The staged media and probe result do not identify the same source.",
            PtsIndexFailureReason.ToolVerificationFailed =>
                "The PTS indexing tool could not be verified.",
            PtsIndexFailureReason.StagedFileInvalid =>
                "The staged media file is invalid for PTS indexing.",
            PtsIndexFailureReason.ProcessTimedOut =>
                "The PTS indexing process exceeded its timeout.",
            PtsIndexFailureReason.ProcessFailed =>
                "The PTS indexing process failed.",
            PtsIndexFailureReason.OutputTruncated =>
                "The PTS index output exceeded the capture limit.",
            PtsIndexFailureReason.StandardErrorPresent =>
                "The PTS indexing process produced standard error output.",
            PtsIndexFailureReason.InvalidEncoding =>
                "The PTS index output is not valid UTF-8.",
            PtsIndexFailureReason.EmptyOutput =>
                "The PTS index output contains no frame entries.",
            PtsIndexFailureReason.EmptyLine =>
                "The PTS index output contains an empty frame line.",
            PtsIndexFailureReason.UnexpectedField =>
                "The PTS index output contains an unexpected frame field.",
            PtsIndexFailureReason.MalformedOutput =>
                "The PTS index output is malformed.",
            PtsIndexFailureReason.MissingStreamIndex =>
                "A PTS index frame is missing its stream index.",
            PtsIndexFailureReason.InvalidStreamIndex =>
                "A PTS index frame has an invalid stream index.",
            PtsIndexFailureReason.StreamIndexMismatch =>
                "A PTS index frame belongs to an unexpected stream.",
            PtsIndexFailureReason.MissingKeyFrameFlag =>
                "A PTS index frame is missing its key-frame flag.",
            PtsIndexFailureReason.InvalidKeyFrameFlag =>
                "A PTS index frame has an invalid key-frame flag.",
            PtsIndexFailureReason.MissingPresentationTimestamp =>
                "A PTS index frame is missing its presentation timestamp.",
            PtsIndexFailureReason.InvalidPresentationTimestamp =>
                "A PTS index frame has an invalid presentation timestamp.",
            PtsIndexFailureReason.MissingDuration =>
                "A PTS index frame is missing its duration.",
            PtsIndexFailureReason.InvalidDuration =>
                "A PTS index frame has an invalid duration.",
            PtsIndexFailureReason.ArithmeticOverflow =>
                "The PTS index cannot be represented exactly.",
            PtsIndexFailureReason.NonMonotonicPresentationTime =>
                "PTS index presentation timestamps are not strictly increasing.",
            PtsIndexFailureReason.KeyFrameAbsent =>
                "The PTS index contains no key frame.",
            _ => "PTS indexing failed.",
        };
}

public sealed class PtsIndexer
{
    // An empty section entry list explicitly suppresses all frame side-data fields.
    internal const string FrameShowEntries =
        "frame=stream_index,key_frame,best_effort_timestamp,duration:frame_side_data=";
    internal const string PacketShowEntries =
        "packet=stream_index,flags,pts,duration";
    internal const string CompactOutputFormat =
        "compact=p=0:nk=0:escape=none:s=|";

    private const int FileBufferSize = 64 * 1024;
    private const string ToolComponentIdentity =
        "tool.ffprobe." +
        FfprobeClient.CanonicalPackageId + "." +
        FfprobeClient.CanonicalPackageVersion;
    private const string StagedMediaComponentIdentity =
        "media.staged-input.pts-index";
    private const string StreamIndexField = "stream_index";
    private const string KeyFrameField = "key_frame";
    private const string PresentationTimestampField = "best_effort_timestamp";
    private const string DurationField = "duration";
    private const string PacketFlagsField = "flags";
    private const string PacketPresentationTimestampField = "pts";

    private static readonly ContentDigest CanonicalFfprobeDigest =
        new(FfprobeClient.CanonicalFfprobeSha256);

    private static readonly HashSet<string> KnownFrameFields =
    [
        StreamIndexField,
        KeyFrameField,
        PresentationTimestampField,
        DurationField,
    ];

    private static readonly HashSet<string> KnownPacketFields =
    [
        StreamIndexField,
        PacketFlagsField,
        PacketPresentationTimestampField,
        DurationField,
    ];

    private readonly ProcessRunner _processRunner;
    private readonly BinaryVerifier _binaryVerifier;
    private readonly string _ffprobePath;

    public PtsIndexer(
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

    public async Task<PtsIndex> IndexAsync(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await IndexCoreAsync(
            stagedMedia,
            probeResult,
            timeout,
            usePacketTimeline: false,
            cancellationToken).ConfigureAwait(false);

    internal async Task<PtsIndex> IndexPacketTimelineAsync(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await IndexCoreAsync(
            stagedMedia,
            probeResult,
            timeout,
            usePacketTimeline: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<PtsIndex> IndexCoreAsync(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        TimeSpan timeout,
        bool usePacketTimeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        cancellationToken.ThrowIfCancellationRequested();

        ProbedMediaStream primaryVideoStream = ValidateInputBinding(
            stagedMedia,
            probeResult);
        ProcessRunRequest request = usePacketTimeline
            ? CreatePacketProcessRequest(
                _ffprobePath,
                stagedMedia.StagingPath,
                primaryVideoStream.Index,
                timeout)
            : CreateProcessRequest(
                _ffprobePath,
                stagedMedia.StagingPath,
                primaryVideoStream.Index,
                timeout);

        using FileStream stagedStream = OpenStagedMedia(stagedMedia.StagingPath);
        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);

        await VerifyAsync(
            new BinaryVerificationRequest(
                ToolComponentIdentity,
                _ffprobePath,
                CanonicalFfprobeDigest,
                FfprobeClient.CanonicalFfprobeByteSize),
            PtsIndexFailureReason.ToolVerificationFailed,
            cancellationToken).ConfigureAwait(false);

        await VerifyAsync(
            new BinaryVerificationRequest(
                StagedMediaComponentIdentity,
                stagedMedia.StagingPath,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize),
            PtsIndexFailureReason.StagedFileInvalid,
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
            throw Failure(PtsIndexFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(PtsIndexFailureReason.ProcessFailed);
        }

        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);
        return ParseProcessResultCore(
            stagedMedia,
            probeResult,
            processResult,
            usePacketTimeline);
    }

    internal static ProbedMediaStream ValidateInputBinding(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);

        if (stagedMedia.CandidateId != probeResult.CandidateId ||
            stagedMedia.ProjectId != probeResult.ProjectId ||
            stagedMedia.JobId != probeResult.JobId ||
            !string.Equals(
                stagedMedia.SourceFileName,
                probeResult.SourceFileName,
                StringComparison.Ordinal) ||
            stagedMedia.ContentDigest != probeResult.ContentDigest ||
            stagedMedia.ByteSize != probeResult.ByteSize)
        {
            throw Failure(PtsIndexFailureReason.InputBindingMismatch);
        }

        return probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .MinBy(static stream => stream.Index)
            ?? throw Failure(PtsIndexFailureReason.InputBindingMismatch);
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffprobePath,
        string stagedPath,
        int streamIndex,
        TimeSpan timeout)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                "A selected ffprobe stream index must not be negative.");
        }

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
                "-select_streams",
                streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_frames",
                "-show_entries",
                FrameShowEntries,
                "-of",
                CompactOutputFormat,
                normalizedStagedPath,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes,
            standardInput: null);
    }

    internal static ProcessRunRequest CreatePacketProcessRequest(
        string ffprobePath,
        string stagedPath,
        int streamIndex,
        TimeSpan timeout)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                "A selected ffprobe stream index must not be negative.");
        }

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
                "-select_streams",
                streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_packets",
                "-show_entries",
                PacketShowEntries,
                "-of",
                CompactOutputFormat,
                normalizedStagedPath,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes,
            standardInput: null);
    }

    internal static PtsIndex ParseProcessResult(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        ProcessRunResult processResult)
    {
        return ParseProcessResultCore(
            stagedMedia,
            probeResult,
            processResult,
            usePacketTimeline: false);
    }

    private static PtsIndex ParseProcessResultCore(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        ProcessRunResult processResult,
        bool usePacketTimeline)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(processResult);
        _ = ValidateInputBinding(stagedMedia, probeResult);

        if (processResult.Truncated)
        {
            throw Failure(PtsIndexFailureReason.OutputTruncated);
        }

        if (processResult.ExitCode != 0 || processResult.StandardError is null)
        {
            throw Failure(PtsIndexFailureReason.ProcessFailed);
        }

        if (processResult.StandardError.Length != 0)
        {
            throw Failure(PtsIndexFailureReason.StandardErrorPresent);
        }

        if (string.IsNullOrEmpty(processResult.StandardOutput))
        {
            throw Failure(PtsIndexFailureReason.EmptyOutput);
        }

        return usePacketTimeline
            ? ParsePacketOutput(
                stagedMedia,
                probeResult,
                processResult.StandardOutput)
            : ParseOutput(
                stagedMedia,
                probeResult,
                processResult.StandardOutput);
    }

    internal static PtsIndex ParseOutput(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        string standardOutput)
    {
        return ParseOutputCore(
            stagedMedia,
            probeResult,
            standardOutput,
            usePacketTimeline: false);
    }

    internal static PtsIndex ParsePacketOutput(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        string standardOutput) =>
        ParseOutputCore(
            stagedMedia,
            probeResult,
            standardOutput,
            usePacketTimeline: true);

    private static PtsIndex ParseOutputCore(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        string standardOutput,
        bool usePacketTimeline)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(standardOutput);

        ProbedMediaStream primaryVideoStream = ValidateInputBinding(
            stagedMedia,
            probeResult);
        if (standardOutput.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(PtsIndexFailureReason.InvalidEncoding);
        }

        RationalTime timeBase = new(
            primaryVideoStream.TimeBase.Numerator,
            primaryVideoStream.TimeBase.Denominator);
        string[] lines = SplitFrameLines(standardOutput);
        List<PtsIndexEntry> entries = new(lines.Length);
        RationalTime? previousPresentationTime = null;
        bool hasKeyFrame = false;
        for (int sequence = 0; sequence < lines.Length; sequence++)
        {
            ParsedFrame frame = usePacketTimeline
                ? ParsePacketLine(
                    lines[sequence],
                    primaryVideoStream.Index)
                : ParseFrameLine(
                    lines[sequence],
                    primaryVideoStream.Index);
            RationalTime presentationTime = ConvertTicks(
                frame.PresentationTimestamp,
                timeBase);
            RationalTime duration = ConvertTicks(frame.Duration, timeBase);
            try
            {
                _ = presentationTime + duration;
            }
            catch (OverflowException)
            {
                throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
            }

            if (previousPresentationTime is not null &&
                presentationTime <= previousPresentationTime.Value)
            {
                throw Failure(PtsIndexFailureReason.NonMonotonicPresentationTime);
            }

            entries.Add(new PtsIndexEntry(
                sequence,
                presentationTime,
                duration,
                frame.KeyFrame));
            previousPresentationTime = presentationTime;
            hasKeyFrame |= frame.KeyFrame;
        }

        if (!hasKeyFrame)
        {
            throw Failure(PtsIndexFailureReason.KeyFrameAbsent);
        }

        try
        {
            return new PtsIndex(
                stagedMedia.CandidateId,
                stagedMedia.ProjectId,
                stagedMedia.JobId,
                stagedMedia.SourceFileName,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize,
                primaryVideoStream.Index,
                timeBase,
                entries);
        }
        catch (OverflowException)
        {
            throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentException)
        {
            throw Failure(PtsIndexFailureReason.MalformedOutput);
        }
    }

    private static string[] SplitFrameLines(string standardOutput)
    {
        if (standardOutput.Length == 0)
        {
            throw Failure(PtsIndexFailureReason.EmptyOutput);
        }

        for (int index = 0; index < standardOutput.Length; index++)
        {
            if (standardOutput[index] == '\r' &&
                (index + 1 >= standardOutput.Length || standardOutput[index + 1] != '\n'))
            {
                throw Failure(PtsIndexFailureReason.MalformedOutput);
            }
        }

        int bodyLength = standardOutput.Length;
        if (standardOutput[^1] == '\n')
        {
            bodyLength--;
            if (bodyLength > 0 && standardOutput[bodyLength - 1] == '\r')
            {
                bodyLength--;
            }
        }

        if (bodyLength == 0)
        {
            throw Failure(PtsIndexFailureReason.EmptyOutput);
        }

        string[] lines = standardOutput[..bodyLength]
            .Split('\n', StringSplitOptions.None);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw Failure(PtsIndexFailureReason.EmptyLine);
            }

            if (!string.Equals(line, line.Trim(), StringComparison.Ordinal) ||
                line.Any(char.IsControl))
            {
                throw Failure(PtsIndexFailureReason.MalformedOutput);
            }

            lines[index] = line;
        }

        return lines;
    }

    private static ParsedFrame ParseFrameLine(
        string line,
        int expectedStreamIndex)
    {
        // compact can emit one separator for the explicitly empty frame_side_data
        // section after the four frame columns. This is not an optional value:
        // all four known, distinct, required values must still pass validation.
        // Do not trim repeated separators or relax the shared packet field parser.
        if (line.EndsWith('|') &&
            line.Count(static character => character == '|') == KnownFrameFields.Count)
        {
            line = line[..^1];
        }

        Dictionary<string, string> fields = ParseFields(line, KnownFrameFields);

        string rawStreamIndex = ReadRequiredField(
            fields,
            StreamIndexField,
            PtsIndexFailureReason.MissingStreamIndex,
            treatNotAvailableAsMissing: false);
        int streamIndex = ParseNonnegativeInt32(
            rawStreamIndex,
            PtsIndexFailureReason.InvalidStreamIndex);
        if (streamIndex != expectedStreamIndex)
        {
            throw Failure(PtsIndexFailureReason.StreamIndexMismatch);
        }

        string rawKeyFrame = ReadRequiredField(
            fields,
            KeyFrameField,
            PtsIndexFailureReason.MissingKeyFrameFlag,
            treatNotAvailableAsMissing: false);
        bool keyFrame = rawKeyFrame switch
        {
            "0" => false,
            "1" => true,
            _ => throw Failure(PtsIndexFailureReason.InvalidKeyFrameFlag),
        };

        string rawPresentationTimestamp = ReadRequiredField(
            fields,
            PresentationTimestampField,
            PtsIndexFailureReason.MissingPresentationTimestamp,
            treatNotAvailableAsMissing: true);
        long presentationTimestamp = ParseSignedInt64(
            rawPresentationTimestamp,
            PtsIndexFailureReason.InvalidPresentationTimestamp);

        string rawDuration = ReadRequiredField(
            fields,
            DurationField,
            PtsIndexFailureReason.MissingDuration,
            treatNotAvailableAsMissing: true);
        long duration = ParsePositiveInt64(
            rawDuration,
            PtsIndexFailureReason.InvalidDuration);

        return new ParsedFrame(keyFrame, presentationTimestamp, duration);
    }

    private static ParsedFrame ParsePacketLine(
        string line,
        int expectedStreamIndex)
    {
        Dictionary<string, string> fields = ParseFields(line, KnownPacketFields);
        string rawStreamIndex = ReadRequiredField(
            fields,
            StreamIndexField,
            PtsIndexFailureReason.MissingStreamIndex,
            treatNotAvailableAsMissing: false);
        int streamIndex = ParseNonnegativeInt32(
            rawStreamIndex,
            PtsIndexFailureReason.InvalidStreamIndex);
        if (streamIndex != expectedStreamIndex)
        {
            throw Failure(PtsIndexFailureReason.StreamIndexMismatch);
        }

        string flags = ReadRequiredField(
            fields,
            PacketFlagsField,
            PtsIndexFailureReason.MissingKeyFrameFlag,
            treatNotAvailableAsMissing: false);
        if (flags.Length != 3 ||
            flags[0] is not ('K' or '_') ||
            flags[1] is not ('D' or '_') ||
            flags[2] != '_')
        {
            throw Failure(PtsIndexFailureReason.InvalidKeyFrameFlag);
        }

        string rawPresentationTimestamp = ReadRequiredField(
            fields,
            PacketPresentationTimestampField,
            PtsIndexFailureReason.MissingPresentationTimestamp,
            treatNotAvailableAsMissing: true);
        long presentationTimestamp = ParseSignedInt64(
            rawPresentationTimestamp,
            PtsIndexFailureReason.InvalidPresentationTimestamp);
        string rawDuration = ReadRequiredField(
            fields,
            DurationField,
            PtsIndexFailureReason.MissingDuration,
            treatNotAvailableAsMissing: true);
        long duration = ParsePositiveInt64(
            rawDuration,
            PtsIndexFailureReason.InvalidDuration);
        return new ParsedFrame(
            flags[0] == 'K',
            presentationTimestamp,
            duration);
    }

    private static Dictionary<string, string> ParseFields(
        string line,
        IReadOnlySet<string> knownFields)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string column in line.Split('|', StringSplitOptions.None))
        {
            int separatorIndex = column.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex != column.LastIndexOf('='))
            {
                throw Failure(PtsIndexFailureReason.UnexpectedField);
            }

            string name = column[..separatorIndex];
            string value = column[(separatorIndex + 1)..];
            if (!knownFields.Contains(name) || !fields.TryAdd(name, value))
            {
                throw Failure(PtsIndexFailureReason.UnexpectedField);
            }
        }

        return fields;
    }

    private static string ReadRequiredField(
        IReadOnlyDictionary<string, string> fields,
        string fieldName,
        PtsIndexFailureReason missingReason,
        bool treatNotAvailableAsMissing)
    {
        if (!fields.TryGetValue(fieldName, out string? value) ||
            value.Length == 0 ||
            treatNotAvailableAsMissing && string.Equals(value, "N/A", StringComparison.Ordinal))
        {
            throw Failure(missingReason);
        }

        return value;
    }

    private static int ParseNonnegativeInt32(
        string value,
        PtsIndexFailureReason invalidReason)
    {
        if (!IsCanonicalUnsignedInteger(value))
        {
            throw Failure(invalidReason);
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
        }

        return parsed;
    }

    private static long ParseSignedInt64(
        string value,
        PtsIndexFailureReason invalidReason)
    {
        if (!IsCanonicalSignedInteger(value))
        {
            throw Failure(invalidReason);
        }

        if (!long.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long parsed))
        {
            throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
        }

        return parsed;
    }

    private static long ParsePositiveInt64(
        string value,
        PtsIndexFailureReason invalidReason)
    {
        if (!IsCanonicalUnsignedInteger(value))
        {
            throw Failure(invalidReason);
        }

        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsed))
        {
            throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
        }

        if (parsed <= 0)
        {
            throw Failure(invalidReason);
        }

        return parsed;
    }

    private static bool IsCanonicalUnsignedInteger(string value)
    {
        if (value.Length == 0 ||
            value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        return value.All(static character => character is >= '0' and <= '9');
    }

    private static bool IsCanonicalSignedInteger(string value)
    {
        int digitStart = value.Length > 0 && value[0] == '-' ? 1 : 0;
        if (digitStart == value.Length ||
            digitStart == 1 && value[digitStart] == '0' ||
            value.Length - digitStart > 1 && value[digitStart] == '0')
        {
            return false;
        }

        return value.AsSpan(digitStart).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static RationalTime ConvertTicks(long ticks, RationalTime timeBase)
    {
        BigInteger numerator = (BigInteger)ticks * timeBase.Value;
        BigInteger denominator = timeBase.Rate;
        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        BigInteger reducedNumerator = numerator / divisor;
        BigInteger reducedDenominator = denominator / divisor;
        if (reducedNumerator < long.MinValue ||
            reducedNumerator > long.MaxValue ||
            reducedDenominator > long.MaxValue)
        {
            throw Failure(PtsIndexFailureReason.ArithmeticOverflow);
        }

        return new RationalTime(
            (long)reducedNumerator,
            (long)reducedDenominator);
    }

    private async Task VerifyAsync(
        BinaryVerificationRequest request,
        PtsIndexFailureReason failureReason,
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
        catch (PtsIndexException)
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
            throw Failure(PtsIndexFailureReason.StagedFileInvalid);
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
                throw Failure(PtsIndexFailureReason.StagedFileInvalid);
            }
        }
        catch (PtsIndexException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(PtsIndexFailureReason.StagedFileInvalid);
        }
    }

    private static PtsIndexException Failure(PtsIndexFailureReason reason) =>
        new(reason);

    private sealed record ParsedFrame(
        bool KeyFrame,
        long PresentationTimestamp,
        long Duration);
}