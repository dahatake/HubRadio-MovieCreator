using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.App.Projects;

/// <summary>
/// Removes the workspace files that belong to one project: <c>&lt;workspace&gt;\projects\&lt;projectId&gt;</c>.
/// Nothing outside that directory is touched, and links are removed without following them.
/// </summary>
internal static class ProjectWorkspaceFileRemover
{
    /// <returns>True when nothing is left (including when nothing existed); false when removal failed.</returns>
    internal static bool Remove(string workspacePath, EntityId<ProjectIdTag> projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(projectId);

        string projectsRoot = Path.GetFullPath(Path.Combine(workspacePath, "projects"));
        string target = Path.GetFullPath(Path.Combine(projectsRoot, projectId.ToString()));
        if (!string.Equals(Path.GetDirectoryName(target), projectsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            FileSystemInfo? entry = Directory.Exists(target)
                ? new DirectoryInfo(target)
                : File.Exists(target) ? new FileInfo(target) : null;
            if (entry is null)
            {
                return true;
            }

            if (entry is DirectoryInfo directory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                directory.Delete(recursive: true);
            }
            else
            {
                // A link (or stray file) is deleted itself; its target is never followed.
                entry.Delete();
            }

            return !Directory.Exists(target) && !File.Exists(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
