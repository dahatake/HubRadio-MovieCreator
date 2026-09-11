using System.Text.Json;
using System.Xml.Linq;

namespace HubRadio.Architecture.Tests;

// Pure source-contract checks. Do not execute targets, compilers, packaging scripts,
// runtime processes or download tools; real build/payload acceptance is separate.
[TestClass]
public sealed class RuntimeResourceBuildContractTests
{
    [TestMethod]
    public void SmallPinnedInputsAreEmbeddedButTheApplicationDigestIsNeverEmbeddedInItsOwnDll()
    {
        XDocument project = Load("src/HubRadio.App/HubRadio.App.csproj");
        Assert.IsTrue(project.Descendants("Import").Any(element => element.Attribute("Project")?.Value == @"..\..\packaging\RuntimeResources.targets"));
        XDocument targets = Load("packaging/RuntimeResources.targets");
        CollectionAssert.AreEquivalent(new[] { "HubRadio.App.Resources.PythonRuntimeManifest.json", "HubRadio.App.Resources.FontManifest.json" },
            targets.Descendants("EmbeddedResource").Select(element => element.Attribute("LogicalName")!.Value).ToArray());
        Assert.IsFalse(targets.Descendants("EmbeddedResource").Any(element => element.ToString().Contains("application-build", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationBuildManifestHashesTheCompiledDllAndIsGeneratedBeforePortableRecording()
    {
        XDocument targets = Load("packaging/RuntimeResources.targets");
        XElement output = Target(targets, "GenerateHubRadioApplicationBuildManifest");
        Assert.AreEqual("CopyFilesToOutputDirectory", output.Attribute("AfterTargets")?.Value);
        Assert.AreEqual("$(TargetPath)", output.Element("GetFileHash")!.Attribute("Files")?.Value);
        Assert.AreEqual("SHA256", output.Element("GetFileHash")!.Attribute("Algorithm")?.Value);
        StringAssert.Contains(output.Element("WriteLinesToFile")!.Attribute("File")!.Value, @"Evidence\application-build.json");
        XElement bundle = Target(targets, "IncludeHubRadioApplicationBuildManifestInBundle");
        Assert.AreEqual("PrepareForBundle", bundle.Attribute("AfterTargets")?.Value);
        CollectionAssert.AreEquivalent(new[] { "RecordHubRadioPortableBundle", "GenerateSingleFileBundle" },
            bundle.Attribute("BeforeTargets")!.Value.Split(';'));
        StringAssert.Contains(bundle.Descendants("_HubRadioBundledApplication").Single().Attribute("Condition")!.Value, "HubRadio.App.dll");
        Assert.AreEqual("@(_HubRadioBundledApplication)", bundle.Element("GetFileHash")!.Attribute("Files")?.Value);
        Assert.AreEqual("Evidence/application-build.json", bundle.Descendants("FilesToBundle").Single().Element("RelativePath")?.Value);
        Assert.IsFalse(output.Descendants("Exec").Any() || bundle.Descendants("Exec").Any());
        string writer = targets.Descendants("UsingTask").Single().Value;
        foreach (string field in new[] { "schemaVersion", "relativePath", "version", "size", "sha256" }) { StringAssert.Contains(writer, field); }
        Assert.IsFalse(writer.Contains("HubRadio.App.exe", StringComparison.Ordinal));
        Assert.IsFalse(writer.Contains("releaseApproved", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimePayloadIsExplicitAndNormalBuildsDoNotCopyModelOrWorkerTreesImplicitly()
    {
        XDocument targets = Load("packaging/RuntimeResources.targets");
        XElement collect = Target(targets, "IncludeHubRadioRuntimePayload");
        StringAssert.Contains(collect.Attribute("Condition")!.Value, "'$(HubRadioRuntimePayloadPath)' != ''");
        StringAssert.Contains(collect.Attribute("Condition")!.Value, "'$(HubRadioPortablePublish)' != 'true'");
        Assert.AreEqual("$(HubRadioPortablePayloadPath)", targets.Descendants("HubRadioRuntimePayloadPath").Single().Value);
        string pattern = collect.Descendants("_HubRadioRuntimePayloadFile").First().Attribute("Include")!.Value;
        foreach (string root in new[] { "Worker", "Tools", "ModelCatalog", "Fonts", "Licenses", "Evidence" }) { StringAssert.Contains(pattern, root + @"\**\*"); }
        Assert.IsFalse(pattern.Contains("WebView2Runtime", StringComparison.Ordinal), "Regular builds use the existing non-portable WebView2 startup contract.");
        StringAssert.Contains(Target(targets, "VerifyCopiedHubRadioRuntimePayload").Element("Exec")!.Attribute("Command")!.Value, "-CopyDestination");
        XElement models = Load("src/HubRadio.App/HubRadio.App.csproj").Descendants("Content").Single(element =>
            element.Attribute("Include")?.Value == @"..\..\packaging\models\Bundles\**\*");
        StringAssert.Contains(models.Attribute("Condition")!.Value, "HubRadioRuntimePayloadPath");
        string script = Read("build.ps1");
        StringAssert.Contains(script, "[string]$RuntimePayloadPath");
        StringAssert.Contains(script, "-p:HubRadioRuntimePayloadPath=$RuntimePayloadPath");
        StringAssert.Contains(Read("packaging/portable/verify-runtime-payload.ps1"), "RUNTIME:COPY_SET");
    }

    [TestMethod]
    public void RuntimeDataIsExcludedFromBothPriCollectorsButRemainsCopyAndPublishContent()
    {
        XDocument targets = Load("packaging/RuntimeResources.targets");
        XElement exclude = Target(targets, "ExcludeHubRadioRuntimePayloadFromPri");
        Assert.AreEqual("GetPriOutputs", exclude.Attribute("AfterTargets")?.Value);
        Assert.AreEqual("_GenerateProjectPriConfigurationFiles", exclude.Attribute("BeforeTargets")?.Value);
        Assert.AreEqual("IncludeHubRadioRuntimePayload", exclude.Attribute("DependsOnTargets")?.Value);
        Assert.AreEqual("@(_HubRadioRuntimePayloadFile)",
            exclude.Descendants("_AppxLayoutAssetPackageFiles").Single().Attribute("Include")?.Value);
        Assert.IsFalse(exclude.Descendants("Content").Any() || exclude.Descendants("PackagingOutputs").Any());
        XElement content = Target(targets, "IncludeHubRadioRuntimePayload").Descendants("Content")
            .Single(element => element.Attribute("Include") is not null);
        Assert.AreEqual("PreserveNewest", content.Element("CopyToOutputDirectory")?.Value);
        Assert.AreEqual("PreserveNewest", content.Element("CopyToPublishDirectory")?.Value);
        Assert.IsFalse(targets.Descendants("AppxGeneratePriEnabled").Any(), "WinUI PRI generation must remain enabled.");
    }

    [TestMethod]
    public void PreparedPayloadNeverContainsThePostCompileApplicationBuildManifest()
    {
        XDocument portable = Load("packaging/portable/PortablePayload.targets");
        StringAssert.Contains(portable.Descendants("_HubRadioPortableRoot").Single().Attribute("Include")!.Value, ";Fonts;");
        Assert.IsTrue(portable.Descendants("Error").Any(error => error.Attribute("Code")?.Value == "OSL010_BUILD_EVIDENCE"));
        string builder = Read("packaging/portable/build-portable.ps1");
        StringAssert.Contains(builder, "$relative -ieq 'Evidence/application-build.json'");
        StringAssert.Contains(builder, "'verify-runtime-payload.ps1'");
        StringAssert.Contains(builder, "Assert-ApplicationBuildEvidence $contentsRoot");
        StringAssert.Contains(builder, "build-identity-not-authenticity-or-release-approval");
        string verifier = Read("packaging/portable/verify-portable.ps1");
        StringAssert.Contains(verifier, "$name -cne 'Evidence/portable-payload.json' -and $name -cne 'Evidence/application-build.json'");
        StringAssert.Contains(verifier, "$name.StartsWith('Fonts/', [StringComparison]::Ordinal)");
        StringAssert.Contains(verifier, "Assert-RecordSet $declaredContents $actualContents 'CONTENTS'");
        StringAssert.Contains(verifier, "$sbom.files.Count -ne $actualContents.Count");
    }

    [TestMethod]
    public void FontContractHasFixedOfficialIdentityAndRequiresMeasuredPinsBeforeItCanBeShipped()
    {
        using JsonDocument document = JsonDocument.Parse(Read("packaging/fonts/font-manifest.json"));
        JsonElement font = document.RootElement;
        Assert.AreEqual(1, font.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("Noto Sans JP", font.GetProperty("family").GetString());
        Assert.AreEqual("Regular", font.GetProperty("style").GetString());
        Assert.AreEqual("2.004", font.GetProperty("version").GetString());
        Assert.AreEqual("523d033d6cb47f4a80c58a35753646f5c3608a78", font.GetProperty("sourceCommit").GetString());
        Assert.AreEqual("Sans/SubsetOTF/JP/NotoSansJP-Regular.otf", font.GetProperty("sourcePath").GetString());
        Assert.AreEqual("NotoSansJP-Regular.otf", font.GetProperty("relativePath").GetString());
        Assert.IsFalse(font.GetProperty("releaseApproved").GetBoolean());
        JsonElement license = font.GetProperty("license");
        Assert.AreEqual("LICENSE", license.GetProperty("sourcePath").GetString());
        Assert.AreEqual("OFL-1.1", license.GetProperty("spdxId").GetString());
        Assert.AreEqual(4533028L, font.GetProperty("byteSize").GetInt64());
        Assert.AreEqual("dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073", font.GetProperty("sha256").GetString());
        Assert.AreEqual(4301L, license.GetProperty("byteSize").GetInt64());
        Assert.AreEqual("6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2", license.GetProperty("sha256").GetString());
        // These pins were measured from the fixed upstream Git blobs, not local self-approval.
        foreach (JsonElement record in new[] { font, license })
        {
            string hash = record.GetProperty("sha256").GetString()!;
            Assert.IsTrue(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            Assert.IsTrue(record.GetProperty("byteSize").GetInt64() > 0);
        }
        StringAssert.Contains(Read("packaging/portable/prepare-payload.ps1"), "OSL009:FONT_PIN");
        StringAssert.Contains(Read("packaging/portable/verify-runtime-payload.ps1"), "RUNTIME:FONT_PIN");
    }

    private static XElement Target(XDocument document, string name) => document.Descendants("Target").Single(element => element.Attribute("Name")?.Value == name);
    private static XDocument Load(string relative) => XDocument.Parse(Read(relative));
    private static string Read(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HubRadio.slnx"))) { root = root.Parent; }
        return File.ReadAllText(Path.Combine((root ?? throw new DirectoryNotFoundException()).FullName,
            relative.Replace('/', Path.DirectorySeparatorChar))).ReplaceLineEndings("\n");
    }
}