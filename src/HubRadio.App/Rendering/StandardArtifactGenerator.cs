using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Layout;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Rendering;

public sealed record StandardShortArtifactInput(
    EntityId<StandardShortFormPlanIdTag> ShortFormId,
    StandardSubtitleInput Subtitles);

/// <summary>
/// Internal-publication generation, NOT approval or delivery. Original and source
/// index are the actual IngestWorkflowRunResult.Original/SourcePtsIndex outputs.
/// Tool/model input digests bind the executable/weights respectively.
/// </summary>
public sealed class StandardArtifactGenerationRequest
{
    public StandardArtifactGenerationRequest(
        StandardCompositionPreparation preparation,
        IngestWorkflowRunResult ingest,
        SourceDisplayGeometry sourceDisplayGeometry,
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        FfmpegToolBinding tool,
        RenderToolEvidence toolEvidence,
        RenderModelEvidence modelEvidence,
        FfmpegFontBinding font,
        StandardSubtitleInput landscapeSubtitles,
        IReadOnlyList<StandardShortArtifactInput> shortForms,
        string applicationVersion,
        ContentDigest applicationDigest,
        TimeSpan processTimeout,
        StandardMusicRenderInput? music = null)
        : this(preparation, RequireCompletedOriginal(ingest), ingest.SourcePtsIndex!, sourceDisplayGeometry,
            settingsProfile, settings, tool, toolEvidence, modelEvidence, font, landscapeSubtitles,
            shortForms, applicationVersion, applicationDigest, processTimeout, music)
    {
    }

