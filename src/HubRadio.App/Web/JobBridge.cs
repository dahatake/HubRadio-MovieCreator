using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.App.Rendering;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum JobBridgeErrorCode
{
    Forbidden,
    NotFound,
    Conflict,
    Unavailable,
}

internal enum JobBridgeKind
{
    MediaIngest,
    FinalRender,
    Unknown,
}

internal enum JobBridgeReconciliationStatus
{
    NotApplicable,
    NotRequired,
    Required,
    Unknown,
}

internal sealed class JobBridgeDetails
{
    internal JobBridgeDetails(
        int? revision,
        IReadOnlyList<MediaInfoWarningCode> warnings,
        JobBridgeReconciliationStatus reconciliationStatus)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (revision is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "A details revision must not be negative.");
        }

        if (!Enum.IsDefined(reconciliationStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconciliationStatus),
                reconciliationStatus,
                "Unknown reconciliation status.");
        }

        MediaInfoWarningCode[] copiedWarnings = warnings.ToArray();
        if (copiedWarnings.Any(static warning => !Enum.IsDefined(warning)) ||
            copiedWarnings.Distinct().Count() != copiedWarnings.Length)
        {
            throw new ArgumentException(
                "Job warnings must contain unique defined codes.",
                nameof(warnings));
        }

        Array.Sort(copiedWarnings);
        Revision = revision;
        Warnings = Array.AsReadOnly(copiedWarnings);
        ReconciliationStatus = reconciliationStatus;
    }

    internal int? Revision { get; }

    internal IReadOnlyList<MediaInfoWarningCode> Warnings { get; }

    internal JobBridgeReconciliationStatus ReconciliationStatus { get; }

    internal static JobBridgeDetails NotApplicable { get; } = new(
        revision: null,
        warnings: [],
        JobBridgeReconciliationStatus.NotApplicable);
}

internal interface IJobBridgeDetailsProvider
{
    JobBridgeDetails GetDetails(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        JobBridgeKind kind,
        JobSnapshot snapshot);
}

internal sealed class DurableJobBridgeDetailsProvider : IJobBridgeDetailsProvider
{
    private readonly IngestWorkflowStore ingestWorkflowStore;

    internal DurableJobBridgeDetailsProvider(
        IngestWorkflowStore ingestWorkflowStore)
    {
        ArgumentNullException.ThrowIfNull(ingestWorkflowStore);
        this.ingestWorkflowStore = ingestWorkflowStore;
    }

    public JobBridgeDetails GetDetails(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        JobBridgeKind kind,
        JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (kind != JobBridgeKind.MediaIngest)
        {
            return JobBridgeDetails.NotApplicable;
        }

        try
        {
            StoredIngestWorkflow? stored = ingestWorkflowStore.Get(jobId);
            if (stored is null)
            {
                return new JobBridgeDetails(
                    revision: null,
                    warnings: [],
                    JobBridgeReconciliationStatus.Unknown);
            }

            if (stored.ProjectId != projectId ||
                !string.Equals(
                    stored.RequestSchemaVersion,
                    IngestWorkflowRequest.CurrentVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    stored.CheckpointSchemaVersion,
                    IngestWorkflowCheckpointCodec.SchemaVersion,
                    StringComparison.Ordinal) ||
                stored.CheckpointAttemptNumber > snapshot.CurrentAttemptNumber)
            {
                throw new InvalidOperationException(
                    "The durable ingest checkpoint binding is invalid.");
            }

            IngestWorkflowRequest request =
                IngestWorkflowCheckpointCodec.DeserializeRequest(
                    stored.RequestPayloadJson);
            if (request.JobId != stored.JobId ||
                request.ProjectId != stored.ProjectId ||
                request.RequestDigest != stored.RequestDigest)
            {
                throw new InvalidOperationException(
                    "The durable ingest request binding is invalid.");
            }

            IReadOnlyList<MediaInfoWarningCode> warnings =
                IngestWorkflowCheckpointCodec.ReadWarningCodes(
                    stored.CheckpointPayloadJson,
                    stored.JobId,
                    stored.ProjectId,
                    stored.RequestDigest,
                    stored.CheckpointStage);
            return new JobBridgeDetails(
                stored.Revision,
                warnings,
                JobBridgeReconciliationStatus.Unknown);
        }
        catch (InvalidOperationException)
        {
            return new JobBridgeDetails(
                revision: null,
                warnings: [],
                JobBridgeReconciliationStatus.Required);
        }
    }
}

