namespace HubRadio.Core.Media;

public enum MusicCueKind
{
    TitleCard,
    TopicCard,
    EndCard,
    Dialogue,
    Other,
}

public enum MusicUsageScope
{
    InternalOnly,
    External,
}

public enum MusicPolicyDomain
{
    MusicalFit,
    BrandFit,
    HumanListeningQuality,
    LegalCompliance,
    CopyrightClearance,
    SpeechUnderstanding,
    MixQuality,
}

public enum MusicPolicyEvaluationStatus
{
    NotEvaluated,
}

public enum MusicCueBlockReason
{
    None,
    UnsupportedCueKind,
    SpeechOverlap,
    DialogueMusicDisabled,
    TrackTooShort,
}

public sealed class MusicPlanningOptions
{
    public MusicPlanningOptions(
        MusicUsageScope usageScope,
        bool usesExternalProfile,
        bool dialogueBackgroundMusicEnabled,
        int dialogueDuckAmountDecibels)
    {
        if (!Enum.IsDefined(usageScope))
        {
            throw new ArgumentOutOfRangeException(nameof(usageScope));
        }

        if (usageScope != MusicUsageScope.InternalOnly)
        {
            throw new ArgumentException(
                "Music use is restricted to the internal scope.",
                nameof(usageScope));
        }

        if (usesExternalProfile)
        {
            throw new ArgumentException(
                "External music profiles are not supported.",
                nameof(usesExternalProfile));
        }

        if (dialogueDuckAmountDecibels != MusicPolicy.DialogueDuckAmountDecibels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dialogueDuckAmountDecibels),
                "The dialogue duck amount must match the closed bundled policy.");
        }

        UsageScope = usageScope;
        DialogueBackgroundMusicEnabled = dialogueBackgroundMusicEnabled;
        DialogueDuckAmountDecibels = dialogueDuckAmountDecibels;
    }

    public MusicUsageScope UsageScope { get; }

    public bool DialogueBackgroundMusicEnabled { get; }

    public int DialogueDuckAmountDecibels { get; }

    public int? AppliedDialogueRelativeGainDecibels =>
        DialogueBackgroundMusicEnabled ? -DialogueDuckAmountDecibels : null;

    public static MusicPlanningOptions Default { get; } = new(
        MusicUsageScope.InternalOnly,
        usesExternalProfile: false,
        dialogueBackgroundMusicEnabled: false,
        dialogueDuckAmountDecibels: MusicPolicy.DialogueDuckAmountDecibels);
}

public sealed class MusicCueEligibility
{
    public MusicCueEligibility(
        bool isEligible,
        MusicCueBlockReason blockReason)
    {
        if (!Enum.IsDefined(blockReason))
        {
            throw new ArgumentOutOfRangeException(nameof(blockReason));
        }

        if (isEligible != (blockReason == MusicCueBlockReason.None))
        {
            throw new ArgumentException(
                "Music cue eligibility must agree with its block reason.",
                nameof(blockReason));
        }

        IsEligible = isEligible;
        BlockReason = blockReason;
    }

    public bool IsEligible { get; }

    public MusicCueBlockReason BlockReason { get; }
}

public sealed class MusicPolicyEvaluation
{
    public MusicPolicyEvaluation(
        MusicPolicyDomain domain,
        MusicPolicyEvaluationStatus status)
    {
        if (!Enum.IsDefined(domain))
        {
            throw new ArgumentOutOfRangeException(nameof(domain));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Domain = domain;
        Status = status;
    }

    public MusicPolicyDomain Domain { get; }

    public MusicPolicyEvaluationStatus Status { get; }
}

public static class MusicPolicy
{
    public const int DialogueDuckAmountDecibels = 20;

    private static readonly IReadOnlyList<MusicPolicyEvaluation> Evaluations =
        Array.AsReadOnly(Enum.GetValues<MusicPolicyDomain>()
            .Select(static domain => new MusicPolicyEvaluation(
                domain,
                MusicPolicyEvaluationStatus.NotEvaluated))
            .ToArray());

    public static IReadOnlyList<MusicPolicyEvaluation> NotEvaluatedDomains =>
        Evaluations;

    public static MusicCueEligibility Evaluate(
        MusicCueKind cueKind,
        bool hasForegroundSpeech,
        decimal cueDurationSeconds,
        decimal trackDurationSeconds,
        MusicPlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(cueKind))
        {
            throw new ArgumentOutOfRangeException(nameof(cueKind));
        }

        if (cueDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cueDurationSeconds));
        }

        if (trackDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackDurationSeconds));
        }

        if (cueKind == MusicCueKind.Dialogue)
        {
            return options.DialogueBackgroundMusicEnabled
                ? DurationResult(cueDurationSeconds, trackDurationSeconds)
                : new MusicCueEligibility(
                    false,
                    MusicCueBlockReason.DialogueMusicDisabled);
        }

        if (cueKind is not (MusicCueKind.TitleCard or
            MusicCueKind.TopicCard or MusicCueKind.EndCard))
        {
            return new MusicCueEligibility(
                false,
                MusicCueBlockReason.UnsupportedCueKind);
        }

        if (hasForegroundSpeech)
        {
            return new MusicCueEligibility(false, MusicCueBlockReason.SpeechOverlap);
        }

        return DurationResult(cueDurationSeconds, trackDurationSeconds);
    }

    private static MusicCueEligibility DurationResult(
        decimal cueDurationSeconds,
        decimal trackDurationSeconds) =>
        trackDurationSeconds >= cueDurationSeconds
            ? new MusicCueEligibility(true, MusicCueBlockReason.None)
            : new MusicCueEligibility(false, MusicCueBlockReason.TrackTooShort);
}