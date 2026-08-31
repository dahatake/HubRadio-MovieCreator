using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;

namespace HubRadio.App.Subtitles;

public sealed class SubtitleCueRevisionIdTag
{
}

public enum SubtitleCueStyle
{
    Normal,
    Emphasis,
    Caution,
    Quote,
}

public enum SubtitleCueChangeKind
{
    Initial,
    Text,
    Timing,
    Style,
    Layout,
}

public sealed class SubtitleCueSnapshot
{
    internal SubtitleCueSnapshot(
        Cue cue,
        string text,
        SubtitleCueStyle style,
        OriginRef textOrigin,
        SubtitleLayoutResolution layoutResolution)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(textOrigin);
        ArgumentNullException.ThrowIfNull(layoutResolution);
        if (cue.Kind != CueKind.Subtitle)
        {
            throw new ArgumentException(
                "A subtitle snapshot must contain a subtitle cue.",
                nameof(cue));
        }

        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(
                nameof(style),
                style,
                "Unknown subtitle cue style.");
        }

        if (textOrigin.Kind == OriginKind.Asset)
        {
            throw new ArgumentException(
                "Subtitle text must originate from source evidence or an editorial record.",
                nameof(textOrigin));
        }

        TranscriptEditorContract.ValidateText(text, nameof(text));
        string? resolvedRegionName = layoutResolution.Plan?.Placement.RegionName;
        bool layoutIsResolved =
            layoutResolution.Status == SubtitleLayoutResolutionStatus.Resolved;
        if (layoutIsResolved != (layoutResolution.Plan is not null)
            || layoutIsResolved != (resolvedRegionName is not null)
            || !string.Equals(
                cue.RegionName,
                resolvedRegionName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The subtitle cue region must match its layout resolution.",
                nameof(layoutResolution));
        }

        Cue = cue;
        Text = text;
        Style = style;
        TextOrigin = textOrigin;
        LayoutResolution = layoutResolution;
    }

    public Cue Cue { get; }

    public string Text { get; }

    public SubtitleCueStyle Style { get; }

    public OriginRef TextOrigin { get; }

    public SubtitleLayoutResolution LayoutResolution { get; }

    public SubtitlePlacementCandidate? ResolvedPlacement =>
        LayoutResolution.Plan?.Placement;

    public bool RequiresManualLayout =>
        LayoutResolution.Status ==
        SubtitleLayoutResolutionStatus.ManualAdjustmentRequired;
}

public sealed class SubtitleCueChange
{
    internal SubtitleCueChange(
        SubtitleCueChangeKind kind,
        SubtitleCueSnapshot? previous,
        SubtitleCueSnapshot current,
        OriginRef origin)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown subtitle cue change kind.");
        }

        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(origin);
        bool isInitial = kind == SubtitleCueChangeKind.Initial;
        if (isInitial != (previous is null)
            || (isInitial
                ? origin != current.TextOrigin
                : origin.Kind != OriginKind.EditorialRecord
                    || !SubtitleServiceContract.OnlyFieldChanged(
                        previous!,
                        current,
                        kind,
                        origin)))
        {
            throw new ArgumentException(
                "The subtitle cue change does not match its typed diff.",
                nameof(kind));
        }

        Kind = kind;
        Previous = previous;
        Current = current;
        Origin = origin;
    }

    public SubtitleCueChangeKind Kind { get; }

    public SubtitleCueSnapshot? Previous { get; }

    public SubtitleCueSnapshot Current { get; }

    public OriginRef Origin { get; }
}

public sealed class SubtitleCueRevision
{
    internal SubtitleCueRevision(
        EntityId<SubtitleCueRevisionIdTag> id,
        EntityId<SubtitleCueRevisionIdTag>? parentRevisionId,
        EntityId<HubRadio.Core.Projects.ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        EntityId<CompositionVersionIdTag>? parentCompositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<SubtitleCueCandidateIdTag> sourceCandidateId,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        OriginRef sourceOrigin,
        SubtitleCueSnapshot snapshot,
        SubtitleCueChange change,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        ArgumentNullException.ThrowIfNull(sourceCandidateId);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(sourceOrigin);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(contentDigest);
        if (id == parentRevisionId
            || compositionVersionId == parentCompositionVersionId
            || sourceOrigin.Kind != OriginKind.SourceSpan
            || (parentRevisionId is null) !=
                (change.Kind == SubtitleCueChangeKind.Initial)
            || !SubtitleServiceContract.StableEquals(
                snapshot,
                change.Current))
        {
            throw new ArgumentException(
                "A subtitle cue revision contains inconsistent binding evidence.",
                nameof(change));
        }

        Id = id;
        ParentRevisionId = parentRevisionId;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        ParentCompositionVersionId = parentCompositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        SourceCandidateId = sourceCandidateId;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        SourceOrigin = sourceOrigin;
        Snapshot = snapshot;
        Change = change;
        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        TranscriptEditorContract.ValidateText(reason, nameof(reason));
        CreatedAt = createdAt;
        Reason = reason;
        ContentDigest = contentDigest;
    }

