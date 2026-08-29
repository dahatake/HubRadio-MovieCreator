using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProxyFrameMapSchemaTests
{
    private const string SchemaPath = "schemas/proxy-frame-map.schema.json";
    private const string LowercaseUuid = "00112233-4455-4677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-4677-8899-AABBCCDDEEFF";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Schema_IsClosedDraft202012WithExactRootAndNestedShapes()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("ProxyFrameMap", root.GetProperty("title").GetString());
        AssertClosed(root);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "sourceAssetId",
                "projectId",
                "sourceContentDigest",
                "sourcePtsIndexDigest",
                "proxyContentDigest",
                "proxyPtsIndexDigest",
                "sourceFrameCount",
                "sourcePresentationStart",
                "sourcePresentationEnd",
                "roundTrip",
                "entries",
            },
            GetStrings(root.GetProperty("required")));
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());

        JsonElement definitions = root.GetProperty("$defs");
        foreach (string name in new[]
        {
            "rationalTime",
            "nonnegativeRationalTime",
            "roundTrip",
            "entry",
        })
        {
            AssertClosed(definitions.GetProperty(name));
        }

        Assert.AreEqual(
            1,
            root.GetProperty("properties")
                .GetProperty("entries")
                .GetProperty("minItems")
                .GetInt32());
    }

    [TestMethod]
    public void Schema_UsesCanonicalIdentityDigestAndExactRationalTime()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string entityPattern = definitions.GetProperty("entityId")
            .GetProperty("pattern")
            .GetString()!;
        string digestPattern = definitions.GetProperty("contentDigest")
            .GetProperty("pattern")
            .GetString()!;

        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, entityPattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, entityPattern));
        Assert.IsTrue(Regex.IsMatch(LowercaseDigest, digestPattern));
        Assert.IsFalse(Regex.IsMatch(
            LowercaseDigest.ToUpperInvariant(),
            digestPattern));

        JsonElement rational = definitions.GetProperty("rationalTime")
            .GetProperty("properties");
        Assert.AreEqual(
            long.MinValue,
            rational.GetProperty("value").GetProperty("minimum").GetInt64());
        Assert.AreEqual(
            long.MaxValue,
            rational.GetProperty("value").GetProperty("maximum").GetInt64());
        Assert.AreEqual(
            1L,
            rational.GetProperty("rate").GetProperty("minimum").GetInt64());
        Assert.AreEqual(
            0L,
            definitions.GetProperty("nonnegativeRationalTime")
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("minimum")
                .GetInt64());
    }

    [TestMethod]
    public void Schema_DocumentsObservedFloorMappingAndForbidsThresholdFallback()
    {
        using JsonDocument document = LoadSchema();
        string comment = document.RootElement.GetProperty("$comment").GetString()!;

        StringAssert.Contains(comment, "observed source and proxy presentation timestamps");
        StringAssert.Contains(comment, "last source frame");
        StringAssert.Contains(comment, "unrepresentable");
        StringAssert.Contains(comment, "not a production acceptance threshold");
        StringAssert.Contains(comment, "nominal frame rate fallback is forbidden");

        CollectionAssert.AreEquivalent(
            new[]
            {
                "proxyFrameSequence",
                "proxyPresentationTime",
                "sourceFrameSequence",
                "sourcePresentationTime",
            },
            GetStrings(document.RootElement
                .GetProperty("$defs")
                .GetProperty("entry")
                .GetProperty("required")));
    }

    private static JsonDocument LoadSchema() =>
        JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(SchemaPath)));

    private static void AssertClosed(JsonElement value) =>
        Assert.AreEqual(
            JsonValueKind.False,
            value.GetProperty("additionalProperties").ValueKind);

    private static string[] GetStrings(JsonElement array) =>
        array.EnumerateArray()
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