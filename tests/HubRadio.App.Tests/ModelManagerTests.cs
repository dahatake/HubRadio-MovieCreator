using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Models;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ModelManagerTests
{
    [TestMethod]
    public async Task PinnedCatalog_MatchesWorkerConstantsAndKeepsNonAdoptedModelsIneligible()
    {
        string repositoryRoot = FindRepositoryRoot();
        string catalogPath = Path.Combine(repositoryRoot, "packaging", "models", "catalog.json");
        byte[] catalogBytes = File.ReadAllBytes(catalogPath);
        Assert.AreEqual(
            ModelManager.PinnedCatalogSha256,
            ContentDigest.Compute(catalogBytes).Hex);

        string workerSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "HubRadio.Worker",
                "src",
                "hubradio_worker",
                "tasks",
                "transcribe.py"),
            Encoding.UTF8);
        using JsonDocument catalog = JsonDocument.Parse(catalogBytes);
        JsonElement runtimeModel = catalog.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Single(static model => model.GetProperty("runtimeEligible").GetBoolean());
        Assert.AreEqual(ModelManager.AdoptedAsrModelId, runtimeModel.GetProperty("modelId").GetString());
        Assert.IsTrue(workerSource.Contains(
            $"MODEL_REPOSITORY = \"{runtimeModel.GetProperty("identity").GetString()}\"",
            StringComparison.Ordinal));
        string revision = runtimeModel.GetProperty("version").GetString()!["revision ".Length..];
        Assert.IsTrue(workerSource.Contains(
            $"MODEL_REVISION = \"{revision}\"",
            StringComparison.Ordinal));
        Assert.IsTrue(workerSource.Contains(
            $"MODEL_MANIFEST_SHA256 = \"{runtimeModel.GetProperty("bundle").GetProperty("manifestSha256").GetString()}\"",
            StringComparison.Ordinal));

        using var fixture = new FileSystemFixture(createBundle: false);
        ModelManager manager = ModelManager.Load(
            catalogPath,
            fixture.BundleRoot,
            fixture.Workspace,
            new BinaryVerifier());
        IReadOnlyList<ModelStatus> statuses = await manager.InspectAllAsync();

        Assert.HasCount(12, statuses);
        ModelStatus adopted = statuses.Single(static status =>
            status.ModelId == ModelManager.AdoptedAsrModelId);
        Assert.AreEqual(ModelDisposition.AdoptedConditional, adopted.Disposition);
        Assert.AreEqual(ModelConsentStatus.NotRequired, adopted.ConsentStatus);
        Assert.AreEqual(ModelAvailability.Missing, adopted.Availability);
        Assert.IsTrue(adopted.RuntimeEligible);
        Assert.IsFalse(adopted.ReleaseApproved);
        ModelManagerException missing =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                manager.RequireOfflinePathAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.ModelMissing, missing.Reason);

        Assert.IsTrue(statuses
            .Where(static status => status.ModelId != ModelManager.AdoptedAsrModelId)
            .All(static status => !status.RuntimeEligible));
        Assert.AreEqual(
            ModelAvailability.ConsentRequired,
            statuses.Single(static status =>
                status.ModelId == "diarization.pyannote-community-1").Availability);
        Assert.AreEqual(
            ModelAvailability.NotEligible,
            statuses.Single(static status =>
                status.ModelId == "text.qwen3-0.6b-cuda").Availability);
    }

    [TestMethod]
    public async Task InstallFromBundleAsync_PromotesExactReadOnlyFilesAndReturnsOfflinePath()
    {
        using var fixture = new FileSystemFixture();

        string installedPath = await fixture.Manager.InstallFromBundleAsync(
            ModelManager.AdoptedAsrModelId);
        string requiredPath = await fixture.Manager.RequireOfflinePathAsync(
            ModelManager.AdoptedAsrModelId);
        ModelStatus status = await fixture.Manager.InspectAsync(
            ModelManager.AdoptedAsrModelId);

        Assert.AreEqual(installedPath, requiredPath);
        Assert.AreEqual(ModelAvailability.Ready, status.Availability);
        CollectionAssert.AreEquivalent(
            FixtureModelFiles.Keys.ToArray(),
            Directory.EnumerateFiles(installedPath).Select(Path.GetFileName).ToArray());
        foreach ((string name, byte[] expected) in FixtureModelFiles)
        {
            string path = Path.Combine(installedPath, name);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
            }
        }

        string[] stagingEntries = Directory.Exists(fixture.StagingRoot)
            ? Directory.GetFileSystemEntries(fixture.StagingRoot)
            : [];
        Assert.HasCount(0, stagingEntries);
    }

    [TestMethod]
    public async Task InstallFromBundleAsync_IsIdempotentAndConcurrentWithinManager()
    {
        using var fixture = new FileSystemFixture();

        Task<string> first = fixture.Manager.InstallFromBundleAsync(
            ModelManager.AdoptedAsrModelId);
        Task<string> second = fixture.Manager.InstallFromBundleAsync(
            ModelManager.AdoptedAsrModelId);
        string[] paths = await Task.WhenAll(first, second);
        string third = await fixture.Manager.InstallFromBundleAsync(
            ModelManager.AdoptedAsrModelId);

        Assert.AreEqual(paths[0], paths[1]);
        Assert.AreEqual(paths[0], third);
        Assert.HasCount(2, Directory.EnumerateFiles(third).ToArray());
    }

    [TestMethod]
    public async Task InstallFromBundleAsync_DistinguishesMissingAndInvalidBundleWithoutCacheOutput()
    {
        using (var missing = new FileSystemFixture(createBundle: false))
        {
            ModelManagerException exception =
                await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                    missing.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
            Assert.AreEqual(ModelManagerFailureReason.BundleMissing, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsFalse(Directory.Exists(missing.ModelCachePath));
        }

        using (var corrupt = new FileSystemFixture())
        {
            File.WriteAllBytes(corrupt.BundleFile("model.bin"), [0xff]);
            ModelManagerException exception =
                await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                    corrupt.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
            Assert.AreEqual(ModelManagerFailureReason.BundleInvalid, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsFalse(Directory.Exists(corrupt.ModelCachePath));
        }

        using (var extra = new FileSystemFixture())
        {
            File.WriteAllText(
                Path.Combine(extra.BundlePath, "unlisted.txt"),
                "not in the manifest",
                Encoding.UTF8);
            ModelManagerException exception =
                await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                    extra.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
            Assert.AreEqual(ModelManagerFailureReason.BundleInvalid, exception.Reason);
            Assert.IsFalse(Directory.Exists(extra.ModelCachePath));
        }
    }

    [TestMethod]
    public async Task InstalledCache_TamperIsCorruptAndNeverSilentlyReplaced()
    {
        using var fixture = new FileSystemFixture();
        string installedPath = await fixture.Manager.InstallFromBundleAsync(
            ModelManager.AdoptedAsrModelId);
        string installedFile = Path.Combine(installedPath, "config.json");
        File.SetAttributes(installedFile, FileAttributes.Normal);
        File.WriteAllText(installedFile, "tampered", Encoding.UTF8);

        ModelStatus status = await fixture.Manager.InspectAsync(
            ModelManager.AdoptedAsrModelId);
        Assert.AreEqual(ModelAvailability.Corrupt, status.Availability);

        ModelManagerException requireFailure =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.RequireOfflinePathAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.CacheCorrupt, requireFailure.Reason);

        ModelManagerException installFailure =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.CacheCorrupt, installFailure.Reason);
        Assert.AreEqual("tampered", File.ReadAllText(installedFile, Encoding.UTF8));
    }

    [TestMethod]
    public async Task ExistingNonDirectoryCacheDestination_IsCorruptAndNotOverwritten()
    {
        using var fixture = new FileSystemFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ModelCachePath)!);
        File.WriteAllText(fixture.ModelCachePath, "occupied", Encoding.UTF8);

        ModelStatus status = await fixture.Manager.InspectAsync(ModelManager.AdoptedAsrModelId);
        Assert.AreEqual(ModelAvailability.Corrupt, status.Availability);
        ModelManagerException exception =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.CacheCorrupt, exception.Reason);
        Assert.AreEqual("occupied", File.ReadAllText(fixture.ModelCachePath, Encoding.UTF8));
    }

    [TestMethod]
    public async Task InstallFromBundleAsync_RejectsHeldConditionFalseAndUnknownModelsPrivately()
    {
        using var fixture = new FileSystemFixture();

        ModelManagerException held =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InstallFromBundleAsync("vision.held"));
        Assert.AreEqual(ModelManagerFailureReason.ModelNotEligible, held.Reason);

        ModelStatus blockedStatus = await fixture.Manager.InspectAsync("diarization.blocked");
        Assert.AreEqual(ModelAvailability.ConsentRequired, blockedStatus.Availability);
        ModelManagerException blocked =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InstallFromBundleAsync("diarization.blocked"));
        Assert.AreEqual(ModelManagerFailureReason.ConsentRequired, blocked.Reason);

        const string privateUnknown = @"C:\Users\private\Bearer-secret";
        ModelManagerException unknown =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InspectAsync(privateUnknown));
        Assert.AreEqual(ModelManagerFailureReason.ModelUnknown, unknown.Reason);
        AssertPrivate(unknown, privateUnknown);
        AssertPrivate(held, "vision.held");
        AssertPrivate(blocked, "diarization.blocked");
    }

    [TestMethod]
    public async Task InstallFromBundleAsync_PreCanceledRequestLeavesNoCacheOrStaging()
    {
        using var fixture = new FileSystemFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Manager.InstallFromBundleAsync(
                ModelManager.AdoptedAsrModelId,
                cancellation.Token));

        Assert.IsFalse(Directory.Exists(fixture.ModelCachePath));
        Assert.IsFalse(Directory.Exists(fixture.StagingRoot));
    }

    [TestMethod]
    public void Load_SemanticallyEquivalentButByteChangedPinnedCatalogIsRejected()
    {
        string repositoryRoot = FindRepositoryRoot();
        byte[] original = File.ReadAllBytes(
            Path.Combine(repositoryRoot, "packaging", "models", "catalog.json"));
        using var fixture = new FileSystemFixture(createBundle: false);
        string changedCatalog = Path.Combine(fixture.Root, "changed-catalog.json");
        File.WriteAllBytes(changedCatalog, [.. original, (byte)'\n']);

        ModelManagerException exception = Assert.ThrowsExactly<ModelManagerException>(() =>
            ModelManager.Load(
                changedCatalog,
                fixture.BundleRoot,
                fixture.Workspace,
                new BinaryVerifier()));

        Assert.AreEqual(ModelManagerFailureReason.CatalogInvalid, exception.Reason);
        AssertPrivate(exception, changedCatalog);
    }

    [TestMethod]
    public void CatalogParser_RejectsNetworkSilentUpgradeReleaseAndNonRuntimeBundle()
    {
        using var fixture = new FileSystemFixture();

        foreach (byte[] invalid in new[]
        {
            fixture.BuildCatalog(networkAcquisitionEnabled: true),
            fixture.BuildCatalog(silentUpgradeEnabled: true),
            fixture.BuildCatalog(releaseApproved: true),
            fixture.BuildCatalog(includeHeldBundle: true),
            fixture.BuildCatalog(bundleRelativePath: "../escape"),
            fixture.BuildCatalog(manifestDigestOverride: new string('0', 64)),
        })
        {
            ModelManagerException exception = Assert.ThrowsExactly<ModelManagerException>(() =>
                ModelManager.CreateForTesting(
                    invalid,
                    fixture.BundleRoot,
                    fixture.Workspace,
                    new BinaryVerifier()));
            Assert.AreEqual(ModelManagerFailureReason.CatalogInvalid, exception.Reason);
        }
    }

    [TestMethod]
    public void CatalogParser_RejectsUnknownAndDuplicateProperties()
    {
        using var fixture = new FileSystemFixture();
        string valid = Encoding.UTF8.GetString(fixture.CatalogBytes);
        string unknown = valid.Replace(
            "\"schemaVersion\":\"1.0.0\"",
            "\"schemaVersion\":\"1.0.0\",\"unexpected\":true",
            StringComparison.Ordinal);
        string duplicate = valid.Replace(
            "\"catalogId\":\"hubradio-model-catalog\"",
            "\"catalogId\":\"hubradio-model-catalog\",\"catalogId\":\"other\"",
            StringComparison.Ordinal);

        foreach (string invalid in new[] { unknown, duplicate })
        {
            ModelManagerException exception = Assert.ThrowsExactly<ModelManagerException>(() =>
                ModelManager.CreateForTesting(
                    Encoding.UTF8.GetBytes(invalid),
                    fixture.BundleRoot,
                    fixture.Workspace,
                    new BinaryVerifier()));
            Assert.AreEqual(ModelManagerFailureReason.CatalogInvalid, exception.Reason);
        }
    }

    [TestMethod]
    public async Task ReparsePointCacheDestination_IsRejectedWhenLinksAreAvailable()
    {
        using var fixture = new FileSystemFixture();
        string target = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ModelCachePath)!);
        try
        {
            _ = Directory.CreateSymbolicLink(fixture.ModelCachePath, target);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
            return;
        }

        ModelManagerException inspectFailure =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InspectAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.UnsafePath, inspectFailure.Reason);
        ModelManagerException installFailure =
            await Assert.ThrowsExactlyAsync<ModelManagerException>(() =>
                fixture.Manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId));
        Assert.AreEqual(ModelManagerFailureReason.UnsafePath, installFailure.Reason);
        Assert.HasCount(0, Directory.EnumerateFileSystemEntries(target).ToArray());
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndContainsNoNetworkAcquisitionApi()
    {
        foreach (Type type in new[] { typeof(ModelStatus) })
        {
            Assert.IsTrue(type.IsPublic);
            Assert.IsTrue(type.IsSealed);
            Assert.IsTrue(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null));
            Assert.HasCount(0, type.GetConstructors());
        }

        Assert.HasCount(0, typeof(ModelManagerException).GetConstructors());
        Assert.IsTrue(typeof(ModelManager)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .All(static method =>
                !method.Name.Contains("Download", StringComparison.OrdinalIgnoreCase) &&
                !method.Name.Contains("Network", StringComparison.OrdinalIgnoreCase) &&
                method.GetParameters().All(static parameter =>
                    parameter.ParameterType != typeof(Uri))));
        Assert.IsTrue(typeof(ModelManager)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .All(static field =>
                field.FieldType.FullName is not "System.Net.Http.HttpClient"));

        foreach (ModelManagerFailureReason reason in Enum.GetValues<ModelManagerFailureReason>())
        {
            ConstructorInfo constructor = typeof(ModelManagerException)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
            var exception = (ModelManagerException)constructor.Invoke([reason, false]);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            AssertPrivate(exception, @"C:\Users\private\Bearer-secret");
        }
    }

    private static readonly IReadOnlyDictionary<string, byte[]> FixtureModelFiles =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{\"fixture\":true}"),
            ["model.bin"] = [0x00, 0x01, 0x02, 0xfe, 0xff],
        };

    private static void AssertPrivate(Exception exception, string privateValue)
    {
        Assert.IsFalse(exception.ToString().Contains(privateValue, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(
            "Bearer-secret",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\Users\private",
            StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HubRadio.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }

    private sealed class FileSystemFixture : IDisposable
    {
        internal FileSystemFixture(bool createBundle = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ModelManagerTests",
                Guid.NewGuid().ToString("N"));
            Workspace = Path.Combine(Root, "workspace");
            BundleRoot = Path.Combine(Root, "package");
            BundlePath = Path.Combine(BundleRoot, "Bundles", "runtime");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(BundleRoot);
            if (createBundle)
            {
                Directory.CreateDirectory(BundlePath);
                foreach ((string name, byte[] bytes) in FixtureModelFiles)
                {
                    File.WriteAllBytes(Path.Combine(BundlePath, name), bytes);
                }
            }

            CatalogBytes = BuildCatalog();
            Manager = ModelManager.CreateForTesting(
                CatalogBytes,
                BundleRoot,
                Workspace,
                new BinaryVerifier());
        }

        internal string Root { get; }

        internal string Workspace { get; }

        internal string BundleRoot { get; }

        internal string BundlePath { get; }

        internal byte[] CatalogBytes { get; }

        internal ModelManager Manager { get; }

        internal string ModelCachePath => Path.Combine(
            Workspace,
            "cache",
            "models",
            ModelManager.AdoptedAsrModelId);

        internal string StagingRoot => Path.Combine(
            Workspace,
            "cache",
            "models",
            ".staging");

        internal string BundleFile(string name) => Path.Combine(BundlePath, name);

        internal byte[] BuildCatalog(
            bool networkAcquisitionEnabled = false,
            bool silentUpgradeEnabled = false,
            bool releaseApproved = false,
            bool includeHeldBundle = false,
            string bundleRelativePath = "Bundles/runtime",
            string? manifestDigestOverride = null)
        {
            object[] files = FixtureModelFiles
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => (object)new
                {
                    name = item.Key,
                    byteSize = item.Value.LongLength,
                    sha256 = ContentDigest.Compute(item.Value).Hex,
                })
                .ToArray();
            long totalBytes = FixtureModelFiles.Sum(static item => item.Value.LongLength);
            string manifestDigest = ComputeManifestDigest(FixtureModelFiles);
            object runtimeBundle = new
            {
                relativePath = bundleRelativePath,
                manifestSha256 = manifestDigestOverride ?? manifestDigest,
                totalBytes,
                files,
            };
            object license = new
            {
                status = "test-evidence",
                declaredSpdx = "MIT",
                evidence = "tests/fixture",
                evidenceSha256 = (string?)null,
                releaseNoticeComplete = false,
            };
            object artifact = new
            {
                kind = "artifact-manifest",
                sha256 = manifestDigest,
                byteSize = totalBytes,
                fileCount = files.Length,
            };

            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0.0",
                catalogId = "hubradio-model-catalog",
                generatedFrom = "docs/dev/licenses/models.csv",
                policy = new
                {
                    localProcessingOnly = true,
                    networkAcquisitionEnabled,
                    silentUpgradeEnabled,
                    runtimeDispositionAllowlist = new[] { "adopted-conditional" },
                    cacheSubdirectory = "cache/models",
                    releaseApproved,
                },
                models = new object[]
                {
                    new
                    {
                        modelId = ModelManager.AdoptedAsrModelId,
                        identity = "Systran/faster-whisper-small",
                        version = "revision fixture",
                        purpose = "Test runtime model",
                        disposition = "adopted-conditional",
                        consentStatus = "not-required",
                        runtimeEligible = true,
                        releaseApproved = false,
                        sourceEvidence = (string?)null,
                        artifactEvidence = artifact,
                        license,
                        bundle = runtimeBundle,
                        conditions = new[] { "Fixture only." },
                        evidence = new[] { "tests/fixture" },
                    },
                    new
                    {
                        modelId = "vision.held",
                        identity = "Held fixture",
                        version = "not acquired",
                        purpose = "Held fixture",
                        disposition = "held",
                        consentStatus = "not-required",
                        runtimeEligible = false,
                        releaseApproved = false,
                        sourceEvidence = (string?)null,
                        artifactEvidence = (object?)null,
                        license,
                        bundle = includeHeldBundle ? runtimeBundle : null,
                        conditions = new[] { "Do not acquire." },
                        evidence = new[] { "tests/fixture" },
                    },
                    new
                    {
                        modelId = "diarization.blocked",
                        identity = "Blocked fixture",
                        version = "not acquired",
                        purpose = "Consent fixture",
                        disposition = "condition-false",
                        consentStatus = "required-not-accepted",
                        runtimeEligible = false,
                        releaseApproved = false,
                        sourceEvidence = (string?)null,
                        artifactEvidence = (object?)null,
                        license,
                        bundle = (object?)null,
                        conditions = new[] { "Terms are not accepted." },
                        evidence = new[] { "tests/fixture" },
                    },
                },
            });
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }

        private static string ComputeManifestDigest(
            IReadOnlyDictionary<string, byte[]> files)
        {
            var text = new StringBuilder();
            foreach ((string name, byte[] bytes) in files.OrderBy(
                static item => item.Key,
                StringComparer.Ordinal))
            {
                text.Append(name);
                text.Append('\0');
                text.Append(bytes.LongLength);
                text.Append('\0');
                text.Append(ContentDigest.Compute(bytes).Hex);
                text.Append('\n');
            }

            return ContentDigest.Compute(Encoding.UTF8.GetBytes(text.ToString())).Hex;
        }
    }
}