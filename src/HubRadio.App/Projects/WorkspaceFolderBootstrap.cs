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
            _ = WorkspaceAcl.VerifyExisting(workspacePath, actorSid);
        }
        else
        {
            _ = WorkspaceAcl.CreateNew(workspacePath, actorSid);
        }

        return workspacePath;
    }
}
