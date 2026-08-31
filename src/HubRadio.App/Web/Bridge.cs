using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App.Web;

internal enum BridgeRequestRejection
{
    None,
    OriginNotAllowed,
    MessageTooLarge,
    MalformedJson,
    SchemaViolation,
    MethodNotAllowed,
}

internal enum BridgeMethod
{
    Ping,
    ListProjects,
    CreateDraft,
    LoadDraft,
    SaveDraft,
    PickSource,
    FinalizeDraft,
    JobsGetStatus,
    JobsCancel,
    JobsRetry,
}

internal sealed class BridgeRequest
{
    private BridgeRequest(
        Guid requestId,
        BridgeMethod method,
        JsonElement parameters,
        EntityId<JobIdTag>? jobId,
        int? expectedRevision)
    {
        RequestId = requestId;
        Method = method;
        Parameters = parameters;
        JobId = jobId;
        ExpectedRevision = expectedRevision;
    }

    internal Guid RequestId { get; }

    internal BridgeMethod Method { get; }

    internal JsonElement Parameters { get; }

    internal EntityId<JobIdTag>? JobId { get; }

    internal int? ExpectedRevision { get; }

    internal static BridgeRequest Create(
        Guid requestId,
        BridgeMethod method,
        JsonElement parameters,
        EntityId<JobIdTag>? jobId = null,
        int? expectedRevision = null) =>
        new(requestId, method, parameters, jobId, expectedRevision);
}

internal static class BridgeProtocol
{
    internal const int Version = 1;
    internal const int MaxRequestUtf8Bytes = 256;
    internal const int MaxResponseUtf8Bytes = 4096;
    internal const int MaxProjectRequestUtf8Bytes = 96 * 1024;
    internal const int MaxProjectResponseUtf8Bytes = 1024 * 1024;
    internal const string PingMethod = "bridge.ping";
    internal const string ListProjectsMethod = "project.list";
    internal const string CreateDraftMethod = "project.createDraft";
    internal const string LoadDraftMethod = "project.loadDraft";
    internal const string SaveDraftMethod = "project.saveDraft";
    internal const string PickSourceMethod = "project.pickSource";
    internal const string FinalizeDraftMethod = "project.finalizeDraft";

    private static readonly string[] DraftInputProperties =
    [
        "sourceCandidateId",
        "contentName",
        "purpose",
        "audience",
        "publicationScope",
        "outputProfileIds",
        "language",
        "performerNames",
        "deadlineSelection",
        "brandTemplateId",
    ];
    internal const string JobsGetStatusMethod = "jobs.getStatus";
    internal const string JobsCancelMethod = "jobs.cancel";
    internal const string JobsRetryMethod = "jobs.retry";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool IsAllowedMessageOrigin(string? source)
    {
        return WebViewHost.IsAllowedOrigin(source);
    }

