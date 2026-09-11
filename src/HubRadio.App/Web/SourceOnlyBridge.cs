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
                return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid);
            }

            string actor = currentActorSid();
            Demand(actor, request.Method);
            EntityId<ProjectIdTag> projectId = SourceOnlyBridgeProtocol.ReadProjectId(request);
            StoredSourceOnlyExecution? execution;
            if (request.Method == BridgeMethod.PipelineRead)
            {
                execution = executionStore.GetLatestForProject(projectId);
            }
            else
            {
                SourceOnlyPipeline? pipeline = pipelineProvider();
                if (pipeline is null)
                {
                    return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable);
                }

                execution = executionStore.GetLatestForProject(projectId);
                if (execution is null)
                {
                    return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.NotFound);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (request.Method == BridgeMethod.PipelineRegenerate)
                {
                    // A terminal execution may still own unresolved side effects. Reject
                    // those before accepting a new root, even when no job is busy.
                    if (execution.Phase < SourceOnlyExecutionPhase.Completed ||
                        execution.Phase == SourceOnlyExecutionPhase.ReconciliationRequired)
                    {
                        return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    if (execution.ResultJson is not null)
                    {
                        SourceOnlyResult saved = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
                        saved.ValidateBinding(execution);
                        if (saved.StagingCleanupRequired || saved.ReconciliationRequired)
                        {
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
                        }
                    }

                    JsonElement parameters = request.Parameters;
                    var parentId = EntityId<CompositionVersionIdTag>.Parse(parameters.GetProperty("expectedVersionId").GetString()!);
                    var parentDigest = new ContentDigest(parameters.GetProperty("expectedVersionDigest").GetString()!);
                    int settingsRevision = SourceOnlyBridgeProtocol.ReadRevision(parameters, "expectedSettingsRevision");
                    StoredProjectSettings? settings = settingsStore.GetProjectSettings(projectId).Settings;
                    if (settings is null)
                    {
                        return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable);
                    }

                    CompositionVersion? parent = versions.GetVersion(parentId);
                    WorkingCopyState? head = versions.GetCurrentState(projectId);
                    if (settings.ProjectId != projectId || settings.Revision != settingsRevision ||
                        settings.CurrentSnapshot.ContentDigest.Hex != parameters.GetProperty("expectedSettingsSnapshotDigest").GetString() ||
                        parent is null || parent.ProjectId != projectId || parent.ContentDigest != parentDigest ||
                        head is null || head.WorkingCopy.ProjectId != projectId || head.WorkingCopy.BaseVersionId != parentId ||
                        !head.WorkingCopy.Elements.SequenceEqual(parent.Elements))
                    {
                        return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
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
                        return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.NotFound);
                    }

                    int expectedRevision = SourceOnlyBridgeProtocol.ReadRevision(request.Parameters, "expectedRevision");
                    if (target.Request.ExecutionId != execution.Request.ExecutionId || target.Revision != expectedRevision)
                    {
                        return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
                    }

                    if (request.Method == BridgeMethod.PipelineRetry)
                    {
                        if (target.Phase is not (SourceOnlyExecutionPhase.Failed or SourceOnlyExecutionPhase.Canceled) ||
                            target.ResultJson is not null || target.VersionId is not null)
                        {
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
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
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
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
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
                        }
                        SourceOnlyPipelineRun? canceled = await pipeline.CancelAsync(executionId).ConfigureAwait(false);
                        if (canceled is null || canceled.InfrastructureFailureCode is not null || canceled.Execution is null)
                        {
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable);
                        }

                        if (canceled.ExecutionId != executionId || canceled.Execution.Request.ProjectId != projectId)
                        {
                            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid);
                        }

                        execution = executionStore.Get(executionId);
                    }
                }
            }

            if (execution is null)
            {
                return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.NotFound);
            }

            if (execution.Request.ProjectId != projectId)
            {
                return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid);
            }

            SourceOnlyBridgeSnapshot snapshot = await projection.ReadAsync(execution, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(actor, currentActorSid(), StringComparison.Ordinal))
            {
                return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Forbidden);
            }

            Demand(actor, request.Method);
            return SourceOnlyBridgeJson.WriteResponse(request,
                snapshot.ResultStatus == "invalid" ? SourceOnlyBridgeStatus.Invalid : SourceOnlyBridgeStatus.Available, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AuthorizationException)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Forbidden);
        }
        catch (StoreConcurrencyException)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
        }
        catch (PipelineConflictException)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Conflict);
        }
        catch (ObjectDisposedException)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or
            SourceOnlyResultException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid);
        }
        catch (Exception)
        {
            return SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable);
        }
    }

    private void Demand(string actor, BridgeMethod method)
    {
        authorization.Demand(actor, AuthorizationOperation.ViewProject);
        if (method != BridgeMethod.PipelineRead)
        {
            authorization.DemandAs(actor, AuthorizationRole.Editor, AuthorizationOperation.CreateCompositionVersion);
        }
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
}