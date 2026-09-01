using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Delivery;
using HubRadio.App.Graphics;
using HubRadio.App.Rendering;
using HubRadio.App.Security;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Security;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum LayoutSubtitleBridgeStatus
{
    Available,
    Forbidden,
    NotFound,
    Conflict,
    NoChange,
    Invalid,
    Unavailable,
}

internal sealed class LayoutSubtitleSourceBinding
{
    internal LayoutSubtitleSourceBinding(
        EntityId<AssetIdTag> sourceAssetId,
        OriginRef origin,
        SourceDisplayGeometry displayGeometry)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(displayGeometry);
        if (origin.Kind == OriginKind.EditorialRecord
            || origin.Kind == OriginKind.Asset && origin.AssetId != sourceAssetId)
        {
            throw new ArgumentException(
                "A layout source origin must identify the bound source or asset.",
                nameof(origin));
        }

        SourceAssetId = sourceAssetId;
        Origin = origin;
        DisplayGeometry = displayGeometry;
    }

    internal EntityId<AssetIdTag> SourceAssetId { get; }

    internal OriginRef Origin { get; }

    internal SourceDisplayGeometry DisplayGeometry { get; }
}

internal sealed class LayoutSubtitleManualSelection
{
    internal LayoutSubtitleManualSelection(
        ManualLayoutResolution resolution,
        IReadOnlyList<ManualLayoutSource> sources,
        IReadOnlyList<ManualDestinationGeometry> destinations,
        TemplateReference? template)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destinations);
        ManualLayoutSource[] copiedSources = sources.ToArray();
        ManualDestinationGeometry[] copiedDestinations = destinations.ToArray();
        if (copiedSources.Length == 0
            || copiedSources.Length != copiedDestinations.Length
            || copiedSources.Any(static source => source is null)
            || copiedDestinations.Any(static destination => destination is null)
            || (resolution.Mode == ManualLayoutMode.FixedTemplatePlacement)
                != (template is not null))
        {
            throw new ArgumentException(
                "The manual layout selection is inconsistent.",
                nameof(sources));
        }

        Resolution = resolution;
        Sources = Array.AsReadOnly(copiedSources);
        Destinations = Array.AsReadOnly(copiedDestinations);
        Template = template;
    }

    internal ManualLayoutResolution Resolution { get; }

    internal IReadOnlyList<ManualLayoutSource> Sources { get; }

    internal IReadOnlyList<ManualDestinationGeometry> Destinations { get; }

    internal TemplateReference? Template { get; }
}

internal sealed class LayoutSubtitleKeyframeBinding
{
    internal LayoutSubtitleKeyframeBinding(
        KeyframeTrack track,
        RationalTime evaluationTime)
    {
        ArgumentNullException.ThrowIfNull(track);
        Track = track;
        EvaluationTime = evaluationTime;
        Evaluation = track.Evaluate(evaluationTime);
    }

    internal KeyframeTrack Track { get; }

    internal RationalTime EvaluationTime { get; }

    internal LayoutKeyframeEvaluation Evaluation { get; }

    internal LayoutSubtitleKeyframeBinding EvaluateAt(RationalTime time) =>
        new(Track, time);
}

internal sealed class LayoutSubtitleCueBinding
{
    internal LayoutSubtitleCueBinding(
        SubtitleCueHistory history,
        LayoutSpec layoutSpec,
        SubtitleLayoutProfile layoutProfile,
        IReadOnlyList<SubtitleProhibitedRegion> prohibitedRegions,
        IReadOnlyList<NormalizedRect> faceRegions)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(layoutProfile);
        ArgumentNullException.ThrowIfNull(prohibitedRegions);
        ArgumentNullException.ThrowIfNull(faceRegions);
        if (history.CurrentRevision.Snapshot.LayoutResolution.LayoutSpecId
                != layoutSpec.Id
            || history.CurrentRevision.Snapshot.LayoutResolution
                    .ProfileVersionDigest
                != layoutProfile.VersionDigest
            || layoutProfile.LayoutSpecId != layoutSpec.Id)
        {
            throw new ArgumentException(
                "The subtitle history and layout context must identify one version.",
                nameof(layoutSpec));
        }

        SubtitleProhibitedRegion[] prohibited = prohibitedRegions.ToArray();
        NormalizedRect[] faces = faceRegions.ToArray();
        if (prohibited.Any(static region => region is null)
            || faces.Any(static region => region is null))
        {
            throw new ArgumentException(
                "Subtitle layout context collections must not contain null values.",
                nameof(prohibitedRegions));
        }

        History = history;
        LayoutSpec = layoutSpec;
        LayoutProfile = layoutProfile;
        ProhibitedRegions = Array.AsReadOnly(prohibited);
        FaceRegions = Array.AsReadOnly(faces);
    }

    internal SubtitleCueHistory History { get; }

    internal LayoutSpec LayoutSpec { get; }

    internal SubtitleLayoutProfile LayoutProfile { get; }

    internal IReadOnlyList<SubtitleProhibitedRegion> ProhibitedRegions { get; }

    internal IReadOnlyList<NormalizedRect> FaceRegions { get; }

    internal SubtitleLayoutResolution ResolveManualPlacement(
        SubtitlePlacementCandidate placement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        SubtitleCueSnapshot current = History.CurrentRevision.Snapshot;
        var request = new SubtitleLayoutRequest(
            History.SourceCandidate,
            LayoutSpec,
            LayoutProfile,
            [placement],
            ProhibitedRegions,
            FaceRegions,
            current.ResolvedPlacement?.Rect);
        return SubtitleLayoutEngine.Resolve(request, cancellationToken);
    }

    internal LayoutSubtitleCueBinding WithHistory(SubtitleCueHistory history) =>
        new(
            history,
            LayoutSpec,
            LayoutProfile,
            ProhibitedRegions,
            FaceRegions);
}

internal sealed class LayoutSubtitleTeropBinding
{
    internal LayoutSubtitleTeropBinding(
        EmphasisCueHistory history,
        TopicMetadataRevision currentTopicMetadata,
        EmphasisCuePlacementContext placementContext)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(currentTopicMetadata);
        ArgumentNullException.ThrowIfNull(placementContext);
        if (history.CurrentRevision.TopicMetadataRevisionId
                != currentTopicMetadata.Id
            || history.CurrentRevision.LayoutSpecId
                != placementContext.LayoutSpec.Id
            || history.CurrentRevision.LayoutProfileDigest
                != placementContext.LayoutProfile.VersionDigest)
        {
            throw new ArgumentException(
                "The terop history and placement context must identify one version.",
                nameof(placementContext));
        }

        History = history;
        CurrentTopicMetadata = currentTopicMetadata;
        PlacementContext = placementContext;
    }

    internal EmphasisCueHistory History { get; }

    internal TopicMetadataRevision CurrentTopicMetadata { get; }

    internal EmphasisCuePlacementContext PlacementContext { get; }

    internal LayoutSubtitleTeropBinding WithHistory(EmphasisCueHistory history)
    {
        Cue previous = History.CurrentRevision.Snapshot.Cue;
        Cue revised = history.CurrentRevision.Snapshot.Cue;
        Cue[] existing = PlacementContext.ExistingCues
            .Select(cue => cue.Id == previous.Id ? revised : cue)
            .ToArray();
        if (!existing.Any(cue => cue.Id == revised.Id))
        {
            existing = [.. existing, revised];
        }

        var context = new EmphasisCuePlacementContext(
            PlacementContext.LayoutSpec,
            PlacementContext.LayoutProfile,
            PlacementContext.TopicOutputRange,
            PlacementContext.EvidenceOutputRange,
            PlacementContext.CurrentSourceRevisionDigest,
            existing,
            PlacementContext.ProhibitedRegions);
        return new LayoutSubtitleTeropBinding(
            history,
            CurrentTopicMetadata,
            context);
    }
}

internal sealed class LayoutSubtitleDisplayBinding
{
    internal LayoutSubtitleDisplayBinding(
        SubtitleDisplayProfile profile,
        IReadOnlyList<EntityId<CueIdTag>> selectedCueIds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selectedCueIds);
        EntityId<CueIdTag>[] copiedSelection = selectedCueIds.ToArray();
        if (copiedSelection.Any(static id => id is null)
            || copiedSelection.Distinct().Count() != copiedSelection.Length)
        {
            throw new ArgumentException(
                "Subtitle display selections must contain unique non-null cue identities.",
                nameof(selectedCueIds));
        }

        Profile = profile;
        SelectedCueIds = Array.AsReadOnly(copiedSelection);
    }

    internal SubtitleDisplayProfile Profile { get; }

    internal IReadOnlyList<EntityId<CueIdTag>> SelectedCueIds { get; }

    internal SubtitleDisplayResult Apply(
        IReadOnlyList<LayoutSubtitleCueBinding> subtitles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        SubtitleDisplaySourceCue[] sources = subtitles
            .Select(static binding => SubtitleDisplayMapper.MapSource(
                binding.History.CurrentRevision.Snapshot))
            .ToArray();
        return SubtitleDisplayPolicy.Apply(
            Profile,
            sources,
            SelectedCueIds,
            cancellationToken);
    }
}

internal sealed class LayoutSubtitleVariantPair
{
    internal LayoutSubtitleVariantPair(SubtitleVariantRunResult result)
        : this(
            GetCompletedManifest(result, burnedIn: true),
            GetCompletedManifest(result, burnedIn: false))
    {
    }

    internal LayoutSubtitleVariantPair(
        DeliveryManifest burnedIn,
        DeliveryManifest subtitleFree)
    {
        ArgumentNullException.ThrowIfNull(burnedIn);
        ArgumentNullException.ThrowIfNull(subtitleFree);
        SubtitleDeliveryVariantEvidence? first = burnedIn.SubtitleVariant;
        SubtitleDeliveryVariantEvidence? second = subtitleFree.SubtitleVariant;
        if (first?.Kind != SubtitleDeliveryVariantKind.BurnedIn
            || second?.Kind != SubtitleDeliveryVariantKind.SubtitleFree
            || burnedIn.Id == subtitleFree.Id
            || first.RenderGraphId == second.RenderGraphId
            || burnedIn.ProjectId != subtitleFree.ProjectId
            || burnedIn.CompositionVersionId != subtitleFree.CompositionVersionId
            || burnedIn.CompositionVersionDigest
                != subtitleFree.CompositionVersionDigest
            || first.DisplayProfileDigest != second.DisplayProfileDigest
            || first.SubtitleEvidenceDigest != second.SubtitleEvidenceDigest)
        {
            throw new ArgumentException(
                "Subtitle variants must be one exact burned-in and subtitle-free pair.",
                nameof(subtitleFree));
        }

        BurnedIn = burnedIn;
        SubtitleFree = subtitleFree;
    }

    internal DeliveryManifest BurnedIn { get; }

    internal DeliveryManifest SubtitleFree { get; }

    private static DeliveryManifest GetCompletedManifest(
        SubtitleVariantRunResult result,
        bool burnedIn)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsComplete)
        {
            throw new ArgumentException(
                "Only a completed subtitle variant run can be displayed as a pair.",
                nameof(result));
        }

        DeliveryCreationResult? delivery = burnedIn
            ? result.BurnedInDelivery
            : result.SubtitleFreeDelivery;
        return delivery?.Manifest
            ?? throw new ArgumentException(
                "The completed subtitle variant run is missing delivery evidence.",
                nameof(result));
    }
}

