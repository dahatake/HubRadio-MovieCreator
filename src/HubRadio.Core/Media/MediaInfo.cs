using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Media;

public enum MediaContainerClassification
{
    IsoBaseMediaFtyp,
    Ebml,
    RiffAvi,
    Flv,
    MpegTransportStream,
    Unknown,
}

public enum MediaStreamType
{
    Video,
    Audio,
    Other,
}

public enum MediaInfoWarningCode
{
    AudioTrackAbsent,
    DurationFieldsDiffer,
    LowResolution,
    MultipleAudioStreams,
    MultipleVideoStreams,
    SignatureFormatMismatch,
    UnrecognizedContainerSignature,
    VariableRateFieldsDiffer,
}

public enum MediaDurationFieldKind
{
    FormatDuration,
    StreamDuration,
    StreamEndOffset,
}

public abstract class MediaInfoWarningEvidence
{
    private protected MediaInfoWarningEvidence()
    {
    }
}

public sealed class MediaStreamCountWarningEvidence : MediaInfoWarningEvidence
{
    public MediaStreamCountWarningEvidence(int observedCount)
    {
        if (observedCount <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedCount),
                "A multiple-stream warning count must be greater than one.");
        }

        ObservedCount = observedCount;
    }

    public int ObservedCount { get; }
}

public sealed class LowResolutionWarningEvidence : MediaInfoWarningEvidence
{
    public LowResolutionWarningEvidence(
        int observedWidth,
        int observedHeight,
        int requiredMinimumWidth,
        int requiredMinimumHeight)
    {
        RequirePositive(observedWidth, nameof(observedWidth));
        RequirePositive(observedHeight, nameof(observedHeight));
        RequirePositive(requiredMinimumWidth, nameof(requiredMinimumWidth));
        RequirePositive(requiredMinimumHeight, nameof(requiredMinimumHeight));

        if (observedWidth >= requiredMinimumWidth &&
            observedHeight >= requiredMinimumHeight)
        {
            throw new ArgumentException(
                "Low-resolution evidence must identify at least one dimension below its required minimum.",
                nameof(observedWidth));
        }

        ObservedWidth = observedWidth;
        ObservedHeight = observedHeight;
        RequiredMinimumWidth = requiredMinimumWidth;
        RequiredMinimumHeight = requiredMinimumHeight;
    }

    public int ObservedWidth { get; }

    public int ObservedHeight { get; }

    public int RequiredMinimumWidth { get; }

    public int RequiredMinimumHeight { get; }

    private static void RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Resolution evidence dimensions must be positive.");
        }
    }
}

public sealed class MediaFrameRateDifference
{
    public MediaFrameRateDifference(
        int streamIndex,
        RationalNumber nominalFrameRate,
        RationalNumber averageFrameRate)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                "A frame-rate evidence stream index must not be negative.");
        }

        if (nominalFrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nominalFrameRate),
                "A nominal frame rate must be positive.");
        }

        if (averageFrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(averageFrameRate),
                "An average frame rate must be positive.");
        }

        if (nominalFrameRate == averageFrameRate)
        {
            throw new ArgumentException(
                "Frame-rate difference evidence must contain unequal rates.",
                nameof(averageFrameRate));
        }

        StreamIndex = streamIndex;
        NominalFrameRate = nominalFrameRate;
        AverageFrameRate = averageFrameRate;
    }

    public int StreamIndex { get; }

    public RationalNumber NominalFrameRate { get; }

    public RationalNumber AverageFrameRate { get; }
}

public sealed class VariableRateFieldsDifferWarningEvidence : MediaInfoWarningEvidence
{
    public VariableRateFieldsDifferWarningEvidence(
        bool probeReportedFieldsDiffer,
        IReadOnlyList<MediaFrameRateDifference> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);

