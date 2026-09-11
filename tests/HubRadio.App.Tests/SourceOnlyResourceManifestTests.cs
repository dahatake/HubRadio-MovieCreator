using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Tests;

// Data/handle tests only. Synthetic manifests never establish vendor approval and
// no Worker, FFmpeg, model, rendering, native App or build process is launched.
[TestClass]
public sealed class SourceOnlyResourceManifestTests
{
    [TestMethod]
    public void ApplicationDigestIsBuildIdentityOfTheNamedDllNotOfTheBundleExecutable()
    {
        using var fixture = new ResourceFixture();
        byte[] bytes = [1, 2, 3];
        byte[] manifest = ApplicationManifest(bytes, "1.2.3.4");
        BinaryVerificationRequest request = SourceOnlyResourceManifest.ReadApplicationBuild(manifest, fixture.Root, "1.2.3.4");
        Assert.AreEqual(Path.Combine(fixture.Root, "HubRadio.App.dll"), request.FilePath);
        Assert.AreEqual("source-only.application-build", request.ComponentIdentity);
        Assert.AreEqual(3L, request.ExpectedByteSize);
        ContentDigest actualDigest = request.ExpectedDigest;
        Assert.AreEqual(ContentDigest.Compute(bytes), actualDigest);
        BinaryVerificationRequest bom = SourceOnlyResourceManifest.ReadApplicationBuild(
            [0xef, 0xbb, 0xbf, .. manifest], fixture.Root, "1.2.3.4");
        Assert.AreEqual(request.ExpectedDigest, bom.ExpectedDigest, "Accept the UTF-8 preamble emitted by build tooling.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "Evidence")));
    }

    [TestMethod]
    [DataRow("relativePath", "\"HubRadio.App.exe\"")]
    [DataRow("relativePath", "\"../HubRadio.App.dll\"")]
    [DataRow("relativePath", "\"C:/private/HubRadio.App.dll\"")]
    [DataRow("relativePath", "\"HubRadio.App.dll:stream\"")]
    [DataRow("schemaVersion", "2")]
    [DataRow("version", "\"1.2.3.5\"")]
    [DataRow("size", "0")]
    [DataRow("sha256", "null")]
    [DataRow("releaseApproved", "true")]
    public void ApplicationManifestRejectsWrongIdentityPathsMissingPinsAndApprovalClaims(string field, string value)
    {
        using var fixture = new ResourceFixture();
        JsonObject manifest = JsonNode.Parse(ApplicationManifest([1, 2, 3], "1.2.3.4"))!.AsObject();
        manifest[field] = JsonNode.Parse(value);
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadApplicationBuild(
            Encoding.UTF8.GetBytes(manifest.ToJsonString()), fixture.Root, "1.2.3.4"));
    }

