using System.Globalization;
using System.Text.Json;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

/// <summary>Measurements from the render filter BEFORE lossy AAC encoding. Not QC approval.</summary>
public sealed record StandardLoudnessEvidence(FfmpegLoudnessTarget Target,
    decimal? InputIntegratedLufs, decimal? InputTruePeakDbtp, decimal InputRangeLu,
    decimal? OutputIntegratedLufs, decimal? OutputTruePeakDbtp, decimal OutputRangeLu,
    decimal InputThresholdLufs, decimal OutputThresholdLufs, decimal? TargetOffsetDb,
    string NormalizationType, bool Silent, string MeasurementStage = "render-filter-before-aac");

internal static class StandardLoudnessReport
{
    internal static StandardLoudnessEvidence Read(ProcessRunResult process, FfmpegLoudnessTarget target)
    {
        if (process.ExitCode != 0 || process.Truncated || process.StandardError.Length > ProcessRunRequest.DefaultMaxCapturedBytes)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.LoudnessReportInvalid);
        string text = process.StandardError;
        int marker = text.LastIndexOf("[Parsed_loudnorm_", StringComparison.Ordinal);
        int begin = marker < 0 ? -1 : text.IndexOf('{', marker);
        int end = begin < 0 ? -1 : text.IndexOf('}', begin);
        if (begin < 0 || end <= begin || end - begin > 4096)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.LoudnessReportInvalid);
        try
        {
            using JsonDocument document = JsonDocument.Parse(text[begin..(end + 1)], new JsonDocumentOptions { MaxDepth = 2 });
            JsonElement root = document.RootElement;
            string[] keys = ["input_i", "input_tp", "input_lra", "input_thresh", "output_i", "output_tp",
                "output_lra", "output_thresh", "normalization_type", "target_offset"];
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != keys.Length || properties.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != keys.Length
                || properties.Any(item => !keys.Contains(item.Name, StringComparer.Ordinal) || item.Value.ValueKind != JsonValueKind.String))
                throw new FormatException();
            string Field(string key) => root.GetProperty(key).GetString()!;
            bool silent = Field("input_i") == "-inf" && Field("input_tp") == "-inf"
                && Field("output_i") == "-inf" && Field("output_tp") == "-inf";
            decimal? Number(string key, bool allowInfinity = false)
            {
                string value = Field(key);
                if (silent && allowInfinity && value is "-inf" or "inf") return null;
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal number) || number is < -150m or > 150m)
                    throw new FormatException();
                return number;
            }
            string normalization = Field("normalization_type");
            decimal inputRange = Number("input_lra")!.Value;
            decimal outputRange = Number("output_lra")!.Value;
            if (normalization is not ("dynamic" or "linear") || inputRange < 0 || outputRange < 0)
                throw new FormatException();
            return new StandardLoudnessEvidence(target, Number("input_i", true), Number("input_tp", true), inputRange,
                Number("output_i", true), Number("output_tp", true), outputRange, Number("input_thresh")!.Value,
                Number("output_thresh")!.Value, Number("target_offset", true), normalization, silent);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.LoudnessReportInvalid);
        }
    }
}