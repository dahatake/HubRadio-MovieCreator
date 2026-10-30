using HubRadio.App.Rendering;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-137: a timed word of a displayed cue (the words concatenate to the cue text).</summary>
public sealed record StyleTemplateWord(string Text, TimeRange Range);

/// <summary>A displayed cue with the word timing the short-word captions need (empty when unknown).</summary>
public sealed record StyleTemplateInputCue(StandardDisplayedCueText Cue, IReadOnlyList<StyleTemplateWord> Words);

/// <summary>FR-051 / FR-012 limits the template must stay inside (the accepted settings).</summary>
public sealed record StyleTemplateSubtitleLimits(
    int LineCountMaximum, int LineLengthMaximum, RationalTime CueMinimumDuration, RationalTime CueMaximumDuration);

/// <summary>FR-137: where the text of a non-emphasis telop came from.</summary>
public enum StyleTemplateTextSource
{
    SubtitlePart,
    UserInput,
    ConfirmedSentence,
    ConfirmedSummary,
}

/// <summary>A telop other than the emphasis telop (retort, inner voice, topic, title, hook, band).</summary>
public sealed record StyleTemplateFreeTelopInput(
    StyleTemplateFreeTelopKind Kind, string Text, TimeRange Range, StyleTemplateTextSource Source, OriginRef Origin,
    double? X = null, double? Y = null);

/// <summary>FR-136: the brand template of a brand-fixed Project (NFR-BR-001). Its colors, fonts and logo win.</summary>
public sealed record StyleTemplateBrand(
    string TextColor, string OutlineColor, IReadOnlyList<string> EmphasisColors, IReadOnlyList<string> FontFamilies,
    string? LogoReference);

public sealed record StyleTemplateApplyOptions(
    StyleTemplateSubtitleLimits Limits, string? EmphasisTelopAmount, bool SoundEffectsEnabled, RationalTime OutputDuration,
    StyleTemplateBrand? Brand = null);

public sealed record StyleTemplateMotion(StyleTemplateMotionKind Kind, TimeRange Range, double FromScale, double ToScale);

public sealed record StyleTemplateHighlight(int Start, int Length, TimeRange Range, string Color);

public sealed record StyleTemplateCaption(
    TimeRange Range, string Text, IReadOnlyList<StyleTemplateHighlight> Highlights, StyleTemplateMotion? Motion);

public sealed record StyleTemplateTelop(StandardEmphasisTelop Telop, string Color, StyleTemplateMotion? Motion);

public sealed record StyleTemplateFreeTelop(
    StyleTemplateFreeTelopInput Input, string Color, StyleTemplateMotion? Motion);

public sealed record StyleTemplateWarning(string Code, TimeRange? Range);