    internal static bool TryParseRequest(
        string? source,
        string? messageJson,
        out BridgeRequest? request,
        out BridgeRequestRejection rejection)
    {
        request = null;

        if (!IsAllowedMessageOrigin(source))
        {
            rejection = BridgeRequestRejection.OriginNotAllowed;
            return false;
        }

        if (messageJson is null)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(messageJson);
        }
        catch (EncoderFallbackException)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }

        if (byteCount > MaxProjectRequestUtf8Bytes)
        {
            rejection = BridgeRequestRejection.MessageTooLarge;
            return false;
        }

        byte[] canonical;
        try
        {
            canonical = CanonicalJson.CanonicalizeToUtf8(
                StrictUtf8.GetBytes(messageJson));
        }
        catch (EncoderFallbackException)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }
        catch (JsonException)
        {
            rejection = byteCount > MaxRequestUtf8Bytes
                ? BridgeRequestRejection.MessageTooLarge
                : BridgeRequestRejection.MalformedJson;
            return false;
        }
        catch (FormatException)
        {
            rejection = byteCount > MaxRequestUtf8Bytes
                ? BridgeRequestRejection.MessageTooLarge
                : BridgeRequestRejection.MalformedJson;
            return false;
        }
        catch (OverflowException)
        {
            rejection = byteCount > MaxRequestUtf8Bytes
                ? BridgeRequestRejection.MessageTooLarge
                : BridgeRequestRejection.MalformedJson;
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement root = document.RootElement;
        string? methodName = null;
        BridgeMethod parsedMethod = default;
        bool hasKnownMethod = false;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("method", out JsonElement candidateMethod)
            && candidateMethod.ValueKind == JsonValueKind.String
            && candidateMethod.GetString() is string candidateMethodName)
        {
            methodName = candidateMethodName;
            hasKnownMethod = TryParseMethod(methodName, out parsedMethod);
        }

        int maximumRequestUtf8Bytes = hasKnownMethod
            ? GetMaximumRequestUtf8Bytes(parsedMethod)
            : MaxRequestUtf8Bytes;
        if (byteCount > maximumRequestUtf8Bytes)
        {
            rejection = BridgeRequestRejection.MessageTooLarge;
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 4
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int parsedVersion)
            || parsedVersion != Version
            || !root.TryGetProperty("requestId", out JsonElement requestId)
            || requestId.ValueKind != JsonValueKind.String
            || !TryParseRequestId(requestId.GetString(), out Guid parsedRequestId)
            || methodName is null
            || !root.TryGetProperty("params", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            rejection = BridgeRequestRejection.SchemaViolation;
            return false;
        }

        if (!hasKnownMethod)
        {
            rejection = BridgeRequestRejection.MethodNotAllowed;
            return false;
        }

        if (!TryCreateRequest(
                parsedRequestId,
                parsedMethod,
                parameters,
                out request))
        {
            rejection = BridgeRequestRejection.SchemaViolation;
            return false;
        }

        rejection = BridgeRequestRejection.None;
        return true;
    }

    internal static string CreatePingResponse(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != BridgeMethod.Ping)
        {
            throw new ArgumentException(
                "A ping response requires a ping request.",
                nameof(request));
        }

        return CreateResultResponse(
            request,
            static writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("protocolVersion", Version);
                writer.WriteEndObject();
            });
    }

    internal static string CreateResultResponse(
        BridgeRequest request,
        Action<Utf8JsonWriter> writeResult)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeResult);

        return WriteBoundedResponse(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteString("requestId", request.RequestId.ToString("D"));
                writer.WriteString("method", GetMethodName(request.Method));
                writer.WritePropertyName("result");
                writeResult(writer);
                writer.WriteEndObject();
            },
            GetMaximumResponseUtf8Bytes(request.Method));
    }

    internal static string CreateErrorResponse(
        BridgeRequest request,
        string code)
    {
        ArgumentNullException.ThrowIfNull(request);
        StableErrorCodes.Validate(code);

        return WriteBoundedResponse(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteString("requestId", request.RequestId.ToString("D"));
                writer.WriteString("method", GetMethodName(request.Method));
                writer.WriteStartObject("error");
                writer.WriteString("code", code);
                writer.WriteString("message", GetErrorMessage(code));
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            GetMaximumResponseUtf8Bytes(request.Method));
    }

    internal static string GetMethodName(BridgeMethod method) =>
        method switch
        {
            BridgeMethod.Ping => PingMethod,
            BridgeMethod.ListProjects => ListProjectsMethod,
            BridgeMethod.CreateDraft => CreateDraftMethod,
            BridgeMethod.LoadDraft => LoadDraftMethod,
            BridgeMethod.SaveDraft => SaveDraftMethod,
            BridgeMethod.PickSource => PickSourceMethod,
            BridgeMethod.FinalizeDraft => FinalizeDraftMethod,
            BridgeMethod.JobsGetStatus => JobsGetStatusMethod,
            BridgeMethod.JobsCancel => JobsCancelMethod,
            BridgeMethod.JobsRetry => JobsRetryMethod,
            _ => throw new ArgumentOutOfRangeException(
                nameof(method),
                method,
                "Unknown bridge method."),
        };

    internal static int GetMaximumResponseUtf8Bytes(BridgeMethod method) =>
        IsProjectMethod(method)
            ? MaxProjectResponseUtf8Bytes
            : MaxResponseUtf8Bytes;

    internal static bool IsProjectMethod(BridgeMethod method) =>
        method is
            BridgeMethod.ListProjects or
            BridgeMethod.CreateDraft or
            BridgeMethod.LoadDraft or
            BridgeMethod.SaveDraft or
            BridgeMethod.PickSource or
            BridgeMethod.FinalizeDraft;

    private static int GetMaximumRequestUtf8Bytes(BridgeMethod method) =>
        IsProjectMethod(method)
            ? MaxProjectRequestUtf8Bytes
            : MaxRequestUtf8Bytes;

    private static bool TryParseMethod(
        string method,
        out BridgeMethod parsedMethod)
    {
        parsedMethod = method switch
        {
            PingMethod => BridgeMethod.Ping,
            ListProjectsMethod => BridgeMethod.ListProjects,
            CreateDraftMethod => BridgeMethod.CreateDraft,
            LoadDraftMethod => BridgeMethod.LoadDraft,
            SaveDraftMethod => BridgeMethod.SaveDraft,
            PickSourceMethod => BridgeMethod.PickSource,
            FinalizeDraftMethod => BridgeMethod.FinalizeDraft,
            JobsGetStatusMethod => BridgeMethod.JobsGetStatus,
            JobsCancelMethod => BridgeMethod.JobsCancel,
            JobsRetryMethod => BridgeMethod.JobsRetry,
            _ => (BridgeMethod)(-1),
        };
        return Enum.IsDefined(parsedMethod);
    }

    private static bool HasValidParameters(
        BridgeMethod method,
        JsonElement parameters)
    {
        if (method is
            BridgeMethod.Ping or
            BridgeMethod.ListProjects or
            BridgeMethod.CreateDraft or
            BridgeMethod.PickSource)
        {
            return parameters.EnumerateObject().Any() is false;
        }

        if (method == BridgeMethod.LoadDraft)
        {
            return HasExactProperties(parameters, ["draftId"]) &&
                IsCanonicalEntityId(parameters.GetProperty("draftId"));
        }

        if (method is BridgeMethod.SaveDraft or BridgeMethod.FinalizeDraft)
        {
            return HasExactProperties(
                    parameters,
                    ["draftId", "expectedRevision", "input"]) &&
                IsCanonicalEntityId(parameters.GetProperty("draftId")) &&
                IsNonnegativeInt32(parameters.GetProperty("expectedRevision")) &&
                IsDraftInput(parameters.GetProperty("input"));
        }

        return false;
    }

    private static bool IsDraftInput(JsonElement input)
    {
        if (!HasExactProperties(input, DraftInputProperties))
        {
            return false;
        }

        JsonElement sourceCandidateId = input.GetProperty("sourceCandidateId");
        JsonElement contentName = input.GetProperty("contentName");
        JsonElement purpose = input.GetProperty("purpose");
        JsonElement audience = input.GetProperty("audience");
        JsonElement publicationScope = input.GetProperty("publicationScope");
        JsonElement outputProfileIds = input.GetProperty("outputProfileIds");
        JsonElement language = input.GetProperty("language");
        JsonElement performerNames = input.GetProperty("performerNames");
        JsonElement deadlineSelection = input.GetProperty("deadlineSelection");
        JsonElement brandTemplateId = input.GetProperty("brandTemplateId");

        return IsNullOrCanonicalEntityId(sourceCandidateId) &&
            IsNullOrString(contentName) &&
            IsNullOrString(purpose) &&
            IsNullOrString(audience) &&
            IsNullOrPublicationScope(publicationScope) &&
            IsArrayOfCanonicalEntityIds(outputProfileIds) &&
            IsNullOrString(language) &&
            IsArrayOfStrings(performerNames) &&
            IsDeadlineSelection(deadlineSelection) &&
            IsNullOrCanonicalEntityId(brandTemplateId);
    }

    private static bool IsDeadlineSelection(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!HasExactProperties(value, ["hasDeadline", "value"]))
        {
            return false;
        }

        JsonElement hasDeadline = value.GetProperty("hasDeadline");
        JsonElement selectedValue = value.GetProperty("value");
        if (hasDeadline.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        return hasDeadline.GetBoolean()
            ? IsCanonicalInputTimestamp(selectedValue)
            : selectedValue.ValueKind == JsonValueKind.Null;
    }

    private static bool IsCanonicalInputTimestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text)
        {
            return false;
        }

        bool parsed = DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        return parsed && timestamp.Offset == TimeSpan.Zero;
    }

    private static bool IsNullOrPublicationScope(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = value.GetString();
        return text is
            "internalOnly" or
            "specificGroupOnly" or
            "externalCandidate" or
            "externalPublicationApproved" or
            "publicationForbidden" or
            "pendingDecision";
    }

    private static bool IsArrayOfCanonicalEntityIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!IsCanonicalEntityId(item) || !ids.Add(item.GetString()!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsArrayOfStrings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not string text ||
                !values.Add(text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNullOrString(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool IsNullOrCanonicalEntityId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || IsCanonicalEntityId(value);

    private static bool IsCanonicalEntityId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        TryParseRequestId(value.GetString(), out _);

    private static bool IsNonnegativeInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) &&
        parsed >= 0;

    private static bool HasExactProperties(
        JsonElement value,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var foundProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal) ||
                !foundProperties.Add(property.Name))
            {
                return false;
            }
        }

        return foundProperties.Count == expectedProperties.Count;
    }

    private static string WriteBoundedResponse(
        Action<Utf8JsonWriter> writeResponse,
        int maximumUtf8Bytes)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writeResponse(writer);
        }

        if (buffer.WrittenCount > maximumUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The bridge response exceeds the supported byte limit.");
        }

        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    internal static string GetBoundedResponse(ArrayBufferWriter<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.WrittenCount > MaxResponseUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The bridge response exceeds the supported size.");
        }

        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static string GetErrorMessage(string code) => code switch
    {
        StableErrorCodes.ProjectAlreadyFinalized =>
            "この案件はすでに作成済みです。",
        StableErrorCodes.ProjectIncomplete =>
            "必須入力をすべて完了してください。",
        StableErrorCodes.ProjectInvalidRequest =>
            "入力内容を確認してください。",
        StableErrorCodes.ProjectNotAuthorized =>
            "この操作を実行する権限がありません。",
        StableErrorCodes.ProjectNotFound =>
            "指定された下書きが見つかりません。",
        StableErrorCodes.ProjectRevisionConflict =>
            "下書きが別の操作で更新されました。再読み込みしてください。",
        StableErrorCodes.ProjectSourceReselectionRequired =>
            "元動画をもう一度選択してください。",
        _ => "案件操作を完了できませんでした。",
    };

    private static bool TryCreateRequest(
        Guid requestId,
        BridgeMethod method,
        JsonElement parameters,
        out BridgeRequest? request)
    {
        request = null;
        if (method == BridgeMethod.Ping || IsProjectMethod(method))
        {
            if (!HasValidParameters(method, parameters))
            {
                return false;
            }

            request = BridgeRequest.Create(
                requestId,
                method,
                parameters.Clone());
            return true;
        }

        int expectedPropertyCount = method == BridgeMethod.JobsGetStatus ? 1 : 2;
        if (parameters.EnumerateObject().Count() != expectedPropertyCount ||
            !parameters.TryGetProperty("jobId", out JsonElement jobId) ||
            jobId.ValueKind != JsonValueKind.String ||
            !TryParseJobId(jobId.GetString(), out EntityId<JobIdTag>? parsedJobId))
        {
            return false;
        }

        if (method == BridgeMethod.JobsGetStatus)
        {
            request = BridgeRequest.Create(
                requestId,
                method,
                parameters.Clone(),
                parsedJobId);
            return true;
        }

        if (!parameters.TryGetProperty(
                "expectedRevision",
                out JsonElement expectedRevision) ||
            !TryParseExpectedRevision(expectedRevision, out int parsedRevision))
        {
            return false;
        }

        request = BridgeRequest.Create(
            requestId,
            method,
            parameters.Clone(),
            parsedJobId,
            parsedRevision);
        return true;
    }

    private static bool TryParseRequestId(string? value, out Guid requestId)
    {
        if (value is not null
            && Guid.TryParseExact(value, "D", out requestId)
            && requestId != Guid.Empty
            && string.Equals(value, requestId.ToString("D"), StringComparison.Ordinal))
        {
            return true;
        }

        requestId = Guid.Empty;
        return false;
    }

    private static bool TryParseJobId(
        string? value,
        [NotNullWhen(true)] out EntityId<JobIdTag>? jobId)
    {
        if (EntityId<JobIdTag>.TryParse(value, out jobId) &&
            string.Equals(value, jobId.ToString(), StringComparison.Ordinal))
        {
            return true;
        }

        jobId = null;
        return false;
    }

    private static bool TryParseExpectedRevision(
        JsonElement value,
        out int expectedRevision)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out decimal parsed) &&
            parsed == decimal.Truncate(parsed) &&
            parsed is >= 0 and <= int.MaxValue)
        {
            expectedRevision = decimal.ToInt32(parsed);
            return true;
        }

        expectedRevision = default;
        return false;
    }
}

