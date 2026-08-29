using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Media;

public enum OriginalIntegrityStatus
{
    Verified,
}

public enum IngestServiceFailureReason
{
    InputBindingMismatch,
    PromotionFailed,
    RegisteredArtifactMissing,
    RegisteredArtifactNotOriginal,
    RegisteredMetadataMismatch,
    RegisteredMetadataInvalid,
    RegisteredStorageUnsafe,
    RegisteredContentMissing,
    RegisteredContentNotRegularFile,
    RegisteredContentReparsePoint,
    RegisteredContentSizeMismatch,
    RegisteredContentDigestMismatch,
    RegisteredContentVerificationFailed,
}

public sealed class IngestServiceException : Exception
{
    internal IngestServiceException(
        IngestServiceFailureReason reason,
        bool reconciliationRequired)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public IngestServiceFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(IngestServiceFailureReason reason) =>
        reason switch
        {
            IngestServiceFailureReason.InputBindingMismatch =>
                "The ingest inputs do not identify the same verified media.",
            IngestServiceFailureReason.PromotionFailed =>
                "The verified original could not be promoted.",
            IngestServiceFailureReason.RegisteredArtifactMissing =>
                "The registered original artifact is missing from the store.",
            IngestServiceFailureReason.RegisteredArtifactNotOriginal =>
                "The registered artifact is not an original.",
            IngestServiceFailureReason.RegisteredMetadataMismatch =>
                "The registered original metadata does not match the expected snapshot.",
            IngestServiceFailureReason.RegisteredMetadataInvalid =>
                "The registered original metadata is invalid.",
            IngestServiceFailureReason.RegisteredStorageUnsafe =>
                "The registered original storage reference is unsafe.",
            IngestServiceFailureReason.RegisteredContentMissing =>
                "The registered original content is missing.",
            IngestServiceFailureReason.RegisteredContentNotRegularFile =>
                "The registered original content is not a regular file.",
            IngestServiceFailureReason.RegisteredContentReparsePoint =>
                "The registered original content path contains a reparse point.",
            IngestServiceFailureReason.RegisteredContentSizeMismatch =>
                "The registered original content size has changed.",
            IngestServiceFailureReason.RegisteredContentDigestMismatch =>
                "The registered original content digest has changed.",
            IngestServiceFailureReason.RegisteredContentVerificationFailed =>
                "The registered original content could not be verified.",
            _ => "The verified original ingest operation failed.",
        };
}

public sealed class ImportedOriginal
{
    internal ImportedOriginal(
        Asset asset,
        StoredArtifact artifact,
        MediaInfo mediaInfo,
        DecodeValidationResult decodeValidation,
        OriginalIntegrityStatus integrityStatus)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(mediaInfo);
        ArgumentNullException.ThrowIfNull(decodeValidation);

        if (!Enum.IsDefined(integrityStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(integrityStatus),
                integrityStatus,
                "The original integrity status must be defined.");
        }

        Asset = asset;
        Artifact = artifact;
        MediaInfo = mediaInfo;
        DecodeValidation = decodeValidation;
        IntegrityStatus = integrityStatus;
    }

    public Asset Asset { get; }

    public StoredArtifact Artifact { get; }

    public MediaInfo MediaInfo { get; }

    public DecodeValidationResult DecodeValidation { get; }

    public OriginalIntegrityStatus IntegrityStatus { get; }
}

public sealed class IngestService
{
    private readonly ArtifactStore _artifactStore;
    private readonly Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyRegisteredAsync;

    public IngestService(
        ArtifactStore artifactStore,
        BinaryVerifier binaryVerifier)
        : this(
            artifactStore,
            (artifactId, cancellationToken) =>
                artifactStore.VerifyRegisteredAsync(
                    artifactId,
                    binaryVerifier,
                    cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
    }

    internal IngestService(
        ArtifactStore artifactStore,
        Func<
            EntityId<ArtifactIdTag>,
            CancellationToken,
            Task<BinaryVerificationResult>> verifyRegisteredAsync)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);