internal sealed class LayoutSubtitleBridgeSnapshot
{
    private readonly IReadOnlyDictionary<
        EntityId<CompositionVersionIdTag>,
        CompositionVersion> compositions;

    internal LayoutSubtitleBridgeSnapshot(
        EntityId<ProjectIdTag> projectId,
        int revision,
        CompositionVersion compositionVersion,
        LayoutSpec outputEnvelope,
        int outputPixelWidth,
        int outputPixelHeight,
        IReadOnlyList<LayoutSubtitleSourceBinding> sources,
        IReadOnlyList<TemplateReference> templates,
        IReadOnlyList<LayoutSubtitleCueBinding> subtitles,
        IReadOnlyList<LayoutSubtitleTeropBinding> terops,
        IReadOnlyList<LayoutSubtitleDisplayBinding> subtitleDisplays,
        LayoutSubtitleManualSelection? manualSelection = null,
        IReadOnlyList<LayoutSubtitleKeyframeBinding>? keyframes = null,
        LayoutSubtitleVariantPair? variantPair = null,
        IReadOnlyDictionary<
            EntityId<CompositionVersionIdTag>,
            CompositionVersion>? compositionVersions = null)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(outputEnvelope);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(terops);
        ArgumentNullException.ThrowIfNull(subtitleDisplays);
        if (revision < 0
            || compositionVersion.ProjectId != projectId
            || (BigInteger)outputEnvelope.OutputAspectWidth * 16
                != (BigInteger)outputEnvelope.OutputAspectHeight * 9
            || outputEnvelope.SafeZones.Count != 1
            || !string.Equals(
                outputEnvelope.SafeZones[0].Name,
                "output-frame-bounds",
                StringComparison.Ordinal)
            || !IsFullFrame(outputEnvelope.SafeZones[0].Rect))
        {
            throw new ArgumentException(
                "The layout workspace identity, revision, or 9:16 output envelope is invalid.",
                nameof(compositionVersion));
        }

        _ = LayoutResolver.Resolve(
            outputEnvelope,
            outputPixelWidth,
            outputPixelHeight);
        LayoutSubtitleSourceBinding[] copiedSources = sources.ToArray();
        TemplateReference[] copiedTemplates = templates.ToArray();
        LayoutSubtitleCueBinding[] copiedSubtitles = subtitles.ToArray();
        LayoutSubtitleTeropBinding[] copiedTerops = terops.ToArray();
        LayoutSubtitleDisplayBinding[] copiedSubtitleDisplays =
            subtitleDisplays.ToArray();
        LayoutSubtitleKeyframeBinding[] copiedKeyframes =
            keyframes?.ToArray() ?? [];
        if (copiedSources.Any(static source => source is null)
            || copiedTemplates.Any(static template => template is null)
            || copiedSubtitles.Any(static subtitle => subtitle is null)
            || copiedTerops.Any(static terop => terop is null)
            || copiedSubtitleDisplays.Any(static display => display is null)
            || copiedKeyframes.Any(static keyframe => keyframe is null)
            || copiedSources.Select(static source => source.SourceAssetId)
                .Distinct().Count() != copiedSources.Length
            || copiedTemplates.Select(static template => template.Id)
                .Distinct().Count() != copiedTemplates.Length
            || copiedSubtitles.Select(static subtitle => subtitle.History.CueId)
                .Distinct().Count() != copiedSubtitles.Length
            || copiedTerops.Select(static terop => terop.History.CueId)
                .Distinct().Count() != copiedTerops.Length
            || copiedSubtitleDisplays
                .Select(static display => display.Profile.VersionDigest)
                .Distinct().Count() != copiedSubtitleDisplays.Length
            || copiedSubtitleDisplays
                .Select(static display => (
                    display.Profile.CueMode,
                    display.Profile.PunctuationDisplay))
                .Distinct().Count() != copiedSubtitleDisplays.Length
            || copiedKeyframes.Select(static keyframe => (
                    keyframe.Track.SourceAssetId,
                    keyframe.Track.DestinationSlot))
                .Distinct().Count() != copiedKeyframes.Length
            || copiedSubtitles.Any(subtitle =>
                subtitle.History.ProjectId != projectId)
            || copiedTerops.Any(terop => terop.History.ProjectId != projectId))
        {
            throw new ArgumentException(
                "The layout workspace collections are inconsistent.",
                nameof(sources));
        }

        try
        {
            foreach (LayoutSubtitleDisplayBinding display in
                copiedSubtitleDisplays)
            {
                _ = display.Apply(copiedSubtitles);
            }
        }
        catch (SubtitleDisplayException exception)
        {
            throw new ArgumentException(
                "The subtitle display profile or selection is inconsistent with the current cues.",
                nameof(subtitleDisplays),
                exception);
        }

        var versions = compositionVersions is null
            ? new Dictionary<
                EntityId<CompositionVersionIdTag>,
                CompositionVersion>()
            : new Dictionary<
                EntityId<CompositionVersionIdTag>,
                CompositionVersion>(compositionVersions);
        versions[compositionVersion.Id] = compositionVersion;
        foreach (LayoutSubtitleCueBinding subtitle in copiedSubtitles)
        {
            foreach (SubtitleCueRevision cueRevision in subtitle.History.Revisions)
            {
                if (!versions.TryGetValue(
                        cueRevision.CompositionVersionId,
                        out CompositionVersion? version)
                    || version.ProjectId != projectId
                    || version.ContentDigest != cueRevision.CompositionVersionDigest)
                {
                    throw new ArgumentException(
                        "Every subtitle revision must bind a supplied composition version.",
                        nameof(compositionVersions));
                }
            }
        }

        foreach (LayoutSubtitleTeropBinding terop in copiedTerops)
        {
            foreach (EmphasisCueRevision cueRevision in terop.History.Revisions)
            {
                if (!versions.TryGetValue(
                        cueRevision.CompositionVersionId,
                        out CompositionVersion? version)
                    || version.ProjectId != projectId
                    || version.ContentDigest != cueRevision.CompositionVersionDigest)
                {
                    throw new ArgumentException(
                        "Every terop revision must bind a supplied composition version.",
                        nameof(compositionVersions));
                }
            }
        }

        if (variantPair is not null
            && (variantPair.BurnedIn.ProjectId != projectId
                || variantPair.BurnedIn.CompositionVersionId
                    != compositionVersion.Id
                || variantPair.BurnedIn.CompositionVersionDigest
                    != compositionVersion.ContentDigest
                || !copiedSubtitleDisplays.Any(display =>
                    display.Profile.VersionDigest
                        == variantPair.BurnedIn.SubtitleVariant!
                            .DisplayProfileDigest)))
        {
            throw new ArgumentException(
                "The subtitle variant pair must bind the displayed composition version.",
                nameof(variantPair));
        }

        ProjectId = projectId;
        Revision = revision;
        CompositionVersion = compositionVersion;
        OutputEnvelope = outputEnvelope;
        OutputPixelWidth = outputPixelWidth;
        OutputPixelHeight = outputPixelHeight;
        Sources = Array.AsReadOnly(copiedSources);
        Templates = Array.AsReadOnly(copiedTemplates);
        Subtitles = Array.AsReadOnly(copiedSubtitles);
        Terops = Array.AsReadOnly(copiedTerops);
        SubtitleDisplays = Array.AsReadOnly(copiedSubtitleDisplays);
        ManualSelection = manualSelection;
        Keyframes = Array.AsReadOnly(copiedKeyframes);
        VariantPair = variantPair;
        this.compositions = versions;
    }

    private static bool IsFullFrame(NormalizedRect rect) =>
        rect.X == default
        && rect.Y == default
        && rect.Width == new RationalNumber(1, 1)
        && rect.Height == new RationalNumber(1, 1);

    internal EntityId<ProjectIdTag> ProjectId { get; }

    internal int Revision { get; }

    internal CompositionVersion CompositionVersion { get; }

    internal LayoutSpec OutputEnvelope { get; }

    internal int OutputPixelWidth { get; }

    internal int OutputPixelHeight { get; }

    internal IReadOnlyList<LayoutSubtitleSourceBinding> Sources { get; }

    internal IReadOnlyList<TemplateReference> Templates { get; }

    internal IReadOnlyList<LayoutSubtitleCueBinding> Subtitles { get; }

    internal IReadOnlyList<LayoutSubtitleTeropBinding> Terops { get; }

    internal IReadOnlyList<LayoutSubtitleDisplayBinding> SubtitleDisplays
    { get; }

    internal LayoutSubtitleManualSelection? ManualSelection { get; }

    internal IReadOnlyList<LayoutSubtitleKeyframeBinding> Keyframes { get; }

    internal LayoutSubtitleVariantPair? VariantPair { get; }

    internal CompositionVersion GetComposition(
        EntityId<CompositionVersionIdTag> versionId) =>
        compositions.TryGetValue(versionId, out CompositionVersion? value)
            ? value
            : throw new KeyNotFoundException(
                "The cue composition version is not registered.");

    internal LayoutSubtitleBridgeSnapshot WithManualSelection(
        LayoutSubtitleManualSelection selection) =>
        Copy(
            revision: checked(Revision + 1),
            manualSelection: selection,
            replaceVariantPair: true);

    internal LayoutSubtitleBridgeSnapshot WithKeyframe(
        LayoutSubtitleKeyframeBinding binding)
    {
        LayoutSubtitleKeyframeBinding[] revised =
        [
            .. Keyframes.Where(current =>
                current.Track.SourceAssetId != binding.Track.SourceAssetId
                || current.Track.DestinationSlot
                    != binding.Track.DestinationSlot),
            binding,
        ];
        return Copy(
            revision: checked(Revision + 1),
            keyframes: revised,
            replaceVariantPair: true);
    }

    internal LayoutSubtitleBridgeSnapshot WithSubtitle(
        int index,
        LayoutSubtitleCueBinding binding,
        CompositionVersion child)
    {
        LayoutSubtitleCueBinding[] revised = Subtitles.ToArray();
        revised[index] = binding;
        var versions = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            CompositionVersion>(compositions)
        {
            [child.Id] = child,
        };
        return Copy(
            revision: checked(Revision + 1),
            compositionVersion: child,
            subtitles: revised,
            variantPair: null,
            replaceVariantPair: true,
            compositionVersions: versions);
    }

    internal LayoutSubtitleBridgeSnapshot WithTerop(
        int index,
        LayoutSubtitleTeropBinding binding,
        CompositionVersion child)
    {
        LayoutSubtitleTeropBinding[] revised = Terops.ToArray();
        revised[index] = binding;
        var versions = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            CompositionVersion>(compositions)
        {
            [child.Id] = child,
        };
        return Copy(
            revision: checked(Revision + 1),
            compositionVersion: child,
            terops: revised,
            variantPair: null,
            replaceVariantPair: true,
            compositionVersions: versions);
    }

    internal LayoutSubtitleBridgeSnapshot EvaluateAt(RationalTime time)
    {
        LayoutSubtitleKeyframeBinding[] evaluated = Keyframes
            .Select(binding => binding.EvaluateAt(time))
            .ToArray();
        return Copy(keyframes: evaluated);
    }

    private LayoutSubtitleBridgeSnapshot Copy(
        int? revision = null,
        CompositionVersion? compositionVersion = null,
        IReadOnlyList<LayoutSubtitleCueBinding>? subtitles = null,
        IReadOnlyList<LayoutSubtitleTeropBinding>? terops = null,
        LayoutSubtitleManualSelection? manualSelection = null,
        IReadOnlyList<LayoutSubtitleKeyframeBinding>? keyframes = null,
        LayoutSubtitleVariantPair? variantPair = null,
        bool replaceVariantPair = false,
        IReadOnlyDictionary<
            EntityId<CompositionVersionIdTag>,
            CompositionVersion>? compositionVersions = null) =>
        new(
            ProjectId,
            revision ?? Revision,
            compositionVersion ?? CompositionVersion,
            OutputEnvelope,
            OutputPixelWidth,
            OutputPixelHeight,
            Sources,
            Templates,
            subtitles ?? Subtitles,
            terops ?? Terops,
            SubtitleDisplays,
            manualSelection ?? ManualSelection,
            keyframes ?? Keyframes,
            replaceVariantPair ? variantPair : VariantPair,
            compositionVersions ?? compositions);
}

