using System.Globalization;
using System.Text;

namespace HubRadio.Infrastructure.Subtitles;

public static class AssSubtitleWriter
{
    private const long TimestampRate = 100;

    private const string StyleFormat =
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, "
        + "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, "
        + "ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
        + "Alignment, MarginL, MarginR, MarginV, Encoding\n";

    public static SubtitleExportFile Write(
        IReadOnlyList<SubtitleFileCue> cues,
        AssSubtitleProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PreparedSubtitleCue[] prepared = SubtitleWriterContract.Prepare(
            cues,
            TimestampRate,
            cancellationToken);
        var document = new StringBuilder();
        _ = document
            .Append("[Script Info]\n")
            .Append("ScriptType: v4.00+\n")
            .Append("PlayResX: ")
            .Append(profile.PlayResolutionWidth.ToString(CultureInfo.InvariantCulture))
            .Append('\n')
            .Append("PlayResY: ")
            .Append(profile.PlayResolutionHeight.ToString(CultureInfo.InvariantCulture))
            .Append("\n\n[V4+ Styles]\n")
            .Append(StyleFormat);
        foreach (AssStyleDefinition style in profile.Styles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendStyle(document, style);
        }

        _ = document
            .Append("\n[Events]\n")
            .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (PreparedSubtitleCue cue in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = document
                .Append("Dialogue: 0,")
                .Append(FormatTimestamp(cue.StartUnits))
                .Append(',')
                .Append(FormatTimestamp(cue.EndUnits))
                .Append(',')
                .Append(cue.Source.Style.ToString())
                .Append(",,0,0,0,,")
                .Append(SubtitleWriterContract.EscapeAssText(cue.Source.Text))
                .Append('\n');
        }

        return SubtitleWriterContract.CreateFile(
            SubtitleFileFormat.AdvancedSubStationAlpha,
            prepared.Length,
            document,
            cancellationToken);
    }

    private static void AppendStyle(StringBuilder document, AssStyleDefinition style)
    {
        _ = document
            .Append("Style: ")
            .Append(style.Name).Append(',')
            .Append(style.FontName).Append(',')
            .Append(style.FontSize.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.PrimaryColor.ToAssValue()).Append(',')
            .Append(style.SecondaryColor.ToAssValue()).Append(',')
            .Append(style.OutlineColor.ToAssValue()).Append(',')
            .Append(style.BackgroundColor.ToAssValue()).Append(',')
            .Append(AssBoolean(style.Bold)).Append(',')
            .Append(AssBoolean(style.Italic)).Append(',')
            .Append(AssBoolean(style.Underline)).Append(',')
            .Append(AssBoolean(style.StrikeOut)).Append(',')
            .Append(style.ScaleX.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.ScaleY.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Spacing.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Angle.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(((int)style.BorderStyle).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.OutlineWidth.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.ShadowDepth.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(((int)style.Alignment).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Margins.Left.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Margins.Right.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Margins.Vertical.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(style.Encoding.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static string AssBoolean(bool value) => value ? "-1" : "0";

    private static string FormatTimestamp(long units) =>
        SubtitleWriterContract.FormatTimestamp(
            units,
            TimestampRate,
            '.',
            fractionalDigits: 2,
            padHours: false);
}