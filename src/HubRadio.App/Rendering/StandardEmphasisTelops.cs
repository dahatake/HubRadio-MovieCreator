using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Rendering;

/// <summary>FR-059: one emphasis caption cut out of a displayed subtitle cue, shown during that cue.</summary>
public sealed record StandardEmphasisTelop(TimeRange OutputRange, string Text, OriginRef Origin, string Rule);

/// <summary>FR-060: which self-generated sound plays.</summary>
public enum StandardSoundEffectKind
{
    Transition,
    Emphasis,
}

/// <summary>FR-060: one sound effect at an output time, with the rule that placed it.</summary>
public sealed record StandardSoundEffectCue(StandardSoundEffectKind Kind, RationalTime OutputStart, string Rule);

/// <summary>A displayed subtitle cue in output time, the only input of the FR-059/FR-060 rules.</summary>
public sealed record StandardDisplayedCueText(TimeRange OutputRange, string Text, OriginRef Origin);

/// <summary>
/// FR-059 / FR-060: deterministic, offline rules. Telop text is always a verbatim part of a cue the
/// viewer already reads (no words are added or rewritten); sounds are placed at those telops and at
/// cues that open with a spoken transition phrase.
/// </summary>
public static class StandardEmphasisTelops
{
    public const string RuleVersion = "fr059-emphasis-telop.v1";
    public const string SoundEffectRuleVersion = "fr060-sound-effect.v1";
    public const int MaximumTelops = 32;
    public const int MaximumSoundEffects = 32;

    /// <summary>Words that usually carry the point of a sentence in Japanese talk.</summary>
    public static readonly IReadOnlyList<string> EmphasisWords =
        ["本当", "すごい", "大事", "重要", "ポイント", "結論", "つまり", "要するに", "一番", "絶対", "ありがとう"];

    /// <summary>Spoken openers that usually start a new topic.</summary>
    public static readonly IReadOnlyList<string> TransitionOpeners =
        ["じゃあ", "では", "さて", "次に", "まず", "続いて", "ところで", "それでは", "最後に"];

    public static readonly RationalTime SoundEffectDuration = new(3, 10);

    private const int MaximumWholeCueCharacters = 12;
    private const int MaximumCounterCharacters = 3;
    private static readonly RationalTime MinimumTelopSpacing = new(5, 1);
    private static readonly RationalTime MinimumSoundEffectSpacing = new(2, 1);

    /// <summary>few: one per minute, standard: one per 30 s, many: one per 15 s (at least 1, 2 and 3).</summary>
    public static int Limit(string amount, RationalTime duration)
    {
        ArgumentNullException.ThrowIfNull(amount);
        long seconds = duration <= default(RationalTime) ? 0 : duration.Value / duration.Rate;
        int limit = amount switch
        {
            "few" => (int)Math.Max(1, seconds / 60),
            "standard" => (int)Math.Max(2, seconds / 30),
            "many" => (int)Math.Max(3, seconds / 15),
            _ => throw new ArgumentOutOfRangeException(nameof(amount)),
        };
        return Math.Min(limit, MaximumTelops);
    }

    /// <summary>
    /// Number phrases first (all amounts), then cues with an emphasis word (standard and many), then short
    /// exclamations or questions (many). Telops never overlap and start at least 5 s apart.
    /// </summary>
    public static IReadOnlyList<StandardEmphasisTelop> Select(
        IReadOnlyList<StandardDisplayedCueText> cues, string? amount, RationalTime duration)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (amount is null)
        {
            return [];
        }

        int limit = Limit(amount, duration);
        StandardDisplayedCueText[] ordered = cues
            .Where(cue => cue.OutputRange.Duration > default(RationalTime) && cue.OutputRange.End <= duration)
            .OrderBy(static cue => cue.OutputRange.Start).ToArray();
        var candidates = new List<StandardEmphasisTelop>();
        foreach (StandardDisplayedCueText cue in ordered)
        {
            if (NumberPhrase(Plain(cue.Text)) is string phrase)
            {
                candidates.Add(new(cue.OutputRange, phrase, cue.Origin, RuleVersion + ":number"));
            }
        }

        if (amount is "standard" or "many")
        {
            foreach (StandardDisplayedCueText cue in ordered)
            {
                string text = Plain(cue.Text);
                // Start at the emphasis word so a cue split mid-sentence does not open with a fragment.
                int at = EmphasisWords.Select(word => text.IndexOf(word, StringComparison.Ordinal))
                    .Where(static index => index >= 0).DefaultIfEmpty(-1).Min();
                if (at >= 0 && text.Length - at <= MaximumWholeCueCharacters)
                {
                    candidates.Add(new(cue.OutputRange, text[at..], cue.Origin, RuleVersion + ":emphasis-word"));
                }
            }
        }

