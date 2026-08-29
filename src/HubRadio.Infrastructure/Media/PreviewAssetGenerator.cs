using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

public sealed class PreviewAssetGenerationProfile
{
    // These values version a deterministic preview-cache representation. They are
    // not perceptual-quality or candidate-ranking acceptance thresholds.
    internal const string CurrentVersion = "proxy-preview-assets-v1";
    internal const int WaveformSampleRate = 8000;
    internal const int ThumbnailWidth = 160;
    internal const int ThumbnailHeight = 90;
    internal const int MaximumThumbnailCount = 100;
    internal const int MaximumColumns = 10;
    internal const string ScaleFlags = "bilinear";

    internal PreviewAssetGenerationProfile()
    {
        Version = CurrentVersion;
        WaveformSamplesPerSecond = WaveformSampleRate;
        SpriteCellWidth = ThumbnailWidth;
        SpriteCellHeight = ThumbnailHeight;
        MaximumSpriteCells = MaximumThumbnailCount;
        MaximumSpriteColumns = MaximumColumns;
    }

    public string Version { get; }

    public int WaveformSamplesPerSecond { get; }

    public int SpriteCellWidth { get; }

    public int SpriteCellHeight { get; }

    public int MaximumSpriteCells { get; }

    public int MaximumSpriteColumns { get; }
}

public sealed class WaveformChannelExtrema
{
    internal WaveformChannelExtrema(
        int channelIndex,
        short minimumSample,
        short maximumSample)
    {
        if (channelIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelIndex),
                "A waveform channel index must not be negative.");
        }

        if (minimumSample > maximumSample)
        {
            throw new ArgumentException(
                "Waveform sample extrema are reversed.",
                nameof(minimumSample));
        }

        ChannelIndex = channelIndex;
        MinimumSample = minimumSample;
        MaximumSample = maximumSample;
    }

    public int ChannelIndex { get; }

    public short MinimumSample { get; }

    public short MaximumSample { get; }
}

public sealed class WaveformFrame
{
    internal WaveformFrame(
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        RationalTime proxyDuration,
        IReadOnlyList<WaveformChannelExtrema> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (proxyFrameSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(proxyFrameSequence),
                "A waveform proxy frame sequence must not be negative.");
        }

        if (proxyDuration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(proxyDuration),
                "A waveform proxy frame duration must be positive.");
        }

        var copiedChannels = new WaveformChannelExtrema[channels.Count];
        for (int index = 0; index < copiedChannels.Length; index++)
        {
            WaveformChannelExtrema channel = channels[index]
                ?? throw new ArgumentException(
                    "Waveform channels must not contain null values.",
                    nameof(channels));
            if (channel.ChannelIndex != index)
            {
                throw new ArgumentException(
                    "Waveform channel indices must be contiguous and zero-based.",
                    nameof(channels));
            }

            copiedChannels[index] = channel;
        }

        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        ProxyDuration = proxyDuration;
        Channels = Array.AsReadOnly(copiedChannels);
    }

    public int ProxyFrameSequence { get; }

    public RationalTime ProxyPresentationTime { get; }

    public RationalTime ProxyDuration { get; }

    public IReadOnlyList<WaveformChannelExtrema> Channels { get; }
}

public sealed class PreviewWaveformIndex
{
    public const string CurrentSchemaVersion = "1.0.0";

    internal PreviewWaveformIndex(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        string profileVersion,
        int sampleRate,
        int channelCount,
        string? channelLayout,
        IReadOnlyList<WaveformFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(proxyArtifactId);
        ArgumentNullException.ThrowIfNull(proxyContentDigest);
        ArgumentNullException.ThrowIfNull(proxyPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(profileVersion);
        ArgumentNullException.ThrowIfNull(frames);
        if (!string.Equals(
                profileVersion,
                profileVersion.Trim(),
                StringComparison.Ordinal) ||
            profileVersion.Length == 0)
        {
            throw new ArgumentException(
                "A waveform profile version must be trimmed and nonblank.",
                nameof(profileVersion));
        }

        if ((channelCount == 0 && (sampleRate != 0 || channelLayout is not null)) ||
            (channelCount > 0 && (sampleRate <= 0 || channelLayout is null)) ||
            channelCount < 0 ||
            (channelLayout is not null &&
                (channelLayout.Length == 0 ||
                    !string.Equals(
                        channelLayout,
                        channelLayout.Trim(),
                        StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "Waveform channel metadata is inconsistent.",
                nameof(channelCount));
        }

        WaveformFrame[] copiedFrames = new WaveformFrame[frames.Count];
        WaveformFrame? previous = null;
        for (int index = 0; index < copiedFrames.Length; index++)
        {
            WaveformFrame frame = frames[index]
                ?? throw new ArgumentException(
                    "Waveform frames must not contain null values.",
                    nameof(frames));
            if (frame.ProxyFrameSequence != index ||
                frame.Channels.Count != channelCount ||
                (previous is not null &&
                    frame.ProxyPresentationTime <=
                        previous.ProxyPresentationTime))
            {
                throw new ArgumentException(
                    "Waveform frames are inconsistent.",
                    nameof(frames));
            }

            copiedFrames[index] = frame;
            previous = frame;
        }

        if (copiedFrames.Length == 0)
        {
            throw new ArgumentException(
                "A waveform index must contain at least one proxy frame.",
                nameof(frames));
        }

        SourceAssetId = sourceAssetId;
        ProjectId = projectId;
        ProxyArtifactId = proxyArtifactId;
        ProxyContentDigest = proxyContentDigest;
        ProxyPtsIndexDigest = proxyPtsIndexDigest;
        ProfileVersion = profileVersion;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        ChannelLayout = channelLayout;
        Frames = Array.AsReadOnly(copiedFrames);
        CanonicalJson = PreviewAssetJson.WriteWaveform(this);
    }

    public string SchemaVersion => CurrentSchemaVersion;

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<ArtifactIdTag> ProxyArtifactId { get; }

    public ContentDigest ProxyContentDigest { get; }

    public ContentDigest ProxyPtsIndexDigest { get; }

    public string ProfileVersion { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public string? ChannelLayout { get; }

    public IReadOnlyList<WaveformFrame> Frames { get; }

    public string CanonicalJson { get; }
}

public sealed class ThumbnailSpriteCell
{
    internal ThumbnailSpriteCell(
        int ordinal,
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        RationalTime proxyDuration,
        int column,
        int row)
    {
        if (ordinal < 0 || proxyFrameSequence < 0 || column < 0 || row < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                "Sprite indices must not be negative.");
        }

        if (proxyDuration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(proxyDuration),
                "A sprite cell duration must be positive.");
        }

        Ordinal = ordinal;
        ProxyFrameSequence = proxyFrameSequence;
        ProxyPresentationTime = proxyPresentationTime;
        ProxyDuration = proxyDuration;
        Column = column;
        Row = row;
    }

    public int Ordinal { get; }

    public int ProxyFrameSequence { get; }

    public RationalTime ProxyPresentationTime { get; }

    public RationalTime ProxyDuration { get; }

    public int Column { get; }

    public int Row { get; }
}

public sealed class ThumbnailSpriteIndex
{
    public const string CurrentSchemaVersion = "1.0.0";

    internal ThumbnailSpriteIndex(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        ContentDigest spriteImageDigest,
        string profileVersion,
        int cellWidth,
        int cellHeight,
        int columns,
        int rows,
        IReadOnlyList<ThumbnailSpriteCell> cells)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(proxyArtifactId);
        ArgumentNullException.ThrowIfNull(proxyContentDigest);
        ArgumentNullException.ThrowIfNull(proxyPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(spriteImageArtifactId);
        ArgumentNullException.ThrowIfNull(spriteImageDigest);
        ArgumentNullException.ThrowIfNull(profileVersion);
        ArgumentNullException.ThrowIfNull(cells);
        if (profileVersion.Length == 0 ||
            !string.Equals(
                profileVersion,
                profileVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A sprite profile version must be trimmed and nonblank.",
                nameof(profileVersion));
        }

        if (cellWidth <= 0 || cellHeight <= 0 || columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellWidth),
                "Sprite dimensions must be positive.");
        }

        ThumbnailSpriteCell[] copiedCells = new ThumbnailSpriteCell[cells.Count];
        ThumbnailSpriteCell? previous = null;
        for (int index = 0; index < copiedCells.Length; index++)
        {
            ThumbnailSpriteCell cell = cells[index]
                ?? throw new ArgumentException(
                    "Sprite cells must not contain null values.",
                    nameof(cells));
            if (cell.Ordinal != index ||
                cell.Column != index % columns ||
                cell.Row != index / columns ||
                cell.Column >= columns ||
                cell.Row >= rows ||
                (previous is not null &&
                    (cell.ProxyFrameSequence <= previous.ProxyFrameSequence ||
                        cell.ProxyPresentationTime <=
                            previous.ProxyPresentationTime)))
            {
                throw new ArgumentException(
                    "Sprite cells are inconsistent.",
                    nameof(cells));
            }

            copiedCells[index] = cell;
            previous = cell;
        }

        if (copiedCells.Length == 0 ||
            rows != checked((copiedCells.Length + columns - 1) / columns))
        {
            throw new ArgumentException(
                "The sprite grid does not match its cells.",
                nameof(cells));
        }

        SourceAssetId = sourceAssetId;
        ProjectId = projectId;
        ProxyArtifactId = proxyArtifactId;
        ProxyContentDigest = proxyContentDigest;
        ProxyPtsIndexDigest = proxyPtsIndexDigest;
        SpriteImageArtifactId = spriteImageArtifactId;
        SpriteImageDigest = spriteImageDigest;
        ProfileVersion = profileVersion;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Columns = columns;
        Rows = rows;
        Cells = Array.AsReadOnly(copiedCells);
        CanonicalJson = PreviewAssetJson.WriteSprite(this);
    }

