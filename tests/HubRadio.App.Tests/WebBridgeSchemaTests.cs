using System.Text.Json;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebBridgeSchemaTests
{
    [TestMethod]
    public void SchemaIsClosedVersionedAndAllowsOnlyPing()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("WebBridgeMessage", root.GetProperty("title").GetString());
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "128 UTF-8 bytes");
        Assert.AreEqual(1, definitions.GetProperty("protocolVersion").GetProperty("const").GetInt32());
        CollectionAssert.AreEqual(
            new[] { "bridge.ping" },
            definitions.GetProperty("method").GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());

        foreach (string definitionName in new[] { "pingRequest", "pingResult", "pingResponse" })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(definitionName).GetProperty("additionalProperties").ValueKind);
        }
    }

    [TestMethod]
    public void RequestAndResponseHaveDistinctExactEnvelopes()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("pingRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("pingResponse").GetProperty("required")));
        Assert.AreEqual(
            0,
            definitions.GetProperty("emptyObject").GetProperty("maxProperties").GetInt32());
        Assert.AreEqual(
            36,
            definitions.GetProperty("requestId").GetProperty("maxLength").GetInt32());
    }

    private static string[] GetStrings(JsonElement array)
    {
        return array.EnumerateArray().Select(static item => item.GetString()!).ToArray();
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}