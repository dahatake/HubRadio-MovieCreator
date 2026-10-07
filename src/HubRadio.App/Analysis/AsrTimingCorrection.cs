using System.Globalization;
using System.Text.Json;

namespace HubRadio.App.Analysis;

/// <summary>
/// FR-057.5 (Q-006 A): Whisper word timings often run into the silence after or before a word. This
/// deterministic rule moves a word edge that lies inside a measured silence to the silence boundary,
/// so the pause between words can be shortened without cutting into any word's range. Words that lie
/// entirely inside a silence, or that contain a whole silence, keep their timing (the recognizer heard
/// something there); a word is never shortened below <see cref="MinimumWordSeconds"/>.
/// </summary>
public static class AsrTimingCorrection
{
    public const string RuleVersion = "fr057-asr-word-edge-to-silence.v1";
    public const string Schema = "source-only.asr-timing-correction.v1";
    public const double MinimumWordSeconds = 0.1;
    public const double EdgeRemainderSeconds = 0.25;
    public const int MaximumSilences = 4096;

    /// <summary>Returns the result with corrected word edges, or the same instance when nothing changes.</summary>
    public static AsrWorkerResult Apply(AsrWorkerResult result, IReadOnlyList<(double Start, double End)> silences)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(silences);
        if (silences.Count == 0)
        {
            return result;
        }

        bool changed = false;
        var segments = new List<AsrWorkerSegment>(result.Segments.Count);
        foreach (AsrWorkerSegment segment in result.Segments)
        {
            var tokens = new List<AsrWorkerToken>(segment.Tokens.Count);
            foreach (AsrWorkerToken token in segment.Tokens)
            {
                if (token.StartSeconds is not double start || token.EndSeconds is not double end || end <= start)
                {
                    tokens.Add(token);
                    continue;
                }

                (double correctedStart, double correctedEnd) = Correct(start, end, silences);
                if (correctedStart == start && correctedEnd == end)
                {
                    tokens.Add(token);
                    continue;
                }

                changed = true;
                tokens.Add(new AsrWorkerToken(token.Text, correctedStart, correctedEnd, token.RecognitionConfidence,
                    token.TimingRequiresReview));
            }

            AsrWorkerSegment corrected;
            try
            {
                corrected = new AsrWorkerSegment(segment.SegmentId, segment.Seek, segment.Text, segment.StartSeconds,
                    segment.EndSeconds, segment.AverageLogProbability, segment.NoSpeechProbability, tokens);
            }
            catch (ArgumentException)
            {
                // A correction that would reorder words keeps the recognizer's timings for this segment.
                corrected = segment;
            }

            segments.Add(corrected);
        }

        return changed
            ? new AsrWorkerResult(result.ModelRepository, result.ModelRevision, result.ModelManifestDigest,
                result.Configuration, result.InputAudioDigest, result.InputAudioByteSize, result.InputAudioDurationSeconds,
                result.Language, result.LanguageConfidence, result.EngineDurationSeconds, segments)
            : result;
    }

    internal static (double Start, double End) Correct(double start, double end, IReadOnlyList<(double Start, double End)> silences)
    {
        foreach ((double silenceStart, double silenceEnd) in silences)
        {
            if (silenceEnd <= start || silenceStart >= end)
            {
                continue;
            }

            bool coversStart = silenceStart <= start;
            bool coversEnd = silenceEnd >= end;
            if (coversStart && coversEnd)
            {
                // Inside one silence: the recognizer heard something there; keep it.
                continue;
            }

            if (!coversStart && !coversEnd)
            {
                // A silence inside the word: a short remainder on one side is trailing/leading noise.
                if (end - silenceEnd < EdgeRemainderSeconds && silenceStart - start >= MinimumWordSeconds)
                {
                    end = silenceStart;
                }
                else if (silenceStart - start < EdgeRemainderSeconds && end - silenceEnd >= MinimumWordSeconds)
                {
                    start = silenceEnd;
                }

                continue;
            }

            if (coversEnd && silenceStart - start >= MinimumWordSeconds)
            {
                end = silenceStart;
            }
            else if (coversStart && end - silenceEnd >= MinimumWordSeconds)
            {
                start = silenceEnd;
            }
        }

        return (start, end);
    }

    /// <summary>The canonical artifact text that pins the silences used for one analysis.</summary>
    public static string Write(string detectorVersion, IReadOnlyList<(long StartSample, long EndSample)> silences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detectorVersion);
        ArgumentNullException.ThrowIfNull(silences);
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"detector\":").Append(JsonSerializer.Serialize(detectorVersion))
            .Append(",\"rule\":").Append(JsonSerializer.Serialize(RuleVersion))
            .Append(",\"schema\":").Append(JsonSerializer.Serialize(Schema))
            .Append(",\"silences\":[");
        for (int index = 0; index < silences.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            // Sample positions are decimal strings: canonical JSON would rewrite integers in exponent form.
            builder.Append("[\"").Append(silences[index].StartSample.ToString(CultureInfo.InvariantCulture)).Append("\",\"")
                .Append(silences[index].EndSample.ToString(CultureInfo.InvariantCulture)).Append("\"]");
        }

        return builder.Append("]}").ToString();
    }

    /// <summary>Parses and validates a saved artifact; returns the silences in seconds of analysis audio.</summary>
    public static IReadOnlyList<(double Start, double End)> Read(string json, string expectedDetectorVersion,
        long analysisSampleCount, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string[] names = root.EnumerateObject().Select(static property => property.Name).ToArray();
        if (!names.Order(StringComparer.Ordinal).SequenceEqual(["detector", "rule", "schema", "silences"], StringComparer.Ordinal)
            || root.GetProperty("schema").GetString() != Schema
            || root.GetProperty("rule").GetString() != RuleVersion
            || root.GetProperty("detector").GetString() != expectedDetectorVersion
            || root.GetProperty("silences").GetArrayLength() > MaximumSilences)
        {
            throw new FormatException("The ASR timing-correction evidence is invalid.");
        }

        var silences = new List<(long StartSample, long EndSample)>();
        long previousEnd = 0;
        foreach (JsonElement item in root.GetProperty("silences").EnumerateArray())
        {
            if (item.GetArrayLength() != 2)
            {
                throw new FormatException("The ASR timing-correction evidence is invalid.");
            }

            long start = Sample(item[0]);
            long end = Sample(item[1]);
            if (start < previousEnd || end <= start || end > analysisSampleCount)
            {
                throw new FormatException("The ASR timing-correction evidence is invalid.");
            }

            silences.Add((start, end));
            previousEnd = end;
        }

        if (!string.Equals(HubRadio.Core.Serialization.CanonicalJson.Canonicalize(Write(expectedDetectorVersion, silences)), json,
            StringComparison.Ordinal))
        {
            throw new FormatException("The ASR timing-correction evidence is not canonical.");
        }

        return silences.Select(item => ((double)item.StartSample / sampleRate, (double)item.EndSample / sampleRate)).ToArray();
    }

    private static long Sample(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is string text && text.Length is > 0 and <= 18
            && text.All(static character => character is >= '0' and <= '9') && (text == "0" || text[0] != '0')
            ? long.Parse(text, CultureInfo.InvariantCulture)
            : throw new FormatException("The ASR timing-correction evidence is invalid.");
}
