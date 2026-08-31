using System.Text;

namespace HubRadio.Infrastructure.Subtitles;

public static class WebVttSubtitleWriter
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
        var document = new StringBuilder("WEBVTT\n\n");
        foreach (PreparedSubtitleCue cue in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleWriterContract.ValidateBlockText(cue.Source.Text);
            _ = document.Append(cue.Source.Cue.Id.ToString()).Append('\n');
            _ = document
                .Append(FormatTimestamp(cue.StartUnits))
                .Append(" --> ")
                .Append(FormatTimestamp(cue.EndUnits))
                .Append('\n');
            _ = document
                .Append("<c.")
                .Append(SubtitleWriterContract.StyleClass(cue.Source.Style))
                .Append('>')
                .Append(SubtitleWriterContract.EscapeHtmlText(cue.Source.Text))
                .Append("</c>\n\n");
        }

        return SubtitleWriterContract.CreateFile(
            SubtitleFileFormat.WebVtt,
            prepared.Length,
            document,
            cancellationToken);
    }

    private static string FormatTimestamp(long units) =>
        SubtitleWriterContract.FormatTimestamp(
            units,
            TimestampRate,
            '.',
            fractionalDigits: 3,
            padHours: true);
}