using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;

namespace HubRadio.App.Graphics;

public sealed class EmphasisCueRevisionIdTag
{
}

public enum EmphasisCueSourceKind
{
    AdoptedSuggestion,
    ManualEditorial,
}

public enum EmphasisCueChangeKind
{
    Initial,
    Text,
    Timing,
    Style,
    Region,
}

public sealed class EmphasisCuePlacementContext
{
    public EmphasisCuePlacementContext(
        LayoutSpec layoutSpec,
        SubtitleLayoutProfile layoutProfile,
        TimeRange topicOutputRange,
        TimeRange? evidenceOutputRange,
        ContentDigest? currentSourceRevisionDigest,
        IReadOnlyList<Cue> existingCues,
        IReadOnlyList<SubtitleProhibitedRegion> prohibitedRegions)
    {
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(layoutProfile);
        ArgumentNullException.ThrowIfNull(existingCues);
        ArgumentNullException.ThrowIfNull(prohibitedRegions);
        if (layoutProfile.LayoutSpecId != layoutSpec.Id)
        {
            throw new ArgumentException(
                "The emphasis layout profile must reference the supplied LayoutSpec.",
                nameof(layoutProfile));
        }

        ValidateOutputRange(topicOutputRange, nameof(topicOutputRange));
        if (evidenceOutputRange is TimeRange evidence)
        {
            ValidateOutputRange(evidence, nameof(evidenceOutputRange));
            if (!Contains(topicOutputRange, evidence))
            {
                throw new ArgumentException(
                    "Mapped emphasis evidence must be contained by its topic output range.",
                    nameof(evidenceOutputRange));
            }
        }

        Cue[] copiedCues = new Cue[existingCues.Count];
        var cueIds = new HashSet<EntityId<CueIdTag>>();
        for (int index = 0; index < copiedCues.Length; index++)
        {
            Cue cue = existingCues[index]
                ?? throw new ArgumentException(
                    "Existing cues must not contain null values.",
                    nameof(existingCues));
            if (!cueIds.Add(cue.Id))
            {
                throw new ArgumentException(
                    "Existing cue identities must be unique.",
                    nameof(existingCues));
            }

            copiedCues[index] = cue;
        }

        SubtitleProhibitedRegion[] copiedProhibited =
            new SubtitleProhibitedRegion[prohibitedRegions.Count];
        var prohibitedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copiedProhibited.Length; index++)
        {
            SubtitleProhibitedRegion region = prohibitedRegions[index]
                ?? throw new ArgumentException(
                    "Prohibited regions must not contain null values.",
                    nameof(prohibitedRegions));
            if (!prohibitedNames.Add(region.Name))
            {
                throw new ArgumentException(
                    "Prohibited-region names must be unique.",
                    nameof(prohibitedRegions));
            }

            copiedProhibited[index] = region;
        }

        LayoutSpec = layoutSpec;
        LayoutProfile = layoutProfile;
        TopicOutputRange = topicOutputRange;
        EvidenceOutputRange = evidenceOutputRange;
        CurrentSourceRevisionDigest = currentSourceRevisionDigest;
        ExistingCues = Array.AsReadOnly(copiedCues);
        ProhibitedRegions = Array.AsReadOnly(copiedProhibited);
    }

    public LayoutSpec LayoutSpec { get; }

    public SubtitleLayoutProfile LayoutProfile { get; }

    public TimeRange TopicOutputRange { get; }

    public TimeRange? EvidenceOutputRange { get; }

    public ContentDigest? CurrentSourceRevisionDigest { get; }

    public IReadOnlyList<Cue> ExistingCues { get; }

    public IReadOnlyList<SubtitleProhibitedRegion> ProhibitedRegions { get; }

    private static void ValidateOutputRange(TimeRange range, string parameterName)
    {
        if (range.Start < default(RationalTime)
            || range.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                range,
                "An emphasis output range must have a nonnegative start and positive duration.");
        }
    }

    private static bool Contains(TimeRange outer, TimeRange inner) =>
        inner.Start >= outer.Start && inner.End <= outer.End;
}

public sealed class EmphasisCueSnapshot
{
    internal EmphasisCueSnapshot(
        Cue cue,
        string text,
        SubtitleCueStyle style,
        OriginRef textOrigin,
        SuggestionReference? adoptedSuggestion,
        EmphasisTextSourceKind? textSource)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(textOrigin);
        if (cue.Kind != CueKind.Graphic || cue.RegionName is null)
        {
            throw new ArgumentException(
                "An emphasis snapshot requires a region-bound graphic cue.",
                nameof(cue));
        }

        EmphasisCueContract.ValidateStyle(style);
        EmphasisCueContract.ValidateText(text, nameof(text));
        bool adoptedShape = adoptedSuggestion is not null
            && textOrigin.Kind is OriginKind.SourceSpan
                or OriginKind.EditorialRecord;
        bool manualShape = adoptedSuggestion is null
            && textOrigin.Kind == OriginKind.EditorialRecord
            && textSource is null;
        if (!adoptedShape && !manualShape)
        {
            throw new ArgumentException(
                "Emphasis text provenance is inconsistent.",
                nameof(textOrigin));
        }

        if (style == SubtitleCueStyle.Quote
            && (textSource != EmphasisTextSourceKind.OriginalTranscript
                || textOrigin.Kind != OriginKind.SourceSpan))
        {
            throw EmphasisCueContract.Failure(
                EmphasisCueFailureReason.UngroundedQuote);
        }

        Cue = cue;
        Text = text;
        Style = style;
        TextOrigin = textOrigin;
        AdoptedSuggestion = adoptedSuggestion;
        TextSource = textSource;
    }

    public Cue Cue { get; }

    public string Text { get; }

    public SubtitleCueStyle Style { get; }

    public OriginRef TextOrigin { get; }

    public SuggestionReference? AdoptedSuggestion { get; }

    public EmphasisTextSourceKind? TextSource { get; }
}

