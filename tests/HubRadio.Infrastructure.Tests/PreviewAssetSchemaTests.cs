using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class PreviewAssetSchemaTests
{
    private const string WaveformSchemaPath =
        "schemas/preview-waveform-index.schema.json";
    private const string SpriteSchemaPath =
        "schemas/thumbnail-sprite-index.schema.json";
    private const string LowercaseUuid =
        "00112233-4455-4677-8899-aabbccddeeff";
    private const string UppercaseUuid =
        "00112233-4455-4677-8899-AABBCCDDEEFF";
    private const string LowercaseDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void WaveformSchema_IsClosedAndDocumentsFrameChannelAlignmentAndNoAudio()
    {
        using JsonDocument document = Load(WaveformSchemaPath);
        JsonElement root = document.RootElement;

        AssertRoot(root, "PreviewWaveformIndex");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "sourceAssetId",
                "projectId",
                "proxyArtifactId",
                "proxyContentDigest",
                "proxyPtsIndexDigest",
                "profileVersion",
                "sampleRate",
                "channelCount",
                "channelLayout",
                "frames",
            },
            GetStrings(root.GetProperty("required")));
        JsonElement definitions = root.GetProperty("$defs");
        foreach (string name in new[]
        {
            "positiveRationalTime",
            "rationalTime",
            "channel",
            "frame",
        })
        {
            AssertClosed(definitions.GetProperty(name));
        }

        JsonElement channel = definitions.GetProperty("channel")
            .GetProperty("properties");
        Assert.AreEqual(
            short.MinValue,
            channel.GetProperty("minimumSample")
                .GetProperty("minimum")
                .GetInt32());
        Assert.AreEqual(
            short.MaxValue,
            channel.GetProperty("maximumSample")
                .GetProperty("maximum")
                .GetInt32());
        string comment = root.GetProperty("$comment").GetString()!;
        StringAssert.Contains(comment, "exact observed proxy PTS");
        StringAssert.Contains(comment, "channel");
        StringAssert.Contains(comment, "no audio");
        StringAssert.Contains(comment, "silence is not invented");
    }

    [TestMethod]
    public void SpriteSchema_IsClosedAndDocumentsVerifiedKeyFrameIndexWithoutQualityClaim()
    {
        using JsonDocument document = Load(SpriteSchemaPath);
        JsonElement root = document.RootElement;

        AssertRoot(root, "ThumbnailSpriteIndex");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "sourceAssetId",
                "projectId",
                "proxyArtifactId",
                "proxyContentDigest",
                "proxyPtsIndexDigest",
                "profileVersion",
                "spriteImageArtifactId",
                "spriteImageDigest",
                "cellWidth",
                "cellHeight",
                "columns",
                "rows",
                "cells",
            },
            GetStrings(root.GetProperty("required")));
        JsonElement definitions = root.GetProperty("$defs");
        AssertClosed(definitions.GetProperty("rationalTime"));
        AssertClosed(definitions.GetProperty("positiveRationalTime"));
        AssertClosed(definitions.GetProperty("cell"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "ordinal",
                "proxyFrameSequence",
                "proxyPresentationTime",
                "proxyDuration",
                "column",
                "row",
            },
            GetStrings(definitions.GetProperty("cell").GetProperty("required")));

        string comment = root.GetProperty("$comment").GetString()!;
        StringAssert.Contains(comment, "verified proxy key frames");
        StringAssert.Contains(comment, "exact PTS/duration equality");
        StringAssert.Contains(comment, "no face, blur, or content-quality claim");
    }

    [TestMethod]
    public void BothSchemas_UseCanonicalLowercaseIdentityDigestAndExactRationals()
    {
        foreach (string path in new[] { WaveformSchemaPath, SpriteSchemaPath })
        {
            using JsonDocument document = Load(path);
            JsonElement definitions = document.RootElement.GetProperty("$defs");
            string entityPattern = definitions.GetProperty("entityId")
                .GetProperty("pattern")
                .GetString()!;
            string digestPattern = definitions.GetProperty("contentDigest")
                .GetProperty("pattern")
                .GetString()!;
            Assert.IsTrue(Regex.IsMatch(LowercaseUuid, entityPattern), path);
            Assert.IsFalse(Regex.IsMatch(UppercaseUuid, entityPattern), path);
            Assert.IsTrue(Regex.IsMatch(LowercaseDigest, digestPattern), path);
            Assert.IsFalse(Regex.IsMatch(
                LowercaseDigest.ToUpperInvariant(),
                digestPattern), path);

            JsonElement rational = definitions.GetProperty("rationalTime")
                .GetProperty("properties");
            Assert.AreEqual(
                long.MinValue,
                rational.GetProperty("value")
                    .GetProperty("minimum")
                    .GetInt64());
            Assert.AreEqual(
                long.MaxValue,
                rational.GetProperty("value")
                    .GetProperty("maximum")
                    .GetInt64());
            Assert.AreEqual(
                1L,
                rational.GetProperty("rate")
                    .GetProperty("minimum")
                    .GetInt64());
        }
    }

    private static void AssertRoot(JsonElement root, string title)
    {
        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual(title, root.GetProperty("title").GetString());
        AssertClosed(root);
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());
    }

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

    private static JsonDocument Load(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(ResolveRepositoryPath(relativePath)));

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