using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Projects;

/// <summary>
/// Identifies one generated landscape thumbnail of a project. It carries IDs only; the
/// media GET boundary (<c>SourceOnlyMediaResponseFactory</c>) revalidates every read.
/// </summary>
internal sealed record ProjectThumbnailReference(
    string ExecutionId,
    string ArtifactId,
    string ContentDigest);

/// <summary>FR-045: finds the newest generated landscape thumbnail of a finalized project.</summary>
internal sealed class ProjectThumbnailResolver
{
    internal const int MaximumExecutionsInspected = 10;

    private readonly SourceOnlyExecutionStore executions;

    internal ProjectThumbnailResolver(SourceOnlyExecutionStore executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        this.executions = executions;
    }

    /// <summary>
    /// Returns null when the project has no usable thumbnail. A damaged or unreadable
    /// record must not break the list, so any failure is treated as "no thumbnail".
    /// </summary>
    internal ProjectThumbnailReference? Resolve(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        try
        {
            foreach (StoredSourceOnlyExecution execution in
                executions.ListForProject(projectId, MaximumExecutionsInspected))
            {
                if (execution.ResultJson is null ||
                    execution.ResultDigest is null ||
                    execution.Request.ProjectId != projectId)
                {
                    continue;
                }

                SourceOnlyResult result =
                    SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
                SourceOnlyResultArtifact? thumbnail = result.Artifacts.FirstOrDefault(static artifact =>
                    artifact.Role == SourceOnlyResultRole.LandscapeThumbnail &&
                    artifact.FailureCode is null &&
                    artifact.Artifact.Kind == ArtifactKind.Render);
                if (thumbnail is not null)
                {
                    return new ProjectThumbnailReference(
                        execution.Request.ExecutionId.ToString(),
                        thumbnail.ArtifactId.ToString(),
                        thumbnail.ArtifactDigest.Hex);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }
}
