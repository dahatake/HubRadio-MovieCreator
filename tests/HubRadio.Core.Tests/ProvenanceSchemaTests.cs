using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProvenanceSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string SourceSpanSchemaRelativePath = "schemas/source-span.schema.json";
    private const string OriginRefSchemaRelativePath = "schemas/origin-ref.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemaFilesParseFromTheirRepositoryRelativePaths()
    {
        using JsonDocument sourceSpan = LoadSchema(SourceSpanSchemaRelativePath);
        using JsonDocument originRef = LoadSchema(OriginRefSchemaRelativePath);

        Assert.AreEqual(JsonValueKind.Object, sourceSpan.RootElement.ValueKind);
        Assert.AreEqual(JsonValueKind.Object, originRef.RootElement.ValueKind);
        Assert.AreEqual(Draft202012, sourceSpan.RootElement.GetProperty("$schema").GetString());
        Assert.AreEqual(Draft202012, originRef.RootElement.GetProperty("$schema").GetString());
        StringAssert.Contains(
            sourceSpan.RootElement.GetProperty("$comment").GetString(),
            "canonical persisted form");
        StringAssert.Contains(
            originRef.RootElement.GetProperty("$comment").GetString(),
            "canonical persisted form");
    }

    [TestMethod]
    public void SourceSpanSchemaRequiresCoreFieldsAndRejectsUnknownProperties()
    {
        using JsonDocument document = LoadSchema(SourceSpanSchemaRelativePath);
        JsonElement root = document.RootElement;

        AssertAdditionalPropertiesFalse(root);
        CollectionAssert.AreEquivalent(
            new[] { "id", "assetId", "streamId", "range", "sourceDigest" },
            GetStringArray(root.GetProperty("required")));

        string[] required = GetStringArray(root.GetProperty("required"));
        Assert.IsFalse(required.Contains("transcriptRevisionId", StringComparer.Ordinal));
        Assert.IsFalse(required.Contains("transcriptTokenIds", StringComparer.Ordinal));
    }

    [TestMethod]
    public void SourceSpanSchemaUsesCanonicalIdDigestAndStreamPatterns()
    {
        using JsonDocument document = LoadSchema(SourceSpanSchemaRelativePath);
        JsonElement properties = document.RootElement.GetProperty("properties");

        AssertCanonicalUuidPattern(properties.GetProperty("id"));
        AssertCanonicalUuidPattern(properties.GetProperty("assetId"));
        AssertCanonicalUuidPattern(properties.GetProperty("transcriptRevisionId"));
        AssertCanonicalUuidPattern(
            properties.GetProperty("transcriptTokenIds").GetProperty("items"));

        JsonElement digest = properties.GetProperty("sourceDigest");
        Assert.AreEqual("string", digest.GetProperty("type").GetString());
        string digestPattern = GetPattern(digest);
        Assert.IsTrue(Regex.IsMatch(LowercaseDigest, digestPattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest.ToUpperInvariant(), digestPattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest[..^1], digestPattern));

        JsonElement streamId = properties.GetProperty("streamId");
        Assert.AreEqual(1, streamId.GetProperty("minLength").GetInt32());
        string streamPattern = GetPattern(streamId);
        Assert.IsTrue(Regex.IsMatch("video:0", streamPattern));
        Assert.IsFalse(Regex.IsMatch(" video:0", streamPattern));
        Assert.IsFalse(Regex.IsMatch("video:0 ", streamPattern));
    }

    [TestMethod]
    public void SourceSpanSchemaUsesIntegerRationalRangeWithPositiveDuration()
    {
        using JsonDocument document = LoadSchema(SourceSpanSchemaRelativePath);
        JsonElement range = document.RootElement
            .GetProperty("properties")
            .GetProperty("range");

        AssertAdditionalPropertiesFalse(range);
        CollectionAssert.AreEquivalent(
            new[] { "start", "duration" },
            GetStringArray(range.GetProperty("required")));

        JsonElement rangeProperties = range.GetProperty("properties");
        AssertRationalShape(rangeProperties.GetProperty("start"), requirePositiveValue: false);
        AssertRationalShape(rangeProperties.GetProperty("duration"), requirePositiveValue: true);
    }

    [TestMethod]
    public void SourceSpanSchemaMakesNonEmptyTokenIdsUniqueAndRevisionDependent()
    {
        using JsonDocument document = LoadSchema(SourceSpanSchemaRelativePath);
        JsonElement root = document.RootElement;
        JsonElement tokenIds = root.GetProperty("properties").GetProperty("transcriptTokenIds");

        Assert.AreEqual("array", tokenIds.GetProperty("type").GetString());
        Assert.IsTrue(tokenIds.GetProperty("uniqueItems").GetBoolean());
        AssertCanonicalUuidPattern(tokenIds.GetProperty("items"));
        JsonElement condition = root.GetProperty("allOf")[0];
        JsonElement ifSchema = condition.GetProperty("if");
        CollectionAssert.AreEqual(
            new[] { "transcriptTokenIds" },
            GetStringArray(ifSchema.GetProperty("required")));
        Assert.AreEqual(
            1,
            ifSchema
                .GetProperty("properties")
                .GetProperty("transcriptTokenIds")
                .GetProperty("minItems")
                .GetInt32());
        CollectionAssert.AreEqual(
            new[] { "transcriptRevisionId" },
            GetStringArray(condition.GetProperty("then").GetProperty("required")));
    }

    [TestMethod]
    public void OriginRefSchemaHasExactlyThreeConstKindShapes()
    {
        using JsonDocument document = LoadSchema(OriginRefSchemaRelativePath);
        JsonElement root = document.RootElement;
        AssertAdditionalPropertiesFalse(root);

        string[] expectedKinds = ["sourceSpan", "asset", "editorialRecord"];
        string[] expectedIdProperties = ["sourceSpanId", "assetId", "editorialRecordId"];
        CollectionAssert.AreEqual(
            expectedKinds,
            GetStringArray(root.GetProperty("properties").GetProperty("kind").GetProperty("enum")));

        JsonElement[] variants = root.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.AreEqual(3, variants.Length);

        for (int index = 0; index < variants.Length; index++)
        {
            JsonElement variant = variants[index];
            string idProperty = expectedIdProperties[index];

            Assert.AreEqual("object", variant.GetProperty("type").GetString());
            AssertAdditionalPropertiesFalse(variant);
            CollectionAssert.AreEquivalent(
                new[] { "kind", idProperty },
                GetStringArray(variant.GetProperty("required")));

            JsonElement properties = variant.GetProperty("properties");
            Assert.AreEqual(2, properties.EnumerateObject().Count());
            Assert.AreEqual(
                expectedKinds[index],
                properties.GetProperty("kind").GetProperty("const").GetString());
            AssertCanonicalUuidPattern(properties.GetProperty(idProperty));
        }
    }

    private static void AssertRationalShape(JsonElement rational, bool requirePositiveValue)
    {
        Assert.AreEqual("object", rational.GetProperty("type").GetString());
        AssertAdditionalPropertiesFalse(rational);
        CollectionAssert.AreEquivalent(
            new[] { "value", "rate" },
            GetStringArray(rational.GetProperty("required")));

        JsonElement properties = rational.GetProperty("properties");
        JsonElement value = properties.GetProperty("value");
        JsonElement rate = properties.GetProperty("rate");
        Assert.AreEqual("integer", value.GetProperty("type").GetString());
        Assert.AreEqual("integer", rate.GetProperty("type").GetString());
        Assert.AreEqual(1L, rate.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, rate.GetProperty("maximum").GetInt64());

        if (requirePositiveValue)
        {
            Assert.AreEqual(1L, value.GetProperty("minimum").GetInt64());
        }
        else
        {
            Assert.AreEqual(long.MinValue, value.GetProperty("minimum").GetInt64());
        }

        Assert.AreEqual(long.MaxValue, value.GetProperty("maximum").GetInt64());
    }

    private static void AssertCanonicalUuidPattern(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        string pattern = GetPattern(schema);

        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(EmptyUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseUuid.Replace("-", string.Empty), pattern));
    }

    private static void AssertAdditionalPropertiesFalse(JsonElement schema)
    {
        Assert.AreEqual(
            JsonValueKind.False,
            schema.GetProperty("additionalProperties").ValueKind);
    }

    private static string GetPattern(JsonElement schema)
    {
        return schema.GetProperty("pattern").GetString()
            ?? throw new InvalidOperationException("Schema pattern must be a string.");
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