internal interface IJobBridgeAuthorization
{
    void DemandView();

    void DemandManage();
}

internal sealed class JobBridgeAuthorization : IJobBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal JobBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorSidProvider(actorProvider))
    {
    }

    internal JobBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public void DemandView()
    {
        authorizationService.Demand(
            getCurrentSid(),
            AuthorizationOperation.ViewProject);
    }

    public void DemandManage()
    {
        authorizationService.DemandAs(
            getCurrentSid(),
            AuthorizationRole.Editor,
            AuthorizationOperation.CreateProject);
    }

    private static Func<string> GetActorSidProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class JobBridgeRequestHandler
{
    private readonly JobCoordinator coordinator;
    private readonly IJobBridgeAuthorization authorization;
    private readonly IJobBridgeDetailsProvider detailsProvider;

    internal JobBridgeRequestHandler(
        JobCoordinator coordinator,
        IJobBridgeAuthorization authorization,
        IJobBridgeDetailsProvider detailsProvider)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(detailsProvider);
        this.coordinator = coordinator;
        this.authorization = authorization;
        this.detailsProvider = detailsProvider;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Method switch
            {
                BridgeMethod.JobsGetStatus => HandleStatus(request),
                BridgeMethod.JobsCancel => HandleCancellation(request),
                BridgeMethod.JobsRetry => HandleRetry(request),
                _ => throw new ArgumentException(
                    "A job bridge handler requires a job request.",
                    nameof(request)),
            };
        }
        catch (AuthorizationException)
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.Forbidden);
        }
        catch (Exception exception)
            when (exception is StoreConcurrencyException or
                JobRevisionConflictException)
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.Conflict);
        }
        catch (Exception)
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.Unavailable);
        }
    }

    private string HandleStatus(BridgeRequest request)
    {
        authorization.DemandView();
        if (!TryGetCurrent(
                request,
                out StoredJob? stored,
                out JobSnapshot? snapshot))
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.NotFound);
        }

        JobBridgeKind kind = ClassifyKind(stored.Job.Kind);
        JobBridgeDetails details = detailsProvider.GetDetails(
            stored.Job.Id,
            stored.Job.ProjectId,
            kind,
            snapshot);
        return JobBridgeResponseSerializer.SerializeStatus(
            request,
            kind,
            snapshot,
            details);
    }

    private string HandleCancellation(BridgeRequest request)
    {
        authorization.DemandManage();
        if (!TryGetCurrent(request, out StoredJob? stored, out _))
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.NotFound);
        }

        JobCancellationResult result = coordinator.RequestCancellation(
            request.JobId!,
            request.ExpectedRevision!.Value);
        JobBridgeKind kind = ClassifyKind(stored.Job.Kind);
        JobBridgeDetails details = detailsProvider.GetDetails(
            stored.Job.Id,
            stored.Job.ProjectId,
            kind,
            result.Snapshot);
        return JobBridgeResponseSerializer.SerializeCancellation(
            request,
            result,
            kind,
            details);
    }

    private string HandleRetry(BridgeRequest request)
    {
        authorization.DemandManage();
        if (!TryGetCurrent(request, out StoredJob? stored, out _))
        {
            return JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.NotFound);
        }

        JobRetryResult result = coordinator.QueueRetry(
            request.JobId!,
            request.ExpectedRevision!.Value);
        JobBridgeKind kind = ClassifyKind(stored.Job.Kind);
        JobBridgeDetails details = detailsProvider.GetDetails(
            stored.Job.Id,
            stored.Job.ProjectId,
            kind,
            result.Snapshot);
        return JobBridgeResponseSerializer.SerializeRetry(
            request,
            result,
            kind,
            details);
    }

    private bool TryGetCurrent(
        BridgeRequest request,
        [NotNullWhen(true)] out StoredJob? stored,
        [NotNullWhen(true)] out JobSnapshot? snapshot)
    {
        stored = coordinator.FindStoredJob(request.JobId!);
        if (stored is null)
        {
            snapshot = null;
            return false;
        }

        snapshot = coordinator.GetSnapshot(request.JobId!);
        return true;
    }

    private static JobBridgeKind ClassifyKind(string kind) =>
        kind switch
        {
            IngestWorkflow.JobKind => JobBridgeKind.MediaIngest,
            FinalRenderService.JobKind => JobBridgeKind.FinalRender,
            _ => JobBridgeKind.Unknown,
        };
}

