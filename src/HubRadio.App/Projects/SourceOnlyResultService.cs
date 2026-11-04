using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Projects;

public sealed record SourceOnlyResultLoadResult(SourceOnlyResult Result, SourceOnlyResultValidation Validation);

/// <summary>
/// One past composition version of a project. No file path, storage reference or
/// artifact content crosses this boundary; restoring is a separate operation.
/// </summary>
public sealed record SourceOnlyVersionHistoryEntry(
    EntityId<CompositionVersionIdTag> VersionId, ContentDigest VersionDigest,
    EntityId<CompositionVersionIdTag>? ParentVersionId,
    EntityId<SourceOnlyExecutionIdTag> ExecutionId, SourceOnlyExecutionPhase Phase,
    bool HasResult, bool IsCurrent,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<DefaultSettingSelection>? Settings = null,
    IReadOnlyList<SourceOnlyVersionOutput>? Outputs = null);

/// <summary>FR-055: a viewable output (video or image) of one listed version; IDs and digest only.</summary>
public sealed record SourceOnlyVersionOutput(
    SourceOnlyResultRole Role, EntityId<ArtifactIdTag> ArtifactId, ContentDigest ContentDigest, string MediaType);

/// <summary>
/// Reopens durable results using store-owned IDs only. Validation is a snapshot, NOT
/// a delivery authorization or held file lease. Delivery must revalidate the whole
/// set immediately before its own trusted side effects/receipt transaction.
/// </summary>
public sealed class SourceOnlyResultService
{
    private readonly ArtifactStore _artifacts;
    private readonly AutomatedEvaluationStore _evaluations;
    private readonly QuickStartWorkflowStore _workflows;
    private readonly ProjectStore _projects;

