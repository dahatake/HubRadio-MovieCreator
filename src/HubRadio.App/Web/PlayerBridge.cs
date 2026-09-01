using System.Buffers;
using System.Text;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Web;

internal enum PlayerBridgeStatus
{
    Available,
    Forbidden,
    Unavailable,
    Stale,
    Mismatched,
    InvalidTime,
}

internal enum PlayerMediaAssetKind
{
    ProxyVideo,
    ThumbnailSprite,
    GeneratedThumbnail,
}

internal sealed class PlayerMediaAssetReference
{
    private const string MediaPathPrefix = "/media/v1/";

    private PlayerMediaAssetReference(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> artifactId,
        PlayerMediaAssetKind kind,
        ContentDigest contentDigest)
    {
        ProjectId = projectId;
        ArtifactId = artifactId;
        Kind = kind;
        ContentDigest = contentDigest;
        string kindSegment = kind switch
        {
            PlayerMediaAssetKind.ProxyVideo => "proxy",
            PlayerMediaAssetKind.ThumbnailSprite => "sprite",
            PlayerMediaAssetKind.GeneratedThumbnail => "thumbnail",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown player media asset kind."),
        };
        Value = string.Concat(
            "https://",
            WebViewHost.MediaHostName,
            MediaPathPrefix,
            projectId.ToString(),
            "/",
            kindSegment,
            "/",
            artifactId.ToString(),
            "/",
            contentDigest.Hex);
    }

    internal EntityId<ProjectIdTag> ProjectId { get; }

    internal EntityId<ArtifactIdTag> ArtifactId { get; }

    internal PlayerMediaAssetKind Kind { get; }

    internal ContentDigest ContentDigest { get; }

    internal string Value { get; }

    internal static PlayerMediaAssetReference ProxyVideo(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(contentDigest);
        return new PlayerMediaAssetReference(
            projectId,
            artifactId,
            PlayerMediaAssetKind.ProxyVideo,
            contentDigest);
    }

    internal static PlayerMediaAssetReference ThumbnailSprite(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(contentDigest);
        return new PlayerMediaAssetReference(
            projectId,
            artifactId,
            PlayerMediaAssetKind.ThumbnailSprite,
            contentDigest);
    }

    internal static PlayerMediaAssetReference GeneratedThumbnail(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(contentDigest);
        return new PlayerMediaAssetReference(
            projectId,
            artifactId,
            PlayerMediaAssetKind.GeneratedThumbnail,
            contentDigest);
    }

