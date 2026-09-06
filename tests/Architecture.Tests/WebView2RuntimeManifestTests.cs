using System.Text.Json;

namespace HubRadio.Architecture.Tests;

// Metadata contract tests only: no ignored binary payload, raw inventory or license snapshot is opened.
// These assertions do not verify runtime execution, clean-machine startup or redistribution acceptance.
[TestClass]
public sealed class WebView2RuntimeManifestTests
{
    private const string RuntimeVersion = "152.0.4191.62";
    private const string RuntimeArchiveSha256 =
        "2dc7d817ddce4d036f33684425c218f1ca2d073911136e5d3f8ce9b2340569b5";
    private const string RuntimeArtifactSetSha256 =
        "fddf4760e18cde307c62c0385024dda51c8a6f8719cf2fb091cdf44778c8c99a";
    private const string OfficialSourcePage =
        "https://developer.microsoft.com/en-us/microsoft-edge/webview2";

    [TestMethod]
    public void RuntimeManifest_PinsOfficialWindowsX64FixedVersionArchive()
    {
        using JsonDocument document = ReadManifest();
        JsonElement manifest = document.RootElement;
        AssertExactProperties(
            manifest,
            "runtime manifest",
            "schemaVersion", "evidenceDate", "runtime", "output", "isolation",
            "releaseApproved", "remainingGates");
        Assert.AreEqual("1.0.0", manifest.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("2026-09-06", manifest.GetProperty("evidenceDate").GetString());

        JsonElement runtime = manifest.GetProperty("runtime");
        AssertExactProperties(
            runtime,
            "runtime",
            "identity", "distributionMode", "version", "architecture", "operatingSystem",
            "sourcePage", "source", "sourceUsage", "publicDownloadShortcutAllowed",
            "distributionReference", "fileName", "byteSize", "sha256", "unpackDirectory",
            "signatureEvidenceScope", "selectedSignedFiles", "license");
        Assert.AreEqual("Microsoft Edge WebView2 Runtime", runtime.GetProperty("identity").GetString());
        Assert.AreEqual("FixedVersion", runtime.GetProperty("distributionMode").GetString());
        Assert.AreEqual(RuntimeVersion, runtime.GetProperty("version").GetString());
        Assert.AreEqual("Windows", runtime.GetProperty("operatingSystem").GetString());
        Assert.AreEqual("x64", runtime.GetProperty("architecture").GetString());
        Assert.AreEqual(OfficialSourcePage, runtime.GetProperty("sourcePage").GetString());
        Assert.AreEqual(
            "https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/" +
            "0a4a34d9-ccaa-4cef-98b4-58cb313fbfeb/" +
            "Microsoft.WebView2.FixedVersionRuntime.152.0.4191.62.x64.cab",
            runtime.GetProperty("source").GetString());
        Assert.AreEqual(
            "https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution",
            runtime.GetProperty("distributionReference").GetString());
        Assert.AreEqual(
            "Microsoft.WebView2.FixedVersionRuntime.152.0.4191.62.x64.cab",
            runtime.GetProperty("fileName").GetString());
        Assert.AreEqual(371_877_369L, runtime.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(RuntimeArchiveSha256, runtime.GetProperty("sha256").GetString());
        AssertCanonicalDigest(runtime.GetProperty("sha256").GetString());
        Assert.AreEqual(
            "Microsoft.WebView2.FixedVersionRuntime.152.0.4191.62.x64",
            runtime.GetProperty("unpackDirectory").GetString());
        AssertCanonicalRelativePath(runtime.GetProperty("fileName").GetString());
        AssertCanonicalRelativePath(runtime.GetProperty("unpackDirectory").GetString());
    }

    [TestMethod]
    public void RuntimeManifest_PinsCompleteUnmodifiedPayloadAndOrdinalFingerprint()
    {
        using JsonDocument document = ReadManifest();
        JsonElement output = document.RootElement.GetProperty("output");
        AssertExactProperties(
            output,
            "output",
            "relativePath", "entryPoint", "expectedFileCount", "expectedTotalBytes",
            "expectedArtifactSetSha256", "artifactSetDigestAlgorithm", "copyMode", "preservationNote");
        Assert.AreEqual("WebView2Runtime", output.GetProperty("relativePath").GetString());
        Assert.AreEqual("msedgewebview2.exe", output.GetProperty("entryPoint").GetString());
        AssertCanonicalRelativePath(output.GetProperty("relativePath").GetString());
        AssertCanonicalRelativePath(output.GetProperty("entryPoint").GetString());
        Assert.AreEqual(548, output.GetProperty("expectedFileCount").GetInt32());
        Assert.AreEqual(837_201_247L, output.GetProperty("expectedTotalBytes").GetInt64());
        Assert.AreEqual(RuntimeArtifactSetSha256, output.GetProperty("expectedArtifactSetSha256").GetString());
        AssertCanonicalDigest(output.GetProperty("expectedArtifactSetSha256").GetString());

        JsonElement algorithm = output.GetProperty("artifactSetDigestAlgorithm");
        AssertExactProperties(
            algorithm,
            "artifact-set digest algorithm",
            "hash", "encoding", "pathBase", "pathSeparator", "pathOrder", "input");
        Assert.AreEqual("SHA256", algorithm.GetProperty("hash").GetString());
        Assert.AreEqual("UTF8-no-BOM", algorithm.GetProperty("encoding").GetString());
        Assert.AreEqual("unpackDirectory-contents", algorithm.GetProperty("pathBase").GetString());
        Assert.AreEqual("/", algorithm.GetProperty("pathSeparator").GetString());
        Assert.AreEqual("Ordinal", algorithm.GetProperty("pathOrder").GetString());
        Assert.AreEqual(
            "concatenated(path NUL byteSize-invariant NUL lowercase-sha256 LF)",
            algorithm.GetProperty("input").GetString());
        Assert.AreEqual("entire-unmodified-tree", output.GetProperty("copyMode").GetString());
        Assert.AreEqual(
            "Vendor BAT, LICENSE and native resource files are preserved byte-for-byte; " +
            "their inclusion does not imply runtime shell invocation or signature validation of every file.",
            output.GetProperty("preservationNote").GetString());
    }

    [TestMethod]
    public void RuntimeManifest_RecordsOnlyMeasuredSelectedAuthenticodeEvidence()
    {
        using JsonDocument document = ReadManifest();
        JsonElement runtime = document.RootElement.GetProperty("runtime");
        Assert.AreEqual("selected-files-only", runtime.GetProperty("signatureEvidenceScope").GetString());
        JsonElement[] files = runtime.GetProperty("selectedSignedFiles").EnumerateArray().ToArray();
        Assert.HasCount(2, files);
        CollectionAssert.AreEqual(
            new[] { "msedge.dll", "msedgewebview2.exe" },
            files.Select(static file => file.GetProperty("path").GetString()).ToArray());

        JsonElement library = files[0];
        AssertExactProperties(
            library,
            "measured msedge.dll evidence",
            "path", "byteSize", "sha256", "signatureStatus", "signerSubject");
        Assert.AreEqual(343_780_680L, library.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(
            "e2c97da08a6a2ebd22e0ae645e720b76ce86f662289359bf73762259771e3564",
            library.GetProperty("sha256").GetString());

        JsonElement executable = files[1];
        AssertExactProperties(
            executable,
            "measured msedgewebview2.exe evidence",
            "path", "byteSize", "sha256", "fileVersion", "signatureStatus", "signerSubject");
        Assert.AreEqual(4_824_392L, executable.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(
            "0f75ba7da899408d4c4474d58358bdbee732705fc090161cbf08e8783520572d",
            executable.GetProperty("sha256").GetString());
        Assert.AreEqual(RuntimeVersion, executable.GetProperty("fileVersion").GetString());

        foreach (JsonElement file in files)
        {
            AssertCanonicalRelativePath(file.GetProperty("path").GetString());
            AssertCanonicalDigest(file.GetProperty("sha256").GetString());
            Assert.AreEqual("Valid", file.GetProperty("signatureStatus").GetString());
            Assert.AreEqual(
                "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
                file.GetProperty("signerSubject").GetString());
        }
    }

    [TestMethod]
    public void RuntimeManifest_DisablesFallbackRuntimeDownloadAndStartupNetwork()
    {
        using JsonDocument document = ReadManifest();
        JsonElement isolation = document.RootElement.GetProperty("isolation");
        AssertExactProperties(
            isolation,
            "packaged runtime startup policy",
            "systemRuntimeFallbackAllowed", "runtimeDownloadAllowed", "networkRequiredAtStartup",
            "automaticUpdatesAllowed", "missingRuntimeBehavior");
        Assert.IsFalse(isolation.GetProperty("systemRuntimeFallbackAllowed").GetBoolean());
        Assert.IsFalse(isolation.GetProperty("runtimeDownloadAllowed").GetBoolean());
        Assert.IsFalse(isolation.GetProperty("networkRequiredAtStartup").GetBoolean());
        Assert.IsFalse(isolation.GetProperty("automaticUpdatesAllowed").GetBoolean());
        Assert.AreEqual("fail-closed", isolation.GetProperty("missingRuntimeBehavior").GetString());

        JsonElement runtime = document.RootElement.GetProperty("runtime");
        Assert.AreEqual("developer-provenance-only", runtime.GetProperty("sourceUsage").GetString());
        Assert.IsFalse(runtime.GetProperty("publicDownloadShortcutAllowed").GetBoolean());
    }

    [TestMethod]
    public void RuntimeManifest_IdentifiesLicensePageSnapshotWithoutStandaloneLicenseClaim()
    {
        using JsonDocument document = ReadManifest();
        JsonElement license = document.RootElement.GetProperty("runtime").GetProperty("license");
        AssertExactProperties(
            license,
            "license",
            "name", "status", "localUseBasis", "objectCodeRedistributionBasis",
            "redistributionAccepted", "preserveThirdPartyNotices", "evidence", "pendingRedistributionConditions");
        Assert.AreEqual(
            "MICROSOFT EDGE WEBVIEW2 RUNTIME (FIXED VERSION)",
            license.GetProperty("name").GetString());
        JsonElement evidence = license.GetProperty("evidence");
        AssertExactProperties(
            evidence,
            "license page snapshot evidence",
            "kind", "source", "path", "sha256", "isStandaloneLicenseFile");
        Assert.AreEqual("official-license-modal-page-text-snapshot", evidence.GetProperty("kind").GetString());
        Assert.AreEqual(OfficialSourcePage, evidence.GetProperty("source").GetString());
        Assert.AreEqual(
            "artifacts/inputs/webview2/fixed-license-page-text.txt",
            evidence.GetProperty("path").GetString());
        AssertCanonicalRelativePath(evidence.GetProperty("path").GetString());
        Assert.AreEqual(
            "15bc46c641bc439514b3f0fe5290e0a5ef906bd8106030b53e1e8a142d6fc9d8",
            evidence.GetProperty("sha256").GetString());
        AssertCanonicalDigest(evidence.GetProperty("sha256").GetString());
        Assert.IsFalse(evidence.GetProperty("isStandaloneLicenseFile").GetBoolean());
    }

    [TestMethod]
    public void RuntimeManifest_KeepsRedistributionConditionsPending()
    {
        using JsonDocument document = ReadManifest();
        JsonElement license = document.RootElement.GetProperty("runtime").GetProperty("license");
        Assert.AreEqual("redistribution-review-pending", license.GetProperty("status").GetString());
        Assert.IsFalse(license.GetProperty("redistributionAccepted").GetBoolean());
        Assert.IsTrue(license.GetProperty("preserveThirdPartyNotices").GetBoolean());
        Assert.AreEqual(
            "Section 1(a): Installation and use on the licensee's own devices, " +
            "subject to compliance with the license terms.",
            license.GetProperty("localUseBasis").GetString());
        Assert.AreEqual(
            "Section 2(a): Object-code redistribution with applications is conditional on " +
            "the requirements and restrictions of the license.",
            license.GetProperty("objectCodeRedistributionBasis").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "Section 2(b)(i): Add significant primary functionality in the application.",
                "Section 2(b)(ii) and Section 8: Require distributors and external end users to accept terms " +
                    "protecting the code and Microsoft at least as much as this agreement, " +
                    "including the required SmartScreen notice when enabled.",
                "Section 2(b)(iii): Retain evidence that all redistributed code was acquired directly from Microsoft.",
                "Section 2(b)(iv): Indemnify, defend, and hold harmless Microsoft from claims, including attorneys' fees, " +
                    "related to application distribution or use, except claims based solely on unmodified distributable code.",
                "Section 2(c) and Section 3(e): No misleading Microsoft endorsement, prohibited source-license changes, " +
                    "public Microsoft download shortcuts, or standalone runtime offering.",
                "Section 1(c) and Section 3(c): Preserve Microsoft and third-party notices and satisfy applicable third-party terms.",
            },
            license.GetProperty("pendingRedistributionConditions")
                .EnumerateArray()
                .Select(static condition => condition.GetString())
                .ToArray());
    }

    [TestMethod]
    public void RuntimeManifest_DoesNotClaimReleaseApprovalOrCleanMachineAcceptance()
    {
        using JsonDocument document = ReadManifest();
        Assert.IsFalse(document.RootElement.GetProperty("releaseApproved").GetBoolean());
        CollectionAssert.AreEqual(
            new[]
            {
                "OSL-006/OSL-007/OSL-009 must verify assembly and explicit bundled-runtime selection against this manifest.",
                "OSL-017/Q-010 redistribution conditions, notices, SBOM, vulnerability and legal review remain pending; " +
                    "selected Authenticode evidence is not general release approval.",
                "OSL-020/OSL-021 clean Windows launch, offline startup, signing and distribution acceptance remain uncompleted; " +
                    "this manifest and its architecture tests do not execute the runtime.",
                "The application distributor must pin, verify and ship Fixed Runtime updates; " +
                    "there is no runtime auto-update or user download shortcut.",
            },
            document.RootElement.GetProperty("remainingGates")
                .EnumerateArray()
                .Select(static gate => gate.GetString())
                .ToArray());
    }

    private static JsonDocument ReadManifest() =>
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(GetRepositoryRoot(), "packaging", "webview2", "runtime-manifest.json")),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });

    private static void AssertCanonicalDigest(string? value)
    {
        Assert.IsNotNull(value);
        Assert.IsTrue(value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static void AssertCanonicalRelativePath(string? value)
    {
        Assert.IsNotNull(value);
        Assert.IsFalse(string.IsNullOrWhiteSpace(value));
        Assert.IsFalse(Path.IsPathRooted(value));
        Assert.AreEqual(value.Trim(), value);
        Assert.IsFalse(value.IndexOfAny(new[] { '\\', ':', '\0', '\r', '\n' }) >= 0);
        Assert.IsTrue(value.Split('/').All(static segment =>
            segment.Length > 0 && segment is not "." and not ".."));
    }

    private static void AssertExactProperties(JsonElement element, string context, params string[] expected)
    {
        string[] actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected.Order(StringComparer.Ordinal).ToArray(), actual, context);
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}