using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace HubRadio.Architecture.Tests;

// Synthetic MSBuild item-pipeline tests, not app publish/runtime or release evidence.
// Only temporary .proj files are executed; no SDK project, restore, compiler or bundler is run.
[TestClass]
public sealed class PortablePayloadTargetTests
{
    private const string TargetsPath = "packaging/portable/PortablePayload.targets";
    private const string EnabledCondition = "'$(HubRadioPortablePublish)' == 'true'";
    private static readonly string[] PayloadRoots = ["Worker", "Tools", "ModelCatalog", "WebView2Runtime", "Licenses", "Evidence"];
    private static readonly string[] PayloadFiles =
    [
        "Worker/python.exe", "Worker/Lib/site-packages/package/data/table.dat",
        "Worker/Lib/site-packages/package/LICENSE", "Worker/Lib/site-packages/native/module.pyd",
        "Worker/Lib/site-packages/native/module.cp312-win_amd64.pyd",
        "Tools/ffmpeg.exe", "Tools/ffprobe.exe", "ModelCatalog/catalog.json",
        "ModelCatalog/Bundles/model/model.bin", "WebView2Runtime/msedgewebview2.exe",
        "WebView2Runtime/locales/ja.pak", "WebView2Runtime/resources.pak",
        "WebView2Runtime/undocked_copilot/Microsoft.UI.Xaml.Controls.pri",
        "Licenses/CPython-LICENSE.txt", "Evidence/portable-payload.json", "Evidence/python-runtime-manifest.json",
    ];