public sealed class EmphasisCueEdit
{
    private EmphasisCueEdit(
        EmphasisCueChangeKind kind,
        OriginRef origin,
        string? text = null,
        TimeRange? outputRange = null,
        SubtitleCueStyle? style = null,
        string? regionName = null)
    {
        if (kind == EmphasisCueChangeKind.Initial || !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Kind != OriginKind.EditorialRecord)
        {
            throw EmphasisCueContract.Failure(
                EmphasisCueFailureReason.InvalidOrigin);
        }

        bool shapeIsValid = kind switch
        {
            EmphasisCueChangeKind.Text =>
                text is not null
                && outputRange is null
                && style is null
                && regionName is null,
            EmphasisCueChangeKind.Timing =>
                text is null
                && outputRange is not null
                && style is null
                && regionName is null,
            EmphasisCueChangeKind.Style =>
                text is null
                && outputRange is null
                && style is not null
                && regionName is null,
            EmphasisCueChangeKind.Region =>
                text is null
                && outputRange is null
                && style is null
                && regionName is not null,
            _ => false,
        };
        if (!shapeIsValid)
        {
            throw new ArgumentException(
                "An emphasis edit must change exactly one typed field.",
                nameof(kind));
        }

        if (text is not null)
        {
            EmphasisCueContract.ValidateText(text, nameof(text));
        }

        if (style is SubtitleCueStyle cueStyle)
        {
            EmphasisCueContract.ValidateStyle(cueStyle);
        }

        if (regionName is not null)
        {
            EmphasisCueContract.ValidateText(regionName, nameof(regionName));
        }

        Kind = kind;
        Origin = origin;
        Text = text;
        OutputRange = outputRange;
        Style = style;
        RegionName = regionName;
    }

    public EmphasisCueChangeKind Kind { get; }

    public OriginRef Origin { get; }

    public string? Text { get; }

    public TimeRange? OutputRange { get; }

    public SubtitleCueStyle? Style { get; }

    public string? RegionName { get; }

    public static EmphasisCueEdit ChangeText(string text, OriginRef origin) =>
        new(EmphasisCueChangeKind.Text, origin, text: text);

    public static EmphasisCueEdit ChangeTiming(
        TimeRange outputRange,
        OriginRef origin) =>
        new(EmphasisCueChangeKind.Timing, origin, outputRange: outputRange);

    public static EmphasisCueEdit ChangeStyle(
        SubtitleCueStyle style,
        OriginRef origin) =>
        new(EmphasisCueChangeKind.Style, origin, style: style);

    public static EmphasisCueEdit ChangeRegion(
        string regionName,
        OriginRef origin) =>
        new(EmphasisCueChangeKind.Region, origin, regionName: regionName);
}

public sealed class EmphasisCueChange
{
    internal EmphasisCueChange(
        EmphasisCueChangeKind kind,
        EmphasisCueSnapshot? previous,
        EmphasisCueSnapshot current,
        OriginRef origin)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(origin);
        bool initial = kind == EmphasisCueChangeKind.Initial;
        if (initial != (previous is null)
            || (!initial && (origin.Kind != OriginKind.EditorialRecord
                || !EmphasisCueContract.OnlyFieldChanged(
                    previous!,
                    current,
                    kind))))
        {
            throw new ArgumentException(
                "The emphasis cue change does not match its typed diff.",
                nameof(kind));
        }

        Kind = kind;
        Previous = previous;
        Current = current;
        Origin = origin;
    }

    public EmphasisCueChangeKind Kind { get; }

    public EmphasisCueSnapshot? Previous { get; }

    public EmphasisCueSnapshot Current { get; }

    public OriginRef Origin { get; }
}

public sealed class EmphasisCueRevision
{
    internal EmphasisCueRevision(
        EntityId<EmphasisCueRevisionIdTag> id,
        EntityId<EmphasisCueRevisionIdTag>? parentRevisionId,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        EntityId<CompositionVersionIdTag>? parentCompositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<TopicMetadataRevisionIdTag> topicMetadataRevisionId,
        TimeRange topicOutputRange,
        TimeRange? evidenceOutputRange,
        EntityId<LayoutSpecIdTag> layoutSpecId,
        ContentDigest layoutProfileDigest,
        EmphasisCueSnapshot snapshot,
        EmphasisCueChange change,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        ArgumentNullException.ThrowIfNull(topicMetadataRevisionId);
        ArgumentNullException.ThrowIfNull(layoutSpecId);
        ArgumentNullException.ThrowIfNull(layoutProfileDigest);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(contentDigest);
        if (id == parentRevisionId
            || compositionVersionId == parentCompositionVersionId
            || (parentRevisionId is null)
                != (change.Kind == EmphasisCueChangeKind.Initial)
            || !EmphasisCueContract.SnapshotsEqual(snapshot, change.Current))
        {
            throw new ArgumentException(
                "An emphasis cue revision contains inconsistent evidence.",
                nameof(change));
        }

        Id = id;
        ParentRevisionId = parentRevisionId;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        ParentCompositionVersionId = parentCompositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        TopicMetadataRevisionId = topicMetadataRevisionId;
        TopicOutputRange = topicOutputRange;
        EvidenceOutputRange = evidenceOutputRange;
        LayoutSpecId = layoutSpecId;
        LayoutProfileDigest = layoutProfileDigest;
        Snapshot = snapshot;
        Change = change;
        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        EmphasisCueContract.ValidateText(reason, nameof(reason));
        CreatedAt = createdAt;
        Reason = reason;
        ContentDigest = contentDigest;
    }

    public EntityId<EmphasisCueRevisionIdTag> Id { get; }

