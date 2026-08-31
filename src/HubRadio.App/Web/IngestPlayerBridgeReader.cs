using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Web;

internal sealed class IngestPlayerBridgeReader : IPlayerBridgeReader
{
    private readonly JobStore jobStore;
    private readonly IngestWorkflowStore workflowStore;
    private readonly IngestWorkflowCheckpointCodec checkpointCodec;

    internal IngestPlayerBridgeReader(
        JobStore jobStore,
        IngestWorkflowStore workflowStore,
        IngestWorkflowCheckpointCodec checkpointCodec)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(workflowStore);
        ArgumentNullException.ThrowIfNull(checkpointCodec);
        this.jobStore = jobStore;
        this.workflowStore = workflowStore;
        this.checkpointCodec = checkpointCodec;
    }

    public Task<PlayerBridgeReadResult> ReadAsync(
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        // Durable stores currently expose synchronous reads and checkpoint
        // canonicalization can be sizeable. Keep both away from the WebView UI thread.
        return Task.Run(
            () => ReadCoreAsync(jobId, cancellationToken),
            cancellationToken);
    }

    private async Task<PlayerBridgeReadResult> ReadCoreAsync(
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
                return PlayerBridgeReadResult.Unavailable();
            }

            if (!string.Equals(
                    storedJob.Job.Kind,
                    IngestWorkflow.JobKind,
                    StringComparison.Ordinal) ||
                storedJob.Job.ProjectId != storedWorkflow.ProjectId)
            {
                return PlayerBridgeReadResult.Mismatched();
            }

            if (storedJob.Job.State != JobState.Succeeded ||
                storedWorkflow.CheckpointStage !=
                    (int)IngestWorkflowStage.PreviewAssetsCommitted)
            {
                return PlayerBridgeReadResult.Unavailable();
            }

            if (storedWorkflow.CheckpointAttemptNumber <= 0 ||
                storedWorkflow.CheckpointAttemptNumber >
                    storedJob.Job.CurrentAttemptNumber)
            {
                return PlayerBridgeReadResult.Mismatched();
            }

            IngestWorkflowRequest request =
                IngestWorkflowCheckpointCodec.DeserializeRequest(
                    storedWorkflow.RequestPayloadJson);
            if (request.JobId != jobId ||
                request.ProjectId != storedJob.Job.ProjectId ||
                request.ProjectId != storedWorkflow.ProjectId ||
                request.RequestDigest != storedWorkflow.RequestDigest)
            {
                return PlayerBridgeReadResult.Mismatched();
            }

            WorkflowResumeState state = await checkpointCodec.DeserializeAsync(
                request,
                storedWorkflow,
                cancellationToken).ConfigureAwait(false);
            if (state.Stage != IngestWorkflowStage.PreviewAssetsCommitted ||
                state.Proxy is null ||
                state.FrameMap is null ||
                state.PreviewAssets is null)
            {
                return PlayerBridgeReadResult.Mismatched();
            }

            GeneratedProxy proxy = state.Proxy;
            GeneratedPreviewAssets preview = state.PreviewAssets;
            PlayerBridgeReadResult result = PlayerBridgeEvidence.CreateReadResult(
                state.FrameMap,
                proxy.ProxyPtsIndex,
                preview,
                PlayerMediaAssetReference.ProxyVideo(
                    request.ProjectId,
                    proxy.ProxyArtifact.Id,
                    proxy.ProxyArtifact.ContentDigest),
                PlayerMediaAssetReference.ThumbnailSprite(
                    request.ProjectId,
                    preview.SpriteImageArtifact.Id,
                    preview.SpriteImageArtifact.ContentDigest));
            if (result.Status != PlayerBridgeStatus.Available)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            StoredJob? currentJob = jobStore.GetJob(jobId);
            StoredIngestWorkflow? currentWorkflow = workflowStore.Get(jobId);
            if (!IsSameJob(storedJob, currentJob) ||
                !IsSameWorkflow(storedWorkflow, currentWorkflow))
            {
                return PlayerBridgeReadResult.Stale();
            }

            return result;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ArtifactStoreException)
        {
            return PlayerBridgeReadResult.Stale();
        }
        catch (InvalidOperationException exception)
            when (ContainsArtifactFailure(exception))
        {
            return PlayerBridgeReadResult.Stale();
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException)
        {
            return PlayerBridgeReadResult.Mismatched();
        }
        catch (Exception)
        {
            return PlayerBridgeReadResult.Unavailable();
        }
    }

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