    [TestMethod]
    public async Task PackageApplicationBuildIsReadFromEvidenceAndBothFilesRemainHeld()
    {
        using var fixture = new ResourceFixture();
        string dll = Path.Combine(fixture.Root, "HubRadio.App.dll");
        File.Copy(typeof(SourceOnlyApplicationRuntime).Assembly.Location, dll);
        byte[] bytes = File.ReadAllBytes(dll);
        string manifest = fixture.Write(SourceOnlyResourceManifest.ApplicationBuildPath,
            ApplicationManifest(bytes, fixture.Resources.ApplicationVersion));
        await using (var lease = new SourceOnlyApplicationResourceLease())
        {
            BinaryVerificationRequest request = await lease.ResolveApplicationBuildAsync(fixture.Resources, CancellationToken.None);
            ContentDigest actualDigest = request.ExpectedDigest;
            Assert.AreEqual(ContentDigest.Compute(bytes), actualDigest);
            Assert.ThrowsExactly<IOException>(() => { using FileStream _ = File.OpenWrite(dll); });
            Assert.ThrowsExactly<IOException>(() => File.Delete(manifest));
            Exception? moveFailure = null;
            try { Directory.Move(fixture.Root, fixture.Root + "-moved"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { moveFailure = exception; }
            Assert.IsNotNull(moveFailure, "An in-use resource ancestor must not be replaceable.");
        }
        using FileStream writable = File.OpenWrite(dll);
        Assert.IsTrue(writable.CanWrite, "The resource lease must release its handles at shutdown.");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("stale")]
    [DataRow("duplicate")]
    [DataRow("non-assembly")]
    public async Task InvalidPackageApplicationEvidenceDoesNotFallbackToSelfHashAndHidesPaths(string kind)
    {
        using var fixture = new ResourceFixture();
        string dll = fixture.Write("HubRadio.App.dll", [1, 2, 3]);
        fixture.Write("HubRadio.App.exe", [4, 5, 6]);
        if (kind != "missing")
        {
            byte[] bytes = ApplicationManifest(kind == "stale" ? [9, 8, 7] : [1, 2, 3], fixture.Resources.ApplicationVersion);
            if (kind == "duplicate") { bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal)); }
            fixture.Write(SourceOnlyResourceManifest.ApplicationBuildPath, bytes);
        }
        await using var lease = new SourceOnlyApplicationResourceLease();
        SourceOnlyApplicationDependencyException error = await Assert.ThrowsAsync<SourceOnlyApplicationDependencyException>(
            () => lease.ResolveApplicationBuildAsync(fixture.Resources, CancellationToken.None));
        Assert.AreEqual(SourceOnlyApplicationDependency.ApplicationEvidence, error.Dependency);
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.ToString().Contains(fixture.Root, StringComparison.Ordinal));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dll));
    }

    [TestMethod]
    public async Task MissingFontOnlyRejectsResourceInitializationWithoutCreatingOrSearchingFonts()
    {
        using var fixture = new ResourceFixture();
        await using var lease = new SourceOnlyApplicationResourceLease();
        SourceOnlyApplicationDependencyException error = await Assert.ThrowsAsync<SourceOnlyApplicationDependencyException>(
            () => lease.ResolveFontAsync(fixture.Resources, CancellationToken.None));
        Assert.AreEqual(SourceOnlyApplicationDependency.Font, error.Dependency);
        Assert.IsNull(error.InnerException);
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [TestMethod]
    public void FontBindingRequiresTheEmbeddedInputPinAndAnActualMatchingFamily()
    {
        using var fixture = new ResourceFixture();
        byte[] font = CreateNameTable("Noto Sans JP");
        byte[] source = FontManifest(font);
        var binding = SourceOnlyResourceManifest.ReadFont(source, source, fixture.Root);
        ContentDigest actualDigest = binding.Binding.VerificationRequest.ExpectedDigest;
        Assert.AreEqual(ContentDigest.Compute(font), actualDigest);
        Assert.AreEqual(Path.Combine(fixture.Root, "Fonts", "NotoSansJP-Regular.otf"), binding.Binding.VerificationRequest.FilePath);
        Assert.AreEqual(Path.Combine(fixture.Root, "Fonts", "LICENSE"), binding.License.FilePath);
        SourceOnlyResourceManifest.ValidateFontNames(font);
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ValidateFontNames(CreateNameTable("Wrong Family")));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ValidateFontNames([1, 2, 3]));

        JsonObject forged = JsonNode.Parse(source)!.AsObject();
        forged["sha256"] = ContentDigest.Compute([8, 9, 10]).Hex;
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadFont(
            Encoding.UTF8.GetBytes(forged.ToJsonString()), source, fixture.Root));
        forged["sha256"] = null;
        byte[] unfilled = Encoding.UTF8.GetBytes(forged.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadFont(unfilled, unfilled, fixture.Root));
    }

    [TestMethod]
    public void WorkerBuildChangesAreAllowedOnlyWhenTheTrustedInputDeclarationChangesWithThem()
    {
        var prior = WorkerManifests([1, 2, 3]);
        var current = WorkerManifests([4, 5, 6, 7]);
        Assert.HasCount(2, SourceOnlyResourceManifest.ReadWorker(prior.Built, prior.Source));
        Assert.HasCount(2, SourceOnlyResourceManifest.ReadWorker(current.Built, current.Source));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadWorker(prior.Built, current.Source));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadWorker(current.Built, prior.Source));
        // Even a self-consistent build declaration cannot change the CPython pin.
        var forged = WorkerManifests([4, 5, 6], ContentDigest.Compute([9, 9, 9]).Hex);
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadWorker(forged.Built, forged.Source));
    }

    [TestMethod]
    [DataRow("../escape.py")]
    [DataRow("Lib\\escape.py")]
    [DataRow("Lib/file.py:stream")]
    [DataRow("Lib/NUL.py")]
    [DataRow("Lib/CONOUT$")]
    [DataRow("Lib/file.py.")]
    [DataRow("/rooted.py")]
    [DataRow("runtime-files.json")]
    [DataRow("PYTHON.EXE")]
    public void WorkerInventoryRejectsUnsafeOrSelfReferentialPathsEvenIfRehashed(string path)
    {
        var manifests = WorkerManifests([1, 2, 3], path: path);
        Assert.ThrowsExactly<InvalidDataException>(() => SourceOnlyResourceManifest.ReadWorker(manifests.Built, manifests.Source));
    }

    [TestMethod]
    public async Task ExtractedWritableAttributesAreAllowedButMissingAndExtraFilesAreNot()
    {
        using var fixture = new ResourceFixture();
        string path = fixture.Write("Lib/data", [1, 2, 3]);
        File.SetAttributes(path, FileAttributes.Normal);
        await using var lease = new SourceOnlyApplicationResourceLease();
        lease.ValidateTree(fixture.Root, ["Lib/data"], CancellationToken.None);
        Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.ThrowsExactly<InvalidDataException>(() => lease.ValidateTree(fixture.Root, ["Lib/data", "missing"], CancellationToken.None));
        fixture.Write("unlisted.py", [4]);
        Assert.ThrowsExactly<InvalidDataException>(() => lease.ValidateTree(fixture.Root, ["Lib/data"], CancellationToken.None));
    }

    [TestMethod]
    public async Task PreCanceledResourceInitializationDoesNotAcquireFiles()
    {
        using var fixture = new ResourceFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var lease = new SourceOnlyApplicationResourceLease();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.ResolveFontAsync(fixture.Resources, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.ResolveApplicationBuildAsync(fixture.Resources, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.VerifyWorkerAsync(fixture.Root, cancellation.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Root));
    }

    private static byte[] ApplicationManifest(byte[] bytes, string version) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        relativePath = "HubRadio.App.dll",
        version,
        size = bytes.LongLength,
        sha256 = ContentDigest.Compute(bytes).Hex,
    });

    private static byte[] FontManifest(byte[] bytes) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        family = "Noto Sans JP",
        style = "Regular",
        version = "2.004",
        sourceCommit = SourceOnlyResourceManifest.FontSourceCommit,
        sourcePath = "Sans/SubsetOTF/JP/NotoSansJP-Regular.otf",
        relativePath = "NotoSansJP-Regular.otf",
        byteSize = bytes.LongLength,
        sha256 = ContentDigest.Compute(bytes).Hex,
        license = new { relativePath = "LICENSE", sourcePath = "LICENSE", spdxId = "OFL-1.1", byteSize = 1, sha256 = ContentDigest.Compute([1]).Hex },
        releaseApproved = false,
    });

    private static byte[] CreateNameTable(string family)
    {
        string[] values = [family, "Regular", "NotoSansJP-Regular"];
        int[] ids = [1, 2, 6];
        byte[][] strings = values.Select(Encoding.BigEndianUnicode.GetBytes).ToArray();
        int storage = 6 + values.Length * 12;
        int length = storage + strings.Sum(value => value.Length);
        byte[] result = new byte[28 + length];
        "OTTO"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), 1);
        "name"u8.CopyTo(result.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), 28);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(24), (uint)length);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(30), (ushort)values.Length);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(32), (ushort)storage);
        int offset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> record = result.AsSpan(34 + i * 12, 12);
            BinaryPrimitives.WriteUInt16BigEndian(record, 3);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], (ushort)ids[i]);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)offset);
            strings[i].CopyTo(result, 28 + storage + offset);
            offset += strings[i].Length;
        }
        return result;
    }

    private static (byte[] Source, byte[] Built) WorkerManifests(byte[] worker, string? pythonSha256 = null,
        string path = "Lib/site-packages/hubradio_worker/__main__.py")
    {
        // A parser-only synthetic build, not a production runtime or authorization.
        var python = new
        {
            path = "python.exe",
            byteSize = SourceOnlyApplicationResourceLease.PythonByteSize,
            sha256 = pythonSha256 ?? SourceOnlyApplicationResourceLease.PythonSha256
        };
        var local = new { path, byteSize = worker.LongLength, sha256 = ContentDigest.Compute(worker).Hex };
        var files = new[] { python, local }.OrderBy(file => file.path,
            StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), false)).ToArray();
        string text = string.Concat(files.Select(file => file.path + '\0' + file.byteSize.ToString(CultureInfo.InvariantCulture) + '\0' + file.sha256 + '\n'));
        string digest = ContentDigest.Compute(Encoding.UTF8.GetBytes(text)).Hex;
        long total = files.Sum(file => file.byteSize);
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.0.0",
            runtime = new
            {
                identity = "CPython",
                version = "3.12.10",
                architecture = "x64",
                selectedSignedFiles = new[] { new { name = "python.exe", python.byteSize, python.sha256 } }
            },
            workerSource = new { files = Array.Empty<object>() },
            output = new
            {
                fileManifestName = "runtime-files.json",
                entryPoint = "python.exe",
                expectedFileCount = files.Length,
                expectedTotalBytes = total,
                expectedArtifactSetSha256 = digest
            },
        });
        byte[] built = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.0.0",
            runtimeIdentity = "CPython",
            runtimeVersion = "3.12.10",
            architecture = "x64",
            sourceManifestSha256 = ContentDigest.Compute(source).Hex,
            fileCount = files.Length,
            totalBytes = total,
            artifactSetSha256 = digest,
            files,
        });
        return (source, built);
    }

    private sealed class ResourceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.ResourceManifest-" + Guid.NewGuid().ToString("N"));
        internal SourceOnlyApplicationResources Resources => SourceOnlyApplicationResources.FromPackage(Root,
            Path.Combine(Root, "Profiles"), Path.Combine(Root, "DefaultSettings/Profiles"));
        internal ResourceFixture() => Directory.CreateDirectory(Root);
        internal string Write(string path, byte[] bytes)
        {
            string full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return full;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}