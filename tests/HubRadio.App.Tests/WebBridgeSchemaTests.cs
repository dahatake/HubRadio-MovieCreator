using System.Text.Json;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebBridgeSchemaTests
{
    [TestMethod]
    public void SchemaIsClosedVersionedAndAllowsOnlyCanonicalMethods()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("WebBridgeMessage", root.GetProperty("title").GetString());
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "96 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "1 MiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "16 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "4 MiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "256 UTF-8 bytes");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "4096-byte responses");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "reserved intercepted HTTPS media origin");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "current Windows actor");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "never accepted from Web messages");
        Assert.AreEqual(1, definitions.GetProperty("protocolVersion").GetProperty("const").GetInt32());
        Assert.IsTrue(
            definitions.GetProperty("timestamp")
                .GetProperty("pattern")
                .GetString()!
                .EndsWith("(?![\\s\\S])", StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            new[]
            {
                "bridge.ping",
                "project.list",
                "project.createDraft",
                "project.loadDraft",
                "project.saveDraft",
                "project.pickSource",
                "project.finalizeDraft",
                "jobs.getStatus",
                "jobs.cancel",
                "jobs.retry",
                "player.read",
                "player.seek",
                "transcript.read",
                "transcript.editToken",
                "transcript.setSpeaker",
                "transcript.removeSpeaker",
                "transcript.adoptTerm",
                "transcript.rejectTerm",
                "timeline.read",
                "timeline.apply",
            },
            definitions.GetProperty("method").GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());

        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/pingRequest",
                "#/$defs/listRequest",
                "#/$defs/createDraftRequest",
                "#/$defs/loadDraftRequest",
                "#/$defs/saveDraftRequest",
                "#/$defs/pickSourceRequest",
                "#/$defs/finalizeDraftRequest",
                "#/$defs/jobStatusRequest",
                "#/$defs/jobCancelRequest",
                "#/$defs/jobRetryRequest",
                "#/$defs/pingResponse",
                "#/$defs/listResponse",
                "#/$defs/draftResponse",
                "#/$defs/pickSourceResponse",
                "#/$defs/jobStatusResponse",
                "#/$defs/jobCancelResponse",
                "#/$defs/jobRetryResponse",
                "#/$defs/projectErrorResponse",
                "#/$defs/errorResponse",
                "#/$defs/playerReadRequest",
                "#/$defs/playerReadResponse",
                "#/$defs/playerSeekRequest",
                "#/$defs/playerSeekResponse",
                "#/$defs/transcriptReadRequest",
                "#/$defs/transcriptEditTokenRequest",
                "#/$defs/transcriptSetSpeakerRequest",
                "#/$defs/transcriptRemoveSpeakerRequest",
                "#/$defs/transcriptAdoptTermRequest",
                "#/$defs/transcriptRejectTermRequest",
                "#/$defs/transcriptReadResponse",
                "#/$defs/transcriptEditTokenResponse",
                "#/$defs/transcriptSetSpeakerResponse",
                "#/$defs/transcriptRemoveSpeakerResponse",
                "#/$defs/transcriptAdoptTermResponse",
                "#/$defs/transcriptRejectTermResponse",
                "#/$defs/timelineReadRequest",
                "#/$defs/timelineReadResponse",
                "#/$defs/timelineApplyRequest",
                "#/$defs/timelineApplyResponse",
            },
            root.GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());

        foreach (string definitionName in new[]
        {
            "pingRequest",
            "listRequest",
            "createDraftRequest",
            "loadDraftRequest",
            "saveDraftRequest",
            "pickSourceRequest",
            "finalizeDraftRequest",
            "mutateDraftParams",
            "draftInput",
            "projectListItem",
            "draft",
            "pingResponse",
            "listResponse",
            "draftResponse",
            "pickSourceResponse",
            "projectError",
            "projectErrorResponse",
            "pingResult",
            "jobStatusParams",
            "jobCommandParams",
            "jobStatusRequest",
            "jobStatusResponse",
            "jobCancelRequest",
            "jobCancelResult",
            "jobCancelResponse",
            "jobRetryRequest",
            "jobRetryResult",
            "jobRetryResponse",
            "jobSnapshot",
            "bridgeError",
            "errorResponse",
            "playerReadParams",
            "playerSeekParams",
            "playerReadRequest",
            "playerReadResponse",
            "playerSeekRequest",
            "playerSeekResponse",
            "playerSnapshot",
            "playerProxy",
            "playerWaveform",
            "playerSprite",
            "playerFrame",
            "playerPosition",
            "rationalTime",
            "transcriptTimeRange",
            "transcriptPerformer",
            "transcriptSpeaker",
            "transcriptToken",
            "transcriptSentence",
            "transcriptUtterance",
            "transcriptTermCandidate",
            "transcriptTermDecision",
            "transcriptTextChange",
            "transcriptStaleRange",
            "transcriptInvalidation",
            "transcriptSnapshot",
            "transcriptReadParams",
            "transcriptEditTokenParams",
            "transcriptSetSpeakerParams",
            "transcriptRemoveSpeakerParams",
            "transcriptTermDecisionParams",
            "transcriptReadRequest",
            "transcriptEditTokenRequest",
            "transcriptSetSpeakerRequest",
            "transcriptRemoveSpeakerRequest",
            "transcriptAdoptTermRequest",
            "transcriptRejectTermRequest",
            "transcriptReadResponse",
            "transcriptEditTokenResponse",
            "transcriptSetSpeakerResponse",
            "transcriptRemoveSpeakerResponse",
            "transcriptAdoptTermResponse",
            "transcriptRejectTermResponse",
            "timelineTimeRange",
            "timelineElement",
            "timelineSnapshot",
            "timelineKeepOperation",
            "timelineDeleteOperation",
            "timelineSplitOperation",
            "timelineMoveOperation",
            "timelineReadParams",
            "timelineApplyParams",
            "timelineReadRequest",
            "timelineReadResponse",
            "timelineApplyRequest",
            "timelineApplyResponse",
        })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(definitionName).GetProperty("additionalProperties").ValueKind);
        }

        string proxyMediaPattern = definitions
            .GetProperty("proxyMediaReference")
            .GetProperty("pattern")
            .GetString()!;
        string spriteMediaPattern = definitions
            .GetProperty("spriteMediaReference")
            .GetProperty("pattern")
            .GetString()!;
        StringAssert.Contains(proxyMediaPattern, "player-media\\.appassets\\.example/media/v1/");
        StringAssert.Contains(proxyMediaPattern, "/proxy/");
        StringAssert.Contains(proxyMediaPattern, "[0-9a-f]{64}");
        StringAssert.Contains(spriteMediaPattern, "/sprite/");
        Assert.AreEqual(
            "#/$defs/proxyMediaReference",
            definitions.GetProperty("playerProxy")
                .GetProperty("properties")
                .GetProperty("mediaReference")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "#/$defs/spriteMediaReference",
            definitions.GetProperty("playerSprite")
                .GetProperty("properties")
                .GetProperty("imageReference")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            2,
            definitions.GetProperty("playerWaveform")
                .GetProperty("oneOf")
                .GetArrayLength());
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("playerReadResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "forbidden");
        Assert.AreEqual(
            4,
            definitions.GetProperty("timelineOperation")
                .GetProperty("oneOf")
                .GetArrayLength());
        Assert.AreEqual(
            1,
            definitions.GetProperty("timelineApplyParams")
                .GetProperty("properties")
                .GetProperty("operations")
                .GetProperty("minItems")
                .GetInt32());
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("timelineApplyResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "conflict");
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("playerSeekResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "forbidden");
    }

    [TestMethod]
    public void RequestAndResponseHaveDistinctExactEnvelopesAndTenFieldInput()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("pingRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("pingResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetStrings(definitions.GetProperty("errorResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetStrings(definitions.GetProperty("projectErrorResponse").GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/jobMethod",
            definitions.GetProperty("errorResponse")
                .GetProperty("properties")
                .GetProperty("method")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "#/$defs/projectMethod",
            definitions.GetProperty("projectErrorResponse")
                .GetProperty("properties")
                .GetProperty("method")
                .GetProperty("$ref")
                .GetString());
        CollectionAssert.AreEqual(
            new[]
            {
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
            },
            GetStrings(definitions.GetProperty("draftInput").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("playerReadRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("playerReadResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("playerSeekRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("playerSeekResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId" },
            GetStrings(definitions.GetProperty("playerReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId", "digest", "proxyFrameSequence" },
            GetStrings(definitions.GetProperty("playerSeekParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId" },
            GetStrings(definitions.GetProperty("timelineReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId", "expectedRevision", "operations" },
            GetStrings(definitions.GetProperty("timelineApplyParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "workingCopyId", "projectId", "baseVersionId", "revision", "createdAt", "updatedAt", "elements" },
            GetStrings(definitions.GetProperty("timelineSnapshot").GetProperty("required")));
        StringAssert.Contains(
            definitions.GetProperty("playerSeekParams")
                .GetProperty("properties")
                .GetProperty("digest")
                .GetProperty("$comment")
                .GetString()!,
            "256-byte request limit");
        Assert.AreEqual(
            0,
            definitions.GetProperty("emptyObject").GetProperty("maxProperties").GetInt32());
        Assert.AreEqual(
            36,
            definitions.GetProperty("canonicalId").GetProperty("maxLength").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { "jobId" },
            GetStrings(definitions.GetProperty("jobStatusParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId", "expectedRevision" },
            GetStrings(definitions.GetProperty("jobCommandParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "jobId",
                "kind",
                "state",
                "currentAttemptId",
                "currentAttemptNumber",
                "revision",
                "detailsRevision",
                "updatedAt",
                "progressRatio",
                "lastProgressAt",
                "lastHeartbeatAt",
                "failure",
                "retryability",
                "cancellationRequested",
                "warnings",
                "reconciliation",
            },
            GetStrings(definitions.GetProperty("jobSnapshot").GetProperty("required")));
    }

    [TestMethod]
    public void TranscriptSchemaMatchesClosedRuntimeContract()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        string[] methods =
        [
            "transcript.read",
            "transcript.editToken",
            "transcript.setSpeaker",
            "transcript.removeSpeaker",
            "transcript.adoptTerm",
            "transcript.rejectTerm",
        ];
        CollectionAssert.AreEqual(
            methods,
            GetStrings(definitions.GetProperty("transcriptMethod").GetProperty("enum")));

        (string Definition, string Method)[] requests =
        [
            ("transcriptReadRequest", methods[0]),
            ("transcriptEditTokenRequest", methods[1]),
            ("transcriptSetSpeakerRequest", methods[2]),
            ("transcriptRemoveSpeakerRequest", methods[3]),
            ("transcriptAdoptTermRequest", methods[4]),
            ("transcriptRejectTermRequest", methods[5]),
        ];
        foreach ((string definitionName, string method) in requests)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "params" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
        }

        (string Definition, string Method)[] responses =
        [
            ("transcriptReadResponse", methods[0]),
            ("transcriptEditTokenResponse", methods[1]),
            ("transcriptSetSpeakerResponse", methods[2]),
            ("transcriptRemoveSpeakerResponse", methods[3]),
            ("transcriptAdoptTermResponse", methods[4]),
            ("transcriptRejectTermResponse", methods[5]),
        ];
        foreach ((string definitionName, string method) in responses)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "result" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
            Assert.AreEqual(
                "#/$defs/transcriptResult",
                definition.GetProperty("properties")
                    .GetProperty("result")
                    .GetProperty("$ref")
                    .GetString());
        }

        CollectionAssert.AreEquivalent(
            new[] { "projectId" },
            GetStrings(definitions.GetProperty("transcriptReadParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "tokenId",
                "expectedText",
                "revisedText",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptEditTokenParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "expectedSpeakerRevisionId",
                "speakerId",
                "performerId",
                "surname",
                "displayFormat",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptSetSpeakerParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "expectedSpeakerRevisionId",
                "speakerId",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptRemoveSpeakerParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "candidateId",
                "termEntryId",
                "termEntryVersion",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptTermDecisionParams")
                .GetProperty("required")));

        JsonElement resultAlternatives = definitions.GetProperty("transcriptResult")
            .GetProperty("oneOf");
        Assert.AreEqual(
            "available",
            resultAlternatives[0].GetProperty("properties")
                .GetProperty("status")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "forbidden",
                "unavailable",
                "staleBase",
                "conflict",
                "noChange",
                "invalid",
            },
            GetStrings(definitions.GetProperty("transcriptFailureStatus")
                .GetProperty("enum")));
        Assert.AreEqual(
            "null",
            resultAlternatives[1].GetProperty("properties")
                .GetProperty("snapshot")
                .GetProperty("type")
                .GetString());

        CollectionAssert.AreEqual(
            new[]
            {
                "projectId",
                "revisionId",
                "revisionDigest",
                "parentRevisionId",
                "changeSource",
                "createdAt",
                "speakerRevisionId",
                "performers",
                "utterances",
                "termCandidates",
                "termDecisions",
                "diff",
                "staleRange",
                "invalidation",
            },
            GetStrings(definitions.GetProperty("transcriptSnapshot")
                .GetProperty("required")));
        CollectionAssert.AreEqual(
            new[]
            {
                "sourceRevisionId",
                "sourceRevisionDigest",
                "revisionId",
                "revisionDigest",
            },
            GetStrings(definitions.GetProperty("transcriptStaleRange")
                .GetProperty("required")));

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("confidenceClass", StringComparison.Ordinal));
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(
            definitions.GetProperty("transcriptTermCandidate")
                .GetProperty("$comment")
                .GetString()!,
            "no invented confidence classification");
    }

    [TestMethod]
    public void ListSchemaRepresentsUnknownColumnsAsNullAndNeverDefinesPrivateValues()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement properties = definitions
            .GetProperty("projectListItem")
            .GetProperty("properties");

        foreach (string unavailable in new[]
        {
            "episodeNumber",
            "owner",
            "editState",
            "reviewState",
        })
        {
            Assert.AreEqual(
                "null",
                properties.GetProperty(unavailable).GetProperty("type").GetString());
        }

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("windowsSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("filePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("contentBody", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("processOutput", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PersistencePayloadSchemaIsClosedVersionedAndOpaque()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath(
                "schemas/project-creation-draft.schema.json")));
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        Assert.AreEqual(12, root.GetProperty("required").GetArrayLength());
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());
        string schema = root.GetRawText();
        StringAssert.Contains(schema, "sourceCandidateId");
        Assert.IsFalse(schema.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetStrings(JsonElement array)
    {
        return array.EnumerateArray().Select(static item => item.GetString()!).ToArray();
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