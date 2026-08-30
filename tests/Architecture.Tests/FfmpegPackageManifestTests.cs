using System.Globalization;
using System.Text.Json;

namespace HubRadio.Architecture.Tests;

[TestClass]
public sealed class FfmpegPackageManifestTests
{
    private const string ManifestPath = "packaging/ffmpeg/manifest.json";
    private const string Stage0EvidencePath = "benchmarks/results/ffmpeg-capabilities.json";
    private const string FfprobeSourcePath =
        "src/HubRadio.Infrastructure/Media/FfprobeClient.cs";
    private const string DecodeSourcePath =
        "src/HubRadio.Infrastructure/Media/DecodeValidator.cs";

    [TestMethod]
    public void ManifestMatchesCanonicalStage0EvidenceAndRuntimeConstants()
    {
        string root = GetRepositoryRoot();
        using JsonDocument manifestDocument = ReadJson(root, ManifestPath);
        using JsonDocument evidenceDocument = ReadJson(root, Stage0EvidencePath);
        JsonElement manifest = manifestDocument.RootElement;
        JsonElement evidence = evidenceDocument.RootElement;
        JsonElement package = manifest.GetProperty("package");
        JsonElement evidencePackage = evidence.GetProperty("package");
        JsonElement archive = package.GetProperty("archive");

        Assert.AreEqual("1.0.0", manifest.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("PK-003", manifest.GetProperty("taskId").GetString());
        Assert.AreEqual(evidencePackage.GetProperty("id").GetString(), package.GetProperty("id").GetString());
        Assert.AreEqual(evidencePackage.GetProperty("version").GetString(), package.GetProperty("version").GetString());
        Assert.AreEqual(evidencePackage.GetProperty("releaseTag").GetString(), package.GetProperty("releaseTag").GetString());
        Assert.AreEqual(evidencePackage.GetProperty("archiveFile").GetString(), archive.GetProperty("fileName").GetString());
        Assert.AreEqual(evidencePackage.GetProperty("archiveSha256").GetString(), archive.GetProperty("expectedSha256").GetString());
        Assert.IsFalse(archive.GetProperty("bytesRetainedAndRehashedByPk003").GetBoolean());
        Assert.IsFalse(evidencePackage.GetProperty("archiveBytesRehashedByStage").GetBoolean());
        Assert.AreEqual("win64-lgpl-static", evidencePackage.GetProperty("variant").GetString());

        Assert.AreEqual(
            evidence.GetProperty("version").GetProperty("firstLine").GetString(),
            manifest.GetProperty("versionEvidence").GetProperty("firstLine").GetString());
        Assert.AreEqual(
            evidence.GetProperty("version").GetProperty("configuration").GetString(),
            manifest.GetProperty("versionEvidence").GetProperty("configuration").GetString());
        Assert.AreEqual(
            evidence.GetProperty("capabilities").GetProperty("runtimeEncodeSuccessMeasured").GetBoolean(),
            manifest.GetProperty("observedCapabilities").GetProperty("runtimeEncodeSuccessMeasured").GetBoolean());

        JsonElement observedCapabilities = manifest.GetProperty("observedCapabilities");
        AssertJsonObjectEquals(
            evidence.GetProperty("capabilities").GetProperty("filters"),
            observedCapabilities.GetProperty("filters"));
        AssertJsonObjectEquals(
            evidence.GetProperty("capabilities").GetProperty("encoders"),
            observedCapabilities.GetProperty("encoders"));
        CollectionAssert.AreEqual(
            evidence
                .GetProperty("capabilities")
                .GetProperty("hardwareAccelerationMethodsListedByFfmpeg")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray(),
            observedCapabilities
                .GetProperty("hardwareAccelerationMethodsListedByFfmpeg")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());

        Dictionary<string, JsonElement> files = manifest
            .GetProperty("selectedFiles")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("role").GetString()!,
                StringComparer.Ordinal);
        Assert.HasCount(3, files);
        AssertFileMatchesEvidence(files["ffmpeg"], evidence.GetProperty("executables").GetProperty("ffmpeg"));
        AssertFileMatchesEvidence(files["ffprobe"], evidence.GetProperty("executables").GetProperty("ffprobe"));

        string ffprobeSource = File.ReadAllText(Path.Combine(root, FfprobeSourcePath));
        string decodeSource = File.ReadAllText(Path.Combine(root, DecodeSourcePath));
        Assert.AreEqual(package.GetProperty("id").GetString(), ReadQuotedConstant(ffprobeSource, "CanonicalPackageId"));
        Assert.AreEqual(package.GetProperty("version").GetString(), ReadQuotedConstant(ffprobeSource, "CanonicalPackageVersion"));
        Assert.AreEqual(files["ffprobe"].GetProperty("byteSize").GetInt64(), ReadInt64Constant(ffprobeSource, "CanonicalFfprobeByteSize"));
        Assert.AreEqual(files["ffprobe"].GetProperty("sha256").GetString(), ReadQuotedConstant(ffprobeSource, "CanonicalFfprobeSha256"));
        Assert.AreEqual(files["ffmpeg"].GetProperty("byteSize").GetInt64(), ReadInt64Constant(decodeSource, "CanonicalFfmpegByteSize"));
        Assert.AreEqual(files["ffmpeg"].GetProperty("sha256").GetString(), ReadQuotedConstant(decodeSource, "CanonicalFfmpegSha256"));
    }

