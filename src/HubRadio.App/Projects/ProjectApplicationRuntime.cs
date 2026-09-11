using HubRadio.App.DefaultSettings;
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
using HubRadio.Infrastructure.Settings;
using Microsoft.UI.Xaml;

namespace HubRadio.App.Projects;

internal sealed class ApplicationBridgeHandlers : IAsyncDisposable
{
    internal ApplicationBridgeHandlers(
        ProjectBridgeHandler project,
        JobBridgeRequestHandler jobs,
        TimelineBridgeRequestHandler timeline,
        TranscriptBridgeRequestHandler transcript,
        ReviewBridgeHandler review,
        ComparisonBridgeRequestHandler comparison,
        DeliveryBridgeRequestHandler delivery,
        AnalysisBridgeRequestHandler analysis,
        LayoutSubtitleBridgeRequestHandler layoutSubtitles,
        SettingsBridgeRequestHandler settings,
        GlobalSettingsBridgeRequestHandler globalSettings,
        SettingsFileBridgeRequestHandler fileSettings,
        SourceOnlyApplicationRuntime? sourceOnlyRuntime = null,
        SourceOnlyBridgeHandler? sourceOnly = null,
        SourceOnlyMediaResponseFactory? sourceOnlyMedia = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(layoutSubtitles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(globalSettings);
        ArgumentNullException.ThrowIfNull(fileSettings);
        Project = project;
        Jobs = jobs;
        Timeline = timeline;
        Transcript = transcript;
        Review = review;
        Comparison = comparison;
        Delivery = delivery;
        Analysis = analysis;
        LayoutSubtitles = layoutSubtitles;
        Settings = settings;
        GlobalSettings = globalSettings;
        FileSettings = fileSettings;
        SourceOnlyRuntime = sourceOnlyRuntime;
        SourceOnly = sourceOnly;
        SourceOnlyMedia = sourceOnlyMedia;
    }

    internal ProjectBridgeHandler Project { get; }

    internal JobBridgeRequestHandler Jobs { get; }

    internal TimelineBridgeRequestHandler Timeline { get; }

    internal TranscriptBridgeRequestHandler Transcript { get; }

    internal ReviewBridgeHandler Review { get; }

    internal ComparisonBridgeRequestHandler Comparison { get; }

    internal DeliveryBridgeRequestHandler Delivery { get; }

    internal AnalysisBridgeRequestHandler Analysis { get; }

    internal LayoutSubtitleBridgeRequestHandler LayoutSubtitles { get; }

    internal SettingsBridgeRequestHandler Settings { get; }

    internal GlobalSettingsBridgeRequestHandler GlobalSettings { get; }

    internal SettingsFileBridgeRequestHandler FileSettings { get; }

    internal SourceOnlyApplicationRuntime? SourceOnlyRuntime { get; }

    internal SourceOnlyBridgeHandler? SourceOnly { get; }

    internal SourceOnlyMediaResponseFactory? SourceOnlyMedia { get; }

    internal SourceOnlyPipeline? SourceOnlyPipeline => SourceOnlyRuntime?.Pipeline;

    internal SourceOnlyResultService? SourceOnlyResults => SourceOnlyRuntime?.Services.Results;

    internal Task<SourceOnlyRecoveryResult> RecoverSourceOnlyAsync(CancellationToken cancellationToken) =>
        SourceOnlyRuntime?.RecoverAsync(cancellationToken)
            ?? Task.FromResult(new SourceOnlyRecoveryResult([], false, null));

    public ValueTask DisposeAsync() => SourceOnlyRuntime?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal static class ProjectApplicationRuntime
{
    internal static async Task<ApplicationBridgeHandlers> CreateAsync(
        Window owner,
        string applicationDataDirectory,
        string profilesDirectory,
        string defaultSettingsProfilesDirectory,
        CancellationToken cancellationToken = default,
        SourceOnlyApplicationResources? trustedResources = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultSettingsProfilesDirectory);
        cancellationToken.ThrowIfCancellationRequested();

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
        var defaultSettingsStore = new DefaultSettingsStore(databasePath);
        _ = new BundledDefaultSettingsBootstrap(
            defaultSettingsStore).Initialize(
                defaultSettingsProfilesDirectory,
                DateTimeOffset.UtcNow);
        var authorization = new AuthorizationService(
            new RoleAssignmentStore(databasePath));
        _ = authorization.BootstrapInitialRoles(actorSid);

        var registry = new AssetCandidateRegistry();
        var picker = new MediaPicker(owner, registry);
        ProjectCreationCatalog catalog = ProjectCreationCatalog.Load(profilesDirectory);
        var services = new SourceOnlyApplicationServices(databasePath, workspacePath, registry, defaultSettingsStore);
        SourceOnlyApplicationRuntime native = await SourceOnlyApplicationRuntime.CreateAsync(
            services,
            trustedResources ?? SourceOnlyApplicationResources.FromPackage(
                AppContext.BaseDirectory, profilesDirectory, defaultSettingsProfilesDirectory),
            cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateHandlers(native, authorization, actorProvider, catalog, picker, applicationRoot, actorSid);
        }
        catch
        {
            await native.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ApplicationBridgeHandlers CreateHandlers(
        SourceOnlyApplicationRuntime native,
        AuthorizationService authorization,
        WindowsActorProvider actorProvider,
        ProjectCreationCatalog catalog,
        MediaPicker picker,
        string applicationRoot,
        string actorSid)
    {
        SourceOnlyApplicationServices services = native.Services;
        var service = new ProjectCreationService(
            services.Drafts,
            authorization,
            services.Candidates,
            catalog,
            services.Settings,
            actorProvider.GetCurrentSid,
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await picker.PickAssetCandidateAsync();
            },
            // Even a missing resource has a non-null coordinator: Start fails closed
            // rather than finalizing an empty project through the legacy fallback.
            sourceOnlyCoordinator: native);
        var projectHandler = new ProjectBridgeHandler(service, native.RestoreAfterReadAsync);
        var jobHandler = new JobBridgeRequestHandler(
            services.Coordinator,
            new JobBridgeAuthorization(authorization, actorProvider),
            new DurableJobBridgeDetailsProvider(
                new IngestWorkflowStore(services.DatabasePath)));
        var transcriptHandler = new TranscriptBridgeRequestHandler(
            services.Registrations.Transcript,
            new TranscriptBridgeAuthorization(authorization, actorProvider));
        var analysisHandler = new AnalysisBridgeRequestHandler(
            services.Registrations.Analysis,
            new AnalysisBridgeAuthorization(authorization, actorProvider));
        var timelineHandler = new TimelineBridgeRequestHandler(
            services.Projects,
            new TimelineBridgeAuthorization(authorization, actorProvider));
        var reviewStore = new ReviewStore(services.DatabasePath);
        var reviewHandler = new ReviewBridgeHandler(
            new ReviewWorkspaceService(
                services.Projects,
                reviewStore,
                authorization,
                actorProvider.GetCurrentSid));
        var comparisonHandler = new ComparisonBridgeRequestHandler(
            services.Projects,
            new ComparisonBridgeAuthorization(authorization, actorProvider));
        var deliveryHandler = new DeliveryBridgeRequestHandler(
            // This legacy API requires separate manual-review approval evidence.
            // Source-only delivery uses the real SourceOnlyDeliveryService instead;
            // do not manufacture that older approval just to populate its editor.
            UnavailableDeliveryEvidenceProvider.Instance,
            new DeliveryAccessAuthorization(authorization, actorProvider));
        var layoutSubtitleHandler = new LayoutSubtitleBridgeRequestHandler(
            services.Registrations,
            new LayoutSubtitleBridgeAuthorization(
                authorization,
                actorProvider));
        var settingsAuthorization = new SettingsBridgeAuthorization(
            authorization,
            actorProvider);
        var settingsHandlers = CreateSettingsHandlers(
            services.Settings,
            settingsAuthorization,
            applicationRoot,
            actorSid);
        var sourceOnlyHandler = new SourceOnlyBridgeHandler(
            () => native.ReadyPipeline,
            services.Executions,
            services.Settings,
            services.Results,
            services.Projects,
            actorProvider.GetCurrentSid,
            authorization);
        var sourceOnlyMedia = new SourceOnlyMediaResponseFactory(
            services.Executions,
            services.Results,
            services.Artifacts,
            actorProvider.GetCurrentSid,
            authorization);
        return new ApplicationBridgeHandlers(
            projectHandler,
            jobHandler,
            timelineHandler,
            transcriptHandler,
            reviewHandler,
            comparisonHandler,
            deliveryHandler,
            analysisHandler,
            layoutSubtitleHandler,
            settingsHandlers.Settings,
            settingsHandlers.GlobalSettings,
            settingsHandlers.FileSettings,
            native,
            sourceOnlyHandler,
            sourceOnlyMedia);
    }

    internal static (
        SettingsBridgeRequestHandler Settings,
        GlobalSettingsBridgeRequestHandler GlobalSettings,
        SettingsFileBridgeRequestHandler FileSettings) CreateSettingsHandlers(
        DefaultSettingsStore defaultSettingsStore,
        SettingsBridgeAuthorization settingsAuthorization,
        string applicationRoot,
        string actorSid) =>
        (
            new SettingsBridgeRequestHandler(defaultSettingsStore, settingsAuthorization),
            new GlobalSettingsBridgeRequestHandler(defaultSettingsStore, settingsAuthorization),
            new SettingsFileBridgeRequestHandler(
                defaultSettingsStore,
                settingsAuthorization,
                // File-only root/SID checks must not prevent opening the existing DB.
                () => new SettingsTextFile(applicationRoot, actorSid))
        );
}
