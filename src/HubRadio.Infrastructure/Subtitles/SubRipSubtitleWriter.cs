using System.Globalization;
using System.Text;

namespace HubRadio.Infrastructure.Subtitles;

public static class SubRipSubtitleWriter
{
    private const long TimestampRate = 1000;

    public static SubtitleExportFile Write(
        IReadOnlyList<SubtitleFileCue> cues,
        CancellationToken cancellationToken = default)
    {
        PreparedSubtitleCue[] prepared = SubtitleWriterContract.Prepare(
            cues,
            TimestampRate,
            cancellationToken);
        var document = new StringBuilder();
        for (int index = 0; index < prepared.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedSubtitleCue cue = prepared[index];
            SubtitleWriterContract.ValidateBlockText(cue.Source.Text);
            _ = document
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append('\n')
                .Append(FormatTimestamp(cue.StartUnits))
                .Append(" --> ")
                .Append(FormatTimestamp(cue.EndUnits))
                .Append('\n')
                .Append(SubtitleWriterContract.EscapeHtmlText(cue.Source.Text))
                .Append("\n\n");
        }

        return SubtitleWriterContract.CreateFile(
            SubtitleFileFormat.SubRip,
            prepared.Length,
            document,
            cancellationToken);
    }

    private static string FormatTimestamp(long units) =>
        SubtitleWriterContract.FormatTimestamp(
            units,
            TimestampRate,
            ',',
            fractionalDigits: 3,
            padHours: true);
}