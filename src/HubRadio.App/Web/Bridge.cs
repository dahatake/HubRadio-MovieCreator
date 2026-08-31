using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

internal sealed class BridgeRequest
{
    internal BridgeRequest(
        Guid requestId,
        string method,
        JsonElement parameters)
    {
        RequestId = requestId;
        Method = method;
        Parameters = parameters;
    }

    internal Guid RequestId { get; }

    internal string Method { get; }

    internal JsonElement Parameters { get; }
}

internal static class BridgeProtocol
{
    internal const int Version = 1;
    internal const int MaxRequestUtf8Bytes = 96 * 1024;
    internal const int MaxResponseUtf8Bytes = 1024 * 1024;
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

        byte[] canonical;
        try
        {
            int byteCount = StrictUtf8.GetByteCount(messageJson);
            if (byteCount > MaxRequestUtf8Bytes)
            {
                rejection = BridgeRequestRejection.MessageTooLarge;
                return false;
            }

            canonical = CanonicalJson.CanonicalizeToUtf8(StrictUtf8.GetBytes(messageJson));
        }
        catch (EncoderFallbackException)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }
        catch (JsonException)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }
        catch (FormatException)
        {
            rejection = BridgeRequestRejection.MalformedJson;
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 4
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int parsedVersion)
            || parsedVersion != Version
            || !root.TryGetProperty("requestId", out JsonElement requestId)
            || requestId.ValueKind != JsonValueKind.String
            || !TryParseRequestId(requestId.GetString(), out Guid parsedRequestId)
            || !root.TryGetProperty("method", out JsonElement method)
            || method.ValueKind != JsonValueKind.String
            || method.GetString() is not string parsedMethod
            || !root.TryGetProperty("params", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            rejection = BridgeRequestRejection.SchemaViolation;
            return false;
        }

        if (!IsAllowedMethod(parsedMethod))
        {
            rejection = BridgeRequestRejection.MethodNotAllowed;
            return false;
        }

        if (!HasValidParameters(parsedMethod, parameters))
        {
            rejection = BridgeRequestRejection.SchemaViolation;
            return false;
        }

        request = new BridgeRequest(
            parsedRequestId,
            parsedMethod,
            parameters.Clone());
        rejection = BridgeRequestRejection.None;
        return true;
    }

    internal static string CreatePingResponse(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Method, PingMethod, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request is not a ping request.", nameof(request));
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

        return WriteBoundedResponse(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteString("method", request.Method);
            writer.WritePropertyName("result");
            writeResult(writer);
            writer.WriteEndObject();
        });
    }

    internal static string CreateErrorResponse(
        BridgeRequest request,
        string code)
    {
        ArgumentNullException.ThrowIfNull(request);
        StableErrorCodes.Validate(code);

        return WriteBoundedResponse(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteString("method", request.Method);
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", GetErrorMessage(code));
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static bool IsAllowedMethod(string method) =>
        string.Equals(method, PingMethod, StringComparison.Ordinal) ||
        string.Equals(method, ListProjectsMethod, StringComparison.Ordinal) ||
        string.Equals(method, CreateDraftMethod, StringComparison.Ordinal) ||
        string.Equals(method, LoadDraftMethod, StringComparison.Ordinal) ||
        string.Equals(method, SaveDraftMethod, StringComparison.Ordinal) ||
        string.Equals(method, PickSourceMethod, StringComparison.Ordinal) ||
        string.Equals(method, FinalizeDraftMethod, StringComparison.Ordinal);

    private static bool HasValidParameters(
        string method,
        JsonElement parameters)
    {
        if (string.Equals(method, PingMethod, StringComparison.Ordinal) ||
            string.Equals(method, ListProjectsMethod, StringComparison.Ordinal) ||
            string.Equals(method, CreateDraftMethod, StringComparison.Ordinal) ||
            string.Equals(method, PickSourceMethod, StringComparison.Ordinal))
        {
            return parameters.EnumerateObject().Any() is false;
        }

        if (string.Equals(method, LoadDraftMethod, StringComparison.Ordinal))
        {
            return HasExactProperties(parameters, ["draftId"]) &&
                IsCanonicalEntityId(parameters.GetProperty("draftId"));
        }

        if (string.Equals(method, SaveDraftMethod, StringComparison.Ordinal) ||
            string.Equals(method, FinalizeDraftMethod, StringComparison.Ordinal))
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

        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return count == expectedProperties.Count;
    }

    private static string WriteBoundedResponse(Action<Utf8JsonWriter> writeResponse)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writeResponse(writer);
        }

        if (buffer.WrittenCount > MaxResponseUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The bridge response exceeds the supported byte limit.");
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
}

internal sealed class Bridge : IDisposable
{
    private readonly CoreWebView2 coreWebView;
    private readonly Func<BridgeRequest, CancellationToken, Task<string>>? handleRequestAsync;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isStarted;
    private bool isDisposed;

    internal Bridge(
        CoreWebView2 coreWebView,
        Func<BridgeRequest, CancellationToken, Task<string>>? handleRequestAsync = null)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        this.coreWebView = coreWebView;
        this.handleRequestAsync = handleRequestAsync;
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
        if (string.Equals(request.Method, BridgeProtocol.PingMethod, StringComparison.Ordinal))
        {
            response = BridgeProtocol.CreatePingResponse(request);
        }
        else if (handleRequestAsync is null)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ProjectOperationFailed);
        }
        else
        {
            try
            {
                response = await handleRequestAsync(
                    request,
                    lifetimeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                response = BridgeProtocol.CreateErrorResponse(
                    request,
                    StableErrorCodes.ProjectOperationFailed);
            }
        }

        if (!isDisposed)
        {
            coreWebView.PostWebMessageAsJson(response);
        }
    }
}