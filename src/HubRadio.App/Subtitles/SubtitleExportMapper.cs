using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Subtitles;

public static class SubtitleExportMapper
{
    public static SubtitleFileCue Map(SubtitleCueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SubtitleFileCueStyle style = snapshot.Style switch
        {
            SubtitleCueStyle.Normal => SubtitleFileCueStyle.Normal,
            SubtitleCueStyle.Emphasis => SubtitleFileCueStyle.Emphasis,
            SubtitleCueStyle.Caution => SubtitleFileCueStyle.Caution,
            SubtitleCueStyle.Quote => SubtitleFileCueStyle.Quote,
            _ => throw new InvalidOperationException(
                "Unknown subtitle cue style."),
        };
        return new SubtitleFileCue(snapshot.Cue, snapshot.Text, style);
    }
}