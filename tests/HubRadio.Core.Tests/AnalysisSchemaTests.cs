using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class AnalysisSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";

    [TestMethod]
    [DataRow("schemas/model-run.schema.json", "ModelRun")]
    [DataRow("schemas/detection.schema.json", "Detection")]
    [DataRow("schemas/suggestion.schema.json", "Suggestion")]
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
    public void ModelRunSchemaRequiresConfigurationInputOutcomeAndAuditedLocation()
    {
        using JsonDocument document = LoadSchema("schemas/model-run.schema.json");
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "provider",
                "modelIdentifier",
                "modelVersion",
                "modelHashAvailability",
                "configurationDigest",
                "inputDigest",
                "outcome",
                "executionLocation",
                "startedAt",
                "completedAt",
            },
            GetStringArray(root.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "provider",
                "modelIdentifier",
                "modelVersion",
                "modelHash",
                "modelHashAvailability",
                "configurationDigest",
                "inputDigest",
                "outputDigest",
                "outcome",
                "errorCode",
                "executionLocation",
                "destination",
                "region",
                "sentDataClassification",
                "retentionPolicy",
                "startedAt",
                "completedAt",
            },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        foreach (string digestName in new[] { "configurationDigest", "inputDigest" })
        {
            Assert.AreEqual(
                "#/$defs/contentDigest",
                properties.GetProperty(digestName).GetProperty("$ref").GetString());
        }

        string digestPattern = GetRequiredString(
            root.GetProperty("$defs").GetProperty("contentDigest"),
            "pattern");
        Assert.IsTrue(Regex.IsMatch(new string('a', 64), digestPattern));
        Assert.IsFalse(Regex.IsMatch(new string('A', 64), digestPattern));
        Assert.IsFalse(Regex.IsMatch(new string('a', 63), digestPattern));

        CollectionAssert.AreEqual(
            new[] { "local", "cloud" },
            GetStringArray(properties.GetProperty("executionLocation").GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[] { "available", "notDisclosed" },
            GetStringArray(properties.GetProperty("modelHashAvailability").GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[] { "succeeded", "failed", "timedOut", "canceled" },
            GetStringArray(properties.GetProperty("outcome").GetProperty("enum")));
    }

    [TestMethod]
    public void ModelRunCloudRuleRequiresTransmissionMetadataAndLocalRuleForbidsClaims()
    {
        using JsonDocument document = LoadSchema("schemas/model-run.schema.json");
        JsonElement rule = document.RootElement.GetProperty("allOf")[1];

        Assert.AreEqual(
            "cloud",
            rule.GetProperty("if")
                .GetProperty("properties")
                .GetProperty("executionLocation")
                .GetProperty("const")
                .GetString());
        string[] cloudFields =
        [
            "destination",
            "region",
            "sentDataClassification",
            "retentionPolicy",
        ];
        CollectionAssert.AreEquivalent(
            cloudFields,
            GetStringArray(rule.GetProperty("then").GetProperty("required")));
        foreach (string field in cloudFields)
        {
            Assert.AreEqual(
                "#/$defs/nonBlankText",
                rule.GetProperty("then").GetProperty("properties").GetProperty(field).GetProperty("$ref").GetString());
            Assert.AreEqual(
                "null",
                rule.GetProperty("else").GetProperty("properties").GetProperty(field).GetProperty("type").GetString());
        }

        Assert.AreEqual(
            "available",
            rule.GetProperty("else")
                .GetProperty("properties")
                .GetProperty("modelHashAvailability")
                .GetProperty("const")
                .GetString());
    }

    [TestMethod]
    public void ModelRunHashRuleAllowsExplicitCloudNondisclosureWithoutFabrication()
    {
        using JsonDocument document = LoadSchema("schemas/model-run.schema.json");
        JsonElement rule = document.RootElement.GetProperty("allOf")[0];

        Assert.AreEqual(
            "available",
            rule.GetProperty("if")
                .GetProperty("properties")
                .GetProperty("modelHashAvailability")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "modelHash" },
            GetStringArray(rule.GetProperty("then").GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/contentDigest",
            rule.GetProperty("then").GetProperty("properties").GetProperty("modelHash").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "null",
            rule.GetProperty("else").GetProperty("properties").GetProperty("modelHash").GetProperty("type").GetString());
    }

    [TestMethod]
    public void ModelRunOutcomeRuleSeparatesSuccessfulOutputFromStableFailureCode()
    {
        using JsonDocument document = LoadSchema("schemas/model-run.schema.json");
        JsonElement root = document.RootElement;
        JsonElement rule = root.GetProperty("allOf")[2];

        Assert.AreEqual(
            "succeeded",
            rule.GetProperty("if")
                .GetProperty("properties")
                .GetProperty("outcome")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "outputDigest" },
            GetStringArray(rule.GetProperty("then").GetProperty("required")));
        Assert.AreEqual(
            "null",
            rule.GetProperty("then").GetProperty("properties").GetProperty("errorCode").GetProperty("type").GetString());
        CollectionAssert.AreEqual(
            new[] { "errorCode" },
            GetStringArray(rule.GetProperty("else").GetProperty("required")));
        Assert.AreEqual(
            "null",
            rule.GetProperty("else").GetProperty("properties").GetProperty("outputDigest").GetProperty("type").GetString());

        string errorCodePattern = GetRequiredString(
            root.GetProperty("$defs").GetProperty("errorCode"),
            "pattern");
        Assert.IsTrue(Regex.IsMatch("model.run_failed", errorCodePattern));
        Assert.IsFalse(Regex.IsMatch("MODEL.RUN_FAILED", errorCodePattern));
        Assert.IsFalse(Regex.IsMatch("model", errorCodePattern));
    }

    [TestMethod]
    public void ModelRunTimestampsRequireExplicitBoundedOffsets()
    {
        using JsonDocument document = LoadSchema("schemas/model-run.schema.json");
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement timestamp = definitions.GetProperty("dateTimeOffset");
        string pattern = GetRequiredString(timestamp, "pattern");

        Assert.AreEqual("date-time", timestamp.GetProperty("format").GetString());
        Assert.IsTrue(Regex.IsMatch("2026-08-29T00:00:00Z", pattern));
        Assert.IsTrue(Regex.IsMatch("2026-08-29T09:00:00.1234567+09:00", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-29T00:00:00", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-99-99T00:00:00Z", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-29T24:00:00Z", pattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-29T00:00:00+15:00", pattern));

        const string calendarInvalid = "2026-02-31T10:00:00Z";
        Assert.IsTrue(
            Regex.IsMatch(calendarInvalid, pattern),
            "The field-bounding regex intentionally does not claim Gregorian calendar validation.");
        Assert.IsFalse(DateTimeOffset.TryParse(
            calendarInvalid,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _));
        StringAssert.Contains(GetRequiredString(timestamp, "$comment"), "runtime deserialization");
        StringAssert.Contains(GetRequiredString(timestamp, "$comment"), "calendar-invalid");
    }

    [TestMethod]
    public void DetectionSchemaUsesClosedKindsModelRunAndSourceSpanOriginOnly()
    {
        using JsonDocument document = LoadSchema("schemas/detection.schema.json");
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        CollectionAssert.AreEquivalent(
            new[] { "id", "modelRunId", "kind", "origin", "description" },
            GetStringArray(root.GetProperty("required")));
        CollectionAssert.AreEqual(
            new[]
            {
                "silence",
                "repetition",
                "preparationConversation",
                "recordingProblem",
                "contextDeviation",
                "audioEvent",
                "editorialInstruction",
                "topicBoundary",
            },
            GetStringArray(properties.GetProperty("kind").GetProperty("enum")));
        Assert.AreEqual(
            "#/$defs/sourceSpanOrigin",
            properties.GetProperty("origin").GetProperty("$ref").GetString());
        AssertSourceSpanIsTheOnlyOrigin(root.GetProperty("$defs"));
        AssertNullableConfidence(root.GetProperty("$defs"));
    }

    [TestMethod]
    public void SuggestionSchemaKeepsFourStatesReasonsContextWarningsAndReferenceShape()
    {
        using JsonDocument document = LoadSchema("schemas/suggestion.schema.json");
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");
        JsonElement definitions = root.GetProperty("$defs");

        CollectionAssert.AreEqual(
            new[] { "proposed", "reviewed", "rejected", "adopted" },
            GetStringArray(properties.GetProperty("status").GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[]
            {
                "deleteRange",
                "topicBoundary",
                "highlight",
                "termCorrection",
            },
            GetStringArray(properties.GetProperty("kind").GetProperty("enum")));
        foreach (string field in new[] { "reason", "missingContext", "warning" })
        {
            Assert.IsTrue(properties.TryGetProperty(field, out _));
        }

        JsonElement reference = definitions.GetProperty("suggestionReference");
        AssertAdditionalPropertiesFalse(reference);
        CollectionAssert.AreEquivalent(
            new[] { "suggestionId", "origin" },
            GetStringArray(reference.GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/entityId",
            reference.GetProperty("properties").GetProperty("suggestionId").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/sourceSpanOrigin",
            reference.GetProperty("properties").GetProperty("origin").GetProperty("$ref").GetString());
        StringAssert.Contains(
            GetRequiredString(reference, "$comment"),
            "adopted composition-producing Suggestion");
        StringAssert.Contains(GetRequiredString(reference, "$comment"), "term corrections");
        AssertSourceSpanIsTheOnlyOrigin(definitions);
        AssertNullableConfidence(definitions);

        string comment = GetRequiredString(root, "$comment");
        StringAssert.Contains(comment, "immutable audit records");
        StringAssert.Contains(comment, "human transcript revision");
        StringAssert.Contains(comment, "Suggestion ID and OriginRef");
        StringAssert.Contains(comment, "does not copy or convert");
    }

    [TestMethod]
    public void AnalysisSchemasDoNotStorePromptTranscriptPathOrAdoptionAsEditorialRecord()
    {
        foreach (string path in new[]
        {
            "schemas/model-run.schema.json",
            "schemas/detection.schema.json",
            "schemas/suggestion.schema.json",
        })
        {
            using JsonDocument document = LoadSchema(path);
            string schema = document.RootElement.GetRawText();
            foreach (string prohibited in new[]
            {
                "promptText",
                "transcriptText",
                "filePath",
                "adoptedByEditorialRecordId",
                "providerApiKey",
            })
            {
                Assert.IsFalse(
                    schema.Contains($"\"{prohibited}\"", StringComparison.Ordinal),
                    $"{path} must not contain prohibited property '{prohibited}'.");
            }
        }
    }

    private static void AssertSourceSpanIsTheOnlyOrigin(JsonElement definitions)
    {
        JsonElement origin = definitions.GetProperty("sourceSpanOrigin");
        AssertAdditionalPropertiesFalse(origin);
        CollectionAssert.AreEquivalent(
            new[] { "kind", "sourceSpanId" },
            GetStringArray(origin.GetProperty("required")));
        Assert.AreEqual(
            "sourceSpan",
            origin.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString());
        Assert.IsFalse(definitions.TryGetProperty("assetOrigin", out _));
        Assert.IsFalse(definitions.TryGetProperty("editorialRecordOrigin", out _));
    }

    private static void AssertNullableConfidence(JsonElement definitions)
    {
        JsonElement confidence = definitions.GetProperty("confidence");
        Assert.AreEqual(0d, confidence.GetProperty("minimum").GetDouble());
        Assert.AreEqual(1d, confidence.GetProperty("maximum").GetDouble());

        JsonElement[] variants = definitions
            .GetProperty("nullableConfidence")
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual("#/$defs/confidence", variants[0].GetProperty("$ref").GetString());
        Assert.AreEqual("null", variants[1].GetProperty("type").GetString());
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