    [TestMethod]
    public void ManifestPinsExactSourceBuildLicenseAndSelectionBoundary()
    {
        string root = GetRepositoryRoot();
        using JsonDocument document = ReadJson(root, ManifestPath);
        JsonElement manifest = document.RootElement;
        AssertUniquePropertyNames(manifest, "$", new List<string>());

        JsonElement sourceAndBuild = manifest.GetProperty("sourceAndBuild");
        Assert.AreEqual(
            496767231L,
            manifest
                .GetProperty("package")
                .GetProperty("archive")
                .GetProperty("githubReleaseAssetId")
                .GetInt64());
        Assert.AreEqual(
            145349121L,
            manifest
                .GetProperty("package")
                .GetProperty("archive")
                .GetProperty("expectedByteSize")
                .GetInt64());
        Assert.AreEqual(
            "a99e8230eae00d1cee38f23076a7a1f55cd984e2",
            sourceAndBuild.GetProperty("buildRepository").GetProperty("commit").GetString());
        Assert.AreEqual(
            "9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b",
            sourceAndBuild.GetProperty("ffmpegSource").GetProperty("commit").GetString());
        Assert.AreEqual(
            "release/8.1",
            sourceAndBuild.GetProperty("ffmpegSource").GetProperty("branchSelectedByVendorAddin").GetString());
        Assert.IsFalse(sourceAndBuild.GetProperty("ffmpegSource").GetProperty("sourceArchiveRetainedAndRehashedByPk003").GetBoolean());
        Assert.IsFalse(sourceAndBuild.GetProperty("exactCommitRebuildVerifiedByPk003").GetBoolean());

        string configuration = manifest
            .GetProperty("versionEvidence")
            .GetProperty("configuration")
            .GetString()!;
        string[] configurationTokens = configuration.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.Contains(configurationTokens, "--enable-version3");
        CollectionAssert.DoesNotContain(configurationTokens, "--enable-gpl");
        CollectionAssert.DoesNotContain(configurationTokens, "--enable-nonfree");

        JsonElement license = manifest.GetProperty("licenseAssessment");
        Assert.IsTrue(license.GetProperty("enableVersion3").GetBoolean());
        Assert.IsFalse(license.GetProperty("enablesGpl").GetBoolean());
        Assert.IsFalse(license.GetProperty("enablesNonFree").GetBoolean());
        Assert.IsTrue(license.GetProperty("staticExecutableDistribution").GetBoolean());
        Assert.IsTrue(license.GetProperty("bundledDependencyLicenseReviewRequired").GetBoolean());
        Assert.IsFalse(license.GetProperty("legalApprovalGranted").GetBoolean());

        JsonElement licenseFile = manifest
            .GetProperty("selectedFiles")
            .EnumerateArray()
            .Single(static item => item.GetProperty("role").GetString() == "license");
        Assert.AreEqual("LICENSE.txt", licenseFile.GetProperty("pathSuffixInArchive").GetString());
        Assert.AreEqual(7651L, licenseFile.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(
            "da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768",
            licenseFile.GetProperty("sha256").GetString());

        string[] selectedRoles = manifest
            .GetProperty("selectedFiles")
            .EnumerateArray()
            .Select(static item => item.GetProperty("role").GetString()!)
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "ffmpeg", "ffprobe", "license" }, selectedRoles);
        string[] excluded = manifest
            .GetProperty("excludedFromProductPackage")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "bin/ffplay.exe" }, excluded);

        foreach (JsonProperty gate in manifest.GetProperty("releaseGates").EnumerateObject())
        {
            Assert.AreEqual(JsonValueKind.False, gate.Value.ValueKind, gate.Name);
        }

        foreach (JsonElement file in manifest.GetProperty("selectedFiles").EnumerateArray())
        {
            Assert.IsTrue(IsLowerSha256(file.GetProperty("sha256").GetString()));
            Assert.IsGreaterThan(0L, file.GetProperty("byteSize").GetInt64());
        }
        Assert.IsTrue(IsLowerSha256(
            manifest.GetProperty("package").GetProperty("archive").GetProperty("expectedSha256").GetString()));
    }

    [TestMethod]
    public void BuildInstructionsNoticeAndLicenseInventoryPreserveOpenGates()
    {
        string root = GetRepositoryRoot();
        string instructions = File.ReadAllText(Path.Combine(root, "packaging/ffmpeg/README.md"));
        string notice = File.ReadAllText(Path.Combine(root, "packaging/ffmpeg/NOTICE.md"));
        string inventoryPath = Path.Combine(root, "docs/dev/licenses/dependencies.csv");
        string[] inventory = File.ReadAllLines(inventoryPath);

        Assert.IsTrue(instructions.Contains("./makeimage.sh win64 lgpl 8.1", StringComparison.Ordinal));
        Assert.IsTrue(instructions.Contains("./build.sh win64 lgpl 8.1", StringComparison.Ordinal));
        Assert.IsTrue(instructions.Contains("9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b", StringComparison.Ordinal));
        Assert.IsTrue(instructions.Contains("does **not** prove an exact rebuild", StringComparison.Ordinal));
        Assert.IsTrue(instructions.Contains("runtimeEncodeSuccessMeasured=false", StringComparison.Ordinal));
        Assert.IsTrue(instructions.Contains("Q-010", StringComparison.Ordinal));

        Assert.IsTrue(notice.Contains("notice inventory (non-final)", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(notice.Contains("Legal approval: **not granted**", StringComparison.Ordinal));
        Assert.IsTrue(notice.Contains("Release approval: **not granted**", StringComparison.Ordinal));
        Assert.IsTrue(notice.Contains("not a complete distribution notice", StringComparison.Ordinal));
        Assert.IsTrue(notice.Contains("Q-010", StringComparison.Ordinal));

        string[] identities =
        [
            "\"BtbN FFmpeg LGPL archive\"",
            "\"ffmpeg.exe from BtbN LGPL build\"",
            "\"ffprobe.exe from BtbN LGPL build\"",
        ];
        foreach (string identity in identities)
        {
            string row = inventory.Single(line => line.StartsWith(identity, StringComparison.Ordinal));
            Assert.IsTrue(row.Contains(ManifestPath, StringComparison.Ordinal));
            Assert.IsTrue(row.Contains("not-approved-by-PK-003", StringComparison.Ordinal));
        }
    }

    private static void AssertFileMatchesEvidence(JsonElement manifestFile, JsonElement evidenceFile)
    {
        Assert.AreEqual(evidenceFile.GetProperty("fileName").GetString(), Path.GetFileName(manifestFile.GetProperty("pathSuffixInArchive").GetString()));
        Assert.AreEqual(evidenceFile.GetProperty("byteSize").GetInt64(), manifestFile.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(evidenceFile.GetProperty("sha256").GetString(), manifestFile.GetProperty("sha256").GetString());
    }

    private static void AssertJsonObjectEquals(JsonElement expected, JsonElement actual)
    {
        Dictionary<string, bool> expectedValues = expected
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.GetBoolean(),
                StringComparer.Ordinal);
        Dictionary<string, bool> actualValues = actual
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.GetBoolean(),
                StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(
            expectedValues.Keys.ToArray(),
            actualValues.Keys.ToArray());
        foreach ((string name, bool expectedValue) in expectedValues)
        {
            Assert.AreEqual(expectedValue, actualValues[name], name);
        }
    }

    private static JsonDocument ReadJson(string root, string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

    private static string ReadQuotedConstant(string source, string name)
    {
        ReadOnlySpan<char> initializer = FindConstantInitializer(source, name);
        int firstQuote = initializer.IndexOf('"');
        int secondQuote = firstQuote < 0 ? -1 : initializer[(firstQuote + 1)..].IndexOf('"');
        if (firstQuote < 0 || secondQuote < 0)
        {
            throw new InvalidOperationException($"Quoted constant '{name}' was not found.");
        }

        return initializer.Slice(firstQuote + 1, secondQuote).ToString();
    }

    private static long ReadInt64Constant(string source, string name)
    {
        string initializer = FindConstantInitializer(source, name).Trim().ToString();
        return long.Parse(initializer, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static ReadOnlySpan<char> FindConstantInitializer(string source, string name)
    {
        int nameIndex = source.IndexOf(name, StringComparison.Ordinal);
        int equalsIndex = nameIndex < 0 ? -1 : source.IndexOf('=', nameIndex + name.Length);
        int semicolonIndex = equalsIndex < 0 ? -1 : source.IndexOf(';', equalsIndex + 1);
        if (nameIndex < 0 || equalsIndex < 0 || semicolonIndex < 0)
        {
            throw new InvalidOperationException($"Constant '{name}' was not found.");
        }

        return source.AsSpan(equalsIndex + 1, semicolonIndex - equalsIndex - 1);
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AssertUniquePropertyNames(
        JsonElement element,
        string path,
        List<string> duplicatePaths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string childPath = string.Concat(path, ".", property.Name);
                if (!names.Add(property.Name))
                {
                    duplicatePaths.Add(childPath);
                }

                AssertUniquePropertyNames(property.Value, childPath, duplicatePaths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertUniquePropertyNames(item, $"{path}[{index}]", duplicatePaths);
                index++;
            }
        }

        Assert.AreEqual(
            0,
            duplicatePaths.Count,
            "Manifest contains duplicate JSON properties: " + string.Join(", ", duplicatePaths));
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HubRadio.slnx")) &&
                File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}