        MediaFrameRateDifference[] copiedDifferences = new MediaFrameRateDifference[
            differences.Count];
        HashSet<int> streamIndices = [];
        for (int index = 0; index < copiedDifferences.Length; index++)
        {
            MediaFrameRateDifference difference = differences[index]
                ?? throw new ArgumentException(
                    "Frame-rate differences must not contain null values.",
                    nameof(differences));
            if (!streamIndices.Add(difference.StreamIndex))
            {
                throw new ArgumentException(
                    "Frame-rate differences must identify unique stream indices.",
                    nameof(differences));
            }

            copiedDifferences[index] = difference;
        }

        if (!probeReportedFieldsDiffer || copiedDifferences.Length == 0)
        {
            throw new ArgumentException(
            "Variable-rate-fields evidence requires both the probe report and at least one observed rate difference.",
                nameof(differences));
        }

        Array.Sort(
            copiedDifferences,
            static (left, right) => left.StreamIndex.CompareTo(right.StreamIndex));

        ProbeReportedFieldsDiffer = probeReportedFieldsDiffer;
        Differences = Array.AsReadOnly(copiedDifferences);
    }

    public bool ProbeReportedFieldsDiffer { get; }

    public IReadOnlyList<MediaFrameRateDifference> Differences { get; }
}

public sealed class MediaDurationFieldEvidence
{
    public MediaDurationFieldEvidence(
        MediaDurationFieldKind kind,
        int? streamIndex,
        RationalTime value)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "A duration evidence field kind must be defined.");
        }

        if (kind == MediaDurationFieldKind.FormatDuration)
        {
            if (streamIndex is not null)
            {
                throw new ArgumentException(
                    "Format-duration evidence must not identify a stream.",
                    nameof(streamIndex));
            }
        }
        else if (streamIndex is null or < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                "Stream-duration evidence must identify a nonnegative stream index.");
        }

        if (kind is MediaDurationFieldKind.FormatDuration or
            MediaDurationFieldKind.StreamDuration && value < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Duration fields must not be negative.");
        }

        Kind = kind;
        StreamIndex = streamIndex;
        Value = value;
    }

    public MediaDurationFieldKind Kind { get; }

    public int? StreamIndex { get; }

    public RationalTime Value { get; }
}

public sealed class DurationFieldsDifferWarningEvidence : MediaInfoWarningEvidence
{
    public DurationFieldsDifferWarningEvidence(
        IReadOnlyList<MediaDurationFieldEvidence> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count < 2)
        {
            throw new ArgumentException(
                "Duration-difference evidence requires at least two known fields.",
                nameof(fields));
        }

        MediaDurationFieldEvidence[] copiedFields = new MediaDurationFieldEvidence[
            fields.Count];
        HashSet<(MediaDurationFieldKind Kind, int? StreamIndex)> identities = [];
        for (int index = 0; index < copiedFields.Length; index++)
        {
            MediaDurationFieldEvidence field = fields[index]
                ?? throw new ArgumentException(
                    "Duration-difference evidence must not contain null values.",
                    nameof(fields));
            if (!identities.Add((field.Kind, field.StreamIndex)))
            {
                throw new ArgumentException(
                    "Duration-difference evidence fields must be unique.",
                    nameof(fields));
            }

            copiedFields[index] = field;
        }

        if (copiedFields.All(field => field.Value == copiedFields[0].Value))
        {
            throw new ArgumentException(
                "Duration-difference evidence must contain unequal values.",
                nameof(fields));
        }

        Array.Sort(copiedFields, CompareDurationFields);
        Fields = Array.AsReadOnly(copiedFields);
    }

    public IReadOnlyList<MediaDurationFieldEvidence> Fields { get; }

    private static int CompareDurationFields(
        MediaDurationFieldEvidence left,
        MediaDurationFieldEvidence right)
    {
        int leftGroup = left.Kind == MediaDurationFieldKind.FormatDuration ? 0 : 1;
        int rightGroup = right.Kind == MediaDurationFieldKind.FormatDuration ? 0 : 1;
        int groupComparison = leftGroup.CompareTo(rightGroup);
        if (groupComparison != 0)
        {
            return groupComparison;
        }

        int streamComparison = Nullable.Compare(left.StreamIndex, right.StreamIndex);
        return streamComparison != 0
            ? streamComparison
            : left.Kind.CompareTo(right.Kind);
    }
}