/// <summary>FR-136 / FR-137: everything the template adds on top of the FR-012/051/059/060 output.</summary>
public sealed record StyleTemplatePlan(
    string TemplateReference,
    string TextColor,
    string OutlineColor,
    IReadOnlyList<string> EmphasisColors,
    IReadOnlyList<string> FontFamilies,
    string? LogoReference,
    string Placement,
    int MinimumCaptionCount,
    IReadOnlyList<StyleTemplateCaption> Captions,
    IReadOnlyList<StyleTemplateTelop> EmphasisTelops,
    IReadOnlyList<StyleTemplateFreeTelop> FreeTelops,
    IReadOnlyList<StandardSoundEffectCue> SoundEffects,
    IReadOnlyList<StyleTemplateWarning> Warnings)
{
    /// <summary>A stable text form; equal inputs and the same template give equal text (FR-136 reproducibility).</summary>
    public string ToCanonicalText()
    {
        static string T(RationalTime time) => $"{time.Value}/{time.Rate}";
        static string R(TimeRange range) => $"{T(range.Start)}+{T(range.Duration)}";
        static string M(StyleTemplateMotion? motion) => motion is null ? "-" : $"{motion.Kind}:{R(motion.Range)}:{motion.FromScale}>{motion.ToScale}";
        var lines = new List<string>
        {
            TemplateReference, TextColor, OutlineColor, string.Join(",", EmphasisColors), string.Join(",", FontFamilies),
            LogoReference ?? "-", Placement, MinimumCaptionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        lines.AddRange(Captions.Select(item => $"C|{R(item.Range)}|{item.Text}|{M(item.Motion)}|"
            + string.Join(";", item.Highlights.Select(h => $"{h.Start},{h.Length},{R(h.Range)},{h.Color}"))));
        lines.AddRange(EmphasisTelops.Select(item => $"E|{R(item.Telop.OutputRange)}|{item.Telop.Text}|{item.Color}|{M(item.Motion)}"));
        lines.AddRange(FreeTelops.Select(item => $"F|{item.Input.Kind}|{R(item.Input.Range)}|{item.Input.Text}|{item.Color}|{M(item.Motion)}|{item.Input.X}|{item.Input.Y}"));
        lines.AddRange(SoundEffects.Select(item => $"S|{item.Kind}|{T(item.OutputStart)}"));
        lines.AddRange(Warnings.Select(item => $"W|{item.Code}|{(item.Range is { } range ? R(range) : "-")}"));
        return string.Join('\n', lines);
    }
}

/// <summary>
/// FR-136 / FR-137: turns one declarative template into a plan over the cues the output displays. It reuses the
/// FR-059 telop rule and the FR-060 sound rule, never writes text of its own and never exceeds the FR-012 / FR-051
/// limits (a cue that cannot reach the minimum duration is kept with a shown-below-minimum-duration warning).
/// </summary>
public static class StyleTemplateApplier
{
    public const string RuleVersion = "fr137-style-template.v1";
    public const string BelowMinimumDurationWarning = "shown-below-minimum-duration";
    public const string RejectedTelopWarning = "telop-text-source-rejected";
    public const string MissingWordTimingWarning = "word-timing-missing";

    private const int MaximumShortWords = 3;
    private const int MaximumShortCharacters = 20;

    /// <summary>The existing FR-059 / FR-060 output with no template: it is the default and stays unchanged (FR-136).</summary>
    public static (IReadOnlyList<StandardEmphasisTelop> Telops, IReadOnlyList<StandardSoundEffectCue> SoundEffects) PlanWithoutTemplate(
        IReadOnlyList<StandardDisplayedCueText> cues, string? emphasisTelopAmount, bool soundEffectsEnabled, RationalTime outputDuration)
    {
        IReadOnlyList<StandardEmphasisTelop> telops = StandardEmphasisTelops.Select(cues, emphasisTelopAmount, outputDuration);
        return (telops, StandardEmphasisTelops.PlanSoundEffects(soundEffectsEnabled, cues, telops, outputDuration));
    }

    public static StyleTemplatePlan Apply(StyleTemplateDefinition template, IReadOnlyList<StyleTemplateInputCue> cues,
        IReadOnlyList<StyleTemplateFreeTelopInput> freeTelops, StyleTemplateApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(freeTelops);
        ArgumentNullException.ThrowIfNull(options);
        var warnings = new List<StyleTemplateWarning>();
        StyleTemplateBrand? brand = options.Brand;
        // Brand-fixed Project: the brand wins on color, font and logo; the template keeps motion, placement and sound.
        string textColor = brand?.TextColor ?? template.TextColor;
        string outlineColor = brand?.OutlineColor ?? template.OutlineColor;
        string[] emphasisColors = (brand?.EmphasisColors ?? template.EmphasisColors)
            .Take(StyleTemplateCatalog.MaximumEmphasisColors).ToArray();
        if (emphasisColors.Length == 0)
        {
            emphasisColors = [textColor];
        }

        string[] fonts = (brand?.FontFamilies ?? template.FontFamilies).Take(StyleTemplateCatalog.MaximumFontFamilies).ToArray();

        StyleTemplateInputCue[] ordered = cues.OrderBy(static item => item.Cue.OutputRange.Start).ToArray();
        StandardDisplayedCueText[] cueTexts = ordered.Select(static item => item.Cue).ToArray();
        int minimumCount = ordered.Sum(item => FitParts(Tokens(item), options.Limits).Count);

        var captions = new List<StyleTemplateCaption>();
        foreach (StyleTemplateInputCue cue in ordered)
        {
            if (template.CaptionMode == StyleTemplateCaptionMode.ShortWords && cue.Words.Count > 0)
            {
                captions.AddRange(ShortWordCaptions(template, cue, options.Limits, emphasisColors[0], warnings));
                continue;
            }

            if (template.CaptionMode == StyleTemplateCaptionMode.ShortWords)
            {
                warnings.Add(new(MissingWordTimingWarning, cue.Cue.OutputRange));
            }

            List<List<Token>> parts = FitParts(Tokens(cue), options.Limits);
            foreach (List<Token> part in parts)
            {
                TimeRange range = parts.Count == 1 ? cue.Cue.OutputRange : PartRange(part, cue.Cue.OutputRange);
                // Without word timing the whole screen still pops in (130% to 100%); only the spoken word cannot be singled out.
                captions.Add(new(range, string.Concat(part.Select(static token => token.Text)), [],
                    template.CaptionMode == StyleTemplateCaptionMode.ShortWords ? Motion(template.EmphasisMotion, range) : null));
                if (range.Duration < options.Limits.CueMinimumDuration)
                {
                    warnings.Add(new(BelowMinimumDurationWarning, range));
                }
            }
        }

        // FR-059: verbatim parts of the displayed cues only.
        IReadOnlyList<StandardEmphasisTelop> selected = StandardEmphasisTelops.Select(cueTexts,
            options.EmphasisTelopAmount ?? "standard", options.OutputDuration);
        var emphasis = new List<StyleTemplateTelop>();
        for (int index = 0; index < selected.Count; index++)
        {
            StandardEmphasisTelop telop = selected[index];
            if (!cueTexts.Any(cue => cue.Text.Contains(telop.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            emphasis.Add(new(telop, emphasisColors[index % emphasisColors.Length], Motion(template.EmphasisMotion, telop.OutputRange)));
        }

        var free = new List<StyleTemplateFreeTelop>();
        int freeIndex = 0;
        foreach (StyleTemplateFreeTelopInput input in freeTelops.OrderBy(static item => item.Range.Start))
        {
            if (!Accepts(template, input, cueTexts, options.OutputDuration))
            {
                warnings.Add(new(RejectedTelopWarning, input.Range));
                continue;
            }

            free.Add(new(input, emphasisColors[freeIndex++ % emphasisColors.Length], Motion(template.FreeTelopMotion, input.Range)));
        }

        // FR-060: sounds only when the setting is on; the template cannot switch them on by itself.
        IReadOnlyList<StandardEmphasisTelop> soundTelops = template.SoundOnTelopAppearance
            ? emphasis.Select(static item => item.Telop)
                .Concat(free.Select(static item => new StandardEmphasisTelop(item.Input.Range, item.Input.Text, item.Input.Origin, RuleVersion)))
                .OrderBy(static item => item.OutputRange.Start).ToArray()
            : [];
        IReadOnlyList<StandardSoundEffectCue> sounds = StandardEmphasisTelops.PlanSoundEffects(
            options.SoundEffectsEnabled, cueTexts, soundTelops, options.OutputDuration);

        return new StyleTemplatePlan(template.Reference, textColor, outlineColor, emphasisColors, fonts,
            brand?.LogoReference, template.Placement, minimumCount, captions, emphasis, free, sounds, warnings);
    }

    private static bool Accepts(StyleTemplateDefinition template, StyleTemplateFreeTelopInput input,
        IReadOnlyList<StandardDisplayedCueText> cues, RationalTime outputDuration)
    {
        if (!template.FreeTelopKinds.Contains(input.Kind) || string.IsNullOrWhiteSpace(input.Text)
            || input.Range.Duration <= default(RationalTime) || input.Range.End > outputDuration)
        {
            return false;
        }

        if ((input.X is { } x && (x is < 0 or > 1)) || (input.Y is { } y && (y is < 0 or > 1)))
        {
            return false;
        }

        bool speech = input.Kind is StyleTemplateFreeTelopKind.Retort or StyleTemplateFreeTelopKind.InnerVoice;
        return input.Source switch
        {
            StyleTemplateTextSource.SubtitlePart => !speech && cues.Any(cue => cue.Text.Contains(input.Text, StringComparison.Ordinal)),
            StyleTemplateTextSource.UserInput or StyleTemplateTextSource.ConfirmedSentence => true,
            StyleTemplateTextSource.ConfirmedSummary => input.Kind is StyleTemplateFreeTelopKind.Topic or StyleTemplateFreeTelopKind.Title,
            _ => false,
        };
    }

    private static StyleTemplateMotion? Motion(StyleTemplateMotionKind kind, TimeRange shown)
    {
        if (kind == StyleTemplateMotionKind.None)
        {
            return null;
        }

        RationalTime length = shown.Duration < StyleTemplateCatalog.MotionDuration ? shown.Duration : StyleTemplateCatalog.MotionDuration;
        double from = kind switch { StyleTemplateMotionKind.ShrinkFromLarge => 1.3, StyleTemplateMotionKind.Pop => 0.5, _ => 1.0 };
        return new(kind, new TimeRange(shown.Start, length), from, 1.0);
    }

    private sealed record Token(string Text, TimeRange Range);

    private static List<Token> Tokens(StyleTemplateInputCue cue)
    {
        TimeRange range = cue.Cue.OutputRange;
        if (cue.Words.Count > 0 && string.Concat(cue.Words.Select(static word => word.Text)) == cue.Cue.Text)
        {
            return cue.Words.Select(static word => new Token(word.Text, word.Range)).ToList();
        }

        // No usable word timing: spread the characters evenly over the cue.
        string[] characters = System.Globalization.StringInfo.GetTextElementEnumerator(cue.Cue.Text) is { } enumerator
            ? Elements(enumerator) : [];
        var tokens = new List<Token>();
        for (int index = 0; index < characters.Length; index++)
        {
            RationalTime start = range.Start + new RationalTime(range.Duration.Value * index, range.Duration.Rate * characters.Length);
            RationalTime end = range.Start + new RationalTime(range.Duration.Value * (index + 1), range.Duration.Rate * characters.Length);
            tokens.Add(new(characters[index], new TimeRange(start, end - start)));
        }

        return tokens;
    }

    private static string[] Elements(System.Globalization.TextElementEnumerator enumerator)
    {
        var items = new List<string>();
        while (enumerator.MoveNext())
        {
            items.Add((string)enumerator.Current);
        }

        return [.. items];
    }

    /// <summary>The fewest parts that stay within lines, line length and cue maximum (FR-051 greedy at token borders).</summary>
    private static List<List<Token>> FitParts(List<Token> tokens, StyleTemplateSubtitleLimits limits)
    {
        int capacity = Math.Max(1, limits.LineCountMaximum * limits.LineLengthMaximum);
        var parts = new List<List<Token>>();
        List<Token>? current = null;
        int length = 0;
        foreach (Token token in tokens)
        {
            if (current is not null && (length + token.Text.Length > capacity
                || token.Range.End - current[0].Range.Start > limits.CueMaximumDuration))
            {
                parts.Add(current);
                current = null;
            }

            if (current is null)
            {
                current = [];
                length = 0;
            }

            current.Add(token);
            length += token.Text.Length;
        }

        if (current is not null)
        {
            parts.Add(current);
        }

        return parts;
    }

    private static TimeRange PartRange(List<Token> part, TimeRange cue) => new(part[0].Range.Start,
        part[^1].Range.End - part[0].Range.Start);

    private static IEnumerable<StyleTemplateCaption> ShortWordCaptions(StyleTemplateDefinition template, StyleTemplateInputCue cue,
        StyleTemplateSubtitleLimits limits, string highlightColor, List<StyleTemplateWarning> warnings)
    {
        List<Token> tokens = Tokens(cue);
        int capacity = Math.Max(1, limits.LineCountMaximum * limits.LineLengthMaximum);
        int shortCapacity = Math.Min(MaximumShortCharacters, capacity);
        var result = new List<StyleTemplateCaption>();
        int index = 0;
        while (index < tokens.Count)
        {
            var chunk = new List<Token>();
            int length = 0;
            while (index < tokens.Count && chunk.Count < MaximumShortWords && (chunk.Count == 0 || length + tokens[index].Text.Length <= shortCapacity))
            {
                chunk.Add(tokens[index]);
                length += tokens[index].Text.Length;
                index++;
            }

            // The minimum cue length wins over the 1-3 word split: relax by taking more words while the limits allow.
            while (index < tokens.Count && NextStart(tokens, index, cue.Cue.OutputRange) - chunk[0].Range.Start < limits.CueMinimumDuration
                && length + tokens[index].Text.Length <= capacity
                && tokens[index].Range.End - chunk[0].Range.Start <= limits.CueMaximumDuration)
            {
                chunk.Add(tokens[index]);
                length += tokens[index].Text.Length;
                index++;
            }

            RationalTime end = NextStart(tokens, index, cue.Cue.OutputRange);
            var range = new TimeRange(chunk[0].Range.Start, end - chunk[0].Range.Start);
            var highlights = new List<StyleTemplateHighlight>();
            int offset = 0;
            foreach (Token token in chunk)
            {
                highlights.Add(new(offset, token.Text.Length, token.Range, highlightColor));
                offset += token.Text.Length;
            }

            result.Add(new(range, string.Concat(chunk.Select(static token => token.Text)), highlights, Motion(template.EmphasisMotion, range)));
            if (range.Duration < limits.CueMinimumDuration)
            {
                warnings.Add(new(BelowMinimumDurationWarning, range));
            }
        }

        return result;
    }

    private static RationalTime NextStart(List<Token> tokens, int index, TimeRange cue) =>
        index < tokens.Count ? tokens[index].Range.Start : cue.End;
}
