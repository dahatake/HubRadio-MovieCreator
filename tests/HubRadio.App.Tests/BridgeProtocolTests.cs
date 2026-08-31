using System.Text;
using System.Text.Json;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BridgeProtocolTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string JobId = "11223344-5566-7788-99aa-bbccddeeff00";

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
        Assert.AreEqual(BridgeMethod.Ping, request.Method);
        Assert.AreEqual(0, request.Parameters.EnumerateObject().Count());
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
    public void JobStatusAcceptsOnlyCanonicalJobIdParameter()
    {
        string message = $$"""
            {"version":1,"requestId":"{{RequestId}}","method":"jobs.getStatus","params":{"jobId":"{{JobId}}"}
            }
            """;

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));

        Assert.AreEqual(BridgeRequestRejection.None, rejection);
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeMethod.JobsGetStatus, request.Method);
        Assert.AreEqual(JobId, request.JobId!.ToString());
        Assert.IsNull(request.ExpectedRevision);
    }

    [TestMethod]
    [DataRow("jobs.cancel", (int)BridgeMethod.JobsCancel)]
    [DataRow("jobs.retry", (int)BridgeMethod.JobsRetry)]
    public void JobCommandsAcceptOnlyCanonicalIdAndNonnegativeRevision(
        string method,
        int expectedMethod)
    {
        string message = $$"""
            {"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{"jobId":"{{JobId}}","expectedRevision":2147483647}
            }
            """;

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));

        Assert.AreEqual(BridgeRequestRejection.None, rejection);
        Assert.IsNotNull(request);
        Assert.AreEqual((BridgeMethod)expectedMethod, request.Method);
        Assert.AreEqual(JobId, request.JobId!.ToString());
        Assert.AreEqual(int.MaxValue, request.ExpectedRevision);
    }

    [TestMethod]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"expectedRevision\":-1}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"expectedRevision\":1.5}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"expectedRevision\":2147483648}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"expectedRevision\":1e999999}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99AA-BBCCDDEEFF00\",\"expectedRevision\":0}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"expectedRevision\":0,\"extra\":true}")]
    [DataRow("{\"jobId\":\"11223344-5566-7788-99aa-bbccddeeff00\"}")]
    public void JobCommandSchemaViolationsFailClosed(string parameters)
    {
        string message =
            $$"""{"version":1,"requestId":"{{RequestId}}","method":"jobs.retry","params":{{parameters}}}""";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));

        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
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
    public void ProjectRequestsUseTheIndependentNinetySixKiBBoundary()
    {
        string valid = CreateRequest(BridgeProtocol.ListProjectsMethod, "{}");
        int validBytes = Encoding.UTF8.GetByteCount(valid);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxProjectRequestUtf8Bytes,
            validBytes);

        string exactLimit =
            new string(
                ' ',
                BridgeProtocol.MaxProjectRequestUtf8Bytes - validBytes) +
            valid;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out BridgeRequest? request,
            out BridgeRequestRejection acceptedRejection));
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeMethod.ListProjects, request.Method);
        Assert.AreEqual(BridgeRequestRejection.None, acceptedRejection);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    public void OnlyRecognizedProjectMethodsCanUseTheLargerRequestBudget()
    {
        string unknown = CreateRequest("project.unknown", "{}");
        string oversizedUnknown =
            new string(
                ' ',
                BridgeProtocol.MaxRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(unknown) +
                1) +
            unknown;
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            oversizedUnknown,
            out _,
            out BridgeRequestRejection unknownRejection));
        Assert.AreEqual(
            BridgeRequestRejection.MessageTooLarge,
            unknownRejection);

        string missingMethod =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"params\":{{}}}}";
        string oversizedMissingMethod =
            new string(
                ' ',
                BridgeProtocol.MaxRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(missingMethod) +
                1) +
            missingMethod;
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            oversizedMissingMethod,
            out _,
            out BridgeRequestRejection missingMethodRejection));
        Assert.AreEqual(
            BridgeRequestRejection.MessageTooLarge,
            missingMethodRejection);

        string invalidProject = CreateRequest(
            BridgeProtocol.ListProjectsMethod,
            "{\"extra\":true}");
        string projectOverCompactLimit =
            new string(
                ' ',
                BridgeProtocol.MaxRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(invalidProject) +
                1) +
            invalidProject;
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            projectOverCompactLimit,
            out _,
            out BridgeRequestRejection projectRejection));
        Assert.AreEqual(
            BridgeRequestRejection.SchemaViolation,
            projectRejection);
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
    public void EveryProjectMethodAcceptsOnlyItsExactClosedParameters()
    {
        string draftId = "11112222-3333-4444-8555-66667777aabb";
        string candidateId = "99990000-aaaa-4bbb-8ccc-ddddeeeeffff";
        string profileId = "14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa";
        string templateId = "bf08c776-0f97-5373-a091-c70b60acd569";
        string input = $$"""
            {
              "sourceCandidateId":"{{candidateId}}",
              "contentName":"name",
              "purpose":"purpose",
              "audience":"audience",
              "publicationScope":"internalOnly",
              "outputProfileIds":["{{profileId}}"],
              "language":"ja-JP",
              "performerNames":["person"],
              "deadlineSelection":{"hasDeadline":false,"value":null},
              "brandTemplateId":"{{templateId}}"
            }
            """;
        string[] valid =
        [
            CreateRequest(BridgeProtocol.ListProjectsMethod, "{}"),
            CreateRequest(BridgeProtocol.CreateDraftMethod, "{}"),
            CreateRequest(BridgeProtocol.PickSourceMethod, "{}"),
            CreateRequest(
                BridgeProtocol.LoadDraftMethod,
                $"{{\"draftId\":\"{draftId}\"}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{input}}}"),
            CreateRequest(
                BridgeProtocol.FinalizeDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":2147483647,\"input\":{input}}}"),
        ];
        string wrongScopeInput = input.Replace(
            "\"internalOnly\"",
            "\"InternalOnly\"",
            StringComparison.Ordinal);
        string duplicateProfileInput = input.Replace(
            $"[\"{profileId}\"]",
            $"[\"{profileId}\",\"{profileId}\"]",
            StringComparison.Ordinal);
        string duplicatePerformerInput = input.Replace(
            "[\"person\"]",
            "[\"person\",\"person\"]",
            StringComparison.Ordinal);
        string invalidDeadlineInput = input.Replace(
            "{\"hasDeadline\":false,\"value\":null}",
            "{\"hasDeadline\":true,\"value\":\"tomorrow\"}",
            StringComparison.Ordinal);
        string extraPropertyInput = input.Replace(
            $"\"brandTemplateId\":\"{templateId}\"",
            $"\"brandTemplateId\":\"{templateId}\",\"extra\":true",
            StringComparison.Ordinal);

        foreach (string message in valid)
        {
            Assert.IsTrue(
                BridgeProtocol.TryParseRequest(
                    WebViewHost.EntryPoint,
                    message,
                    out BridgeRequest? request,
                    out BridgeRequestRejection rejection),
                rejection.ToString());
            Assert.IsNotNull(request);
        }

        string[] invalid =
        [
            CreateRequest(BridgeProtocol.ListProjectsMethod, "{\"extra\":true}"),
            CreateRequest(BridgeProtocol.LoadDraftMethod, "{}"),
            CreateRequest(
                BridgeProtocol.LoadDraftMethod,
                $"{{\"draftId\":\"{draftId.ToUpperInvariant()}\"}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":-1,\"input\":{input}}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{input},\"extra\":true}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{extraPropertyInput}}}"),
            CreateRequest(
                BridgeProtocol.FinalizeDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{wrongScopeInput}}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{duplicateProfileInput}}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{duplicatePerformerInput}}}"),
            CreateRequest(
                BridgeProtocol.SaveDraftMethod,
                $"{{\"draftId\":\"{draftId}\",\"expectedRevision\":0,\"input\":{invalidDeadlineInput}}}"),
        ];

        foreach (string message in invalid)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out _,
                out BridgeRequestRejection rejection));
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }
    }

    [TestMethod]
    public void ErrorResponseIsCorrelatedClosedAndContainsNoExceptionOrPrivateDetail()
    {
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            CreateRequest(BridgeProtocol.ListProjectsMethod, "{}"),
            out BridgeRequest? request,
            out _));
        Assert.IsNotNull(request);

        string responseJson = BridgeProtocol.CreateErrorResponse(
            request,
            HubRadio.Infrastructure.Diagnostics.StableErrorCodes.ProjectNotAuthorized);
        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement root = response.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        JsonElement error = root.GetProperty("error");
        CollectionAssert.AreEquivalent(
            new[] { "code", "message" },
            error.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(
            "project.not_authorized",
            error.GetProperty("code").GetString());
        Assert.IsFalse(responseJson.Contains("SID", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains("Exception", StringComparison.Ordinal));
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

    private static string CreateRequest(string method, string parameters)
    {
        return $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"{method}\",\"params\":{parameters}}}";
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