internal sealed class LayoutSubtitleBridgeConflictException : Exception
{
}

internal sealed class LayoutSubtitleBridgeOperationResult
{
    private LayoutSubtitleBridgeOperationResult(
        LayoutSubtitleBridgeStatus status,
        LayoutSubtitleBridgeSnapshot? snapshot)
    {
        if ((status == LayoutSubtitleBridgeStatus.Available)
            != (snapshot is not null))
        {
            throw new ArgumentException(
                "The layout bridge result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
    }

    internal LayoutSubtitleBridgeStatus Status { get; }

    internal LayoutSubtitleBridgeSnapshot? Snapshot { get; }

    internal static LayoutSubtitleBridgeOperationResult Available(
        LayoutSubtitleBridgeSnapshot snapshot) =>
        new(LayoutSubtitleBridgeStatus.Available, snapshot);

    internal static LayoutSubtitleBridgeOperationResult Failure(
        LayoutSubtitleBridgeStatus status) =>
        new(status, snapshot: null);
}

internal interface ILayoutSubtitleBridgeRuntime
{
    LayoutSubtitleBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableLayoutSubtitleBridgeRuntime :
    ILayoutSubtitleBridgeRuntime
{
    internal static UnavailableLayoutSubtitleBridgeRuntime Instance { get; } =
        new();

    private UnavailableLayoutSubtitleBridgeRuntime()
    {
    }

    public LayoutSubtitleBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        cancellationToken.ThrowIfCancellationRequested();
        return LayoutSubtitleBridgeOperationResult.Failure(
            LayoutSubtitleBridgeStatus.Unavailable);
    }
}

internal sealed class LayoutSubtitleBridgeRuntime :
    ILayoutSubtitleBridgeRuntime
{
    private const string SubtitleChangeReason =
        "Apply a typed subtitle cue edit.";
    private const string TeropChangeReason =
        "Apply a typed terop cue edit.";

    private readonly object gate = new();
    private readonly Dictionary<
        EntityId<ProjectIdTag>,
        LayoutSubtitleBridgeSnapshot> snapshots = [];
    private readonly Func<DateTimeOffset> getCurrentTime;
    private readonly Func<Guid> createId;

    internal LayoutSubtitleBridgeRuntime()
        : this(
            static () => DateTimeOffset.UtcNow,
            static () => Guid.NewGuid())
    {
    }

    internal LayoutSubtitleBridgeRuntime(
        Func<DateTimeOffset> getCurrentTime,
        Func<Guid> createId)
    {
        ArgumentNullException.ThrowIfNull(getCurrentTime);
        ArgumentNullException.ThrowIfNull(createId);
        this.getCurrentTime = getCurrentTime;
        this.createId = createId;
    }

    internal void Register(LayoutSubtitleBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            snapshots[snapshot.ProjectId] = snapshot;
        }
    }

    internal LayoutSubtitleBridgeSnapshot GetRequired(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        lock (gate)
        {
            return snapshots.TryGetValue(
                projectId,
                out LayoutSubtitleBridgeSnapshot? snapshot)
                ? snapshot
                : throw new KeyNotFoundException(
                    "No layout workspace is registered for the project.");
        }
    }

    public LayoutSubtitleBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EntityId<ProjectIdTag> projectId = ParseEntity<ProjectIdTag>(
                request.Parameters.GetProperty("projectId"));
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!snapshots.TryGetValue(
                        projectId,
                        out LayoutSubtitleBridgeSnapshot? snapshot))
                {
                    return LayoutSubtitleBridgeOperationResult.Failure(
                        LayoutSubtitleBridgeStatus.NotFound);
                }

                if (request.Method == BridgeMethod.LayoutRead)
                {
                    JsonElement evaluation = request.Parameters
                        .GetProperty("evaluationTime");
                    LayoutSubtitleBridgeSnapshot projected =
                        evaluation.ValueKind == JsonValueKind.Null
                            ? snapshot
                            : snapshot.EvaluateAt(ReadTime(evaluation));
                    return LayoutSubtitleBridgeOperationResult.Available(
                        projected);
                }

                int expectedRevision = request.Parameters
                    .GetProperty("expectedRevision")
                    .GetInt32();
                if (snapshot.Revision != expectedRevision)
                {
                    return LayoutSubtitleBridgeOperationResult.Failure(
                        LayoutSubtitleBridgeStatus.Conflict);
                }

                JsonElement operation = request.Parameters
                    .GetProperty("operation");
                string kind = operation.GetProperty("kind").GetString()
                    ?? throw new FormatException(
                        "The layout operation kind is invalid.");
                LayoutSubtitleBridgeSnapshot revised = kind switch
                {
                    "setManualLayout" => SetManualLayout(
                        snapshot,
                        operation),
                    "setKeyframes" => SetKeyframes(snapshot, operation),
                    "editSubtitle" => EditSubtitle(
                        snapshot,
                        operation,
                        actorSid,
                        cancellationToken),
                    "editTerop" => EditTerop(
                        snapshot,
                        operation,
                        actorSid,
                        cancellationToken),
                    _ => throw new FormatException(
                        "The layout operation kind is invalid."),
                };
                var result = LayoutSubtitleBridgeOperationResult.Available(
                    revised);
                if (!LayoutSubtitleBridgeJson.CanWriteResponse(request, result))
                {
                    return LayoutSubtitleBridgeOperationResult.Failure(
                        LayoutSubtitleBridgeStatus.Unavailable);
                }