    public EntityId<EmphasisCueRevisionIdTag>? ParentRevisionId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public EntityId<CompositionVersionIdTag>? ParentCompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<TopicMetadataRevisionIdTag> TopicMetadataRevisionId { get; }

    public TimeRange TopicOutputRange { get; }

    public TimeRange? EvidenceOutputRange { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest LayoutProfileDigest { get; }

    public EmphasisCueSnapshot Snapshot { get; }

    public EmphasisCueChange Change { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Reason { get; }

    public ContentDigest ContentDigest { get; }
}

public sealed class EmphasisCueHistory
{
    internal EmphasisCueHistory(
        EmphasisCueSourceKind sourceKind,
        EmphasisCandidate? sourceCandidate,
        SuggestionReference? adoptedSuggestion,
        IReadOnlyList<EmphasisCueRevision> revisions)
    {
        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        ArgumentNullException.ThrowIfNull(revisions);
        bool sourceShape = sourceKind switch
        {
            EmphasisCueSourceKind.AdoptedSuggestion =>
                sourceCandidate is not null
                && adoptedSuggestion is not null
                && adoptedSuggestion.SuggestionId == sourceCandidate.Suggestion.Id
                && adoptedSuggestion.Origin == sourceCandidate.Suggestion.Origin,
            EmphasisCueSourceKind.ManualEditorial =>
                sourceCandidate is null && adoptedSuggestion is null,
            _ => false,
        };
        if (!sourceShape || revisions.Count == 0)
        {
            throw new ArgumentException(
                "An emphasis cue history has inconsistent source evidence.",
                nameof(sourceCandidate));
        }

        EmphasisCueRevision[] copied = new EmphasisCueRevision[revisions.Count];
        var revisionIds = new HashSet<EntityId<EmphasisCueRevisionIdTag>>();
        var compositionIds = new HashSet<EntityId<CompositionVersionIdTag>>();
        EmphasisCueRevision? previous = null;
        for (int index = 0; index < copied.Length; index++)
        {
            EmphasisCueRevision revision = revisions[index]
                ?? throw new ArgumentException(
                    "Emphasis cue revisions must not contain null values.",
                    nameof(revisions));
            bool chainMismatch = previous is null
                ? revision.ParentRevisionId is not null
                    || revision.Change.Previous is not null
                : revision.ParentRevisionId != previous.Id
                    || revision.ParentCompositionVersionId !=
                        previous.CompositionVersionId
                    || revision.ProjectId != previous.ProjectId
                    || revision.TopicMetadataRevisionId !=
                        previous.TopicMetadataRevisionId
                    || revision.TopicOutputRange != previous.TopicOutputRange
                    || revision.EvidenceOutputRange !=
                        previous.EvidenceOutputRange
                    || revision.LayoutSpecId != previous.LayoutSpecId
                    || revision.LayoutProfileDigest !=
                        previous.LayoutProfileDigest
                    || revision.Snapshot.Cue.Id != previous.Snapshot.Cue.Id
                    || revision.CreatedAt < previous.CreatedAt
                    || revision.Change.Previous is null
                    || !EmphasisCueContract.SnapshotsEqual(
                        revision.Change.Previous,
                        previous.Snapshot);
            if (!revisionIds.Add(revision.Id)
                || !compositionIds.Add(revision.CompositionVersionId)
                || chainMismatch
                || revision.ContentDigest !=
                    EmphasisCueSnapshotJson.ComputeContentDigest(
                        revision.Snapshot))
            {
                throw new ArgumentException(
                    "The emphasis cue revision chain is inconsistent.",
                    nameof(revisions));
            }

            copied[index] = revision;
            previous = revision;
        }

        SourceKind = sourceKind;
        SourceCandidate = sourceCandidate;
        AdoptedSuggestion = adoptedSuggestion;
        Revisions = Array.AsReadOnly(copied);
        CurrentRevision = copied[^1];
        ProjectId = copied[0].ProjectId;
        CueId = copied[0].Snapshot.Cue.Id;
    }

    public EmphasisCueSourceKind SourceKind { get; }

    public EmphasisCandidate? SourceCandidate { get; }

    public EntityId<SuggestionIdTag>? SourceSuggestionId =>
        SourceCandidate?.Suggestion.Id;

    public SuggestionReference? AdoptedSuggestion { get; }

    public ContentDigest? SourceRevisionDigest =>
        SourceCandidate?.SourceRevisionDigest;

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CueIdTag> CueId { get; }

    public EmphasisCueRevision CurrentRevision { get; }

    public IReadOnlyList<EmphasisCueRevision> Revisions { get; }
}

public enum EmphasisCueFailureReason
{
    AdoptionBindingMismatch,
    SourceCandidateStale,
    TopicBindingMismatch,
    LayoutBindingMismatch,
    InvalidGraphicRegion,
    ProhibitedRegionOverlap,
    VisualConflict,
    TimingOutsideEvidence,
    UngroundedQuote,
    CompositionVersionConflict,
    RevisionConflict,
    NoChange,
    InvalidText,
    InvalidStyle,
    InvalidOrigin,
    InvalidActor,
    InvalidReason,
    SerializationFailed,
}

public sealed class EmphasisCueException : Exception
{
    internal EmphasisCueException(EmphasisCueFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public EmphasisCueFailureReason Reason { get; }

    private static string GetStableMessage(EmphasisCueFailureReason reason) =>
        reason switch
        {
            EmphasisCueFailureReason.AdoptionBindingMismatch =>
                "The emphasis adoption evidence is inconsistent.",
            EmphasisCueFailureReason.SourceCandidateStale =>
                "The emphasis source candidate is stale.",
            EmphasisCueFailureReason.TopicBindingMismatch =>
                "The emphasis topic evidence is inconsistent.",
            EmphasisCueFailureReason.LayoutBindingMismatch =>
                "The emphasis layout evidence is inconsistent.",
            EmphasisCueFailureReason.InvalidGraphicRegion =>
                "The emphasis cue does not reference a graphic region.",
            EmphasisCueFailureReason.ProhibitedRegionOverlap =>
                "The emphasis cue overlaps a prohibited region.",
            EmphasisCueFailureReason.VisualConflict =>
                "The emphasis cue conflicts with existing on-screen content.",
            EmphasisCueFailureReason.TimingOutsideEvidence =>
                "The emphasis timing is outside its mapped evidence.",
            EmphasisCueFailureReason.UngroundedQuote =>
                "A quote cue requires exact original transcript evidence.",
            EmphasisCueFailureReason.CompositionVersionConflict =>
                "The emphasis composition version is inconsistent.",
            EmphasisCueFailureReason.RevisionConflict =>
                "The emphasis cue revision conflicts with its history.",
            EmphasisCueFailureReason.NoChange =>
                "The emphasis edit would not change its parent.",
            EmphasisCueFailureReason.InvalidText =>
                "The emphasis cue text is invalid.",
            EmphasisCueFailureReason.InvalidStyle =>
                "The emphasis cue style is invalid.",
            EmphasisCueFailureReason.InvalidOrigin =>
                "The emphasis edit origin is invalid.",
            EmphasisCueFailureReason.InvalidActor =>
                "The emphasis editor identity is invalid.",
            EmphasisCueFailureReason.InvalidReason =>
                "The emphasis edit reason is invalid.",
            EmphasisCueFailureReason.SerializationFailed =>
                "The emphasis cue could not be serialized canonically.",
            _ => "The emphasis cue operation failed.",
        };
}

public static class EmphasisCueService
{
    public static EmphasisCueHistory CreateFromAdoption(
        CompositionVersion compositionVersion,
        HighlightSuggestionAdoptionResult adoption,
        TopicMetadataRevision currentTopicMetadata,
        EmphasisCuePlacementContext context,
        SubtitleCueStyle style,
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(adoption);
        ArgumentNullException.ThrowIfNull(currentTopicMetadata);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revisionId);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCompositionIntegrity(compositionVersion);
        ValidateActorReasonAndStyle(actorSid, reason, style);
        if (adoption.OriginalCandidate is not EmphasisSuggestionCandidate original)
        {
            throw Failure(EmphasisCueFailureReason.AdoptionBindingMismatch);
        }

        EmphasisCandidate candidate = original.Payload;
        if (adoption.DecisionRecord.Decision != SuggestionDecisionKind.Adopted
            || adoption.DecidedCandidate.Status != SuggestionStatus.Adopted
            || adoption.Reference.SuggestionId != original.Id
            || adoption.Reference.Origin != original.Origin
            || adoption.Cue.Kind != CueKind.Graphic
            || !string.Equals(adoption.Text, candidate.Text, StringComparison.Ordinal)
            || adoption.TextSource != candidate.TextSource)
        {
            throw Failure(EmphasisCueFailureReason.AdoptionBindingMismatch);
        }

        if (createdAt < adoption.DecisionRecord.DecidedAt)
        {
            throw Failure(EmphasisCueFailureReason.RevisionConflict);
        }

        ValidateSourceAndTopic(
            compositionVersion,
            candidate,
            currentTopicMetadata,
            context);
        if (context.EvidenceOutputRange is null)
        {
            throw Failure(EmphasisCueFailureReason.TimingOutsideEvidence);
        }

        ValidateCue(
            adoption.Cue,
            context,
            requireEvidenceRange: true,
            replacedCue: null);
        EmphasisCueSnapshot snapshot = CreateSnapshot(
            adoption.Cue,
            candidate.Text,
            style,
            adoption.Reference.Origin,
            adoption.Reference,
            candidate.TextSource);
        return CreateInitialHistory(
            EmphasisCueSourceKind.AdoptedSuggestion,
            candidate,
            adoption.Reference,
            compositionVersion,
            currentTopicMetadata,
            context,
            revisionId,
            snapshot,
            adoption.Reference.Origin,
            actorSid,
            createdAt,
            reason,
            cancellationToken);
    }

    public static EmphasisCueHistory CreateManual(
        CompositionVersion compositionVersion,
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string regionName,
        string text,
        SubtitleCueStyle style,
        OriginRef editorialOrigin,
        TopicMetadataRevision currentTopicMetadata,
        EmphasisCuePlacementContext context,
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(cueId);
        ArgumentNullException.ThrowIfNull(editorialOrigin);
        ArgumentNullException.ThrowIfNull(currentTopicMetadata);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revisionId);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCompositionIntegrity(compositionVersion);
        ValidateActorReasonAndStyle(actorSid, reason, style);
        ValidateManualContext(
            compositionVersion,
            currentTopicMetadata,
            context,
            editorialOrigin);
        EmphasisCueContract.ValidateText(text, nameof(text));
        Cue cue = CreateCue(cueId, outputRange, regionName);
        ValidateCue(
            cue,
            context,
            requireEvidenceRange: false,
            replacedCue: null);
        EmphasisCueSnapshot snapshot = CreateSnapshot(
            cue,
            text,
            style,
            editorialOrigin,
            adoptedSuggestion: null,
            textSource: null);
        return CreateInitialHistory(
            EmphasisCueSourceKind.ManualEditorial,
            sourceCandidate: null,
            adoptedSuggestion: null,
            compositionVersion,
            currentTopicMetadata,
            context,
            revisionId,
            snapshot,
            editorialOrigin,
            actorSid,
            createdAt,
            reason,
            cancellationToken);
    }

    public static EmphasisCueHistory Revise(
        EmphasisCueHistory history,
        CompositionVersion childCompositionVersion,
        TopicMetadataRevision currentTopicMetadata,
        EmphasisCuePlacementContext context,
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        EmphasisCueEdit edit,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(childCompositionVersion);
        ArgumentNullException.ThrowIfNull(currentTopicMetadata);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCompositionIntegrity(childCompositionVersion);
        ValidateChildComposition(history, childCompositionVersion);
        ValidateRevisionRequest(
            history,
            childCompositionVersion,
            revisionId,
            createdAt);
        ValidateHistoryContext(history, currentTopicMetadata, context);
        ValidateActorAndReason(actorSid, reason);

        EmphasisCueSnapshot current = history.CurrentRevision.Snapshot;
        if (edit.Kind == EmphasisCueChangeKind.Text
            && string.Equals(
                edit.Text,
                current.Text,
                StringComparison.Ordinal))
        {
            throw Failure(EmphasisCueFailureReason.NoChange);
        }

        EmphasisCueSnapshot revised = ApplyEdit(current, edit);
        if (EmphasisCueContract.SnapshotsEqual(current, revised))
        {
            throw Failure(EmphasisCueFailureReason.NoChange);
        }

        ValidateCue(
            revised.Cue,
            context,
            requireEvidenceRange:
                history.SourceKind == EmphasisCueSourceKind.AdoptedSuggestion,
            replacedCue: current.Cue);
        ValidateQuote(revised, history.SourceCandidate);
        ContentDigest contentDigest = ComputeContentDigest(revised);
        cancellationToken.ThrowIfCancellationRequested();
        EmphasisCueChange change = new(
            edit.Kind,
            current,
            revised,
            edit.Origin);
        EmphasisCueRevision revision = CreateRevision(
            revisionId,
            history.CurrentRevision.Id,
            history.ProjectId,
            childCompositionVersion,
            currentTopicMetadata,
            context,
            revised,
            change,
            actorSid,
            createdAt,
            reason,
            contentDigest);
        cancellationToken.ThrowIfCancellationRequested();
        return new EmphasisCueHistory(
            history.SourceKind,
            history.SourceCandidate,
            history.AdoptedSuggestion,
            [.. history.Revisions, revision]);
    }

    private static EmphasisCueHistory CreateInitialHistory(
        EmphasisCueSourceKind sourceKind,
        EmphasisCandidate? sourceCandidate,
        SuggestionReference? adoptedSuggestion,
        CompositionVersion compositionVersion,
        TopicMetadataRevision currentTopicMetadata,
        EmphasisCuePlacementContext context,
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        EmphasisCueSnapshot snapshot,
        OriginRef origin,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        CancellationToken cancellationToken)
    {
        if (createdAt < compositionVersion.CreatedAt)
        {
            throw Failure(EmphasisCueFailureReason.RevisionConflict);
        }

        ContentDigest contentDigest = ComputeContentDigest(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var change = new EmphasisCueChange(
            EmphasisCueChangeKind.Initial,
            previous: null,
            snapshot,
            origin);
        EmphasisCueRevision revision = CreateRevision(
            revisionId,
            parentRevisionId: null,
            compositionVersion.ProjectId,
            compositionVersion,
            currentTopicMetadata,
            context,
            snapshot,
            change,
            actorSid,
            createdAt,
            reason,
            contentDigest);
        return new EmphasisCueHistory(
            sourceKind,
            sourceCandidate,
            adoptedSuggestion,
            [revision]);
    }

    private static EmphasisCueRevision CreateRevision(
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        EntityId<EmphasisCueRevisionIdTag>? parentRevisionId,
        EntityId<ProjectIdTag> projectId,
        CompositionVersion compositionVersion,
        TopicMetadataRevision topicMetadata,
        EmphasisCuePlacementContext context,
        EmphasisCueSnapshot snapshot,
        EmphasisCueChange change,
        string actorSid,
        DateTimeOffset createdAt,
        string reason,
        ContentDigest contentDigest)
    {
        try
        {
            return new EmphasisCueRevision(
                revisionId,
                parentRevisionId,
                projectId,
                compositionVersion.Id,
                compositionVersion.ParentVersionId,
                compositionVersion.ContentDigest,
                topicMetadata.Id,
                context.TopicOutputRange,
                context.EvidenceOutputRange,
                context.LayoutSpec.Id,
                context.LayoutProfile.VersionDigest,
                snapshot,
                change,
                actorSid,
                createdAt,
                reason,
                contentDigest);
        }
        catch (EmphasisCueException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw Failure(EmphasisCueFailureReason.SerializationFailed);
        }
    }

    private static EmphasisCueSnapshot ApplyEdit(
        EmphasisCueSnapshot current,
        EmphasisCueEdit edit)
    {
        return edit.Kind switch
        {
            EmphasisCueChangeKind.Text => CreateSnapshot(
                current.Cue,
                edit.Text!,
                current.Style,
                edit.Origin,
                current.AdoptedSuggestion,
                textSource: null),
            EmphasisCueChangeKind.Timing => CreateSnapshot(
                CreateCue(
                    current.Cue.Id,
                    edit.OutputRange!.Value,
                    current.Cue.RegionName!),
                current.Text,
                current.Style,
                current.TextOrigin,
                current.AdoptedSuggestion,
                current.TextSource),
            EmphasisCueChangeKind.Style => CreateSnapshot(
                current.Cue,
                current.Text,
                edit.Style!.Value,
                current.TextOrigin,
                current.AdoptedSuggestion,
                current.TextSource),
            EmphasisCueChangeKind.Region => CreateSnapshot(
                CreateCue(
                    current.Cue.Id,
                    current.Cue.Range,
                    edit.RegionName!),
                current.Text,
                current.Style,
                current.TextOrigin,
                current.AdoptedSuggestion,
                current.TextSource),
            _ => throw new InvalidOperationException(
                "Unknown emphasis cue edit kind."),
        };
    }

    private static EmphasisCueSnapshot CreateSnapshot(
        Cue cue,
        string text,
        SubtitleCueStyle style,
        OriginRef textOrigin,
        SuggestionReference? adoptedSuggestion,
        EmphasisTextSourceKind? textSource)
    {
        try
        {
            return new EmphasisCueSnapshot(
                cue,
                text,
                style,
                textOrigin,
                adoptedSuggestion,
                textSource);
        }
        catch (EmphasisCueException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(EmphasisCueFailureReason.InvalidStyle);
        }
        catch (ArgumentException)
        {
            throw Failure(EmphasisCueFailureReason.InvalidText);
        }
    }

    private static Cue CreateCue(
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string regionName)
    {
        try
        {
            return new Cue(cueId, CueKind.Graphic, outputRange, regionName);
        }
        catch (ArgumentException)
        {
            throw Failure(EmphasisCueFailureReason.TimingOutsideEvidence);
        }
        catch (OverflowException)
        {
            throw Failure(EmphasisCueFailureReason.TimingOutsideEvidence);
        }
    }

    private static void ValidateSourceAndTopic(
        CompositionVersion compositionVersion,
        EmphasisCandidate candidate,
        TopicMetadataRevision topicMetadata,
        EmphasisCuePlacementContext context)
    {
        if (candidate.TopicMetadataRevisionId != topicMetadata.Id
            || candidate.SourceRevisionId != topicMetadata.SourceRevisionId
            || candidate.SourceRevisionDigest !=
                topicMetadata.SourceRevisionDigest
            || topicMetadata.ProjectId != compositionVersion.ProjectId)
        {
            throw Failure(EmphasisCueFailureReason.TopicBindingMismatch);
        }

        if (context.CurrentSourceRevisionDigest !=
            candidate.SourceRevisionDigest)
        {
            throw Failure(EmphasisCueFailureReason.SourceCandidateStale);
        }
    }

    private static void ValidateManualContext(
        CompositionVersion compositionVersion,
        TopicMetadataRevision topicMetadata,
        EmphasisCuePlacementContext context,
        OriginRef editorialOrigin)
    {
        if (topicMetadata.ProjectId != compositionVersion.ProjectId)
        {
            throw Failure(EmphasisCueFailureReason.TopicBindingMismatch);
        }

        if (editorialOrigin.Kind != OriginKind.EditorialRecord)
        {
            throw Failure(EmphasisCueFailureReason.InvalidOrigin);
        }

        if (context.EvidenceOutputRange is not null
            || context.CurrentSourceRevisionDigest is not null)
        {
            throw Failure(EmphasisCueFailureReason.LayoutBindingMismatch);
        }
    }

    private static void ValidateHistoryContext(
        EmphasisCueHistory history,
        TopicMetadataRevision topicMetadata,
        EmphasisCuePlacementContext context)
    {
        EmphasisCueRevision current = history.CurrentRevision;
        if (topicMetadata.Id != current.TopicMetadataRevisionId
            || topicMetadata.ProjectId != history.ProjectId
            || context.LayoutSpec.Id != current.LayoutSpecId
            || context.LayoutProfile.VersionDigest !=
                current.LayoutProfileDigest
            || context.TopicOutputRange != current.TopicOutputRange
            || context.EvidenceOutputRange != current.EvidenceOutputRange)
        {
            throw Failure(EmphasisCueFailureReason.LayoutBindingMismatch);
        }

        if (history.SourceKind == EmphasisCueSourceKind.AdoptedSuggestion)
        {
            if (context.CurrentSourceRevisionDigest !=
                history.SourceRevisionDigest
                || topicMetadata.SourceRevisionDigest !=
                    history.SourceRevisionDigest)
            {
                throw Failure(EmphasisCueFailureReason.SourceCandidateStale);
            }
        }
        else if (context.CurrentSourceRevisionDigest is not null)
        {
            throw Failure(EmphasisCueFailureReason.LayoutBindingMismatch);
        }
    }

    private static void ValidateCue(
        Cue cue,
        EmphasisCuePlacementContext context,
        bool requireEvidenceRange,
        Cue? replacedCue)
    {
        if (!Contains(context.TopicOutputRange, cue.Range)
            || requireEvidenceRange
                && (context.EvidenceOutputRange is not TimeRange evidence
                    || !Contains(evidence, cue.Range)))
        {
            throw Failure(EmphasisCueFailureReason.TimingOutsideEvidence);
        }

        LayoutRegion? targetRegion = context.LayoutSpec.Regions.SingleOrDefault(
            region => string.Equals(
                region.Name,
                cue.RegionName,
                StringComparison.Ordinal));
        if (targetRegion is null || targetRegion.Role != LayoutRegionRole.Graphic)
        {
            throw Failure(EmphasisCueFailureReason.InvalidGraphicRegion);
        }

        var safeZones = context.LayoutSpec.SafeZones.ToDictionary(
            static zone => zone.Name,
            StringComparer.Ordinal);
        foreach (string safeZoneName in
            context.LayoutProfile.RequiredSafeZoneNames)
        {
            if (!safeZones.TryGetValue(safeZoneName, out SafeZone? safeZone)
                || !EmphasisCueGeometry.Contains(
                    safeZone.Rect,
                    targetRegion.Rect))
            {
                throw Failure(EmphasisCueFailureReason.LayoutBindingMismatch);
            }
        }

        if (context.ProhibitedRegions.Any(region =>
            EmphasisCueGeometry.Overlaps(targetRegion.Rect, region.Rect)))
        {
            throw Failure(
                EmphasisCueFailureReason.ProhibitedRegionOverlap);
        }

        foreach (Cue existingCue in context.ExistingCues)
        {
            if (existingCue.Id == cue.Id)
            {
                if (replacedCue is null || !existingCue.Equals(replacedCue))
                {
                    throw Failure(EmphasisCueFailureReason.VisualConflict);
                }

                continue;
            }

            if (existingCue.Kind == CueKind.Audio
                || !existingCue.Range.Overlaps(cue.Range))
            {
                continue;
            }

            if (existingCue.RegionName is null)
            {
                throw Failure(EmphasisCueFailureReason.VisualConflict);
            }

            LayoutRegion? existingRegion = context.LayoutSpec.Regions
                .SingleOrDefault(region => string.Equals(
                    region.Name,
                    existingCue.RegionName,
                    StringComparison.Ordinal));
            if (existingRegion is null)
            {
                throw Failure(EmphasisCueFailureReason.LayoutBindingMismatch);
            }

            if (EmphasisCueGeometry.Overlaps(
                targetRegion.Rect,
                existingRegion.Rect))
            {
                throw Failure(EmphasisCueFailureReason.VisualConflict);
            }
        }
    }

    private static void ValidateQuote(
        EmphasisCueSnapshot snapshot,
        EmphasisCandidate? sourceCandidate)
    {
        if (snapshot.Style != SubtitleCueStyle.Quote)
        {
            return;
        }

        if (sourceCandidate is null
            || !sourceCandidate.IsExactOriginalText
            || !string.Equals(
                snapshot.Text,
                sourceCandidate.Text,
                StringComparison.Ordinal)
            || snapshot.TextOrigin.Kind != OriginKind.SourceSpan)
        {
            throw Failure(EmphasisCueFailureReason.UngroundedQuote);
        }
    }

    private static void ValidateChildComposition(
        EmphasisCueHistory history,
        CompositionVersion child)
    {
        if (child.ProjectId != history.ProjectId
            || child.ParentVersionId !=
                history.CurrentRevision.CompositionVersionId
            || history.Revisions.Any(revision =>
                revision.CompositionVersionId == child.Id))
        {
            throw Failure(
                EmphasisCueFailureReason.CompositionVersionConflict);
        }
    }

    private static void ValidateRevisionRequest(
        EmphasisCueHistory history,
        CompositionVersion child,
        EntityId<EmphasisCueRevisionIdTag> revisionId,
        DateTimeOffset createdAt)
    {
        if (history.Revisions.Any(revision => revision.Id == revisionId)
            || createdAt < history.CurrentRevision.CreatedAt
            || createdAt < child.CreatedAt)
        {
            throw Failure(EmphasisCueFailureReason.RevisionConflict);
        }
    }

    private static void ValidateCompositionIntegrity(
        CompositionVersion compositionVersion)
    {
        ContentDigest actual;
        try
        {
            actual = ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8(
                    compositionVersion.Elements));
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or EncoderFallbackException)
        {
            throw Failure(
                EmphasisCueFailureReason.CompositionVersionConflict);
        }

        if (actual != compositionVersion.ContentDigest)
        {
            throw Failure(
                EmphasisCueFailureReason.CompositionVersionConflict);
        }
    }

    private static void ValidateActorReasonAndStyle(
        string actorSid,
        string reason,
        SubtitleCueStyle style)
    {
        ValidateActorAndReason(actorSid, reason);
        try
        {
            EmphasisCueContract.ValidateStyle(style);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(EmphasisCueFailureReason.InvalidStyle);
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
            throw Failure(EmphasisCueFailureReason.InvalidActor);
        }

        try
        {
            EmphasisCueContract.ValidateText(reason, nameof(reason));
        }
        catch (ArgumentException)
        {
            throw Failure(EmphasisCueFailureReason.InvalidReason);
        }
    }

    private static ContentDigest ComputeContentDigest(
        EmphasisCueSnapshot snapshot)
    {
        try
        {
            return EmphasisCueSnapshotJson.ComputeContentDigest(snapshot);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or JsonException
                or EncoderFallbackException)
        {
            throw Failure(EmphasisCueFailureReason.SerializationFailed);
        }
    }

    private static bool Contains(TimeRange outer, TimeRange inner) =>
        inner.Start >= outer.Start && inner.End <= outer.End;

    private static EmphasisCueException Failure(
        EmphasisCueFailureReason reason) =>
        new(reason);
}

internal static class EmphasisCueContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Emphasis cue text must be nonblank and trimmed.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Emphasis cue text must be valid Unicode.",
                parameterName,
                exception);
        }
    }

    internal static void ValidateStyle(SubtitleCueStyle style)
    {
        if (!Enum.IsDefined(style) || style == SubtitleCueStyle.Normal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(style),
                style,
                "An emphasis cue must use emphasis, caution, or quote style.");
        }
    }

    internal static bool SnapshotsEqual(
        EmphasisCueSnapshot left,
        EmphasisCueSnapshot right) =>
        left.Cue.Equals(right.Cue)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.Style == right.Style
        && left.TextOrigin == right.TextOrigin
        && SuggestionEquals(left.AdoptedSuggestion, right.AdoptedSuggestion)
        && left.TextSource == right.TextSource;

    internal static bool OnlyFieldChanged(
        EmphasisCueSnapshot previous,
        EmphasisCueSnapshot current,
        EmphasisCueChangeKind kind)
    {
        bool cueIdAndKindEqual = previous.Cue.Id == current.Cue.Id
            && previous.Cue.Kind == current.Cue.Kind;
        bool textEqual = string.Equals(
            previous.Text,
            current.Text,
            StringComparison.Ordinal);
        bool styleEqual = previous.Style == current.Style;
        bool suggestionEqual = SuggestionEquals(
            previous.AdoptedSuggestion,
            current.AdoptedSuggestion);
        return kind switch
        {
            EmphasisCueChangeKind.Text =>
                previous.Cue.Equals(current.Cue)
                && !textEqual
                && styleEqual
                && current.TextOrigin.Kind == OriginKind.EditorialRecord
                && suggestionEqual
                && current.TextSource is null,
            EmphasisCueChangeKind.Timing =>
                cueIdAndKindEqual
                && previous.Cue.Range != current.Cue.Range
                && string.Equals(
                    previous.Cue.RegionName,
                    current.Cue.RegionName,
                    StringComparison.Ordinal)
                && textEqual
                && styleEqual
                && previous.TextOrigin == current.TextOrigin
                && suggestionEqual
                && previous.TextSource == current.TextSource,
            EmphasisCueChangeKind.Style =>
                previous.Cue.Equals(current.Cue)
                && textEqual
                && previous.Style != current.Style
                && previous.TextOrigin == current.TextOrigin
                && suggestionEqual
                && previous.TextSource == current.TextSource,
            EmphasisCueChangeKind.Region =>
                cueIdAndKindEqual
                && previous.Cue.Range == current.Cue.Range
                && !string.Equals(
                    previous.Cue.RegionName,
                    current.Cue.RegionName,
                    StringComparison.Ordinal)
                && textEqual
                && styleEqual
                && previous.TextOrigin == current.TextOrigin
                && suggestionEqual
                && previous.TextSource == current.TextSource,
            _ => false,
        };
    }

    private static bool SuggestionEquals(
        SuggestionReference? left,
        SuggestionReference? right) =>
        left is null
            ? right is null
            : right is not null
                && left.SuggestionId == right.SuggestionId
                && left.Origin == right.Origin;

    internal static EmphasisCueException Failure(
        EmphasisCueFailureReason reason) =>
        new(reason);
}

