using System.Text.Json.Serialization;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Compositions;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Projects;

public enum SourceOnlyResultRole { LandscapeClean, LandscapeBurnIn, VerticalBurnIn, SubRip, WebVtt, LandscapeThumbnail, VerticalThumbnail, VerticalClean }

internal static class SourceOnlyResultRoleExtensions
{
    /// <summary>FR-132: a short (vertical) video, with or without burned-in subtitles. Carries a short-form rendition id.</summary>
    public static bool IsShortVideo(this SourceOnlyResultRole role) => role is SourceOnlyResultRole.VerticalBurnIn or SourceOnlyResultRole.VerticalClean;
}
public enum SourceOnlyResultEvidenceKind { Graph, Manifest, RenderingInputs, QualityControl, DeliveryReceipt, Transcript, UsedSourceRanges }
public enum SourceOnlyResultQcApplicability { VideoOutputQc, SubtitleFileOnly, ImageFileOnly }
public enum SourceOnlyResultFailureCode
{
    GenerationFailed, GenerationCanceled, GenerationReconciliationRequired, NoArtifacts,
    PrimaryArtifactMissing, BindingMismatch, SettingsBindingMismatch, VersionNotPersisted,
    ArtifactValidationFailed, QualityControlFailed, Canceled, EvidencePersistenceFailed,
    EvaluationPersistenceFailed, WorkflowConflict, StoredEvidenceInvalid, DeliveryNotValidated,
    EvidencePromotionReconciliationRequired,
}
public enum SourceOnlyResultOmissionRole
{
    ShortForm, Thumbnail, LandscapeSubtitles, VerticalSubtitles, VerticalCallToAction,
    PreRollMargin, Opening, ProgramName, EpisodeNumber, EpisodeTitle, PerformerIntroduction,
    PublicationScopeIndicator, Body, Topic, Summary, CallToAction, Ending, PostRollMargin, Other,
    Transcript, UsedSourceRanges,
}
public enum SourceOnlyResultOmissionReason
{
    NoGroundedCandidate, NoAdditionalGroundedCandidate, NoMeasuredSourceFrame,
    NoAdditionalMeasuredSourceFrame, NoDisplayCues, UntimedPlanMetadataNotRendered,
    ThumbnailRendererNotConnected, CallToActionRendererNotConnected, UnrecognizedOmission,
    AncillaryExportFailed,
}

/// <summary>The six fields required by SourceOnlyExecutionStore.ResultJson.</summary>
public sealed record SourceOnlyResultBinding(
    EntityId<ProjectIdTag> ProjectId, EntityId<SourceOnlyExecutionIdTag> ExecutionId,
    EntityId<JobIdTag> RootJobId, ContentDigest SettingsSnapshotDigest,
    EntityId<CompositionVersionIdTag> VersionId, ContentDigest VersionDigest);

/// <summary>No file path or storage reference crosses this boundary.</summary>
public sealed record SourceOnlyResultArtifactReference(
    EntityId<ArtifactIdTag> ArtifactId, ContentDigest ContentDigest, long ByteSize, ArtifactKind Kind);

public sealed record SourceOnlyResultEvidenceReference(
    SourceOnlyResultEvidenceKind Kind, SourceOnlyResultArtifactReference Artifact);

public sealed record SourceOnlyResultTranscriptReference(
    EntityId<TranscriptRevisionIdTag> RevisionId, ContentDigest RevisionDigest,
    EntityId<ArtifactIdTag> SourceArtifactId);

public sealed record SourceOnlyResultOmission(
    SourceOnlyResultOmissionRole Role, SourceOnlyResultOmissionReason Reason,
    EntityId<StandardShortFormPlanIdTag>? RenditionId = null);

public sealed record SourceOnlyResultFailure(
    SourceOnlyResultFailureCode Code, EntityId<ArtifactIdTag>? ArtifactId = null,
    SourceOnlyResultArtifactReference? PromotionArtifact = null, SourceOnlyResultEvidenceKind? EvidenceKind = null);