    internal static bool TryParse(
        string? value,
        out PlayerMediaAssetReference? reference)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !WebViewHost.IsAllowedMediaOrigin(value) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            reference = null;
            return false;
        }

        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6 ||
            !string.Equals(segments[0], "media", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "v1", StringComparison.Ordinal) ||
            !EntityId<ProjectIdTag>.TryParse(
                segments[2],
                out EntityId<ProjectIdTag>? projectId) ||
            !string.Equals(
                segments[2],
                projectId.ToString(),
                StringComparison.Ordinal) ||
            !TryParseKind(segments[3], out PlayerMediaAssetKind kind) ||
            !EntityId<ArtifactIdTag>.TryParse(
                segments[4],
                out EntityId<ArtifactIdTag>? artifactId) ||
            !string.Equals(
                segments[4],
                artifactId.ToString(),
                StringComparison.Ordinal) ||
            !ContentDigest.TryParse(
                segments[5],
                out ContentDigest? contentDigest) ||
            !string.Equals(
                segments[5],
                contentDigest.Hex,
                StringComparison.Ordinal))
        {
            reference = null;
            return false;
        }

        reference = new PlayerMediaAssetReference(
            projectId,
            artifactId,
            kind,
            contentDigest);
        if (!string.Equals(value, reference.Value, StringComparison.Ordinal))
        {
            reference = null;
            return false;
        }

        return true;
    }

    internal static bool IsAllowedValue(string? value) =>
        TryParse(value, out _);

    private static bool TryParseKind(
        string value,
        out PlayerMediaAssetKind kind)
    {
        switch (value)
        {
            case "proxy":
                kind = PlayerMediaAssetKind.ProxyVideo;
                return true;
            case "sprite":
                kind = PlayerMediaAssetKind.ThumbnailSprite;
                return true;
            case "thumbnail":
                kind = PlayerMediaAssetKind.GeneratedThumbnail;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

internal sealed class PlayerBridgeWaveformChannel
{
    internal PlayerBridgeWaveformChannel(
        int channelIndex,
        short minimumSample,
        short maximumSample)
    {
        ChannelIndex = channelIndex;
        MinimumSample = minimumSample;
        MaximumSample = maximumSample;
    }

    internal int ChannelIndex { get; }

    internal short MinimumSample { get; }

    internal short MaximumSample { get; }
}

internal sealed class PlayerBridgeSpriteCell
{
    internal PlayerBridgeSpriteCell(int ordinal, int column, int row)
    {
        Ordinal = ordinal;
        Column = column;
        Row = row;
    }

    internal int Ordinal { get; }

    internal int Column { get; }

    internal int Row { get; }
}

internal sealed class PlayerBridgePosition
{
    internal PlayerBridgePosition(
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        int sourceFrameSequence,
        RationalTime sourcePresentationTime,
        RationalTime? outputPresentationTime)
    {
        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
        OutputPresentationTime = outputPresentationTime;
    }

    internal int ProxyFrameSequence { get; }

    internal RationalTime ProxyPresentationTime { get; }

    internal int SourceFrameSequence { get; }

    internal RationalTime SourcePresentationTime { get; }

    internal RationalTime? OutputPresentationTime { get; }
}

internal sealed class PlayerBridgeFrame
{
    internal PlayerBridgeFrame(
        PlayerBridgePosition position,
        RationalTime proxyDuration,
        IReadOnlyList<PlayerBridgeWaveformChannel> waveformChannels,
        PlayerBridgeSpriteCell? spriteCell)
    {
        Position = position;
        ProxyDuration = proxyDuration;
        WaveformChannels = waveformChannels;
        SpriteCell = spriteCell;
    }

    internal PlayerBridgePosition Position { get; }

    internal RationalTime ProxyDuration { get; }

    internal IReadOnlyList<PlayerBridgeWaveformChannel> WaveformChannels { get; }

    internal PlayerBridgeSpriteCell? SpriteCell { get; }
}

internal sealed class PlayerBridgeSnapshot
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private PlayerBridgeSnapshot(
        ContentDigest evidenceDigest,
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        RationalTime sourcePresentationStart,
        RationalTime sourcePresentationEnd,
        PlayerMediaAssetReference proxyMediaReference,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        RationalTime proxyPresentationStart,
        RationalTime proxyPresentationEnd,
        int waveformSampleRate,
        int waveformChannelCount,
        string? waveformChannelLayout,
        PlayerMediaAssetReference spriteImageReference,
        ContentDigest spriteImageDigest,
        int spriteCellWidth,
        int spriteCellHeight,
        int spriteColumns,
        int spriteRows,
        IReadOnlyList<PlayerBridgeFrame> frames)
    {
        EvidenceDigest = evidenceDigest;
        SourceAssetId = sourceAssetId;
        ProjectId = projectId;
        SourcePresentationStart = sourcePresentationStart;
        SourcePresentationEnd = sourcePresentationEnd;
        ProxyMediaReference = proxyMediaReference;
        ProxyContentDigest = proxyContentDigest;
        ProxyPtsIndexDigest = proxyPtsIndexDigest;
        ProxyPresentationStart = proxyPresentationStart;
        ProxyPresentationEnd = proxyPresentationEnd;
        WaveformSampleRate = waveformSampleRate;
        WaveformChannelCount = waveformChannelCount;
        WaveformChannelLayout = waveformChannelLayout;
        SpriteImageReference = spriteImageReference;
        SpriteImageDigest = spriteImageDigest;
        SpriteCellWidth = spriteCellWidth;
        SpriteCellHeight = spriteCellHeight;
        SpriteColumns = spriteColumns;
        SpriteRows = spriteRows;
        Frames = frames;
    }

    internal ContentDigest EvidenceDigest { get; }

    internal EntityId<AssetIdTag> SourceAssetId { get; }

    internal EntityId<ProjectIdTag> ProjectId { get; }

    internal RationalTime SourcePresentationStart { get; }

    internal RationalTime SourcePresentationEnd { get; }

    internal PlayerMediaAssetReference ProxyMediaReference { get; }

    internal ContentDigest ProxyContentDigest { get; }

    internal ContentDigest ProxyPtsIndexDigest { get; }

    internal RationalTime ProxyPresentationStart { get; }

    internal RationalTime ProxyPresentationEnd { get; }

    internal int WaveformSampleRate { get; }

    internal int WaveformChannelCount { get; }

    internal string? WaveformChannelLayout { get; }

    internal PlayerMediaAssetReference SpriteImageReference { get; }

    internal ContentDigest SpriteImageDigest { get; }

    internal int SpriteCellWidth { get; }

    internal int SpriteCellHeight { get; }

    internal int SpriteColumns { get; }

    internal int SpriteRows { get; }

    internal IReadOnlyList<PlayerBridgeFrame> Frames { get; }

    internal static PlayerBridgeSnapshot Create(
        ProxyFrameMap frameMap,
        PtsIndex proxyPtsIndex,
        GeneratedPreviewAssets previewAssets,
        PlayerMediaAssetReference proxyMediaReference,
        PlayerMediaAssetReference spriteImageReference)
    {
        ArgumentNullException.ThrowIfNull(frameMap);
        ArgumentNullException.ThrowIfNull(proxyPtsIndex);
        ArgumentNullException.ThrowIfNull(previewAssets);
        ArgumentNullException.ThrowIfNull(proxyMediaReference);
        ArgumentNullException.ThrowIfNull(spriteImageReference);

        PreviewWaveformIndex waveform = previewAssets.WaveformIndex;
        ThumbnailSpriteIndex sprite = previewAssets.SpriteIndex;
        ContentDigest proxyPtsIndexDigest = ContentDigest.Compute(
            StrictUtf8.GetBytes(proxyPtsIndex.CanonicalJson));
        bool identityMatches =
            frameMap.SourceAssetId == proxyPtsIndex.CandidateId &&
            frameMap.SourceAssetId == waveform.SourceAssetId &&
            frameMap.SourceAssetId == sprite.SourceAssetId &&
            frameMap.ProjectId == proxyPtsIndex.ProjectId &&
            frameMap.ProjectId == waveform.ProjectId &&
            frameMap.ProjectId == sprite.ProjectId &&
            frameMap.ProxyContentDigest == proxyPtsIndex.ContentDigest &&
            frameMap.ProxyContentDigest == waveform.ProxyContentDigest &&
            frameMap.ProxyContentDigest == sprite.ProxyContentDigest &&
            frameMap.ProxyPtsIndexDigest == proxyPtsIndexDigest &&
            waveform.ProxyPtsIndexDigest == proxyPtsIndexDigest &&
            sprite.ProxyPtsIndexDigest == proxyPtsIndexDigest &&
            waveform.ProxyArtifactId == sprite.ProxyArtifactId &&
            proxyMediaReference.Kind == PlayerMediaAssetKind.ProxyVideo &&
            proxyMediaReference.ProjectId == frameMap.ProjectId &&
            proxyMediaReference.ArtifactId == waveform.ProxyArtifactId &&
            proxyMediaReference.ContentDigest == frameMap.ProxyContentDigest &&
            spriteImageReference.Kind == PlayerMediaAssetKind.ThumbnailSprite &&
            spriteImageReference.ProjectId == frameMap.ProjectId &&
            spriteImageReference.ArtifactId == sprite.SpriteImageArtifactId &&
            spriteImageReference.ContentDigest == sprite.SpriteImageDigest &&
            previewAssets.WaveformIndexArtifact.ProjectId == frameMap.ProjectId &&
            previewAssets.WaveformIndexArtifact.Kind == ArtifactKind.Preview &&
            previewAssets.SpriteImageArtifact.ProjectId == frameMap.ProjectId &&
            previewAssets.SpriteImageArtifact.Kind == ArtifactKind.Preview &&
            previewAssets.SpriteImageArtifact.Id == sprite.SpriteImageArtifactId &&
            previewAssets.SpriteImageArtifact.ContentDigest == sprite.SpriteImageDigest &&
            previewAssets.SpriteIndexArtifact.ProjectId == frameMap.ProjectId &&
            previewAssets.SpriteIndexArtifact.Kind == ArtifactKind.Preview &&
            frameMap.Entries.Count == proxyPtsIndex.Entries.Count &&
            frameMap.Entries.Count == waveform.Frames.Count;
        if (!identityMatches)
        {
            throw new InvalidOperationException(
                "The player evidence does not identify one proxy timeline.");
        }

        Dictionary<int, ThumbnailSpriteCell> spriteCells = [];
        foreach (ThumbnailSpriteCell cell in sprite.Cells)
        {
            if ((uint)cell.ProxyFrameSequence >=
                    (uint)proxyPtsIndex.Entries.Count ||
                !spriteCells.TryAdd(cell.ProxyFrameSequence, cell))
            {
                throw new InvalidOperationException(
                    "The player sprite index does not match the proxy timeline.");
            }

            PtsIndexEntry indexed = proxyPtsIndex.Entries[cell.ProxyFrameSequence];
            if (cell.ProxyPresentationTime != indexed.PresentationTime ||
                cell.ProxyDuration != indexed.Duration)
            {
                throw new InvalidOperationException(
                    "The player sprite index does not match the proxy timeline.");
            }
        }

        var frames = new PlayerBridgeFrame[frameMap.Entries.Count];
        for (int index = 0; index < frames.Length; index++)
        {
            ProxyFrameMapEntry mapped = frameMap.Entries[index];
            PtsIndexEntry indexed = proxyPtsIndex.Entries[index];
            WaveformFrame waveformFrame = waveform.Frames[index];
            if (mapped.ProxyFrameSequence != index ||
                indexed.Sequence != index ||
                waveformFrame.ProxyFrameSequence != index ||
                mapped.ProxyPresentationTime != indexed.PresentationTime ||
                waveformFrame.ProxyPresentationTime != indexed.PresentationTime ||
                waveformFrame.ProxyDuration != indexed.Duration ||
                mapped.SourcePresentationTime < frameMap.SourcePresentationStart ||
                mapped.SourcePresentationTime >= frameMap.SourcePresentationEnd ||
                (index > 0 &&
                    proxyPtsIndex.Entries[index - 1].PresentationTime +
                        proxyPtsIndex.Entries[index - 1].Duration !=
                    indexed.PresentationTime))
            {
                throw new InvalidOperationException(
                    "The player indices do not match the proxy timeline.");
            }

            PlayerBridgeWaveformChannel[] channels = waveformFrame.Channels
                .Select(static channel => new PlayerBridgeWaveformChannel(
                    channel.ChannelIndex,
                    channel.MinimumSample,
                    channel.MaximumSample))
                .ToArray();
            PlayerBridgeSpriteCell? playerSpriteCell =
                spriteCells.TryGetValue(index, out ThumbnailSpriteCell? cell)
                    ? new PlayerBridgeSpriteCell(
                        cell.Ordinal,
                        cell.Column,
                        cell.Row)
                    : null;
            frames[index] = new PlayerBridgeFrame(
                new PlayerBridgePosition(
                    index,
                    mapped.ProxyPresentationTime,
                    mapped.SourceFrameSequence,
                    mapped.SourcePresentationTime,
                    outputPresentationTime: null),
                indexed.Duration,
                Array.AsReadOnly(channels),
                playerSpriteCell);
        }

        PtsIndexEntry firstProxyFrame = proxyPtsIndex.Entries[0];
        PtsIndexEntry lastProxyFrame = proxyPtsIndex.Entries[^1];
        ContentDigest evidenceDigest = ComputeEvidenceDigest(
            frameMap,
            proxyPtsIndexDigest,
            previewAssets);
        return new PlayerBridgeSnapshot(
            evidenceDigest,
            frameMap.SourceAssetId,
            frameMap.ProjectId,
            frameMap.SourcePresentationStart,
            frameMap.SourcePresentationEnd,
            proxyMediaReference,
            frameMap.ProxyContentDigest,
            proxyPtsIndexDigest,
            firstProxyFrame.PresentationTime,
            lastProxyFrame.PresentationTime + lastProxyFrame.Duration,
            waveform.SampleRate,
            waveform.ChannelCount,
            waveform.ChannelLayout,
            spriteImageReference,
            sprite.SpriteImageDigest,
            sprite.CellWidth,
            sprite.CellHeight,
            sprite.Columns,
            sprite.Rows,
            Array.AsReadOnly(frames));
    }

    private static ContentDigest ComputeEvidenceDigest(
        ProxyFrameMap frameMap,
        ContentDigest proxyPtsIndexDigest,
        GeneratedPreviewAssets previewAssets)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("player-bridge-evidence-v1");
        writer.WriteStringValue(ContentDigest.Compute(
            StrictUtf8.GetBytes(frameMap.CanonicalJson)).Hex);
        writer.WriteStringValue(proxyPtsIndexDigest.Hex);
        writer.WriteStringValue(
            previewAssets.WaveformIndexArtifact.ContentDigest.Hex);
        writer.WriteStringValue(
            previewAssets.SpriteIndexArtifact.ContentDigest.Hex);
        writer.WriteStringValue(
            previewAssets.SpriteImageArtifact.ContentDigest.Hex);
        writer.WriteEndArray();
        writer.Flush();
        return ContentDigest.Compute(buffer.WrittenSpan);
    }
}

