using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using CoreProjectSnapshotJson = HubRadio.Core.Projects.ProjectSnapshotJson;

namespace HubRadio.App.Compositions;

public enum StandardCompositionPlanningFailureReason
{
    ParentMismatch,
    TimelineMapMismatch,
    SettingsMismatch,
    ProfileMismatch,
    SourceEvidenceMismatch,
    TopicMetadataMismatch,
    CandidateEvidenceMismatch,
    DuplicateCandidate,
}

public sealed class StandardCompositionPlanningException : Exception
{
    internal StandardCompositionPlanningException(
        StandardCompositionPlanningFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public StandardCompositionPlanningFailureReason Reason { get; }

    private static string GetStableMessage(
        StandardCompositionPlanningFailureReason reason) =>
        reason switch
        {
            StandardCompositionPlanningFailureReason.ParentMismatch =>
                "The standard-composition parent does not match the long-form project.",
            StandardCompositionPlanningFailureReason.TimelineMapMismatch =>
                "The standard-composition timeline map does not match its parent.",
            StandardCompositionPlanningFailureReason.SettingsMismatch =>
                "The standard-composition settings do not match the closed source-only policy.",
            StandardCompositionPlanningFailureReason.ProfileMismatch =>
                "The standard-composition render profiles do not match the required outputs.",
            StandardCompositionPlanningFailureReason.SourceEvidenceMismatch =>
                "The standard-composition source evidence is inconsistent.",
            StandardCompositionPlanningFailureReason.TopicMetadataMismatch =>
                "The standard-composition topic metadata is inconsistent.",
            StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch =>
                "The standard-composition candidate or adoption evidence is inconsistent.",
            StandardCompositionPlanningFailureReason.DuplicateCandidate =>
                "The standard-composition candidate inventory contains a duplicate identity.",
            _ => "The standard-composition plan could not be created.",
        };
}

public sealed class StandardCompositionPlanningRequest
{
    public StandardCompositionPlanningRequest(
        CompositionVersion parent,
        TimelineMap timelineMap,
        LongFormCompositionPlan longForm,
        BundledRenderProfileDefinition shortFormProfile,
        SourceDisplayGeometry sourceDisplayGeometry,
        RationalNumber sourceFrameRate,
        StandardProjectStoreState projectStoreState,
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        ContentDigest toolDigest,
        ContentDigest modelDigest,
        MusicPlanningResult musicPlanning,
        IReadOnlyList<TopicMetadataRevision> topicMetadataRevisions,
        IReadOnlyList<EmphasisSuggestionCandidate> candidates,
        IReadOnlyList<HighlightSuggestionAdoptionResult> adoptedHighlights,
        StandardThumbnailEvidence? thumbnailEvidence = null)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        TimelineMap = timelineMap
            ?? throw new ArgumentNullException(nameof(timelineMap));
        LongForm = longForm ?? throw new ArgumentNullException(nameof(longForm));
        ShortFormProfile = shortFormProfile
            ?? throw new ArgumentNullException(nameof(shortFormProfile));
        SourceDisplayGeometry = sourceDisplayGeometry
            ?? throw new ArgumentNullException(nameof(sourceDisplayGeometry));
        if (sourceFrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFrameRate));
        }

        SourceFrameRate = sourceFrameRate;
        ProjectStoreState = projectStoreState
            ?? throw new ArgumentNullException(nameof(projectStoreState));
        SettingsProfile = settingsProfile
            ?? throw new ArgumentNullException(nameof(settingsProfile));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ToolDigest = toolDigest
            ?? throw new ArgumentNullException(nameof(toolDigest));
        ModelDigest = modelDigest
            ?? throw new ArgumentNullException(nameof(modelDigest));
        MusicPlanning = musicPlanning
            ?? throw new ArgumentNullException(nameof(musicPlanning));
        TopicMetadataRevisions = Copy(
            topicMetadataRevisions,
            nameof(topicMetadataRevisions));
        Candidates = Copy(candidates, nameof(candidates));
        AdoptedHighlights = Copy(
            adoptedHighlights,
            nameof(adoptedHighlights));
        ThumbnailEvidence = thumbnailEvidence;
    }

    public CompositionVersion Parent { get; }

    public TimelineMap TimelineMap { get; }

    public LongFormCompositionPlan LongForm { get; }

    public BundledRenderProfileDefinition ShortFormProfile { get; }

    public SourceDisplayGeometry SourceDisplayGeometry { get; }

    public RationalNumber SourceFrameRate { get; }

    public StandardProjectStoreState ProjectStoreState { get; }

    public DefaultSettingsProfile SettingsProfile { get; }

    public DefaultSettingsSnapshot Settings { get; }

    public ContentDigest ToolDigest { get; }

    public ContentDigest ModelDigest { get; }

    public MusicPlanningResult MusicPlanning { get; }

    public IReadOnlyList<TopicMetadataRevision> TopicMetadataRevisions { get; }

    public IReadOnlyList<EmphasisSuggestionCandidate> Candidates { get; }

    public IReadOnlyList<HighlightSuggestionAdoptionResult> AdoptedHighlights
    { get; }

    public StandardThumbnailEvidence? ThumbnailEvidence { get; }

    private static IReadOnlyList<T> Copy<T>(
        IReadOnlyList<T> values,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        int count = values.Count;
        StandardCompositionResourceLimits.ValidateEvidenceCollectionCount(
            count,
            parameterName);
        var copied = new T[count];
        for (int index = 0; index < copied.Length; index++)
        {
            copied[index] = values[index] ?? throw new ArgumentException(
                "Standard-composition inputs must not contain null elements.",
                parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}

public sealed class StandardThumbnailFrameEvidence
{
    internal StandardThumbnailFrameEvidence(
        int spriteOrdinal,
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        int sourceFrameSequence,
        RationalTime sourcePresentationTime,
        RationalTime sourceDuration,
        bool sourceKeyFrame)
    {
        if (spriteOrdinal < 0
            || proxyFrameSequence < 0
            || sourceFrameSequence < 0
            || sourceDuration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(nameof(spriteOrdinal));
        }

        SpriteOrdinal = spriteOrdinal;
        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
        SourceDuration = sourceDuration;
        SourceKeyFrame = sourceKeyFrame;
    }

    public int SpriteOrdinal { get; }

    public int ProxyFrameSequence { get; }

    public RationalTime ProxyPresentationTime { get; }

    public int SourceFrameSequence { get; }

    public RationalTime SourcePresentationTime { get; }

    public RationalTime SourceDuration { get; }

    public bool SourceKeyFrame { get; }
}

public sealed class StandardThumbnailEvidence
{
    public const string SelectionRuleVersion =
        "preview-sprite-evenly-spaced-unique-source-v2";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal StandardThumbnailEvidence(
        EntityId<ProjectIdTag> projectId,
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ArtifactIdTag> sourceArtifactId,
        string sourceFileName,
        ContentDigest sourceContentDigest,
        long sourceByteSize,
        int sourceStreamIndex,
        ContentDigest sourcePtsIndexDigest,
        ContentDigest proxyFrameMapDigest,
        EntityId<ArtifactIdTag> proxyArtifactId,
        ContentDigest proxyContentDigest,
        long proxyByteSize,
        ContentDigest proxyPtsIndexDigest,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        ContentDigest spriteImageDigest,
        long spriteImageByteSize,
        EntityId<ArtifactIdTag> spriteIndexArtifactId,
        ContentDigest spriteIndexDigest,
        long spriteIndexByteSize,
        string previewProfileVersion,
        IReadOnlyList<StandardThumbnailFrameEvidence> frames)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(sourceArtifactId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(sourceContentDigest);
        ArgumentNullException.ThrowIfNull(sourcePtsIndexDigest);
        ArgumentNullException.ThrowIfNull(proxyFrameMapDigest);
        ArgumentNullException.ThrowIfNull(proxyArtifactId);
        ArgumentNullException.ThrowIfNull(proxyContentDigest);
        ArgumentNullException.ThrowIfNull(proxyPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(spriteImageArtifactId);
        ArgumentNullException.ThrowIfNull(spriteImageDigest);
        ArgumentNullException.ThrowIfNull(spriteIndexArtifactId);
        ArgumentNullException.ThrowIfNull(spriteIndexDigest);
        ArgumentNullException.ThrowIfNull(previewProfileVersion);
        ArgumentNullException.ThrowIfNull(frames);
        if (sourceByteSize <= 0
            || proxyByteSize <= 0
            || spriteImageByteSize <= 0
            || spriteIndexByteSize <= 0
            || sourceStreamIndex < 0
            || string.IsNullOrWhiteSpace(sourceFileName)
            || sourceFileName.Contains('/', StringComparison.Ordinal)
            || sourceFileName.Contains('\\', StringComparison.Ordinal)
            || sourceFileName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(previewProfileVersion)
            || !string.Equals(
                previewProfileVersion,
                previewProfileVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Standard thumbnail evidence metadata is invalid.",
                nameof(frames));
        }

        EntityId<ArtifactIdTag>[] artifactIds =
        [
            sourceArtifactId,
            proxyArtifactId,
            spriteImageArtifactId,
            spriteIndexArtifactId,
        ];
        if (artifactIds.Distinct().Count() != artifactIds.Length)
        {
            throw new ArgumentException(
                "Every thumbnail evidence artifact must have a distinct ID.",
                nameof(sourceArtifactId));
        }

        StandardThumbnailFrameEvidence[] copied = frames.ToArray();
        if (copied.Length is < 1 or > StandardCompositionPlan.MaximumThumbnailCount
            || copied.Any(static frame => frame is null)
            || copied.Select(static frame => frame.SpriteOrdinal).Distinct()
                .Count() != copied.Length
            || copied.Select(static frame => frame.ProxyFrameSequence).Distinct()
                .Count() != copied.Length
            || copied.Select(static frame => frame.SourceFrameSequence).Distinct()
                .Count() != copied.Length
            || !copied.SequenceEqual(copied.OrderBy(
                static frame => frame.SpriteOrdinal)))
        {
            throw new ArgumentException(
                "Standard thumbnail frames must be one to three unique ordinally ordered source frames.",
                nameof(frames));
        }

        ProjectId = projectId;
        SourceAssetId = sourceAssetId;
        SourceArtifactId = sourceArtifactId;
        SourceFileName = sourceFileName;
        SourceContentDigest = sourceContentDigest;
        SourceByteSize = sourceByteSize;
        SourceStreamIndex = sourceStreamIndex;
        SourcePtsIndexDigest = sourcePtsIndexDigest;
        ProxyFrameMapDigest = proxyFrameMapDigest;
        ProxyArtifactId = proxyArtifactId;
        ProxyContentDigest = proxyContentDigest;
        ProxyByteSize = proxyByteSize;
        ProxyPtsIndexDigest = proxyPtsIndexDigest;
        SpriteImageArtifactId = spriteImageArtifactId;
        SpriteImageDigest = spriteImageDigest;
        SpriteImageByteSize = spriteImageByteSize;
        SpriteIndexArtifactId = spriteIndexArtifactId;
        SpriteIndexDigest = spriteIndexDigest;
        SpriteIndexByteSize = spriteIndexByteSize;
        PreviewProfileVersion = previewProfileVersion;
        Frames = Array.AsReadOnly(copied);
        EvidenceDigest = ComputeEvidenceDigest(this);
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public EntityId<ArtifactIdTag> SourceArtifactId { get; }

    public string SourceFileName { get; }

    public ContentDigest SourceContentDigest { get; }

    public long SourceByteSize { get; }

    public int SourceStreamIndex { get; }

    public ContentDigest SourcePtsIndexDigest { get; }

    public ContentDigest ProxyFrameMapDigest { get; }

    public EntityId<ArtifactIdTag> ProxyArtifactId { get; }

    public ContentDigest ProxyContentDigest { get; }

    public long ProxyByteSize { get; }

    public ContentDigest ProxyPtsIndexDigest { get; }

    public EntityId<ArtifactIdTag> SpriteImageArtifactId { get; }

    public ContentDigest SpriteImageDigest { get; }

    public long SpriteImageByteSize { get; }

    public EntityId<ArtifactIdTag> SpriteIndexArtifactId { get; }

    public ContentDigest SpriteIndexDigest { get; }

    public long SpriteIndexByteSize { get; }

    public string PreviewProfileVersion { get; }

    public IReadOnlyList<StandardThumbnailFrameEvidence> Frames { get; }

    public ContentDigest EvidenceDigest { get; }

    public static async Task<StandardThumbnailEvidence> FromCompletedIngestAsync(
        IngestWorkflowRunResult ingest,
        ArtifactStore artifactStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(artifactStore);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ingest.IsComplete
            || ingest.ReconciliationRequired
            || ingest.Asset is not Asset asset
            || ingest.SourcePtsIndex is not PtsIndex sourcePtsIndex
            || ingest.Original is not ImportedOriginal original
            || ingest.Proxy is not GeneratedProxy proxy
            || ingest.FrameMap is not ProxyFrameMap proxyFrameMap
            || ingest.PreviewAssets is not GeneratedPreviewAssets preview
            || original.IntegrityStatus != OriginalIntegrityStatus.Verified
            || original.Asset.Id != asset.Id
            || original.Asset.ContentDigest != asset.ContentDigest
            || proxy.SourceAssetId != asset.Id
            || proxy.SourceContentDigest != asset.ContentDigest
            || proxy.SourceOriginalArtifactId != original.Artifact.Id
            || preview.SpriteIndex.SourceAssetId != asset.Id
            || preview.SpriteIndex.ProxyArtifactId != proxy.ProxyArtifact.Id)
        {
            throw new ArgumentException(
                "A completed verified ingest workflow is required for thumbnail evidence.",
                nameof(ingest));
        }

        await using ValidatedArtifactReadStream sourceStream =
            await artifactStore.OpenValidatedReadAsync(
                original.Artifact.Id,
                cancellationToken).ConfigureAwait(false);
        await using ValidatedArtifactReadStream proxyStream =
            await artifactStore.OpenValidatedReadAsync(
                proxy.ProxyArtifact.Id,
                cancellationToken).ConfigureAwait(false);
        await using ValidatedArtifactReadStream spriteImageStream =
            await artifactStore.OpenValidatedReadAsync(
                preview.SpriteImageArtifact.Id,
                cancellationToken).ConfigureAwait(false);
        await using ValidatedArtifactReadStream spriteIndexStream =
            await artifactStore.OpenValidatedReadAsync(
                preview.SpriteIndexArtifact.Id,
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateArtifactStream(
            sourceStream,
            original.Artifact,
            ArtifactKind.Original);
        ValidateArtifactStream(
            proxyStream,
            proxy.ProxyArtifact,
            ArtifactKind.Proxy);
        ValidateArtifactStream(
            spriteImageStream,
            preview.SpriteImageArtifact,
            ArtifactKind.Preview);
        ValidateArtifactStream(
            spriteIndexStream,
            preview.SpriteIndexArtifact,
            ArtifactKind.Preview);
        return FromValidatedPreview(
            sourcePtsIndex,
            proxyFrameMap,
            preview.SpriteIndex,
            original.Artifact,
            proxy.ProxyArtifact,
            preview.SpriteImageArtifact,
            preview.SpriteIndexArtifact);
    }

    internal static StandardThumbnailEvidence FromValidatedPreview(
        PtsIndex sourcePtsIndex,
        ProxyFrameMap proxyFrameMap,
        ThumbnailSpriteIndex spriteIndex,
        StoredArtifact sourceArtifact,
        StoredArtifact proxyArtifact,
        StoredArtifact spriteImageArtifact,
        StoredArtifact spriteIndexArtifact)
    {
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(proxyFrameMap);
        ArgumentNullException.ThrowIfNull(spriteIndex);
        ArgumentNullException.ThrowIfNull(sourceArtifact);
        ArgumentNullException.ThrowIfNull(proxyArtifact);
        ArgumentNullException.ThrowIfNull(spriteImageArtifact);
        ArgumentNullException.ThrowIfNull(spriteIndexArtifact);
        ContentDigest sourcePtsIndexDigest = Digest(
            sourcePtsIndex.CanonicalJson);
        ContentDigest proxyFrameMapDigest = Digest(
            proxyFrameMap.CanonicalJson);
        ContentDigest spriteIndexDigest = Digest(spriteIndex.CanonicalJson);
        if (proxyFrameMap.SourceAssetId != sourcePtsIndex.CandidateId
            || proxyFrameMap.ProjectId != sourcePtsIndex.ProjectId
            || proxyFrameMap.SourceContentDigest != sourcePtsIndex.ContentDigest
            || proxyFrameMap.SourcePtsIndexDigest != sourcePtsIndexDigest
            || proxyFrameMap.SourceFrameCount != sourcePtsIndex.Entries.Count
            || proxyFrameMap.SourcePresentationStart
                != sourcePtsIndex.PresentationStart
            || proxyFrameMap.SourcePresentationEnd
                != sourcePtsIndex.PresentationEnd
            || spriteIndex.SourceAssetId != sourcePtsIndex.CandidateId
            || spriteIndex.ProjectId != sourcePtsIndex.ProjectId
            || spriteIndex.ProxyContentDigest
                != proxyFrameMap.ProxyContentDigest
            || spriteIndex.ProxyPtsIndexDigest
                != proxyFrameMap.ProxyPtsIndexDigest
            || sourceArtifact.ProjectId != sourcePtsIndex.ProjectId
            || sourceArtifact.Kind != ArtifactKind.Original
            || sourceArtifact.ContentDigest != sourcePtsIndex.ContentDigest
            || sourceArtifact.ByteSize != sourcePtsIndex.ByteSize
            || proxyArtifact.Id != spriteIndex.ProxyArtifactId
            || proxyArtifact.ProjectId != sourcePtsIndex.ProjectId
            || proxyArtifact.Kind != ArtifactKind.Proxy
            || proxyArtifact.ContentDigest != spriteIndex.ProxyContentDigest
            || spriteImageArtifact.Id != spriteIndex.SpriteImageArtifactId
            || spriteImageArtifact.ProjectId != sourcePtsIndex.ProjectId
            || spriteImageArtifact.Kind != ArtifactKind.Preview
            || spriteImageArtifact.ContentDigest != spriteIndex.SpriteImageDigest
            || spriteIndexArtifact.ProjectId != sourcePtsIndex.ProjectId
            || spriteIndexArtifact.Kind != ArtifactKind.Preview
            || spriteIndexArtifact.ContentDigest != spriteIndexDigest
            || spriteIndexArtifact.ByteSize
                != StrictUtf8.GetByteCount(spriteIndex.CanonicalJson))
        {
            throw new ArgumentException(
                "Thumbnail preview evidence does not identify one source timeline.",
                nameof(spriteIndex));
        }

        var validated = new List<StandardThumbnailFrameEvidence>(
            spriteIndex.Cells.Count);
        var sourceSequences = new HashSet<int>();
        foreach (ThumbnailSpriteCell cell in spriteIndex.Cells.OrderBy(
            static value => value.Ordinal))
        {
            ProxyFrameMapEntry mapped;
            try
            {
                mapped = proxyFrameMap.MapProxyFrameToSource(
                    cell.ProxyFrameSequence);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new ArgumentException(
                    "Thumbnail sprite evidence is outside the proxy frame map.",
                    nameof(spriteIndex),
                    exception);
            }

            if (mapped.ProxyPresentationTime != cell.ProxyPresentationTime
                || (uint)mapped.SourceFrameSequence
                    >= (uint)sourcePtsIndex.Entries.Count)
            {
                throw new ArgumentException(
                    "Thumbnail sprite evidence does not match the proxy frame map.",
                    nameof(spriteIndex));
            }

            PtsIndexEntry source = sourcePtsIndex.Entries[
                mapped.SourceFrameSequence];
            if (source.PresentationTime != mapped.SourcePresentationTime)
            {
                throw new ArgumentException(
                    "Thumbnail source-frame evidence does not match the PTS index.",
                    nameof(spriteIndex));
            }

            if (!sourceSequences.Add(source.Sequence))
            {
                continue;
            }

            validated.Add(new StandardThumbnailFrameEvidence(
                cell.Ordinal,
                cell.ProxyFrameSequence,
                cell.ProxyPresentationTime,
                source.Sequence,
                source.PresentationTime,
                source.Duration,
                source.KeyFrame));
        }

        if (validated.Count == 0)
        {
            throw new ArgumentException(
                "Thumbnail preview evidence contains no mappable source frame.",
                nameof(spriteIndex));
        }

        StandardThumbnailFrameEvidence[] selected = SelectFrames(validated);

        return new StandardThumbnailEvidence(
            sourcePtsIndex.ProjectId,
            sourcePtsIndex.CandidateId,
            sourceArtifact.Id,
            sourcePtsIndex.SourceFileName,
            sourcePtsIndex.ContentDigest,
            sourcePtsIndex.ByteSize,
            sourcePtsIndex.StreamIndex,
            sourcePtsIndexDigest,
            proxyFrameMapDigest,
            spriteIndex.ProxyArtifactId,
            spriteIndex.ProxyContentDigest,
            proxyArtifact.ByteSize,
            spriteIndex.ProxyPtsIndexDigest,
            spriteIndex.SpriteImageArtifactId,
            spriteIndex.SpriteImageDigest,
            spriteImageArtifact.ByteSize,
            spriteIndexArtifact.Id,
            spriteIndexDigest,
            spriteIndexArtifact.ByteSize,
            spriteIndex.ProfileVersion,
            selected);
    }

    private static StandardThumbnailFrameEvidence[] SelectFrames(
        IReadOnlyList<StandardThumbnailFrameEvidence> frames)
    {
        int count = Math.Min(
            frames.Count,
            StandardCompositionPlan.MaximumThumbnailCount);
        if (count == frames.Count)
        {
            return frames.ToArray();
        }

        var selected = new StandardThumbnailFrameEvidence[count];
        int lastSourceIndex = frames.Count - 1;
        int lastSelectionIndex = count - 1;
        for (int index = 0; index < selected.Length; index++)
        {
            int sourceIndex = checked((int)(
                (long)index * lastSourceIndex / lastSelectionIndex));
            selected[index] = frames[sourceIndex];
        }

        return selected;
    }

    private static void ValidateArtifactStream(
        ValidatedArtifactReadStream stream,
        StoredArtifact expected,
        ArtifactKind kind)
    {
        if (stream.Id != expected.Id
            || stream.ProjectId != expected.ProjectId
            || stream.Kind != kind
            || stream.ContentDigest != expected.ContentDigest
            || stream.ByteSize != expected.ByteSize
            || stream.Length != expected.ByteSize)
        {
            throw new InvalidOperationException(
                "A verified thumbnail artifact does not match its ingest snapshot.");
        }
    }

    private static ContentDigest Digest(string canonicalJson) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(canonicalJson));

    private static ContentDigest ComputeEvidenceDigest(
        StandardThumbnailEvidence value)
    {
        var fields = new List<string>
        {
            SelectionRuleVersion,
            value.ProjectId.ToString(),
            value.SourceAssetId.ToString(),
            value.SourceArtifactId.ToString(),
            value.SourceFileName,
            value.SourceContentDigest.Hex,
            value.SourceByteSize.ToString(CultureInfo.InvariantCulture),
            value.SourceStreamIndex.ToString(CultureInfo.InvariantCulture),
            value.SourcePtsIndexDigest.Hex,
            value.ProxyFrameMapDigest.Hex,
            value.ProxyArtifactId.ToString(),
            value.ProxyContentDigest.Hex,
            value.ProxyByteSize.ToString(CultureInfo.InvariantCulture),
            value.ProxyPtsIndexDigest.Hex,
            value.SpriteImageArtifactId.ToString(),
            value.SpriteImageDigest.Hex,
            value.SpriteImageByteSize.ToString(CultureInfo.InvariantCulture),
            value.SpriteIndexArtifactId.ToString(),
            value.SpriteIndexDigest.Hex,
            value.SpriteIndexByteSize.ToString(CultureInfo.InvariantCulture),
            value.PreviewProfileVersion,
        };
        foreach (StandardThumbnailFrameEvidence frame in value.Frames)
        {
            fields.Add(frame.SpriteOrdinal.ToString(CultureInfo.InvariantCulture));
            fields.Add(frame.ProxyFrameSequence.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.ProxyPresentationTime.Value.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.ProxyPresentationTime.Rate.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourceFrameSequence.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourcePresentationTime.Value.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourcePresentationTime.Rate.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourceDuration.Value.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourceDuration.Rate.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(frame.SourceKeyFrame.ToString(
                CultureInfo.InvariantCulture));
        }

        return StandardCompositionCanonical.ComputeDigest(
            "hubradio.standard-thumbnail-evidence.v1",
            fields);
    }
}

public static class StandardCompositionPlanner
{
    private const string RequiredVideoCodec = "h264";
    private const string RequiredAudioCodec = "aac";
    private const string RequiredHookPolicy =
        "sourceGroundedQuestionOrConclusionOnly";

    public static StandardCompositionPlan Create(
        StandardCompositionPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateParent(request);
        ValidateTimelineMap(request);
        ValidateSettings(request);
        ValidateProfiles(request);
        SourceSpan[] sourceEvidence = ValidateSourceEvidence(request);
        Dictionary<EntityId<TopicMetadataRevisionIdTag>, TopicMetadataRevision>
            topicMetadata = ValidateTopicMetadata(
                request,
                sourceEvidence,
                cancellationToken);
        Dictionary<EntityId<SuggestionIdTag>, HighlightSuggestionAdoptionResult>
            adoptions = ValidateCandidateInventory(request, sourceEvidence);
        ValidateThumbnailEvidence(request, sourceEvidence);

        ContentDigest evaluationInputDigest = ComputeEvaluationInputDigest(
            request,
            cancellationToken);
        ContentDigest autoDecisionDigest = ResolveAutoDecisionDigest(
            request.Parent);
        StandardPlanInputDigest[] inputs =
        [
            new StandardPlanInputDigest(
                StandardPlanInputKind.SourceAsset,
                sourceEvidence[0].SourceDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.SourceFrameRate,
                StandardCompositionCanonical.ComputeDigest(
                    "hubradio.standard-source-frame-rate.v1",
                    [
                        request.SourceFrameRate.Numerator.ToString(
                            CultureInfo.InvariantCulture),
                        request.SourceFrameRate.Denominator.ToString(
                            CultureInfo.InvariantCulture),
                    ])),
            new StandardPlanInputDigest(
                StandardPlanInputKind.ProjectSnapshot,
                StandardCompositionCanonical.ComputeProjectSnapshotDigest(
                    request.LongForm.Project)),
            new StandardPlanInputDigest(
                StandardPlanInputKind.ProjectStoreState,
                StandardCompositionCanonical.ComputeProjectStoreStateDigest(
                    request.LongForm.Project,
                    request.ProjectStoreState)),
            new StandardPlanInputDigest(
                StandardPlanInputKind.TranscriptRevision,
                request.LongForm.Provenance.TranscriptRevisionDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.TimelineMap,
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                    request.TimelineMap)),
            new StandardPlanInputDigest(
                StandardPlanInputKind.SettingsProfile,
                request.SettingsProfile.ContentDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.SettingsSnapshot,
                request.Settings.ContentDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.AutoDecision,
                autoDecisionDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.LongFormPlan,
                request.LongForm.PlanDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.Layout,
                StandardCompositionCanonical.ComputeProfileBindingDigest(
                    request.ShortFormProfile)),
            new StandardPlanInputDigest(
                StandardPlanInputKind.Tool,
                request.ToolDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.Model,
                request.ModelDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.MusicCatalog,
                request.MusicPlanning.CatalogDigest),
            new StandardPlanInputDigest(
                StandardPlanInputKind.MusicPlanning,
                StandardCompositionCanonical.ComputeMusicPlanningDigest(
                    request.MusicPlanning)),
            new StandardPlanInputDigest(
                StandardPlanInputKind.ThumbnailEvidence,
                request.ThumbnailEvidence?.EvidenceDigest
                    ?? StandardCompositionCanonical.ComputeDigest(
                        "hubradio.standard-thumbnail-evidence-absence.v1",
                        [])),
            new StandardPlanInputDigest(
                StandardPlanInputKind.EvaluationInput,
                evaluationInputDigest),
        ];
        ContentDigest inputDigest = StandardCompositionCanonical.ComputeDigest(
            "hubradio.standard-composition-input.v1",
            inputs.OrderBy(static item => item.Kind)
                .Select(static item => string.Concat(
                    item.Kind.ToString(),
                    ":",
                    item.ContentDigest.Hex))
                .Prepend(request.Parent.ContentDigest.Hex)
                .Prepend(request.Parent.Id.ToString()));
        EntityId<StandardCompositionPlanIdTag> planId =
            StandardCompositionCanonical.DeterministicId<
                StandardCompositionPlanIdTag>(inputDigest, "plan");
        int shortLimit = ReadCount(
            request,
            "project.verticalShortCandidateCount");
        StandardThumbnailPlan[] thumbnails = CreateThumbnails(
            request,
            inputDigest,
            ReadCount(request, "thumbnails.candidateCount"));

        var selected = new List<SelectedCandidate>();
        var exclusions = new List<StandardShortCandidateExclusion>();
        EmphasisSuggestionCandidate[] orderedCandidates = request.Candidates
            .OrderBy(static candidate => candidate.Payload.SourceSpan.AssetId.ToString(),
                StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Payload.SourceSpan.StreamId,
                StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Payload.SourceSpan.Range.Start)
            .ThenBy(static candidate => candidate.Payload.SourceSpan.Range.End)
            .ThenBy(static candidate => candidate.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        foreach (EmphasisSuggestionCandidate candidate in orderedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            adoptions.TryGetValue(
                candidate.Id,
                out HighlightSuggestionAdoptionResult? adoption);

            if (!IsCurrentCandidate(request, candidate, topicMetadata))
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.StaleEvidence));
                continue;
            }

            SourceSpan span = candidate.Payload.SourceSpan;
            SourceSpan[] groundingSpans = sourceEvidence.Where(evidence =>
                    IsGroundedBySource(span, evidence)
                    && HasTimelineMapping(request.TimelineMap, evidence, span))
                .ToArray();
            if (groundingSpans.Length != 1)
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.UnmappedSourceRange));
                continue;
            }

            if (span.Range.Duration < StandardShortFormPlan.MinimumDuration
                || span.Range.Duration > StandardShortFormPlan.MaximumDuration)
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.DurationOutsidePolicy));
                continue;
            }

            if (selected.Any(item => SourceSpanContentEquals(
                    item.Candidate.Payload.SourceSpan,
                    span)))
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.DuplicateSource));
                continue;
            }

            if (selected.Any(item => SourceSpansOverlap(
                    item.Candidate.Payload.SourceSpan,
                    span)))
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.OverlappingSource));
                continue;
            }

            if (selected.Count >= shortLimit)
            {
                exclusions.Add(new StandardShortCandidateExclusion(
                    candidate.Id,
                    StandardShortCandidateExclusionCode.CandidateLimitApplied));
                continue;
            }

            selected.Add(new SelectedCandidate(candidate, adoption));
        }

        StandardSubtitleVariantReference[] longSubtitles =
            CreateLongFormSubtitleReferences(request);
        var shorts = new List<StandardShortFormPlan>(selected.Count);
        var warningCodes = new HashSet<StandardCompositionWarningCode>();
        if (!request.ShortFormProfile.PlatformSafeZonesMeasured)
        {
            warningCodes.Add(
                StandardCompositionWarningCode.PlatformUiUnmeasured);
        }

        foreach (SelectedCandidate item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceSpan span = item.Candidate.Payload.SourceSpan;
            EntityId<LayoutSpecIdTag> layoutId =
                StandardCompositionCanonical.DeterministicId<LayoutSpecIdTag>(
                    inputDigest,
                    string.Concat("short-layout:", item.Candidate.Id));
            ManualLayoutPlan layout = CreateFallbackLayout(
                request.ShortFormProfile,
                request.SourceDisplayGeometry,
                span,
                layoutId);
            warningCodes.Add(
                StandardCompositionWarningCode.SubjectRegionUnavailable);
            warningCodes.Add(layout.Mode == ManualLayoutMode.FixedTemplatePlacement
                ? StandardCompositionWarningCode.FixedLayoutFallback
                : StandardCompositionWarningCode.BlurredBackgroundFallback);

            SuggestionReference selectedReference = item.Adoption?.Reference
                ?? SuggestionReference.FromPersistedReference(
                    item.Candidate.Id,
                    item.Candidate.Origin);
            StandardShortHookReference? hook = IsGroundedHook(item.Candidate)
                ? new StandardShortHookReference(span, selectedReference)
                : null;
            StandardSubtitleVariantReference shortSubtitle =
                CreateSubtitleReference(
                    StandardSubtitleVariantKind.BurnedIn,
                    request.Settings.ContentDigest,
                    request.LongForm.Provenance.TranscriptRevisionDigest,
                    request.ShortFormProfile.Profile.ProfileDigest);
            shorts.Add(new StandardShortFormPlan(
                StandardCompositionCanonical.DeterministicId<
                    StandardShortFormPlanIdTag>(
                    inputDigest,
                    string.Concat("short:", item.Candidate.Id)),
                [span],
                item.Adoption is null
                    ? StandardShortSelectionKind.GroundedCandidate
                    : StandardShortSelectionKind.ExplicitAdoption,
                selectedReference,
                item.Candidate.Suggestion.Reason,
                item.Candidate.Suggestion.MissingContext,
                item.Candidate.Suggestion.Warning,
                hook,
                layout,
                new StandardCallToActionReference(
                    request.LongForm.PlanId,
                    StandardCallToActionKind.ViewLongForm),
                [shortSubtitle]));
        }

        if (exclusions.Any(static exclusion =>
                exclusion.Code
                    == StandardShortCandidateExclusionCode.CandidateLimitApplied))
        {
            warningCodes.Add(
                StandardCompositionWarningCode.CandidateLimitApplied);
        }

        if (thumbnails.Length > 0)
        {
            warningCodes.Add(
                StandardCompositionWarningCode.ThumbnailQualityNotEvaluated);
        }

        var omissions = new List<StandardArtifactOmission>(2);
        if (shorts.Count < StandardCompositionPlan.MaximumShortFormCount)
        {
            omissions.Add(new StandardArtifactOmission(
                StandardCompositionArtifactKind.ShortForm,
                shorts.Count == 0
                    ? StandardCompositionOmissionCode.NoGroundedCandidate
                    : StandardCompositionOmissionCode.NoAdditionalGroundedCandidate));
        }

        if (thumbnails.Length < StandardCompositionPlan.MaximumThumbnailCount)
        {
            omissions.Add(new StandardArtifactOmission(
                StandardCompositionArtifactKind.Thumbnail,
                thumbnails.Length == 0
                    ? StandardCompositionOmissionCode.NoMeasuredSourceFrame
                    : StandardCompositionOmissionCode
                        .NoAdditionalMeasuredSourceFrame));
        }
        StandardCompositionWarning[] warnings = warningCodes
            .Order()
            .Select(static code => new StandardCompositionWarning(code))
            .ToArray();

        ContentDigest planDigest = StandardCompositionCanonical.ComputePlanDigest(
            planId,
            request.LongForm,
            request.ShortFormProfile,
            shorts,
            exclusions,
            thumbnails,
            longSubtitles,
            omissions,
            warnings,
            inputs);
        return new StandardCompositionPlan(
            planId,
            request.LongForm,
            request.ShortFormProfile,
            request.ProjectStoreState,
            request.MusicPlanning,
            shorts,
            exclusions,
            thumbnails,
            longSubtitles,
            omissions,
            warnings,
            inputs,
            planDigest);
    }

    private static void ValidateParent(StandardCompositionPlanningRequest request)
    {
        ContentDigest actualDigest = ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(
                request.Parent.Elements));
        if (request.Parent.ProjectId != request.LongForm.Project.Id
            || request.LongForm.Provenance.ProjectId
                != request.LongForm.Project.Id
            || actualDigest != request.Parent.ContentDigest)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.ParentMismatch);
        }
    }

    private static void ValidateTimelineMap(
        StandardCompositionPlanningRequest request)
    {
        if (request.TimelineMap.CompositionVersionId != request.Parent.Id)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.TimelineMapMismatch);
        }

        TimelineMap expected;
        try
        {
            expected = TimelineMapBuilder.Build(
                request.TimelineMap.Id,
                request.Parent);
        }
        catch (ArgumentException)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.TimelineMapMismatch);
        }

        if (!expected.Entries.SequenceEqual(request.TimelineMap.Entries))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.TimelineMapMismatch);
        }
    }

    private static void ValidateSettings(
        StandardCompositionPlanningRequest request)
    {
        try
        {
            DefaultSettingsSnapshot restored =
                DefaultSettingsSnapshot.ParseCanonical(
                    request.Settings.CanonicalJson,
                    request.SettingsProfile);
            if (request.Settings.ProfileId != request.SettingsProfile.Id
                || request.Settings.ProfileVersion
                    != request.SettingsProfile.Version
                || request.Settings.ProfileDigest
                    != request.SettingsProfile.ContentDigest
                || restored.ContentDigest != request.Settings.ContentDigest
                || !string.Equals(
                    restored.CanonicalJson,
                    request.Settings.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    StandardCompositionPlanningFailureReason.SettingsMismatch);
            }
        }
        catch (JsonException)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SettingsMismatch);
        }
        catch (ArgumentException)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SettingsMismatch);
        }

        Dictionary<string, DefaultSettingValue> values = request.Settings.Values
            .ToDictionary(
                static selection => selection.Key,
                static selection => selection.Value,
                StringComparer.Ordinal);
        if (!HasText(values, "project.publicationScope", "internalOnly")
            || !HasText(values, "output.landscapeResolution", "1920x1080")
            || !HasText(values, "output.verticalResolution", "1080x1920")
            || !HasIntegerInRange(
                values,
                "project.verticalShortCandidateCount",
                0,
                StandardCompositionPlan.MaximumShortFormCount)
            || !HasText(
                values,
                "editing.shortHookPolicy",
                RequiredHookPolicy)
            || !HasIntegerInRange(values, "editing.shortHookMaximumStart", 1, 5)
            || !HasInteger(values, "output.shortTargetDuration", 30)
            || !HasShortDurationPolicy(request.SettingsProfile)
            || !HasIntegerInRange(
                values,
                "thumbnails.candidateCount",
                1,
                StandardCompositionPlan.MaximumThumbnailCount)
            || !HasBoolean(values, "thumbnails.sourceFrameOnly", true)
            || !HasBoolean(values, "thumbnails.generatedFaceEnabled", false)
            || !HasBoolean(values, "thumbnails.generatedSceneEnabled", false))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SettingsMismatch);
        }
    }

    private static void ValidateProfiles(
        StandardCompositionPlanningRequest request)
    {
        BundledRenderProfileDefinition longProfile = request.LongForm.Profile;
        BundledRenderProfileDefinition shortProfile = request.ShortFormProfile;
        if (!ProfileMatches(
                longProfile,
                width: 1920,
                height: 1080,
                aspectWidth: 16,
                aspectHeight: 9,
                subtitleBurnIn: true)
            || !ProfileMatches(
                shortProfile,
                width: 1080,
                height: 1920,
                aspectWidth: 9,
                aspectHeight: 16,
                subtitleBurnIn: true))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.ProfileMismatch);
        }

        if (longProfile.Profile.FrameRate != request.SourceFrameRate
            || shortProfile.Profile.FrameRate != request.SourceFrameRate)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.ProfileMismatch);
        }
    }

    private static bool ProfileMatches(
        BundledRenderProfileDefinition definition,
        int width,
        int height,
        int aspectWidth,
        int aspectHeight,
        bool subtitleBurnIn) =>
        definition.Profile.Width == width
        && definition.Profile.Height == height
        && definition.Profile.SubtitleBurnIn == subtitleBurnIn
        && definition.Profile.LayoutSpecId == definition.Layout.Id
        && definition.Profile.TemplateId == definition.TemplateReference.Id
        && definition.Profile.TemplateVersionDigest
            == definition.TemplateReference.VersionDigest
        && definition.Layout.OutputAspectWidth == aspectWidth
        && definition.Layout.OutputAspectHeight == aspectHeight
        && string.Equals(
            definition.Profile.VideoCodec,
            RequiredVideoCodec,
            StringComparison.Ordinal)
        && string.Equals(
            definition.Profile.AudioCodec,
            RequiredAudioCodec,
            StringComparison.Ordinal);

    private static SourceSpan[] ValidateSourceEvidence(
        StandardCompositionPlanningRequest request)
    {
        SourceSpan[] spans = request.LongForm.Provenance.SourceSpans.ToArray();
        EntityId<AssetIdTag>[] assetIds = spans
            .Select(static span => span.AssetId)
            .Distinct()
            .ToArray();
        ContentDigest[] sourceDigests = spans
            .Select(static span => span.SourceDigest)
            .Distinct()
            .ToArray();
        if (spans.Length == 0
            || assetIds.Length != 1
            || sourceDigests.Length != 1
            || request.LongForm.Project.Assets.Count(asset =>
                asset.Id == assetIds[0]
                && asset.ContentDigest == sourceDigests[0]) != 1
            || spans.Any(span =>
                span.TranscriptRevisionId
                    != request.LongForm.Provenance.TranscriptRevisionId))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }

        SourceSpan[] ordered = spans
            .OrderBy(static span => span.Range.Start)
            .ThenBy(static span => span.Range.End)
            .ThenBy(static span => span.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        SourceSpan first = ordered[0];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        if (ordered.Any(span =>
            !ids.Add(span.Id)
            || span.AssetId != first.AssetId
                || span.SourceDigest != first.SourceDigest
                || !string.Equals(
                    span.StreamId,
                    first.StreamId,
                    StringComparison.Ordinal)))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }

        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> byId = ordered
            .ToDictionary(static span => span.Id);
        foreach (CompositionElement element in request.Parent.Elements)
        {
            if (element.Origin.Kind != OriginKind.SourceSpan
                || element.Origin.SourceSpanId is not
                    EntityId<SourceSpanIdTag> sourceSpanId
                || element.SourceRange is not TimeRange sourceRange
                || !byId.TryGetValue(sourceSpanId, out SourceSpan? span)
                || sourceRange.Start < span.Range.Start
                || sourceRange.End > span.Range.End)
            {
                throw Failure(
                    StandardCompositionPlanningFailureReason
                        .SourceEvidenceMismatch);
            }
        }

        return ordered;
    }

    private static void ValidateThumbnailEvidence(
        StandardCompositionPlanningRequest request,
        IReadOnlyList<SourceSpan> sourceEvidence)
    {
        StandardThumbnailEvidence? thumbnail = request.ThumbnailEvidence;
        if (thumbnail is null)
        {
            return;
        }

        Asset[] sourceAssets = request.LongForm.Project.Assets
            .Where(asset => asset.Id == thumbnail.SourceAssetId)
            .ToArray();
        if (thumbnail.ProjectId != request.LongForm.Project.Id
            || sourceAssets.Length != 1
            || !string.Equals(
                sourceAssets[0].FileName,
                thumbnail.SourceFileName,
                StringComparison.Ordinal)
            || sourceAssets[0].ContentDigest != thumbnail.SourceContentDigest
            || sourceAssets[0].ByteSize != thumbnail.SourceByteSize
            || sourceEvidence.Any(span =>
                span.AssetId != thumbnail.SourceAssetId
                || span.SourceDigest != thumbnail.SourceContentDigest))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }
    }

    private static StandardThumbnailPlan[] CreateThumbnails(
        StandardCompositionPlanningRequest request,
        ContentDigest inputDigest,
        int limit)
    {
        if (request.ThumbnailEvidence is not StandardThumbnailEvidence evidence)
        {
            return [];
        }

        return SpreadFrames(evidence.Frames, limit).Select(frame => new StandardThumbnailPlan(
                StandardCompositionCanonical.DeterministicId<
                    StandardThumbnailPlanIdTag>(
                    inputDigest,
                    string.Concat(
                        "thumbnail:",
                        frame.SpriteOrdinal.ToString(
                            CultureInfo.InvariantCulture))),
                OriginRef.FromAsset(evidence.SourceAssetId),
                frame.SourceFrameSequence,
                frame.SourcePresentationTime))
            .ToArray();
    }

    private static IReadOnlyList<StandardThumbnailFrameEvidence> SpreadFrames(
        IReadOnlyList<StandardThumbnailFrameEvidence> frames,
        int limit)
    {
        if (frames.Count <= limit)
        {
            return frames;
        }

        if (limit == 1)
        {
            return [frames[frames.Count / 2]];
        }

        var spread = new StandardThumbnailFrameEvidence[limit];
        for (int index = 0; index < limit; index++)
        {
            spread[index] = frames[checked((int)(
                (long)index * (frames.Count - 1) / (limit - 1)))];
        }

        return spread;
    }

    private static Dictionary<
        EntityId<TopicMetadataRevisionIdTag>,
        TopicMetadataRevision> ValidateTopicMetadata(
            StandardCompositionPlanningRequest request,
            IReadOnlyList<SourceSpan> sourceEvidence,
            CancellationToken cancellationToken)
    {
        var byId = new Dictionary<
            EntityId<TopicMetadataRevisionIdTag>,
            TopicMetadataRevision>();
        var metadataIds = new HashSet<EntityId<TopicMetadataIdTag>>();
        var topicRangeIds = new HashSet<EntityId<TopicRangeIdTag>>();
        var topicRangeRevisionIds = new HashSet<
            EntityId<TopicRangeRevisionIdTag>>();
        HashSet<EntityId<SourceSpanIdTag>> sourceSpanIds = sourceEvidence
            .Select(static span => span.Id)
            .ToHashSet();
        foreach (TopicMetadataRevision revision in request.TopicMetadataRevisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryAdd(revision.Id, revision)
                || !metadataIds.Add(revision.TopicMetadataId)
                || !topicRangeIds.Add(revision.TopicRangeId)
                || !topicRangeRevisionIds.Add(revision.TopicRangeRevisionId)
                || revision.ProjectId != request.LongForm.Project.Id
                || revision.SourceRevisionId
                    != request.LongForm.Provenance.TranscriptRevisionId
                || revision.SourceRevisionDigest
                    != request.LongForm.Provenance.TranscriptRevisionDigest
                || revision.TopicRange.Start < default(RationalTime)
                || revision.TopicRange.Duration <= default(RationalTime)
                || !sourceEvidence.Any(span =>
                    span.Range.Start < revision.TopicRange.End
                    && revision.TopicRange.Start < span.Range.End)
                || revision.Metadata.ImportantStatementSourceSpanIds.Any(
                    sourceSpanId => !sourceSpanIds.Contains(sourceSpanId)))
            {
                throw Failure(
                    StandardCompositionPlanningFailureReason.TopicMetadataMismatch);
            }
        }

        return byId;
    }

    private static Dictionary<
        EntityId<SuggestionIdTag>,
        HighlightSuggestionAdoptionResult> ValidateCandidateInventory(
            StandardCompositionPlanningRequest request,
            IReadOnlyList<SourceSpan> sourceEvidence)
    {
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> sourceEvidenceById =
            sourceEvidence.ToDictionary(static span => span.Id);
        var candidateById = new Dictionary<
            EntityId<SuggestionIdTag>,
            EmphasisSuggestionCandidate>();
        var candidateSpanById = new Dictionary<
            EntityId<SourceSpanIdTag>,
            SourceSpan>();
        foreach (EmphasisSuggestionCandidate candidate in request.Candidates)
        {
            SourceSpan candidateSpan = candidate.Payload.SourceSpan;
            if (!candidateById.TryAdd(candidate.Id, candidate)
                || candidateSpanById.TryGetValue(
                    candidateSpan.Id,
                    out SourceSpan? existingCandidateSpan)
                    && !SourceSpanContentEquals(
                        candidateSpan,
                        existingCandidateSpan)
                || sourceEvidenceById.TryGetValue(
                    candidateSpan.Id,
                    out SourceSpan? authoritativeSpan)
                    && !SourceSpanContentEquals(
                        candidateSpan,
                        authoritativeSpan))
            {
                throw Failure(
                    StandardCompositionPlanningFailureReason
                        .CandidateEvidenceMismatch);
            }

            candidateSpanById.TryAdd(candidateSpan.Id, candidateSpan);
        }

        var adoptionById = new Dictionary<
            EntityId<SuggestionIdTag>,
            HighlightSuggestionAdoptionResult>();
        foreach (HighlightSuggestionAdoptionResult adoption in
            request.AdoptedHighlights)
        {
            EntityId<SuggestionIdTag> candidateId =
                adoption.OriginalCandidate.Id;
            if (!candidateById.TryGetValue(
                    candidateId,
                    out EmphasisSuggestionCandidate? candidate)
                || !ReferenceEquals(candidate, adoption.OriginalCandidate)
                || adoption.DecidedCandidate.Status != SuggestionStatus.Adopted
                || adoption.Reference.SuggestionId != candidate.Id
                || adoption.Reference.Origin != candidate.Origin
                || !adoptionById.TryAdd(candidateId, adoption))
            {
                throw Failure(
                    StandardCompositionPlanningFailureReason.CandidateEvidenceMismatch);
            }
        }

        return adoptionById;
    }

    private static bool IsCurrentCandidate(
        StandardCompositionPlanningRequest request,
        EmphasisSuggestionCandidate candidate,
        IReadOnlyDictionary<
            EntityId<TopicMetadataRevisionIdTag>,
            TopicMetadataRevision> topicMetadata)
    {
        EmphasisCandidate payload = candidate.Payload;
        if (candidate.Producer != SuggestionProducerKind.Emphasis
            || candidate.Status != SuggestionStatus.Proposed
            || candidate.SourceDigest
                != request.LongForm.Provenance.TranscriptRevisionDigest
            || payload.SourceRevisionId
                != request.LongForm.Provenance.TranscriptRevisionId
            || payload.SourceRevisionDigest
                != request.LongForm.Provenance.TranscriptRevisionDigest
            || payload.SourceSpan.TranscriptRevisionId
                != request.LongForm.Provenance.TranscriptRevisionId
            || !string.Equals(
                payload.RuleVersion,
                EmphasisCandidateService.RuleVersion,
                StringComparison.Ordinal)
            || !topicMetadata.TryGetValue(
                payload.TopicMetadataRevisionId,
                out TopicMetadataRevision? metadata)
            || metadata.ProjectId != request.LongForm.Project.Id
            || metadata.SourceRevisionId
                != request.LongForm.Provenance.TranscriptRevisionId
            || metadata.SourceRevisionDigest
                != request.LongForm.Provenance.TranscriptRevisionDigest
            || payload.SourceSpan.Range.Start < metadata.TopicRange.Start
            || payload.SourceSpan.Range.End > metadata.TopicRange.End)
        {
            return false;
        }

        return true;
    }

    private static bool IsGroundedBySource(
        SourceSpan selected,
        SourceSpan evidence) =>
        (selected.Id != evidence.Id
            || SourceSpanContentEquals(selected, evidence))
        &&
        selected.AssetId == evidence.AssetId
        && selected.SourceDigest == evidence.SourceDigest
        && string.Equals(
            selected.StreamId,
            evidence.StreamId,
            StringComparison.Ordinal)
        && selected.TranscriptRevisionId == evidence.TranscriptRevisionId
        && selected.Range.Start >= evidence.Range.Start
        && selected.Range.End <= evidence.Range.End
        && ContainsContiguousTokens(
            evidence.TranscriptTokenIds,
            selected.TranscriptTokenIds);

    private static bool HasTimelineMapping(
        TimelineMap timelineMap,
        SourceSpan evidence,
        SourceSpan selected) =>
        timelineMap.Entries.Count(entry =>
            entry.Origin.Kind == OriginKind.SourceSpan
            && entry.Origin.SourceSpanId == evidence.Id
            && entry.SourceRange is TimeRange sourceRange
            && selected.Range.Start >= sourceRange.Start
            && selected.Range.End <= sourceRange.End) == 1;

    private static bool ContainsContiguousTokens<T>(
        IReadOnlyList<T> evidence,
        IReadOnlyList<T> selected)
        where T : IEquatable<T>
    {
        if (selected.Count == 0 || selected.Count > evidence.Count)
        {
            return false;
        }

        for (int start = 0; start + selected.Count <= evidence.Count; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < selected.Count; offset++)
            {
                if (!evidence[start + offset].Equals(selected[offset]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SourceSpanContentEquals(
        SourceSpan left,
        SourceSpan right) =>
        left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);

    private static bool SourceSpansOverlap(
        SourceSpan left,
        SourceSpan right) =>
        left.AssetId == right.AssetId
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range.Start < right.Range.End
        && right.Range.Start < left.Range.End;

    private static ManualLayoutPlan CreateFallbackLayout(
        BundledRenderProfileDefinition profile,
        SourceDisplayGeometry geometry,
        SourceSpan span,
        EntityId<LayoutSpecIdTag> layoutId)
    {
        NormalizedRect fullFrame = Rect(0, 1, 0, 1, 1, 1, 1, 1);
        OriginRef origin = OriginRef.FromSourceSpan(span.Id);
        try
        {
            ManualLayoutRequest request;
            if ((long)geometry.Width * 16 == (long)geometry.Height * 9)
            {
                var source = new ManualLayoutSource(
                    span.AssetId,
                    origin,
                    geometry,
                    new ManualSourceCrop(fullFrame),
                    new ManualSubjectRegion(fullFrame));
                request = ManualLayoutRequest.FixedTemplatePlacement(
                    layoutId,
                    profile.Layout,
                    ManualStaticCropDecision.SingleStaticCropSufficient,
                    source,
                    new ManualDestinationGeometry(fullFrame),
                    profile.TemplateReference);
            }
            else
            {
                NormalizedRect backgroundCrop = CreateTargetAspectCrop(
                    geometry,
                    targetWidth: 9,
                    targetHeight: 16);
                var background = new ManualLayoutSource(
                    span.AssetId,
                    origin,
                    geometry,
                    new ManualSourceCrop(backgroundCrop),
                    new ManualSubjectRegion(backgroundCrop));
                var foreground = new ManualLayoutSource(
                    span.AssetId,
                    origin,
                    geometry,
                    new ManualSourceCrop(fullFrame),
                    new ManualSubjectRegion(fullFrame));
                request = ManualLayoutRequest.BlurredSourceBackground(
                    layoutId,
                    profile.Layout,
                    ManualStaticCropDecision.SingleStaticCropSufficient,
                    background,
                    new ManualDestinationGeometry(fullFrame),
                    foreground,
                    new ManualDestinationGeometry(
                        CreateContainedDestination(geometry, 9, 16)));
            }

            ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);
            return resolution.Plan ?? throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }
        catch (ArgumentException)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }
    }

    private static NormalizedRect CreateTargetAspectCrop(
        SourceDisplayGeometry geometry,
        int targetWidth,
        int targetHeight)
    {
        int unit = Math.Min(
            geometry.Width / targetWidth,
            geometry.Height / targetHeight);
        if (unit <= 0)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.SourceEvidenceMismatch);
        }

        int cropWidth = targetWidth * unit;
        int cropHeight = targetHeight * unit;
        int x = (geometry.Width - cropWidth) / 2;
        int y = (geometry.Height - cropHeight) / 2;
        return Rect(
            x,
            geometry.Width,
            y,
            geometry.Height,
            cropWidth,
            geometry.Width,
            cropHeight,
            geometry.Height);
    }

    private static NormalizedRect CreateContainedDestination(
        SourceDisplayGeometry geometry,
        int outputWidth,
        int outputHeight)
    {
        long comparisonLeft = (long)geometry.Width * outputHeight;
        long comparisonRight = (long)geometry.Height * outputWidth;
        if (comparisonLeft >= comparisonRight)
        {
            long heightNumerator = (long)outputWidth * geometry.Height;
            long heightDenominator = (long)outputHeight * geometry.Width;
            return Rect(
                0,
                1,
                heightDenominator - heightNumerator,
                2 * heightDenominator,
                1,
                1,
                heightNumerator,
                heightDenominator);
        }

        long widthNumerator = (long)geometry.Width * outputHeight;
        long widthDenominator = (long)geometry.Height * outputWidth;
        return Rect(
            widthDenominator - widthNumerator,
            2 * widthDenominator,
            0,
            1,
            widthNumerator,
            widthDenominator,
            1,
            1);
    }

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator) =>
        new(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));

    private static bool IsGroundedHook(
        EmphasisSuggestionCandidate candidate) =>
        candidate.Payload.TextSource == EmphasisTextSourceKind.OriginalTranscript
        && candidate.Payload.Reasons.Contains(
            EmphasisReasonKind.AnswerAfterExplicitQuestion);

    private static StandardSubtitleVariantReference[]
        CreateLongFormSubtitleReferences(
            StandardCompositionPlanningRequest request) =>
        Enum.GetValues<StandardSubtitleVariantKind>()
            .Select(kind => CreateSubtitleReference(
                kind,
                request.Settings.ContentDigest,
                request.LongForm.Provenance.TranscriptRevisionDigest,
                request.LongForm.Profile.Profile.ProfileDigest))
            .ToArray();

    private static StandardSubtitleVariantReference CreateSubtitleReference(
        StandardSubtitleVariantKind kind,
        ContentDigest settingsDigest,
        ContentDigest transcriptDigest,
        ContentDigest renderProfileDigest) =>
        new(
            kind,
            StandardCompositionCanonical.ComputeDigest(
                "hubradio.standard-subtitle-configuration.v1",
                [
                    kind.ToString(),
                    settingsDigest.Hex,
                    transcriptDigest.Hex,
                    renderProfileDigest.Hex,
                ]));

    private static ContentDigest ResolveAutoDecisionDigest(
        CompositionVersion parent)
    {
        if (parent.Evidence is null)
        {
            return StandardCompositionCanonical.ComputeDigest(
                "hubradio.standard-auto-decision-absence.v1",
                []);
        }

        if (!string.Equals(
            parent.Evidence.Kind,
            CompositionEvidenceEnvelope.AutoDecisionKind,
            StringComparison.Ordinal)
            && !string.Equals(
                parent.Evidence.Kind,
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                StringComparison.Ordinal))
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.ParentMismatch);
        }

        if (string.Equals(
            parent.Evidence.Kind,
            CompositionEvidenceEnvelope.AutoDecisionKind,
            StringComparison.Ordinal))
        {
            return parent.Evidence.ContentDigest;
        }

        try
        {
            return StandardCompositionEvidenceCodec
                .ReadPriorAutoDecisionDigest(parent);
        }
        catch (Exception exception)
            when (exception is FormatException
                or JsonException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            throw Failure(
                StandardCompositionPlanningFailureReason.ParentMismatch);
        }
    }

    private static ContentDigest ComputeEvaluationInputDigest(
        StandardCompositionPlanningRequest request,
        CancellationToken cancellationToken) =>
        StandardCompositionEvidenceCodec.ComputeEvaluationInputDigest(
            request.SourceDisplayGeometry,
            request.TopicMetadataRevisions,
            request.Candidates,
            request.AdoptedHighlights,
            cancellationToken);

    private static bool HasText(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        string expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Text
        && string.Equals(value.TextValue, expected, StringComparison.Ordinal);

    private static bool HasInteger(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        long expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Integer
        && value.IntegerValue == expected;

    private static bool HasIntegerInRange(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        long minimum,
        long maximum) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Integer
        && value.IntegerValue >= minimum
        && value.IntegerValue <= maximum;

    private static int ReadCount(
        StandardCompositionPlanningRequest request,
        string key) =>
        checked((int)request.Settings.Values
            .Single(selection => string.Equals(
                selection.Key,
                key,
                StringComparison.Ordinal))
            .Value.IntegerValue!.Value);

    private static bool HasBoolean(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        bool expected) =>
        values.TryGetValue(key, out DefaultSettingValue? value)
        && value.Kind == DefaultSettingValueKind.Boolean
        && value.BooleanValue == expected;

    private static bool HasShortDurationPolicy(
        DefaultSettingsProfile profile)
    {
        DefaultSettingDefinition? definition = profile.Definitions
            .SingleOrDefault(static item => string.Equals(
                item.Key,
                "output.shortTargetDuration",
                StringComparison.Ordinal));
        return definition is not null
            && definition.Unit == DefaultSettingUnit.Seconds
            && definition.DefaultValue.Kind == DefaultSettingValueKind.Integer
            && definition.DefaultValue.IntegerValue == 30
            && definition.Minimum?.Kind == DefaultSettingValueKind.Integer
            && definition.Minimum.IntegerValue == 15
            && definition.Maximum?.Kind == DefaultSettingValueKind.Integer
            && definition.Maximum.IntegerValue == 60;
    }

    private static StandardCompositionPlanningException Failure(
        StandardCompositionPlanningFailureReason reason) => new(reason);

    private sealed record SelectedCandidate(
        EmphasisSuggestionCandidate Candidate,
        HighlightSuggestionAdoptionResult? Adoption);
}

internal static class StandardCompositionCanonical
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static ContentDigest ComputePlanDigest(
        EntityId<StandardCompositionPlanIdTag> id,
        LongFormCompositionPlan longForm,
        BundledRenderProfileDefinition shortFormProfile,
        IReadOnlyList<StandardShortFormPlan> shortForms,
        IReadOnlyList<StandardShortCandidateExclusion> exclusions,
        IReadOnlyList<StandardThumbnailPlan> thumbnails,
        IReadOnlyList<StandardSubtitleVariantReference> longSubtitles,
        IReadOnlyList<StandardArtifactOmission> omissions,
        IReadOnlyList<StandardCompositionWarning> warnings,
        IReadOnlyList<StandardPlanInputDigest> inputs)
    {
        var fields = new List<string>
        {
            id.ToString(),
            longForm.Project.Id.ToString(),
            longForm.PlanId.ToString(),
            longForm.PlanDigest.Hex,
            ComputeProfileBindingDigest(shortFormProfile).Hex,
        };
        foreach (StandardPlanInputDigest input in inputs.OrderBy(
            static item => item.Kind))
        {
            fields.Add(input.Kind.ToString());
            fields.Add(input.ContentDigest.Hex);
        }

        foreach (StandardShortFormPlan shortForm in shortForms.OrderBy(
            static item => item.Id.ToString(),
            StringComparer.Ordinal))
        {
            fields.Add("short");
            fields.Add(shortForm.Id.ToString());
            fields.Add(shortForm.SelectionKind.ToString());
            fields.Add(shortForm.SelectedHighlight.SuggestionId.ToString());
            fields.Add(OriginField(shortForm.SelectedHighlight.Origin));
            fields.Add(shortForm.SelectionReason);
            fields.Add(shortForm.MissingContext is null
                ? "missing-context:null"
                : "missing-context:value");
            if (shortForm.MissingContext is not null)
            {
                fields.Add(shortForm.MissingContext);
            }

            fields.Add(shortForm.SelectionWarning is null
                ? "selection-warning:null"
                : "selection-warning:value");
            if (shortForm.SelectionWarning is not null)
            {
                fields.Add(shortForm.SelectionWarning);
            }
            foreach (SourceSpan span in shortForm.SourceSpans)
            {
                AddSourceSpanFields(fields, span);
            }

            fields.Add(shortForm.Hook is null ? "no-hook" : "hook");
            if (shortForm.Hook is not null)
            {
                AddSourceSpanFields(fields, shortForm.Hook.SourceSpan);
                fields.Add(
                    shortForm.Hook.SelectedHighlight.SuggestionId.ToString());
            }

            AddLayoutFields(fields, shortForm.Layout);
            fields.Add(shortForm.CallToAction.Kind.ToString());
            fields.Add(shortForm.CallToAction.TargetLongFormPlanId.ToString());
            foreach (StandardSubtitleVariantReference subtitle in
                shortForm.SubtitleVariants.OrderBy(static item => item.Kind))
            {
                fields.Add(subtitle.Kind.ToString());
                fields.Add(subtitle.ProfileDigest.Hex);
            }
        }

        foreach (StandardShortCandidateExclusion exclusion in exclusions
            .OrderBy(static item => item.CandidateId.ToString(),
                StringComparer.Ordinal))
        {
            fields.Add("exclusion");
            fields.Add(exclusion.CandidateId.ToString());
            fields.Add(exclusion.Code.ToString());
        }

        foreach (StandardThumbnailPlan thumbnail in thumbnails.OrderBy(
            static item => item.Id.ToString(),
            StringComparer.Ordinal))
        {
            fields.Add("thumbnail");
            fields.Add(thumbnail.Id.ToString());
            fields.Add(OriginField(thumbnail.Origin));
            fields.Add(thumbnail.SourceFrameSequence.ToString(
                CultureInfo.InvariantCulture));
            AddTimeFields(fields, thumbnail.SourcePresentationTime);
        }

        foreach (StandardSubtitleVariantReference subtitle in longSubtitles
            .OrderBy(static item => item.Kind))
        {
            fields.Add("long-subtitle");
            fields.Add(subtitle.Kind.ToString());
            fields.Add(subtitle.ProfileDigest.Hex);
        }

        foreach (StandardArtifactOmission omission in omissions.OrderBy(
            static item => item.ArtifactKind))
        {
            fields.Add("omission");
            fields.Add(omission.ArtifactKind.ToString());
            fields.Add(omission.Code.ToString());
        }

        foreach (StandardCompositionWarning warning in warnings.OrderBy(
            static item => item.Code))
        {
            fields.Add("warning");
            fields.Add(warning.Code.ToString());
        }

        return ComputeDigest("hubradio.standard-composition-plan.v2", fields);
    }

    internal static ContentDigest ComputeProfileBindingDigest(
        BundledRenderProfileDefinition definition)
    {
        RenderProfile profile = definition.Profile;
        var fields = new List<string>
        {
            profile.Id.ToString(),
            profile.Name,
            profile.ProfileDigest.Hex,
            profile.Width.ToString(CultureInfo.InvariantCulture),
            profile.Height.ToString(CultureInfo.InvariantCulture),
            profile.FrameRateNumerator.ToString(CultureInfo.InvariantCulture),
            profile.FrameRateDenominator.ToString(CultureInfo.InvariantCulture),
            profile.LayoutSpecId.ToString(),
            profile.TemplateId.ToString(),
            profile.TemplateVersionDigest.Hex,
            profile.VideoCodec,
            profile.AudioCodec,
            profile.SubtitleBurnIn.ToString(CultureInfo.InvariantCulture),
            definition.LayoutDigest.Hex,
            definition.TemplateReference.Id.ToString(),
            definition.TemplateReference.Name,
            definition.TemplateReference.VersionDigest.Hex,
            definition.PixelFormatEvidence,
            definition.PlatformSafeZonesMeasured.ToString(
                CultureInfo.InvariantCulture),
            definition.EvidenceSource,
            definition.TemplateStatus,
            definition.RequiredExternalBrandAssets.ToString(
                CultureInfo.InvariantCulture),
        };
        fields.Add("regions");
        fields.Add(definition.Layout.Regions.Count.ToString(
            CultureInfo.InvariantCulture));
        foreach (LayoutRegion region in definition.Layout.Regions
            .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            fields.Add("region");
            fields.Add(region.Name);
            fields.Add(region.Role.ToString());
            AddRectFields(fields, region.Rect);
        }

        fields.Add("safe-zones");
        fields.Add(definition.Layout.SafeZones.Count.ToString(
            CultureInfo.InvariantCulture));
        foreach (SafeZone safeZone in definition.Layout.SafeZones
            .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            fields.Add("safe-zone");
            fields.Add(safeZone.Name);
            AddRectFields(fields, safeZone.Rect);
        }

        fields.Add("capabilities");
        fields.Add(definition.Capabilities.Count.ToString(
            CultureInfo.InvariantCulture));
        foreach (string capability in definition.Capabilities.Order(
            StringComparer.Ordinal))
        {
            fields.Add("capability");
            fields.Add(capability);
        }

        fields.Add("unmeasured");
        fields.Add(definition.Unmeasured.Count.ToString(
            CultureInfo.InvariantCulture));
        foreach (string unmeasured in definition.Unmeasured.Order(
            StringComparer.Ordinal))
        {
            fields.Add("unmeasured-item");
            fields.Add(unmeasured);
        }

        return ComputeDigest("hubradio.standard-profile-binding.v1", fields);
    }

    internal static ContentDigest ComputeMusicPlanningDigest(
        MusicPlanningResult musicPlanning)
    {
        ArgumentNullException.ThrowIfNull(musicPlanning);
        var fields = new List<string>
        {
            musicPlanning.CatalogDigest.Hex,
        };
        foreach (MusicCueAssignment assignment in musicPlanning.Assignments
            .OrderBy(static item => item.CueDigest.Hex, StringComparer.Ordinal))
        {
            fields.Add("assignment");
            fields.Add(assignment.CueDigest.Hex);
            fields.Add(assignment.TrackDigest?.Hex ?? string.Empty);
            fields.Add(assignment.Reason.ToString());
            fields.Add(assignment.RelativeGainDecibels?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty);
            fields.Add(assignment.Loop.ToString(CultureInfo.InvariantCulture));
            fields.Add(assignment.Stretch.ToString(CultureInfo.InvariantCulture));
        }

        foreach (MusicCatalogWarning warning in musicPlanning.Warnings.Order())
        {
            fields.Add("warning");
            fields.Add(warning.ToString());
        }

        foreach (MusicPolicyEvaluation evaluation in
            musicPlanning.PolicyEvaluations.OrderBy(
                static item => item.Domain))
        {
            fields.Add("policy");
            fields.Add(evaluation.Domain.ToString());
            fields.Add(evaluation.Status.ToString());
        }

        return ComputeDigest("hubradio.standard-music-planning.v1", fields);
    }

    internal static ContentDigest ComputeProjectSnapshotDigest(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ContentDigest.Compute(StrictUtf8.GetBytes(
            CoreProjectSnapshotJson.WriteProject(project)));
    }

    internal static ContentDigest ComputeProjectStoreStateDigest(
        Project project,
        StandardProjectStoreState state)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(state);
        return ComputeDigest(
            "hubradio.standard-project-store-state.v1",
            [
                project.Id.ToString(),
                state.Revision.ToString(CultureInfo.InvariantCulture),
                state.PayloadDigest.Hex,
                state.UpdatedAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                project.PublicationScope.ToString(),
                project.Deadline?.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture) ?? string.Empty,
            ]);
    }

    internal static ContentDigest ComputeDigest(
        string domain,
        IEnumerable<string> fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendDigestField(hash, tag: 1, domain);
        foreach (string field in fields)
        {
            AppendDigestField(hash, tag: 2, field);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static EntityId<TTag> DeterministicId<TTag>(
        ContentDigest seed,
        string label)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendDigestField(hash, tag: 1, "hubradio.standard-composition-id.v1");
        AppendDigestField(hash, tag: 2, seed.Hex);
        AppendDigestField(hash, tag: 3, label);
        byte[] bytes = hash.GetHashAndReset();
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return EntityId<TTag>.From(new Guid(guidBytes));
    }

    private static void AddLayoutFields(
        List<string> fields,
        ManualLayoutPlan layout)
    {
        fields.Add(layout.Mode.ToString());
        fields.Add(layout.DerivedLayoutSpec.Id.ToString());
        fields.Add(layout.DerivedLayoutSpec.OutputAspectWidth.ToString(
            CultureInfo.InvariantCulture));
        fields.Add(layout.DerivedLayoutSpec.OutputAspectHeight.ToString(
            CultureInfo.InvariantCulture));
        foreach (ManualSourcePlacement placement in layout.SourcePlacements)
        {
            fields.Add(placement.SourceAssetId.ToString());
            fields.Add(OriginField(placement.Origin));
            fields.Add(placement.SourceDisplayGeometry.Width.ToString(
                CultureInfo.InvariantCulture));
            fields.Add(placement.SourceDisplayGeometry.Height.ToString(
                CultureInfo.InvariantCulture));
            AddRectFields(fields, placement.Crop.Rect);
            AddRectFields(fields, placement.SubjectRegion.Rect);
            fields.Add(placement.Destination.Slot.ToString());
            fields.Add(placement.Destination.Region.Name);
            fields.Add(placement.Destination.Region.Role.ToString());
            AddRectFields(fields, placement.Destination.Region.Rect);
            fields.Add(placement.SemanticRole.ToString());
            fields.Add(placement.Treatment.ToString());
        }

        if (layout.TemplateReference is not null)
        {
            fields.Add(layout.TemplateReference.Id.ToString());
            fields.Add(layout.TemplateReference.Name);
            fields.Add(layout.TemplateReference.VersionDigest.Hex);
        }
    }

    private static void AddSourceSpanFields(
        List<string> fields,
        SourceSpan span)
    {
        fields.Add(span.Id.ToString());
        fields.Add(span.AssetId.ToString());
        fields.Add(span.StreamId);
        AddRangeFields(fields, span.Range);
        fields.Add(span.SourceDigest.Hex);
        fields.Add(span.TranscriptRevisionId?.ToString() ?? string.Empty);
        fields.AddRange(span.TranscriptTokenIds.Select(static id => id.ToString()));
    }

    private static void AddRangeFields(List<string> fields, TimeRange range)
    {
        AddTimeFields(fields, range.Start);
        AddTimeFields(fields, range.Duration);
    }

    private static void AddTimeFields(List<string> fields, RationalTime time)
    {
        fields.Add(time.Value.ToString(CultureInfo.InvariantCulture));
        fields.Add(time.Rate.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddRectFields(
        List<string> fields,
        NormalizedRect rect)
    {
        AddRationalFields(fields, rect.X);
        AddRationalFields(fields, rect.Y);
        AddRationalFields(fields, rect.Width);
        AddRationalFields(fields, rect.Height);
    }

    private static void AddRationalFields(
        List<string> fields,
        RationalNumber value)
    {
        fields.Add(value.Numerator.ToString(CultureInfo.InvariantCulture));
        fields.Add(value.Denominator.ToString(CultureInfo.InvariantCulture));
    }

    private static string OriginField(OriginRef origin) =>
        origin.Kind switch
        {
            OriginKind.SourceSpan when origin.SourceSpanId is not null =>
                string.Concat("sourceSpan:", origin.SourceSpanId),
            OriginKind.Asset when origin.AssetId is not null =>
                string.Concat("asset:", origin.AssetId),
            OriginKind.EditorialRecord when origin.EditorialRecordId is not null =>
                string.Concat("editorialRecord:", origin.EditorialRecordId),
            _ => throw new InvalidOperationException(
                "A standard-composition origin is invalid."),
        };

    private static void AppendDigestField(
        IncrementalHash hash,
        byte tag,
        string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        hash.AppendData(header);
        hash.AppendData(bytes);
    }
}