internal static class JobBridgeResponseSerializer
{
    internal static string SerializeStatus(
        BridgeRequest request,
        JobBridgeKind kind,
        JobSnapshot snapshot,
        JobBridgeDetails details)
    {
        ArrayBufferWriter<byte> buffer = CreateBuffer();
        using Utf8JsonWriter writer = CreateWriter(buffer);
        WriteEnvelopeStart(writer, request);
        writer.WritePropertyName("result");
        WriteSnapshot(writer, kind, snapshot, details);
        writer.WriteEndObject();
        writer.Flush();
        return BridgeProtocol.GetBoundedResponse(buffer);
    }

    internal static string SerializeCancellation(
        BridgeRequest request,
        JobCancellationResult result,
        JobBridgeKind kind,
        JobBridgeDetails details)
    {
        ArgumentNullException.ThrowIfNull(result);
        return SerializeCommand(
            request,
            FormatCancellationOutcome(result.Outcome),
            kind,
            result.Snapshot,
            details);
    }

    internal static string SerializeRetry(
        BridgeRequest request,
        JobRetryResult result,
        JobBridgeKind kind,
        JobBridgeDetails details)
    {
        ArgumentNullException.ThrowIfNull(result);
        return SerializeCommand(
            request,
            FormatRetryOutcome(result.Outcome),
            kind,
            result.Snapshot,
            details);
    }

    internal static string SerializeError(
        BridgeRequest request,
        JobBridgeErrorCode errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArrayBufferWriter<byte> buffer = CreateBuffer();
        using Utf8JsonWriter writer = CreateWriter(buffer);
        WriteEnvelopeStart(writer, request);
        writer.WriteStartObject("error");
        writer.WriteString("code", FormatErrorCode(errorCode));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return BridgeProtocol.GetBoundedResponse(buffer);
    }

    private static string SerializeCommand(
        BridgeRequest request,
        string outcome,
        JobBridgeKind kind,
        JobSnapshot snapshot,
        JobBridgeDetails details)
    {
        ArrayBufferWriter<byte> buffer = CreateBuffer();
        using Utf8JsonWriter writer = CreateWriter(buffer);
        WriteEnvelopeStart(writer, request);
        writer.WriteStartObject("result");
        writer.WriteString("outcome", outcome);
        writer.WritePropertyName("job");
        WriteSnapshot(writer, kind, snapshot, details);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return BridgeProtocol.GetBoundedResponse(buffer);
    }

    private static void WriteEnvelopeStart(
        Utf8JsonWriter writer,
        BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString("method", BridgeProtocol.GetMethodName(request.Method));
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        JobBridgeKind kind,
        JobSnapshot snapshot,
        JobBridgeDetails details)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(details);
        writer.WriteStartObject();
        writer.WriteString("jobId", snapshot.JobId.ToString());
        writer.WriteString("kind", FormatKind(kind));
        writer.WriteString("state", FormatState(snapshot.State));
        if (snapshot.CurrentAttemptId is null)
        {
            writer.WriteNull("currentAttemptId");
        }
        else
        {
            writer.WriteString(
                "currentAttemptId",
                snapshot.CurrentAttemptId.ToString());
        }

        writer.WriteNumber("currentAttemptNumber", snapshot.CurrentAttemptNumber);
        writer.WriteNumber("revision", snapshot.Revision);
        if (details.Revision is int detailsRevision)
        {
            writer.WriteNumber("detailsRevision", detailsRevision);
        }
        else
        {
            writer.WriteNull("detailsRevision");
        }

        writer.WriteString("updatedAt", FormatTimestamp(snapshot.UpdatedAt));
        if (snapshot.ProgressRatio is decimal progressRatio)
        {
            writer.WriteNumber("progressRatio", progressRatio);
        }
        else
        {
            writer.WriteNull("progressRatio");
        }

        WriteOptionalTimestamp(writer, "lastProgressAt", snapshot.LastProgressAt);
        WriteOptionalTimestamp(writer, "lastHeartbeatAt", snapshot.LastHeartbeatAt);
        if (snapshot.ErrorCode is null)
        {
            writer.WriteNull("failure");
        }
        else
        {
            writer.WriteString("failure", FormatFailure(snapshot.ErrorCode));
        }