    public string SchemaVersion => CurrentSchemaVersion;

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<ArtifactIdTag> ProxyArtifactId { get; }

    public ContentDigest ProxyContentDigest { get; }

    public ContentDigest ProxyPtsIndexDigest { get; }

    public EntityId<ArtifactIdTag> SpriteImageArtifactId { get; }

    public ContentDigest SpriteImageDigest { get; }

    public string ProfileVersion { get; }

    public int CellWidth { get; }

    public int CellHeight { get; }

    public int Columns { get; }

    public int Rows { get; }

    public IReadOnlyList<ThumbnailSpriteCell> Cells { get; }

    public string CanonicalJson { get; }
}

public sealed class GeneratedPreviewAssets
{
    internal GeneratedPreviewAssets(
        StoredArtifact waveformIndexArtifact,
        StoredArtifact spriteImageArtifact,
        StoredArtifact spriteIndexArtifact,
        PreviewWaveformIndex waveformIndex,
        ThumbnailSpriteIndex spriteIndex,
        PreviewAssetGenerationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(waveformIndexArtifact);
        ArgumentNullException.ThrowIfNull(spriteImageArtifact);
        ArgumentNullException.ThrowIfNull(spriteIndexArtifact);
        ArgumentNullException.ThrowIfNull(waveformIndex);
        ArgumentNullException.ThrowIfNull(spriteIndex);
        ArgumentNullException.ThrowIfNull(profile);
        byte[] waveformJson = StrictEncoding.GetBytes(waveformIndex.CanonicalJson);
        byte[] spriteJson = StrictEncoding.GetBytes(spriteIndex.CanonicalJson);
        if (waveformIndexArtifact.Kind != ArtifactKind.Preview ||
            spriteImageArtifact.Kind != ArtifactKind.Preview ||
            spriteIndexArtifact.Kind != ArtifactKind.Preview ||
            waveformIndexArtifact.ProjectId != waveformIndex.ProjectId ||
            spriteImageArtifact.ProjectId != spriteIndex.ProjectId ||
            spriteIndexArtifact.ProjectId != spriteIndex.ProjectId ||
            waveformIndex.SourceAssetId != spriteIndex.SourceAssetId ||
            waveformIndex.ProjectId != spriteIndex.ProjectId ||
            waveformIndex.ProxyArtifactId != spriteIndex.ProxyArtifactId ||
            waveformIndex.ProxyContentDigest != spriteIndex.ProxyContentDigest ||
            waveformIndex.ProxyPtsIndexDigest != spriteIndex.ProxyPtsIndexDigest ||
            !string.Equals(
                waveformIndex.ProfileVersion,
                profile.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                spriteIndex.ProfileVersion,
                profile.Version,
                StringComparison.Ordinal) ||
            (waveformIndex.ChannelCount == 0
                ? waveformIndex.SampleRate != 0
                : waveformIndex.SampleRate != profile.WaveformSamplesPerSecond) ||
            spriteIndex.CellWidth != profile.SpriteCellWidth ||
            spriteIndex.CellHeight != profile.SpriteCellHeight ||
            spriteIndex.Columns > profile.MaximumSpriteColumns ||
            spriteIndex.Rows > profile.MaximumSpriteColumns ||
            spriteIndex.Cells.Count > profile.MaximumSpriteCells ||
            waveformIndexArtifact.ContentDigest !=
                ContentDigest.Compute(waveformJson) ||
            waveformIndexArtifact.ByteSize != waveformJson.LongLength ||
            spriteIndexArtifact.ContentDigest != ContentDigest.Compute(spriteJson) ||
            spriteIndexArtifact.ByteSize != spriteJson.LongLength ||
            spriteImageArtifact.Id != spriteIndex.SpriteImageArtifactId ||
            spriteImageArtifact.ContentDigest != spriteIndex.SpriteImageDigest)
        {
            throw new ArgumentException(
                "Generated preview assets are inconsistent.",
                nameof(spriteIndex));
        }

        WaveformIndexArtifact = waveformIndexArtifact;
        SpriteImageArtifact = spriteImageArtifact;
        SpriteIndexArtifact = spriteIndexArtifact;
        WaveformIndex = waveformIndex;
        SpriteIndex = spriteIndex;
        Profile = profile;
    }

    public StoredArtifact WaveformIndexArtifact { get; }

    public StoredArtifact SpriteImageArtifact { get; }

    public StoredArtifact SpriteIndexArtifact { get; }

    public PreviewWaveformIndex WaveformIndex { get; }

    public ThumbnailSpriteIndex SpriteIndex { get; }

    public PreviewAssetGenerationProfile Profile { get; }

    private static readonly UTF8Encoding StrictEncoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
}

public enum PreviewAssetGenerationFailureReason
{
    InputBindingMismatch,
    RegisteredProxyInvalid,
    ToolVerificationFailed,
    StagingConflict,
    StagingUnavailable,
    ProcessTimedOut,
    WaveformGenerationFailed,
    WaveformOutputInvalid,
    SpriteGenerationFailed,
    SpriteOutputInvalid,
    PromotionFailed,
    RegisteredPreviewInvalid,
    CleanupFailed,
    ArithmeticOverflow,
}

public sealed class PreviewAssetGenerationException : Exception
{
    internal PreviewAssetGenerationException(
        PreviewAssetGenerationFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public PreviewAssetGenerationFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(
        PreviewAssetGenerationFailureReason reason) =>
        reason switch
        {
            PreviewAssetGenerationFailureReason.InputBindingMismatch =>
                "The preview inputs do not identify one generated proxy.",
            PreviewAssetGenerationFailureReason.RegisteredProxyInvalid =>
                "The registered proxy could not be verified for preview generation.",
            PreviewAssetGenerationFailureReason.ToolVerificationFailed =>
                "The preview generation tool could not be verified.",
            PreviewAssetGenerationFailureReason.StagingConflict =>
                "The preview staging destination already exists.",
            PreviewAssetGenerationFailureReason.StagingUnavailable =>
                "The preview staging destination is unavailable.",
            PreviewAssetGenerationFailureReason.ProcessTimedOut =>
                "The preview generation process exceeded its timeout.",
            PreviewAssetGenerationFailureReason.WaveformGenerationFailed =>
                "The preview waveform could not be generated.",
            PreviewAssetGenerationFailureReason.WaveformOutputInvalid =>
                "The generated waveform output is invalid.",
            PreviewAssetGenerationFailureReason.SpriteGenerationFailed =>
                "The thumbnail sprite could not be generated.",
            PreviewAssetGenerationFailureReason.SpriteOutputInvalid =>
                "The generated thumbnail sprite is invalid.",
            PreviewAssetGenerationFailureReason.PromotionFailed =>
                "The preview assets could not be promoted.",
            PreviewAssetGenerationFailureReason.RegisteredPreviewInvalid =>
                "The registered preview assets could not be verified.",
            PreviewAssetGenerationFailureReason.CleanupFailed =>
                "Preview staging cleanup requires reconciliation.",
            PreviewAssetGenerationFailureReason.ArithmeticOverflow =>
                "The preview timeline cannot be represented exactly.",
            _ => "Preview asset generation failed.",
        };
}

public sealed class PreviewAssetGenerator
{
    private const int FileBufferSize = 64 * 1024;
    private const string ToolComponentIdentity =
        "tool.ffmpeg." +
        DecodeValidator.CanonicalPackageId + "." +
        DecodeValidator.CanonicalPackageVersion + ".preview-assets";

    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(DecodeValidator.CanonicalFfmpegSha256);
    private static readonly PreviewAssetGenerationProfile Profile = new();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ArtifactStore _artifactStore;
    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
        _runProcessAsync;
    private readonly Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>>
        _verifyBinaryAsync;
    private readonly Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>>
        _verifyRegisteredAsync;
    private readonly Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
        _promoteAsync;
    private readonly string _workspacePath;
    private readonly string _ffmpegPath;
    private readonly TimeProvider _timeProvider;