/// <summary>An attempted move/registration, never proof that a registered row exists. Native only.</summary>
public sealed record SourceOnlyResultPromotionAttempt(
    SourceOnlyResultArtifactReference Artifact, EntityId<ProjectIdTag> ProjectId,
    string StagingPath, string StorageReference, SourceOnlyResultEvidenceKind? EvidenceKind = null);

public sealed record SourceOnlyResultGenerationWarning(string Role, string Code)
{
    /// <summary>
    /// The (role, code) pairs of the generator's warnings, each once. The generator keeps one warning per affected cue (a 10-minute video has
    /// thousands, FR-145 AC-189), while the result records which warning kinds occurred; repeating them would exceed
    /// <see cref="SourceOnlyResult.MaximumGenerationWarningCount"/> and turn a finished or failed generation into a malformed result.
    /// </summary>
    internal static SourceOnlyResultGenerationWarning[] FromGenerated(IEnumerable<StandardGenerationWarning> warnings) =>
        [.. warnings.Select(static item => new SourceOnlyResultGenerationWarning(item.Role, item.Code)).Distinct()];
}

/// <summary>Exact planned original-frame selection, not a claim of visual quality.</summary>
public sealed record SourceOnlyResultFrameEvidence(
    EntityId<AssetIdTag> SourceAssetId, EntityId<SourceSpanIdTag> SourceSpanId,
    ContentDigest SourceDigest, ContentDigest SourcePtsIndexDigest, int SourceFrameSequence,
    RationalTime SourcePresentationTime, RationalTime SourceFrameDuration,
    int SourceWidth, int SourceHeight, int Width, int Height, string ExtractionRule);

/// <summary>Persisted delivery bytes AND a bound receipt are necessary; a Boolean is not proof.</summary>
public sealed record SourceOnlyResultDelivery(
    SourceOnlyResultArtifactReference Artifact, SourceOnlyResultEvidenceReference Receipt);

public sealed record SourceOnlyResultWarning(
    string Id, AutomatedEvaluationRuleId RuleId, AutomatedEvaluationStatus Status,
    string ReasonCode, AutomatedEvaluationTarget Target);

public sealed record SourceOnlyResultWorkflowReference(
    EntityId<ArtifactIdTag> ArtifactId, int Revision, ContentDigest CanonicalDigest,
    QuickStartWorkflowState State, EntityId<AutomatedEvaluationIdTag> EvaluationId);

public sealed record SourceOnlyResultMeasuredValue(OutputQcMeasuredValueKind Kind, decimal? Value);

/// <summary>Measurements of the supplied display text/layout, not OCR or semantic proof.</summary>
public sealed record SourceOnlyResultSubtitleMeasurement(
    EntityId<HubRadio.Core.Layout.CueIdTag> CueId, RationalTime Start, RationalTime Duration,
    int CharacterCount, int MaximumLineCharacters, int LineCount, bool SafeZoneSatisfied);

