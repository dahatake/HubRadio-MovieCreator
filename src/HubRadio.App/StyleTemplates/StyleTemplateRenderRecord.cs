using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-142: one word of a recorded cue (seconds on the output timeline).</summary>
public sealed record RecordedWord(string Text, double Start, double End);

/// <summary>FR-140 / FR-142: one displayed cue as the template drew it (seconds on the output timeline).</summary>
public sealed record RecordedCue(
    double Start, double End, string Text, string? Speaker, string? SpeakerName, string Color, string? PortraitProbeColor,
    IReadOnlyList<RecordedWord> Words);

/// <summary>FR-140: a standing picture shown while a confirmed speaker's cue is on screen.</summary>
public sealed record RecordedPortrait(string CharacterId, string Name, double Start, double End, bool Shown, string RightsStatus);

/// <summary>
/// FR-140 / FR-142: what the template put on the cues of a Job (<c>style-template-render.json</c> in the Job's own directory):
/// the speaker and color of each cue, the words with their times, the standing pictures shown and the warnings. It states the
/// facts the render used; it adds no text.
/// </summary>
public sealed record StyleTemplateRenderRecord(
    string TemplateId, int Version, int Width, int Height, string DefaultSubtitleColor, string HighlightColor, string NormalColor,
    bool SpeakersConfirmed, IReadOnlyList<RecordedCue> Subtitles, IReadOnlyList<RecordedPortrait> Portraits,
    IReadOnlyList<string> Warnings)
{
    public const string FileName = "style-template-render.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static double Seconds(RationalTime time) => (double)time.Value / time.Rate;

    /// <summary>Builds the record of a plan over the cues the output displays, with the words each cue kept.</summary>
    public static StyleTemplateRenderRecord Create(StyleTemplateDefinition template, StyleTemplatePlan plan, int width, int height,
        IReadOnlyList<StyleTemplateInputCue> cues, bool speakersConfirmed, Func<StyleTemplatePortraitShot, string?> probeColor)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(cues);
        var recorded = new List<RecordedCue>();
        foreach (StyleTemplateInputCue cue in cues.OrderBy(static item => item.Cue.OutputRange.Start))
        {
            StyleTemplateFreeTelop? line = plan.FreeTelops.FirstOrDefault(item =>
                item.Input.Kind == StyleTemplateFreeTelopKind.Dialogue && item.Input.Range.Equals(cue.Cue.OutputRange));
            StyleTemplatePortraitShot? shot = line is null ? null
                : plan.Portraits.FirstOrDefault(item => item.Range.Equals(cue.Cue.OutputRange) && item.CharacterId == line.Input.Speaker);
            bool timed = CueWordTiming.IsValid(cue.Cue, cue.Words);
            recorded.Add(new(Seconds(cue.Cue.OutputRange.Start), Seconds(cue.Cue.OutputRange.End), cue.Cue.Text,
                line?.Input.Speaker, shot?.CharacterName, line?.Color ?? plan.TextColor, shot is null ? null : probeColor(shot),
                timed ? cue.Words.Select(static word => new RecordedWord(word.Text, Seconds(word.Range.Start), Seconds(word.Range.End))).ToArray() : []));
        }

        return new(template.Id, template.Version, width, height, plan.TextColor,
            plan.EmphasisColors.Count > 0 ? plan.EmphasisColors[0] : plan.TextColor, plan.TextColor, speakersConfirmed, recorded,
            plan.Portraits.Select(static shot => new RecordedPortrait(shot.CharacterId, shot.CharacterName,
                Seconds(shot.Range.Start), Seconds(shot.Range.End), true, string.Empty)).ToArray(),
            plan.Warnings.Select(static warning => warning.Range is { } range
                ? string.Create(CultureInfo.InvariantCulture, $"{warning.Code}@{Seconds(range.Start):F3}") : warning.Code).ToArray());
    }

    public static void Save(string jobDirectory, StyleTemplateRenderRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(record);
        _ = Directory.CreateDirectory(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
