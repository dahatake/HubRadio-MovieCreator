using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Media;
using CoreProjectSnapshotJson = HubRadio.Core.Projects.ProjectSnapshotJson;

namespace HubRadio.App.Projects;

internal enum ProjectCreationFailureReason
{
    DraftNotFound,
    DraftAlreadyFinalized,
    DraftIncomplete,
    SourceReselectionRequired,
    DeleteBlockedByRunningJob,
}

internal sealed class ProjectCreationException : InvalidOperationException
{
    internal ProjectCreationException(ProjectCreationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    internal ProjectCreationFailureReason Reason { get; }

    private static string GetStableMessage(ProjectCreationFailureReason reason) =>
        reason switch
        {
            ProjectCreationFailureReason.DraftNotFound =>
                "The project creation draft does not exist.",
            ProjectCreationFailureReason.DraftAlreadyFinalized =>
                "The project creation draft is already finalized.",
            ProjectCreationFailureReason.DraftIncomplete =>
                "The project creation draft is incomplete.",
            ProjectCreationFailureReason.SourceReselectionRequired =>
                "The source media must be selected again.",
            ProjectCreationFailureReason.DeleteBlockedByRunningJob =>
                "The project cannot be deleted while a job is running.",
            _ => "The project creation operation failed.",
        };
}

internal sealed record ProjectListItem(
    string DraftId,
    string? ProjectId,
    string State,
    string? ContentName,
    string SourceStatus,
    string? PublicationScope,
    IReadOnlyList<string> OutputFormats,
    string UpdatedAt);

internal sealed record ProjectCreationOverview(
    IReadOnlyList<ProjectListItem> Projects,
    IReadOnlyList<ProjectCatalogOption> OutputProfiles,
    IReadOnlyList<ProjectCatalogOption> BrandTemplates,
    bool CanCreate);

internal sealed record ProjectCreationDraftView(
    string DraftId,
    int Revision,
    bool IsFinalized,
    string? ProjectId,
    string? SourceCandidateId,
    string SourceStatus,
    string? ContentName,
    string? Purpose,
    string? Audience,
    string? PublicationScope,
    IReadOnlyList<string> OutputProfileIds,
    string? Language,
    IReadOnlyList<string> PerformerNames,
    bool? HasDeadline,
    string? Deadline,
    string? BrandTemplateId,
    IReadOnlyList<string> MissingFields,
    string UpdatedAt,
    SourceOnlyProjectSummary? SourceOnly);

internal sealed record SourceOnlyProjectSummary(
    string ProfileId,
    string ProfileVersion,
    string ProfileDigest,
    string SettingsSnapshotDigest,
    IReadOnlyList<string> ArtifactSet);

internal sealed class ProjectCreationService
{
    private readonly ProjectCreationDraftStore draftStore;
    private readonly AuthorizationService authorizationService;
    private readonly AssetCandidateRegistry candidateRegistry;
    private readonly ProjectCreationCatalog catalog;
    private readonly DefaultSettingsStore? settingsStore;
    private readonly ISourceOnlyProjectCoordinator? sourceOnlyCoordinator;
    private readonly Func<string> getCurrentActorSid;
    private readonly Func<CancellationToken, Task<EntityId<AssetIdTag>?>> pickSourceAsync;
    private readonly TimeProvider timeProvider;

    internal ProjectCreationService(
        ProjectCreationDraftStore draftStore,
        AuthorizationService authorizationService,
        AssetCandidateRegistry candidateRegistry,
        ProjectCreationCatalog catalog,
        Func<string> getCurrentActorSid,
        Func<CancellationToken, Task<EntityId<AssetIdTag>?>> pickSourceAsync,
        TimeProvider? timeProvider = null,
        ISourceOnlyProjectCoordinator? sourceOnlyCoordinator = null)
        : this(
            draftStore,
            authorizationService,
            candidateRegistry,
            catalog,
            settingsStore: null,
            getCurrentActorSid,
            pickSourceAsync,
            timeProvider,
            sourceOnlyCoordinator,
            initialize: true)
    {
    }

