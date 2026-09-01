using System.Text;
using System.Text.Json;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BridgeProtocolTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string JobId = "11223344-5566-7788-99aa-bbccddeeff00";
    private const string VersionId = "11112222-3333-4444-8555-666677778888";
    private const string VersionDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
    public void ThumbnailRequestsAreClosedCanonicalAndShareExtendedBudget()
    {
        const string generationId =
            "22334455-6677-4889-9aab-ccddeeff0011";
        const string templateId =
            "33445566-7788-499a-8bbc-ddeeff001122";
        string digest = new('a', 64);
        string playerDigest = new('b', 64);
        string read = CreateRequest(
            BridgeProtocol.ThumbnailReadMethod,
            $"{{\"jobId\":\"{JobId}\"}}");
        string generate = CreateRequest(
            BridgeProtocol.ThumbnailGenerateMethod,
            $"{{\"jobId\":\"{JobId}\",\"generationId\":\"{generationId}\",\"digest\":\"{digest}\",\"playerDigest\":\"{playerDigest}\",\"templateId\":\"{templateId}\",\"proxyFrameSequence\":2147483647}}");
        string cancel = CreateRequest(
            BridgeProtocol.ThumbnailCancelMethod,
            $"{{\"generationId\":\"{generationId}\"}}");

        Assert.IsGreaterThan(
            BridgeProtocol.MaxRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(generate));
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxThumbnailRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(generate));
        foreach (string message in new[] { read, generate, cancel })
        {
            Assert.IsTrue(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? parsed,
                out BridgeRequestRejection rejection),
                rejection.ToString());
            Assert.IsNotNull(parsed);
        }

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            generate,
            out BridgeRequest? generation,
            out BridgeRequestRejection generationRejection));
        Assert.AreEqual(BridgeRequestRejection.None, generationRejection);
        Assert.IsNotNull(generation);
        Assert.AreEqual(BridgeMethod.ThumbnailGenerate, generation.Method);
        Assert.AreEqual(JobId, generation.JobId!.ToString());
        Assert.AreEqual(Guid.Parse(generationId), generation.GenerationId);
        Assert.AreEqual(digest, generation.EvidenceDigest!.Hex);
        Assert.AreEqual(playerDigest, generation.PlayerEvidenceDigest!.Hex);
        Assert.AreEqual(templateId, generation.TemplateId!.ToString());
        Assert.AreEqual(int.MaxValue, generation.ProxyFrameSequence);

        string exactLimit = new(
            ' ',
            BridgeProtocol.MaxThumbnailRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(generate));
        exactLimit += generate;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out _,
            out BridgeRequestRejection exactRejection));
        Assert.AreEqual(BridgeRequestRejection.None, exactRejection);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection oversizedRejection));
        Assert.AreEqual(
            BridgeRequestRejection.MessageTooLarge,
            oversizedRejection);

        string[] invalid =
        [
            generate.Replace(digest, digest.ToUpperInvariant(),
                StringComparison.Ordinal),
            generate.Replace(playerDigest, playerDigest + "0",
                StringComparison.Ordinal),
            generate.Replace(templateId, templateId.ToUpperInvariant(),
                StringComparison.Ordinal),
            generate.Replace("2147483647", "-1", StringComparison.Ordinal),
            generate.Replace("2147483647", "0.5", StringComparison.Ordinal),
            generate.Replace(
                $"\"generationId\":\"{generationId}\"",
                "\"generationId\":42",
                StringComparison.Ordinal),
            generate.Replace(
                $"\"digest\":\"{digest}\"",
                "\"digest\":false",
                StringComparison.Ordinal),
            generate.Replace(
                $"\"templateId\":\"{templateId}\"",
                "\"templateId\":{}",
                StringComparison.Ordinal),
            cancel.Replace(
                $"\"generationId\":\"{generationId}\"",
                "\"generationId\":[]",
                StringComparison.Ordinal),
            generate.Replace(
                "\"proxyFrameSequence\":2147483647",
                "\"proxyFrameSequence\":2147483647,\"actorSid\":\"S-1-5-21-private\"",
                StringComparison.Ordinal),
        ];
        foreach (string message in invalid)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? rejected,
                out BridgeRequestRejection rejection),
                message);
            Assert.IsNull(rejected);
            Assert.AreEqual(
                BridgeRequestRejection.SchemaViolation,
                rejection);
        }
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
    public void EveryReviewMethodAcceptsOnlyExactVersionBoundParameters()
    {
        string outputTime = "{\"value\":\"1\",\"rate\":\"2\"}";
        (string Method, string Parameters, BridgeMethod Expected)[] valid =
        [
            (
                BridgeProtocol.ReviewGetSnapshotMethod,
                $"{{\"versionId\":\"{VersionId}\"}}",
                BridgeMethod.ReviewGetSnapshot),
            (
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{outputTime},\"text\":\"確認コメントです。\"}}",
                BridgeMethod.ReviewAddComment),
            (
                BridgeProtocol.ReviewRequestChangesMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"expectedState\":\"contentReview\",\"outputTime\":{outputTime},\"text\":\"修正してください。\"}}",
                BridgeMethod.ReviewRequestChanges),
            (
                BridgeProtocol.ReviewRecordApprovalMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"rightsApprover\",\"reason\":\"内容を承認します。\"}}",
                BridgeMethod.ReviewRecordApproval),
        ];

        foreach ((string method, string parameters, BridgeMethod expected) in valid)
        {
            Assert.IsTrue(
                BridgeProtocol.TryParseRequest(
                    WebViewHost.EntryPoint,
                    CreateRequest(method, parameters),
                    out BridgeRequest? request,
                    out BridgeRequestRejection rejection),
                $"{method}: {rejection}");
            Assert.IsNotNull(request);
            Assert.AreEqual(expected, request.Method);
            Assert.AreEqual(VersionId, request.Parameters.GetProperty("versionId").GetString());
        }

        string[] invalid =
        [
            CreateRequest(
                BridgeProtocol.ReviewGetSnapshotMethod,
                $"{{\"versionId\":\"{VersionId}\",\"actorSid\":\"S-1-5-21-private\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{outputTime},\"text\":\"確認\",\"actorSid\":\"S-1-5-21-private\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId[..^1]}A\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{outputTime},\"text\":\"確認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewRecordApprovalMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest.ToUpperInvariant()}\",\"actorRole\":\"contentApprover\",\"reason\":\"承認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{{\"value\":\"2\",\"rate\":\"4\"}},\"text\":\"確認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{{\"value\":\"-1\",\"rate\":\"1\"}},\"text\":\"確認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"outputTime\":{{\"value\":1,\"rate\":2}},\"text\":\"確認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewRequestChangesMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"expectedState\":\"ContentReview\",\"outputTime\":{outputTime},\"text\":\"修正\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewRecordApprovalMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"reason\":\" 承認\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"outputTime\":{outputTime},\"text\":\"役割なし\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewAddCommentMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"administrator\",\"outputTime\":{outputTime},\"text\":\"管理者迂回なし\"}}"),
            CreateRequest(
                BridgeProtocol.ReviewRecordApprovalMethod,
                $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"editor\",\"reason\":\"承認役割ではない\"}}"),
        ];

        foreach (string message in invalid)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? request,
                out BridgeRequestRejection rejection));
            Assert.IsNull(request);
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }
    }

    [TestMethod]
    public void ReviewTextUsesExactUtf8LimitAndRejectsInvalidUnicode()
    {
        string validText = new('x', BridgeProtocol.MaxReviewTextUtf8Bytes);
        string invalidText = validText + "x";
        string valid = CreateRequest(
            BridgeProtocol.ReviewRecordApprovalMethod,
            $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"reason\":{JsonSerializer.Serialize(validText)}}}");
        string oversizedText = CreateRequest(
            BridgeProtocol.ReviewRecordApprovalMethod,
            $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"reason\":{JsonSerializer.Serialize(invalidText)}}}");

        Assert.AreEqual(
            BridgeProtocol.MaxReviewTextUtf8Bytes,
            Encoding.UTF8.GetByteCount(validText));
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            valid,
            out BridgeRequest? request,
            out BridgeRequestRejection acceptedRejection));
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeRequestRejection.None, acceptedRejection);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            oversizedText,
            out _,
            out BridgeRequestRejection rejected));
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejected);

        string invalidUnicode = CreateRequest(
            BridgeProtocol.ReviewRecordApprovalMethod,
            $"{{\"versionId\":\"{VersionId}\",\"versionDigest\":\"{VersionDigest}\",\"actorRole\":\"contentApprover\",\"reason\":\"invalid-\uD800\"}}");
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            invalidUnicode,
            out _,
            out BridgeRequestRejection unicodeRejection));
        Assert.AreEqual(BridgeRequestRejection.MalformedJson, unicodeRejection);
    }

    [TestMethod]
    public void ReviewRequestsUseAnIndependentTwentyFourKiBBoundary()
    {
        string valid = CreateRequest(
            BridgeProtocol.ReviewGetSnapshotMethod,
            $"{{\"versionId\":\"{VersionId}\"}}");
        int validBytes = Encoding.UTF8.GetByteCount(valid);
        string exactLimit =
            new string(
                ' ',
                BridgeProtocol.MaxReviewRequestUtf8Bytes - validBytes) +
            valid;

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out BridgeRequest? request,
            out BridgeRequestRejection acceptedRejection));
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeMethod.ReviewGetSnapshot, request.Method);
        Assert.AreEqual(BridgeRequestRejection.None, acceptedRejection);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection rejected));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejected);
        Assert.AreEqual(
            BridgeProtocol.MaxReviewResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(
                BridgeMethod.ReviewGetSnapshot));
    }

    [TestMethod]
    public void DeliveryReadAcceptsOnlyExactVersionAndOptionalReducedOutputTime()
    {
        string withoutTrace = CreateRequest(
            BridgeProtocol.DeliveryReadMethod,
            $"{{\"versionId\":\"{VersionId}\",\"outputTime\":null}}");
        string withTrace = CreateRequest(
            BridgeProtocol.DeliveryReadMethod,
            $"{{\"versionId\":\"{VersionId}\",\"outputTime\":{{\"value\":\"1\",\"rate\":\"2\"}}}}");

        foreach (string message in new[] { withoutTrace, withTrace })
        {
            Assert.IsTrue(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? request,
                out BridgeRequestRejection rejection),
                rejection.ToString());
            Assert.IsNotNull(request);
            Assert.AreEqual(BridgeMethod.DeliveryRead, request.Method);
            Assert.AreEqual(
                VersionId,
                request.Parameters.GetProperty("versionId").GetString());
        }

        string[] invalid =
        [
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId}\"}}"),
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId[..^1]}A\",\"outputTime\":null}}"),
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId}\",\"outputTime\":{{\"value\":\"-1\",\"rate\":\"1\"}}}}"),
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId}\",\"outputTime\":{{\"value\":\"2\",\"rate\":\"4\"}}}}"),
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId}\",\"outputTime\":{{\"value\":1,\"rate\":2}}}}"),
            CreateRequest(
                BridgeProtocol.DeliveryReadMethod,
                $"{{\"versionId\":\"{VersionId}\",\"outputTime\":null,\"actorSid\":\"S-1-5-21-private\"}}"),
        ];
        foreach (string message in invalid)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? request,
                out BridgeRequestRejection rejection));
            Assert.IsNull(request);
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }

        int bytes = Encoding.UTF8.GetByteCount(withTrace);
        string exactLimit = new(
            ' ',
            BridgeProtocol.MaxDeliveryRequestUtf8Bytes - bytes);
        exactLimit += withTrace;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out _,
            out BridgeRequestRejection exactRejection));
        Assert.AreEqual(BridgeRequestRejection.None, exactRejection);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection oversizedRejection));
        Assert.AreEqual(
            BridgeRequestRejection.MessageTooLarge,
            oversizedRejection);
        Assert.AreEqual(
            BridgeProtocol.MaxDeliveryResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(
                BridgeMethod.DeliveryRead));
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