    [TestMethod]
    public void IntegrationIsOptInAndUsesOnlyBuiltInItemAndFileTasks()
    {
        XElement project = XDocument.Load(Path.Combine(GetRepositoryRoot(), TargetsPath)).Root!;
        Assert.IsNull(project.Attribute("Sdk"));
        foreach (XElement element in project.Elements())
        {
            Assert.IsTrue(element.Name.LocalName is "PropertyGroup" or "Target", element.ToString());
            Assert.AreEqual(EnabledCondition, element.Attribute("Condition")?.Value);
        }
        Dictionary<string, XElement> targets = project.Elements("Target")
            .ToDictionary(static target => target.Attribute("Name")!.Value, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(new[] { "IncludeHubRadioPortablePayload", "ExcludeHubRadioPortablePayloadFromPri", "RecordHubRadioPortableBundle" }, targets.Keys.ToArray());
        Assert.AreEqual("AssignTargetPaths", targets["IncludeHubRadioPortablePayload"].Attribute("BeforeTargets")?.Value);
        Assert.AreEqual("GetPriOutputs", targets["ExcludeHubRadioPortablePayloadFromPri"].Attribute("AfterTargets")?.Value);
        Assert.AreEqual("IncludeHubRadioPortablePayload", targets["ExcludeHubRadioPortablePayloadFromPri"].Attribute("DependsOnTargets")?.Value);
        XElement priRemoval = targets["ExcludeHubRadioPortablePayloadFromPri"].Elements("ItemGroup").Single().Elements().Single();
        Assert.AreEqual("PriOutputs", priRemoval.Name.LocalName);
        Assert.AreEqual("@(_HubRadioPortableFile)", priRemoval.Attribute("Remove")?.Value);
        Assert.AreEqual("FullPath", priRemoval.Attribute("MatchOnMetadata")?.Value);
        Assert.AreEqual("PathLike", priRemoval.Attribute("MatchOnMetadataOptions")?.Value);
        Assert.AreEqual("PrepareForBundle", targets["RecordHubRadioPortableBundle"].Attribute("AfterTargets")?.Value);
        Assert.AreEqual("IncludeHubRadioPortablePayload", targets["RecordHubRadioPortableBundle"].Attribute("DependsOnTargets")?.Value);
        foreach (XElement task in targets.Values.SelectMany(static target => target.Elements()))
        {
            Assert.IsTrue(task.Name.LocalName is "Error" or "PropertyGroup" or "ItemGroup" or
                "RemoveDuplicates" or "MakeDir" or "WriteLinesToFile", task.ToString());
        }
        foreach (XElement error in project.Descendants("Error"))
        {
            StringAssert.StartsWith(error.Attribute("Code")!.Value, "OSL010_");
            Assert.IsFalse((error.Attribute("Condition")?.Value ?? string.Empty).Contains(".xbf", StringComparison.OrdinalIgnoreCase));
        }
        foreach (string forbidden in new[] { "Import", "UsingTask", "Exec", "PackageReference", "ProjectReference", "TargetFramework" })
        {
            Assert.IsFalse(project.Descendants(forbidden).Any(), forbidden);
        }
        Assert.IsFalse(project.Descendants("FilesToBundle").Any(), "Bundle inputs must come from the SDK, not be fabricated by the integration.");
    }

    [TestMethod]
    public void ContentContractLimitsRootsAndReplacesOnlyOriginalModelSources()
    {
        XDocument project = XDocument.Load(Path.Combine(GetRepositoryRoot(), TargetsPath));
        CollectionAssert.AreEqual(PayloadRoots, project.Descendants("_HubRadioPortableRoot").Single()
            .Attribute("Include")!.Value.Split(';'));
        XElement[] payloadSteps = project.Descendants("_HubRadioPortableFile").ToArray();
        Assert.HasCount(2, payloadSteps);
        XElement payload = payloadSteps[0];
        CollectionAssert.AreEqual(PayloadRoots.Select(static root => "$(_HubRadioPortableRootPath)" + root + @"\**\*").ToArray(),
            payload.Attribute("Include")!.Value.Split(';', StringSplitOptions.TrimEntries));
        Assert.IsFalse(payload.HasElements, "Create the items before evaluating their metadata.");
        XElement metadata = payloadSteps[1];
        Assert.IsFalse(metadata.HasAttributes, "Inside a Target, modify existing items without Include or Update.");
        Assert.AreEqual("$([MSBuild]::MakeRelative('$(_HubRadioPortableRootPath)', '%(_HubRadioPortableFile.FullPath)'))",
            metadata.Element("RelativePath")?.Value);
        Assert.IsTrue(metadata.Parent!.ElementsBeforeSelf("ItemGroup").Contains(payload.Parent!));
        XElement content = project.Descendants("Content").Single(static item => item.Attribute("Include") is not null);
        Assert.IsTrue(content.Parent!.ElementsBeforeSelf("ItemGroup").Contains(metadata.Parent!));
        Assert.AreEqual("@(_HubRadioPortableFile)", content.Attribute("Include")?.Value);
        Assert.AreEqual("%(_HubRadioPortableFile.RelativePath)", content.Element("Link")?.Value);
        Assert.AreEqual(content.Element("Link")?.Value, content.Element("TargetPath")?.Value);
        Assert.AreEqual("Never", content.Element("CopyToOutputDirectory")?.Value);
        Assert.AreEqual("PreserveNewest", content.Element("CopyToPublishDirectory")?.Value);
        Assert.AreEqual("false", content.Element("ExcludeFromSingleFile")?.Value);
        XElement removal = project.Descendants("Content").Single(static item => item.Attribute("Remove")?.Value == "@(_HubRadioPortableLegacyModel)");
        Assert.AreEqual("@(_HubRadioPortableLegacyModel)", removal.Attribute("Remove")?.Value);
        Assert.AreEqual("FullPath", removal.Attribute("MatchOnMetadata")?.Value);
        Assert.AreEqual("PathLike", removal.Attribute("MatchOnMetadataOptions")?.Value);
        XElement rawAppXaml = project.Descendants("_HubRadioPortableRawAppXaml").Single();
        Assert.AreEqual("@(ApplicationDefinition);@(Page)", rawAppXaml.Attribute("Include")?.Value);
        foreach (string itemType in new[] { "Content", "None" })
        {
            XElement rawRemoval = project.Descendants(itemType).Single(static item => item.Attribute("Remove")?.Value == "@(_HubRadioPortableRawAppXaml)");
            Assert.AreEqual("FullPath", rawRemoval.Attribute("MatchOnMetadata")?.Value);
            Assert.AreEqual("PathLike", rawRemoval.Attribute("MatchOnMetadataOptions")?.Value);
            Assert.AreEqual("IncludeHubRadioPortablePayload", rawRemoval.Parent!.Parent!.Attribute("Name")?.Value);
        }
        Assert.IsFalse(project.Descendants("ApplicationDefinition").Any(), "Do not change the XAML compiler inputs.");
        Assert.IsFalse(project.Descendants("Page").Any(), "Do not change the XAML compiler inputs.");
        XElement unlinkedContent = project.Descendants("_HubRadioPortableUnlinkedContent").Single();
        Assert.AreEqual("@(Content)", unlinkedContent.Attribute("Include")?.Value);
        Assert.AreEqual("'%(Content.Link)' == '' and '%(Content.TargetPath)' == ''", unlinkedContent.Attribute("Condition")?.Value);
        XElement noneRemoval = project.Descendants("None").Single(static item => item.Attribute("Remove")?.Value == "@(_HubRadioPortableUnlinkedContent)");
        Assert.AreEqual("@(_HubRadioPortableUnlinkedContent)", noneRemoval.Attribute("Remove")?.Value);
        Assert.AreEqual("FullPath", noneRemoval.Attribute("MatchOnMetadata")?.Value);
        Assert.AreEqual("PathLike", noneRemoval.Attribute("MatchOnMetadataOptions")?.Value);
        Assert.AreEqual("'%(None.Link)' == '' and '%(None.TargetPath)' == ''", noneRemoval.Attribute("Condition")?.Value);
        Assert.AreEqual("IncludeHubRadioPortablePayload", noneRemoval.Parent!.Parent!.Attribute("Name")?.Value);
        XElement report = project.Descendants("WriteLinesToFile").Single();
        Assert.AreEqual("@(FilesToBundle->'%(FullPath)\t%(RelativePath)')", report.Attribute("Lines")?.Value);
        Assert.AreEqual("true", report.Attribute("Overwrite")?.Value);
        XElement reportGuard = project.Descendants("Error").Single(static error => error.Attribute("Code")?.Value == "OSL010_REPORT_PATH");
        Assert.AreEqual("IncludeHubRadioPortablePayload", reportGuard.Parent?.Attribute("Name")?.Value);
        Assert.IsTrue(reportGuard.ElementsAfterSelf("ItemGroup").Contains(payload.Parent!), "Reject report paths before collecting payload files.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SdkPipelinePreservesAllPayloadPathsAndIncludesChildExecutables(bool trailingSlash)
    {
        using var fixture = new PayloadProject();
        if (trailingSlash) { fixture.Properties["HubRadioPortablePayloadPath"] += Path.DirectorySeparatorChar; }
        AssertSucceeded(fixture.Run());
        string[][] content = ReadRows(fixture.ItemsPath, 6);
        string[][] bundle = ReadRows(fixture.ManifestPath, 2);
        string[] expected = [.. PayloadFiles, "DefaultSettings/test.json", "ModelCatalog/custom.json"];
        CollectionAssert.AreEquivalent(expected, bundle.Select(static row => Normalize(row[1])).ToArray());
        Assert.HasCount(expected.Length, content);
        foreach (string relative in PayloadFiles)
        {
            string source = Path.GetFullPath(Path.Combine(fixture.PayloadPath, relative));
            string[] item = content.Single(row => row[0] == source);
            Assert.AreEqual(relative, Normalize(item[1]), "Link: " + relative);
            Assert.AreEqual(relative, Normalize(item[2]), "TargetPath: " + relative);
            Assert.AreEqual("Never", item[3]);
            Assert.AreEqual("PreserveNewest", item[4]);
            Assert.AreEqual("ExcludeFromSingleFile=false", item[5]);
            string[] bundled = bundle.Single(row => Normalize(row[1]) == relative);
            Assert.AreEqual(source, bundled[0], relative);
            Assert.IsTrue(Path.IsPathFullyQualified(bundled[0]));
        }
        Assert.IsFalse(bundle.Any(row => row[0] == fixture.ManifestPath), "The local-path report is not distributable content.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "publish")), "The synthetic target must not publish or bundle an app.");
        Assert.AreEqual(File.ReadAllText(Path.Combine(fixture.PayloadPath, "ModelCatalog/catalog.json")),
            File.ReadAllText(Path.Combine(fixture.Root, "packaging/models/catalog.json")), "The original catalog must remain untouched.");
    }

    [TestMethod]
    public void PriInputsExcludePreparedFilesButPreserveSdkResourcesAndBundledPayload()
    {
        using var fixture = new PayloadProject();
        Dictionary<string, byte[]> originalPayload = PayloadFiles.ToDictionary(static relative => relative,
            relative => File.ReadAllBytes(Path.Combine(fixture.PayloadPath, relative)), StringComparer.Ordinal);
        AssertSucceeded(fixture.Run());
        string[] appContent =
        [
            Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App/local/default.json")),
            Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App/local/custom.json")),
        ];
        CollectionAssert.AreEquivalent(appContent.Append(fixture.SdkPriPath).ToArray(),
            ReadRows(fixture.PriInputsPath, 2).Select(static row => row[0]).ToArray(),
            "Only prepared payload files may leave PriOutputs; unrelated App Content and the SDK PRI must remain.");
        CollectionAssert.AreEqual(new[] { fixture.SdkPriPath },
            ReadRows(fixture.PriFilesPath, 2).Select(static row => row[0]).ToArray(),
            "The exclusion must run before _GetPriFilesFromPayload captures the PRI merge inputs.");
        CollectionAssert.AreEquivalent(appContent, ReadRows(fixture.LayoutFilesPath, 2).Select(static row => row[0]).ToArray(),
            "The exclusion must also precede _GenerateProjectPriConfigurationFiles layout/qualifier scanning.");
        string[] expectedContent = ["DefaultSettings/test.json", "ModelCatalog/custom.json", .. PayloadFiles];
        CollectionAssert.AreEquivalent(expectedContent,
            ReadRows(fixture.ContentInputsPath, 2).Select(static row => Normalize(row[1])).ToArray(),
            "PRI exclusion must not remove any Content, including vendor PRI and CPython modules.");
        CollectionAssert.AreEquivalent(expectedContent,
            ReadRows(fixture.ManifestPath, 2).Select(static row => Normalize(row[1])).ToArray(),
            "All prepared files, including vendor PRI and CPython modules, must remain in FilesToBundle.");
        foreach (string relative in PayloadFiles)
        {
            CollectionAssert.AreEqual(originalPayload[relative], File.ReadAllBytes(Path.Combine(fixture.PayloadPath, relative)), relative);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("false")]
    [DataRow("1")]
    public void DisabledIntegrationPreservesDefaultContentWithoutValidInputs(string marker)
    {
        using var fixture = new PayloadProject();
        fixture.Properties["HubRadioPortablePublish"] = marker;
        fixture.Properties.Remove("HubRadioPortablePayloadPath");
        fixture.Properties["SelfContained"] = "false";
        AssertSucceeded(fixture.Run());
        string[][] items = ReadRows(fixture.ItemsPath, 6);
        Assert.HasCount(4, items);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(fixture.Root, "packaging/models/catalog.json")),
            items.Single(static row => Normalize(row[2]) == "ModelCatalog/catalog.json")[0]);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(fixture.Root, "packaging/models/Bundles/model/model.bin")),
            items.Single(static row => Normalize(row[2]) == "ModelCatalog/Bundles/model/model.bin")[0]);
        Assert.IsTrue(items.All(static row => row[3] == "PreserveNewest" && row[4] == "PreserveNewest"));
        Assert.IsFalse(File.Exists(fixture.ManifestPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "obj")));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DeveloperPublishProfilesAreExcludedOnlyWhenPortableIsEnabled(bool enabled)
    {
        using var fixture = new PayloadProject();
        fixture.Properties["HubRadioPortablePublish"] = enabled ? "true" : "false";
        string[] realProfiles =
        [
            "Properties/PublishProfiles/win-x64-singlefile.pubxml",
            "Properties/PublishProfiles/win-x64.pubxml",
        ];
        Dictionary<string, byte[]> originals = realProfiles.ToDictionary(static relative => relative,
            relative => File.ReadAllBytes(Path.Combine(GetRepositoryRoot(), "src/HubRadio.App", relative)), StringComparer.Ordinal);
        originals.Add("Properties/PublishProfiles/日本語/notes.txt", Encoding.UTF8.GetBytes("developer-only notes"));
        foreach ((string relative, byte[] bytes) in originals)
        {
            string source = Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App", relative));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, bytes);
            // Fake SDK default None has no copy metadata; explicit Content uses an absolute
            // source and an unrelated destination so exclusion must use the source directory.
            fixture.ExtraItems.Add(new XElement("None", new XAttribute("Include", relative.Replace('/', '\\'))));
            fixture.ExtraItems.Add(FileItem("Content", source, "DeveloperSettings/" + Path.GetFileName(relative)));
        }
        const string vendorRelative = "Worker/Lib/site-packages/package/Properties/PublishProfiles/vendor.pubxml";
        string vendorSource = Path.GetFullPath(Path.Combine(fixture.PayloadPath, vendorRelative));
        byte[] vendorBytes = Encoding.UTF8.GetBytes("unchanged vendor profile");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorSource)!);
        File.WriteAllBytes(vendorSource, vendorBytes);

