using System.Text;
using System.Text.Json;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BridgeProtocolTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";

    [TestMethod]
    public void MessageOriginAllowsOnlyBundledHttpsAuthority()
    {
        string[] allowed =
        [
            WebViewHost.EntryPoint,
            "https://appassets.example/another-path",
            "https://appassets.example:443/index.html",
        ];
        foreach (string source in allowed)
        {
            Assert.IsTrue(BridgeProtocol.IsAllowedMessageOrigin(source), source);
        }

        string[] rejected =
        [
            "http://appassets.example/index.html",
            "https://appassets.example.evil.test/index.html",
            "https://example.com/index.html",
            "https://user@appassets.example/index.html",
            "https://appassets.example:444/index.html",
            "file:///C:/private/index.html",
            "data:text/html,untrusted",
            "",
        ];
        foreach (string source in rejected)
        {
            Assert.IsFalse(BridgeProtocol.IsAllowedMessageOrigin(source), source);
        }
    }

    [TestMethod]
    public void ValidPingIsCanonicalizedValidatedAndCorrelated()
    {
        string message = $$"""
            { "params": {}, "method": "bridge.ping", "requestId": "{{RequestId}}", "version": 1.0 }
            """;

        bool accepted = BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection);

        Assert.IsTrue(accepted);
        Assert.IsNotNull(request);
        Assert.AreEqual(Guid.Parse(RequestId), request.RequestId);
        Assert.AreEqual(BridgeRequestRejection.None, rejection);

        using JsonDocument response = JsonDocument.Parse(
            BridgeProtocol.CreatePingResponse(request));
        JsonElement root = response.RootElement;
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(RequestId, root.GetProperty("requestId").GetString());
        Assert.AreEqual("bridge.ping", root.GetProperty("method").GetString());
        Assert.AreEqual(1, root.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
    }

    [TestMethod]
    public void SizeLimitIsEnforcedBeforeParsingAtTheDocumentedBoundary()
    {
        string valid = CreateRequest();
        int validBytes = Encoding.UTF8.GetByteCount(valid);
        Assert.IsLessThanOrEqualTo(BridgeProtocol.MaxRequestUtf8Bytes, validBytes);

        string exactLimit =
            new string(' ', BridgeProtocol.MaxRequestUtf8Bytes - validBytes) + valid;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out _,
            out BridgeRequestRejection acceptedRejection));
        Assert.AreEqual(BridgeRequestRejection.None, acceptedRejection);

        string oversizedMalformed = exactLimit + "{";
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            oversizedMalformed,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    public void UntrustedOriginIsRejectedBeforeAnOversizedMessageIsExamined()
    {
        string oversized = new('x', BridgeProtocol.MaxRequestUtf8Bytes + 1);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            "https://evil.example/index.html",
            oversized,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.OriginNotAllowed, rejection);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{\"version\":1,\"version\":1,\"requestId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"method\":\"bridge.ping\",\"params\":{}}")]
    public void MalformedOrDuplicatePropertyJsonIsRejected(string message)
    {
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MalformedJson, rejection);
    }

    [TestMethod]
    public void InvalidUtf16IsRejectedAsMalformedJson()
    {
        string message = CreateRequest().Replace("bridge.ping", "bridge.\uD800", StringComparison.Ordinal);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MalformedJson, rejection);
    }

    [TestMethod]
    [DataRow("{\"version\":2,\"requestId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"method\":\"bridge.ping\",\"params\":{}}")]
    [DataRow("{\"version\":1,\"requestId\":\"00112233-4455-6677-8899-AABBCCDDEEFF\",\"method\":\"bridge.ping\",\"params\":{}}")]
    [DataRow("{\"version\":1,\"requestId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"method\":\"bridge.ping\",\"params\":{\"extra\":true}}")]
    [DataRow("{\"version\":1,\"requestId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"method\":\"bridge.ping\",\"params\":{},\"extra\":true}")]
    [DataRow("[]")]
    public void SchemaViolationsAreRejected(string message)
    {
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    public void UnknownMethodIsRejectedByTheAllowlist()
    {
        string message = CreateRequest().Replace("bridge.ping", "project.open", StringComparison.Ordinal);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MethodNotAllowed, rejection);
    }

    [TestMethod]
    public void NullMessageIsRejectedWithoutARequest()
    {
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            null,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.MalformedJson, rejection);
    }

    [TestMethod]
    public void BridgeUsesWebMessagesWithoutScriptHostObjects()
    {
        string source = File.ReadAllText(
            ResolveRepositoryPath("src/HubRadio.App/Web/Bridge.cs"));

        StringAssert.Contains(source, "WebMessageReceived");
        StringAssert.Contains(source, "PostWebMessageAsJson");
        Assert.IsFalse(source.Contains("AddHostObjectToScript", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("hostObjects", StringComparison.Ordinal));
    }

    private static string CreateRequest()
    {
        return $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"bridge.ping\",\"params\":{{}}}}";
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}