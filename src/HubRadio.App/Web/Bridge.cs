using System.Buffers;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Serialization;
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
    internal BridgeRequest(Guid requestId)
    {
        RequestId = requestId;
    }

    internal Guid RequestId { get; }
}

internal static class BridgeProtocol
{
    internal const int Version = 1;
    internal const int MaxRequestUtf8Bytes = 128;
    internal const string PingMethod = "bridge.ping";

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
            || !root.TryGetProperty("params", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Any())
        {
            rejection = BridgeRequestRejection.SchemaViolation;
            return false;
        }

        if (!string.Equals(method.GetString(), PingMethod, StringComparison.Ordinal))
        {
            rejection = BridgeRequestRejection.MethodNotAllowed;
            return false;
        }

        request = new BridgeRequest(parsedRequestId);
        rejection = BridgeRequestRejection.None;
        return true;
    }

    internal static string CreatePingResponse(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString("method", PingMethod);
        writer.WriteStartObject("result");
        writer.WriteNumber("protocolVersion", Version);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
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
}

internal sealed class Bridge : IDisposable
{
    private readonly CoreWebView2 coreWebView;
    private bool isStarted;
    private bool isDisposed;

    internal Bridge(CoreWebView2 coreWebView)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        this.coreWebView = coreWebView;
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
        if (!isStarted)
        {
            return;
        }

        coreWebView.Settings.IsWebMessageEnabled = false;
        coreWebView.WebMessageReceived -= OnWebMessageReceived;
    }

    private static void OnWebMessageReceived(
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

        coreWebView.PostWebMessageAsJson(BridgeProtocol.CreatePingResponse(request));
    }
}