        if (amount == "many")
        {
            foreach (StandardDisplayedCueText cue in ordered)
            {
                string text = Plain(cue.Text);
                if (text.Length is > 1 and <= MaximumWholeCueCharacters && text[^1] is '！' or '!' or '？' or '?')
                {
                    candidates.Add(new(cue.OutputRange, text, cue.Origin, RuleVersion + ":exclamation-or-question"));
                }
            }
        }

        var chosen = new List<StandardEmphasisTelop>();
        foreach (StandardEmphasisTelop candidate in candidates)
        {
            if (chosen.Count >= limit)
            {
                break;
            }

            if (chosen.Any(item => item.OutputRange.Overlaps(candidate.OutputRange)
                || Distance(item.OutputRange.Start, candidate.OutputRange.Start) < MinimumTelopSpacing))
            {
                continue;
            }

            chosen.Add(candidate);
        }

        return chosen.OrderBy(static item => item.OutputRange.Start).ToArray();
    }

    /// <summary>
    /// An emphasis sound at every telop and a transition sound at every cue that opens with a transition
    /// phrase, at least 2 s apart, never at time zero and always fully inside the output.
    /// </summary>
    public static IReadOnlyList<StandardSoundEffectCue> PlanSoundEffects(bool enabled,
        IReadOnlyList<StandardDisplayedCueText> cues, IReadOnlyList<StandardEmphasisTelop> telops, RationalTime duration)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(telops);
        if (!enabled)
        {
            return [];
        }

        var proposed = telops
            .Select(static telop => new StandardSoundEffectCue(StandardSoundEffectKind.Emphasis, telop.OutputRange.Start,
                SoundEffectRuleVersion + ":emphasis-telop"))
            .Concat(cues.Where(static cue => TransitionOpeners.Any(opener => Plain(cue.Text).StartsWith(opener, StringComparison.Ordinal)))
                .Select(static cue => new StandardSoundEffectCue(StandardSoundEffectKind.Transition, cue.OutputRange.Start,
                    SoundEffectRuleVersion + ":transition-opener")))
            .OrderBy(static item => item.OutputStart).ThenBy(static item => item.Kind == StandardSoundEffectKind.Emphasis ? 0 : 1);
        var chosen = new List<StandardSoundEffectCue>();
        foreach (StandardSoundEffectCue cue in proposed)
        {
            if (chosen.Count >= MaximumSoundEffects)
            {
                break;
            }

            if (cue.OutputStart <= default(RationalTime) || cue.OutputStart + SoundEffectDuration > duration
                || chosen.Any(item => Distance(item.OutputStart, cue.OutputStart) < MinimumSoundEffectSpacing))
            {
                continue;
            }

            chosen.Add(cue);
        }

        return chosen.ToArray();
    }

    /// <summary>
    /// The first number in the text with an optional leading 「第」 and up to three following kanji, katakana
    /// or percent signs (the counter, for example 「10本」「第1回」「30%」). Null when there is no number.
    /// </summary>
    public static string? NumberPhrase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int index = 0;
        while (index < text.Length && !IsDigit(text[index]))
        {
            index++;
        }

        if (index == text.Length)
        {
            return null;
        }

        int start = index > 0 && text[index - 1] == '第' ? index - 1 : index;
        int end = index;
        while (end < text.Length && (IsDigit(text[end]) || (text[end] is '.' or '．' or ',' or '，'
            && end + 1 < text.Length && IsDigit(text[end + 1]))))
        {
            end++;
        }

        int counter = 0;
        while (end < text.Length && counter < MaximumCounterCharacters && IsCounter(text[end]))
        {
            end++;
            counter++;
            if (text[end - 1] is '%' or '％')
            {
                break;
            }
        }

        string phrase = text[start..end];
        return phrase.Length >= 2 ? phrase : null;
    }

    private static string Plain(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal).Trim();

    private static bool IsDigit(char value) => value is >= '0' and <= '9' or >= '０' and <= '９';

    private static bool IsCounter(char value) =>
        value is '%' or '％' or >= '\u30A0' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF';

    private static RationalTime Distance(RationalTime left, RationalTime right) => left > right ? left - right : right - left;
}
