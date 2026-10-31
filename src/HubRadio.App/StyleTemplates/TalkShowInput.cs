using System.Text.Json;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-141: where a typed value came from. A system draft is shown only after the user confirmed it.</summary>
public enum TalkShowValueOrigin
{
    UserEntered,
    SystemDraft,
}

/// <summary>FR-141: the text of a topic side telop with its confirmation state (seconds from the output start).</summary>
public sealed record TalkShowTopicInput(string Id, string? Text, double StartSeconds, TalkShowValueOrigin Origin, bool Confirmed);

/// <summary>
/// FR-141 (NFR-BR-002): a speaker's name and title as the user entered or confirmed them. The title has its own
/// confirmation state and the date (or recording point) it was confirmed at.
/// </summary>
public sealed record TalkShowSpeakerInput(
    string SpeakerId, string? Name, TalkShowValueOrigin NameOrigin, bool NameConfirmed,
    string? Title, bool TitleConfirmed, string? TitleConfirmedOn)
{
    /// <summary>A title may be confirmed only with the date (yyyy-MM-dd) or the recording point it is valid at.</summary>
    public bool HasConfirmationDate => !string.IsNullOrWhiteSpace(TitleConfirmedOn);
}

/// <summary>FR-141: everything the user typed for a talk-show Job. Saved in the Job's own directory.</summary>
public sealed record TalkShowInput(IReadOnlyList<TalkShowTopicInput> Topics, IReadOnlyList<TalkShowSpeakerInput> Speakers)
{
    public static readonly TalkShowInput Empty = new([], []);

    /// <summary>The same input with the speaker's name, title and title confirmation replaced (a changed confirmed value takes effect on the next build).</summary>
    public TalkShowInput WithSpeaker(TalkShowSpeakerInput speaker)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        return this with { Speakers = [.. Speakers.Where(item => item.SpeakerId != speaker.SpeakerId), speaker] };
    }

    public TalkShowInput WithTopic(TalkShowTopicInput topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return this with { Topics = [.. Topics.Where(item => item.Id != topic.Id), topic] };
    }
}

/// <summary>FR-141: what is shown in the product and what is held back (with the reason).</summary>
public sealed record TalkShowPlan(
    IReadOnlyList<StyleTemplateTopic> Topics, IReadOnlyList<StyleTemplateSpeaker> Speakers,
    IReadOnlyList<StyleTemplateWarning> Warnings)
{
    public const string TitleUnconfirmedWarning = "title-unconfirmed";
    public const string TitleDateMissingWarning = "title-confirmation-date-missing";
    public const string TopicUnconfirmedWarning = "topic-draft-unconfirmed";
    public const string TopicTooLongWarning = "topic-too-long";
    public const string BandTooLongWarning = "band-too-long";
}

/// <summary>FR-141 / FR-138: turns the user's input into the topics and speakers the layout may show.</summary>
public static class TalkShowPlanner
{
    public static TalkShowPlan Plan(TalkShowInput input, Func<string, OriginRef> origin)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(origin);
        var warnings = new List<StyleTemplateWarning>();
        var topics = new List<StyleTemplateTopic>();
        foreach (TalkShowTopicInput topic in input.Topics.OrderBy(static item => item.StartSeconds))
        {
            if (string.IsNullOrWhiteSpace(topic.Text))
            {
                continue;
            }

            if (topic.Origin == TalkShowValueOrigin.SystemDraft && !topic.Confirmed)
            {
                warnings.Add(new(TalkShowPlan.TopicUnconfirmedWarning, null));
                continue;
            }

            topics.Add(new(topic.Text.Trim(), TimeAt(topic.StartSeconds),
                topic.Origin == TalkShowValueOrigin.UserEntered ? StyleTemplateTextSource.UserInput : StyleTemplateTextSource.ConfirmedSummary,
                origin(topic.Id)));
        }

        var speakers = new List<StyleTemplateSpeaker>();
        foreach (TalkShowSpeakerInput speaker in input.Speakers.OrderBy(static item => item.SpeakerId, StringComparer.Ordinal))
        {
            // Name: typed by the user, or a draft the user confirmed. A draft nobody confirmed is Unconfirmed and never shown.
            StyleTemplatePersonSource nameSource = speaker.NameOrigin == TalkShowValueOrigin.UserEntered || speaker.NameConfirmed
                ? StyleTemplatePersonSource.UserInput : StyleTemplatePersonSource.Unconfirmed;
            // Title: only when confirmed (with a date); typing alone does not show it.
            StyleTemplatePersonSource titleSource = speaker.TitleConfirmed && speaker.HasConfirmationDate
                ? StyleTemplatePersonSource.Confirmed : StyleTemplatePersonSource.Unconfirmed;
            if (!string.IsNullOrWhiteSpace(speaker.Title) && titleSource != StyleTemplatePersonSource.Confirmed)
            {
                warnings.Add(new(speaker.TitleConfirmed ? TalkShowPlan.TitleDateMissingWarning : TalkShowPlan.TitleUnconfirmedWarning, null));
            }

            speakers.Add(new(speaker.Name, nameSource, speaker.Title, titleSource));
        }

        return new(topics, speakers, warnings);
    }

    private static RationalTime TimeAt(double seconds) => new((long)Math.Round(seconds * 1000), 1000);
}

/// <summary>FR-141: the user's input is kept in the Job's directory (<c>talk-show-input.json</c>), written atomically.</summary>
public static class TalkShowInputStore
{
    public const string FileName = "talk-show-input.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string jobDirectory, TalkShowInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(input);
        Directory.CreateDirectory(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(input, Options));
        File.Move(temporary, path, overwrite: true);
    }

    public static TalkShowInput Load(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<TalkShowInput>(File.ReadAllText(path)) ?? TalkShowInput.Empty : TalkShowInput.Empty;
    }
}
