using System.Globalization;
using System.Numerics;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Rendering;

/// <summary>Fresh probe of the held, digest-verified original; MediaInfo does not retain SAR/rotation.</summary>
internal static class StandardSourceGeometry
{
    internal const string ShowEntries =
        "stream=index,width,height,sample_aspect_ratio:stream_side_data=rotation:stream_tags=rotate";

    internal static async Task<SourceDisplayGeometry> ProbeAsync(MediaInfo media, string sourcePath,
        string ffprobePath, ProcessRunner runner, TimeSpan timeout, CancellationToken token)
    {
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(ffprobePath)!);
        WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(ffprobePath));
        using var held = new FileStream(ffprobePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var binding = new BinaryVerificationRequest("standard.geometry.ffprobe", ffprobePath,
            new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize);
        BinaryVerificationResult verified = await new BinaryVerifier().VerifyAsync(binding, token).ConfigureAwait(false);
        if (!verified.IsVerified)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        ProcessRunResult result = await runner.RunAsync(new ProcessRunRequest(ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", ShowEntries, "-of", "json", sourcePath],
            Path.GetDirectoryName(sourcePath)!, timeout, maxCapturedBytes: 64 * 1024), token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated || result.StandardError.Length != 0)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        return Parse(media, result.StandardOutput);
    }

    internal static SourceDisplayGeometry Parse(MediaInfo media, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(CanonicalJson.Canonicalize(json));
            JsonElement streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) throw new FormatException();
            JsonElement stream = streams[0];
            MediaStreamInfo video = media.PrimaryVideoStream;
            int width = ReadMathematicalInt32(stream.GetProperty("width"));
            int height = ReadMathematicalInt32(stream.GetProperty("height"));
            if (ReadMathematicalInt32(stream.GetProperty("index")) != video.Index
                || width != video.Width || height != video.Height) throw new FormatException();
            // ffprobe reports an unspecified SAR by omitting the key or, with -show_optional_fields always, as "N/A".
            string sarText = stream.TryGetProperty("sample_aspect_ratio", out JsonElement sarValue)
                ? sarValue.GetString()!
                : "1:1";
            string[] sar = (sarText == "N/A" ? "1:1" : sarText).Split(':');
            if (sar.Length != 2
                || !long.TryParse(sar[0], NumberStyles.None, CultureInfo.InvariantCulture, out long numerator)
                || !long.TryParse(sar[1], NumberStyles.None, CultureInfo.InvariantCulture, out long denominator)
                || numerator <= 0 || denominator <= 0) throw new FormatException();
            BigInteger displayWidth = BigInteger.DivRem((BigInteger)width * numerator, denominator, out BigInteger remainder);
            if (!remainder.IsZero || displayWidth <= 0 || displayWidth > int.MaxValue) throw new FormatException();

            int? rotation = null;
            if (stream.TryGetProperty("side_data_list", out JsonElement sideData))
            {
                foreach (JsonElement side in sideData.EnumerateArray())
                {
                    if (!side.TryGetProperty("rotation", out JsonElement value)) continue;
                    int next = NormalizeRotation(ReadMathematicalInt32(value));
                    if (rotation is not null && rotation != next) throw new FormatException();
                    rotation = next;
                }
            }

            if (stream.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty("rotate", out JsonElement tag))
            {
                int tagged = NormalizeRotation(int.Parse(tag.GetString()!, CultureInfo.InvariantCulture));
                if (rotation is not null && rotation != tagged) throw new FormatException();
                rotation = tagged;
            }

            // No rotation metadata means identity, not an invented measured angle.
            return rotation is 90 or 270
                ? new SourceDisplayGeometry(height, (int)displayWidth)
                : new SourceDisplayGeometry((int)displayWidth, height);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException or NullReferenceException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        }
    }

    internal static int ReadMathematicalInt32(JsonElement element)
    {
        // As in StandardCompositionEvidenceCodec, require a JSON number that is
        // integral and within Int32. Parse directly so tiny fractions cannot round
        // to an integer through a decimal/double conversion; exponent notation is valid.
        if (element.ValueKind != JsonValueKind.Number
            || !int.TryParse(element.GetRawText(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException();
        }

        return value;
    }

    private static int NormalizeRotation(int rotation)
    {
        int normalized = (rotation % 360 + 360) % 360;
        if (normalized % 90 != 0) throw new FormatException();
        return normalized;
    }
}