using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Projects;

internal sealed record SourceOnlyResultRoleIdentity(SourceOnlyResultRole Role,
    EntityId<StandardShortFormPlanIdTag>? RenditionId = null, EntityId<StandardThumbnailPlanIdTag>? ThumbnailPlanId = null);

/// <summary>
/// Measures each generated video with OutputQc and persists independent immutable runs.
/// Only landscape-clean participates in the existing single-artifact workflow. This
/// service never approves, delivers, finishes Jobs, or writes execution.ResultJson.
/// The coordinator persists the returned canonical result, including partial failures.
/// If generation returns no typed artifact, the coordinator must already have bound
/// execution.VersionId/VersionDigest to its standard child; this service never guesses
/// a version from a mutable working copy or invents an identity for a failed render.
/// </summary>
public sealed class SourceOnlyOutputEvaluation
{
    public const string EnvironmentVersion = "source-only.output-evaluation.v2";
    public const string RuleSetVersion = "2.0.0";
    private readonly ArtifactStore _artifacts;
    private readonly Func<OutputQcRequest, CancellationToken, Task<OutputQcResult>> _runQc;
    private readonly AutomatedEvaluationStore _evaluations;
    private readonly QuickStartWorkflowStore _workflows;
    private readonly ProjectStore _projects;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>? _promoteEvidence;

    public SourceOnlyOutputEvaluation(ArtifactStore artifacts, OutputQc qc, AutomatedEvaluationStore evaluations,
        QuickStartWorkflowStore workflows, ProjectStore projects)
        : this(artifacts, (qc ?? throw new ArgumentNullException(nameof(qc))).RunAsync, evaluations, workflows, projects)
    {
    }