internal sealed class PlayerBridgeReadResult
{
    private PlayerBridgeReadResult(
        PlayerBridgeStatus status,
        PlayerBridgeSnapshot? snapshot)
    {
        if ((status == PlayerBridgeStatus.Available) != (snapshot is not null) ||
            status == PlayerBridgeStatus.InvalidTime)
        {
            throw new ArgumentException(
                "The player read result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
    }

    internal PlayerBridgeStatus Status { get; }

    internal PlayerBridgeSnapshot? Snapshot { get; }

    internal static PlayerBridgeReadResult Available(
        PlayerBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new PlayerBridgeReadResult(
            PlayerBridgeStatus.Available,
            snapshot);
    }

    internal static PlayerBridgeReadResult Forbidden() =>
        new(PlayerBridgeStatus.Forbidden, snapshot: null);

    internal static PlayerBridgeReadResult Unavailable() =>
        new(PlayerBridgeStatus.Unavailable, snapshot: null);

    internal static PlayerBridgeReadResult Stale() =>
        new(PlayerBridgeStatus.Stale, snapshot: null);

    internal static PlayerBridgeReadResult Mismatched() =>
        new(PlayerBridgeStatus.Mismatched, snapshot: null);
}

internal static class PlayerBridgeEvidence
{
    internal static PlayerBridgeReadResult CreateReadResult(
        ProxyFrameMap? frameMap,
        PtsIndex? proxyPtsIndex,
        GeneratedPreviewAssets? previewAssets,
        PlayerMediaAssetReference? proxyMediaReference,
        PlayerMediaAssetReference? spriteImageReference,
        ContentDigest? expectedEvidenceDigest = null)
    {
        if (frameMap is null ||
            proxyPtsIndex is null ||
            previewAssets is null ||
            proxyMediaReference is null ||
            spriteImageReference is null)
        {
            return PlayerBridgeReadResult.Unavailable();
        }

        PlayerBridgeSnapshot snapshot;
        try
        {
            snapshot = PlayerBridgeSnapshot.Create(
                frameMap,
                proxyPtsIndex,
                previewAssets,
                proxyMediaReference,
                spriteImageReference);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException)
        {
            return PlayerBridgeReadResult.Mismatched();
        }

        if (expectedEvidenceDigest is not null &&
            expectedEvidenceDigest != snapshot.EvidenceDigest)
        {
            return PlayerBridgeReadResult.Stale();
        }

        return PlayerBridgeReadResult.Available(snapshot);
    }
}

internal interface IPlayerBridgeReader
{
    Task<PlayerBridgeReadResult> ReadAsync(
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken);
}

internal sealed class UnavailablePlayerBridgeReader : IPlayerBridgeReader
{
    internal static UnavailablePlayerBridgeReader Instance { get; } = new();

    private UnavailablePlayerBridgeReader()
    {
    }

    public Task<PlayerBridgeReadResult> ReadAsync(
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlayerBridgeReadResult.Unavailable());
    }
}

internal sealed class PlayerBridgeRequestHandler
{
    private readonly IPlayerBridgeReader reader;
    private readonly IPlayerAccessAuthorization authorization;