    public EntityId<SubtitleCueRevisionIdTag> Id { get; }

    public EntityId<SubtitleCueRevisionIdTag>? ParentRevisionId { get; }

    public EntityId<HubRadio.Core.Projects.ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public EntityId<CompositionVersionIdTag>? ParentCompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<SubtitleCueCandidateIdTag> SourceCandidateId { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public OriginRef SourceOrigin { get; }

    public SubtitleCueSnapshot Snapshot { get; }

    public SubtitleCueChange Change { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Reason { get; }

    public ContentDigest ContentDigest { get; }
}

public sealed class SubtitleCueHistory
{
    internal SubtitleCueHistory(
        SubtitleCueCandidate sourceCandidate,
        IReadOnlyList<SubtitleCueRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(sourceCandidate);
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count == 0)
        {
            throw new ArgumentException(
                "A subtitle cue history must contain at least one revision.",
                nameof(revisions));
        }

        SubtitleCueRevision[] copied = new SubtitleCueRevision[revisions.Count];
        var revisionIds = new HashSet<EntityId<SubtitleCueRevisionIdTag>>();
        var compositionVersionIds =
            new HashSet<EntityId<CompositionVersionIdTag>>();
        SubtitleCueRevision? previous = null;
        for (int index = 0; index < copied.Length; index++)
        {
            SubtitleCueRevision revision = revisions[index]
                ?? throw new ArgumentException(
                    "Subtitle cue revisions must not contain null values.",
                    nameof(revisions));
            bool sourceBindingMismatch =
                revision.SourceCandidateId != sourceCandidate.Id
                || revision.SourceRevisionId != sourceCandidate.SourceRevisionId
                || revision.SourceRevisionDigest !=
                    sourceCandidate.SourceRevisionDigest
                || revision.SourceOrigin != sourceCandidate.Origin;
            bool chainMismatch = previous is null
                ? revision.ParentRevisionId is not null
                    || revision.Change.Previous is not null
                    || revision.Change.Kind != SubtitleCueChangeKind.Initial
                : revision.ParentRevisionId != previous.Id
                    || revision.ParentCompositionVersionId !=
                        previous.CompositionVersionId
                    || revision.ProjectId != previous.ProjectId
                    || revision.Snapshot.Cue.Id != previous.Snapshot.Cue.Id
                    || revision.CreatedAt < previous.CreatedAt
                    || revision.Change.Previous is null
                    || !SubtitleServiceContract.StableEquals(
                        revision.Change.Previous,
                        previous.Snapshot);
            if (!revisionIds.Add(revision.Id)
                || !compositionVersionIds.Add(revision.CompositionVersionId)
                || sourceBindingMismatch
                || chainMismatch
                || !SubtitleServiceContract.StableEquals(
                    revision.Change.Current,
                    revision.Snapshot)
                || revision.ContentDigest !=
                    SubtitleSnapshotJson.ComputeContentDigest(revision.Snapshot))
            {
                throw new ArgumentException(
                    "The subtitle cue revision chain is inconsistent.",
                    nameof(revisions));
            }

            copied[index] = revision;
            previous = revision;
        }

        SourceCandidate = sourceCandidate;
        Revisions = Array.AsReadOnly(copied);
        CurrentRevision = copied[^1];
        ProjectId = copied[0].ProjectId;
        CueId = copied[0].Snapshot.Cue.Id;
    }

    public SubtitleCueCandidate SourceCandidate { get; }

    public EntityId<SubtitleCueCandidateIdTag> SourceCandidateId =>
        SourceCandidate.Id;

    public EntityId<HubRadio.Core.Projects.ProjectIdTag> ProjectId { get; }

    public EntityId<CueIdTag> CueId { get; }

    public SubtitleCueRevision CurrentRevision { get; }

    public IReadOnlyList<SubtitleCueRevision> Revisions { get; }
}

public enum SubtitleServiceFailureReason
{
    ContextMismatch,
    SourceCandidateStale,
    LayoutBindingMismatch,
    CompositionVersionConflict,
    RevisionConflict,
    NoChange,
    InvalidText,
    InvalidTiming,
    InvalidStyle,
    InvalidOrigin,
    InvalidActor,
    InvalidReason,
    SerializationFailed,
}

public sealed class SubtitleServiceException : Exception
{
    internal SubtitleServiceException(SubtitleServiceFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SubtitleServiceFailureReason Reason { get; }

    private static string GetStableMessage(SubtitleServiceFailureReason reason) =>
        reason switch
        {
            SubtitleServiceFailureReason.ContextMismatch =>
                "The subtitle edit context is inconsistent.",
            SubtitleServiceFailureReason.SourceCandidateStale =>
                "The subtitle source candidate is stale.",
            SubtitleServiceFailureReason.LayoutBindingMismatch =>
                "The subtitle layout evidence does not match its source candidate.",
            SubtitleServiceFailureReason.CompositionVersionConflict =>
                "The subtitle edit composition version is inconsistent.",
            SubtitleServiceFailureReason.RevisionConflict =>
                "The subtitle cue revision conflicts with its history.",
            SubtitleServiceFailureReason.NoChange =>
                "The subtitle edit would not change its current revision.",
            SubtitleServiceFailureReason.InvalidText =>
                "The subtitle text could not be processed safely.",
            SubtitleServiceFailureReason.InvalidTiming =>
                "The subtitle output timing is invalid.",
            SubtitleServiceFailureReason.InvalidStyle =>
                "The subtitle style is invalid.",
            SubtitleServiceFailureReason.InvalidOrigin =>
                "The subtitle edit origin is invalid.",
            SubtitleServiceFailureReason.InvalidActor =>
                "The subtitle editor identity is invalid.",
            SubtitleServiceFailureReason.InvalidReason =>
                "The subtitle edit reason is invalid.",
            SubtitleServiceFailureReason.SerializationFailed =>
                "The subtitle revision could not be serialized canonically.",
            _ => "The subtitle operation failed.",
        };
}

public static class SubtitleService
{
    public static SubtitleCueHistory CreateInitial(
        CompositionVersion compositionVersion,
        SubtitleCueCandidate sourceCandidate,
        SubtitleLayoutResolution layoutResolution,
        EntityId<CueIdTag> cueId,
        EntityId<SubtitleCueRevisionIdTag> revisionId,
        TimeRange outputRange,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(sourceCandidate);
        ArgumentNullException.ThrowIfNull(layoutResolution);
        ArgumentNullException.ThrowIfNull(cueId);
        ArgumentNullException.ThrowIfNull(revisionId);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateCompositionIntegrity(compositionVersion);
        ValidateInitialSource(sourceCandidate);
        ValidateLayoutBinding(sourceCandidate, layoutResolution);
        ValidateActorAndReason(actorSid, reason);
        if (createdAt < compositionVersion.CreatedAt)
        {
            throw Failure(SubtitleServiceFailureReason.RevisionConflict);
        }

        Cue cue = CreateCue(
            cueId,
            outputRange,
            layoutResolution.Plan?.Placement.RegionName);
        SubtitleCueSnapshot snapshot = CreateSnapshot(
            cue,
            sourceCandidate.Text,
            SubtitleCueStyle.Normal,
            sourceCandidate.Origin,
            layoutResolution);
        ContentDigest contentDigest = ComputeContentDigest(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        SubtitleCueChange change;
        SubtitleCueRevision revision;
        try
        {
            change = new SubtitleCueChange(
                SubtitleCueChangeKind.Initial,
                previous: null,
                snapshot,
                sourceCandidate.Origin);
            revision = new SubtitleCueRevision(
                revisionId,
                parentRevisionId: null,
                compositionVersion.ProjectId,
                compositionVersion.Id,
                compositionVersion.ParentVersionId,
                compositionVersion.ContentDigest,
                sourceCandidate.Id,
                sourceCandidate.SourceRevisionId,
                sourceCandidate.SourceRevisionDigest,
                sourceCandidate.Origin,
                snapshot,
                change,
                actorSid,
                createdAt,
                reason,
                contentDigest);
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.SerializationFailed);
        }

        return new SubtitleCueHistory(sourceCandidate, [revision]);
    }

