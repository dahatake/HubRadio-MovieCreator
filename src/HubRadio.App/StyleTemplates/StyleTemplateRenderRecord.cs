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
    IReadOnlyList<RecordedWord> Words)
{
    /// <summary>FR-143: where the subtitle box lies in the output (pixels).</summary>
    public TalkShowBox? Box { get; init; }
}

/// <summary>FR-138 / FR-143: a telop other than the subtitle (emphasis telop, title, ...) with the box it was burned into.</summary>
public sealed record RecordedTelop(string Role, string Text, double Start, double End, TalkShowBox Box);

/// <summary>FR-143: the size of the output.</summary>
public sealed record RecordedOutput(int Width, int Height);

/// <summary>FR-143 (c): one inspected position and its verdict.</summary>
public sealed record RecordedInspectionItem(string Id, TalkShowBox Box, bool InsideMargins, bool InsideRegion, bool Ok);

/// <summary>
/// FR-143 (c): the evidence of the output inspection of a vertical placement: the rule applied, the region and margins used,
/// the positions inspected and the verdict ("pass" or "fail").
/// </summary>
public sealed record RecordedInspection(
    string Verdict, string Rule, int Width, int Height, StyleTemplateMargins? Margins, TalkShowBox Region,
    IReadOnlyList<RecordedInspectionItem> Items, IReadOnlyList<string> Warnings);

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

    /// <summary>FR-141: the topics the user entered or confirmed (with the span each is shown for).</summary>
    public IReadOnlyList<TalkShowTopicRecord> Topics { get; init; } = [];

    /// <summary>FR-141: the speakers; a name and a title are present only when they may be shown.</summary>
    public IReadOnlyList<TalkShowSpeakerRecord> Speakers { get; init; } = [];

    public IReadOnlyList<TalkShowSideTelopRecord> SideTelops { get; init; } = [];

    public IReadOnlyList<TalkShowNameBandRecord> NameBands { get; init; } = [];

    /// <summary>FR-138: the emphasis telops and the other overlays with the boxes they were burned into.</summary>
    public IReadOnlyList<RecordedTelop> Emphasis { get; init; } = [];

    /// <summary>FR-143: the size of the output.</summary>
    public RecordedOutput? Output { get; init; }

    /// <summary>FR-143: the PARAM-005 margins of a vertical output (null for a landscape one).</summary>
    public StyleTemplateMargins? Margins { get; init; }

    /// <summary>FR-143: the FR-014 subtitle region used (x, y, width, height).</summary>
    public TalkShowBox? SubtitleArea { get; init; }

    /// <summary>FR-143 (c): the inspection of a vertical output (null for a landscape one).</summary>
    public RecordedInspection? OutputInspection { get; init; }

    /// <summary>
    /// FR-141 / FR-143: the same record with the talk-show items, the burned-in overlays and subtitle box, and (for a vertical output)
    /// the placement inspection against the PARAM-005 margins and the FR-014 region.
    /// </summary>
    public StyleTemplateRenderRecord WithPlacement(TalkShowRenderPlan? talkShow, IReadOnlyList<RecordedTelop> overlays,
        TalkShowBox? subtitleBox, StyleTemplateMargins? margins, bool platformUiConfirmed)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        var region = new StyleTemplateRect(0, 0, Width, Height);
        bool vertical = Height > Width;
        IReadOnlyList<RecordedCue> cues = subtitleBox is null ? Subtitles : [.. Subtitles.Select(cue => cue with { Box = subtitleBox })];
        TalkShowRenderPlan show = talkShow ?? TalkShowRenderPlan.Empty;
        RecordedInspection? inspection = null;
        if (vertical && margins is not null)
        {
            var positions = new List<(string, StyleTemplateRect)>();
            if (subtitleBox is not null)
            {
                positions.Add(("subtitle", subtitleBox.ToRect()));
            }

            positions.AddRange(overlays.Select(static item => ($"{item.Role}@{item.Start:F3}", item.Box.ToRect())));
            positions.AddRange(show.SideTelops.Select(static item => ($"sideTelop:{item.TopicId}", item.Box.ToRect())));
            positions.AddRange(show.NameBands.Select(static item => ($"nameBand:{item.SpeakerId}", item.Box.ToRect())));
            VerticalPlacementEvidence evidence = VerticalPlacementInspector.Inspect(Width, Height, margins, region, positions, platformUiConfirmed);
            inspection = new(evidence.Passed ? "pass" : "fail", "template-margins-and-region", Width, Height, margins, TalkShowBox.From(region),
                [.. evidence.Items.Select(static item => new RecordedInspectionItem(item.Id, TalkShowBox.From(item.Rect),
                    item.InsideMargins, item.InsideRegion, item.Passed))], evidence.Warnings);
        }

        return this with
        {
            Subtitles = cues,
            Topics = show.Topics,
            Speakers = show.Speakers,
            SideTelops = show.SideTelops,
            NameBands = show.NameBands,
            Emphasis = overlays,
            Output = new(Width, Height),
            Margins = vertical ? margins : null,
            SubtitleArea = TalkShowBox.From(region),
            OutputInspection = inspection,
            Warnings = [.. Warnings, .. show.Warnings,
                .. inspection is { Verdict: "fail" } ? ["字幕・テロップが余白または字幕領域の外にあります（出力検査 不合格）"] : Array.Empty<string>()],
        };
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // The record is read by people and by tests as plain text: Japanese text is written as it is, not as \u escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
