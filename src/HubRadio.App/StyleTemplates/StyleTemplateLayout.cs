using System.Text.RegularExpressions;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>A rectangle in output pixels (right and bottom are exclusive-free edges: Right &gt; Left, Bottom &gt; Top).</summary>
public readonly record struct StyleTemplateRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public bool Contains(StyleTemplateRect inner) =>
        inner.Left >= Left && inner.Top >= Top && inner.Right <= Right && inner.Bottom <= Bottom;

    public StyleTemplateRect Intersect(StyleTemplateRect other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

/// <summary>PARAM-005: the margins a vertical output keeps free of subtitles and telops.</summary>
public sealed record StyleTemplateMargins(int Top, int Bottom, int Left, int Right)
{
    public static readonly StyleTemplateMargins Vertical = new(250, 480, 160, 160);
}

/// <summary>FR-138: the three tiers of a 1080×1920 output.</summary>
public sealed record StyleTemplateVerticalFrame(StyleTemplateRect TitleTier, StyleTemplateRect VideoTier,
    StyleTemplateRect BandRow, StyleTemplateRect CaptionTier);

/// <summary>FR-138: where a telop was placed.</summary>
public sealed record StyleTemplatePlacedTelop(StyleTemplateFreeTelopKind Kind, string Text, TimeRange Range, StyleTemplateRect Rect);

/// <summary>FR-138: where the text of a speaker's name or title came from. Only the first two may be shown.</summary>
public enum StyleTemplatePersonSource
{
    UserInput,
    Confirmed,
    Unconfirmed,
}

/// <summary>FR-138: a speaker as the user entered or confirmed them. A system guess is <see cref="StyleTemplatePersonSource.Unconfirmed"/>.</summary>
public sealed record StyleTemplateSpeaker(
    string? Name, StyleTemplatePersonSource NameSource, string? Title, StyleTemplatePersonSource TitleSource);

/// <summary>FR-138: a speaker's turn with the origin of the entered or confirmed values.</summary>
public sealed record StyleTemplateSpeakerTurn(StyleTemplateSpeaker Speaker, TimeRange Range, OriginRef Origin);

/// <summary>FR-138: a topic that starts at a point of the output and lasts until the next topic.</summary>
public sealed record StyleTemplateTopic(string Text, RationalTime Start, StyleTemplateTextSource Source, OriginRef Origin);

/// <summary>
/// FR-138: the talk-show side telop and name band, the vertical three-tier frame, and the PARAM-005 margins. It places
/// telops and never writes text of its own.
/// </summary>
public static partial class StyleTemplateLayout
{
    public const int VerticalWidth = 1080;
    public const int VerticalHeight = 1920;

    /// <summary>FR-138: one vertical screen of subtitle holds at most this many characters.</summary>
    public const int VerticalCaptionMaximumCharacters = 20;

    /// <summary>FR-138 AC-158 (PARAM-010 range): the bottom tier shows at least this many characters per screen where the text allows.</summary>
    public const int VerticalCaptionMinimumCharacters = 15;

    private const int TitleTierHeight = 300;
    private const int TitleTextHeight = 195;
    private const int VideoTierHeight = 580;
    private const int BandRowHeight = 120;
    private const int CaptionTierHeight = 170;
    private const int SideTelopWidth = 420;
    private const int SideTelopHeight = 95;

    [GeneratedRegex(@"^\s*(話者|発言者|Speaker|SPEAKER|speaker)\s*[0-9０-９A-Za-zＡ-Ｚ]{1,3}\s*$")]
    private static partial Regex AnonymousLabel();

    public static StyleTemplateRect SafeArea(int width, int height, StyleTemplateMargins margins) =>
        new(margins.Left, margins.Top, width - margins.Right, height - margins.Bottom);

    /// <summary>The area a vertical subtitle or telop may use: inside the margins and, when given, the FR-014 subtitle region.</summary>
    public static StyleTemplateRect AllowedArea(int width, int height, StyleTemplateMargins margins, StyleTemplateRect? region)
    {
        StyleTemplateRect safe = SafeArea(width, height, margins);
        return region is { } given ? safe.Intersect(given) : safe;
    }

    /// <summary>FR-138: title and hook on top (from 250 px), the source video in the middle, the subtitle below.</summary>
    public static StyleTemplateVerticalFrame VerticalFrame(StyleTemplateMargins? margins = null)
    {
        StyleTemplateMargins m = margins ?? StyleTemplateMargins.Vertical;
        StyleTemplateRect safe = SafeArea(VerticalWidth, VerticalHeight, m);
        int titleBottom = safe.Top + TitleTierHeight;
        int videoBottom = titleBottom + VideoTierHeight;
        int bandBottom = videoBottom + 10 + BandRowHeight;
        return new(new(safe.Left, safe.Top, safe.Right, titleBottom),
            new(0, titleBottom, VerticalWidth, videoBottom),
            new(safe.Left, videoBottom + 10, safe.Right, bandBottom),
            new(safe.Left, bandBottom + 10, safe.Right, Math.Min(safe.Bottom, bandBottom + 10 + CaptionTierHeight)));
    }

    /// <summary>Where a telop of this kind goes.</summary>
    public static StyleTemplateRect Rect(StyleTemplateFreeTelopKind kind, int width, int height, bool vertical,
        StyleTemplateMargins? margins = null)
    {
        if (vertical)
        {
            StyleTemplateVerticalFrame frame = VerticalFrame(margins);
            return kind switch
            {
                StyleTemplateFreeTelopKind.Title or StyleTemplateFreeTelopKind.Hook =>
                    frame.TitleTier with { Bottom = frame.TitleTier.Top + TitleTextHeight },
                StyleTemplateFreeTelopKind.Band => frame.BandRow with { Right = frame.BandRow.Left + 520 },
                StyleTemplateFreeTelopKind.Topic => new(frame.TitleTier.Right - SideTelopWidth, frame.TitleTier.Top + TitleTextHeight + 5,
                    frame.TitleTier.Right, frame.TitleTier.Top + TitleTextHeight + 5 + SideTelopHeight),
                _ => new(frame.CaptionTier.Left, frame.VideoTier.Top + 120, frame.CaptionTier.Right, frame.VideoTier.Top + 120 + SideTelopHeight),
            };
        }

        int left = width / 20;
        return kind switch
        {
            StyleTemplateFreeTelopKind.Band => new(left, height * 66 / 100, left + width * 35 / 100, height * 66 / 100 + height * 8 / 100),
            StyleTemplateFreeTelopKind.Topic => new(left, height * 2 / 100, left + width * 30 / 100, height * 9 / 100),
            StyleTemplateFreeTelopKind.Title or StyleTemplateFreeTelopKind.Hook => new(left, height * 6 / 100, width - left, height * 6 / 100 + height * 14 / 100),
            _ => new(left, height * 20 / 100, width - left, height * 20 / 100 + height * 12 / 100),
        };
    }

    public static StyleTemplatePlacedTelop Place(StyleTemplateFreeTelopInput telop, int width, int height, bool vertical,
        StyleTemplateMargins? margins = null) =>
        new(telop.Kind, telop.Text, telop.Range, Rect(telop.Kind, width, height, vertical, margins));

    /// <summary>
    /// FR-138 / PARAM-005: the placements that fall outside the margins or outside the subtitle region (empty when all
    /// are inside). Only vertical outputs have margins.
    /// </summary>
    public static IReadOnlyList<StyleTemplatePlacedTelop> OutsideAllowedArea(IEnumerable<StyleTemplatePlacedTelop> placed,
        int width, int height, StyleTemplateMargins margins, StyleTemplateRect? region)
    {
        StyleTemplateRect allowed = AllowedArea(width, height, margins, region);
        return placed.Where(item => !allowed.Contains(item.Rect)).ToArray();
    }

    /// <summary>FR-138: the text of the name band, or null when nothing may be shown (never guessed, never anonymous).</summary>
    public static string? BandText(StyleTemplateSpeaker speaker)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        static bool Shown(string? text, StyleTemplatePersonSource source) =>
            !string.IsNullOrWhiteSpace(text) && source is StyleTemplatePersonSource.UserInput or StyleTemplatePersonSource.Confirmed;
        if (!Shown(speaker.Name, speaker.NameSource) || AnonymousLabel().IsMatch(speaker.Name!))
        {
            return null;
        }

        string name = speaker.Name!.Trim();
        return Shown(speaker.Title, speaker.TitleSource) ? $"{name}　{speaker.Title!.Trim()}" : name;
    }

    /// <summary>FR-138: one band telop for each turn that has a name the user entered or confirmed.</summary>
    public static IReadOnlyList<StyleTemplateFreeTelopInput> BandTelops(IEnumerable<StyleTemplateSpeakerTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var result = new List<StyleTemplateFreeTelopInput>();
        foreach (StyleTemplateSpeakerTurn turn in turns)
        {
            if (BandText(turn.Speaker) is { } text)
            {
                result.Add(new(StyleTemplateFreeTelopKind.Band, text, turn.Range, StyleTemplateTextSource.UserInput, turn.Origin));
            }
        }

        return result;
    }

    /// <summary>
    /// FR-138 (FR-014): a side telop for each topic, shown from where the topic starts until the next topic starts (or the
    /// output ends). A topic never stays on screen after the next one starts.
    /// </summary>
    public static IReadOnlyList<StyleTemplateFreeTelopInput> TopicTelops(IEnumerable<StyleTemplateTopic> topics, RationalTime outputEnd)
    {
        ArgumentNullException.ThrowIfNull(topics);
        StyleTemplateTopic[] ordered = topics.OrderBy(static item => item.Start).ToArray();
        var result = new List<StyleTemplateFreeTelopInput>();
        for (int index = 0; index < ordered.Length; index++)
        {
            StyleTemplateTopic topic = ordered[index];
            RationalTime end = index + 1 < ordered.Length ? ordered[index + 1].Start : outputEnd;
            if (string.IsNullOrWhiteSpace(topic.Text) || end <= topic.Start)
            {
                continue;
            }

            result.Add(new(StyleTemplateFreeTelopKind.Topic, topic.Text, new TimeRange(topic.Start, end - topic.Start),
                topic.Source, topic.Origin));
        }

        return result;
    }

    /// <summary>The telops of this kind shown at this time.</summary>
    public static IReadOnlyList<T> ShownAt<T>(IEnumerable<T> items, Func<T, TimeRange> range, RationalTime time) =>
        items.Where(item => range(item).Start <= time && time < range(item).End).ToArray();

    /// <summary>
    /// FR-138 AC-158: the subtitle screens of the bottom tier — the displayed cue text joined in time order and cut into screens
    /// of <see cref="VerticalCaptionMinimumCharacters"/>–<see cref="VerticalCaptionMaximumCharacters"/> characters (the text is
    /// kept as it is; only a text shorter than the minimum, which cannot be longer, stays as one short screen). Each screen is
    /// shown for the time its characters take in the cues that hold them.
    /// </summary>
    public static IReadOnlyList<RecordedCue> BottomScreens(IReadOnlyList<RecordedCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var result = new List<RecordedCue>();
        foreach (RecordedCue cue in cues.OrderBy(static item => item.Start))
        {
            result.AddRange(CueScreens(cue));
        }

        return result;
    }

    /// <summary>FR-012 / FR-051: the shortest time a cue (and so a screen cut out of it) is shown — 5/6 second. It wins over the 15-20 split.</summary>
    public const double MinimumScreenSeconds = 5d / 6d;

    /// <summary>
    /// FR-138 AC-158: the screens of one cue. A text shorter than the minimum is not split; the split is not made when a screen
    /// would be shown shorter than the FR-012 / FR-051 minimum (that lower bound has priority). The same split is burned into the ASS.
    /// </summary>
    public static IReadOnlyList<RecordedCue> CueScreens(RecordedCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        string[] elements = Elements(System.Globalization.StringInfo.GetTextElementEnumerator(cue.Text));
        if (elements.Length == 0)
        {
            return [];
        }

        int screens = Math.Max(1, (elements.Length + VerticalCaptionMaximumCharacters - 1) / VerticalCaptionMaximumCharacters);
        // Fewer screens of 15 or more characters when the balanced size of the most screens would fall below the minimum.
        while (screens > 1 && elements.Length / screens < VerticalCaptionMinimumCharacters
            && (elements.Length + screens - 2) / (screens - 1) <= VerticalCaptionMaximumCharacters)
        {
            screens--;
        }

        if (screens > 1 && (cue.End - cue.Start) / screens < MinimumScreenSeconds - 1e-9)
        {
            screens = 1;
        }

        if (screens == 1)
        {
            return [cue with { Words = [] }];
        }

        int size = (elements.Length + screens - 1) / screens;
        double length = (cue.End - cue.Start) / elements.Length;
        var result = new List<RecordedCue>();
        int at = 0;
        foreach (string[] chunk in elements.Chunk(size))
        {
            result.Add(cue with { Start = cue.Start + (length * at), End = cue.Start + (length * (at + chunk.Length)), Text = string.Concat(chunk), Words = [] });
            at += chunk.Length;
        }

        return result;
    }

    /// <summary>FR-138: the vertical subtitle split so that no screen holds more than 20 characters (the text is kept as it is).</summary>
    public static IReadOnlyList<string> VerticalScreens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] elements = System.Globalization.StringInfo.GetTextElementEnumerator(text) is { } enumerator
            ? Elements(enumerator) : [];
        if (elements.Length == 0)
        {
            return [];
        }

        int screens = (elements.Length + VerticalCaptionMaximumCharacters - 1) / VerticalCaptionMaximumCharacters;
        int size = (elements.Length + screens - 1) / screens;
        return elements.Chunk(size).Select(static chunk => string.Concat(chunk)).ToArray();
    }

    /// <summary>FR-138: the source ranges of an output that opens with the moment FR-011 chose, then the body.</summary>
    public static IReadOnlyList<TimeRange> HookFirst(TimeRange hook, IEnumerable<TimeRange> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return [hook, .. body];
    }

    public static bool StartsWithHook(IReadOnlyList<TimeRange> orderedRanges, TimeRange hook) =>
        orderedRanges.Count > 0 && orderedRanges[0].Equals(hook);

    private static string[] Elements(System.Globalization.TextElementEnumerator enumerator)
    {
        var items = new List<string>();
        while (enumerator.MoveNext())
        {
            items.Add((string)enumerator.Current);
        }

        return [.. items];
    }
}
