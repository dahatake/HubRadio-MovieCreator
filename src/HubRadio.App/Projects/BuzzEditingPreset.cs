using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Projects;

/// <summary>FR-054: facts read from the selected source video before the Project is created.</summary>
internal sealed record SourceMediaFacts(double DurationSeconds, int DisplayWidth, int DisplayHeight)
{
    internal bool IsVertical => DisplayHeight > DisplayWidth;
}

/// <summary>FR-054: one setting value chosen by the preset, with the reason shown to the user.</summary>
internal sealed record AutoSettingRecommendation(string Key, DefaultSettingValue Value, string Label, string ValueText, string Reason);

/// <summary>
/// FR-054: built-in editing preset distilled from short-video best practices (docs/dev/research/
/// buzz-editing-best-practices.md). It runs offline: no web search at run time. It only proposes
/// values that the settings screen offers (FR-050), so every result is a combination the pipeline
/// is known to build, and it never overrides a value the user already changed from its default.
/// </summary>
internal static class BuzzEditingPreset
{
    internal const string RuleVersion = "buzz-editing-preset.v2";

    internal static IReadOnlyList<AutoSettingRecommendation> Recommend(SourceMediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var result = new List<AutoSettingRecommendation>();
        double seconds = facts.DurationSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0 || facts.DisplayWidth <= 0 || facts.DisplayHeight <= 0)
        {
            return result;
        }

        // Short clips are already near Shorts/TikTok length: one strong cut beats several overlapping ones.
        // Longer talks yield more independent topics. A vertical source is native to Shorts/TikTok.
        int shorts = seconds < 45 ? 1 : seconds < 180 ? 2 : 3;
        if (facts.IsVertical && seconds >= 45)
        {
            shorts = 3;
        }

        result.Add(new("project.verticalShortCandidateCount", DefaultSettingValue.FromInteger(shorts), "縦型ショートの本数",
            $"{shorts}本", seconds < 45
                ? "元動画が45秒未満と短いため、内容が重ならないよう1本にしました。"
                : facts.IsVertical
                    ? "縦長で撮影された動画のため、ショート向けの切り抜きを最大の3本にしました。"
                    : seconds < 180
                        ? "元動画が3分未満のため、話題が重ならない2本にしました。"
                        : "元動画が3分以上あり話題が多いため、3本にしました。"));

        // Viewers decide to stay or swipe within the first seconds; start the hook early.
        result.Add(new("editing.shortHookMaximumStart", DefaultSettingValue.FromInteger(2), "つかみ（最初の見せ場）の開始",
            "2秒以内", "ショート動画は最初の1〜3秒で見続けるかが決まるため、見せ場を2秒以内に置くようにしました。"));

        // Many short-video viewers watch muted; short caption chunks are read at a glance on a phone.
        result.Add(new("subtitles.verticalLineLengthMaximum", DefaultSettingValue.FromInteger(9), "縦型の字幕1行の文字数",
            "9文字", "音を出さずに見る人が多いため、スマートフォンで一目で読める短い字幕（1行9文字まで）にしました。"));

        // YouTube end screens can run up to 20 seconds; long talks benefit from a full end screen.
        if (seconds >= 480)
        {
            result.Add(new("output.endScreenDuration", DefaultSettingValue.FromInteger(20), "終了画面の長さ",
                "20秒", "8分以上の長い動画のため、次の動画へ誘導する終了画面を最大の20秒にしました。"));
        }

        // FR-057: short-form viewers swipe away during dead air; a vertical clip gets the tightest pacing.
        // Profiles before 1.2.0 do not define these keys, so Apply skips them there.
        if (facts.IsVertical)
        {
            result.Add(new("editing.pauseTightening", DefaultSettingValue.FromText("strong"), "話の間を詰める強さ",
                "強め（0.4秒以上の間を詰める）", "縦長のショート向けの動画は、テンポが良いほど最後まで見られやすいため、0.4秒以上の間を詰めるようにしました。"));
        }