    public static SubtitleCueHistory SetText(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        SubtitleCueCandidate currentSourceCandidate,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        string text,
        OriginRef editOrigin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ValidateEditedText(text);
        return Revise(
            history,
            childCompositionVersion,
            currentSourceCandidate,
            newRevisionId,
            SubtitleCueChangeKind.Text,
            editOrigin,
            actorSid,
            createdAt,
            reason,
            snapshot => CreateSnapshot(
                snapshot.Cue,
                text,
                snapshot.Style,
                string.Equals(text, snapshot.Text, StringComparison.Ordinal)
                    ? snapshot.TextOrigin
                    : editOrigin,
                snapshot.LayoutResolution),
            cancellationToken);
    }

    public static SubtitleCueHistory SetTiming(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        SubtitleCueCandidate currentSourceCandidate,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        TimeRange outputRange,
        OriginRef editOrigin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return Revise(
            history,
            childCompositionVersion,
            currentSourceCandidate,
            newRevisionId,
            SubtitleCueChangeKind.Timing,
            editOrigin,
            actorSid,
            createdAt,
            reason,
            snapshot => CreateSnapshot(
                CreateCue(
                    snapshot.Cue.Id,
                    outputRange,
                    snapshot.Cue.RegionName),
                snapshot.Text,
                snapshot.Style,
                snapshot.TextOrigin,
                snapshot.LayoutResolution),
            cancellationToken);
    }

