using System.Collections.ObjectModel;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Projects;

public sealed partial class SourceOnlyPipeline
{
    internal const int MaximumSubtitleProjectionParentDepth = 16;
    private const string SubtitleProjectionConflictCode = "source_only.subtitle_projection_conflict";

    /// <summary>
    /// The sole production projection lane. Reads only the accepted parent's exact
    /// result-bearing execution and immutable journal prefixes; never dispatches callbacks,
    /// selects a current editor head, changes the ASR, or writes a journal entry.
    /// </summary>
    internal async Task<SourceOnlyGeneratedRuntimeEvidence> ApplyCapturedSubtitleEditsAsync(
        StoredSourceOnlyExecution execution, SourceOnlyGeneratedRuntimeEvidence baseGenerated,
        CancellationToken token, int depth = 0)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        try
        {
            ValidateSubtitleProjectionDepth(depth);
            ValidateRuntimeComposition(execution, baseGenerated.Preparation, baseGenerated.Import);
            ValidateCheckpointLineage(execution, baseGenerated.Preparation.Child);
            if (baseGenerated.Versions.Values.Any(item => item.SubtitleProjection is not null
                || item.SubtitleProjectionSource is not null || item.Build.Subtitles.Projection is not null))
            {
                throw SubtitleProjectionConflict();
            }
            IReadOnlyList<StoredSourceOnlyExecution> history = executions.ListForProject(
                execution.Request.ProjectId, SourceOnlyExecutionStore.MaximumListResults);
            if (!RequiresSubtitleProjection(execution, history)) { return baseGenerated; }
            if (depth == MaximumSubtitleProjectionParentDepth) { throw SubtitleProjectionConflict(); }

            StoredSourceOnlyExecution previous = FindSubtitleProjectionParent(execution, history);
            StoredJob previousJob = RequireJob(previous.Request.RootJobId);
            SourceOnlyResult previousResult = ValidateProjectionParentJob(previous, previousJob);
            StoredJobAggregate previousAggregate = jobs.GetJobAggregate(previous.Request.RootJobId) ?? throw SubtitleProjectionConflict();
            if (previousJob.Job.UpdatedAt > execution.Request.CreatedAt || previousAggregate.Revision != previousJob.Revision
                || previousAggregate.Attempts.Count != previousJob.Job.CurrentAttemptNumber
                || previousAggregate.Attempts[^1].State != previousJob.Job.State) { throw SubtitleProjectionConflict(); }
            SourceOnlyResultGenerationCheckpoint generation = await SourceOnlyResultGenerationCheckpoint.ReadAsync(
                artifacts, previous, token).ConfigureAwait(false) ?? throw SubtitleProjectionConflict();
            generation.ValidateSet(previousResult);
            StandardCompositionPreparation previousPreparation = await ReadCompositionCheckpointAsync(previous, token).ConfigureAwait(false)
                ?? throw SubtitleProjectionConflict();
            if (previousPreparation.Child.Id != execution.Request.ParentVersionId
                || previousPreparation.Child.ContentDigest != execution.Request.ParentDigest)
                throw SubtitleProjectionConflict();

            // Use the shared ingest's persisted codec, not an ingest/analysis producer.
            // No Read/RestoreRuntime call here: those select the latest owner and invoke
            // callbacks, which would recurse during a generated-checkpoint callback.
            if (options is null) { throw SubtitleProjectionConflict(); }
            IngestWorkflowRunResult ingest = await CreateIngestWorkflow(new ProcessRunner(), new BinaryVerifier())
                .ReadCompletedAsync(previous.Request.IngestJobId, token).ConfigureAwait(false);
            var source = ReadAcceptedSource(previous);
            ValidateIngest(previous, source, ingest);
            await ValidateRightsCheckpointAsync(previous, source, token).ConfigureAwait(false);
            AnalysisState analysis = await ReadAnalysisAsync(previous, ingest, token).ConfigureAwait(false)
                ?? throw SubtitleProjectionConflict();
            ValidateRuntimeComposition(previous, previousPreparation, analysis.Import);
            _ = await ReadAnalysisEvidenceAsync(previousPreparation.Plan.LongForm.Project, analysis.Import, previous, token).ConfigureAwait(false);
            SourceOnlyGeneratedRuntimeEvidence previousGenerated = await ReadGeneratedRuntimeEvidenceAsync(
                previous, previousPreparation, analysis.Import, token, depth + 1).ConfigureAwait(false)
                ?? throw SubtitleProjectionConflict();
            await ValidateSubtitleWarningsAsync(previous, previousPreparation, previousGenerated, token).ConfigureAwait(false);

            var heads = (execution.Request.SubtitleEdits ?? []).ToDictionary(head => head.RootVersionId);
            if (heads.Keys.Any(root => !previousGenerated.Versions.ContainsKey(root))) { throw SubtitleProjectionConflict(); }
            var captures = new List<SubtitleEditCapture>();
            foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in previousGenerated.Versions.Values)
            {
                token.ThrowIfCancellationRequested();
                heads.TryGetValue(item.Version.Id, out SubtitleEditHeadReference? head);
                if (head is null && item.SubtitleProjection is null) { continue; }
                StandardShortFormPlan? shortForm = GeneratedShortForm(previousGenerated, item.Version.Id);
                var service = new DurableSubtitleEditService(new SubtitleEditStore(options.DatabasePath));
                service.RegisterGenerated(SourceOnlyApplicationRegistrations.CreateLayout(previousGenerated, item, shortForm),
                    SourceOnlyApplicationRegistrations.GeneratedKey(previousGenerated, item), token,
                    makeDefault: false, projection: item.SubtitleProjection);
                captures.Add(service.CapturePrefix(execution.Request.ProjectId, item.Version.Id,
                    head?.Sequence ?? 0, head?.RecordDigest, token));
            }
            return ProjectCapturedSubtitleEdits(execution, baseGenerated, previous, previousGenerated, captures, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw SubtitleProjectionConflict(); }
    }

    /// <summary>
    /// Only accepted branch heads authorize projection. Follow immutable owner links,
    /// not current journal heads, result JSON or recursively reopened media evidence.
    /// </summary>
    internal static bool RequiresSubtitleProjection(StoredSourceOnlyExecution execution,
        IReadOnlyList<StoredSourceOnlyExecution> history)
    {
        if (history.Count >= SourceOnlyExecutionStore.MaximumListResults) { throw SubtitleProjectionConflict(); }
        var seen = new HashSet<EntityId<SourceOnlyExecutionIdTag>>();
        StoredSourceOnlyExecution current = execution;
        bool required = false;
        while (true)
        {
            if (seen.Count == SourceOnlyExecutionStore.MaximumVersionLineageLength
                || !seen.Add(current.Request.ExecutionId)
                || (current.Request.ParentVersionId is null) != (current.Request.ParentDigest is null))
                throw SubtitleProjectionConflict();
            if (current.Request.ParentVersionId is null)
            {
                if (current.Request.SubtitleEdits is { Count: > 0 }) { throw SubtitleProjectionConflict(); }
                return required;
            }
            required |= current.Request.SubtitleEdits is { Count: > 0 };
            // Resolve by version ID first: a recorded owner with a wrong digest or
            // project must not be mistaken for an ownerless legacy composition.
            StoredSourceOnlyExecution[] owners = history.Where(item => item.VersionId == current.Request.ParentVersionId).ToArray();
            if (owners.Length == 0) { return required; }
            if (owners.Length != 1) { throw SubtitleProjectionConflict(); }
            StoredSourceOnlyExecution previous = owners[0];
            if (previous.VersionDigest != current.Request.ParentDigest
                || seen.Contains(previous.Request.ExecutionId)
                || previous.Request.RootJobId == current.Request.RootJobId
                || previous.Request.ProjectId != current.Request.ProjectId || previous.Request.DraftId != current.Request.DraftId
                || previous.Request.IngestJobId != current.Request.IngestJobId
                || previous.Request.SourceCandidateId != current.Request.SourceCandidateId
                || previous.Request.CreatedAt >= current.Request.CreatedAt) { throw SubtitleProjectionConflict(); }
            current = previous;
        }
    }

    // A phase/header alone is not generated-input proof. Callers must additionally
    // read both generation-evidence and generated-runtime from validated actual files.
    internal static SourceOnlyResult ValidateProjectionParentJob(StoredSourceOnlyExecution previous, StoredJob job)
    {
        try
        {
            JobState state = previous.Phase switch
            {
                SourceOnlyExecutionPhase.Completed => JobState.Succeeded,
                SourceOnlyExecutionPhase.Failed => JobState.Failed,
                _ => throw SubtitleProjectionConflict(),
            };
            if (job.Job.Id != previous.Request.RootJobId || job.Job.ProjectId != previous.Request.ProjectId
                || job.Job.Kind != SourceOnlyExecutionStore.RootJobKind || job.Job.State != state
                || job.Job.CurrentAttemptNumber <= 0 || job.Job.CreatedAt != previous.Request.CreatedAt
                || job.Job.UpdatedAt < job.Job.CreatedAt || previous.VersionId is null || previous.VersionDigest is null)
                throw SubtitleProjectionConflict();
            SourceOnlyResult result = SourceOnlyResult.ParseCanonical(previous.ResultJson ?? throw SubtitleProjectionConflict(),
                previous.ResultDigest ?? throw SubtitleProjectionConflict());
            result.ValidateBinding(previous);
            EnsureManuallyResolved(previous);
            // Measured QC rule failures retain an evaluation with no FailureCode;
            // QC exceptions carry QualityControlFailed instead. Neither grants delivery.
            if (result.GenerationDisposition is not (StandardArtifactGenerationDisposition.Completed or StandardArtifactGenerationDisposition.GeneratedWithOmissions)
                || result.Artifacts.Count == 0 || !result.Artifacts.Any(item => item.Role == SourceOnlyResultRole.LandscapeClean)
                || result.Failures.Any(item => item.Code != SourceOnlyResultFailureCode.QualityControlFailed)
                || result.Artifacts.Any(item => item.FailureCode is not (null or SourceOnlyResultFailureCode.QualityControlFailed)
                    || item.Evaluation is null && item.FailureCode != SourceOnlyResultFailureCode.QualityControlFailed))
                throw SubtitleProjectionConflict();
            return result;
        }
        catch (Exception) { throw SubtitleProjectionConflict(); }
    }

    // Pure selection boundary: a newer failed/successful execution with another
    // result is not a replacement for a missing, corrupt or ambiguous fixed parent.
    // Selection is not generated-input proof or QC/delivery permission: callers
    // must still validate the exact owner's jobs and immutable checkpoints.
    internal static StoredSourceOnlyExecution FindSubtitleProjectionParent(StoredSourceOnlyExecution execution,
        IReadOnlyList<StoredSourceOnlyExecution> history)
    {
        if (execution.Request.ParentVersionId is null || execution.Request.ParentDigest is null
            || history.Count >= SourceOnlyExecutionStore.MaximumListResults) { throw SubtitleProjectionConflict(); }
        // Resolve ownership before checking eligibility. Filtering failed/corrupt
        // owners out first could turn an ambiguous parent into a success fallback.
        StoredSourceOnlyExecution[] matches = history.Where(item => item.VersionId == execution.Request.ParentVersionId
            && item.VersionDigest == execution.Request.ParentDigest).ToArray();
        if (matches.Length != 1) { throw SubtitleProjectionConflict(); }
        StoredSourceOnlyExecution previous = matches[0];
        if (previous.Phase is not (SourceOnlyExecutionPhase.Completed or SourceOnlyExecutionPhase.Failed)
            || previous.ResultJson is null || previous.ResultDigest is null
            || previous.Request.ExecutionId == execution.Request.ExecutionId
            || previous.Request.RootJobId == execution.Request.RootJobId
            || previous.Request.ProjectId != execution.Request.ProjectId || previous.Request.DraftId != execution.Request.DraftId
            || previous.Request.SourceCandidateId != execution.Request.SourceCandidateId
            || previous.Request.IngestJobId != execution.Request.IngestJobId
            || previous.Request.CreatedAt >= execution.Request.CreatedAt) { throw SubtitleProjectionConflict(); }
        try
        {
            SourceOnlyResult.ParseCanonical(previous.ResultJson, previous.ResultDigest).ValidateBinding(previous);
            EnsureManuallyResolved(previous);
        }
        catch (Exception) { throw SubtitleProjectionConflict(); }
        return previous;
    }

    /// <summary>Pure integration of verified native captures into one immutable generator input set.</summary>
    internal static SourceOnlyGeneratedRuntimeEvidence ProjectCapturedSubtitleEdits(StoredSourceOnlyExecution execution,
        SourceOnlyGeneratedRuntimeEvidence baseGenerated, StoredSourceOnlyExecution previous,
        SourceOnlyGeneratedRuntimeEvidence previousGenerated, IReadOnlyList<SubtitleEditCapture> captures, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            _ = FindSubtitleProjectionParent(execution, [previous]);
            ValidateRuntimeComposition(execution, baseGenerated.Preparation, baseGenerated.Import);
            ValidateRuntimeComposition(previous, previousGenerated.Preparation, previousGenerated.Import);
            if (previousGenerated.Preparation.Child.Id != execution.Request.ParentVersionId
                || previousGenerated.Preparation.Child.ContentDigest != execution.Request.ParentDigest
                || previousGenerated.CreatedAt > execution.Request.CreatedAt
                || baseGenerated.CreatedAt < execution.Request.CreatedAt
                || previousGenerated.Import.AudioTimeline.CanonicalJson != baseGenerated.Import.AudioTimeline.CanonicalJson
                || TranscriptSnapshotJson.WriteRevision(previousGenerated.Import.Revision) != TranscriptSnapshotJson.WriteRevision(baseGenerated.Import.Revision)
                || baseGenerated.Versions.Values.Any(item => item.SubtitleProjection is not null
                    || item.SubtitleProjectionSource is not null || item.Build.Subtitles.Projection is not null))
                throw SubtitleProjectionConflict();

            var heads = (execution.Request.SubtitleEdits ?? []).ToDictionary(head => head.RootVersionId);
            var byRoot = captures.ToDictionary(capture => capture.RootVersionId);
            var required = previousGenerated.Versions.Values.Where(item => heads.ContainsKey(item.Version.Id)
                || item.SubtitleProjection is not null).Select(item => item.Version.Id).ToHashSet();
            if (heads.Keys.Any(root => !previousGenerated.Versions.ContainsKey(root)) || !required.SetEquals(byRoot.Keys))
                throw SubtitleProjectionConflict();
            var replacements = baseGenerated.Versions.ToDictionary(pair => pair.Key, pair => pair.Value);
            var assigned = new HashSet<EntityId<CompositionVersionIdTag>>();
            foreach (SubtitleEditCapture capture in captures)
            {
                token.ThrowIfCancellationRequested();
                SourceOnlyGeneratedSubtitleRuntimeEvidence original = previousGenerated.Versions[capture.RootVersionId];
                StandardShortFormPlan? previousShort = GeneratedShortForm(previousGenerated, original.Version.Id);
                heads.TryGetValue(capture.RootVersionId, out SubtitleEditHeadReference? head);
                if (capture.ProjectId != execution.Request.ProjectId || capture.Sequence != (head?.Sequence ?? 0)
                    || capture.Edits.LastOrDefault()?.RecordDigest != head?.RecordDigest
                    || capture.Edits.Any(row => row.OccurredAt > execution.Request.CreatedAt)
                    || capture.Head.CompositionVersion.CreatedAt > execution.Request.CreatedAt
                    || capture.GeneratedInputDigest != SourceOnlyApplicationRegistrations.GeneratedKey(previousGenerated, original)
                    || capture.InitialSnapshotDigest != DurableSubtitleEditService.ComputeSnapshotDigest(
                        SourceOnlyApplicationRegistrations.CreateLayout(previousGenerated, original, previousShort))
                    || capture.InitialProjection?.ContentDigest != original.SubtitleProjection?.ContentDigest)
                    throw SubtitleProjectionConflict();

                EntityId<CompositionVersionIdTag>[] targets = previousShort is null ? [baseGenerated.Preparation.Child.Id]
                    : baseGenerated.Preparation.Plan.ShortForms.Where(item => SameSubtitleSourceSpans(previousShort.SourceSpans, item.SourceSpans))
                        .Select(item => StandardRenderGraphFactory.CreateShortVersion(baseGenerated.Preparation, item.Id).Id).ToArray();
                bool manual = capture.Sequence > 0 || capture.InitialProjection is { ProjectedCueCount: > 0 };
                if (targets.Length != 1)
                {
                    if (manual) { throw SubtitleProjectionConflict(); }
                    continue; // Only a proof with no manual fields may have no successor.
                }
                if (!assigned.Add(targets[0])) { throw SubtitleProjectionConflict(); }
                SourceOnlyGeneratedSubtitleRuntimeEvidence target = replacements[targets[0]];
                // The catalog requires exact cut coverage, not every unused plan
                // span. Resolve full spans from BOTH plans, never cue-sized subsets.
                var sourceCuts = original.Timeline.Entries.Select(item => item.Origin.SourceSpanId).ToHashSet();
                var targetCuts = target.Timeline.Entries.Select(item => item.Origin.SourceSpanId).ToHashSet();
                SubtitleProjectionCutCatalog catalog = SubtitleProjectionCutCatalog.Create(
                    previousGenerated.Preparation.Plan.LongForm.Provenance.SourceSpans
                        .Concat(previousGenerated.Preparation.Plan.ShortForms.SelectMany(item => item.SourceSpans))
                        .DistinctBy(item => item.Id).Where(item => sourceCuts.Contains(item.Id)).ToArray(),
                    baseGenerated.Preparation.Plan.LongForm.Provenance.SourceSpans
                        .Concat(baseGenerated.Preparation.Plan.ShortForms.SelectMany(item => item.SourceSpans))
                        .DistinctBy(item => item.Id).Where(item => targetCuts.Contains(item.Id)).ToArray());
                SubtitleEditProjectionResult result = SubtitleEditProjection.Apply(capture, target,
                    execution.Request.ActorSid, baseGenerated.CreatedAt, token, cutCatalog: catalog);
                StandardSubtitleInput input = result.Output.Build.Subtitles.WithProjection(result.Evidence, result.Output);
                SourceOnlyGeneratedSubtitleRuntimeEvidence output = result.Output with
                {
                    Build = new StandardSubtitleBuildResult(input, result.Output.Build.Warnings,
                        result.Output.Build.SegmentationRuleVersion, result.Output.Build.SettingsDigest),
                    SubtitleProjection = result.Evidence,
                    SubtitleProjectionSource = new(previous.Request.ExecutionId, capture.RootVersionId, capture.Sequence),
                };
                result.Evidence.ValidateOutput(output, token);
                _ = input.ValidateProjection(output.Version, output.Timeline, output.Layout, output.Profile, output.Build.SettingsDigest);
                replacements[output.Version.Id] = output;
            }
            if (assigned.Count == 0) { return baseGenerated; }
            token.ThrowIfCancellationRequested();
            return baseGenerated with
            {
                Versions = new ReadOnlyDictionary<EntityId<CompositionVersionIdTag>, SourceOnlyGeneratedSubtitleRuntimeEvidence>(replacements),
                LandscapeSubtitles = replacements[baseGenerated.Preparation.Child.Id].Build,
                ShortForms = Array.AsReadOnly(baseGenerated.ShortForms.Select(item => new StandardShortArtifactInput(item.ShortFormId,
                    replacements[StandardRenderGraphFactory.CreateShortVersion(baseGenerated.Preparation, item.ShortFormId).Id].Build.Subtitles)).ToArray()),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw SubtitleProjectionConflict(); }
    }

    internal static bool SameSubtitleSourceSpans(IReadOnlyList<SourceSpan> previous, IReadOnlyList<SourceSpan> current) =>
        previous.Count == current.Count && previous.Zip(current).All(pair => pair.First.AssetId == pair.Second.AssetId
            && pair.First.SourceDigest == pair.Second.SourceDigest && pair.First.StreamId == pair.Second.StreamId
            && pair.First.TranscriptRevisionId == pair.Second.TranscriptRevisionId && pair.First.Range == pair.Second.Range
            && pair.First.TranscriptTokenIds.SequenceEqual(pair.Second.TranscriptTokenIds));

    private static StandardShortFormPlan? GeneratedShortForm(SourceOnlyGeneratedRuntimeEvidence evidence,
        EntityId<CompositionVersionIdTag> versionId) => versionId == evidence.Preparation.Child.Id ? null
        : evidence.Preparation.Plan.ShortForms.Single(item => StandardRenderGraphFactory.CreateShortVersion(evidence.Preparation, item.Id).Id == versionId);

    private static object[] WriteSubtitleProjections(StoredSourceOnlyExecution execution, SourceOnlyGeneratedRuntimeEvidence evidence)
    {
        var result = new List<object>();
        var roots = new HashSet<EntityId<CompositionVersionIdTag>>();
        var heads = (execution.Request.SubtitleEdits ?? []).ToDictionary(head => head.RootVersionId);
        foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in evidence.Versions.Values.OrderBy(item => item.Version.Id.ToString(), StringComparer.Ordinal))
        {
            if (item.SubtitleProjection is not { } proof)
            {
                if (item.SubtitleProjectionSource is not null || item.Build.Subtitles.Projection is not null
                    || item.Histories.Any(history => history.Revisions[0].Reason == SubtitleEditProjection.MaterializationReason))
                    throw SubtitleProjectionConflict();
                continue;
            }
            SourceOnlySubtitleProjectionSource source = item.SubtitleProjectionSource ?? throw SubtitleProjectionConflict();
            if (!ReferenceEquals(item.Build.Subtitles.Projection, proof) || !roots.Add(source.RootVersionId)
                || execution.Request.ParentVersionId is null || source.ExecutionId == execution.Request.ExecutionId
                || source.CapturedSequence != (heads.GetValueOrDefault(source.RootVersionId)?.Sequence ?? 0))
                throw SubtitleProjectionConflict();
            proof.ValidateOutput(item);
            // These values come from a native, freshly computed proof, not checkpoint
            // input. Bind the coordinator's source annotation to that proof as well.
            using JsonDocument document = JsonDocument.Parse(proof.CanonicalJson, SubtitleEditProjection.EvidenceDocumentOptions);
            JsonElement captured = document.RootElement.GetProperty("source");
            if (SourceOnlyResultEvidenceStore.NativeId(captured, "RootVersionId") != source.RootVersionId.ToString()
                || SourceOnlyResultEvidenceStore.NativeId(captured, "ProjectId") != execution.Request.ProjectId.ToString()
                || SourceOnlyResultEvidenceStore.Integer(captured.GetProperty("Sequence")) != source.CapturedSequence)
                throw SubtitleProjectionConflict();
            JsonElement prefix = captured.GetProperty("recordPrefix");
            if (heads.TryGetValue(source.RootVersionId, out SubtitleEditHeadReference? head)
                && (prefix.GetArrayLength() != head.Sequence || SourceOnlyResultEvidenceStore.NativeDigest(prefix[head.Sequence - 1], "RecordDigest") != head.RecordDigest.Hex))
                throw SubtitleProjectionConflict();
            StandardShortFormPlan? shortForm = GeneratedShortForm(evidence, item.Version.Id);
            StandardSubtitleInput input = shortForm is null ? evidence.LandscapeSubtitles.Subtitles
                : evidence.ShortForms.Single(value => value.ShortFormId == shortForm.Id).Subtitles;
            if (!ReferenceEquals(input, item.Build.Subtitles)) { throw SubtitleProjectionConflict(); }
            StandardRenderGraphFactory.ValidateSubtitleProjection(evidence.Preparation, input, shortForm?.Id);
            result.Add(new
            {
                versionId = item.Version.Id.ToString(),
                canonicalJson = proof.CanonicalJson,
                digest = proof.ContentDigest.Hex,
                sourceRootVersionId = source.RootVersionId.ToString(),
                sourceExecutionId = source.ExecutionId.ToString(),
                capturedSequence = source.CapturedSequence,
            });
        }
        if (heads.Keys.Any(root => !roots.Contains(root))) { throw SubtitleProjectionConflict(); }
        return result.ToArray();
    }

    private static void ValidateSubtitleProjectionDepth(int depth)
    {
        if (depth is < 0 or > MaximumSubtitleProjectionParentDepth) { throw SubtitleProjectionConflict(); }
    }

    private static PipelineFailure SubtitleProjectionConflict() =>
        new(SourceOnlyExecutionPhase.Failed, SubtitleProjectionConflictCode);
}