        _artifactStore = artifactStore;
        _verifyRegisteredAsync = verifyRegisteredAsync;
    }

    public async Task<ImportedOriginal> PromoteVerifiedOriginalAsync(
        EntityId<ArtifactIdTag> artifactId,
        Asset asset,
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        MediaInfo mediaInfo,
        DecodeValidationResult decodeValidation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(mediaInfo);
        ArgumentNullException.ThrowIfNull(decodeValidation);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateInputBinding(
            asset,
            stagedMedia,
            probeResult,
            mediaInfo,
            decodeValidation);

        string relativePath = BuildOriginalRelativePath(asset);
        var request = new ArtifactPromotionRequest(
            artifactId,
            stagedMedia.ProjectId,
            ArtifactKind.Original,
            stagedMedia.StagingPath,
            relativePath,
            stagedMedia.ContentDigest,
            stagedMedia.ByteSize,
            stagedMedia.CreatedAt);

        StoredArtifact storedArtifact;
        try
        {
            storedArtifact = await _artifactStore.PromoteAsync(
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
                IngestServiceFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                IngestServiceFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }

        string expectedStorageReference = BuildOriginalStorageReference(
            stagedMedia.ProjectId,
            relativePath);
        if (storedArtifact.Id != artifactId ||
            storedArtifact.ProjectId != stagedMedia.ProjectId ||
            storedArtifact.Kind != ArtifactKind.Original ||
            !string.Equals(
                storedArtifact.StorageReference,
                expectedStorageReference,
                StringComparison.Ordinal) ||
            storedArtifact.ContentDigest != stagedMedia.ContentDigest ||
            storedArtifact.ByteSize != stagedMedia.ByteSize ||
            storedArtifact.CreatedAt != stagedMedia.CreatedAt)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredMetadataMismatch,
                reconciliationRequired: true);
        }

        OriginalIntegrityStatus integrityStatus;
        try
        {
            integrityStatus = await VerifyRegisteredOriginalAsync(
                storedArtifact,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredContentVerificationFailed,
                reconciliationRequired: true);
        }

        return new ImportedOriginal(
            asset,
            storedArtifact,
            mediaInfo,
            decodeValidation,
            integrityStatus);
    }

    public async Task<OriginalIntegrityStatus> VerifyRegisteredOriginalAsync(
        StoredArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        if (artifact.Kind != ArtifactKind.Original)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredArtifactNotOriginal,
                reconciliationRequired: true);
        }

        StoredArtifact registeredArtifact = GetMatchingRegisteredOriginal(artifact);

        try
        {
            BinaryVerificationResult verification =
                await _verifyRegisteredAsync(
                    registeredArtifact.Id,
                    cancellationToken).ConfigureAwait(false);
            if (!verification.IsVerified ||
                verification.ExpectedDigest != registeredArtifact.ContentDigest ||
                verification.ExpectedByteSize != registeredArtifact.ByteSize ||
                verification.ActualDigest != registeredArtifact.ContentDigest ||
                verification.ActualByteSize != registeredArtifact.ByteSize)
            {
                throw Failure(
                    IngestServiceFailureReason.RegisteredContentVerificationFailed,
                    reconciliationRequired: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IngestServiceException)
        {
            throw;
        }
        catch (BinaryVerificationException exception)
        {
            throw Failure(
                MapBinaryFailure(exception.Reason),
                reconciliationRequired: true);
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                MapArtifactStoreVerificationFailure(exception.Reason),
                reconciliationRequired: true);
        }
        catch (Exception)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredContentVerificationFailed,
                reconciliationRequired: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = GetMatchingRegisteredOriginal(registeredArtifact);
        return OriginalIntegrityStatus.Verified;
    }

    private static void ValidateInputBinding(
        Asset asset,
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        MediaInfo mediaInfo,
        DecodeValidationResult decodeValidation)
    {
        if (asset.Id != stagedMedia.CandidateId ||
            !string.Equals(
                asset.FileName,
                stagedMedia.SourceFileName,
                StringComparison.Ordinal) ||
            asset.ContentDigest != stagedMedia.ContentDigest ||
            asset.ByteSize != stagedMedia.ByteSize ||
            !HasSameSourceIdentity(stagedMedia, probeResult) ||
            !HasSameSourceIdentity(stagedMedia, mediaInfo) ||
            !HasSameSourceIdentity(stagedMedia, decodeValidation) ||
            !HasSameNormalizedMediaInfo(probeResult, mediaInfo) ||
            !decodeValidation.DecodedSuccessfully)
        {
            throw Failure(IngestServiceFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream? probedVideo = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .MinBy(static stream => stream.Index);
        MediaStreamInfo? normalizedVideo = mediaInfo.Streams
            .Where(static stream => stream.Kind == MediaStreamType.Video)
            .MinBy(static stream => stream.Index);
        if (probedVideo is null ||
            normalizedVideo is null ||
            mediaInfo.PrimaryVideoStream.Index != probedVideo.Index ||
            normalizedVideo.Index != probedVideo.Index ||
            decodeValidation.VideoStreamIndex != probedVideo.Index)
        {
            throw Failure(IngestServiceFailureReason.InputBindingMismatch);
        }

        int? probedAudioIndex = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .MinBy(static stream => stream.Index)
            ?.Index;
        int? normalizedAudioIndex = mediaInfo.Streams
            .Where(static stream => stream.Kind == MediaStreamType.Audio)
            .MinBy(static stream => stream.Index)
            ?.Index;
        if (normalizedAudioIndex != probedAudioIndex ||
            decodeValidation.AudioStreamIndex != probedAudioIndex)
        {
            throw Failure(IngestServiceFailureReason.InputBindingMismatch);
        }
    }

    private StoredArtifact GetMatchingRegisteredOriginal(StoredArtifact expected)
    {
        StoredArtifact? registeredArtifact;
        try
        {
            registeredArtifact = _artifactStore.GetArtifact(expected.Id);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredMetadataInvalid,
                reconciliationRequired: true);
        }

        if (registeredArtifact is null)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredArtifactMissing,
                reconciliationRequired: true);
        }

        if (registeredArtifact.Kind != ArtifactKind.Original)
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredArtifactNotOriginal,
                reconciliationRequired: true);
        }

        if (!HasSameMetadata(expected, registeredArtifact))
        {
            throw Failure(
                IngestServiceFailureReason.RegisteredMetadataMismatch,
                reconciliationRequired: true);
        }

        return registeredArtifact;
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
        MediaInfo mediaInfo) =>
        stagedMedia.CandidateId == mediaInfo.CandidateId &&
        stagedMedia.ProjectId == mediaInfo.ProjectId &&
        stagedMedia.JobId == mediaInfo.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            mediaInfo.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == mediaInfo.ContentDigest &&
        stagedMedia.ByteSize == mediaInfo.ByteSize;

    private static bool HasSameSourceIdentity(
        StagedMediaImport stagedMedia,
        DecodeValidationResult decodeValidation) =>
        stagedMedia.CandidateId == decodeValidation.CandidateId &&
        stagedMedia.ProjectId == decodeValidation.ProjectId &&
        stagedMedia.JobId == decodeValidation.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            decodeValidation.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == decodeValidation.ContentDigest &&
        stagedMedia.ByteSize == decodeValidation.ByteSize;

    private static bool HasSameNormalizedMediaInfo(
        MediaProbeResult probeResult,
        MediaInfo mediaInfo)
    {
        MediaInfo expected;
        try
        {
            expected = IngestValidator.Validate(
                probeResult,
                new IngestValidationRequirements(
                    mediaInfo.RequiredMinimumVideoWidth,
                    mediaInfo.RequiredMinimumVideoHeight));
        }
        catch (Exception exception)
            when (exception is ArgumentException or OverflowException)
        {
            return false;
        }

        return HasSameMediaInfo(expected, mediaInfo);
    }

    private static bool HasSameMediaInfo(MediaInfo expected, MediaInfo actual)
    {
        if (expected.CandidateId != actual.CandidateId ||
            expected.ProjectId != actual.ProjectId ||
            expected.JobId != actual.JobId ||
            !string.Equals(
                expected.SourceFileName,
                actual.SourceFileName,
                StringComparison.Ordinal) ||
            expected.ContentDigest != actual.ContentDigest ||
            expected.ByteSize != actual.ByteSize ||
            expected.ContainerClassification != actual.ContainerClassification ||
            !expected.FormatNames.SequenceEqual(
                actual.FormatNames,
                StringComparer.Ordinal) ||
            !string.Equals(
                expected.FormatLongName,
                actual.FormatLongName,
                StringComparison.Ordinal) ||
            expected.FormatDuration != actual.FormatDuration ||
            expected.RequiredMinimumVideoWidth != actual.RequiredMinimumVideoWidth ||
            expected.RequiredMinimumVideoHeight != actual.RequiredMinimumVideoHeight ||
            expected.PrimaryVideoStream.Index != actual.PrimaryVideoStream.Index ||
            expected.Streams.Count != actual.Streams.Count ||
            expected.Warnings.Count != actual.Warnings.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Streams.Count; index++)
        {
            if (!HasSameStream(expected.Streams[index], actual.Streams[index]))
            {
                return false;
            }
        }

        for (int index = 0; index < expected.Warnings.Count; index++)
        {
            if (!HasSameWarning(expected.Warnings[index], actual.Warnings[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameStream(
        MediaStreamInfo expected,
        MediaStreamInfo actual) =>
        expected.Index == actual.Index &&
        expected.Kind == actual.Kind &&
        string.Equals(expected.CodecName, actual.CodecName, StringComparison.Ordinal) &&
        string.Equals(expected.Profile, actual.Profile, StringComparison.Ordinal) &&
        expected.Width == actual.Width &&
        expected.Height == actual.Height &&
        string.Equals(expected.PixelFormat, actual.PixelFormat, StringComparison.Ordinal) &&
        expected.NominalFrameRate == actual.NominalFrameRate &&
        expected.AverageFrameRate == actual.AverageFrameRate &&
        expected.TimeBase == actual.TimeBase &&
        expected.Start == actual.Start &&
        expected.Duration == actual.Duration &&
        expected.SampleRate == actual.SampleRate &&
        expected.Channels == actual.Channels &&
        string.Equals(
            expected.ChannelLayout,
            actual.ChannelLayout,
            StringComparison.Ordinal);

    private static bool HasSameWarning(
        MediaInfoWarning expected,
        MediaInfoWarning actual)
    {
        if (expected.Code != actual.Code)
        {
            return false;
        }

        return (expected.Evidence, actual.Evidence) switch
        {
            (null, null) => true,
            (MediaStreamCountWarningEvidence left,
                MediaStreamCountWarningEvidence right) =>
                left.ObservedCount == right.ObservedCount,
            (LowResolutionWarningEvidence left,
                LowResolutionWarningEvidence right) =>
                left.ObservedWidth == right.ObservedWidth &&
                left.ObservedHeight == right.ObservedHeight &&
                left.RequiredMinimumWidth == right.RequiredMinimumWidth &&
                left.RequiredMinimumHeight == right.RequiredMinimumHeight,
            (VariableRateFieldsDifferWarningEvidence left,
                VariableRateFieldsDifferWarningEvidence right) =>
                left.ProbeReportedFieldsDiffer == right.ProbeReportedFieldsDiffer &&
                HasSameFrameRateDifferences(left.Differences, right.Differences),
            (DurationFieldsDifferWarningEvidence left,
                DurationFieldsDifferWarningEvidence right) =>
                HasSameDurationFields(left.Fields, right.Fields),
            _ => false,
        };
    }

    private static bool HasSameFrameRateDifferences(
        IReadOnlyList<MediaFrameRateDifference> expected,
        IReadOnlyList<MediaFrameRateDifference> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].StreamIndex != actual[index].StreamIndex ||
                expected[index].NominalFrameRate != actual[index].NominalFrameRate ||
                expected[index].AverageFrameRate != actual[index].AverageFrameRate)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameDurationFields(
        IReadOnlyList<MediaDurationFieldEvidence> expected,
        IReadOnlyList<MediaDurationFieldEvidence> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index].Kind != actual[index].Kind ||
                expected[index].StreamIndex != actual[index].StreamIndex ||
                expected[index].Value != actual[index].Value)
            {
                return false;
            }
        }

        return true;
    }

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

    private static string BuildOriginalRelativePath(Asset asset) =>
        string.Concat(
            asset.Id.ToString(),
            "/",
            asset.ContentDigest.Hex,
            ".original");

    private static string BuildOriginalStorageReference(
        EntityId<ProjectIdTag> projectId,
        string relativePath) =>
        string.Concat(
            "projects/",
            projectId.ToString(),
            "/original/",
            relativePath);

    private static IngestServiceFailureReason MapBinaryFailure(
        BinaryVerificationFailureReason reason) =>
        reason switch
        {
            BinaryVerificationFailureReason.FileMissing =>
                IngestServiceFailureReason.RegisteredContentMissing,
            BinaryVerificationFailureReason.NotRegularFile =>
                IngestServiceFailureReason.RegisteredContentNotRegularFile,
            BinaryVerificationFailureReason.ReparsePoint =>
                IngestServiceFailureReason.RegisteredContentReparsePoint,
            BinaryVerificationFailureReason.SizeMismatch =>
                IngestServiceFailureReason.RegisteredContentSizeMismatch,
            BinaryVerificationFailureReason.DigestMismatch =>
                IngestServiceFailureReason.RegisteredContentDigestMismatch,
            _ => IngestServiceFailureReason.RegisteredContentVerificationFailed,
        };

    private static IngestServiceFailureReason MapArtifactStoreVerificationFailure(
        ArtifactStoreFailureReason reason) =>
        reason switch
        {
            ArtifactStoreFailureReason.RegisteredArtifactMissing =>
                IngestServiceFailureReason.RegisteredArtifactMissing,
            ArtifactStoreFailureReason.StagingMissing =>
                IngestServiceFailureReason.RegisteredContentMissing,
            ArtifactStoreFailureReason.UnsafePath =>
                IngestServiceFailureReason.RegisteredStorageUnsafe,
            ArtifactStoreFailureReason.NotRegularFile =>
                IngestServiceFailureReason.RegisteredContentNotRegularFile,
            ArtifactStoreFailureReason.ReparsePoint =>
                IngestServiceFailureReason.RegisteredContentReparsePoint,
            ArtifactStoreFailureReason.SizeMismatch =>
                IngestServiceFailureReason.RegisteredContentSizeMismatch,
            ArtifactStoreFailureReason.DigestMismatch =>
                IngestServiceFailureReason.RegisteredContentDigestMismatch,
            _ => IngestServiceFailureReason.RegisteredContentVerificationFailed,
        };

    private static IngestServiceException Failure(
        IngestServiceFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);
}