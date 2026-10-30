using HubRadio.App.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-142: the timed words of one cue, or none (the cue is then shown as a whole) with the reason.</summary>
public sealed record CueWordTimingResult(IReadOnlyList<StyleTemplateWord> Words, string? Warning)
{
    public bool HasWordTiming => Words.Count > 0;
}

/// <summary>
/// FR-142: keeps word-level start and end times on a cue. A word's time lies inside the cue, the words concatenate to the cue text
/// exactly (nothing is added or changed, FR-059) and a cue whose words cannot be established is shown as a whole with a warning:
/// the cue time is never divided by the number of words.
/// </summary>
public static class CueWordTiming
{
    public const string MissingWarning = StyleTemplateApplier.MissingWordTimingWarning;
    public const string RealignedPartlyWarning = "word-timing-not-mapped";

    /// <summary>The words (in output time) that make up <paramref name="cue"/>; empty with a warning when they do not.</summary>
    public static CueWordTimingResult For(StandardDisplayedCueText cue, IEnumerable<StyleTemplateWord>? candidates)
    {
        ArgumentNullException.ThrowIfNull(cue);
        StyleTemplateWord[] inside = (candidates ?? [])
            .Where(word => word.Range.Start >= cue.OutputRange.Start && word.Range.End <= cue.OutputRange.End && word.Range.Duration > default(RationalTime))
            .OrderBy(static word => word.Range.Start).ToArray();
        return IsValid(cue, inside) ? new(inside, null) : new([], MissingWarning);
    }

    /// <summary>True when the words are in order, do not overlap, lie inside the cue and concatenate to its text.</summary>
    public static bool IsValid(StandardDisplayedCueText cue, IReadOnlyList<StyleTemplateWord> words)
    {
        if (words.Count == 0 || string.Concat(words.Select(static word => word.Text)) != cue.Text)
        {
            return false;
        }

        RationalTime previousEnd = cue.OutputRange.Start;
        foreach (StyleTemplateWord word in words)
        {
            if (word.Range.Start < previousEnd || word.Range.End > cue.OutputRange.End || word.Range.Duration <= default(RationalTime))
            {
                return false;
            }

            previousEnd = word.Range.End;
        }

        return true;
    }

    /// <summary>
    /// The user corrected the cue text: the time of each word is attached to the corrected word it still matches, in order. When a
    /// corrected word has no matching timed word the whole cue falls back to the cue-level display (and a warning), never to a guess.
    /// </summary>
    public static CueWordTimingResult Realign(StandardDisplayedCueText cue, IReadOnlyList<StyleTemplateWord> previousWords, string correctedText)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(previousWords);
        string text = correctedText ?? string.Empty;
        if (previousWords.Count == 0 || text.Length == 0)
        {
            return new([], MissingWarning);
        }

        var result = new List<StyleTemplateWord>();
        int offset = 0;
        foreach (StyleTemplateWord word in previousWords)
        {
            int at = text.IndexOf(word.Text, offset, StringComparison.Ordinal);
            if (at != offset)
            {
                // A changed or removed word: the text between is not covered by any timed word.
                return new([], RealignedPartlyWarning);
            }

            result.Add(word);
            offset += word.Text.Length;
        }

        if (offset != text.Length)
        {
            return new([], RealignedPartlyWarning);
        }

        return IsValid(cue with { Text = text }, result) ? new(result, null) : new([], RealignedPartlyWarning);
    }
}
