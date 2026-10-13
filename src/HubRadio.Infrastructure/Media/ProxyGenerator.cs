using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

public enum ProxyGenerationFailureReason
{
    InputBindingMismatch,
    RegisteredOriginalInvalid,
    ToolVerificationFailed,
    SourceProbeFailed,
    ProcessTimedOut,
    StagingConflict,
    StagingUnavailable,
    GenerationFailed,
    OutputInvalid,
    OutputProfileMismatch,
    GopValidationFailed,
    PromotionFailed,
    RegisteredProxyInvalid,
    CleanupFailed,
}

public sealed class ProxyGenerationException : Exception
{
    internal ProxyGenerationException(
        ProxyGenerationFailureReason reason,
        bool reconciliationRequired)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public ProxyGenerationFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(ProxyGenerationFailureReason reason) =>
        reason switch
        {
            ProxyGenerationFailureReason.InputBindingMismatch =>
                "The proxy generation inputs do not identify the same original media.",
            ProxyGenerationFailureReason.RegisteredOriginalInvalid =>
                "The registered original could not be verified for proxy generation.",
            ProxyGenerationFailureReason.ToolVerificationFailed =>
                "The proxy generation tool could not be verified.",
            ProxyGenerationFailureReason.SourceProbeFailed =>
                "The registered original could not be probed for proxy generation.",
            ProxyGenerationFailureReason.ProcessTimedOut =>
                "The proxy generation process exceeded its timeout.",
            ProxyGenerationFailureReason.StagingConflict =>
                "The proxy staging destination already exists.",
            ProxyGenerationFailureReason.StagingUnavailable =>
                "The proxy staging destination is unavailable.",
            ProxyGenerationFailureReason.GenerationFailed =>
                "The proxy generation process failed.",
            ProxyGenerationFailureReason.OutputInvalid =>
                "The generated proxy output is invalid.",
            ProxyGenerationFailureReason.OutputProfileMismatch =>
                "The generated proxy does not match the required profile.",
            ProxyGenerationFailureReason.GopValidationFailed =>
                "The generated proxy does not satisfy the required key-frame spacing.",
            ProxyGenerationFailureReason.PromotionFailed =>
                "The generated proxy could not be promoted.",
            ProxyGenerationFailureReason.RegisteredProxyInvalid =>
                "The registered proxy could not be verified.",
            ProxyGenerationFailureReason.CleanupFailed =>
                "Proxy staging cleanup requires reconciliation.",
            _ => "Proxy generation failed.",
        };
}

public sealed class ProxyGenerationProfile
{
    // These are the two exact Stage 0 encoder requests. They select a reproducible
    // representation path; their measured quality is not a production threshold.
    internal const string Stage0EvidenceLabel = "stage0-proxy-640x360-v1";
    internal const int Stage0Width = 640;
    internal const int Stage0Height = 360;
    internal const string Stage0ScaleFlags = "bilinear";
    internal const string Stage0PixelFormat = "yuv420p";
    internal const string Stage0VideoEncoder = "h264_nvenc";
    internal const string Stage0EncoderPreset = "p4";
    internal const int Stage0ConstantQuality = 28;
    internal const string Stage0VideoBitRate = "0";
    internal const string MediaFoundationVideoEncoder = "h264_mf";
    internal const string MediaFoundationVideoBitRate = "4M";
    internal const int Stage0GopSize = 30;
    internal const string Stage0AudioCodec = "aac";
    internal const string Stage0AudioBitRate = "96k";

    private static readonly RationalNumber Stage0FrameRate = new(30000, 1001);

    internal ProxyGenerationProfile()
        : this(
            Stage0VideoEncoder,
            Stage0EncoderPreset,
            Stage0ConstantQuality,
            Stage0VideoBitRate)
    {
    }

    private ProxyGenerationProfile(
        string videoEncoder,
        string? encoderPreset,
        int? constantQuality,
        string videoBitRate)
    {
        EvidenceLabel = Stage0EvidenceLabel;
        Width = Stage0Width;
        Height = Stage0Height;
        FrameRate = Stage0FrameRate;
        ScaleFlags = Stage0ScaleFlags;
        PixelFormat = Stage0PixelFormat;
        VideoEncoder = videoEncoder;
        EncoderPreset = encoderPreset;
        ConstantQuality = constantQuality;
        VideoBitRate = videoBitRate;
        GopSize = Stage0GopSize;
        AudioCodec = Stage0AudioCodec;
        AudioBitRate = Stage0AudioBitRate;
        MetadataStripped = true;
        FastStart = true;
    }

    public string EvidenceLabel { get; }

    public int Width { get; }

    public int Height { get; }

    public RationalNumber FrameRate { get; }

    public string ScaleFlags { get; }

    public string PixelFormat { get; }

    public string VideoEncoder { get; }

    public string? EncoderPreset { get; }

    public int? ConstantQuality { get; }

    public string VideoBitRate { get; }

    public int GopSize { get; }

    public string AudioCodec { get; }

    public string AudioBitRate { get; }

    public bool MetadataStripped { get; }

    public bool FastStart { get; }

    internal static ProxyGenerationProfile CreateMediaFoundationFallback() =>
        new(
            MediaFoundationVideoEncoder,
            encoderPreset: null,
            constantQuality: null,
            MediaFoundationVideoBitRate);
}

public sealed class GeneratedProxy
{
    internal GeneratedProxy(
        EntityId<AssetIdTag> sourceAssetId,
        ContentDigest sourceContentDigest,
        EntityId<ArtifactIdTag> sourceOriginalArtifactId,
        StoredArtifact proxyArtifact,
        MediaProbeResult proxyProbeResult,
        PtsIndex proxyPtsIndex,
        ProxyGenerationProfile profile,
        TimeSpan generationElapsed)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(sourceContentDigest);
        ArgumentNullException.ThrowIfNull(sourceOriginalArtifactId);
        ArgumentNullException.ThrowIfNull(proxyArtifact);
        ArgumentNullException.ThrowIfNull(proxyProbeResult);
        ArgumentNullException.ThrowIfNull(proxyPtsIndex);
        ArgumentNullException.ThrowIfNull(profile);