    public StandardArtifactGenerationRequest(
        StandardCompositionPreparation preparation,
        ImportedOriginal original,
        PtsIndex sourcePtsIndex,
        SourceDisplayGeometry sourceDisplayGeometry,
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        FfmpegToolBinding tool,
        RenderToolEvidence toolEvidence,
        RenderModelEvidence modelEvidence,
        FfmpegFontBinding font,
        StandardSubtitleInput landscapeSubtitles,
        IReadOnlyList<StandardShortArtifactInput> shortForms,
        string applicationVersion,
        ContentDigest applicationDigest,
        TimeSpan processTimeout,
        StandardMusicRenderInput? music = null)
    {
        Preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        Original = original ?? throw new ArgumentNullException(nameof(original));
        SourcePtsIndex = sourcePtsIndex ?? throw new ArgumentNullException(nameof(sourcePtsIndex));
        SourceDisplayGeometry = sourceDisplayGeometry ?? throw new ArgumentNullException(nameof(sourceDisplayGeometry));
        SettingsProfile = settingsProfile ?? throw new ArgumentNullException(nameof(settingsProfile));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        ToolEvidence = toolEvidence ?? throw new ArgumentNullException(nameof(toolEvidence));
        ModelEvidence = modelEvidence ?? throw new ArgumentNullException(nameof(modelEvidence));
        Font = font ?? throw new ArgumentNullException(nameof(font));
        LandscapeSubtitles = landscapeSubtitles ?? throw new ArgumentNullException(nameof(landscapeSubtitles));
        ApplicationDigest = applicationDigest ?? throw new ArgumentNullException(nameof(applicationDigest));
        Music = music;
        ArgumentNullException.ThrowIfNull(shortForms);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (applicationVersion.Length > 128 || applicationVersion != applicationVersion.Trim())
        {
            throw new ArgumentException("Invalid application version.", nameof(applicationVersion));
        }

        if (processTimeout <= TimeSpan.Zero || processTimeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        }

        int count = shortForms.Count;
        if (count > StandardCompositionPlan.MaximumShortFormCount)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ResourceLimit);
        }

        var copy = new StandardShortArtifactInput[count];
        for (int i = 0; i < count; i++)
        {
            copy[i] = shortForms[i] ?? throw new ArgumentException("Null short input.", nameof(shortForms));
            if (copy[i].ShortFormId is null || copy[i].Subtitles is null)
            {
                throw new ArgumentException("Incomplete short input.", nameof(shortForms));
            }
        }

        ShortForms = Array.AsReadOnly(copy);
        ApplicationVersion = applicationVersion;
        ProcessTimeout = processTimeout;
        Validate();
        Configuration = StandardRenderConfiguration.FromSettings(SettingsProfile, Settings,
            Plan.LongForm.Profile.Profile.FrameRate,
            Original.MediaInfo.Streams.Single(static item => item.Kind == MediaStreamType.Audio).Channels
                ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting));
    }

    public StandardCompositionPreparation Preparation { get; }
    public StandardCompositionPlan Plan => Preparation.Plan;
    public EntityId<ProjectIdTag> ProjectId => Plan.ProjectId;
    public CompositionVersion CompositionVersion => Preparation.Child;
    public ImportedOriginal Original { get; }
    public PtsIndex SourcePtsIndex { get; }
    public SourceDisplayGeometry SourceDisplayGeometry { get; }
    public DefaultSettingsProfile SettingsProfile { get; }
    public DefaultSettingsSnapshot Settings { get; }
    public FfmpegToolBinding Tool { get; }
    public RenderToolEvidence ToolEvidence { get; }
    public RenderModelEvidence ModelEvidence { get; }
    public FfmpegFontBinding Font { get; }
    public StandardSubtitleInput LandscapeSubtitles { get; }
    public IReadOnlyList<StandardShortArtifactInput> ShortForms { get; }
    public string ApplicationVersion { get; }
    public ContentDigest ApplicationDigest { get; }
    public TimeSpan ProcessTimeout { get; }
    public StandardRenderConfiguration Configuration { get; }
    public StandardMusicRenderInput? Music { get; }

    private static ImportedOriginal RequireCompletedOriginal(IngestWorkflowRunResult ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (!ingest.IsComplete || ingest.ReconciliationRequired || ingest.Original is null || ingest.SourcePtsIndex is null)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        }

        return ingest.Original;
    }

    internal void Validate()
    {
        StandardRenderGraphFactory.ValidatePreparation(Preparation);
        StandardRenderGraphFactory.ValidateCapturedGeometry(Preparation, SourceDisplayGeometry);
        StandardThumbnailGraphFactory.ValidateCapturedIndex(Preparation, SourcePtsIndex, sourceArtifactId: Original.Artifact.Id);
        if (Plan.MusicPlanning.Assignments.Any(static item => item.TrackDigest is not null) && Music is null)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
        if (Music is not null)
        {
            var eligible = Settings.Values.Single(static item => item.Key == "assets.musicEligibleSections").Value.TextListValue;
            foreach (var soundtrack in Music.Resolve(Preparation))
            {
                string section = soundtrack.Evidence.Kind switch
                {
                    MusicCueKind.TitleCard => "titleCard",
                    MusicCueKind.TopicCard => "topicCard",
                    MusicCueKind.EndCard => "endCard",
                    _ => throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic),
                };
                if (!eligible.Contains(section, StringComparer.Ordinal))
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            }
        }
        string fontExtension = Path.GetExtension(Font.VerificationRequest.FilePath).ToLowerInvariant();
        if (fontExtension is not (".ttf" or ".otf" or ".ttc"))
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        }

        if (StandardRenderGraphFactory.InputDigest(Plan, StandardPlanInputKind.SettingsSnapshot) != Settings.ContentDigest
            || StandardRenderGraphFactory.InputDigest(Plan, StandardPlanInputKind.SettingsProfile) != SettingsProfile.ContentDigest
            || DefaultSettingsSnapshot.ParseCanonical(Settings.CanonicalJson, SettingsProfile).ContentDigest != Settings.ContentDigest
            || Settings.Values.Single(item => item.Key == "output.audioSampleRate").Value.IntegerValue != FfmpegCompiler.OutputAudioSampleRate
            || Settings.Values.Single(item => item.Key == "output.audioCodec").Value.TextValue != "AAC-LC"
            || StandardRenderGraphFactory.InputDigest(Plan, StandardPlanInputKind.Tool) != ToolEvidence.ExecutableDigest
            || StandardRenderGraphFactory.InputDigest(Plan, StandardPlanInputKind.Model) != ModelEvidence.WeightsDigest
            || ToolEvidence.Name != "ffmpeg" || ToolEvidence.Version != DecodeValidator.CanonicalPackageVersion
            || ToolEvidence.ContainerImageDigest is not null
            || ToolEvidence.ExecutableDigest != new ContentDigest(FfmpegCompiler.FixedFfmpegSha256)
            || Tool.VerificationRequest.ComponentIdentity != "ffmpeg"
            || Tool.VerificationRequest.ExpectedDigest != ToolEvidence.ExecutableDigest
            || Tool.VerificationRequest.ExpectedByteSize != FfmpegCompiler.FixedFfmpegByteSize
            || ShortForms.Count != Plan.ShortForms.Count
            || ShortForms.Select(static item => item.ShortFormId).Distinct().Count() != ShortForms.Count
            || ShortForms.Any(item => !Plan.ShortForms.Any(plan => plan.Id == item.ShortFormId)))
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        }

        Asset source = Original.Asset;
        if (Original.IntegrityStatus != OriginalIntegrityStatus.Verified
            || Original.Artifact.Kind != ArtifactKind.Original || Original.Artifact.ProjectId != ProjectId
            || Original.Artifact.ContentDigest != source.ContentDigest || Original.Artifact.ByteSize != source.ByteSize
            || SourcePtsIndex.ProjectId != ProjectId || SourcePtsIndex.CandidateId != source.Id
            || SourcePtsIndex.ContentDigest != source.ContentDigest || SourcePtsIndex.ByteSize != source.ByteSize
            || Original.MediaInfo.ProjectId != ProjectId || Original.MediaInfo.CandidateId != source.Id
            || Original.MediaInfo.ContentDigest != source.ContentDigest || Original.MediaInfo.ByteSize != source.ByteSize
            || !Original.DecodeValidation.DecodedSuccessfully
            || Original.DecodeValidation.ContentDigest != source.ContentDigest
            || Original.DecodeValidation.CandidateId != source.Id
            || Original.DecodeValidation.ProjectId != ProjectId
            || Original.DecodeValidation.ByteSize != source.ByteSize
            || Original.DecodeValidation.JobId != SourcePtsIndex.JobId
            || Original.MediaInfo.JobId != SourcePtsIndex.JobId
            || Original.DecodeValidation.PresentationStart != SourcePtsIndex.PresentationStart
            || Original.DecodeValidation.PresentationEnd != SourcePtsIndex.PresentationEnd
            || Original.DecodeValidation.IndexedFrameCount != SourcePtsIndex.Entries.Count
            || StandardRenderGraphFactory.InputDigest(Plan, StandardPlanInputKind.SourceAsset) != source.ContentDigest)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        }

        MediaStreamInfo[] video = Original.MediaInfo.Streams.Where(static item => item.Kind == MediaStreamType.Video).ToArray();
        MediaStreamInfo[] audio = Original.MediaInfo.Streams.Where(static item => item.Kind == MediaStreamType.Audio).ToArray();
        // Input seeking currently assumes a zero-origin shared clock. Do not reset
        // independently offset streams and thereby silently change their sync.
        if (video.Length != 1 || audio.Length != 1 || video[0].Start != default(RationalTime)
            || audio[0].Start != default(RationalTime) || audio[0].Duration is null
            || audio[0].SampleRate is null
            || SourcePtsIndex.PresentationStart != default(RationalTime)
            || SourcePtsIndex.StreamIndex != video[0].Index
            || Original.DecodeValidation.VideoStreamIndex != video[0].Index
            || Original.DecodeValidation.AudioStreamIndex != audio[0].Index
            || video[0].NominalFrameRate != Plan.LongForm.Profile.Profile.FrameRate)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
        }

        foreach (TimeRange range in Preparation.ChildTimelineMap.Entries.Select(static entry => entry.SourceRange)
            .Concat(Plan.ShortForms.SelectMany(static item => item.SourceSpans).Select(static span => (TimeRange?)span.Range))
            .Select(static range => range ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch)))
        {
            if (range.Start < default(RationalTime) || range.Duration <= default(RationalTime)
                || range.End > SourcePtsIndex.PresentationEnd
                || range.End > audio[0].Duration!.Value
                || !FfmpegCompiler.IsOnOutputGrid(range.Start, Plan.LongForm.Profile.Profile)
                || !FfmpegCompiler.IsOnOutputGrid(range.Duration, Plan.LongForm.Profile.Profile)
                || !FfmpegCompiler.IsOnSampleGrid(range.Start, audio[0].SampleRate!.Value)
                || !FfmpegCompiler.IsOnSampleGrid(range.Duration, audio[0].SampleRate!.Value)
                || !HasExactSourceFrameEdges(range))
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
            }
        }

        ValidateCfrSourceFrames();

        foreach (TimelineMapEntry entry in Preparation.ChildTimelineMap.Entries)
        {
            if (!FfmpegCompiler.IsOnOutputGrid(entry.OutputRange.Start, Plan.LongForm.Profile.Profile)
                || !FfmpegCompiler.IsOnOutputGrid(entry.OutputRange.Duration, Plan.LongForm.Profile.Profile))
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
            }
        }

        if (Plan.ShortForms.SelectMany(static item => item.Layout.SourcePlacements)
            .Any(item => !item.SourceDisplayGeometry.Equals(SourceDisplayGeometry)))
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        }

        // Validate native projection bindings before any staging/probing/promotion,
        // including short inputs with no cues that would otherwise be omitted.
        StandardRenderGraphFactory.ValidateSubtitleProjection(Preparation, LandscapeSubtitles);
        foreach (StandardShortArtifactInput shortInput in ShortForms)
            StandardRenderGraphFactory.ValidateSubtitleProjection(Preparation, shortInput.Subtitles, shortInput.ShortFormId);
    }

    private bool HasExactSourceFrameEdges(TimeRange range)
    {
        int first = FirstSourceFrameAtOrAfter(range.Start);
        int afterLast = FirstSourceFrameAtOrAfter(range.End);
        return first < SourcePtsIndex.Entries.Count && afterLast > first
            && SourcePtsIndex.Entries[first].PresentationTime == range.Start
            && SourcePtsIndex.Entries[afterLast - 1].PresentationTime
                + SourcePtsIndex.Entries[afterLast - 1].Duration == range.End;
    }

    private int FirstSourceFrameAtOrAfter(RationalTime time)
    {
        int low = 0;
        int high = SourcePtsIndex.Entries.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (SourcePtsIndex.Entries[middle].PresentationTime < time)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private void ValidateCfrSourceFrames()
    {
        RenderProfile profile = Plan.LongForm.Profile.Profile;
        RationalTime frameDuration = RationalTime.FromFrames(1,
            profile.FrameRateNumerator, profile.FrameRateDenominator);
        RationalTime expectedStart = default;
        // The current renderer supports only an exact, zero-origin CFR source.
        // Check every measured frame, including excluded cuts, rather than letting
        // the output fps filter fill holes, drop frames or round a VFR source.
        foreach (PtsIndexEntry frame in SourcePtsIndex.Entries)
        {
            if (frame.PresentationTime != expectedStart || frame.Duration != frameDuration)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
            expectedStart = frame.PresentationTime + frame.Duration;
        }
    }
}

