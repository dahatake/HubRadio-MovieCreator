using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Media;

internal sealed class IngestWorkflowCheckpointCodec
{
    internal const string SchemaVersion = "media-ingest-checkpoint-v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new CanonicalInt16Converter());
        options.Converters.Add(new CanonicalInt32Converter());
        options.Converters.Add(new CanonicalInt64Converter());
        return options;
    }

    private readonly string _workspacePath;
    private readonly ArtifactStore _artifactStore;

    internal IngestWorkflowCheckpointCodec(
        string workspacePath,
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(artifactStore);
        _workspacePath = workspacePath;
        _artifactStore = artifactStore;
    }

    internal static IngestWorkflowRequest DeserializeRequest(
        string canonicalJson)
    {
        RequestDto dto = DeserializeCanonical<RequestDto>(canonicalJson);
        if (!string.Equals(
                dto.Version,
                IngestWorkflowRequest.CurrentVersion,
                StringComparison.Ordinal) ||
            dto.StageCount != IngestWorkflowRequest.StageCount)
        {
            throw InvalidCheckpoint();
        }

        try
        {
            var request = new IngestWorkflowRequest(
                ParseId<JobIdTag>(dto.JobId),
                ParseId<ProjectIdTag>(dto.ProjectId),
                ParseId<AssetIdTag>(dto.AssetCandidateId),
                ParseId<ArtifactIdTag>(dto.OriginalArtifactId),
                ParseId<ArtifactIdTag>(dto.ProxyArtifactId),
                ParseId<ArtifactIdTag>(dto.WaveformIndexArtifactId),
                ParseId<ArtifactIdTag>(dto.SpriteImageArtifactId),
                ParseId<ArtifactIdTag>(dto.SpriteIndexArtifactId),
                dto.MediaType,
                RestoreRights(dto.Rights),
                new IngestValidationRequirements(
                    dto.MinimumVideoWidth,
                    dto.MinimumVideoHeight),
                TimeSpan.FromTicks(dto.OperationTimeoutTicks));
            if (!string.Equals(
                    request.CanonicalJson,
                    canonicalJson,
                    StringComparison.Ordinal))
            {
                throw InvalidCheckpoint();
            }

            return request;
        }
        catch (Exception exception)
            when (IsDomainValidationFailure(exception))
        {
            throw InvalidCheckpoint();
        }
    }

    internal static IReadOnlyList<MediaInfoWarningCode> ReadWarningCodes(
        string canonicalCheckpointJson,
        EntityId<JobIdTag> expectedJobId,
        EntityId<ProjectIdTag> expectedProjectId,
        ContentDigest expectedRequestDigest,
        int expectedCheckpointStage)
    {
        ArgumentNullException.ThrowIfNull(expectedJobId);
        ArgumentNullException.ThrowIfNull(expectedProjectId);
        ArgumentNullException.ThrowIfNull(expectedRequestDigest);
        if (expectedCheckpointStage < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedCheckpointStage),
                expectedCheckpointStage,
                "A checkpoint stage must not be negative.");
        }

        try
        {
            CheckpointDto dto = DeserializeCanonical<CheckpointDto>(
                canonicalCheckpointJson);
            ValidateShape(dto);
            if (dto.Stage != expectedCheckpointStage ||
                !string.Equals(
                    dto.JobId,
                    expectedJobId.ToString(),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    dto.RequestDigest,
                    expectedRequestDigest.Hex,
                    StringComparison.Ordinal))
            {
                throw InvalidCheckpoint();
            }

            // Warning reads must not depend on staging or artifact files. Compare
            // the entire encoded structure even before warnings exist: numeric
            // converters can round a tiny JSON fraction to an integer.
            if (!string.Equals(
                    SerializeCanonical(dto),
                    canonicalCheckpointJson,
                    StringComparison.Ordinal))
            {
                throw InvalidCheckpoint();
            }

            if (dto.MediaInfo is null)
            {
                return Array.Empty<MediaInfoWarningCode>();
            }

            MediaInfo mediaInfo = RestoreMediaInfo(dto.MediaInfo);
            if (mediaInfo.JobId != expectedJobId ||
                mediaInfo.ProjectId != expectedProjectId)
            {
                throw InvalidCheckpoint();
            }

            return Array.AsReadOnly(
                mediaInfo.Warnings
                    .Select(static warning => warning.Code)
                    .ToArray());
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsDomainValidationFailure(exception))
        {
            throw InvalidCheckpoint();
        }
    }

    internal string Serialize(WorkflowResumeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var dto = new CheckpointDto
        {
            SchemaVersion = SchemaVersion,
            Stage = (int)state.Stage,
            JobId = state.JobId.ToString(),
            RequestDigest = state.RequestDigest.Hex,
            StagedMedia = state.StagedMedia is null
                ? null
                : CreateStagedDto(state.StagedMedia),
            Asset = state.Asset is null ? null : CreateAssetDto(state.Asset),
            ProbeResult = state.ProbeResult is null
                ? null
                : CreateProbeDto(state.ProbeResult),
            SourcePtsIndex = state.SourcePtsIndex is null
                ? null
                : CreatePtsDto(state.SourcePtsIndex),
            MediaInfo = state.MediaInfo is null
                ? null
                : CreateMediaInfoDto(state.MediaInfo),
            DecodeValidation = state.DecodeValidation is null
                ? null
                : CreateDecodeDto(state.DecodeValidation),
            OriginalArtifact = state.Original is null
                ? null
                : CreateArtifactDto(state.Original.Artifact),
            OriginalIntegrityStatus = state.Original?.IntegrityStatus.ToString(),
            Proxy = state.Proxy is null ? null : CreateProxyDto(state.Proxy),
            FrameMapDigest = state.FrameMap is null
                ? null
                : DigestCanonicalJson(state.FrameMap.CanonicalJson).Hex,
            PreviewAssets = state.PreviewAssets is null
                ? null
                : CreatePreviewDto(state.PreviewAssets),
        };
        ValidateShape(dto);
        return SerializeCanonical(dto);
    }

    internal void ValidateStagingCheckpointBoundary(
        IngestWorkflowRequest request,
        int checkpointStage)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (checkpointStage is < 0 or >
            (int)IngestWorkflowStage.PreviewAssetsCommitted)
        {
            throw InvalidCheckpoint();
        }

        bool readyExpected = checkpointStage >=
                (int)IngestWorkflowStage.CopiedToStaging &&
            checkpointStage < (int)IngestWorkflowStage.OriginalCommitted;
        try
        {
            MediaCheckpointFactory.ValidateStagingCheckpointBoundary(
                _workspacePath,
                request.AssetCandidateId,
                request.ProjectId,
                request.JobId,
                readyExpected);
        }
        catch (MediaCheckpointValidationException)
        {
            throw InvalidCheckpoint();
        }
    }

    internal async Task<WorkflowResumeState> DeserializeAsync(
        IngestWorkflowRequest request,
        StoredIngestWorkflow stored,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stored);
        cancellationToken.ThrowIfCancellationRequested();
        if (stored.JobId != request.JobId ||
            stored.ProjectId != request.ProjectId ||
            stored.RequestDigest != request.RequestDigest ||
            !string.Equals(
                stored.RequestSchemaVersion,
                IngestWorkflowRequest.CurrentVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.RequestPayloadJson,
                request.CanonicalJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.CheckpointSchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        CheckpointDto dto = DeserializeCanonical<CheckpointDto>(
            stored.CheckpointPayloadJson);
        ValidateShape(dto);
        if (dto.Stage != stored.CheckpointStage ||
            !string.Equals(dto.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(dto.JobId, request.JobId.ToString(), StringComparison.Ordinal) ||
            !string.Equals(
                dto.RequestDigest,
                request.RequestDigest.Hex,
                StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        try
        {
            WorkflowResumeState state = WorkflowResumeState.Create(request);
            if (dto.Stage >= (int)IngestWorkflowStage.CopiedToStaging)
            {
                StagedDto stagedDto = dto.StagedMedia!;
                StagedMediaImport staged = dto.Stage <
                        (int)IngestWorkflowStage.OriginalCommitted
                    ? await RestoreStagedAsync(stagedDto, cancellationToken)
                        .ConfigureAwait(false)
                    : RestorePromotedStaged(stagedDto);
                state.SetStaged(staged, RestoreAsset(dto.Asset!));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.Probed)
            {
                state.SetProbe(RestoreProbe(dto.ProbeResult!));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.SourceIndexed)
            {
                state.SetSourceIndex(RestorePts(dto.SourcePtsIndex!));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.WarningsNormalized)
            {
                MediaInfo restoredMediaInfo = RestoreMediaInfo(dto.MediaInfo!);
                MediaInfo expectedMediaInfo = IngestValidator.Validate(
                    state.ProbeResult!,
                    request.ValidationRequirements);
                if (!string.Equals(
                        SerializeCanonical(CreateMediaInfoDto(restoredMediaInfo)),
                        SerializeCanonical(CreateMediaInfoDto(expectedMediaInfo)),
                        StringComparison.Ordinal))
                {
                    throw InvalidCheckpoint();
                }

                state.SetMediaInfo(restoredMediaInfo);
            }

            if (dto.Stage >= (int)IngestWorkflowStage.DecodeValidated)
            {
                state.SetDecodeValidation(
                    RestoreDecode(dto.DecodeValidation!));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.OriginalCommitted)
            {
                StoredArtifact originalArtifact =
                    await RestoreArtifactAsync(
                        dto.OriginalArtifact!,
                        request.OriginalArtifactId,
                        cancellationToken).ConfigureAwait(false);
                OriginalIntegrityStatus status = ParseEnumExact<OriginalIntegrityStatus>(
                    dto.OriginalIntegrityStatus!);
                state.SetOriginal(new ImportedOriginal(
                    state.Asset!,
                    originalArtifact,
                    state.MediaInfo!,
                    state.DecodeValidation!,
                    status));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.ProxyCommitted)
            {
                state.SetProxy(await RestoreProxyAsync(
                    dto.Proxy!,
                    request.ProxyArtifactId,
                    cancellationToken).ConfigureAwait(false));
            }

            if (dto.Stage >= (int)IngestWorkflowStage.FrameMapBuilt)
            {
                ProxyFrameMap frameMap = new ProxyFrameMapGenerator().Generate(
                    state.SourcePtsIndex!,
                    state.Proxy!);
                if (DigestCanonicalJson(frameMap.CanonicalJson) !=
                    ParseDigest(dto.FrameMapDigest!))
                {
                    throw InvalidCheckpoint();
                }

                state.SetFrameMap(frameMap);
            }

            if (dto.Stage >= (int)IngestWorkflowStage.PreviewAssetsCommitted)
            {
                state.SetPreviewAssets(await RestorePreviewAsync(
                    dto.PreviewAssets!,
                    request,
                    cancellationToken).ConfigureAwait(false));
            }

            state.ValidateFor(request);
            // Canonical JSON can still lose numeric precision during deserialization.
            // Require every reader to preserve the exact persisted payload.
            if (!string.Equals(
                    Serialize(state),
                    stored.CheckpointPayloadJson,
                    StringComparison.Ordinal))
            {
                throw InvalidCheckpoint();
            }

            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
            when (exception.Message == "The durable ingest checkpoint is invalid.")
        {
            throw;
        }
        catch (ArtifactStoreException exception)
        {
            throw InvalidCheckpoint(exception);
        }
        catch (Exception exception)
            when (IsDomainValidationFailure(exception) ||
                exception is MediaCheckpointValidationException or
                    ProxyFrameMapException)
        {
            throw InvalidCheckpoint();
        }
    }

    private StagedDto CreateStagedDto(StagedMediaImport staged) =>
        new()
        {
            CandidateId = staged.CandidateId.ToString(),
            ProjectId = staged.ProjectId.ToString(),
            JobId = staged.JobId.ToString(),
            SourceFileName = staged.SourceFileName,
            StagingReference = MediaCheckpointFactory.GetStagingReference(
                _workspacePath,
                staged),
            ContentDigest = staged.ContentDigest.Hex,
            ByteSize = staged.ByteSize,
            CreatedAt = FormatTimestamp(staged.CreatedAt),
        };

    private Task<StagedMediaImport> RestoreStagedAsync(
        StagedDto dto,
        CancellationToken cancellationToken) =>
        MediaCheckpointFactory.RestoreStagedMediaAsync(
            _workspacePath,
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            dto.StagingReference,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            ParseTimestamp(dto.CreatedAt),
            cancellationToken);

    private StagedMediaImport RestorePromotedStaged(StagedDto dto) =>
        MediaCheckpointFactory.RestorePromotedStagedMedia(
            _workspacePath,
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            dto.StagingReference,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            ParseTimestamp(dto.CreatedAt));

    private async Task<StoredArtifact> RestoreArtifactAsync(
        ArtifactDto dto,
        EntityId<ArtifactIdTag> expectedId,
        CancellationToken cancellationToken)
    {
        StoredArtifact artifact = await _artifactStore.ValidateRegisteredAsync(
            expectedId,
            cancellationToken).ConfigureAwait(false);
        if (artifact.Id != ParseId<ArtifactIdTag>(dto.Id) ||
            artifact.ProjectId != ParseId<ProjectIdTag>(dto.ProjectId) ||
            artifact.Kind != ParseEnumExact<ArtifactKind>(dto.Kind) ||
            !string.Equals(
                artifact.StorageReference,
                dto.StorageReference,
                StringComparison.Ordinal) ||
            artifact.ContentDigest != ParseDigest(dto.ContentDigest) ||
            artifact.ByteSize != dto.ByteSize ||
            artifact.CreatedAt != ParseTimestamp(dto.CreatedAt))
        {
            throw InvalidCheckpoint();
        }

        return artifact;
    }

    private async Task<GeneratedProxy> RestoreProxyAsync(
        ProxyDto dto,
        EntityId<ArtifactIdTag> expectedProxyId,
        CancellationToken cancellationToken)
    {
        StoredArtifact artifact = await RestoreArtifactAsync(
            dto.ProxyArtifact,
            expectedProxyId,
            cancellationToken).ConfigureAwait(false);
        return MediaCheckpointFactory.RestoreGeneratedProxy(
            ParseId<AssetIdTag>(dto.SourceAssetId),
            ParseDigest(dto.SourceContentDigest),
            ParseId<ArtifactIdTag>(dto.SourceOriginalArtifactId),
            artifact,
            RestoreProbe(dto.ProxyProbeResult),
            RestorePts(dto.ProxyPtsIndex),
            MediaCheckpointFactory.RestoreProxyProfile(dto.ProfileKind),
            TimeSpan.FromTicks(dto.GenerationElapsedTicks));
    }

    private async Task<GeneratedPreviewAssets> RestorePreviewAsync(
        PreviewDto dto,
        IngestWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        StoredArtifact waveformArtifact = await RestoreArtifactAsync(
            dto.WaveformIndexArtifact,
            request.WaveformIndexArtifactId,
            cancellationToken).ConfigureAwait(false);
        StoredArtifact spriteImageArtifact = await RestoreArtifactAsync(
            dto.SpriteImageArtifact,
            request.SpriteImageArtifactId,
            cancellationToken).ConfigureAwait(false);
        StoredArtifact spriteIndexArtifact = await RestoreArtifactAsync(
            dto.SpriteIndexArtifact,
            request.SpriteIndexArtifactId,
            cancellationToken).ConfigureAwait(false);
        PreviewWaveformIndex waveform = RestoreWaveformIndex(dto.WaveformIndex);
        ThumbnailSpriteIndex sprite = RestoreSpriteIndex(dto.SpriteIndex);
        PreviewAssetGenerationProfile profile =
            MediaCheckpointFactory.RestorePreviewProfile(dto.ProfileVersion);
        return MediaCheckpointFactory.RestoreGeneratedPreviewAssets(
            waveformArtifact,
            spriteImageArtifact,
            spriteIndexArtifact,
            waveform,
            sprite,
            profile);
    }

    private static AssetDto CreateAssetDto(Asset asset) =>
        new()
        {
            Id = asset.Id.ToString(),
            FileName = asset.FileName,
            MediaType = asset.MediaType,
            ContentDigest = asset.ContentDigest.Hex,
            ByteSize = asset.ByteSize,
            Rights = CreateRightsDto(asset.Rights),
        };

    private static Asset RestoreAsset(AssetDto dto) =>
        new(
            ParseId<AssetIdTag>(dto.Id),
            dto.FileName,
            dto.MediaType,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            RestoreRights(dto.Rights));

    private static RightsDto CreateRightsDto(RightsRecord rights) =>
        new()
        {
            Status = rights.Status.ToString(),
            EvidenceReference = rights.EvidenceReference,
            AllowedUse = rights.AllowedUse,
            ExternalPublicationAllowed = rights.ExternalPublicationAllowed,
        };

    private static RightsRecord RestoreRights(RightsDto dto) =>
        new(
            ParseEnumExact<RightsStatus>(dto.Status),
            dto.EvidenceReference,
            dto.AllowedUse,
            dto.ExternalPublicationAllowed);

    private static ProbeDto CreateProbeDto(MediaProbeResult probe) =>
        new()
        {
            CandidateId = probe.CandidateId.ToString(),
            ProjectId = probe.ProjectId.ToString(),
            JobId = probe.JobId.ToString(),
            SourceFileName = probe.SourceFileName,
            ContentDigest = probe.ContentDigest.Hex,
            ByteSize = probe.ByteSize,
            ContainerSignature = probe.ContainerSignature.ToString(),
            FormatNames = [.. probe.FormatNames],
            FormatLongName = probe.FormatLongName,
            DurationSeconds = probe.DurationSeconds,
            DeclaredByteSize = probe.DeclaredByteSize,
            BitRate = probe.BitRate,
            ProbeScore = probe.ProbeScore,
            Streams = probe.Streams.Select(CreateProbedStreamDto).ToList(),
            Warnings = [.. probe.Warnings],
        };

    private static MediaProbeResult RestoreProbe(ProbeDto dto) =>
        new(
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            ParseEnumExact<ContainerSignature>(dto.ContainerSignature),
            dto.FormatNames,
            dto.FormatLongName,
            dto.DurationSeconds,
            dto.DeclaredByteSize,
            dto.BitRate,
            dto.ProbeScore,
            dto.Streams.Select(RestoreProbedStream).ToArray(),
            dto.Warnings);

    private static ProbedStreamDto CreateProbedStreamDto(ProbedMediaStream stream) =>
        new()
        {
            Index = stream.Index,
            Kind = stream.Kind.ToString(),
            CodecName = stream.CodecName,
            Profile = stream.Profile,
            Width = stream.Width,
            Height = stream.Height,
            PixelFormat = stream.PixelFormat,
            NominalFrameRate = ToDto(stream.NominalFrameRate),
            AverageFrameRate = ToDto(stream.AverageFrameRate),
            TimeBase = ToDto(stream.TimeBase),
            StartSeconds = stream.StartSeconds,
            DurationSeconds = stream.DurationSeconds,
            SampleRate = stream.SampleRate,
            Channels = stream.Channels,
            ChannelLayout = stream.ChannelLayout,
        };

    private static ProbedMediaStream RestoreProbedStream(ProbedStreamDto dto) =>
        new(
            dto.Index,
            ParseEnumExact<MediaStreamKind>(dto.Kind),
            dto.CodecName,
            dto.Profile,
            dto.Width,
            dto.Height,
            dto.PixelFormat,
            FromNullableNumber(dto.NominalFrameRate),
            FromNullableNumber(dto.AverageFrameRate),
            FromNumber(dto.TimeBase),
            dto.StartSeconds,
            dto.DurationSeconds,
            dto.SampleRate,
            dto.Channels,
            dto.ChannelLayout);

    private static PtsDto CreatePtsDto(PtsIndex index) =>
        new()
        {
            SchemaVersion = index.SchemaVersion,
            CandidateId = index.CandidateId.ToString(),
            ProjectId = index.ProjectId.ToString(),
            JobId = index.JobId.ToString(),
            SourceFileName = index.SourceFileName,
            ContentDigest = index.ContentDigest.Hex,
            ByteSize = index.ByteSize,
            StreamIndex = index.StreamIndex,
            TimeBase = ToDto(index.TimeBase),
            Entries = index.Entries.Select(static entry => new PtsEntryDto
            {
                Sequence = entry.Sequence,
                PresentationTime = ToDto(entry.PresentationTime),
                Duration = ToDto(entry.Duration),
                KeyFrame = entry.KeyFrame,
            }).ToList(),
        };

    private static PtsIndex RestorePts(PtsDto dto)
    {
        if (!string.Equals(
                dto.SchemaVersion,
                PtsIndex.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return new PtsIndex(
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            dto.StreamIndex,
            From(dto.TimeBase),
            dto.Entries.Select(static entry => new PtsIndexEntry(
                entry.Sequence,
                From(entry.PresentationTime),
                From(entry.Duration),
                entry.KeyFrame)).ToArray());
    }

    private static MediaInfoDto CreateMediaInfoDto(MediaInfo mediaInfo) =>
        new()
        {
            CandidateId = mediaInfo.CandidateId.ToString(),
            ProjectId = mediaInfo.ProjectId.ToString(),
            JobId = mediaInfo.JobId.ToString(),
            SourceFileName = mediaInfo.SourceFileName,
            ContentDigest = mediaInfo.ContentDigest.Hex,
            ByteSize = mediaInfo.ByteSize,
            ContainerClassification = mediaInfo.ContainerClassification.ToString(),
            FormatNames = [.. mediaInfo.FormatNames],
            FormatLongName = mediaInfo.FormatLongName,
            FormatDuration = ToDto(mediaInfo.FormatDuration),
            Streams = mediaInfo.Streams.Select(CreateMediaStreamDto).ToList(),
            RequiredMinimumVideoWidth = mediaInfo.RequiredMinimumVideoWidth,
            RequiredMinimumVideoHeight = mediaInfo.RequiredMinimumVideoHeight,
            Warnings = mediaInfo.Warnings.Select(CreateWarningDto).ToList(),
        };

    private static MediaInfo RestoreMediaInfo(MediaInfoDto dto) =>
        new(
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            ParseEnumExact<MediaContainerClassification>(
                dto.ContainerClassification),
            dto.FormatNames,
            dto.FormatLongName,
            FromNullable(dto.FormatDuration),
            dto.Streams.Select(RestoreMediaStream).ToArray(),
            dto.RequiredMinimumVideoWidth,
            dto.RequiredMinimumVideoHeight,
            dto.Warnings.Select(RestoreWarning).ToArray());

    private static MediaStreamDto CreateMediaStreamDto(MediaStreamInfo stream) =>
        new()
        {
            Index = stream.Index,
            Kind = stream.Kind.ToString(),
            CodecName = stream.CodecName,
            Profile = stream.Profile,
            Width = stream.Width,
            Height = stream.Height,
            PixelFormat = stream.PixelFormat,
            NominalFrameRate = ToDto(stream.NominalFrameRate),
            AverageFrameRate = ToDto(stream.AverageFrameRate),
            TimeBase = ToDto(stream.TimeBase),
            Start = ToDto(stream.Start),
            Duration = ToDto(stream.Duration),
            SampleRate = stream.SampleRate,
            Channels = stream.Channels,
            ChannelLayout = stream.ChannelLayout,
        };

    private static MediaStreamInfo RestoreMediaStream(MediaStreamDto dto) =>
        new(
            dto.Index,
            ParseEnumExact<MediaStreamType>(dto.Kind),
            dto.CodecName,
            dto.Profile,
            dto.Width,
            dto.Height,
            dto.PixelFormat,
            FromNullableNumber(dto.NominalFrameRate),
            FromNullableNumber(dto.AverageFrameRate),
            From(dto.TimeBase),
            FromNullable(dto.Start),
            FromNullable(dto.Duration),
            dto.SampleRate,
            dto.Channels,
            dto.ChannelLayout);

    private static WarningDto CreateWarningDto(MediaInfoWarning warning)
    {
        var dto = new WarningDto
        {
            Code = warning.Code.ToString(),
            StreamCount = null,
            LowResolution = null,
            VariableRate = null,
            DurationFields = null,
        };
        switch (warning.Evidence)
        {
            case null:
                break;
            case MediaStreamCountWarningEvidence count:
                dto.StreamCount = new StreamCountEvidenceDto
                {
                    ObservedCount = count.ObservedCount,
                };
                break;
            case LowResolutionWarningEvidence resolution:
                dto.LowResolution = new LowResolutionEvidenceDto
                {
                    ObservedWidth = resolution.ObservedWidth,
                    ObservedHeight = resolution.ObservedHeight,
                    RequiredMinimumWidth = resolution.RequiredMinimumWidth,
                    RequiredMinimumHeight = resolution.RequiredMinimumHeight,
                };
                break;
            case VariableRateFieldsDifferWarningEvidence variableRate:
                dto.VariableRate = new VariableRateEvidenceDto
                {
                    ProbeReportedFieldsDiffer =
                        variableRate.ProbeReportedFieldsDiffer,
                    Differences = variableRate.Differences.Select(
                        static difference => new FrameRateDifferenceDto
                        {
                            StreamIndex = difference.StreamIndex,
                            NominalFrameRate = ToDto(
                                difference.NominalFrameRate),
                            AverageFrameRate = ToDto(
                                difference.AverageFrameRate),
                        }).ToList(),
                };
                break;
            case DurationFieldsDifferWarningEvidence duration:
                dto.DurationFields = new DurationEvidenceDto
                {
                    Fields = duration.Fields.Select(static field =>
                        new DurationFieldDto
                        {
                            Kind = field.Kind.ToString(),
                            StreamIndex = field.StreamIndex,
                            Value = ToDto(field.Value),
                        }).ToList(),
                };
                break;
            default:
                throw InvalidCheckpoint();
        }

        return dto;
    }

    private static MediaInfoWarning RestoreWarning(WarningDto dto)
    {
        MediaInfoWarningCode code = ParseEnumExact<MediaInfoWarningCode>(dto.Code);
        int evidenceCount = CountNotNull(
            dto.StreamCount,
            dto.LowResolution,
            dto.VariableRate,
            dto.DurationFields);
        MediaInfoWarningEvidence? evidence = code switch
        {
            MediaInfoWarningCode.AudioTrackAbsent or
            MediaInfoWarningCode.SignatureFormatMismatch or
            MediaInfoWarningCode.UnrecognizedContainerSignature
                when evidenceCount == 0 => null,
            MediaInfoWarningCode.MultipleAudioStreams or
            MediaInfoWarningCode.MultipleVideoStreams
                when evidenceCount == 1 && dto.StreamCount is not null =>
                    new MediaStreamCountWarningEvidence(
                        dto.StreamCount.ObservedCount),
            MediaInfoWarningCode.LowResolution
                when evidenceCount == 1 && dto.LowResolution is not null =>
                    new LowResolutionWarningEvidence(
                        dto.LowResolution.ObservedWidth,
                        dto.LowResolution.ObservedHeight,
                        dto.LowResolution.RequiredMinimumWidth,
                        dto.LowResolution.RequiredMinimumHeight),
            MediaInfoWarningCode.VariableRateFieldsDiffer
                when evidenceCount == 1 && dto.VariableRate is not null =>
                    new VariableRateFieldsDifferWarningEvidence(
                        dto.VariableRate.ProbeReportedFieldsDiffer,
                        dto.VariableRate.Differences.Select(static difference =>
                            new MediaFrameRateDifference(
                                difference.StreamIndex,
                                FromNumber(difference.NominalFrameRate),
                                FromNumber(difference.AverageFrameRate))).ToArray()),
            MediaInfoWarningCode.DurationFieldsDiffer
                when evidenceCount == 1 && dto.DurationFields is not null =>
                    new DurationFieldsDifferWarningEvidence(
                        dto.DurationFields.Fields.Select(static field =>
                            new MediaDurationFieldEvidence(
                                ParseEnumExact<MediaDurationFieldKind>(field.Kind),
                                field.StreamIndex,
                                From(field.Value))).ToArray()),
            _ => throw InvalidCheckpoint(),
        };
        return new MediaInfoWarning(code, evidence);
    }

    private static DecodeDto CreateDecodeDto(DecodeValidationResult decode) =>
        new()
        {
            CandidateId = decode.CandidateId.ToString(),
            ProjectId = decode.ProjectId.ToString(),
            JobId = decode.JobId.ToString(),
            SourceFileName = decode.SourceFileName,
            ContentDigest = decode.ContentDigest.Hex,
            ByteSize = decode.ByteSize,
            VideoStreamIndex = decode.VideoStreamIndex,
            AudioStreamIndex = decode.AudioStreamIndex,
            ElapsedTicks = decode.Elapsed.Ticks,
            StandardErrorLineCount = decode.StandardErrorLineCount,
            StandardErrorContentDigest = decode.StandardErrorContentDigest.Hex,
            PresentationStart = ToDto(decode.PresentationStart),
            PresentationEnd = ToDto(decode.PresentationEnd),
            IndexedFrameCount = decode.IndexedFrameCount,
        };

    private static DecodeValidationResult RestoreDecode(DecodeDto dto) =>
        MediaCheckpointFactory.RestoreDecodeValidation(
            ParseId<AssetIdTag>(dto.CandidateId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<JobIdTag>(dto.JobId),
            dto.SourceFileName,
            ParseDigest(dto.ContentDigest),
            dto.ByteSize,
            dto.VideoStreamIndex,
            dto.AudioStreamIndex,
            TimeSpan.FromTicks(dto.ElapsedTicks),
            dto.StandardErrorLineCount,
            ParseDigest(dto.StandardErrorContentDigest),
            From(dto.PresentationStart),
            From(dto.PresentationEnd),
            dto.IndexedFrameCount);

    private static ArtifactDto CreateArtifactDto(StoredArtifact artifact) =>
        new()
        {
            Id = artifact.Id.ToString(),
            ProjectId = artifact.ProjectId.ToString(),
            Kind = artifact.Kind.ToString(),
            StorageReference = artifact.StorageReference,
            ContentDigest = artifact.ContentDigest.Hex,
            ByteSize = artifact.ByteSize,
            CreatedAt = FormatTimestamp(artifact.CreatedAt),
        };

    private static ProxyDto CreateProxyDto(GeneratedProxy proxy) =>
        new()
        {
            SourceAssetId = proxy.SourceAssetId.ToString(),
            SourceContentDigest = proxy.SourceContentDigest.Hex,
            SourceOriginalArtifactId = proxy.SourceOriginalArtifactId.ToString(),
            ProxyArtifact = CreateArtifactDto(proxy.ProxyArtifact),
            ProxyProbeResult = CreateProbeDto(proxy.ProxyProbeResult),
            ProxyPtsIndex = CreatePtsDto(proxy.ProxyPtsIndex),
            ProfileKind = MediaCheckpointFactory.GetProxyProfileKind(proxy.Profile),
            GenerationElapsedTicks = proxy.GenerationElapsed.Ticks,
        };

    private static PreviewDto CreatePreviewDto(GeneratedPreviewAssets preview) =>
        new()
        {
            WaveformIndexArtifact = CreateArtifactDto(
                preview.WaveformIndexArtifact),
            SpriteImageArtifact = CreateArtifactDto(preview.SpriteImageArtifact),
            SpriteIndexArtifact = CreateArtifactDto(preview.SpriteIndexArtifact),
            WaveformIndex = CreateWaveformIndexDto(preview.WaveformIndex),
            SpriteIndex = CreateSpriteIndexDto(preview.SpriteIndex),
            ProfileVersion = preview.Profile.Version,
        };

    private static WaveformIndexDto CreateWaveformIndexDto(
        PreviewWaveformIndex index) =>
        new()
        {
            SchemaVersion = index.SchemaVersion,
            SourceAssetId = index.SourceAssetId.ToString(),
            ProjectId = index.ProjectId.ToString(),
            ProxyArtifactId = index.ProxyArtifactId.ToString(),
            ProxyContentDigest = index.ProxyContentDigest.Hex,
            ProxyPtsIndexDigest = index.ProxyPtsIndexDigest.Hex,
            ProfileVersion = index.ProfileVersion,
            SampleRate = index.SampleRate,
            ChannelCount = index.ChannelCount,
            ChannelLayout = index.ChannelLayout,
            Frames = index.Frames.Select(static frame => new WaveformFrameDto
            {
                ProxyFrameSequence = frame.ProxyFrameSequence,
                ProxyPresentationTime = ToDto(frame.ProxyPresentationTime),
                ProxyDuration = ToDto(frame.ProxyDuration),
                Channels = frame.Channels.Select(static channel =>
                    new WaveformChannelDto
                    {
                        ChannelIndex = channel.ChannelIndex,
                        MinimumSample = channel.MinimumSample,
                        MaximumSample = channel.MaximumSample,
                    }).ToList(),
            }).ToList(),
        };

    private static PreviewWaveformIndex RestoreWaveformIndex(
        WaveformIndexDto dto)
    {
        if (!string.Equals(
                dto.SchemaVersion,
                PreviewWaveformIndex.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return MediaCheckpointFactory.RestoreWaveformIndex(
            ParseId<AssetIdTag>(dto.SourceAssetId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<ArtifactIdTag>(dto.ProxyArtifactId),
            ParseDigest(dto.ProxyContentDigest),
            ParseDigest(dto.ProxyPtsIndexDigest),
            dto.ProfileVersion,
            dto.SampleRate,
            dto.ChannelCount,
            dto.ChannelLayout,
            dto.Frames.Select(static frame =>
                MediaCheckpointFactory.RestoreWaveformFrame(
                    frame.ProxyFrameSequence,
                    From(frame.ProxyPresentationTime),
                    From(frame.ProxyDuration),
                    frame.Channels.Select(static channel =>
                        MediaCheckpointFactory.RestoreWaveformChannel(
                            channel.ChannelIndex,
                            channel.MinimumSample,
                            channel.MaximumSample)).ToArray())).ToArray());
    }

    private static SpriteIndexDto CreateSpriteIndexDto(
        ThumbnailSpriteIndex index) =>
        new()
        {
            SchemaVersion = index.SchemaVersion,
            SourceAssetId = index.SourceAssetId.ToString(),
            ProjectId = index.ProjectId.ToString(),
            ProxyArtifactId = index.ProxyArtifactId.ToString(),
            ProxyContentDigest = index.ProxyContentDigest.Hex,
            ProxyPtsIndexDigest = index.ProxyPtsIndexDigest.Hex,
            SpriteImageArtifactId = index.SpriteImageArtifactId.ToString(),
            SpriteImageDigest = index.SpriteImageDigest.Hex,
            ProfileVersion = index.ProfileVersion,
            CellWidth = index.CellWidth,
            CellHeight = index.CellHeight,
            Columns = index.Columns,
            Rows = index.Rows,
            Cells = index.Cells.Select(static cell => new SpriteCellDto
            {
                Ordinal = cell.Ordinal,
                ProxyFrameSequence = cell.ProxyFrameSequence,
                ProxyPresentationTime = ToDto(cell.ProxyPresentationTime),
                ProxyDuration = ToDto(cell.ProxyDuration),
                Column = cell.Column,
                Row = cell.Row,
            }).ToList(),
        };

    private static ThumbnailSpriteIndex RestoreSpriteIndex(SpriteIndexDto dto)
    {
        if (!string.Equals(
                dto.SchemaVersion,
                ThumbnailSpriteIndex.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return MediaCheckpointFactory.RestoreSpriteIndex(
            ParseId<AssetIdTag>(dto.SourceAssetId),
            ParseId<ProjectIdTag>(dto.ProjectId),
            ParseId<ArtifactIdTag>(dto.ProxyArtifactId),
            ParseDigest(dto.ProxyContentDigest),
            ParseDigest(dto.ProxyPtsIndexDigest),
            ParseId<ArtifactIdTag>(dto.SpriteImageArtifactId),
            ParseDigest(dto.SpriteImageDigest),
            dto.ProfileVersion,
            dto.CellWidth,
            dto.CellHeight,
            dto.Columns,
            dto.Rows,
            dto.Cells.Select(static cell =>
                MediaCheckpointFactory.RestoreSpriteCell(
                    cell.Ordinal,
                    cell.ProxyFrameSequence,
                    From(cell.ProxyPresentationTime),
                    From(cell.ProxyDuration),
                    cell.Column,
                    cell.Row)).ToArray());
    }

    private static void ValidateShape(CheckpointDto dto)
    {
        if (!string.Equals(dto.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
            dto.Stage is < 0 or > (int)IngestWorkflowStage.PreviewAssetsCommitted ||
            !HasValueAt(dto.StagedMedia, dto.Stage, IngestWorkflowStage.CopiedToStaging) ||
            !HasValueAt(dto.Asset, dto.Stage, IngestWorkflowStage.CopiedToStaging) ||
            !HasValueAt(dto.ProbeResult, dto.Stage, IngestWorkflowStage.Probed) ||
            !HasValueAt(dto.SourcePtsIndex, dto.Stage, IngestWorkflowStage.SourceIndexed) ||
            !HasValueAt(dto.MediaInfo, dto.Stage, IngestWorkflowStage.WarningsNormalized) ||
            !HasValueAt(dto.DecodeValidation, dto.Stage, IngestWorkflowStage.DecodeValidated) ||
            !HasValueAt(dto.OriginalArtifact, dto.Stage, IngestWorkflowStage.OriginalCommitted) ||
            !HasValueAt(dto.OriginalIntegrityStatus, dto.Stage, IngestWorkflowStage.OriginalCommitted) ||
            !HasValueAt(dto.Proxy, dto.Stage, IngestWorkflowStage.ProxyCommitted) ||
            !HasValueAt(dto.FrameMapDigest, dto.Stage, IngestWorkflowStage.FrameMapBuilt) ||
            !HasValueAt(dto.PreviewAssets, dto.Stage, IngestWorkflowStage.PreviewAssetsCommitted))
        {
            throw InvalidCheckpoint();
        }

        _ = ParseId<JobIdTag>(dto.JobId);
        _ = ParseDigest(dto.RequestDigest);
    }

    private static bool HasValueAt<T>(
        T? value,
        int stage,
        IngestWorkflowStage requiredStage)
        where T : class =>
        (value is not null) == (stage >= (int)requiredStage);

    private static string SerializeCanonical<T>(T value)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return StrictUtf8.GetString(CanonicalJson.CanonicalizeToUtf8(serialized));
    }

    private static T DeserializeCanonical<T>(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(canonicalJson);
            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(bytes);
            if (!bytes.AsSpan().SequenceEqual(canonical))
            {
                throw InvalidCheckpoint();
            }

            T? value = JsonSerializer.Deserialize<T>(canonical, JsonOptions);
            return value ?? throw InvalidCheckpoint();
        }
        catch (InvalidOperationException exception)
            when (exception.Message == "The durable ingest checkpoint is invalid.")
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or DecoderFallbackException or
                EncoderFallbackException or NotSupportedException)
        {
            throw InvalidCheckpoint();
        }
    }

    private static EntityId<TTag> ParseId<TTag>(string value)
    {
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return id;
    }

    private static ContentDigest ParseDigest(string value)
    {
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return digest;
    }

    private static TEnum ParseEnumExact<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return parsed;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(value, FormatTimestamp(parsed), StringComparison.Ordinal))
        {
            throw InvalidCheckpoint();
        }

        return parsed;
    }

    private static ContentDigest DigestCanonicalJson(string canonicalJson) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(canonicalJson));

    private static RationalDto ToDto(RationalTime value) =>
        new() { Value = value.Value, Rate = value.Rate };

    private static RationalDto? ToDto(RationalTime? value) =>
        value is null ? null : ToDto(value.Value);

    private static RationalDto ToDto(RationalNumber value) =>
        new() { Value = value.Numerator, Rate = value.Denominator };

    private static RationalDto? ToDto(RationalNumber? value) =>
        value is null ? null : ToDto(value.Value);

    private static RationalTime From(RationalDto dto) =>
        new(dto.Value, dto.Rate);

    private static RationalTime? FromNullable(RationalDto? dto) =>
        dto is null ? null : From(dto);

    private static RationalNumber FromNumber(RationalDto dto) =>
        new(dto.Value, dto.Rate);

    private static RationalNumber? FromNullableNumber(RationalDto? dto) =>
        dto is null ? null : FromNumber(dto);

    private static int CountNotNull(params object?[] values) =>
        values.Count(static value => value is not null);

    private static bool IsDomainValidationFailure(Exception exception) =>
        exception is ArgumentException or OverflowException;

    private static InvalidOperationException InvalidCheckpoint() =>
        new("The durable ingest checkpoint is invalid.");

    private static InvalidOperationException InvalidCheckpoint(
        Exception innerException) =>
        new("The durable ingest checkpoint is invalid.", innerException);

    private sealed class RequestDto
    {
        public required string Version { get; set; }
        public required long StageCount { get; set; }
        public required string JobId { get; set; }
        public required string ProjectId { get; set; }
        public required string AssetCandidateId { get; set; }
        public required string OriginalArtifactId { get; set; }
        public required string ProxyArtifactId { get; set; }
        public required string WaveformIndexArtifactId { get; set; }
        public required string SpriteImageArtifactId { get; set; }
        public required string SpriteIndexArtifactId { get; set; }
        public required string MediaType { get; set; }
        public required RightsDto Rights { get; set; }
        public required int? MinimumVideoWidth { get; set; }
        public required int? MinimumVideoHeight { get; set; }
        public required long OperationTimeoutTicks { get; set; }
    }

    private sealed class CheckpointDto
    {
        public required string SchemaVersion { get; set; }
        public required int Stage { get; set; }
        public required string JobId { get; set; }
        public required string RequestDigest { get; set; }
        public required StagedDto? StagedMedia { get; set; }
        public required AssetDto? Asset { get; set; }
        public required ProbeDto? ProbeResult { get; set; }
        public required PtsDto? SourcePtsIndex { get; set; }
        public required MediaInfoDto? MediaInfo { get; set; }
        public required DecodeDto? DecodeValidation { get; set; }
        public required ArtifactDto? OriginalArtifact { get; set; }
        public required string? OriginalIntegrityStatus { get; set; }
        public required ProxyDto? Proxy { get; set; }
        public required string? FrameMapDigest { get; set; }
        public required PreviewDto? PreviewAssets { get; set; }
    }

    private sealed class StagedDto
    {
        public required string CandidateId { get; set; }
        public required string ProjectId { get; set; }
        public required string JobId { get; set; }
        public required string SourceFileName { get; set; }
        public required string StagingReference { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required string CreatedAt { get; set; }
    }

    private sealed class AssetDto
    {
        public required string Id { get; set; }
        public required string FileName { get; set; }
        public required string MediaType { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required RightsDto Rights { get; set; }
    }

    private sealed class RightsDto
    {
        public required string Status { get; set; }
        public required string? EvidenceReference { get; set; }
        public required string AllowedUse { get; set; }
        public required bool ExternalPublicationAllowed { get; set; }
    }

    private sealed class ProbeDto
    {
        public required string CandidateId { get; set; }
        public required string ProjectId { get; set; }
        public required string JobId { get; set; }
        public required string SourceFileName { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required string ContainerSignature { get; set; }
        public required List<string> FormatNames { get; set; }
        public required string? FormatLongName { get; set; }
        public required decimal? DurationSeconds { get; set; }
        public required long? DeclaredByteSize { get; set; }
        public required long? BitRate { get; set; }
        public required int? ProbeScore { get; set; }
        public required List<ProbedStreamDto> Streams { get; set; }
        public required List<string> Warnings { get; set; }
    }

    private sealed class ProbedStreamDto
    {
        public required int Index { get; set; }
        public required string Kind { get; set; }
        public required string CodecName { get; set; }
        public required string? Profile { get; set; }
        public required int? Width { get; set; }
        public required int? Height { get; set; }
        public required string? PixelFormat { get; set; }
        public required RationalDto? NominalFrameRate { get; set; }
        public required RationalDto? AverageFrameRate { get; set; }
        public required RationalDto TimeBase { get; set; }
        public required decimal? StartSeconds { get; set; }
        public required decimal? DurationSeconds { get; set; }
        public required int? SampleRate { get; set; }
        public required int? Channels { get; set; }
        public required string? ChannelLayout { get; set; }
    }

    private sealed class PtsDto
    {
        public required string SchemaVersion { get; set; }
        public required string CandidateId { get; set; }
        public required string ProjectId { get; set; }
        public required string JobId { get; set; }
        public required string SourceFileName { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required int StreamIndex { get; set; }
        public required RationalDto TimeBase { get; set; }
        public required List<PtsEntryDto> Entries { get; set; }
    }

    private sealed class PtsEntryDto
    {
        public required int Sequence { get; set; }
        public required RationalDto PresentationTime { get; set; }
        public required RationalDto Duration { get; set; }
        public required bool KeyFrame { get; set; }
    }

    private sealed class MediaInfoDto
    {
        public required string CandidateId { get; set; }
        public required string ProjectId { get; set; }
        public required string JobId { get; set; }
        public required string SourceFileName { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required string ContainerClassification { get; set; }
        public required List<string> FormatNames { get; set; }
        public required string? FormatLongName { get; set; }
        public required RationalDto? FormatDuration { get; set; }
        public required List<MediaStreamDto> Streams { get; set; }
        public required int? RequiredMinimumVideoWidth { get; set; }
        public required int? RequiredMinimumVideoHeight { get; set; }
        public required List<WarningDto> Warnings { get; set; }
    }

    private sealed class MediaStreamDto
    {
        public required int Index { get; set; }
        public required string Kind { get; set; }
        public required string CodecName { get; set; }
        public required string? Profile { get; set; }
        public required int? Width { get; set; }
        public required int? Height { get; set; }
        public required string? PixelFormat { get; set; }
        public required RationalDto? NominalFrameRate { get; set; }
        public required RationalDto? AverageFrameRate { get; set; }
        public required RationalDto TimeBase { get; set; }
        public required RationalDto? Start { get; set; }
        public required RationalDto? Duration { get; set; }
        public required int? SampleRate { get; set; }
        public required int? Channels { get; set; }
        public required string? ChannelLayout { get; set; }
    }

    private sealed class WarningDto
    {
        public required string Code { get; set; }
        public required StreamCountEvidenceDto? StreamCount { get; set; }
        public required LowResolutionEvidenceDto? LowResolution { get; set; }
        public required VariableRateEvidenceDto? VariableRate { get; set; }
        public required DurationEvidenceDto? DurationFields { get; set; }
    }

    private sealed class StreamCountEvidenceDto
    {
        public required int ObservedCount { get; set; }
    }

    private sealed class LowResolutionEvidenceDto
    {
        public required int ObservedWidth { get; set; }
        public required int ObservedHeight { get; set; }
        public required int RequiredMinimumWidth { get; set; }
        public required int RequiredMinimumHeight { get; set; }
    }

    private sealed class VariableRateEvidenceDto
    {
        public required bool ProbeReportedFieldsDiffer { get; set; }
        public required List<FrameRateDifferenceDto> Differences { get; set; }
    }

    private sealed class FrameRateDifferenceDto
    {
        public required int StreamIndex { get; set; }
        public required RationalDto NominalFrameRate { get; set; }
        public required RationalDto AverageFrameRate { get; set; }
    }

    private sealed class DurationEvidenceDto
    {
        public required List<DurationFieldDto> Fields { get; set; }
    }

    private sealed class DurationFieldDto
    {
        public required string Kind { get; set; }
        public required int? StreamIndex { get; set; }
        public required RationalDto Value { get; set; }
    }

    private sealed class DecodeDto
    {
        public required string CandidateId { get; set; }
        public required string ProjectId { get; set; }
        public required string JobId { get; set; }
        public required string SourceFileName { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required int VideoStreamIndex { get; set; }
        public required int? AudioStreamIndex { get; set; }
        public required long ElapsedTicks { get; set; }
        public required int StandardErrorLineCount { get; set; }
        public required string StandardErrorContentDigest { get; set; }
        public required RationalDto PresentationStart { get; set; }
        public required RationalDto PresentationEnd { get; set; }
        public required int IndexedFrameCount { get; set; }
    }

    private sealed class ArtifactDto
    {
        public required string Id { get; set; }
        public required string ProjectId { get; set; }
        public required string Kind { get; set; }
        public required string StorageReference { get; set; }
        public required string ContentDigest { get; set; }
        public required long ByteSize { get; set; }
        public required string CreatedAt { get; set; }
    }

    private sealed class ProxyDto
    {
        public required string SourceAssetId { get; set; }
        public required string SourceContentDigest { get; set; }
        public required string SourceOriginalArtifactId { get; set; }
        public required ArtifactDto ProxyArtifact { get; set; }
        public required ProbeDto ProxyProbeResult { get; set; }
        public required PtsDto ProxyPtsIndex { get; set; }
        public required string ProfileKind { get; set; }
        public required long GenerationElapsedTicks { get; set; }
    }

    private sealed class PreviewDto
    {
        public required ArtifactDto WaveformIndexArtifact { get; set; }
        public required ArtifactDto SpriteImageArtifact { get; set; }
        public required ArtifactDto SpriteIndexArtifact { get; set; }
        public required WaveformIndexDto WaveformIndex { get; set; }
        public required SpriteIndexDto SpriteIndex { get; set; }
        public required string ProfileVersion { get; set; }
    }

    private sealed class WaveformIndexDto
    {
        public required string SchemaVersion { get; set; }
        public required string SourceAssetId { get; set; }
        public required string ProjectId { get; set; }
        public required string ProxyArtifactId { get; set; }
        public required string ProxyContentDigest { get; set; }
        public required string ProxyPtsIndexDigest { get; set; }
        public required string ProfileVersion { get; set; }
        public required int SampleRate { get; set; }
        public required int ChannelCount { get; set; }
        public required string? ChannelLayout { get; set; }
        public required List<WaveformFrameDto> Frames { get; set; }
    }

    private sealed class WaveformFrameDto
    {
        public required int ProxyFrameSequence { get; set; }
        public required RationalDto ProxyPresentationTime { get; set; }
        public required RationalDto ProxyDuration { get; set; }
        public required List<WaveformChannelDto> Channels { get; set; }
    }

    private sealed class WaveformChannelDto
    {
        public required int ChannelIndex { get; set; }
        public required short MinimumSample { get; set; }
        public required short MaximumSample { get; set; }
    }

    private sealed class SpriteIndexDto
    {
        public required string SchemaVersion { get; set; }
        public required string SourceAssetId { get; set; }
        public required string ProjectId { get; set; }
        public required string ProxyArtifactId { get; set; }
        public required string ProxyContentDigest { get; set; }
        public required string ProxyPtsIndexDigest { get; set; }
        public required string SpriteImageArtifactId { get; set; }
        public required string SpriteImageDigest { get; set; }
        public required string ProfileVersion { get; set; }
        public required int CellWidth { get; set; }
        public required int CellHeight { get; set; }
        public required int Columns { get; set; }
        public required int Rows { get; set; }
        public required List<SpriteCellDto> Cells { get; set; }
    }

    private sealed class SpriteCellDto
    {
        public required int Ordinal { get; set; }
        public required int ProxyFrameSequence { get; set; }
        public required RationalDto ProxyPresentationTime { get; set; }
        public required RationalDto ProxyDuration { get; set; }
        public required int Column { get; set; }
        public required int Row { get; set; }
    }

    private sealed class RationalDto
    {
        public required long Value { get; set; }
        public required long Rate { get; set; }
    }

    private sealed class CanonicalInt16Converter : JsonConverter<short>
    {
        public override short Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            decimal value = ReadIntegralDecimal(ref reader);
            if (value < short.MinValue || value > short.MaxValue)
            {
                throw new JsonException();
            }

            return decimal.ToInt16(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            short value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private sealed class CanonicalInt32Converter : JsonConverter<int>
    {
        public override int Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            decimal value = ReadIntegralDecimal(ref reader);
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new JsonException();
            }

            return decimal.ToInt32(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            int value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private sealed class CanonicalInt64Converter : JsonConverter<long>
    {
        public override long Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            decimal value = ReadIntegralDecimal(ref reader);
            if (value < long.MinValue || value > long.MaxValue)
            {
                throw new JsonException();
            }

            return decimal.ToInt64(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    private static decimal ReadIntegralDecimal(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number ||
            !reader.TryGetDecimal(out decimal value) ||
            value != decimal.Truncate(value))
        {
            throw new JsonException();
        }

        return value;
    }
}
