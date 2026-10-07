using System.Globalization;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using static HubRadio.App.Projects.SourceOnlyResultCodec;

namespace HubRadio.App.Projects;

/// <summary>
/// Re-derives saved subtitle capabilities from actual immutable inputs. This reader
/// owns no pipeline, lease, process, callback or application defaults, and never
/// persists a derived version, journal entry, job or registration.
/// </summary>
internal sealed class SourceOnlySubtitleEvidenceReader
{
    private const int MaximumEvidenceBytes = 32 * 1024 * 1024;
    private const int MaximumCheckpointBytes = 64 * 1024;
    private readonly ArtifactStore artifacts;
    private readonly ProjectStore projects;
    private readonly DefaultSettingsStore settings;
    private readonly SourceOnlyExecutionStore executions;

    internal SourceOnlySubtitleEvidenceReader(ArtifactStore artifacts, ProjectStore projects,
        DefaultSettingsStore settings, SourceOnlyExecutionStore executions)
    {
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
    }

    internal async Task<SourceOnlyGeneratedRuntimeEvidence?> ReadAsync(StoredSourceOnlyExecution execution,
        CancellationToken token, int depth = 0)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Require(depth is >= 0 and <= SourceOnlyPipeline.MaximumSubtitleProjectionParentDepth);
            StoredSourceOnlyExecution current = ReadCurrent(execution);
            var id = Stable<ArtifactIdTag>(current.Request.ExecutionId.ToString(), "generated-runtime");
            StoredArtifact? saved = artifacts.GetArtifact(id);
            if (saved is null) { return null; } // Legacy absence is not an empty generated proof.

            var observed = new Dictionary<EntityId<ArtifactIdTag>, StoredArtifact>();
            byte[] bytes = await ReadBytesAsync(saved, current.Request.ProjectId, MaximumEvidenceBytes, observed, token).ConfigureAwait(false);
            using JsonDocument document = ParseDocument(Utf8.GetString(bytes), MaximumEvidenceBytes,
                maximumArrayLength: 100000, maximumStringLength: MaximumEvidenceBytes);
            JsonElement root = document.RootElement;
            string schema = Text(root, "schema");
            Require(schema is "source-only.generated-runtime.v1" or "source-only.generated-runtime.v2");
            string[] keys = ["schema", "executionId", "projectId", "settingsDigest", "productionVersionId", "productionVersionDigest",
                "planDigest", "transcriptRevisionId", "transcriptDigest", "createdAt", "font", "builderRule", "projectionRule", "versions", "shortForms"];
            Shape(root, schema == "source-only.generated-runtime.v2" ? [.. keys, "projections"] : keys);
            Require(Text(root, "executionId") == current.Request.ExecutionId.ToString()
                && Text(root, "projectId") == current.Request.ProjectId.ToString()
                && Digest(Text(root, "settingsDigest")) == current.Request.SettingsSnapshot.ContentDigest);
            Require(root.GetProperty("versions").ValueKind == JsonValueKind.Array
                && root.GetProperty("versions").GetArrayLength() is >= 1 and <= SourceOnlyExecutionStore.MaximumSubtitleEditRoots
                && root.GetProperty("shortForms").ValueKind == JsonValueKind.Array
                && root.GetProperty("shortForms").GetArrayLength() <= StandardCompositionPlan.MaximumShortFormCount);
            if (schema == "source-only.generated-runtime.v2")
                Require(root.GetProperty("projections").ValueKind == JsonValueKind.Array
                    && root.GetProperty("projections").GetArrayLength() is >= 1 and <= SourceOnlyExecutionStore.MaximumSubtitleEditRoots);