    public static SubtitleCueHistory SetStyle(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        SubtitleCueCandidate currentSourceCandidate,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        SubtitleCueStyle style,
        OriginRef editOrigin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(style))
        {
            throw Failure(SubtitleServiceFailureReason.InvalidStyle);
        }

        return Revise(
            history,
            childCompositionVersion,
            currentSourceCandidate,
            newRevisionId,
            SubtitleCueChangeKind.Style,
            editOrigin,
            actorSid,
            createdAt,
            reason,
            snapshot => CreateSnapshot(
                snapshot.Cue,
                snapshot.Text,
                style,
                snapshot.TextOrigin,
                snapshot.LayoutResolution),
            cancellationToken);
    }

    public static SubtitleCueHistory SetLayoutResolution(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        SubtitleCueCandidate currentSourceCandidate,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        SubtitleLayoutResolution layoutResolution,
        OriginRef editOrigin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layoutResolution);
        ValidateLayoutBinding(currentSourceCandidate, layoutResolution);
        return Revise(
            history,
            childCompositionVersion,
            currentSourceCandidate,
            newRevisionId,
            SubtitleCueChangeKind.Layout,
            editOrigin,
            actorSid,
            createdAt,
            reason,
            snapshot => CreateSnapshot(
                CreateCue(
                    snapshot.Cue.Id,
                    snapshot.Cue.Range,
                    layoutResolution.Plan?.Placement.RegionName),
                snapshot.Text,
                snapshot.Style,
                snapshot.TextOrigin,
                layoutResolution),
            cancellationToken);
    }

    private static SubtitleCueHistory Revise(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        SubtitleCueCandidate currentSourceCandidate,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        SubtitleCueChangeKind changeKind,
        OriginRef editOrigin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        Func<SubtitleCueSnapshot, SubtitleCueSnapshot> edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(childCompositionVersion);
        ArgumentNullException.ThrowIfNull(currentSourceCandidate);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        ArgumentNullException.ThrowIfNull(editOrigin);
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateCurrentSource(history.SourceCandidate, currentSourceCandidate);
        ValidateChildComposition(history, childCompositionVersion);
        ValidateRevisionRequest(
            history,
            childCompositionVersion,
            newRevisionId,
            createdAt);
        ValidateEditOrigin(editOrigin);
        ValidateActorAndReason(actorSid, reason);

        SubtitleCueSnapshot current = history.CurrentRevision.Snapshot;
        SubtitleCueSnapshot revised = edit(current);
        if (SubtitleServiceContract.StableEquals(current, revised))
        {
            throw Failure(SubtitleServiceFailureReason.NoChange);
        }

        ContentDigest contentDigest = ComputeContentDigest(revised);
        cancellationToken.ThrowIfCancellationRequested();
        SubtitleCueChange change;
        SubtitleCueRevision revision;
        try
        {
            change = new SubtitleCueChange(
                changeKind,
                current,
                revised,
                editOrigin);
            revision = new SubtitleCueRevision(
                newRevisionId,
                history.CurrentRevision.Id,
                history.ProjectId,
                childCompositionVersion.Id,
                childCompositionVersion.ParentVersionId,
                childCompositionVersion.ContentDigest,
                history.SourceCandidate.Id,
                history.SourceCandidate.SourceRevisionId,
                history.SourceCandidate.SourceRevisionDigest,
                history.SourceCandidate.Origin,
                revised,
                change,
                actorSid,
                createdAt,
                reason,
                contentDigest);
        }
        catch (SubtitleServiceException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.SerializationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SubtitleCueHistory(
            history.SourceCandidate,
            [.. history.Revisions, revision]);
    }

    private static void ValidateInitialSource(SubtitleCueCandidate candidate)
    {
        if (candidate.Origin.Kind != OriginKind.SourceSpan
            || candidate.Origin.SourceSpanId != candidate.SourceSpan.Id
            || candidate.SourceSpan.TranscriptRevisionId !=
                candidate.SourceRevisionId
            || candidate.SourceRevisionDigest is null)
        {
            throw Failure(SubtitleServiceFailureReason.ContextMismatch);
        }
    }

    private static void ValidateCurrentSource(
        SubtitleCueCandidate expected,
        SubtitleCueCandidate actual)
    {
        if (!SubtitleServiceContract.CandidatesEqual(expected, actual))
        {
            throw Failure(SubtitleServiceFailureReason.SourceCandidateStale);
        }
    }

    private static void ValidateLayoutBinding(
        SubtitleCueCandidate candidate,
        SubtitleLayoutResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.SourceCandidateId != candidate.Id)
        {
            throw Failure(SubtitleServiceFailureReason.LayoutBindingMismatch);
        }
    }