/// <summary>
/// Bounded QC summary. Raw timing/luma capture digests identify the actual OutputQc
/// measurements; retained cue statistics describe renderer inputs, not visual OCR.
/// </summary>
public sealed class SourceOnlyResultMeasurements
{
    internal SourceOnlyResultMeasurements(
        RationalTime expectedDuration, RationalTime videoStart, RationalTime videoEnd,
        RationalTime? audioStart, RationalTime? audioEnd, int videoFrameCount, int? audioFrameCount,
        ContentDigest probeDigest, ContentDigest videoTimingDigest, ContentDigest? audioTimingDigest,
        ContentDigest? subtitleTimingDigest, ContentDigest lumaDigest, ContentDigest? audioDigest,
        decimal minimumLuma, decimal maximumLuma, decimal meanLuma,
        SourceOnlyResultMeasuredValue? loudness, SourceOnlyResultMeasuredValue? truePeak,
        bool decodedSuccessfully, IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues,
        RationalTime videoTimeBase = default, RationalTime? audioTimeBase = null,
        bool videoTimingContinuous = false, bool videoFrameCadenceExact = false)
    {
        ExpectedDuration = expectedDuration; VideoStart = videoStart; VideoEnd = videoEnd;
        AudioStart = audioStart; AudioEnd = audioEnd; VideoFrameCount = videoFrameCount;
        AudioFrameCount = audioFrameCount; ProbeDigest = probeDigest; VideoTimingDigest = videoTimingDigest;
        AudioTimingDigest = audioTimingDigest; SubtitleTimingDigest = subtitleTimingDigest;
        LumaDigest = lumaDigest; AudioDigest = audioDigest; MinimumLuma = minimumLuma;
        MaximumLuma = maximumLuma; MeanLuma = meanLuma; Loudness = loudness; TruePeak = truePeak;
        DecodedSuccessfully = decodedSuccessfully;
        Cues = Array.AsReadOnly(cues.ToArray());
        VideoTimeBase = videoTimeBase; AudioTimeBase = audioTimeBase;
        VideoTimingContinuous = videoTimingContinuous; VideoFrameCadenceExact = videoFrameCadenceExact;
    }

    public RationalTime ExpectedDuration { get; }
    public RationalTime VideoStart { get; }
    public RationalTime VideoEnd { get; }
    public RationalTime? AudioStart { get; }
    public RationalTime? AudioEnd { get; }
    public RationalTime VideoDuration => VideoEnd - VideoStart;
    public RationalTime? AudioDuration => AudioEnd - AudioStart;
    public RationalTime? AudioVideoStartOffset => AudioStart - VideoStart;
    public RationalTime? AudioVideoEndOffset => AudioEnd - VideoEnd;
    public int VideoFrameCount { get; }
    public int? AudioFrameCount { get; }
    public ContentDigest ProbeDigest { get; }
    public ContentDigest VideoTimingDigest { get; }
    public ContentDigest? AudioTimingDigest { get; }
    public ContentDigest? SubtitleTimingDigest { get; }
    public ContentDigest LumaDigest { get; }
    public ContentDigest? AudioDigest { get; }
    public decimal MinimumLuma { get; }
    public decimal MaximumLuma { get; }
    public decimal MeanLuma { get; }
    public SourceOnlyResultMeasuredValue? Loudness { get; }
    public SourceOnlyResultMeasuredValue? TruePeak { get; }
    public bool DecodedSuccessfully { get; }
    public IReadOnlyList<SourceOnlyResultSubtitleMeasurement> Cues { get; }
    public RationalTime VideoTimeBase { get; }
    public RationalTime? AudioTimeBase { get; }
    public bool VideoTimingContinuous { get; }
    public bool VideoFrameCadenceExact { get; }
    public bool OutputDigitalSilence => AudioDigest is not null &&
        Loudness?.Kind == OutputQcMeasuredValueKind.NegativeInfinity &&
        TruePeak?.Kind == OutputQcMeasuredValueKind.NegativeInfinity;
}

