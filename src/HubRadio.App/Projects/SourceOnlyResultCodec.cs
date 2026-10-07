using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Projects;

// Explicit constructor-based DTOs. Never deserialize RenderGraph/RenderManifest/OutputQcResult
// (their get-only properties are not a persistence contract). All strings here are closed
// enum names, fixed warning reasons, IDs, digests or media types; evaluation has its own strict codec.
public sealed record SourceOnlyResultBindingDto(string ProjectId, string ExecutionId, string RootJobId,
    string SettingsSnapshotDigest, string VersionId, string VersionDigest);
public sealed record SourceOnlyResultReferenceDto(string ArtifactId, string ContentDigest, long ByteSize, string Kind);
public sealed record SourceOnlyResultEvidenceDto(string Kind, SourceOnlyResultReferenceDto Artifact);
public sealed record SourceOnlyResultTimeDto(long Value, long Rate);
public sealed record SourceOnlyResultValueDto(string Kind, decimal? Value);
public sealed record SourceOnlyResultFrameDto(string SourceAssetId, string SourceSpanId, string SourceDigest,
    string SourcePtsIndexDigest, int SourceFrameSequence, SourceOnlyResultTimeDto SourcePresentationTime,
    SourceOnlyResultTimeDto SourceFrameDuration, int SourceWidth, int SourceHeight, int Width, int Height, string ExtractionRule);
internal sealed record SourceOnlyResultNativePromotionDto(SourceOnlyResultReferenceDto Artifact, string ProjectId,
    string StagingPath, string StorageReference, string? EvidenceKind);
public sealed record SourceOnlyResultCueDto(string CueId, SourceOnlyResultTimeDto Start, SourceOnlyResultTimeDto Duration,
    int CharacterCount, int MaximumLineCharacters, int LineCount, bool SafeZoneSatisfied);
public sealed record SourceOnlyResultMeasurementsDto(SourceOnlyResultTimeDto ExpectedDuration,
    SourceOnlyResultTimeDto VideoStart, SourceOnlyResultTimeDto VideoEnd,
    SourceOnlyResultTimeDto? AudioStart, SourceOnlyResultTimeDto? AudioEnd, int VideoFrameCount, int? AudioFrameCount,
    string ProbeDigest, string VideoTimingDigest, string? AudioTimingDigest, string? SubtitleTimingDigest,
    string LumaDigest, string? AudioDigest, decimal MinimumLuma, decimal MaximumLuma, decimal MeanLuma,
    SourceOnlyResultValueDto? Loudness, SourceOnlyResultValueDto? TruePeak, bool DecodedSuccessfully,
    SourceOnlyResultCueDto[] Cues, SourceOnlyResultTimeDto VideoTimeBase, SourceOnlyResultTimeDto? AudioTimeBase,
    bool VideoTimingContinuous, bool VideoFrameCadenceExact, bool OutputDigitalSilence,
    SourceOnlyResultTimeDto? AudioVideoStartOffset, SourceOnlyResultTimeDto? AudioVideoEndOffset);
public sealed record SourceOnlyResultDeliveryDto(SourceOnlyResultReferenceDto Artifact, SourceOnlyResultEvidenceDto Receipt);
public sealed record SourceOnlyResultTargetDto(string Kind, string ProjectId, string VersionId, string VersionDigest,
    string? ArtifactId, string? ArtifactDigest, string? TimeBasis, SourceOnlyResultTimeDto? Time, string? SettingKey, string? EditorKind);
public sealed record SourceOnlyResultWarningDto(string Id, string RuleId, string Status, string ReasonCode, SourceOnlyResultTargetDto Target);
public sealed record SourceOnlyResultArtifactDto(string Role, string? RenditionId, SourceOnlyResultReferenceDto Artifact,
    string MediaType, string VersionId, string VersionDigest, SourceOnlyResultReferenceDto Source,
    string GraphDigest, string? ManifestDigest, SourceOnlyResultEvidenceDto[] Evidence,
    JsonElement? Evaluation, string? EvaluationDigest, SourceOnlyResultMeasurementsDto? Measurements,
    string? FailureCode, string? QcFailureReason, string? QcFailureStage, SourceOnlyResultWarningDto[] Warnings,
    bool Delivered, SourceOnlyResultDeliveryDto? Delivery, string? ThumbnailPlanId,
    SourceOnlyResultFrameDto? FrameEvidence, string QcApplicability, string? ProfileId, string? ProfileDigest);
public sealed record SourceOnlyResultOmissionDto(string Role, string Reason, string? RenditionId);
public sealed record SourceOnlyResultFailureDto(string Code, string? ArtifactId,
    SourceOnlyResultReferenceDto? PromotionArtifact, string? EvidenceKind);
public sealed record SourceOnlyResultGenerationWarningDto(string Role, string Code);
public sealed record SourceOnlyResultTranscriptDto(string RevisionId, string RevisionDigest, string SourceArtifactId);
public sealed record SourceOnlyResultWorkflowDto(string ArtifactId, int Revision, string CanonicalDigest, string State, string EvaluationId);
/// <summary>
/// Canonical persistence omits <c>AncillaryArtifacts</c> entirely when absent so
/// results stored before ancillary delivery keep their exact bytes and digest.
/// </summary>
public sealed record SourceOnlyResultDto(string SchemaVersion, SourceOnlyResultBindingDto Binding,
    string GenerationDisposition, SourceOnlyResultArtifactDto[] Artifacts, SourceOnlyResultOmissionDto[] Omissions,
    SourceOnlyResultFailureDto[] Failures, SourceOnlyResultReferenceDto[] ReconciliationArtifacts,
    SourceOnlyResultTranscriptDto[] TranscriptReferences, SourceOnlyResultWorkflowDto? PrimaryWorkflow,
    bool StagingCleanupRequired, SourceOnlyResultGenerationWarningDto[] GenerationWarnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SourceOnlyResultEvidenceDto[]? AncillaryArtifacts = null);