        // FR-059: frequent emphasis captions suit fast short-form viewing; a long talk reads better with fewer.
        if (facts.IsVertical && seconds < 480)
        {
            result.Add(new("subtitles.emphasisTelopAmount", DefaultSettingValue.FromText("many"), "強調テロップの量",
                "多め", "縦長の短い動画は、音を出さずに流し見されることが多いため、大事な言葉を強調テロップで多めに見せるようにしました。"));
        }
        else if (seconds >= 480)
        {
            result.Add(new("subtitles.emphasisTelopAmount", DefaultSettingValue.FromText("few"), "強調テロップの量",
                "少なめ", "8分以上の長い動画では、強調テロップが多いと見づらくなるため、少なめにしました。"));
        }

        return result;
    }

    /// <summary>
    /// Applies recommendations to keys whose value still equals the profile default. Returns the new
    /// snapshot and the recommendations actually applied (in order).
    /// </summary>
    internal static (DefaultSettingsSnapshot Snapshot, IReadOnlyList<AutoSettingRecommendation> Applied) Apply(
        DefaultSettingsProfile profile, DefaultSettingsSnapshot current, IReadOnlyList<AutoSettingRecommendation> recommendations)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(recommendations);
        var values = current.Values.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        var applied = new List<AutoSettingRecommendation>();
        foreach (AutoSettingRecommendation recommendation in recommendations)
        {
            if (!values.TryGetValue(recommendation.Key, out DefaultSettingValue? value)
                || !value.Equals(profile.GetDefinition(recommendation.Key).DefaultValue)
                || value.Equals(recommendation.Value))
            {
                continue;
            }

            values[recommendation.Key] = recommendation.Value;
            applied.Add(recommendation);
        }

        if (applied.Count == 0)
        {
            return (current, applied);
        }

        DefaultSettingsSnapshot tuned = profile.CreateSnapshot(current.Values
            .Select(item => new DefaultSettingSelection(item.Key, values[item.Key])).ToArray());
        return (tuned, applied);
    }
}

/// <summary>FR-054: duration and displayed size (rotation applied) of the selected source video.</summary>
internal static class SourceMediaProbe
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal static IReadOnlyList<string> BuildArguments(string sourcePath) =>
    [
        "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height:stream_side_data=rotation:stream_tags=rotate:format=duration",
        "-of", "json", sourcePath,
    ];

    internal static async Task<SourceMediaFacts?> ProbeAsync(string ffprobePath, string sourcePath, string workingDirectory,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ffprobePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            ProcessRunResult result = await run(new ProcessRunRequest(ffprobePath, BuildArguments(sourcePath),
                workingDirectory, Timeout), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && !result.Truncated ? Parse(result.StandardOutput) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static SourceMediaFacts? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement stream = root.GetProperty("streams")[0];
            int width = stream.GetProperty("width").GetInt32();
            int height = stream.GetProperty("height").GetInt32();
            double duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!,
                NumberStyles.Float, CultureInfo.InvariantCulture);
            int rotation = 0;
            if (stream.TryGetProperty("side_data_list", out JsonElement sideData))
            {
                foreach (JsonElement item in sideData.EnumerateArray())
                {
                    if (item.TryGetProperty("rotation", out JsonElement value) && value.TryGetInt32(out int degrees))
                    {
                        rotation = degrees;
                    }
                }
            }
            else if (stream.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty("rotate", out JsonElement rotate)
                && int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tagged))
            {
                rotation = tagged;
            }

            bool quarterTurn = Math.Abs(rotation % 180) == 90;
            return width > 0 && height > 0 && double.IsFinite(duration) && duration > 0
                ? new SourceMediaFacts(duration, quarterTurn ? height : width, quarterTurn ? width : height)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or IndexOutOfRangeException or OverflowException or ArgumentNullException)
        {
            return null;
        }
    }
}
