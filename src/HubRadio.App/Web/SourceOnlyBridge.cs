using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Web;

internal enum SourceOnlyBridgeStatus
{
    Available,
    NotFound,
    Conflict,
    Forbidden,
    Unavailable,
    Invalid,
}

// This is the complete polling snapshot. Never serialize StoredSourceOnlyExecution,
// its request, or native result JSON directly into the WebView.
internal sealed record SourceOnlyBridgeSnapshot(
    string ProjectId,
    string ExecutionId,
    int Revision,
    string Phase,
    string RootJobId,
    string IngestJobId,
    string SettingsSnapshotDigest,
    string? VersionId,
    string? VersionDigest,
    string? ParentVersionId,
    string? ParentDigest,
    string ResultStatus,
    SourceOnlyResultDto? Result,
    SourceOnlyResultDto? PreviousResult,
    string? FailureCode);

internal static class SourceOnlyBridgeProtocol
{
    private const NumberStyles RevisionStyles = NumberStyles.AllowLeadingSign |
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters)
    {
        string[] keys = method switch
        {
            BridgeMethod.PipelineRead => ["projectId"],
            BridgeMethod.PipelineVersions => ["projectId"],
            BridgeMethod.PipelineRestore =>
            [
                "projectId", "targetVersionId", "targetVersionDigest",
                "expectedWorkingCopyRevision", "expectedSettingsRevision", "expectedSettingsSnapshotDigest",
            ],
            BridgeMethod.PipelineRegenerate =>
            [
                "projectId", "expectedVersionId", "expectedVersionDigest",
                "expectedSettingsRevision", "expectedSettingsSnapshotDigest",
            ],
            BridgeMethod.PipelineRetry or BridgeMethod.PipelineCancel =>
                ["projectId", "executionId", "expectedRevision"],
            _ => [],
        };
        if (keys.Length == 0 || !SettingsBridgeProtocol.HasExactProperties(parameters, keys) ||
            !SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("projectId")))
        {
            return false;
        }

        return method switch
        {
            BridgeMethod.PipelineRead => true,
            BridgeMethod.PipelineVersions => true,
            BridgeMethod.PipelineRestore =>
                SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("targetVersionId")) &&
                SettingsBridgeProtocol.IsDigest(parameters.GetProperty("targetVersionDigest")) &&
                IsRevision(parameters.GetProperty("expectedWorkingCopyRevision")) &&
                IsRevision(parameters.GetProperty("expectedSettingsRevision")) &&
                SettingsBridgeProtocol.IsDigest(parameters.GetProperty("expectedSettingsSnapshotDigest")),
            BridgeMethod.PipelineRegenerate =>
                SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("expectedVersionId")) &&
                SettingsBridgeProtocol.IsDigest(parameters.GetProperty("expectedVersionDigest")) &&
                IsRevision(parameters.GetProperty("expectedSettingsRevision")) &&
                SettingsBridgeProtocol.IsDigest(parameters.GetProperty("expectedSettingsSnapshotDigest")),
            _ => SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("executionId")) &&
                IsRevision(parameters.GetProperty("expectedRevision")),
        };
    }

    internal static EntityId<ProjectIdTag> ReadProjectId(BridgeRequest request) =>
        EntityId<ProjectIdTag>.Parse(request.Parameters.GetProperty("projectId").GetString()!);

    internal static int ReadRevision(JsonElement parameters, string name) =>
        int.Parse(parameters.GetProperty(name).GetRawText(), RevisionStyles, CultureInfo.InvariantCulture);

    private static bool IsRevision(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.GetRawText().Length <= 128 &&
        int.TryParse(value.GetRawText(), RevisionStyles, CultureInfo.InvariantCulture, out int revision) && revision >= 0;
}

/// <summary>
/// Native polling/control boundary. The host supplies stores/services for the SAME
/// workspace database and its actual Windows actor provider; no renderer identity,
/// permissions, paths, settings values or approvals are accepted. Construction does
/// not start/recover a pipeline or bootstrap roles. The provider is resolved per
/// mutation, allowing host startup to finish later. A missing pipeline makes commands
/// unavailable; read-only durable snapshots still use the supplied result service.
/// </summary>
internal sealed class SourceOnlyBridgeHandler
{
    private readonly Func<SourceOnlyPipeline?> pipelineProvider;
    private readonly SourceOnlyExecutionStore executionStore;
    private readonly DefaultSettingsStore settingsStore;
    private readonly VersionService versions;
    private readonly SourceOnlyResultService results;
    private readonly SourceOnlyBridgeProjection projection;
    private readonly Func<string> currentActorSid;
    private readonly AuthorizationService authorization;
    private readonly TimeProvider timeProvider;

