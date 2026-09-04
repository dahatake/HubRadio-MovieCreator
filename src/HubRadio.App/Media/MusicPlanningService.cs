using HubRadio.Core.Identity;
using HubRadio.Core.Media;

namespace HubRadio.App.Media;

public sealed class MusicCue
{
    public MusicCue(
        ContentDigest cueDigest,
        MusicCueKind kind,
        decimal durationSeconds,
        bool hasForegroundSpeech)
    {
        ArgumentNullException.ThrowIfNull(cueDigest);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        CueDigest = cueDigest;
        Kind = kind;
        DurationSeconds = durationSeconds;
        HasForegroundSpeech = hasForegroundSpeech;
    }

    public ContentDigest CueDigest { get; }

    public MusicCueKind Kind { get; }

    public decimal DurationSeconds { get; }

    public bool HasForegroundSpeech { get; }
}

public enum MusicAssignmentReason
{
    Assigned,
    CatalogUnavailable,
    UnsupportedCueKind,
    SpeechOverlap,
    DialogueMusicDisabled,
    NoTrackLongEnough,
}

public sealed class MusicCueAssignment
{
    internal MusicCueAssignment(
        ContentDigest cueDigest,
        ContentDigest? trackDigest,
        MusicAssignmentReason reason,
        int? relativeGainDecibels,
        bool loop,
        bool stretch)
    {
        ArgumentNullException.ThrowIfNull(cueDigest);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (loop || stretch)
        {
            throw new ArgumentException(
                "Music assignments do not permit looping or stretching.",
                loop ? nameof(loop) : nameof(stretch));
        }

        bool assigned = reason == MusicAssignmentReason.Assigned;
        if (assigned != (trackDigest is not null) ||
            (!assigned && relativeGainDecibels is not null) ||
            (relativeGainDecibels is not null &&
                relativeGainDecibels != -MusicPolicy.DialogueDuckAmountDecibels))
        {
            throw new ArgumentException(
                "Music assignment evidence is inconsistent.",
                nameof(reason));
        }

        CueDigest = cueDigest;
        TrackDigest = trackDigest;
        Reason = reason;
        RelativeGainDecibels = relativeGainDecibels;
        Loop = loop;
        Stretch = stretch;
    }

    public ContentDigest CueDigest { get; }

    public ContentDigest? TrackDigest { get; }

    public MusicAssignmentReason Reason { get; }

    public int? RelativeGainDecibels { get; }

    public bool Loop { get; }

    public bool Stretch { get; }
}

public sealed class MusicPlanningResult
{
    internal MusicPlanningResult(
        ContentDigest catalogDigest,
        IReadOnlyList<MusicCueAssignment> assignments,
        IReadOnlyList<MusicCatalogWarning> warnings,
        IReadOnlyList<MusicPolicyEvaluation> policyEvaluations)
    {
        CatalogDigest = catalogDigest;
        Assignments = assignments;
        Warnings = warnings;
        PolicyEvaluations = policyEvaluations;
    }

    public ContentDigest CatalogDigest { get; }

    public IReadOnlyList<MusicCueAssignment> Assignments { get; }

    public IReadOnlyList<MusicCatalogWarning> Warnings { get; }

    public IReadOnlyList<MusicPolicyEvaluation> PolicyEvaluations { get; }
}

public sealed class MusicPlanningService
{
    public MusicPlanningResult Plan(
        MusicCatalog catalog,
        IReadOnlyList<MusicCue> cues,
        MusicPlanningOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        MusicCue[] orderedCues = CopyCues(cues);
        List<MusicCueAssignment> assignments = new(orderedCues.Length);
        foreach (MusicCue cue in orderedCues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            assignments.Add(Assign(catalog, cue, options));
        }

        return new MusicPlanningResult(
            catalog.CatalogDigest,
            Array.AsReadOnly(assignments.ToArray()),
            Array.AsReadOnly(catalog.Warnings.ToArray()),
            MusicPolicy.NotEvaluatedDomains);
    }

    private static MusicCueAssignment Assign(
        MusicCatalog catalog,
        MusicCue cue,
        MusicPlanningOptions options)
    {
        if (catalog.State != MusicLibraryState.Available)
        {
            return Unassigned(cue, MusicAssignmentReason.CatalogUnavailable);
        }

        MusicCueBlockReason lastReason = MusicCueBlockReason.TrackTooShort;
        foreach (MusicTrack track in catalog.Tracks)
        {
            MusicCueEligibility eligibility = MusicPolicy.Evaluate(
                cue.Kind,
                cue.HasForegroundSpeech,
                cue.DurationSeconds,
                track.DurationSeconds,
                options);
            if (eligibility.IsEligible)
            {
                return new MusicCueAssignment(
                    cue.CueDigest,
                    track.ContentDigest,
                    MusicAssignmentReason.Assigned,
                    cue.Kind == MusicCueKind.Dialogue
                        ? options.AppliedDialogueRelativeGainDecibels
                        : null,
                    loop: false,
                    stretch: false);
            }

            lastReason = eligibility.BlockReason;
            if (lastReason != MusicCueBlockReason.TrackTooShort)
            {
                break;
            }
        }

        return Unassigned(cue, MapReason(lastReason));
    }

    private static MusicCue[] CopyCues(IReadOnlyList<MusicCue> cues)
    {
        MusicCue[] copy = new MusicCue[cues.Count];
        HashSet<string> identities = new(StringComparer.Ordinal);
        for (int index = 0; index < copy.Length; index++)
        {
            MusicCue? cue = cues[index];
            if (cue is null || !identities.Add(cue.CueDigest.Hex))
            {
                throw new ArgumentException(
                    "Music cues must be nonnull and have unique digests.",
                    nameof(cues));
            }

            copy[index] = cue;
        }

        Array.Sort(
            copy,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.CueDigest.Hex,
                right.CueDigest.Hex));
        return copy;
    }

    private static MusicCueAssignment Unassigned(
        MusicCue cue,
        MusicAssignmentReason reason) =>
        new(cue.CueDigest, null, reason, null, loop: false, stretch: false);

    private static MusicAssignmentReason MapReason(MusicCueBlockReason reason) =>
        reason switch
        {
            MusicCueBlockReason.UnsupportedCueKind =>
                MusicAssignmentReason.UnsupportedCueKind,
            MusicCueBlockReason.SpeechOverlap => MusicAssignmentReason.SpeechOverlap,
            MusicCueBlockReason.DialogueMusicDisabled =>
                MusicAssignmentReason.DialogueMusicDisabled,
            MusicCueBlockReason.TrackTooShort =>
                MusicAssignmentReason.NoTrackLongEnough,
            _ => throw new InvalidOperationException(
                "An ineligible cue must have a stable block reason."),
        };
}