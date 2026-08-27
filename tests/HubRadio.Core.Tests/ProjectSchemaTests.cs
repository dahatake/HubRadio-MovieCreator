using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProjectSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string SchemaRelativePath = "schemas/project.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemaParsesAsDraft202012FromItsRepositoryRelativePath()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual(Draft202012, root.GetProperty("$schema").GetString());
        Assert.AreEqual("Project", root.GetProperty("title").GetString());
        StringAssert.Contains(root.GetProperty("$comment").GetString(), "canonical persisted");
        StringAssert.Contains(root.GetProperty("$comment").GetString(), "explicit null");
    }

    [TestMethod]
    public void ProjectShapeRequiresAggregateFieldsAndMakesDeadlineOptionalNullable()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        AssertAdditionalPropertiesFalse(root);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "contentName",
                "purpose",
                "audience",
                "publicationScope",
                "assets",
                "performers",
            },
            GetStringArray(root.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "contentName",
                "purpose",
                "audience",
                "publicationScope",
                "deadline",
                "assets",
                "performers",
            },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        AssertCanonicalUuidPattern(properties.GetProperty("id"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("contentName"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("purpose"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("audience"));
        AssertNullableDateTimeShape(properties.GetProperty("deadline"));
    }

    [TestMethod]
    public void PublicationScopeContainsAllRequiredInputStates()
    {
        using JsonDocument document = LoadSchema();
        JsonElement scope = document.RootElement
            .GetProperty("properties")
            .GetProperty("publicationScope");

        Assert.AreEqual("string", scope.GetProperty("type").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "internalOnly",
                "specificGroupOnly",
                "externalCandidate",
                "externalPublicationApproved",
                "publicationForbidden",
                "pendingDecision",
            },
            GetStringArray(scope.GetProperty("enum")));
    }

    [TestMethod]
    public void AssetShapeUsesCanonicalIdentityDigestAndPositiveInt64Size()
    {
        using JsonDocument document = LoadSchema();
        JsonElement assets = document.RootElement.GetProperty("properties").GetProperty("assets");
        JsonElement asset = assets.GetProperty("items");
        JsonElement properties = asset.GetProperty("properties");

        Assert.AreEqual("array", assets.GetProperty("type").GetString());
        Assert.IsTrue(assets.GetProperty("uniqueItems").GetBoolean());
        AssertAdditionalPropertiesFalse(asset);
        CollectionAssert.AreEquivalent(
            new[] { "id", "fileName", "mediaType", "contentDigest", "byteSize", "rights" },
            GetStringArray(asset.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "id", "fileName", "mediaType", "contentDigest", "byteSize", "rights" },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        AssertCanonicalUuidPattern(properties.GetProperty("id"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("fileName"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("mediaType"));

        JsonElement digest = properties.GetProperty("contentDigest");
        Assert.AreEqual("string", digest.GetProperty("type").GetString());
        string digestPattern = GetPattern(digest);
        Assert.IsTrue(Regex.IsMatch(LowercaseDigest, digestPattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest.ToUpperInvariant(), digestPattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest[..^1], digestPattern));

        JsonElement byteSize = properties.GetProperty("byteSize");
        Assert.AreEqual("integer", byteSize.GetProperty("type").GetString());
        Assert.AreEqual(1L, byteSize.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, byteSize.GetProperty("maximum").GetInt64());
    }

    [TestMethod]
    public void RightsShapeIsFailClosedAndRequiresEvidenceForApproval()
    {
        using JsonDocument document = LoadSchema();
        JsonElement rights = GetRightsSchema(document.RootElement);
        JsonElement properties = rights.GetProperty("properties");

        AssertAdditionalPropertiesFalse(rights);
        CollectionAssert.AreEquivalent(
            new[] { "status", "allowedUse", "externalPublicationAllowed" },
            GetStringArray(rights.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "status", "evidenceReference", "allowedUse", "externalPublicationAllowed" },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        JsonElement status = properties.GetProperty("status");
        Assert.AreEqual("string", status.GetProperty("type").GetString());
        CollectionAssert.AreEqual(
            new[] { "unconfirmed", "approved", "rejected" },
            GetStringArray(status.GetProperty("enum")));
        AssertNullableTrimmedStringShape(properties.GetProperty("evidenceReference"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("allowedUse"));
        Assert.AreEqual(
            "boolean",
            properties.GetProperty("externalPublicationAllowed").GetProperty("type").GetString());

        JsonElement[] rules = rights.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.AreEqual(2, rules.Length);

        JsonElement approvedRule = rules[0];
        Assert.AreEqual(
            "approved",
            approvedRule
                .GetProperty("if")
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "evidenceReference" },
            GetStringArray(approvedRule.GetProperty("then").GetProperty("required")));
        JsonElement approvedEvidence = approvedRule
            .GetProperty("then")
            .GetProperty("properties")
            .GetProperty("evidenceReference");
        AssertTrimmedRequiredStringShape(approvedEvidence);

        JsonElement failClosedRule = rules[1];
        CollectionAssert.AreEqual(
            new[] { "unconfirmed", "rejected" },
            GetStringArray(
                failClosedRule
                    .GetProperty("if")
                    .GetProperty("properties")
                    .GetProperty("status")
                    .GetProperty("enum")));
        Assert.AreEqual(
            JsonValueKind.False,
            failClosedRule
                .GetProperty("then")
                .GetProperty("properties")
                .GetProperty("externalPublicationAllowed")
                .GetProperty("const")
                .ValueKind);
    }

    [TestMethod]
    public void PerformerShapeUsesTypedIdentityAndOptionalNullableConfirmationTime()
    {
        using JsonDocument document = LoadSchema();
        JsonElement performers = document.RootElement
            .GetProperty("properties")
            .GetProperty("performers");
        JsonElement performer = performers.GetProperty("items");
        JsonElement properties = performer.GetProperty("properties");

        Assert.AreEqual("array", performers.GetProperty("type").GetString());
        Assert.IsTrue(performers.GetProperty("uniqueItems").GetBoolean());
        AssertAdditionalPropertiesFalse(performer);
        CollectionAssert.AreEquivalent(
            new[] { "id", "displayName", "role" },
            GetStringArray(performer.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "id", "displayName", "role", "titleConfirmedAt" },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        AssertCanonicalUuidPattern(properties.GetProperty("id"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("displayName"));
        AssertTrimmedRequiredStringShape(properties.GetProperty("role"));
        AssertNullableDateTimeShape(properties.GetProperty("titleConfirmedAt"));
    }

    [TestMethod]
    public void EveryObjectRejectsUnknownPropertiesAndNoFloatingNumberTypeIsUsed()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        AssertAdditionalPropertiesFalse(root);
        AssertAdditionalPropertiesFalse(properties.GetProperty("assets").GetProperty("items"));
        AssertAdditionalPropertiesFalse(GetRightsSchema(root));
        AssertAdditionalPropertiesFalse(properties.GetProperty("performers").GetProperty("items"));
        AssertNoFloatingNumberType(root);
    }

    private static JsonElement GetRightsSchema(JsonElement root)
    {
        return root
            .GetProperty("properties")
            .GetProperty("assets")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("rights");
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

    private static void AssertTrimmedRequiredStringShape(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        Assert.AreEqual(1, schema.GetProperty("minLength").GetInt32());
        AssertTrimmedPattern(schema);
    }

    private static void AssertNullableTrimmedStringShape(JsonElement schema)
    {
        CollectionAssert.AreEquivalent(
            new[] { "string", "null" },
            GetStringArray(schema.GetProperty("type")));
        Assert.AreEqual(1, schema.GetProperty("minLength").GetInt32());
        AssertTrimmedPattern(schema);
    }

    private static void AssertNullableDateTimeShape(JsonElement schema)
    {
        CollectionAssert.AreEquivalent(
            new[] { "string", "null" },
            GetStringArray(schema.GetProperty("type")));
        Assert.AreEqual("date-time", schema.GetProperty("format").GetString());
        string pattern = GetPattern(schema);
        Assert.IsTrue(Regex.IsMatch("2026-08-28T01:23:45Z", pattern));
        Assert.IsTrue(Regex.IsMatch("2026-08-28T10:23:45.1234567+09:00", pattern));
        Assert.IsFalse(Regex.IsMatch("not-a-date", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-28", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-28T01:23:45", pattern));
    }

    private static void AssertTrimmedPattern(JsonElement schema)
    {
        string pattern = GetPattern(schema);
        Assert.IsTrue(Regex.IsMatch("value", pattern));
        Assert.IsFalse(Regex.IsMatch(string.Empty, pattern));
        Assert.IsFalse(Regex.IsMatch(" value", pattern));
        Assert.IsFalse(Regex.IsMatch("value ", pattern));
    }

    private static void AssertNoFloatingNumberType(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("type"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        Assert.AreNotEqual("number", property.Value.GetString());
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        Assert.IsFalse(
                            GetStringArray(property.Value).Contains("number", StringComparer.Ordinal));
                    }
                }

                AssertNoFloatingNumberType(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertNoFloatingNumberType(item);
            }
        }
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

    private static JsonDocument LoadSchema()
    {
        string fullPath = ResolveRepositoryPath(SchemaRelativePath);
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