internal static class EmphasisCueGeometry
{
    internal static bool Contains(NormalizedRect outer, NormalizedRect inner) =>
        outer.X <= inner.X
        && outer.Y <= inner.Y
        && CompareSums(outer.X, outer.Width, inner.X, inner.Width) >= 0
        && CompareSums(outer.Y, outer.Height, inner.Y, inner.Height) >= 0;

    internal static bool Overlaps(NormalizedRect left, NormalizedRect right) =>
        CompareSums(left.X, left.Width, right.X, default) > 0
        && CompareSums(right.X, right.Width, left.X, default) > 0
        && CompareSums(left.Y, left.Height, right.Y, default) > 0
        && CompareSums(right.Y, right.Height, left.Y, default) > 0;

    private static int CompareSums(
        RationalNumber firstLeft,
        RationalNumber secondLeft,
        RationalNumber firstRight,
        RationalNumber secondRight)
    {
        BigInteger leftNumerator =
            ((BigInteger)firstLeft.Numerator * secondLeft.Denominator)
            + ((BigInteger)secondLeft.Numerator * firstLeft.Denominator);
        BigInteger leftDenominator =
            (BigInteger)firstLeft.Denominator * secondLeft.Denominator;
        BigInteger rightNumerator =
            ((BigInteger)firstRight.Numerator * secondRight.Denominator)
            + ((BigInteger)secondRight.Numerator * firstRight.Denominator);
        BigInteger rightDenominator =
            (BigInteger)firstRight.Denominator * secondRight.Denominator;
        return (leftNumerator * rightDenominator)
            .CompareTo(rightNumerator * leftDenominator);
    }
}

