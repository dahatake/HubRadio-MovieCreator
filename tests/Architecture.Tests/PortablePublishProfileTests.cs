using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace HubRadio.Architecture.Tests;

// OSL-011: profile contracts and a synthetic SDK item pipeline, not app publish,
// runtime acceptance or release evidence. The probe creates only a temporary .proj.
[TestClass]
public sealed class PortablePublishProfileTests
{
    private const string ProfilePath = "src/HubRadio.App/Properties/PublishProfiles/win-x64-singlefile.pubxml";
    private const string DefaultProfilePath = "src/HubRadio.App/Properties/PublishProfiles/win-x64.pubxml";
    private const string TargetsPath = "packaging/portable/PortablePayload.targets";
    private const string PayloadImport = @"$(MSBuildThisFileDirectory)..\..\..\..\packaging\portable\PortablePayload.targets";
    private static readonly Dictionary<string, string> RequiredProperties = new(StringComparer.Ordinal)
    {
        ["PublishProtocol"] = "FileSystem",
        ["Platform"] = "x64",
        ["RuntimeIdentifier"] = "win-x64",
        ["WindowsPackageType"] = "None",
        ["SelfContained"] = "true",
        ["WindowsAppSDKSelfContained"] = "true",
        ["PublishSingleFile"] = "true",
        ["IncludeAllContentForSelfExtract"] = "true",
        ["EnableMsixTooling"] = "true",
        ["WindowsAppSdkUndockedRegFreeWinRTInitialize"] = "true",
        ["PublishTrimmed"] = "false",
        ["DebugType"] = "None",
        ["DebugSymbols"] = "false",
        ["HubRadioPortablePublish"] = "true",
    };
    private static readonly string[] PayloadFiles =
    [
        "Worker/python.exe", "Tools/ffmpeg.exe", "Tools/ffprobe.exe",
        "ModelCatalog/catalog.json", "WebView2Runtime/msedgewebview2.exe",
        "Licenses/NOTICE", "Evidence/portable-payload.json",
    ];

    [TestMethod]
    public void RequiredSettingsAreExplicitWithoutPayloadOrApprovalDefaults()
    {
        XDocument profile = LoadProfile(ProfilePath);
        foreach ((string name, string expected) in RequiredProperties)
        {
            XElement property = profile.Descendants().Single(element => element.Name.LocalName == name);
            Assert.AreEqual(expected, property.Value, name);
            Assert.IsNull(property.Attribute("Condition"), name);
            Assert.AreEqual("PropertyGroup", property.Parent!.Name.LocalName, name);
            Assert.IsNull(property.Parent.Attribute("Condition"), name);
        }
        foreach (string external in new[] { "HubRadioPortablePayloadPath", "HubRadioPortableBundleManifestPath", "ReleaseApproved" })
        {
            Assert.IsFalse(profile.Descendants().Any(element =>
                string.Equals(element.Name.LocalName, external, StringComparison.OrdinalIgnoreCase)), external);
        }
        foreach (string optimization in new[] { "PublishAot", "PublishReadyToRun", "EnableCompressionInSingleFile" })
        {
            Assert.IsTrue(Property(profile, optimization) is null or "false", optimization);
        }
        Assert.AreNotEqual("false", Property(profile, "WindowsAppSDKSingleFileVerifyConfiguration"));
    }

