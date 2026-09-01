using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class AncillaryArtifactSchemaTests
{
    private const string SchemaRelativePath =
        "schemas/ancillary-artifact.schema.json";

    [TestMethod]
    public void SchemaDefinesClosedVersionedEvidenceEnvelope()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("AncillaryArtifact", root.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty(
            "additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "exportId",
                "kind",
                "projectId",
                "compositionVersionId",
                "compositionVersionDigest",
                "reviewEvidenceDigest",
                "language",
                "payloadDigest",
                "origins",
                "payload",
            },
            GetStrings(root.GetProperty("required")));
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());

        string comment = GetString(root, "$comment");
        StringAssert.Contains(comment, "no title");
        StringAssert.Contains(comment, "may be fabricated");
        StringAssert.Contains(comment, "OriginRef");
    }

    [TestMethod]
    public void EveryKindSelectsExactlyOneClosedPayloadContract()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        string[] kinds = GetStrings(
            root.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum"));
        CollectionAssert.AreEqual(
            new[]
            {
                "transcript",
                "chapters",
                "publishingText",
                "editingInstructions",
                "reviewEvidence",
                "usedSourceRanges",
            },
            kinds);

        Dictionary<string, string> selectors = root
            .GetProperty("allOf")
            .EnumerateArray()
            .ToDictionary(
                static selector => selector
                    .GetProperty("if")
                    .GetProperty("properties")
                    .GetProperty("kind")
                    .GetProperty("const")
                    .GetString()!,
                static selector => selector
                    .GetProperty("then")
                    .GetProperty("properties")
                    .GetProperty("payload")
                    .GetProperty("$ref")
                    .GetString()!,
                StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(kinds, selectors.Keys.ToArray());
        Assert.AreEqual("#/$defs/transcriptPayload", selectors["transcript"]);
        Assert.AreEqual("#/$defs/chaptersPayload", selectors["chapters"]);
        Assert.AreEqual(
            "#/$defs/publishingTextPayload",
            selectors["publishingText"]);
        Assert.AreEqual(
            "#/$defs/editingInstructionsPayload",
            selectors["editingInstructions"]);
        Assert.AreEqual(
            "#/$defs/reviewEvidencePayload",
            selectors["reviewEvidence"]);
        Assert.AreEqual(
            "#/$defs/usedSourceRangesPayload",
            selectors["usedSourceRanges"]);

        JsonElement definitions = root.GetProperty("$defs");
        foreach (string definitionName in selectors.Values.Select(
            static value => value["#/$defs/".Length..]))
        {
            JsonElement payload = definitions.GetProperty(definitionName);
            Assert.AreEqual("object", payload.GetProperty("type").GetString());
            Assert.AreEqual(
                JsonValueKind.False,
                payload.GetProperty("additionalProperties").ValueKind,
                definitionName);
        }
    }

    [TestMethod]
    public void PublishingAndChaptersPreserveExplicitAbsenceWithoutFallbacks()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement publishing = definitions.GetProperty(
            "publishingTextPayload");
        CollectionAssert.AreEquivalent(
            new[] { "title", "summary", "postText", "hashtags" },
            GetStrings(publishing.GetProperty("required")));

        JsonElement publishingProperties = publishing.GetProperty("properties");
        foreach (string name in new[] { "title", "summary", "postText" })
        {
            Assert.AreEqual(
                "#/$defs/nullableAttributedText",
                publishingProperties.GetProperty(name)
                    .GetProperty("$ref")
                    .GetString());
        }

        JsonElement hashtags = publishingProperties.GetProperty("hashtags");
        Assert.AreEqual("array", hashtags.GetProperty("type").GetString());
        Assert.IsFalse(hashtags.TryGetProperty("minItems", out _));
        Assert.IsTrue(hashtags.GetProperty("uniqueItems").GetBoolean());

        JsonElement chapters = definitions.GetProperty("chaptersPayload")
            .GetProperty("properties")
            .GetProperty("chapters");
        Assert.AreEqual("array", chapters.GetProperty("type").GetString());
        Assert.IsFalse(chapters.TryGetProperty("minItems", out _));
        Assert.IsTrue(chapters.GetProperty("uniqueItems").GetBoolean());

        JsonElement chapter = definitions.GetProperty("chapter");
        Assert.AreEqual(
            "manualEditorial",
            chapter.GetProperty("properties")
                .GetProperty("titleValueOrigin")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            "#/$defs/editorialRecordOrigin",
            chapter.GetProperty("properties")
                .GetProperty("origin")
                .GetProperty("$ref")
                .GetString());

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("projectTitleFallback", StringComparison.Ordinal));
        Assert.IsFalse(schema.Contains("generatedSummary", StringComparison.Ordinal));
        Assert.IsFalse(schema.Contains("generatedHashtags", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProvenanceVersionAndReviewEvidenceAreClosedAndRequired()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        JsonElement origins = root.GetProperty("properties")
            .GetProperty("origins");
        Assert.AreEqual(1, origins.GetProperty("minItems").GetInt32());
        Assert.IsTrue(origins.GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual(
            "#/$defs/originRef",
            origins.GetProperty("items").GetProperty("$ref").GetString());

        JsonElement origin = definitions.GetProperty("originRef");
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/sourceSpanOrigin",
                "#/$defs/assetOrigin",
                "#/$defs/editorialRecordOrigin",
            },
            origin.GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());
        foreach (string name in new[]
        {
            "sourceSpanOrigin",
            "assetOrigin",
            "editorialRecordOrigin",
            "sourceSpan",
            "reviewRecord",
            "reviewComment",
            "approval",
            "usedSourceRange",
        })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(name)
                    .GetProperty("additionalProperties")
                    .ValueKind,
                name);
        }

        JsonElement review = definitions.GetProperty("reviewEvidencePayload");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "compositionVersionId",
                "compositionVersionDigest",
                "reviewRecords",
                "reviewComments",
                "approvals",
            },
            GetStrings(review.GetProperty("required")));
        Assert.AreEqual(
            "transcript.schema.json",
            definitions.GetProperty("transcriptPayload")
                .GetProperty("properties")
                .GetProperty("revision")
                .GetProperty("$ref")
                .GetString());
    }

    [TestMethod]
    public void CanonicalIdentifiersDigestsAndLanguageTagsHaveExactPatterns()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string idPattern = GetString(definitions.GetProperty("entityId"), "pattern");
        string digestPattern = GetString(
            definitions.GetProperty("contentDigest"),
            "pattern");
        string languagePattern = GetString(
            definitions.GetProperty("languageTag"),
            "pattern");

        Assert.IsTrue(Regex.IsMatch(
            "00112233-4455-6677-8899-aabbccddeeff",
            idPattern));
        Assert.IsFalse(Regex.IsMatch(
            "00112233-4455-6677-8899-AABBCCDDEEFF",
            idPattern));
        Assert.IsFalse(Regex.IsMatch(
            "00000000-0000-0000-0000-000000000000",
            idPattern));
        Assert.IsTrue(Regex.IsMatch(new string('a', 64), digestPattern));
        Assert.IsFalse(Regex.IsMatch(new string('A', 64), digestPattern));
        Assert.IsTrue(Regex.IsMatch("ja-JP", languagePattern));
        Assert.IsTrue(Regex.IsMatch("en-US-123", languagePattern));
        Assert.IsFalse(Regex.IsMatch(" ja-JP", languagePattern));
        Assert.IsFalse(Regex.IsMatch("ja--JP", languagePattern));
    }

    private static JsonDocument LoadSchema() => JsonDocument.Parse(
        File.ReadAllText(ResolveRepositoryPath(SchemaRelativePath)));

    private static string GetString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException(
            $"Schema property '{propertyName}' must be a string.");

    private static string[] GetStrings(JsonElement array) => array
        .EnumerateArray()
        .Select(static item => item.GetString()
            ?? throw new InvalidOperationException(
                "Schema array item must be a string."))
        .ToArray();

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