public sealed record StandardGenerationOmission(string Role, string Reason);

public sealed record StandardGenerationWarning(string Role, string Code)
{
    /// <summary>The source builder's setting or policy key; Code retains its exact reason.</summary>
    public string? Key { get; init; }
    public string? Rule { get; init; }
    public bool IsUnresolved { get; init; }
    /// <summary>Original-source time, never retimed to the output or inferred from surviving cues.</summary>
    public TimeRange? SourceRange { get; init; }
    public EntityId<SourceSpanIdTag>? SourceSpanId { get; init; }
    public EntityId<ArtifactIdTag>? SourceArtifactId { get; init; }
    public ContentDigest? SettingsSnapshotDigest { get; init; }
}

public enum StandardArtifactRole
{
    LandscapeCleanVideo,
    LandscapeBurnInVideo,
    VerticalBurnInVideo,
    LandscapeSubRip,
    LandscapeWebVtt,
    LandscapeThumbnail,
    VerticalThumbnail,
}

public sealed record StandardTypedArtifact(StandardArtifactRole Role, StoredArtifact Artifact)
{
    public bool IsVideo => Role is StandardArtifactRole.LandscapeCleanVideo
        or StandardArtifactRole.LandscapeBurnInVideo or StandardArtifactRole.VerticalBurnInVideo;
}

public sealed record StandardRenderExecutionEvidence(FfmpegEncoderSettings? EncoderSettings,
    ContentDigest FilterScriptDigest, long FilterScriptByteSize, string ScriptTransport,
    IReadOnlyList<StandardTextOverlayEvidence> TextOverlays,
    StandardLoudnessEvidence? Loudness, StandardThumbnailRenderEvidence? Thumbnail,
    StandardSourceColorEvidence? SourceColor = null,
    IReadOnlyList<StandardMusicRenderEvidence>? Music = null);

/// <summary>An attempted promotion, not a claim that a DB row exists.</summary>
public sealed record StandardPromotionAttempt(
    EntityId<ArtifactIdTag> ArtifactId,
    EntityId<ProjectIdTag> ProjectId,
    string StagingPath,
    string StorageReference,
    ContentDigest ContentDigest,
    long ByteSize);

/// <summary>ConfigurationDigest in the manifest hashes this exact evidence.</summary>
public sealed record StandardArtifactBindingEvidence(
    string Role,
    EntityId<ArtifactIdTag> ArtifactId,
    EntityId<StandardCompositionPlanIdTag> PlanId,
    ContentDigest PlanDigest,
    EntityId<CompositionVersionIdTag> VersionId,
    ContentDigest VersionDigest,
    ContentDigest SettingsSnapshotDigest,
    EntityId<ArtifactIdTag> SourceArtifactId,
    ContentDigest SourcePtsIndexDigest,
    EntityId<RenderGraphIdTag> GraphId,
    ContentDigest GraphDigest,
    ContentDigest GeometryDigest,
    ContentDigest DisplayCueDigest,
    ContentDigest SubtitleConfigurationDigest,
    ContentDigest OutputDigest,
    long OutputByteSize,
    StandardRenderExecutionEvidence? Execution = null);