    private static void ValidateCompositionIntegrity(
        CompositionVersion compositionVersion)
    {
        ContentDigest actualDigest;
        try
        {
            actualDigest = ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8(
                    compositionVersion.Elements));
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or EncoderFallbackException)
        {
            throw Failure(
                SubtitleServiceFailureReason.CompositionVersionConflict);
        }

        if (actualDigest != compositionVersion.ContentDigest)
        {
            throw Failure(
                SubtitleServiceFailureReason.CompositionVersionConflict);
        }
    }

    private static void ValidateChildComposition(
        SubtitleCueHistory history,
        CompositionVersion child)
    {
        ValidateCompositionIntegrity(child);
        SubtitleCueRevision current = history.CurrentRevision;
        if (child.ProjectId != history.ProjectId
            || child.ParentVersionId != current.CompositionVersionId
            || child.Id == current.CompositionVersionId
            || child.CreatedAt < current.CreatedAt
            || history.Revisions.Any(revision =>
                revision.CompositionVersionId == child.Id))
        {
            throw Failure(
                SubtitleServiceFailureReason.CompositionVersionConflict);
        }
    }

    private static void ValidateRevisionRequest(
        SubtitleCueHistory history,
        CompositionVersion childCompositionVersion,
        EntityId<SubtitleCueRevisionIdTag> newRevisionId,
        DateTimeOffset createdAt)
    {
        if (history.Revisions.Any(revision => revision.Id == newRevisionId)
            || createdAt < history.CurrentRevision.CreatedAt
            || createdAt < childCompositionVersion.CreatedAt)
        {
            throw Failure(SubtitleServiceFailureReason.RevisionConflict);
        }
    }

    private static void ValidateEditOrigin(OriginRef editOrigin)
    {
        if (editOrigin.Kind != OriginKind.EditorialRecord)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidOrigin);
        }
    }

    private static void ValidateEditedText(string text)
    {
        try
        {
            TranscriptEditorContract.ValidateText(text, nameof(text));
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidText);
        }
    }

    private static void ValidateActorAndReason(string actorSid, string reason)
    {
        try
        {
            _ = TranscriptEditorContract.ValidateCanonicalSid(
                actorSid,
                nameof(actorSid));
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidActor);
        }

        try
        {
            TranscriptEditorContract.ValidateText(reason, nameof(reason));
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidReason);
        }
    }

    private static Cue CreateCue(
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string? regionName)
    {
        try
        {
            return new Cue(cueId, CueKind.Subtitle, outputRange, regionName);
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidTiming);
        }
        catch (OverflowException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidTiming);
        }
    }

    private static SubtitleCueSnapshot CreateSnapshot(
        Cue cue,
        string text,
        SubtitleCueStyle style,
        OriginRef textOrigin,
        SubtitleLayoutResolution layoutResolution)
    {
        try
        {
            return new SubtitleCueSnapshot(
                cue,
                text,
                style,
                textOrigin,
                layoutResolution);
        }
        catch (SubtitleServiceException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(SubtitleServiceFailureReason.InvalidStyle);
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleServiceFailureReason.SerializationFailed);
        }
    }

    private static ContentDigest ComputeContentDigest(
        SubtitleCueSnapshot snapshot)
    {
        try
        {
            return SubtitleSnapshotJson.ComputeContentDigest(snapshot);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or JsonException
                or EncoderFallbackException)
        {
            throw Failure(SubtitleServiceFailureReason.SerializationFailed);
        }
    }

    private static SubtitleServiceException Failure(
        SubtitleServiceFailureReason reason) =>
        new(reason);
}