public sealed class MediaInfoWarning
{
    public MediaInfoWarning(
        MediaInfoWarningCode code,
        MediaInfoWarningEvidence? evidence = null)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(
                nameof(code),
                "A media warning code must be defined.");
        }

        bool evidenceMatchesCode = code switch
        {
            MediaInfoWarningCode.AudioTrackAbsent or
            MediaInfoWarningCode.SignatureFormatMismatch or
            MediaInfoWarningCode.UnrecognizedContainerSignature => evidence is null,
            MediaInfoWarningCode.MultipleAudioStreams or
            MediaInfoWarningCode.MultipleVideoStreams =>
                evidence is MediaStreamCountWarningEvidence,
            MediaInfoWarningCode.LowResolution =>
                evidence is LowResolutionWarningEvidence,
            MediaInfoWarningCode.VariableRateFieldsDiffer =>
                evidence is VariableRateFieldsDifferWarningEvidence,
            MediaInfoWarningCode.DurationFieldsDiffer =>
                evidence is DurationFieldsDifferWarningEvidence,
            _ => false,
        };
        if (!evidenceMatchesCode)
        {
            throw new ArgumentException(
                "Media warning evidence does not match its warning code.",
                nameof(evidence));
        }

        Code = code;
        Evidence = evidence;
    }

    public MediaInfoWarningCode Code { get; }

    public MediaInfoWarningEvidence? Evidence { get; }
}

public sealed class MediaStreamInfo
{
    public MediaStreamInfo(
        int index,
        MediaStreamType kind,
        string codecName,
        string? profile,
        int? width,
        int? height,
        string? pixelFormat,
        RationalNumber? nominalFrameRate,
        RationalNumber? averageFrameRate,
        RationalTime timeBase,
        RationalTime? start,
        RationalTime? duration,
        int? sampleRate,
        int? channels,
        string? channelLayout)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                "A media stream index must not be negative.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "A media stream kind must be defined.");
        }

        codecName = RequireTrimmedText(
            codecName,
            nameof(codecName),
            "A media codec name");
        profile = RequireOptionalTrimmedText(
            profile,
            nameof(profile),
            "A media codec profile");
        pixelFormat = RequireOptionalTrimmedText(
            pixelFormat,
            nameof(pixelFormat),
            "A media pixel format");
        channelLayout = RequireOptionalTrimmedText(
            channelLayout,
            nameof(channelLayout),
            "A media channel layout");

        if (kind == MediaStreamType.Video)
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

            ValidatePositiveRate(nominalFrameRate, nameof(nominalFrameRate), required: true);
            ValidatePositiveRate(averageFrameRate, nameof(averageFrameRate), required: true);
        }
        else
        {
            if (width is not null || height is not null || pixelFormat is not null)
            {
                throw new ArgumentException(
                    "Non-video streams must not contain video-only metadata.",
                    nameof(width));
            }

            ValidatePositiveRate(nominalFrameRate, nameof(nominalFrameRate), required: false);
            ValidatePositiveRate(averageFrameRate, nameof(averageFrameRate), required: false);
        }

        if (timeBase <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeBase),
                "A media stream time base must be positive.");
        }

        if (duration is not null && duration.Value < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "A media stream duration must not be negative.");
        }

        if (kind == MediaStreamType.Audio)
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
        Start = start;
        Duration = duration;
        SampleRate = sampleRate;
        Channels = channels;
        ChannelLayout = channelLayout;
    }

    public int Index { get; }

    public MediaStreamType Kind { get; }

    public string CodecName { get; }

    public string? Profile { get; }

    public int? Width { get; }

    public int? Height { get; }

    public string? PixelFormat { get; }

    public RationalNumber? NominalFrameRate { get; }

    public RationalNumber? AverageFrameRate { get; }

    public RationalTime TimeBase { get; }

    public RationalTime? Start { get; }

    public RationalTime? Duration { get; }

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
                "A media stream frame rate must be positive when present.");
        }
    }
}

