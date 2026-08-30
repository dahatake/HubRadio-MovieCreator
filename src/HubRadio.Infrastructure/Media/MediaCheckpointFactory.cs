using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Media;

public sealed class MediaCheckpointValidationException : Exception
{
    internal MediaCheckpointValidationException()
        : base("The durable media checkpoint could not be validated safely.")
    {
    }
}

public static class MediaCheckpointFactory
{
    private const int FileBufferSize = 64 * 1024;
    private const int Sha256ByteLength = 32;
    private const string NvencProfileKind = "stage0-nvenc";
    private const string MediaFoundationProfileKind = "stage0-media-foundation";

    public static string GetStagingReference(
        string workspacePath,
        StagedMediaImport stagedMedia)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        string workspace = ValidateWorkspace(workspacePath);
        string expected = GetExpectedStagingReference(
            stagedMedia.ProjectId,
            stagedMedia.JobId,
            stagedMedia.CandidateId);
        if (!WorkspacePathSafety.TryGetPortableRelativePath(
                workspace,
                stagedMedia.StagingPath,
                out string actual) ||
            !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw Failure();
        }

        return actual;
    }

    public static async Task<StagedMediaImport> RestoreStagedMediaAsync(
        string workspacePath,
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        string stagingReference,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(stagingReference);
        ArgumentNullException.ThrowIfNull(contentDigest);
        cancellationToken.ThrowIfCancellationRequested();

        string workspace = ValidateWorkspace(workspacePath);
        string expectedReference = GetExpectedStagingReference(
            projectId,
            jobId,
            candidateId);
        if (!string.Equals(
                stagingReference,
                expectedReference,
                StringComparison.Ordinal) ||
            !WorkspacePathSafety.TryResolvePortableRelativePath(
                workspace,
                stagingReference,
                out string stagingPath))
        {
            throw Failure();
        }

        ValidateStagingPath(workspace, stagingPath);
        cancellationToken.ThrowIfCancellationRequested();
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            using var stream = new FileStream(stagingPath, options);
            ValidateStagingPath(workspace, stagingPath);
            ValidateOpenedFile(stream);
            if (stream.Length != byteSize)
            {
                throw Failure();
            }

            byte[] actualDigest = new byte[Sha256ByteLength];
            int written = await SHA256.HashDataAsync(
                stream,
                actualDigest,
                cancellationToken).ConfigureAwait(false);
            if (written != Sha256ByteLength || stream.Length != byteSize)
            {
                throw Failure();
            }

            ValidateOpenedFile(stream);
            ValidateStagingPath(workspace, stagingPath);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(contentDigest.Hex),
                    actualDigest))
            {
                throw Failure();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MediaCheckpointValidationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException or ArgumentException or
                CryptographicException)
        {
            throw Failure();
        }

        return new StagedMediaImport(
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            stagingPath,
            contentDigest,
            byteSize,
            createdAt);
    }

    public static StagedMediaImport RestorePromotedStagedMedia(
        string workspacePath,
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        string stagingReference,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(stagingReference);
        ArgumentNullException.ThrowIfNull(contentDigest);

        string workspace = ValidateWorkspace(workspacePath);
        string expectedReference = GetExpectedStagingReference(
            projectId,
            jobId,
            candidateId);
        if (!string.Equals(
                stagingReference,
                expectedReference,
                StringComparison.Ordinal) ||
            !WorkspacePathSafety.TryResolvePortableRelativePath(
                workspace,
                stagingReference,
                out string stagingPath))
        {
            throw Failure();
        }

        try
        {
            if (WorkspacePathSafety.PathEntryExistsConservatively(stagingPath))
            {
                throw Failure();
            }
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure();
        }

        return new StagedMediaImport(
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            stagingPath,
            contentDigest,
            byteSize,
            createdAt);
    }

    public static void ValidateStagingCheckpointBoundary(
        string workspacePath,
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        bool readyExpected)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);

        string workspace = ValidateWorkspace(workspacePath);
        string readyReference = GetExpectedStagingReference(
            projectId,
            jobId,
            candidateId);
        if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                workspace,
                readyReference,
                out string readyPath))
        {
            throw Failure();
        }

        string partialPath = string.Concat(
            readyPath.AsSpan(0, readyPath.Length - ".ready".Length),
            ".partial");
        bool readyExists = WorkspacePathSafety.PathEntryExistsConservatively(
            readyPath);
        bool partialExists = WorkspacePathSafety.PathEntryExistsConservatively(
            partialPath);
        if (readyExists != readyExpected || partialExists)
        {
            throw Failure();
        }

        if (readyExists)
        {
            ValidateStagingPath(workspace, readyPath);
        }
    }

    public static DecodeValidationResult RestoreDecodeValidation(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan elapsed,
        int standardErrorLineCount,
        ContentDigest standardErrorContentDigest,
        RationalTime presentationStart,
        RationalTime presentationEnd,
        int indexedFrameCount) =>
        new(
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            contentDigest,
            byteSize,
            videoStreamIndex,
            audioStreamIndex,
            elapsed,
            standardErrorLineCount,
            standardErrorContentDigest,
            presentationStart,
            presentationEnd,
            indexedFrameCount);

    public static string GetProxyProfileKind(ProxyGenerationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (ProfileMatches(profile, new ProxyGenerationProfile()))
        {
            return NvencProfileKind;
        }

        if (ProfileMatches(
                profile,
                ProxyGenerationProfile.CreateMediaFoundationFallback()))
        {
            return MediaFoundationProfileKind;
        }

        throw Failure();
    }

    public static ProxyGenerationProfile RestoreProxyProfile(string profileKind) =>
        profileKind switch
        {
            NvencProfileKind => new ProxyGenerationProfile(),
            MediaFoundationProfileKind =>
                ProxyGenerationProfile.CreateMediaFoundationFallback(),
            _ => throw Failure(),
        };

    public static GeneratedProxy RestoreGeneratedProxy(
        EntityId<AssetIdTag> sourceAssetId,
        ContentDigest sourceContentDigest,
        EntityId<ArtifactIdTag> sourceOriginalArtifactId,
        StoredArtifact proxyArtifact,
        MediaProbeResult proxyProbeResult,
        PtsIndex proxyPtsIndex,
        ProxyGenerationProfile profile,
        TimeSpan generationElapsed) =>
        new(
            sourceAssetId,
            sourceContentDigest,
            sourceOriginalArtifactId,
            proxyArtifact,
            proxyProbeResult,
            proxyPtsIndex,
            profile,
            generationElapsed);

    public static PreviewWaveformIndex RestoreWaveformIndex(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        ContentDigest proxyContentDigest,
        ContentDigest proxyPtsIndexDigest,
        string profileVersion,
        int sampleRate,
        int channelCount,
        string? channelLayout,
        IReadOnlyList<WaveformFrame> frames) =>
        new(
            sourceAssetId,
            projectId,
            proxyArtifactId,
            proxyContentDigest,
            proxyPtsIndexDigest,
            profileVersion,
            sampleRate,
            channelCount,
            channelLayout,
            frames);

    public static WaveformFrame RestoreWaveformFrame(
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        RationalTime proxyDuration,
        IReadOnlyList<WaveformChannelExtrema> channels) =>
        new(
            proxyFrameSequence,
            proxyPresentationTime,
            proxyDuration,
            channels);

    public static WaveformChannelExtrema RestoreWaveformChannel(
        int channelIndex,
        short minimumSample,
        short maximumSample) =>
        new(channelIndex, minimumSample, maximumSample);

    public static ThumbnailSpriteIndex RestoreSpriteIndex(
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
        IReadOnlyList<ThumbnailSpriteCell> cells) =>
        new(
            sourceAssetId,
            projectId,
            proxyArtifactId,
            proxyContentDigest,
            proxyPtsIndexDigest,
            spriteImageArtifactId,
            spriteImageDigest,
            profileVersion,
            cellWidth,
            cellHeight,
            columns,
            rows,
            cells);

    public static ThumbnailSpriteCell RestoreSpriteCell(
        int ordinal,
        int proxyFrameSequence,
        RationalTime proxyPresentationTime,
        RationalTime proxyDuration,
        int column,
        int row) =>
        new(
            ordinal,
            proxyFrameSequence,
            proxyPresentationTime,
            proxyDuration,
            column,
            row);

    public static PreviewAssetGenerationProfile RestorePreviewProfile(
        string profileVersion)
    {
        var profile = new PreviewAssetGenerationProfile();
        if (!string.Equals(
                profile.Version,
                profileVersion,
                StringComparison.Ordinal))
        {
            throw Failure();
        }

        return profile;
    }

    public static GeneratedPreviewAssets RestoreGeneratedPreviewAssets(
        StoredArtifact waveformIndexArtifact,
        StoredArtifact spriteImageArtifact,
        StoredArtifact spriteIndexArtifact,
        PreviewWaveformIndex waveformIndex,
        ThumbnailSpriteIndex spriteIndex,
        PreviewAssetGenerationProfile profile) =>
        new(
            waveformIndexArtifact,
            spriteImageArtifact,
            spriteIndexArtifact,
            waveformIndex,
            spriteIndex,
            profile);

    private static string GetExpectedStagingReference(
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        EntityId<AssetIdTag> candidateId) =>
        string.Concat(
            "projects/",
            projectId.ToString(),
            "/staging/imports/",
            jobId.ToString(),
            "/",
            candidateId.ToString(),
            ".ready");

    private static string ValidateWorkspace(string workspacePath)
    {
        string workspace = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(workspace);
            return workspace;
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure();
        }
    }

    private static void ValidateStagingPath(string workspace, string stagingPath)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                workspace,
                stagingPath,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure();
        }
    }

    private static void ValidateOpenedFile(FileStream stream)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(stream.SafeFileHandle);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw Failure();
        }

        try
        {
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure();
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw Failure();
        }
    }

    private static bool ProfileMatches(
        ProxyGenerationProfile actual,
        ProxyGenerationProfile expected) =>
        string.Equals(actual.EvidenceLabel, expected.EvidenceLabel, StringComparison.Ordinal) &&
        actual.Width == expected.Width &&
        actual.Height == expected.Height &&
        actual.FrameRate == expected.FrameRate &&
        string.Equals(actual.ScaleFlags, expected.ScaleFlags, StringComparison.Ordinal) &&
        string.Equals(actual.PixelFormat, expected.PixelFormat, StringComparison.Ordinal) &&
        string.Equals(actual.VideoEncoder, expected.VideoEncoder, StringComparison.Ordinal) &&
        string.Equals(actual.EncoderPreset, expected.EncoderPreset, StringComparison.Ordinal) &&
        actual.ConstantQuality == expected.ConstantQuality &&
        string.Equals(actual.VideoBitRate, expected.VideoBitRate, StringComparison.Ordinal) &&
        actual.GopSize == expected.GopSize &&
        string.Equals(actual.AudioCodec, expected.AudioCodec, StringComparison.Ordinal) &&
        string.Equals(actual.AudioBitRate, expected.AudioBitRate, StringComparison.Ordinal) &&
        actual.MetadataStripped == expected.MetadataStripped &&
        actual.FastStart == expected.FastStart;

    private static MediaCheckpointValidationException Failure() => new();
}
