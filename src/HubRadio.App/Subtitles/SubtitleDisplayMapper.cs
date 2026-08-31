using HubRadio.Core.Subtitles;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Subtitles;

public static class SubtitleDisplayMapper
{
    public static SubtitleDisplaySourceCue MapSource(SubtitleCueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SubtitleDisplaySourceCue(snapshot.Cue, snapshot.Text);
    }

    public static SubtitleFileCue MapForExport(
        SubtitleCueSnapshot snapshot,
        SubtitleDisplayedCue displayedCue)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(displayedCue);
        SubtitleDisplaySourceCue source = MapSource(snapshot);
        if (!displayedCue.Cue.Equals(snapshot.Cue)
            || displayedCue.SourceTextDigest != source.SourceTextDigest)
        {
            throw new ArgumentException(
                "Displayed subtitle text does not match its source snapshot.",
                nameof(displayedCue));
        }

        SubtitleFileCueStyle style = snapshot.Style switch
        {
            SubtitleCueStyle.Normal => SubtitleFileCueStyle.Normal,
            SubtitleCueStyle.Emphasis => SubtitleFileCueStyle.Emphasis,
            SubtitleCueStyle.Caution => SubtitleFileCueStyle.Caution,
            SubtitleCueStyle.Quote => SubtitleFileCueStyle.Quote,
            _ => throw new InvalidOperationException("Unknown subtitle style."),
        };
        return new SubtitleFileCue(
            snapshot.Cue,
            displayedCue.DisplayText,
            style);
    }
}