    [TestMethod]
    public void PayloadImportResolvesFromExactlyFourParentsAndIsMarkerGated()
    {
        XDocument profile = LoadProfile(ProfilePath);
        XElement import = profile.Root!.Elements().Single(static element => element.Name.LocalName == "Import");
        string expression = import.Attribute("Project")!.Value;
        Assert.AreEqual(PayloadImport, expression);
        Assert.AreEqual("'$(HubRadioPortablePublish)' == 'true'", import.Attribute("Condition")?.Value);
        string root = GetRepositoryRoot();
        string relative = expression["$(MSBuildThisFileDirectory)".Length..].Replace('\\', Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(relative, Path.GetDirectoryName(Path.Combine(root, ProfilePath))!);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, TargetsPath)), resolved);
        Assert.IsTrue(File.Exists(resolved), "The dedicated profile must import the existing OSL-010 targets.");
    }

    [TestMethod]
    public void DefaultDevelopmentProfileRemainsNonPortable()
    {
        XDocument profile = LoadProfile(DefaultProfilePath);
        Assert.AreEqual("false", Property(profile, "PublishSingleFile"));
        Assert.IsFalse(string.Equals(Property(profile, "HubRadioPortablePublish"), "true", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse((Property(profile, "DefineConstants") ?? string.Empty).Contains("HUBRADIO_PORTABLE", StringComparison.Ordinal));
        Assert.IsFalse(profile.Descendants().Any(static element => element.Name.LocalName == "Import" &&
            (element.Attribute("Project")?.Value ?? string.Empty).Contains("PortablePayload.targets", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PortableCompilationSymbolAppendsToExistingDefinitions()
    {
        XDocument profile = LoadProfile(ProfilePath);
        XElement constants = profile.Descendants().Single(static element => element.Name.LocalName == "DefineConstants");
        Assert.AreEqual("$(DefineConstants);HUBRADIO_PORTABLE", constants.Value);
        Assert.IsNull(constants.Attribute("Condition"));
        Assert.IsNull(constants.Parent!.Attribute("Condition"));
        // The SDK probe below also checks that the evaluated pre-existing symbols survive.
    }

    [TestMethod]
    public void SdkEvaluationConsumesProfileMarkerAndExplicitBuilderPaths()
    {
        using var fixture = new ProfileProject();
        (int exitCode, string output) = fixture.Run();
        Assert.AreEqual(0, exitCode, output);
        Dictionary<string, string> evaluated = ReadRows(fixture.PropertiesPath)
            .ToDictionary(static row => row[0], static row => row[1], StringComparer.Ordinal);
        foreach ((string name, string expected) in RequiredProperties)
        {
            Assert.AreEqual(expected, evaluated[name], name);
        }
        Assert.AreEqual(fixture.PayloadPath, evaluated["HubRadioPortablePayloadPath"]);
        Assert.AreEqual(fixture.ManifestPath, evaluated["HubRadioPortableBundleManifestPath"]);
        CollectionAssert.AreEqual(new[] { "TRACE", "EXISTING_OSL011", "HUBRADIO_PORTABLE" }, File.ReadAllLines(fixture.ConstantsPath));
        string[][] bundle = ReadRows(fixture.ManifestPath);
        CollectionAssert.AreEquivalent(PayloadFiles, bundle.Select(static row => row[1].Replace('\\', '/')).ToArray());
        foreach (string[] row in bundle)
        {
            Assert.IsTrue(Path.IsPathFullyQualified(row[0]));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(fixture.PayloadPath, row[1])), row[0]);
        }
        Assert.IsFalse(bundle.Any(row => row[0] == fixture.ManifestPath), "The build-only TSV must not become payload.");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "obj/portable-files-to-bundle.tsv")), "Honor the builder's optional report path.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "publish")), "This probe must not publish or bundle an app.");
    }

    private static XDocument LoadProfile(string relativePath) => XDocument.Load(Path.Combine(GetRepositoryRoot(), relativePath));

    private static string? Property(XDocument project, string name) =>
        project.Descendants().SingleOrDefault(element => element.Name.LocalName == name)?.Value;

    private static string[][] ReadRows(string path)
    {
        Assert.IsTrue(File.Exists(path), "Missing probe report: " + path);
        string[][] rows = File.ReadAllLines(path).Select(static line => line.Split('\t')).ToArray();
        Assert.IsNotEmpty(rows);
        foreach (string[] row in rows) { Assert.HasCount(2, row, path); }
        return rows;
    }

    private sealed class ProfileProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.OSL011-" + Guid.NewGuid().ToString("N"));
        public string PayloadPath => Path.Combine(Root, "prepared payload 日本語");
        public string ManifestPath => Path.Combine(Root, "reports/bundle.tsv");
        public string PropertiesPath => Path.Combine(Root, "evaluated-properties.tsv");
        public string ConstantsPath => Path.Combine(Root, "evaluated-constants.txt");

        public ProfileProject()
        {
            string repository = GetRepositoryRoot();
            Write(Path.Combine(Root, ProfilePath), File.ReadAllText(Path.Combine(repository, ProfilePath)));
            Write(Path.Combine(Root, TargetsPath), File.ReadAllText(Path.Combine(repository, TargetsPath)));
            // Layout-only fixtures: the targets do not validate payload hashes or release evidence.
            foreach (string relative in PayloadFiles) { Write(Path.Combine(PayloadPath, relative), "synthetic: " + relative); }
            Write(Path.Combine(PayloadPath, "Evidence/portable-payload.json"), """{"fixtureOnly":true,"releaseApproved":false}""");
        }

        public (int ExitCode, string Output) Run()
        {
            string projectPath = Path.Combine(Root, "src/HubRadio.App/profile-test.proj");
            var project = new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("DefineConstants", "TRACE;EXISTING_OSL011"),
                    new XElement("HubRadioPortablePublish", "false"),
                    new XElement("PublishSingleFile", "false"),
                    new XElement("IntermediateOutputPath", Path.Combine(Root, "obj") + Path.DirectorySeparatorChar),
                    new XElement("PublishDir", Path.Combine(Root, "publish") + Path.DirectorySeparatorChar),
                    new XElement("AssemblyName", "ProfileProbe"),
                    new XElement("_NativeExecutableExtension", ".exe"),
                    new XElement("MicrosoftNETBuildTasksAssembly", @"$(MSBuildSDKsPath)\Microsoft.NET.Sdk\tools\net10.0\Microsoft.NET.Build.Tasks.dll")),
                new XElement("Import", new XAttribute("Project", "Properties/PublishProfiles/win-x64-singlefile.pubxml")),
                new XElement("Import", new XAttribute("Project", @"$(MSBuildSDKsPath)\Microsoft.NET.Sdk\targets\Microsoft.NET.Publish.targets")),
                new XElement("Target", new XAttribute("Name", "AssignTargetPaths"),
                    AssignPaths("Content", "ContentWithTargetPath"),
                    AssignPaths("None", "_NoneWithTargetPath")),
                // Supply only the surrounding item pipeline; no restore, compiler or bundler runs.
                new XElement("Target", new XAttribute("Name", "_SplitProjectReferencesByFileExistence")),
                new XElement("Target", new XAttribute("Name", "_GetProjectReferenceTargetFrameworkProperties")),
                new XElement("Target", new XAttribute("Name", "_HandleFileConflictsForPublish")),
                new XElement("Target", new XAttribute("Name", "Probe"),
                    new XAttribute("DependsOnTargets", "_ComputeCopyToPublishDirectoryItems;PrepareForBundle"),
                    new XElement("ItemGroup", RequiredProperties.Keys
                        .Concat(new[] { "HubRadioPortablePayloadPath", "HubRadioPortableBundleManifestPath" })
                        .Select(static name => new XElement("_EvaluatedProfileProperty", new XAttribute("Include", name),
                            new XElement("Value", "$(" + name + ")")))),
                    Report(PropertiesPath, "@(_EvaluatedProfileProperty->'%(Identity)\t%(Value)')"),
                    Report(ConstantsPath, "$(DefineConstants)")));
            new XDocument(project).Save(projectPath);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GetRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string argument in new[]
            {
                "msbuild", projectPath, "-nologo", "-v:minimal", "-nr:false", "-m:1", "-t:Probe",
                "-p:HubRadioPortablePayloadPath=" + PayloadPath,
                "-p:HubRadioPortableBundleManifestPath=" + ManifestPath,
            })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            using var process = new Process { StartInfo = start };
            Assert.IsTrue(process.Start());
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("The synthetic profile probe timed out; no app build should run.");
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }

        private static XElement AssignPaths(string source, string output) => new("AssignTargetPath",
            new XAttribute("Files", "@(" + source + ")"), new XAttribute("RootFolder", "$(MSBuildProjectDirectory)"),
            new XElement("Output", new XAttribute("TaskParameter", "AssignedFiles"), new XAttribute("ItemName", output)));

        private static XElement Report(string path, string lines) => new("WriteLinesToFile",
            new XAttribute("File", path), new XAttribute("Lines", lines),
            new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8"));

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}