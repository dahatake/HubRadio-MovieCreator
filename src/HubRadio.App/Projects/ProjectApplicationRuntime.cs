using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.App.Reviews;
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

internal sealed class ApplicationBridgeHandlers
{
    internal ApplicationBridgeHandlers(
        ProjectBridgeHandler project,
        JobBridgeRequestHandler jobs,
        TimelineBridgeRequestHandler timeline,
        TranscriptBridgeRequestHandler transcript,
        ReviewBridgeHandler review,
        ComparisonBridgeRequestHandler comparison,
        DeliveryBridgeRequestHandler delivery)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(delivery);
        Project = project;
        Jobs = jobs;
        Timeline = timeline;
        Transcript = transcript;
        Review = review;
        Comparison = comparison;
        Delivery = delivery;
    }

    internal ProjectBridgeHandler Project { get; }

    internal JobBridgeRequestHandler Jobs { get; }

    internal TimelineBridgeRequestHandler Timeline { get; }

    internal TranscriptBridgeRequestHandler Transcript { get; }

    internal ReviewBridgeHandler Review { get; }

    internal ComparisonBridgeRequestHandler Comparison { get; }

    internal DeliveryBridgeRequestHandler Delivery { get; }
}

internal static class ProjectApplicationRuntime
{
    internal static ApplicationBridgeHandlers Create(
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
        var projectHandler = new ProjectBridgeHandler(service);
        var jobStore = new JobStore(databasePath);
        var jobCoordinator = new JobCoordinator(
            jobStore,
            new LocalJobRunner(jobStore));
        var jobHandler = new JobBridgeRequestHandler(
            jobCoordinator,
            new JobBridgeAuthorization(authorization, actorProvider),
            new DurableJobBridgeDetailsProvider(
                new IngestWorkflowStore(databasePath)));
        var transcriptHandler = new TranscriptBridgeRequestHandler(
            new TranscriptBridgeRuntime(),
            new TranscriptBridgeAuthorization(authorization, actorProvider));
        var projectStore = new ProjectStore(databasePath);
        var timelineHandler = new TimelineBridgeRequestHandler(
            projectStore,
            new TimelineBridgeAuthorization(authorization, actorProvider));
        var reviewStore = new ReviewStore(databasePath);
        var reviewHandler = new ReviewBridgeHandler(
            new ReviewWorkspaceService(
                projectStore,
                reviewStore,
                authorization,
                actorProvider.GetCurrentSid));
        var comparisonHandler = new ComparisonBridgeRequestHandler(
            projectStore,
            new ComparisonBridgeAuthorization(authorization, actorProvider));
        var deliveryHandler = new DeliveryBridgeRequestHandler(
            UnavailableDeliveryEvidenceProvider.Instance,
            new DeliveryAccessAuthorization(authorization, actorProvider));
        return new ApplicationBridgeHandlers(
            projectHandler,
            jobHandler,
            timelineHandler,
            transcriptHandler,
            reviewHandler,
            comparisonHandler,
            deliveryHandler);
    }
}