        if (proxyArtifact.Kind != ArtifactKind.Proxy ||
            proxyProbeResult.CandidateId != sourceAssetId ||
            proxyPtsIndex.CandidateId != sourceAssetId ||
            proxyArtifact.ProjectId != proxyProbeResult.ProjectId ||
            proxyArtifact.ProjectId != proxyPtsIndex.ProjectId ||
            proxyProbeResult.JobId != proxyPtsIndex.JobId ||
            proxyArtifact.ContentDigest != proxyProbeResult.ContentDigest ||
            proxyArtifact.ContentDigest != proxyPtsIndex.ContentDigest ||
            proxyArtifact.ByteSize != proxyProbeResult.ByteSize ||
            proxyArtifact.ByteSize != proxyPtsIndex.ByteSize ||
            !string.Equals(
                proxyProbeResult.SourceFileName,
                proxyPtsIndex.SourceFileName,
                StringComparison.Ordinal) ||
            generationElapsed < TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Generated proxy evidence must identify one verified proxy artifact.",
                nameof(proxyArtifact));
        }

        SourceAssetId = sourceAssetId;
        SourceContentDigest = sourceContentDigest;
        SourceOriginalArtifactId = sourceOriginalArtifactId;
        ProxyArtifact = proxyArtifact;
        ProxyProbeResult = proxyProbeResult;
        ProxyPtsIndex = proxyPtsIndex;
        Profile = profile;
        GenerationElapsed = generationElapsed;
    }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public ContentDigest SourceContentDigest { get; }

    public EntityId<ArtifactIdTag> SourceOriginalArtifactId { get; }

    public StoredArtifact ProxyArtifact { get; }

    public MediaProbeResult ProxyProbeResult { get; }

    public PtsIndex ProxyPtsIndex { get; }

    public ProxyGenerationProfile Profile { get; }

    public TimeSpan GenerationElapsed { get; }
}

public sealed class ProxyGenerator
{
    private const int FileBufferSize = 64 * 1024;
    private const int Sha256ByteLength = 32;
    private const string ToolComponentIdentity =
        "tool.ffmpeg." +
        DecodeValidator.CanonicalPackageId + "." +
        DecodeValidator.CanonicalPackageVersion + ".proxy";

    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(DecodeValidator.CanonicalFfmpegSha256);
    private static readonly ProxyGenerationProfile Stage0Profile = new();
    private static readonly ProxyGenerationProfile MediaFoundationProfile =
        ProxyGenerationProfile.CreateMediaFoundationFallback();
    private static readonly RationalNumber RequiredFrameRate = new(30000, 1001);
    private static readonly RationalTime RequiredFrameDuration = new(1001, 30000);

    private readonly ArtifactStore _artifactStore;
    private readonly Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> _runProcessAsync;
    private readonly Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyBinaryAsync;
    private readonly Func<
        StagedMediaImport,
        TimeSpan,
        CancellationToken,
        Task<MediaProbeResult>> _probeAsync;
    private readonly Func<
        StagedMediaImport,
        MediaProbeResult,
        TimeSpan,
        CancellationToken,
        Task<PtsIndex>> _indexAsync;
    private readonly Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyRegisteredAsync;
    private readonly string _workspacePath;
    private readonly string _ffmpegPath;
    private readonly TimeProvider _timeProvider;

    public ProxyGenerator(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        FfprobeClient ffprobeClient,
        PtsIndexer ptsIndexer,
        string workspacePath,
        string ffmpegPath)
        : this(
            artifactStore,
            GetProcessDelegate(processRunner),
            GetBinaryVerificationDelegate(binaryVerifier),
            GetProbeDelegate(ffprobeClient),
            GetIndexDelegate(ptsIndexer),
            GetRegisteredVerificationDelegate(artifactStore, binaryVerifier),
            workspacePath,
            ffmpegPath,
            TimeProvider.System)
    {
    }

    internal ProxyGenerator(
        ArtifactStore artifactStore,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>> verifyBinaryAsync,
        Func<StagedMediaImport, TimeSpan, CancellationToken, Task<MediaProbeResult>> probeAsync,
        Func<StagedMediaImport, MediaProbeResult, TimeSpan, CancellationToken, Task<PtsIndex>> indexAsync,
        Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>> verifyRegisteredAsync,
        string workspacePath,
        string ffmpegPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(probeAsync);
        ArgumentNullException.ThrowIfNull(indexAsync);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);