            StandardCompositionPreparation preparation = await ReadPreparationAsync(current, root, observed, token).ConfigureAwait(false);
            TranscriptImportResult import = await ReadImportAsync(current, preparation, observed, token).ConfigureAwait(false);
            DefaultSettingsSnapshot snapshot = current.Request.SettingsSnapshot;
            DefaultSettingsProfile profile = settings.GetProfile(snapshot.ProfileId, snapshot.ProfileVersion) ?? throw Failure();
            Require(profile.Id == snapshot.ProfileId && profile.Version == snapshot.ProfileVersion && profile.ContentDigest == snapshot.ProfileDigest);
            _ = DefaultSettingsSnapshot.ParseCanonical(snapshot.CanonicalJson, profile);
            JsonElement fontJson = root.GetProperty("font");
            Shape(fontJson, "Name", "FileDigest");
            Shape(fontJson.GetProperty("FileDigest"), "Hex");
            var font = new RenderFontEvidence(Text(fontJson, "Name"), Digest(Text(fontJson.GetProperty("FileDigest"), "Hex")));
            DateTimeOffset createdAt = Utc(Text(root, "createdAt"));
            Require(createdAt >= preparation.Child.CreatedAt && createdAt >= current.Request.CreatedAt
                && createdAt >= import.Revision.CreatedAt && saved.CreatedAt >= createdAt);
            Require(StandardSubtitleBuilder.TryParseRuleVersion(Text(root, "builderRule"), out StandardSubtitleBuildPolicy builderPolicy));
            SourceOnlyGeneratedRuntimeEvidence replay = SourceOnlyPipeline.BuildGeneratedRuntimeEvidence(preparation,
                import, profile, snapshot, font, current.Request.ActorSid, createdAt, token, builderPolicy);

