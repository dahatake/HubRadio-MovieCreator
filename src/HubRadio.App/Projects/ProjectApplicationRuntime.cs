using HubRadio.App.Media;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Security;
using Microsoft.UI.Xaml;

namespace HubRadio.App.Projects;

internal static class ProjectApplicationRuntime
{
    internal static ProjectBridgeHandler Create(
        Window owner,
        string applicationDataDirectory,
        string profilesDirectory)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);

        var actorProvider = new WindowsActorProvider();
        string actorSid = actorProvider.GetCurrentSid();
        string applicationRoot = Path.GetFullPath(applicationDataDirectory);
        Directory.CreateDirectory(applicationRoot);
        string workspacePath = Path.Combine(applicationRoot, "Workspace");
        if (Directory.Exists(workspacePath))
        {
            _ = WorkspaceAcl.VerifyExisting(workspacePath, actorSid);
        }
        else
        {
            _ = WorkspaceAcl.CreateNew(workspacePath, actorSid);
        }

        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        _ = DatabaseMigrator.Migrate(
            databasePath,
            Path.Combine(workspacePath, "backups"));
        var authorization = new AuthorizationService(
            new RoleAssignmentStore(databasePath));
        _ = authorization.BootstrapInitialRoles(actorSid);

        var registry = new AssetCandidateRegistry();
        var picker = new MediaPicker(owner, registry);
        ProjectCreationCatalog catalog = ProjectCreationCatalog.Load(profilesDirectory);
        var service = new ProjectCreationService(
            new ProjectCreationDraftStore(databasePath),
            authorization,
            registry,
            catalog,
            actorProvider.GetCurrentSid,
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                EntityId<AssetIdTag>? candidateId =
                    await picker.PickAssetCandidateAsync();
                cancellationToken.ThrowIfCancellationRequested();
                return candidateId;
            });
        return new ProjectBridgeHandler(service);
    }
}