    internal PlayerBridgeRequestHandler(
        IPlayerBridgeReader reader,
        IPlayerAccessAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(authorization);
        this.reader = reader;
        this.authorization = authorization;
    }

    internal async Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == BridgeMethod.Ping)
        {
            return BridgeProtocol.CreatePingResponse(request);
        }

        PlayerBridgeReadResult result;
        try
        {
            authorization.DemandPreview();
            EntityId<JobIdTag> jobId = request.JobId
                ?? throw new InvalidOperationException(
                    "A player bridge request is missing its job identity.");
            result = await reader.ReadAsync(
                jobId,
                cancellationToken).ConfigureAwait(false) ??
                PlayerBridgeReadResult.Unavailable();
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested &&
                exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch (AuthorizationException)
        {
            result = PlayerBridgeReadResult.Forbidden();
        }
        catch (Exception)
        {
            result = PlayerBridgeReadResult.Unavailable();
        }

        return request.Method switch
        {
            BridgeMethod.PlayerRead =>
                PlayerBridgeJson.WriteReadResponse(request, result),
            BridgeMethod.PlayerSeek =>
                HandleSeek(request, result),
            _ => throw new InvalidOperationException(
                "The parsed bridge request method is unsupported."),
        };
    }

    private static string HandleSeek(
        BridgeRequest request,
        PlayerBridgeReadResult readResult)
    {
        PlayerBridgeStatus status = readResult.Status;
        PlayerBridgePosition? position = null;
        if (status == PlayerBridgeStatus.Available)
        {
            PlayerBridgeSnapshot snapshot = readResult.Snapshot!;
            if (request.EvidenceDigest != snapshot.EvidenceDigest)
            {
                status = PlayerBridgeStatus.Stale;
            }
            else if (request.ProxyFrameSequence is not int frameSequence ||
                (uint)frameSequence >= (uint)snapshot.Frames.Count)
            {
                status = PlayerBridgeStatus.InvalidTime;
            }
            else
            {
                position = snapshot.Frames[frameSequence].Position;
            }
        }

        return PlayerBridgeJson.WriteSeekResponse(
            request,
            status,
            position);
    }
}