public static class SourceOnlyResultCodec
{
    public const string SchemaVersion = "source-only.result.v2";
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 48,
        };
        options.Converters.Add(new ExactInt64Converter());
        options.Converters.Add(new ExactInt32Converter());
        options.Converters.Add(new ExactDecimalConverter());
        return options;
    }

    public static string Serialize(SourceOnlyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Persistence has one additional native-only field. ToWebDto never contains
        // recovery locators, and reading it never authorizes access to those paths.
        JsonElement dto = Element(Write(ToDto(result), SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes));
        Dictionary<string, JsonElement> fields = dto.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields.Add("nativeReconciliationAttempts", Element(Write(result.NativeReconciliationAttempts.Select(p =>
            new SourceOnlyResultNativePromotionDto(ReferenceDto(p.Artifact), p.ProjectId.ToString(),
                p.StagingPath, p.StorageReference, p.EvidenceKind?.ToString())).ToArray())));
        return Write(fields, SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes);
    }

    public static SourceOnlyResult ParseCanonical(string canonicalJson, ContentDigest? expectedDigest = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            using JsonDocument document = ParseDocument(canonicalJson, SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes);
            if (expectedDigest is not null && ContentDigest.Compute(Utf8.GetBytes(canonicalJson)) != expectedDigest) throw Invalid();
            JsonElement root = document.RootElement;
            CheckArray(root, "artifacts", SourceOnlyResult.MaximumArtifactCount);
            CheckArray(root, "omissions", SourceOnlyResult.MaximumOmissionCount);
            CheckArray(root, "failures", SourceOnlyResult.MaximumFailureCount);
            CheckArray(root, "reconciliationArtifacts", SourceOnlyResult.MaximumReconciliationCount);
            CheckArray(root, "nativeReconciliationAttempts", SourceOnlyResult.MaximumReconciliationCount);
            CheckArray(root, "generationWarnings", SourceOnlyResult.MaximumGenerationWarningCount);
            CheckArray(root, "transcriptReferences", SourceOnlyResult.MaximumTranscriptCount);
            if (root.TryGetProperty("ancillaryArtifacts", out _))
            {
                CheckArray(root, "ancillaryArtifacts", SourceOnlyResult.MaximumAncillaryArtifactCount);
            }
            foreach (JsonElement artifact in root.GetProperty("artifacts").EnumerateArray())
            {
                CheckArray(artifact, "evidence", 4);
                CheckArray(artifact, "warnings", AutomatedEvaluationRules.Machine.Count);
                if (artifact.TryGetProperty("measurements", out JsonElement measured) && measured.ValueKind != JsonValueKind.Null)
                    CheckArray(measured, "cues", StandardSubtitleInput.MaximumCueCount);
            }
            var projected = root.EnumerateObject().Where(p => p.Name != "nativeReconciliationAttempts")
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            SourceOnlyResultDto dto = JsonSerializer.Deserialize<SourceOnlyResultDto>(Write(projected,
                SourceOnlyResult.MaximumCanonicalJsonUtf8Bytes), Options) ?? throw Invalid();
            SourceOnlyResultNativePromotionDto[] native = JsonSerializer.Deserialize<SourceOnlyResultNativePromotionDto[]>(
                root.GetProperty("nativeReconciliationAttempts"), Options) ?? throw Invalid();
            Require(dto.SchemaVersion == SchemaVersion);
            var result = new SourceOnlyResult(Binding(dto.Binding), EnumValue<StandardArtifactGenerationDisposition>(dto.GenerationDisposition),
                dto.Artifacts.Select(Artifact).ToArray(), dto.Omissions.Select(item => new SourceOnlyResultOmission(
                    EnumValue<SourceOnlyResultOmissionRole>(item.Role), EnumValue<SourceOnlyResultOmissionReason>(item.Reason),
                    OptionalId<StandardShortFormPlanIdTag>(item.RenditionId))).ToArray(),
                dto.Failures.Select(item => new SourceOnlyResultFailure(EnumValue<SourceOnlyResultFailureCode>(item.Code),
                    OptionalId<ArtifactIdTag>(item.ArtifactId), item.PromotionArtifact is null ? null : Reference(item.PromotionArtifact),
                    OptionalEnum<SourceOnlyResultEvidenceKind>(item.EvidenceKind))).ToArray(), dto.ReconciliationArtifacts.Select(Reference).ToArray(),
                dto.TranscriptReferences.Select(item => new SourceOnlyResultTranscriptReference(Id<TranscriptRevisionIdTag>(item.RevisionId),
                    Digest(item.RevisionDigest), Id<ArtifactIdTag>(item.SourceArtifactId))).ToArray(),
                dto.PrimaryWorkflow is null ? null : new SourceOnlyResultWorkflowReference(Id<ArtifactIdTag>(dto.PrimaryWorkflow.ArtifactId),
                    dto.PrimaryWorkflow.Revision, Digest(dto.PrimaryWorkflow.CanonicalDigest), EnumValue<QuickStartWorkflowState>(dto.PrimaryWorkflow.State),
                    Id<AutomatedEvaluationIdTag>(dto.PrimaryWorkflow.EvaluationId)), dto.StagingCleanupRequired,
                dto.GenerationWarnings.Select(w => new SourceOnlyResultGenerationWarning(w.Role, w.Code)).ToArray(),
                native.Select(p => new SourceOnlyResultPromotionAttempt(Reference(p.Artifact), Id<ProjectIdTag>(p.ProjectId),
                    p.StagingPath, p.StorageReference, OptionalEnum<SourceOnlyResultEvidenceKind>(p.EvidenceKind))).ToArray(),
                (dto.AncillaryArtifacts ?? []).Select(Evidence).ToArray());
            Require(result.CanonicalJson == canonicalJson);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or
            OverflowException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            // Do not leak raw property names, values, paths, or nested parser diagnostics.
            throw Invalid();
        }
    }

    internal static SourceOnlyResultDto ToWebDto(SourceOnlyResult result) => ToDto(result) with
    {
        // Persistence omits the key entirely for results stored before ancillary
        // delivery; the closed web schema always carries it, empty when absent.
        AncillaryArtifacts = result.AncillaryArtifacts.Select(EvidenceDto).ToArray(),
    };

    internal static SourceOnlyResultDto ToDto(SourceOnlyResult result) => new(SchemaVersion, BindingDto(result.Binding),
        result.GenerationDisposition.ToString(), result.Artifacts.Select(ArtifactDto).ToArray(),
        result.Omissions.Select(item => new SourceOnlyResultOmissionDto(item.Role.ToString(), item.Reason.ToString(), item.RenditionId?.ToString())).ToArray(),
        result.Failures.Select(item => new SourceOnlyResultFailureDto(item.Code.ToString(), item.ArtifactId?.ToString(),
            item.PromotionArtifact is null ? null : ReferenceDto(item.PromotionArtifact), item.EvidenceKind?.ToString())).ToArray(),
        result.ReconciliationArtifacts.Select(ReferenceDto).ToArray(), result.TranscriptReferences.Select(item =>
            new SourceOnlyResultTranscriptDto(item.RevisionId.ToString(), item.RevisionDigest.Hex, item.SourceArtifactId.ToString())).ToArray(),
        result.PrimaryWorkflow is null ? null : new SourceOnlyResultWorkflowDto(result.PrimaryWorkflow.ArtifactId.ToString(),
            result.PrimaryWorkflow.Revision, result.PrimaryWorkflow.CanonicalDigest.Hex, result.PrimaryWorkflow.State.ToString(),
            result.PrimaryWorkflow.EvaluationId.ToString()), result.StagingCleanupRequired,
        result.GenerationWarnings.Select(w => new SourceOnlyResultGenerationWarningDto(w.Role, w.Code)).ToArray(),
        result.AncillaryArtifacts.Count == 0 ? null : result.AncillaryArtifacts.Select(EvidenceDto).ToArray());

    internal static SourceOnlyResultBindingDto BindingDto(SourceOnlyResultBinding b) =>
        new(b.ProjectId.ToString(), b.ExecutionId.ToString(), b.RootJobId.ToString(), b.SettingsSnapshotDigest.Hex, b.VersionId.ToString(), b.VersionDigest.Hex);
    internal static SourceOnlyResultBinding Binding(SourceOnlyResultBindingDto b) => new(Id<ProjectIdTag>(b.ProjectId),
        Id<SourceOnlyExecutionIdTag>(b.ExecutionId), Id<JobIdTag>(b.RootJobId), Digest(b.SettingsSnapshotDigest),
        Id<CompositionVersionIdTag>(b.VersionId), Digest(b.VersionDigest));
    internal static SourceOnlyResultReferenceDto ReferenceDto(SourceOnlyResultArtifactReference r) =>
        new(r.ArtifactId.ToString(), r.ContentDigest.Hex, r.ByteSize, r.Kind.ToString());
    internal static SourceOnlyResultArtifactReference Reference(SourceOnlyResultReferenceDto r) =>
        new(Id<ArtifactIdTag>(r.ArtifactId), Digest(r.ContentDigest), r.ByteSize, EnumValue<ArtifactKind>(r.Kind));
    private static SourceOnlyResultEvidenceDto EvidenceDto(SourceOnlyResultEvidenceReference r) => new(r.Kind.ToString(), ReferenceDto(r.Artifact));
    private static SourceOnlyResultEvidenceReference Evidence(SourceOnlyResultEvidenceDto r) =>
        new(EnumValue<SourceOnlyResultEvidenceKind>(r.Kind), Reference(r.Artifact));
    internal static SourceOnlyResultTimeDto TimeDto(RationalTime t) => new(t.Value, t.Rate);
    internal static RationalTime Time(SourceOnlyResultTimeDto t)
    {
        Require(t.Rate > 0); Safe(t.Value); Safe(t.Rate);
        var time = new RationalTime(t.Value, t.Rate);
        Require(time.Value == t.Value && time.Rate == t.Rate);
        return time;
    }
    private static SourceOnlyResultValueDto? ValueDto(SourceOnlyResultMeasuredValue? v) => v is null ? null : new(v.Kind.ToString(), v.Value);
    private static SourceOnlyResultMeasuredValue? Value(SourceOnlyResultValueDto? v) =>
        v is null ? null : new(EnumValue<OutputQcMeasuredValueKind>(v.Kind), v.Value);
    internal static SourceOnlyResultMeasurementsDto? MeasurementsDto(SourceOnlyResultMeasurements? m) => m is null ? null : new(
        TimeDto(m.ExpectedDuration), TimeDto(m.VideoStart), TimeDto(m.VideoEnd), m.AudioStart is null ? null : TimeDto(m.AudioStart.Value),
        m.AudioEnd is null ? null : TimeDto(m.AudioEnd.Value), m.VideoFrameCount, m.AudioFrameCount,
        m.ProbeDigest.Hex, m.VideoTimingDigest.Hex, m.AudioTimingDigest?.Hex, m.SubtitleTimingDigest?.Hex,
        m.LumaDigest.Hex, m.AudioDigest?.Hex, m.MinimumLuma, m.MaximumLuma, m.MeanLuma,
        ValueDto(m.Loudness), ValueDto(m.TruePeak), m.DecodedSuccessfully,
        m.Cues.Select(c => new SourceOnlyResultCueDto(c.CueId.ToString(), TimeDto(c.Start), TimeDto(c.Duration),
            c.CharacterCount, c.MaximumLineCharacters, c.LineCount, c.SafeZoneSatisfied)).ToArray(),
        TimeDto(m.VideoTimeBase), m.AudioTimeBase is null ? null : TimeDto(m.AudioTimeBase.Value),
        m.VideoTimingContinuous, m.VideoFrameCadenceExact, m.OutputDigitalSilence,
        m.AudioVideoStartOffset is null ? null : TimeDto(m.AudioVideoStartOffset.Value),
        m.AudioVideoEndOffset is null ? null : TimeDto(m.AudioVideoEndOffset.Value));
    internal static SourceOnlyResultMeasurements? Measurements(SourceOnlyResultMeasurementsDto? m) => m is null ? null : new(
        Time(m.ExpectedDuration), Time(m.VideoStart), Time(m.VideoEnd), m.AudioStart is null ? null : Time(m.AudioStart),
        m.AudioEnd is null ? null : Time(m.AudioEnd), m.VideoFrameCount, m.AudioFrameCount,
        Digest(m.ProbeDigest), Digest(m.VideoTimingDigest), OptionalDigest(m.AudioTimingDigest), OptionalDigest(m.SubtitleTimingDigest),
        Digest(m.LumaDigest), OptionalDigest(m.AudioDigest), m.MinimumLuma, m.MaximumLuma, m.MeanLuma, Value(m.Loudness), Value(m.TruePeak),
        m.DecodedSuccessfully, m.Cues.Select(c => new SourceOnlyResultSubtitleMeasurement(Id<CueIdTag>(c.CueId), Time(c.Start),
            Time(c.Duration), c.CharacterCount, c.MaximumLineCharacters, c.LineCount, c.SafeZoneSatisfied)).ToArray(),
        Time(m.VideoTimeBase), m.AudioTimeBase is null ? null : Time(m.AudioTimeBase), m.VideoTimingContinuous, m.VideoFrameCadenceExact);

    internal static SourceOnlyResultFrameDto? FrameDto(SourceOnlyResultFrameEvidence? f) => f is null ? null : new(
        f.SourceAssetId.ToString(), f.SourceSpanId.ToString(), f.SourceDigest.Hex, f.SourcePtsIndexDigest.Hex, f.SourceFrameSequence,
        TimeDto(f.SourcePresentationTime), TimeDto(f.SourceFrameDuration), f.SourceWidth, f.SourceHeight, f.Width, f.Height, f.ExtractionRule);
    internal static SourceOnlyResultFrameEvidence? Frame(SourceOnlyResultFrameDto? f) => f is null ? null : new(
        Id<AssetIdTag>(f.SourceAssetId), Id<SourceSpanIdTag>(f.SourceSpanId), Digest(f.SourceDigest), Digest(f.SourcePtsIndexDigest),
        f.SourceFrameSequence, Time(f.SourcePresentationTime), Time(f.SourceFrameDuration),
        f.SourceWidth, f.SourceHeight, f.Width, f.Height, f.ExtractionRule);

    private static SourceOnlyResultArtifactDto ArtifactDto(SourceOnlyResultArtifact a) => new(a.Role.ToString(), a.RenditionId?.ToString(),
        ReferenceDto(a.Artifact), a.MediaType, a.VersionId.ToString(), a.VersionDigest.Hex, ReferenceDto(a.Source), a.GraphDigest.Hex,
        a.ManifestDigest?.Hex, a.Evidence.Select(EvidenceDto).ToArray(), a.Evaluation is null ? null : Element(a.Evaluation.CanonicalJson),
        a.Evaluation?.CanonicalDigest.Hex, MeasurementsDto(a.Measurements), a.FailureCode?.ToString(), a.QcFailureReason?.ToString(),
        a.QcFailureStage?.ToString(), a.Warnings.Select(WarningDto).ToArray(), a.Delivered,
        a.Delivery is null ? null : new(ReferenceDto(a.Delivery.Artifact), EvidenceDto(a.Delivery.Receipt)),
        a.ThumbnailPlanId?.ToString(), FrameDto(a.FrameEvidence), a.QcApplicability.ToString(), a.ProfileId?.ToString(), a.ProfileDigest?.Hex);
    private static SourceOnlyResultWarningDto WarningDto(SourceOnlyResultWarning w)
    {
        AutomatedEvaluationTarget t = w.Target;
        return new(w.Id, AutomatedEvaluationRules.ToWireName(w.RuleId), w.Status.ToString(), w.ReasonCode,
            new(t.Kind.ToString(), t.ProjectId.ToString(), t.CompositionVersionId.ToString(), t.CompositionVersionDigest.Hex,
                t.ArtifactId?.ToString(), t.ArtifactDigest?.Hex, t.TimeBasis?.ToString(), t.Time is null ? null : TimeDto(t.Time.Value),
                t.SettingKey, t.EditorKind?.ToString()));
    }
    private static SourceOnlyResultArtifact Artifact(SourceOnlyResultArtifactDto a)
    {
        Require(a.Delivered == (a.Delivery is not null));
        AutomatedEvaluationRun? run = a.Evaluation is null ? null : AutomatedEvaluationRun.ParseCanonical(
            CanonicalJson.Canonicalize(a.Evaluation.Value.GetRawText()));
        Require((run is null) == (a.EvaluationDigest is null) && (run is null || run.CanonicalDigest == Digest(a.EvaluationDigest!)));
        var artifact = new SourceOnlyResultArtifact(EnumValue<SourceOnlyResultRole>(a.Role), OptionalId<StandardShortFormPlanIdTag>(a.RenditionId),
            Reference(a.Artifact), Id<CompositionVersionIdTag>(a.VersionId), Digest(a.VersionDigest), Reference(a.Source), Digest(a.GraphDigest),
            OptionalDigest(a.ManifestDigest), a.Evidence.Select(Evidence).ToArray(), run, Measurements(a.Measurements),
            OptionalEnum<SourceOnlyResultFailureCode>(a.FailureCode), OptionalEnum<OutputQcFailureReason>(a.QcFailureReason),
            OptionalEnum<OutputQcStage>(a.QcFailureStage), a.Delivery is null ? null : new(Reference(a.Delivery.Artifact), Evidence(a.Delivery.Receipt)),
            OptionalId<StandardThumbnailPlanIdTag>(a.ThumbnailPlanId), Frame(a.FrameEvidence),
            OptionalId<HubRadio.Core.Rendering.RenderProfileIdTag>(a.ProfileId), OptionalDigest(a.ProfileDigest));
        Require(artifact.MediaType == a.MediaType && artifact.QcApplicability.ToString() == a.QcApplicability &&
            (artifact.Measurements is null || artifact.Measurements.OutputDigitalSilence == a.Measurements!.OutputDigitalSilence));
        Require(Write(a.Warnings) == Write(artifact.Warnings.Select(WarningDto).ToArray()));
        return artifact;
    }

    internal static void ValidateShape(SourceOnlyResult result)
    {
        SourceOnlyResultBinding b = result.Binding;
        Require(b is not null && b.ProjectId is not null && b.ExecutionId is not null && b.RootJobId is not null &&
            b.SettingsSnapshotDigest is not null && b.VersionId is not null && b.VersionDigest is not null);
        Require(Enum.IsDefined(result.GenerationDisposition));
        Require(result.Artifacts.Count <= SourceOnlyResult.MaximumArtifactCount && result.Omissions.Count <= SourceOnlyResult.MaximumOmissionCount &&
            result.Failures.Count <= SourceOnlyResult.MaximumFailureCount && result.ReconciliationArtifacts.Count <= SourceOnlyResult.MaximumReconciliationCount &&
            result.NativeReconciliationAttempts.Count <= SourceOnlyResult.MaximumReconciliationCount &&
            result.GenerationWarnings.Count <= SourceOnlyResult.MaximumGenerationWarningCount && result.TranscriptReferences.Count <= SourceOnlyResult.MaximumTranscriptCount);
        Require(result.Artifacts.Select(a => a.ArtifactId).Distinct().Count() == result.Artifacts.Count);
        Require(result.Artifacts.Select(a => (a.Role, a.RenditionId, a.ThumbnailPlanId)).Distinct().Count() == result.Artifacts.Count);
        Require(result.Artifacts.Count(a => a.Role == SourceOnlyResultRole.VerticalBurnIn) <= StandardCompositionPlan.MaximumShortFormCount);
        foreach (SourceOnlyResultArtifact a in result.Artifacts)
        {
            Require(Enum.IsDefined(a.Role) && (a.Role == SourceOnlyResultRole.VerticalBurnIn) == (a.RenditionId is not null));
            Require(a.IsImage == (a.ThumbnailPlanId is not null) && (a.FrameEvidence is null || a.IsImage) &&
                (a.ProfileId is null) == (a.ProfileDigest is null));
            if (a.FrameEvidence is SourceOnlyResultFrameEvidence frame)
            {
                ValidateTime(frame.SourcePresentationTime); ValidateTime(frame.SourceFrameDuration);
                Require(frame.SourceAssetId is not null && frame.SourceSpanId is not null && frame.SourceDigest == a.Source.ContentDigest &&
                    frame.SourcePtsIndexDigest is not null && frame.SourceFrameSequence >= 0 && frame.SourcePresentationTime >= default(RationalTime) &&
                    frame.SourceFrameDuration > default(RationalTime) && frame.SourceWidth > 0 && frame.SourceHeight > 0 &&
                    frame.Width is >= 2 and <= 4096 && frame.Height is >= 2 and <= 4096 &&
                    frame.ExtractionRule == "decode-original-select-source-sequence-contain-v1");
            }
            ValidateReference(a.Artifact, ArtifactKind.Render); ValidateReference(a.Source, ArtifactKind.Original);
            Require(a.VersionId is not null && a.VersionDigest is not null && a.GraphDigest is not null &&
                a.ArtifactId != a.Source.ArtifactId && a.Evidence.Count <= 4 && a.Evidence.Select(e => e.Kind).Distinct().Count() == a.Evidence.Count);
            if (a.ManifestDigest is null) Require(a.FailureCode is not null && a.Evidence.Count == 0 && a.Evaluation is null);
            if (a.Role != SourceOnlyResultRole.VerticalBurnIn && !a.IsImage) Require(a.VersionId == b!.VersionId && a.VersionDigest == b.VersionDigest);
            foreach (SourceOnlyResultEvidenceReference evidence in a.Evidence)
            {
                Require(Enum.IsDefined(evidence.Kind) && evidence.Kind != SourceOnlyResultEvidenceKind.DeliveryReceipt);
                ValidateReference(evidence.Artifact, ArtifactKind.Evidence);
            }
            Require(a.FailureCode is null || Enum.IsDefined(a.FailureCode.Value));
            Require((a.QcFailureReason is null) == (a.QcFailureStage is null));
            if (a.QcFailureReason is not null) Require(Enum.IsDefined(a.QcFailureReason.Value) && Enum.IsDefined(a.QcFailureStage!.Value) && a.Measurements is null);
            if (a.Measurements is not null) { Require(a.IsVideo); ValidateMeasurements(a.Measurements); }
            if (a.Evaluation is AutomatedEvaluationRun run)
            {
                Require(run.ProjectId == b!.ProjectId && run.JobId == b.RootJobId && run.CompositionVersionId == a.VersionId &&
                    run.CompositionVersionDigest == a.VersionDigest && run.ArtifactId?.ToString() == a.ArtifactId.ToString() &&
                    run.ArtifactDigest == a.ArtifactDigest && run.OutputDigest == a.ArtifactDigest && run.SettingsSnapshotDigest == b.SettingsSnapshotDigest &&
                    run.EnvironmentVersion == SourceOnlyOutputEvaluation.EnvironmentVersion && run.RuleSetVersion.ToString() == SourceOnlyOutputEvaluation.RuleSetVersion);
                if (!a.IsVideo)
                    Require(run.Rules.Where(r => r.RuleId is AutomatedEvaluationRuleId.Duration or AutomatedEvaluationRuleId.AvSync or
                        AutomatedEvaluationRuleId.Loudness or AutomatedEvaluationRuleId.TruePeak or AutomatedEvaluationRuleId.FullDecode)
                        .All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
                Require(run.Rules.Where(r => r.Scope == AutomatedEvaluationRuleScope.UserInterface).All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
                Require(run.Limitations.Count == 9 && run.Limitations.All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
                SourceOnlyResultEvidenceReference? proof = a.Evidence.SingleOrDefault(e => e.Kind == SourceOnlyResultEvidenceKind.QualityControl);
                Require(proof is not null && run.Rules.Where(r => r.Status != AutomatedEvaluationStatus.NotEvaluated)
                    .All(r => r.EvidenceDigest == proof.Artifact.ContentDigest));
                foreach (AutomatedEvaluationRuleResult rule in run.Rules)
                {
                    Require(rule.ReasonCode.StartsWith("source_only.", StringComparison.Ordinal) ||
                        rule.RuleId == AutomatedEvaluationRuleId.AvSync && rule.Status == AutomatedEvaluationStatus.Warning &&
                        rule.ReasonCode == "timing.numeric_tolerance_unapproved");
                    if (rule.Target?.Time is RationalTime t) ValidateTime(t);
                    if (rule.Status is AutomatedEvaluationStatus.Warning or AutomatedEvaluationStatus.Fail) Require(rule.Target is not null);
                }
            }
            if (a.FailureCode is null)
            {
                Require(a.Evaluation is not null && a.Evidence.Count == 4 && a.ProfileId is not null &&
                    (!a.IsVideo || a.Measurements is not null) && (!a.IsImage || a.FrameEvidence is not null));
            }
            if (a.Delivery is not null)
            {
                Require(!a.HasBlockingFailure);
                ValidateReference(a.Delivery.Artifact, ArtifactKind.Delivery);
                ValidateReference(a.Delivery.Receipt.Artifact, ArtifactKind.Evidence);
                Require(a.Delivery.Receipt.Kind == SourceOnlyResultEvidenceKind.DeliveryReceipt &&
                    a.Delivery.Artifact.ArtifactId != a.ArtifactId && a.Delivery.Artifact.ArtifactId != a.Source.ArtifactId &&
                    a.Delivery.Receipt.Artifact.ArtifactId != a.Delivery.Artifact.ArtifactId &&
                    a.Delivery.Artifact.ContentDigest == a.ArtifactDigest && a.Delivery.Artifact.ByteSize == a.ByteSize);
            }
        }
        foreach (SourceOnlyResultOmission o in result.Omissions)
            Require(Enum.IsDefined(o.Role) && Enum.IsDefined(o.Reason) &&
                (o.Role is SourceOnlyResultOmissionRole.VerticalCallToAction or SourceOnlyResultOmissionRole.VerticalSubtitles) == (o.RenditionId is not null));
        foreach (SourceOnlyResultFailure f in result.Failures)
        {
            Require(Enum.IsDefined(f.Code));
            if (f.PromotionArtifact is not null)
            {
                ValidateReference(f.PromotionArtifact, ArtifactKind.Evidence);
                Require(f.Code == SourceOnlyResultFailureCode.EvidencePromotionReconciliationRequired && f.EvidenceKind is not null &&
                    f.EvidenceKind != SourceOnlyResultEvidenceKind.DeliveryReceipt && Enum.IsDefined(f.EvidenceKind.Value) &&
                    result.ReconciliationArtifacts.Contains(f.PromotionArtifact));
            }
            else Require(f.EvidenceKind is null);
        }
        foreach (SourceOnlyResultArtifactReference a in result.ReconciliationArtifacts)
        { Require(a.Kind is ArtifactKind.Render or ArtifactKind.Evidence or ArtifactKind.Delivery); ValidateReference(a, a.Kind); }
        foreach (SourceOnlyResultPromotionAttempt p in result.NativeReconciliationAttempts)
        {
            Require(p.ProjectId == b!.ProjectId && result.ReconciliationArtifacts.Contains(p.Artifact) &&
                Path.IsPathFullyQualified(p.StagingPath) && !p.StagingPath.StartsWith(@"\\", StringComparison.Ordinal) &&
                !p.StagingPath.Any(char.IsControl) && p.StagingPath.Length <= 32768 &&
                p.StorageReference.StartsWith($"projects/{b.ProjectId}/{ArtifactStore.GetKindFolder(p.Artifact.Kind)}/", StringComparison.Ordinal) &&
                !p.StorageReference.Contains('\\') && !p.StorageReference.Contains(':') && !p.StorageReference.Any(char.IsControl) &&
                p.StorageReference.Split('/').All(s => s.Length > 0 && s is not "." and not "..") &&
                (p.Artifact.Kind == ArtifactKind.Evidence) == (p.EvidenceKind is not null));
            if (p.EvidenceKind is not null) Require(Enum.IsDefined(p.EvidenceKind.Value));
        }
        Require(result.NativeReconciliationAttempts.Select(p => p.Artifact.ArtifactId).Distinct().Count() == result.NativeReconciliationAttempts.Count);
        foreach (SourceOnlyResultGenerationWarning warning in result.GenerationWarnings) ValidateGenerationWarning(warning);
        Require(result.ReconciliationArtifacts.Select(a => a.ArtifactId).Distinct().Count() == result.ReconciliationArtifacts.Count);
        Require(result.TranscriptReferences.Select(t => t.RevisionId).Distinct().Count() == result.TranscriptReferences.Count);
        foreach (SourceOnlyResultTranscriptReference t in result.TranscriptReferences)
            Require(t.RevisionId is not null && t.RevisionDigest is not null && t.SourceArtifactId is not null);
        Require(result.AncillaryArtifacts.Count <= SourceOnlyResult.MaximumAncillaryArtifactCount &&
            result.AncillaryArtifacts.Select(e => e.Kind).Distinct().Count() == result.AncillaryArtifacts.Count &&
            result.AncillaryArtifacts.Select(e => e.Artifact.ArtifactId).Distinct().Count() == result.AncillaryArtifacts.Count);
        foreach (SourceOnlyResultEvidenceReference e in result.AncillaryArtifacts)
        {
            Require(e.Kind is SourceOnlyResultEvidenceKind.Transcript or SourceOnlyResultEvidenceKind.UsedSourceRanges);
            ValidateReference(e.Artifact, ArtifactKind.Delivery);
            Require(result.Artifacts.All(a => a.ArtifactId != e.Artifact.ArtifactId));
        }
        if (result.PrimaryWorkflow is SourceOnlyResultWorkflowReference w)
        {
            SourceOnlyResultArtifact? a = result.Artifacts.SingleOrDefault(a => a.ArtifactId == w.ArtifactId);
            Require(a?.Role == SourceOnlyResultRole.LandscapeClean && a.Evaluation is not null &&
                a.Evaluation.EvaluationId == w.EvaluationId && w.Revision > 0 && w.CanonicalDigest is not null &&
                w.State == QuickStartEvaluationPolicy.DetermineCompletedState(a.Evaluation));
        }
    }

    internal static void ValidateReference(SourceOnlyResultArtifactReference a, ArtifactKind kind) =>
        Require(a is not null && a.ArtifactId is not null && a.ContentDigest is not null && a.Kind == kind &&
            a.ByteSize is > 0 and <= SourceOnlyResult.MaximumSafeInteger);
    internal static void ValidateMeasurements(SourceOnlyResultMeasurements m)
    {
        ValidateTime(m.ExpectedDuration); ValidateTime(m.VideoStart); ValidateTime(m.VideoEnd); ValidateTime(m.VideoDuration); ValidateTime(m.VideoTimeBase);
        Require(m.ExpectedDuration > default(RationalTime) && m.VideoEnd > m.VideoStart && m.VideoFrameCount > 0 &&
            m.MinimumLuma >= 0 && m.MaximumLuma <= 255 && m.MinimumLuma <= m.MeanLuma && m.MeanLuma <= m.MaximumLuma &&
            m.ProbeDigest is not null && m.VideoTimingDigest is not null && m.LumaDigest is not null && m.VideoTimeBase > default(RationalTime));
        bool audio = m.AudioStart is not null;
        Require(audio == (m.AudioEnd is not null) && audio == (m.AudioFrameCount is not null) && audio == (m.AudioTimingDigest is not null) &&
            audio == (m.AudioDigest is not null) && audio == (m.Loudness is not null) && audio == (m.TruePeak is not null) && audio == (m.AudioTimeBase is not null));
        if (audio)
        {
            ValidateTime(m.AudioStart!.Value); ValidateTime(m.AudioEnd!.Value); ValidateTime(m.AudioDuration!.Value);
            ValidateTime(m.AudioVideoStartOffset!.Value); ValidateTime(m.AudioVideoEndOffset!.Value);
            ValidateTime(m.AudioTimeBase!.Value);
            Require(m.AudioEnd > m.AudioStart && m.AudioFrameCount > 0 && m.AudioTimeBase > default(RationalTime));
        }
        foreach (SourceOnlyResultMeasuredValue? v in new[] { m.Loudness, m.TruePeak })
            if (v is not null) Require(Enum.IsDefined(v.Kind) && (v.Kind == OutputQcMeasuredValueKind.Finite) == (v.Value is not null));
        Require(m.Cues.Count <= StandardSubtitleInput.MaximumCueCount && m.Cues.Select(c => c.CueId).Distinct().Count() == m.Cues.Count);
        foreach (SourceOnlyResultSubtitleMeasurement c in m.Cues)
        {
            ValidateTime(c.Start); ValidateTime(c.Duration);
            Require(c.CueId is not null && c.Start >= default(RationalTime) && c.Duration > default(RationalTime) &&
                c.CharacterCount is >= 0 and <= 32768 && c.MaximumLineCharacters is >= 0 and <= 32768 && c.LineCount is > 0 and <= 1024);
        }
    }

    internal static void ValidateGenerationWarning(SourceOnlyResultGenerationWarning warning)
    {
        Require(warning is not null && warning.Role is { Length: > 0 and <= 128 } &&
            warning.Code is { Length: > 0 and <= 128 } && GenerationWarningCodes.Contains(warning.Code, StringComparer.Ordinal));
        // Warning subjects include omitted subtitle inputs; they are not playable artifact roles.
        if (warning.Role is "plan" or "music" or "video" or "landscape-subtitles" or "human-evaluation") return;
        if (warning.Role.StartsWith("vertical-subtitles-", StringComparison.Ordinal))
            _ = Id<StandardShortFormPlanIdTag>(warning.Role["vertical-subtitles-".Length..]);
        else _ = SourceOnlyOutputEvaluation.ParseRole(warning.Role);
    }

    // Closed producer vocabulary, mirrored by pipelineProtocol.ts. Never trim, rewrite, or
    // accept arbitrary source text. NOT EVALUATED is the exact semantic-completeness reason,
    // not a QC status conversion. Generated warnings, checkpoints and Web DTOs keep identical pairs.
    internal static readonly IReadOnlyList<string> GenerationWarningCodes = Array.AsReadOnly(new[]
    {
        // StandardCompositionWarningCode.
        "SubjectRegionUnavailable", "FixedLayoutFallback", "BlurredBackgroundFallback",
        "PlatformUiUnmeasured", "CandidateLimitApplied", "ThumbnailQualityNotEvaluated",
        // MusicCatalogWarning and unassigned MusicAssignmentReason (Assigned is not a warning).
        "LibraryAbsent", "LibraryEmpty", "LibraryInaccessible", "UnsafeLibraryLocation",
        "TrackUnreadable", "DuplicateContent", "SourceChangedDuringScan", "NoEligibleTracks",
        "CatalogUnavailable", "UnsupportedCueKind", "SpeechOverlap", "DialogueMusicDisabled", "NoTrackLongEnough",
        // StandardArtifactGenerator fixed render reasons.
        "NoEligibleNonDialogueCues", "UntaggedSourceColorFallback", "HdrToneMappedToSdrBt709NotQualityApproval",
        "MusicLibraryUnavailableAtRenderCompletedWithoutMusic", "SilentAudioLoudnessNotMeasurable",
        // StandardSubtitleBuilder fixed reasons, including exclusions when no subtitle artifact survives.
        "no-real-asr-cues", "unresolved-output-source-span", "disabled-by-settings", "missing-token-timing",
        "unmatched-text-without-token-timing", "overlapping-token-timing", "cut-crosses-token-without-inner-timing",
        "outside-output-cuts", "token-too-long-without-inner-timing", "unsupported-source-display-text",
        "text-cannot-split-without-inner-token-timing", "renderer-cue-limit", "no-versioned-safe-zone", "layout-not-resolved",
        "no-displayable-source-text", "no-grapheme-safe-token-partition", "line-too-long-without-feasible-token-partition",
        "too-many-source-lines", "duration-too-long-without-feasible-token-partition", "source-duration-below-minimum",
        "source-reading-speed-exceeded", "no-feasible-partition-with-source-gaps", "no-feasible-exact-token-partition",
        "cue-boundary-whitespace-trimmed", "output-cues-overlap", "output-gap-below-minimum-without-retiming",
        "line-height-rounded-inward-to-pixels", "opaque-setting-explicitly-overridden-by-transparent-background-request",
        "platform-ui-mask-not-measured", "NOT EVALUATED", "source-gaps-preserved-instead-of-retimed",
        // FR-051 FullCoverage summaries (one per build).
        "cue-ends-may-extend-into-following-silence-starts-never-move", "cue-end-extended-into-following-silence", "shown-below-minimum-duration",
        "shown-above-maximum-reading-speed", "shown-with-gap-below-minimum", "untimed-token-shown-between-timed-neighbours",
    });

    internal static JsonDocument ParseDocument(string json, int maximumBytes, int maximumArrayLength = 8192,
        int maximumStringLength = 32768)
    {
        Require(json.Length > 0 && json.Length <= maximumBytes && Utf8.GetByteCount(json) <= maximumBytes);
        JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        try
        {
            ValidateTree(document.RootElement, maximumArrayLength, maximumStringLength);
            Require(CanonicalJson.Canonicalize(json) == json);
            return document;
        }
        catch { document.Dispose(); throw; }
    }
    private static void ValidateTree(JsonElement value, int maximumArrayLength, int maximumStringLength)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    Require(p.Name.Length <= 128 && names.Count < 64 && names.Add(p.Name)); ValidateTree(p.Value, maximumArrayLength, maximumStringLength);
                }
                break;
            case JsonValueKind.Array:
                Require(value.GetArrayLength() <= maximumArrayLength);
                foreach (JsonElement item in value.EnumerateArray()) ValidateTree(item, maximumArrayLength, maximumStringLength);
                break;
            case JsonValueKind.String: Require(value.GetString()!.Length <= maximumStringLength); break;
            case JsonValueKind.Number: Require(value.GetRawText().Length <= 128); break;
        }
    }
    private static void CheckArray(JsonElement root, string key, int maximum)
    {
        Require(root.TryGetProperty(key, out JsonElement array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= maximum);
    }
    internal static JsonElement Element(string json) { using JsonDocument d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    internal static string Write<T>(T value, int maximumBytes = 2 * 1024 * 1024, JsonSerializerOptions? options = null)
    {
        var buffer = new BoundedBuffer(maximumBytes);
        using (var writer = new Utf8JsonWriter(buffer)) { JsonSerializer.Serialize(writer, value, options ?? Options); writer.Flush(); }
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
        Require(canonical.Length <= maximumBytes);
        return Utf8.GetString(canonical);
    }
    internal static TEnum EnumValue<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.TryParse(value, false, out TEnum parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value ? parsed : throw Invalid();
    private static TEnum? OptionalEnum<TEnum>(string? value) where TEnum : struct, Enum => value is null ? null : EnumValue<TEnum>(value);
    internal static EntityId<T> Id<T>(string text) => EntityId<T>.TryParse(text, out EntityId<T>? id) && id.ToString() == text ? id : throw Invalid();
    internal static EntityId<T>? OptionalId<T>(string? text) => text is null ? null : Id<T>(text);
    internal static ContentDigest Digest(string text) => ContentDigest.TryParse(text, out ContentDigest? d) && d.Hex == text ? d : throw Invalid();
    internal static ContentDigest? OptionalDigest(string? text) => text is null ? null : Digest(text);
    internal static void Require([DoesNotReturnIf(false)] bool valid) { if (!valid) throw Invalid(); }
    private static void Safe(long n) => Require(n is >= -SourceOnlyResult.MaximumSafeInteger and <= SourceOnlyResult.MaximumSafeInteger);
    private static void ValidateTime(RationalTime t) { Safe(t.Value); Safe(t.Rate); Require(t.Rate > 0); }
    internal static JsonException Invalid() => new("Invalid source-only canonical evidence.");
    private static long Integer(ref Utf8JsonReader reader)
    {
        using JsonDocument d = JsonDocument.ParseValue(ref reader);
        JsonElement v = d.RootElement;
        Require(v.ValueKind == JsonValueKind.Number && v.GetRawText().Length <= 128);
        // long.TryParse rejects fractional tails exactly; decimal.TryParse may round them away.
        if (!long.TryParse(v.GetRawText(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out long result)) throw Invalid();
        Safe(result); return result;
    }
    private sealed class ExactInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Integer(ref reader);
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) { Safe(value); writer.WriteNumberValue(value); }
    }
    private sealed class ExactInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => checked((int)Integer(ref reader));
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
    private sealed class ExactDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using JsonDocument d = JsonDocument.ParseValue(ref reader);
            JsonElement v = d.RootElement;
            if (v.ValueKind != JsonValueKind.Number || v.GetRawText().Length > 128 || !v.TryGetDecimal(out decimal n) ||
                CanonicalJson.Canonicalize(v.GetRawText()) != CanonicalJson.Canonicalize(JsonSerializer.Serialize(n))) throw Invalid();
            return n;
        }
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
    private sealed class BoundedBuffer(int maximumBytes) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;
        public void Advance(int count) { Require(count >= 0 && count <= maximumBytes - _buffer.WrittenCount); _buffer.Advance(count); }
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(Length(sizeHint))[..Length(sizeHint)];
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(Length(sizeHint))[..Length(sizeHint)];
        private int Length(int sizeHint)
        {
            int remaining = maximumBytes - _buffer.WrittenCount;
            Require(sizeHint >= 0 && Math.Max(1, sizeHint) <= remaining);
            return Math.Min(remaining, Math.Max(4096, sizeHint));
        }
    }
}