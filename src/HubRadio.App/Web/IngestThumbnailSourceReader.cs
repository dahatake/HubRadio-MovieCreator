using HubRadio.App.Media;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Web;

internal sealed class IngestThumbnailSourceReader : IThumbnailSourceReader
{
    private readonly JobStore jobStore;
    private readonly IngestWorkflowStore workflowStore;
    private readonly ProjectStore projectStore;
    private readonly IngestWorkflowCheckpointCodec checkpointCodec;
    private readonly ProjectCreationCatalog projectCatalog;

    internal IngestThumbnailSourceReader(
        JobStore jobStore,
        IngestWorkflowStore workflowStore,
        ProjectStore projectStore,
        IngestWorkflowCheckpointCodec checkpointCodec,
        ProjectCreationCatalog projectCatalog)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(workflowStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(checkpointCodec);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        this.jobStore = jobStore;
        this.workflowStore = workflowStore;
        this.projectStore = projectStore;
        this.checkpointCodec = checkpointCodec;
        this.projectCatalog = projectCatalog;
    }

    public Task<ThumbnailSourceReadResult> ReadAsync(
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ReadCoreAsync(jobId, cancellationToken),
            cancellationToken);
    }

    private async Task<ThumbnailSourceReadResult> ReadCoreAsync(
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredJob? storedJob = jobStore.GetJob(jobId);
            StoredIngestWorkflow? storedWorkflow = workflowStore.Get(jobId);
            if (storedJob is null || storedWorkflow is null)
            {
                return ThumbnailSourceReadResult.Unavailable();
            }

            StoredProject? storedProject = projectStore.GetProject(
                storedWorkflow.ProjectId);
            if (storedProject is null)
            {
                return ThumbnailSourceReadResult.Unavailable();
            }

            if (!string.Equals(
                    storedJob.Job.Kind,
                    IngestWorkflow.JobKind,
                    StringComparison.Ordinal) ||
                storedJob.Job.ProjectId != storedWorkflow.ProjectId ||
                storedJob.Job.ProjectId != storedProject.Id)
            {
                return ThumbnailSourceReadResult.Mismatched();
            }

            if (storedJob.Job.State != JobState.Succeeded ||
                storedWorkflow.CheckpointStage !=
                    (int)IngestWorkflowStage.PreviewAssetsCommitted)
            {
                return ThumbnailSourceReadResult.Unavailable();
            }

            if (storedWorkflow.CheckpointAttemptNumber <= 0 ||
                storedWorkflow.CheckpointAttemptNumber >
                    storedJob.Job.CurrentAttemptNumber)
            {
                return ThumbnailSourceReadResult.Mismatched();
            }

            IngestWorkflowRequest request =
                IngestWorkflowCheckpointCodec.DeserializeRequest(
                    storedWorkflow.RequestPayloadJson);
            if (request.JobId != jobId ||
                request.ProjectId != storedJob.Job.ProjectId ||
                request.ProjectId != storedWorkflow.ProjectId ||
                request.ProjectId != storedProject.Id ||
                request.RequestDigest != storedWorkflow.RequestDigest)
            {
                return ThumbnailSourceReadResult.Mismatched();
            }

            WorkflowResumeState state = await checkpointCodec.DeserializeAsync(
                request,
                storedWorkflow,
                cancellationToken).ConfigureAwait(false);
            if (state.Stage != IngestWorkflowStage.PreviewAssetsCommitted ||
                state.Asset is null ||
                state.SourcePtsIndex is null ||
                state.Original is null ||
                state.Proxy is null ||
                state.FrameMap is null ||
                state.PreviewAssets is null)
            {
                return ThumbnailSourceReadResult.Mismatched();
            }

            ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseStored(
                storedProject.CanonicalJson,
                projectCatalog);
            if (!draft.IsComplete ||
                draft.SourceCandidateId != request.AssetCandidateId ||
                draft.PublicationScope != storedProject.PublicationScope ||
                !SameDeadline(
                    draft.DeadlineSelection!.Deadline,
                    storedProject.Deadline))
            {
                return ThumbnailSourceReadResult.Mismatched();
            }

            var project = new Project(
                storedProject.Id,
                draft.ContentName!,
                draft.Purpose!,
                draft.Audience!,
                draft.PublicationScope!.Value,
                draft.DeadlineSelection.Deadline,
                [state.Asset],
                performers: []);
            PlayerBridgeReadResult player = PlayerBridgeEvidence.CreateReadResult(
                state.FrameMap,
                state.Proxy.ProxyPtsIndex,
                state.PreviewAssets,
                PlayerMediaAssetReference.ProxyVideo(
                    request.ProjectId,
                    state.Proxy.ProxyArtifact.Id,
                    state.Proxy.ProxyArtifact.ContentDigest),
                PlayerMediaAssetReference.ThumbnailSprite(
                    request.ProjectId,
                    state.PreviewAssets.SpriteImageArtifact.Id,
                    state.PreviewAssets.SpriteImageArtifact.ContentDigest));
            if (player.Status != PlayerBridgeStatus.Available ||
                player.Snapshot is null)
            {
                return player.Status == PlayerBridgeStatus.Stale
                    ? ThumbnailSourceReadResult.Stale()
                    : ThumbnailSourceReadResult.Mismatched();
            }

            var context = new ThumbnailSourceContext(
                project,
                state.Original.Artifact,
                state.Asset,
                state.SourcePtsIndex,
                state.FrameMap,
                player.Snapshot.EvidenceDigest);

            cancellationToken.ThrowIfCancellationRequested();
            StoredJob? currentJob = jobStore.GetJob(jobId);
            StoredIngestWorkflow? currentWorkflow = workflowStore.Get(jobId);
            StoredProject? currentProject = projectStore.GetProject(
                storedProject.Id);
            if (!IsSameJob(storedJob, currentJob) ||
                !IsSameWorkflow(storedWorkflow, currentWorkflow) ||
                !IsSameProject(storedProject, currentProject))
            {
                return ThumbnailSourceReadResult.Stale();
            }

            return ThumbnailSourceReadResult.Available(context);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ArtifactStoreException)
        {
            return ThumbnailSourceReadResult.Stale();
        }
        catch (InvalidOperationException exception)
            when (ContainsArtifactFailure(exception))
        {
            return ThumbnailSourceReadResult.Stale();
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                System.Text.Json.JsonException or OverflowException)
        {
            return ThumbnailSourceReadResult.Mismatched();
        }
        catch (Exception)
        {
            return ThumbnailSourceReadResult.Unavailable();
        }
    }

    private static bool SameDeadline(
        DateTimeOffset? expected,
        DateTimeOffset? actual) =>
        expected is null
            ? actual is null
            : actual is not null &&
                expected.Value.ToUniversalTime() == actual.Value.ToUniversalTime();

    private static bool IsSameJob(
        StoredJob expected,
        StoredJob? actual) =>
        actual is not null &&
        actual.Revision == expected.Revision &&
        actual.Job.Id == expected.Job.Id &&
        actual.Job.ProjectId == expected.Job.ProjectId &&
        string.Equals(
            actual.Job.Kind,
            expected.Job.Kind,
            StringComparison.Ordinal) &&
        actual.Job.State == expected.Job.State &&
        actual.Job.CurrentAttemptNumber == expected.Job.CurrentAttemptNumber &&
        actual.Job.CreatedAt == expected.Job.CreatedAt &&
        actual.Job.UpdatedAt == expected.Job.UpdatedAt;

    private static bool IsSameWorkflow(
        StoredIngestWorkflow expected,
        StoredIngestWorkflow? actual) =>
        actual is not null &&
        actual.Revision == expected.Revision &&
        actual.JobId == expected.JobId &&
        actual.ProjectId == expected.ProjectId &&
        actual.RequestDigest == expected.RequestDigest &&
        actual.CheckpointDigest == expected.CheckpointDigest &&
        actual.CheckpointStage == expected.CheckpointStage &&
        actual.CheckpointAttemptNumber == expected.CheckpointAttemptNumber &&
        string.Equals(
            actual.RequestSchemaVersion,
            expected.RequestSchemaVersion,
            StringComparison.Ordinal) &&
        string.Equals(
            actual.CheckpointSchemaVersion,
            expected.CheckpointSchemaVersion,
            StringComparison.Ordinal) &&
        actual.UpdatedAt == expected.UpdatedAt;

    private static bool IsSameProject(
        StoredProject expected,
        StoredProject? actual) =>
        actual is not null &&
        actual.Id == expected.Id &&
        actual.Revision == expected.Revision &&
        actual.PublicationScope == expected.PublicationScope &&
        SameDeadline(actual.Deadline, expected.Deadline) &&
        actual.UpdatedAt == expected.UpdatedAt &&
        string.Equals(
            actual.CanonicalJson,
            expected.CanonicalJson,
            StringComparison.Ordinal);

    private static bool ContainsArtifactFailure(Exception exception)
    {
        for (Exception? current = exception;
            current is not null;
            current = current.InnerException)
        {
            if (current is ArtifactStoreException)
            {
                return true;
            }
        }

        return false;
    }
}
