using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Subtitles;

public enum SubtitleCueDisplayMode
{
    Full,
    Selected,
}

public enum SubtitlePunctuationDisplay
{
    Preserve,
    HideJapaneseCommaAndPeriod,
}

public sealed class SubtitleDisplayProfile
{
    public SubtitleDisplayProfile(
        ContentDigest versionDigest,
        SubtitleCueDisplayMode cueMode,
        SubtitlePunctuationDisplay punctuationDisplay)
    {
        ArgumentNullException.ThrowIfNull(versionDigest);
        if (!Enum.IsDefined(cueMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cueMode),
                cueMode,
                "Unknown subtitle cue display mode.");
        }

        if (!Enum.IsDefined(punctuationDisplay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(punctuationDisplay),
                punctuationDisplay,
                "Unknown subtitle punctuation display mode.");
        }

        VersionDigest = versionDigest;
        CueMode = cueMode;
        PunctuationDisplay = punctuationDisplay;
    }

    public ContentDigest VersionDigest { get; }

    public SubtitleCueDisplayMode CueMode { get; }

    public SubtitlePunctuationDisplay PunctuationDisplay { get; }
}

public sealed class SubtitleDisplaySourceCue
{
    public SubtitleDisplaySourceCue(Cue cue, string text)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (cue.Kind != CueKind.Subtitle)
        {
            throw new ArgumentException(
                "A subtitle display source must contain a subtitle cue.",
                nameof(cue));
        }

        SubtitleDisplayContract.ValidateText(text);
        Cue = cue;
        Text = text;
        SourceTextDigest = ContentDigest.Compute(
            SubtitleDisplayContract.StrictUtf8.GetBytes(text));
    }

    public Cue Cue { get; }

    public string Text { get; }

    public ContentDigest SourceTextDigest { get; }
}

public sealed class SubtitleDisplayedCue
{
    internal SubtitleDisplayedCue(
        Cue cue,
        ContentDigest sourceTextDigest,
        string displayText,
        bool textChanged)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(sourceTextDigest);
        SubtitleDisplayContract.ValidateText(displayText);
        Cue = cue;
        SourceTextDigest = sourceTextDigest;
        DisplayText = displayText;
        TextChanged = textChanged;
    }

    public Cue Cue { get; }

    public ContentDigest SourceTextDigest { get; }

    public string DisplayText { get; }

    public bool TextChanged { get; }
}

public sealed class SubtitleDisplayResult
{
    internal SubtitleDisplayResult(
        ContentDigest profileVersionDigest,
        int sourceCueCount,
        IReadOnlyList<SubtitleDisplayedCue> displayedCues,
        IReadOnlyList<EntityId<CueIdTag>> omittedCueIds)
    {
        ArgumentNullException.ThrowIfNull(profileVersionDigest);
        ArgumentNullException.ThrowIfNull(displayedCues);
        ArgumentNullException.ThrowIfNull(omittedCueIds);
        if (sourceCueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceCueCount));
        }

        SubtitleDisplayedCue[] copiedDisplayed = displayedCues.ToArray();
        EntityId<CueIdTag>[] copiedOmitted = omittedCueIds.ToArray();
        var ids = new HashSet<EntityId<CueIdTag>>();
        if (copiedDisplayed.Any(static cue => cue is null)
            || copiedDisplayed.Any(cue => !ids.Add(cue.Cue.Id))
            || copiedOmitted.Any(static id => id is null)
            || copiedOmitted.Any(id => !ids.Add(id))
            || ids.Count > sourceCueCount)
        {
            throw new ArgumentException(
                "A subtitle display result contains inconsistent cue identities.",
                nameof(displayedCues));
        }

        ProfileVersionDigest = profileVersionDigest;
        SourceCueCount = sourceCueCount;
        DisplayedCues = Array.AsReadOnly(copiedDisplayed);
        OmittedCueIds = Array.AsReadOnly(copiedOmitted);
    }

    public ContentDigest ProfileVersionDigest { get; }

    public int SourceCueCount { get; }

    public IReadOnlyList<SubtitleDisplayedCue> DisplayedCues { get; }

    public IReadOnlyList<EntityId<CueIdTag>> OmittedCueIds { get; }
}

public enum SubtitleDisplayFailureReason
{
    InvalidSourceCue,
    DuplicateSourceCue,
    InvalidSelection,
    DuplicateSelection,
    UnknownSelectedCue,
    AmbiguousSelection,
    InvalidText,
}

public sealed class SubtitleDisplayException : Exception
{
    internal SubtitleDisplayException(SubtitleDisplayFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SubtitleDisplayFailureReason Reason { get; }

    private static string GetStableMessage(SubtitleDisplayFailureReason reason) =>
        reason switch
        {
            SubtitleDisplayFailureReason.InvalidSourceCue =>
                "The subtitle display source contains an invalid cue.",
            SubtitleDisplayFailureReason.DuplicateSourceCue =>
                "The subtitle display source contains a duplicate cue.",
            SubtitleDisplayFailureReason.InvalidSelection =>
                "The subtitle display selection contains an invalid cue identity.",
            SubtitleDisplayFailureReason.DuplicateSelection =>
                "The subtitle display selection contains a duplicate cue.",
            SubtitleDisplayFailureReason.UnknownSelectedCue =>
                "The subtitle display selection contains an unknown cue.",
            SubtitleDisplayFailureReason.AmbiguousSelection =>
                "Full subtitle display cannot include a cue selection.",
            SubtitleDisplayFailureReason.InvalidText =>
                "The subtitle display text is invalid.",
            _ => "The subtitle display policy failed.",
        };
}

public static class SubtitleDisplayPolicy
{
    public static SubtitleDisplayResult Apply(
        SubtitleDisplayProfile profile,
        IReadOnlyList<SubtitleDisplaySourceCue> sourceCues,
        IReadOnlyList<EntityId<CueIdTag>> selectedCueIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sourceCues);
        ArgumentNullException.ThrowIfNull(selectedCueIds);
        cancellationToken.ThrowIfCancellationRequested();