        _artifactStore = artifactStore;
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
        _probeAsync = probeAsync;
        _indexAsync = indexAsync;
        _verifyRegisteredAsync = verifyRegisteredAsync;
        _workspacePath = ValidateWorkspacePath(workspacePath);
        _ffmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GeneratedProxy> GenerateAsync(
        EntityId<ArtifactIdTag> proxyArtifactId,
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        PtsIndex sourcePtsIndex,
        EntityId<JobIdTag> proxyJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxyArtifactId);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourceOriginal);
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(proxyJobId);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);
        ValidateInputBinding(sourceAsset, sourceOriginal, sourcePtsIndex);
        EnsureProxyArtifactIdAvailable(proxyArtifactId);

        string sourcePath = GetRegisteredOriginalPath(sourceOriginal);
        using FileStream sourceStream = OpenRegisteredOriginal(sourcePath);
        HeldFileSnapshot sourceSnapshot = CaptureHeldSource(
            sourceStream,
            sourceOriginal.ByteSize);
        await VerifyRegisteredExactAsync(
            sourceOriginal,
            ProxyGenerationFailureReason.RegisteredOriginalInvalid,
            reconciliationRequired: true,
            cancellationToken).ConfigureAwait(false);
        ValidateRegisteredSourcePath(sourceOriginal, sourcePath);
        ValidateHeldSource(sourceStream, sourceSnapshot);

        StagedMediaImport sourceMedia = new(
            sourceAsset.Id,
            sourceOriginal.ProjectId,
            sourcePtsIndex.JobId,
            sourceAsset.FileName,
            sourcePath,
            sourceAsset.ContentDigest,
            sourceAsset.ByteSize,
            sourceOriginal.CreatedAt);
        MediaProbeResult sourceProbe = await ProbeSourceAsync(
            sourceMedia,
            timeout,
            cancellationToken).ConfigureAwait(false);
        (ProbedMediaStream sourceVideo, ProbedMediaStream? sourceAudio) =
            ValidateSourceEvidence(sourceMedia, sourceProbe, sourcePtsIndex);

        await VerifyFfmpegAsync(cancellationToken).ConfigureAwait(false);
        ValidateHeldSource(sourceStream, sourceSnapshot);

        StagingPaths staging = PrepareStaging(
            sourceOriginal.ProjectId,
            proxyJobId,
            proxyArtifactId,
            cancellationToken);
        bool promoted = false;

        try
        {
            if (!WorkspacePathSafety.HasSameWindowsDriveRoot(
                    sourcePath,
                    staging.OutputPath))
            {
                throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
            }

            (ProcessRunResult processResult, ProxyGenerationProfile selectedProfile) =
                await RunGenerationWithFallbackAsync(
                    sourcePath,
                    staging,
                    sourceVideo.Index,
                    sourceAudio?.Index,
                    timeout,
                    sourceStream,
                    sourceSnapshot,
                    cancellationToken).ConfigureAwait(false);
            ValidateHeldSource(sourceStream, sourceSnapshot);

            StagedMediaImport proxyMedia;
            MediaProbeResult proxyProbe;
            PtsIndex proxyPtsIndex;
            using (FileStream outputStream = OpenGeneratedOutput(staging.OutputPath))
            {
                HeldFileSnapshot outputSnapshot = CaptureHeldOutput(outputStream);
                ContentDigest outputDigest = await ComputeDigestAsync(
                    outputStream,
                    cancellationToken).ConfigureAwait(false);
                ValidateHeldOutput(
                    outputStream,
                    outputSnapshot,
                    staging.OutputPath);

                DateTimeOffset createdAt = _timeProvider
                    .GetUtcNow()
                    .ToUniversalTime();
                proxyMedia = new StagedMediaImport(
                    sourceAsset.Id,
                    sourceOriginal.ProjectId,
                    proxyJobId,
                    string.Concat(proxyArtifactId.ToString(), ".mp4"),
                    staging.OutputPath,
                    outputDigest,
                    outputSnapshot.Length,
                    createdAt);
                proxyProbe = await ProbeOutputAsync(
                    proxyMedia,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                proxyPtsIndex = await IndexOutputAsync(
                    proxyMedia,
                    proxyProbe,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                ValidateOutputEvidence(
                    proxyMedia,
                    proxyProbe,
                    proxyPtsIndex,
                    sourceAudio is not null);
                ValidateHeldOutput(
                    outputStream,
                    outputSnapshot,
                    staging.OutputPath);
            }

            ValidateHeldSource(sourceStream, sourceSnapshot);
            ValidateRegisteredSourcePath(sourceOriginal, sourcePath);
            await VerifyRegisteredExactAsync(
                sourceOriginal,
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
            ValidateHeldSource(sourceStream, sourceSnapshot);

            StoredArtifact proxyArtifact = await PromoteAsync(
                proxyArtifactId,
                sourceAsset.Id,
                sourceOriginal.ProjectId,
                proxyMedia,
                cancellationToken).ConfigureAwait(false);
            promoted = true;

            if (!TryCleanupOwnedStaging(staging))
            {
                throw Failure(
                    ProxyGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            ValidatePromotedMetadata(
                proxyArtifact,
                proxyArtifactId,
                sourceAsset.Id,
                sourceOriginal.ProjectId,
                proxyMedia);
            await VerifyRegisteredExactAsync(
                proxyArtifact,
                ProxyGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new GeneratedProxy(
                sourceAsset.Id,
                sourceAsset.ContentDigest,
                sourceOriginal.Id,
                proxyArtifact,
                proxyProbe,
                proxyPtsIndex,
                selectedProfile,
                processResult.Duration);
        }
        catch (Exception exception)
        {
            if (!promoted && !TryCleanupOwnedStaging(staging))
            {
                throw Failure(
                    ProxyGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (promoted)
            {
                if (exception is ProxyGenerationException proxyException &&
                    proxyException.ReconciliationRequired)
                {
                    throw;
                }

                throw Failure(
                    ProxyGenerationFailureReason.RegisteredProxyInvalid,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is ProxyGenerationException)
            {
                throw;
            }

            throw Failure(ProxyGenerationFailureReason.GenerationFailed);
        }
    }

    internal static void ValidateInputBinding(
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        PtsIndex sourcePtsIndex)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourceOriginal);
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);

        if (sourceOriginal.Kind != ArtifactKind.Original ||
            sourceAsset.Id != sourcePtsIndex.CandidateId ||
            !string.Equals(
                sourceAsset.FileName,
                sourcePtsIndex.SourceFileName,
                StringComparison.Ordinal) ||
            sourceAsset.ContentDigest != sourcePtsIndex.ContentDigest ||
            sourceAsset.ByteSize != sourcePtsIndex.ByteSize ||
            sourceOriginal.ProjectId != sourcePtsIndex.ProjectId ||
            !string.Equals(
                sourceOriginal.StorageReference,
                BuildOriginalStorageReference(sourceAsset, sourceOriginal.ProjectId),
                StringComparison.Ordinal) ||
            sourceOriginal.ContentDigest != sourceAsset.ContentDigest ||
            sourceOriginal.ByteSize != sourceAsset.ByteSize)
        {
            throw Failure(ProxyGenerationFailureReason.InputBindingMismatch);
        }
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan timeout) =>
        CreateProcessRequest(
            ffmpegPath,
            sourcePath,
            outputPath,
            videoStreamIndex,
            audioStreamIndex,
            timeout,
            Stage0Profile);

    private static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan timeout,
        ProxyGenerationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (videoStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoStreamIndex),
                "A selected FFmpeg video stream index must not be negative.");
        }

        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioStreamIndex),
                "A selected FFmpeg audio stream index must not be negative when present.");
        }

        string normalizedFfmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string normalizedSourcePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            sourcePath,
            nameof(sourcePath));
        string normalizedOutputPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            outputPath,
            nameof(outputPath));
        string? workingDirectory = Path.GetDirectoryName(normalizedOutputPath);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            throw new ArgumentException(
                "The proxy output path must have a parent directory.",
                nameof(outputPath));
        }

        List<string> arguments =
        [
            "-nostdin",
            "-v",
            "error",
            "-i",
            normalizedSourcePath,
            "-map",
            string.Concat(
                "0:",
                videoStreamIndex.ToString(CultureInfo.InvariantCulture)),
        ];
        if (audioStreamIndex is not null)
        {
            arguments.Add("-map");
            arguments.Add(string.Concat(
                "0:",
                audioStreamIndex.Value.ToString(CultureInfo.InvariantCulture)));
        }

        arguments.Add("-vf");
        arguments.Add(
            "scale=640:360:flags=bilinear,fps=30000/1001,format=yuv420p");
        arguments.Add("-c:v");
        arguments.Add(profile.VideoEncoder);
        if (profile.EncoderPreset is string encoderPreset)
        {
            arguments.Add("-preset");
            arguments.Add(encoderPreset);
        }

        if (profile.ConstantQuality is int constantQuality)
        {
            arguments.Add("-cq");
            arguments.Add(constantQuality.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-b:v");
        arguments.Add(profile.VideoBitRate);
        arguments.Add("-g");
        arguments.Add(ProxyGenerationProfile.Stage0GopSize.ToString(
            CultureInfo.InvariantCulture));
        if (audioStreamIndex is not null)
        {
            arguments.Add("-c:a");
            arguments.Add(ProxyGenerationProfile.Stage0AudioCodec);
            arguments.Add("-b:a");
            arguments.Add(ProxyGenerationProfile.Stage0AudioBitRate);
        }

        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add("-movflags");
        arguments.Add("+faststart");
        arguments.Add("-n");
        arguments.Add(normalizedOutputPath);

        return new ProcessRunRequest(
            normalizedFfmpegPath,
            arguments,
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    private async Task<(ProcessRunResult Result, ProxyGenerationProfile Profile)>
        RunGenerationWithFallbackAsync(
            string sourcePath,
            StagingPaths staging,
            int videoStreamIndex,
            int? audioStreamIndex,
            TimeSpan timeout,
            FileStream sourceStream,
            HeldFileSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
    {
        ProcessRunRequest primaryRequest = CreateProcessRequest(
            _ffmpegPath,
            sourcePath,
            staging.OutputPath,
            videoStreamIndex,
            audioStreamIndex,
            timeout,
            Stage0Profile);
        if (WorkspacePathSafety.PathEntryExistsConservatively(staging.OutputPath))
        {
            throw Failure(
                ProxyGenerationFailureReason.StagingConflict,
                reconciliationRequired: true);
        }

        try
        {
            ProcessRunResult primary = await RunGenerationAsync(
                primaryRequest,
                cancellationToken).ConfigureAwait(false);
            ValidateProcessResult(primary);
            return (primary, Stage0Profile);
        }
        catch (ProxyGenerationException exception)
            when (exception.Reason is
                    ProxyGenerationFailureReason.GenerationFailed or
                    ProxyGenerationFailureReason.ProcessTimedOut &&
                !exception.ReconciliationRequired)
        {
            if (WorkspacePathSafety.PathEntryExistsConservatively(
                    staging.OutputPath))
            {
                DeleteFailedOutputForFallback(staging);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateHeldSource(sourceStream, sourceSnapshot);
        ProcessRunRequest fallbackRequest = CreateProcessRequest(
            _ffmpegPath,
            sourcePath,
            staging.OutputPath,
            videoStreamIndex,
            audioStreamIndex,
            timeout,
            MediaFoundationProfile);
        ProcessRunResult fallback = await RunGenerationAsync(
            fallbackRequest,
            cancellationToken).ConfigureAwait(false);
        ValidateProcessResult(fallback);
        return (fallback, MediaFoundationProfile);
    }

    private void DeleteFailedOutputForFallback(StagingPaths staging)
    {
        try
        {
            if (!WorkspacePathSafety.TryGetAttributes(
                    staging.OutputPath,
                    out FileAttributes attributes))
            {
                return;
            }

            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            WorkspacePathSafety.ValidateExistingPathChain(
                staging.DirectoryPath,
                staging.OutputPath,
                targetIsDirectory: false);
            File.Delete(staging.OutputPath);
            if (WorkspacePathSafety.PathEntryExistsConservatively(
                    staging.OutputPath))
            {
                throw new IOException(
                    "The failed proxy output could not be removed.");
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException or
                WorkspacePathSafetyException)
        {
            throw Failure(
                ProxyGenerationFailureReason.CleanupFailed,
                reconciliationRequired: true);
        }
    }

    internal static void ValidateProcessResult(ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(processResult);

        if (processResult.ExitCode != 0 ||
            processResult.Truncated ||
            processResult.StandardOutput is null ||
            processResult.StandardOutput.Length != 0 ||
            processResult.StandardError is null ||
            processResult.StandardError.Length != 0 ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(ProxyGenerationFailureReason.GenerationFailed);
        }
    }

    internal static void ValidateOutputEvidence(
        StagedMediaImport proxyMedia,
        MediaProbeResult proxyProbe,
        PtsIndex proxyPtsIndex,
        bool sourceHasAudio)
    {
        ArgumentNullException.ThrowIfNull(proxyMedia);
        ArgumentNullException.ThrowIfNull(proxyProbe);
        ArgumentNullException.ThrowIfNull(proxyPtsIndex);

        if (!HasSameSourceIdentity(proxyMedia, proxyProbe) ||
            !HasSameSourceIdentity(proxyMedia, proxyPtsIndex))
        {
            throw Failure(ProxyGenerationFailureReason.OutputProfileMismatch);
        }

        ProbedMediaStream[] videoStreams = proxyProbe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .ToArray();
        ProbedMediaStream[] audioStreams = proxyProbe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .ToArray();
        int otherStreamCount = proxyProbe.Streams.Count(
            static stream => stream.Kind == MediaStreamKind.Other);
        int? lowestStreamIndex = proxyProbe.Streams.Count == 0
            ? null
            : proxyProbe.Streams.Min(static stream => stream.Index);

        if (videoStreams.Length != 1 ||
            videoStreams[0].Index != lowestStreamIndex ||
            !string.Equals(
                videoStreams[0].CodecName,
                "h264",
                StringComparison.Ordinal) ||
            videoStreams[0].Width != ProxyGenerationProfile.Stage0Width ||
            videoStreams[0].Height != ProxyGenerationProfile.Stage0Height ||
            !string.Equals(
                videoStreams[0].PixelFormat,
                ProxyGenerationProfile.Stage0PixelFormat,
                StringComparison.Ordinal) ||
            videoStreams[0].NominalFrameRate != RequiredFrameRate ||
            videoStreams[0].AverageFrameRate != RequiredFrameRate ||
            proxyProbe.ContainerSignature != ContainerSignature.IsoBaseMediaFtyp ||
            !proxyProbe.FormatNames.Contains("mov", StringComparer.Ordinal) ||
            !proxyProbe.FormatNames.Contains("mp4", StringComparer.Ordinal) ||
            proxyProbe.DeclaredByteSize != proxyMedia.ByteSize ||
            otherStreamCount != 0 ||
            proxyPtsIndex.StreamIndex != videoStreams[0].Index ||
            proxyPtsIndex.TimeBase != new RationalTime(
                videoStreams[0].TimeBase.Numerator,
                videoStreams[0].TimeBase.Denominator) ||
            !HasConstantFrameTiming(proxyPtsIndex))
        {
            throw Failure(ProxyGenerationFailureReason.OutputProfileMismatch);
        }

        bool audioWarningPresent = proxyProbe.Warnings.Contains(
            MediaProbeResult.AudioTrackAbsentWarning,
            StringComparer.Ordinal);
        if (sourceHasAudio)
        {
            if (audioStreams.Length != 1 ||
                !string.Equals(
                    audioStreams[0].CodecName,
                    ProxyGenerationProfile.Stage0AudioCodec,
                    StringComparison.Ordinal) ||
                audioWarningPresent ||
                proxyProbe.Warnings.Count != 0)
            {
                throw Failure(ProxyGenerationFailureReason.OutputProfileMismatch);
            }
        }
        else if (audioStreams.Length != 0 ||
            !audioWarningPresent ||
            proxyProbe.Warnings.Count != 1)
        {
            throw Failure(ProxyGenerationFailureReason.OutputProfileMismatch);
        }

        ValidateGop(proxyPtsIndex);
    }

    private static bool HasConstantFrameTiming(PtsIndex proxyPtsIndex)
    {
        try
        {
            for (int index = 0; index < proxyPtsIndex.Entries.Count; index++)
            {
                PtsIndexEntry entry = proxyPtsIndex.Entries[index];
                if (entry.Duration != RequiredFrameDuration ||
                    (index > 0 &&
                        entry.PresentationTime -
                        proxyPtsIndex.Entries[index - 1].PresentationTime !=
                        RequiredFrameDuration))
                {
                    return false;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return true;
    }

    internal static void ValidateGop(PtsIndex proxyPtsIndex)
    {
        ArgumentNullException.ThrowIfNull(proxyPtsIndex);

        if (!proxyPtsIndex.Entries[0].KeyFrame)
        {
            throw Failure(ProxyGenerationFailureReason.GopValidationFailed);
        }

        int previousKeyFrameSequence = 0;
        for (int index = 1; index < proxyPtsIndex.Entries.Count; index++)
        {
            PtsIndexEntry entry = proxyPtsIndex.Entries[index];
            if (!entry.KeyFrame)
            {
                continue;
            }

            if (entry.Sequence - previousKeyFrameSequence >
                ProxyGenerationProfile.Stage0GopSize)
            {
                throw Failure(ProxyGenerationFailureReason.GopValidationFailed);
            }

            previousKeyFrameSequence = entry.Sequence;
        }

        if (proxyPtsIndex.Entries.Count - previousKeyFrameSequence >
            ProxyGenerationProfile.Stage0GopSize)
        {
            throw Failure(ProxyGenerationFailureReason.GopValidationFailed);
        }
    }

    private static Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
        GetProcessDelegate(ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> GetBinaryVerificationDelegate(
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return binaryVerifier.VerifyAsync;
    }

    private static Func<
        StagedMediaImport,
        TimeSpan,
        CancellationToken,
        Task<MediaProbeResult>> GetProbeDelegate(FfprobeClient ffprobeClient)
    {
        ArgumentNullException.ThrowIfNull(ffprobeClient);
        return ffprobeClient.ProbeAsync;
    }

    internal static Func<
        StagedMediaImport,
        MediaProbeResult,
        TimeSpan,
        CancellationToken,
        Task<PtsIndex>> GetIndexDelegate(PtsIndexer ptsIndexer)
    {
        ArgumentNullException.ThrowIfNull(ptsIndexer);
        return async (stagedMedia, probeResult, timeout, cancellationToken) =>
        {
            try
            {
                return await ptsIndexer.IndexAsync(
                    stagedMedia,
                    probeResult,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PtsIndexException exception)
                when (exception.Reason is
                    PtsIndexFailureReason.MissingDuration or
                    PtsIndexFailureReason.UnexpectedField)
            {
                return await ptsIndexer.IndexPacketTimelineAsync(
                    stagedMedia,
                    probeResult,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
        };
    }

    private static Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> GetRegisteredVerificationDelegate(
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
                "The proxy generation timeout must be positive and supported by the runtime timer.");
        }
    }

    private void EnsureProxyArtifactIdAvailable(
        EntityId<ArtifactIdTag> proxyArtifactId)
    {
        try
        {
            if (_artifactStore.GetArtifact(proxyArtifactId) is not null)
            {
                throw Failure(ProxyGenerationFailureReason.PromotionFailed);
            }
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private string GetRegisteredOriginalPath(StoredArtifact sourceOriginal)
    {
        StoredArtifact? registered;
        try
        {
            registered = _artifactStore.GetArtifact(sourceOriginal.Id);
        }
        catch (Exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }

        if (registered is null)
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }

        if (!HasSameMetadata(sourceOriginal, registered))
        {
            throw Failure(ProxyGenerationFailureReason.InputBindingMismatch);
        }

        string sourcePath;
        try
        {
            sourcePath = _artifactStore.GetValidatedRegisteredArtifactPath(
                sourceOriginal.Id);
        }
        catch (Exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }

        if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                _workspacePath,
                sourceOriginal.StorageReference,
                out string expectedPath) ||
            !string.Equals(
                sourcePath,
                expectedPath,
                WorkspacePathSafety.PathComparison))
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }

        return sourcePath;
    }

    private void ValidateRegisteredSourcePath(
        StoredArtifact sourceOriginal,
        string expectedPath)
    {
        try
        {
            string currentPath = _artifactStore.GetValidatedRegisteredArtifactPath(
                sourceOriginal.Id);
            StoredArtifact current = _artifactStore.GetArtifact(sourceOriginal.Id)
                ?? throw Failure(
                    ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            if (!string.Equals(
                    currentPath,
                    expectedPath,
                    WorkspacePathSafety.PathComparison) ||
                !HasSameMetadata(sourceOriginal, current))
            {
                throw Failure(
                    ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            }
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private async Task VerifyRegisteredExactAsync(
        StoredArtifact expected,
        ProxyGenerationFailureReason failureReason,
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
                result.ExpectedByteSize != expected.ByteSize ||
                result.ActualDigest != expected.ContentDigest ||
                result.ActualByteSize != expected.ByteSize)
            {
                throw Failure(failureReason, reconciliationRequired);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason, reconciliationRequired);
        }
    }

    private async Task VerifyFfmpegAsync(CancellationToken cancellationToken)
    {
        var request = new BinaryVerificationRequest(
            ToolComponentIdentity,
            _ffmpegPath,
            CanonicalFfmpegDigest,
            DecodeValidator.CanonicalFfmpegByteSize);
        try
        {
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified ||
                result.ExpectedDigest != CanonicalFfmpegDigest ||
                result.ExpectedByteSize != DecodeValidator.CanonicalFfmpegByteSize ||
                result.ActualDigest != CanonicalFfmpegDigest ||
                result.ActualByteSize != DecodeValidator.CanonicalFfmpegByteSize)
            {
                throw Failure(ProxyGenerationFailureReason.ToolVerificationFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(ProxyGenerationFailureReason.ToolVerificationFailed);
        }
    }

    private async Task<MediaProbeResult> ProbeSourceAsync(
        StagedMediaImport sourceMedia,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probeAsync(
                sourceMedia,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is TimeoutException or MediaProbeException
            {
                Reason: MediaProbeFailureReason.ProcessTimedOut,
            })
        {
            throw Failure(ProxyGenerationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(ProxyGenerationFailureReason.SourceProbeFailed);
        }
    }

    private static (
        ProbedMediaStream VideoStream,
        ProbedMediaStream? AudioStream) ValidateSourceEvidence(
        StagedMediaImport sourceMedia,
        MediaProbeResult sourceProbe,
        PtsIndex sourcePtsIndex)
    {
        if (!HasSameSourceIdentity(sourceMedia, sourceProbe) ||
            !HasSameSourceIdentity(sourceMedia, sourcePtsIndex))
        {
            throw Failure(ProxyGenerationFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream videoStream = sourceProbe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .MinBy(static stream => stream.Index)
            ?? throw Failure(ProxyGenerationFailureReason.SourceProbeFailed);
        RationalTime observedTimeBase = new(
            videoStream.TimeBase.Numerator,
            videoStream.TimeBase.Denominator);
        if (sourcePtsIndex.StreamIndex != videoStream.Index ||
            sourcePtsIndex.TimeBase != observedTimeBase)
        {
            throw Failure(ProxyGenerationFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream? audioStream = sourceProbe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .MinBy(static stream => stream.Index);
        return (videoStream, audioStream);
    }

    private async Task<ProcessRunResult> RunGenerationAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(ProxyGenerationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(ProxyGenerationFailureReason.GenerationFailed);
        }
    }

    private async Task<MediaProbeResult> ProbeOutputAsync(
        StagedMediaImport proxyMedia,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probeAsync(
                proxyMedia,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is TimeoutException or MediaProbeException
            {
                Reason: MediaProbeFailureReason.ProcessTimedOut,
            })
        {
            throw Failure(ProxyGenerationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
    }

    private async Task<PtsIndex> IndexOutputAsync(
        StagedMediaImport proxyMedia,
        MediaProbeResult proxyProbe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _indexAsync(
                proxyMedia,
                proxyProbe,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is TimeoutException or PtsIndexException
            {
                Reason: PtsIndexFailureReason.ProcessTimedOut,
            })
        {
            throw Failure(ProxyGenerationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
    }

    private async Task<StoredArtifact> PromoteAsync(
        EntityId<ArtifactIdTag> proxyArtifactId,
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        StagedMediaImport proxyMedia,
        CancellationToken cancellationToken)
    {
        var request = new ArtifactPromotionRequest(
            proxyArtifactId,
            projectId,
            ArtifactKind.Proxy,
            proxyMedia.StagingPath,
            BuildProxyRelativePath(sourceAssetId, proxyArtifactId),
            proxyMedia.ContentDigest,
            proxyMedia.ByteSize,
            proxyMedia.CreatedAt);
        try
        {
            return await _artifactStore.PromoteAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                ProxyGenerationFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private static void ValidatePromotedMetadata(
        StoredArtifact proxyArtifact,
        EntityId<ArtifactIdTag> proxyArtifactId,
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ProjectIdTag> projectId,
        StagedMediaImport proxyMedia)
    {
        string expectedStorageReference = string.Concat(
            "projects/",
            projectId.ToString(),
            "/proxy/",
            BuildProxyRelativePath(sourceAssetId, proxyArtifactId));
        if (proxyArtifact.Id != proxyArtifactId ||
            proxyArtifact.ProjectId != projectId ||
            proxyArtifact.Kind != ArtifactKind.Proxy ||
            !string.Equals(
                proxyArtifact.StorageReference,
                expectedStorageReference,
                StringComparison.Ordinal) ||
            proxyArtifact.ContentDigest != proxyMedia.ContentDigest ||
            proxyArtifact.ByteSize != proxyMedia.ByteSize ||
            proxyArtifact.CreatedAt != proxyMedia.CreatedAt)
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredProxyInvalid,
                reconciliationRequired: true);
        }
    }

    private StagingPaths PrepareStaging(
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> proxyJobId,
        EntityId<ArtifactIdTag> proxyArtifactId,
        CancellationToken cancellationToken)
    {
        string proxyStagingRoot = Path.GetFullPath(Path.Combine(
            ProjectFolders.GetPath(_workspacePath, projectId),
            "staging",
            "proxies"));
        string stagingDirectory = Path.Combine(
            proxyStagingRoot,
            proxyJobId.ToString());
        string outputPath = Path.Combine(
            stagingDirectory,
            string.Concat(proxyArtifactId.ToString(), ".partial.mp4"));
        if (!WorkspacePathSafety.IsStrictDescendant(
                _workspacePath,
                proxyStagingRoot) ||
            !WorkspacePathSafety.IsStrictDescendant(
                proxyStagingRoot,
                stagingDirectory) ||
            !WorkspacePathSafety.IsStrictDescendant(
                stagingDirectory,
                outputPath) ||
            !WorkspacePathSafety.HasSameWindowsDriveRoot(
                _workspacePath,
                outputPath))
        {
            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }

        EnsureDirectoryChain(proxyStagingRoot, cancellationToken);
        if (WorkspacePathSafety.PathEntryExistsConservatively(stagingDirectory))
        {
            throw Failure(ProxyGenerationFailureReason.StagingConflict);
        }

        string claimDirectory = Path.Combine(
            proxyStagingRoot,
            string.Concat(
                ".",
                proxyJobId.ToString(),
                ".",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ".claim"));
        bool claimOwned = false;
        bool stagingOwned = false;
        var staging = new StagingPaths(stagingDirectory, outputPath);

        try
        {
            if (WorkspacePathSafety.PathEntryExistsConservatively(claimDirectory))
            {
                throw Failure(ProxyGenerationFailureReason.StagingConflict);
            }

            _ = Directory.CreateDirectory(claimDirectory);
            claimOwned = true;
            ValidateExistingWorkspaceDirectory(claimDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(claimDirectory, stagingDirectory);
                claimOwned = false;
                stagingOwned = true;
            }
            catch (IOException)
            {
                throw Failure(
                    WorkspacePathSafety.PathEntryExistsConservatively(stagingDirectory)
                        ? ProxyGenerationFailureReason.StagingConflict
                        : ProxyGenerationFailureReason.StagingUnavailable);
            }

            ValidateExistingWorkspaceDirectory(stagingDirectory);
            if (WorkspacePathSafety.PathEntryExistsConservatively(outputPath))
            {
                throw Failure(ProxyGenerationFailureReason.StagingConflict);
            }

            return staging;
        }
        catch (Exception exception)
        {
            bool cleaned = true;
            if (stagingOwned)
            {
                cleaned = TryCleanupOwnedStaging(staging);
            }
            else if (claimOwned)
            {
                cleaned = TryDeleteOwnedEmptyDirectory(claimDirectory);
            }

            if (!cleaned)
            {
                throw Failure(
                    ProxyGenerationFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is ProxyGenerationException)
            {
                throw;
            }

            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }
    }

    private void EnsureDirectoryChain(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!WorkspacePathSafety.IsStrictDescendant(
                _workspacePath,
                destinationDirectory))
        {
            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }

        string relativeDirectory = Path.GetRelativePath(
            _workspacePath,
            destinationDirectory);
        string[] segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = _workspacePath;
        ValidateWorkspaceChain();
        ValidateExistingWorkspaceDirectory(current);

        foreach (string segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWorkspaceChain();
            ValidateExistingWorkspaceDirectory(current);
            current = Path.Combine(current, segment);

            bool exists;
            try
            {
                exists = WorkspacePathSafety.TryGetAttributes(
                    current,
                    out FileAttributes attributes);
                if (exists)
                {
                    WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
                }
            }
            catch (WorkspacePathSafetyException)
            {
                throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
            }

            if (!exists)
            {
                try
                {
                    _ = Directory.CreateDirectory(current);
                }
                catch (Exception exception)
                    when (IsFileSystemFailure(exception))
                {
                    throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
                }
            }

            ValidateWorkspaceChain();
            ValidateExistingWorkspaceDirectory(current);
        }

        if (!string.Equals(
                current,
                destinationDirectory,
                WorkspacePathSafety.PathComparison))
        {
            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }
    }

    private void ValidateWorkspaceChain()
    {
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspacePath);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }
    }

    private void ValidateExistingWorkspaceDirectory(string directoryPath)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                directoryPath,
                targetIsDirectory: true);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(ProxyGenerationFailureReason.StagingUnavailable);
        }
    }

    private bool TryCleanupOwnedStaging(StagingPaths staging)
    {
        try
        {
            if (!WorkspacePathSafety.IsStrictDescendant(
                    _workspacePath,
                    staging.DirectoryPath) ||
                !WorkspacePathSafety.IsStrictDescendant(
                    staging.DirectoryPath,
                    staging.OutputPath))
            {
                return false;
            }

            ValidateWorkspaceChain();
            if (WorkspacePathSafety.TryGetAttributes(
                    staging.OutputPath,
                    out FileAttributes outputAttributes))
            {
                WorkspacePathSafety.ValidateRegularFileAttributes(outputAttributes);
                WorkspacePathSafety.ValidateExistingPathChain(
                    _workspacePath,
                    staging.OutputPath,
                    targetIsDirectory: false);
                File.Delete(staging.OutputPath);
                if (WorkspacePathSafety.PathEntryExistsConservatively(
                        staging.OutputPath))
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

    private bool TryDeleteOwnedEmptyDirectory(string directoryPath)
    {
        try
        {
            if (!WorkspacePathSafety.IsStrictDescendant(
                    _workspacePath,
                    directoryPath))
            {
                return false;
            }

            ValidateWorkspaceChain();
            if (!WorkspacePathSafety.TryGetAttributes(
                    directoryPath,
                    out FileAttributes attributes))
            {
                return true;
            }

            WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                directoryPath,
                targetIsDirectory: true);
            Directory.Delete(directoryPath, recursive: false);
            return !WorkspacePathSafety.PathEntryExistsConservatively(directoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FileStream OpenRegisteredOriginal(string path)
    {
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
            return new FileStream(path, options);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private static HeldFileSnapshot CaptureHeldSource(
        FileStream stream,
        long expectedLength)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead ||
                !stream.CanSeek ||
                stream.Length <= 0 ||
                stream.Length != expectedLength)
            {
                throw Failure(
                    ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            }

            return new HeldFileSnapshot(
                stream.Length,
                attributes,
                File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private static void ValidateHeldSource(
        FileStream stream,
        HeldFileSnapshot expected)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead ||
                !stream.CanSeek ||
                stream.Length != expected.Length ||
                attributes != expected.Attributes ||
                File.GetLastWriteTimeUtc(stream.SafeFileHandle) != expected.LastWriteTimeUtc)
            {
                throw Failure(
                    ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            }
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(
                ProxyGenerationFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private FileStream OpenGeneratedOutput(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        FileStream? stream = null;
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            stream = new FileStream(path, options);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            return stream;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            stream?.Dispose();
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
    }

    private static HeldFileSnapshot CaptureHeldOutput(FileStream stream)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead || !stream.CanSeek || stream.Length <= 0)
            {
                throw Failure(ProxyGenerationFailureReason.OutputInvalid);
            }

            return new HeldFileSnapshot(
                stream.Length,
                attributes,
                File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
    }

    private void ValidateHeldOutput(
        FileStream stream,
        HeldFileSnapshot expected,
        string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead ||
                !stream.CanSeek ||
                stream.Length != expected.Length ||
                attributes != expected.Attributes ||
                File.GetLastWriteTimeUtc(stream.SafeFileHandle) != expected.LastWriteTimeUtc)
            {
                throw Failure(ProxyGenerationFailureReason.OutputInvalid);
            }

            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
    }

    private static async Task<ContentDigest> ComputeDigestAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] digestBytes = new byte[Sha256ByteLength];
        try
        {
            stream.Position = 0;
            int bytesWritten = await SHA256.HashDataAsync(
                stream,
                digestBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytesWritten != Sha256ByteLength)
            {
                throw Failure(ProxyGenerationFailureReason.OutputInvalid);
            }

            return ContentDigest.FromBytes(digestBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProxyGenerationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or NotSupportedException)
        {
            throw Failure(ProxyGenerationFailureReason.OutputInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digestBytes);
        }
    }

    private static bool HasSameSourceIdentity(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult) =>
        stagedMedia.CandidateId == probeResult.CandidateId &&
        stagedMedia.ProjectId == probeResult.ProjectId &&
        stagedMedia.JobId == probeResult.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            probeResult.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == probeResult.ContentDigest &&
        stagedMedia.ByteSize == probeResult.ByteSize;

    private static bool HasSameSourceIdentity(
        StagedMediaImport stagedMedia,
        PtsIndex ptsIndex) =>
        stagedMedia.CandidateId == ptsIndex.CandidateId &&
        stagedMedia.ProjectId == ptsIndex.ProjectId &&
        stagedMedia.JobId == ptsIndex.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            ptsIndex.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == ptsIndex.ContentDigest &&
        stagedMedia.ByteSize == ptsIndex.ByteSize;

    private static bool HasSameMetadata(
        StoredArtifact expected,
        StoredArtifact actual) =>
        expected.Id == actual.Id &&
        expected.ProjectId == actual.ProjectId &&
        expected.Kind == actual.Kind &&
        string.Equals(
            expected.StorageReference,
            actual.StorageReference,
            StringComparison.Ordinal) &&
        expected.ContentDigest == actual.ContentDigest &&
        expected.ByteSize == actual.ByteSize &&
        expected.CreatedAt == actual.CreatedAt;

    private static string BuildProxyRelativePath(
        EntityId<AssetIdTag> sourceAssetId,
        EntityId<ArtifactIdTag> proxyArtifactId) =>
        string.Concat(
            sourceAssetId.ToString(),
            "/",
            proxyArtifactId.ToString(),
            ".mp4");

    private static string BuildOriginalStorageReference(
        Asset sourceAsset,
        EntityId<ProjectIdTag> projectId) =>
        string.Concat(
            "projects/",
            projectId.ToString(),
            "/original/",
            sourceAsset.Id.ToString(),
            "/",
            sourceAsset.ContentDigest.Hex,
            ".original");

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static ProxyGenerationException Failure(
        ProxyGenerationFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record StagingPaths(
        string DirectoryPath,
        string OutputPath);

    private readonly record struct HeldFileSnapshot(
        long Length,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);
}