    internal ProjectCreationService(
        ProjectCreationDraftStore draftStore,
        AuthorizationService authorizationService,
        AssetCandidateRegistry candidateRegistry,
        ProjectCreationCatalog catalog,
        DefaultSettingsStore settingsStore,
        Func<string> getCurrentActorSid,
        Func<CancellationToken, Task<EntityId<AssetIdTag>?>> pickSourceAsync,
        TimeProvider? timeProvider = null,
        ISourceOnlyProjectCoordinator? sourceOnlyCoordinator = null)
        : this(
            draftStore,
            authorizationService,
            candidateRegistry,
            catalog,
            settingsStore ?? throw new ArgumentNullException(nameof(settingsStore)),
            getCurrentActorSid,
            pickSourceAsync,
            timeProvider,
            sourceOnlyCoordinator,
            initialize: true)
    {
    }

    private ProjectCreationService(
        ProjectCreationDraftStore draftStore,
        AuthorizationService authorizationService,
        AssetCandidateRegistry candidateRegistry,
        ProjectCreationCatalog catalog,
        DefaultSettingsStore? settingsStore,
        Func<string> getCurrentActorSid,
        Func<CancellationToken, Task<EntityId<AssetIdTag>?>> pickSourceAsync,
        TimeProvider? timeProvider,
        ISourceOnlyProjectCoordinator? sourceOnlyCoordinator,
        bool initialize)
    {
        _ = initialize;
        ArgumentNullException.ThrowIfNull(draftStore);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(candidateRegistry);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(getCurrentActorSid);
        ArgumentNullException.ThrowIfNull(pickSourceAsync);

        this.draftStore = draftStore;
        this.authorizationService = authorizationService;
        this.candidateRegistry = candidateRegistry;
        this.catalog = catalog;
        this.settingsStore = settingsStore;
        this.sourceOnlyCoordinator = sourceOnlyCoordinator;
        this.getCurrentActorSid = getCurrentActorSid;
        this.pickSourceAsync = pickSourceAsync;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ProjectCreationOverview ListProjects()
    {
        string actorSid = Demand(AuthorizationOperation.ViewProject);
        ProjectListItem[] projects = draftStore.ListDrafts()
            .Select(ReadAndProjectListItem)
            .ToArray();
        return new ProjectCreationOverview(
            Array.AsReadOnly(projects),
            catalog.OutputProfiles,
            catalog.BrandTemplates,
            authorizationService.CanPerform(
                actorSid,
                AuthorizationOperation.CreateProject));
    }

    internal ProjectCreationDraftView CreateDraft()
    {
        _ = Demand(AuthorizationOperation.CreateProject);
        var draft = new ProjectCreationDraft(
            EntityId<ProjectCreationDraftIdTag>.New(),
            sourceCandidateId: null,
            contentName: null,
            purpose: null,
            audience: null,
            publicationScope: null,
            outputProfiles: [],
            language: null,
            performerNames: [],
            deadlineSelection: null,
            brandTemplate: null);
        StoredProjectCreationDraft stored = draftStore.CreateDraft(
            draft,
            ProjectCreationPayloadCodec.Serialize(draft),
            GetUtcNow());
        return BuildView(stored, draft, sourceOnly: null);
    }

    internal async Task<ProjectCreationDraftView> CreateSourceOnlyDraftAsync(
        CancellationToken cancellationToken = default)
    {
        string actorSid = Demand(AuthorizationOperation.CreateProject);
        DefaultSettingsStore requiredSettingsStore = settingsStore
            ?? throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.SettingsUnavailable);
        cancellationToken.ThrowIfCancellationRequested();
        EntityId<AssetIdTag>? candidateId =
            await pickSourceAsync(cancellationToken).ConfigureAwait(true);
        if (candidateId is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.SourceSelectionCanceled);
        }