                snapshots[projectId] = revised;
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SubtitleServiceException exception)
        {
            return LayoutSubtitleBridgeOperationResult.Failure(
                exception.Reason switch
                {
                    SubtitleServiceFailureReason.NoChange =>
                        LayoutSubtitleBridgeStatus.NoChange,
                    SubtitleServiceFailureReason.SourceCandidateStale or
                    SubtitleServiceFailureReason.CompositionVersionConflict or
                    SubtitleServiceFailureReason.RevisionConflict =>
                        LayoutSubtitleBridgeStatus.Conflict,
                    _ => LayoutSubtitleBridgeStatus.Invalid,
                });
        }
        catch (EmphasisCueException exception)
        {
            return LayoutSubtitleBridgeOperationResult.Failure(
                exception.Reason switch
                {
                    EmphasisCueFailureReason.NoChange =>
                        LayoutSubtitleBridgeStatus.NoChange,
                    EmphasisCueFailureReason.SourceCandidateStale or
                    EmphasisCueFailureReason.CompositionVersionConflict or
                    EmphasisCueFailureReason.RevisionConflict =>
                        LayoutSubtitleBridgeStatus.Conflict,
                    _ => LayoutSubtitleBridgeStatus.Invalid,
                });
        }
        catch (LayoutSubtitleBridgeConflictException)
        {
            return LayoutSubtitleBridgeOperationResult.Failure(
                LayoutSubtitleBridgeStatus.Conflict);
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or
                InvalidOperationException or KeyNotFoundException or
                OverflowException or JsonException)
        {
            return LayoutSubtitleBridgeOperationResult.Failure(
                LayoutSubtitleBridgeStatus.Invalid);
        }
    }

    private LayoutSubtitleBridgeSnapshot SetManualLayout(
        LayoutSubtitleBridgeSnapshot snapshot,
        JsonElement operation)
    {
        ManualLayoutMode mode = ParseManualMode(
            GetRequiredString(operation, "mode"));
        ManualStaticCropDecision decision = ParseStaticCropDecision(
            GetRequiredString(operation, "staticCropDecision"));
        var sources = new List<ManualLayoutSource>();
        var destinations = new List<ManualDestinationGeometry>();
        foreach (JsonElement sourceValue in operation
            .GetProperty("sources")
            .EnumerateArray())
        {
            EntityId<AssetIdTag> sourceAssetId = ParseEntity<AssetIdTag>(
                sourceValue.GetProperty("sourceAssetId"));
            LayoutSubtitleSourceBinding sourceBinding = snapshot.Sources
                .Single(source => source.SourceAssetId == sourceAssetId);
            var source = new ManualLayoutSource(
                sourceBinding.SourceAssetId,
                sourceBinding.Origin,
                sourceBinding.DisplayGeometry,
                new ManualSourceCrop(ReadNormalizedRect(
                    sourceValue.GetProperty("crop"))),
                new ManualSubjectRegion(ReadNormalizedRect(
                    sourceValue.GetProperty("subject"))));
            sources.Add(source);
            destinations.Add(new ManualDestinationGeometry(
                ReadNormalizedRect(
                    sourceValue.GetProperty("destination"))));
        }

        TemplateReference? template = operation.GetProperty("templateId")
            .ValueKind == JsonValueKind.Null
            ? null
            : snapshot.Templates.Single(candidate =>
                candidate.Id == ParseEntity<TemplateIdTag>(
                    operation.GetProperty("templateId")));
        EntityId<LayoutSpecIdTag> derivedLayoutId =
            FreshEntity<LayoutSpecIdTag>();
        ManualLayoutRequest request = mode switch
        {
            ManualLayoutMode.SingleSubjectCrop =>
                ManualLayoutRequest.SingleSubjectCrop(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0]),
            ManualLayoutMode.TwoSpeakerStack =>
                ManualLayoutRequest.TwoSpeakerStack(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0],
                    sources[1],
                    destinations[1]),
            ManualLayoutMode.SpeakerMaterialStack =>
                ManualLayoutRequest.SpeakerMaterialStack(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0],
                    sources[1],
                    destinations[1]),
            ManualLayoutMode.MaterialWithSpeakerInset =>
                ManualLayoutRequest.MaterialWithSpeakerInset(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0],
                    sources[1],
                    destinations[1]),
            ManualLayoutMode.ImportantRegionZoom =>
                ManualLayoutRequest.ImportantRegionZoom(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0]),
            ManualLayoutMode.BlurredSourceBackground =>
                ManualLayoutRequest.BlurredSourceBackground(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0],
                    sources[1],
                    destinations[1]),
            ManualLayoutMode.FixedTemplatePlacement =>
                ManualLayoutRequest.FixedTemplatePlacement(
                    derivedLayoutId,
                    snapshot.OutputEnvelope,
                    decision,
                    sources[0],
                    destinations[0],
                    template!),
            _ => throw new FormatException(
                "The manual layout mode is invalid."),
        };
        ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);
        return snapshot.WithManualSelection(
            new LayoutSubtitleManualSelection(
                resolution,
                sources,
                destinations,
                template));
    }

    private static LayoutSubtitleBridgeSnapshot SetKeyframes(
        LayoutSubtitleBridgeSnapshot snapshot,
        JsonElement operation)
    {
        EntityId<AssetIdTag> sourceAssetId = ParseEntity<AssetIdTag>(
            operation.GetProperty("sourceAssetId"));
        LayoutSubtitleSourceBinding source = snapshot.Sources.Single(
            binding => binding.SourceAssetId == sourceAssetId);
        ManualDestinationSlot slot = ParseDestinationSlot(
            GetRequiredString(operation, "destinationSlot"));
        var segments = new List<LayoutKeyframeSegment>();
        foreach (JsonElement segmentValue in operation
            .GetProperty("segments")
            .EnumerateArray())
        {
            var keyframes = new List<LayoutKeyframe>();
            foreach (JsonElement keyframeValue in segmentValue
                .GetProperty("keyframes")
                .EnumerateArray())
            {
                keyframes.Add(new LayoutKeyframe(
                    ReadTime(keyframeValue.GetProperty("time")),
                    new LayoutKeyframeValue(
                        ReadNormalizedRect(
                            keyframeValue.GetProperty("sourceCrop")),
                        ReadNormalizedRect(
                            keyframeValue.GetProperty("destination"))),
                    ParseInterpolation(GetRequiredString(
                        keyframeValue,
                        "interpolation"))));
            }

            segments.Add(new LayoutKeyframeSegment(
                ReadTimeRange(segmentValue.GetProperty("range")),
                keyframes));
        }

        var track = new KeyframeTrack(
            sourceAssetId,
            slot,
            source.DisplayGeometry,
            snapshot.OutputPixelWidth,
            snapshot.OutputPixelHeight,
            segments);
        var binding = new LayoutSubtitleKeyframeBinding(
            track,
            ReadTime(operation.GetProperty("evaluationTime")));
        return snapshot.WithKeyframe(binding);
    }

    private LayoutSubtitleBridgeSnapshot EditSubtitle(
        LayoutSubtitleBridgeSnapshot snapshot,
        JsonElement operation,
        string actorSid,
        CancellationToken cancellationToken)
    {
        EntityId<CueIdTag> cueId = ParseEntity<CueIdTag>(
            operation.GetProperty("cueId"));
        int index = FindSubtitle(snapshot.Subtitles, cueId);
        LayoutSubtitleCueBinding binding = snapshot.Subtitles[index];
        SubtitleCueHistory history = binding.History;
        ValidateExpectedRevision(
            history.CurrentRevision.Id,
            history.CurrentRevision.ContentDigest,
            operation);
        ValidateCurrentComposition(
            snapshot,
            history.CurrentRevision.CompositionVersionId,
            history.CurrentRevision.CompositionVersionDigest);
        string reason = GetRequiredString(operation, "reason");
        CompositionVersion parent = snapshot.GetComposition(
            history.CurrentRevision.CompositionVersionId);
        DateTimeOffset createdAt = GetMutationTime(
            history.CurrentRevision.CreatedAt > parent.CreatedAt
                ? history.CurrentRevision.CreatedAt
                : parent.CreatedAt);
        CompositionVersion child = CreateChildComposition(
            parent,
            actorSid,
            createdAt,
            SubtitleChangeReason);
        EntityId<SubtitleCueRevisionIdTag> revisionId =
            FreshEntity<SubtitleCueRevisionIdTag>();
        OriginRef origin = OriginRef.FromEditorialRecord(
            FreshEntity<EditorialRecordIdTag>());
        JsonElement edit = operation.GetProperty("edit");
        string kind = GetRequiredString(edit, "kind");
        SubtitleCueHistory revised = kind switch
        {
            "text" => SubtitleService.SetText(
                history,
                child,
                history.SourceCandidate,
                revisionId,
                GetRequiredString(edit, "text"),
                origin,
                actorSid,
                createdAt,
                reason,
                cancellationToken),
            "timing" => SubtitleService.SetTiming(
                history,
                child,
                history.SourceCandidate,
                revisionId,
                ReadTimeRange(edit.GetProperty("outputRange")),
                origin,
                actorSid,
                createdAt,
                reason,
                cancellationToken),
            "style" => SubtitleService.SetStyle(
                history,
                child,
                history.SourceCandidate,
                revisionId,
                ParseSubtitleStyle(GetRequiredString(edit, "style")),
                origin,
                actorSid,
                createdAt,
                reason,
                cancellationToken),
            "placement" => SubtitleService.SetLayoutResolution(
                history,
                child,
                history.SourceCandidate,
                revisionId,
                binding.ResolveManualPlacement(
                    new SubtitlePlacementCandidate(
                        FreshEntity<SubtitlePlacementCandidateIdTag>(),
                        GetRequiredString(edit, "regionName"),
                        ReadSubtitleRect(edit.GetProperty("rect"))),
                    cancellationToken),
                origin,
                actorSid,
                createdAt,
                reason,
                cancellationToken),
            _ => throw new FormatException(
                "The subtitle edit kind is invalid."),
        };
        return snapshot.WithSubtitle(
            index,
            binding.WithHistory(revised),
            child);
    }

    private LayoutSubtitleBridgeSnapshot EditTerop(
        LayoutSubtitleBridgeSnapshot snapshot,
        JsonElement operation,
        string actorSid,
        CancellationToken cancellationToken)
    {
        EntityId<CueIdTag> cueId = ParseEntity<CueIdTag>(
            operation.GetProperty("cueId"));
        int index = FindTerop(snapshot.Terops, cueId);
        LayoutSubtitleTeropBinding binding = snapshot.Terops[index];
        EmphasisCueHistory history = binding.History;
        ValidateExpectedRevision(
            history.CurrentRevision.Id,
            history.CurrentRevision.ContentDigest,
            operation);
        ValidateCurrentComposition(
            snapshot,
            history.CurrentRevision.CompositionVersionId,
            history.CurrentRevision.CompositionVersionDigest);
        string reason = GetRequiredString(operation, "reason");
        CompositionVersion parent = snapshot.GetComposition(
            history.CurrentRevision.CompositionVersionId);
        DateTimeOffset createdAt = GetMutationTime(
            history.CurrentRevision.CreatedAt > parent.CreatedAt
                ? history.CurrentRevision.CreatedAt
                : parent.CreatedAt);
        CompositionVersion child = CreateChildComposition(
            parent,
            actorSid,
            createdAt,
            TeropChangeReason);
        OriginRef origin = OriginRef.FromEditorialRecord(
            FreshEntity<EditorialRecordIdTag>());
        JsonElement editValue = operation.GetProperty("edit");
        string kind = GetRequiredString(editValue, "kind");
        EmphasisCueEdit edit = kind switch
        {
            "text" => EmphasisCueEdit.ChangeText(
                GetRequiredString(editValue, "text"),
                origin),
            "timing" => EmphasisCueEdit.ChangeTiming(
                ReadTimeRange(editValue.GetProperty("outputRange")),
                origin),
            "style" => EmphasisCueEdit.ChangeStyle(
                ParseSubtitleStyle(GetRequiredString(editValue, "style")),
                origin),
            "region" => EmphasisCueEdit.ChangeRegion(
                GetRequiredString(editValue, "regionName"),
                origin),
            _ => throw new FormatException(
                "The terop edit kind is invalid."),
        };
        EmphasisCueHistory revised = EmphasisCueService.Revise(
            history,
            child,
            binding.CurrentTopicMetadata,
            binding.PlacementContext,
            FreshEntity<EmphasisCueRevisionIdTag>(),
            edit,
            actorSid,
            createdAt,
            reason,
            cancellationToken);
        return snapshot.WithTerop(
            index,
            binding.WithHistory(revised),
            child);
    }

    private DateTimeOffset GetMutationTime(DateTimeOffset minimum)
    {
        DateTimeOffset value = getCurrentTime();
        return value < minimum ? minimum : value;
    }

    private CompositionVersion CreateChildComposition(
        CompositionVersion parent,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        new(
            FreshEntity<CompositionVersionIdTag>(),
            parent.ProjectId,
            parent.Id,
            actorSid,
            createdAt < parent.CreatedAt ? parent.CreatedAt : createdAt,
            reason,
            parent.ContentDigest,
            parent.Elements);

    private EntityId<TTag> FreshEntity<TTag>() =>
        EntityId<TTag>.From(createId());

    private static int FindSubtitle(
        IReadOnlyList<LayoutSubtitleCueBinding> subtitles,
        EntityId<CueIdTag> cueId)
    {
        for (int index = 0; index < subtitles.Count; index++)
        {
            if (subtitles[index].History.CueId == cueId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException("The subtitle cue does not exist.");
    }

    private static int FindTerop(
        IReadOnlyList<LayoutSubtitleTeropBinding> terops,
        EntityId<CueIdTag> cueId)
    {
        for (int index = 0; index < terops.Count; index++)
        {
            if (terops[index].History.CueId == cueId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException("The terop cue does not exist.");
    }

    private static void ValidateExpectedRevision<TTag>(
        EntityId<TTag> actualRevisionId,
        ContentDigest actualDigest,
        JsonElement operation)
    {
        EntityId<TTag> expectedRevisionId = ParseEntity<TTag>(
            operation.GetProperty("expectedRevisionId"));
        ContentDigest expectedDigest = ContentDigest.Parse(
            GetRequiredString(operation, "expectedRevisionDigest"));
        if (actualRevisionId != expectedRevisionId
            || actualDigest != expectedDigest)
        {
            throw new LayoutSubtitleBridgeConflictException();
        }
    }

    private static void ValidateCurrentComposition(
        LayoutSubtitleBridgeSnapshot snapshot,
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest digest)
    {
        if (snapshot.CompositionVersion.Id != versionId
            || snapshot.CompositionVersion.ContentDigest != digest)
        {
            throw new LayoutSubtitleBridgeConflictException();
        }
    }

    internal static EntityId<TTag> ParseEntity<TTag>(JsonElement value) =>
        EntityId<TTag>.Parse(value.GetString()
            ?? throw new FormatException("An entity ID is invalid."));

    private static string GetRequiredString(
        JsonElement value,
        string propertyName) =>
        value.GetProperty(propertyName).GetString()
            ?? throw new FormatException("A required string is invalid.");

    private static RationalNumber ReadRational(JsonElement value) =>
        new(
            ReadInt64(value.GetProperty("numerator")),
            ReadInt64(value.GetProperty("denominator")));

    private static RationalTime ReadTime(JsonElement value) =>
        new(
            ReadInt64(value.GetProperty("value")),
            ReadInt64(value.GetProperty("rate")));

    private static TimeRange ReadTimeRange(JsonElement value) =>
        new(
            ReadTime(value.GetProperty("start")),
            ReadTime(value.GetProperty("duration")));

    private static long ReadInt64(JsonElement value) =>
        long.Parse(
            value.GetString()
                ?? throw new FormatException("A rational integer is invalid."),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);

    private static NormalizedRect ReadNormalizedRect(JsonElement value) =>
        new(
            ReadRational(value.GetProperty("x")),
            ReadRational(value.GetProperty("y")),
            ReadRational(value.GetProperty("width")),
            ReadRational(value.GetProperty("height")));

    private static SubtitleLayoutRect ReadSubtitleRect(JsonElement value) =>
        new(
            ReadRational(value.GetProperty("x")),
            ReadRational(value.GetProperty("y")),
            ReadRational(value.GetProperty("width")),
            ReadRational(value.GetProperty("height")));

    private static ManualLayoutMode ParseManualMode(string value) =>
        value switch
        {
            "singleSubjectCrop" => ManualLayoutMode.SingleSubjectCrop,
            "twoSpeakerStack" => ManualLayoutMode.TwoSpeakerStack,
            "speakerMaterialStack" => ManualLayoutMode.SpeakerMaterialStack,
            "materialWithSpeakerInset" =>
                ManualLayoutMode.MaterialWithSpeakerInset,
            "importantRegionZoom" => ManualLayoutMode.ImportantRegionZoom,
            "blurredSourceBackground" =>
                ManualLayoutMode.BlurredSourceBackground,
            "fixedTemplatePlacement" =>
                ManualLayoutMode.FixedTemplatePlacement,
            _ => throw new FormatException("The manual layout mode is invalid."),
        };

    private static ManualStaticCropDecision ParseStaticCropDecision(
        string value) =>
        value switch
        {
            "singleStaticCropSufficient" =>
                ManualStaticCropDecision.SingleStaticCropSufficient,
            "manualKeyframesRequired" =>
                ManualStaticCropDecision.ManualKeyframesRequired,
            _ => throw new FormatException(
                "The static crop decision is invalid."),
        };

    private static LayoutKeyframeInterpolation ParseInterpolation(
        string value) =>
        value switch
        {
            "hold" => LayoutKeyframeInterpolation.Hold,
            "linear" => LayoutKeyframeInterpolation.Linear,
            _ => throw new FormatException(
                "The keyframe interpolation is invalid."),
        };

    private static ManualDestinationSlot ParseDestinationSlot(string value) =>
        value switch
        {
            "singleSubject" => ManualDestinationSlot.SingleSubject,
            "topSpeaker" => ManualDestinationSlot.TopSpeaker,
            "bottomSpeaker" => ManualDestinationSlot.BottomSpeaker,
            "stackedSpeaker" => ManualDestinationSlot.StackedSpeaker,
            "stackedMaterial" => ManualDestinationSlot.StackedMaterial,
            "mainMaterial" => ManualDestinationSlot.MainMaterial,
            "speakerInset" => ManualDestinationSlot.SpeakerInset,
            "zoomedImportantRegion" =>
                ManualDestinationSlot.ZoomedImportantRegion,
            "blurredBackground" => ManualDestinationSlot.BlurredBackground,
            "foregroundSubject" => ManualDestinationSlot.ForegroundSubject,
            "templateVideo" => ManualDestinationSlot.TemplateVideo,
            _ => throw new FormatException(
                "The manual destination slot is invalid."),
        };

    private static SubtitleCueStyle ParseSubtitleStyle(string value) =>
        value switch
        {
            "normal" => SubtitleCueStyle.Normal,
            "emphasis" => SubtitleCueStyle.Emphasis,
            "caution" => SubtitleCueStyle.Caution,
            "quote" => SubtitleCueStyle.Quote,
            _ => throw new FormatException("The cue style is invalid."),
        };
}

internal interface ILayoutSubtitleBridgeAuthorization
{
    string DemandRead();

    string DemandMutation();
}

internal sealed class LayoutSubtitleBridgeAuthorization :
    ILayoutSubtitleBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal LayoutSubtitleBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(
            authorizationService,
            (actorProvider ?? throw new ArgumentNullException(
                nameof(actorProvider))).GetCurrentSid)
    {
    }

    internal LayoutSubtitleBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public string DemandRead()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.ViewProject);
        return actorSid;
    }

    public string DemandMutation()
    {
        string actorSid = getCurrentSid();
        authorizationService.DemandAs(
            actorSid,
            AuthorizationRole.Editor,
            AuthorizationOperation.CreateCompositionVersion);
        return actorSid;
    }
}

internal sealed class LayoutSubtitleBridgeRequestHandler
{
    private readonly ILayoutSubtitleBridgeRuntime runtime;
    private readonly ILayoutSubtitleBridgeAuthorization authorization;

    internal LayoutSubtitleBridgeRequestHandler(
        ILayoutSubtitleBridgeRuntime runtime,
        ILayoutSubtitleBridgeAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(authorization);
        this.runtime = runtime;
        this.authorization = authorization;
    }

    internal Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        LayoutSubtitleBridgeOperationResult result;
        try
        {
            string actorSid = request.Method == BridgeMethod.LayoutRead
                ? authorization.DemandRead()
                : authorization.DemandMutation();
            result = runtime.Execute(request, actorSid, cancellationToken)
                ?? LayoutSubtitleBridgeOperationResult.Failure(
                    LayoutSubtitleBridgeStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthorizationException)
        {
            result = LayoutSubtitleBridgeOperationResult.Failure(
                LayoutSubtitleBridgeStatus.Forbidden);
        }
        catch (Exception)
        {
            result = LayoutSubtitleBridgeOperationResult.Failure(
                LayoutSubtitleBridgeStatus.Unavailable);
        }

        return Task.FromResult(
            LayoutSubtitleBridgeJson.WriteResponse(request, result));
    }
}

internal static class LayoutSubtitleBridgeProtocol
{
    private const int MaxCollectionItems = 256;
    private const int MaxTextUtf8Bytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool HasValidParameters(
        BridgeMethod method,
        JsonElement parameters)
    {
        if (method == BridgeMethod.LayoutRead)
        {
            return HasExactProperties(
                    parameters,
                    ["projectId", "evaluationTime"])
                && IsCanonicalId(parameters.GetProperty("projectId"))
                && IsNullOrTime(parameters.GetProperty("evaluationTime"));
        }

        return method == BridgeMethod.LayoutApply
            && HasExactProperties(
                parameters,
                ["projectId", "expectedRevision", "operation"])
            && IsCanonicalId(parameters.GetProperty("projectId"))
            && IsNonnegativeInt32(
                parameters.GetProperty("expectedRevision"))
            && IsOperation(parameters.GetProperty("operation"));
    }

    private static bool IsOperation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("kind", out JsonElement kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return kind.GetString() switch
        {
            "setManualLayout" => IsManualLayoutOperation(value),
            "setKeyframes" => IsKeyframeOperation(value),
            "editSubtitle" => IsCueEditOperation(
                value,
                subtitle: true),
            "editTerop" => IsCueEditOperation(
                value,
                subtitle: false),
            _ => false,
        };
    }

    private static bool IsManualLayoutOperation(JsonElement value)
    {
        if (!HasExactProperties(
                value,
                [
                    "kind",
                    "mode",
                    "staticCropDecision",
                    "sources",
                    "templateId",
                ])
            || !IsManualMode(value.GetProperty("mode"))
            || !IsStaticCropDecision(
                value.GetProperty("staticCropDecision"))
            || !IsNullOrCanonicalId(value.GetProperty("templateId")))
        {
            return false;
        }

        JsonElement sources = value.GetProperty("sources");
        if (sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() is < 1 or > 2)
        {
            return false;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (!HasExactProperties(
                    source,
                    ["sourceAssetId", "crop", "subject", "destination"])
                || !IsCanonicalId(source.GetProperty("sourceAssetId"))
                || !IsNormalizedRect(source.GetProperty("crop"))
                || !IsNormalizedRect(source.GetProperty("subject"))
                || !IsNormalizedRect(source.GetProperty("destination")))
            {
                return false;
            }
        }

        string? mode = value.GetProperty("mode").GetString();
        int expectedCount = mode is
            "twoSpeakerStack" or
            "speakerMaterialStack" or
            "materialWithSpeakerInset" or
            "blurredSourceBackground"
            ? 2
            : 1;
        bool templateRequired = mode == "fixedTemplatePlacement";
        return sources.GetArrayLength() == expectedCount
            && templateRequired
                == (value.GetProperty("templateId").ValueKind
                    != JsonValueKind.Null);
    }

    private static bool IsKeyframeOperation(JsonElement value)
    {
        if (!HasExactProperties(
                value,
                [
                    "kind",
                    "sourceAssetId",
                    "destinationSlot",
                    "segments",
                    "evaluationTime",
                ])
            || !IsCanonicalId(value.GetProperty("sourceAssetId"))
            || !IsDestinationSlot(value.GetProperty("destinationSlot"))
            || !IsTime(value.GetProperty("evaluationTime")))
        {
            return false;
        }

        JsonElement segments = value.GetProperty("segments");
        if (segments.ValueKind != JsonValueKind.Array
            || segments.GetArrayLength() is < 1 or > MaxCollectionItems)
        {
            return false;
        }

        int keyframeCount = 0;
        foreach (JsonElement segment in segments.EnumerateArray())
        {
            if (!HasExactProperties(segment, ["range", "keyframes"])
                || !IsTimeRange(segment.GetProperty("range")))
            {
                return false;
            }

            JsonElement keyframes = segment.GetProperty("keyframes");
            if (keyframes.ValueKind != JsonValueKind.Array
                || keyframes.GetArrayLength() is < 1 or > MaxCollectionItems)
            {
                return false;
            }

            keyframeCount += keyframes.GetArrayLength();
            if (keyframeCount > MaxCollectionItems)
            {
                return false;
            }

            foreach (JsonElement keyframe in keyframes.EnumerateArray())
            {
                if (!HasExactProperties(
                        keyframe,
                        [
                            "time",
                            "sourceCrop",
                            "destination",
                            "interpolation",
                        ])
                    || !IsTime(keyframe.GetProperty("time"))
                    || !IsNormalizedRect(
                        keyframe.GetProperty("sourceCrop"))
                    || !IsNormalizedRect(
                        keyframe.GetProperty("destination"))
                    || !IsInterpolation(
                        keyframe.GetProperty("interpolation")))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCueEditOperation(
        JsonElement value,
        bool subtitle)
    {
        return HasExactProperties(
                value,
                [
                    "kind",
                    "cueId",
                    "expectedRevisionId",
                    "expectedRevisionDigest",
                    "edit",
                    "reason",
                ])
            && IsCanonicalId(value.GetProperty("cueId"))
            && IsCanonicalId(value.GetProperty("expectedRevisionId"))
            && IsCanonicalDigest(
                value.GetProperty("expectedRevisionDigest"))
            && IsRequiredText(value.GetProperty("reason"))
            && IsCueEdit(value.GetProperty("edit"), subtitle);
    }

    private static bool IsCueEdit(JsonElement value, bool subtitle)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("kind", out JsonElement kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return kind.GetString() switch
        {
            "text" =>
                HasExactProperties(value, ["kind", "text"])
                && IsRequiredText(value.GetProperty("text")),
            "timing" =>
                HasExactProperties(value, ["kind", "outputRange"])
                && IsTimeRange(value.GetProperty("outputRange")),
            "style" =>
                HasExactProperties(value, ["kind", "style"])
                && IsCueStyle(value.GetProperty("style")),
            "placement" when subtitle =>
                HasExactProperties(
                    value,
                    ["kind", "regionName", "rect"])
                && IsRequiredText(value.GetProperty("regionName"))
                && IsSubtitleRect(value.GetProperty("rect")),
            "region" when !subtitle =>
                HasExactProperties(value, ["kind", "regionName"])
                && IsRequiredText(value.GetProperty("regionName")),
            _ => false,
        };
    }

    private static bool IsNormalizedRect(JsonElement value)
    {
        if (!IsRect(value))
        {
            return false;
        }

        try
        {
            _ = new NormalizedRect(
                ReadRational(value.GetProperty("x")),
                ReadRational(value.GetProperty("y")),
                ReadRational(value.GetProperty("width")),
                ReadRational(value.GetProperty("height")));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSubtitleRect(JsonElement value)
    {
        if (!IsRect(value))
        {
            return false;
        }

        try
        {
            _ = new SubtitleLayoutRect(
                ReadRational(value.GetProperty("x")),
                ReadRational(value.GetProperty("y")),
                ReadRational(value.GetProperty("width")),
                ReadRational(value.GetProperty("height")));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsRect(JsonElement value) =>
        HasExactProperties(value, ["x", "y", "width", "height"])
        && IsRational(value.GetProperty("x"), positive: false)
        && IsRational(value.GetProperty("y"), positive: false)
        && IsRational(value.GetProperty("width"), positive: true)
        && IsRational(value.GetProperty("height"), positive: true);

    private static bool IsTimeRange(JsonElement value) =>
        HasExactProperties(value, ["start", "duration"])
        && IsTime(value.GetProperty("start"))
        && IsTime(value.GetProperty("duration"))
        && ReadTime(value.GetProperty("duration")) > default(RationalTime);

    private static bool IsNullOrTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || IsTime(value);

    private static bool IsTime(JsonElement value)
    {
        if (!HasExactProperties(value, ["value", "rate"])
            || !TryReadCanonicalInt64(
                value.GetProperty("value"),
                positive: false,
                out long numerator)
            || !TryReadCanonicalInt64(
                value.GetProperty("rate"),
                positive: true,
                out long rate))
        {
            return false;
        }

        var parsed = new RationalTime(numerator, rate);
        return parsed.Value == numerator && parsed.Rate == rate;
    }

    private static RationalTime ReadTime(JsonElement value) =>
        new(
            long.Parse(
                value.GetProperty("value").GetString()!,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            long.Parse(
                value.GetProperty("rate").GetString()!,
                NumberStyles.None,
                CultureInfo.InvariantCulture));

    private static bool IsRational(JsonElement value, bool positive)
    {
        if (!HasExactProperties(value, ["numerator", "denominator"])
            || !TryReadCanonicalInt64(
                value.GetProperty("numerator"),
                positive,
                out long numerator)
            || !TryReadCanonicalInt64(
                value.GetProperty("denominator"),
                positive: true,
                out long denominator))
        {
            return false;
        }

        var parsed = new RationalNumber(numerator, denominator);
        return parsed.Numerator == numerator
            && parsed.Denominator == denominator;
    }

    private static RationalNumber ReadRational(JsonElement value) =>
        new(
            long.Parse(
                value.GetProperty("numerator").GetString()!,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            long.Parse(
                value.GetProperty("denominator").GetString()!,
                NumberStyles.None,
                CultureInfo.InvariantCulture));

    private static bool TryReadCanonicalInt64(
        JsonElement value,
        bool positive,
        out long parsed)
    {
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not string text
            || !long.TryParse(
                text,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out parsed)
            || positive && parsed <= 0
            || !positive && text.StartsWith("+", StringComparison.Ordinal)
            || !string.Equals(
                text,
                parsed.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            parsed = default;
            return false;
        }

        return true;
    }

    private static bool IsCanonicalId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is string text
        && Guid.TryParseExact(text, "D", out Guid parsed)
        && parsed != Guid.Empty
        && string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal);

    private static bool IsNullOrCanonicalId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || IsCanonicalId(value);

    private static bool IsCanonicalDigest(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && ContentDigest.TryParse(value.GetString(), out ContentDigest? digest)
        && string.Equals(
            value.GetString(),
            digest.Hex,
            StringComparison.Ordinal);

    private static bool IsNonnegativeInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out decimal parsed)
        && parsed == decimal.Truncate(parsed)
        && parsed is >= 0 and <= int.MaxValue;

    private static bool IsRequiredText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not string text
            || string.IsNullOrWhiteSpace(text)
            || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(text) <= MaxTextUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsManualMode(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is
            "singleSubjectCrop" or
            "twoSpeakerStack" or
            "speakerMaterialStack" or
            "materialWithSpeakerInset" or
            "importantRegionZoom" or
            "blurredSourceBackground" or
            "fixedTemplatePlacement";

    private static bool IsStaticCropDecision(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is
            "singleStaticCropSufficient" or
            "manualKeyframesRequired";

    private static bool IsInterpolation(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is "hold" or "linear";

    private static bool IsDestinationSlot(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is
            "singleSubject" or
            "topSpeaker" or
            "bottomSpeaker" or
            "stackedSpeaker" or
            "stackedMaterial" or
            "mainMaterial" or
            "speakerInset" or
            "zoomedImportantRegion" or
            "blurredBackground" or
            "foregroundSubject" or
            "templateVideo";

    private static bool IsCueStyle(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is
            "normal" or "emphasis" or "caution" or "quote";

    private static bool HasExactProperties(
        JsonElement value,
        IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal)
                || !found.Add(property.Name))
            {
                return false;
            }
        }

        return found.Count == expected.Count;
    }
}

internal static class LayoutSubtitleBridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteResponse(
        BridgeRequest request,
        LayoutSubtitleBridgeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == LayoutSubtitleBridgeStatus.Available)
        {
            return WriteResponseCore(
                request,
                LayoutSubtitleBridgeOperationResult.Failure(
                    LayoutSubtitleBridgeStatus.Unavailable));
        }
    }

    internal static bool CanWriteResponse(
        BridgeRequest request,
        LayoutSubtitleBridgeOperationResult result)
    {
        try
        {
            _ = WriteResponseCore(request, result);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string WriteResponseCore(
        BridgeRequest request,
        LayoutSubtitleBridgeOperationResult result)
    {
        var buffer = new PlayerBridgeJson.BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString(
            "method",
            BridgeProtocol.GetMethodName(request.Method));
        writer.WriteStartObject("result");
        writer.WriteString("status", GetStatus(result.Status));
        writer.WritePropertyName("snapshot");
        if (result.Snapshot is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteSnapshot(writer, result.Snapshot);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        LayoutSubtitleBridgeSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("projectId", snapshot.ProjectId.ToString());
        writer.WriteNumber("revision", snapshot.Revision);
        writer.WriteString(
            "compositionVersionId",
            snapshot.CompositionVersion.Id.ToString());
        writer.WriteString(
            "compositionVersionDigest",
            snapshot.CompositionVersion.ContentDigest.Hex);
        writer.WriteString("persistence", "notPersisted");
        writer.WriteStartObject("output");
        writer.WriteNumber("width", snapshot.OutputPixelWidth);
        writer.WriteNumber("height", snapshot.OutputPixelHeight);
        writer.WriteNumber(
            "aspectWidth",
            snapshot.OutputEnvelope.OutputAspectWidth);
        writer.WriteNumber(
            "aspectHeight",
            snapshot.OutputEnvelope.OutputAspectHeight);
        writer.WriteEndObject();

        LayoutSpec activeLayout = snapshot.ManualSelection?.Resolution.Plan
            ?.DerivedLayoutSpec ?? snapshot.OutputEnvelope;
        ResolvedLayout resolved = LayoutResolver.Resolve(
            activeLayout,
            snapshot.OutputPixelWidth,
            snapshot.OutputPixelHeight);
        WriteResolvedLayout(writer, activeLayout, resolved);
        WriteSourceCatalog(writer, snapshot.Sources);
        WriteTemplates(writer, snapshot.Templates);
        WriteManualSelection(writer, snapshot);
        WriteKeyframes(writer, snapshot.Keyframes);
        WriteSubtitles(writer, snapshot);
        WriteSubtitleDisplays(writer, snapshot);
        WriteTerops(writer, snapshot.Terops);
        WriteVariantPair(writer, snapshot.VariantPair);
        writer.WriteEndObject();
    }

    private static void WriteResolvedLayout(
        Utf8JsonWriter writer,
        LayoutSpec layout,
        ResolvedLayout resolved)
    {
        writer.WriteStartObject("layout");
        writer.WriteString("layoutSpecId", layout.Id.ToString());
        writer.WriteStartArray("regions");
        for (int index = 0; index < layout.Regions.Count; index++)
        {
            LayoutRegion source = layout.Regions[index];
            ResolvedLayoutRegion pixels = resolved.Regions[index];
            writer.WriteStartObject();
            writer.WriteString("name", source.Name);
            writer.WriteString("role", GetRegionRole(source.Role));
            writer.WritePropertyName("rect");
            WriteNormalizedRect(writer, source.Rect);
            writer.WritePropertyName("pixels");
            WritePixelRect(writer, pixels.Rect);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("safeZones");
        for (int index = 0; index < layout.SafeZones.Count; index++)
        {
            SafeZone source = layout.SafeZones[index];
            ResolvedSafeZone pixels = resolved.SafeZones[index];
            writer.WriteStartObject();
            writer.WriteString("name", source.Name);
            writer.WritePropertyName("rect");
            WriteNormalizedRect(writer, source.Rect);
            writer.WritePropertyName("pixels");
            WritePixelRect(writer, pixels.Rect);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSourceCatalog(
        Utf8JsonWriter writer,
        IReadOnlyList<LayoutSubtitleSourceBinding> sources)
    {
        writer.WriteStartArray("sources");
        foreach (LayoutSubtitleSourceBinding source in sources)
        {
            writer.WriteStartObject();
            writer.WriteString("sourceAssetId", source.SourceAssetId.ToString());
            writer.WriteNumber("displayWidth", source.DisplayGeometry.Width);
            writer.WriteNumber("displayHeight", source.DisplayGeometry.Height);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTemplates(
        Utf8JsonWriter writer,
        IReadOnlyList<TemplateReference> templates)
    {
        writer.WriteStartArray("templates");
        foreach (TemplateReference template in templates)
        {
            writer.WriteStartObject();
            writer.WriteString("templateId", template.Id.ToString());
            writer.WriteString("name", template.Name);
            writer.WriteString("versionDigest", template.VersionDigest.Hex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteManualSelection(
        Utf8JsonWriter writer,
        LayoutSubtitleBridgeSnapshot snapshot)
    {
        writer.WritePropertyName("manualSelection");
        LayoutSubtitleManualSelection? selection = snapshot.ManualSelection;
        if (selection is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("mode", GetManualMode(selection.Resolution.Mode));
        writer.WriteString(
            "status",
            selection.Resolution.Status == ManualLayoutResolutionStatus.Resolved
                ? "resolved"
                : "manualKeyframesRequired");
        writer.WriteStartArray("placements");
        for (int index = 0; index < selection.Sources.Count; index++)
        {
            ManualLayoutSource source = selection.Sources[index];
            ManualDestinationGeometry destination =
                selection.Destinations[index];
            ManualSourcePlacement? planPlacement =
                selection.Resolution.Plan?.SourcePlacements[index];
            writer.WriteStartObject();
            writer.WriteString("sourceAssetId", source.SourceAssetId.ToString());
            writer.WriteNumber("sourceWidth", source.DisplayGeometry.Width);
            writer.WriteNumber("sourceHeight", source.DisplayGeometry.Height);
            writer.WritePropertyName("sourceCrop");
            WriteNormalizedAndPixels(
                writer,
                source.Crop.Rect,
                source.DisplayGeometry.Width,
                source.DisplayGeometry.Height);
            writer.WritePropertyName("subject");
            WriteNormalizedAndPixels(
                writer,
                source.SubjectRegion.Rect,
                source.DisplayGeometry.Width,
                source.DisplayGeometry.Height);
            writer.WritePropertyName("destination");
            WriteNormalizedAndPixels(
                writer,
                destination.Rect,
                snapshot.OutputPixelWidth,
                snapshot.OutputPixelHeight);
            writer.WriteString(
                "destinationSlot",
                planPlacement is null
                    ? GetDestinationSlot(
                        GetDestinationSlot(selection.Resolution.Mode, index))
                    : GetDestinationSlot(
                        planPlacement.Destination.Slot));
            writer.WriteString(
                "semanticRole",
                planPlacement is null
                    ? GetSemanticRole(
                        GetSemanticRole(selection.Resolution.Mode, index))
                    : GetSemanticRole(planPlacement.SemanticRole));
            writer.WriteString(
                "treatment",
                planPlacement is null
                    ? GetTreatment(selection.Resolution.Mode, index)
                    : GetTreatment(planPlacement.Treatment));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("template");
        if (selection.Template is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString(
                "templateId",
                selection.Template.Id.ToString());
            writer.WriteString("name", selection.Template.Name);
            writer.WriteString(
                "versionDigest",
                selection.Template.VersionDigest.Hex);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteKeyframes(
        Utf8JsonWriter writer,
        IReadOnlyList<LayoutSubtitleKeyframeBinding> bindings)
    {
        writer.WriteStartArray("keyframeTracks");
        foreach (LayoutSubtitleKeyframeBinding binding in bindings)
        {
            KeyframeTrack track = binding.Track;
            writer.WriteStartObject();
            writer.WriteString("sourceAssetId", track.SourceAssetId.ToString());
            writer.WriteString(
                "destinationSlot",
                GetDestinationSlot(track.DestinationSlot));
            writer.WriteNumber("sourceWidth", track.SourceDisplayGeometry.Width);
            writer.WriteNumber("sourceHeight", track.SourceDisplayGeometry.Height);
            writer.WriteNumber("outputWidth", track.OutputPixelWidth);
            writer.WriteNumber("outputHeight", track.OutputPixelHeight);
            writer.WritePropertyName("range");
            WriteTimeRange(writer, track.Range);
            writer.WriteStartArray("segments");
            foreach (LayoutKeyframeSegment segment in track.Segments)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("range");
                WriteTimeRange(writer, segment.Range);
                writer.WriteStartArray("keyframes");
                foreach (LayoutKeyframe keyframe in segment.Keyframes)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("time");
                    WriteTime(writer, keyframe.Time);
                    writer.WritePropertyName("sourceCrop");
                    WriteNormalizedRect(writer, keyframe.Value.SourceCrop);
                    writer.WritePropertyName("destination");
                    WriteNormalizedRect(writer, keyframe.Value.Destination);
                    writer.WriteString(
                        "interpolation",
                        keyframe.Interpolation ==
                            LayoutKeyframeInterpolation.Hold
                            ? "hold"
                            : "linear");
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("evaluation");
            writer.WritePropertyName("requestedTime");
            WriteTime(writer, binding.Evaluation.RequestedTime);
            writer.WritePropertyName("sourceCrop");
            WriteNormalizedRect(
                writer,
                binding.Evaluation.Value.SourceCrop);
            writer.WritePropertyName("destination");
            WriteNormalizedRect(
                writer,
                binding.Evaluation.Value.Destination);
            writer.WritePropertyName("sourcePixels");
            WritePixelRect(writer, binding.Evaluation.SourceCropPixels);
            writer.WritePropertyName("destinationPixels");
            WritePixelRect(writer, binding.Evaluation.DestinationPixels);
            writer.WriteNumber(
                "segmentIndex",
                binding.Evaluation.SegmentIndex);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSubtitles(
        Utf8JsonWriter writer,
        LayoutSubtitleBridgeSnapshot snapshot)
    {
        writer.WriteStartArray("subtitles");
        foreach (LayoutSubtitleCueBinding binding in snapshot.Subtitles)
        {
            SubtitleCueHistory history = binding.History;
            SubtitleCueRevision revision = history.CurrentRevision;
            SubtitleCueSnapshot cue = revision.Snapshot;
            writer.WriteStartObject();
            writer.WriteString("cueId", history.CueId.ToString());
            writer.WriteString("revisionId", revision.Id.ToString());
            writer.WriteString("revisionDigest", revision.ContentDigest.Hex);
            writer.WriteString(
                "compositionVersionId",
                revision.CompositionVersionId.ToString());
            writer.WriteString("text", cue.Text);
            writer.WriteString("style", GetCueStyle(cue.Style));
            writer.WritePropertyName("outputRange");
            WriteTimeRange(writer, cue.Cue.Range);
            writer.WritePropertyName("sourceRange");
            WriteTimeRange(writer, history.SourceCandidate.SourceRange);
            writer.WriteNumber("revisionCount", history.Revisions.Count);
            WriteSubtitleResolution(
                writer,
                cue.LayoutResolution,
                snapshot.OutputPixelWidth,
                snapshot.OutputPixelHeight);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSubtitleResolution(
        Utf8JsonWriter writer,
        SubtitleLayoutResolution resolution,
        int outputWidth,
        int outputHeight)
    {
        writer.WriteStartObject("layoutResolution");
        writer.WriteString(
            "status",
            resolution.Status == SubtitleLayoutResolutionStatus.Resolved
                ? "resolved"
                : "manualAdjustmentRequired");
        writer.WriteString("layoutSpecId", resolution.LayoutSpecId.ToString());
        writer.WriteString(
            "profileVersionDigest",
            resolution.ProfileVersionDigest.Hex);
        writer.WriteStartArray("globalHardConstraints");
        foreach (SubtitleHardConstraintKind constraint in
            resolution.GlobalHardConstraints)
        {
            writer.WriteStringValue(GetHardConstraint(constraint));
        }

        writer.WriteEndArray();
        writer.WriteStartArray("evaluations");
        foreach (SubtitlePlacementEvaluation evaluation in
            resolution.Evaluations)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "candidateId",
                evaluation.Placement.Id.ToString());
            writer.WriteString(
                "regionName",
                evaluation.Placement.RegionName);
            writer.WritePropertyName("rect");
            WriteSubtitleRect(writer, evaluation.Placement.Rect);
            writer.WritePropertyName("pixels");
            WriteOptionalSubtitlePixels(
                writer,
                evaluation.Placement.Rect,
                outputWidth,
                outputHeight);
            writer.WriteStartArray("hardConstraints");
            foreach (SubtitleHardConstraintKind constraint in
                evaluation.HardConstraints)
            {
                writer.WriteStringValue(GetHardConstraint(constraint));
            }

            writer.WriteEndArray();
            writer.WriteStartArray("softConstraints");
            foreach (SubtitleSoftConstraintKind constraint in
                evaluation.SoftConstraints)
            {
                writer.WriteStringValue(GetSoftConstraint(constraint));
            }

            writer.WriteEndArray();
            writer.WriteString(
                "softCost",
                evaluation.SoftCost.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("selectedCandidateId");
        if (resolution.Plan is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(resolution.Plan.Placement.Id.ToString());
        }

        writer.WriteEndObject();
    }

    private static void WriteSubtitleDisplays(
        Utf8JsonWriter writer,
        LayoutSubtitleBridgeSnapshot snapshot)
    {
        writer.WriteStartArray("subtitleDisplays");
        foreach (LayoutSubtitleDisplayBinding binding in
            snapshot.SubtitleDisplays)
        {
            SubtitleDisplayProfile profile = binding.Profile;
            SubtitleDisplayResult result = binding.Apply(snapshot.Subtitles);
            writer.WriteStartObject();
            writer.WriteString(
                "profileVersionDigest",
                result.ProfileVersionDigest.Hex);
            writer.WriteString(
                "cueMode",
                profile.CueMode == SubtitleCueDisplayMode.Full
                    ? "full"
                    : "selected");
            writer.WriteString(
                "punctuationDisplay",
                profile.PunctuationDisplay ==
                    SubtitlePunctuationDisplay.Preserve
                    ? "preserve"
                    : "hideJapaneseCommaAndPeriod");
            writer.WriteStartArray("selectedCueIds");
            foreach (EntityId<CueIdTag> cueId in binding.SelectedCueIds)
            {
                writer.WriteStringValue(cueId.ToString());
            }

            writer.WriteEndArray();
            writer.WriteNumber("sourceCueCount", result.SourceCueCount);
            writer.WriteStartArray("displayedCues");
            foreach (SubtitleDisplayedCue cue in result.DisplayedCues)
            {
                writer.WriteStartObject();
                writer.WriteString("cueId", cue.Cue.Id.ToString());
                writer.WriteString("text", cue.DisplayText);
                writer.WriteBoolean("textChanged", cue.TextChanged);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("omittedCueIds");
            foreach (EntityId<CueIdTag> cueId in result.OmittedCueIds)
            {
                writer.WriteStringValue(cueId.ToString());
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTerops(
        Utf8JsonWriter writer,
        IReadOnlyList<LayoutSubtitleTeropBinding> terops)
    {
        writer.WriteStartArray("terops");
        foreach (LayoutSubtitleTeropBinding binding in terops)
        {
            EmphasisCueHistory history = binding.History;
            EmphasisCueRevision revision = history.CurrentRevision;
            EmphasisCueSnapshot cue = revision.Snapshot;
            writer.WriteStartObject();
            writer.WriteString("cueId", history.CueId.ToString());
            writer.WriteString("revisionId", revision.Id.ToString());
            writer.WriteString("revisionDigest", revision.ContentDigest.Hex);
            writer.WriteString(
                "compositionVersionId",
                revision.CompositionVersionId.ToString());
            writer.WriteString("sourceKind", history.SourceKind ==
                EmphasisCueSourceKind.AdoptedSuggestion
                ? "adoptedSuggestion"
                : "manualEditorial");
            writer.WriteString("text", cue.Text);
            writer.WriteString("style", GetCueStyle(cue.Style));
            writer.WriteString("regionName", cue.Cue.RegionName);
            writer.WritePropertyName("outputRange");
            WriteTimeRange(writer, cue.Cue.Range);
            writer.WriteNumber("revisionCount", history.Revisions.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteVariantPair(
        Utf8JsonWriter writer,
        LayoutSubtitleVariantPair? pair)
    {
        writer.WritePropertyName("variantPair");
        if (pair is null)
        {
            writer.WriteNullValue();
            return;
        }

        SubtitleDeliveryVariantEvidence burnedIn =
            pair.BurnedIn.SubtitleVariant!;
        SubtitleDeliveryVariantEvidence subtitleFree =
            pair.SubtitleFree.SubtitleVariant!;
        writer.WriteStartObject();
        writer.WriteString(
            "compositionVersionId",
            pair.BurnedIn.CompositionVersionId.ToString());
        writer.WriteString(
            "compositionVersionDigest",
            pair.BurnedIn.CompositionVersionDigest.Hex);
        writer.WriteString(
            "displayProfileDigest",
            burnedIn.DisplayProfileDigest.Hex);
        writer.WriteString(
            "subtitleEvidenceDigest",
            burnedIn.SubtitleEvidenceDigest.Hex);
        writer.WriteStartObject("burnedIn");
        writer.WriteString(
            "deliveryManifestId",
            pair.BurnedIn.Id.ToString());
        writer.WriteString(
            "renderGraphId",
            burnedIn.RenderGraphId.ToString());
        writer.WriteEndObject();
        writer.WriteStartObject("subtitleFree");
        writer.WriteString(
            "deliveryManifestId",
            pair.SubtitleFree.Id.ToString());
        writer.WriteString(
            "renderGraphId",
            subtitleFree.RenderGraphId.ToString());
        writer.WriteBoolean(
            "hasSidecar",
            subtitleFree.SidecarArtifact is not null);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteOptionalSubtitlePixels(
        Utf8JsonWriter writer,
        SubtitleLayoutRect rect,
        int width,
        int height)
    {
        try
        {
            var normalized = new NormalizedRect(
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height);
            WriteExactPixelRect(writer, normalized, width, height);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException
                or OverflowException)
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteNormalizedAndPixels(
        Utf8JsonWriter writer,
        NormalizedRect rect,
        int width,
        int height)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("rect");
        WriteNormalizedRect(writer, rect);
        writer.WritePropertyName("pixels");
        WriteExactPixelRect(writer, rect, width, height);
        writer.WriteEndObject();
    }

    private static void WriteExactPixelRect(
        Utf8JsonWriter writer,
        NormalizedRect rect,
        int width,
        int height)
    {
        int x = ResolveCoordinate(rect.X, width);
        int y = ResolveCoordinate(rect.Y, height);
        int resolvedWidth = ResolveCoordinate(rect.Width, width);
        int resolvedHeight = ResolveCoordinate(rect.Height, height);
        if (resolvedWidth <= 0
            || resolvedHeight <= 0
            || (long)x + resolvedWidth > width
            || (long)y + resolvedHeight > height)
        {
            throw new InvalidOperationException(
                "The normalized rectangle does not resolve on the pixel grid.");
        }

        writer.WriteStartObject();
        writer.WriteNumber("x", x);
        writer.WriteNumber("y", y);
        writer.WriteNumber("width", resolvedWidth);
        writer.WriteNumber("height", resolvedHeight);
        writer.WriteEndObject();
    }

    private static int ResolveCoordinate(RationalNumber value, int extent)
    {
        BigInteger quotient = BigInteger.DivRem(
            (BigInteger)value.Numerator * extent,
            value.Denominator,
            out BigInteger remainder);
        if (!remainder.IsZero)
        {
            throw new InvalidOperationException(
                "The normalized coordinate is not pixel aligned.");
        }

        return checked((int)quotient);
    }

    private static void WriteNormalizedRect(
        Utf8JsonWriter writer,
        NormalizedRect rect)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("x");
        WriteRational(writer, rect.X);
        writer.WritePropertyName("y");
        WriteRational(writer, rect.Y);
        writer.WritePropertyName("width");
        WriteRational(writer, rect.Width);
        writer.WritePropertyName("height");
        WriteRational(writer, rect.Height);
        writer.WriteEndObject();
    }

    private static void WriteSubtitleRect(
        Utf8JsonWriter writer,
        SubtitleLayoutRect rect)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("x");
        WriteRational(writer, rect.X);
        writer.WritePropertyName("y");
        WriteRational(writer, rect.Y);
        writer.WritePropertyName("width");
        WriteRational(writer, rect.Width);
        writer.WritePropertyName("height");
        WriteRational(writer, rect.Height);
        writer.WriteEndObject();
    }

    private static void WriteRational(
        Utf8JsonWriter writer,
        RationalNumber value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "numerator",
            value.Numerator.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "denominator",
            value.Denominator.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteTimeRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WritePixelRect(Utf8JsonWriter writer, PixelRect rect)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", rect.X);
        writer.WriteNumber("y", rect.Y);
        writer.WriteNumber("width", rect.Width);
        writer.WriteNumber("height", rect.Height);
        writer.WriteEndObject();
    }

    private static string GetStatus(LayoutSubtitleBridgeStatus status) =>
        status switch
        {
            LayoutSubtitleBridgeStatus.Available => "available",
            LayoutSubtitleBridgeStatus.Forbidden => "forbidden",
            LayoutSubtitleBridgeStatus.NotFound => "notFound",
            LayoutSubtitleBridgeStatus.Conflict => "conflict",
            LayoutSubtitleBridgeStatus.NoChange => "noChange",
            LayoutSubtitleBridgeStatus.Invalid => "invalid",
            LayoutSubtitleBridgeStatus.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string GetRegionRole(LayoutRegionRole role) =>
        role switch
        {
            LayoutRegionRole.Video => "video",
            LayoutRegionRole.Material => "material",
            LayoutRegionRole.Subtitle => "subtitle",
            LayoutRegionRole.Graphic => "graphic",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static string GetManualMode(ManualLayoutMode mode) =>
        mode switch
        {
            ManualLayoutMode.SingleSubjectCrop => "singleSubjectCrop",
            ManualLayoutMode.TwoSpeakerStack => "twoSpeakerStack",
            ManualLayoutMode.SpeakerMaterialStack =>
                "speakerMaterialStack",
            ManualLayoutMode.MaterialWithSpeakerInset =>
                "materialWithSpeakerInset",
            ManualLayoutMode.ImportantRegionZoom => "importantRegionZoom",
            ManualLayoutMode.BlurredSourceBackground =>
                "blurredSourceBackground",
            ManualLayoutMode.FixedTemplatePlacement =>
                "fixedTemplatePlacement",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static ManualDestinationSlot GetDestinationSlot(
        ManualLayoutMode mode,
        int index) =>
        (mode, index) switch
        {
            (ManualLayoutMode.SingleSubjectCrop, 0) =>
                ManualDestinationSlot.SingleSubject,
            (ManualLayoutMode.TwoSpeakerStack, 0) =>
                ManualDestinationSlot.TopSpeaker,
            (ManualLayoutMode.TwoSpeakerStack, 1) =>
                ManualDestinationSlot.BottomSpeaker,
            (ManualLayoutMode.SpeakerMaterialStack, 0) =>
                ManualDestinationSlot.StackedSpeaker,
            (ManualLayoutMode.SpeakerMaterialStack, 1) =>
                ManualDestinationSlot.StackedMaterial,
            (ManualLayoutMode.MaterialWithSpeakerInset, 0) =>
                ManualDestinationSlot.MainMaterial,
            (ManualLayoutMode.MaterialWithSpeakerInset, 1) =>
                ManualDestinationSlot.SpeakerInset,
            (ManualLayoutMode.ImportantRegionZoom, 0) =>
                ManualDestinationSlot.ZoomedImportantRegion,
            (ManualLayoutMode.BlurredSourceBackground, 0) =>
                ManualDestinationSlot.BlurredBackground,
            (ManualLayoutMode.BlurredSourceBackground, 1) =>
                ManualDestinationSlot.ForegroundSubject,
            (ManualLayoutMode.FixedTemplatePlacement, 0) =>
                ManualDestinationSlot.TemplateVideo,
            _ => throw new InvalidOperationException(
                "The manual layout placement index is invalid."),
        };

    private static string GetDestinationSlot(ManualDestinationSlot slot) =>
        slot switch
        {
            ManualDestinationSlot.SingleSubject => "singleSubject",
            ManualDestinationSlot.TopSpeaker => "topSpeaker",
            ManualDestinationSlot.BottomSpeaker => "bottomSpeaker",
            ManualDestinationSlot.StackedSpeaker => "stackedSpeaker",
            ManualDestinationSlot.StackedMaterial => "stackedMaterial",
            ManualDestinationSlot.MainMaterial => "mainMaterial",
            ManualDestinationSlot.SpeakerInset => "speakerInset",
            ManualDestinationSlot.ZoomedImportantRegion =>
                "zoomedImportantRegion",
            ManualDestinationSlot.BlurredBackground => "blurredBackground",
            ManualDestinationSlot.ForegroundSubject => "foregroundSubject",
            ManualDestinationSlot.TemplateVideo => "templateVideo",
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

    private static ManualSourceSemanticRole GetSemanticRole(
        ManualLayoutMode mode,
        int index) =>
        (mode, index) switch
        {
            (ManualLayoutMode.SingleSubjectCrop, 0) =>
                ManualSourceSemanticRole.Subject,
            (ManualLayoutMode.TwoSpeakerStack, _) =>
                ManualSourceSemanticRole.Speaker,
            (ManualLayoutMode.SpeakerMaterialStack, 0) =>
                ManualSourceSemanticRole.Speaker,
            (ManualLayoutMode.SpeakerMaterialStack, 1) =>
                ManualSourceSemanticRole.Material,
            (ManualLayoutMode.MaterialWithSpeakerInset, 0) =>
                ManualSourceSemanticRole.Material,
            (ManualLayoutMode.MaterialWithSpeakerInset, 1) =>
                ManualSourceSemanticRole.Speaker,
            (ManualLayoutMode.ImportantRegionZoom, 0) =>
                ManualSourceSemanticRole.ImportantRegion,
            (ManualLayoutMode.BlurredSourceBackground, _) =>
                ManualSourceSemanticRole.Subject,
            (ManualLayoutMode.FixedTemplatePlacement, 0) =>
                ManualSourceSemanticRole.TemplateVideo,
            _ => throw new InvalidOperationException(
                "The manual source role is invalid."),
        };

    private static string GetSemanticRole(ManualSourceSemanticRole role) =>
        role switch
        {
            ManualSourceSemanticRole.Subject => "subject",
            ManualSourceSemanticRole.Speaker => "speaker",
            ManualSourceSemanticRole.Material => "material",
            ManualSourceSemanticRole.ImportantRegion => "importantRegion",
            ManualSourceSemanticRole.TemplateVideo => "templateVideo",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static string GetTreatment(ManualLayoutMode mode, int index) =>
        mode == ManualLayoutMode.BlurredSourceBackground && index == 0
            ? "blurredBackground"
            : "standard";

    private static string GetTreatment(ManualSourceTreatment treatment) =>
        treatment switch
        {
            ManualSourceTreatment.Standard => "standard",
            ManualSourceTreatment.BlurredBackground => "blurredBackground",
            _ => throw new ArgumentOutOfRangeException(nameof(treatment)),
        };

    private static string GetCueStyle(SubtitleCueStyle style) =>
        style switch
        {
            SubtitleCueStyle.Normal => "normal",
            SubtitleCueStyle.Emphasis => "emphasis",
            SubtitleCueStyle.Caution => "caution",
            SubtitleCueStyle.Quote => "quote",
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };

    private static string GetHardConstraint(
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
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string GetSoftConstraint(
        SubtitleSoftConstraintKind kind) =>
        kind switch
        {
            SubtitleSoftConstraintKind.FaceRegionOverlap =>
                "faceRegionOverlap",
            SubtitleSoftConstraintKind.MaterialRegionOverlap =>
                "materialRegionOverlap",
            SubtitleSoftConstraintKind.GraphicRegionOverlap =>
                "graphicRegionOverlap",
            SubtitleSoftConstraintKind.PlacementChanged => "placementChanged",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
