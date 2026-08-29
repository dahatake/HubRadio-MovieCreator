using System.Numerics;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Media;

public sealed class IngestValidationRequirements
{
    public IngestValidationRequirements(
        int? minimumVideoWidth = null,
        int? minimumVideoHeight = null)
    {
        if ((minimumVideoWidth is null) != (minimumVideoHeight is null))
        {
            throw new ArgumentException(
                "Minimum video width and height must either both be omitted or both be provided.",
                nameof(minimumVideoWidth));
        }

        if (minimumVideoWidth is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumVideoWidth),
                "A minimum video width must be positive when provided.");
        }

        if (minimumVideoHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumVideoHeight),
                "A minimum video height must be positive when provided.");
        }

        MinimumVideoWidth = minimumVideoWidth;
        MinimumVideoHeight = minimumVideoHeight;
    }

    public int? MinimumVideoWidth { get; }

    public int? MinimumVideoHeight { get; }
}

public static class IngestValidator
{
    private const string UnrecognizedContainerSignatureWarning =
        "unrecognized-container-signature";
    private const string SignatureFormatMismatchWarning =
        "signature-format-mismatch";
    private const string AudioTrackAbsentWarning = "audio-track-absent";
    private const string MultipleVideoStreamsWarning = "multiple-video-streams";
    private const string MultipleAudioStreamsWarning = "multiple-audio-streams";
    private const string VariableRateFieldsDifferWarning =
        "variable-rate-fields-differ";

    public static MediaInfo Validate(
        MediaProbeResult probeResult,
        IngestValidationRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(requirements);

        MediaContainerClassification containerClassification = MapContainer(
            probeResult.ContainerSignature);
        RationalTime? formatDuration = probeResult.DurationSeconds is decimal duration
            ? DecimalSecondsToRationalTime(duration)
            : null;
        MediaStreamInfo[] streams = probeResult.Streams
            .Select(MapStream)
            .ToArray();
        MediaStreamInfo primaryVideoStream = streams
            .Where(static stream => stream.Kind == MediaStreamType.Video)
            .MinBy(static stream => stream.Index)
            ?? throw new ArgumentException(
                "A media probe result must contain at least one video stream.",
                nameof(probeResult));

        MediaInfoWarning[] warnings = BuildWarnings(
            probeResult.Warnings,
            containerClassification,
            probeResult.FormatNames,
            formatDuration,
            streams,
            primaryVideoStream,
            requirements);

        return new MediaInfo(
            probeResult.CandidateId,
            probeResult.ProjectId,
            probeResult.JobId,
            probeResult.SourceFileName,
            probeResult.ContentDigest,
            probeResult.ByteSize,
            containerClassification,
            probeResult.FormatNames,
            probeResult.FormatLongName,
            formatDuration,
            streams,
            requirements.MinimumVideoWidth,
            requirements.MinimumVideoHeight,
            warnings);
    }

    internal static RationalTime DecimalSecondsToRationalTime(decimal seconds)
    {
        int[] bits = decimal.GetBits(seconds);
        BigInteger numerator = new BigInteger((uint)bits[0]) +
            (new BigInteger((uint)bits[1]) << 32) +
            (new BigInteger((uint)bits[2]) << 64);
        if ((bits[3] & int.MinValue) != 0)
        {
            numerator = -numerator;
        }

        int scale = (bits[3] >> 16) & 0xff;
        BigInteger denominator = BigInteger.Pow(10, scale);
        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        BigInteger reducedNumerator = numerator / divisor;
        BigInteger reducedDenominator = denominator / divisor;
        if (reducedNumerator < long.MinValue ||
            reducedNumerator > long.MaxValue ||
            reducedDenominator > long.MaxValue)
        {
            throw new OverflowException(
                "The exact decimal seconds value cannot be represented as RationalTime.");
        }

        return new RationalTime(
            (long)reducedNumerator,
            (long)reducedDenominator);
    }

