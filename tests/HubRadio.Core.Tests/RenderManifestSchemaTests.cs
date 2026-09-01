using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RenderManifestSchemaTests
{
    private const string RenderSchemaPath = "schemas/render-manifest.schema.json";
    private const string DeliverySchemaPath = "schemas/delivery-manifest.schema.json";

    [TestMethod]
    public void RenderSchemaIsClosedSuccessfulRunEvidence()
    {
        using JsonDocument document = LoadSchema(RenderSchemaPath);
        JsonElement root = document.RootElement;

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("RenderManifest", root.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        StringAssert.Contains(GetString(root, "$comment"), "successful render");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "projectId",
                "compositionVersionId",
                "compositionVersionDigest",
                "timelineMapId",
                "timelineMapDigest",
                "profileId",
                "profileDigest",
                "templateId",
                "templateVersionDigest",
                "configurationDigest",
                "applicationVersion",
                "applicationDigest",
                "inputs",
                "tools",
                "models",
                "fonts",
                "outputs",
            },
            GetStrings(root.GetProperty("required")));
    }

    [TestMethod]
    public void RenderSchemaRequiresHashedInputsToolsModelsFontsAndOutputs()
    {
        using JsonDocument document = LoadSchema(RenderSchemaPath);
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        AssertRequiredArray(root, "inputs", "#/$defs/renderInputEvidence", minimum: 1);
        AssertRequiredArray(root, "tools", "#/$defs/renderToolEvidence", minimum: 1);
        AssertRequiredArray(root, "models", "#/$defs/renderModelEvidence", minimum: null);
        AssertRequiredArray(root, "fonts", "#/$defs/renderFontEvidence", minimum: null);
        AssertRequiredArray(root, "outputs", "#/$defs/manifestArtifact", minimum: 1);

        CollectionAssert.Contains(
            GetStrings(definitions.GetProperty("renderToolEvidence").GetProperty("required")),
            "executableDigest");
        CollectionAssert.Contains(
            GetStrings(definitions.GetProperty("renderModelEvidence").GetProperty("required")),
            "weightsDigest");
        CollectionAssert.Contains(
            GetStrings(definitions.GetProperty("renderFontEvidence").GetProperty("required")),
            "fileDigest");
        AssertManifestArtifactDefinition(definitions.GetProperty("manifestArtifact"));
    }

    [TestMethod]
    public void DeliverySchemaBindsArtifactsToOneVersionRenderAndProfile()
    {
        using JsonDocument document = LoadSchema(DeliverySchemaPath);
        JsonElement root = document.RootElement;

        Assert.AreEqual("DeliveryManifest", root.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "projectId",
                "compositionVersionId",
                "compositionVersionDigest",
                "renderManifestId",
                "profileId",
                "profileDigest",
                "artifacts",
            },
            GetStrings(root.GetProperty("required")));
        StringAssert.Contains(GetString(root, "$comment"), "one project");
        AssertRequiredArray(root, "artifacts", "#/$defs/manifestArtifact", minimum: 1);
        AssertManifestArtifactDefinition(root.GetProperty("$defs").GetProperty("manifestArtifact"));
    }

    [TestMethod]
    public void DeliverySchemaCarriesClosedOptionalSubtitleVariantEvidence()
    {
        using JsonDocument document = LoadSchema(DeliverySchemaPath);
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        JsonElement property = root
            .GetProperty("properties")
            .GetProperty("subtitleVariant");
        JsonElement[] alternatives = property.GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();

        Assert.AreEqual(
            "#/$defs/subtitleDeliveryVariantEvidence",
            alternatives[0].GetProperty("$ref").GetString());
        Assert.AreEqual("null", alternatives[1].GetProperty("type").GetString());
        CollectionAssert.DoesNotContain(
            GetStrings(root.GetProperty("required")),
            "subtitleVariant");

        JsonElement evidence = definitions.GetProperty(
            "subtitleDeliveryVariantEvidence");
        Assert.AreEqual(
            JsonValueKind.False,
            evidence.GetProperty("additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "kind",
                "renderGraphId",
                "displayProfileDigest",
                "subtitleEvidenceDigest",
                "sidecarArtifact",
            },
            GetStrings(evidence.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "burnedIn", "subtitleFree" },
            GetStrings(evidence.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")));
        JsonElement[] sidecarAlternatives = evidence
            .GetProperty("properties")
            .GetProperty("sidecarArtifact")
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        JsonElement sidecarOverrides = sidecarAlternatives[0]
            .GetProperty("allOf")[1]
            .GetProperty("properties");
        Assert.AreEqual(
            "subtitle",
            sidecarOverrides.GetProperty("type")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            "#/$defs/nonBlankText",
            sidecarOverrides.GetProperty("language")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "null",
            sidecarAlternatives[1].GetProperty("type").GetString());
        JsonElement burnedInConstraint = evidence
            .GetProperty("allOf")[0];
        Assert.AreEqual(
            "burnedIn",
            burnedInConstraint.GetProperty("if")
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            "null",
            burnedInConstraint.GetProperty("then")
                .GetProperty("properties")
                .GetProperty("sidecarArtifact")
                .GetProperty("type")
                .GetString());
        StringAssert.Contains(GetString(root, "$comment"), "R-026");
    }

    [TestMethod]
    public void SchemasUseCanonicalLowercaseSha256AndUuidForms()
    {
        foreach (string path in new[] { RenderSchemaPath, DeliverySchemaPath })
        {
            using JsonDocument document = LoadSchema(path);
            JsonElement definitions = document.RootElement.GetProperty("$defs");
            string digestPattern = GetString(definitions.GetProperty("contentDigest"), "pattern");
            string idPattern = GetString(definitions.GetProperty("entityId"), "pattern");

            Assert.IsTrue(Regex.IsMatch(new string('a', 64), digestPattern));
            Assert.IsFalse(Regex.IsMatch(new string('A', 64), digestPattern));
            Assert.IsTrue(Regex.IsMatch("00112233-4455-6677-8899-aabbccddeeff", idPattern));
            Assert.IsFalse(Regex.IsMatch("00000000-0000-0000-0000-000000000000", idPattern));
        }
    }

    private static void AssertRequiredArray(
        JsonElement root,
        string propertyName,
        string expectedItemReference,
        int? minimum)
    {
        JsonElement array = root.GetProperty("properties").GetProperty(propertyName);
        Assert.AreEqual("array", array.GetProperty("type").GetString());
        Assert.AreEqual(JsonValueKind.True, array.GetProperty("uniqueItems").ValueKind);
        Assert.AreEqual(expectedItemReference, array.GetProperty("items").GetProperty("$ref").GetString());

        if (minimum is null)
        {
            Assert.IsFalse(array.TryGetProperty("minItems", out _));
        }
        else
        {
            Assert.AreEqual(minimum.Value, array.GetProperty("minItems").GetInt32());
        }
    }

    private static void AssertManifestArtifactDefinition(JsonElement artifact)
    {
        Assert.AreEqual(JsonValueKind.False, artifact.GetProperty("additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[] { "type", "mediaType", "language", "byteSize", "sha256", "storageReference" },
            GetStrings(artifact.GetProperty("required")));
        StringAssert.Contains(GetString(artifact, "$comment"), "D-015");
    }

    private static JsonDocument LoadSchema(string relativePath)
    {
        return JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(relativePath)));
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.GetProperty(name).GetString()
            ?? throw new InvalidOperationException($"Schema property '{name}' must be a string.");
    }

    private static string[] GetStrings(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(static item => item.GetString()
                ?? throw new InvalidOperationException("Schema array item must be a string."))
            .ToArray();
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative schema '{relativePath}'.",
                        path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}