internal static class SubtitleServiceContract
{
    internal static bool StableEquals(
        SubtitleCueSnapshot left,
        SubtitleCueSnapshot right) =>
        left.Cue.Equals(right.Cue)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.Style == right.Style
        && left.TextOrigin == right.TextOrigin
        && LayoutEquals(left.LayoutResolution, right.LayoutResolution);

    internal static bool OnlyFieldChanged(
        SubtitleCueSnapshot previous,
        SubtitleCueSnapshot current,
        SubtitleCueChangeKind kind,
        OriginRef editOrigin)
    {
        bool cueIdAndKindEqual = previous.Cue.Id == current.Cue.Id
            && previous.Cue.Kind == current.Cue.Kind;
        bool textEqual = string.Equals(
            previous.Text,
            current.Text,
            StringComparison.Ordinal);
        bool styleEqual = previous.Style == current.Style;
        bool textOriginEqual = previous.TextOrigin == current.TextOrigin;
        bool layoutEqual = LayoutEquals(
            previous.LayoutResolution,
            current.LayoutResolution);
        return kind switch
        {
            SubtitleCueChangeKind.Text =>
                cueIdAndKindEqual
                && previous.Cue.Equals(current.Cue)
                && !textEqual
                && styleEqual
                && current.TextOrigin == editOrigin
                && layoutEqual,
            SubtitleCueChangeKind.Timing =>
                cueIdAndKindEqual
                && previous.Cue.Range != current.Cue.Range
                && string.Equals(
                    previous.Cue.RegionName,
                    current.Cue.RegionName,
                    StringComparison.Ordinal)
                && textEqual
                && styleEqual
                && textOriginEqual
                && layoutEqual,
            SubtitleCueChangeKind.Style =>
                previous.Cue.Equals(current.Cue)
                && textEqual
                && previous.Style != current.Style
                && textOriginEqual
                && layoutEqual,
            SubtitleCueChangeKind.Layout =>
                cueIdAndKindEqual
                && previous.Cue.Range == current.Cue.Range
                && textEqual
                && styleEqual
                && textOriginEqual
                && !layoutEqual,
            _ => false,
        };
    }

    internal static bool CandidatesEqual(
        SubtitleCueCandidate left,
        SubtitleCueCandidate right)
    {
        return left.Id == right.Id
            && left.SourceRevisionId == right.SourceRevisionId
            && left.SourceRevisionDigest == right.SourceRevisionDigest
            && left.SpeakerId == right.SpeakerId
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
            && left.SourceRange == right.SourceRange
            && left.Origin == right.Origin
            && SourceSpansEqual(left.SourceSpan, right.SourceSpan)
            && left.EvidenceTokenIds.SequenceEqual(right.EvidenceTokenIds)
            && TimingFallbacksEqual(left.TimingFallbacks, right.TimingFallbacks)
            && left.BoundaryReasons.SequenceEqual(right.BoundaryReasons)
            && left.ObservedPositiveGapAfter == right.ObservedPositiveGapAfter
            && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);
    }

    internal static bool LayoutEquals(
        SubtitleLayoutResolution left,
        SubtitleLayoutResolution right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Status != right.Status
            || left.SourceCandidateId != right.SourceCandidateId
            || left.LayoutSpecId != right.LayoutSpecId
            || left.ProfileVersionDigest != right.ProfileVersionDigest
            || !left.GlobalHardConstraints.SequenceEqual(
                right.GlobalHardConstraints)
            || left.Evaluations.Count != right.Evaluations.Count
            || !PlansEqual(left.Plan, right.Plan))
        {
            return false;
        }

        for (int index = 0; index < left.Evaluations.Count; index++)
        {
            SubtitlePlacementEvaluation first = left.Evaluations[index];
            SubtitlePlacementEvaluation second = right.Evaluations[index];
            if (!first.Placement.Equals(second.Placement)
                || !first.HardConstraints.SequenceEqual(second.HardConstraints)
                || !first.SoftConstraints.SequenceEqual(second.SoftConstraints)
                || first.SoftCost != second.SoftCost)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SourceSpansEqual(SourceSpan left, SourceSpan right) =>
        left.Id == right.Id
        && left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);

    private static bool TimingFallbacksEqual(
        IReadOnlyList<SubtitleTimingFallback> left,
        IReadOnlyList<SubtitleTimingFallback> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].TokenId != right[index].TokenId
                || left[index].ParentUtteranceId !=
                    right[index].ParentUtteranceId
                || left[index].ParentUtteranceRange !=
                    right[index].ParentUtteranceRange)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PlansEqual(
        SubtitleLayoutPlan? left,
        SubtitleLayoutPlan? right) =>
        left is null
            ? right is null
            : right is not null
                && left.Placement.Equals(right.Placement)
                && left.SoftConstraints.SequenceEqual(right.SoftConstraints)
                && left.SoftCost == right.SoftCost;
}

