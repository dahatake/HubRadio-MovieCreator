using HubRadio.App.Media;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Thumbnails;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Web;

internal sealed class ThumbnailApplicationRuntime
{
    private ThumbnailApplicationRuntime(
        ThumbnailBridgeRequestHandler requestHandler)
    {
        RequestHandler = requestHandler;
    }

    internal ThumbnailBridgeRequestHandler RequestHandler { get; }

    internal static ThumbnailApplicationRuntime Create(
        string applicationDataDirectory,
        string profilesDirectory,
        IApprovedThumbnailTemplateProvider? templateProvider = null)
    {
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
        var authorizationService = new AuthorizationService(
            new RoleAssignmentStore(databasePath));
        _ = authorizationService.BootstrapInitialRoles(actorSid);

        var artifactStore = new ArtifactStore(databasePath, workspacePath);
        var checkpointCodec = new IngestWorkflowCheckpointCodec(
            workspacePath,
            artifactStore);
        ProjectCreationCatalog projectCatalog = ProjectCreationCatalog.Load(
            profilesDirectory);
        var sourceReader = new IngestThumbnailSourceReader(
            new JobStore(databasePath),
            new IngestWorkflowStore(databasePath),
            new ProjectStore(databasePath),
            checkpointCodec,
            projectCatalog);
        var service = new ManualThumbnailService(
            artifactStore,
            new ProcessRunner(),
            new BinaryVerifier(),
            workspacePath,
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"));
        var handler = new ThumbnailBridgeRequestHandler(
            sourceReader,
            templateProvider ??
                UnavailableApprovedThumbnailTemplateProvider.Instance,
            new ThumbnailAccessAuthorization(
                authorizationService,
                actorProvider),
            service);
        return new ThumbnailApplicationRuntime(handler);
    }
}