    // Internal test seam only. Production ALWAYS invokes the real OutputQc.RunAsync.
    internal SourceOnlyOutputEvaluation(ArtifactStore artifacts,
        Func<OutputQcRequest, CancellationToken, Task<OutputQcResult>> runQc,
        AutomatedEvaluationStore evaluations, QuickStartWorkflowStore workflows, ProjectStore projects,
        Func<DateTimeOffset>? utcNow = null,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>? promoteEvidence = null)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _runQc = runQc ?? throw new ArgumentNullException(nameof(runQc));
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _promoteEvidence = promoteEvidence;
    }

    public async Task<SourceOnlyResult> EvaluateAsync(StoredSourceOnlyExecution execution,
        StandardArtifactGenerationResult generated, DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution); ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(settingsProfile); ArgumentNullException.ThrowIfNull(settings);
        if (execution.ResultJson is not null)
        {
            if (settings.CanonicalJson != execution.Request.SettingsSnapshot.CanonicalJson ||
                DefaultSettingsSnapshot.ParseCanonical(settings.CanonicalJson, settingsProfile).ContentDigest != settings.ContentDigest)
                throw new SourceOnlyResultException(SourceOnlyResultFailureCode.SettingsBindingMismatch);
            SourceOnlyResultLoadResult prior = await new SourceOnlyResultService(_artifacts, _evaluations, _workflows, _projects)
                .LoadAsync(execution, ct).ConfigureAwait(false);
            if (!prior.Validation.IsValid) throw new SourceOnlyResultException(SourceOnlyResultFailureCode.StoredEvidenceInvalid);
            if (generated.Artifacts.Count != prior.Result.Artifacts.Count || generated.Artifacts.Any(a =>
                !prior.Result.Artifacts.Any(p => p.ArtifactId == a.Artifact.Id && p.ArtifactDigest == a.Artifact.ContentDigest &&
                    p.ByteSize == a.Artifact.ByteSize && p.VersionId == a.Graph.CompositionVersionId && p.VersionDigest == a.Graph.CompositionVersionDigest)))
                throw new SourceOnlyResultException(SourceOnlyResultFailureCode.BindingMismatch);
            StandardCompositionPreparation replayPreparation = new VersionService(_projects).GetStandardComposition(prior.Result.VersionId)
                ?? throw new SourceOnlyResultException(SourceOnlyResultFailureCode.StoredEvidenceInvalid);
            SourceOnlyResultCodec.Require(generated.Warnings.Select(w => new SourceOnlyResultGenerationWarning(w.Role, w.Code))
                .SequenceEqual(prior.Result.GenerationWarnings) && generated.Omissions.Select(MapOmission).SequenceEqual(prior.Result.Omissions));
            foreach (StandardGeneratedArtifact item in generated.Artifacts)
            {
                SourceOnlyResultArtifact artifact = prior.Result.Artifacts.Single(a => a.ArtifactId == item.Artifact.Id);
                SourceOnlyResultRoleIdentity identity = ParseRole(item.BindingEvidence.Role);
                SourceOnlyResultCodec.Require(identity.Role == artifact.Role && identity.RenditionId == artifact.RenditionId &&
                    identity.ThumbnailPlanId == artifact.ThumbnailPlanId);
                ValidateBindings(execution, item, artifact, replayPreparation, prior.Result.Binding, settings);
                JsonElement graph = SourceOnlyResultEvidenceStore.Snapshot(item.Graph), manifest = SourceOnlyResultEvidenceStore.Snapshot(item.Manifest);
                SourceOnlyResultCodec.Require(SourceOnlyResultEvidenceStore.PayloadDigest(graph) == artifact.GraphDigest &&
                    SourceOnlyResultEvidenceStore.PayloadDigest(manifest) == artifact.ManifestDigest);
                _ = SourceOnlyResultInputValidation.Validate(SourceOnlyResultInputValidation.Capture(item, replayPreparation),
                    artifact, replayPreparation, settings, graph, manifest, SourceOnlyResultInputValidation.GetLiveSubtitles(item));
            }
            return prior.Result;
        }
        SourceOnlyResultCodec.Require(generated.Artifacts.Count <= SourceOnlyResult.MaximumArtifactCount &&
            generated.Omissions.Count <= SourceOnlyResult.MaximumOmissionCount &&
            generated.Warnings.Count <= SourceOnlyResult.MaximumGenerationWarningCount &&
            generated.ReconciliationArtifacts.Count <= SourceOnlyResult.MaximumArtifactCount &&
            generated.Artifacts.Select(a => a.Artifact.Id).Distinct().Count() == generated.Artifacts.Count);
        var roles = generated.Artifacts.Select(a => ParseRole(a.BindingEvidence.Role)).ToArray();
        SourceOnlyResultCodec.Require(roles.Distinct().Count() == roles.Length);
        SourceOnlyResultBinding binding = ResolveBinding(execution, generated);
        var failures = new List<SourceOnlyResultFailure>();
        var outputs = new List<SourceOnlyResultArtifact>();
        var transcripts = new List<SourceOnlyResultTranscriptReference>();
        SourceOnlyResultWorkflowReference? primaryWorkflow = null;
        var evidenceStore = new SourceOnlyResultEvidenceStore(_artifacts, _promoteEvidence);
        SourceOnlyResultGenerationCheckpoint? generationCheckpoint = null;
        try
        {
            generationCheckpoint = await SourceOnlyResultGenerationCheckpoint.ReadAsync(_artifacts, execution, ct).ConfigureAwait(false);
            generationCheckpoint?.ValidateGenerated(generated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (Recoverable(e)) { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)); }
        StandardCompositionPreparation? preparation = null;
        try
        {
            preparation = new VersionService(_projects).GetStandardComposition(binding.VersionId);
            SourceOnlyResultCodec.Require(preparation is not null && preparation.Child.ContentDigest == binding.VersionDigest &&
                preparation.Plan.ProjectId == binding.ProjectId);
        }
        catch (Exception e) when (Recoverable(e)) { failures.Add(new(SourceOnlyResultFailureCode.BindingMismatch)); }
        bool settingsValid = SettingsMatch(execution, settingsProfile, settings, preparation);
        if (!settingsValid) failures.Add(new(SourceOnlyResultFailureCode.SettingsBindingMismatch));
        switch (generated.Disposition)
        {
            case StandardArtifactGenerationDisposition.Failed: failures.Add(new(SourceOnlyResultFailureCode.GenerationFailed)); break;
            case StandardArtifactGenerationDisposition.Cancelled: failures.Add(new(SourceOnlyResultFailureCode.GenerationCanceled)); break;
            case StandardArtifactGenerationDisposition.ReconciliationRequired: failures.Add(new(SourceOnlyResultFailureCode.GenerationReconciliationRequired)); break;
        }
        if (generated.Artifacts.Count == 0) failures.Add(new(SourceOnlyResultFailureCode.NoArtifacts));
        if (!roles.Any(r => r.Role == SourceOnlyResultRole.LandscapeClean)) failures.Add(new(SourceOnlyResultFailureCode.PrimaryArtifactMissing));

        for (int index = 0; index < generated.Artifacts.Count; index++)
        {
            StandardGeneratedArtifact item = generated.Artifacts[index];
            (SourceOnlyResultRole role, EntityId<StandardShortFormPlanIdTag>? renditionId, EntityId<StandardThumbnailPlanIdTag>? thumbnailId) = roles[index];
            var proofs = new List<SourceOnlyResultEvidenceReference>();
            SourceOnlyResultFailureCode? failure = null;
            OutputQcFailureReason? qcFailureReason = null;
            OutputQcStage? qcFailureStage = null;
            SourceOnlyResultMeasurements? measured = null;
            AutomatedEvaluationRun? run = null;
            QuickStartWorkflow? evaluating = null;
            bool provenanceValid = false;
            bool sidecarVerified = false;
            bool imageVerified = false;
            SourceOnlyRenderingInputsPayload? renderingInputs = null;
            var sourceAsset = preparation?.Plan.LongForm.Project.Assets.SingleOrDefault(a => a.Id == execution.Request.SourceCandidateId)
                ?? item.ValidationContext.Project.Assets.Single(a => a.Id == execution.Request.SourceCandidateId);
            var source = new SourceOnlyResultArtifactReference(item.BindingEvidence.SourceArtifactId,
                sourceAsset.ContentDigest, sourceAsset.ByteSize, ArtifactKind.Original);
            ContentDigest graphDigest = item.BindingEvidence.GraphDigest;
            ContentDigest? manifestDigest = null;
            SourceOnlyResultArtifact Record() => new(role, renditionId, SourceOnlyResultEvidenceStore.Reference(item.Artifact),
                item.Graph.CompositionVersionId, item.Graph.CompositionVersionDigest, source, graphDigest, manifestDigest,
                proofs, run, measured, failure, qcFailureReason, qcFailureStage, thumbnailPlanId: thumbnailId,
                frameEvidence: thumbnailId is null ? null : SourceOnlyResultImageValidation.Capture(item),
                profileId: item.Graph.ProfileId, profileDigest: item.Graph.ProfileDigest);
            JsonElement graph;
            JsonElement manifest;
            try
            {
                graph = SourceOnlyResultEvidenceStore.Snapshot(item.Graph);
                manifest = SourceOnlyResultEvidenceStore.Snapshot(item.Manifest);
                graphDigest = SourceOnlyResultEvidenceStore.PayloadDigest(graph);
                manifestDigest = SourceOnlyResultEvidenceStore.PayloadDigest(manifest);
                if (preparation is not null) renderingInputs = SourceOnlyResultInputValidation.Capture(item, preparation) with
                { GenerationCheckpoint = generationCheckpoint is null ? null : SourceOnlyResultCodec.ReferenceDto(generationCheckpoint.Artifact) };
            }
            catch (Exception e) when (Recoverable(e))
            {
                failure = SourceOnlyResultFailureCode.EvidencePersistenceFailed;
                outputs.Add(Record());
                continue;
            }
            SourceOnlyResultArtifact record = Record();
            DateTimeOffset startedAt = _utcNow().ToUniversalTime();
            var evaluationId = EntityId<AutomatedEvaluationIdTag>.New();
            ContentDigest inputDigest = IndependentInputDigest(binding, evaluationId, record);
            OutputQcResult? qcResult = null;
            IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues = [];
            RationalTime expectedDuration = item.Graph.Nodes.SingleOrDefault(n => n.Id == item.Graph.OutputNodeId)?.OutputRange.Duration ?? default;
            RationalNumber frameRate = item.ValidationContext.Profile.FrameRate;
            try
            {
                ct.ThrowIfCancellationRequested();
                SourceOnlyResultCodec.ValidateReference(record.Artifact, ArtifactKind.Render);
                SourceOnlyResultCodec.ValidateReference(source, ArtifactKind.Original);
                StoredArtifact actual = await _artifacts.ValidateRegisteredAsync(record.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(actual, record.Artifact, binding.ProjectId);
                StoredArtifact original = await _artifacts.ValidateRegisteredAsync(source.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(original, source, binding.ProjectId);
                ValidateBindings(execution, item, record, preparation, binding, settings);
                SourceOnlyResultEvidenceStore.ValidateGraph(graph, record);
                SourceOnlyResultEvidenceStore.ValidateManifest(manifest, binding, record, actual);
                SourceOnlyResultCodec.Require(renderingInputs is not null && preparation is not null);
                cues = SourceOnlyResultInputValidation.Validate(renderingInputs, record, preparation, settings, graph, manifest,
                    SourceOnlyResultInputValidation.GetLiveSubtitles(item));
                generationCheckpoint?.ValidateArtifact(record, graph, manifest, renderingInputs);
                provenanceValid = true;
                if (preparation is not null && transcripts.Count == 0)
                    transcripts.Add(new(preparation.Plan.LongForm.Provenance.TranscriptRevisionId,
                        preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest, source.ArtifactId));

                StoredCompositionVersion? storedVersion = _projects.GetCompositionVersion(record.VersionId);
                if (storedVersion is null && (role.IsShortVideo() || record.IsImage))
                {
                    // The generator already created this exact short version and bound its graph
                    // and manifest to it. Persist it unchanged; never rebind a landscape output.
                    try
                    {
                        _projects.AppendCompositionVersion(item.ValidationContext.CompositionVersion,
                            CompositionSnapshotJson.WriteVersion(item.ValidationContext.CompositionVersion));
                    }
                    catch (Exception e) when (Recoverable(e))
                    {
                        // A concurrent identical append is acceptable only after exact reread.
                        if (_projects.GetCompositionVersion(record.VersionId) is null)
                            failure = SourceOnlyResultFailureCode.VersionNotPersisted;
                    }
                    storedVersion = _projects.GetCompositionVersion(record.VersionId);
                }
                if (storedVersion is null) failure = SourceOnlyResultFailureCode.VersionNotPersisted;
                else if (storedVersion.ProjectId != binding.ProjectId || storedVersion.ContentDigest != record.VersionDigest ||
                    storedVersion.CanonicalJson != CompositionSnapshotJson.WriteVersion(item.ValidationContext.CompositionVersion))
                    throw new SourceOnlyResultException(SourceOnlyResultFailureCode.BindingMismatch);

                if (role == SourceOnlyResultRole.LandscapeClean && settingsValid && failure is null)
                {
                    try
                    {
                        evaluating = StartPrimary(execution, binding, item, startedAt);
                        inputDigest = evaluating.GetEvaluationInputDigest();
                    }
                    catch (Exception e) when (Recoverable(e))
                    {
                        failure = SourceOnlyResultFailureCode.WorkflowConflict;
                        failures.Add(new(SourceOnlyResultFailureCode.WorkflowConflict, record.ArtifactId));
                    }
                }
                if (item.QualityControlRequest is OutputQcRequest qcRequest)
                {
                    try { qcResult = await _runQc(qcRequest, ct).ConfigureAwait(false); }
                    catch (Exception e) when (Recoverable(e) && e is not (OutputQcException or OperationCanceledException))
                    { throw new SourceOnlyResultException(SourceOnlyResultFailureCode.QualityControlFailed); }
                    measured = SourceOnlyResultObservationBuilder.Capture(item, qcResult);
                    SourceOnlyResultCodec.Require(SourceOnlyResultCodec.Write(CuesDto(cues)) == SourceOnlyResultCodec.Write(CuesDto(measured.Cues)));
                }
                else if (record.IsVideo) throw new SourceOnlyResultException(SourceOnlyResultFailureCode.QualityControlFailed);
                else if (record.IsImage)
                {
                    try { await SourceOnlyResultImageValidation.ValidatePngAsync(_artifacts, record, binding.ProjectId, ct).ConfigureAwait(false); imageVerified = true; }
                    catch (Exception e) when (Recoverable(e) && e is not OperationCanceledException)
                    { throw new SourceOnlyResultException(SourceOnlyResultFailureCode.QualityControlFailed); }
                }
                else
                {
                    SubtitleExportFile file = role == SourceOnlyResultRole.SubRip
                        ? SubRipSubtitleWriter.Write(item.DisplayCues, ct) : WebVttSubtitleWriter.Write(item.DisplayCues, ct);
                    // The actual file has just been hashed. Matching exact generated sidecar
                    // bytes proves format/text/timestamps without substituting a video's QC.
                    sidecarVerified = file.ContentDigest == record.ArtifactDigest && file.ByteSize == record.ByteSize &&
                        cues.Count > 0 && cues.All(c => c.Start + c.Duration <= expectedDuration);
                    if (!sidecarVerified) failure = SourceOnlyResultFailureCode.QualityControlFailed;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { failure = SourceOnlyResultFailureCode.Canceled; }
            catch (OutputQcException e) { failure = SourceOnlyResultFailureCode.QualityControlFailed; qcFailureReason = e.Reason; qcFailureStage = e.Stage; }
            catch (ArtifactStoreException) { failure = SourceOnlyResultFailureCode.ArtifactValidationFailed; }
            catch (SourceOnlyResultException e) { failure = e.Code; }
            catch (Exception e) when (Recoverable(e)) { failure = SourceOnlyResultFailureCode.BindingMismatch; }

            DateTimeOffset completedAt = _utcNow().ToUniversalTime();
            record = Record();
            try
            {
                ct.ThrowIfCancellationRequested();
                proofs.Add(await evidenceStore.SaveAsync(binding, record, SourceOnlyResultEvidenceKind.Graph, graph, ct).ConfigureAwait(false));
                proofs.Add(await evidenceStore.SaveAsync(binding, record, SourceOnlyResultEvidenceKind.Manifest, manifest, ct).ConfigureAwait(false));
                SourceOnlyResultCodec.Require(renderingInputs is not null);
                JsonElement inputPayload = SourceOnlyResultCodec.Element(SourceOnlyResultCodec.Write(renderingInputs, SourceOnlyResultEvidenceStore.MaximumEvidenceBytes));
                SourceOnlyResultEvidenceReference inputProof = await evidenceStore.SaveAsync(binding, record,
                    SourceOnlyResultEvidenceKind.RenderingInputs, inputPayload, ct).ConfigureAwait(false);
                proofs.Add(inputProof);
                JsonElement renderTools = SourceOnlyResultEvidenceStore.Snapshot(item.Manifest.Tools);
                JsonElement? qcTools = qcResult is null ? null : SourceOnlyResultEvidenceStore.Snapshot(qcResult.Tools);
                ContentDigest toolDigest = ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(SourceOnlyResultCodec.Write(new { renderTools, qcTools })));
                ContentDigest? modelDigest = item.Manifest.Models.Count == 0 ? null : StandardRenderGraphFactory.Digest(item.Manifest.Models);
                var qcEvidence = new SourceOnlyQcEvidencePayload(RuleSetVersion, evaluationId.ToString(), inputDigest.Hex,
                    startedAt.ToString("O", CultureInfo.InvariantCulture), completedAt.ToString("O", CultureInfo.InvariantCulture),
                    item.Manifest.ApplicationDigest.Hex, toolDigest.Hex, modelDigest?.Hex, item.Manifest.TemplateVersionDigest.Hex,
                    SourceOnlyResultEvidenceStore.Snapshot(item.BindingEvidence), SourceOnlyResultCodec.Element(settingsProfile.CanonicalJson),
                    SourceOnlyResultCodec.Element(settings.CanonicalJson), renderTools, qcTools,
                    SourceOnlyResultCodec.MeasurementsDto(measured), CuesDto(cues), new(expectedDuration.Value, expectedDuration.Rate),
                    new(frameRate.Numerator, frameRate.Denominator), provenanceValid, settingsValid, sidecarVerified,
                    failure?.ToString(), qcFailureReason?.ToString(), qcFailureStage?.ToString(), imageVerified,
                    inputProof.Artifact.ContentDigest.Hex, measured is null ? null : SourceOnlyResultEvidenceStore.Snapshot(qcResult));
                JsonElement qcPayload = SourceOnlyResultCodec.Element(SourceOnlyResultCodec.Write(qcEvidence, SourceOnlyResultEvidenceStore.MaximumEvidenceBytes));
                SourceOnlyResultEvidenceReference qcProof = await evidenceStore.SaveAsync(binding, record,
                    SourceOnlyResultEvidenceKind.QualityControl, qcPayload, ct).ConfigureAwait(false);
                proofs.Add(qcProof);
                record = Record();
                IReadOnlyList<AutomatedEvaluationRuleObservation> observations = SourceOnlyResultObservationBuilder.Build(binding, record,
                    settingsProfile, settings, qcProof.Artifact.ContentDigest, provenanceValid, settingsValid, sidecarVerified, cues, expectedDuration, frameRate, imageVerified);
                var request = new AutomatedEvaluationRequest(evaluationId, binding.RootJobId, binding.ProjectId, record.VersionId, record.VersionDigest,
                    EntityId<AutomatedEvaluationArtifactIdTag>.Parse(record.ArtifactId.ToString()), record.ArtifactDigest,
                    execution.Request.SettingsSnapshot.ProfileId, execution.Request.SettingsSnapshot.ProfileVersion,
                    execution.Request.SettingsSnapshot.ProfileDigest, binding.SettingsSnapshotDigest, SettingsProfileVersion.Parse(RuleSetVersion),
                    EnvironmentVersion, item.Manifest.ApplicationDigest, toolDigest, modelDigest, item.Manifest.TemplateVersionDigest, null,
                    startedAt, completedAt, completedAt, inputDigest, record.ArtifactDigest, observations);
                AutomatedEvaluationRun evaluated = new AutomatedEvaluationEngine().Evaluate(request);
                try { run = _evaluations.Append(evaluated); }
                catch (Exception e) when (Recoverable(e)) { failure ??= SourceOnlyResultFailureCode.EvaluationPersistenceFailed; }
                if (run is not null && evaluating is not null)
                {
                    try
                    {
                        QuickStartWorkflow completed = _workflows.Append(QuickStartWorkflowEvent.EvaluationCompleted(
                            EntityId<QuickStartWorkflowEventIdTag>.New(), evaluating, run,
                            QuickStartActor.System(QuickStartSystemActor.AutomatedEvaluation), completedAt), evaluating.Revision);
                        primaryWorkflow = new(record.ArtifactId, completed.Revision, completed.CanonicalDigest, completed.State, run.EvaluationId);
                    }
                    catch (Exception e) when (Recoverable(e))
                    {
                        failure ??= SourceOnlyResultFailureCode.WorkflowConflict;
                        failures.Add(new(SourceOnlyResultFailureCode.WorkflowConflict, record.ArtifactId));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { failure ??= SourceOnlyResultFailureCode.Canceled; }
            catch (Exception e) when (Recoverable(e)) { failure ??= SourceOnlyResultFailureCode.EvidencePersistenceFailed; }
            // No successfully generated artifact is removed on a QC/store/workflow failure.
            outputs.Add(Record());
        }

        var nativeAttempts = generated.ReconciliationArtifacts.Select(a => new SourceOnlyResultPromotionAttempt(
            new(a.ArtifactId, a.ContentDigest, a.ByteSize, ArtifactKind.Render), a.ProjectId, a.StagingPath, a.StorageReference))
            .Concat(evidenceStore.ReconciliationAttempts).ToArray();
        failures.AddRange(evidenceStore.ReconciliationAttempts.Select(a => new SourceOnlyResultFailure(
            SourceOnlyResultFailureCode.EvidencePromotionReconciliationRequired,
            outputs.SingleOrDefault(o => a.StorageReference.Contains($"/{o.ArtifactId}/", StringComparison.Ordinal))?.ArtifactId,
            a.Artifact, a.EvidenceKind)));
        return new SourceOnlyResult(binding, evidenceStore.ReconciliationAttempts.Count == 0 ? generated.Disposition :
            StandardArtifactGenerationDisposition.ReconciliationRequired, outputs,
            generated.Omissions.Select(MapOmission).ToArray(), failures.Distinct().ToArray(),
            nativeAttempts.Select(a => a.Artifact).ToArray(),
            transcripts, primaryWorkflow, generated.StagingCleanupRequired || evidenceStore.CleanupRequired,
            generated.Warnings.Select(w => new SourceOnlyResultGenerationWarning(w.Role, w.Code)).ToArray(), nativeAttempts);
    }

    private SourceOnlyResultBinding ResolveBinding(StoredSourceOnlyExecution execution, StandardArtifactGenerationResult generated)
    {
        StandardGeneratedArtifact? primary = generated.Artifacts.FirstOrDefault(a => a.BindingEvidence.Role == "landscape-clean") ?? generated.Artifacts.FirstOrDefault();
        EntityId<CompositionVersionIdTag>? id = execution.VersionId ?? (primary is not null &&
            (ParseRole(primary.BindingEvidence.Role).Role.IsShortVideo() || ParseRole(primary.BindingEvidence.Role).ThumbnailPlanId is not null)
            ? primary.ValidationContext.CompositionVersion.ParentVersionId : primary?.Graph.CompositionVersionId);
        ContentDigest? digest = execution.VersionDigest ?? (id == primary?.Graph.CompositionVersionId
            ? primary?.Graph.CompositionVersionDigest : id is null ? null : _projects.GetCompositionVersion(id)?.ContentDigest);
        if (id is null || digest is null) throw new SourceOnlyResultException(SourceOnlyResultFailureCode.NoArtifacts);
        return new(execution.Request.ProjectId, execution.Request.ExecutionId, execution.Request.RootJobId,
            execution.Request.SettingsSnapshot.ContentDigest, id, digest);
    }

    private QuickStartWorkflow StartPrimary(StoredSourceOnlyExecution execution, SourceOnlyResultBinding binding,
        StandardGeneratedArtifact generated, DateTimeOffset startedAt)
    {
        var workflowBinding = new QuickStartWorkflowBinding(binding.ProjectId, binding.VersionId, binding.VersionDigest);
        QuickStartWorkflow? current = _workflows.Get(workflowBinding);
        if (current is null) current = _workflows.Append(QuickStartWorkflowEvent.StartedEditing(EntityId<QuickStartWorkflowEventIdTag>.New(),
            workflowBinding, QuickStartActor.User(execution.Request.ActorSid), execution.Request.CreatedAt), 0);
        if (current.State != QuickStartWorkflowState.Editing) throw new SourceOnlyResultException(SourceOnlyResultFailureCode.WorkflowConflict);
        QuickStartProjectSnapshot snapshot = _workflows.GetCurrentProjectSnapshot(binding.ProjectId);
        StoredProject stored = _projects.GetProject(binding.ProjectId) ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(stored.Revision == snapshot.Revision &&
            ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(stored.CanonicalJson)) == snapshot.CanonicalPayloadDigest &&
            stored.CanonicalJson == HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(generated.ValidationContext.Project));
        return _workflows.Append(QuickStartWorkflowEvent.EvaluationStarted(EntityId<QuickStartWorkflowEventIdTag>.New(), workflowBinding,
            QuickStartActor.System(QuickStartSystemActor.AutomatedEvaluation), startedAt, binding.SettingsSnapshotDigest, snapshot), current.Revision);
    }

    private static bool SettingsMatch(StoredSourceOnlyExecution execution, DefaultSettingsProfile profile,
        DefaultSettingsSnapshot settings, StandardCompositionPreparation? preparation)
    {
        try
        {
            return DefaultSettingsSnapshot.ParseCanonical(settings.CanonicalJson, profile).ContentDigest == settings.ContentDigest &&
                execution.Request.SettingsSnapshot.CanonicalJson == settings.CanonicalJson && preparation is not null &&
                preparation.Plan.InputDigests.Single(i => i.Kind == StandardPlanInputKind.SettingsSnapshot).ContentDigest == settings.ContentDigest &&
                preparation.Plan.InputDigests.Single(i => i.Kind == StandardPlanInputKind.SettingsProfile).ContentDigest == profile.ContentDigest;
        }
        catch (Exception e) when (Recoverable(e)) { return false; }
    }

    private static void ValidateBindings(StoredSourceOnlyExecution execution, StandardGeneratedArtifact item,
        SourceOnlyResultArtifact record, StandardCompositionPreparation? preparation, SourceOnlyResultBinding binding, DefaultSettingsSnapshot settings)
    {
        StandardArtifactBindingEvidence b = item.BindingEvidence;
        StandardArtifactRole expectedRole = record.Role switch
        {
            SourceOnlyResultRole.LandscapeClean => StandardArtifactRole.LandscapeCleanVideo,
            SourceOnlyResultRole.LandscapeBurnIn => StandardArtifactRole.LandscapeBurnInVideo,
            SourceOnlyResultRole.VerticalBurnIn => StandardArtifactRole.VerticalBurnInVideo,
            SourceOnlyResultRole.VerticalClean => StandardArtifactRole.VerticalCleanVideo,
            SourceOnlyResultRole.SubRip => StandardArtifactRole.LandscapeSubRip,
            SourceOnlyResultRole.WebVtt => StandardArtifactRole.LandscapeWebVtt,
            SourceOnlyResultRole.LandscapeThumbnail => StandardArtifactRole.LandscapeThumbnail,
            SourceOnlyResultRole.VerticalThumbnail => StandardArtifactRole.VerticalThumbnail,
            _ => throw SourceOnlyResultCodec.Invalid(),
        };
        SourceOnlyResultCodec.Require(preparation is not null && b.PlanId == preparation.Plan.Id && b.PlanDigest == preparation.Plan.PlanDigest &&
            item.TypedArtifact.Role == expectedRole && item.TypedArtifact.Artifact == item.Artifact &&
            item.Artifact.ProjectId == binding.ProjectId && b.ArtifactId == item.Artifact.Id &&
            b.VersionId == item.Graph.CompositionVersionId && b.VersionDigest == item.Graph.CompositionVersionDigest &&
            b.GraphId == item.Graph.Id && b.GraphDigest == record.GraphDigest && b.OutputDigest == record.ArtifactDigest && b.OutputByteSize == record.ByteSize &&
            b.SettingsSnapshotDigest == settings.ContentDigest && b.SettingsSnapshotDigest == binding.SettingsSnapshotDigest &&
            item.Manifest.ConfigurationDigest == StandardRenderGraphFactory.Digest(b) &&
            b.DisplayCueDigest == StandardRenderGraphFactory.Digest(item.DisplayCues) &&
            item.ValidationContext.Project.Assets.Single().Id == execution.Request.SourceCandidateId &&
            RenderGraphValidator.Validate(item.Graph, item.ValidationContext).IsValid);
        SourceOnlyResultImageExpected? image = record.IsImage ? SourceOnlyResultImageValidation.Expected(preparation!, record, settings) : null;
        CompositionVersion expected = image?.Version ?? (record.Role.IsShortVideo()
            ? StandardRenderGraphFactory.CreateShortVersion(preparation!, record.RenditionId!) : preparation!.Child);
        SourceOnlyResultCodec.Require(CompositionSnapshotJson.WriteVersion(expected) == CompositionSnapshotJson.WriteVersion(item.ValidationContext.CompositionVersion));
        RenderProfile expectedProfile = image?.Profile ?? StandardRenderGraphFactory.GetProfile(preparation!.Plan,
            record.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn, record.RenditionId);
        SourceOnlyResultCodec.Require(StandardRenderGraphFactory.Digest(expectedProfile) == StandardRenderGraphFactory.Digest(item.ValidationContext.Profile) &&
            StandardRenderGraphFactory.Digest(image?.Layout ?? StandardRenderGraphFactory.GetLayout(preparation!.Plan, record.RenditionId)) ==
                StandardRenderGraphFactory.Digest(item.ValidationContext.LayoutSpec));
        if (item.QualityControlRequest is OutputQcRequest request)
        {
            SourceOnlyResultCodec.Require(record.IsVideo && request.OutputArtifactId == record.ArtifactId &&
                ReferenceEquals(request.Graph, item.Graph) && ReferenceEquals(request.RenderManifest, item.Manifest) &&
                ReferenceEquals(request.ValidationContext, item.ValidationContext) && ReferenceEquals(request.SubtitleEvidence, item.SubtitleEvidence));
            OutputQc.ValidateEvidence(request, CancellationToken.None);
        }
        SourceOnlyResultCodec.Require(record.IsVideo == (item.QualityControlRequest is not null));
        if (record.IsImage)
            SourceOnlyResultCodec.Require(item.BindingEvidence.Execution is not null &&
                item.BindingEvidence.Execution.Thumbnail?.ThumbnailPlanId == record.ThumbnailPlanId &&
                item.BindingEvidence.Execution.EncoderSettings is null && item.BindingEvidence.Execution.Loudness is null &&
                item.DisplayCues.Count == 0 && item.SubtitleFiles.Count == 0 && item.SubtitleEvidence is null);
        SourceOnlyResultCodec.Require(item.Manifest.ApplicationDigest is not null && item.Manifest.Outputs.Count == 1);
    }

    internal static SourceOnlyResultRoleIdentity ParseRole(string role) => role switch
    {
        "landscape-clean" => new(SourceOnlyResultRole.LandscapeClean),
        "landscape-burn-in" => new(SourceOnlyResultRole.LandscapeBurnIn),
        "landscape-srt" => new(SourceOnlyResultRole.SubRip),
        "landscape-vtt" => new(SourceOnlyResultRole.WebVtt),
        _ when role?.StartsWith("vertical-burn-in-", StringComparison.Ordinal) == true =>
            new(SourceOnlyResultRole.VerticalBurnIn, SourceOnlyResultCodec.Id<StandardShortFormPlanIdTag>(role["vertical-burn-in-".Length..])),
        _ when role?.StartsWith("vertical-clean-", StringComparison.Ordinal) == true =>
            new(SourceOnlyResultRole.VerticalClean, SourceOnlyResultCodec.Id<StandardShortFormPlanIdTag>(role["vertical-clean-".Length..])),
        _ when role?.StartsWith("thumbnail-landscape-", StringComparison.Ordinal) == true =>
            new(SourceOnlyResultRole.LandscapeThumbnail, ThumbnailPlanId: SourceOnlyResultCodec.Id<StandardThumbnailPlanIdTag>(role["thumbnail-landscape-".Length..])),
        _ when role?.StartsWith("thumbnail-vertical-", StringComparison.Ordinal) == true =>
            new(SourceOnlyResultRole.VerticalThumbnail, ThumbnailPlanId: SourceOnlyResultCodec.Id<StandardThumbnailPlanIdTag>(role["thumbnail-vertical-".Length..])),
        _ => throw SourceOnlyResultCodec.Invalid(),
    };
    internal static SourceOnlyResultCueDto[] CuesDto(IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues) => cues.Select(c =>
        new SourceOnlyResultCueDto(c.CueId.ToString(), new(c.Start.Value, c.Start.Rate), new(c.Duration.Value, c.Duration.Rate),
            c.CharacterCount, c.MaximumLineCharacters, c.LineCount, c.SafeZoneSatisfied)).ToArray();
    internal static ContentDigest IndependentInputDigest(SourceOnlyResultBinding binding,
        EntityId<AutomatedEvaluationIdTag> evaluationId, SourceOnlyResultArtifact artifact) =>
        ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(SourceOnlyResultCodec.Write(new
        {
            schema = "source-only.evaluation-attempt.v2",
            binding = SourceOnlyResultCodec.BindingDto(binding),
            evaluationId = evaluationId.ToString(),
            artifactId = artifact.ArtifactId.ToString(),
            artifactDigest = artifact.ArtifactDigest.Hex,
            graphDigest = artifact.GraphDigest.Hex,
            manifestDigest = artifact.ManifestDigest?.Hex,
        })));
    private static SourceOnlyResultOmission MapOmission(StandardGenerationOmission omission)
    {
        SourceOnlyResultOmissionRole role = omission.Role switch
        {
            "landscape-burn-in/srt/vtt" => SourceOnlyResultOmissionRole.LandscapeSubtitles,
            "thumbnail" => SourceOnlyResultOmissionRole.Thumbnail,
            _ when omission.Role.StartsWith("vertical-call-to-action-", StringComparison.Ordinal) => SourceOnlyResultOmissionRole.VerticalCallToAction,
            _ when omission.Role.StartsWith("vertical-burn-in-", StringComparison.Ordinal) => SourceOnlyResultOmissionRole.VerticalSubtitles,
            _ => Enum.TryParse(omission.Role, false, out SourceOnlyResultOmissionRole parsed) && Enum.IsDefined(parsed) && parsed.ToString() == omission.Role
                ? parsed : SourceOnlyResultOmissionRole.Other,
        };
        SourceOnlyResultOmissionReason reason = Enum.TryParse(omission.Reason, false, out SourceOnlyResultOmissionReason r) && Enum.IsDefined(r) && r.ToString() == omission.Reason
            ? r : SourceOnlyResultOmissionReason.UnrecognizedOmission;
        EntityId<StandardShortFormPlanIdTag>? renditionId = null;
        if (role is SourceOnlyResultOmissionRole.VerticalCallToAction or SourceOnlyResultOmissionRole.VerticalSubtitles)
        {
            string prefix = role == SourceOnlyResultOmissionRole.VerticalCallToAction ? "vertical-call-to-action-" : "vertical-burn-in-";
            if (!omission.Role.StartsWith(prefix, StringComparison.Ordinal) ||
                !EntityId<StandardShortFormPlanIdTag>.TryParse(omission.Role[prefix.Length..], out renditionId) ||
                renditionId.ToString() != omission.Role[prefix.Length..])
            { role = SourceOnlyResultOmissionRole.Other; renditionId = null; reason = SourceOnlyResultOmissionReason.UnrecognizedOmission; }
        }
        return new(role, reason, renditionId);
    }
    internal static bool Recoverable(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}