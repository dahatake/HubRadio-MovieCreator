using System.Text.Json;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RenderGraphSchemaTests
{
    [TestMethod]
    public void SchemaIsClosedDraft202012WithExactRootContract()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "compositionVersionId",
                "compositionVersionDigest",
                "timelineMapId",
                "timelineMapDigest",
                "profileId",
                "profileDigest",
                "layoutSpecId",
                "layoutSpecDigest",
                "template",
                "inputs",
                "fonts",
                "nodes",
                "outputNodeId",
            },
            Strings(root.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            Strings(root.GetProperty("required")),
            root.GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
    }

    [TestMethod]
    public void NodeUnionHasExactlySixClosedTypedDiscriminators()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string[] references = definitions.GetProperty("renderNode")
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(static item => item.GetProperty("$ref").GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/videoNode",
                "#/$defs/audioNode",
                "#/$defs/subtitleNode",
                "#/$defs/graphicNode",
                "#/$defs/transitionNode",
                "#/$defs/outputNode",
            },
            references);
        CollectionAssert.AreEqual(
            new[] { "video", "audio", "subtitle", "graphic", "transition", "output" },
            references.Select(reference => definitions
                .GetProperty(reference["#/$defs/".Length..])
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString())
                .ToArray());

        foreach (string reference in references)
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(reference["#/$defs/".Length..])
                    .GetProperty("additionalProperties")
                    .ValueKind);
        }
    }

    [TestMethod]
    public void SchemaUsesCanonicalIdentityDigestOriginAndNoExecutionStrings()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        Assert.AreEqual(
            "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            definitions.GetProperty("entityId").GetProperty("pattern").GetString());
        Assert.AreEqual(
            "^[0-9a-f]{64}$",
            definitions.GetProperty("contentDigest").GetProperty("pattern").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/sourceSpanOrigin",
                "#/$defs/assetOrigin",
                "#/$defs/editorialOrigin",
            },
            definitions.GetProperty("origin")
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("storagePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("filterExpression", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("commandLine", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("argumentList", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SchemaRetainsRuntimeConditionalSourceCueAndTransitionRules()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        Assert.IsTrue(definitions.GetProperty("videoNode").TryGetProperty("allOf", out _));
        Assert.IsTrue(definitions.GetProperty("audioNode").TryGetProperty("allOf", out _));
        Assert.AreEqual(
            "#/$defs/subtitleCue",
            definitions.GetProperty("subtitleNode")
                .GetProperty("properties")
                .GetProperty("cue")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "#/$defs/graphicCue",
            definitions.GetProperty("graphicNode")
                .GetProperty("properties")
                .GetProperty("cue")
                .GetProperty("$ref")
                .GetString());
        JsonElement transitionRules = definitions.GetProperty("transition")
            .GetProperty("allOf");
        Assert.AreEqual(2, transitionRules.GetArrayLength());
        CollectionAssert.AreEqual(
            new[] { "wipe", "slide" },
            Strings(transitionRules[0]
                .GetProperty("if")
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")));
        Assert.AreEqual(
            0,
            transitionRules[1]
                .GetProperty("then")
                .GetProperty("properties")
                .GetProperty("duration")
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("const")
                .GetInt32());
    }

    private static JsonDocument LoadSchema() =>
        JsonDocument.Parse(File.ReadAllBytes(ResolveRepositoryPath(
            "schemas/render-graph.schema.json")));

    private static string[] Strings(JsonElement array) =>
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}