    public SourceOnlyResultService(ArtifactStore artifacts, AutomatedEvaluationStore evaluations,
        QuickStartWorkflowStore workflows, ProjectStore projects)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    }

    /// <summary>
    /// Past composition versions of a project, newest execution first. Reading
    /// this never mutates a version, a working copy or an artifact. It reads the
    /// version rows directly instead of reconstructing evidence lineages, which
    /// a list surface does not need.
    /// </summary>
    public IReadOnlyList<SourceOnlyVersionHistoryEntry> ListVersionHistory(
        EntityId<ProjectIdTag> projectId, SourceOnlyExecutionStore executions, int maximumCount = 100)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(executions);
        // The head of the working copy is the version a restore is measured
        // against, not merely the newest execution's version.
        EntityId<CompositionVersionIdTag>? head = new VersionService(_projects)
            .GetCurrentState(projectId)?.WorkingCopy.BaseVersionId;
        var seen = new HashSet<EntityId<CompositionVersionIdTag>>();
        var entries = new List<SourceOnlyVersionHistoryEntry>();
        foreach (StoredSourceOnlyExecution execution in executions.ListForProject(projectId, maximumCount))
        {
            if (execution.VersionId is not EntityId<CompositionVersionIdTag> versionId ||
                execution.VersionDigest is not ContentDigest versionDigest || !seen.Add(versionId))
            {
                continue;
            }

            StoredCompositionVersion? version = _projects.GetCompositionVersion(versionId);
            // A missing or disagreeing row means one of two durable records was
            // altered. Reporting a shorter list would assert a false history.
            if (version is null || version.ProjectId != projectId || version.ContentDigest != versionDigest)
            {
                throw new SourceOnlyResultException(SourceOnlyResultFailureCode.StoredEvidenceInvalid);
            }

            entries.Add(new SourceOnlyVersionHistoryEntry(versionId, versionDigest, version.ParentVersionId,
                execution.Request.ExecutionId, execution.Phase, execution.ResultJson is not null, versionId == head,
                execution.Request.CreatedAt, execution.Request.SettingsSnapshot.Values, ReadOutputs(execution)));
        }

        return KeepDifferingSettings(entries);
    }

    /// <summary>
    /// FR-055: keeps, in every entry, only the settings whose value is not the same in all entries (or that
    /// some entry lacks), ordered by key, so the list states exactly what changed from one video to the next.
    /// </summary>
    internal static IReadOnlyList<SourceOnlyVersionHistoryEntry> KeepDifferingSettings(
        IReadOnlyList<SourceOnlyVersionHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var differing = entries.SelectMany(static entry => entry.Settings ?? []).GroupBy(static item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Select(static item => item.Value).Distinct().Count() > 1 || group.Count() != entries.Count)
            .Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        return entries.Select(entry => entry with
        {
            Settings = Array.AsReadOnly((entry.Settings ?? []).Where(item => differing.Contains(item.Key))
                .OrderBy(static item => item.Key, StringComparer.Ordinal).ToArray()),
        }).ToArray();
    }

    private static IReadOnlyList<SourceOnlyVersionOutput> ReadOutputs(StoredSourceOnlyExecution execution)
    {
        if (execution.ResultJson is null || execution.ResultDigest is null)
        {
            return [];
        }

        try
        {
            SourceOnlyResult result = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
            return Array.AsReadOnly(result.Artifacts.Where(static item => item.FailureCode is null
                    && item.Artifact.Kind == ArtifactKind.Render && item.MediaType is "video/mp4" or "image/png")
                .Select(static item => new SourceOnlyVersionOutput(item.Role, item.ArtifactId, item.ArtifactDigest, item.MediaType))
                .ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A damaged record only loses its previews; the media route revalidates every read anyway.
            return [];
        }
    }

    /// <summary>Invalid canonical/binding data is rejected; missing/stale resources are returned as per-artifact issues.</summary>
    public async Task<SourceOnlyResultLoadResult> LoadAsync(StoredSourceOnlyExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ct.ThrowIfCancellationRequested();
        if (execution.ResultJson is null || execution.ResultDigest is null || execution.VersionId is null || execution.VersionDigest is null)
            throw new SourceOnlyResultException(SourceOnlyResultFailureCode.StoredEvidenceInvalid);
        SourceOnlyResult result = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
        result.ValidateBinding(execution);
        return new(result, await ValidateAsync(result, execution, ct).ConfigureAwait(false));
    }

    public async Task<SourceOnlyResultValidation> ValidateAsync(SourceOnlyResult result, StoredSourceOnlyExecution execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(execution);
        ct.ThrowIfCancellationRequested();
        var failures = new List<SourceOnlyResultFailure>();
        try { result.ValidateBinding(execution); SourceOnlyResultCodec.ValidateShape(result); }
        catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
        { return new([new(SourceOnlyResultFailureCode.BindingMismatch)]); }

        StandardCompositionPreparation? preparation = null;
        QuickStartProjectSnapshot? capturedProject = null;
        try
        {
            preparation = new VersionService(_projects).GetStandardComposition(result.Binding.VersionId);
            SourceOnlyResultCodec.Require(preparation is not null && preparation.Child.ProjectId == result.Binding.ProjectId &&
                preparation.Child.ContentDigest == result.Binding.VersionDigest &&
                preparation.Plan.InputDigests.Single(i => i.Kind == StandardPlanInputKind.SettingsSnapshot).ContentDigest == result.Binding.SettingsSnapshotDigest);
            QuickStartProjectSnapshot current = _workflows.GetCurrentProjectSnapshot(result.Binding.ProjectId);
            capturedProject = current;
            SourceOnlyResultCodec.Require(preparation!.Plan.ProjectStoreState.Revision == current.Revision &&
                preparation.Plan.ProjectStoreState.PayloadDigest == current.CanonicalPayloadDigest);
            foreach (SourceOnlyResultTranscriptReference transcript in result.TranscriptReferences)
                SourceOnlyResultCodec.Require(transcript.RevisionId == preparation.Plan.LongForm.Provenance.TranscriptRevisionId &&
                    transcript.RevisionDigest == preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest &&
                    result.Artifacts.Any(a => a.Source.ArtifactId == transcript.SourceArtifactId));
            if (result.Artifacts.Any(a => a.Evaluation?.Rules.Any(r => r.RuleId == AutomatedEvaluationRuleId.Provenance && r.Status == AutomatedEvaluationStatus.Pass) == true))
                SourceOnlyResultCodec.Require(result.TranscriptReferences.Count == 1);
        }
        catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
        { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)); }

        var evidenceStore = new SourceOnlyResultEvidenceStore(_artifacts);
        SourceOnlyResultGenerationCheckpoint? generationCheckpoint = null;
        try
        {
            generationCheckpoint = await SourceOnlyResultGenerationCheckpoint.ReadAsync(_artifacts, execution, ct).ConfigureAwait(false);
            generationCheckpoint?.ValidateSet(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
        { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)); }
        // Read the complete set before validating any member: a missing proof on
        // an earlier clean/sidecar artifact must not escape a later projected one.
        var renderingInputProofs = new Dictionary<EntityId<ArtifactIdTag>,
            (SourceOnlyNativeEvidence Evidence, SourceOnlyRenderingInputsPayload Payload)>();
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
        {
            SourceOnlyResultEvidenceReference? reference = artifact.Evidence.SingleOrDefault(e => e.Kind == SourceOnlyResultEvidenceKind.RenderingInputs);
            if (reference is null) continue;
            try
            {
                SourceOnlyNativeEvidence evidence = await evidenceStore.ReadAsync(result.Binding, artifact, reference, ct).ConfigureAwait(false);
                SourceOnlyRenderingInputsPayload payload = SourceOnlyResultInputValidation.Read(evidence.Payload);
                SourceOnlyResultCodec.Require(!artifact.IsImage || payload.SubtitleEditProjection is null);
                renderingInputProofs.Add(artifact.ArtifactId, (evidence, payload));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid, artifact.ArtifactId)); }
        }
        bool hasProjectedInputs = renderingInputProofs.Values.Any(p => p.Payload.SchemaVersion == "source-only.rendering-inputs.v3");
        // No host, process, pipeline lock or mutable runtime. The independent reader
        // replays the saved pre-render generated-runtime checkpoint and journal roots.
        // Accepted history cannot be downgraded by stripping every payload's proof.
        // Read once for the entire set, including when no rendering inputs survived.
        bool shouldReadSubtitleEvidence = false;
        SourceOnlyGeneratedRuntimeEvidence? regeneratedRuntime = null;
        try
        {
            var executions = new SourceOnlyExecutionStore(_artifacts.LocalDatabasePath);
            shouldReadSubtitleEvidence = ShouldReadSubtitleEvidence(execution, executions.ListForProject(
                execution.Request.ProjectId, SourceOnlyExecutionStore.MaximumListResults), hasProjectedInputs);
            if (shouldReadSubtitleEvidence)
            {
                regeneratedRuntime = await new SourceOnlySubtitleEvidenceReader(_artifacts, _projects,
                    new DefaultSettingsStore(_artifacts.LocalDatabasePath), executions).ReadAsync(execution, ct, 0).ConfigureAwait(false);
                SourceOnlyResultCodec.Require(regeneratedRuntime is not null && regeneratedRuntime.Versions.Count > 0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
        { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)); }
        foreach (SourceOnlyResultEvidenceReference ancillary in result.AncillaryArtifacts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using ValidatedArtifactReadStream stream = await _artifacts
                    .OpenValidatedReadAsync(ancillary.Artifact.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(stream, ancillary.Artifact, result.Binding.ProjectId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid, ancillary.Artifact.ArtifactId)); }
        }
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using ValidatedArtifactReadStream output = await _artifacts.OpenValidatedReadAsync(artifact.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(output, artifact.Artifact, result.Binding.ProjectId);
                await using ValidatedArtifactReadStream source = await _artifacts.OpenValidatedReadAsync(artifact.Source.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(source, artifact.Source, result.Binding.ProjectId);
                if (preparation is not null)
                {
                    var asset = preparation.Plan.LongForm.Project.Assets.Single(a => a.Id == execution.Request.SourceCandidateId);
                    SourceOnlyResultCodec.Require(source.ContentDigest == asset.ContentDigest && source.ByteSize == asset.ByteSize);
                    CompositionVersion expected = artifact.IsImage ? SourceOnlyResultImageValidation.Expected(preparation, artifact,
                        execution.Request.SettingsSnapshot).Version : artifact.Role.IsShortVideo()
                            ? StandardRenderGraphFactory.CreateShortVersion(preparation, artifact.RenditionId!) : preparation.Child;
                    StoredCompositionVersion? stored = _projects.GetCompositionVersion(artifact.VersionId);
                    if (stored is null) failures.Add(new(SourceOnlyResultFailureCode.VersionNotPersisted, artifact.ArtifactId));
                    else SourceOnlyResultCodec.Require(expected.Id == artifact.VersionId && expected.ContentDigest == artifact.VersionDigest &&
                        stored.ProjectId == result.Binding.ProjectId && stored.CanonicalJson == CompositionSnapshotJson.WriteVersion(expected));
                }
                var proofs = new Dictionary<SourceOnlyResultEvidenceKind, SourceOnlyNativeEvidence>();
                foreach (SourceOnlyResultEvidenceReference reference in artifact.Evidence)
                {
                    if (reference.Kind == SourceOnlyResultEvidenceKind.RenderingInputs)
                    {
                        SourceOnlyResultCodec.Require(renderingInputProofs.TryGetValue(artifact.ArtifactId, out var captured));
                        proofs.Add(reference.Kind, captured.Evidence);
                    }
                    else proofs.Add(reference.Kind, await evidenceStore.ReadAsync(result.Binding, artifact, reference, ct).ConfigureAwait(false));
                }
                if (proofs.TryGetValue(SourceOnlyResultEvidenceKind.Graph, out SourceOnlyNativeEvidence? graph))
                {
                    SourceOnlyResultEvidenceStore.ValidateGraph(graph.Payload, artifact);
                    if (preparation is not null)
                    {
                        RenderProfile expectedProfile = artifact.IsImage ? SourceOnlyResultImageValidation.Expected(preparation, artifact,
                            execution.Request.SettingsSnapshot).Profile : StandardRenderGraphFactory.GetProfile(preparation.Plan,
                            artifact.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn, artifact.RenditionId);
                        SourceOnlyResultCodec.Require(SourceOnlyResultEvidenceStore.NativeId(graph.Payload, "ProfileId") == expectedProfile.Id.ToString() &&
                            SourceOnlyResultEvidenceStore.NativeDigest(graph.Payload, "ProfileDigest") == expectedProfile.ProfileDigest.Hex &&
                            SourceOnlyResultEvidenceStore.NativeId(graph.Payload, "LayoutSpecId") == expectedProfile.LayoutSpecId.ToString());
                    }
                }
                if (proofs.TryGetValue(SourceOnlyResultEvidenceKind.Manifest, out SourceOnlyNativeEvidence? manifest))
                    SourceOnlyResultEvidenceStore.ValidateManifest(manifest.Payload, result.Binding, artifact, output.RegisteredArtifact);
                SourceOnlyRenderingInputsPayload? renderingInputs = renderingInputProofs.TryGetValue(artifact.ArtifactId, out var inputs)
                    ? inputs.Payload : null;
                SourceOnlyGeneratedSubtitleRuntimeEvidence? regeneratedSubtitles = null;
                if (shouldReadSubtitleEvidence && !artifact.IsImage)
                {
                    SourceOnlyResultCodec.Require(regeneratedRuntime is not null && preparation is not null &&
                        regeneratedRuntime.Preparation.Plan.PlanDigest == preparation.Plan.PlanDigest &&
                        CompositionSnapshotJson.WriteVersion(regeneratedRuntime.Preparation.Child) == CompositionSnapshotJson.WriteVersion(preparation.Child) &&
                        regeneratedRuntime.Versions.TryGetValue(artifact.VersionId, out regeneratedSubtitles));
                }
                // An unrelated unprojected version may legitimately match null;
                // a required proof may never disappear with the whole inputs payload.
                SourceOnlyResultCodec.Require((renderingInputs?.SubtitleEditProjection is null) == (regeneratedSubtitles?.SubtitleProjection is null));
                if (renderingInputs is not null)
                {
                    if (artifact.Evaluation is null && renderingInputs.SubtitleEditProjection is not null)
                    {
                        SourceOnlyResultCodec.Require(graph is not null && manifest is not null && preparation is not null);
                        _ = SourceOnlyResultInputValidation.Validate(renderingInputs, artifact, preparation, execution.Request.SettingsSnapshot,
                            graph.Payload, manifest.Payload, regeneratedSubtitles);
                    }
                }
                if (artifact.Evaluation is AutomatedEvaluationRun run)
                {
                    AutomatedEvaluationRun? stored = _evaluations.Get(run.EvaluationId);
                    SourceOnlyResultCodec.Require(stored is not null && stored.CanonicalJson == run.CanonicalJson && stored.CanonicalDigest == run.CanonicalDigest);
                    SourceOnlyNativeEvidence qc = proofs[SourceOnlyResultEvidenceKind.QualityControl];
                    SourceOnlyResultCodec.Require(renderingInputs is not null);
                    SourceOnlyResultCodec.Require(renderingInputs.GenerationCheckpoint == (generationCheckpoint is null
                        ? null : SourceOnlyResultCodec.ReferenceDto(generationCheckpoint.Artifact)));
                    if (graph is not null && manifest is not null)
                        generationCheckpoint?.ValidateArtifact(artifact, graph.Payload, manifest.Payload, renderingInputs);
                    ValidateQc(result, execution, artifact, qc.Payload, graph?.Payload, manifest?.Payload, renderingInputs, preparation, regeneratedSubtitles);
                    if (artifact.IsImage && artifact.FailureCode is null)
                        await SourceOnlyResultImageValidation.ValidatePngAsync(_artifacts, artifact, result.Binding.ProjectId, ct).ConfigureAwait(false);
                    ContentDigest independent = SourceOnlyOutputEvaluation.IndependentInputDigest(result.Binding, run.EvaluationId, artifact);
                    if (run.InputDigest != independent)
                    {
                        SourceOnlyResultCodec.Require(artifact.Role == SourceOnlyResultRole.LandscapeClean);
                        QuickStartWorkflow? primary = _workflows.Get(new(result.Binding.ProjectId, result.Binding.VersionId, result.Binding.VersionDigest));
                        SourceOnlyResultCodec.Require(primary is not null);
                        ValidateEvaluationHistory(primary, run, artifact);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ArtifactStoreException) { failures.Add(new(SourceOnlyResultFailureCode.ArtifactValidationFailed, artifact.ArtifactId)); }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid, artifact.ArtifactId)); }
        }
        if (result.Artifacts.Any(a => a.Delivery is not null))
        {
            try
            {
                // One shared SET receipt, not the old per-artifact native evidence envelope.
                // Reopen actual copied bytes, exact registrations, independent evaluation
                // identities, base-result digest, and the still-valid project/rights snapshot.
                await SourceOnlyDeliveryReceipt.ValidateAsync(_artifacts, _projects, _workflows,
                    execution, result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.DeliveryNotValidated)); }
        }
        foreach (SourceOnlyResultArtifactReference reconciliation in result.ReconciliationArtifacts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using ValidatedArtifactReadStream actual = await _artifacts.OpenValidatedReadAsync(reconciliation.ArtifactId, ct).ConfigureAwait(false);
                SourceOnlyResultEvidenceStore.RequireSame(actual, reconciliation, result.Binding.ProjectId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.ArtifactValidationFailed, reconciliation.ArtifactId)); }
        }
        if (result.PrimaryWorkflow is SourceOnlyResultWorkflowReference referenceWorkflow)
        {
            try
            {
                QuickStartWorkflow? workflow = _workflows.Get(new(result.Binding.ProjectId, result.Binding.VersionId, result.Binding.VersionDigest));
                SourceOnlyResultCodec.Require(workflow is not null && workflow.Events.Count >= referenceWorkflow.Revision);
                // Saved result is a historical snapshot. SelfReviewed/ChangesRequested/
                // later evaluations append events, not invalidate its immutable prefix.
                QuickStartWorkflow recorded = QuickStartWorkflow.Reduce(workflow.Events.Take(referenceWorkflow.Revision).ToArray());
                SourceOnlyResultArtifact artifact = result.Artifacts.Single(a => a.ArtifactId == referenceWorkflow.ArtifactId);
                SourceOnlyResultCodec.Require(recorded.Revision == referenceWorkflow.Revision && recorded.CanonicalDigest == referenceWorkflow.CanonicalDigest &&
                    recorded.State == referenceWorkflow.State && recorded.EvaluationId == referenceWorkflow.EvaluationId &&
                    recorded.ArtifactId?.ToString() == referenceWorkflow.ArtifactId.ToString() && recorded.ArtifactDigest == artifact.ArtifactDigest &&
                    recorded.EvaluationCanonicalDigest == artifact.Evaluation!.CanonicalDigest && recorded.SettingsSnapshotDigest == result.Binding.SettingsSnapshotDigest &&
                    recorded.Events[^1].Kind == QuickStartWorkflowEventKind.EvaluationCompleted);
                ValidateEvaluationHistory(recorded, artifact.Evaluation, artifact);
            }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.WorkflowConflict, referenceWorkflow.ArtifactId)); }
        }
        if (capturedProject is not null)
        {
            try
            {
                QuickStartProjectSnapshot current = _workflows.GetCurrentProjectSnapshot(result.Binding.ProjectId);
                SourceOnlyResultCodec.Require(current.Revision == capturedProject.Revision && current.CanonicalPayloadDigest == capturedProject.CanonicalPayloadDigest);
            }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e))
            { failures.Add(new(SourceOnlyResultFailureCode.StoredEvidenceInvalid)); }
        }
        return new(failures);
    }

    internal static bool ShouldReadSubtitleEvidence(StoredSourceOnlyExecution execution,
        IReadOnlyList<StoredSourceOnlyExecution> history, bool hasProjectedInputs) =>
        SourceOnlyPipeline.RequiresSubtitleProjection(execution, history) || hasProjectedInputs;

    private static void ValidateQc(SourceOnlyResult result, StoredSourceOnlyExecution execution, SourceOnlyResultArtifact artifact,
        JsonElement payload, JsonElement? graph, JsonElement? manifest, SourceOnlyRenderingInputsPayload renderingInputs,
        StandardCompositionPreparation? preparation, SourceOnlyGeneratedSubtitleRuntimeEvidence? regeneratedSubtitles = null)
    {
        SourceOnlyQcEvidencePayload proof = JsonSerializer.Deserialize<SourceOnlyQcEvidencePayload>(payload, SourceOnlyResultCodec.Options)
            ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(SourceOnlyResultCodec.Write(proof, SourceOnlyResultEvidenceStore.MaximumEvidenceBytes) == HubRadio.Core.Serialization.CanonicalJson.Canonicalize(payload.GetRawText()));
        AutomatedEvaluationRun run = artifact.Evaluation!;
        SourceOnlyResultCodec.Require(proof.RuleSetVersion == SourceOnlyOutputEvaluation.RuleSetVersion && run.RuleSetVersion.ToString() == proof.RuleSetVersion &&
            proof.EvaluationId == run.EvaluationId.ToString() && proof.InputDigest == run.InputDigest.Hex &&
            proof.StartedAt == run.StartedAt.ToString("O", CultureInfo.InvariantCulture) && proof.CompletedAt == run.CompletedAt.ToString("O", CultureInfo.InvariantCulture) &&
            proof.EnvironmentDigest == run.EnvironmentDigest.Hex && proof.ToolVersionsDigest == run.ToolVersionsDigest.Hex &&
            proof.ModelVersionsDigest == run.ModelVersionsDigest?.Hex && proof.TemplateDigest == run.TemplateDigest?.Hex && run.MusicDigest is null &&
            SourceOnlyResultCodec.Write(proof.Measurements) == SourceOnlyResultCodec.Write(SourceOnlyResultCodec.MeasurementsDto(artifact.Measurements)) &&
            proof.QcFailureReason == artifact.QcFailureReason?.ToString() && proof.QcFailureStage == artifact.QcFailureStage?.ToString());
        SourceOnlyResultCodec.Require(proof.RenderingInputsDigest == artifact.Evidence.Single(e =>
            e.Kind == SourceOnlyResultEvidenceKind.RenderingInputs).Artifact.ContentDigest.Hex);
        SourceOnlyResultInputValidation.Same(proof.BindingEvidence, renderingInputs.BindingEvidence);
        // Only a later persistence/workflow failure can be added after the immutable
        // QC evidence was written; captured failures may never be silently cleared.
        SourceOnlyResultCodec.Require(proof.FailureCode == artifact.FailureCode?.ToString() ||
            proof.FailureCode is null && artifact.FailureCode is SourceOnlyResultFailureCode.WorkflowConflict or
                SourceOnlyResultFailureCode.EvaluationPersistenceFailed or SourceOnlyResultFailureCode.Canceled or SourceOnlyResultFailureCode.EvidencePersistenceFailed);
        DefaultSettingsProfile profile = DefaultSettingsProfile.ParseCanonical(HubRadio.Core.Serialization.CanonicalJson.Canonicalize(proof.SettingsProfile.GetRawText()));
        DefaultSettingsSnapshot settings = DefaultSettingsSnapshot.ParseCanonical(HubRadio.Core.Serialization.CanonicalJson.Canonicalize(proof.SettingsSnapshot.GetRawText()), profile);
        if (proof.SettingsValid)
            SourceOnlyResultCodec.Require(settings.CanonicalJson == execution.Request.SettingsSnapshot.CanonicalJson &&
                profile.Id == run.SettingsProfileId && profile.Version == run.SettingsProfileVersion && profile.ContentDigest == run.SettingsProfileDigest);
        SourceOnlyResultCodec.Require(proof.ToolVersionsDigest == ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(
            SourceOnlyResultCodec.Write(new { renderTools = proof.RenderTools, qcTools = proof.QcTools }))).Hex);
        IReadOnlyList<SourceOnlyResultSubtitleMeasurement>? independentlyMeasuredCues = null;
        if (proof.ProvenanceValid || renderingInputs.SubtitleEditProjection is not null || regeneratedSubtitles?.SubtitleProjection is not null)
        {
            SourceOnlyResultCodec.Require(graph is not null && manifest is not null && preparation is not null);
            independentlyMeasuredCues = SourceOnlyResultInputValidation.Validate(renderingInputs, artifact, preparation, settings,
                graph.Value, manifest.Value, regeneratedSubtitles);
            JsonElement b = proof.BindingEvidence;
            SourceOnlyResultCodec.Require(SourceOnlyResultEvidenceStore.NativeId(b, "ArtifactId") == artifact.ArtifactId.ToString() &&
                SourceOnlyResultEvidenceStore.NativeId(b, "SourceArtifactId") == artifact.Source.ArtifactId.ToString() &&
                SourceOnlyResultEvidenceStore.NativeId(b, "VersionId") == artifact.VersionId.ToString() &&
                SourceOnlyResultEvidenceStore.NativeDigest(b, "VersionDigest") == artifact.VersionDigest.Hex &&
                SourceOnlyResultEvidenceStore.NativeDigest(b, "OutputDigest") == artifact.ArtifactDigest.Hex &&
                SourceOnlyResultEvidenceStore.NativeDigest(b, "GraphDigest") == artifact.GraphDigest.Hex &&
                SourceOnlyResultEvidenceStore.NativeDigest(b, "SettingsSnapshotDigest") == result.Binding.SettingsSnapshotDigest.Hex &&
                SourceOnlyResultEvidenceStore.PayloadDigest(b).Hex == SourceOnlyResultEvidenceStore.NativeDigest(manifest!.Value, "ConfigurationDigest") &&
                SourceOnlyResultEvidenceStore.PayloadDigest(proof.RenderTools) == SourceOnlyResultEvidenceStore.PayloadDigest(manifest.Value.GetProperty("Tools")) &&
                SourceOnlyResultEvidenceStore.NativeDigest(manifest.Value, "ApplicationDigest") == proof.EnvironmentDigest &&
                SourceOnlyResultEvidenceStore.NativeDigest(manifest.Value, "TemplateVersionDigest") == proof.TemplateDigest);
            JsonElement models = manifest.Value.GetProperty("Models");
            SourceOnlyResultCodec.Require((models.GetArrayLength() == 0 ? null : SourceOnlyResultEvidenceStore.PayloadDigest(models).Hex) == proof.ModelVersionsDigest);
            string outputId = SourceOnlyResultEvidenceStore.NativeId(graph!.Value, "OutputNodeId");
            JsonElement output = graph.Value.GetProperty("Nodes").EnumerateArray().Single(n => SourceOnlyResultEvidenceStore.NativeId(n, "Id") == outputId);
            SourceOnlyResultTimeDto expectedDuration = ReadNativeTime(output.GetProperty("OutputRange").GetProperty("Duration"));
            SourceOnlyResultCodec.Require(ReadTime(expectedDuration) == ReadTime(proof.ExpectedDuration) &&
                (artifact.Measurements is null || artifact.Measurements.ExpectedDuration == ReadTime(expectedDuration)));
        }
        SourceOnlyResultCodec.Require(proof.Cues.Length <= StandardSubtitleInput.MaximumCueCount);
        SourceOnlyResultSubtitleMeasurement[] cues = proof.Cues.Select(c => new SourceOnlyResultSubtitleMeasurement(SourceOnlyResultCodec.Id<CueIdTag>(c.CueId),
            ReadTime(c.Start), ReadTime(c.Duration), c.CharacterCount, c.MaximumLineCharacters, c.LineCount, c.SafeZoneSatisfied)).ToArray();
        if (artifact.Measurements is not null)
        {
            SourceOnlyResultCodec.Require(SourceOnlyResultCodec.Write(proof.Cues) == SourceOnlyResultCodec.Write(SourceOnlyOutputEvaluation.CuesDto(artifact.Measurements.Cues)));
            SourceOnlyResultCodec.Require(proof.OutputQc is not null && proof.QcTools is not null && graph is not null && manifest is not null &&
                independentlyMeasuredCues is not null);
            SourceOnlyResultRawQc.Validate(proof.OutputQc.Value, artifact, graph.Value, manifest.Value, renderingInputs,
                proof.QcTools.Value, independentlyMeasuredCues);
        }
        else SourceOnlyResultCodec.Require(proof.OutputQc is null);
        if (independentlyMeasuredCues is not null)
            SourceOnlyResultCodec.Require(SourceOnlyResultCodec.Write(proof.Cues) == SourceOnlyResultCodec.Write(SourceOnlyOutputEvaluation.CuesDto(independentlyMeasuredCues)));
        SourceOnlyResultCodec.Require(!proof.ImageVerified || artifact.IsImage && proof.ProvenanceValid && artifact.Measurements is null);
        SourceOnlyResultCodec.Require(!proof.SidecarVerified || !artifact.IsVideo && !artifact.IsImage && proof.ProvenanceValid);
        SourceOnlyResultCodec.Require(ReadTime(proof.FrameRate) == new RationalTime(
            SourceOnlyResultEvidenceStore.Integer(renderingInputs.Profile.GetProperty("FrameRate").GetProperty("Numerator")),
            SourceOnlyResultEvidenceStore.Integer(renderingInputs.Profile.GetProperty("FrameRate").GetProperty("Denominator"))));
        SourceOnlyResultEvidenceReference evidence = artifact.Evidence.Single(e => e.Kind == SourceOnlyResultEvidenceKind.QualityControl);
        IReadOnlyList<AutomatedEvaluationRuleObservation> observations = SourceOnlyResultObservationBuilder.Build(result.Binding, artifact, profile, settings,
            evidence.Artifact.ContentDigest, proof.ProvenanceValid, proof.SettingsValid, proof.SidecarVerified, cues,
            ReadTime(proof.ExpectedDuration), new RationalNumber(proof.FrameRate.Value, proof.FrameRate.Rate), proof.ImageVerified);
        AutomatedEvaluationRun replay = new AutomatedEvaluationEngine().Evaluate(new AutomatedEvaluationRequest(run.EvaluationId, run.JobId,
            run.ProjectId, run.CompositionVersionId, run.CompositionVersionDigest, run.ArtifactId, run.ArtifactDigest,
            run.SettingsProfileId, run.SettingsProfileVersion, run.SettingsProfileDigest, run.SettingsSnapshotDigest, run.RuleSetVersion,
            run.EnvironmentVersion, run.EnvironmentDigest, run.ToolVersionsDigest, run.ModelVersionsDigest, run.TemplateDigest, run.MusicDigest,
            run.StartedAt, run.CompletedAt, run.EvaluatedAt, run.InputDigest, run.OutputDigest, observations));
        SourceOnlyResultCodec.Require(replay.CanonicalJson == run.CanonicalJson);
    }

    private static void ValidateEvaluationHistory(QuickStartWorkflow workflow, AutomatedEvaluationRun run, SourceOnlyResultArtifact artifact)
    {
        QuickStartWorkflowEvent completed = workflow.Events.Single(e => e.Kind == QuickStartWorkflowEventKind.EvaluationCompleted && e.EvaluationId == run.EvaluationId);
        QuickStartWorkflowEvent start = workflow.Events.TakeWhile(e => e.EventId != completed.EventId).Last(e =>
            e.Kind is QuickStartWorkflowEventKind.EvaluationStarted or QuickStartWorkflowEventKind.TechnicalReevaluationStarted);
        SourceOnlyResultCodec.Require(start.CanonicalDigest == run.InputDigest && start.SettingsSnapshotDigest == run.SettingsSnapshotDigest &&
            start.OccurredAt <= run.StartedAt && completed.OccurredAt >= run.CompletedAt &&
            completed.EvaluationCanonicalDigest == run.CanonicalDigest && completed.EvaluationEvaluatedAt == run.EvaluatedAt &&
            completed.EvaluationAggregateStatus == run.AggregateStatus && completed.ArtifactId?.ToString() == artifact.ArtifactId.ToString() &&
            completed.ArtifactDigest == artifact.ArtifactDigest && completed.SettingsSnapshotDigest == run.SettingsSnapshotDigest &&
            completed.ProjectSnapshot?.Revision == start.ProjectSnapshot?.Revision &&
            completed.ProjectSnapshot?.CanonicalPayloadDigest == start.ProjectSnapshot?.CanonicalPayloadDigest);
    }

    private static RationalTime ReadTime(SourceOnlyResultTimeDto value)
    {
        var time = new RationalTime(value.Value, value.Rate);
        SourceOnlyResultCodec.Require(time.Value == value.Value && time.Rate == value.Rate);
        return time;
    }
    private static SourceOnlyResultTimeDto ReadNativeTime(JsonElement value)
    {
        long Read(string name) => long.TryParse(value.GetProperty(name).GetRawText(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out long number) ? number : throw SourceOnlyResultCodec.Invalid();
        return new(Read("Value"), Read("Rate"));
    }
}