public sealed class MediaInfo
{
    public MediaInfo(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        MediaContainerClassification containerClassification,
        IReadOnlyList<string> formatNames,
        string? formatLongName,
        RationalTime? formatDuration,
        IReadOnlyList<MediaStreamInfo> streams,
        int? requiredMinimumVideoWidth,
        int? requiredMinimumVideoHeight,
        IReadOnlyList<MediaInfoWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(formatNames);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(warnings);

        if (!IsLeafFileName(sourceFileName))
        {
            throw new ArgumentException(
                "A media source file name must be a nonblank leaf name without control characters.",
                nameof(sourceFileName));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                "A media byte size must be positive.");
        }

        if (!Enum.IsDefined(containerClassification))
        {
            throw new ArgumentOutOfRangeException(
                nameof(containerClassification),
                "A media container classification must be defined.");
        }

        string[] copiedFormatNames = CopyFormatNames(formatNames);
        formatLongName = RequireOptionalTrimmedText(
            formatLongName,
            nameof(formatLongName),
            "A media long format name");
        if (formatDuration is not null &&
            formatDuration.Value < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatDuration),
                "A media format duration must not be negative.");
        }

        ValidateResolutionRequirements(
            requiredMinimumVideoWidth,
            requiredMinimumVideoHeight);

        MediaStreamInfo[] copiedStreams = CopyStreams(streams);
        MediaStreamInfo primaryVideoStream = copiedStreams
            .Where(static stream => stream.Kind == MediaStreamType.Video)
            .MinBy(static stream => stream.Index)
            ?? throw new ArgumentException(
                "Media info requires at least one video stream.",
                nameof(streams));

        MediaInfoWarning[] copiedWarnings = CopyAndSortWarnings(warnings);
        ValidateWarnings(
            containerClassification,
            copiedFormatNames,
            formatDuration,
            copiedStreams,
            primaryVideoStream,
            requiredMinimumVideoWidth,
            requiredMinimumVideoHeight,
            copiedWarnings);

        CandidateId = candidateId;
        ProjectId = projectId;
        JobId = jobId;
        SourceFileName = sourceFileName;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        ContainerClassification = containerClassification;
        FormatNames = Array.AsReadOnly(copiedFormatNames);
        FormatLongName = formatLongName;
        FormatDuration = formatDuration;
        Streams = Array.AsReadOnly(copiedStreams);
        PrimaryVideoStream = primaryVideoStream;
        RequiredMinimumVideoWidth = requiredMinimumVideoWidth;
        RequiredMinimumVideoHeight = requiredMinimumVideoHeight;
        Warnings = Array.AsReadOnly(copiedWarnings);
    }

    public EntityId<AssetIdTag> CandidateId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> JobId { get; }

    public string SourceFileName { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public MediaContainerClassification ContainerClassification { get; }

    public IReadOnlyList<string> FormatNames { get; }

    public string? FormatLongName { get; }

    public RationalTime? FormatDuration { get; }

    public IReadOnlyList<MediaStreamInfo> Streams { get; }

    public MediaStreamInfo PrimaryVideoStream { get; }

    public int? RequiredMinimumVideoWidth { get; }

    public int? RequiredMinimumVideoHeight { get; }

    public IReadOnlyList<MediaInfoWarning> Warnings { get; }

    private static bool IsLeafFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static string[] CopyFormatNames(IReadOnlyList<string> formatNames)
    {
        if (formatNames.Count == 0)
        {
            throw new ArgumentException(
                "Media info requires at least one format name.",
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
                    "Media format names must contain trimmed nonblank tokens.",
                    nameof(formatNames));
            }

            copiedFormatNames[index] = formatName;
        }

        return copiedFormatNames;
    }

    private static string? RequireOptionalTrimmedText(
        string? value,
        string parameterName,
        string description)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) ||
             !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"{description} must be trimmed and nonblank when present.",
                parameterName);
        }

        return value;
    }

    private static void ValidateResolutionRequirements(
        int? requiredMinimumVideoWidth,
        int? requiredMinimumVideoHeight)
    {
        if ((requiredMinimumVideoWidth is null) != (requiredMinimumVideoHeight is null))
        {
            throw new ArgumentException(
                "Minimum video width and height must either both be omitted or both be provided.",
                nameof(requiredMinimumVideoWidth));
        }

        if (requiredMinimumVideoWidth is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredMinimumVideoWidth),
                "A minimum video width must be positive when provided.");
        }

        if (requiredMinimumVideoHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredMinimumVideoHeight),
                "A minimum video height must be positive when provided.");
        }
    }

    private static MediaStreamInfo[] CopyStreams(IReadOnlyList<MediaStreamInfo> streams)
    {
        if (streams.Count == 0)
        {
            throw new ArgumentException(
                "Media info requires at least one stream.",
                nameof(streams));
        }

        MediaStreamInfo[] copiedStreams = new MediaStreamInfo[streams.Count];
        HashSet<int> streamIndices = [];
        for (int index = 0; index < copiedStreams.Length; index++)
        {
            MediaStreamInfo stream = streams[index]
                ?? throw new ArgumentException(
                    "Media streams must not contain null values.",
                    nameof(streams));
            if (!streamIndices.Add(stream.Index))
            {
                throw new ArgumentException(
                    "Media streams must have unique indices.",
                    nameof(streams));
            }

            copiedStreams[index] = stream;
        }

        return copiedStreams;
    }

    private static MediaInfoWarning[] CopyAndSortWarnings(
        IReadOnlyList<MediaInfoWarning> warnings)
    {
        MediaInfoWarning[] copiedWarnings = new MediaInfoWarning[warnings.Count];
        HashSet<MediaInfoWarningCode> codes = [];
        for (int index = 0; index < copiedWarnings.Length; index++)
        {
            MediaInfoWarning warning = warnings[index]
                ?? throw new ArgumentException(
                    "Media warnings must not contain null values.",
                    nameof(warnings));
            if (!codes.Add(warning.Code))
            {
                throw new ArgumentException(
                    "Media warning codes must be unique.",
                    nameof(warnings));
            }

            copiedWarnings[index] = warning;
        }

        Array.Sort(
            copiedWarnings,
            static (left, right) => left.Code.CompareTo(right.Code));
        return copiedWarnings;
    }

    private static void ValidateWarnings(
        MediaContainerClassification containerClassification,
        IReadOnlyList<string> formatNames,
        RationalTime? formatDuration,
        IReadOnlyList<MediaStreamInfo> streams,
        MediaStreamInfo primaryVideoStream,
        int? requiredMinimumVideoWidth,
        int? requiredMinimumVideoHeight,
        IReadOnlyList<MediaInfoWarning> warnings)
    {
        Dictionary<MediaInfoWarningCode, MediaInfoWarning> byCode = warnings.ToDictionary(
            static warning => warning.Code);
        int videoCount = streams.Count(
            static stream => stream.Kind == MediaStreamType.Video);
        int audioCount = streams.Count(
            static stream => stream.Kind == MediaStreamType.Audio);

        RequireFlagWarning(
            byCode,
            MediaInfoWarningCode.AudioTrackAbsent,
            audioCount == 0);
        ValidateDurationWarning(byCode, formatDuration, streams);
        ValidateLowResolutionWarning(
            byCode,
            primaryVideoStream,
            requiredMinimumVideoWidth,
            requiredMinimumVideoHeight);
        ValidateCountWarning(
            byCode,
            MediaInfoWarningCode.MultipleAudioStreams,
            audioCount);
        ValidateCountWarning(
            byCode,
            MediaInfoWarningCode.MultipleVideoStreams,
            videoCount);
        RequireFlagWarning(
            byCode,
            MediaInfoWarningCode.SignatureFormatMismatch,
            containerClassification != MediaContainerClassification.Unknown &&
            !ContainerMatchesFormat(containerClassification, formatNames));
        RequireFlagWarning(
            byCode,
            MediaInfoWarningCode.UnrecognizedContainerSignature,
            containerClassification == MediaContainerClassification.Unknown);
        ValidateVariableRateWarning(byCode, streams);
    }

    private static void RequireFlagWarning(
        IReadOnlyDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        MediaInfoWarningCode code,
        bool required)
    {
        bool present = warnings.ContainsKey(code);
        if (present != required)
        {
            throw new ArgumentException(
                "Media warnings do not match the normalized media metadata.",
                nameof(warnings));
        }
    }

    private static void ValidateCountWarning(
        IReadOnlyDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        MediaInfoWarningCode code,
        int observedCount)
    {
        bool required = observedCount > 1;
        if (!warnings.TryGetValue(code, out MediaInfoWarning? warning))
        {
            if (required)
            {
                throw new ArgumentException(
                    "Media warnings do not match the normalized stream counts.",
                    nameof(warnings));
            }

            return;
        }

        if (!required ||
            warning.Evidence is not MediaStreamCountWarningEvidence evidence ||
            evidence.ObservedCount != observedCount)
        {
            throw new ArgumentException(
                "A multiple-stream warning does not match the normalized stream count.",
                nameof(warnings));
        }
    }

    private static void ValidateLowResolutionWarning(
        IReadOnlyDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        MediaStreamInfo primaryVideoStream,
        int? requiredMinimumVideoWidth,
        int? requiredMinimumVideoHeight)
    {
        bool required = requiredMinimumVideoWidth is not null &&
            (primaryVideoStream.Width < requiredMinimumVideoWidth ||
             primaryVideoStream.Height < requiredMinimumVideoHeight);
        if (!warnings.TryGetValue(
                MediaInfoWarningCode.LowResolution,
                out MediaInfoWarning? warning))
        {
            if (required)
            {
                throw new ArgumentException(
                    "A low-resolution warning is required by the normalized metadata.",
                    nameof(warnings));
            }

            return;
        }

        if (!required ||
            warning.Evidence is not LowResolutionWarningEvidence evidence ||
            evidence.ObservedWidth != primaryVideoStream.Width ||
            evidence.ObservedHeight != primaryVideoStream.Height ||
            evidence.RequiredMinimumWidth != requiredMinimumVideoWidth ||
            evidence.RequiredMinimumHeight != requiredMinimumVideoHeight)
        {
            throw new ArgumentException(
                "Low-resolution warning evidence does not match the primary video and requirements.",
                nameof(warnings));
        }
    }

    private static void ValidateVariableRateWarning(
        IReadOnlyDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        IReadOnlyList<MediaStreamInfo> streams)
    {
        MediaFrameRateDifference[] expectedDifferences = streams
            .Where(static stream =>
                stream.Kind == MediaStreamType.Video &&
                stream.NominalFrameRate != stream.AverageFrameRate)
            .Select(static stream => new MediaFrameRateDifference(
                stream.Index,
                stream.NominalFrameRate!.Value,
                stream.AverageFrameRate!.Value))
            .OrderBy(static difference => difference.StreamIndex)
            .ToArray();

        if (!warnings.TryGetValue(
                MediaInfoWarningCode.VariableRateFieldsDiffer,
                out MediaInfoWarning? warning))
        {
            if (expectedDifferences.Length != 0)
            {
                throw new ArgumentException(
                    "A variable-rate warning is required by the normalized metadata.",
                    nameof(warnings));
            }

            return;
        }

        if (warning.Evidence is not VariableRateFieldsDifferWarningEvidence evidence ||
            !evidence.ProbeReportedFieldsDiffer ||
            expectedDifferences.Length == 0 ||
            !FrameRateDifferencesEqual(expectedDifferences, evidence.Differences))
        {
            throw new ArgumentException(
                "Variable-rate warning evidence does not match the normalized video streams.",
                nameof(warnings));
        }
    }

    private static bool FrameRateDifferencesEqual(
        IReadOnlyList<MediaFrameRateDifference> expected,
        IReadOnlyList<MediaFrameRateDifference> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].StreamIndex != actual[index].StreamIndex ||
                expected[index].NominalFrameRate != actual[index].NominalFrameRate ||
                expected[index].AverageFrameRate != actual[index].AverageFrameRate)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateDurationWarning(
        IReadOnlyDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        RationalTime? formatDuration,
        IReadOnlyList<MediaStreamInfo> streams)
    {
        MediaDurationFieldEvidence[] expectedFields = BuildDurationFields(
            formatDuration,
            streams);
        bool required = expectedFields.Length > 1 &&
            expectedFields.Any(field => field.Value != expectedFields[0].Value);
        if (!warnings.TryGetValue(
                MediaInfoWarningCode.DurationFieldsDiffer,
                out MediaInfoWarning? warning))
        {
            if (required)
            {
                throw new ArgumentException(
                    "A duration-fields warning is required by the normalized metadata.",
                    nameof(warnings));
            }

            return;
        }

        if (!required ||
            warning.Evidence is not DurationFieldsDifferWarningEvidence evidence ||
            !DurationFieldsEqual(expectedFields, evidence.Fields))
        {
            throw new ArgumentException(
                "Duration warning evidence does not match the normalized duration fields.",
                nameof(warnings));
        }
    }

    private static MediaDurationFieldEvidence[] BuildDurationFields(
        RationalTime? formatDuration,
        IReadOnlyList<MediaStreamInfo> streams)
    {
        List<MediaDurationFieldEvidence> fields = [];
        if (formatDuration is not null)
        {
            fields.Add(new MediaDurationFieldEvidence(
                MediaDurationFieldKind.FormatDuration,
                streamIndex: null,
                formatDuration.Value));
        }

        foreach (MediaStreamInfo stream in streams
            .Where(static stream =>
                stream.Kind is MediaStreamType.Video or MediaStreamType.Audio &&
                stream.Duration is not null)
            .OrderBy(static stream => stream.Index))
        {
            if (stream.Start is null)
            {
                fields.Add(new MediaDurationFieldEvidence(
                    MediaDurationFieldKind.StreamDuration,
                    stream.Index,
                    stream.Duration!.Value));
            }
            else
            {
                fields.Add(new MediaDurationFieldEvidence(
                    MediaDurationFieldKind.StreamEndOffset,
                    stream.Index,
                    stream.Start.Value + stream.Duration!.Value));
            }
        }

        return fields.ToArray();
    }

    private static bool DurationFieldsEqual(
        IReadOnlyList<MediaDurationFieldEvidence> expected,
        IReadOnlyList<MediaDurationFieldEvidence> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].Kind != actual[index].Kind ||
                expected[index].StreamIndex != actual[index].StreamIndex ||
                expected[index].Value != actual[index].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainerMatchesFormat(
        MediaContainerClassification classification,
        IReadOnlyList<string> formatNames) =>
        classification switch
        {
            MediaContainerClassification.IsoBaseMediaFtyp => ContainsAnyFormat(
                formatNames,
                "mov",
                "mp4",
                "m4a",
                "3gp",
                "3g2",
                "mj2"),
            MediaContainerClassification.Ebml => ContainsAnyFormat(
                formatNames,
                "matroska",
                "webm"),
            MediaContainerClassification.RiffAvi => ContainsAnyFormat(formatNames, "avi"),
            MediaContainerClassification.Flv => ContainsAnyFormat(formatNames, "flv"),
            MediaContainerClassification.MpegTransportStream => ContainsAnyFormat(
                formatNames,
                "mpegts"),
            MediaContainerClassification.Unknown => true,
            _ => false,
        };

    private static bool ContainsAnyFormat(
        IReadOnlyList<string> formatNames,
        params string[] expectedNames) =>
        formatNames.Any(formatName =>
            expectedNames.Contains(formatName, StringComparer.Ordinal));
}