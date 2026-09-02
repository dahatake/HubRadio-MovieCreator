using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class DefaultSettingsSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string ProfileSchemaPath = "schemas/default-settings-profile.schema.json";
    private const string SnapshotSchemaPath = "schemas/default-settings-snapshot.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemasAreClosedDraft202012CanonicalContracts()
    {
        using JsonDocument profile = LoadSchema(ProfileSchemaPath);
        using JsonDocument snapshot = LoadSchema(SnapshotSchemaPath);

        AssertRoot(profile.RootElement, "DefaultSettingsProfile");
        AssertRoot(snapshot.RootElement, "DefaultSettingsSnapshot");
        StringAssert.Contains(profile.RootElement.GetProperty("$comment").GetString(), "contentDigest");
        StringAssert.Contains(snapshot.RootElement.GetProperty("$comment").GetString(), "supplied profile");
    }

    [TestMethod]
    public void ProfileRequiresIdentityVersionDigestAndDefinitionArrayOnly()
    {
        using JsonDocument document = LoadSchema(ProfileSchemaPath);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "schemaVersion", "id", "name", "version", "contentDigest", "settings" },
            GetStringArray(root.GetProperty("required")));
        Assert.AreEqual(6, root.GetProperty("properties").EnumerateObject().Count());
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetString());

        JsonElement settings = root.GetProperty("properties").GetProperty("settings");
        Assert.AreEqual("array", settings.GetProperty("type").GetString());
        Assert.AreEqual(1, settings.GetProperty("minItems").GetInt32());
        StringAssert.Contains(settings.GetProperty("$comment").GetString(), "duplicate keys");
    }

    [TestMethod]
    public void DefinitionSchemaHasFiveGroupsClosedUnitsTypedValuesAndEvidence()
    {
        using JsonDocument document = LoadSchema(ProfileSchemaPath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement setting = definitions.GetProperty("settingDefinition");
        AssertAdditionalPropertiesFalse(setting);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "key",
                "group",
                "isAdvanced",
                "displayName",
                "description",
                "unit",
                "defaultValue",
                "minimum",
                "maximum",
                "allowedTextValues",
                "evidence",
            },
            GetStringArray(setting.GetProperty("required")));
        CollectionAssert.AreEqual(
            new[]
            {
                "projectAndOutput",
                "editing",
                "subtitlesAndGraphics",
                "audioAndAssets",
                "privacyAndStorage",
            },
            GetStringArray(definitions.GetProperty("settingGroup").GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[]
            {
                "none",
                "count",
                "milliseconds",
                "seconds",
                "frames",
                "pixels",
                "hertz",
                "bitsPerSecond",
                "percent",
                "charactersPerSecond",
                "fullWidthCharacters",
                "decibels",
                "decibelsFullScale",
                "loudnessUnitsFullScale",
            },
            GetStringArray(definitions.GetProperty("settingUnit").GetProperty("enum")));
        Assert.AreEqual(5, definitions.GetProperty("settingValue").GetProperty("oneOf").GetArrayLength());
        Assert.AreEqual(4, definitions.GetProperty("evidence").GetProperty("oneOf").GetArrayLength());
        Assert.AreEqual(
            1,
            setting.GetProperty("properties").GetProperty("evidence").GetProperty("minItems").GetInt32());
        Assert.IsTrue(
            setting.GetProperty("properties").GetProperty("evidence").GetProperty("uniqueItems").GetBoolean());
    }

    [TestMethod]
    public void ValueAndEvidenceVariantsAreClosedAndCorrelateKindsWithSourceIds()
    {
        using JsonDocument document = LoadSchema(ProfileSchemaPath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        foreach (string valueName in new[]
        {
            "booleanValue",
            "integerValue",
            "rationalValue",
            "textValue",
            "textListValue",
        })
        {
            AssertAdditionalPropertiesFalse(definitions.GetProperty(valueName));
        }

        AssertAdditionalPropertiesFalse(definitions.GetProperty("productDecisionEvidence"));
        AssertAdditionalPropertiesFalse(definitions.GetProperty("userDecisionEvidence"));
        AssertAdditionalPropertiesFalse(definitions.GetProperty("currentImplementationEvidence"));
        AssertAdditionalPropertiesFalse(definitions.GetProperty("externalEvidence"));
        Assert.AreEqual(
            "productDecision",
            definitions.GetProperty("productDecisionEvidence")
                .GetProperty("properties")
                .GetProperty("sourceId")
                .GetProperty("const")
                .GetString());
        AssertPatternAcceptsOnly(
            definitions.GetProperty("userDecisionEvidence").GetProperty("properties").GetProperty("sourceId"),
            "U01",
            "R01");
        AssertPatternAcceptsOnly(
            definitions.GetProperty("currentImplementationEvidence").GetProperty("properties").GetProperty("sourceId"),
            "C05",
            "U05");
        AssertPatternAcceptsOnly(
            definitions.GetProperty("externalEvidence").GetProperty("properties").GetProperty("sourceId"),
            "R26",
            "C26");
    }

    [TestMethod]
    public void CanonicalPatternsRejectUppercaseIdentityDigestAndNonCanonicalVersions()
    {
        using JsonDocument document = LoadSchema(ProfileSchemaPath);
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        AssertPatternAcceptsOnly(
            definitions.GetProperty("entityId"),
            LowercaseUuid,
            LowercaseUuid.ToUpperInvariant());
        AssertPatternAcceptsOnly(
            definitions.GetProperty("contentDigest"),
            LowercaseDigest,
            LowercaseDigest.ToUpperInvariant());
        string semanticVersionPattern = GetPattern(definitions.GetProperty("semanticVersion"));
        Assert.IsTrue(Regex.IsMatch("1.0.0", semanticVersionPattern));
        Assert.IsTrue(Regex.IsMatch("0.12.300", semanticVersionPattern));
        Assert.IsFalse(Regex.IsMatch("01.0.0", semanticVersionPattern));
        Assert.IsFalse(Regex.IsMatch("1.0", semanticVersionPattern));
        Assert.IsFalse(Regex.IsMatch("1.0.0-alpha", semanticVersionPattern));
        AssertPatternAcceptsOnly(
            definitions.GetProperty("settingKey"),
            "subtitles.cueMinimum",
            "Subtitles.cueMinimum");
    }

    [TestMethod]
    public void SnapshotRequiresOnlyExactProfileBindingDigestAndTypedValues()
    {
        using JsonDocument document = LoadSchema(SnapshotSchemaPath);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "profileId",
                "profileVersion",
                "profileDigest",
                "contentDigest",
                "values",
            },
            GetStringArray(root.GetProperty("required")));
        Assert.AreEqual(6, root.GetProperty("properties").EnumerateObject().Count());
        JsonElement values = root.GetProperty("properties").GetProperty("values");
        Assert.AreEqual(1, values.GetProperty("minItems").GetInt32());
        StringAssert.Contains(values.GetProperty("$comment").GetString(), "duplicate");
        JsonElement selection = root.GetProperty("$defs").GetProperty("selection");
        AssertAdditionalPropertiesFalse(selection);
        CollectionAssert.AreEquivalent(
            new[] { "key", "value" },
            GetStringArray(selection.GetProperty("required")));
    }

    [TestMethod]
    public void SchemasUseIntegerOrRationalValuesAndContainNoRuntimeOrPersistenceState()
    {
        using JsonDocument profile = LoadSchema(ProfileSchemaPath);
        using JsonDocument snapshot = LoadSchema(SnapshotSchemaPath);

        AssertNoFloatingNumberType(profile.RootElement);
        AssertNoFloatingNumberType(snapshot.RootElement);
        foreach (JsonElement root in new[] { profile.RootElement, snapshot.RootElement })
        {
            JsonElement properties = root.GetProperty("properties");
            foreach (string prohibited in new[]
            {
                "currentValue",
                "status",
                "result",
                "actorId",
                "startedAt",
                "completedAt",
                "databaseId",
                "outputPath",
            })
            {
                Assert.IsFalse(properties.TryGetProperty(prohibited, out _));
            }
        }
    }

    private static void AssertRoot(JsonElement root, string title)
    {
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual(Draft202012, root.GetProperty("$schema").GetString());
        Assert.AreEqual(title, root.GetProperty("title").GetString());
        AssertAdditionalPropertiesFalse(root);
    }

    private static void AssertPatternAcceptsOnly(
        JsonElement schema,
        string accepted,
        string rejected)
    {
        string pattern = GetPattern(schema);
        Assert.IsTrue(Regex.IsMatch(accepted, pattern), accepted);
        Assert.IsFalse(Regex.IsMatch(rejected, pattern), rejected);
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
        return array.EnumerateArray().Select(static item =>
            item.GetString()
            ?? throw new InvalidOperationException("Schema array item must be a string.")).ToArray();
    }

    private static JsonDocument LoadSchema(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string fullPath = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                return JsonDocument.Parse(File.ReadAllText(fullPath));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}
