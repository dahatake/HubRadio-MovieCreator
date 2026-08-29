using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class PtsIndexSchemaTests
{
    private const string SchemaPath = "schemas/pts-index.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void SchemaIsClosedDraft202012WithExactRootShape()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("PtsIndex", root.GetProperty("title").GetString());
        AssertAdditionalPropertiesFalse(root);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "candidateId",
                "projectId",
                "jobId",
                "sourceFileName",
                "contentDigest",
                "byteSize",
                "streamIndex",
                "timeBase",
                "presentationRange",
                "entries",
            },
            GetStrings(root.GetProperty("required")));
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());
    }

    [TestMethod]
    public void SchemaClosesNestedShapesAndRequiresExactIntegerRationals()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        foreach (string name in new[]
        {
            "rationalTime",
            "positiveRationalTime",
            "timeRange",
            "entry",
        })
        {
            AssertAdditionalPropertiesFalse(definitions.GetProperty(name));
        }

        JsonElement rational = definitions.GetProperty("rationalTime");
        CollectionAssert.AreEquivalent(
            new[] { "value", "rate" },
            GetStrings(rational.GetProperty("required")));
        JsonElement rationalProperties = rational.GetProperty("properties");
        Assert.AreEqual(
            long.MinValue,
            rationalProperties.GetProperty("value").GetProperty("minimum").GetInt64());
        Assert.AreEqual(
            long.MaxValue,
            rationalProperties.GetProperty("value").GetProperty("maximum").GetInt64());
        Assert.AreEqual(
            1L,
            rationalProperties.GetProperty("rate").GetProperty("minimum").GetInt64());

        JsonElement positive = definitions.GetProperty("positiveRationalTime")
            .GetProperty("properties");
        Assert.AreEqual(1L, positive.GetProperty("value").GetProperty("minimum").GetInt64());
        Assert.AreEqual(1L, positive.GetProperty("rate").GetProperty("minimum").GetInt64());

        JsonElement entries = document.RootElement
            .GetProperty("properties")
            .GetProperty("entries");
        Assert.AreEqual(1, entries.GetProperty("minItems").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { "sequence", "presentationTime", "duration", "keyFrame" },
            GetStrings(definitions.GetProperty("entry").GetProperty("required")));
        Assert.AreEqual(
            "boolean",
            definitions.GetProperty("entry")
                .GetProperty("properties")
                .GetProperty("keyFrame")
                .GetProperty("type")
                .GetString());
    }

    [TestMethod]
    public void SchemaRequiresLowercaseIdsDigestsAndDocumentsRuntimeInvariants()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        string entityPattern = definitions.GetProperty("entityId")
            .GetProperty("pattern")
            .GetString()!;
        string digestPattern = definitions.GetProperty("contentDigest")
            .GetProperty("pattern")
            .GetString()!;

        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, entityPattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, entityPattern));
        Assert.IsTrue(Regex.IsMatch(LowercaseDigest, digestPattern));
        Assert.IsFalse(Regex.IsMatch(LowercaseDigest.ToUpperInvariant(), digestPattern));

        string comment = root.GetProperty("$comment").GetString()!;
        StringAssert.Contains(comment, "reduced RationalTime");
        StringAssert.Contains(comment, "integral stream-time-base multiples");
        StringAssert.Contains(comment, "contiguous zero-based");
        StringAssert.Contains(comment, "strictly increasing");
        StringAssert.Contains(comment, "at least one key frame");
        StringAssert.Contains(comment, "final presentation time plus final duration");
    }

    private static void AssertAdditionalPropertiesFalse(JsonElement schema)
    {
        Assert.AreEqual(
            JsonValueKind.False,
            schema.GetProperty("additionalProperties").ValueKind);
    }

    private static string[] GetStrings(JsonElement array) =>
        array.EnumerateArray()
            .Select(static item => item.GetString()
                ?? throw new InvalidOperationException("Schema array item must be a string."))
            .ToArray();

    private static JsonDocument LoadSchema() =>
        JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(SchemaPath)));

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