    private static MediaContainerClassification MapContainer(
        ContainerSignature signature) =>
        signature switch
        {
            ContainerSignature.IsoBaseMediaFtyp =>
                MediaContainerClassification.IsoBaseMediaFtyp,
            ContainerSignature.Ebml => MediaContainerClassification.Ebml,
            ContainerSignature.RiffAvi => MediaContainerClassification.RiffAvi,
            ContainerSignature.Flv => MediaContainerClassification.Flv,
            ContainerSignature.MpegTransportStream =>
                MediaContainerClassification.MpegTransportStream,
            ContainerSignature.Unknown => MediaContainerClassification.Unknown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(signature),
                "A media probe container signature must be defined."),
        };

    private static MediaStreamInfo MapStream(ProbedMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new MediaStreamInfo(
            stream.Index,
            MapStreamType(stream.Kind),
            stream.CodecName,
            stream.Profile,
            stream.Width,
            stream.Height,
            stream.PixelFormat,
            stream.NominalFrameRate,
            stream.AverageFrameRate,
            new RationalTime(stream.TimeBase.Numerator, stream.TimeBase.Denominator),
            stream.StartSeconds is decimal start
                ? DecimalSecondsToRationalTime(start)
                : null,
            stream.DurationSeconds is decimal duration
                ? DecimalSecondsToRationalTime(duration)
                : null,
            stream.SampleRate,
            stream.Channels,
            stream.ChannelLayout);
    }

    private static MediaStreamType MapStreamType(MediaStreamKind kind) =>
        kind switch
        {
            MediaStreamKind.Video => MediaStreamType.Video,
            MediaStreamKind.Audio => MediaStreamType.Audio,
            MediaStreamKind.Other => MediaStreamType.Other,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                "A probed media stream kind must be defined."),
        };

    private static MediaInfoWarning[] BuildWarnings(
        IReadOnlyList<string> probeWarnings,
        MediaContainerClassification containerClassification,
        IReadOnlyList<string> formatNames,
        RationalTime? formatDuration,
        IReadOnlyList<MediaStreamInfo> streams,
        MediaStreamInfo primaryVideoStream,
        IngestValidationRequirements requirements)
    {
        HashSet<MediaInfoWarningCode> reportedCodes = MapProbeWarnings(probeWarnings);
        Dictionary<MediaInfoWarningCode, MediaInfoWarning> warnings = [];
        int videoCount = streams.Count(
            static stream => stream.Kind == MediaStreamType.Video);
        int audioCount = streams.Count(
            static stream => stream.Kind == MediaStreamType.Audio);

        if (audioCount == 0 ||
            reportedCodes.Contains(MediaInfoWarningCode.AudioTrackAbsent))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent));
        }

        MediaDurationFieldEvidence[] durationFields = BuildDurationFields(
            formatDuration,
            streams);
        if (durationFields.Length > 1 &&
            durationFields.Any(field => field.Value != durationFields[0].Value))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.DurationFieldsDiffer,
                    new DurationFieldsDifferWarningEvidence(durationFields)));
        }

        if (requirements.MinimumVideoWidth is int minimumWidth &&
            requirements.MinimumVideoHeight is int minimumHeight &&
            (primaryVideoStream.Width < minimumWidth ||
             primaryVideoStream.Height < minimumHeight))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.LowResolution,
                    new LowResolutionWarningEvidence(
                        primaryVideoStream.Width!.Value,
                        primaryVideoStream.Height!.Value,
                        minimumWidth,
                        minimumHeight)));
        }

        if (audioCount > 1 ||
            reportedCodes.Contains(MediaInfoWarningCode.MultipleAudioStreams))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.MultipleAudioStreams,
                    new MediaStreamCountWarningEvidence(audioCount)));
        }

        if (videoCount > 1 ||
            reportedCodes.Contains(MediaInfoWarningCode.MultipleVideoStreams))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.MultipleVideoStreams,
                    new MediaStreamCountWarningEvidence(videoCount)));
        }

        if (reportedCodes.Contains(MediaInfoWarningCode.SignatureFormatMismatch) ||
            containerClassification != MediaContainerClassification.Unknown &&
            !ContainerMatchesFormat(containerClassification, formatNames))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(MediaInfoWarningCode.SignatureFormatMismatch));
        }

        if (containerClassification == MediaContainerClassification.Unknown ||
            reportedCodes.Contains(MediaInfoWarningCode.UnrecognizedContainerSignature))
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.UnrecognizedContainerSignature));
        }

        MediaFrameRateDifference[] frameRateDifferences = streams
            .Where(static stream =>
                stream.Kind == MediaStreamType.Video &&
                stream.NominalFrameRate != stream.AverageFrameRate)
            .Select(static stream => new MediaFrameRateDifference(
                stream.Index,
                stream.NominalFrameRate!.Value,
                stream.AverageFrameRate!.Value))
            .OrderBy(static difference => difference.StreamIndex)
            .ToArray();
        bool probeReportedVariableRate = reportedCodes.Contains(
            MediaInfoWarningCode.VariableRateFieldsDiffer);
        bool observedVariableRateFields = frameRateDifferences.Length != 0;
        if (probeReportedVariableRate != observedVariableRateFields)
        {
            throw new ArgumentException(
                "The media probe variable-rate warning contradicts its stream rate fields.",
                nameof(probeWarnings));
        }

        if (observedVariableRateFields)
        {
            AddWarning(
                warnings,
                new MediaInfoWarning(
                    MediaInfoWarningCode.VariableRateFieldsDiffer,
                    new VariableRateFieldsDifferWarningEvidence(
                        probeReportedFieldsDiffer: true,
                        frameRateDifferences)));
        }

        return warnings.Values
            .OrderBy(static warning => warning.Code)
            .ToArray();
    }

    private static HashSet<MediaInfoWarningCode> MapProbeWarnings(
        IReadOnlyList<string> probeWarnings)
    {
        ArgumentNullException.ThrowIfNull(probeWarnings);
        HashSet<MediaInfoWarningCode> mapped = [];
        foreach (string warning in probeWarnings)
        {
            MediaInfoWarningCode code = warning switch
            {
                AudioTrackAbsentWarning => MediaInfoWarningCode.AudioTrackAbsent,
                MultipleAudioStreamsWarning => MediaInfoWarningCode.MultipleAudioStreams,
                MultipleVideoStreamsWarning => MediaInfoWarningCode.MultipleVideoStreams,
                SignatureFormatMismatchWarning =>
                    MediaInfoWarningCode.SignatureFormatMismatch,
                UnrecognizedContainerSignatureWarning =>
                    MediaInfoWarningCode.UnrecognizedContainerSignature,
                VariableRateFieldsDifferWarning =>
                    MediaInfoWarningCode.VariableRateFieldsDiffer,
                _ => throw new ArgumentException(
                    "A media probe result contains an unrecognized warning code.",
                    nameof(probeWarnings)),
            };
            mapped.Add(code);
        }

        return mapped;
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

    private static void AddWarning(
        IDictionary<MediaInfoWarningCode, MediaInfoWarning> warnings,
        MediaInfoWarning warning)
    {
        warnings[warning.Code] = warning;
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