    // Host constructor contract (parameter order is intentional):
    // SourceOnlyBridgeHandler(Func<SourceOnlyPipeline?> pipelineProvider,
    //   SourceOnlyExecutionStore executionStore, DefaultSettingsStore settingsStore,
    //   SourceOnlyResultService resultService, ProjectStore projectStore,
    //   Func<string> currentActorSid, AuthorizationService authorization,
    //   TimeProvider? timeProvider = null)
    internal SourceOnlyBridgeHandler(
        Func<SourceOnlyPipeline?> pipelineProvider,
        SourceOnlyExecutionStore executionStore,
        DefaultSettingsStore settingsStore,
        SourceOnlyResultService resultService,
        ProjectStore projectStore,
        Func<string> currentActorSid,
        AuthorizationService authorization,
        TimeProvider? timeProvider = null)
    {
        this.pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        this.executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        versions = new VersionService(projectStore ?? throw new ArgumentNullException(nameof(projectStore)));
        results = resultService ?? throw new ArgumentNullException(nameof(resultService));
        projection = new SourceOnlyBridgeProjection(executionStore, resultService);
        this.currentActorSid = currentActorSid ?? throw new ArgumentNullException(nameof(currentActorSid));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<string> HandleAsync(BridgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BridgeProtocol.IsSourceOnlyMethod(request.Method))
        {
            throw new ArgumentException("A source-only pipeline request is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!SourceOnlyBridgeProtocol.HasValidParameters(request.Method, request.Parameters))
            {
                return Fail(request, SourceOnlyBridgeStatus.Invalid);
            }

            string actor = currentActorSid();
            Demand(actor, request.Method);
            EntityId<ProjectIdTag> projectId = SourceOnlyBridgeProtocol.ReadProjectId(request);
            StoredSourceOnlyExecution? execution;
            if (request.Method == BridgeMethod.PipelineVersions)
            {
                IReadOnlyList<SourceOnlyVersionHistoryEntry> history =
                    results.ListVersionHistory(projectId, executionStore);
                int? workingCopyRevision = versions.GetCurrentState(projectId)?.Revision;
                if (!string.Equals(actor, currentActorSid(), StringComparison.Ordinal))
                {
                    return Fail(request, SourceOnlyBridgeStatus.Forbidden);
                }

                return history.Count == 0 || workingCopyRevision is null
                    ? Fail(request, SourceOnlyBridgeStatus.NotFound)
                    : SourceOnlyBridgeJson.WriteVersionsResponse(request, SourceOnlyBridgeStatus.Available,
                        history, workingCopyRevision.Value);
            }

            if (request.Method == BridgeMethod.PipelineRead)
            {
                execution = executionStore.GetLatestForProject(projectId);
            }
            else
            {
                SourceOnlyPipeline? pipeline = pipelineProvider();
                if (pipeline is null)
                {
                    return Fail(request, SourceOnlyBridgeStatus.Unavailable);
                }

                execution = executionStore.GetLatestForProject(projectId);
                if (execution is null)
                {
                    return Fail(request, SourceOnlyBridgeStatus.NotFound);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (request.Method is BridgeMethod.PipelineRestore or BridgeMethod.PipelineRegenerate)
                {
                    // A terminal execution may still own unresolved side effects. Reject
                    // those before accepting a new root, even when no job is busy.
                    if (execution.Phase < SourceOnlyExecutionPhase.Completed ||
                        execution.Phase == SourceOnlyExecutionPhase.ReconciliationRequired)
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    if (execution.ResultJson is not null)
                    {
                        SourceOnlyResult saved = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
                        saved.ValidateBinding(execution);
                        if (saved.StagingCleanupRequired || saved.ReconciliationRequired)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Conflict);
                        }
                    }
                }

                if (request.Method == BridgeMethod.PipelineRestore)
                {
                    JsonElement restoreParameters = request.Parameters;
                    var targetId = EntityId<CompositionVersionIdTag>.Parse(restoreParameters.GetProperty("targetVersionId").GetString()!);
                    var targetDigest = new ContentDigest(restoreParameters.GetProperty("targetVersionDigest").GetString()!);
                    int workingCopyRevision = SourceOnlyBridgeProtocol.ReadRevision(restoreParameters, "expectedWorkingCopyRevision");
                    int restoreSettingsRevision = SourceOnlyBridgeProtocol.ReadRevision(restoreParameters, "expectedSettingsRevision");
                    StoredProjectSettings? restoreSettings = settingsStore.GetProjectSettings(projectId).Settings;
                    if (restoreSettings is null)
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Unavailable);
                    }

                    CompositionVersion? target = versions.GetVersion(targetId);
                    WorkingCopyState? restoreHead = versions.GetCurrentState(projectId);
                    if (target is null || target.ProjectId != projectId || target.ContentDigest != targetDigest ||
                        restoreHead is null || restoreHead.WorkingCopy.ProjectId != projectId ||
                        restoreHead.Revision != workingCopyRevision ||
                        restoreHead.WorkingCopy.BaseVersionId == targetId ||
                        restoreSettings.ProjectId != projectId || restoreSettings.Revision != restoreSettingsRevision ||
                        restoreSettings.CurrentSnapshot.ContentDigest.Hex !=
                            restoreParameters.GetProperty("expectedSettingsSnapshotDigest").GetString())
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    DemandCurrent(actor, request.Method);
                    cancellationToken.ThrowIfCancellationRequested();
                    // An earlier restore may have committed its child version and then
                    // failed to start generation. Adopt that exact child instead of
                    // stacking another one, so retrying the same restore recovers.
                    CompositionVersion? orphan = AdoptRestoredHead(projectId, restoreHead, target);
                    // Rolling back as a child keeps the current version, every past
                    // version and every previously delivered artifact untouched.
                    CompositionVersion child = orphan ?? versions.RollbackAsChild(restoreHead.WorkingCopy, target,
                        workingCopyRevision, EntityId<CompositionVersionIdTag>.New(), actor,
                        timeProvider.GetUtcNow().ToUniversalTime(),
                        "Restore an earlier composition as a new child version.").Version;
                    execution = Invoke(() => pipeline.Regenerate(projectId, child.Id, child.ContentDigest,
                        restoreSettings.CurrentSnapshot, restoreSettingsRevision, actor,
                        timeProvider.GetUtcNow().ToUniversalTime()));
                }
                else if (request.Method == BridgeMethod.PipelineRegenerate)
                {
                    if (execution.ResultJson is not null)
                    {
                        SourceOnlyResult saved = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
                        saved.ValidateBinding(execution);
                        if (saved.StagingCleanupRequired || saved.ReconciliationRequired)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Conflict);
                        }
                    }

