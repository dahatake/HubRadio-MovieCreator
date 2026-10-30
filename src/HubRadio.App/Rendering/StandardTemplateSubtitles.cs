using System.Globalization;
using System.Text;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Subtitles;
using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Rendering;

/// <summary>FR-137: the ASS override tags that make a template motion (about half a second) and color.</summary>
internal static class StandardTemplateAss
{
    internal static long Centiseconds(RationalTime time) =>
        (long)Math.Round((decimal)time.Value * 100m / time.Rate, MidpointRounding.AwayFromZero);

    internal static string Timestamp(long centiseconds)
    {
        long hours = centiseconds / 360000;
        long minutes = centiseconds / 6000 % 60;
        long seconds = centiseconds / 100 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{centiseconds % 100:00}");
    }

    /// <summary>Tags for the motion of a telop that starts with its dialogue (times are relative to the start).</summary>
    internal static string MotionTags(StyleTemplateMotion? motion)
    {
        if (motion is null)
        {
            return "";
        }

        int length = Math.Max(1, (int)Math.Min(500, Centiseconds(motion.Range.Duration) * 10));
        int from = (int)Math.Round(motion.FromScale * 100);
        return motion.Kind switch
        {
            StyleTemplateMotionKind.Fade => FormattableString.Invariant($"\\fad({length / 2},0)"),
            StyleTemplateMotionKind.ShrinkFromLarge or StyleTemplateMotionKind.Pop =>
                FormattableString.Invariant($"\\fscx{from}\\fscy{from}\\t(0,{length},\\fscx100\\fscy100)"),
            StyleTemplateMotionKind.Shake => FormattableString.Invariant(
                $"\\frz3\\t(0,{length / 3},\\frz-3)\\t({length / 3},{length * 2 / 3},\\frz3)\\t({length * 2 / 3},{length},\\frz0)"),
            StyleTemplateMotionKind.Rotate => FormattableString.Invariant($"\\frz-15\\t(0,{length},\\frz0)"),
            _ => "",
        };
    }

    internal static string Colors(string text, string outline) =>
        $"\\1c{StyleTemplateColor.ToAss(text)}\\3c{StyleTemplateColor.ToAss(outline)}\\4c{StyleTemplateColor.ToAss(outline)}";
}

/// <summary>
/// FR-136 / FR-137 / FR-138: the burned-in subtitle of one displayed cue as the template shows it: the template's text and
/// outline colors (4.5:1 on the background), the 1-3 word captions of the short-pop template with only the spoken word in the
/// emphasis color and shrinking from 130% to 100%, and, for a vertical output, a place inside the PARAM-005 margins.
/// </summary>
internal static class StandardTemplateSubtitles
{
    private const string Marker = ",,0,0,0,,";

    internal static SubtitleExportFile Write(SubtitleFileCue cue, AssSubtitleProfile profile, SubtitleLayoutResolution resolution,
        StyleTemplatePlan plan, bool vertical, StyleTemplateMargins? margins = null)
    {
        StandardSubtitlePlacement.Validate(profile, resolution);
        (int left, int top, int right, int bottom) = StandardSubtitlePlacement.Bounds(profile, resolution);
        if (vertical)
        {
            // FR-138: the vertical subtitle sits on the lower tier, inside the PARAM-005 margins and inside the subtitle region
            // (the standard layouts' subtitle region is the whole frame).
            StyleTemplateRect allowed = StyleTemplateLayout.AllowedArea(profile.PlayResolutionWidth, profile.PlayResolutionHeight,
                margins ?? StyleTemplateMargins.Vertical, new StyleTemplateRect(0, 0, profile.PlayResolutionWidth, profile.PlayResolutionHeight))
                .Intersect(StyleTemplateLayout.VerticalFrame(margins).CaptionTier);
            if (allowed.IsEmpty || profile.PlayResolutionWidth != StyleTemplateLayout.VerticalWidth
                || profile.PlayResolutionHeight != StyleTemplateLayout.VerticalHeight)
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
            }

            (left, top, right, bottom) = (allowed.Left, allowed.Top, allowed.Right, allowed.Bottom);
        }

        SubtitleExportFile plain = StandardSubtitlePlacement.WriteAtBounds(cue, profile, left, top, right, bottom);
        StyleTemplateCaption[] captions = plan.Captions
            .Where(item => item.Range.Start >= cue.Cue.Range.Start && item.Range.End <= cue.Cue.Range.End)
            .OrderBy(static item => item.Range.Start).ToArray();
        StyleTemplateFreeTelop[] dialogues = plan.FreeTelops
            .Where(item => item.Input.Kind == StyleTemplateFreeTelopKind.Dialogue
                && item.Input.Range.Start >= cue.Cue.Range.Start && item.Input.Range.Start < cue.Cue.Range.End)
            .OrderBy(static item => item.Input.Range.Start).ToArray();
        if (captions.Length == 0 && dialogues.Length == 0)
        {
            return plain;
        }