        AssertSucceeded(fixture.Run());
        string[][] bundle = ReadRows(fixture.BundleInputsPath, 2);
        string[][] content = ReadRows(fixture.ItemsPath, 6);
        Assert.HasCount(enabled ? PayloadFiles.Length + 3 : 4 + 2 * originals.Count, bundle);
        Assert.AreEqual(enabled, File.Exists(fixture.ManifestPath));
        foreach ((string relative, byte[] bytes) in originals)
        {
            string source = Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App", relative));
            string[][] entries = bundle.Where(row => row[0] == source).ToArray();
            Assert.HasCount(enabled ? 0 : 2, entries, relative);
            Assert.HasCount(enabled ? 0 : 1, content.Where(row => row[0] == source).ToArray(), relative);
            if (!enabled)
            {
                CollectionAssert.AreEquivalent(new[] { relative, "DeveloperSettings/" + Path.GetFileName(relative) },
                    entries.Select(static row => Normalize(row[1])).ToArray(), "Disabled integration must preserve both original destinations.");
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source), "Profile bytes must not change: " + relative);
        }
        if (enabled)
        {
            Assert.AreEqual(vendorRelative, Normalize(bundle.Single(row => row[0] == vendorSource)[1]));
        }
        else
        {
            Assert.IsFalse(bundle.Any(row => row[0] == vendorSource));
        }
        CollectionAssert.AreEqual(vendorBytes, File.ReadAllBytes(vendorSource));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RawAppXamlIsExcludedOnlyFromPortableContentAndNoneWhileCompilerInputsRemain(bool enabled)
    {
        using var fixture = new PayloadProject();
        fixture.Properties["HubRadioPortablePublish"] = enabled ? "true" : "false";
        string appRoot = Path.Combine(fixture.Root, "src", "HubRadio.App");
        (string ItemType, string Relative)[] rawInputs = [("ApplicationDefinition", "App.xaml"), ("Page", "MainWindow.xaml")];
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string itemType, string relative) in rawInputs)
        {
            string source = Path.Combine(appRoot, relative);
            File.WriteAllText(source, "synthetic raw XAML metadata only: " + relative);
            originals.Add(source, File.ReadAllBytes(source));
            fixture.ExtraItems.Add(new XElement(itemType, new XAttribute("Include", relative),
                new XElement("Generator", "MSBuild:Compile"), new XElement("SubType", "Designer")));
            // Reproduce SDK None without copy metadata and linked Content with a different
            // identity/destination: only FullPath, not basename or Link, establishes overlap.
            fixture.ExtraItems.Add(new XElement("None", new XAttribute("Include", @".\" + relative)));
            fixture.ExtraItems.Add(FileItem("Content", source, "RawSources/" + relative));
        }
        (string ItemType, string Source, string Destination)[] retained =
        [
            ("Content", Path.Combine(fixture.Root, "compiled", "App.xbf"), "App.xbf"),
            ("None", Path.Combine(fixture.Root, "compiled", "MainWindow.xbf"), "MainWindow.xbf"),
            ("Content", Path.Combine(fixture.Root, "compiled", "HubRadio.App.pri"), "HubRadio.App.pri"),
            ("None", fixture.SdkPriPath, "Microsoft.UI.Xaml.Controls.pri"),
            ("Content", Path.Combine(fixture.Root, "OtherRoot", "App.xaml"), "OtherRoot/App.xaml"),
            ("None", Path.Combine(fixture.Root, "OtherRoot", "MainWindow.xaml"), "OtherRoot/MainWindow.xaml"),
            ("Content", Path.Combine(fixture.Root, "OtherRoot", "vendor.raw.xaml"), "OtherRoot/vendor.raw.xaml"),
        ];
        foreach ((string itemType, string source, string destination) in retained)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            if (!File.Exists(source)) { File.WriteAllText(source, "synthetic retained metadata only: " + destination); }
            originals.Add(source, File.ReadAllBytes(source));
            fixture.ExtraItems.Add(FileItem(itemType, source, destination));
        }

        AssertSucceeded(fixture.Run());
        string[][] bundle = ReadRows(fixture.BundleInputsPath, 2);
        string[][] content = ReadRows(fixture.ItemsPath, 6);
        // No XAML compiler runs: prove only that its item types and metadata survive.
        string[][] compilerInputs = ReadRows(fixture.XamlInputsPath, 4);
        Assert.HasCount(rawInputs.Length, compilerInputs);
        Assert.HasCount((enabled ? PayloadFiles.Length + 2 : 4 + 2 * rawInputs.Length) + retained.Length, bundle);
        Assert.AreEqual(enabled, File.Exists(fixture.ManifestPath));
        foreach ((string itemType, string relative) in rawInputs)
        {
            string source = Path.Combine(appRoot, relative);
            CollectionAssert.AreEqual(new[] { itemType, source, "MSBuild:Compile", "Designer" },
                compilerInputs.Single(row => row[0] == itemType));
            string[][] entries = bundle.Where(row => row[0] == source).ToArray();
            Assert.HasCount(enabled ? 0 : 2, entries, relative);
            Assert.HasCount(enabled ? 0 : 1, content.Where(row => row[0] == source).ToArray(), relative);
            if (!enabled)
            {
                CollectionAssert.AreEquivalent(new[] { relative, "RawSources/" + relative },
                    entries.Select(static row => Normalize(row[1])).ToArray());
            }
        }
        foreach ((string itemType, string source, string destination) in retained)
        {
            Assert.AreEqual(destination, Normalize(bundle.Single(row => row[0] == source)[1]));
            Assert.HasCount(itemType == "Content" ? 1 : 0, content.Where(row => row[0] == source).ToArray(), destination);
        }
        Assert.IsTrue(ReadRows(fixture.PriFilesPath, 2).Any(row => row[0] == fixture.SdkPriPath), "The SDK PRI merge input must remain.");
        foreach ((string source, byte[] bytes) in originals) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); }
    }

    [TestMethod]
    [DataRow("missing", "OSL010_INPUT_PATH")]
    [DataRow("relative", "OSL010_INPUT_PATH")]
    [DataRow("drive-relative", "OSL010_INPUT_PATH")]
    [DataRow("nonexistent", "OSL010_PAYLOAD")]
    [DataRow("file", "OSL010_PAYLOAD")]
    [DataRow("missing-evidence", "OSL010_EVIDENCE")]
    [DataRow("evidence-directory", "OSL010_EVIDENCE")]
    public void InvalidInputsFailBeforeAssigningContent(string kind, string code)
    {
        using var fixture = new PayloadProject();
        switch (kind)
        {
            case "missing": fixture.Properties.Remove("HubRadioPortablePayloadPath"); break;
            case "relative": fixture.Properties["HubRadioPortablePayloadPath"] = "relative-payload"; break;
            case "drive-relative": fixture.Properties["HubRadioPortablePayloadPath"] = "C:payload"; break;
            case "nonexistent": fixture.Properties["HubRadioPortablePayloadPath"] = Path.Combine(fixture.Root, "missing"); break;
            case "file": fixture.Properties["HubRadioPortablePayloadPath"] = Path.Combine(fixture.PayloadPath, "Worker/python.exe"); break;
            default:
                string evidence = Path.Combine(fixture.PayloadPath, "Evidence/portable-payload.json");
                File.Delete(evidence);
                if (kind == "evidence-directory") { Directory.CreateDirectory(evidence); }
                break;
        }
        AssertRejected(fixture, code, "AssignTargetPaths");
    }

    [TestMethod]
    [DataRow("Worker")]
    [DataRow("Tools")]
    [DataRow("ModelCatalog")]
    [DataRow("WebView2Runtime")]
    [DataRow("Licenses")]
    [DataRow("Evidence")]
    public void EveryPreparedRootIsRequired(string root)
    {
        using var fixture = new PayloadProject();
        Directory.Delete(Path.Combine(fixture.PayloadPath, root), recursive: true);
        StringAssert.Contains(AssertRejected(fixture, "OSL010_ROOT", "AssignTargetPaths"), root);
    }

    [TestMethod]
    [DataRow("SelfContained", "false")]
    [DataRow("WindowsAppSDKSelfContained", "false")]
    [DataRow("PublishSingleFile", "false")]
    [DataRow("IncludeAllContentForSelfExtract", "false")]
    [DataRow("EnableMsixTooling", "false")]
    [DataRow("RuntimeIdentifier", "win-arm64")]
    [DataRow("WindowsPackageType", "MSIX")]
    public void IncompatiblePublishPropertiesFailClosed(string property, string value)
    {
        using var fixture = new PayloadProject();
        fixture.Properties[property] = value;
        StringAssert.Contains(AssertRejected(fixture, "OSL010_PROFILE", "AssignTargetPaths"), property);
    }

    [TestMethod]
    public void DefaultNoneAndExplicitContentPublishEachProfileOnce()
    {
        using var fixture = new PayloadProject();
        string[] profiles =
        [
            "DefaultSettings/Profiles/1.0.0.json", "Profiles/16x9/landscape-short.json",
            "Profiles/16x9/long-form.json", "Profiles/16x9/youtube-landscape.json",
            "Profiles/9x16/vertical-short.json",
        ];
        foreach (string relative in profiles)
        {
            string source = Path.Combine(fixture.Root, "src/HubRadio.App", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "fixture profile: " + relative);
            // Reproduce the SDK default None (no copy metadata) alongside App's explicit Content.
            fixture.ExtraItems.Add(new XElement("None", new XAttribute("Include", relative)));
            fixture.ExtraItems.Add(FileItem("Content", relative, null));
        }

        AssertSucceeded(fixture.Run());
        string[][] bundle = ReadRows(fixture.ManifestPath, 2);
        string[][] content = ReadRows(fixture.ItemsPath, 6);
        Assert.HasCount(PayloadFiles.Length + 2 + profiles.Length, bundle);
        foreach (string relative in profiles)
        {
            string source = Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App", relative));
            Assert.HasCount(1, bundle.Where(row => row[0] == source && Normalize(row[1]) == relative).ToArray());
            string[] item = content.Single(row => row[0] == source);
            Assert.AreEqual(relative, Normalize(item[2]));
            Assert.AreEqual("PreserveNewest", item[3]);
            Assert.AreEqual("PreserveNewest", item[4]);
            Assert.AreEqual("fixture profile: " + relative, File.ReadAllText(source));
        }
    }

    [TestMethod]
    public void SameSourceWithDifferentDestinationsIsPreserved()
    {
        foreach (string destinationMetadata in new[] { "Link", "TargetPath" })
        {
            using var fixture = new PayloadProject();
            const string original = "local/default.json";
            const string alternate = "DefaultSettings/alternate.json";
            fixture.ExtraItems.Add(new XElement("Content", new XAttribute("Remove", original)));
            fixture.ExtraItems.Add(FileItem("Content", original, null));
            fixture.ExtraItems.Add(new XElement("None", new XAttribute("Include", original),
                new XElement(destinationMetadata, alternate)));

            AssertSucceeded(fixture.Run());
            string source = Path.GetFullPath(Path.Combine(fixture.Root, "src/HubRadio.App/local/default.json"));
            string[][] bundle = ReadRows(fixture.ManifestPath, 2);
            Assert.HasCount(PayloadFiles.Length + 3, bundle);
            CollectionAssert.AreEquivalent(new[] { original, alternate },
                bundle.Where(row => row[0] == source).Select(static row => Normalize(row[1])).ToArray(), destinationMetadata);
        }
    }

    [TestMethod]
    [DataRow("Content", @"Tools\ffmpeg.exe")]
    [DataRow("None", "tools/FFMPEG.EXE")]
    [DataRow("Content", @"Tools\nested\..\ffmpeg.exe")]
    public void ConflictingDestinationsAreRejectedInsteadOfOverwritten(string itemType, string destination)
    {
        using var fixture = new PayloadProject();
        fixture.ExtraItems.Add(FileItem(itemType, "local/custom.json", destination));
        AssertRejected(fixture, "OSL010_COLLISION");
    }

    [TestMethod]
    public void LateBundleProfilesAreRejectedByNormalizedRelativePathRatherThanSourceName()
    {
        foreach (string destination in new[]
        {
            @"Properties\PublishProfiles\win-x64-singlefile.pubxml",
            @"pRoPeRtIeS/PublishProfiles\日本語 profile.PuBxMl",
            @"other\..\Properties\PublishProfiles\..\forced.pubxml",
        })
        {
            using var fixture = new PayloadProject();
            // Bypass both item collection and PrepareForBundle; the existing .json source
            // deliberately disagrees with the .pubxml bundle destination.
            fixture.BundleMutations.Add(new XElement("FilesToBundle", new XAttribute("Include", "local/custom.json"),
                new XElement("RelativePath", destination)));
            string output = AssertRejected(fixture, "OSL010_BUNDLE_PROFILE");
            StringAssert.Contains(output, "FilesToBundle");
            StringAssert.Contains(output, Path.GetFileName(Normalize(destination)));
        }
    }

    [TestMethod]
    [DataRow("exclude")]
    [DataRow("remove")]
    [DataRow("retarget")]
    [DataRow("substitute")]
    public void MissingOrChangedSdkBundleInputsAreRejected(string mutation)
    {
        using var fixture = new PayloadProject();
        string source = Path.GetFullPath(Path.Combine(fixture.PayloadPath, "Worker/python.exe"));
        if (mutation is "remove" or "substitute")
        {
            fixture.Mutations.Add(new XElement("ResolvedFileToPublish", new XAttribute("Remove", source)));
            if (mutation == "substitute")
            {
                fixture.Mutations.Add(new XElement("ResolvedFileToPublish",
                    new XAttribute("Include", "local/custom.json"), new XElement("RelativePath", "Worker/python.exe")));
            }
        }
        else
        {
            fixture.Mutations.Add(new XElement("ResolvedFileToPublish",
                new XAttribute("Condition", "'%(ResolvedFileToPublish.FullPath)' == '" + source + "'"),
                new XElement(mutation == "exclude" ? "ExcludeFromSingleFile" : "RelativePath",
                    mutation == "exclude" ? "true" : "wrong-root/python.exe")));
        }
        StringAssert.Contains(AssertRejected(fixture, "OSL010_BUNDLE_MISSING"), "python.exe");
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("root-with-separator")]
    [DataRow("evidence")]
    [DataRow("case-variant")]
    [DataRow("normalized")]
    [DataRow("relative")]
    public void ReportPathsInsidePayloadAreRejectedBeforeCollectionOnRepeatedAttempts(string kind)
    {
        using var fixture = new PayloadProject();
        string reportPath = Path.Combine(fixture.PayloadPath, "Evidence/bundle.tsv");
        fixture.Properties["HubRadioPortableBundleManifestPath"] = kind switch
        {
            "root" => fixture.PayloadPath,
            "root-with-separator" => fixture.PayloadPath + Path.DirectorySeparatorChar,
            "case-variant" => reportPath.ToUpperInvariant(),
            "normalized" => Path.Combine(fixture.Root, "outside", "..", Path.GetFileName(fixture.PayloadPath), "Evidence/bundle.tsv"),
            "relative" => Path.GetRelativePath(Path.Combine(fixture.Root, "src/HubRadio.App"), reportPath),
            _ => reportPath,
        };
        AssertRejected(fixture, "OSL010_REPORT_PATH", "AssignTargetPaths");
        AssertRejected(fixture, "OSL010_REPORT_PATH");
    }

    [TestMethod]
    public void ExistingReportInsidePayloadIsRejectedWithoutCollectionOrRewrite()
    {
        using var fixture = new PayloadProject();
        fixture.Properties["HubRadioPortableBundleManifestPath"] = Path.Combine(fixture.PayloadPath, "Evidence/bundle.tsv");
        byte[] existing = Encoding.UTF8.GetBytes(Path.Combine(fixture.PayloadPath, "Worker/python.exe") + "\tWorker/python.exe\n");
        File.WriteAllBytes(fixture.ManifestPath, existing);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (int exitCode, string output) = fixture.Run();
            Assert.AreNotEqual(0, exitCode, output);
            StringAssert.Contains(output, "OSL010_REPORT_PATH");
            CollectionAssert.AreEqual(existing, File.ReadAllBytes(fixture.ManifestPath));
            Assert.IsFalse(File.Exists(fixture.ItemsPath), "A stale local-path TSV must not be collected into the bundle.");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "obj")));
        }
    }

    [TestMethod]
    [DataRow("artifacts/portable/debug")]
    [DataRow("prepared payload 日本語-reports")]
    [DataRow("prepared payload 日本語/../artifacts/portable/debug")]
    public void ExplicitBuildArtifactReportIsRepeatableAndNotBundled(string reportDirectory)
    {
        using var fixture = new PayloadProject();
        fixture.Properties["HubRadioPortableBundleManifestPath"] = Path.Combine(fixture.Root, reportDirectory, "bundle.tsv");
        AssertSucceeded(fixture.Run());
        byte[] first = File.ReadAllBytes(fixture.ManifestPath);
        AssertSucceeded(fixture.Run());
        CollectionAssert.AreEqual(first, File.ReadAllBytes(fixture.ManifestPath));
        Assert.HasCount(PayloadFiles.Length + 2, ReadRows(fixture.ManifestPath, 2));
        Assert.IsFalse(ReadRows(fixture.ManifestPath, 2).Any(row => row[0] == fixture.ManifestPath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "obj/portable-files-to-bundle.tsv")));
    }

    private static string AssertRejected(PayloadProject fixture, string code, string target = "Probe")
    {
        (int exitCode, string output) = fixture.Run(target);
        Assert.AreNotEqual(0, exitCode, output);
        StringAssert.Contains(output, code);
        Assert.IsFalse(File.Exists(fixture.ManifestPath), "A rejected publish must not produce a success report.");
        Assert.IsFalse(File.Exists(fixture.ItemsPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "obj")));
        return output;
    }

    private static void AssertSucceeded((int ExitCode, string Output) result) => Assert.AreEqual(0, result.ExitCode, result.Output);
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string[][] ReadRows(string path, int columns)
    {
        Assert.IsTrue(File.Exists(path), "Missing item report: " + path);
        string[][] rows = File.ReadAllLines(path).Select(static line => line.Split('\t')).ToArray();
        Assert.IsNotEmpty(rows);
        foreach (string[] row in rows) { Assert.HasCount(columns, row, path); }
        return rows;
    }

    private static XElement FileItem(string type, string source, string? link) => new(type,
        new XAttribute("Include", source), link is null ? null : new XElement("Link", link),
        new XElement("CopyToOutputDirectory", "PreserveNewest"), new XElement("CopyToPublishDirectory", "PreserveNewest"));

    private sealed class PayloadProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.OSL010-" + Guid.NewGuid().ToString("N"));
        public string PayloadPath => Path.Combine(Root, "prepared payload 日本語");
        public string ItemsPath => Path.Combine(Root, "assigned-items.tsv");
        public string ContentInputsPath => Path.Combine(Root, "content-inputs.tsv");
        public string BundleInputsPath => Path.Combine(Root, "bundle-inputs.tsv");
        public string XamlInputsPath => Path.Combine(Root, "xaml-inputs.tsv");
        public string PriInputsPath => Path.Combine(Root, "pri-inputs.tsv");
        public string PriFilesPath => Path.Combine(Root, "pri-files.tsv");
        public string LayoutFilesPath => Path.Combine(Root, "layout-files.tsv");
        public string SdkPriPath => Path.Combine(Root, "sdk", "Microsoft.UI.Xaml.Controls.pri");
        public string ManifestPath => Path.GetFullPath(Properties.TryGetValue("HubRadioPortableBundleManifestPath", out string? path)
            ? path : Path.Combine(Root, "obj/portable-files-to-bundle.tsv"), Path.Combine(Root, "src/HubRadio.App"));
        public Dictionary<string, string> Properties { get; }
        public List<XElement> ExtraItems { get; } = [];
        public List<XElement> Mutations { get; } = [];
        public List<XElement> BundleMutations { get; } = [];

        public PayloadProject()
        {
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HubRadioPortablePublish"] = "true",
                ["HubRadioPortablePayloadPath"] = PayloadPath,
                ["SelfContained"] = "true",
                ["WindowsAppSDKSelfContained"] = "true",
                ["PublishSingleFile"] = "true",
                ["IncludeAllContentForSelfExtract"] = "true",
                ["EnableMsixTooling"] = "true",
                ["EnableCoreMrtTooling"] = "false",
                ["EnablePriGenTooling"] = "false",
                ["ShouldComputeInputPris"] = "true",
                ["ProjectName"] = "SyntheticApp",
                ["RuntimeIdentifier"] = "win-x64",
                ["WindowsPackageType"] = "None",
                ["IntermediateOutputPath"] = Path.Combine(Root, "obj") + Path.DirectorySeparatorChar,
                ["PublishDir"] = Path.Combine(Root, "publish") + Path.DirectorySeparatorChar,
                ["AssemblyName"] = "SyntheticApp",
                ["_NativeExecutableExtension"] = ".exe",
                ["MicrosoftNETBuildTasksAssembly"] = @"$(MSBuildSDKsPath)\Microsoft.NET.Sdk\tools\net10.0\Microsoft.NET.Build.Tasks.dll",
            };
            foreach (string relative in PayloadFiles) { Write(Path.Combine(PayloadPath, relative), "fixture: " + relative); }
            // Only existence/layout is tested here; OSL-009 / OSL-012 own evidence/hash validation.
            Write(Path.Combine(PayloadPath, "Evidence/portable-payload.json"),
                """{"schemaVersion":"1.0.0","taskId":"OSL009","architecture":"x64","releaseApproved":false,"appPublishRequired":true}""");
            Write(Path.Combine(PayloadPath, "Unexpected/not-bundled.exe"), "outside the six allowed roots");
            Write(Path.Combine(PayloadPath, "root-note.txt"), "not a payload root");
            Write(Path.Combine(Root, "packaging/models/catalog.json"), File.ReadAllText(Path.Combine(PayloadPath, "ModelCatalog/catalog.json")));
            Write(Path.Combine(Root, "packaging/models/Bundles/model/model.bin"), File.ReadAllText(Path.Combine(PayloadPath, "ModelCatalog/Bundles/model/model.bin")));
            Write(Path.Combine(Root, "src/HubRadio.App/local/default.json"), "existing default");
            Write(Path.Combine(Root, "src/HubRadio.App/local/custom.json"), "existing non-repository model metadata");
            Write(SdkPriPath, "synthetic SDK PRI outside the prepared payload");
            Write(Path.Combine(Root, TargetsPath), File.ReadAllText(Path.Combine(GetRepositoryRoot(), TargetsPath)));
            // Exercise the exact OSL-011 import depth from Properties/PublishProfiles, not five parents.
            Write(Path.Combine(Root, "src/HubRadio.App/Properties/PublishProfiles/PortableTest.pubxml"),
                new XElement("Project", new XElement("Import", new XAttribute("Project",
                    @"$(MSBuildThisFileDirectory)..\..\..\..\packaging\portable\PortablePayload.targets"))).ToString());
        }

        public (int ExitCode, string Output) Run(string target = "Probe")
        {
            string projectPath = Path.Combine(Root, "src/HubRadio.App/payload-test.proj");
            // Use the actual App model Content declarations, but resolve them inside this temporary tree.
            XElement[] models = XDocument.Load(Path.Combine(GetRepositoryRoot(), "src/HubRadio.App/HubRadio.App.csproj"))
                .Descendants("Content").Where(static item => (item.Attribute("Include")?.Value ?? string.Empty)
                    .StartsWith(@"..\..\packaging\models\", StringComparison.Ordinal))
                .Select(static item => new XElement(item)).ToArray();
            Assert.HasCount(2, models, "The fixture must cover both current App model source declarations.");
            var project = new XElement("Project",
                new XElement("PropertyGroup", Properties.Select(static pair => new XElement(pair.Key, pair.Value))),
                new XElement("Import", new XAttribute("Project", "Properties/PublishProfiles/PortableTest.pubxml")),
                new XElement("ItemGroup", models,
                    FileItem("Content", "local/default.json", "DefaultSettings/test.json"),
                    FileItem("Content", "local/custom.json", "ModelCatalog/custom.json"), ExtraItems),
                new XElement("Import", new XAttribute("Project", @"$(MSBuildSDKsPath)\Microsoft.NET.Sdk\targets\Microsoft.NET.Publish.targets")),
                new XElement("Target", new XAttribute("Name", "AssignTargetPaths"),
                    AssignPaths("Content", "ContentWithTargetPath"),
                    AssignPaths("None", "_NoneWithTargetPath")),
                // MSIX auto-includes both item types for single-file publish, even default None items
                // with no copy metadata. Without this hook the measured App duplicates are invisible.
                new XElement("Target", new XAttribute("Name", "AutoIncludeCopyToPublishDirectoryItems"),
                    new XAttribute("Condition", "'$(PublishSingleFile)' == 'true'"),
                    new XAttribute("BeforeTargets", "GetCopyToPublishDirectoryItems"),
                    new XAttribute("DependsOnTargets", "AssignTargetPaths"),
                    new XElement("ItemGroup",
                        new XElement("ContentWithTargetPath",
                            new XElement("CopyToPublishDirectory", "PreserveNewest")),
                        new XElement("_NoneWithTargetPath",
                            new XElement("CopyToPublishDirectory", "PreserveNewest")))),
                // Model Microsoft.Windows.SDK.BuildTools.MSIX.Pri.targets, not the inactive MRT Core path.
                // Capture both downstream inputs without invoking MakePri or importing the WinUI build.
                new XElement("Target", new XAttribute("Name", "GenerateProjectPriFile"),
                    new XAttribute("DependsOnTargets", "GetPriOutputs;_GetPriFilesFromPayload;_ComputeInputPriFiles;_GenerateProjectPriConfigurationFiles")),
                new XElement("Target", new XAttribute("Name", "GetPriOutputs"),
                    new XAttribute("DependsOnTargets", "AssignTargetPaths"), new XAttribute("Returns", "@(PriOutputs)"),
                    new XElement("ItemGroup",
                        new XElement("PriOutputs", new XAttribute("Include", "@(ContentWithTargetPath)"),
                            new XElement("OutputGroup", "ContentFilesProjectOutputGroup"), new XElement("ProjectName", "$(ProjectName)")),
                        new XElement("PriOutputs", new XAttribute("Include", SdkPriPath),
                            new XElement("TargetPath", "Microsoft.UI.Xaml.Controls.pri"),
                            new XElement("OutputGroup", "CopyLocalFilesOutputGroup"), new XElement("ProjectName", "$(ProjectName)")))),
                new XElement("Target", new XAttribute("Name", "_GetPriFilesFromPayload"),
                    new XAttribute("Condition", "'$(ShouldComputeInputPris)' == 'true'"), new XAttribute("Returns", "@(_PriFilesFromPayload)"),
                    new XElement("ItemGroup",
                        new XElement("_PriFilesFromPayload", new XAttribute("Include", "@(PriOutputs)"),
                            new XAttribute("Condition", "'%(Extension)' == '.pri' and '%(PriOutputs.ProjectName)' != '$(ProjectName)'")),
                        new XElement("_PriFilesFromPayload", new XAttribute("Include", "@(PriOutputs)"),
                            new XAttribute("Condition", "'%(Extension)' == '.pri' and '%(PriOutputs.ProjectName)' == '$(ProjectName)' and '%(PriOutputs.OutputGroup)' != 'ProjectPriFile'")))),
                new XElement("Target", new XAttribute("Name", "_ComputeInputPriFiles"),
                    new XAttribute("Condition", "'$(ShouldComputeInputPris)' == 'true'"),
                    new XElement("ItemGroup", new XElement("_PriFile", new XAttribute("Include", "@(_PriFilesFromPayload)")))),
                new XElement("Target", new XAttribute("Name", "_GenerateProjectPriConfigurationFiles"),
                    new XElement("ItemGroup", new XElement("_LayoutFile", new XAttribute("Include", "@(PriOutputs)"),
                        new XAttribute("Condition", "'%(PriOutputs.OutputGroup)' == 'ContentFilesProjectOutputGroup' and '%(PriOutputs.ProjectName)' == '$(ProjectName)'"))),
                    new XElement("WriteLinesToFile", new XAttribute("File", PriInputsPath),
                        new XAttribute("Lines", "@(PriOutputs->'%(FullPath)\t%(TargetPath)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
                    new XElement("WriteLinesToFile", new XAttribute("File", PriFilesPath),
                        new XAttribute("Lines", "@(_PriFile->'%(FullPath)\t%(TargetPath)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
                    new XElement("WriteLinesToFile", new XAttribute("File", LayoutFilesPath),
                        new XAttribute("Lines", "@(_LayoutFile->'%(FullPath)\t%(TargetPath)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8"))),
                new XElement("Target", new XAttribute("Name", "_SplitProjectReferencesByFileExistence")),
                new XElement("Target", new XAttribute("Name", "_GetProjectReferenceTargetFrameworkProperties")),
                // Deliberately bypass the SDK conflict guard to exercise OSL-010's own rejection.
                new XElement("Target", new XAttribute("Name", "_HandleFileConflictsForPublish")),
                new XElement("Target", new XAttribute("Name", "MutatePublishItems"),
                    new XAttribute("AfterTargets", "_ComputeCopyToPublishDirectoryItems"),
                    new XElement("ItemGroup", Mutations)),
                new XElement("Target", new XAttribute("Name", "MutateBundleItems"),
                    new XAttribute("BeforeTargets", "RecordHubRadioPortableBundle"),
                    new XElement("ItemGroup", BundleMutations)),
                new XElement("Target", new XAttribute("Name", "Probe"),
                    new XAttribute("DependsOnTargets", "GenerateProjectPriFile;_ComputeCopyToPublishDirectoryItems;PrepareForBundle"),
                    new XElement("WriteLinesToFile", new XAttribute("File", ItemsPath),
                        new XAttribute("Lines", "@(ContentWithTargetPath->'%(FullPath)\t%(Link)\t%(TargetPath)\t%(CopyToOutputDirectory)\t%(CopyToPublishDirectory)\tExcludeFromSingleFile=%(ExcludeFromSingleFile)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
                    new XElement("WriteLinesToFile", new XAttribute("File", ContentInputsPath),
                        new XAttribute("Lines", "@(Content->'%(FullPath)\t%(Link)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
                    new XElement("WriteLinesToFile", new XAttribute("File", BundleInputsPath),
                        new XAttribute("Lines", "@(FilesToBundle->'%(FullPath)\t%(RelativePath)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8")),
                    new XElement("WriteLinesToFile", new XAttribute("File", XamlInputsPath),
                        new XAttribute("Lines", "@(ApplicationDefinition->'ApplicationDefinition\t%(FullPath)\t%(Generator)\t%(SubType)');@(Page->'Page\t%(FullPath)\t%(Generator)\t%(SubType)')"),
                        new XAttribute("Overwrite", "true"), new XAttribute("Encoding", "UTF-8"))));
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
            foreach (string argument in new[] { "msbuild", projectPath, "-nologo", "-v:minimal", "-nr:false", "-m:1", "-t:" + target })
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
                Assert.Fail("The synthetic item-only MSBuild target timed out; no app build should run.");
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }

        private static XElement AssignPaths(string source, string output) => new("AssignTargetPath",
            new XAttribute("Files", "@(" + source + ")"), new XAttribute("RootFolder", "$(MSBuildProjectDirectory)"),
            new XElement("Output", new XAttribute("TaskParameter", "AssignedFiles"), new XAttribute("ItemName", output)));

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