                    JsonElement parameters = request.Parameters;
                    var parentId = EntityId<CompositionVersionIdTag>.Parse(parameters.GetProperty("expectedVersionId").GetString()!);
                    var parentDigest = new ContentDigest(parameters.GetProperty("expectedVersionDigest").GetString()!);
                    int settingsRevision = SourceOnlyBridgeProtocol.ReadRevision(parameters, "expectedSettingsRevision");
                    StoredProjectSettings? settings = settingsStore.GetProjectSettings(projectId).Settings;
                    if (settings is null)
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Unavailable);
                    }

                    CompositionVersion? parent = versions.GetVersion(parentId);
                    WorkingCopyState? head = versions.GetCurrentState(projectId);
                    if (settings.ProjectId != projectId || settings.Revision != settingsRevision ||
                        settings.CurrentSnapshot.ContentDigest.Hex != parameters.GetProperty("expectedSettingsSnapshotDigest").GetString() ||
                        parent is null || parent.ProjectId != projectId || parent.ContentDigest != parentDigest ||
                        head is null || head.WorkingCopy.ProjectId != projectId || head.WorkingCopy.BaseVersionId != parentId ||
                        !head.WorkingCopy.Elements.SequenceEqual(parent.Elements))
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    // The pipeline must repeat reconciliation and current-parent/settings
                    // checks under its acceptance gate; this boundary does not own that lock.
                    DemandCurrent(actor, request.Method);
                    cancellationToken.ThrowIfCancellationRequested();
                    execution = Invoke(() => pipeline.Regenerate(projectId, parentId, parentDigest,
                        settings.CurrentSnapshot, settingsRevision, actor, timeProvider.GetUtcNow().ToUniversalTime()));
                }
                else
                {
                    var executionId = EntityId<SourceOnlyExecutionIdTag>.Parse(request.Parameters.GetProperty("executionId").GetString()!);
                    StoredSourceOnlyExecution? target = executionStore.Get(executionId);
                    if (target is null || target.Request.ProjectId != projectId)
                    {
                        return Fail(request, SourceOnlyBridgeStatus.NotFound);
                    }

                    int expectedRevision = SourceOnlyBridgeProtocol.ReadRevision(request.Parameters, "expectedRevision");
                    if (target.Request.ExecutionId != execution.Request.ExecutionId || target.Revision != expectedRevision)
                    {
                        return Fail(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    if (request.Method == BridgeMethod.PipelineRetry)
                    {
                        if (target.Phase is not (SourceOnlyExecutionPhase.Failed or SourceOnlyExecutionPhase.Canceled) ||
                            target.ResultJson is not null || target.VersionId is not null)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Conflict);
                        }

                        DemandCurrent(actor, request.Method);
                        cancellationToken.ThrowIfCancellationRequested();
                        execution = Invoke(() => pipeline.Retry(executionId, expectedRevision, actor,
                            timeProvider.GetUtcNow().ToUniversalTime()));
                    }
                    else if (target.Phase == SourceOnlyExecutionPhase.Canceled)
                    {
                        execution = target; // Exact already-canceled snapshot, not a new cancellation.
                    }
                    else
                    {
                        if (target.Phase >= SourceOnlyExecutionPhase.Completed)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Conflict);
                        }

                        // CancelAsync has no revision argument. Check the durable identity/revision
                        // immediately before signaling, then await owned cleanup, never write a
                        // synthetic Canceled row. Subsequent stage revisions remain pipeline-owned.
                        DemandCurrent(actor, request.Method);
                        cancellationToken.ThrowIfCancellationRequested();
                        StoredSourceOnlyExecution? beforeCancel = executionStore.Get(executionId);
                        if (beforeCancel is null || beforeCancel.Request.ProjectId != projectId || beforeCancel.Revision != expectedRevision ||
                            executionStore.GetLatestForProject(projectId)?.Request.ExecutionId != executionId)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Conflict);
                        }
                        SourceOnlyPipelineRun? canceled = await pipeline.CancelAsync(executionId).ConfigureAwait(false);
                        if (canceled is null || canceled.InfrastructureFailureCode is not null || canceled.Execution is null)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Unavailable);
                        }

                        if (canceled.ExecutionId != executionId || canceled.Execution.Request.ProjectId != projectId)
                        {
                            return Fail(request, SourceOnlyBridgeStatus.Invalid);
                        }

                        execution = executionStore.Get(executionId);
                    }
                }
            }

            if (execution is null)
            {
                return Fail(request, SourceOnlyBridgeStatus.NotFound);
            }

            if (execution.Request.ProjectId != projectId)
            {
                return Fail(request, SourceOnlyBridgeStatus.Invalid);
            }

            SourceOnlyBridgeSnapshot snapshot = await projection.ReadAsync(execution, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(actor, currentActorSid(), StringComparison.Ordinal))
            {
                return Fail(request, SourceOnlyBridgeStatus.Forbidden);
            }

            Demand(actor, request.Method);
            return SourceOnlyBridgeJson.WriteResponse(request,
                snapshot.ResultStatus == "invalid" ? SourceOnlyBridgeStatus.Invalid : SourceOnlyBridgeStatus.Available, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AuthorizationException)
        {
            return Fail(request, SourceOnlyBridgeStatus.Forbidden);
        }
        catch (StoreConcurrencyException)
        {
            return Fail(request, SourceOnlyBridgeStatus.Conflict);
        }
        catch (PipelineConflictException)
        {
            return Fail(request, SourceOnlyBridgeStatus.Conflict);
        }
        catch (ObjectDisposedException)
        {
            return Fail(request, SourceOnlyBridgeStatus.Unavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or
            SourceOnlyResultException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return Fail(request, SourceOnlyBridgeStatus.Invalid);
        }
        catch (Exception)
        {
            return Fail(request, SourceOnlyBridgeStatus.Unavailable);
        }
    }

    /// <summary>Writes the response shape that belongs to the requested method.</summary>
    private static string Fail(BridgeRequest request, SourceOnlyBridgeStatus status) =>
        request.Method == BridgeMethod.PipelineVersions
            ? SourceOnlyBridgeJson.WriteVersionsResponse(request, status, [], 0)
            : SourceOnlyBridgeJson.WriteResponse(request, status);

    private void Demand(string actor, BridgeMethod method)
    {
        authorization.Demand(actor, AuthorizationOperation.ViewProject);
        if (method is not (BridgeMethod.PipelineRead or BridgeMethod.PipelineVersions))
        {
            authorization.DemandAs(actor, AuthorizationRole.Editor, AuthorizationOperation.CreateCompositionVersion);
        }
    }

    /// <summary>
    /// The exact child an earlier restore of the same target already committed
    /// before it failed to start generation. Null when no such head exists.
    /// </summary>
    private CompositionVersion? AdoptRestoredHead(EntityId<ProjectIdTag> projectId,
        WorkingCopyState head, CompositionVersion target)
    {
        if (head.WorkingCopy.BaseVersionId is not EntityId<CompositionVersionIdTag> headId ||
            executionStore.GetLatestForProject(projectId)?.VersionId == headId)
        {
            return null;
        }

        CompositionVersion? headVersion = versions.GetVersion(headId);
        return headVersion is not null && headVersion.ProjectId == projectId &&
            headVersion.ParentVersionId is not null &&
            headVersion.Elements.SequenceEqual(target.Elements) &&
            head.WorkingCopy.Elements.SequenceEqual(headVersion.Elements)
            ? headVersion
            : null;
    }

    private void DemandCurrent(string actor, BridgeMethod method)
    {
        if (!string.Equals(actor, currentActorSid(), StringComparison.Ordinal))
        {
            throw new AuthorizationException(AuthorizationOperation.ViewProject, actingRole: null);
        }
        Demand(actor, method);
    }

    private static StoredSourceOnlyExecution Invoke(Func<StoredSourceOnlyExecution> operation)
    {
        try { return operation(); }
        catch (ObjectDisposedException) { throw; }
        catch (InvalidOperationException) { throw new PipelineConflictException(); }
    }

    private sealed class PipelineConflictException : Exception { }
}