            IReadOnlyList<StoredSourceOnlyExecution> history = executions.ListForProject(
                current.Request.ProjectId, SourceOnlyExecutionStore.MaximumListResults);
            if (SourceOnlyPipeline.RequiresSubtitleProjection(current, history))
            {
                Require(depth < SourceOnlyPipeline.MaximumSubtitleProjectionParentDepth);
                StoredSourceOnlyExecution previous = SourceOnlyPipeline.FindSubtitleProjectionParent(current, history);
                var jobStore = new JobStore(artifacts.LocalDatabasePath);
                StoredJob previousJob = jobStore.GetJob(previous.Request.RootJobId) ?? throw Failure();
                SourceOnlyResult previousResult = SourceOnlyPipeline.ValidateProjectionParentJob(previous, previousJob);
                StoredJobAggregate previousAggregate = jobStore.GetJobAggregate(previous.Request.RootJobId) ?? throw Failure();
                Require(previousJob.Job.UpdatedAt <= current.Request.CreatedAt && previousAggregate.Revision == previousJob.Revision
                    && previousAggregate.Attempts.Count == previousJob.Job.CurrentAttemptNumber
                    && previousAggregate.Attempts[^1].State == previousJob.Job.State);
                SourceOnlyResultGenerationCheckpoint generation = await SourceOnlyResultGenerationCheckpoint.ReadAsync(
                    artifacts, previous, token).ConfigureAwait(false) ?? throw Failure();
                generation.ValidateSet(previousResult);
                SourceOnlyGeneratedRuntimeEvidence previousGenerated = await ReadAsync(previous, token, depth + 1).ConfigureAwait(false)
                    ?? throw Failure();
                Require(previousGenerated.Preparation.Child.Id == current.Request.ParentVersionId
                    && previousGenerated.Preparation.Child.ContentDigest == current.Request.ParentDigest);
                var heads = (current.Request.SubtitleEdits ?? []).ToDictionary(head => head.RootVersionId);
                Require(heads.Keys.All(previousGenerated.Versions.ContainsKey));
                var captures = new List<SubtitleEditCapture>();
                foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in previousGenerated.Versions.Values)
                {
                    token.ThrowIfCancellationRequested();
                    heads.TryGetValue(item.Version.Id, out SubtitleEditHeadReference? head);
                    if (head is null && item.SubtitleProjection is null) { continue; }
                    StandardShortFormPlan? shortForm = item.Version.Id == previousGenerated.Preparation.Child.Id ? null
                        : previousGenerated.Preparation.Plan.ShortForms.Single(value =>
                            StandardRenderGraphFactory.CreateShortVersion(previousGenerated.Preparation, value.Id).Id == item.Version.Id);
                    // This service and its cache are local to the read, never the editor's live service.
                    var service = new DurableSubtitleEditService(new SubtitleEditStore(artifacts.LocalDatabasePath));
                    service.RegisterGenerated(SourceOnlyApplicationRegistrations.CreateLayout(previousGenerated, item, shortForm),
                        SourceOnlyApplicationRegistrations.GeneratedKey(previousGenerated, item), token,
                        makeDefault: false, projection: item.SubtitleProjection);
                    captures.Add(service.CapturePrefix(current.Request.ProjectId, item.Version.Id,
                        head?.Sequence ?? 0, head?.RecordDigest, token));
                }
                replay = SourceOnlyPipeline.ProjectCapturedSubtitleEdits(current, replay, previous, previousGenerated, captures, token);
            }

            // Saved proofs are comparison targets, NEVER deserialized capabilities or inputs.
            Require(bytes.AsSpan().SequenceEqual(Utf8.GetBytes(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(current, replay))));
            foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in replay.Versions.Values)
            {
                token.ThrowIfCancellationRequested();
                StoredCompositionVersion stored = projects.GetCompositionVersion(item.Version.Id) ?? throw Failure();
                Require(stored.ProjectId == current.Request.ProjectId && stored.ContentDigest == item.Version.ContentDigest
                    && stored.ParentVersionId == item.Version.ParentVersionId && stored.CreatedAt == item.Version.CreatedAt
                    && stored.CanonicalJson == CompositionSnapshotJson.WriteVersion(item.Version));
            }
            ValidateProject(preparation);
            foreach (StoredArtifact artifact in observed.Values) { RequireSameArtifact(artifact, artifacts.GetArtifact(artifact.Id)); }
            StoredSourceOnlyExecution after = ReadCurrent(current);
            Require(after.VersionId is null || after.VersionId == preparation.Child.Id && after.VersionDigest == preparation.Child.ContentDigest);
            token.ThrowIfCancellationRequested();
            return replay;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) { throw Failure(); }
    }

    private StoredSourceOnlyExecution ReadCurrent(StoredSourceOnlyExecution supplied)
    {
        StoredSourceOnlyExecution current = executions.Get(supplied.Request.ExecutionId) ?? throw Failure();
        // State/phase/result revisions may advance during evaluation or delivery.
        // Only immutable acceptance is compared; never insist on a stale StateDigest.
        Require(current.RequestDigest == supplied.RequestDigest && current.RequestJson == supplied.RequestJson
            && ContentDigest.Compute(Utf8.GetBytes(supplied.RequestJson)) == supplied.RequestDigest
            && current.SettingsRevision == supplied.SettingsRevision);
        SourceOnlyExecutionRequest a = current.Request, b = supplied.Request;
        Require(a.ExecutionId == b.ExecutionId && a.ProjectId == b.ProjectId && a.DraftId == b.DraftId
            && a.SourceCandidateId == b.SourceCandidateId && a.RootJobId == b.RootJobId && a.IngestJobId == b.IngestJobId
            && a.ActorSid == b.ActorSid && a.CreatedAt == b.CreatedAt && a.ParentVersionId == b.ParentVersionId
            && a.ParentDigest == b.ParentDigest && a.SettingsSnapshot.CanonicalJson == b.SettingsSnapshot.CanonicalJson
            && (a.SubtitleEdits ?? []).SequenceEqual(b.SubtitleEdits ?? []));
        return current;
    }

    private async Task<StandardCompositionPreparation> ReadPreparationAsync(StoredSourceOnlyExecution execution,
        JsonElement generated, Dictionary<EntityId<ArtifactIdTag>, StoredArtifact> observed, CancellationToken token)
    {
        var versionId = Id<CompositionVersionIdTag>(Text(generated, "productionVersionId"));
        Require(execution.VersionId is null || execution.VersionId == versionId
            && execution.VersionDigest == Digest(Text(generated, "productionVersionDigest")));
        // Bound the stored ancestry BEFORE VersionService traverses evidence-bearing parents.
        ValidateLineage(execution, versionId, token);
        var versions = new VersionService(projects);
        StandardCompositionPreparation preparation = versions.GetStandardComposition(execution.VersionId ?? versionId) ?? throw Failure();
        CompositionVersion child = preparation.Child;
        Require(child.Id == versionId && child.ProjectId == execution.Request.ProjectId
            && child.CreatedByActorId == execution.Request.ActorSid && child.CreatedAt >= execution.Request.CreatedAt
            && child.ContentDigest == Digest(Text(generated, "productionVersionDigest"))
            && preparation.Plan.PlanDigest == Digest(Text(generated, "planDigest"))
            && StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) == execution.Request.SettingsSnapshot.ContentDigest
            && StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsProfile) == execution.Request.SettingsSnapshot.ProfileDigest);
        ValidateProject(preparation);

        // Before completion VersionId is normally null. The execution-owned stage
        // checkpoint, not a guessed latest version, binds that actual production child.
        StoredArtifact checkpoint = Required(Stable<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "composition-checkpoint"));
        using JsonDocument document = ParseDocument(Utf8.GetString(await ReadBytesAsync(checkpoint, execution.Request.ProjectId,
            MaximumEvidenceBytes, observed, token).ConfigureAwait(false)), MaximumEvidenceBytes, maximumStringLength: MaximumEvidenceBytes);
        JsonElement root = document.RootElement;
        Shape(root, "schema", "executionId", "stage", "versionId", "digest", "workingCopyRevision", "workingCopy", "settingsDigest");
        var copy = new WorkingCopy(preparation.PreparedWorkingCopy.Id, child.ProjectId, child.Id,
            preparation.PreparedWorkingCopy.CreatedAt, child.CreatedAt, child.Elements);
        Require(Text(root, "schema") == "source-only.composition-checkpoint.v1" && Text(root, "stage") == "composition-checkpoint"
            && Text(root, "executionId") == execution.Request.ExecutionId.ToString() && Text(root, "versionId") == child.Id.ToString()
            && Text(root, "digest") == child.ContentDigest.Hex && Text(root, "settingsDigest") == execution.Request.SettingsSnapshot.ContentDigest.Hex
            && Text(root, "workingCopyRevision") == checked(preparation.ExpectedWorkingCopyRevision + 1).ToString(CultureInfo.InvariantCulture)
            && Text(root, "workingCopy") == CompositionSnapshotJson.WriteWorkingCopy(copy) && checkpoint.CreatedAt >= child.CreatedAt);
        return preparation;
    }

    private void ValidateProject(StandardCompositionPreparation preparation)
    {
        Project project = preparation.Plan.LongForm.Project;
        StoredProject stored = projects.GetProject(project.Id) ?? throw Failure();
        Require(project.Id == preparation.Child.ProjectId && project.Assets.Count == 1
            && stored.CanonicalJson == ProjectSnapshotJson.WriteProject(project)
            && ContentDigest.Compute(Utf8.GetBytes(stored.CanonicalJson)) == preparation.Plan.ProjectStoreState.PayloadDigest);
    }

    private void ValidateLineage(StoredSourceOnlyExecution execution, EntityId<CompositionVersionIdTag> id, CancellationToken token)
    {
        var seen = new HashSet<EntityId<CompositionVersionIdTag>>();
        bool reachedParent = false;
        DateTimeOffset? latest = null;
        EntityId<CompositionVersionIdTag>? next = id;
        while (next is not null)
        {
            token.ThrowIfCancellationRequested();
            Require(seen.Count < SourceOnlyExecutionStore.MaximumVersionLineageLength && seen.Add(next));
            StoredCompositionVersion stored = projects.GetCompositionVersion(next) ?? throw Failure();
            Require(Utf8.GetByteCount(stored.CanonicalJson) <= MaximumEvidenceBytes);
            CompositionVersion version = CompositionSnapshotJson.ReadVersion(stored.CanonicalJson);
            Require(version.Id == next && version.Id == stored.Id && version.ProjectId == execution.Request.ProjectId
                && version.ProjectId == stored.ProjectId && version.ParentVersionId == stored.ParentVersionId
                && version.ContentDigest == stored.ContentDigest && version.CreatedAt == stored.CreatedAt
                && (latest is null || version.CreatedAt <= latest));
            if (next == execution.Request.ParentVersionId)
            {
                Require(next != id && version.ContentDigest == execution.Request.ParentDigest && version.CreatedAt <= execution.Request.CreatedAt);
                reachedParent = true;
            }
            if (!reachedParent) { Require(version.CreatedAt >= execution.Request.CreatedAt); }
            latest = version.CreatedAt;
            next = version.ParentVersionId;
        }
        Require(execution.Request.ParentVersionId is null || reachedParent);
    }

    private async Task<TranscriptImportResult> ReadImportAsync(StoredSourceOnlyExecution execution,
        StandardCompositionPreparation preparation, Dictionary<EntityId<ArtifactIdTag>, StoredArtifact> observed, CancellationToken token)
    {
        string seed = execution.Request.IngestJobId.ToString();
        StoredArtifact checkpoint = Required(Stable<ArtifactIdTag>(seed, "analysis-checkpoint"));
        using JsonDocument document = ParseDocument(Utf8.GetString(await ReadBytesAsync(checkpoint, execution.Request.ProjectId,
            MaximumCheckpointBytes, observed, token).ConfigureAwait(false)), MaximumCheckpointBytes);
        JsonElement root = document.RootElement;
        Shape(root, "schema", "executionId", "projectId", "draftId", "ingestJobId", "sourceCandidateId", "sourceDigest",
            "originalArtifactId", "ptsDigest", "audio", "timeline", "raw", "transcript", "revisionId", "speakerId", "createdAt", "requestId", "requestedPolicy", "idRule");
        StoredSourceOnlyExecution producer = executions.Get(Id<SourceOnlyExecutionIdTag>(Text(root, "executionId"))) ?? throw Failure();
        Require(Text(root, "schema") == "source-only.analysis-checkpoint.v1" && Text(root, "idRule") == "source-only.import-ids.v1"
            && Text(root, "projectId") == execution.Request.ProjectId.ToString() && Text(root, "draftId") == execution.Request.DraftId.ToString()
            && Text(root, "ingestJobId") == seed && Text(root, "sourceCandidateId") == execution.Request.SourceCandidateId.ToString()
            && producer.Request.ProjectId == execution.Request.ProjectId && producer.Request.DraftId == execution.Request.DraftId
            && producer.Request.SourceCandidateId == execution.Request.SourceCandidateId && producer.Request.IngestJobId == execution.Request.IngestJobId);

        WorkflowResumeState ingest = await ReadIngestAsync(execution, observed, token).ConfigureAwait(false);
        Asset asset = ingest.Asset ?? throw Failure();
        Require(StandardRenderGraphFactory.Digest(asset) == StandardRenderGraphFactory.Digest(preparation.Plan.LongForm.Project.Assets.Single())
            && Text(root, "sourceDigest") == asset.ContentDigest.Hex && Text(root, "originalArtifactId") == ingest.Original!.Artifact.Id.ToString()
            && Text(root, "ptsDigest") == ContentDigest.Compute(Utf8.GetBytes(ingest.SourcePtsIndex!.CanonicalJson)).Hex);
        StoredArtifact audio = Reference(root, "audio", Stable<ArtifactIdTag>(seed, "analysis-audio"), execution.Request.ProjectId);
        StoredArtifact timelineArtifact = Reference(root, "timeline", Stable<ArtifactIdTag>(seed, "analysis-timeline"), execution.Request.ProjectId);
        StoredArtifact rawArtifact = Reference(root, "raw", Stable<ArtifactIdTag>(seed, "asr-raw"), execution.Request.ProjectId);
        StoredArtifact transcript = Reference(root, "transcript", Stable<ArtifactIdTag>(seed, "machine-transcript"), execution.Request.ProjectId);
        // Audio may legitimately exceed the JSON envelope: verify its entire stream without allocating it.
        await using (ValidatedArtifactReadStream audioStream = await artifacts.OpenValidatedReadAsync(audio.Id, token).ConfigureAwait(false))
        {
            RequireSameArtifact(audio, audioStream.RegisteredArtifact);
            observed.Add(audio.Id, audio);
        }
        string timelineJson = Utf8.GetString(await ReadBytesAsync(timelineArtifact, execution.Request.ProjectId,
            MaximumCheckpointBytes, observed, token).ConfigureAwait(false));
        using JsonDocument timelineDocument = ParseDocument(timelineJson, MaximumCheckpointBytes);
        AnalysisAudioTimeline timeline = AnalysisAudioTimeline.ParseCanonical(timelineJson);
        Require(timeline.ProjectId == execution.Request.ProjectId && timeline.AnalysisJobId == producer.Request.RootJobId
            && timeline.SourceAssetId == asset.Id && timeline.SourceContentDigest == asset.ContentDigest && timeline.SourceByteSize == asset.ByteSize
            && timeline.SourceOriginalArtifactId == ingest.Original!.Artifact.Id && timeline.SourceVideoPtsIndexDigest.Hex == Text(root, "ptsDigest")
            && timeline.AnalysisAudioArtifactId == audio.Id && timeline.AnalysisAudioDigest == audio.ContentDigest
            && timeline.AnalysisAudioByteSize == audio.ByteSize && timeline.TimelineArtifactId == timelineArtifact.Id);
        byte[] rawBytes = await ReadBytesAsync(rawArtifact, execution.Request.ProjectId, LocalAnalysisWorker.MaxResultBytes, observed, token).ConfigureAwait(false);
        string requestId = Text(root, "requestId");
        Require(Guid.TryParseExact(requestId, "D", out Guid requestGuid) && requestGuid != Guid.Empty && requestGuid.ToString("D") == requestId);
        LocalAsrResult raw = LocalAnalysisWorker.ParseResult(rawBytes, rawArtifact.ContentDigest,
            artifacts.GetValidatedRegisteredArtifactPath(rawArtifact.Id), requestId, Text(root, "requestedPolicy"), timeline);
        DateTimeOffset createdAt = Utc(Text(root, "createdAt"));
        var revisionId = Stable<TranscriptRevisionIdTag>(seed, "machine-revision");
        var speakerId = Stable<TranscriptSpeakerIdTag>(seed, "anonymous-speaker");
        Require(Text(root, "revisionId") == revisionId.ToString() && Text(root, "speakerId") == speakerId.ToString()
            && createdAt == rawArtifact.CreatedAt && createdAt >= producer.Request.CreatedAt && checkpoint.CreatedAt >= createdAt
            && transcript.CreatedAt >= createdAt && createdAt >= audio.CreatedAt && createdAt >= timelineArtifact.CreatedAt);
        TranscriptImportResult imported = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(
            await CorrectedWorkerAsync(seed, execution.Request.ProjectId, raw.WorkerResult, timeline, observed, token).ConfigureAwait(false),
            timeline, revisionId, speakerId, createdAt));
        byte[] savedTranscript = await ReadBytesAsync(transcript, execution.Request.ProjectId, MaximumEvidenceBytes, observed, token).ConfigureAwait(false);
        Require(savedTranscript.AsSpan().SequenceEqual(Utf8.GetBytes(TranscriptSnapshotJson.WriteRevision(imported.Revision))));
        return imported;
    }

    private async Task<WorkflowResumeState> ReadIngestAsync(StoredSourceOnlyExecution execution,
        Dictionary<EntityId<ArtifactIdTag>, StoredArtifact> observed, CancellationToken token)
    {
        StoredJobAggregate job = RequireSucceededJob(execution.Request.IngestJobId, execution.Request.ProjectId, SourceOnlyExecutionStore.IngestJobKind);
        var store = new IngestWorkflowStore(artifacts.LocalDatabasePath);
        StoredIngestWorkflow stored = store.Get(execution.Request.IngestJobId) ?? throw Failure();
        using JsonDocument requestDocument = ParseDocument(stored.RequestPayloadJson, IngestWorkflowStore.MaximumPayloadUtf8Bytes);
        using JsonDocument checkpointDocument = ParseDocument(stored.CheckpointPayloadJson, IngestWorkflowStore.MaximumPayloadUtf8Bytes,
            maximumArrayLength: 1000000);
        IngestWorkflowRequest request = IngestWorkflowCheckpointCodec.DeserializeRequest(stored.RequestPayloadJson);
        Require(request.JobId == execution.Request.IngestJobId && request.ProjectId == execution.Request.ProjectId
            && request.AssetCandidateId == execution.Request.SourceCandidateId && request.OriginalArtifactId.Value == request.AssetCandidateId.Value
            && stored.CheckpointStage == (int)IngestWorkflowStage.PreviewAssetsCommitted && stored.CheckpointAttemptNumber > 0
            && stored.CheckpointAttemptNumber <= job.Job.CurrentAttemptNumber && stored.UpdatedAt <= job.Job.UpdatedAt);
        var codec = new IngestWorkflowCheckpointCodec(artifacts.LocalWorkspacePath, artifacts);
        codec.ValidateStagingCheckpointBoundary(request, stored.CheckpointStage);
        WorkflowResumeState state = await codec.DeserializeAsync(request, stored, token).ConfigureAwait(false);
        codec.ValidateStagingCheckpointBoundary(request, stored.CheckpointStage);
        foreach (StoredArtifact artifact in new[] { state.Original!.Artifact, state.Proxy!.ProxyArtifact,
            state.PreviewAssets!.WaveformIndexArtifact, state.PreviewAssets.SpriteImageArtifact, state.PreviewAssets.SpriteIndexArtifact })
        {
            RequireSameArtifact(artifact, artifacts.GetArtifact(artifact.Id));
            observed.Add(artifact.Id, artifact);
        }
        StoredJobAggregate afterJob = RequireSucceededJob(request.JobId, request.ProjectId, SourceOnlyExecutionStore.IngestJobKind);
        StoredIngestWorkflow after = store.Get(request.JobId) ?? throw Failure();
        Require(afterJob.Revision == job.Revision && afterJob.Job.UpdatedAt == job.Job.UpdatedAt
            && after.Revision == stored.Revision && after.RequestDigest == stored.RequestDigest && after.CheckpointDigest == stored.CheckpointDigest
            && after.CheckpointStage == stored.CheckpointStage && after.CheckpointAttemptNumber == stored.CheckpointAttemptNumber
            && after.UpdatedAt == stored.UpdatedAt);
        return state;
    }

    private StoredJobAggregate RequireSucceededJob(EntityId<JobIdTag> id, EntityId<ProjectIdTag> project, string kind)
    {
        StoredJobAggregate job = new JobStore(artifacts.LocalDatabasePath).GetJobAggregate(id) ?? throw Failure();
        Require(job.Job.Id == id && job.Job.ProjectId == project && job.Job.Kind == kind && job.Job.State == JobState.Succeeded
            && job.Job.CurrentAttemptNumber > 0 && job.Attempts.Count > 0 && job.Attempts[^1].State == JobState.Succeeded);
        return job;
    }

    private StoredArtifact Reference(JsonElement root, string key, EntityId<ArtifactIdTag> expectedId, EntityId<ProjectIdTag> project)
    {
        JsonElement reference = root.GetProperty(key);
        Shape(reference, "id", "digest", "byteSize");
        StoredArtifact artifact = Required(expectedId);
        Require(artifact.Kind == ArtifactKind.Analysis && artifact.ProjectId == project && Text(reference, "id") == expectedId.ToString()
            && Text(reference, "digest") == artifact.ContentDigest.Hex && Text(reference, "byteSize") == artifact.ByteSize.ToString(CultureInfo.InvariantCulture));
        return artifact;
    }

    private async Task<byte[]> ReadBytesAsync(StoredArtifact stored, EntityId<ProjectIdTag> project, int maximum,
        Dictionary<EntityId<ArtifactIdTag>, StoredArtifact> observed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(stored.ProjectId == project && stored.Kind == ArtifactKind.Analysis && stored.ByteSize > 0 && stored.ByteSize <= maximum);
        await using ValidatedArtifactReadStream stream = await artifacts.OpenValidatedReadAsync(stored.Id, token).ConfigureAwait(false);
        RequireSameArtifact(stored, stream.RegisteredArtifact);
        Require(stream.Length == stored.ByteSize);
        byte[] bytes = new byte[checked((int)stored.ByteSize)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Require(stream.Length == bytes.Length && ContentDigest.Compute(bytes) == stored.ContentDigest);
        if (observed.TryGetValue(stored.Id, out StoredArtifact? previous)) { RequireSameArtifact(previous, stored); }
        else { observed.Add(stored.Id, stored); }
        return bytes;
    }

    private static void RequireSameArtifact(StoredArtifact expected, StoredArtifact? actual) => Require(actual is not null
        && expected.Id == actual.Id && expected.ProjectId == actual.ProjectId && expected.Kind == actual.Kind
        && expected.ContentDigest == actual.ContentDigest && expected.ByteSize == actual.ByteSize
        && expected.StorageReference == actual.StorageReference && expected.CreatedAt == actual.CreatedAt);
    private StoredArtifact Required(EntityId<ArtifactIdTag> id) => artifacts.GetArtifact(id) ?? throw Failure();

    // FR-057.5: the same pinned silence evidence the pipeline applied when it imported the transcript.
    private async Task<AsrWorkerResult> CorrectedWorkerAsync(string seed, EntityId<ProjectIdTag> project, AsrWorkerResult worker,
        AnalysisAudioTimeline timeline, Dictionary<EntityId<ArtifactIdTag>, StoredArtifact> observed, CancellationToken token)
    {
        StoredArtifact? stored = artifacts.GetArtifact(Stable<ArtifactIdTag>(seed, "asr-timing-correction"));
        if (stored is null) { return worker; }
        string json = Utf8.GetString(await ReadBytesAsync(stored, project, MaximumEvidenceBytes, observed, token).ConfigureAwait(false));
        try
        {
            return AsrTimingCorrection.Apply(worker, AsrTimingCorrection.Read(json, SilenceDetector.PauseThresholdVersion,
                timeline.AnalysisSampleCount, AnalysisAudioTimeline.FixedSampleRate));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw Failure();
        }
    }
    private static EntityId<T> Stable<T>(string seed, string role) => SourceOnlyPipeline.StableId<T>(seed, role);
    private static string Text(JsonElement root, string key) => root.GetProperty(key).GetString() ?? throw Failure();
    private static DateTimeOffset Utc(string text) => DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out DateTimeOffset value) && value.Offset == TimeSpan.Zero
        && value.ToString("O", CultureInfo.InvariantCulture) == text ? value : throw Failure();
    private static void Shape(JsonElement root, params string[] keys)
    {
        Require(root.ValueKind == JsonValueKind.Object);
        var remaining = keys.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject()) { Require(remaining.Remove(property.Name)); }
        Require(remaining.Count == 0);
    }
    private static EvidenceReadException Failure() => new();
    private sealed class EvidenceReadException() : InvalidOperationException("The saved source-only subtitle evidence is invalid.") { }
}