using System.Globalization;
using System.Text.Json;

namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-141: reads the JSON text the Job screen sends (<c>talkShowInput</c> of <c>pipeline.regenerate</c>, at most 8192 characters).
/// The text is never trusted: unknown keys, wrong types, too many entries and over-long strings are rejected.
/// </summary>
public static class TalkShowInputJson
{
    public const int MaximumLength = 8192;
    private const int MaximumTopics = 20;
    private const int MaximumSpeakers = 8;
    private const int MaximumText = 200;

    public static bool TryParse(string json, out TalkShowInput input)
    {
        input = TalkShowInput.Empty;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumLength)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(static item => item.Name is not ("topics" or "speakers"))
                || !root.TryGetProperty("topics", out JsonElement topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() > MaximumTopics
                || !root.TryGetProperty("speakers", out JsonElement speakers) || speakers.ValueKind != JsonValueKind.Array || speakers.GetArrayLength() > MaximumSpeakers)
            {
                return false;
            }

            var topicList = new List<TalkShowTopicInput>();
            int order = 0;
            foreach (JsonElement item in topics.EnumerateArray())
            {
                if (!Text(item, "id", out string id, required: true) || id.Length == 0 || !Text(item, "text", out string text)
                    || !Origin(item, "origin", out TalkShowValueOrigin origin) || !Flag(item, "confirmed", out bool confirmed))
                {
                    return false;
                }

                topicList.Add(new(id, text, order++, origin, confirmed));
            }

            var speakerList = new List<TalkShowSpeakerInput>();
            foreach (JsonElement item in speakers.EnumerateArray())
            {
                if (!Text(item, "id", out string id, required: true) || id.Length == 0 || !Text(item, "name", out string name)
                    || !Origin(item, "nameOrigin", out TalkShowValueOrigin nameOrigin) || !Flag(item, "nameConfirmed", out bool nameConfirmed)
                    || !Text(item, "title", out string title) || !Flag(item, "titleConfirmed", out bool titleConfirmed)
                    || !Text(item, "titleConfirmedOn", out string confirmedOn))
                {
                    return false;
                }

                speakerList.Add(new(id, name.Length == 0 ? null : name, nameOrigin, nameConfirmed,
                    title.Length == 0 ? null : title, titleConfirmed, confirmedOn.Length == 0 ? null : confirmedOn));
            }

            if (topicList.Select(static item => item.Id).Distinct().Count() != topicList.Count
                || speakerList.Select(static item => item.SpeakerId).Distinct().Count() != speakerList.Count)
            {
                return false;
            }

            input = new TalkShowInput(topicList, speakerList);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Text(JsonElement item, string name, out string value, bool required = false)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out JsonElement element))
        {
            return !required;
        }

        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text || text.Length > MaximumText
            || text.Any(char.IsControl))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool Flag(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool Origin(JsonElement item, string name, out TalkShowValueOrigin value)
    {
        value = TalkShowValueOrigin.UserEntered;
        if (!item.TryGetProperty(name, out JsonElement element))
        {
            return true;
        }

        switch (element.ValueKind == JsonValueKind.String ? element.GetString() : null)
        {
            case "user": return true;
            case "draft": value = TalkShowValueOrigin.SystemDraft; return true;
            default: return false;
        }
    }
}

/// <summary>FR-141 / FR-143: a rectangle in output pixels as recorded (x, y, width, height).</summary>
public sealed record TalkShowBox(int X, int Y, int Width, int Height)
{
    public static TalkShowBox From(StyleTemplateRect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    public StyleTemplateRect ToRect() => new(X, Y, X + Width, Y + Height);
}

/// <summary>A topic the user entered or confirmed (a topic nobody confirmed is not listed).</summary>
public sealed record TalkShowTopicRecord(string Id, string Text, double Start, double End);

/// <summary>A speaker as entered: the name and title are only given when they may be shown.</summary>
public sealed record TalkShowSpeakerRecord(string Id, string? Name, string? Title, bool NameConfirmed, bool TitleConfirmed, string? TitleConfirmedOn);

/// <summary>FR-141: a topic side telop, shown from the topic's start until the next topic starts.</summary>
public sealed record TalkShowSideTelopRecord(string TopicId, string Text, double Start, double End, string Position, bool Confirmed, TalkShowBox Box);

/// <summary>FR-141: a name band; the title is set only when it was confirmed with a date.</summary>
public sealed record TalkShowNameBandRecord(
    string SpeakerId, string Name, string? Title, bool TitleConfirmed, double Start, double End, string TextColor, TalkShowBox Box)
{
    public string Text => Title is null ? Name : $"{Name}　{Title}";
}

/// <summary>FR-141: what the output shows of the user's talk-show input, and the warnings of what was held back.</summary>
public sealed record TalkShowRenderPlan(
    IReadOnlyList<TalkShowTopicRecord> Topics, IReadOnlyList<TalkShowSpeakerRecord> Speakers,
    IReadOnlyList<TalkShowSideTelopRecord> SideTelops, IReadOnlyList<TalkShowNameBandRecord> NameBands, IReadOnlyList<string> Warnings)
{
    public static readonly TalkShowRenderPlan Empty = new([], [], [], [], []);
}

/// <summary>
/// FR-141 / FR-138: places the side telops and name bands of a talk-show Job from the user's input only. A draft nobody
/// confirmed, an anonymous label and an unconfirmed title are never shown; the held-back title is reported as a warning.
/// </summary>
public static class TalkShowRenderPlanner
{
    /// <summary>A name band is introduced at the start for at most this long (seconds); several speakers follow one another.</summary>
    public const double IntroductionSeconds = 3;

