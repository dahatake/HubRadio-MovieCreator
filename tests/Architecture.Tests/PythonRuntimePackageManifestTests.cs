using System.Diagnostics;
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
        "1a1b1ab2e6ad4ecef67c2bb24b2229a7d18ce9279e3eae7207e2619db7107595";

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
        Assert.AreEqual(2696, output.GetProperty("expectedFileCount").GetInt32());
        Assert.AreEqual(268_462_606, output.GetProperty("expectedTotalBytes").GetInt64());
        Assert.AreEqual(
            "eaadb7f2273afdb228b83df3966464703550c27a76a3ae0bb6b5e8539bb50350",
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
        Assert.AreEqual(7, expectedFiles.Length);
        Assert.AreEqual(7, worker.GetProperty("fileCount").GetInt32());
        Assert.AreEqual(65_381, worker.GetProperty("totalBytes").GetInt64());
        Assert.AreEqual(65_381, expectedFiles.Sum(static file => file.GetProperty("byteSize").GetInt64()));

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

    [TestMethod]
    [DataRow(true, "none")]
    [DataRow(false, "none")]
    [DataRow(false, "directory")]
    [DataRow(false, "file")]
    [DataRow(true, "directory")]
    [DataRow(true, "file")]
    public void RuntimeTreeMove_RequiresSuccessfulMoveAndPreservesExistingDestination(
        bool sourceExists, string destinationKind)
    {
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "hubradio-runtime-move-" + Guid.NewGuid().ToString("N"));
        string sourcePath = Path.Combine(fixtureRoot, ".runtime.partial");
        string destinationPath = Path.Combine(fixtureRoot, "runtime");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            if (sourceExists)
            {
                Directory.CreateDirectory(sourcePath);
                File.WriteAllText(Path.Combine(sourcePath, "payload.txt"), "owned payload");
            }
            if (destinationKind == "directory")
            {
                Directory.CreateDirectory(destinationPath);
                File.WriteAllText(Path.Combine(destinationPath, "payload.txt"), "foreign payload");
            }
            else if (destinationKind == "file")
            {
                File.WriteAllText(destinationPath, "foreign payload");
            }

            using JsonDocument result = RunRuntimeProbe("build-runtime.ps1", $$"""
                $moveFunctions = @($ast.FindAll({ param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq 'Move-GeneratedTreeWithRetry'
                }, $true))
                if ($moveFunctions.Count -ne 1) { throw 'Expected exactly one runtime move helper.' }
                . ([scriptblock]::Create($moveFunctions[0].Extent.Text))
                $succeeded = $false
                try {
                    Move-GeneratedTreeWithRetry `
                        -SourcePath '{{sourcePath.Replace("'", "''", StringComparison.Ordinal)}}' `
                        -DestinationPath '{{destinationPath.Replace("'", "''", StringComparison.Ordinal)}}'
                    $succeeded = $true
                }
                catch [IO.IOException], [UnauthorizedAccessException] { }
                [pscustomobject]@{ succeeded = $succeeded } | ConvertTo-Json -Compress
                """);

            bool expectedSuccess = sourceExists && destinationKind == "none";
            Assert.AreEqual(expectedSuccess, result.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.AreEqual(sourceExists && !expectedSuccess, Directory.Exists(sourcePath));
            if (sourceExists)
            {
                string retainedRoot = expectedSuccess ? destinationPath : sourcePath;
                Assert.AreEqual("owned payload", File.ReadAllText(Path.Combine(retainedRoot, "payload.txt")));
            }
            if (destinationKind == "directory")
            {
                Assert.AreEqual("foreign payload", File.ReadAllText(Path.Combine(destinationPath, "payload.txt")));
            }
            else if (destinationKind == "file")
            {
                Assert.AreEqual("foreign payload", File.ReadAllText(destinationPath));
            }
            else
            {
                Assert.AreEqual(expectedSuccess, Directory.Exists(destinationPath));
                Assert.IsFalse(File.Exists(destinationPath));
            }
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RuntimeBuildCleanup_PreservesOriginalFailureWhenCleanupFails(bool pathCheckFails)
    {
        using JsonDocument result = RunRuntimeProbe("build-runtime.ps1", $$"""
            # Execute the real outer finally block, never the runtime build body.
            $buildFinalizers = @($ast.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.TryStatementAst] -and $null -ne $_.Finally -and
                $_.Finally.Extent.Text.Contains('Remove-GeneratedTree', [StringComparison]::Ordinal)
            })
            if ($buildFinalizers.Count -ne 1) { throw 'Expected exactly one runtime build finalizer.' }
            $pathCheckFails = {{(pathCheckFails ? "$true" : "$false")}}
            $script:pathChecks = 0
            $script:removals = 0
            function Test-Path {
                param([string]$LiteralPath)
                $script:pathChecks++
                if ($pathCheckFails) { throw [UnauthorizedAccessException]::new('synthetic cleanup path failure') }
                return $true
            }
            function Remove-GeneratedTree {
                param([string]$Path, [switch]$Confirm)
                $script:removals++
                throw [UnauthorizedAccessException]::new('synthetic cleanup removal failure')
            }
            $originalError = [Management.Automation.ErrorRecord]::new(
                [IO.IOException]::new('original runtime move failure'), 'RuntimeMoveFailure',
                [Management.Automation.ErrorCategory]::WriteError, 'synthetic-runtime-stage')
            $moved = $false
            $stagingPath = 'synthetic-runtime-stage'
            $WarningPreference = 'Stop'
            $probe = [scriptblock]::Create('try { throw $originalError } finally ' + $buildFinalizers[0].Finally.Extent.Text)
            $caught = $null
            try { & $probe 3>$null }
            catch { $caught = $_ }
            if ($null -eq $caught) { throw 'The original build error must propagate.' }
            [pscustomobject]@{
                sameException = [object]::ReferenceEquals($originalError.Exception, $caught.Exception)
                errorId = $caught.FullyQualifiedErrorId
                target = $caught.TargetObject
                pathChecks = $script:pathChecks
                removals = $script:removals
            } | ConvertTo-Json -Compress
            """);

        Assert.IsTrue(result.RootElement.GetProperty("sameException").GetBoolean());
        Assert.AreEqual("RuntimeMoveFailure", result.RootElement.GetProperty("errorId").GetString());
        Assert.AreEqual("synthetic-runtime-stage", result.RootElement.GetProperty("target").GetString());
        Assert.AreEqual(1, result.RootElement.GetProperty("pathChecks").GetInt32());
        Assert.AreEqual(pathCheckFails ? 0 : 1, result.RootElement.GetProperty("removals").GetInt32());
    }

    [TestMethod]
    [DataRow("build-runtime.ps1")]
    [DataRow("verify-runtime.ps1")]
    public void RuntimeArtifactDigest_PreservesLegacyEnUsAcrossChildCultures(string scriptName)
    {
        string[] properties = scriptName == "build-runtime.ps1" ? ["path", "fileName"] : ["path"];
        string pathArgument = scriptName == "build-runtime.ps1" ? "-PathProperty $property" : string.Empty;
        // Both the fixed en-US oracle and the real function run in the same pwsh/.NET
        // globalization implementation. Only the disposable child changes CurrentCulture.
        using JsonDocument result = RunRuntimeProbe(scriptName, $$"""
            $results = foreach ($property in @({{string.Join(", ", properties.Select(static value => "'" + value + "'"))}})) {
                $names = if ($property -ceq 'fileName') {
                    @('IMAGE-1.0-py3-none-any.whl', 'identity-1.0-py3-none-any.whl',
                        'zeta-1.0-py3-none-any.whl', 'alpha_tools-1.0-py3-none-any.whl')
                } else {
                    @('LICENSE.txt', 'Lib/site-packages/pkg/__init__.py', 'Lib/site-packages/pkg/LICENSE',
                        'python312._pth', 'python.exe', '_decimal.pyd')
                }
                $records = @(for ($index = 0; $index -lt $names.Count; $index++) {
                    [pscustomobject]@{
                        $property = $names[$index]
                        byteSize = [long]2147483648 + $index
                        sha256 = ([string]($index + 1)) * 64
                    }
                })
                $ordered = @($records | Sort-Object -Property $property -CaseSensitive -Culture en-US)
                $builder = [Text.StringBuilder]::new()
                foreach ($record in $ordered) {
                    [void]$builder.Append([string]$record.$property)
                    [void]$builder.Append([char]0)
                    [void]$builder.Append(([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture))
                    [void]$builder.Append([char]0)
                    [void]$builder.Append([string]$record.sha256)
                    [void]$builder.Append("`n")
                }
                $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
                $cultures = @(foreach ($cultureName in @('en-US', 'ja-JP', 'tr-TR', '')) {
                    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($cultureName)
                    $actual = Get-ArtifactSetSha256 -Records $records {{pathArgument}}
                    [pscustomobject]@{
                        culture = $cultureName; cultureAfter = [Globalization.CultureInfo]::CurrentCulture.Name
                        actual = $actual
                    }
                })
                [pscustomobject]@{
                    property = $property; expected = $expected
                    legacyOrder = @($ordered | ForEach-Object { $_.$property }); cultures = $cultures
                }
            }
            ConvertTo-Json -InputObject @($results) -Depth 6 -Compress
            """);

        JsonElement[] sets = result.RootElement.EnumerateArray().ToArray();
        CollectionAssert.AreEqual(properties, sets.Select(static set => set.GetProperty("property").GetString()).ToArray());
        foreach (JsonElement set in sets)
        {
            string expected = set.GetProperty("expected").GetString()!;
            AssertCanonicalDigest(expected);
            string[] order = set.GetProperty("legacyOrder").EnumerateArray().Select(static value => value.GetString()!).ToArray();
            Assert.IsFalse(order.SequenceEqual(order.Order(StringComparer.Ordinal)),
                "The fixture must distinguish LEGACY en-US order from an incompatible Ordinal migration.");
            JsonElement[] cultures = set.GetProperty("cultures").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "en-US", "ja-JP", "tr-TR", "" },
                cultures.Select(static culture => culture.GetProperty("culture").GetString()).ToArray());
            foreach (JsonElement culture in cultures)
            {
                string context = scriptName + "/" + set.GetProperty("property").GetString() + "/" + culture.GetProperty("culture").GetString();
                Assert.AreEqual(expected, culture.GetProperty("actual").GetString(), context);
                Assert.AreEqual(culture.GetProperty("culture").GetString(), culture.GetProperty("cultureAfter").GetString(),
                    "The production function must not change its caller's culture: " + context);
            }
        }
    }

    [TestMethod]
    [DataRow("build-runtime.ps1", 5)]
    [DataRow("verify-runtime.ps1", 3)]
    public void RuntimeScripts_AllLegacyManifestAndFileArraySortsPinEnUs(string scriptName, int expectedSortCount)
    {
        using JsonDocument result = RunRuntimeProbe(scriptName, """
            $sorts = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Sort-Object'
            }, $true))
            $results = @(foreach ($sort in $sorts) {
                $elements = $sort.CommandElements
                $cultures = @(for ($index = 1; $index -lt $elements.Count; $index++) {
                    $element = $elements[$index]
                    if ($element -is [Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -ieq 'Culture') {
                        if ($null -ne $element.Argument) { $element.Argument.SafeGetValue() }
                        elseif ($index + 1 -lt $elements.Count) { $elements[$index + 1].SafeGetValue() }
                    }
                })
                [pscustomobject]@{ line = $sort.Extent.StartLineNumber; cultures = $cultures }
            })
            ConvertTo-Json -InputObject $results -Depth 4 -Compress
            """);

        Assert.AreEqual(expectedSortCount, result.RootElement.GetArrayLength());
        foreach (JsonElement sort in result.RootElement.EnumerateArray())
        {
            CollectionAssert.AreEqual(new[] { "en-US" },
                sort.GetProperty("cultures").EnumerateArray().Select(static value => value.GetString()).ToArray(),
                scriptName + ":" + sort.GetProperty("line").GetInt32().ToString(CultureInfo.InvariantCulture));
        }
    }

    private static JsonDocument RunRuntimeProbe(string scriptName, string body)
    {
        // Parse the actual source; never execute either script's top-level build/verification body.
        // Import the pure digest function; finalization probes select isolated helper/finally ASTs
        // with synthetic inputs only. No runtime, wheelhouse or packaged manifest is needed.
        string path = Path.Combine(GetRepositoryRoot(), "packaging", "python", scriptName);
        string code = $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSEdition -cne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7) {
                throw 'PowerShell 7+ Core is required.'
            }
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                '{{path.Replace("'", "''", StringComparison.Ordinal)}}', [ref]$tokens, [ref]$errors)
            if (@($errors).Count -ne 0) { throw 'Runtime script AST must parse without errors.' }
            $definitions = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-ArtifactSetSha256'
            }, $true))
            if ($definitions.Count -ne 1) { throw 'Expected exactly one artifact digest function.' }
            . ([scriptblock]::Create($definitions[0].Extent.Text))
            {{body}}
            """;
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
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" })
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(code)));
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        using var process = new Process { StartInfo = start };
        Assert.IsTrue(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.IsTrue(process.WaitForExit(5000), "Runtime probe cleanup exceeded its safety bound.");
            Assert.Fail("The isolated AST/function probe exceeded 30 seconds; no runtime build or launch is allowed.");
        }
        Assert.IsTrue(Task.WaitAll([stdout, stderr], 5000), "Runtime probe output drain exceeded its safety bound.");
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        Assert.AreEqual(0, process.ExitCode, output + error);
        Assert.IsTrue(string.IsNullOrWhiteSpace(error), error);
        return JsonDocument.Parse(output);
    }

    private static string FindLatestPowerShell()
    {
        // Reuse PortableBuildTests' installed-directory/PATH version resolution, not a 5.1 fallback.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Runtime culture probes require Windows and PowerShell 7+.");
        }
        var candidates = new List<string>();
        string installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
        if (Directory.Exists(installedRoot))
        {
            candidates.AddRange(Directory.GetDirectories(installedRoot).Select(static path => Path.Combine(path, "pwsh.exe")));
        }
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static path => path.Trim('"')).Where(Path.IsPathFullyQualified).Select(static path => Path.Combine(path, "pwsh.exe")));
        string? executable = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .Select(static path => (Path: path, Info: FileVersionInfo.GetVersionInfo(path)))
            .Where(static entry => entry.Info.FileMajorPart >= 7)
            .OrderByDescending(static entry => new Version(entry.Info.FileMajorPart, entry.Info.FileMinorPart, entry.Info.FileBuildPart, entry.Info.FilePrivatePart))
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal).Select(static entry => entry.Path).FirstOrDefault();
        if (executable is null) { Assert.Inconclusive("Install PowerShell 7+; never fall back to Windows PowerShell 5.1."); }
        return executable!;
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