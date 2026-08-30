using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HubRadio.Architecture.Tests;

[TestClass]
public sealed class PythonRuntimePackageManifestTests
{
    private const string RuntimeArchiveSha256 =
        "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3";
    private const string RuntimeWheelSetSha256 =
        "a08c4fafe13d834fdbdbdf031774b0b57f7dd03227700a8b525d0193f707f141";
    private const string WorkerSourceSetSha256 =
        "6af6e608d0778200d9b55393384af477b6ae3b3e28a251cba04f146314ba430c";

    [TestMethod]
    public void RuntimeManifest_PinsOfficialWindowsX64EmbeddableArtifactAndIsolation()
    {
        string root = GetRepositoryRoot();
        using JsonDocument document = ReadJson(
            Path.Combine(root, "packaging", "python", "runtime-manifest.json"));
        JsonElement manifest = document.RootElement;
        AssertExactProperties(
            manifest,
            "runtime manifest",
            "schemaVersion",
            "evidenceDate",
            "runtime",
            "isolation",
            "wheelInput",
            "workerSource",
            "output",
            "releaseApproved",
            "remainingGates");
        Assert.AreEqual("1.0.0", manifest.GetProperty("schemaVersion").GetString());
        Assert.IsFalse(manifest.GetProperty("releaseApproved").GetBoolean());

        JsonElement runtime = manifest.GetProperty("runtime");
        Assert.AreEqual("CPython", runtime.GetProperty("identity").GetString());
        Assert.AreEqual("3.12.10", runtime.GetProperty("version").GetString());
        Assert.AreEqual("Windows", runtime.GetProperty("operatingSystem").GetString());
        Assert.AreEqual("x64", runtime.GetProperty("architecture").GetString());
        Assert.AreEqual(
            "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
            runtime.GetProperty("source").GetString());
        Assert.AreEqual("python-3.12.10-embed-amd64.zip", runtime.GetProperty("fileName").GetString());
        Assert.AreEqual(11_133_606, runtime.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(RuntimeArchiveSha256, runtime.GetProperty("sha256").GetString());
        Assert.AreEqual(35, runtime.GetProperty("archiveEntryCount").GetInt32());
        string[] archiveEntries = runtime.GetProperty("archiveEntries")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        Assert.HasCount(35, archiveEntries);
        CollectionAssert.AreEqual(
            archiveEntries.Order(StringComparer.Ordinal).ToArray(),
            archiveEntries);
        CollectionAssert.Contains(archiveEntries, "python.exe");
        CollectionAssert.Contains(archiveEntries, "python312.dll");
        CollectionAssert.Contains(archiveEntries, "python312.zip");
        CollectionAssert.Contains(archiveEntries, "LICENSE.txt");

        foreach (JsonElement signedFile in runtime.GetProperty("selectedSignedFiles").EnumerateArray())
        {
            Assert.AreEqual("Valid", signedFile.GetProperty("signatureStatus").GetString());
            Assert.AreEqual(
                "CN=Python Software Foundation, O=Python Software Foundation, L=Beaverton, S=Oregon, C=US",
                signedFile.GetProperty("signerSubject").GetString());
            Assert.AreEqual(
                "DE01DAAE82D04F466A576E178F6B07A839238953",
                signedFile.GetProperty("signerThumbprint").GetString());
            AssertCanonicalDigest(signedFile.GetProperty("sha256").GetString());
        }

        JsonElement isolation = manifest.GetProperty("isolation");
        CollectionAssert.AreEqual(
            new[] { "-I", "-B" },
            isolation.GetProperty("pythonArguments")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.IsFalse(isolation.GetProperty("importSiteEnabled").GetBoolean());
        Assert.IsFalse(isolation.GetProperty("pipIncludedAtRuntime").GetBoolean());
        Assert.IsFalse(isolation.GetProperty("systemPathRequired").GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "python312.zip", ".", "Lib", "Lib\\site-packages" },
            isolation.GetProperty("pthLines")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "python312.zip", ".", "Lib", "Lib\\site-packages" },
            File.ReadAllLines(Path.Combine(root, "packaging", "python", "python312._pth")));

        JsonElement output = manifest.GetProperty("output");
        Assert.AreEqual(2694, output.GetProperty("expectedFileCount").GetInt32());
        Assert.AreEqual(268_422_987, output.GetProperty("expectedTotalBytes").GetInt64());
        Assert.AreEqual(
            "4ed693b55b4b2198f549b5bdce1b68cb8a77e1167cdb6c3721a5d0fa44baa905",
            output.GetProperty("expectedArtifactSetSha256").GetString());
    }