public sealed record StandardGeneratedArtifact(
    StoredArtifact Artifact,
    RenderGraph Graph,
    RenderManifest Manifest,
    StandardArtifactBindingEvidence BindingEvidence,
    RenderGraphValidationContext ValidationContext,
    IReadOnlyList<SubtitleFileCue> DisplayCues,
    IReadOnlyList<StandardSubtitleRenderFile> SubtitleFiles,
    OutputQcSubtitleEvidence? SubtitleEvidence,
    OutputQcRequest? QualityControlRequest,
    IReadOnlyList<StandardGraphicRenderFile>? GraphicFiles = null)
{
    public RenderGraphValidationContext Context => ValidationContext;

    // Producer-only handoff. The output-evaluation lane must explicitly retain
    // CanonicalJson as optional SubtitleEditProjection rendering-input evidence
    // and rederive native proof on reload; default/public JSON stays unchanged.
    internal SubtitleEditProjectionEvidence? SubtitleProjection { get; init; }

    public StandardTypedArtifact TypedArtifact => new(BindingEvidence.Role switch
    {
        "landscape-clean" => StandardArtifactRole.LandscapeCleanVideo,
        "landscape-burn-in" => StandardArtifactRole.LandscapeBurnInVideo,
        "landscape-srt" => StandardArtifactRole.LandscapeSubRip,
        "landscape-vtt" => StandardArtifactRole.LandscapeWebVtt,
        string role when role.StartsWith("vertical-burn-in-", StringComparison.Ordinal) => StandardArtifactRole.VerticalBurnInVideo,
        string role when role.StartsWith("thumbnail-landscape-", StringComparison.Ordinal) => StandardArtifactRole.LandscapeThumbnail,
        string role when role.StartsWith("thumbnail-vertical-", StringComparison.Ordinal) => StandardArtifactRole.VerticalThumbnail,
        _ => throw new InvalidOperationException("Unknown standard artifact role."),
    }, Artifact);
}

public enum StandardArtifactGenerationDisposition
{
    Completed,
    Failed,
    Cancelled,
    ReconciliationRequired,
    GeneratedWithOmissions,
}

public sealed class StandardArtifactGenerationResult
{
    internal StandardArtifactGenerationResult(
        StandardArtifactGenerationDisposition disposition,
        IReadOnlyList<StandardGeneratedArtifact> artifacts,
        IReadOnlyList<StandardGenerationOmission> omissions,
        IReadOnlyList<StandardPromotionAttempt> reconciliationArtifacts,
        StandardGenerationFailureReason? failureReason,
        FfmpegCompilerFailureReason? compilerFailureReason,
        bool stagingCleanupRequired,
        IReadOnlyList<StandardGenerationWarning>? warnings = null)
    {
        Disposition = disposition;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
        Omissions = Array.AsReadOnly(omissions.ToArray());
        ReconciliationArtifacts = Array.AsReadOnly(reconciliationArtifacts.ToArray());
        ReconciliationArtifactIds = Array.AsReadOnly(reconciliationArtifacts.Select(item => item.ArtifactId).ToArray());
        FailureReason = failureReason;
        CompilerFailureReason = compilerFailureReason;
        StagingCleanupRequired = stagingCleanupRequired;
        Warnings = Array.AsReadOnly((warnings ?? []).ToArray());
        CompositionVersions = Array.AsReadOnly(artifacts.Select(static item => item.Context.CompositionVersion)
            .DistinctBy(static version => version.Id).ToArray());
    }

    public StandardArtifactGenerationDisposition Disposition { get; }
    /// <summary>File generation completed; subtitle coverage and unmeasured policies remain in Warnings.</summary>
    public bool IsComplete => Disposition == StandardArtifactGenerationDisposition.Completed;
    public IReadOnlyList<StandardGeneratedArtifact> Artifacts { get; }
    public IReadOnlyList<StandardGenerationOmission> Omissions { get; }
    public IReadOnlyList<EntityId<ArtifactIdTag>> ReconciliationArtifactIds { get; }
    public IReadOnlyList<StandardPromotionAttempt> ReconciliationArtifacts { get; }
    public StandardGenerationFailureReason? FailureReason { get; }
    public FfmpegCompilerFailureReason? CompilerFailureReason { get; }
    public bool StagingCleanupRequired { get; }
    public IReadOnlyList<StandardGenerationWarning> Warnings { get; }
    /// <summary>
    /// Exact versions used by the outputs. The application must persist any
    /// missing derived version BEFORE evaluation. Generation does not advance or
    /// replace the project's working-copy head to checkpoint a short or image.
    /// </summary>
    public IReadOnlyList<CompositionVersion> CompositionVersions { get; }
}

public sealed class StandardArtifactGenerator
{
    private readonly ArtifactStore _store;
    private readonly VersionService _versions;
    private readonly string _workspace;
    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> _run;
    private readonly Func<FfmpegCompileRequest, CancellationToken, Task<FfmpegCompilation>> _compile;
    private readonly Func<StandardArtifactGenerationRequest, string, CancellationToken, Task<SourceDisplayGeometry>> _probeGeometry;
    private readonly Func<StandardArtifactGenerationRequest, string, CancellationToken, Task<StandardSourceColorEvidence?>> _probeColor;
    private readonly Func<StandardArtifactGenerationRequest, CancellationToken, Task<bool>> _verifyMusic;
    private readonly Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>> _promote;

    public StandardArtifactGenerator(ArtifactStore artifactStore, VersionService versionService,
        ProcessRunner processRunner, string workspacePath, string ffprobePath)
        : this(artifactStore, versionService, workspacePath,
            (processRunner ?? throw new ArgumentNullException(nameof(processRunner))).RunAsync,
            FfmpegCompiler.CompileAsync,
            (request, sourcePath, token) => StandardSourceGeometry.ProbeAsync(
                request.Original.MediaInfo, sourcePath, ffprobePath, processRunner, request.ProcessTimeout, token),
            probeColor: async (request, sourcePath, token) => await StandardSourceColor.ProbeAsync(
                request.Original.MediaInfo, sourcePath, ffprobePath, processRunner, request.ProcessTimeout, token).ConfigureAwait(false),
            verifyMusic: (request, token) => request.Music?.VerifyCatalogAsync(ffprobePath, processRunner, request.ProcessTimeout, token)
                ?? Task.FromResult(true))
    {
    }