    public static TalkShowRenderPlan Plan(TalkShowInput input, double durationSeconds, int width, int height, string textColor,
        StyleTemplateMargins? margins = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (durationSeconds <= 0)
        {
            return TalkShowRenderPlan.Empty;
        }

        bool vertical = height > width;
        // The planner needs an origin per topic only to carry it; the user's input has none, so it plans the speakers only and
        // the topics are selected here.
        TalkShowPlan shown = TalkShowPlanner.Plan(input with { Topics = [] }, static id => throw new InvalidOperationException(id));
        TalkShowTopicInput[] topics = [.. input.Topics.OrderBy(static item => item.StartSeconds)
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text) && (item.Origin == TalkShowValueOrigin.UserEntered || item.Confirmed))];
        var warnings = new List<string>();
        foreach (TalkShowTopicInput hidden in input.Topics.Where(item => !string.IsNullOrWhiteSpace(item.Text) && !topics.Contains(item)))
        {
            warnings.Add($"話題の下書きが未確認のため出しません（{TalkShowPlan.TopicUnconfirmedWarning}）: {hidden.Id}");
        }

        var topicRecords = new List<TalkShowTopicRecord>();
        var sideTelops = new List<TalkShowSideTelopRecord>();
        StyleTemplateRect sideRect = StyleTemplateLayout.Rect(StyleTemplateFreeTelopKind.Topic, width, height, vertical, margins);
        int sideCapacity = Capacity(sideRect, width);
        TalkShowTopicInput[] fitting = [.. topics.Where(item => item.Text!.Trim().Length <= sideCapacity)];
        foreach (TalkShowTopicInput tooLong in topics.Except(fitting))
        {
            warnings.Add($"話題が長すぎて枠に入らないため出しません（{TalkShowPlan.TopicTooLongWarning}）: {tooLong.Id}");
        }

        topics = fitting;
        for (int index = 0; index < topics.Length; index++)
        {
            double start = durationSeconds * index / topics.Length;
            double end = durationSeconds * (index + 1) / topics.Length;
            topicRecords.Add(new(topics[index].Id, topics[index].Text!.Trim(), Round(start), Round(end)));
            sideTelops.Add(new(topics[index].Id, topics[index].Text!.Trim(), Round(start), Round(end), vertical ? "top-right" : "top-left",
                true, TalkShowBox.From(sideRect)));
        }

        TalkShowSpeakerInput[] ordered = [.. input.Speakers.OrderBy(static item => item.SpeakerId, StringComparer.Ordinal)];
        var speakerRecords = new List<TalkShowSpeakerRecord>();
        var bandSources = new List<(TalkShowSpeakerInput Input, StyleTemplateSpeaker Speaker)>();
        int bandCapacity = Capacity(StyleTemplateLayout.Rect(StyleTemplateFreeTelopKind.Band, width, height, vertical, margins), width);
        for (int index = 0; index < ordered.Length; index++)
        {
            StyleTemplateSpeaker speaker = shown.Speakers[index];
            TalkShowSpeakerInput entered = ordered[index];
            bool nameShown = StyleTemplateLayout.BandText(speaker with { Title = null }) is not null;
            bool titleShown = nameShown && speaker.TitleSource == StyleTemplatePersonSource.Confirmed && !string.IsNullOrWhiteSpace(speaker.Title);
            if (nameShown && StyleTemplateLayout.BandText(titleShown ? speaker : speaker with { Title = null })!.Length > bandCapacity)
            {
                warnings.Add($"名前・肩書きが長すぎて枠に入らないため出しません（{TalkShowPlan.BandTooLongWarning}）: {entered.SpeakerId}");
                nameShown = false;
                titleShown = false;
            }
            speakerRecords.Add(new(entered.SpeakerId, nameShown ? speaker.Name!.Trim() : null, titleShown ? speaker.Title!.Trim() : null,
                nameShown, titleShown, titleShown ? entered.TitleConfirmedOn : null));
            if (nameShown)
            {
                bandSources.Add((entered, speaker));
            }

            if (nameShown && !string.IsNullOrWhiteSpace(entered.Title) && !titleShown)
            {
                warnings.Add(entered.TitleConfirmed
                    ? $"肩書きの確認日がないため出しません（{TalkShowPlan.TitleDateMissingWarning}）: {entered.SpeakerId}"
                    : $"肩書きが未確認のため出しません（{TalkShowPlan.TitleUnconfirmedWarning}）: {entered.SpeakerId}");
            }
        }

        var bands = new List<TalkShowNameBandRecord>();
        if (bandSources.Count > 0)
        {
            StyleTemplateRect bandRect = StyleTemplateLayout.Rect(StyleTemplateFreeTelopKind.Band, width, height, vertical, margins);
            double each = Math.Min(IntroductionSeconds, durationSeconds / bandSources.Count);
            for (int index = 0; index < bandSources.Count; index++)
            {
                (TalkShowSpeakerInput entered, StyleTemplateSpeaker speaker) = bandSources[index];
                bool titleShown = speakerRecords.Single(item => item.Id == entered.SpeakerId).TitleConfirmed;
                bands.Add(new(entered.SpeakerId, speaker.Name!.Trim(), titleShown ? speaker.Title!.Trim() : null, titleShown,
                    Round(each * index), Round(each * (index + 1)), textColor, TalkShowBox.From(bandRect)));
            }
        }

        return new(topicRecords, speakerRecords, sideTelops, bands, warnings);
    }

    private static double Round(double value) => double.Parse(value.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>How many characters a telop box holds (at most two lines, as the burn-in of a metadata telop allows).</summary>
    public static int Capacity(StyleTemplateRect box, int outputWidth)
    {
        int size = Math.Min(outputWidth >= 1920 ? 64 : 48, (box.Height - 12) / 2);
        return size < 20 ? 0 : 2 * Math.Max(1, (box.Width - 24) / size);
    }
}