internal static class PlayerBridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteReadResponse(
        BridgeRequest request,
        PlayerBridgeReadResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteReadResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == PlayerBridgeStatus.Available)
        {
            return WriteReadResponseCore(
                request,
                PlayerBridgeReadResult.Unavailable());
        }
    }

    private static string WriteReadResponseCore(
        BridgeRequest request,
        PlayerBridgeReadResult result)
    {
        var buffer = new BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using Utf8JsonWriter writer = new(buffer);
        WriteEnvelopeStart(writer, request);
        writer.WriteStartObject("result");
        writer.WriteString("status", GetStatusText(result.Status));
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

    internal static string WriteSeekResponse(
        BridgeRequest request,
        PlayerBridgeStatus status,
        PlayerBridgePosition? position)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((status == PlayerBridgeStatus.Available) != (position is not null))
        {
            throw new ArgumentException(
                "The player seek response is inconsistent.",
                nameof(status));
        }

        var buffer = new BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using Utf8JsonWriter writer = new(buffer);
        WriteEnvelopeStart(writer, request);
        writer.WriteStartObject("result");
        writer.WriteString("status", GetStatusText(status));
        writer.WritePropertyName("position");
        if (position is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WritePosition(writer, position);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteEnvelopeStart(
        Utf8JsonWriter writer,
        BridgeRequest request)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString(
            "method",
            BridgeProtocol.GetMethodName(request.Method));
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        PlayerBridgeSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("evidenceDigest", snapshot.EvidenceDigest.Hex);
        writer.WriteString("sourceAssetId", snapshot.SourceAssetId.ToString());
        writer.WriteString("projectId", snapshot.ProjectId.ToString());
        writer.WritePropertyName("sourcePresentationStart");
        WriteRationalTime(writer, snapshot.SourcePresentationStart);
        writer.WritePropertyName("sourcePresentationEnd");
        WriteRationalTime(writer, snapshot.SourcePresentationEnd);
        writer.WriteStartObject("proxy");
        writer.WriteString(
            "mediaReference",
            snapshot.ProxyMediaReference.Value);
        writer.WriteString("contentDigest", snapshot.ProxyContentDigest.Hex);
        writer.WriteString("ptsIndexDigest", snapshot.ProxyPtsIndexDigest.Hex);
        writer.WritePropertyName("presentationStart");
        WriteRationalTime(writer, snapshot.ProxyPresentationStart);
        writer.WritePropertyName("presentationEnd");
        WriteRationalTime(writer, snapshot.ProxyPresentationEnd);
        writer.WriteEndObject();
        writer.WriteStartObject("waveform");
        writer.WriteNumber("sampleRate", snapshot.WaveformSampleRate);
        writer.WriteNumber("channelCount", snapshot.WaveformChannelCount);
        if (snapshot.WaveformChannelLayout is null)
        {
            writer.WriteNull("channelLayout");
        }
        else
        {
            writer.WriteString(
                "channelLayout",
                snapshot.WaveformChannelLayout);
        }

        writer.WriteEndObject();
        writer.WriteStartObject("sprite");
        writer.WriteString(
            "imageReference",
            snapshot.SpriteImageReference.Value);
        writer.WriteString("imageDigest", snapshot.SpriteImageDigest.Hex);
        writer.WriteNumber("cellWidth", snapshot.SpriteCellWidth);
        writer.WriteNumber("cellHeight", snapshot.SpriteCellHeight);
        writer.WriteNumber("columns", snapshot.SpriteColumns);
        writer.WriteNumber("rows", snapshot.SpriteRows);
        writer.WriteEndObject();
        writer.WriteStartArray("frames");
        foreach (PlayerBridgeFrame frame in snapshot.Frames)
        {
            writer.WriteStartObject();
            WritePositionProperties(writer, frame.Position);
            writer.WritePropertyName("proxyDuration");
            WriteRationalTime(writer, frame.ProxyDuration);
            writer.WriteStartArray("waveformChannels");
            foreach (PlayerBridgeWaveformChannel channel in
                frame.WaveformChannels)
            {
                writer.WriteStartObject();
                writer.WriteNumber("channelIndex", channel.ChannelIndex);
                writer.WriteNumber("minimumSample", channel.MinimumSample);
                writer.WriteNumber("maximumSample", channel.MaximumSample);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("spriteCell");
            if (frame.SpriteCell is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", frame.SpriteCell.Ordinal);
                writer.WriteNumber("column", frame.SpriteCell.Column);
                writer.WriteNumber("row", frame.SpriteCell.Row);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePosition(
        Utf8JsonWriter writer,
        PlayerBridgePosition position)
    {
        writer.WriteStartObject();
        WritePositionProperties(writer, position);
        writer.WriteEndObject();
    }

    private static void WritePositionProperties(
        Utf8JsonWriter writer,
        PlayerBridgePosition position)
    {
        writer.WriteNumber(
            "proxyFrameSequence",
            position.ProxyFrameSequence);
        writer.WritePropertyName("proxyPresentationTime");
        WriteRationalTime(writer, position.ProxyPresentationTime);
        writer.WriteNumber(
            "sourceFrameSequence",
            position.SourceFrameSequence);
        writer.WritePropertyName("sourcePresentationTime");
        WriteRationalTime(writer, position.SourcePresentationTime);
        writer.WritePropertyName("outputPresentationTime");
        if (position.OutputPresentationTime is RationalTime outputTime)
        {
            WriteRationalTime(writer, outputTime);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("rate", value.Rate.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static string GetStatusText(PlayerBridgeStatus status) =>
        status switch
        {
            PlayerBridgeStatus.Available => "available",
            PlayerBridgeStatus.Forbidden => "forbidden",
            PlayerBridgeStatus.Unavailable => "unavailable",
            PlayerBridgeStatus.Stale => "stale",
            PlayerBridgeStatus.Mismatched => "mismatched",
            PlayerBridgeStatus.InvalidTime => "invalidTime",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown player bridge status."),
        };

    internal sealed class BoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> buffer = new();
        private readonly int maximumBytes;

        internal BoundedBufferWriter(int maximumBytes)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            this.maximumBytes = maximumBytes;
        }

        internal ReadOnlySpan<byte> WrittenSpan => buffer.WrittenSpan;

        public void Advance(int count)
        {
            if (count < 0 || count > maximumBytes - buffer.WrittenCount)
            {
                throw new InvalidOperationException(
                    "The bridge response exceeds the supported size.");
            }

            buffer.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            int requested = GetRequestedSize(sizeHint);
            Memory<byte> memory = buffer.GetMemory(requested);
            return memory[..Math.Min(memory.Length, RemainingBytes)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int requested = GetRequestedSize(sizeHint);
            Span<byte> span = buffer.GetSpan(requested);
            return span[..Math.Min(span.Length, RemainingBytes)];
        }

        private int RemainingBytes => maximumBytes - buffer.WrittenCount;

        private int GetRequestedSize(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            int requested = Math.Max(sizeHint, 1);
            if (requested > RemainingBytes)
            {
                throw new InvalidOperationException(
                    "The bridge response exceeds the supported size.");
            }

            return requested;
        }
    }
}