internal sealed class SourceOnlyBridgeProjection
{
    private readonly SourceOnlyExecutionStore executions;
    private readonly SourceOnlyResultService results;

    internal SourceOnlyBridgeProjection(SourceOnlyExecutionStore executions, SourceOnlyResultService results)
    {
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
        this.results = results ?? throw new ArgumentNullException(nameof(results));
    }

    internal async Task<SourceOnlyBridgeSnapshot> ReadAsync(StoredSourceOnlyExecution execution, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        (string resultStatus, SourceOnlyResultDto? result) = await ReadResultAsync(execution, token).ConfigureAwait(false);
        SourceOnlyResultDto? previous = null;
        if (execution.Request.ParentVersionId is EntityId<CompositionVersionIdTag> parentId &&
            execution.Request.ParentDigest is ContentDigest parentDigest)
        {
            foreach (StoredSourceOnlyExecution earlier in executions.ListForProject(
                execution.Request.ProjectId, SourceOnlyExecutionStore.MaximumListResults))
            {
                token.ThrowIfCancellationRequested();
                if (earlier.Request.ExecutionId == execution.Request.ExecutionId ||
                    earlier.Request.ProjectId != execution.Request.ProjectId ||
                    earlier.Request.CreatedAt >= execution.Request.CreatedAt || earlier.ResultJson is null ||
                    earlier.VersionId != parentId || earlier.VersionDigest != parentDigest)
                {
                    continue;
                }

                (string status, SourceOnlyResultDto? candidate) = await ReadResultAsync(earlier, token).ConfigureAwait(false);
                previous = status == "valid" ? candidate : null;
                // This is the exact parent's result, never a "latest valid" fallback.
                break;
            }
        }

        return new(execution.Request.ProjectId.ToString(), execution.Request.ExecutionId.ToString(), execution.Revision,
            execution.Phase.ToString(), execution.Request.RootJobId.ToString(), execution.Request.IngestJobId.ToString(),
            execution.Request.SettingsSnapshot.ContentDigest.Hex, execution.VersionId?.ToString(), execution.VersionDigest?.Hex,
            execution.Request.ParentVersionId?.ToString(), execution.Request.ParentDigest?.Hex,
            resultStatus, result, previous, execution.FailureCode);
    }