internal static class SubtitleSnapshotJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static ContentDigest ComputeContentDigest(
        SubtitleCueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ContentDigest.Compute(WriteSnapshotToUtf8(snapshot));
    }

    private static byte[] WriteSnapshotToUtf8(SubtitleCueSnapshot snapshot)
    {
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("cue");
            WriteCue(writer, snapshot.Cue);
            WriteString(writer, "text", snapshot.Text);
            WriteString(writer, "style", StyleName(snapshot.Style));
            writer.WritePropertyName("textOrigin");
            WriteOrigin(writer, snapshot.TextOrigin);
            writer.WritePropertyName("layoutResolution");
            WriteLayoutResolution(writer, snapshot.LayoutResolution);
            writer.WriteEndObject();
        });
    }

    private static void WriteCue(Utf8JsonWriter writer, Cue cue)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", cue.Id.ToString());
        WriteString(writer, "kind", "subtitle");
        writer.WritePropertyName("range");
        WriteTimeRange(writer, cue.Range);
        if (cue.RegionName is null)
        {
            writer.WriteNull("regionName");
        }
        else
        {
            WriteString(writer, "regionName", cue.RegionName);
        }

        writer.WriteEndObject();
    }

    private static void WriteLayoutResolution(
        Utf8JsonWriter writer,
        SubtitleLayoutResolution resolution)
    {
        writer.WriteStartObject();
        WriteString(writer, "status", ResolutionStatusName(resolution.Status));
        WriteString(
            writer,
            "sourceCandidateId",
            resolution.SourceCandidateId.ToString());
        WriteString(writer, "layoutSpecId", resolution.LayoutSpecId.ToString());
        WriteString(
            writer,
            "profileVersionDigest",
            resolution.ProfileVersionDigest.Hex);
        writer.WritePropertyName("globalHardConstraints");
        writer.WriteStartArray();
        foreach (SubtitleHardConstraintKind constraint in
            resolution.GlobalHardConstraints)
        {
            writer.WriteStringValue(HardConstraintName(constraint));
        }

        writer.WriteEndArray();
        writer.WritePropertyName("evaluations");
        writer.WriteStartArray();
        foreach (SubtitlePlacementEvaluation evaluation in resolution.Evaluations)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("placement");
            WritePlacement(writer, evaluation.Placement);
            writer.WritePropertyName("hardConstraints");
            writer.WriteStartArray();
            foreach (SubtitleHardConstraintKind constraint in
                evaluation.HardConstraints)
            {
                writer.WriteStringValue(HardConstraintName(constraint));
            }

            writer.WriteEndArray();
            writer.WritePropertyName("softConstraints");
            writer.WriteStartArray();
            foreach (SubtitleSoftConstraintKind constraint in
                evaluation.SoftConstraints)
            {
                writer.WriteStringValue(SoftConstraintName(constraint));
            }

            writer.WriteEndArray();
            WriteString(
                writer,
                "softCost",
                evaluation.SoftCost.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (resolution.Plan is null)
        {
            writer.WriteNull("plan");
        }
        else
        {
            writer.WritePropertyName("plan");
            writer.WriteStartObject();
            writer.WritePropertyName("placement");
            WritePlacement(writer, resolution.Plan.Placement);
            writer.WritePropertyName("softConstraints");
            writer.WriteStartArray();
            foreach (SubtitleSoftConstraintKind constraint in
                resolution.Plan.SoftConstraints)
            {
                writer.WriteStringValue(SoftConstraintName(constraint));
            }

            writer.WriteEndArray();
            WriteString(
                writer,
                "softCost",
                resolution.Plan.SoftCost.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WritePlacement(
        Utf8JsonWriter writer,
        SubtitlePlacementCandidate placement)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", placement.Id.ToString());
        WriteString(writer, "regionName", placement.RegionName);
        writer.WritePropertyName("rect");
        writer.WriteStartObject();
        writer.WritePropertyName("x");
        WriteRationalNumber(writer, placement.Rect.X);
        writer.WritePropertyName("y");
        WriteRationalNumber(writer, placement.Rect.Y);
        writer.WritePropertyName("width");
        WriteRationalNumber(writer, placement.Rect.Width);
        writer.WritePropertyName("height");
        WriteRationalNumber(writer, placement.Rect.Height);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan when origin.SourceSpanId is not null:
                WriteString(writer, "kind", "sourceSpan");
                WriteString(writer, "sourceSpanId", origin.SourceSpanId.ToString());
                break;
            case OriginKind.EditorialRecord
                when origin.EditorialRecordId is not null:
                WriteString(writer, "kind", "editorialRecord");
                WriteString(
                    writer,
                    "editorialRecordId",
                    origin.EditorialRecordId.ToString());
                break;
            default:
                throw new InvalidOperationException(
                    "The subtitle origin is invalid.");
        }

        writer.WriteEndObject();
    }

    private static void WriteTimeRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteNumber("rate", value.Rate);
        writer.WriteEndObject();
    }

    private static void WriteRationalNumber(
        Utf8JsonWriter writer,
        RationalNumber value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("numerator", value.Numerator);
        writer.WriteNumber("denominator", value.Denominator);
        writer.WriteEndObject();
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            write(writer);
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string value)
    {
        _ = StrictUtf8.GetByteCount(value);
        writer.WriteString(propertyName, value);
    }

    private static string StyleName(SubtitleCueStyle style) =>
        style switch
        {
            SubtitleCueStyle.Normal => "normal",
            SubtitleCueStyle.Emphasis => "emphasis",
            SubtitleCueStyle.Caution => "caution",
            SubtitleCueStyle.Quote => "quote",
            _ => throw new InvalidOperationException("Unknown subtitle style."),
        };

    private static string ResolutionStatusName(
        SubtitleLayoutResolutionStatus status) =>
        status switch
        {
            SubtitleLayoutResolutionStatus.Resolved => "resolved",
            SubtitleLayoutResolutionStatus.ManualAdjustmentRequired =>
                "manualAdjustmentRequired",
            _ => throw new InvalidOperationException(
                "Unknown subtitle layout resolution status."),
        };

    private static string HardConstraintName(
        SubtitleHardConstraintKind kind) =>
        kind switch
        {
            SubtitleHardConstraintKind.NoPlacementCandidate =>
                "noPlacementCandidate",
            SubtitleHardConstraintKind.SourceTimingAdjustmentRequired =>
                "sourceTimingAdjustmentRequired",
            SubtitleHardConstraintKind.RequiredSafeZoneMissing =>
                "requiredSafeZoneMissing",
            SubtitleHardConstraintKind.OutsideOutputFrame =>
                "outsideOutputFrame",
            SubtitleHardConstraintKind.UnknownLayoutRegion =>
                "unknownLayoutRegion",
            SubtitleHardConstraintKind.LayoutRegionIsNotSubtitle =>
                "layoutRegionIsNotSubtitle",
            SubtitleHardConstraintKind.OutsideSubtitleRegion =>
                "outsideSubtitleRegion",
            SubtitleHardConstraintKind.OutsideRequiredSafeZone =>
                "outsideRequiredSafeZone",
            SubtitleHardConstraintKind.ProhibitedRegionOverlap =>
                "prohibitedRegionOverlap",
            _ => throw new InvalidOperationException(
                "Unknown subtitle hard constraint."),
        };

    private static string SoftConstraintName(
        SubtitleSoftConstraintKind kind) =>
        kind switch
        {
            SubtitleSoftConstraintKind.FaceRegionOverlap =>
                "faceRegionOverlap",
            SubtitleSoftConstraintKind.MaterialRegionOverlap =>
                "materialRegionOverlap",
            SubtitleSoftConstraintKind.GraphicRegionOverlap =>
                "graphicRegionOverlap",
            SubtitleSoftConstraintKind.PlacementChanged =>
                "placementChanged",
            _ => throw new InvalidOperationException(
                "Unknown subtitle soft constraint."),
        };
}