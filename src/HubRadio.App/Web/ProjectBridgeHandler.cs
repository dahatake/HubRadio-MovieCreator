using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Diagnostics;

namespace HubRadio.App.Web;

internal sealed class ProjectBridgeHandler
{
    private readonly ProjectCreationService service;
    private readonly Func<EntityId<ProjectIdTag>, CancellationToken, Task>? restoreAfterReadAsync;

    private readonly Func<CancellationToken, Task<bool>>? repairRuntimeAsync;

    internal ProjectBridgeHandler(ProjectCreationService service,
        Func<EntityId<ProjectIdTag>, CancellationToken, Task>? restoreAfterReadAsync = null,
        Func<CancellationToken, Task<bool>>? repairRuntimeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
        this.restoreAfterReadAsync = restoreAfterReadAsync;
        this.repairRuntimeAsync = repairRuntimeAsync;
    }

    internal async Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Method switch
            {
                BridgeMethod.ListProjects =>
                    WriteOverview(request, service.ListProjects()),
                BridgeMethod.CreateDraft =>
                    WriteDraft(
                        request,
                        await service.CreateSourceOnlyDraftAsync(
                            cancellationToken)),
                BridgeMethod.LoadDraft =>
                    WriteDraft(request, await LoadDraftAsync(request, cancellationToken)),
                BridgeMethod.SaveDraft =>
                    WriteDraft(
                        request,
                        service.SaveDraft(
                            ReadDraftId(request),
                            ReadExpectedRevision(request),
                            request.Parameters.GetProperty("input"))),
                BridgeMethod.DeleteProject =>
                    DeleteProject(request),
                BridgeMethod.RenameProject =>
                    WriteDraft(
                        request,
                        service.RenameProject(
                            ReadDraftId(request),
                            request.Parameters.GetProperty("displayName").GetString()!)),
                BridgeMethod.PickSource =>
                    WritePickedSource(
                        request,
                        await service.PickSourceAsync(cancellationToken)),
                BridgeMethod.FinalizeDraft =>
                    WriteDraft(request, await FinalizeWithRepairAsync(request, cancellationToken)),
                _ => BridgeProtocol.CreateErrorResponse(
                    request,
                    StableErrorCodes.ProjectInvalidRequest),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthorizationException)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectNotAuthorized);
        }
        catch (StoreConcurrencyException)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectRevisionConflict);
        }
        catch (SourceOnlyApplicationDependencyException exception)
        {
            // Keep the existing response schema: no private paths or dependency
            // exception text, and critically no finalized draft/result on failure.
            return BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectRuntimeUnavailable,
                SourceOnlyDependencyGuidance.Describe(
                    exception.Dependency,
                    repairAttempted: repairRuntimeAsync is not null &&
                        request.Method == BridgeMethod.FinalizeDraft));
        }
        catch (ProjectCreationException exception)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                exception.Reason switch
                {
                    ProjectCreationFailureReason.DraftNotFound =>
                        StableErrorCodes.ProjectNotFound,
                    ProjectCreationFailureReason.DraftAlreadyFinalized =>
                        StableErrorCodes.ProjectAlreadyFinalized,
                    ProjectCreationFailureReason.DraftIncomplete =>
                        StableErrorCodes.ProjectIncomplete,
                    ProjectCreationFailureReason.SourceReselectionRequired =>
                        StableErrorCodes.ProjectSourceReselectionRequired,
                    ProjectCreationFailureReason.DeleteBlockedByRunningJob =>
                        StableErrorCodes.ProjectDeleteBlocked,
                    ProjectCreationFailureReason.ProjectNameInvalid =>
                        StableErrorCodes.ProjectNameInvalid,
                    ProjectCreationFailureReason.ProjectNameDuplicate =>
                        StableErrorCodes.ProjectNameDuplicate,
                    ProjectCreationFailureReason.ProjectFolderRenameFailed =>
                        StableErrorCodes.ProjectFolderRenameFailed,
                    _ => StableErrorCodes.ProjectOperationFailed,
                });
        }
        catch (SourceOnlyProjectCreationException exception)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                exception.Reason switch
                {
                    SourceOnlyProjectCreationFailureReason.SourceSelectionCanceled =>
                        StableErrorCodes.ProjectSourceSelectionCanceled,
                    SourceOnlyProjectCreationFailureReason.UnsafeSourceName =>
                        StableErrorCodes.ProjectUnsafeSourceName,
                    SourceOnlyProjectCreationFailureReason.SettingsUnavailable =>
                        StableErrorCodes.ProjectSettingsUnavailable,
                    SourceOnlyProjectCreationFailureReason.SettingsMismatch =>
                        StableErrorCodes.ProjectSettingsMismatch,
                    SourceOnlyProjectCreationFailureReason.CatalogUnavailable =>
                        StableErrorCodes.ProjectCatalogUnavailable,
                    _ => StableErrorCodes.ProjectOperationFailed,
                });
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or JsonException)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectInvalidRequest);
        }
        catch (Exception)
        {
            return BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectOperationFailed);
        }
    }

    private string DeleteProject(BridgeRequest request)
    {
        EntityId<ProjectCreationDraftIdTag> draftId = ReadDraftId(request);
        bool filesRemoved = service.DeleteProject(draftId);
        return BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("deletedDraftId", draftId.ToString());
                writer.WriteBoolean("filesRemoved", filesRemoved);
                writer.WriteEndObject();
            });
    }

    // Accept fails before any state changes, so one repair-and-retry cannot double-start work.
    private async Task<ProjectCreationDraftView> FinalizeWithRepairAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        EntityId<ProjectCreationDraftIdTag> draftId = ReadDraftId(request);
        int expectedRevision = ReadExpectedRevision(request);
        JsonElement input = request.Parameters.GetProperty("input");
        try
        {
            return service.FinalizeDraft(draftId, expectedRevision, input);
        }
        catch (SourceOnlyApplicationDependencyException) when (repairRuntimeAsync is not null)
        {
            bool repaired;
            try
            {
                repaired = await repairRuntimeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                repaired = false;
            }

            if (!repaired)
            {
                throw;
            }

            return service.FinalizeDraft(draftId, expectedRevision, input);
        }
    }

    private async Task<ProjectCreationDraftView> LoadDraftAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // LoadDraft authorizes before the optional native read-only rehydration hook.
        ProjectCreationDraftView draft = service.LoadDraft(ReadDraftId(request));
        if (draft.IsFinalized && draft.SourceOnly is not null && draft.ProjectId is not null && restoreAfterReadAsync is not null)
        {
            try
            {
                await restoreAfterReadAsync(EntityId<ProjectIdTag>.Parse(draft.ProjectId), cancellationToken);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or ObjectDisposedException
                or OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // Rehydration is best-effort: a stale checkpoint must not stop the Project from opening.
            }
        }
        return draft;
    }

    private static EntityId<ProjectCreationDraftIdTag> ReadDraftId(
        BridgeRequest request) =>
        EntityId<ProjectCreationDraftIdTag>.Parse(
            request.Parameters.GetProperty("draftId").GetString()!);

    private static int ReadExpectedRevision(BridgeRequest request) =>
        request.Parameters.GetProperty("expectedRevision").GetInt32();

    private static string WriteOverview(
        BridgeRequest request,
        ProjectCreationOverview overview) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("projects");
                foreach (ProjectListItem item in overview.Projects)
                {
                    writer.WriteStartObject();
                    writer.WriteString("draftId", item.DraftId);
                    WriteNullableString(writer, "projectId", item.ProjectId);
                    writer.WriteString("state", item.State);
                    WriteNullableString(writer, "contentName", item.ContentName);
                    WriteNullableString(writer, "displayName", item.DisplayName);
                    writer.WriteNull("episodeNumber");
                    writer.WriteString("sourceStatus", item.SourceStatus);
                    writer.WriteNull("owner");
                    WriteNullableString(
                        writer,
                        "publicationScope",
                        item.PublicationScope);
                    writer.WriteNull("editState");
                    writer.WriteNull("reviewState");
                    writer.WriteStartArray("outputFormats");
                    foreach (string outputFormat in item.OutputFormats)
                    {
                        writer.WriteStringValue(outputFormat);
                    }

                    writer.WriteEndArray();
                    writer.WriteString("updatedAt", item.UpdatedAt);
                    if (item.Thumbnail is { } thumbnail)
                    {
                        writer.WriteStartObject("thumbnail");
                        writer.WriteString("executionId", thumbnail.ExecutionId);
                        writer.WriteString("artifactId", thumbnail.ArtifactId);
                        writer.WriteString("contentDigest", thumbnail.ContentDigest);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteNull("thumbnail");
                    }

                    // FR-053: the poster frame is a fallback picture; digest only, never a path or bytes.
                    if (item.PosterDigest is { } posterDigest)
                    {
                        writer.WriteStartObject("poster");
                        writer.WriteString("contentDigest", posterDigest);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteNull("poster");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartObject("catalog");
                WriteCatalogOptions(
                    writer,
                    "outputProfiles",
                    overview.OutputProfiles);
                WriteCatalogOptions(
                    writer,
                    "brandTemplates",
                    overview.BrandTemplates);
                writer.WriteEndObject();
                writer.WriteBoolean("canCreate", overview.CanCreate);
                writer.WriteEndObject();
            });

    private static string WriteDraft(
        BridgeRequest request,
        ProjectCreationDraftView draft) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("draft");
                writer.WriteString("draftId", draft.DraftId);
                writer.WriteNumber("revision", draft.Revision);
                writer.WriteBoolean("isFinalized", draft.IsFinalized);
                WriteNullableString(writer, "projectId", draft.ProjectId);
                WriteNullableString(
                    writer,
                    "sourceCandidateId",
                    draft.SourceCandidateId);
                writer.WriteString("sourceStatus", draft.SourceStatus);
                WriteNullableString(writer, "contentName", draft.ContentName);
                WriteNullableString(writer, "displayName", draft.DisplayName);
                WriteNullableString(writer, "purpose", draft.Purpose);
                WriteNullableString(writer, "audience", draft.Audience);
                WriteNullableString(
                    writer,
                    "publicationScope",
                    draft.PublicationScope);
                writer.WriteStartArray("outputProfileIds");
                foreach (string outputProfileId in draft.OutputProfileIds)
                {
                    writer.WriteStringValue(outputProfileId);
                }

                writer.WriteEndArray();
                WriteNullableString(writer, "language", draft.Language);
                writer.WriteStartArray("performerNames");
                foreach (string performerName in draft.PerformerNames)
                {
                    writer.WriteStringValue(performerName);
                }

                writer.WriteEndArray();
                if (draft.HasDeadline is null)
                {
                    writer.WriteNull("deadlineSelection");
                }
                else
                {
                    writer.WriteStartObject("deadlineSelection");
                    writer.WriteBoolean("hasDeadline", draft.HasDeadline.Value);
                    WriteNullableString(writer, "value", draft.Deadline);
                    writer.WriteEndObject();
                }

                WriteNullableString(
                    writer,
                    "brandTemplateId",
                    draft.BrandTemplateId);
                writer.WriteStartArray("missingFields");
                foreach (string missingField in draft.MissingFields)
                {
                    writer.WriteStringValue(missingField);
                }

                writer.WriteEndArray();
                writer.WriteString("updatedAt", draft.UpdatedAt);
                if (draft.SourceOnly is null)
                {
                    writer.WriteNull("sourceOnly");
                }
                else
                {
                    writer.WriteStartObject("sourceOnly");
                    writer.WriteString("profileId", draft.SourceOnly.ProfileId);
                    writer.WriteString(
                        "profileVersion",
                        draft.SourceOnly.ProfileVersion);
                    writer.WriteString(
                        "profileDigest",
                        draft.SourceOnly.ProfileDigest);
                    writer.WriteString(
                        "settingsSnapshotDigest",
                        draft.SourceOnly.SettingsSnapshotDigest);
                    writer.WriteStartArray("artifactSet");
                    foreach (string artifact in draft.SourceOnly.ArtifactSet)
                    {
                        writer.WriteStringValue(artifact);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                // FR-054: what the built-in preset set for this Project (labels and reasons only).
                if (draft.AutoSettings is null)
                {
                    writer.WriteNull("autoSettings");
                }
                else
                {
                    writer.WriteStartArray("autoSettings");
                    foreach (AutoSettingNote note in draft.AutoSettings)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("key", note.Key);
                        writer.WriteString("label", note.Label);
                        writer.WriteString("valueText", note.ValueText);
                        writer.WriteString("reason", note.Reason);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            });

    private static string WritePickedSource(
        BridgeRequest request,
        EntityId<HubRadio.Core.Provenance.AssetIdTag>? candidateId) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                WriteNullableString(
                    writer,
                    "candidateId",
                    candidateId?.ToString());
                writer.WriteEndObject();
            });

    private static void WriteCatalogOptions(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<ProjectCatalogOption> options)
    {
        writer.WriteStartArray(propertyName);
        foreach (ProjectCatalogOption option in options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", option.Id);
            writer.WriteString("name", option.Name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}