internal static class EmphasisCueSnapshotJson
{
    internal static ContentDigest ComputeContentDigest(
        EmphasisCueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("cue");
            writer.WriteStartObject();
            writer.WriteString("id", snapshot.Cue.Id.ToString());
            writer.WriteString("kind", "graphic");
            writer.WriteString("regionName", snapshot.Cue.RegionName);
            writer.WritePropertyName("range");
            WriteRange(writer, snapshot.Cue.Range);
            writer.WriteEndObject();
            writer.WriteString("text", snapshot.Text);
            writer.WriteString(
                "style",
                snapshot.Style.ToString().ToLowerInvariant());
            writer.WritePropertyName("textOrigin");
            WriteOrigin(writer, snapshot.TextOrigin);
            if (snapshot.AdoptedSuggestion is null)
            {
                writer.WriteNull("adoptedSuggestion");
            }
            else
            {
                writer.WritePropertyName("adoptedSuggestion");
                writer.WriteStartObject();
                writer.WriteString(
                    "suggestionId",
                    snapshot.AdoptedSuggestion.SuggestionId.ToString());
                writer.WritePropertyName("origin");
                WriteOrigin(writer, snapshot.AdoptedSuggestion.Origin);
                writer.WriteEndObject();
            }

            if (snapshot.TextSource is EmphasisTextSourceKind textSource)
            {
                writer.WriteString(
                    "textSource",
                    textSource == EmphasisTextSourceKind.OriginalTranscript
                        ? "originalTranscript"
                        : "manualSummary");
            }
            else
            {
                writer.WriteNull("textSource");
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return ContentDigest.Compute(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    private static void WriteRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, RationalTime time)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", time.Value);
        writer.WriteNumber("rate", time.Rate);
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan when origin.SourceSpanId is not null:
                writer.WriteString("kind", "sourceSpan");
                writer.WriteString(
                    "sourceSpanId",
                    origin.SourceSpanId.ToString());
                break;
            case OriginKind.EditorialRecord
                when origin.EditorialRecordId is not null:
                writer.WriteString("kind", "editorialRecord");
                writer.WriteString(
                    "editorialRecordId",
                    origin.EditorialRecordId.ToString());
                break;
            default:
                throw new InvalidOperationException(
                    "The emphasis cue origin is invalid.");
        }

        writer.WriteEndObject();
    }
}