internal sealed class Bridge : IDisposable
{
    private readonly CoreWebView2 coreWebView;
    private readonly Func<BridgeRequest, CancellationToken, Task<string>>?
        handleProjectRequestAsync;
    private readonly JobBridgeRequestHandler? jobHandler;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isStarted;
    private bool isDisposed;

    internal Bridge(CoreWebView2 coreWebView)
        : this(
            coreWebView,
            handleProjectRequestAsync: null,
            jobHandler: null)
    {
    }

    internal Bridge(
        CoreWebView2 coreWebView,
        Func<BridgeRequest, CancellationToken, Task<string>> handleProjectRequestAsync)
        : this(coreWebView, handleProjectRequestAsync, jobHandler: null)
    {
    }

    internal Bridge(
        CoreWebView2 coreWebView,
        JobBridgeRequestHandler? jobHandler)
        : this(coreWebView, handleProjectRequestAsync: null, jobHandler)
    {
    }

    internal Bridge(
        CoreWebView2 coreWebView,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleProjectRequestAsync,
        JobBridgeRequestHandler? jobHandler)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        this.coreWebView = coreWebView;
        this.handleProjectRequestAsync = handleProjectRequestAsync;
        this.jobHandler = jobHandler;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (isStarted)
        {
            throw new InvalidOperationException("The web bridge is already started.");
        }

