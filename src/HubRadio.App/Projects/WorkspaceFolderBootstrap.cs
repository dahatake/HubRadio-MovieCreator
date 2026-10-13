using HubRadio.Infrastructure.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Projects;

/// <summary>FR-062: resolves the configured Workspace folder and verifies or creates it.</summary>
internal static class WorkspaceFolderBootstrap
{
    internal static string Prepare(string applicationRoot, string actorSid)
    {
        var store = new WorkspaceLocationStore(applicationRoot);
        _ = store.TryMigrateLegacyWorkspace();
        string workspacePath = store.Resolve().CurrentPath;
        if (Directory.Exists(workspacePath))
        {
            try
            {
                _ = WorkspaceAcl.VerifyExisting(workspacePath, actorSid);
            }
            catch (WorkspaceAclException exception)
                when (exception.Reason == WorkspaceAclFailureReason.VerificationFailed &&
                      IsPlainEmptyDirectory(workspacePath))
            {
                // FR-062 AC-037: settings accept an empty existing folder. The app did not create it, so it
                // has inherited access rules. Nothing is inside, so re-create it with the protected rules.
                Directory.Delete(workspacePath, recursive: false);
                _ = WorkspaceAcl.CreateNew(workspacePath, actorSid);
            }
        }
        else
        {
            _ = WorkspaceAcl.CreateNew(workspacePath, actorSid);
        }

        return workspacePath;
    }

    private static bool IsPlainEmptyDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists &&
                   (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   !directory.EnumerateFileSystemInfos().Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
