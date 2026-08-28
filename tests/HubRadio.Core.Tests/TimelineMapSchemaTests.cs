using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TimelineMapSchemaTests
{
    private const string SchemaPath = "schemas/timeline-map.schema.json";
    private const string LowercaseUuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseUuid = "00112233-4455-6677-8899-AABBCCDDEEFF";

    [TestMethod]
    public void SchemaIsClosedDraft202012TimelineMap()
    {
        using JsonDocument document = LoadSchema();
        JsonElement root = document.RootElement;

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("TimelineMap", root.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        CollectionAssert.AreEquivalent(
            new[] { "id", "compositionVersionId", "entries" },
            GetStrings(root.GetProperty("required")));
        StringAssert.Contains(GetString(root, "$comment"), "one-to-one");
    }

    [TestMethod]
    public void SchemaUsesCanonicalIdentityAndExactRationalTime()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string pattern = GetString(definitions.GetProperty("entityId"), "pattern");

        Assert.IsTrue(Regex.IsMatch(LowercaseUuid, pattern));
        Assert.IsFalse(Regex.IsMatch(UppercaseUuid, pattern));

        JsonElement rational = definitions.GetProperty("rationalTime");
        JsonElement properties = rational.GetProperty("properties");
        Assert.AreEqual(long.MinValue, properties.GetProperty("value").GetProperty("minimum").GetInt64());
        Assert.AreEqual(long.MaxValue, properties.GetProperty("value").GetProperty("maximum").GetInt64());
        Assert.AreEqual(1L, properties.GetProperty("rate").GetProperty("minimum").GetInt64());

        JsonElement outputStart = definitions.GetProperty("outputTimeRange")
            .GetProperty("properties")
            .GetProperty("start")
            .GetProperty("properties")
            .GetProperty("value");
        Assert.AreEqual(0L, outputStart.GetProperty("minimum").GetInt64());
    }

    [TestMethod]
    public void EntryUnionRequiresSourceRangeOnlyForSourceSpan()
    {
        using JsonDocument document = LoadSchema();
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement source = definitions.GetProperty("sourceSpanEntry");
        JsonElement asset = definitions.GetProperty("assetEntry");
        JsonElement editorial = definitions.GetProperty("editorialRecordEntry");

        CollectionAssert.AreEquivalent(
            new[] { "outputRange", "origin", "sourceRange" },
            GetStrings(source.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "outputRange", "origin" },
            GetStrings(asset.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "outputRange", "origin" },
            GetStrings(editorial.GetProperty("required")));
        Assert.IsFalse(asset.GetProperty("properties").TryGetProperty("sourceRange", out _));
        Assert.IsFalse(editorial.GetProperty("properties").TryGetProperty("sourceRange", out _));
        StringAssert.Contains(GetString(source, "$comment"), "equal");

        string[] entryVariants = definitions.GetProperty("timelineMapEntry")
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(static item => item.GetProperty("$ref").GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/sourceSpanEntry",
                "#/$defs/assetEntry",
                "#/$defs/editorialRecordEntry",
            },
            entryVariants);
    }

    private static JsonDocument LoadSchema()
    {
        return JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(SchemaPath)));
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.GetProperty(name).GetString()
            ?? throw new InvalidOperationException($"Schema property '{name}' must be a string.");
    }

    private static string[] GetStrings(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(static item => item.GetString()
                ?? throw new InvalidOperationException("Schema array item must be a string."))
            .ToArray();
    }

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