        SubtitleDisplaySourceCue[] sources = CopyAndOrderSources(
            sourceCues,
            cancellationToken);
        HashSet<EntityId<CueIdTag>> selected = CopySelection(
            profile,
            sources,
            selectedCueIds,
            cancellationToken);
        var displayed = new List<SubtitleDisplayedCue>(sources.Length);
        var omitted = new List<EntityId<CueIdTag>>();
        foreach (SubtitleDisplaySourceCue source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.CueMode == SubtitleCueDisplayMode.Selected
                && !selected.Contains(source.Cue.Id))
            {
                continue;
            }

            string displayText = profile.PunctuationDisplay switch
            {
                SubtitlePunctuationDisplay.Preserve => source.Text,
                SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod =>
                    HideJapaneseCommaAndPeriod(source.Text),
                _ => throw new InvalidOperationException(
                    "Unknown subtitle punctuation display mode."),
            };
            if (string.IsNullOrWhiteSpace(displayText))
            {
                omitted.Add(source.Cue.Id);
                continue;
            }

            try
            {
                displayed.Add(new SubtitleDisplayedCue(
                    source.Cue,
                    source.SourceTextDigest,
                    displayText,
                    !string.Equals(
                        displayText,
                        source.Text,
                        StringComparison.Ordinal)));
            }
            catch (ArgumentException)
            {
                throw Failure(SubtitleDisplayFailureReason.InvalidText);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SubtitleDisplayResult(
            profile.VersionDigest,
            sources.Length,
            displayed,
            omitted);
    }

    private static SubtitleDisplaySourceCue[] CopyAndOrderSources(
        IReadOnlyList<SubtitleDisplaySourceCue> sourceCues,
        CancellationToken cancellationToken)
    {
        var copied = new SubtitleDisplaySourceCue[sourceCues.Count];
        var ids = new HashSet<EntityId<CueIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleDisplaySourceCue? source = sourceCues[index];
            if (source is null)
            {
                throw Failure(SubtitleDisplayFailureReason.InvalidSourceCue);
            }

            if (!ids.Add(source.Cue.Id))
            {
                throw Failure(SubtitleDisplayFailureReason.DuplicateSourceCue);
            }

            copied[index] = source;
        }

        return copied
            .OrderBy(static source => source.Cue.Range.Start)
            .ThenBy(static source => source.Cue.Range.End)
            .ThenBy(
                static source => source.Cue.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<EntityId<CueIdTag>> CopySelection(
        SubtitleDisplayProfile profile,
        IReadOnlyList<SubtitleDisplaySourceCue> sources,
        IReadOnlyList<EntityId<CueIdTag>> selectedCueIds,
        CancellationToken cancellationToken)
    {
        if (profile.CueMode == SubtitleCueDisplayMode.Full
            && selectedCueIds.Count != 0)
        {
            throw Failure(SubtitleDisplayFailureReason.AmbiguousSelection);
        }

        var selected = new HashSet<EntityId<CueIdTag>>();
        var sourceIds = sources
            .Select(static source => source.Cue.Id)
            .ToHashSet();
        foreach (EntityId<CueIdTag>? id in selectedCueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id is null)
            {
                throw Failure(SubtitleDisplayFailureReason.InvalidSelection);
            }

            if (!selected.Add(id))
            {
                throw Failure(SubtitleDisplayFailureReason.DuplicateSelection);
            }

            if (!sourceIds.Contains(id))
            {
                throw Failure(SubtitleDisplayFailureReason.UnknownSelectedCue);
            }
        }

        return selected;
    }

    private static string HideJapaneseCommaAndPeriod(string source)
    {
        var result = new StringBuilder(source.Length);
        foreach (Rune rune in source.EnumerateRunes())
        {
            if (rune.Value is 0x3001 or 0x3002 or 0xFF61 or 0xFF64)
            {
                continue;
            }

            _ = result.Append(rune);
        }

        return result.ToString().Trim();
    }

    private static SubtitleDisplayException Failure(
        SubtitleDisplayFailureReason reason) =>
        new(reason);
}

internal static class SubtitleDisplayContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateText(string value)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(value);
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Any(static character =>
                    char.IsControl(character)
                    && character is not ('\t' or '\r' or '\n')))
            {
                throw new ArgumentException("Invalid subtitle display text.");
            }

            _ = StrictUtf8.GetByteCount(value);
        }
        catch (ArgumentException)
        {
            throw new SubtitleDisplayException(
                SubtitleDisplayFailureReason.InvalidText);
        }
    }
}