        coreWebView.WebMessageReceived += OnWebMessageReceived;
        try
        {
            coreWebView.Settings.IsWebMessageEnabled = true;
            isStarted = true;
        }
        catch
        {
            coreWebView.WebMessageReceived -= OnWebMessageReceived;
            throw;
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        lifetimeCancellation.Cancel();
        if (!isStarted)
        {
            lifetimeCancellation.Dispose();
            return;
        }

        coreWebView.Settings.IsWebMessageEnabled = false;
        coreWebView.WebMessageReceived -= OnWebMessageReceived;
        lifetimeCancellation.Dispose();
    }

    private async void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (sender is not CoreWebView2 coreWebView
            || !BridgeProtocol.TryParseRequest(
                args.Source,
                args.WebMessageAsJson,
                out BridgeRequest? request,
                out _)
            || request is null)
        {
            return;
        }

        string response;
        try
        {
            response = await HandleRequestAsync(
                request,
                lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            response = CreateUnavailableResponse(request);
        }

        if (!isDisposed)
        {
            coreWebView.PostWebMessageAsJson(response);
        }
    }

    private async Task<string> HandleRequestAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method == BridgeMethod.Ping)
        {
            return BridgeProtocol.CreatePingResponse(request);
        }

        if (BridgeProtocol.IsProjectMethod(request.Method))
        {
            return handleProjectRequestAsync is null
                ? BridgeProtocol.CreateErrorResponse(
                    request,
                    StableErrorCodes.ProjectOperationFailed)
                : await handleProjectRequestAsync(request, cancellationToken);
        }

        return jobHandler?.Handle(request) ??
            JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.Unavailable);
    }

    private static string CreateUnavailableResponse(BridgeRequest request)
    {
        return BridgeProtocol.IsProjectMethod(request.Method)
            ? BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectOperationFailed)
            : JobBridgeResponseSerializer.SerializeError(
                request,
                JobBridgeErrorCode.Unavailable);
    }
}