using System.Text.Json;
using HubRadio.Infrastructure.Worker;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class WorkerProtocolSchemaTests
{
    private const string SchemaRelativePath = "schemas/worker-protocol.schema.json";

    [TestMethod]
    public void SchemaIsClosedDraft202012OneOfWithExactDefinitions()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("WorkerProtocolMessage", root.GetProperty("title").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/request",
                "#/$defs/progress",
                "#/$defs/response",
                "#/$defs/error",
            },
            root.GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "protocolVersion",
                "requestId",
                "method",
                "emptyParams",
                "request",
                "progress",
                "pingResult",
                "response",
                "errorCode",
                "errorBody",
                "error",
            },
            definitions.EnumerateObject().Select(static item => item.Name).ToArray());

        foreach (string definitionName in new[]
        {
            "emptyParams",
            "request",
            "progress",
            "pingResult",
            "response",
            "errorBody",
            "error",
        })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(definitionName).GetProperty("additionalProperties").ValueKind);
        }
    }

    [TestMethod]
    public void SchemaContainsOnlyPingAndTheFourStableWorkerErrors()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        Assert.AreEqual(
            WorkerProtocolParser.ProtocolVersion,
            definitions.GetProperty("protocolVersion").GetProperty("const").GetInt32());
        Assert.AreEqual(
            WorkerProtocolParser.PingMethod,
            definitions.GetProperty("method").GetProperty("const").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "worker.invalid_message",
                "worker.message_too_large",
                "worker.unknown_method",
                "worker.processing_failed",
            },
            GetStrings(definitions.GetProperty("errorCode").GetProperty("enum")));
    }

    [TestMethod]
    public void EnvelopesHaveExactRequiredAndDeclaredProperties()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["request"] = ["version", "type", "requestId", "method", "params"],
            ["progress"] =
            [
                "version",
                "type",
                "requestId",
                "sequence",
                "completedUnits",
                "totalUnits",
                "phase",
            ],
            ["response"] = ["version", "type", "requestId", "method", "result"],
            ["error"] = ["version", "type", "requestId", "error"],
            ["pingResult"] = ["protocolVersion"],
            ["errorBody"] = ["code"],
        };

        foreach ((string definitionName, string[] properties) in expected)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(properties, GetStrings(definition.GetProperty("required")));
            CollectionAssert.AreEquivalent(
                properties,
                definition.GetProperty("properties")
                    .EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray());
            Assert.AreEqual(properties.Length, definition.GetProperty("maxProperties").GetInt32());
        }

        Assert.AreEqual(0, definitions.GetProperty("emptyParams").GetProperty("maxProperties").GetInt32());
    }

    [TestMethod]
    public void EveryAcceptedStringIsExplicitlyBoundedWithinOneRecord()
    {
        using JsonDocument document = LoadSchema();
        var stringSchemas = new List<JsonElement>();
        CollectStringSchemas(document.RootElement, stringSchemas);

        Assert.IsTrue(stringSchemas.Count > 0);
        foreach (JsonElement schema in stringSchemas)
        {
            Assert.IsTrue(schema.TryGetProperty("maxLength", out JsonElement maxLength));
            Assert.IsTrue(maxLength.GetInt32() <= WorkerProtocolParser.MaxMessageUtf8Bytes);
        }
    }

    [TestMethod]
    public void ProgressBoundsAndRuntimeOnlyCrossFieldRulesAreExplicit()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("$defs").GetProperty("progress").GetProperty("properties");
        string comment = root.GetProperty("$comment").GetString()!;

        Assert.AreEqual(1, properties.GetProperty("sequence").GetProperty("minimum").GetInt32());
        Assert.AreEqual(int.MaxValue, properties.GetProperty("sequence").GetProperty("maximum").GetInt32());
        Assert.AreEqual(0, properties.GetProperty("completedUnits").GetProperty("minimum").GetInt32());
        Assert.AreEqual(int.MaxValue, properties.GetProperty("completedUnits").GetProperty("maximum").GetInt32());
        Assert.AreEqual(1, properties.GetProperty("totalUnits").GetProperty("minimum").GetInt32());
        Assert.AreEqual(int.MaxValue, properties.GetProperty("totalUnits").GetProperty("maximum").GetInt32());
        StringAssert.Contains(comment, "strict UTF-8 without BOM");
        StringAssert.Contains(comment, "512 UTF-8 bytes");
        StringAssert.Contains(comment, "duplicate properties");
        StringAssert.Contains(comment, "record ordering");
        StringAssert.Contains(comment, "completedUnits greater than totalUnits");
        StringAssert.Contains(comment, "mismatched requestId, method, or version");
    }

    private static void CollectStringSchemas(JsonElement value, List<JsonElement> result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "string", StringComparison.Ordinal))
            {
                result.Add(value);
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                CollectStringSchemas(property.Value, result);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CollectStringSchemas(item, result);
            }
        }
    }

    private static JsonDocument LoadSchema() =>
        JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(SchemaRelativePath)));

    private static string[] GetStrings(JsonElement array) =>
        array.EnumerateArray().Select(static item => item.GetString()!).ToArray();

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