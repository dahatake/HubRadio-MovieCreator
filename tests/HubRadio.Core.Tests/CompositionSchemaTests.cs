using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class CompositionSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";

    [TestMethod]
    [DataRow("schemas/working-copy.schema.json", "WorkingCopy")]
    [DataRow("schemas/composition-version.schema.json", "CompositionVersion")]
    public void SchemasParseAsClosedDraft202012Contracts(string relativePath, string title)
    {
        using JsonDocument document = LoadSchema(relativePath);
        JsonElement root = document.RootElement;

        Assert.AreEqual(Draft202012, root.GetProperty("$schema").GetString());
        Assert.AreEqual(title, root.GetProperty("title").GetString());
        Assert.AreEqual("object", root.GetProperty("type").GetString());
        AssertAdditionalPropertiesFalse(root);
        AssertCanonicalUuidPattern(root.GetProperty("$defs").GetProperty("entityId"));
    }

    [TestMethod]
    public void WorkingCopySchemaContainsCompleteDraftStateWithoutVersionApprovalOrDigest()
    {
        using JsonDocument document = LoadSchema("schemas/working-copy.schema.json");
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        CollectionAssert.AreEquivalent(
            new[] { "id", "projectId", "createdAt", "updatedAt", "elements" },
            GetStringArray(root.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "id", "projectId", "baseVersionId", "createdAt", "updatedAt", "elements" },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.IsFalse(properties.TryGetProperty("contentDigest", out _));
        Assert.IsFalse(properties.TryGetProperty("approvalStatus", out _));
        StringAssert.Contains(GetRequiredString(root, "$comment"), "recoverable draft state");
    }

    [TestMethod]
    public void CompositionVersionSchemaBindsImmutableParentActorReasonAndDigest()
    {
        using JsonDocument document = LoadSchema("schemas/composition-version.schema.json");
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "projectId",
                "createdByActorId",
                "createdAt",
                "changeReason",
                "contentDigest",
                "elements",
            },
            GetStringArray(root.GetProperty("required")));
        Assert.IsTrue(properties.TryGetProperty("parentVersionId", out _));
        Assert.AreEqual(
            "#/$defs/contentDigest",
            properties.GetProperty("contentDigest").GetProperty("$ref").GetString());
        Assert.IsFalse(properties.TryGetProperty("children", out _));
        Assert.IsFalse(properties.TryGetProperty("approvalStatus", out _));
        StringAssert.Contains(GetRequiredString(root, "$comment"), "Approval is a separate record");
    }

    [TestMethod]
    [DataRow("schemas/working-copy.schema.json")]
    [DataRow("schemas/composition-version.schema.json")]
    public void CompositionElementSchemaPreservesOriginsAndAdoptedSuggestionReference(
        string relativePath)
    {
        using JsonDocument document = LoadSchema(relativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement element = definitions.GetProperty("compositionElement");
        JsonElement properties = element.GetProperty("properties");

        AssertAdditionalPropertiesFalse(element);
        CollectionAssert.AreEquivalent(
            new[] { "id", "outputRange", "origin" },
            GetStringArray(element.GetProperty("required")));
        Assert.IsTrue(properties.TryGetProperty("adoptedSuggestion", out _));
        Assert.AreEqual(
            "#/$defs/sourceTimeRange",
            properties.GetProperty("sourceRange").GetProperty("$ref").GetString());

        JsonElement sourceRangeRule = element.GetProperty("allOf")
            .EnumerateArray()
            .Single(static rule => rule.TryGetProperty("else", out _));
        Assert.AreEqual(
            "sourceSpan",
            sourceRangeRule
                .GetProperty("if")
                .GetProperty("properties")
                .GetProperty("origin")
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "sourceRange" },
            GetStringArray(sourceRangeRule.GetProperty("then").GetProperty("required")));
        Assert.AreEqual(
            JsonValueKind.False,
            sourceRangeRule
                .GetProperty("else")
                .GetProperty("properties")
                .GetProperty("sourceRange")
                .ValueKind);

        JsonElement origin = definitions.GetProperty("originRef");
        string[] variants = origin.GetProperty("oneOf")
            .EnumerateArray()
            .Select(static item => item.GetProperty("$ref").GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/sourceSpanOrigin",
                "#/$defs/assetOrigin",
                "#/$defs/editorialRecordOrigin",
            },
            variants);

        JsonElement suggestionReference = definitions.GetProperty("suggestionReference");
        CollectionAssert.AreEquivalent(
            new[] { "suggestionId", "origin" },
            GetStringArray(suggestionReference.GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/sourceSpanOrigin",
            suggestionReference.GetProperty("properties").GetProperty("origin").GetProperty("$ref").GetString());
        StringAssert.Contains(GetRequiredString(suggestionReference, "$comment"), "adopted Suggestion");
        StringAssert.Contains(
            GetRequiredString(definitions.GetProperty("editorialRecordOrigin"), "$comment"),
            "Human-authored");
    }

    [TestMethod]
    [DataRow("schemas/working-copy.schema.json")]
    [DataRow("schemas/composition-version.schema.json")]
    public void OutputRangesAreNonNegativePositiveIntegerRationals(string relativePath)
    {
        using JsonDocument document = LoadSchema(relativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement range = definitions.GetProperty("outputTimeRange");

        AssertAdditionalPropertiesFalse(range);
        AssertRational(definitions.GetProperty("rationalTime"), 0);
        AssertRational(definitions.GetProperty("positiveDuration"), 1);
    }

    [TestMethod]
    [DataRow("schemas/working-copy.schema.json")]
    [DataRow("schemas/composition-version.schema.json")]
    public void SourceTimeRangesAllowNegativeStartsAndRequirePositiveDurations(
        string relativePath)
    {
        using JsonDocument document = LoadSchema(relativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement sourceRange = definitions.GetProperty("sourceTimeRange");

        AssertAdditionalPropertiesFalse(sourceRange);
        CollectionAssert.AreEquivalent(
            new[] { "start", "duration" },
            GetStringArray(sourceRange.GetProperty("required")));

        JsonElement start = sourceRange.GetProperty("properties").GetProperty("start");
        AssertRational(start, long.MinValue);
        Assert.AreEqual(
            "#/$defs/positiveDuration",
            sourceRange
                .GetProperty("properties")
                .GetProperty("duration")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            1L,
            definitions
                .GetProperty("positiveDuration")
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("minimum")
                .GetInt64());
        StringAssert.Contains(GetRequiredString(sourceRange, "$comment"), "equal");
    }

    private static void AssertRational(JsonElement rational, long minimumValue)
    {
        AssertAdditionalPropertiesFalse(rational);
        CollectionAssert.AreEquivalent(
            new[] { "value", "rate" },
            GetStringArray(rational.GetProperty("required")));
        JsonElement properties = rational.GetProperty("properties");
        Assert.AreEqual(minimumValue, properties.GetProperty("value").GetProperty("minimum").GetInt64());
        Assert.AreEqual(1L, properties.GetProperty("rate").GetProperty("minimum").GetInt64());
    }

    private static void AssertCanonicalUuidPattern(JsonElement schema)
    {
        string pattern = GetRequiredString(schema, "pattern");
        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(EmptyUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseUuid.Replace("-", string.Empty), pattern));
    }

    private static void AssertAdditionalPropertiesFalse(JsonElement schema)
    {
        Assert.AreEqual(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"Schema property '{propertyName}' must be a string.");
    }

    private static string[] GetStringArray(JsonElement array)
    {
        return array
            .EnumerateArray()
            .Select(static item => item.GetString()
                ?? throw new InvalidOperationException("Schema array item must be a string."))
            .ToArray();
    }

    private static JsonDocument LoadSchema(string relativePath)
    {
        string fullPath = ResolveRepositoryPath(relativePath);
        return JsonDocument.Parse(File.ReadAllText(fullPath));
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