    private async Task<(string Status, SourceOnlyResultDto? Result)> ReadResultAsync(StoredSourceOnlyExecution execution, CancellationToken token)
    {
        if (execution.ResultJson is null) { return ("none", null); }
        try
        {
            SourceOnlyResultLoadResult loaded = await results.LoadAsync(execution, token).ConfigureAwait(false);
            StoredSourceOnlyExecution? current = executions.Get(execution.Request.ExecutionId);
            if (current is null || current.Request.ProjectId != execution.Request.ProjectId ||
                current.RequestDigest != execution.RequestDigest || current.ResultDigest != execution.ResultDigest ||
                current.ResultJson != execution.ResultJson)
            {
                return ("invalid", null);
            }
            // ToWebDto is the existing public codec projection, not Serialize(result).
            // Its evaluation property remains a nested JsonElement, not a quoted JSON string.
            return (loaded.Validation.IsValid ? "valid" : "invalid", loaded.Result.ToWebDto());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is SourceOnlyResultException or JsonException or FormatException)
        {
            return ("invalid", null);
        }
    }
}

internal static class SourceOnlyBridgeJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Invalid may retain a diagnostic snapshot/codec DTO. It never makes that
    // descriptor usable media; the media boundary must independently reject invalid results.
    internal static string WriteResponse(BridgeRequest request, SourceOnlyBridgeStatus status, SourceOnlyBridgeSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BridgeProtocol.IsSourceOnlyMethod(request.Method) ||
            (status == SourceOnlyBridgeStatus.Available && snapshot is null) ||
            (status is not (SourceOnlyBridgeStatus.Available or SourceOnlyBridgeStatus.Invalid) && snapshot is not null) ||
            (snapshot is not null && (snapshot.ResultStatus is not ("none" or "valid" or "invalid") ||
                (status == SourceOnlyBridgeStatus.Invalid) != (snapshot.ResultStatus == "invalid") ||
                (snapshot.ResultStatus == "valid" && snapshot.Result is null) ||
                (snapshot.ResultStatus == "none" && snapshot.Result is not null))))
        {
            throw new ArgumentException("The source-only response is inconsistent.", nameof(snapshot));
        }

        string text = status switch
        {
            SourceOnlyBridgeStatus.Available => "available",
            SourceOnlyBridgeStatus.NotFound => "notFound",
            SourceOnlyBridgeStatus.Conflict => "conflict",
            SourceOnlyBridgeStatus.Forbidden => "forbidden",
            SourceOnlyBridgeStatus.Unavailable => "unavailable",
            SourceOnlyBridgeStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        return BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", text);
            writer.WritePropertyName("snapshot");
            JsonSerializer.Serialize(writer, snapshot, Options);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The read-only past-version list. No file path, storage reference or
    /// artifact content crosses this boundary.
    /// </summary>
    internal static string WriteVersionsResponse(BridgeRequest request, SourceOnlyBridgeStatus status,
        IReadOnlyList<SourceOnlyVersionHistoryEntry> versions, int workingCopyRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(versions);
        if (request.Method != BridgeMethod.PipelineVersions || workingCopyRevision < 0 ||
            (status == SourceOnlyBridgeStatus.Available) != (versions.Count != 0))
        {
            throw new ArgumentException("The source-only version response is inconsistent.", nameof(versions));
        }

        string text = status switch
        {
            SourceOnlyBridgeStatus.Available => "available",
            SourceOnlyBridgeStatus.NotFound => "notFound",
            SourceOnlyBridgeStatus.Conflict => "conflict",
            SourceOnlyBridgeStatus.Forbidden => "forbidden",
            SourceOnlyBridgeStatus.Unavailable => "unavailable",
            SourceOnlyBridgeStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        return BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", text);
            writer.WriteNumber("workingCopyRevision", workingCopyRevision);
            writer.WriteStartArray("versions");
            foreach (SourceOnlyVersionHistoryEntry entry in versions)
            {
                writer.WriteStartObject();
                writer.WriteString("versionId", entry.VersionId.ToString());
                writer.WriteString("versionDigest", entry.VersionDigest.Hex);
                if (entry.ParentVersionId is null) { writer.WriteNull("parentVersionId"); }
                else { writer.WriteString("parentVersionId", entry.ParentVersionId.ToString()); }
                writer.WriteString("executionId", entry.ExecutionId.ToString());
                writer.WriteString("phase", entry.Phase.ToString());
                writer.WriteBoolean("hasResult", entry.HasResult);
                writer.WriteBoolean("isCurrent", entry.IsCurrent);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}