public sealed class SourceOnlyResultArtifact
{
    internal SourceOnlyResultArtifact(SourceOnlyResultRole role,
        EntityId<StandardShortFormPlanIdTag>? renditionId, SourceOnlyResultArtifactReference artifact,
        EntityId<CompositionVersionIdTag> versionId, ContentDigest versionDigest,
        SourceOnlyResultArtifactReference source, ContentDigest graphDigest, ContentDigest? manifestDigest,
        IReadOnlyList<SourceOnlyResultEvidenceReference> evidence, AutomatedEvaluationRun? evaluation,
        SourceOnlyResultMeasurements? measurements, SourceOnlyResultFailureCode? failureCode,
        OutputQcFailureReason? qcFailureReason = null, OutputQcStage? qcFailureStage = null,
        SourceOnlyResultDelivery? delivery = null,
        EntityId<StandardThumbnailPlanIdTag>? thumbnailPlanId = null,
        SourceOnlyResultFrameEvidence? frameEvidence = null,
        EntityId<RenderProfileIdTag>? profileId = null, ContentDigest? profileDigest = null)
    {
        Role = role; RenditionId = renditionId; Artifact = artifact; VersionId = versionId;
        VersionDigest = versionDigest; Source = source; GraphDigest = graphDigest; ManifestDigest = manifestDigest;
        Evidence = Array.AsReadOnly(evidence.ToArray()); Evaluation = evaluation; Measurements = measurements;
        FailureCode = failureCode; QcFailureReason = qcFailureReason; QcFailureStage = qcFailureStage; Delivery = delivery;
        ThumbnailPlanId = thumbnailPlanId; FrameEvidence = frameEvidence; ProfileId = profileId; ProfileDigest = profileDigest;
        Warnings = Array.AsReadOnly((evaluation?.Rules ?? []).Where(rule =>
            (rule.Status is AutomatedEvaluationStatus.Warning or AutomatedEvaluationStatus.Fail) && rule.Target is not null)
            .Select(rule => new SourceOnlyResultWarning($"{artifact.ArtifactId}:{AutomatedEvaluationRules.ToWireName(rule.RuleId)}",
                rule.RuleId, rule.Status, rule.ReasonCode, rule.Target!)).ToArray());
    }

    public SourceOnlyResultRole Role { get; }
    public EntityId<StandardShortFormPlanIdTag>? RenditionId { get; }
    public EntityId<StandardThumbnailPlanIdTag>? ThumbnailPlanId { get; }
    public SourceOnlyResultFrameEvidence? FrameEvidence { get; }
    public EntityId<RenderProfileIdTag>? ProfileId { get; }
    public ContentDigest? ProfileDigest { get; }
    public SourceOnlyResultArtifactReference Artifact { get; }
    public EntityId<ArtifactIdTag> ArtifactId => Artifact.ArtifactId;
    public ContentDigest ArtifactDigest => Artifact.ContentDigest;
    public long ByteSize => Artifact.ByteSize;
    public string MediaType => Role switch
    {
        SourceOnlyResultRole.SubRip => "application/x-subrip",
        SourceOnlyResultRole.WebVtt => "text/vtt",
        SourceOnlyResultRole.LandscapeThumbnail or SourceOnlyResultRole.VerticalThumbnail => "image/png",
        SourceOnlyResultRole.LandscapeClean or SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn
            or SourceOnlyResultRole.VerticalClean => "video/mp4",
        _ => throw SourceOnlyResultCodec.Invalid(),
    };
    public bool IsVideo => Role is SourceOnlyResultRole.LandscapeClean or SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn
        or SourceOnlyResultRole.VerticalClean;
    public bool IsImage => Role is SourceOnlyResultRole.LandscapeThumbnail or SourceOnlyResultRole.VerticalThumbnail;
    public SourceOnlyResultQcApplicability QcApplicability => IsVideo ? SourceOnlyResultQcApplicability.VideoOutputQc :
        IsImage ? SourceOnlyResultQcApplicability.ImageFileOnly : SourceOnlyResultQcApplicability.SubtitleFileOnly;
    public EntityId<CompositionVersionIdTag> VersionId { get; }
    public ContentDigest VersionDigest { get; }
    public SourceOnlyResultArtifactReference Source { get; }
    public ContentDigest GraphDigest { get; }
    public ContentDigest? ManifestDigest { get; }
    public IReadOnlyList<SourceOnlyResultEvidenceReference> Evidence { get; }
    public AutomatedEvaluationRun? Evaluation { get; }
    public SourceOnlyResultMeasurements? Measurements { get; }
    public SourceOnlyResultFailureCode? FailureCode { get; }
    public OutputQcFailureReason? QcFailureReason { get; }
    public OutputQcStage? QcFailureStage { get; }
    public IReadOnlyList<SourceOnlyResultWarning> Warnings { get; }
    public SourceOnlyResultDelivery? Delivery { get; }
    public bool Delivered => Delivery is not null;
    // Eligibility is NOT authorization. A subsequent delivery service must verify the entire set.
    public bool HasBlockingFailure => FailureCode is not null || Evaluation is null ||
        (IsVideo ? QuickStartEvaluationPolicy.DetermineCompletedState(Evaluation) == QuickStartWorkflowState.Failed :
            Evaluation.Rules.Any(r => r.Status == AutomatedEvaluationStatus.Fail) ||
            new[] { AutomatedEvaluationRuleId.Provenance, AutomatedEvaluationRuleId.SettingsBinding, AutomatedEvaluationRuleId.Codec }
                .Any(id => Evaluation.Rules.Single(r => r.RuleId == id).Status != AutomatedEvaluationStatus.Pass));
}

