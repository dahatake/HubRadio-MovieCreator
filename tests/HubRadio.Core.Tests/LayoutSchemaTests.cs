using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class LayoutSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string LayoutSchemaRelativePath = "schemas/layout-spec.schema.json";
    private const string RenderProfileSchemaRelativePath = "schemas/render-profile.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemaFilesParseAsDraft202012Objects()
    {
        using JsonDocument layout = LoadSchema(LayoutSchemaRelativePath);
        using JsonDocument profile = LoadSchema(RenderProfileSchemaRelativePath);

        Assert.AreEqual(JsonValueKind.Object, layout.RootElement.ValueKind);
        Assert.AreEqual(JsonValueKind.Object, profile.RootElement.ValueKind);
        Assert.AreEqual(Draft202012, layout.RootElement.GetProperty("$schema").GetString());
        Assert.AreEqual(Draft202012, profile.RootElement.GetProperty("$schema").GetString());
        AssertAdditionalPropertiesFalse(layout.RootElement);
        AssertAdditionalPropertiesFalse(profile.RootElement);
    }

    [TestMethod]
    public void LayoutSpecSchemaRequiresOnlyTheImmutableLayoutAggregateShape()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement root = document.RootElement;

        CollectionAssert.AreEquivalent(
            new[] { "id", "outputAspectWidth", "outputAspectHeight", "regions", "safeZones" },
            GetStringArray(root.GetProperty("required")));

        JsonElement properties = root.GetProperty("properties");
        Assert.AreEqual(5, properties.EnumerateObject().Count());
        AssertPositiveInt32(properties.GetProperty("outputAspectWidth"));
        AssertPositiveInt32(properties.GetProperty("outputAspectHeight"));

        JsonElement regions = properties.GetProperty("regions");
        JsonElement safeZones = properties.GetProperty("safeZones");
        Assert.AreEqual("array", regions.GetProperty("type").GetString());
        Assert.AreEqual("array", safeZones.GetProperty("type").GetString());
        Assert.IsTrue(regions.GetProperty("uniqueItems").GetBoolean());
        Assert.IsTrue(safeZones.GetProperty("uniqueItems").GetBoolean());
        StringAssert.Contains(regions.GetProperty("$comment").GetString(), "runtime validation");
        StringAssert.Contains(safeZones.GetProperty("$comment").GetString(), "runtime validation");
        Assert.AreEqual("#/$defs/layoutRegion", regions.GetProperty("items").GetProperty("$ref").GetString());
        Assert.AreEqual("#/$defs/safeZone", safeZones.GetProperty("items").GetProperty("$ref").GetString());
    }

    [TestMethod]
    public void LayoutSchemaUsesCanonicalLowercaseIdentityAndDigestPatterns()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        AssertCanonicalUuidPattern(definitions.GetProperty("entityId"));
        AssertCanonicalDigestPattern(definitions.GetProperty("contentDigest"));
        AssertTrimmedNonBlankPattern(definitions.GetProperty("nonBlankText"));
    }

    [TestMethod]
    public void NormalizedRectSchemaUsesBoundedIntegerRationalsOnly()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement coordinate = definitions.GetProperty("normalizedCoordinate");
        JsonElement extent = definitions.GetProperty("positiveNormalizedExtent");
        JsonElement rect = definitions.GetProperty("normalizedRect");

        StringAssert.Contains(coordinate.GetProperty("$comment").GetString(), "runtime validation");
        StringAssert.Contains(extent.GetProperty("$comment").GetString(), "runtime validation");
        StringAssert.Contains(rect.GetProperty("$comment").GetString(), "BigInteger");

        AssertRationalShape(coordinate, minimumNumerator: 0);
        AssertRationalShape(extent, minimumNumerator: 1);
        AssertAdditionalPropertiesFalse(rect);
        CollectionAssert.AreEquivalent(
            new[] { "x", "y", "width", "height" },
            GetStringArray(rect.GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/normalizedCoordinate",
            rect.GetProperty("properties").GetProperty("x").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/normalizedCoordinate",
            rect.GetProperty("properties").GetProperty("y").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/positiveNormalizedExtent",
            rect.GetProperty("properties").GetProperty("width").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/positiveNormalizedExtent",
            rect.GetProperty("properties").GetProperty("height").GetProperty("$ref").GetString());
    }

    [TestMethod]
    public void RegionAndSafeZoneSchemasHaveClosedNamedShapesAndExactRoles()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement region = definitions.GetProperty("layoutRegion");
        JsonElement safeZone = definitions.GetProperty("safeZone");

        AssertAdditionalPropertiesFalse(region);
        AssertAdditionalPropertiesFalse(safeZone);
        CollectionAssert.AreEquivalent(
            new[] { "name", "rect", "role" },
            GetStringArray(region.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "name", "rect" },
            GetStringArray(safeZone.GetProperty("required")));
        CollectionAssert.AreEqual(
            new[] { "video", "material", "subtitle", "graphic" },
            GetStringArray(region.GetProperty("properties").GetProperty("role").GetProperty("enum")));
    }

    [TestMethod]
    public void CueSchemaHasOnlyTimingKindAndOptionalVisualRegionAssociation()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement cue = definitions.GetProperty("cue");
        JsonElement properties = cue.GetProperty("properties");

        AssertAdditionalPropertiesFalse(cue);
        CollectionAssert.AreEquivalent(
            new[] { "id", "kind", "range" },
            GetStringArray(cue.GetProperty("required")));
        CollectionAssert.AreEqual(
            new[] { "subtitle", "graphic", "audio" },
            GetStringArray(properties.GetProperty("kind").GetProperty("enum")));
        Assert.IsTrue(properties.TryGetProperty("regionName", out _));
        Assert.IsFalse(properties.TryGetProperty("text", out _));
        Assert.IsFalse(properties.TryGetProperty("audio", out _));
        Assert.IsFalse(properties.TryGetProperty("payload", out _));

        JsonElement range = definitions.GetProperty("outputTimeRange");
        StringAssert.Contains(range.GetProperty("$comment").GetString(), "runtime validation");
        AssertAdditionalPropertiesFalse(range);
        Assert.AreEqual(
            "#/$defs/nonNegativeTime",
            range.GetProperty("properties").GetProperty("start").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/positiveTime",
            range.GetProperty("properties").GetProperty("duration").GetProperty("$ref").GetString());
        AssertTimeRationalShape(definitions.GetProperty("nonNegativeTime"), minimumValue: 0);
        AssertTimeRationalShape(definitions.GetProperty("positiveTime"), minimumValue: 1);

        JsonElement condition = cue.GetProperty("allOf")[0];
        Assert.AreEqual(
            "audio",
            condition.GetProperty("if").GetProperty("properties").GetProperty("kind").GetProperty("const").GetString());
        CollectionAssert.AreEqual(
            new[] { "regionName" },
            GetStringArray(condition.GetProperty("then").GetProperty("not").GetProperty("required")));
    }

    [TestMethod]
    public void TemplateReferenceSchemaContainsOnlyIdNameAndVersionDigest()
    {
        using JsonDocument document = LoadSchema(LayoutSchemaRelativePath);
        JsonElement template = document.RootElement.GetProperty("$defs").GetProperty("templateReference");

        AssertAdditionalPropertiesFalse(template);
        CollectionAssert.AreEquivalent(
            new[] { "id", "name", "versionDigest" },
            GetStringArray(template.GetProperty("required")));
        Assert.AreEqual(3, template.GetProperty("properties").EnumerateObject().Count());
    }

    [TestMethod]
    public void RenderProfileSchemaRequiresVersionedOutputSpecificationOnly()
    {
        using JsonDocument document = LoadSchema(RenderProfileSchemaRelativePath);
        JsonElement root = document.RootElement;
        string[] required = GetStringArray(root.GetProperty("required"));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "name",
                "profileDigest",
                "width",
                "height",
                "frameRate",
                "layoutSpecId",
                "templateId",
                "templateVersionDigest",
                "videoCodec",
                "audioCodec",
                "subtitleBurnIn",
            },
            required);

        JsonElement properties = root.GetProperty("properties");
        AssertPositiveInt32(properties.GetProperty("width"));
        AssertPositiveInt32(properties.GetProperty("height"));
        Assert.AreEqual("boolean", properties.GetProperty("subtitleBurnIn").GetProperty("type").GetString());

        JsonElement frameRate = properties.GetProperty("frameRate");
        StringAssert.Contains(frameRate.GetProperty("$comment").GetString(), "D-012");
        AssertAdditionalPropertiesFalse(frameRate);
        CollectionAssert.AreEquivalent(
            new[] { "numerator", "denominator" },
            GetStringArray(frameRate.GetProperty("required")));
        JsonElement frameRateProperties = frameRate.GetProperty("properties");
        AssertPositiveInt64(frameRateProperties.GetProperty("numerator"));
        AssertPositiveInt64(frameRateProperties.GetProperty("denominator"));
    }

    [TestMethod]
    public void RenderProfileSchemaUsesCanonicalReferencesAndTrimmedCodecNames()
    {
        using JsonDocument document = LoadSchema(RenderProfileSchemaRelativePath);
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        JsonElement properties = root.GetProperty("properties");

        AssertCanonicalUuidPattern(definitions.GetProperty("entityId"));
        AssertCanonicalDigestPattern(definitions.GetProperty("contentDigest"));
        AssertTrimmedNonBlankPattern(definitions.GetProperty("nonBlankText"));

        foreach (string propertyName in new[] { "id", "layoutSpecId", "templateId" })
        {
            Assert.AreEqual(
                "#/$defs/entityId",
                properties.GetProperty(propertyName).GetProperty("$ref").GetString());
        }

        foreach (string propertyName in new[] { "profileDigest", "templateVersionDigest" })
        {
            Assert.AreEqual(
                "#/$defs/contentDigest",
                properties.GetProperty(propertyName).GetProperty("$ref").GetString());
        }

        foreach (string propertyName in new[] { "name", "videoCodec", "audioCodec" })
        {
            Assert.AreEqual(
                "#/$defs/nonBlankText",
                properties.GetProperty(propertyName).GetProperty("$ref").GetString());
        }
    }

    [TestMethod]
    public void RenderProfileSchemaContainsNoExecutionResultOrProductionThresholdFields()
    {
        using JsonDocument document = LoadSchema(RenderProfileSchemaRelativePath);
        JsonElement properties = document.RootElement.GetProperty("properties");
        string[] prohibitedProperties =
        [
            "status",
            "result",
            "manifest",
            "startedAt",
            "completedAt",
            "outputPath",
            "exitCode",
            "productionThreshold",
            "productionApproved",
        ];

        foreach (string propertyName in prohibitedProperties)
        {
            Assert.IsFalse(properties.TryGetProperty(propertyName, out _));
        }
    }

    private static void AssertRationalShape(JsonElement rational, long minimumNumerator)
    {
        Assert.AreEqual("object", rational.GetProperty("type").GetString());
        AssertAdditionalPropertiesFalse(rational);
        CollectionAssert.AreEquivalent(
            new[] { "numerator", "denominator" },
            GetStringArray(rational.GetProperty("required")));

        JsonElement properties = rational.GetProperty("properties");
        JsonElement numerator = properties.GetProperty("numerator");
        JsonElement denominator = properties.GetProperty("denominator");
        Assert.AreEqual("integer", numerator.GetProperty("type").GetString());
        Assert.AreEqual(minimumNumerator, numerator.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, numerator.GetProperty("maximum").GetInt64());
        AssertPositiveInt64(denominator);
    }

    private static void AssertTimeRationalShape(JsonElement rational, long minimumValue)
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
        Assert.AreEqual(minimumValue, value.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, value.GetProperty("maximum").GetInt64());
        AssertPositiveInt64(rate);
    }

    private static void AssertPositiveInt32(JsonElement schema)
    {
        Assert.AreEqual("integer", schema.GetProperty("type").GetString());
        Assert.AreEqual(1, schema.GetProperty("minimum").GetInt32());
        Assert.AreEqual(int.MaxValue, schema.GetProperty("maximum").GetInt32());
    }

    private static void AssertPositiveInt64(JsonElement schema)
    {
        Assert.AreEqual("integer", schema.GetProperty("type").GetString());
        Assert.AreEqual(1L, schema.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, schema.GetProperty("maximum").GetInt64());
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

    private static void AssertCanonicalDigestPattern(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        string pattern = GetPattern(schema);

        Assert.IsTrue(Regex.IsMatch(LowercaseDigest, pattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest.ToUpperInvariant(), pattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest[..^1], pattern));
    }

    private static void AssertTrimmedNonBlankPattern(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        Assert.AreEqual(1, schema.GetProperty("minLength").GetInt32());
        string pattern = GetPattern(schema);

        Assert.IsTrue(Regex.IsMatch("subtitle-main", pattern));
        Assert.IsFalse(Regex.IsMatch(string.Empty, pattern));
        Assert.IsFalse(Regex.IsMatch(" subtitle-main", pattern));
        Assert.IsFalse(Regex.IsMatch("subtitle-main ", pattern));
    }

    private static void AssertAdditionalPropertiesFalse(JsonElement schema)
    {
        Assert.AreEqual(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
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