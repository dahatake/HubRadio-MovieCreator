using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

public sealed record StandardSourceColorEvidence(ContentDigest SourceDigest, int StreamIndex,
    string? Primaries, string? Transfer, string? Matrix, string? Range,
    FfmpegSourceColor Conversion, bool UsedUntaggedFallback,
    string Rule = "source-color-to-sdr-bt709-v1");

internal static class StandardSourceColor
{
    internal static async Task<StandardSourceColorEvidence> ProbeAsync(MediaInfo media, string sourcePath,
        string ffprobePath, ProcessRunner runner, TimeSpan timeout, CancellationToken token)
    {
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(ffprobePath)!);
        WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(ffprobePath));
        using var held = new FileStream(ffprobePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var binding = new BinaryVerificationRequest("standard.color.ffprobe", ffprobePath,
            new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize);
        BinaryVerificationResult verified = await new BinaryVerifier().VerifyAsync(binding, token).ConfigureAwait(false);
        if (!verified.IsVerified || verified.ActualDigest != binding.ExpectedDigest || verified.ActualByteSize != binding.ExpectedByteSize)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        ProcessRunResult result = await runner.RunAsync(new ProcessRunRequest(ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=index,width,height,color_range,color_space,color_transfer,color_primaries", "-of", "json", sourcePath],
            Path.GetDirectoryName(sourcePath)!, timeout, maxCapturedBytes: 64 * 1024), token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated || result.StandardError.Length != 0)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        return Parse(media, result.StandardOutput);
    }

    internal static StandardSourceColorEvidence Parse(MediaInfo media, string json)
    {
        try
        {
            if (json.Length > 64 * 1024) throw new FormatException();
            using JsonDocument document = JsonDocument.Parse(CanonicalJson.Canonicalize(json));
            JsonElement streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) throw new FormatException();
            JsonElement stream = streams[0];
            MediaStreamInfo source = media.PrimaryVideoStream;
            if (StandardSourceGeometry.ReadMathematicalInt32(stream.GetProperty("index")) != source.Index
                || StandardSourceGeometry.ReadMathematicalInt32(stream.GetProperty("width")) != source.Width
                || StandardSourceGeometry.ReadMathematicalInt32(stream.GetProperty("height")) != source.Height)
                throw new FormatException();
            string? Tag(string key)
            {
                if (!stream.TryGetProperty(key, out JsonElement value)) return null;
                if (value.ValueKind != JsonValueKind.String) throw new FormatException();
                string text = value.GetString()!;
                return text is "unknown" or "unspecified" ? null : text;
            }
            string? primaries = Tag("color_primaries"), transfer = Tag("color_transfer"), matrix = Tag("color_space"), range = Tag("color_range");
            if (range is not (null or "tv" or "pc")) throw new FormatException();
            bool untagged = primaries is null && transfer is null && matrix is null;
            FfmpegSourceColorModel model;
            if (untagged)
            {
                // Explicit, warned product fallback, not a measured color claim.
                model = source.Height <= 480 ? FfmpegSourceColorModel.Bt601525Sdr
                    : source.Height <= 576 ? FfmpegSourceColorModel.Bt601625Sdr : FfmpegSourceColorModel.Bt709Sdr;
            }
            else if (primaries == "bt2020" && matrix == "bt2020nc")
            {
                model = transfer switch
                {
                    "smpte2084" => FfmpegSourceColorModel.Bt2020Pq,
                    "arib-std-b67" => FfmpegSourceColorModel.Bt2020Hlg,
                    "bt2020-10" => FfmpegSourceColorModel.Bt2020Sdr,
                    _ => throw new FormatException(),
                };
            }
            else if (primaries == "bt709" && transfer == "bt709" && matrix == "bt709")
                model = FfmpegSourceColorModel.Bt709Sdr;
            else if (primaries == "smpte170m" && transfer == "smpte170m" && matrix == "smpte170m")
                model = FfmpegSourceColorModel.Bt601525Sdr;
            else if (primaries == "bt470bg" && transfer == "gamma28" && matrix == "bt470bg")
                model = FfmpegSourceColorModel.Bt601625Sdr;
            else throw new FormatException();
            return new StandardSourceColorEvidence(media.ContentDigest, source.Index, primaries, transfer, matrix, range,
                new FfmpegSourceColor(model, range == "pc"), untagged || range is null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSourceColor);
        }
    }
}