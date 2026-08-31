using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Projects;

internal enum ProjectCreationFailureReason
{
    DraftNotFound,
    DraftAlreadyFinalized,
    DraftIncomplete,
    SourceReselectionRequired,
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
    string UpdatedAt);

internal sealed class ProjectCreationService
{
    private readonly ProjectCreationDraftStore draftStore;
    private readonly AuthorizationService authorizationService;
    private readonly AssetCandidateRegistry candidateRegistry;
    private readonly ProjectCreationCatalog catalog;
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
        TimeProvider? timeProvider = null)
    {
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
        return BuildView(stored, draft);
    }

    internal ProjectCreationDraftView LoadDraft(
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = Demand(AuthorizationOperation.ViewProject);
        StoredProjectCreationDraft stored = GetRequiredDraft(draftId);
        return BuildView(stored, ParseStored(stored));
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
        ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseInput(
            input,
            draftId,
            catalog);
        StoredProjectCreationDraft stored = draftStore.SaveDraft(
            draft,
            ProjectCreationPayloadCodec.Serialize(draft),
            expectedRevision,
            GetUtcNow());
        return BuildView(stored, draft);
    }

    internal ProjectCreationDraftView FinalizeDraft(
        EntityId<ProjectCreationDraftIdTag> draftId,
        int expectedRevision,
        JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = Demand(AuthorizationOperation.CreateProject);
        StoredProjectCreationDraft current = GetRequiredDraft(draftId);
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

        if (!candidateRegistry.IsAvailable(draft.SourceCandidateId!))
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
            expectedRevision,
            GetUtcNow());
        return BuildView(stored, draft);
    }

    internal async Task<EntityId<AssetIdTag>?> PickSourceAsync(
        CancellationToken cancellationToken = default)
    {
        _ = Demand(AuthorizationOperation.CreateProject);
        cancellationToken.ThrowIfCancellationRequested();
        EntityId<AssetIdTag>? candidateId =
            await pickSourceAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidateId is not null && !candidateRegistry.IsAvailable(candidateId))
        {
            throw new InvalidOperationException(
                "The selected source token is not registered.");
        }

        return candidateId;
    }

    private ProjectListItem ReadAndProjectListItem(StoredProjectCreationDraft stored)
    {
        ProjectCreationDraft draft = ParseStored(stored);
        return new ProjectListItem(
            draft.Id.ToString(),
            stored.FinalizedProjectId?.ToString(),
            stored.IsFinalized ? "finalized" : "draft",
            draft.ContentName,
            GetSourceStatus(draft.SourceCandidateId),
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
        ProjectCreationDraft draft)
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
            GetSourceStatus(draft.SourceCandidateId),
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
            ProjectCreationPayloadCodec.FormatTimestamp(stored.UpdatedAt));
    }

    private ProjectCreationDraft ParseStored(StoredProjectCreationDraft stored)
    {
        ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseStored(
            stored.CanonicalJson,
            catalog);
        if (draft.Id != stored.Id)
        {
            throw new InvalidOperationException(
                "The stored project creation draft identity is invalid.");
        }

        return draft;
    }

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

    private string GetSourceStatus(EntityId<AssetIdTag>? sourceCandidateId) =>
        sourceCandidateId is null
            ? "notSelected"
            : candidateRegistry.IsAvailable(sourceCandidateId)
                ? "available"
                : "reselectionRequired";

    private string Demand(AuthorizationOperation operation)
    {
        string actorSid = getCurrentActorSid();
        authorizationService.Demand(actorSid, operation);
        return actorSid;
    }

    private DateTimeOffset GetUtcNow() =>
        timeProvider.GetUtcNow().ToUniversalTime();
}