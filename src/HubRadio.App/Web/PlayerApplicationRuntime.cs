using HubRadio.App.Media;
using HubRadio.App.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Web;

internal sealed class PlayerApplicationRuntime
{
    private PlayerApplicationRuntime(
        IPlayerAccessAuthorization authorization,
        IPlayerBridgeReader bridgeReader,
        IPlayerMediaAssetReader mediaAssetReader)
    {
        Authorization = authorization;
        BridgeReader = bridgeReader;
        MediaAssetReader = mediaAssetReader;
    }

    internal IPlayerAccessAuthorization Authorization { get; }

    internal IPlayerBridgeReader BridgeReader { get; }

    internal IPlayerMediaAssetReader MediaAssetReader { get; }

    internal static PlayerApplicationRuntime Create(
        string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);

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
        var authorizationService = new AuthorizationService(
            new RoleAssignmentStore(databasePath));
        _ = authorizationService.BootstrapInitialRoles(actorSid);

        var artifactStore = new ArtifactStore(databasePath, workspacePath);
        var checkpointCodec = new IngestWorkflowCheckpointCodec(
            workspacePath,
            artifactStore);
        return new PlayerApplicationRuntime(
            new PlayerAccessAuthorization(
                authorizationService,
                actorProvider),
            new IngestPlayerBridgeReader(
                new JobStore(databasePath),
                new IngestWorkflowStore(databasePath),
                checkpointCodec),
            new ArtifactStorePlayerMediaAssetReader(artifactStore));
    }
}