/// <summary>
/// Immutable native result. Use CanonicalJson for execution persistence and ToWebDto for
/// projection. Parsing verifies structure/integrity, not the continued existence of files;
/// use SourceOnlyResultService.LoadAsync/ValidateAsync after restart and before delivery.
/// </summary>
public sealed class SourceOnlyResult
{
    public const int MaximumCanonicalJsonUtf8Bytes = SourceOnlyExecutionStore.MaximumResultUtf8Bytes;
    public const int MaximumArtifactCount = 16;
    public const int MaximumOmissionCount = 128;
    public const int MaximumFailureCount = 64;
    public const int MaximumTranscriptCount = 8;
    public const int MaximumAncillaryArtifactCount = 2;
    public const int MaximumGenerationWarningCount = 256;
    public const int MaximumReconciliationCount = MaximumArtifactCount * 5;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;

    internal SourceOnlyResult(SourceOnlyResultBinding binding, StandardArtifactGenerationDisposition generationDisposition,
        IReadOnlyList<SourceOnlyResultArtifact> artifacts, IReadOnlyList<SourceOnlyResultOmission> omissions,
        IReadOnlyList<SourceOnlyResultFailure> failures, IReadOnlyList<SourceOnlyResultArtifactReference> reconciliationArtifacts,
        IReadOnlyList<SourceOnlyResultTranscriptReference> transcripts, SourceOnlyResultWorkflowReference? primaryWorkflow,
        bool stagingCleanupRequired, IReadOnlyList<SourceOnlyResultGenerationWarning>? generationWarnings = null,
        IReadOnlyList<SourceOnlyResultPromotionAttempt>? nativeReconciliationAttempts = null,
        IReadOnlyList<SourceOnlyResultEvidenceReference>? ancillaryArtifacts = null)
    {
        Binding = binding; GenerationDisposition = generationDisposition;
        Artifacts = Array.AsReadOnly(artifacts.ToArray()); Omissions = Array.AsReadOnly(omissions.ToArray());
        Failures = Array.AsReadOnly(failures.ToArray()); ReconciliationArtifacts = Array.AsReadOnly(reconciliationArtifacts.ToArray());
        TranscriptReferences = Array.AsReadOnly(transcripts.ToArray()); PrimaryWorkflow = primaryWorkflow;
        AncillaryArtifacts = Array.AsReadOnly((ancillaryArtifacts ?? []).ToArray());
        StagingCleanupRequired = stagingCleanupRequired;
        GenerationWarnings = Array.AsReadOnly((generationWarnings ?? []).ToArray());
        NativeReconciliationAttempts = Array.AsReadOnly((nativeReconciliationAttempts ?? []).ToArray());
        SourceOnlyResultCodec.ValidateShape(this);
        CanonicalJson = SourceOnlyResultCodec.Serialize(this);
        CanonicalDigest = ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(CanonicalJson));
    }

    public SourceOnlyResultBinding Binding { get; }
    public EntityId<CompositionVersionIdTag> VersionId => Binding.VersionId;
    public ContentDigest VersionDigest => Binding.VersionDigest;
    public StandardArtifactGenerationDisposition GenerationDisposition { get; }
    public IReadOnlyList<SourceOnlyResultArtifact> Artifacts { get; }
    public IReadOnlyList<SourceOnlyResultOmission> Omissions { get; }
    public IReadOnlyList<SourceOnlyResultFailure> Failures { get; }
    public IReadOnlyList<SourceOnlyResultArtifactReference> ReconciliationArtifacts { get; }
    public IReadOnlyList<SourceOnlyResultTranscriptReference> TranscriptReferences { get; }

    /// <summary>
    /// Delivered ancillary artifacts (transcript, used-source-range list). An
    /// entry exists only when that artifact was actually written and promoted.
    /// The pipeline records a named omission for every kind it could not write;
    /// this type does not by itself enforce that pairing, because an evaluated
    /// result exists before the ancillary step runs.
    /// </summary>
    public IReadOnlyList<SourceOnlyResultEvidenceReference> AncillaryArtifacts { get; }
    public SourceOnlyResultWorkflowReference? PrimaryWorkflow { get; }
    public bool StagingCleanupRequired { get; }
    public IReadOnlyList<SourceOnlyResultGenerationWarning> GenerationWarnings { get; }
    [JsonIgnore] public IReadOnlyList<SourceOnlyResultPromotionAttempt> NativeReconciliationAttempts { get; }
    public bool ReconciliationRequired => ReconciliationArtifacts.Count != 0 ||
        GenerationDisposition == StandardArtifactGenerationDisposition.ReconciliationRequired;
    public bool HasBlockingFailures => Failures.Count != 0 || Artifacts.Count == 0 ||
        !Artifacts.Any(item => item.Role == SourceOnlyResultRole.LandscapeClean) ||
        Artifacts.Any(item => item.HasBlockingFailure) || ReconciliationArtifacts.Count != 0 ||
        GenerationDisposition is not (StandardArtifactGenerationDisposition.Completed or StandardArtifactGenerationDisposition.GeneratedWithOmissions);
    public bool HasWarnings => Omissions.Count != 0 || GenerationWarnings.Count != 0 || StagingCleanupRequired || Artifacts.Any(item =>
        item.Evaluation?.StatusCounts.NotEvaluated > 0 || item.Evaluation?.StatusCounts.Warning > 0);
    public bool RequiresSetDeliveryValidation => true;
    [JsonIgnore] public string CanonicalJson { get; }
    public ContentDigest CanonicalDigest { get; }
    public SourceOnlyResultDto ToWebDto() => SourceOnlyResultCodec.ToWebDto(this);
    public static SourceOnlyResult ParseCanonical(string canonicalJson, ContentDigest? expectedDigest = null) =>
        SourceOnlyResultCodec.ParseCanonical(canonicalJson, expectedDigest);

    public void ValidateBinding(StoredSourceOnlyExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        SourceOnlyExecutionRequest request = execution.Request;
        if (Binding.ProjectId != request.ProjectId || Binding.ExecutionId != request.ExecutionId ||
            Binding.RootJobId != request.RootJobId || Binding.SettingsSnapshotDigest != request.SettingsSnapshot.ContentDigest ||
            execution.VersionId is not null && (Binding.VersionId != execution.VersionId || Binding.VersionDigest != execution.VersionDigest) ||
            execution.ResultJson is not null && (execution.ResultJson != CanonicalJson || execution.ResultDigest != CanonicalDigest) &&
                !SourceOnlyDeliveryReceipt.IsResultExtension(this, execution.ResultJson, execution.ResultDigest))
        { throw new SourceOnlyResultException(SourceOnlyResultFailureCode.BindingMismatch); }
    }
}

public sealed class SourceOnlyResultException : Exception
{
    public SourceOnlyResultException(SourceOnlyResultFailureCode code)
        : base("Source-only result validation failed.") => Code = code;
    public SourceOnlyResultFailureCode Code { get; }
}

public sealed class SourceOnlyResultValidation
{
    internal SourceOnlyResultValidation(IEnumerable<SourceOnlyResultFailure> failures) =>
        Failures = Array.AsReadOnly(failures.Distinct().ToArray());
    public bool IsValid => Failures.Count == 0;
    public IReadOnlyList<SourceOnlyResultFailure> Failures { get; }
}