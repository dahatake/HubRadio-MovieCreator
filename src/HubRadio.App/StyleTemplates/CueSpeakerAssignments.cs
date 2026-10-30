using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-140: whether a speaker assignment is only the system's draft or the user's confirmed decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeakerAssignmentState
{
    /// <summary>下書き: estimated by the system or edited but not yet confirmed. It never reaches the output.</summary>
    Draft,

    /// <summary>確定済み: the user confirmed (or corrected and confirmed) it. Only this reaches the output.</summary>
    Confirmed,
}

/// <summary>One displayed cue (by its 0-based position among the cues the output displays) and its speaker; null leaves it unassigned.</summary>
public sealed record CueSpeakerAssignment(int CueIndex, string? SpeakerId);

/// <summary>
/// FR-140: which registered character speaks each displayed cue of a Job. A system estimate is a draft until the user confirms or
/// corrects it (FR-139's draft rule); a cue without a speaker shows no standing picture and keeps the default subtitle color.
/// The assignment never carries text: the line stays a part of the subtitle cue (FR-137).
/// </summary>
public sealed record CueSpeakerAssignmentSet(IReadOnlyList<CueSpeakerAssignment> Assignments, SpeakerAssignmentState State, bool Estimated)
{
    public const string DraftLabel = "下書き（未確認）";
    public const string ConfirmedLabel = "確定済み";

    public static CueSpeakerAssignmentSet Empty { get; } = new([], SpeakerAssignmentState.Draft, false);

    public bool IsConfirmed => State == SpeakerAssignmentState.Confirmed;

    public string Label => IsConfirmed ? ConfirmedLabel : DraftLabel;

    /// <summary>The system's guess: the registered characters take turns by cue. It is only ever a draft.</summary>
    public static CueSpeakerAssignmentSet Estimate(int cueCount, IReadOnlyList<CharacterPortrait> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        if (cueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cueCount));
        }

        if (characters.Count == 0)
        {
            return new([], SpeakerAssignmentState.Draft, true);
        }

        return new(Enumerable.Range(0, cueCount).Select(index => new CueSpeakerAssignment(index, characters[index % characters.Count].Id)).ToArray(),
            SpeakerAssignmentState.Draft, true);
    }

    /// <summary>The speaker the user (or the draft) gave a cue, whatever the state; null when unassigned.</summary>
    public string? SpeakerOf(int cueIndex) => Assignments.FirstOrDefault(item => item.CueIndex == cueIndex)?.SpeakerId;

    /// <summary>The speaker the output may use: only a confirmed assignment counts, so a draft never shows a wrong speaker.</summary>
    public string? ConfirmedSpeakerOf(int cueIndex) => IsConfirmed ? SpeakerOf(cueIndex) : null;

    /// <summary>The user changes (or clears, with null) one cue's speaker. The result is a draft again until it is confirmed.</summary>
    public CueSpeakerAssignmentSet Assign(int cueIndex, string? speakerId)
    {
        if (cueIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cueIndex));
        }

        List<CueSpeakerAssignment> items = Assignments.Where(item => item.CueIndex != cueIndex).ToList();
        if (!string.IsNullOrWhiteSpace(speakerId))
        {
            items.Add(new(cueIndex, speakerId));
        }

        return new(items.OrderBy(static item => item.CueIndex).ToArray(), SpeakerAssignmentState.Draft, Estimated);
    }

    /// <summary>
    /// The user confirms what is shown (確認して確定). A confirmed assignment is the user's own decision, not an estimate. An estimate
    /// asked for before the cues are known (no cue listed yet) stays "estimated" so that <see cref="Resolve"/> expands it over the
    /// cues of the run; the user confirmed that the system's turn-taking guess may be used.
    /// </summary>
    public CueSpeakerAssignmentSet Confirm() =>
        new(Assignments, SpeakerAssignmentState.Confirmed, Estimated && Assignments.Count == 0);

    /// <summary>
    /// The assignment to use for a run that displays <paramref name="cueCount"/> cues: a confirmed set as it is, a confirmed
    /// not-yet-expanded estimate expanded over the cues, and anything unconfirmed as is (it reaches no output).
    /// </summary>
    public CueSpeakerAssignmentSet Resolve(int cueCount, IReadOnlyList<CharacterPortrait> characters) =>
        IsConfirmed && Estimated && Assignments.Count == 0
            ? Estimate(cueCount, characters).Confirm() with { Estimated = false }
            : Prune(characters, cueCount);

    /// <summary>Drops speakers that are not registered characters (a removed character, or a changed cue count) and returns a draft if any dropped.</summary>
    public CueSpeakerAssignmentSet Prune(IReadOnlyList<CharacterPortrait> characters, int cueCount)
    {
        CueSpeakerAssignment[] kept = Assignments
            .Where(item => item.CueIndex < cueCount && item.SpeakerId is not null && characters.Any(c => c.Id == item.SpeakerId)).ToArray();
        return kept.Length == Assignments.Count ? this : new(kept, SpeakerAssignmentState.Draft, Estimated);
    }

    /// <summary>
    /// The dialogue lines of the confirmed assignment: the text and time are those of the displayed cue (nothing is added), the
    /// speaker is the assigned character. An unconfirmed or unassigned cue yields no line.
    /// </summary>
    public IReadOnlyList<StyleTemplateFreeTelopInput> DialogueLines(IReadOnlyList<StandardDisplayedCueText> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var lines = new List<StyleTemplateFreeTelopInput>();
        for (int index = 0; index < cues.Count; index++)
        {
            if (ConfirmedSpeakerOf(index) is { } speaker)
            {
                lines.Add(new(StyleTemplateFreeTelopKind.Dialogue, cues[index].Text, cues[index].OutputRange,
                    StyleTemplateTextSource.SubtitlePart, cues[index].Origin, Speaker: speaker));
            }
        }

        return lines;
    }
}

/// <summary>FR-140: keeps the speaker assignment in the Job's own directory (<c>speaker-assignments.json</c>), written atomically.</summary>
public static class CueSpeakerAssignmentStore
{
    public const string FileName = "speaker-assignments.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string jobDirectory, CueSpeakerAssignmentSet set)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(set);
        _ = Directory.CreateDirectory(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(set, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Null when nothing was assigned; a damaged file also reads as nothing (no speaker is invented).</summary>
    public static CueSpeakerAssignmentSet? Load(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            CueSpeakerAssignmentSet? set = JsonSerializer.Deserialize<CueSpeakerAssignmentSet>(File.ReadAllText(path));
            return set is null || set.Assignments is null || !Enum.IsDefined(set.State) ? null : set;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
