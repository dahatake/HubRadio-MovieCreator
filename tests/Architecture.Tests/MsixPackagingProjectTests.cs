using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace HubRadio.Architecture.Tests;

[TestClass]
public sealed class MsixPackagingProjectTests
{
    private const string PackageDirectory = "packaging/HubRadio.Package";

    [TestMethod]
    public void PackagingProjectDefinesOneX64SelfContainedMultiExecutableBoundary()
    {
        string root = GetRepositoryRoot();
        string[] packageProjects = Directory.GetFiles(
            Path.Combine(root, "packaging"),
            "*.wapproj",
            SearchOption.AllDirectories);
        Assert.HasCount(1, packageProjects);

        XDocument project = XDocument.Load(packageProjects[0]);
        string[] imports = project
            .Descendants()
            .Where(static element => element.Name.LocalName == "Import")
            .Select(static element => element.Attribute("Project")?.Value ?? string.Empty)
            .ToArray();
        CollectionAssert.Contains(
            imports,
            @"$(MSBuildExtensionsPath)\Microsoft\DesktopBridge\Microsoft.DesktopBridge.props");
        CollectionAssert.Contains(
            imports,
            @"$(MSBuildExtensionsPath)\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets");

        Assert.AreEqual("10.0.26100.0", Property(project, "TargetPlatformVersion"));
        Assert.AreEqual("10.0.26100.0", Property(project, "TargetPlatformMinVersion"));
        Assert.AreEqual("x64", Property(project, "AppxBundlePlatforms"));
        Assert.AreEqual("Never", Property(project, "AppxBundle"));
        Assert.AreEqual("false", Property(project, "AppxPackageSigningEnabled"));
        Assert.AreEqual("false", Property(project, "GenerateAppInstallerFile"));
        Assert.AreEqual("true", Property(project, "WindowsAppSDKSelfContained"));
        Assert.AreEqual(
            @"..\..\src\HubRadio.App\HubRadio.App.csproj",
            Property(project, "EntryPointProjectUniqueName"));

        XElement projectReference = project
            .Descendants()
            .Single(static element => element.Name.LocalName == "ProjectReference");
        Assert.AreEqual(
            @"..\..\src\HubRadio.App\HubRadio.App.csproj",
            projectReference.Attribute("Include")?.Value);
        Assert.AreEqual(
            @"Properties\PublishProfiles\win-$(Platform).pubxml",
            projectReference
                .Elements()
                .Single(static element => element.Name.LocalName == "PublishProfile")
                .Value);
        string additionalProperties = projectReference
            .Elements()
            .Single(static element => element.Name.LocalName == "AdditionalProperties")
            .Value;
        foreach (string required in new[]
        {
            "RuntimeIdentifier=win-x64",
            "SelfContained=true",
            "WindowsAppSDKSelfContained=true",
            "PublishSingleFile=false",
            "WindowsPackageType=None",
        })
        {
            StringAssert.Contains(additionalProperties, required);
        }

        string[] links = project
            .Descendants()
            .Where(static element => element.Name.LocalName == "Link")
            .Select(static element => element.Value)
            .ToArray();
        foreach (string required in new[]
        {
            @"Worker\%(RecursiveDir)%(Filename)%(Extension)",
            @"Tools\ffmpeg.exe",
            @"Tools\ffprobe.exe",
            @"Models\faster-whisper-small\model.bin",
            @"Licenses\FFmpeg-LICENSE.txt",
            @"Licenses\CPython-LICENSE.txt",
            @"Evidence\ffmpeg-manifest.json",
            @"Evidence\model-catalog.json",
            @"Evidence\python-runtime-manifest.json",
            @"Evidence\web-dependency-manifest.json",
        })
        {
            CollectionAssert.Contains(links, required);
        }

        XElement validationTarget = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Target" &&
                element.Attribute("Name")?.Value == "ValidateHubRadioPackageInputs");
        Assert.HasCount(6, validationTarget.Elements().Where(static element => element.Name.LocalName == "Error"));