    // Test seam only: the public entry point always uses the pinned compiler and ProcessRunner.
    internal StandardArtifactGenerator(
        ArtifactStore artifactStore, VersionService versionService, string workspacePath,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run,
        Func<FfmpegCompileRequest, CancellationToken, Task<FfmpegCompilation>> compile,
        Func<StandardArtifactGenerationRequest, string, CancellationToken, Task<SourceDisplayGeometry>> probeGeometry,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>? promote = null,
        Func<StandardArtifactGenerationRequest, string, CancellationToken, Task<StandardSourceColorEvidence?>>? probeColor = null,
        Func<StandardArtifactGenerationRequest, CancellationToken, Task<bool>>? verifyMusic = null)
    {
        _store = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _versions = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _compile = compile ?? throw new ArgumentNullException(nameof(compile));
        _probeGeometry = probeGeometry ?? throw new ArgumentNullException(nameof(probeGeometry));
        _probeColor = probeColor ?? ((_, _, _) => Task.FromResult<StandardSourceColorEvidence?>(null));
        _verifyMusic = verifyMusic ?? ((_, _) => Task.FromResult(true));
        _promote = promote ?? artifactStore.PromoteAsync;
        if (!Path.IsPathFullyQualified(workspacePath) || workspacePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A local absolute workspace is required.", nameof(workspacePath));
        }

        _workspace = Path.GetFullPath(workspacePath);
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspace);
    }

    public async Task<StandardArtifactGenerationResult> GenerateAsync(
        StandardArtifactGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        ValidateCheckpoint(request.Preparation);
        var omissions = request.Plan.Omissions.Select(static item =>
            new StandardGenerationOmission(item.ArtifactKind.ToString(), item.Code.ToString())).ToList();
        var warnings = request.Plan.Warnings.Select(static item => new StandardGenerationWarning("plan", item.Code.ToString()))
            .Concat(request.Plan.MusicPlanning.Warnings.Select(static item => new StandardGenerationWarning("music", item.ToString())))
            .Concat(request.Plan.MusicPlanning.Assignments.Where(static item => item.TrackDigest is null)
                .Select(static item => new StandardGenerationWarning("music", item.Reason.ToString()))).ToList();
        if (request.Plan.MusicPlanning.Assignments.Count == 0)
            warnings.Add(new StandardGenerationWarning("music", "NoEligibleNonDialogueCues"));

        AddSubtitleWarnings("landscape-subtitles", request.LandscapeSubtitles);
        foreach (StandardShortArtifactInput shortInput in request.ShortForms)
            AddSubtitleWarnings($"vertical-subtitles-{shortInput.ShortFormId}", shortInput.Subtitles);

        void AddSubtitleWarnings(string role, StandardSubtitleInput subtitles)
        {
            // Preserve warnings even if all cues for this input were excluded.
            // Surviving cues and successful generation do not establish completeness.
            warnings.AddRange(subtitles.Warnings.Select(warning => new StandardGenerationWarning(role, warning.Reason)
            {
                Key = warning.Key,
                Rule = warning.Rule,
                IsUnresolved = warning.IsUnresolved,
                SourceRange = warning.SourceRange,
                SourceSpanId = warning.SourceSpanId,
                SourceArtifactId = request.Original.Artifact.Id,
                SettingsSnapshotDigest = request.Settings.ContentDigest,
            }));
        }

        string runName = "standard-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(_workspace, "projects", request.ProjectId.ToString(), "staging", runName);
        var ownedFiles = new List<string>();
        var pending = new List<PendingArtifact>();
        var promoted = new List<StandardGeneratedArtifact>();
        var held = new List<IDisposable>();
        bool promotionStarted = false;
        bool stagingOwned = false;
        bool cleanupRequired = false;
        StandardArtifactGenerationDisposition disposition = StandardArtifactGenerationDisposition.Failed;
        StandardGenerationFailureReason? failure = null;
        FfmpegCompilerFailureReason? compilerFailure = null;
        var residual = new List<StandardPromotionAttempt>();
        try
        {
            string sourcePath = _store.GetValidatedRegisteredArtifactPath(request.Original.Artifact.Id);
            held.Add(OpenHeld(sourcePath));
            StoredArtifact source = await _store.ValidateRegisteredAsync(request.Original.Artifact.Id, cancellationToken).ConfigureAwait(false);
            if (source.ProjectId != request.ProjectId || source.Kind != ArtifactKind.Original
                || source.ContentDigest != request.Original.Asset.ContentDigest || source.ByteSize != request.Original.Asset.ByteSize
                || !WorkspacePathSafety.TryResolvePortableRelativePath(_workspace, source.StorageReference, out string expectedSourcePath)
                || !string.Equals(expectedSourcePath, sourcePath, WorkspacePathSafety.PathComparison))
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
            }

            SourceDisplayGeometry measuredGeometry = await _probeGeometry(request, sourcePath, cancellationToken).ConfigureAwait(false);
            if (!request.SourceDisplayGeometry.Equals(measuredGeometry))
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
            }

            StandardSourceColorEvidence? sourceColor = await _probeColor(request, sourcePath, cancellationToken).ConfigureAwait(false);
            if (sourceColor is not null && (sourceColor.SourceDigest != source.ContentDigest
                || sourceColor.StreamIndex != request.Original.MediaInfo.PrimaryVideoStream.Index))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
            if (sourceColor?.UsedUntaggedFallback == true)
                warnings.Add(new StandardGenerationWarning("video", "UntaggedSourceColorFallback"));
            if (sourceColor?.Conversion.ToneMapping == true)
                warnings.Add(new StandardGenerationWarning("video", "HdrToneMappedToSdrBt709NotQualityApproval"));
            FfmpegEncoderSettings configured = request.Configuration.Encoder;
            var encoder = new FfmpegEncoderSettings(configured.VideoBitrateBitsPerSecond, configured.AudioBitrateBitsPerSecond,
                configured.AudioChannels, configured.Loudness, sourceColor?.Conversion);

            bool renderMusic = await _verifyMusic(request, cancellationToken).ConfigureAwait(false);
            foreach (BinaryVerificationRequest track in renderMusic ? request.Music?.Tracks ?? [] : [])
            {
                try { held.Add(OpenHeld(track.FilePath)); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
                    || exception is WorkspacePathSafetyException { Failure: WorkspacePathSafetyFailure.Missing })
                {
                    renderMusic = false;
                    break;
                }
                BinaryVerificationResult verified;
                try
                {
                    verified = await new BinaryVerifier().VerifyAsync(track, cancellationToken).ConfigureAwait(false);
                }
                catch (BinaryVerificationException)
                {
                    // A changed/unverifiable assigned track is an input rejection,
                    // not an encoder failure or permission to publish without it.
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
                }
                if (!verified.IsVerified || verified.ActualDigest != track.ExpectedDigest || verified.ActualByteSize != track.ExpectedByteSize)
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedMusic);
            }
            if (!renderMusic)
                warnings.Add(new StandardGenerationWarning("music", "MusicLibraryUnavailableAtRenderCompletedWithoutMusic"));

            CreateStagingDirectory(staging);
            stagingOwned = true;
            held.Add(OpenHeld(request.Tool.VerificationRequest.FilePath));
            held.Add(OpenHeld(request.Font.VerificationRequest.FilePath));
            BinaryVerificationResult verifiedFont = await new BinaryVerifier().VerifyAsync(
                request.Font.VerificationRequest, cancellationToken).ConfigureAwait(false);
            if (!verifiedFont.IsVerified || verifiedFont.ActualDigest != request.Font.Evidence.FileDigest)
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
            }

            string fontsDirectory = Path.Combine(staging, "fonts");
            Directory.CreateDirectory(fontsDirectory);
            string fontPath = Path.Combine(fontsDirectory, "font" + Path.GetExtension(request.Font.VerificationRequest.FilePath));
            ownedFiles.Add(fontPath);
            File.Copy(request.Font.VerificationRequest.FilePath, fontPath, overwrite: false);
            held.Add(OpenHeld(fontPath));
            var localFont = new FfmpegFontBinding(request.Font.Evidence, new BinaryVerificationRequest(
                "standard.font", fontPath, request.Font.Evidence.FileDigest, request.Font.VerificationRequest.ExpectedByteSize));
            var outputs = new List<StandardRenderGraphOutput>();
            StandardRenderGraphOutput clean = StandardRenderGraphFactory.Create(request.Preparation, request.Original.Asset,
                request.SourceDisplayGeometry, false, request.LandscapeSubtitles, request.Font.Evidence,
                configuration: request.Configuration, music: request.Music);
            outputs.Add(clean);
            if (clean.DisplayCues.Count > 0)
            {
                outputs.Add(StandardRenderGraphFactory.Create(request.Preparation, request.Original.Asset,
                    request.SourceDisplayGeometry, true, request.LandscapeSubtitles, request.Font.Evidence,
                    configuration: request.Configuration, music: request.Music));
            }
            else
            {
                omissions.Add(new StandardGenerationOmission("landscape-burn-in/srt/vtt", "NoDisplayCues"));
            }

            foreach (StandardShortArtifactInput shortInput in request.ShortForms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shortInput.Subtitles.Revisions.Count == 0)
                {
                    omissions.Add(new StandardGenerationOmission($"vertical-burn-in-{shortInput.ShortFormId}", "NoDisplayCues"));
                    continue;
                }

                outputs.Add(StandardRenderGraphFactory.Create(request.Preparation, request.Original.Asset,
                    request.SourceDisplayGeometry, true, shortInput.Subtitles, request.Font.Evidence, shortInput.ShortFormId,
                    request.Configuration, request.Music));
            }

            foreach (StandardThumbnailPlan thumbnail in request.Plan.Thumbnails)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (bool vertical in new[] { false, true })
                    outputs.Add(StandardThumbnailGraphFactory.Create(request.Preparation, request.Original.Asset,
                        request.SourceDisplayGeometry, request.SourcePtsIndex, thumbnail, request.Configuration, vertical, sourceColor?.Conversion));
            }

            foreach (StandardRenderGraphOutput output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bindings = new List<FfmpegInputBinding>();
                foreach (RenderGraphInput input in output.Graph.Inputs)
                {
                    string path = sourcePath;
                    if (input.Kind is RenderInputKind.Subtitle or RenderInputKind.Graphic)
                    {
                        path = Path.Combine(staging, input.Id + ".ass");
                        ownedFiles.Add(path);
                        SubtitleExportFile file = input.Kind == RenderInputKind.Subtitle
                            ? output.SubtitleFiles.Single(item => item.Input.Id == input.Id).File
                            : output.GraphicFiles.Single(item => item.Input.Id == input.Id).File;
                        await WriteNewAsync(path, file.GetUtf8Bytes(), cancellationToken).ConfigureAwait(false);
                        held.Add(OpenHeld(path));
                    }

                    bindings.Add(new FfmpegInputBinding(input.Id, new BinaryVerificationRequest(
                        "standard.input", path, input.ContentDigest, input.ByteSize)));
                }

                EntityId<ArtifactIdTag> id = EntityId<ArtifactIdTag>.New();
                bool image = output.FrameExtraction is not null;
                string extension = image ? ".png" : ".mp4";
                string outputPath = Path.Combine(staging, id + extension);
                string scriptPath = Path.Combine(staging, id + ".ffscript");
                ownedFiles.Add(outputPath);
                ownedFiles.Add(scriptPath);
                FfmpegEncoderSettings? outputEncoder = image ? null : encoder;
                FfmpegAssGraphicBinding[] graphics = output.GraphicFiles.Select(static file => file.Binding).ToArray();
                IReadOnlyList<FfmpegMusicBinding> music = renderMusic ? output.MusicBindings : [];
                if (outputEncoder?.Loudness is FfmpegLoudnessTarget singlePass)
                {
                    // Measurement pass for loudnorm's linear mode (DEC3-1 (f)).
                    string measurementPath = Path.Combine(staging, id + ".loudness.mp4");
                    string measurementScriptPath = Path.Combine(staging, id + ".loudness.ffscript");
                    ownedFiles.Add(measurementPath);
                    ownedFiles.Add(measurementScriptPath);
                    using FfmpegCompilation measurement = await _compile(new FfmpegCompileRequest(
                        output.Graph, output.ValidationContext, request.Tool, bindings, measurementPath, staging,
                        request.ProcessTimeout, output.VideoGeometry, output.Graph.Fonts.Count == 0 ? [] : [localFont],
                        outputEncoder, new FfmpegGeneratedScriptTarget(measurementScriptPath), graphics, null, music,
                        loudnessMeasurement: true), cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(measurement.OutputPath, measurementPath, StringComparison.Ordinal)
                        || !string.Equals(measurement.GeneratedScriptPath, measurementScriptPath, StringComparison.Ordinal))
                    {
                        throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
                    }

                    ProcessRunResult measured = await _run(measurement.ProcessRequest, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (measured.ExitCode != 0 || measured.Truncated)
                    {
                        throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ProcessFailed);
                    }

                    StandardLoudnessEvidence input = StandardLoudnessReport.Read(measured, singlePass);
                    // Silence has no finite measurement; it keeps the single-pass target and its warning.
                    if (!input.Silent)
                        outputEncoder = new FfmpegEncoderSettings(encoder.VideoBitrateBitsPerSecond, encoder.AudioBitrateBitsPerSecond,
                            encoder.AudioChannels, singlePass.WithMeasurement(input.InputIntegratedLufs!.Value,
                                input.InputTruePeakDbtp!.Value, input.InputRangeLu, input.InputThresholdLufs, input.TargetOffsetDb!.Value),
                            encoder.SourceColor);
                }

                using FfmpegCompilation compilation = await _compile(new FfmpegCompileRequest(
                    output.Graph, output.ValidationContext, request.Tool, bindings, outputPath, staging,
                    request.ProcessTimeout, output.VideoGeometry, output.Graph.Fonts.Count == 0 ? [] : [localFont],
                    outputEncoder, new FfmpegGeneratedScriptTarget(scriptPath),
                    graphics, output.FrameExtraction, music), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(compilation.OutputPath, outputPath, StringComparison.Ordinal)
                    || !string.Equals(compilation.GeneratedScriptPath, scriptPath, StringComparison.Ordinal))
                {
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
                }

                ProcessRunResult process = await _run(compilation.ProcessRequest, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || process.Truncated)
                {
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ProcessFailed);
                }

                (ContentDigest digest, long size) = await MeasureAsync(outputPath, cancellationToken).ConfigureAwait(false);
                StandardLoudnessEvidence? loudness = image ? null
                    : StandardLoudnessReport.Read(process, outputEncoder!.Loudness!);
                if (loudness?.Silent == true)
                    warnings.Add(new StandardGenerationWarning(output.Role, "SilentAudioLoudnessNotMeasurable"));
                var execution = new StandardRenderExecutionEvidence(outputEncoder,
                    compilation.FilterScriptDigest, Encoding.UTF8.GetByteCount(compilation.FilterScript), "compiler-created-file",
                    Array.AsReadOnly(output.GraphicFiles.Select(static file => file.Evidence).ToArray()), loudness, output.ThumbnailEvidence,
                    sourceColor, renderMusic ? output.MusicEvidence : []);
                pending.Add(new PendingArtifact(id, outputPath, $"{runName}/{output.Role}/{id}{extension}", output.Role,
                    image ? "image/png" : "video/mp4", image ? "thumbnail" : "video", digest, size, output, execution));
            }

            if (clean.DisplayCues.Count > 0)
            {
                foreach ((string extension, SubtitleExportFile file) in new[]
                {
                    ("srt", SubRipSubtitleWriter.Write(clean.DisplayCues, cancellationToken)),
                    ("vtt", WebVttSubtitleWriter.Write(clean.DisplayCues, cancellationToken)),
                })
                {
                    EntityId<ArtifactIdTag> id = EntityId<ArtifactIdTag>.New();
                    string path = Path.Combine(staging, id + "." + extension);
                    ownedFiles.Add(path);
                    await WriteNewAsync(path, file.GetUtf8Bytes(), cancellationToken).ConfigureAwait(false);
                    (ContentDigest digest, long size) = await MeasureAsync(path, cancellationToken).ConfigureAwait(false);
                    pending.Add(new PendingArtifact(id, path, $"{runName}/landscape-{extension}/{id}.{extension}",
                        "landscape-" + extension, extension == "srt" ? "application/x-subrip" : "text/vtt",
                        "subtitle", digest, size, clean));
                }
            }

            // All processes have succeeded before the first non-transactional batch promotion.
            foreach (PendingArtifact item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                promotionStarted = true;
                // Record BEFORE calling: rollback can fail after File.Move and before INSERT.
                residual.Add(new StandardPromotionAttempt(item.Id, request.ProjectId, item.Path,
                    $"projects/{request.ProjectId}/renders/{item.RelativePath}", item.Digest, item.Size));
                StoredArtifact artifact = await _promote(new ArtifactPromotionRequest(item.Id,
                    request.ProjectId, ArtifactKind.Render, item.Path, item.RelativePath, item.Digest, item.Size,
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                StoredArtifact validated = await _store.ValidateRegisteredAsync(item.Id, cancellationToken).ConfigureAwait(false);
                if (artifact.Id != item.Id || artifact.ContentDigest != item.Digest || artifact.ByteSize != item.Size
                    || validated.ContentDigest != item.Digest || validated.ByteSize != item.Size
                    || validated.StorageReference != $"projects/{request.ProjectId}/renders/{item.RelativePath}")
                {
                    throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.PromotionFailed);
                }

                var evidence = new StandardArtifactBindingEvidence(item.Role, item.Id, request.Plan.Id, request.Plan.PlanDigest,
                    item.Output.Graph.CompositionVersionId, item.Output.Graph.CompositionVersionDigest,
                    request.Settings.ContentDigest, request.Original.Artifact.Id,
                    ContentDigest.Compute(Encoding.UTF8.GetBytes(request.SourcePtsIndex.CanonicalJson)),
                    item.Output.Graph.Id, item.Output.GraphDigest,
                    StandardRenderGraphFactory.Digest(item.Output.VideoGeometry),
                    StandardRenderGraphFactory.Digest(item.Output.DisplayCues), item.Output.SubtitleConfigurationDigest,
                    item.Digest, item.Size, item.Execution);
                RenderGraphValidationContext context = item.Output.ValidationContext;
                var manifest = new RenderManifest(EntityId<RenderManifestIdTag>.New(), context.CompositionVersion,
                    context.TimelineMap, context.TimelineMapDigest, context.Profile,
                    StandardRenderGraphFactory.Digest(evidence), request.ApplicationVersion, request.ApplicationDigest,
                    item.Output.Graph.Inputs.Select(input => new RenderInputEvidence(
                        ManifestInputName(item, request.Plan, input), input.ContentDigest)).ToArray(),
                    [request.ToolEvidence], [request.ModelEvidence], item.Output.Graph.Fonts,
                    [new ManifestArtifact(item.ArtifactType, item.MediaType, null, item.Size, item.Digest, validated.StorageReference)]);
                OutputQcRequest? qc = item.ArtifactType == "video" ? new OutputQcRequest(item.Id, manifest,
                    item.Output.Graph, context, item.Output.SubtitleEvidence, request.ProcessTimeout) : null;
                promoted.Add(new StandardGeneratedArtifact(validated, item.Output.Graph, manifest, evidence,
                    context, item.Output.DisplayCues, item.Output.SubtitleFiles, item.Output.SubtitleEvidence, qc,
                    item.Output.GraphicFiles)
                {
                    SubtitleProjection = item.Output.SubtitleProjection,
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            disposition = StandardArtifactGenerationDisposition.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            disposition = promotionStarted ? StandardArtifactGenerationDisposition.ReconciliationRequired
                : StandardArtifactGenerationDisposition.Cancelled;
        }
        catch (Exception exception)
        {
            failure = exception is StandardGenerationException standard ? standard.Reason
                : promotionStarted ? StandardGenerationFailureReason.PromotionFailed : StandardGenerationFailureReason.ProcessFailed;
            compilerFailure = (exception as FfmpegCompilerException)?.Reason;
            disposition = promotionStarted ? StandardArtifactGenerationDisposition.ReconciliationRequired
                : StandardArtifactGenerationDisposition.Failed;
        }
        finally
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                held[i].Dispose();
            }

            foreach (string path in ownedFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        WorkspacePathSafety.ValidateExistingPathChain(_workspace, path, targetIsDirectory: false);
                        File.Delete(path);
                    }
                }
                catch (Exception)
                {
                    cleanupRequired = true;
                }
            }

            foreach (string directory in stagingOwned ? new[] { Path.Combine(staging, "fonts"), staging } : [])
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(directory);
                        Directory.Delete(directory, recursive: false);
                    }
                }
                catch (Exception)
                {
                    cleanupRequired = true;
                }
            }
        }

        bool generated = disposition is StandardArtifactGenerationDisposition.Completed
            or StandardArtifactGenerationDisposition.GeneratedWithOmissions;
        return new StandardArtifactGenerationResult(disposition,
            generated ? promoted : [], omissions,
            generated ? [] : residual,
            failure, compilerFailure, cleanupRequired, warnings);
    }

    private void ValidateCheckpoint(StandardCompositionPreparation expected)
    {
        try
        {
            StandardCompositionPreparation? stored = _versions.GetStandardComposition(expected.Child.Id);
            if (stored is null
                || CompositionSnapshotJson.WriteVersion(stored.Child) != CompositionSnapshotJson.WriteVersion(expected.Child)
                || StandardRenderGraphFactory.Digest(stored.Plan) != StandardRenderGraphFactory.Digest(expected.Plan)
                || stored.ApplicationDigest != expected.ApplicationDigest
                || stored.ExpectedWorkingCopyRevision != expected.ExpectedWorkingCopyRevision
                || CompositionSnapshotJson.WriteWorkingCopy(stored.PreparedWorkingCopy)
                    != CompositionSnapshotJson.WriteWorkingCopy(expected.PreparedWorkingCopy)
                || StandardRenderGraphFactory.Digest(stored.ParentTimelineMap) != StandardRenderGraphFactory.Digest(expected.ParentTimelineMap)
                || StandardRenderGraphFactory.Digest(stored.ChildTimelineMap) != StandardRenderGraphFactory.Digest(expected.ChildTimelineMap))
            {
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.PreparationNotCheckpointed);
            }
        }
        catch (Exception exception) when (exception is not StandardGenerationException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.PreparationNotCheckpointed);
        }
    }

    private static string ManifestInputName(PendingArtifact artifact, StandardCompositionPlan plan, RenderGraphInput input)
    {
        string name = $"{artifact.Role}/output:{artifact.Id}/plan:{plan.Id}/graph:{artifact.Output.Graph.Id}/input:{input.Id}";
        StandardGraphicRenderFile? graphic = artifact.Output.GraphicFiles.SingleOrDefault(item => item.Input.Id == input.Id);
        if (graphic is null) return name;
        // Human text stays in the hashed ASS/binding payload, never a filename or
        // filter expression. These closed names expose provenance in the manifest.
        StandardTextOverlayEvidence text = graphic.Evidence;
        return name + $"/text-role:{text.Role}/provenance:{text.Provenance}/element:{text.ElementId}"
            + $"/text-evidence:{StandardRenderGraphFactory.Digest(text).Hex}/target:{graphic.Binding.TargetVisualNodeId}";
    }

    private void CreateStagingDirectory(string path)
    {
        string relative = Path.GetRelativePath(_workspace, path);
        string current = _workspace;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(current);
            current = Path.Combine(current, segment);
            if (current == path && WorkspacePathSafety.PathEntryExistsConservatively(current))
            {
                throw new IOException("The generation staging directory already exists.");
            }

            Directory.CreateDirectory(current);
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(current);
        }
    }

    private static FileStream OpenHeld(string path)
    {
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(Path.GetDirectoryName(path)!);
        WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(path));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal static async Task<(ContentDigest Digest, long ByteSize)> MeasureAsync(string path, CancellationToken token)
    {
        using FileStream stream = OpenHeld(path);
        long size = stream.Length;
        if (size <= 0)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ProcessFailed);
        }

        ContentDigest digest = ContentDigest.FromBytes(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (stream.Length != size)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ProcessFailed);
        }

        return (digest, size);
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed record PendingArtifact(EntityId<ArtifactIdTag> Id, string Path, string RelativePath,
        string Role, string MediaType, string ArtifactType, ContentDigest Digest, long Size, StandardRenderGraphOutput Output,
        StandardRenderExecutionEvidence? Execution = null);
}