        if (!candidateRegistry.TryReserve(candidateId, out var reservation))
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.SourceReselectionRequired);
        }

        SourceOnlyProjectCreationResolution resolution;
        StoredProjectCreationDraft stored;
        var draftId = EntityId<ProjectCreationDraftIdTag>.New();
        using (reservation)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourceOnlyCoordinator is null && !File.Exists(reservation.SelectedPath))
                {
                    throw new ProjectCreationException(
                        ProjectCreationFailureReason.SourceReselectionRequired);
                }

                resolution = SourceOnlyProjectCreationContract.Resolve(
                    draftId,
                    candidateId,
                    reservation.SelectedPath,
                    requiredSettingsStore.GetGlobalSettings(),
                    catalog);
                if (sourceOnlyCoordinator is not null)
                {
                    await sourceOnlyCoordinator.CaptureSelectionAsync(draftId, candidateId,
                        reservation.SelectedPath, actorSid, GetUtcNow(), cancellationToken).ConfigureAwait(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                stored = draftStore.CreateDraft(
                    resolution.Draft,
                    SourceOnlyProjectCreationContract.Serialize(resolution),
                    GetUtcNow());
            }
            catch
            {
                reservation.Discard();
                sourceOnlyCoordinator?.ForgetSelection(draftId);
                throw;
            }
        }

        return BuildView(stored, resolution.Draft, resolution);
    }

    internal ProjectCreationDraftView LoadDraft(
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = Demand(AuthorizationOperation.ViewProject);
        StoredProjectCreationDraft stored = GetRequiredDraft(draftId);
        StoredProjectPayload payload = ParseStored(stored);
        return BuildView(stored, payload.Draft, payload.SourceOnly);
    }

    /// <summary>
    /// Removes a finalized project's workspace files after the deletion is recorded.
    /// Returns true when nothing is left; null means the host does not manage files.
    /// </summary>
    internal Func<EntityId<ProjectIdTag>, bool>? ProjectFilesRemover { get; set; }

    /// <returns>False when the project is hidden but some of its workspace files could not be removed.</returns>
    internal bool DeleteProject(
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = Demand(AuthorizationOperation.DeleteProject);
        EntityId<ProjectIdTag>? projectId = GetRequiredDraft(draftId).FinalizedProjectId;
        if (!draftStore.DeleteDraft(draftId, GetUtcNow()))
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.DeleteBlockedByRunningJob);
        }

        return projectId is null || ProjectFilesRemover is null || ProjectFilesRemover(projectId);
    }

    internal ProjectCreationDraftView SaveDraft(
        EntityId<ProjectCreationDraftIdTag> draftId,
        int expectedRevision,
        JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = Demand(AuthorizationOperation.CreateProject);
        StoredProjectCreationDraft current = GetRequiredDraft(draftId);
        EnsureMutable(current);
        if (SourceOnlyProjectCreationContract.IsSourceOnlyPayload(
            current.CanonicalJson))
        {
            throw new ArgumentException(
            "Source-only project defaults cannot be changed through the legacy draft API.",
            nameof(input));
        }

        ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseInput(
            input,
            draftId,
            catalog);
        StoredProjectCreationDraft stored = draftStore.SaveDraft(
            draft,
            ProjectCreationPayloadCodec.Serialize(draft),
            expectedRevision,
            GetUtcNow());
        return BuildView(stored, draft, sourceOnly: null);
    }

    internal ProjectCreationDraftView FinalizeDraft(
        EntityId<ProjectCreationDraftIdTag> draftId,
        int expectedRevision,
        JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        string actorSid = Demand(AuthorizationOperation.CreateProject);
        StoredProjectCreationDraft current = GetRequiredDraft(draftId);
        if (SourceOnlyProjectCreationContract.IsSourceOnlyPayload(
            current.CanonicalJson))
        {
            if (sourceOnlyCoordinator is null) { EnsureMutable(current); }
            return FinalizeSourceOnlyDraft(
            current,
            expectedRevision,
            input,
            actorSid);
        }

        EnsureMutable(current);
        ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseInput(
            input,
            draftId,
            catalog);
        if (!draft.IsComplete)
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.DraftIncomplete);
        }

        if (!candidateRegistry.TryReserve(draft.SourceCandidateId!, out var reservation))
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.SourceReselectionRequired);
        }

        using (reservation)
        {
            if (!File.Exists(reservation.SelectedPath))
            {
                throw new ProjectCreationException(
                    ProjectCreationFailureReason.SourceReselectionRequired);
            }

            var project = new Project(
                EntityId<ProjectIdTag>.New(),
                draft.ContentName!,
                draft.Purpose!,
                draft.Audience!,
                draft.PublicationScope!.Value,
                draft.DeadlineSelection!.Deadline,
                assets: [],
                performers: []);
            StoredProjectCreationDraft stored = draftStore.FinalizeDraft(
                draft,
                project,
                ProjectCreationPayloadCodec.Serialize(draft),
                CoreProjectSnapshotJson.WriteProject(project),
                expectedRevision,
                GetUtcNow());
            reservation.Commit();
            return BuildView(stored, draft, sourceOnly: null);
        }
    }

    internal async Task<EntityId<AssetIdTag>?> PickSourceAsync(
        CancellationToken cancellationToken = default)
    {
        _ = Demand(AuthorizationOperation.CreateProject);
        cancellationToken.ThrowIfCancellationRequested();
        EntityId<AssetIdTag>? candidateId =
            await pickSourceAsync(cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested)
        {
            if (candidateId is not null &&
                candidateRegistry.TryReserve(candidateId, out var reservation))
            {
                using (reservation)
                {
                    reservation.Discard();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (candidateId is not null && !candidateRegistry.IsAvailable(candidateId))
        {
            throw new InvalidOperationException(
                "The selected source token is not registered.");
        }

        return candidateId;
    }

    private ProjectListItem ReadAndProjectListItem(StoredProjectCreationDraft stored)
    {
        StoredProjectPayload payload = ParseStored(stored);
        ProjectCreationDraft draft = payload.Draft;
        return new ProjectListItem(
            draft.Id.ToString(),
            stored.FinalizedProjectId?.ToString(),
            stored.IsFinalized ? "finalized" : "draft",
            draft.ContentName,
            GetSourceStatus(draft, stored.IsFinalized, payload.SourceOnly is not null),
            draft.PublicationScope is PublicationScope scope
                ? ProjectCreationPayloadCodec.ToWireName(scope)
                : null,
            Array.AsReadOnly(draft.OutputProfiles
                .Select(static profile => profile.Name)
                .ToArray()),
            ProjectCreationPayloadCodec.FormatTimestamp(stored.UpdatedAt));
    }

    private ProjectCreationDraftView BuildView(
        StoredProjectCreationDraft stored,
        ProjectCreationDraft draft,
        SourceOnlyProjectCreationResolution? sourceOnly)
    {
        if (stored.Id != draft.Id)
        {
            throw new InvalidOperationException(
                "The stored project creation draft identity is invalid.");
        }

        return new ProjectCreationDraftView(
            draft.Id.ToString(),
            stored.Revision,
            stored.IsFinalized,
            stored.FinalizedProjectId?.ToString(),
            draft.SourceCandidateId?.ToString(),
            GetSourceStatus(draft, stored.IsFinalized, sourceOnly is not null),
            draft.ContentName,
            draft.Purpose,
            draft.Audience,
            draft.PublicationScope is PublicationScope scope
                ? ProjectCreationPayloadCodec.ToWireName(scope)
                : null,
            Array.AsReadOnly(draft.OutputProfiles
                .Select(static profile => profile.Id.ToString())
                .ToArray()),
            draft.Language,
            Array.AsReadOnly(draft.PerformerNames.ToArray()),
            draft.DeadlineSelection is null
                ? null
                : draft.DeadlineSelection.Deadline is not null,
            draft.DeadlineSelection?.Deadline is DateTimeOffset deadline
                ? ProjectCreationPayloadCodec.FormatTimestamp(deadline)
                : null,
            draft.BrandTemplate?.Id.ToString(),
            Array.AsReadOnly(draft.MissingFields
                .Select(ProjectCreationPayloadCodec.ToWireName)
                .ToArray()),
            ProjectCreationPayloadCodec.FormatTimestamp(stored.UpdatedAt),
            sourceOnly is null
                ? null
                : CreateSourceOnlySummary(
                    sourceOnly.SettingsSnapshot,
                    sourceOnly.ArtifactSet));
    }

    private StoredProjectPayload ParseStored(StoredProjectCreationDraft stored)
    {
        StoredProjectPayload payload;
        if (SourceOnlyProjectCreationContract.IsSourceOnlyPayload(stored.CanonicalJson))
        {
            DefaultSettingsStore requiredSettingsStore = settingsStore
                ?? throw new SourceOnlyProjectCreationException(
                    SourceOnlyProjectCreationFailureReason.SettingsUnavailable);
            StoredSourceOnlyProjectCreation sourceOnly =
                SourceOnlyProjectCreationContract.ParseStored(
                    stored.CanonicalJson,
                    catalog,
                    requiredSettingsStore);
            payload = new StoredProjectPayload(
                sourceOnly.Draft,
                new SourceOnlyProjectCreationResolution(
                    sourceOnly.Draft,
                    sourceOnly.SettingsSnapshot,
                    sourceOnly.ArtifactSet));
        }
        else
        {
            payload = new StoredProjectPayload(
                ProjectCreationPayloadCodec.ParseStored(
                    stored.CanonicalJson,
                    catalog),
                SourceOnly: null);
        }

        ProjectCreationDraft draft = payload.Draft;
        if (draft.Id != stored.Id)
        {
            throw new InvalidOperationException(
                "The stored project creation draft identity is invalid.");
        }

        return payload;
    }

    private ProjectCreationDraftView FinalizeSourceOnlyDraft(
        StoredProjectCreationDraft current,
        int expectedRevision,
        JsonElement input,
        string actorSid)
    {
        StoredProjectPayload payload = ParseStored(current);
        SourceOnlyProjectCreationResolution sourceOnly = payload.SourceOnly
            ?? throw new InvalidOperationException(
                "The stored source-only project creation metadata is invalid.");
        ProjectCreationDraft submitted = ProjectCreationPayloadCodec.ParseInput(
            input,
            current.Id,
            catalog);
        if (!HasSameSourceOnlyDefaults(submitted, payload.Draft))
        {
            throw new ArgumentException(
                "Source-only project defaults cannot be changed during finalization.",
                nameof(input));
        }

        if (submitted.SourceCandidateId is null)
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.SourceReselectionRequired);
        }

        ProjectCreationDraft draft = submitted;
        if (sourceOnlyCoordinator is not null)
        {
            // Native execution binds the ORIGINAL picker identity, not a same-name file
            // or a replacement opaque token. Legacy reselection behavior remains below.
            if (draft.SourceCandidateId != payload.Draft.SourceCandidateId)
            {
                throw new ProjectCreationException(ProjectCreationFailureReason.SourceReselectionRequired);
            }
            var acceptedSourceOnly = new SourceOnlyProjectCreationResolution(
                draft, sourceOnly.SettingsSnapshot, sourceOnly.ArtifactSet);
            StoredProjectCreationDraft accepted = sourceOnlyCoordinator.Accept(
                draft, sourceOnly.SettingsSnapshot,
                SourceOnlyProjectCreationContract.Serialize(acceptedSourceOnly),
                expectedRevision, actorSid, GetUtcNow());
            return BuildView(accepted, draft, acceptedSourceOnly);
        }

        if (!candidateRegistry.TryReserve(draft.SourceCandidateId!, out var reservation))
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.SourceReselectionRequired);
        }

        using (reservation)
        {
            if (!File.Exists(reservation.SelectedPath))
            {
                throw new ProjectCreationException(
                    ProjectCreationFailureReason.SourceReselectionRequired);
            }

            if (!string.Equals(
                    SourceOnlyProjectCreationContract.DeriveContentName(
                        reservation.SelectedPath),
                    draft.ContentName,
                    StringComparison.Ordinal))
            {
                throw new ProjectCreationException(
                    ProjectCreationFailureReason.SourceReselectionRequired);
            }

            var project = new Project(
                EntityId<ProjectIdTag>.New(),
                draft.ContentName!,
                draft.Purpose!,
                draft.Audience!,
                draft.PublicationScope!.Value,
                draft.DeadlineSelection!.Deadline,
                assets: [],
                performers: []);
            var finalizedSourceOnly = new SourceOnlyProjectCreationResolution(
                draft,
                sourceOnly.SettingsSnapshot,
                sourceOnly.ArtifactSet);
            StoredProjectCreationDraft finalized = draftStore.FinalizeDraft(
                draft,
                project,
                SourceOnlyProjectCreationContract.Serialize(finalizedSourceOnly),
                CoreProjectSnapshotJson.WriteProject(project),
                expectedRevision,
                GetUtcNow(),
                sourceOnly.SettingsSnapshot);
            reservation.Commit();
            return BuildView(finalized, draft, finalizedSourceOnly);
        }
    }

    private static bool HasSameSourceOnlyDefaults(
        ProjectCreationDraft submitted,
        ProjectCreationDraft stored) =>
        string.Equals(submitted.ContentName, stored.ContentName, StringComparison.Ordinal) &&
        string.Equals(submitted.Purpose, stored.Purpose, StringComparison.Ordinal) &&
        string.Equals(submitted.Audience, stored.Audience, StringComparison.Ordinal) &&
        submitted.PublicationScope == stored.PublicationScope &&
        submitted.OutputProfiles.SequenceEqual(stored.OutputProfiles) &&
        string.Equals(submitted.Language, stored.Language, StringComparison.Ordinal) &&
        submitted.PerformerNames.SequenceEqual(
            stored.PerformerNames,
            StringComparer.Ordinal) &&
        Equals(submitted.DeadlineSelection, stored.DeadlineSelection) &&
        Equals(submitted.BrandTemplate, stored.BrandTemplate);

    private static SourceOnlyProjectSummary CreateSourceOnlySummary(
        HubRadio.Core.Settings.DefaultSettingsSnapshot snapshot,
        IReadOnlyList<string> artifactSet) =>
        new(
            snapshot.ProfileId.ToString(),
            snapshot.ProfileVersion.ToString(),
            snapshot.ProfileDigest.Hex,
            snapshot.ContentDigest.Hex,
            Array.AsReadOnly(artifactSet.ToArray()));

    private sealed record StoredProjectPayload(
        ProjectCreationDraft Draft,
        SourceOnlyProjectCreationResolution? SourceOnly);

    private StoredProjectCreationDraft GetRequiredDraft(
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        return draftStore.GetDraft(draftId)
            ?? throw new ProjectCreationException(
                ProjectCreationFailureReason.DraftNotFound);
    }

    private static void EnsureMutable(StoredProjectCreationDraft stored)
    {
        if (stored.IsFinalized)
        {
            throw new ProjectCreationException(
                ProjectCreationFailureReason.DraftAlreadyFinalized);
        }
    }

    private string GetSourceStatus(
        ProjectCreationDraft draft,
        bool isFinalized,
        bool isSourceOnly)
    {
        if (draft.SourceCandidateId is null) { return "notSelected"; }
        if (isFinalized) { return "available"; }
        bool available = isSourceOnly && sourceOnlyCoordinator is not null
            ? sourceOnlyCoordinator.RestoreSelection(draft.Id, draft.SourceCandidateId)
            : candidateRegistry.IsAvailable(draft.SourceCandidateId);
        return available ? "available" : "reselectionRequired";
    }

    private string Demand(AuthorizationOperation operation)
    {
        string actorSid = getCurrentActorSid();
        authorizationService.Demand(actorSid, operation);
        return actorSid;
    }

    private DateTimeOffset GetUtcNow() =>
        timeProvider.GetUtcNow().ToUniversalTime();
}