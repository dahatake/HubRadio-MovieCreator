using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BridgeProtocolSecurityTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string ProjectId = "11112222-3333-4444-8555-666677778888";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly string[] SettingsMutationMethods =
    [
        "settings.update", "settings.reset", "settings.resetAll",
        "settings.global.update", "settings.global.reset", "settings.global.resetAll",
        "settings.file.export", "settings.file.preview", "settings.file.apply",
    ];

    [TestMethod]
    [DataRow("<script>x</script>")]
    [DataRow("bridge.ping;calc")]
    [DataRow("bridge.p\u0456ng")]
    [DataRow("--filter=movie=attack")]
    [DataRow("review.approve")]
    [DataRow("review.getSnapshot;calc")]
    [DataRow("review.getSnapsh\u043et")]
    [DataRow("delivery.download")]
    [DataRow("delivery.read;calc")]
    [DataRow("delivery.re\u0430d")]
    [DataRow("layout.render")]
    [DataRow("layout.apply;calc")]
    [DataRow("layout.re\u0430d")]
    [DataRow("settings.write")]
    [DataRow("settings.reset;calc")]
    [DataRow("settings.re\u0455etAll")]
    [DataRow("settings.global.write")]
    [DataRow("settings.global.resetall")]
    [DataRow("settings.global.read;calc")]
    [DataRow("settings.global.re\u0430d")]
    [DataRow("Settings.global.read")]
    [DataRow("settings.file.read")]
    [DataRow("settings.file.apply;calc")]
    [DataRow("settings.file.\u0430pply")]
    [DataRow("settings.file.apply.extra")]
    [DataRow("pipeline.start")]
    [DataRow("pipeline.fetch")]
    [DataRow("pipeline.read;calc")]
    [DataRow("pipeline.re\u0430d")]
    [DataRow("Pipeline.read")]
    [DataRow("pipeline.retry.extra")]
    public void AdversarialUnknownMethodsAreRejectedWithoutReflection(string method)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"{method}\",\"params\":{{}}}}";
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(message));

        bool accepted = BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection);

        Assert.IsFalse(accepted);
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.MethodNotAllowed, rejection);
        Assert.IsFalse(rejection.ToString().Contains(method, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"actorSid\":\"S-1-5-21-1\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"actorRole\":\"contentApprover\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"windowsSid\":\"S-1-5-18\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"__proto__\":{\"actorRole\":\"administrator\"}}")]
    public void ReviewSnapshotRequestRejectsWebIdentityAndOpenParameters(
        string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"review.getSnapshot\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"outputTime\":null,\"actorSid\":\"S-1-5-21-1\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"outputTime\":null,\"storageReference\":\"C:\\\\private\\\\delivery.mp4\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"outputTime\":null,\"downloadUrl\":\"https://evil.example/file\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"outputTime\":null,\"__proto__\":{\"actorRole\":\"administrator\"}}")]
    public void DeliveryReadRejectsIdentityStorageDownloadAndOpenParameters(
        string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"delivery.read\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"evaluationTime\":null,\"actorSid\":\"S-1-5-21-1\"}")]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"evaluationTime\":null,\"sourcePath\":\"C:\\\\private\\\\source.mov\"}")]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"evaluationTime\":null,\"provider\":\"attacker\"}")]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"evaluationTime\":null,\"__proto__\":{\"actorRole\":\"administrator\"}}")]
    public void LayoutReadRejectsIdentityPathProviderAndOpenParameters(
        string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"layout.read\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    public void LayoutApplyRejectsIdentityInsideClosedOperation()
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"layout.apply\",\"params\":{{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"expectedRevision\":0,\"operation\":{{\"kind\":\"setManualLayout\",\"mode\":\"singleSubjectCrop\",\"staticCropDecision\":\"singleStaticCropSufficient\",\"sources\":[],\"templateId\":null,\"actorSid\":\"S-1-5-21-1\"}}}}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"actorSid\":\"S-1-5-18\"}")]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"sourcePath\":\"C:\\\\private\\\\settings.json\"}")]
    [DataRow("{\"projectId\":\"11112222-3333-4444-8555-666677778888\",\"__proto__\":{\"expectedRevision\":0}}")]
    public void SettingsReadRejectsIdentityPathAndOpenParameters(string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"settings.read\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    [TestMethod]
    [DataRow("settings.global.read", "SettingsGlobalRead")]
    [DataRow("settings.global.update", "SettingsGlobalUpdate")]
    [DataRow("settings.global.reset", "SettingsGlobalReset")]
    [DataRow("settings.global.resetAll", "SettingsGlobalResetAll")]
    [DataRow("settings.file.export", "SettingsFileExport")]
    [DataRow("settings.file.preview", "SettingsFilePreview")]
    [DataRow("settings.file.apply", "SettingsFileApply")]
    public void SettingsGlobalAndFileMethodsRoundTripThroughFiniteProtocol(string method, string enumName)
    {
        BridgeRequest request = AssertSettingsAccepted(method, CreateSettingsParameters(method));

        Assert.AreEqual(enumName, request.Method.ToString());
        Assert.AreEqual(BridgeProtocol.ParseMethodName(method), request.Method);
        Assert.AreEqual(method, BridgeProtocol.GetMethodName(request.Method));
        Assert.AreEqual(Guid.Parse(RequestId), request.RequestId);
        Assert.IsTrue(BridgeProtocol.IsSettingsMethod(request.Method));
    }

    [TestMethod]
    [DataRow("settings.read")]
    [DataRow("settings.update")]
    [DataRow("settings.reset")]
    [DataRow("settings.resetAll")]
    [DataRow("settings.global.read")]
    [DataRow("settings.global.update")]
    [DataRow("settings.global.reset")]
    [DataRow("settings.global.resetAll")]
    [DataRow("settings.file.export")]
    [DataRow("settings.file.preview")]
    [DataRow("settings.file.apply")]
    public void SettingsMethodsEnforceTheirExactProjectScope(string method)
    {
        JsonObject parameters = CreateSettingsParameters(method);
        _ = AssertSettingsAccepted(method, parameters);
        if (method.StartsWith("settings.global.", StringComparison.Ordinal))
        {
            parameters["projectId"] = null;
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
            parameters["projectId"] = ProjectId;
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
            return;
        }

        Assert.IsTrue(parameters.Remove("projectId"));
        AssertSettingsParametersRejected(method, parameters.ToJsonString());
        parameters["projectId"] = null;
        if (method.StartsWith("settings.file.", StringComparison.Ordinal))
        {
            _ = AssertSettingsAccepted(method, parameters);
        }
        else
        {
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
        }

        parameters["projectId"] = ProjectId;
        _ = AssertSettingsAccepted(method, parameters);
        foreach (string invalidScope in new[]
        {
            "\"\"", "\"00000000-0000-0000-0000-000000000000\"",
            JsonSerializer.Serialize(RequestId.ToUpperInvariant()),
            JsonSerializer.Serialize(RequestId.Replace("-", string.Empty, StringComparison.Ordinal)),
            JsonSerializer.Serialize(ProjectId + "\n"), "0", "false", "{}", "[]",
        })
        {
            parameters["projectId"] = JsonNode.Parse(invalidScope);
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
        }
    }

    [TestMethod]
    public void SettingsMutationsRequireAllFiveExpectedIdentityFields()
    {
        foreach (string method in SettingsMutationMethods)
        {
            foreach (string field in new[]
            {
                "expectedRevision", "expectedProfileId", "expectedProfileVersion",
                "expectedProfileDigest", "expectedSnapshotDigest",
            })
            {
                JsonObject parameters = CreateSettingsParameters(method);
                Assert.IsTrue(parameters.Remove(field));
                AssertSettingsParametersRejected(method, parameters.ToJsonString());
            }
        }
    }

    [TestMethod]
    [DataRow("expectedRevision", "null")]
    [DataRow("expectedRevision", "\"0\"")]
    [DataRow("expectedRevision", "false")]
    [DataRow("expectedRevision", "-1")]
    [DataRow("expectedRevision", "2147483648")]
    [DataRow("expectedRevision", "1.5")]
    [DataRow("expectedRevision", "1.00000000000000000000000000001")]
    [DataRow("expectedRevision", "2147483647.00000000000000000000001")]
    [DataRow("expectedRevision", "1e-100")]
    [DataRow("expectedRevision", "1e100")]
    [DataRow("expectedProfileId", "null")]
    [DataRow("expectedProfileId", "0")]
    [DataRow("expectedProfileId", "\"00000000-0000-0000-0000-000000000000\"")]
    [DataRow("expectedProfileId", "\"00112233-4455-6677-8899-AABBCCDDEEFF\"")]
    [DataRow("expectedProfileVersion", "null")]
    [DataRow("expectedProfileVersion", "\"01.0.0\"")]
    [DataRow("expectedProfileVersion", "\"1.0\"")]
    [DataRow("expectedProfileVersion", "\"1.0.0-beta\"")]
    [DataRow("expectedProfileVersion", "\"2147483648.0.0\"")]
    [DataRow("expectedProfileVersion", "\"1.0.0\\n\"")]
    [DataRow("expectedProfileDigest", "null")]
    [DataRow("expectedProfileDigest", "\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"")]
    [DataRow("expectedSnapshotDigest", "0")]
    [DataRow("expectedSnapshotDigest", "\"short\"")]
    [DataRow("expectedSnapshotDigest", "\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"")]
    public void SettingsMutationsRejectNoncanonicalIdentityAndOverflow(string field, string json)
    {
        foreach (string method in SettingsMutationMethods)
        {
            JsonObject parameters = CreateSettingsParameters(method);
            parameters[field] = JsonNode.Parse(json);
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
        }
    }

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("10", 10)]
    [DataRow("1e1", 10)]
    [DataRow("10.0", 10)]
    [DataRow("1.00e2", 100)]
    [DataRow("2147483640", 2147483640)]
    [DataRow("2147483647", 2147483647)]
    public void SettingsMutationRevisionsRoundTripWithoutRounding(string json, int expected)
    {
        foreach (string method in SettingsMutationMethods)
        {
            JsonObject parameters = CreateSettingsParameters(method);
            parameters["expectedRevision"] = JsonNode.Parse(json);
            BridgeRequest request = AssertSettingsAccepted(method, parameters);
            Assert.AreEqual(expected, SettingsBridgeProtocol.ReadExpectedRevision(request), method);
        }
    }

    [TestMethod]
    public void SettingsFileHashRequirementsAreMethodSpecific()
    {
        JsonObject export = CreateSettingsParameters("settings.file.export");
        _ = AssertSettingsAccepted("settings.file.export", export);
        Assert.IsTrue(export.Remove("expectedFileSha256"));
        AssertSettingsParametersRejected("settings.file.export", export.ToJsonString());
        export["expectedFileSha256"] = Digest;
        _ = AssertSettingsAccepted("settings.file.export", export);

        JsonObject apply = CreateSettingsParameters("settings.file.apply");
        _ = AssertSettingsAccepted("settings.file.apply", apply);
        apply["expectedFileSha256"] = null;
        AssertSettingsParametersRejected("settings.file.apply", apply.ToJsonString());
        Assert.IsTrue(apply.Remove("expectedFileSha256"));
        AssertSettingsParametersRejected("settings.file.apply", apply.ToJsonString());

        JsonObject preview = CreateSettingsParameters("settings.file.preview");
        _ = AssertSettingsAccepted("settings.file.preview", preview);
        preview["expectedFileSha256"] = null;
        AssertSettingsParametersRejected("settings.file.preview", preview.ToJsonString());
        preview["expectedFileSha256"] = Digest;
        AssertSettingsParametersRejected("settings.file.preview", preview.ToJsonString());
    }

    [TestMethod]
    public void SettingsFileMethodsRejectNoncanonicalFileHashes()
    {
        foreach (string method in new[] { "settings.file.export", "settings.file.apply" })
        {
            foreach (string json in new[]
            {
                "\"\"", JsonSerializer.Serialize(Digest.ToUpperInvariant()),
                JsonSerializer.Serialize(Digest[..63]), JsonSerializer.Serialize(Digest + "0"),
                JsonSerializer.Serialize(Digest + "\n"), JsonSerializer.Serialize("sha256:" + Digest),
                JsonSerializer.Serialize(new string('g', 64)), "0", "false", "{}", "[]",
            })
            {
                JsonObject parameters = CreateSettingsParameters(method);
                parameters["expectedFileSha256"] = JsonNode.Parse(json);
                AssertSettingsParametersRejected(method, parameters.ToJsonString());
            }
        }
    }

    [TestMethod]
    [DataRow("settings.global.read")]
    [DataRow("settings.global.update")]
    [DataRow("settings.global.reset")]
    [DataRow("settings.global.resetAll")]
    [DataRow("settings.file.export")]
    [DataRow("settings.file.preview")]
    [DataRow("settings.file.apply")]
    public void SettingsGlobalAndFileRejectMissingUnknownAndDuplicateProperties(string method)
    {
        JsonObject original = CreateSettingsParameters(method);
        string parametersJson = original.ToJsonString();
        AssertSettingsParametersRejected(method, "null");
        AssertSettingsParametersRejected(method, "[]");
        foreach (string key in original.Select(static item => item.Key))
        {
            JsonObject missing = CreateSettingsParameters(method);
            Assert.IsTrue(missing.Remove(key));
            AssertSettingsParametersRejected(method, missing.ToJsonString());
            string duplicate = parametersJson[..^1] + "," + JsonSerializer.Serialize(key) + ":" +
                (original[key]?.ToJsonString() ?? "null") + "}";
            AssertSettingsParametersRejected(method, duplicate);
        }

        foreach (string key in new[] { "actorSid", "filePath", "contentBody", "exception", "sql", "command", "__proto__" })
        {
            JsonObject extra = CreateSettingsParameters(method);
            extra[key] = "not allowed";
            AssertSettingsParametersRejected(method, extra.ToJsonString());
        }

        if (original.ContainsKey("expectedRevision"))
        {
            AssertSettingsParametersRejected(method, parametersJson[..^1] + ",\"expected\\u0052evision\":0}");
        }
        else
        {
            AssertSettingsParametersRejected(method, "{\"projectId\":null,\"projectId\":null}");
        }

        string message = CreateSettingsMessage(method, parametersJson);
        using JsonDocument envelope = JsonDocument.Parse(message);
        foreach (JsonProperty property in envelope.RootElement.EnumerateObject())
        {
            AssertSettingsMessageRejected(message[..^1] + "," + JsonSerializer.Serialize(property.Name) + ":" +
                property.Value.GetRawText() + "}");
        }

        AssertSettingsMessageRejected(message[..^1] + ",\"extra\":true}");
    }

    [TestMethod]
    [DataRow("{\"kind\":\"boolean\",\"booleanValue\":true}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"0\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"-9223372036854775808\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"9223372036854775807\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"-1\",\"denominator\":\"3\"}")]
    [DataRow("{\"kind\":\"text\",\"textValue\":\"日本語\"}")]
    [DataRow("{\"kind\":\"textList\",\"textListValue\":[]}")]
    [DataRow("{\"kind\":\"textList\",\"textListValue\":[\"first\",\"second\"]}")]
    public void SettingsGlobalUpdateAcceptsExistingValueKinds(string valueJson)
    {
        JsonObject parameters = CreateSettingsParameters("settings.global.update");
        parameters["value"] = JsonNode.Parse(valueJson);
        BridgeRequest request = AssertSettingsAccepted("settings.global.update", parameters);

        Assert.AreEqual("subtitles.enabled", SettingsBridgeProtocol.ReadKey(request));
        _ = SettingsBridgeProtocol.ReadValue(request);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"kind\":\"unknown\",\"booleanValue\":true}")]
    [DataRow("{\"kind\":\"boolean\",\"booleanValue\":\"true\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":0}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"+1\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"01\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"-0\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"1e0\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"1\\n\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"9223372036854775808\"}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"-9223372036854775809\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"1\",\"denominator\":\"0\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"1\",\"denominator\":\"-1\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"1\",\"denominator\":\"01\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"1\",\"denominator\":\"9223372036854775808\"}")]
    [DataRow("{\"kind\":\"rational\",\"numerator\":\"9223372036854775808\",\"denominator\":\"1\"}")]
    [DataRow("{\"kind\":\"text\",\"textValue\":\"\"}")]
    [DataRow("{\"kind\":\"textList\",\"textListValue\":[\"same\",\"same\"]}")]
    [DataRow("{\"kind\":\"textList\",\"textListValue\":[0]}")]
    [DataRow("{\"kind\":\"boolean\",\"booleanValue\":true,\"actorSid\":\"S-1-5-18\"}")]
    [DataRow("{\"kind\":\"boolean\",\"kind\":\"boolean\",\"booleanValue\":true}")]
    [DataRow("{\"kind\":\"integer\",\"integerValue\":\"1\",\"integerValue\":\"2\"}")]
    public void SettingsGlobalUpdateRejectsMalformedValues(string valueJson)
    {
        string parameters = CreateSettingsParameters("settings.global.reset").ToJsonString();
        AssertSettingsParametersRejected("settings.global.update", parameters[..^1] + ",\"value\":" + valueJson + "}");
    }

    [TestMethod]
    public void SettingsGlobalMutationsRejectInvalidKeysAndValueBounds()
    {
        foreach (string method in new[] { "settings.global.update", "settings.global.reset" })
        {
            JsonObject parameters = CreateSettingsParameters(method);
            parameters["key"] = new string('a', 128);
            _ = AssertSettingsAccepted(method, parameters);
            foreach (string key in new[] { "", "Uppercase", "a..b", "a.", "a_b", "a\n", "設定", new string('a', 129) })
            {
                parameters["key"] = key;
                AssertSettingsParametersRejected(method, parameters.ToJsonString());
            }

            parameters["key"] = null;
            AssertSettingsParametersRejected(method, parameters.ToJsonString());
        }

        JsonObject update = CreateSettingsParameters("settings.global.update");
        update["value"] = JsonSerializer.SerializeToNode(new { kind = "text", textValue = new string('a', 4096) });
        _ = AssertSettingsAccepted("settings.global.update", update);
        update["value"] = JsonSerializer.SerializeToNode(new { kind = "text", textValue = new string('a', 4097) });
        AssertSettingsParametersRejected("settings.global.update", update.ToJsonString());
        update["value"] = JsonSerializer.SerializeToNode(new
        {
            kind = "textList",
            textListValue = Enumerable.Range(0, 64).Select(static index => $"item{index}").ToArray(),
        });
        _ = AssertSettingsAccepted("settings.global.update", update);
        update["value"] = JsonSerializer.SerializeToNode(new
        {
            kind = "textList",
            textListValue = Enumerable.Range(0, 65).Select(static index => $"item{index}").ToArray(),
        });
        AssertSettingsParametersRejected("settings.global.update", update.ToJsonString());
    }

    [TestMethod]
    [DataRow("settings.global.read")]
    [DataRow("settings.global.update")]
    [DataRow("settings.global.reset")]
    [DataRow("settings.global.resetAll")]
    [DataRow("settings.file.export")]
    [DataRow("settings.file.preview")]
    [DataRow("settings.file.apply")]
    public void SettingsGlobalAndFileRequestsRetain64KiBUtf8Limit(string method)
    {
        string message = CreateSettingsMessage(method, CreateSettingsParameters(method).ToJsonString());
        string atLimit = message + new string(' ', (64 * 1024) - Encoding.UTF8.GetByteCount(message));
        Assert.AreEqual(64 * 1024, Encoding.UTF8.GetByteCount(atLimit));
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, atLimit, out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
        Assert.IsNotNull(request);

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, atLimit + " ", out request, out rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    [DataRow("settings.global.read")]
    [DataRow("settings.global.update")]
    [DataRow("settings.global.reset")]
    [DataRow("settings.global.resetAll")]
    [DataRow("settings.file.export")]
    [DataRow("settings.file.preview")]
    [DataRow("settings.file.apply")]
    public void SettingsGlobalAndFileResponsesRetain4MiBUtf8Limit(string method)
    {
        BridgeRequest request = AssertSettingsAccepted(method, CreateSettingsParameters(method));
        Assert.AreEqual(4 * 1024 * 1024, BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        // Result shapes are covered separately; this isolates the transport's exact byte ceiling.
        string empty = BridgeProtocol.CreateResultResponse(request, static writer => writer.WriteStringValue(string.Empty));
        int payloadBytes = (4 * 1024 * 1024) - Encoding.UTF8.GetByteCount(empty);
        string atLimit = BridgeProtocol.CreateResultResponse(request, writer => writer.WriteStringValue(new string('a', payloadBytes)));
        Assert.AreEqual(4 * 1024 * 1024, Encoding.UTF8.GetByteCount(atLimit));
        Assert.ThrowsExactly<InvalidOperationException>(() => BridgeProtocol.CreateResultResponse(
            request, writer => writer.WriteStringValue(new string('a', payloadBytes + 1))));
    }

    [TestMethod]
    [DataRow("pipeline.read", "PipelineRead")]
    [DataRow("pipeline.regenerate", "PipelineRegenerate")]
    [DataRow("pipeline.retry", "PipelineRetry")]
    [DataRow("pipeline.cancel", "PipelineCancel")]
    public void PipelineMethodsRetainRequestIdentityProjectScopeAndClosedParameters(string method, string enumName)
    {
        JsonObject parameters = PipelineParameters(method);
        string message = CreateSettingsMessage(method, parameters.ToJsonString());
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, message, out BridgeRequest? request, out _));
        Assert.IsNotNull(request);
        Assert.AreEqual(enumName, request.Method.ToString());
        Assert.AreEqual(method, BridgeProtocol.GetMethodName(request.Method));
        Assert.IsTrue(BridgeProtocol.IsSourceOnlyMethod(request.Method));
        Assert.AreEqual(Guid.Parse(RequestId), request.RequestId);
        Assert.AreEqual(ProjectId, request.Parameters.GetProperty("projectId").GetString());
        Assert.IsNull(request.JobId, "Pipeline requests must never fall through to jobs dispatch.");
        int? actualRevision = request.ExpectedRevision;
        Assert.AreEqual(method is "pipeline.retry" or "pipeline.cancel" ? (int?)0 : null, actualRevision);

        foreach (string key in parameters.Select(p => p.Key))
        {
            JsonObject missing = PipelineParameters(method);
            Assert.IsTrue(missing.Remove(key));
            AssertSettingsMessageRejected(CreateSettingsMessage(method, missing.ToJsonString()));
            string json = parameters.ToJsonString();
            AssertSettingsMessageRejected(CreateSettingsMessage(method, json[..^1] + "," + JsonSerializer.Serialize(key) + ":" + parameters[key]!.ToJsonString() + "}"));
        }
        foreach (string key in new[] { "actorSid", "actorRole", "approved", "permission", "filePath", "result", "settings", "__proto__" })
        {
            JsonObject open = PipelineParameters(method);
            open[key] = true;
            AssertSettingsMessageRejected(CreateSettingsMessage(method, open.ToJsonString()));
        }
        foreach (string origin in new[] { "https://evil.test/index.html", "https://player-media.appassets.example/source-only/x", "http://appassets.example/index.html" })
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(origin, message, out _, out BridgeRequestRejection rejection));
            Assert.AreEqual(BridgeRequestRejection.OriginNotAllowed, rejection);
        }
    }

    [TestMethod]
    public void PipelineIdentityFieldsRejectNullNoncanonicalIdsAndDigests()
    {
        foreach (string method in new[] { "pipeline.read", "pipeline.regenerate", "pipeline.retry", "pipeline.cancel" })
        {
            JsonObject original = PipelineParameters(method);
            foreach (string key in original.Select(p => p.Key).Where(key => key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("Digest", StringComparison.Ordinal)))
            {
                string valid = original[key]!.GetValue<string>();
                foreach (JsonNode? invalid in new JsonNode?[]
                {
                    null, JsonValue.Create(1), JsonValue.Create(""), JsonValue.Create(valid + "\n"),
                    JsonValue.Create(key.EndsWith("Id", StringComparison.Ordinal) ? RequestId.ToUpperInvariant() : Digest.ToUpperInvariant()),
                    JsonValue.Create(key.EndsWith("Id", StringComparison.Ordinal) ? Guid.Empty.ToString("D") : "sha256:" + valid),
                })
                {
                    JsonObject parameters = PipelineParameters(method);
                    parameters[key] = invalid?.DeepClone();
                    AssertSettingsMessageRejected(CreateSettingsMessage(method, parameters.ToJsonString()));
                }
            }
        }
    }

    [TestMethod]
    [DataRow("0", true)]
    [DataRow("1e1", true)]
    [DataRow("10.0", true)]
    [DataRow("2147483647", true)]
    [DataRow("-1", false)]
    [DataRow("2147483648", false)]
    [DataRow("1.5", false)]
    [DataRow("1.00000000000000000000000000001", false)]
    [DataRow("1e-100", false)]
    [DataRow("1e100", false)]
    [DataRow("\"0\"", false)]
    [DataRow("null", false)]
    [DataRow("true", false)]
    public void PipelineRevisionsAreExactNonnegativeInt32WithoutFractionalRounding(string number, bool valid)
    {
        foreach (string method in new[] { "pipeline.regenerate", "pipeline.retry", "pipeline.cancel" })
        {
            string key = method == "pipeline.regenerate" ? "expectedSettingsRevision" : "expectedRevision";
            JsonObject parameters = PipelineParameters(method);
            parameters[key] = JsonNode.Parse(number);
            bool accepted = BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, CreateSettingsMessage(method, parameters.ToJsonString()),
                out BridgeRequest? request, out _);
            Assert.AreEqual(valid, accepted, method + ": " + number);
            if (accepted)
            {
                int expected = number is "1e1" or "10.0" ? 10 : int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(expected, SourceOnlyBridgeProtocol.ReadRevision(request!.Parameters, key));
            }
        }
    }

    [TestMethod]
    [DataRow("pipeline.read")]
    [DataRow("pipeline.regenerate")]
    [DataRow("pipeline.retry")]
    [DataRow("pipeline.cancel")]
    public void PipelineHasExact1KiBRequestAnd8MiBResponseCeilings(string method)
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement limits = schema.RootElement.GetProperty("$defs").GetProperty("sourceOnlyProtocolLimits");
        Assert.AreEqual(BridgeProtocol.MaxSourceOnlyRequestUtf8Bytes, limits.GetProperty("x-maxRequestUtf8Bytes").GetInt32());
        Assert.AreEqual(BridgeProtocol.MaxSourceOnlyResponseUtf8Bytes, limits.GetProperty("x-maxResponseUtf8Bytes").GetInt32());
        string message = CreateSettingsMessage(method, PipelineParameters(method).ToJsonString());
        string atLimit = message + new string(' ', 1024 - Encoding.UTF8.GetByteCount(message));
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, atLimit, out BridgeRequest? request, out _));
        Assert.IsNotNull(request);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, atLimit + " ", out _, out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
        Assert.AreEqual(8388608, BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        // Deliberately isolates transport size; exact domain shapes are tested separately.
        int envelope = Encoding.UTF8.GetByteCount(BridgeProtocol.CreateResultResponse(request, writer => writer.WriteStringValue("")));
        int payload = 8388608 - envelope;
        string response = BridgeProtocol.CreateResultResponse(request, writer => writer.WriteStringValue(new string('a', payload)));
        Assert.AreEqual(8388608, Encoding.UTF8.GetByteCount(response));
        Assert.ThrowsExactly<InvalidOperationException>(() => BridgeProtocol.CreateResultResponse(request, writer => writer.WriteStringValue(new string('a', payload + 1))));
    }

    private static string RepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static JsonObject PipelineParameters(string method)
    {
        var parameters = new JsonObject { ["projectId"] = ProjectId };
        if (method == "pipeline.regenerate")
        {
            parameters["expectedVersionId"] = RequestId;
            parameters["expectedVersionDigest"] = Digest;
            parameters["expectedSettingsRevision"] = 0;
            parameters["expectedSettingsSnapshotDigest"] = Digest;
        }
        else if (method is "pipeline.retry" or "pipeline.cancel")
        {
            parameters["executionId"] = RequestId;
            parameters["expectedRevision"] = 0;
        }
        return parameters;
    }

    private static JsonObject CreateSettingsParameters(string method)
    {
        var parameters = new JsonObject();
        switch (method)
        {
            case "settings.read":
            case "settings.update":
            case "settings.reset":
            case "settings.resetAll":
                parameters["projectId"] = ProjectId;
                break;
            case "settings.global.read":
            case "settings.global.update":
            case "settings.global.reset":
            case "settings.global.resetAll":
                break;
            case "settings.file.export":
            case "settings.file.preview":
            case "settings.file.apply":
                parameters["projectId"] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown settings test method.");
        }

        if (method is "settings.read" or "settings.global.read")
        {
            return parameters;
        }

        parameters["expectedRevision"] = 0;
        parameters["expectedProfileId"] = RequestId;
        parameters["expectedProfileVersion"] = "1.0.0";
        parameters["expectedProfileDigest"] = Digest;
        parameters["expectedSnapshotDigest"] = Digest;
        if (method is "settings.update" or "settings.reset" or "settings.global.update" or "settings.global.reset")
        {
            parameters["key"] = "subtitles.enabled";
        }

        if (method is "settings.update" or "settings.global.update")
        {
            parameters["value"] = new JsonObject { ["kind"] = "boolean", ["booleanValue"] = true };
        }

        if (method == "settings.file.export")
        {
            parameters["expectedFileSha256"] = null;
        }
        else if (method == "settings.file.apply")
        {
            parameters["expectedFileSha256"] = Digest;
        }

        return parameters;
    }

    private static string CreateSettingsMessage(string method, string parameters) =>
        $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"{method}\",\"params\":{parameters}}}";

    private static BridgeRequest AssertSettingsAccepted(string method, JsonObject parameters)
    {
        string json = parameters.ToJsonString();
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(SettingsBridgeProtocol.HasValidParameters(BridgeProtocol.ParseMethodName(method), document.RootElement), method);
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, CreateSettingsMessage(method, json), out BridgeRequest? request, out BridgeRequestRejection rejection),
            $"{method}: {rejection}");
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeRequestRejection.None, rejection);
        return request;
    }

    private static void AssertSettingsParametersRejected(string method, string parameters)
    {
        using JsonDocument document = JsonDocument.Parse(parameters);
        Assert.IsFalse(SettingsBridgeProtocol.HasValidParameters(BridgeProtocol.ParseMethodName(method), document.RootElement), method);
        AssertSettingsMessageRejected(CreateSettingsMessage(method, parameters));
    }

    private static void AssertSettingsMessageRejected(string message)
    {
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, message, out BridgeRequest? request, out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreNotEqual(BridgeRequestRejection.None, rejection);
    }
}
