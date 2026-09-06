using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;

namespace HubRadio.Architecture.Tests;

// Contract and negative-process tests only. No ignored vendor binaries are used;
// these tests do not establish real payload, runtime, clean-machine or release success.
[TestClass]
public sealed class PortablePayloadTests
{
    private const string ScriptPath = "packaging/portable/prepare-payload.ps1";
    private static readonly string[] ParameterNames =
    [
        "PythonRuntimePath", "FfmpegPath", "FfprobePath", "FfmpegLicensePath",
        "ModelBundlePath", "WebView2RuntimePath", "WebView2LicensePath", "OutputPath",
    ];

    [TestMethod]
    public void BuilderDeclaresOnlyEightMandatoryInputPaths()
    {
        string script = ReadScript();
        StringAssert.StartsWith(script, "#Requires -Version 7.0\n\n<#\n");
        StringAssert.Contains(script, "\n#>\n");
        string parameters = Match(script, @"(?ms)^param\(\n(?<value>.*?)^\)").Groups["value"].Value;
        string[] names = Matches(parameters, @"\[Parameter\(Mandatory\)\] \[string\]\$(\w+)")
            .Select(static match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(ParameterNames, names);
        Assert.HasCount(8, Matches(parameters, @"\[Parameter\("));
        Assert.IsFalse(parameters.Contains('=') || parameters.Contains("[switch]", StringComparison.Ordinal));
        StringAssert.Contains(script, "$PSVersionTable.PSEdition -ne 'Core'");
        StringAssert.Contains(script, "-not $IsWindows");
        StringAssert.Contains(script, "::OSArchitecture -ne 'X64'");
        StringAssert.Contains(script, "::ProcessArchitecture -ne 'X64'");
    }

    [TestMethod]
    public void MetadataContractIsPayloadOnlyOrdinalAndExcludesItsOwnManifest()
    {
        string script = ReadScript();
        string roots = Match(script, @"(?m)^\$payloadRoots = @\((?<value>[^\n]+)\)$").Groups["value"].Value;
        CollectionAssert.AreEqual(
            new[] { "Worker", "Tools", "ModelCatalog", "WebView2Runtime", "Licenses", "Evidence" },
            Matches(roots, "'([^']+)'").Select(static match => match.Groups[1].Value).ToArray());
        string metadata = Match(
            script,
            @"(?ms)^    \$payloadManifest = \[ordered\]@\{\n(?<value>.*?)^    \}").Groups["value"].Value;
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion", "taskId", "architecture", "releaseApproved", "appPublishRequired",
                "rootDirectories", "webView2LicenseSnapshot", "fileCount", "totalBytes", "artifactSetSha256", "files",
            },
            Matches(metadata, @"(?m)^        (\w+) =").Select(static match => match.Groups[1].Value).ToArray());
        foreach (string required in new[]
        {
            "schemaVersion = '1.0.0'", "releaseApproved = $false", "appPublishRequired = $true",
            "kind = 'official-license-modal-page-text-snapshot'", "isStandaloneLicenseFile = $false",
            "fileCount = $actual.Count", "files = @($actual.Values)", "artifactSetSha256 = $digest",
        })
        {
            StringAssert.Contains(metadata, required);
        }
        StringAssert.Contains(script, "SortedDictionary[string, object]]::new([StringComparer]::Ordinal)");
        StringAssert.Contains(script, "concatenated(path NUL byteSize-invariant NUL lowercase-sha256 LF)");
        StringAssert.Contains(script, "path = $relative; byteSize = [long]$Expected.byteSize; sha256 = [string]$Expected.sha256");
        int inventory = script.IndexOf("$actual = Get-TreeRecords $stageRoot", StringComparison.Ordinal);
        int manifest = script.IndexOf("$manifestPath = Join-Path $stageRoot 'Evidence\\portable-payload.json'", StringComparison.Ordinal);
        Assert.IsTrue(inventory >= 0 && manifest > inventory, "Inventory must precede manifest creation to avoid a recursive hash.");
        StringAssert.Contains(script, "} | ConvertTo-Json -Compress");
    }

    [TestMethod]
    public void FixedLayoutConsumesManifestsAndContainsTheEligibleFourModelFiles()
    {
        string root = GetRepositoryRoot();
        string script = ReadScript();
        string evidence = Match(script, @"(?ms)^\$evidenceSources = \[ordered\]@\{\n(?<value>.*?)^\}").Groups["value"].Value;
        CollectionAssert.AreEqual(
            new[]
            {
                "python-runtime-manifest.json", "ffmpeg-manifest.json", "model-catalog.json",
                "webview2-runtime-manifest.json", "web-dependency-manifest.json",
            },
            Matches(evidence, @"(?m)^    '([^']+)' =").Select(static match => match.Groups[1].Value).ToArray());
        foreach (string required in new[]
        {
            "'Tools/ffmpeg.exe'", "'Tools/ffprobe.exe'", "'ModelCatalog/catalog.json'",
            "'ModelCatalog/' + $runtimeModel.bundle.relativePath + '/' + $file.name",
            "'Licenses/FFmpeg-LICENSE.txt'", "'Licenses/CPython-LICENSE.txt'",
            "'Licenses/WebView2-FIXED-LICENSE-PAGE.txt'", "'Licenses/THIRD-PARTY-NOTICES-NONFINAL.md'",
            "$entry.Name -cne '.cache'", "$licenseEvidence.sha256 -cne $licenseSnapshot.sha256",
            "$modelRelativePath = [string]$runtimeModel.bundle.relativePath",
            "[string]::IsNullOrWhiteSpace($modelRelativePath)", "[IO.Path]::IsPathRooted($modelRelativePath)",
            "$modelCatalogRoot = Join-Path $OutputPath 'ModelCatalog'",
            "Get-LocalPath (Join-Path $modelCatalogRoot $modelRelativePath) 'ModelBundleRelativePath'",
            "$webFiles.Count -ne $webOutput.expectedFileCount",
            "[long](($webFiles.Values | Measure-Object byteSize -Sum).Sum) -ne $webOutput.expectedTotalBytes",
            "(Get-ArtifactSetSha256 $webFiles) -cne $webOutput.expectedArtifactSetSha256",
        })
        {
            StringAssert.Contains(script, required);
        }
        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "packaging/models/catalog.json")));
        JsonElement[] eligible = catalog.RootElement.GetProperty("models").EnumerateArray()
            .Where(static model => model.GetProperty("runtimeEligible").GetBoolean()).ToArray();
        Assert.HasCount(1, eligible);
        Assert.AreEqual("asr.faster-whisper-small.cpu-int8", eligible[0].GetProperty("modelId").GetString());
        Assert.IsFalse(eligible[0].GetProperty("releaseApproved").GetBoolean());
        JsonElement bundle = eligible[0].GetProperty("bundle");
        CollectionAssert.AreEqual(
            new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" },
            bundle.GetProperty("files").EnumerateArray().Select(static file => file.GetProperty("name").GetString()).ToArray());
        int containment = script.IndexOf(
            "if ($modelDestination -ieq $modelCatalogRoot -or -not (Test-Within $modelDestination $modelCatalogRoot))", StringComparison.Ordinal);
        int firstOutputCreation = script.IndexOf("\nNew-SafeDirectory $parent\n", StringComparison.Ordinal);
        Assert.IsTrue(containment >= 0 && firstOutputCreation > containment,
            "The catalog bundle destination must be a strict descendant of ModelCatalog before output creation.");
    }

    [TestMethod]
    public void BuilderKeepsOfflineHashEvidenceCopyAndVerificationBoundariesExplicit()
    {
        string script = ReadScript();
        foreach (string required in new[]
        {
            "GetFinalPathNameByHandleW", "[IO.FileAttributes]::ReparsePoint", "[IO.DriveType]::Fixed",
            "@($webView2Manifest.runtime.selectedSignedFiles).Count -ne 2", "@('msedge.dll', 'msedgewebview2.exe')",
            "$selected[0].signerSubject -cne $microsoftSubject", "$selected[0].signatureStatus -cne 'Valid'",
            "Assert-FileEvidence (Join-Path $WebView2RuntimePath $name) $selected[0]",
            "[IO.FileMode]::CreateNew", "[IO.FileAttributes]::ReadOnly",
            "[IO.Directory]::Move($stageRoot, $OutputPath)", "CreateNewDirectory($stageRoot)",
            "'.verification.partial'", "Remove-OwnedTree $verificationWorkspace", "Remove-OwnedTree $stageRoot",
            "& $pythonVerifyScriptPath -RuntimePath $PythonRuntimePath -WorkspacePath $verificationWorkspace",
            "([int]$pythonManifest.output.expectedFileCount + 1)", "OSL009:WORKER_FILES",
        })
        {
            StringAssert.Contains(script, required);
        }
        string copy = Match(script, @"(?ms)^function Copy-PayloadFile\(.*?\{\n(?<value>.*?)^\}").Groups["value"].Value;
        Assert.HasCount(2, Matches(copy, @"Assert-FileEvidence \$SourcePath \$Expected"));
        StringAssert.Contains(copy, "Assert-FileEvidence $destination $Expected");
        foreach (string forbidden in new[]
        {
            "Invoke-WebRequest", "Invoke-RestMethod", "Start-BitsTransfer", "HttpClient",
            "wintrust.dll", "VerifyMicrosoftSignature", "X509Certificates",
            "Get-AuthenticodeSignature", "Install-Module", "Install-Package", "winget", "pip install",
            "Start-Process", "cmd.exe", "powershell.exe", "dotnet publish", "dotnet build",
        })
        {
            Assert.IsFalse(script.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
    }

    [TestMethod]
    [DataRow("PythonRuntimePath")]
    [DataRow("FfmpegPath")]
    [DataRow("FfprobePath")]
    [DataRow("FfmpegLicensePath")]
    [DataRow("ModelBundlePath")]
    [DataRow("WebView2RuntimePath")]
    [DataRow("WebView2LicensePath")]
    [DataRow("OutputPath")]
    public void RelativeInputsAreRejectedBeforeCreatingOutput(string parameter)
    {
        using var fixture = new RejectionInputs();
        fixture.Paths[parameter] = "relative-input";
        AssertRejected(fixture, $"OSL009:INPUT_PATH:{parameter}");
    }

    [TestMethod]
    [DataRow(@"\\server\share\runtime")]
    [DataRow(@"\\?\C:\runtime")]
    [DataRow(@"C:runtime")]
    [DataRow(@"C:\runtime:secret")]
    [DataRow(@"C:\input\..\runtime")]
    [DataRow(@"C:\NUL.txt")]
    [DataRow(@"C:\input.\runtime")]
    [DataRow(@"C:\input\\runtime")]
    public void NoncanonicalAndDevicePathsAreRejectedBeforeFileAccess(string path)
    {
        using var fixture = new RejectionInputs();
        fixture.Paths["PythonRuntimePath"] = path;
        AssertRejected(fixture, "OSL009:INPUT_PATH:PythonRuntimePath");
    }

    [TestMethod]
    [DataRow(new[] { "payload;items", "payload,items", "payload%3Bescaped" })]
    [DataRow(new[] { "payload'quoted", "payload\"quoted" })]
    [DataRow(new[] { "payload$(Property)", "payload@(Item)" })]
    [DataRow(new[] { "payload`literal" })]
    [DataRow(new[] { "payload\tcontrol", "payload\u007fcontrol" })]
    [DataRow(new[] { @"PAYLOA~1\payload" })]
    public void UnsupportedMsbuildOutputPathsAreRejectedBeforeFileAccess(string[] names)
    {
        // Test each name separately so one forbidden character cannot mask another.
        foreach (string name in names)
        {
            using var fixture = new RejectionInputs();
            fixture.Paths["OutputPath"] = Path.Combine(fixture.OutputParent, name);
            AssertRejected(fixture, "OSL009:OUTPUT_MSBUILD_PATH");
        }
    }

    [TestMethod]
    public void OutputOutsidePortableRootIsRejected()
    {
        using var fixture = new RejectionInputs();
        fixture.Paths["OutputPath"] = Path.Combine(fixture.InputRoot, "outside-output");
        AssertRejected(fixture, "OSL009:OUTPUT_SCOPE");
    }

    [TestMethod]
    [DataRow("equal")]
    [DataRow("ancestor")]
    [DataRow("descendant")]
    public void OutputAndInputOverlapIsRejectedInBothDirections(string relationship)
    {
        using var fixture = new RejectionInputs();
        string output = fixture.Paths["OutputPath"];
        fixture.Paths["PythonRuntimePath"] = relationship switch
        {
            "equal" => output.ToUpperInvariant(),
            "ancestor" => fixture.OutputParent,
            _ => Path.Combine(output, "runtime"),
        };
        AssertRejected(fixture, "OSL009:OVERLAP");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingOutputIsNeverOverwritten(bool directory)
    {
        using var fixture = new RejectionInputs();
        string output = fixture.Paths["OutputPath"];
        Directory.CreateDirectory(fixture.OutputParent);
        string sentinel = directory ? Path.Combine(Directory.CreateDirectory(output).FullName, "keep.txt") : output;
        File.WriteAllText(sentinel, "pre-existing output");
        AssertRejected(fixture, "OSL009:OUTPUT_EXISTS", outputAlreadyExists: true);
        Assert.AreEqual("pre-existing output", File.ReadAllText(sentinel));
        CollectionAssert.AreEqual(new[] { output }, Directory.GetFileSystemEntries(fixture.OutputParent));
    }

    [TestMethod]
    public void MissingRuntimeIsRejectedWithoutCreatingStaging()
    {
        using var fixture = new RejectionInputs();
        // Input/vendor names remain literal; only OutputPath crosses the MSBuild boundary.
        foreach (string name in ParameterNames)
        {
            if (name != "OutputPath") { fixture.Paths[name] += "-literal;,%'$@`~1"; }
        }
        fixture.Paths["OutputPath"] += "-local (v1)+[draft]~draft";
        AssertRejected(fixture, "OSL009:MISSING_INPUT");
    }

    [TestMethod]
    public void NonmatchingComponentBytesAreRejectedWithoutInvokingRuntime()
    {
        using var fixture = new RejectionInputs();
        foreach ((string name, string path) in fixture.Paths)
        {
            if (name == "OutputPath") { continue; }
            if (name is "PythonRuntimePath" or "ModelBundlePath" or "WebView2RuntimePath")
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, "deliberately invalid component, not an executable");
            }
        }
        AssertRejected(fixture, "OSL009:HASH");
    }

    private static void AssertRejected(RejectionInputs fixture, string reason, bool outputAlreadyExists = false)
    {
        string[] before = Directory.GetFileSystemEntries(fixture.InputRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        Dictionary<string, byte[]> inputBytes = Directory.GetFiles(fixture.InputRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(static path => path, File.ReadAllBytes, StringComparer.Ordinal);
        var start = new ProcessStartInfo(FindLatestPowerShell())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(GetRepositoryRoot(), ScriptPath) })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (string name in ParameterNames)
        {
            start.ArgumentList.Add("-" + name);
            start.ArgumentList.Add(fixture.Paths[name]);
        }
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        using var process = new Process { StartInfo = start };
        Assert.IsTrue(process.Start());
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("Invalid inputs must be rejected promptly, without launching component runtimes.");
        }
        string stdout = output.GetAwaiter().GetResult();
        string stderr = error.GetAwaiter().GetResult();
        Assert.AreNotEqual(0, process.ExitCode, stdout + stderr);
        Assert.IsFalse(stderr.Contains("ParserError", StringComparison.OrdinalIgnoreCase), stderr);
        StringAssert.Contains(stderr, reason);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), "A rejection must not emit a success result: " + stdout);
        Assert.AreEqual("untouched input", File.ReadAllText(Path.Combine(fixture.InputRoot, "keep.txt")));
        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(fixture.InputRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray());
        foreach ((string path, byte[] bytes) in inputBytes)
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "Rejection modified an input: " + path);
        }
        if (!outputAlreadyExists)
        {
            Assert.IsFalse(Directory.Exists(fixture.OutputParent), "Rejection created output or partial staging directories.");
        }
    }

    private static string FindLatestPowerShell()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != CpuArchitecture.X64 ||
            RuntimeInformation.ProcessArchitecture != CpuArchitecture.X64)
        {
            Assert.Inconclusive("Negative process tests require Windows x64; metadata tests remain platform independent.");
        }
        var candidates = new List<string>();
        string installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
        if (Directory.Exists(installedRoot))
        {
            candidates.AddRange(Directory.GetDirectories(installedRoot).Select(static path => Path.Combine(path, "pwsh.exe")));
        }
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static path => path.Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(static path => Path.Combine(path, "pwsh.exe")));
        string? executable = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .Select(static path => (Path: path, Info: FileVersionInfo.GetVersionInfo(path)))
            .Where(static entry => entry.Info.FileMajorPart >= 7)
            .OrderByDescending(static entry => new Version(
                entry.Info.FileMajorPart, entry.Info.FileMinorPart, entry.Info.FileBuildPart, entry.Info.FilePrivatePart))
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .Select(static entry => entry.Path).FirstOrDefault();
        if (executable is null) { Assert.Inconclusive("Install PowerShell 7+ before running negative process tests; never fall back to Windows PowerShell."); }
        return executable!;
    }

    private sealed class RejectionInputs : IDisposable
    {
        public string InputRoot { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.OSL009-" + Guid.NewGuid().ToString("N"));
        public string OutputParent { get; } = Path.Combine(GetRepositoryRoot(), "artifacts", "portable", "osl009-test-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Paths { get; }

        public RejectionInputs()
        {
            Directory.CreateDirectory(InputRoot);
            File.WriteAllText(Path.Combine(InputRoot, "keep.txt"), "untouched input");
            Paths = ParameterNames.ToDictionary(static name => name, name => Path.Combine(InputRoot, name), StringComparer.Ordinal);
            Paths["OutputPath"] = Path.Combine(OutputParent, "payload");
        }

        public void Dispose()
        {
            Directory.Delete(InputRoot, recursive: true);
            if (Directory.Exists(OutputParent)) { Directory.Delete(OutputParent, recursive: true); }
        }
    }

    private static string ReadScript() => File.ReadAllText(Path.Combine(GetRepositoryRoot(), ScriptPath)).ReplaceLineEndings("\n");
    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static Match Match(string text, string pattern)
    {
        Match result = Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.IsTrue(result.Success, "Missing contract section: " + pattern);
        return result;
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