    public PreviewAssetGenerator(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string workspacePath,
        string ffmpegPath)
        : this(
            artifactStore,
            GetProcessDelegate(processRunner),
            GetBinaryVerificationDelegate(binaryVerifier),
            GetRegisteredVerificationDelegate(artifactStore, binaryVerifier),
            GetPromotionDelegate(artifactStore),
            workspacePath,
            ffmpegPath,
            TimeProvider.System)
    {
    }

    internal PreviewAssetGenerator(
        ArtifactStore artifactStore,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>> verifyBinaryAsync,
        Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>> verifyRegisteredAsync,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>> promoteAsync,
        string workspacePath,
        string ffmpegPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);
        ArgumentNullException.ThrowIfNull(promoteAsync);

        _artifactStore = artifactStore;
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
        _verifyRegisteredAsync = verifyRegisteredAsync;
        _promoteAsync = promoteAsync;
        _workspacePath = ValidateWorkspacePath(workspacePath);
        _ffmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GeneratedPreviewAssets> GenerateAsync(
        EntityId<ArtifactIdTag> waveformIndexArtifactId,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        EntityId<ArtifactIdTag> spriteIndexArtifactId,
        GeneratedProxy generatedProxy,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waveformIndexArtifactId);
        ArgumentNullException.ThrowIfNull(spriteImageArtifactId);
        ArgumentNullException.ThrowIfNull(spriteIndexArtifactId);
        ArgumentNullException.ThrowIfNull(generatedProxy);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);
        ValidateDistinctOutputIds(
            waveformIndexArtifactId,
            spriteImageArtifactId,
            spriteIndexArtifactId,
            generatedProxy.ProxyArtifact.Id);
        try
        {
            ValidateInputBinding(generatedProxy);
        }
        catch (OverflowException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ArithmeticOverflow);
        }

        EnsureOutputIdsAvailable(
            waveformIndexArtifactId,
            spriteImageArtifactId,
            spriteIndexArtifactId);

        string proxyPath = GetRegisteredProxyPath(generatedProxy.ProxyArtifact);
        using FileStream proxyStream = OpenRegisteredProxy(proxyPath);
        HeldFileSnapshot heldProxy = CaptureHeldProxy(
            proxyStream,
            generatedProxy.ProxyArtifact.ByteSize);
        await VerifyRegisteredExactAsync(
            generatedProxy.ProxyArtifact,
            PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
            reconciliationRequired: true,
            cancellationToken).ConfigureAwait(false);
        ValidateHeldProxy(proxyStream, heldProxy);
        await VerifyFfmpegAsync(cancellationToken).ConfigureAwait(false);
        ValidateHeldProxy(proxyStream, heldProxy);

        PreviewStaging staging = PrepareStaging(
            generatedProxy.ProxyArtifact.ProjectId,
            generatedProxy.ProxyPtsIndex.JobId,
            spriteImageArtifactId,
            cancellationToken);
        int promotedCount = 0;
        try
        {
            ContentDigest proxyPtsIndexDigest = ComputeIndexDigest(
                generatedProxy.ProxyPtsIndex);
            ProbedMediaStream? audioStream = SelectAudioStream(
                generatedProxy.ProxyProbeResult);
            PreviewWaveformIndex waveform = await GenerateWaveformAsync(
                generatedProxy,
                proxyPath,
                proxyPtsIndexDigest,
                audioStream,
                staging,
                timeout,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<PtsIndexEntry> selectedFrames = SelectSpriteFrames(
                generatedProxy.ProxyPtsIndex);
            SpriteGrid grid = CreateSpriteGrid(selectedFrames.Count);
            ProcessRunRequest spriteRequest = CreateSpriteProcessRequest(
                _ffmpegPath,
                proxyPath,
                staging.SpriteImagePath,
                generatedProxy.ProxyPtsIndex.StreamIndex,
                selectedFrames.Select(static frame => frame.Sequence).ToArray(),
                grid.Columns,
                grid.Rows,
                timeout);
            ProcessRunResult spriteResult = await RunProcessAsync(
                spriteRequest,
                PreviewAssetGenerationFailureReason.SpriteGenerationFailed,
                cancellationToken).ConfigureAwait(false);
            ValidateCleanProcessResult(
                spriteResult,
                PreviewAssetGenerationFailureReason.SpriteGenerationFailed);
            ValidateHeldProxy(proxyStream, heldProxy);
            FileSnapshot spriteSnapshot = await ValidatePngAsync(
                staging.SpriteImagePath,
                checked(grid.Columns * Profile.SpriteCellWidth),
                checked(grid.Rows * Profile.SpriteCellHeight),
                cancellationToken).ConfigureAwait(false);
            ThumbnailSpriteIndex sprite = CreateSpriteIndex(
                generatedProxy,
                proxyPtsIndexDigest,
                spriteImageArtifactId,
                spriteSnapshot.Digest,
                grid,
                selectedFrames);

            FileSnapshot waveformIndexSnapshot = await WriteCanonicalIndexAsync(
                staging.WaveformIndexPath,
                waveform.CanonicalJson,
                cancellationToken).ConfigureAwait(false);
            FileSnapshot spriteIndexSnapshot = await WriteCanonicalIndexAsync(
                staging.SpriteIndexPath,
                sprite.CanonicalJson,
                cancellationToken).ConfigureAwait(false);
            ValidateHeldProxy(proxyStream, heldProxy);
            await VerifyRegisteredExactAsync(
                generatedProxy.ProxyArtifact,
                PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
            ValidateHeldProxy(proxyStream, heldProxy);
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset createdAt = _timeProvider.GetUtcNow().ToUniversalTime();
            string relativeRoot = string.Concat(
                generatedProxy.SourceAssetId.ToString(),
                "/",
                generatedProxy.ProxyArtifact.Id.ToString(),
                "/");
            StoredArtifact spriteImageArtifact = await PromoteCommittedAsync(
                new ArtifactPromotionRequest(
                    spriteImageArtifactId,
                    generatedProxy.ProxyArtifact.ProjectId,
                    ArtifactKind.Preview,
                    staging.SpriteImagePath,
                    string.Concat(
                        relativeRoot,
                        spriteImageArtifactId.ToString(),
                        ".sprite.png"),
                    spriteSnapshot.Digest,
                    spriteSnapshot.ByteSize,
                    createdAt)).ConfigureAwait(false);
            promotedCount++;
            StoredArtifact spriteIndexArtifact = await PromoteCommittedAsync(
                new ArtifactPromotionRequest(
                    spriteIndexArtifactId,
                    generatedProxy.ProxyArtifact.ProjectId,
                    ArtifactKind.Preview,
                    staging.SpriteIndexPath,
                    string.Concat(
                        relativeRoot,
                        spriteIndexArtifactId.ToString(),
                        ".sprite-index.json"),
                    spriteIndexSnapshot.Digest,
                    spriteIndexSnapshot.ByteSize,
                    createdAt)).ConfigureAwait(false);
            promotedCount++;
            StoredArtifact waveformIndexArtifact = await PromoteCommittedAsync(
                new ArtifactPromotionRequest(
                    waveformIndexArtifactId,
                    generatedProxy.ProxyArtifact.ProjectId,
                    ArtifactKind.Preview,
                    staging.WaveformIndexPath,
                    string.Concat(
                        relativeRoot,
                        waveformIndexArtifactId.ToString(),
                        ".waveform-index.json"),
                    waveformIndexSnapshot.Digest,
                    waveformIndexSnapshot.ByteSize,
                    createdAt)).ConfigureAwait(false);
            promotedCount++;

            if (!TryCleanupOwnedStaging(staging))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            ValidatePromotedMetadata(
                generatedProxy,
                waveformIndexArtifact,
                spriteImageArtifact,
                spriteIndexArtifact,
                waveformIndexArtifactId,
                spriteImageArtifactId,
                spriteIndexArtifactId,
                waveformIndexSnapshot,
                spriteSnapshot,
                spriteIndexSnapshot,
                createdAt);
            await VerifyRegisteredExactAsync(
                spriteImageArtifact,
                PreviewAssetGenerationFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true,
                CancellationToken.None).ConfigureAwait(false);
            await VerifyRegisteredExactAsync(
                spriteIndexArtifact,
                PreviewAssetGenerationFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true,
                CancellationToken.None).ConfigureAwait(false);
            await VerifyRegisteredExactAsync(
                waveformIndexArtifact,
                PreviewAssetGenerationFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true,
                CancellationToken.None).ConfigureAwait(false);

            return new GeneratedPreviewAssets(
                waveformIndexArtifact,
                spriteImageArtifact,
                spriteIndexArtifact,
                waveform,
                sprite,
                Profile);
        }
        catch (Exception exception)
        {
            bool cleaned = TryCleanupOwnedStaging(staging);
            if (!cleaned)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (promotedCount > 0)
            {
                if (exception is PreviewAssetGenerationException previewException &&
                    previewException.ReconciliationRequired)
                {
                    throw;
                }

                throw Failure(
                    PreviewAssetGenerationFailureReason.PromotionFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is PreviewAssetGenerationException)
            {
                throw;
            }

            if (exception is OverflowException)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.ArithmeticOverflow);
            }

            throw Failure(
                PreviewAssetGenerationFailureReason.StagingUnavailable);
        }
    }

    internal static ProcessRunRequest CreateWaveformProcessRequest(
        string ffmpegPath,
        string proxyPath,
        string outputPath,
        int audioStreamIndex,
        int channelCount,
        string channelLayout,
        long sampleCount,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(channelLayout);
        if (audioStreamIndex < 0 || channelCount <= 0 || sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioStreamIndex),
                "Waveform stream and sample values must be positive or zero-based.");
        }

        if (channelLayout.Length == 0 ||
            !string.Equals(
                channelLayout,
                channelLayout.Trim(),
                StringComparison.Ordinal) ||
            channelLayout.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The waveform channel layout must be trimmed and nonblank.",
                nameof(channelLayout));
        }

        string executable = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string source = SqliteStoreSupport.ValidateLocalAbsolutePath(
            proxyPath,
            nameof(proxyPath));
        string destination = SqliteStoreSupport.ValidateLocalAbsolutePath(
            outputPath,
            nameof(outputPath));
        ValidateTimeout(timeout);
        string workingDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "The waveform output path must have a parent directory.",
                nameof(outputPath));
        string filter = string.Concat(
            "aresample=",
            PreviewAssetGenerationProfile.WaveformSampleRate.ToString(
                CultureInfo.InvariantCulture),
            ":async=1:first_pts=0,apad=whole_len=",
            sampleCount.ToString(CultureInfo.InvariantCulture),
            ",atrim=end_sample=",
            sampleCount.ToString(CultureInfo.InvariantCulture));
        return new ProcessRunRequest(
            executable,
            [
                "-nostdin",
                "-v",
                "error",
                "-i",
                source,
                "-map",
                string.Concat(
                    "0:",
                    audioStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-vn",
                "-af",
                filter,
                "-ac",
                channelCount.ToString(CultureInfo.InvariantCulture),
                "-channel_layout",
                channelLayout,
                "-ar",
                PreviewAssetGenerationProfile.WaveformSampleRate.ToString(
                    CultureInfo.InvariantCulture),
                "-c:a",
                "pcm_s16le",
                "-f",
                "s16le",
                "-n",
                destination,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static ProcessRunRequest CreateSpriteProcessRequest(
        string ffmpegPath,
        string proxyPath,
        string outputPath,
        int videoStreamIndex,
        IReadOnlyList<int> selectedFrameSequences,
        int columns,
        int rows,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(selectedFrameSequences);
        if (videoStreamIndex < 0 ||
            selectedFrameSequences.Count == 0 ||
            selectedFrameSequences.Count >
                PreviewAssetGenerationProfile.MaximumThumbnailCount ||
            columns <= 0 ||
            rows <= 0 ||
            rows != checked(
                (selectedFrameSequences.Count + columns - 1) / columns))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedFrameSequences),
                "The sprite selection and grid are invalid.");
        }

        int previous = -1;
        foreach (int sequence in selectedFrameSequences)
        {
            if (sequence <= previous)
            {
                throw new ArgumentException(
                    "Sprite frame sequences must be strictly increasing.",
                    nameof(selectedFrameSequences));
            }

            previous = sequence;
        }

        string executable = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string source = SqliteStoreSupport.ValidateLocalAbsolutePath(
            proxyPath,
            nameof(proxyPath));
        string destination = SqliteStoreSupport.ValidateLocalAbsolutePath(
            outputPath,
            nameof(outputPath));
        ValidateTimeout(timeout);
        string workingDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "The sprite output path must have a parent directory.",
                nameof(outputPath));
        string selection = string.Join(
            "+",
            selectedFrameSequences.Select(static sequence => string.Concat(
                "eq(n\\,",
                sequence.ToString(CultureInfo.InvariantCulture),
                ")")));
        string filter = string.Concat(
            "select=",
            selection,
            ",scale=",
            PreviewAssetGenerationProfile.ThumbnailWidth.ToString(
                CultureInfo.InvariantCulture),
            ":",
            PreviewAssetGenerationProfile.ThumbnailHeight.ToString(
                CultureInfo.InvariantCulture),
            ":flags=",
            PreviewAssetGenerationProfile.ScaleFlags,
            ",tile=",
            columns.ToString(CultureInfo.InvariantCulture),
            "x",
            rows.ToString(CultureInfo.InvariantCulture),
            ":nb_frames=",
            selectedFrameSequences.Count.ToString(CultureInfo.InvariantCulture),
            ":padding=0:margin=0");
        return new ProcessRunRequest(
            executable,
            [
                "-nostdin",
                "-v",
                "error",
                "-i",
                source,
                "-map",
                string.Concat(
                    "0:",
                    videoStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-vf",
                filter,
                "-frames:v",
                "1",
                "-an",
                "-c:v",
                "png",
                "-f",
                "image2",
                "-n",
                destination,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static IReadOnlyList<PtsIndexEntry> SelectSpriteFrames(
        PtsIndex proxyPtsIndex)
    {
        ArgumentNullException.ThrowIfNull(proxyPtsIndex);
        PtsIndexEntry[] keyFrames = proxyPtsIndex.Entries
            .Where(static entry => entry.KeyFrame)
            .ToArray();
        if (keyFrames.Length == 0)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        if (keyFrames.Length <= PreviewAssetGenerationProfile.MaximumThumbnailCount)
        {
            return Array.AsReadOnly(keyFrames);
        }

        var selected = new PtsIndexEntry[
            PreviewAssetGenerationProfile.MaximumThumbnailCount];
        int lastSourceIndex = keyFrames.Length - 1;
        int lastSelectionIndex = selected.Length - 1;
        for (int index = 0; index < selected.Length; index++)
        {
            int sourceIndex = checked((int)(
                (long)index * lastSourceIndex / lastSelectionIndex));
            selected[index] = keyFrames[sourceIndex];
        }

        return Array.AsReadOnly(selected);
    }

    private async Task<PreviewWaveformIndex> GenerateWaveformAsync(
        GeneratedProxy generatedProxy,
        string proxyPath,
        ContentDigest proxyPtsIndexDigest,
        ProbedMediaStream? audioStream,
        PreviewStaging staging,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (audioStream is null)
        {
            return CreateNoAudioWaveform(generatedProxy, proxyPtsIndexDigest);
        }

        int channelCount = audioStream.Channels
            ?? throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        string channelLayout = audioStream.ChannelLayout
            ?? throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        long sampleCount = GetWaveformSampleCount(
            generatedProxy.ProxyPtsIndex);
        ProcessRunRequest request = CreateWaveformProcessRequest(
            _ffmpegPath,
            proxyPath,
            staging.WaveformPcmPath,
            audioStream.Index,
            channelCount,
            channelLayout,
            sampleCount,
            timeout);
        ProcessRunResult result = await RunProcessAsync(
            request,
            PreviewAssetGenerationFailureReason.WaveformGenerationFailed,
            cancellationToken).ConfigureAwait(false);
        ValidateCleanProcessResult(
            result,
            PreviewAssetGenerationFailureReason.WaveformGenerationFailed);
        IReadOnlyList<WaveformFrame> frames = ReadWaveformFrames(
            staging.WaveformPcmPath,
            generatedProxy.ProxyPtsIndex,
            channelCount,
            sampleCount,
            cancellationToken);
        return new PreviewWaveformIndex(
            generatedProxy.SourceAssetId,
            generatedProxy.ProxyArtifact.ProjectId,
            generatedProxy.ProxyArtifact.Id,
            generatedProxy.ProxyArtifact.ContentDigest,
            proxyPtsIndexDigest,
            Profile.Version,
            Profile.WaveformSamplesPerSecond,
            channelCount,
            audioStream.ChannelLayout,
            frames);
    }

    private static PreviewWaveformIndex CreateNoAudioWaveform(
        GeneratedProxy generatedProxy,
        ContentDigest proxyPtsIndexDigest)
    {
        WaveformFrame[] frames = generatedProxy.ProxyPtsIndex.Entries
            .Select(static entry => new WaveformFrame(
                entry.Sequence,
                entry.PresentationTime,
                entry.Duration,
                []))
            .ToArray();
        return new PreviewWaveformIndex(
            generatedProxy.SourceAssetId,
            generatedProxy.ProxyArtifact.ProjectId,
            generatedProxy.ProxyArtifact.Id,
            generatedProxy.ProxyArtifact.ContentDigest,
            proxyPtsIndexDigest,
            Profile.Version,
            sampleRate: 0,
            channelCount: 0,
            channelLayout: null,
            frames);
    }

    private static ProbedMediaStream? SelectAudioStream(
        MediaProbeResult proxyProbeResult)
    {
        ProbedMediaStream[] audioStreams = proxyProbeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .ToArray();
        if (audioStreams.Length > 1)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        return audioStreams.SingleOrDefault();
    }

    private static long GetWaveformSampleCount(PtsIndex proxyPtsIndex)
    {
        PtsIndexEntry first = proxyPtsIndex.Entries[0];
        PtsIndexEntry last = proxyPtsIndex.Entries[^1];
        if (first.PresentationTime != default(RationalTime))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        RationalTime end = last.PresentationTime + last.Duration;
        return ScaleCeiling(
            end,
            PreviewAssetGenerationProfile.WaveformSampleRate);
    }

    private static IReadOnlyList<WaveformFrame> ReadWaveformFrames(
        string path,
        PtsIndex proxyPtsIndex,
        int channelCount,
        long sampleCount,
        CancellationToken cancellationToken)
    {
        long expectedByteSize;
        try
        {
            expectedByteSize = checked(sampleCount * channelCount * sizeof(short));
        }
        catch (OverflowException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ArithmeticOverflow);
        }

        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                Path.GetDirectoryName(path)!,
                path,
                targetIsDirectory: false);
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.SequentialScan);
            ValidateOpenedRegularFile(stream);
            if (stream.Length != expectedByteSize)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.WaveformOutputInvalid);
            }

            var frames = new WaveformFrame[proxyPtsIndex.Entries.Count];
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
            long consumedSamples = 0;
            try
            {
                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PtsIndexEntry entry = proxyPtsIndex.Entries[frameIndex];
                    long frameEndSample = frameIndex + 1 < frames.Length
                        ? ScaleFloor(
                            proxyPtsIndex.Entries[frameIndex + 1]
                                .PresentationTime,
                            PreviewAssetGenerationProfile.WaveformSampleRate)
                        : sampleCount;
                    long frameSampleCount = frameEndSample - consumedSamples;
                    if (frameSampleCount <= 0)
                    {
                        throw Failure(
                            PreviewAssetGenerationFailureReason.WaveformOutputInvalid);
                    }

                    int[] minima = Enumerable.Repeat(
                        (int)short.MaxValue,
                        channelCount).ToArray();
                    int[] maxima = Enumerable.Repeat(
                        (int)short.MinValue,
                        channelCount).ToArray();
                    long remainingBytes = checked(
                        frameSampleCount * channelCount * sizeof(short));
                    int channelIndex = 0;
                    while (remainingBytes > 0)
                    {
                        int requested = checked((int)Math.Min(
                            buffer.Length - (buffer.Length % sizeof(short)),
                            remainingBytes));
                        stream.ReadExactly(buffer.AsSpan(0, requested));
                        for (int offset = 0; offset < requested; offset += sizeof(short))
                        {
                            short sample = BinaryPrimitives.ReadInt16LittleEndian(
                                buffer.AsSpan(offset, sizeof(short)));
                            minima[channelIndex] = Math.Min(
                                minima[channelIndex],
                                sample);
                            maxima[channelIndex] = Math.Max(
                                maxima[channelIndex],
                                sample);
                            channelIndex++;
                            if (channelIndex == channelCount)
                            {
                                channelIndex = 0;
                            }
                        }

                        remainingBytes -= requested;
                    }

                    if (channelIndex != 0)
                    {
                        throw Failure(
                            PreviewAssetGenerationFailureReason.WaveformOutputInvalid);
                    }

                    WaveformChannelExtrema[] channels = Enumerable
                        .Range(0, channelCount)
                        .Select(index => new WaveformChannelExtrema(
                            index,
                            checked((short)minima[index]),
                            checked((short)maxima[index])))
                        .ToArray();
                    frames[frameIndex] = new WaveformFrame(
                        entry.Sequence,
                        entry.PresentationTime,
                        entry.Duration,
                        channels);
                    consumedSamples = frameEndSample;
                }

                if (consumedSamples != sampleCount || stream.Position != stream.Length)
                {
                    throw Failure(
                        PreviewAssetGenerationFailureReason.WaveformOutputInvalid);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            ValidateOpenedRegularFile(stream);
            return Array.AsReadOnly(frames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.WaveformOutputInvalid);
        }
    }

    private static ThumbnailSpriteIndex CreateSpriteIndex(
        GeneratedProxy generatedProxy,
        ContentDigest proxyPtsIndexDigest,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        ContentDigest spriteImageDigest,
        SpriteGrid grid,
        IReadOnlyList<PtsIndexEntry> selectedFrames)
    {
        ThumbnailSpriteCell[] cells = selectedFrames
            .Select((frame, index) => new ThumbnailSpriteCell(
                index,
                frame.Sequence,
                frame.PresentationTime,
                frame.Duration,
                index % grid.Columns,
                index / grid.Columns))
            .ToArray();
        return new ThumbnailSpriteIndex(
            generatedProxy.SourceAssetId,
            generatedProxy.ProxyArtifact.ProjectId,
            generatedProxy.ProxyArtifact.Id,
            generatedProxy.ProxyArtifact.ContentDigest,
            proxyPtsIndexDigest,
            spriteImageArtifactId,
            spriteImageDigest,
            Profile.Version,
            Profile.SpriteCellWidth,
            Profile.SpriteCellHeight,
            grid.Columns,
            grid.Rows,
            cells);
    }

    private static SpriteGrid CreateSpriteGrid(int cellCount)
    {
        if (cellCount <= 0 ||
            cellCount > PreviewAssetGenerationProfile.MaximumThumbnailCount)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        int columns = Math.Min(
            PreviewAssetGenerationProfile.MaximumColumns,
            cellCount);
        int rows = checked((cellCount + columns - 1) / columns);
        return new SpriteGrid(columns, rows);
    }

    private static long ScaleFloor(RationalTime time, int targetRate)
    {
        if (time < default(RationalTime))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        BigInteger numerator = (BigInteger)time.Value * targetRate;
        BigInteger value = BigInteger.DivRem(
            numerator,
            time.Rate,
            out _);
        if (value < 0 || value > long.MaxValue)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ArithmeticOverflow);
        }

        return (long)value;
    }

    private static long ScaleCeiling(RationalTime time, int targetRate)
    {
        if (time <= default(RationalTime))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        BigInteger numerator = (BigInteger)time.Value * targetRate;
        BigInteger value = BigInteger.DivRem(
            numerator,
            time.Rate,
            out BigInteger remainder);
        if (!remainder.IsZero)
        {
            value++;
        }

        if (value <= 0 || value > long.MaxValue)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ArithmeticOverflow);
        }

        return (long)value;
    }

    private static void ValidateInputBinding(GeneratedProxy generatedProxy)
    {
        PtsIndex proxyIndex = generatedProxy.ProxyPtsIndex;
        MediaProbeResult probe = generatedProxy.ProxyProbeResult;
        ProbedMediaStream[] videos = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .ToArray();
        ProbedMediaStream[] audio = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .ToArray();
        var requiredRate = new HubRadio.Core.Layout.RationalNumber(30000, 1001);
        RationalTime requiredFrameDuration = new(1001, 30000);
        if (generatedProxy.ProxyArtifact.Kind != ArtifactKind.Proxy ||
            generatedProxy.ProxyArtifact.ProjectId != proxyIndex.ProjectId ||
            proxyIndex.CandidateId != generatedProxy.SourceAssetId ||
            proxyIndex.ContentDigest != generatedProxy.ProxyArtifact.ContentDigest ||
            proxyIndex.ByteSize != generatedProxy.ProxyArtifact.ByteSize ||
            probe.CandidateId != generatedProxy.SourceAssetId ||
            probe.ProjectId != generatedProxy.ProxyArtifact.ProjectId ||
            probe.JobId != proxyIndex.JobId ||
            probe.ContentDigest != generatedProxy.ProxyArtifact.ContentDigest ||
            probe.ByteSize != generatedProxy.ProxyArtifact.ByteSize ||
            videos.Length != 1 ||
            videos[0].Index != proxyIndex.StreamIndex ||
            !string.Equals(videos[0].CodecName, "h264", StringComparison.Ordinal) ||
            videos[0].Width != ProxyGenerationProfile.Stage0Width ||
            videos[0].Height != ProxyGenerationProfile.Stage0Height ||
            !string.Equals(
                videos[0].PixelFormat,
                ProxyGenerationProfile.Stage0PixelFormat,
                StringComparison.Ordinal) ||
            videos[0].NominalFrameRate != requiredRate ||
            videos[0].AverageFrameRate != requiredRate ||
            proxyIndex.TimeBase != new RationalTime(
                videos[0].TimeBase.Numerator,
                videos[0].TimeBase.Denominator) ||
            proxyIndex.Entries[0].PresentationTime != default(RationalTime) ||
            audio.Length > 1 ||
            (audio.Length == 1 &&
                !string.Equals(
                    audio[0].CodecName,
                    ProxyGenerationProfile.Stage0AudioCodec,
                    StringComparison.Ordinal)) ||
            generatedProxy.Profile.Width != ProxyGenerationProfile.Stage0Width ||
            generatedProxy.Profile.Height != ProxyGenerationProfile.Stage0Height ||
            generatedProxy.Profile.FrameRate != requiredRate)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }

        for (int index = 0; index < proxyIndex.Entries.Count; index++)
        {
            PtsIndexEntry entry = proxyIndex.Entries[index];
            if (entry.Duration != requiredFrameDuration ||
                (index > 0 &&
                    entry.PresentationTime -
                        proxyIndex.Entries[index - 1].PresentationTime !=
                            requiredFrameDuration))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.InputBindingMismatch);
            }
        }
    }

    private static void ValidateDistinctOutputIds(
        EntityId<ArtifactIdTag> waveformIndexArtifactId,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        EntityId<ArtifactIdTag> spriteIndexArtifactId,
        EntityId<ArtifactIdTag> proxyArtifactId)
    {
        if (waveformIndexArtifactId == spriteImageArtifactId ||
            waveformIndexArtifactId == spriteIndexArtifactId ||
            waveformIndexArtifactId == proxyArtifactId ||
            spriteImageArtifactId == spriteIndexArtifactId ||
            spriteImageArtifactId == proxyArtifactId ||
            spriteIndexArtifactId == proxyArtifactId)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.InputBindingMismatch);
        }
    }

    private void EnsureOutputIdsAvailable(
        params EntityId<ArtifactIdTag>[] artifactIds)
    {
        try
        {
            if (artifactIds.Any(id => _artifactStore.GetArtifact(id) is not null))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.PromotionFailed);
            }
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private string GetRegisteredProxyPath(StoredArtifact expected)
    {
        try
        {
            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(
                    PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                    reconciliationRequired: true);
            if (!HasSameMetadata(expected, current))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.InputBindingMismatch);
            }

            return _artifactStore.GetValidatedRegisteredArtifactPath(expected.Id);
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true);
        }
    }

    private async Task VerifyFfmpegAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new BinaryVerificationRequest(
                ToolComponentIdentity,
                _ffmpegPath,
                CanonicalFfmpegDigest,
                DecodeValidator.CanonicalFfmpegByteSize);
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!IsExactVerification(result, request))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.ToolVerificationFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ToolVerificationFailed);
        }
    }

    private async Task VerifyRegisteredExactAsync(
        StoredArtifact expected,
        PreviewAssetGenerationFailureReason failureReason,
        bool reconciliationRequired,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyRegisteredAsync(
                expected.Id,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified ||
                result.ExpectedDigest != expected.ContentDigest ||
                result.ActualDigest != expected.ContentDigest ||
                result.ExpectedByteSize != expected.ByteSize ||
                result.ActualByteSize != expected.ByteSize)
            {
                throw Failure(failureReason, reconciliationRequired);
            }

            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(failureReason, reconciliationRequired);
            if (!HasSameMetadata(expected, current))
            {
                throw Failure(failureReason, reconciliationRequired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason, reconciliationRequired);
        }
    }

    private static bool IsExactVerification(
        BinaryVerificationResult result,
        BinaryVerificationRequest request) =>
        result.IsVerified &&
        result.ExpectedDigest == request.ExpectedDigest &&
        result.ActualDigest == request.ExpectedDigest &&
        result.ExpectedByteSize == request.ExpectedByteSize &&
        result.ActualByteSize == request.ExpectedByteSize;

    private async Task<ProcessRunResult> RunProcessAsync(
        ProcessRunRequest request,
        PreviewAssetGenerationFailureReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runProcessAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(reason);
        }
    }

    private static void ValidateCleanProcessResult(
        ProcessRunResult result,
        PreviewAssetGenerationFailureReason reason)
    {
        if (result.ExitCode != 0 ||
            result.Truncated ||
            result.StandardOutput is null ||
            result.StandardOutput.Length != 0 ||
            result.StandardError is null ||
            result.StandardError.Length != 0 ||
            result.Duration < TimeSpan.Zero)
        {
            throw Failure(reason);
        }
    }

    private async Task<StoredArtifact> PromoteCommittedAsync(
        ArtifactPromotionRequest request)
    {
        try
        {
            return await _promoteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private PreviewStaging PrepareStaging(
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> proxyJobId,
        EntityId<ArtifactIdTag> spriteImageArtifactId,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(Path.Combine(
            _workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "preview-assets"));
        EnsureDirectoryChain(root, cancellationToken);
        string directory = Path.Combine(
            root,
            string.Concat(
                proxyJobId.ToString(),
                "-",
                spriteImageArtifactId.ToString()));
        if (WorkspacePathSafety.PathEntryExistsConservatively(directory))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.StagingConflict);
        }

        string claim = Path.Combine(
            root,
            string.Concat(
                ".",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ".claim"));
        bool claimCreated = false;
        bool directoryMoved = false;
        try
        {
            _ = Directory.CreateDirectory(claim);
            claimCreated = true;
            ValidateExistingWorkspaceDirectory(claim);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(claim, directory);
            claimCreated = false;
            directoryMoved = true;
            ValidateExistingWorkspaceDirectory(directory);
            return new PreviewStaging(
                directory,
                Path.Combine(directory, "waveform.partial.s16le"),
                Path.Combine(directory, "waveform-index.partial.json"),
                Path.Combine(directory, "sprite.partial.png"),
                Path.Combine(directory, "sprite-index.partial.json"));
        }
        catch (Exception exception)
        {
            if (directoryMoved)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (claimCreated && !TryDeleteOwnedEmptyDirectory(claim))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is PreviewAssetGenerationException)
            {
                throw;
            }

            throw Failure(
                WorkspacePathSafety.PathEntryExistsConservatively(directory)
                    ? PreviewAssetGenerationFailureReason.StagingConflict
                    : PreviewAssetGenerationFailureReason.StagingUnavailable);
        }
    }

    private void EnsureDirectoryChain(
        string destination,
        CancellationToken cancellationToken)
    {
        if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, destination))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.StagingUnavailable);
        }

        string relative = Path.GetRelativePath(_workspacePath, destination);
        string current = _workspacePath;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExistingWorkspaceDirectory(current);
            current = Path.Combine(current, segment);
            if (!WorkspacePathSafety.TryGetAttributes(current, out _))
            {
                Directory.CreateDirectory(current);
            }

            ValidateExistingWorkspaceDirectory(current);
        }
    }

    private bool TryCleanupOwnedStaging(PreviewStaging staging)
    {
        try
        {
            foreach (string path in new[]
            {
                staging.WaveformPcmPath,
                staging.WaveformIndexPath,
                staging.SpriteImagePath,
                staging.SpriteIndexPath,
            })
            {
                if (!WorkspacePathSafety.TryGetAttributes(
                        path,
                        out FileAttributes attributes))
                {
                    continue;
                }

                WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
                WorkspacePathSafety.ValidateExistingPathChain(
                    _workspacePath,
                    path,
                    targetIsDirectory: false);
                File.Delete(path);
                if (WorkspacePathSafety.PathEntryExistsConservatively(path))
                {
                    return false;
                }
            }

            return TryDeleteOwnedEmptyDirectory(staging.DirectoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryDeleteOwnedEmptyDirectory(string path)
    {
        try
        {
            if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, path))
            {
                return false;
            }

            if (!WorkspacePathSafety.TryGetAttributes(
                    path,
                    out FileAttributes attributes))
            {
                return true;
            }

            WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: true);
            Directory.Delete(path, recursive: false);
            return !WorkspacePathSafety.PathEntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ValidateExistingWorkspaceDirectory(string path)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: true);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.StagingUnavailable);
        }
    }

    private static async Task<FileSnapshot> WriteCanonicalIndexAsync(
        string path,
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        byte[] bytes = StrictUtf8.GetBytes(canonicalJson);
        await using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        return new FileSnapshot(bytes.LongLength, ContentDigest.Compute(bytes));
    }

    private static async Task<FileSnapshot> ValidatePngAsync(
        string path,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ValidateOpenedRegularFile(stream);
            if (stream.Length < 57)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
            }

            byte[] signatureBytes = new byte[8];
            await stream.ReadExactlyAsync(signatureBytes, cancellationToken)
                .ConfigureAwait(false);
            ReadOnlySpan<byte> signature =
                [137, 80, 78, 71, 13, 10, 26, 10];
            if (!signatureBytes.AsSpan().SequenceEqual(signature))
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
            }

            bool seenHeader = false;
            bool seenImageData = false;
            bool imageDataEnded = false;
            bool seenEnd = false;
            int decodedChannelCount = 0;
            long expectedDecodedByteCount = 0;
            long maximumCompressedByteCount = 0;
            using var compressedImageData = new MemoryStream();
            byte[] chunkHeader = new byte[8];
            byte[] crcBytes = new byte[4];
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
            try
            {
                while (!seenEnd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await stream.ReadExactlyAsync(chunkHeader, cancellationToken)
                        .ConfigureAwait(false);
                    uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(
                        chunkHeader.AsSpan(0, 4));
                    ReadOnlySpan<byte> chunkType = chunkHeader.AsSpan(4, 4);
                    if (!chunkType.ToArray().All(static value =>
                            value is >= (byte)'A' and <= (byte)'Z' or
                                >= (byte)'a' and <= (byte)'z') ||
                        (long)chunkLength + sizeof(uint) >
                            stream.Length - stream.Position)
                    {
                        throw Failure(
                            PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                    }

                    bool isHeader = chunkType.SequenceEqual("IHDR"u8);
                    bool isImageData = chunkType.SequenceEqual("IDAT"u8);
                    bool isEnd = chunkType.SequenceEqual("IEND"u8);
                    if ((!seenHeader && !isHeader) ||
                        (seenHeader && isHeader) ||
                        (isHeader && chunkLength != 13) ||
                        (isEnd && chunkLength != 0) ||
                        (isImageData && (chunkLength == 0 || imageDataEnded)) ||
                        (seenImageData && !isImageData && !isEnd &&
                            chunkType[0] is >= (byte)'A' and <= (byte)'Z') ||
                        (!isHeader && !isImageData && !isEnd &&
                            chunkType[0] is >= (byte)'A' and <= (byte)'Z' &&
                            !chunkType.SequenceEqual("PLTE"u8)))
                    {
                        throw Failure(
                            PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                    }

                    uint crc = 0xffffffffu;
                    crc = UpdateCrc32(crc, chunkType);
                    byte[]? headerData = isHeader ? new byte[13] : null;
                    long remaining = chunkLength;
                    int headerOffset = 0;
                    while (remaining > 0)
                    {
                        int requested = checked((int)Math.Min(
                            buffer.Length,
                            remaining));
                        await stream.ReadExactlyAsync(
                            buffer.AsMemory(0, requested),
                            cancellationToken).ConfigureAwait(false);
                        crc = UpdateCrc32(crc, buffer.AsSpan(0, requested));
                        if (headerData is not null)
                        {
                            buffer.AsSpan(0, requested).CopyTo(
                                headerData.AsSpan(headerOffset));
                            headerOffset += requested;
                        }
                        else if (isImageData)
                        {
                            if (compressedImageData.Length + requested >
                                maximumCompressedByteCount)
                            {
                                throw Failure(
                                    PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                            }

                            compressedImageData.Write(buffer, 0, requested);
                        }

                        remaining -= requested;
                    }

                    await stream.ReadExactlyAsync(crcBytes, cancellationToken)
                        .ConfigureAwait(false);
                    uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);
                    if ((crc ^ 0xffffffffu) != expectedCrc)
                    {
                        throw Failure(
                            PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                    }

                    if (headerData is not null)
                    {
                        if (BinaryPrimitives.ReadUInt32BigEndian(
                                headerData.AsSpan(0, 4)) != (uint)expectedWidth ||
                            BinaryPrimitives.ReadUInt32BigEndian(
                                headerData.AsSpan(4, 4)) != (uint)expectedHeight ||
                            headerData[8] != 8 ||
                            headerData[9] is not (2 or 6) ||
                            headerData[10] != 0 ||
                            headerData[11] != 0 ||
                            headerData[12] != 0)
                        {
                            throw Failure(
                                PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                        }

                        decodedChannelCount = headerData[9] == 2 ? 3 : 4;
                        expectedDecodedByteCount = checked(
                            (long)expectedHeight *
                            (1L + checked((long)expectedWidth * decodedChannelCount)));
                        maximumCompressedByteCount = GetMaximumZlibByteCount(
                            expectedDecodedByteCount);
                        seenHeader = true;
                    }
                    else if (isImageData)
                    {
                        seenImageData = true;
                    }
                    else if (isEnd)
                    {
                        seenEnd = true;
                    }

                    if (seenImageData && !isImageData)
                    {
                        imageDataEnded = true;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (!seenHeader ||
                !seenImageData ||
                !seenEnd ||
                decodedChannelCount == 0 ||
                stream.Position != stream.Length)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
            }

            ValidatePngImageData(
                compressedImageData,
                expectedWidth,
                expectedHeight,
                decodedChannelCount,
                expectedDecodedByteCount);

            stream.Position = 0;
            byte[] digestBytes = await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            ValidateOpenedRegularFile(stream);
            return new FileSnapshot(
                stream.Length,
                ContentDigest.FromBytes(digestBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception) ||
                exception is InvalidDataException ||
                exception is CryptographicException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
        }
    }

    private static void ValidateOpenedRegularFile(FileStream stream)
    {
        FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
        WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDataException("The preview file handle is invalid.");
        }
    }

    private static void ValidatePngImageData(
        MemoryStream compressedImageData,
        int width,
        int height,
        int channelCount,
        long expectedByteCount)
    {
        compressedImageData.Position = 0;
        using var zlib = new ZLibStream(
            compressedImageData,
            CompressionMode.Decompress,
            leaveOpen: true);
        int rowByteCount = checked(1 + checked(width * channelCount));
        byte[] row = ArrayPool<byte>.Shared.Rent(rowByteCount);
        try
        {
            long decodedByteCount = 0;
            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            {
                zlib.ReadExactly(row.AsSpan(0, rowByteCount));
                if (row[0] > 4)
                {
                    throw Failure(
                        PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
                }

                decodedByteCount = checked(decodedByteCount + rowByteCount);
            }

            if (decodedByteCount != expectedByteCount || zlib.ReadByte() != -1)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row, clearArray: true);
        }
    }

    private static long GetMaximumZlibByteCount(long sourceByteCount)
    {
        if (sourceByteCount <= 0)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.SpriteOutputInvalid);
        }

        return checked(
            sourceByteCount +
            (sourceByteCount >> 12) +
            (sourceByteCount >> 14) +
            (sourceByteCount >> 25) +
            13);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^
                    (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc;
    }

    private static FileStream OpenRegisteredProxy(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true);
        }
    }

    private static HeldFileSnapshot CaptureHeldProxy(
        FileStream stream,
        long expectedByteSize)
    {
        try
        {
            ValidateOpenedRegularFile(stream);
            if (stream.Length != expectedByteSize)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                    reconciliationRequired: true);
            }

            return new HeldFileSnapshot(
                stream.Length,
                File.GetAttributes(stream.SafeFileHandle),
                File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception) ||
                exception is WorkspacePathSafetyException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true);
        }
    }

    private static void ValidateHeldProxy(
        FileStream stream,
        HeldFileSnapshot expected)
    {
        try
        {
            ValidateOpenedRegularFile(stream);
            if (stream.Length != expected.ByteSize ||
                File.GetAttributes(stream.SafeFileHandle) != expected.Attributes ||
                File.GetLastWriteTimeUtc(stream.SafeFileHandle) !=
                    expected.LastWriteTimeUtc)
            {
                throw Failure(
                    PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                    reconciliationRequired: true);
            }
        }
        catch (PreviewAssetGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception) ||
                exception is WorkspacePathSafetyException)
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true);
        }
    }

    private static void ValidatePromotedMetadata(
        GeneratedProxy generatedProxy,
        StoredArtifact waveform,
        StoredArtifact spriteImage,
        StoredArtifact spriteIndex,
        EntityId<ArtifactIdTag> waveformId,
        EntityId<ArtifactIdTag> spriteImageId,
        EntityId<ArtifactIdTag> spriteIndexId,
        FileSnapshot waveformSnapshot,
        FileSnapshot spriteImageSnapshot,
        FileSnapshot spriteIndexSnapshot,
        DateTimeOffset createdAt)
    {
        EntityId<ProjectIdTag> projectId = generatedProxy.ProxyArtifact.ProjectId;
        if (!MatchesPromoted(
                waveform,
                waveformId,
                projectId,
                waveformSnapshot,
                createdAt) ||
            !MatchesPromoted(
                spriteImage,
                spriteImageId,
                projectId,
                spriteImageSnapshot,
                createdAt) ||
            !MatchesPromoted(
                spriteIndex,
                spriteIndexId,
                projectId,
                spriteIndexSnapshot,
                createdAt))
        {
            throw Failure(
                PreviewAssetGenerationFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true);
        }
    }

    private static bool MatchesPromoted(
        StoredArtifact artifact,
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        FileSnapshot snapshot,
        DateTimeOffset createdAt) =>
        artifact.Id == id &&
        artifact.ProjectId == projectId &&
        artifact.Kind == ArtifactKind.Preview &&
        artifact.ContentDigest == snapshot.Digest &&
        artifact.ByteSize == snapshot.ByteSize &&
        artifact.CreatedAt == createdAt;

    private static bool HasSameMetadata(
        StoredArtifact left,
        StoredArtifact right) =>
        left.Id == right.Id &&
        left.ProjectId == right.ProjectId &&
        left.Kind == right.Kind &&
        string.Equals(
            left.StorageReference,
            right.StorageReference,
            StringComparison.Ordinal) &&
        left.ContentDigest == right.ContentDigest &&
        left.ByteSize == right.ByteSize &&
        left.CreatedAt == right.CreatedAt;

    private static Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
        GetProcessDelegate(ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>>
        GetBinaryVerificationDelegate(BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return binaryVerifier.VerifyAsync;
    }

    private static Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>>
        GetRegisteredVerificationDelegate(
            ArtifactStore artifactStore,
            BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return (artifactId, cancellationToken) =>
            artifactStore.VerifyRegisteredAsync(
                artifactId,
                binaryVerifier,
                cancellationToken);
    }

    private static Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
        GetPromotionDelegate(ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.PromoteAsync;
    }

    private static ContentDigest ComputeIndexDigest(PtsIndex index) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(index.CanonicalJson));

    private static string ValidateWorkspacePath(string workspacePath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(normalized);
        }
        catch (WorkspacePathSafetyException)
        {
            throw new ArgumentException(
                "The workspace must be an existing local directory without reparse points.",
                nameof(workspacePath));
        }

        return normalized;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The preview generation timeout must be positive and supported by the runtime timer.");
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static PreviewAssetGenerationException Failure(
        PreviewAssetGenerationFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record PreviewStaging(
        string DirectoryPath,
        string WaveformPcmPath,
        string WaveformIndexPath,
        string SpriteImagePath,
        string SpriteIndexPath);

    private readonly record struct FileSnapshot(
        long ByteSize,
        ContentDigest Digest);

    private readonly record struct HeldFileSnapshot(
        long ByteSize,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);

    private readonly record struct SpriteGrid(int Columns, int Rows);
}

