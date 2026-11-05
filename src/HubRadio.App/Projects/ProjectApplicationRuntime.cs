using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.App.StyleTemplates;
using HubRadio.App.Web;
using HubRadio.Core.Security;
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
        SourceOnlyMediaResponseFactory? sourceOnlyMedia = null,
        BookendStitchBridgeHandler? stitch = null,
        WorkspaceLocationBridgeHandler? workspace = null,
        JobExportBridgeHandler? export = null,
        PortraitBridgeHandler? portraits = null,
        DisclosureBridgeHandler? disclosure = null,
        ProxyBridgeHandler? proxy = null)
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
        Stitch = stitch;
        Workspace = workspace;
        Export = export;
        Portraits = portraits;
        Disclosure = disclosure;
        Proxy = proxy;
    }

    /// <summary>FR-137 (6): registers and lists the user's standing pictures (none is bundled).</summary>
    internal PortraitBridgeHandler? Portraits { get; }

    /// <summary>NFR-BR-005: lists AI-generated elements and adds destination labels.</summary>
    internal DisclosureBridgeHandler? Disclosure { get; }

    /// <summary>FR-144: the settings screen's source-video proxy preview.</summary>
    internal ProxyBridgeHandler? Proxy { get; }

    /// <summary>FR-132 handler that saves a Job's videos and thumbnails to a user-chosen folder.</summary>
    internal JobExportBridgeHandler? Export { get; }

    /// <summary>FR-062 workspace location handler.</summary>
    internal WorkspaceLocationBridgeHandler? Workspace { get; }

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

    /// <summary>FR-035 GUI handler; null when the bundled tools are absent.</summary>
    internal BookendStitchBridgeHandler? Stitch { get; }

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
        string workspacePath = WorkspaceFolderBootstrap.Prepare(applicationRoot, actorSid);

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
        SourceOnlyApplicationResources resources = trustedResources ?? SourceOnlyApplicationResources.FromPackage(
            AppContext.BaseDirectory, profilesDirectory, defaultSettingsProfilesDirectory);
        SourceOnlyApplicationRuntime native = await SourceOnlyApplicationRuntime.CreateAsync(
            services,
            resources,
            cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateHandlers(native, authorization, actorProvider, catalog, picker, applicationRoot, actorSid,
                CreateStitchHandler(native.Services, authorization, actorProvider, new BookendStitchFilePicker(owner), resources),
                new WorkspaceFolderPicker(owner));
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
        string actorSid,
        BookendStitchBridgeHandler? stitch,
        IWorkspaceFolderPicker? workspaceFolderPicker = null)
    {
        SourceOnlyApplicationServices services = native.Services;
        var posters = new ProjectPosterStore(services.WorkspacePath);
        var autoSettings = new ProjectAutoSettingsStore(services.WorkspacePath);
        var templateAutoSelections = new TemplateAutoSelectionStore(Path.Combine(services.WorkspacePath, "template-auto-selection"));
        var characterPortraits = new CharacterPortraitStore(Path.Combine(services.WorkspacePath, "character-portraits"));
        // FR-144: the low-resolution proxy of the source video for the settings screen (Project folder once created).
        var proxies = new ProjectProxyService(services.WorkspacePath,
            (draftId, cancellationToken) => ((ISourceOnlyProjectCoordinator)native).ResolveProxySourceAsync(draftId, cancellationToken),
            draftId => services.Drafts.GetDraft(draftId)?.FinalizedProjectId);
        var posterAttempts = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
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
            sourceOnlyCoordinator: native)
        {
            ProjectFilesRemover = projectId =>
                ProjectWorkspaceFileRemover.Remove(services.WorkspacePath, projectId),
            ThumbnailResolver = new ProjectThumbnailResolver(services.Executions).Resolve,
            PosterResolver = draftId => posters.TryGetDigest(draftId)?.Hex,
            PosterRequester = draftId =>
            {
                // FR-053: one background attempt per Project and app session for posters missing
                // from Projects made before FR-053 (or whose first attempt failed).
                if (posterAttempts.TryAdd(draftId.ToString(), 0))
                {
                    _ = ((ISourceOnlyProjectCoordinator)native).EnsurePosterAsync(draftId, CancellationToken.None);
                }
            },
            PosterRemover = draftId =>
            {
                posters.Delete(draftId);
                autoSettings.Delete(draftId);
                templateAutoSelections.Delete(Guid.Parse(draftId.ToString()));
                new TemplateSpeechFactsStore(Path.Combine(services.WorkspacePath, "template-auto-selection")).Delete(Guid.Parse(draftId.ToString()));
            },
            TemplateAutoSelectionStarter = (draftId, enabled, facts) => _ = TemplateAutoSelectionRunner.Start(
                templateAutoSelections, Guid.Parse(draftId.ToString()), enabled,
                // FR-145 (1) / AC-186: the provisional selection uses length, aspect ratio and audio only and never waits for the transcript.
                cancellationToken => Task.FromResult<TemplateAnalysisInput?>(facts is null ? null
                    : new TemplateAnalysisInput(facts.DurationSeconds, facts.DisplayWidth, facts.DisplayHeight, facts.HasAudio)),
                () => characterPortraits.Count,
                speechFactsWait: (input, keepWaiting, cancellationToken) =>
                    // FR-145 (2) / AC-186: speakers (FR-003) and speech speed (FR-002) come from the transcript the Job produces, on this machine, once it
                    // is done; the wait counts from the start of the transcription and ends at "source length × PARAM-014 + PARAM-015" (then 不明).
                    new TemplateSpeechFactsStore(Path.Combine(services.WorkspacePath, "template-auto-selection"))
                        .WaitAfterTranscriptionStartAsync(Guid.Parse(draftId.ToString()), input.DurationSeconds, cancellationToken, keepWaiting)),
            ProxyRequester = proxies.Request,
            ProxyFinalizer = proxies.OnFinalized,
            ProxyRemover = proxies.Delete,
            AutoSettingsRecorder = autoSettings.Save,
            AutoSettingsReader = autoSettings.Read,
            SourceMediaRecorder = (id, path, facts) => autoSettings.SaveSource(id, SourceMediaNote.Create(path, facts, Environment.UserName)),
            SourceMediaReader = autoSettings.ReadSource,
            WorkspacePath = services.WorkspacePath,
        };
        // FR-134: GUID-named Project folders become Project-name folders at start (rename only; safe to repeat).
        _ = service.MigrateProjectFolders();
        var projectHandler = new ProjectBridgeHandler(service, native.RestoreAfterReadAsync, native.RepairAsync);
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
            authorization)
        {
            Posters = posters,
            Proxies = proxies,
            Drafts = new ProjectCreationDraftStore(services.DatabasePath),
        };
        var proxyHandler = new ProxyBridgeHandler(proxies,
            () => authorization.CanPerform(actorProvider.GetCurrentSid(), AuthorizationOperation.PreviewProject),
            draftId => services.Drafts.GetDraft(draftId) is not null);
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
            sourceOnlyMedia,
            stitch,
            new WorkspaceLocationBridgeHandler(
                new WorkspaceLocationStore(applicationRoot),
                settingsAuthorization,
                services.WorkspacePath,
                workspaceFolderPicker),
            JobExportSources.CreateHandler(
                services.Executions, services.Results, services.Artifacts, services.WorkspacePath,
                actorProvider.GetCurrentSid, authorization),
            new PortraitBridgeHandler(
                new CharacterPortraitStore(Path.Combine(services.WorkspacePath, "character-portraits")),
                () => authorization.CanPerform(actorProvider.GetCurrentSid(), AuthorizationOperation.ExportComposition),
                TimeProvider.System,
                Path.Combine(services.WorkspacePath, "project-speaker-assignments"),
                new TemplateAutoSelectionAccess(
                    projectId => ReadAutoSelection(services, templateAutoSelections, projectId, touch: false),
                    projectId => ReadAutoSelection(services, templateAutoSelections, projectId, touch: true),
                    projectId => ReadLatestJobOrigin(services, projectId))),
            new DisclosureBridgeHandler(
                new HubRadio.App.Audit.AiGenerationDisclosureService(reviewStore),
                authorization,
                actorProvider.GetCurrentSid),
            proxyHandler);
    }

    /// <summary>FR-145: the origin (auto / manual / none) recorded for the Project's latest Job, or null when none is recorded yet.</summary>
    private static string? ReadLatestJobOrigin(SourceOnlyApplicationServices services, Guid projectId)
    {
        StoredSourceOnlyExecution? latest = services.Executions.GetLatestForProject(EntityId<HubRadio.Core.Projects.ProjectIdTag>.Parse(projectId.ToString("D")));
        return latest is null
            ? null
            : JobTemplateOriginStore.Load(Path.Combine(services.WorkspacePath, "job-style-templates", latest.Request.RootJobId.ToString()))?.Origin;
    }

    /// <summary>FR-145: the automatic selection of a Project (resolved through the Project's first execution to its creation draft).</summary>
    private static TemplateAutoSelectionRecord? ReadAutoSelection(SourceOnlyApplicationServices services,
        TemplateAutoSelectionStore store, Guid projectId, bool touch)
    {
        StoredSourceOnlyExecution? latest = services.Executions.GetLatestForProject(EntityId<HubRadio.Core.Projects.ProjectIdTag>.Parse(projectId.ToString("D")));
        if (latest is null || !Guid.TryParse(latest.Request.DraftId.ToString(), out Guid key))
        {
            return null;
        }

        return touch ? store.Touch(key) : store.Load(key);
    }

    // FR-035: without both bundled tools the GUI reports unavailable instead of failing later.
    internal static BookendStitchBridgeHandler? CreateStitchHandler(
        SourceOnlyApplicationServices services,
        AuthorizationService authorization,
        WindowsActorProvider actorProvider,
        IBookendStitchFilePicker picker,
        SourceOnlyApplicationResources resources) =>
        File.Exists(resources.FfmpegPath) && File.Exists(resources.FfprobePath)
            ? new BookendStitchBridgeHandler(services.Executions, services.Results, services.Artifacts,
                actorProvider.GetCurrentSid, authorization, picker, resources.FfmpegPath, resources.FfprobePath)
            : null;

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
