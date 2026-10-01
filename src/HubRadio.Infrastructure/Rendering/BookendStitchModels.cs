using System.Globalization;
using System.Text.Json;

namespace HubRadio.Infrastructure.Rendering;

/// <summary>Stable failure kinds of FR-035. Codes and exit codes are a public contract.</summary>
public enum BookendStitchFailure
{
    Usage,
    InputPathInvalid,
    InputNotFound,
    InputUnsupported,
    InputInvalidDuration,
    UnsupportedGeometry,
    UnsupportedFrameRate,
    OutputPathInvalid,
    OutputDirectoryMissing,
    OutputExists,
    ToolUnverified,
    EncodeFailed,
    Timeout,
    OutputVerificationFailed,
    Cancelled,
    InternalError,
}

public sealed class BookendStitchException : Exception
{
    public BookendStitchException(BookendStitchFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public BookendStitchFailure Failure { get; }

    public string Code => CodeOf(Failure);

    public int ExitCode => ExitCodeOf(Failure);

    public static string CodeOf(BookendStitchFailure failure) => failure switch
    {
        BookendStitchFailure.Usage => "stitch.usage",
        BookendStitchFailure.InputPathInvalid => "stitch.input_path_invalid",
        BookendStitchFailure.InputNotFound => "stitch.input_not_found",
        BookendStitchFailure.InputUnsupported => "stitch.input_unsupported",
        BookendStitchFailure.InputInvalidDuration => "stitch.input_invalid_duration",
        BookendStitchFailure.UnsupportedGeometry => "stitch.unsupported_geometry",
        BookendStitchFailure.UnsupportedFrameRate => "stitch.unsupported_frame_rate",
        BookendStitchFailure.OutputPathInvalid => "stitch.output_path_invalid",
        BookendStitchFailure.OutputDirectoryMissing => "stitch.output_directory_missing",
        BookendStitchFailure.OutputExists => "stitch.output_exists",
        BookendStitchFailure.ToolUnverified => "stitch.tool_unverified",
        BookendStitchFailure.EncodeFailed => "stitch.encode_failed",
        BookendStitchFailure.Timeout => "stitch.timeout",
        BookendStitchFailure.OutputVerificationFailed => "stitch.output_verification_failed",
        BookendStitchFailure.Cancelled => "stitch.cancelled",
        _ => "stitch.internal_error",
    };

    public static int ExitCodeOf(BookendStitchFailure failure) => failure switch
    {
        BookendStitchFailure.Usage => 2,
        BookendStitchFailure.InputPathInvalid or BookendStitchFailure.InputNotFound or BookendStitchFailure.InputUnsupported
            or BookendStitchFailure.InputInvalidDuration or BookendStitchFailure.UnsupportedGeometry
            or BookendStitchFailure.UnsupportedFrameRate => 3,
        BookendStitchFailure.OutputPathInvalid or BookendStitchFailure.OutputDirectoryMissing
            or BookendStitchFailure.OutputExists => 4,
        BookendStitchFailure.ToolUnverified => 5,
        BookendStitchFailure.EncodeFailed or BookendStitchFailure.Timeout or BookendStitchFailure.OutputVerificationFailed
            or BookendStitchFailure.Cancelled => 6,
        _ => 1,
    };
}

/// <summary>Raw (possibly relative) paths; the stitcher resolves and validates them.</summary>
public sealed record BookendStitchRequest(
    string MainPath,
    string? OpeningPath,
    string? ClosingPath,
    string OutputPath,
    string FfmpegPath,
    string FfprobePath,
    TimeSpan ProcessTimeout);

public enum BookendSegmentRole
{
    Opening,
    Main,
    Closing,
}

public sealed record BookendStitchSegment(
    BookendSegmentRole Role,
    string FileName,
    string Sha256,
    long ByteSize,
    bool HadAudio,
    decimal SourceDurationSeconds,
    decimal OutputStartSeconds,
    decimal OutputEndSeconds);

public sealed record BookendStitchResult(
    string OutputPath,
    string ManifestPath,
    int Width,
    int Height,
    string FrameRate,
    int AudioChannels,
    decimal ExpectedDurationSeconds,
    decimal OutputDurationSeconds,
    IReadOnlyList<BookendStitchSegment> Segments);

internal sealed record ProbedVideoStream(
    int Width,
    int Height,
    (long Numerator, long Denominator)? SampleAspectRatio,
    (long Numerator, long Denominator)? AverageFrameRate,
    (long Numerator, long Denominator)? RealFrameRate,
    decimal? Duration,
    string? CodecName);

internal sealed record ProbedAudioStream(int Channels, int SampleRate, string? CodecName);

internal sealed record ProbedMedia(
    IReadOnlyList<string> FormatNames,
    decimal? FormatDuration,
    ProbedVideoStream? Video,
    ProbedAudioStream? Audio)
{
    /// <summary>Parses ffprobe <c>-show_streams -show_format -of json</c>; returns null if the JSON is malformed.</summary>
    internal static ProbedMedia? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string[] formatNames = [];
            decimal? formatDuration = null;
            if (root.TryGetProperty("format", out JsonElement format) && format.ValueKind == JsonValueKind.Object)
            {
                formatNames = (Text(format, "format_name") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                formatDuration = PositiveDecimal(Text(format, "duration"));
            }

            ProbedVideoStream? video = null;
            ProbedAudioStream? audio = null;
            if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string? type = Text(stream, "codec_type");
                    if (type == "video" && video is null && !IsAttachedPicture(stream))
                    {
                        video = new ProbedVideoStream(Integer(stream, "width"), Integer(stream, "height"),
                            Ratio(Text(stream, "sample_aspect_ratio"), ':'), Ratio(Text(stream, "avg_frame_rate"), '/'),
                            Ratio(Text(stream, "r_frame_rate"), '/'), PositiveDecimal(Text(stream, "duration")),
                            Text(stream, "codec_name"));
                    }
                    else if (type == "audio" && audio is null)
                    {
                        audio = new ProbedAudioStream(Integer(stream, "channels"),
                            int.TryParse(Text(stream, "sample_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out int rate) ? rate : 0,
                            Text(stream, "codec_name"));
                    }
                }
            }

            return new ProbedMedia(formatNames, formatDuration, video, audio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out JsonElement disposition) && disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty("attached_pic", out JsonElement attached)
        && attached.ValueKind == JsonValueKind.Number && attached.TryGetInt32(out int value) && value != 0;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result) ? result : 0;

    internal static decimal? PositiveDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) && value > 0
            ? value : null;

    internal static (long Numerator, long Denominator)? Ratio(string? text, char separator)
    {
        string[] parts = (text ?? "").Split(separator);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long numerator)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long denominator)
            && numerator > 0 && denominator > 0
            ? (numerator, denominator) : null;
    }
}