internal static class PreviewAssetJson
{
    internal static string WriteWaveform(PreviewWaveformIndex index) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            WriteCommon(
                writer,
                index.SchemaVersion,
                index.SourceAssetId,
                index.ProjectId,
                index.ProxyArtifactId,
                index.ProxyContentDigest,
                index.ProxyPtsIndexDigest,
                index.ProfileVersion);
            writer.WriteNumber("sampleRate", index.SampleRate);
            writer.WriteNumber("channelCount", index.ChannelCount);
            if (index.ChannelLayout is null)
            {
                writer.WriteNull("channelLayout");
            }
            else
            {
                writer.WriteString("channelLayout", index.ChannelLayout);
            }

            writer.WritePropertyName("frames");
            writer.WriteStartArray();
            foreach (WaveformFrame frame in index.Frames)
            {
                writer.WriteStartObject();
                writer.WriteNumber("proxyFrameSequence", frame.ProxyFrameSequence);
                writer.WritePropertyName("proxyPresentationTime");
                WriteRationalTime(writer, frame.ProxyPresentationTime);
                writer.WritePropertyName("proxyDuration");
                WriteRationalTime(writer, frame.ProxyDuration);
                writer.WritePropertyName("channels");
                writer.WriteStartArray();
                foreach (WaveformChannelExtrema channel in frame.Channels)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("channelIndex", channel.ChannelIndex);
                    writer.WriteNumber("minimumSample", channel.MinimumSample);
                    writer.WriteNumber("maximumSample", channel.MaximumSample);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    internal static string WriteSprite(ThumbnailSpriteIndex index) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            WriteCommon(
                writer,
                index.SchemaVersion,
                index.SourceAssetId,
                index.ProjectId,
                index.ProxyArtifactId,
                index.ProxyContentDigest,
                index.ProxyPtsIndexDigest,
                index.ProfileVersion);
            writer.WriteString(
                "spriteImageArtifactId",
                index.SpriteImageArtifactId.ToString());
            writer.WriteString("spriteImageDigest", index.SpriteImageDigest.Hex);
            writer.WriteNumber("cellWidth", index.CellWidth);
            writer.WriteNumber("cellHeight", index.CellHeight);
            writer.WriteNumber("columns", index.Columns);
            writer.WriteNumber("rows", index.Rows);
            writer.WritePropertyName("cells");
            writer.WriteStartArray();
            foreach (ThumbnailSpriteCell cell in index.Cells)
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", cell.Ordinal);
                writer.WriteNumber(
                    "proxyFrameSequence",
                    cell.ProxyFrameSequence);
                writer.WritePropertyName("proxyPresentationTime");
                WriteRationalTime(writer, cell.ProxyPresentationTime);
                writer.WritePropertyName("proxyDuration");
                WriteRationalTime(writer, cell.ProxyDuration);
                writer.WriteNumber("column", cell.Column);
                writer.WriteNumber("row", cell.Row);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static void WriteCommon(
        Utf8JsonWriter writer,
        string schemaVersion,
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        string profileVersion)
    {
        writer.WriteString("schemaVersion", schemaVersion);
        writer.WriteString("sourceAssetId", sourceAssetId.ToString());
        writer.WriteString("projectId", projectId.ToString());
        writer.WriteString("proxyArtifactId", proxyArtifactId.ToString());
        writer.WriteString("proxyContentDigest", proxyContentDigest.Hex);
        writer.WriteString("proxyPtsIndexDigest", proxyPtsIndexDigest.Hex);
        writer.WriteString("profileVersion", profileVersion);
    }

    private static string Write(Action<Utf8JsonWriter> write)
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

        byte[] canonical =
            HubRadio.Core.Serialization.CanonicalJson.CanonicalizeToUtf8(
                buffer.WrittenSpan);
        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(canonical);
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
}