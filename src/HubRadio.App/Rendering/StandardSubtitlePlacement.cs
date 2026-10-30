using System.Numerics;
using System.Text;
using HubRadio.Core.Layout;
using HubRadio.Core.Subtitles;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Rendering;

/// <summary>
/// Uses the QC resolution for actual ASS placement. Clipping constrains rendered
/// pixels; it is not a glyph-fit/readability measurement or platform certification.
/// </summary>
internal static class StandardSubtitlePlacement
{
    internal static void Validate(AssSubtitleProfile profile, SubtitleLayoutResolution resolution)
    {
        (int left, int top, int right, int bottom) = Bounds(profile, resolution);
        foreach (AssStyleDefinition style in profile.Styles)
        {
            if (style.Angle != 0 || style.ScaleX != 100 || style.ScaleY != 100
                || style.FontSize + 2L * (style.OutlineWidth + (long)style.ShadowDepth) > bottom - top
                || style.Margins.Left + (long)style.Margins.Right >= right - left)
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
            }
        }
    }

    internal static SubtitleExportFile Write(SubtitleFileCue cue, AssSubtitleProfile profile,
        SubtitleLayoutResolution resolution)
    {
        Validate(profile, resolution);
        (int left, int top, int right, int bottom) = Bounds(profile, resolution);
        return WriteAtBounds(cue, profile, left, top, right, bottom);
    }

    internal static SubtitleExportFile WriteAtBounds(SubtitleFileCue cue, AssSubtitleProfile profile,
        int left, int top, int right, int bottom, string extraTags = "")
    {
        if (left < 0 || top < 0 || right <= left || bottom <= top
            || right > profile.PlayResolutionWidth || bottom > profile.PlayResolutionHeight)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        // Each file has exactly one Dialogue. The existing writer escapes all user text.
        SubtitleExportFile original = AssSubtitleWriter.Write([cue], profile);
        string text = Encoding.UTF8.GetString(original.GetUtf8Bytes());
        string placement = FormattableString.Invariant(
            $"{{\\an5\\pos({(left + (long)right) / 2m},{(top + (long)bottom) / 2m})\\clip({left},{top},{right},{bottom})\\q2{extraTags}}}");
        const string marker = ",,0,0,0,,";
        int dialogue = text.IndexOf("\nDialogue: ", StringComparison.Ordinal);
        int start = dialogue < 0 ? -1 : text.IndexOf(marker, dialogue, StringComparison.Ordinal);
        if (start < 0) throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        text = text.Insert(start + marker.Length, placement);
        return new SubtitleExportFile(original.Format, original.CueCount, original.TimestampRuleVersion,
            new UTF8Encoding(false, true).GetBytes(text));
    }

    internal static (int Left, int Top, int Right, int Bottom) Bounds(
        AssSubtitleProfile profile, SubtitleLayoutResolution resolution)
    {
        SubtitleLayoutRect rect = resolution.Plan?.Placement.Rect
            ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        int Pixel(RationalNumber coordinate, int extent)
        {
            BigInteger value = BigInteger.DivRem((BigInteger)coordinate.Numerator * extent,
                coordinate.Denominator, out BigInteger remainder);
            if (!remainder.IsZero || value < 0 || value > extent)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
            return (int)value;
        }

        int left = Pixel(rect.X, profile.PlayResolutionWidth);
        int top = Pixel(rect.Y, profile.PlayResolutionHeight);
        int width = Pixel(rect.Width, profile.PlayResolutionWidth);
        int height = Pixel(rect.Height, profile.PlayResolutionHeight);
        if (resolution.Status != SubtitleLayoutResolutionStatus.Resolved || width == 0 || height == 0
            || left + (long)width > profile.PlayResolutionWidth || top + (long)height > profile.PlayResolutionHeight)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        return (left, top, left + width, top + height);
    }
}