    [TestMethod]
    public void RuntimeManifest_MatchesRuntimeWheelMatrixExactly()
    {
        string root = GetRepositoryRoot();
        string manifestPath = Path.Combine(root, "packaging", "python", "runtime-manifest.json");
        string matrixPath = Path.Combine(root, "src", "HubRadio.Worker", "wheel-matrix.json");
        using JsonDocument manifestDocument = ReadJson(manifestPath);
        using JsonDocument matrixDocument = ReadJson(matrixPath);
        JsonElement wheelInput = manifestDocument.RootElement.GetProperty("wheelInput");

        Assert.AreEqual(
            Sha256(File.ReadAllBytes(matrixPath)),
            wheelInput.GetProperty("matrixSha256").GetString());
        JsonElement[] runtimeWheels = matrixDocument.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .Where(static package => package.GetProperty("lock").GetString() == "runtime")
            .OrderBy(static package => package.GetProperty("fileName").GetString(), StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(25, runtimeWheels.Length);
        Assert.AreEqual(
            runtimeWheels.Sum(static package => package.GetProperty("byteSize").GetInt64()),
            wheelInput.GetProperty("runtimeWheelBytes").GetInt64());
        Assert.AreEqual(
            RuntimeWheelSetSha256,
            ComputeSetDigest(runtimeWheels, "fileName"));
        Assert.AreEqual(
            RuntimeWheelSetSha256,
            wheelInput.GetProperty("runtimeWheelSetSha256").GetString());
    }

    [TestMethod]
    public void RuntimeManifest_MatchesTrackedWorkerSourceExactly()
    {
        string root = GetRepositoryRoot();
        using JsonDocument document = ReadJson(
            Path.Combine(root, "packaging", "python", "runtime-manifest.json"));
        JsonElement worker = document.RootElement.GetProperty("workerSource");
        string workerRoot = Path.Combine(root, worker.GetProperty("root").GetString()!);
        JsonElement[] expectedFiles = worker.GetProperty("files")
            .EnumerateArray()
            .OrderBy(static file => file.GetProperty("path").GetString(), StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(5, expectedFiles.Length);

        string[] actualPaths = Directory
            .EnumerateFiles(Path.Combine(workerRoot, "hubradio_worker"), "*.py", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workerRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedFiles.Select(static file => file.GetProperty("path").GetString()).ToArray(),
            actualPaths);

        foreach (JsonElement file in expectedFiles)
        {
            string path = Path.Combine(
                workerRoot,
                file.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.AreEqual(file.GetProperty("byteSize").GetInt64(), new FileInfo(path).Length);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Sha256(File.ReadAllBytes(path)));
        }

        Assert.AreEqual(WorkerSourceSetSha256, ComputeSetDigest(expectedFiles, "path"));
        Assert.AreEqual(
            WorkerSourceSetSha256,
            worker.GetProperty("artifactSetSha256").GetString());
    }

    [TestMethod]
    public void RuntimeBuildAndWorkerLaunchRemainOfflineAndIsolated()
    {
        string root = GetRepositoryRoot();
        string buildScript = File.ReadAllText(
            Path.Combine(root, "packaging", "python", "build-runtime.ps1"));
        string verifyScript = File.ReadAllText(
            Path.Combine(root, "packaging", "python", "verify-runtime.ps1"));
        string workerClient = File.ReadAllText(
            Path.Combine(root, "src", "HubRadio.Infrastructure", "Worker", "WorkerClient.cs"));

        foreach (string forbidden in new[]
        {
            "Invoke-WebRequest",
            "Invoke-RestMethod",
            "HttpClient",
            "Start-BitsTransfer",
        })
        {
            Assert.IsFalse(buildScript.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(verifyScript.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string required in new[]
        {
            "--require-hashes",
            "--only-binary=:all:",
            "--no-index",
            "--no-deps",
            "--no-compile",
            "--no-cache-dir",
            "[IO.Directory]::Move",
        })
        {
            Assert.IsTrue(buildScript.Contains(required, StringComparison.Ordinal), required);
        }

        Assert.IsTrue(verifyScript.Contains("$startInfo.Environment.Clear()", StringComparison.Ordinal));
        Assert.IsTrue(verifyScript.Contains("$startInfo.Environment['PATH'] = ''", StringComparison.Ordinal));
        Assert.IsTrue(verifyScript.Contains("HF_HUB_OFFLINE", StringComparison.Ordinal));
        Assert.IsTrue(verifyScript.Contains("TRANSFORMERS_OFFLINE", StringComparison.Ordinal));
        Assert.IsTrue(workerClient.Contains(
            "[\"-I\", \"-B\", \"-m\", \"hubradio_worker\", \"--protocol\"]",
            StringComparison.Ordinal));
    }

    private static string ComputeSetDigest(IEnumerable<JsonElement> entries, string pathProperty)
    {
        var builder = new StringBuilder();
        foreach (JsonElement entry in entries.OrderBy(
            entry => entry.GetProperty(pathProperty).GetString(),
            StringComparer.Ordinal))
        {
            builder.Append(entry.GetProperty(pathProperty).GetString());
            builder.Append('\0');
            builder.Append(entry.GetProperty("byteSize").GetInt64().ToString(CultureInfo.InvariantCulture));
            builder.Append('\0');
            builder.Append(entry.GetProperty("sha256").GetString());
            builder.Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static JsonDocument ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertCanonicalDigest(string? value)
    {
        Assert.IsNotNull(value);
        Assert.IsTrue(value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static void AssertExactProperties(
        JsonElement element,
        string context,
        params string[] expected)
    {
        string[] actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual,
            context);
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