using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TranscriptSchemaTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private const string SchemaRelativePath = "schemas/transcript.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string EmptyUuid = "00000000-0000-0000-0000-000000000000";

    [TestMethod]
    public void SchemaParsesAsDraft202012CanonicalTranscriptRevision()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual(Draft202012, root.GetProperty("$schema").GetString());
        Assert.AreEqual("TranscriptRevision", root.GetProperty("title").GetString());
        AssertAdditionalPropertiesFalse(root);

        string comment = GetRequiredString(root, "$comment");
        StringAssert.Contains(comment, "Canonical persisted");
        StringAssert.Contains(comment, "runtime");
        StringAssert.Contains(comment, "sourceSpanId");
        StringAssert.Contains(comment, "never be fabricated");
    }

    [TestMethod]
    public void RevisionShapeRequiresIdentityOriginTimestampAndUtterancesOnly()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement properties = root.GetProperty("properties");

        CollectionAssert.AreEquivalent(
            new[] { "id", "changeSource", "createdAt", "utterances" },
            GetStringArray(root.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "id", "parentRevisionId", "changeSource", "createdAt", "utterances" },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("#/$defs/entityId", properties.GetProperty("id").GetProperty("$ref").GetString());

        JsonElement parent = properties.GetProperty("parentRevisionId");
        JsonElement[] parentVariants = parent.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.AreEqual(2, parentVariants.Length);
        Assert.AreEqual("#/$defs/entityId", parentVariants[0].GetProperty("$ref").GetString());
        Assert.AreEqual("null", parentVariants[1].GetProperty("type").GetString());

        JsonElement changeSource = properties.GetProperty("changeSource");
        Assert.AreEqual("string", changeSource.GetProperty("type").GetString());
        CollectionAssert.AreEqual(
            new[] { "machine", "human" },
            GetStringArray(changeSource.GetProperty("enum")));

        JsonElement createdAt = properties.GetProperty("createdAt");
        Assert.AreEqual("string", createdAt.GetProperty("type").GetString());
        Assert.AreEqual("date-time", createdAt.GetProperty("format").GetString());
        string datePattern = GetRequiredString(createdAt, "pattern");
        Assert.IsTrue(Regex.IsMatch("2026-08-28T00:00:00Z", datePattern));
        Assert.IsTrue(Regex.IsMatch("2026-08-28T09:00:00.1234567+09:00", datePattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-28T00:00:00", datePattern));
        Assert.IsFalse(Regex.IsMatch("2026-99-99T00:00:00Z", datePattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-28T24:00:00Z", datePattern));
        Assert.IsFalse(Regex.IsMatch("2026-08-28T00:00:00+15:00", datePattern));
        StringAssert.Contains(GetRequiredString(createdAt, "$comment"), "DateTimeOffset");

        JsonElement utterances = properties.GetProperty("utterances");
        Assert.AreEqual("array", utterances.GetProperty("type").GetString());
        Assert.IsTrue(utterances.GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual("#/$defs/utterance", utterances.GetProperty("items").GetProperty("$ref").GetString());
        StringAssert.Contains(GetRequiredString(utterances, "$comment"), "overlapping speakers");
    }

    [TestMethod]
    public void EntityTextConfidenceAndRangeDefinitionsAreCanonicalAndBounded()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        AssertCanonicalUuidPattern(definitions.GetProperty("entityId"));
        AssertTrimmedNonBlankPattern(definitions.GetProperty("nonBlankText"));

        JsonElement confidence = definitions.GetProperty("confidence");
        Assert.AreEqual("number", confidence.GetProperty("type").GetString());
        Assert.AreEqual(0d, confidence.GetProperty("minimum").GetDouble());
        Assert.AreEqual(1d, confidence.GetProperty("maximum").GetDouble());
        StringAssert.Contains(GetRequiredString(confidence, "$comment"), "Omission or null");

        JsonElement nullableConfidence = definitions.GetProperty("nullableConfidence");
        JsonElement[] confidenceVariants = nullableConfidence
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual("#/$defs/confidence", confidenceVariants[0].GetProperty("$ref").GetString());
        Assert.AreEqual("null", confidenceVariants[1].GetProperty("type").GetString());

        JsonElement timeRange = definitions.GetProperty("timeRange");
        AssertAdditionalPropertiesFalse(timeRange);
        CollectionAssert.AreEquivalent(
            new[] { "start", "duration" },
            GetStringArray(timeRange.GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/rationalTime",
            timeRange.GetProperty("properties").GetProperty("start").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/positiveDuration",
            timeRange.GetProperty("properties").GetProperty("duration").GetProperty("$ref").GetString());
        AssertRationalShape(definitions.GetProperty("rationalTime"), long.MinValue);
        AssertRationalShape(definitions.GetProperty("positiveDuration"), 1L);
    }

    [TestMethod]
    public void UtteranceShapeLinksSourceSpanAnonymousSpeakerSentenceAndOptionalConfidence()
    {
        using JsonDocument document = LoadSchema();
        JsonElement utterance = document.RootElement.GetProperty("$defs").GetProperty("utterance");
        JsonElement properties = utterance.GetProperty("properties");

        AssertAdditionalPropertiesFalse(utterance);
        CollectionAssert.AreEquivalent(
            new[] { "id", "sourceSpanId", "speakerId", "speakerLabel", "text", "range", "sentences" },
            GetStringArray(utterance.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "sourceSpanId",
                "speakerId",
                "speakerLabel",
                "text",
                "range",
                "recognitionConfidence",
                "sentences",
            },
            properties.EnumerateObject().Select(static property => property.Name).ToArray());

        foreach (string propertyName in new[] { "id", "sourceSpanId", "speakerId" })
        {
            Assert.AreEqual(
                "#/$defs/entityId",
                properties.GetProperty(propertyName).GetProperty("$ref").GetString());
        }

        foreach (string propertyName in new[] { "speakerLabel", "text" })
        {
            Assert.AreEqual(
                "#/$defs/nonBlankText",
                properties.GetProperty(propertyName).GetProperty("$ref").GetString());
        }

        Assert.AreEqual("#/$defs/timeRange", properties.GetProperty("range").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "#/$defs/nullableConfidence",
            properties.GetProperty("recognitionConfidence").GetProperty("$ref").GetString());

        JsonElement sentences = properties.GetProperty("sentences");
        Assert.AreEqual(1, sentences.GetProperty("minItems").GetInt32());
        Assert.IsTrue(sentences.GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual("#/$defs/sentence", sentences.GetProperty("items").GetProperty("$ref").GetString());
        StringAssert.Contains(GetRequiredString(utterance, "$comment"), "foreign-key reference only");
        StringAssert.Contains(GetRequiredString(utterance, "$comment"), "contained");
    }

    [TestMethod]
    public void SentenceAndTokenShapesProvideExplicitGranularityAndNoTimingFallbackFabrication()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement sentence = definitions.GetProperty("sentence");
        JsonElement token = definitions.GetProperty("token");

        AssertAdditionalPropertiesFalse(sentence);
        CollectionAssert.AreEquivalent(
            new[] { "id", "text", "tokens" },
            GetStringArray(sentence.GetProperty("required")));
        JsonElement tokens = sentence.GetProperty("properties").GetProperty("tokens");
        Assert.AreEqual("array", tokens.GetProperty("type").GetString());
        Assert.AreEqual(1, tokens.GetProperty("minItems").GetInt32());
        Assert.IsTrue(tokens.GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual("#/$defs/token", tokens.GetProperty("items").GetProperty("$ref").GetString());
        StringAssert.Contains(GetRequiredString(tokens, "$comment"), "Untimed tokens");

        AssertAdditionalPropertiesFalse(token);
        CollectionAssert.AreEquivalent(
            new[] { "id", "text", "manualTimingAdjustmentRequired" },
            GetStringArray(token.GetProperty("required")));
        JsonElement tokenProperties = token.GetProperty("properties");
        CollectionAssert.AreEquivalent(
            new[] { "id", "text", "range", "recognitionConfidence", "manualTimingAdjustmentRequired" },
            tokenProperties.EnumerateObject().Select(static property => property.Name).ToArray());

        JsonElement[] rangeVariants = tokenProperties
            .GetProperty("range")
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual("#/$defs/timeRange", rangeVariants[0].GetProperty("$ref").GetString());
        Assert.AreEqual("null", rangeVariants[1].GetProperty("type").GetString());
        Assert.AreEqual(
            "#/$defs/nullableConfidence",
            tokenProperties.GetProperty("recognitionConfidence").GetProperty("$ref").GetString());
        Assert.AreEqual(
            "boolean",
            tokenProperties.GetProperty("manualTimingAdjustmentRequired").GetProperty("type").GetString());

        JsonElement timingRule = token.GetProperty("allOf")[0];
        CollectionAssert.AreEqual(
            new[] { "range" },
            GetStringArray(timingRule.GetProperty("if").GetProperty("required")));
        Assert.AreEqual(
            JsonValueKind.False,
            timingRule
                .GetProperty("then")
                .GetProperty("properties")
                .GetProperty("manualTimingAdjustmentRequired")
                .GetProperty("const")
                .ValueKind);
        Assert.AreEqual(
            JsonValueKind.True,
            timingRule
                .GetProperty("else")
                .GetProperty("properties")
                .GetProperty("manualTimingAdjustmentRequired")
                .GetProperty("const")
                .ValueKind);

        string tokenComment = GetRequiredString(token, "$comment");
        StringAssert.Contains(tokenComment, "owning utterance range");
        StringAssert.Contains(tokenComment, "never clamped or synthesized");
    }

    [TestMethod]
    public void SchemaContainsNoEditingAssignmentModelRunOrSerializationResponsibilities()
    {
        using JsonDocument document = LoadSchema();
        string schema = document.RootElement.GetRawText();

        string[] prohibitedNames =
        [
            "modelRunId",
            "performerId",
            "assignedPerformerId",
            "editorService",
            "serializedBy",
            "diff",
            "changeReason",
        ];
        foreach (string prohibitedName in prohibitedNames)
        {
            Assert.IsFalse(schema.Contains($"\"{prohibitedName}\"", StringComparison.Ordinal));
        }
    }

    private static void AssertRationalShape(JsonElement rational, long minimumValue)
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
        Assert.AreEqual("integer", rate.GetProperty("type").GetString());
        Assert.AreEqual(1L, rate.GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, rate.GetProperty("maximum").GetInt64());
    }

    private static void AssertCanonicalUuidPattern(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        string pattern = GetRequiredString(schema, "pattern");

        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(EmptyUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseUuid.Replace("-", string.Empty), pattern));
    }

    private static void AssertTrimmedNonBlankPattern(JsonElement schema)
    {
        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        Assert.AreEqual(1, schema.GetProperty("minLength").GetInt32());
        string pattern = GetRequiredString(schema, "pattern");

        Assert.IsTrue(Regex.IsMatch("Speaker 1", pattern));
        Assert.IsFalse(Regex.IsMatch(string.Empty, pattern));
        Assert.IsFalse(Regex.IsMatch(" Speaker 1", pattern));
        Assert.IsFalse(Regex.IsMatch("Speaker 1 ", pattern));
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