using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class BrandTemplateSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string SchemaRelativePath = "schemas/brand-template.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemaIsAClosedDraft202012BrandTemplateVersion()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(Draft202012, root.GetProperty("$schema").GetString());
        Assert.AreEqual("BrandTemplateVersion", root.GetProperty("title").GetString());
        AssertAdditionalPropertiesFalse(root);
        CollectionAssert.AreEquivalent(
            new[] { "seriesId", "reference", "kind", "parameterDefinitions" },
            GetStringArray(root.GetProperty("required")));
        Assert.AreEqual(4, root.GetProperty("properties").EnumerateObject().Count());
        StringAssert.Contains(root.GetProperty("$comment").GetString(), "not by itself a cryptographic signature");
        StringAssert.Contains(root.GetProperty("$comment").GetString(), "approval record");
    }

    [TestMethod]
    public void SchemaContainsExactlyTheRequirementTemplateKinds()
    {
        using JsonDocument document = LoadSchema();
        JsonElement kind = document.RootElement.GetProperty("$defs").GetProperty("brandTemplateKind");

        CollectionAssert.AreEqual(
            new[]
            {
                "long-form",
                "youtube-landscape",
                "youtube-shorts",
                "tiktok",
                "quiz",
                "announcement",
                "thumbnail",
                "opening",
                "ending",
                "telop",
                "subtitle",
                "topic-card",
                "call-to-action",
                "publication-scope-indicator",
            },
            GetStringArray(kind.GetProperty("enum")));
    }

    [TestMethod]
    public void ReferenceAndKeysUseCanonicalIdentityDigestAndAsciiPatterns()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement reference = definitions.GetProperty("templateReference");

        AssertPatternAcceptsOnly(definitions.GetProperty("entityId"), LowercaseUuid);
        AssertPatternAcceptsOnly(definitions.GetProperty("contentDigest"), LowercaseDigest);
        AssertPatternAcceptsOnly(definitions.GetProperty("parameterName"), "accent-color");
        Assert.AreEqual(36, definitions.GetProperty("entityId").GetProperty("minLength").GetInt32());
        Assert.AreEqual(36, definitions.GetProperty("entityId").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(64, definitions.GetProperty("contentDigest").GetProperty("minLength").GetInt32());
        Assert.AreEqual(64, definitions.GetProperty("contentDigest").GetProperty("maxLength").GetInt32());
        string parameterPattern = definitions.GetProperty("parameterName").GetProperty("pattern").GetString()!;
        Assert.IsFalse(Regex.IsMatch("Accent-color", parameterPattern));
        Assert.IsFalse(Regex.IsMatch("accent_color", parameterPattern));
        Assert.IsFalse(Regex.IsMatch("accent--color", parameterPattern));
        Assert.IsFalse(Regex.IsMatch("аccent-color", parameterPattern));

        AssertAdditionalPropertiesFalse(reference);
        CollectionAssert.AreEquivalent(
            new[] { "id", "name", "versionDigest" },
            GetStringArray(reference.GetProperty("required")));
        Assert.AreEqual(3, reference.GetProperty("properties").EnumerateObject().Count());
    }

    [TestMethod]
    public void ParameterValueIsAClosedFiveArmTaggedUnion()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement[] arms = definitions
            .GetProperty("templateParameterValue")
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();

        Assert.AreEqual(5, arms.Length);
        CollectionAssert.AreEqual(
            new[] { "text", "integer", "boolean", "rational", "asset-reference" },
            arms.Select(static arm => arm
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString()).ToArray());

        foreach (JsonElement arm in arms)
        {
            AssertAdditionalPropertiesFalse(arm);
            Assert.IsTrue(GetStringArray(arm.GetProperty("required")).Contains("kind"));
        }

        JsonElement rational = definitions.GetProperty("rationalNumber");
        AssertAdditionalPropertiesFalse(rational);
        CollectionAssert.AreEquivalent(
            new[] { "numerator", "denominator" },
            GetStringArray(rational.GetProperty("required")));
        Assert.AreEqual(
            long.MinValue,
            rational.GetProperty("properties").GetProperty("numerator").GetProperty("minimum").GetInt64());
        Assert.AreEqual(
            1L,
            rational.GetProperty("properties").GetProperty("denominator").GetProperty("minimum").GetInt64());
        StringAssert.Contains(rational.GetProperty("$comment").GetString(), "reduced form");
    }

    [TestMethod]
    public void DefinitionAndAppliedShapesAreClosedAndDocumentRuntimeGuards()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement definition = definitions.GetProperty("templateParameterDefinition");
        JsonElement assignment = definitions.GetProperty("templateParameterAssignment");
        JsonElement applied = definitions.GetProperty("appliedBrandTemplate");

        AssertAdditionalPropertiesFalse(definition);
        AssertAdditionalPropertiesFalse(assignment);
        AssertAdditionalPropertiesFalse(applied);
        CollectionAssert.AreEquivalent(
            new[] { "name", "defaultValue", "editorOverrideAllowed" },
            GetStringArray(definition.GetProperty("required")));
        Assert.IsTrue(
            definition.GetProperty("properties").GetProperty("editorValueAllowlist").GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual(
            1,
            definition.GetProperty("properties").GetProperty("editorValueAllowlist").GetProperty("minItems").GetInt32());
        Assert.IsTrue(
            definition.GetProperty("properties").GetProperty("editorOverrideAllowed").GetProperty("type").GetString()
                == "boolean");
        JsonElement fixedParameterRule = definition.GetProperty("allOf")[0];
        Assert.IsFalse(
            fixedParameterRule
                .GetProperty("if")
                .GetProperty("properties")
                .GetProperty("editorOverrideAllowed")
                .GetProperty("const")
                .GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "editorValueAllowlist" },
            GetStringArray(
                fixedParameterRule.GetProperty("then").GetProperty("not").GetProperty("required")));
        StringAssert.Contains(definition.GetProperty("$comment").GetString(), "same kind as defaultValue");
        StringAssert.Contains(definition.GetProperty("$comment").GetString(), "optional editorValueAllowlist");
        CollectionAssert.AreEquivalent(
            new[] { "name", "value" },
            GetStringArray(assignment.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "seriesId", "reference", "kind", "parameters" },
            GetStringArray(applied.GetProperty("required")));
        StringAssert.Contains(applied.GetProperty("$comment").GetString(), "runtime validation");
    }

    private static void AssertPatternAcceptsOnly(JsonElement schema, string canonical)
    {
        string pattern = schema.GetProperty("pattern").GetString()!;

        Assert.IsTrue(Regex.IsMatch(canonical, pattern));
        Assert.IsFalse(Regex.IsMatch($" {canonical}", pattern));
        Assert.IsFalse(Regex.IsMatch($"{canonical} ", pattern));
        Assert.IsFalse(Regex.IsMatch($"{canonical}\n", pattern));
        Assert.IsFalse(Regex.IsMatch($"{canonical}\r", pattern));
        Assert.IsFalse(Regex.IsMatch($"{canonical}\u2028", pattern));
        Assert.IsFalse(Regex.IsMatch($"{canonical}\u2029", pattern));
        Assert.IsFalse(Regex.IsMatch(canonical.ToUpperInvariant(), pattern));
    }

    private static void AssertAdditionalPropertiesFalse(JsonElement schema)
    {
        Assert.AreEqual(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
    }

    private static string[] GetStringArray(JsonElement array)
    {
        return array
            .EnumerateArray()
            .Select(static item => item.GetString()
                ?? throw new InvalidOperationException("Schema array item must be a string."))
            .ToArray();
    }

    private static JsonDocument LoadSchema()
    {
        return JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(SchemaRelativePath)));
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string fullPath = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative schema '{relativePath}'.",
                        fullPath);
                }

                return fullPath;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}