using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HubRadio.Core.Identity;

namespace HubRadio.App.Tests;

[TestClass]
public sealed partial class WebAssetPackagingTests
{
    private const string AssetManifestName = "asset-manifest.json";

    [TestMethod]
    public void BundledWebAssets_MatchGeneratedManifestExactly()
    {
        string webAssetsRoot = Path.Combine(AppContext.BaseDirectory, "WebAssets");
        string manifestPath = Path.Combine(webAssetsRoot, AssetManifestName);
        Assert.IsTrue(File.Exists(manifestPath));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "entryPoint",
                "sourceMapsIncluded",
                "remoteAssetsAllowed",
                "assets",
            },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("index.html", root.GetProperty("entryPoint").GetString());
        Assert.IsFalse(root.GetProperty("sourceMapsIncluded").GetBoolean());
        Assert.IsFalse(root.GetProperty("remoteAssetsAllowed").GetBoolean());

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            AssetManifestName,
        };
        string previousPath = string.Empty;
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            CollectionAssert.AreEquivalent(
                new[] { "path", "byteSize", "sha256" },
                asset.EnumerateObject().Select(static property => property.Name).ToArray());
            string relativePath = asset.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("An asset path is missing.");
            Assert.IsTrue(IsSafePortableAssetPath(relativePath));
            Assert.IsTrue(
                string.CompareOrdinal(previousPath, relativePath) < 0,
                "Manifest paths must be unique and ordinally sorted.");
            previousPath = relativePath;
            Assert.IsTrue(expectedPaths.Add(relativePath));

            string fullPath = Path.GetFullPath(Path.Combine(
                webAssetsRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsTrue(fullPath.StartsWith(
                string.Concat(webAssetsRoot, Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(fullPath));
            var information = new FileInfo(fullPath);
            Assert.AreEqual(asset.GetProperty("byteSize").GetInt64(), information.Length);
            Assert.AreEqual(
                asset.GetProperty("sha256").GetString(),
                ContentDigest.Compute(File.ReadAllBytes(fullPath)).Hex);
        }

        string[] actualPaths = Directory
            .EnumerateFiles(webAssetsRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(webAssetsRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            expectedPaths.Order(StringComparer.Ordinal).ToArray(),
            actualPaths);
    }

    [TestMethod]
    public void BundledEntryPoint_UsesClosedCspAndOnlyRelativeHashedAssets()
    {
        string webAssetsRoot = Path.Combine(AppContext.BaseDirectory, "WebAssets");
        string indexHtml = ReadStrictUtf8(Path.Combine(webAssetsRoot, "index.html"));
        Assert.IsFalse(indexHtml.Contains("<base", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(indexHtml.Contains("'unsafe-inline'", StringComparison.Ordinal));
        Assert.IsFalse(indexHtml.Contains("'unsafe-eval'", StringComparison.Ordinal));
        foreach (string directive in new[]
        {
            "default-src 'none'",
            "script-src 'self'",
            "style-src 'self'",
            "connect-src 'none'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'none'",
            "frame-src 'none'",
            "worker-src 'none'",
        })
        {
            Assert.IsTrue(indexHtml.Contains(directive, StringComparison.Ordinal));
        }

        MatchCollection references = AssetReferenceRegex().Matches(indexHtml);
        Assert.IsGreaterThanOrEqualTo(2, references.Count);
        foreach (Match match in references)
        {
            string reference = match.Groups["value"].Value;
            Assert.IsTrue(LocalHashedAssetRegex().IsMatch(reference), reference);
            Assert.IsTrue(File.Exists(Path.Combine(
                webAssetsRoot,
                reference[2..].Replace('/', Path.DirectorySeparatorChar))));
        }

        Assert.HasCount(
            0,
            Directory.EnumerateFiles(webAssetsRoot, "*.map", SearchOption.AllDirectories).ToArray());
    }

    [TestMethod]
    public void ProductionConfiguration_DisablesPublicPassthroughAndRequiresVerifiedManifest()
    {
        string repositoryRoot = FindRepositoryRoot();
        string viteConfig = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "HubRadio.Web", "vite.config.ts"),
            Encoding.UTF8);
        string packageJson = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "HubRadio.Web", "package.json"),
            Encoding.UTF8);
        string appProject = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "HubRadio.App", "HubRadio.App.csproj"),
            Encoding.UTF8);

        foreach (string required in new[]
        {
            "publicDir: false",
            "assetsInlineLimit: 0",
            "copyPublicDir: false",
            "emptyOutDir: true",
            "sourcemap: false",
        })
        {
            Assert.IsTrue(viteConfig.Contains(required, StringComparison.Ordinal), required);
        }

        Assert.IsTrue(packageJson.Contains(
            "vite build && npm run verify:dist",
            StringComparison.Ordinal));
        Assert.IsTrue(packageJson.Contains(
            "node scripts/verify-production-assets.mjs",
            StringComparison.Ordinal));
        Assert.IsTrue(appProject.Contains(
            "dist\\asset-manifest.json",
            StringComparison.Ordinal));
        Assert.IsTrue(appProject.Contains(
            "<Link>WebAssets\\%(RecursiveDir)%(Filename)%(Extension)</Link>",
            StringComparison.Ordinal));
        Assert.IsTrue(appProject.Contains(
            "Exclude=\"..\\HubRadio.Web\\dist\\**\\*.partial\"",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeDependencyManifest_MatchesPackageLockAndAggregateDigest()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument dependencyManifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repositoryRoot, "packaging", "web", "dependency-manifest.json")));
        using JsonDocument packageLock = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repositoryRoot, "src", "HubRadio.Web", "package-lock.json")));
        JsonElement manifestRoot = dependencyManifest.RootElement;
        Assert.AreEqual("1.0.0", manifestRoot.GetProperty("schemaVersion").GetString());
        Assert.IsFalse(manifestRoot.GetProperty("releaseApproved").GetBoolean());

        JsonElement lockPackages = packageLock.RootElement.GetProperty("packages");
        var records = new List<string>();
        string previousName = string.Empty;
        foreach (JsonElement package in manifestRoot.GetProperty("packages").EnumerateArray())
        {
            string name = package.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("A package name is missing.");
            string version = package.GetProperty("version").GetString()
                ?? throw new InvalidOperationException("A package version is missing.");
            long byteSize = package.GetProperty("byteSize").GetInt64();
            string digest = package.GetProperty("sha256").GetString()
                ?? throw new InvalidOperationException("A package digest is missing.");
            string registrySha1 = package.GetProperty("registrySha1").GetString()
                ?? throw new InvalidOperationException("A registry digest is missing.");
            Assert.IsTrue(string.CompareOrdinal(previousName, name) < 0);
            previousName = name;
            Assert.IsTrue(byteSize > 0);
            Assert.AreEqual(digest, new ContentDigest(digest).Hex);

            JsonElement locked = lockPackages.GetProperty($"node_modules/{name}");
            Assert.AreEqual(version, locked.GetProperty("version").GetString());
            Assert.AreEqual(package.GetProperty("source").GetString(), locked.GetProperty("resolved").GetString());
            string integrity = locked.GetProperty("integrity").GetString()
                ?? throw new InvalidOperationException("Lock integrity is missing.");
            Assert.IsTrue(integrity.StartsWith("sha1-", StringComparison.Ordinal));
            Assert.AreEqual(
                registrySha1,
                Convert.ToHexString(Convert.FromBase64String(integrity[5..])).ToLowerInvariant());
            Assert.AreEqual("MIT", package.GetProperty("declaredLicense").GetString());

            records.Add(string.Concat(
                name,
                "\0",
                version,
                "\0",
                byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "\0",
                digest,
                "\n"));
        }

        Assert.HasCount(3, records);
        Assert.AreEqual(
            manifestRoot.GetProperty("artifactSetSha256").GetString(),
            ContentDigest.Compute(Encoding.UTF8.GetBytes(string.Concat(records))).Hex);
    }

    private static bool IsSafePortableAssetPath(string value) =>
        value.Length > 0 &&
        !value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Contains(':', StringComparison.Ordinal) &&
        value.Split('/').All(static segment =>
            segment.Length > 0 && segment is not "." and not "..");

    private static string ReadStrictUtf8(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new UTF8Encoding(false, true).GetString(bytes);
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

    [GeneratedRegex("(?:src|href)=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AssetReferenceRegex();

    [GeneratedRegex("^\\./assets/[A-Za-z0-9._-]+\\.(?:css|js)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalHashedAssetRegex();
}