        string solution = File.ReadAllText(Path.Combine(root, "HubRadio.slnx"));
        Assert.IsFalse(
            solution.Contains(".wapproj", StringComparison.OrdinalIgnoreCase),
            "The WAP project requires the Visual Studio MSIX Packaging Tools workload and must not break the SDK-only solution build.");
    }

    [TestMethod]
    public void PackageManifestDeclaresTheSingleFullTrustWinUiEntryPoint()
    {
        string root = GetRepositoryRoot();
        XDocument manifest = XDocument.Load(
            Path.Combine(root, PackageDirectory, "Package.appxmanifest"),
            LoadOptions.SetLineInfo);
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        XNamespace restricted =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        XElement identity = manifest.Root!.Element(foundation + "Identity")!;
        Assert.AreEqual("HubRadio.MovieCreator.Development", identity.Attribute("Name")?.Value);
        Assert.AreEqual("CN=HubRadio MovieCreator Development", identity.Attribute("Publisher")?.Value);
        Assert.AreEqual("0.1.4.0", identity.Attribute("Version")?.Value);
        Assert.AreEqual("x64", identity.Attribute("ProcessorArchitecture")?.Value);
        string buildScript = File.ReadAllText(Path.Combine(root, PackageDirectory, "build-package.ps1"));
        string verifyScript = File.ReadAllText(Path.Combine(root, PackageDirectory, "verify-package.ps1"));
        StringAssert.Contains(buildScript, "HubRadio.MovieCreator_0.1.4.0_x64.msix");
        StringAssert.Contains(buildScript, "packageVersion = '0.1.4.0'");
        StringAssert.Contains(verifyScript, "$layout.packageVersion -cne '0.1.4.0'");

        XElement targetFamily = manifest
            .Descendants(foundation + "TargetDeviceFamily")
            .Single();
        Assert.AreEqual("Windows.Desktop", targetFamily.Attribute("Name")?.Value);
        Assert.AreEqual("10.0.26100.0", targetFamily.Attribute("MinVersion")?.Value);
        Assert.AreEqual("10.0.26100.0", targetFamily.Attribute("MaxVersionTested")?.Value);

        XElement application = manifest.Descendants(foundation + "Application").Single();
        Assert.AreEqual("HubRadioMovieCreator", application.Attribute("Id")?.Value);
        Assert.AreEqual("$targetnametoken$.exe", application.Attribute("Executable")?.Value);
        Assert.AreEqual("packagedClassicApp", application.Attribute(uap10 + "RuntimeBehavior")?.Value);
        Assert.AreEqual("mediumIL", application.Attribute(uap10 + "TrustLevel")?.Value);

        XElement capability = manifest.Descendants(restricted + "Capability").Single();
        Assert.AreEqual("runFullTrust", capability.Attribute("Name")?.Value);
        Assert.HasCount(1, manifest.Descendants(foundation + "Application"));
        Assert.HasCount(1, manifest.Descendants(foundation + "Capabilities").Single().Elements());
    }

    [TestMethod]
    public void PackagingScriptsStayOfflineFailClosedAndDoNotOverclaimInstallation()
    {
        string root = GetRepositoryRoot();
        string build = File.ReadAllText(
            Path.Combine(root, PackageDirectory, "build-package.ps1"));
        string verify = File.ReadAllText(
            Path.Combine(root, PackageDirectory, "verify-package.ps1"));

        foreach (string forbidden in new[]
        {
            "Invoke-WebRequest",
            "Invoke-RestMethod",
            "Start-BitsTransfer",
            "System.Net.Http.HttpClient",
            "Add-AppxPackage",
            "SignTool",
            "'/nv'",
        })
        {
            Assert.IsFalse(build.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
            Assert.IsFalse(verify.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }

        foreach (string required in new[]
        {
            "'build', $appProjectPath",
            "'-p:SelfContained=true'",
            "'App.xbf'",
            "'MainWindow.xbf'",
            "'HubRadio.App.pri'",
            "Write-ManualPackageManifest",
            "'Executable=\"App\\HubRadio.App.exe\"'",
            "Get-ArtifactSetSha256",
            "Get-AuthenticodeSignature",
            "PACKAGE-NOT-FOR-RELEASE.txt",
            "[IO.File]::Move",
            "Models\\faster-whisper-small\\",
        })
        {
            StringAssert.Contains(build, required);
        }
        Assert.IsFalse(build.Contains("System.Drawing", StringComparison.Ordinal));

        foreach (string required in new[]
        {
            "'unpack', '/p'",
            "AppxBlockMap.xml",
            "packagedClassicApp",
            "mediumIL",
            "runFullTrust",
            "HF_HUB_OFFLINE",
            "TRANSFORMERS_OFFLINE",
            "CpuInt8ModelLoadPassed = $true",
            "InstalledMsixValidated = $false",
            "CleanMachineValidated = $false",
            "ReleaseApproved = $false",
            "EvidenceStatus = 'unpacked-development-evidence'",
        })
        {
            StringAssert.Contains(verify, required);
        }
    }

    [TestMethod]
    public void PackageLogoSourcesDecodeToFixedPngDimensionsAndDigests()
    {
        string root = GetRepositoryRoot();
        var expected = new Dictionary<string, (int Width, int Height, int Bytes, string Sha256)>(StringComparer.Ordinal)
        {
            ["Square44x44Logo.png.b64"] = (
                44,
                44,
                255,
                "bf764a0cdfcc4ccf7080781ce5580adb538c87e320e2dc1fa6634ef1c076026c"),
            ["Square150x150Logo.png.b64"] = (
                150,
                150,
                1028,
                "f44fceb26344cdaeeb5ff5437574938364830e349118f2bf43cba807a232096e"),
            ["StoreLogo.png.b64"] = (
                50,
                50,
                288,
                "7ac7bd4603467a0ccd9dc451a151852e7e8677b4b3d7d6d8d4c5bad5fcf7e382"),
        };

        foreach ((string fileName, (int width, int height, int bytes, string sha256)) in expected)
        {
            byte[] content = Convert.FromBase64String(
                File.ReadAllText(Path.Combine(root, PackageDirectory, "Assets", fileName)).Trim());
            Assert.AreEqual(bytes, content.Length, fileName);
            CollectionAssert.AreEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                content[..8],
                fileName);
            Assert.AreEqual(width, BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(16, 4)), fileName);
            Assert.AreEqual(height, BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4)), fileName);
            Assert.AreEqual(
                sha256,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                fileName);
        }
    }

    [TestMethod]
    public void PackageMarkerKeepsEveryExternalReleaseGateOpen()
    {
        string root = GetRepositoryRoot();
        string marker = File.ReadAllText(
            Path.Combine(root, PackageDirectory, "PACKAGE-NOT-FOR-RELEASE.txt"));
        foreach (string required in new[]
        {
            "unsigned package",
            "not approved for installation",
            "Package signing",
            "trusted installation",
            "upgrade",
            "rollback",
            "uninstall",
            "clean production-channel Windows validation",
            "SBOM",
            "legal review",
            "release approval remain open",
        })
        {
            StringAssert.Contains(marker, required, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Property(XDocument project, string name) =>
        project
            .Descendants()
            .Single(element => element.Name.LocalName == name)
            .Value;

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