        string document = Encoding.UTF8.GetString(plain.GetUtf8Bytes());
        int dialogue = document.IndexOf("\nDialogue: ", StringComparison.Ordinal);
        int markerAt = dialogue < 0 ? -1 : document.IndexOf(Marker, dialogue, StringComparison.Ordinal);
        if (markerAt < 0)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        }

        string[] head = document[(dialogue + 1)..markerAt].Split(',');
        string style = head[3];
        string placement = FormattableString.Invariant(
            $"\\an5\\pos({(left + (long)right) / 2m},{(top + (long)bottom) / 2m})\\clip({left},{top},{right},{bottom})\\q2");
        // FR-139: only a bundled typeface is named (\fn); a family of the user's PC never reaches the subtitle.
        string font = FontTag(plan);
        var lines = new StringBuilder();
        foreach (StyleTemplateCaption caption in captions)
        {
            long start = StandardTemplateAss.Centiseconds(caption.Range.Start);
            long end = StandardTemplateAss.Centiseconds(caption.Range.End);
            string[] foregrounds = [plan.TextColor, .. caption.Highlights.Select(static item => item.Color)];
            string background = StyleTemplateColor.EnsureBackground(foregrounds, plan.OutlineColor);
            _ = lines.Append("Dialogue: 0,").Append(StandardTemplateAss.Timestamp(start)).Append(',')
                .Append(StandardTemplateAss.Timestamp(end)).Append(',').Append(style).Append(Marker)
                .Append('{').Append(placement).Append(font).Append(StandardTemplateAss.Colors(plan.TextColor, background))
                .Append(StandardTemplateAss.MotionTags(caption.Motion)).Append('}')
                .Append(CaptionText(caption, plan.TextColor, start)).Append('\n');
        }

        foreach (StyleTemplateFreeTelop line in dialogues)
        {
            // FR-137 (6): the line sits to the right of the standing picture and takes the color of its character.
            long start = StandardTemplateAss.Centiseconds(line.Input.Range.Start);
            long end = StandardTemplateAss.Centiseconds(line.Input.Range.End);
            int width = right - left;
            int height = bottom - top;
            int textLeft = left + (int)Math.Round(width * (StyleTemplateApplier.PortraitLeft + StyleTemplateApplier.PortraitWidth + 0.02));
            int textTop = top + (int)Math.Round(height * StyleTemplateApplier.PortraitTop);
            int textBottom = top + (int)Math.Round(height * (StyleTemplateApplier.PortraitTop + StyleTemplateApplier.PortraitHeight));
            string dialogueBackground = StyleTemplateColor.EnsureBackground(line.Color, plan.OutlineColor);
            string dialoguePlacement = FormattableString.Invariant(
                $"\\an5\\pos({(textLeft + (long)right) / 2m},{(textTop + (long)textBottom) / 2m})\\clip({textLeft},{textTop},{right},{textBottom})\\q2");
            _ = lines.Append("Dialogue: 0,").Append(StandardTemplateAss.Timestamp(start)).Append(',')
                .Append(StandardTemplateAss.Timestamp(end)).Append(',').Append(style).Append(Marker)
                .Append('{').Append(dialoguePlacement).Append(font).Append(StandardTemplateAss.Colors(line.Color, dialogueBackground))
                .Append(StandardTemplateAss.MotionTags(line.Motion)).Append('}')
                .Append(SubtitleWriterContract.EscapeAssText(line.Input.Text)).Append('\n');
        }

        string result = document[..(dialogue + 1)] + lines.ToString().TrimEnd('\n') + "\n";
        return new SubtitleExportFile(plain.Format, plain.CueCount, plain.TimestampRuleVersion,
            new UTF8Encoding(false, true).GetBytes(result));
    }

    private static string FontTag(StyleTemplatePlan plan)
    {
        string? family = plan.FontFamilies.FirstOrDefault(StyleTemplateFonts.IsBundledFamily);
        return family is null ? string.Empty : "\\fn" + family;
    }

    private static string CaptionText(StyleTemplateCaption caption, string baseColor, long dialogueStart)
    {
        bool safe = caption.Text.IndexOfAny(['{', '}', '\\', '\n', '\r']) < 0;
        if (!safe)
        {
            return SubtitleWriterContract.EscapeAssText(caption.Text);
        }

        SubtitleWriterContract.ValidateAssText(caption.Text);
        if (caption.Highlights.Count == 0)
        {
            return caption.Text;
        }

        // Only the word that is being spoken takes the emphasis color (and the shrink); the others keep the text color.
        var text = new StringBuilder();
        int index = 0;
        foreach (StyleTemplateHighlight highlight in caption.Highlights.OrderBy(static item => item.Start))
        {
            if (highlight.Start < index || highlight.Start + highlight.Length > caption.Text.Length)
            {
                continue;
            }

            _ = text.Append(caption.Text, index, highlight.Start - index);
            long on = StandardTemplateAss.Centiseconds(highlight.Range.Start) - dialogueStart;
            long off = StandardTemplateAss.Centiseconds(highlight.Range.End) - dialogueStart;
            long shrink = Math.Min(50, Math.Max(1, off - on));
            _ = text.Append(string.Create(CultureInfo.InvariantCulture,
                $"{{\\1c{StyleTemplateColor.ToAss(baseColor)}\\t({on * 10},{on * 10 + 1},\\1c{StyleTemplateColor.ToAss(highlight.Color)}\\fscx130\\fscy130)"
                + $"\\t({on * 10 + 1},{(on + shrink) * 10},\\fscx100\\fscy100)\\t({off * 10},{off * 10 + 1},\\1c{StyleTemplateColor.ToAss(baseColor)})}}"))
                .Append(caption.Text, highlight.Start, highlight.Length);
            index = highlight.Start + highlight.Length;
        }

        _ = text.Append(caption.Text, index, caption.Text.Length - index);
        return text.ToString();
    }
}