        writer.WriteString("retryability", FormatRetryability(snapshot.Retryability));
        writer.WriteBoolean(
            "cancellationRequested",
            snapshot.CancellationRequested);
        writer.WriteStartArray("warnings");
        foreach (MediaInfoWarningCode warning in details.Warnings)
        {
            writer.WriteStringValue(FormatWarning(warning));
        }

        writer.WriteEndArray();
        writer.WriteString(
            "reconciliation",
            FormatReconciliation(details.ReconciliationStatus));
        writer.WriteEndObject();
    }

    private static void WriteOptionalTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            writer.WriteString(propertyName, FormatTimestamp(timestamp));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static ArrayBufferWriter<byte> CreateBuffer() => new();

    private static Utf8JsonWriter CreateWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        });

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatKind(JobBridgeKind kind) =>
        kind switch
        {
            JobBridgeKind.MediaIngest => "mediaIngest",
            JobBridgeKind.FinalRender => "finalRender",
            JobBridgeKind.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown job bridge kind."),
        };

    private static string FormatState(JobState state) =>
        state switch
        {
            JobState.Queued => "queued",
            JobState.Running => "running",
            JobState.Succeeded => "succeeded",
            JobState.Failed => "failed",
            JobState.Cancelled => "cancelled",
            JobState.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unknown job state."),
        };

    private static string FormatFailure(string errorCode) =>
        errorCode switch
        {
            "job.execution_timed_out" => "timedOut",
            "job.app_restarted" => "appRestarted",
            _ => "other",
        };

    private static string FormatRetryability(JobRetryability retryability) =>
        retryability switch
        {
            JobRetryability.NotApplicable => "notApplicable",
            JobRetryability.Retryable => "retryable",
            JobRetryability.NonRetryable => "nonRetryable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(retryability),
                retryability,
                "Unknown retryability."),
        };

    private static string FormatWarning(MediaInfoWarningCode warning) =>
        warning switch
        {
            MediaInfoWarningCode.AudioTrackAbsent => "audioTrackAbsent",
            MediaInfoWarningCode.DurationFieldsDiffer => "durationFieldsDiffer",
            MediaInfoWarningCode.LowResolution => "lowResolution",
            MediaInfoWarningCode.MultipleAudioStreams => "multipleAudioStreams",
            MediaInfoWarningCode.MultipleVideoStreams => "multipleVideoStreams",
            MediaInfoWarningCode.SignatureFormatMismatch => "signatureFormatMismatch",
            MediaInfoWarningCode.UnrecognizedContainerSignature =>
                "unrecognizedContainerSignature",
            MediaInfoWarningCode.VariableRateFieldsDiffer =>
                "variableRateFieldsDiffer",
            _ => throw new ArgumentOutOfRangeException(
                nameof(warning),
                warning,
                "Unknown media warning."),
        };

    private static string FormatReconciliation(
        JobBridgeReconciliationStatus status) =>
        status switch
        {
            JobBridgeReconciliationStatus.NotApplicable => "notApplicable",
            JobBridgeReconciliationStatus.NotRequired => "notRequired",
            JobBridgeReconciliationStatus.Required => "required",
            JobBridgeReconciliationStatus.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown reconciliation status."),
        };

    private static string FormatCancellationOutcome(
        JobCancellationOutcome outcome) =>
        outcome switch
        {
            JobCancellationOutcome.Requested => "requested",
            JobCancellationOutcome.AlreadyRequested => "alreadyRequested",
            JobCancellationOutcome.NotRunning => "notRunning",
            JobCancellationOutcome.NotTracked => "notTracked",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown cancellation outcome."),
        };

    private static string FormatRetryOutcome(JobRetryOutcome outcome) =>
        outcome switch
        {
            JobRetryOutcome.Queued => "queued",
            JobRetryOutcome.StateNotEligible => "stateNotEligible",
            JobRetryOutcome.ErrorNotRetryable => "errorNotRetryable",
            JobRetryOutcome.CoordinatorRunActive => "coordinatorRunActive",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown retry outcome."),
        };

    private static string FormatErrorCode(JobBridgeErrorCode errorCode) =>
        errorCode switch
        {
            JobBridgeErrorCode.Forbidden => "forbidden",
            JobBridgeErrorCode.NotFound => "notFound",
            JobBridgeErrorCode.Conflict => "conflict",
            JobBridgeErrorCode.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(errorCode),
                errorCode,
                "Unknown job bridge error code."),
        };
}