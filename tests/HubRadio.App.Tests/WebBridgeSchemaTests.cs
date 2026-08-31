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
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "256 UTF-8 bytes");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "4096-byte responses");
        Assert.AreEqual(1, definitions.GetProperty("protocolVersion").GetProperty("const").GetInt32());
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
            },
            definitions.GetProperty("method").GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
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
        })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(definitionName).GetProperty("additionalProperties").ValueKind);
        }
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