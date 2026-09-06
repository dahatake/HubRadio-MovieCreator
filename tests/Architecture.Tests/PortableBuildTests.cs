using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;

namespace HubRadio.Architecture.Tests;

// OSL-012: syntax/metadata contracts and rejection-only process tests. Tiny
// self-inventoried fixtures are NOT OSL-009 acceptance evidence. No fake tool
// claims publish/SBOM success; the real multi-GB build is a separate main-run gate.
[TestClass]
public sealed class PortableBuildTests
{
    private const string ScriptPath = "packaging/portable/build-portable.ps1";
    private static readonly string[] ParameterNames = ["DotnetPath", "SbomToolPath", "PayloadPath", "Version", "OutputPath"];
    private static readonly string[] BundleSourceFunctions =
        ["Get-LocalPath", "Test-Within", "Assert-SafePath", "Get-RelativeName", "Assert-DistributableBundleInput", "Read-BundleSources"];
    private static readonly string[] BuildToolFunctions =
        ["Get-LocalPath", "Assert-SafePath", "Get-BuildToolTimeoutMilliseconds", "Invoke-BuildTool"];
    private static readonly string[] PrivateEvidenceFunctions =
        ["Get-LocalPath", "Assert-SafePath", "Move-PrivateEvidence"];
    private static readonly string[] SbomValidationFunctions =
        ["Get-LocalPath", "Assert-SafePath", "Get-FileEvidence", "Assert-FileEvidence", "Read-Json", "Get-PublicSbomValidation", "Assert-PublicMetadata"];

    [TestMethod]
    public void AstDeclaresFiveMandatoryParametersAndKeepsPublishAndSbomTokenBoundaries()
    {
        // Parse the real script, evaluating ONLY its three argument-array expressions
        // with synthetic strings. Never dot-source or execute the builder/prepare script.
        string code = $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                '{{Path.Combine(GetRepositoryRoot(), ScriptPath).Replace("'", "''", StringComparison.Ordinal)}}', [ref]$tokens, [ref]$errors)
            if (@($errors).Count -ne 0) { throw ($errors | Out-String) }
            $parameters = @($ast.ParamBlock.Parameters | ForEach-Object {
                $parameter = $_
                $attributes = @($parameter.Attributes | Where-Object { $_.TypeName.FullName -eq 'Parameter' })
                $mandatory = @($attributes | ForEach-Object { $_.NamedArguments } | Where-Object {
                    $_.ArgumentName -eq 'Mandatory' -and ($_.ExpressionOmitted -or $_.Argument.SafeGetValue() -eq $true)
                })
                [pscustomobject]@{ name = $parameter.Name.VariablePath.UserPath; type = $parameter.StaticType.FullName
                    mandatory = ($mandatory.Count -eq 1); hasDefault = ($null -ne $parameter.DefaultValue) }
            })
            $appProjectPath = 'C:\source 日本語\src\HubRadio.App\HubRadio.App.csproj'
            $PayloadPath = 'C:\prepared payload 日本語'
            $stage = 'C:\candidate 日本語.partial'
            $appRoot = Join-Path $stage 'App'
            $contentsRoot = Join-Path $stage 'Contents'
            $sbomRoot = Join-Path $stage 'Sbom'
            $componentRoot = Join-Path $stage 'ComponentInputs'
            $bundleManifestPath = Join-Path $stage 'BuildLogs\files-to-bundle.tsv'
            $privateValidationPath = Join-Path $stage 'BuildLogs\sbom-validation.private.json'
            $Version = '1.2.3-rc.1+local'
            function Read-Arguments([string]$Name) {
                $assignments = @($ast.FindAll({ param($node)
                    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -ceq $Name
                }, $true))
                if ($assignments.Count -ne 1) { throw "Expected one argument array: $Name" }
                & ([scriptblock]::Create($assignments[0].Right.Extent.Text))
            }
            $argumentListCalls = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
                $node.Expression.Extent.Text -ceq '$start.ArgumentList' -and $node.Member.Value -ceq 'Add'
            }, $true))
            [pscustomobject]@{ parameters = $parameters; usesArgumentList = ($argumentListCalls.Count -eq 1)
                publish = @(Read-Arguments 'publishArguments'); generate = @(Read-Arguments 'generateArguments')
                validate = @(Read-Arguments 'validateArguments') } | ConvertTo-Json -Depth 6 -Compress
            """;
        ProcessStartInfo start = NewPowerShellStart();
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(code)));
        (int exitCode, string stdout, string stderr) = Run(start);
        Assert.AreEqual(0, exitCode, stdout + stderr);
        using JsonDocument result = JsonDocument.Parse(stdout);
        JsonElement root = result.RootElement;
        JsonElement[] parameters = root.GetProperty("parameters").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(ParameterNames, parameters.Select(static parameter => parameter.GetProperty("name").GetString()).ToArray());
        foreach (JsonElement parameter in parameters)
        {
            Assert.AreEqual("System.String", parameter.GetProperty("type").GetString());
            Assert.IsTrue(parameter.GetProperty("mandatory").GetBoolean());
            Assert.IsFalse(parameter.GetProperty("hasDefault").GetBoolean());
        }
        Assert.IsTrue(root.GetProperty("usesArgumentList").GetBoolean(), "Do not flatten argv into a shell command string.");
        CollectionAssert.AreEqual(new[]
        {
            "publish", @"C:\source 日本語\src\HubRadio.App\HubRadio.App.csproj", "-c", "Release", "-r", "win-x64", "--no-restore",
            "-p:Platform=x64", "-p:PublishProfile=win-x64-singlefile", "-p:DebugType=None", "-p:DebugSymbols=false",
            "-p:HubRadioPortablePayloadPath=C:\\prepared payload 日本語",
            "-p:HubRadioPortableBundleManifestPath=C:\\candidate 日本語.partial\\BuildLogs\\files-to-bundle.tsv",
            "-p:Version=1.2.3-rc.1+local", "--output", @"C:\candidate 日本語.partial\App",
        }, Strings(root, "publish"));
        CollectionAssert.AreEqual(new[]
        {
            "generate", "-mi", "SPDX:2.2", "-b", @"C:\candidate 日本語.partial\Contents", "-m", @"C:\candidate 日本語.partial\Sbom",
            "-pn", "HubRadio.MovieCreator", "-pv", "1.2.3-rc.1+local", "-ps", "HubRadio-MovieCreator",
            "-nsb", "https://github.com/dahatake/HubRadio-MovieCreator", "-li", "false", "-F", "false",
            "-bc", @"C:\candidate 日本語.partial\ComponentInputs",
        }, Strings(root, "generate"));
        CollectionAssert.AreEqual(new[]
        {
            "validate", "-b", @"C:\candidate 日本語.partial\Contents", "-m", @"C:\candidate 日本語.partial\Sbom\_manifest",
            "-o", @"C:\candidate 日本語.partial\BuildLogs\sbom-validation.private.json", "-mi", "SPDX:2.2",
        }, Strings(root, "validate"));
    }

    [TestMethod]
    public void PublicSbomValidationSummarizesActualDiagnosticShapeWithoutPrivatePaths()
    {
        using var fixture = new RejectionInputs();
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(fixture.InputRoot, "BuildLogs")).FullName, "sbom-validation.private.json");
        string[] canaries = ["private-build-drop-canary", "private-manifest-canary", "private-output-canary"];
        // Actual sbom-tool 4.1.5 diagnostic shape, with synthetic private paths.
        // This is a projection test, not evidence of a successful real tool run.
        byte[] original = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Result = "Success",
            ValidationErrors = new { Count = 0, Errors = Array.Empty<object>() },
            Summary = new
            {
                TotalExecutionTimeInSeconds = 0.125,
                ValidationTelemetery = new Dictionary<string, object>(),
                Parameters = new
                {
                    BuildDropPath = new { Value = @"C:\" + canaries[0] + @"\Contents" },
                    ManifestDirPath = new { Value = @"D:\" + canaries[1] + @"\Sbom\_manifest" },
                    OutputPath = new { Value = @"E:\" + canaries[2] + @"\BuildLogs\sbom-validation.private.json" },
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true }) + "\r\n");
        File.WriteAllBytes(path, original);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        (int exitCode, string stdout, string stderr) = RunFunctions(SbomValidationFunctions, $$"""
            $path = {{PowerShellLiteral(path)}}
            $rawReason = ''
            try { Assert-PublicMetadata (Read-Json $path) }
            catch { $rawReason = $_.Exception.Message }
            $summary = Get-PublicSbomValidation $path
            Assert-PublicMetadata $summary
            [pscustomobject]@{ rawReason = $rawReason; summary = $summary } | ConvertTo-Json -Depth 3 -Compress
        """);
        using JsonDocument result = ReadProbeJson((exitCode, stdout, stderr));
        StringAssert.StartsWith(result.RootElement.GetProperty("rawReason").GetString()!, "OSL012:METADATA_PATH");
        JsonElement summary = result.RootElement.GetProperty("summary");
        CollectionAssert.AreEqual(new[] { "schemaVersion", "result", "validationErrorCount", "toolVersion", "sourceSha256" },
            summary.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("1.0.0", summary.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("Success", summary.GetProperty("result").GetString());
        Assert.AreEqual(0, summary.GetProperty("validationErrorCount").GetInt32());
        Assert.AreEqual("4.1.5", summary.GetProperty("toolVersion").GetString());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), summary.GetProperty("sourceSha256").GetString());
        foreach (string canary in canaries) { Assert.IsFalse((stdout + stderr).Contains(canary, StringComparison.Ordinal)); }
        Assert.IsTrue(string.IsNullOrWhiteSpace(stderr));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void PublicSbomValidationRejectsUnsuccessfulMalformedAndUnknownResultsWithoutPrivateDiagnostics()
    {
        using var fixture = new RejectionInputs();
        string path = Path.Combine(fixture.InputRoot, "sbom-validation.private.json");
        // One table-driven test: each rejected value must emit no partial summary.
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["failure"] = """{"Result":"Failure","ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["unknown-result"] = """{"Result":"Unknown","ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["wrong-case"] = """{"Result":"success","ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["padded-result"] = """{"Result":"Success ","ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["missing-result"] = """{"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["null-result"] = """{"Result":null,"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["boolean-result"] = """{"Result":true,"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["number-result"] = """{"Result":0,"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["array-result"] = """{"Result":["Success"],"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["object-result"] = """{"Result":{"Success":true},"ValidationErrors":{"Count":0,"Errors":[]}}""",
            ["missing-errors"] = """{"Result":"Success"}""",
            ["null-errors"] = """{"Result":"Success","ValidationErrors":null}""",
            ["array-errors"] = """{"Result":"Success","ValidationErrors":[]}""",
            ["string-errors"] = """{"Result":"Success","ValidationErrors":""}""",
            ["boolean-errors"] = """{"Result":"Success","ValidationErrors":false}""",
            ["number-errors"] = """{"Result":"Success","ValidationErrors":0}""",
            ["nonempty-errors"] = """{"Result":"Success","ValidationErrors":{"private-diagnostic-canary":"C:\\private-error-canary\\secret.txt"}}""",
            ["empty-errors-object"] = """{"Result":"Success","ValidationErrors":{}}""",
            ["nonzero-count"] = """{"Result":"Success","ValidationErrors":{"Count":1,"Errors":[]}}""",
            ["hidden-error"] = """{"Result":"Success","ValidationErrors":{"Count":0,"Errors":["private-diagnostic-canary"]}}""",
            ["string-count"] = """{"Result":"Success","ValidationErrors":{"Count":"0","Errors":[]}}""",
            ["boolean-count"] = """{"Result":"Success","ValidationErrors":{"Count":false,"Errors":[]}}""",
            ["object-error-list"] = """{"Result":"Success","ValidationErrors":{"Count":0,"Errors":{}}}""",
            ["extra-error-field"] = """{"Result":"Success","ValidationErrors":{"Count":0,"Errors":[],"Other":0}}""",
            ["count-key"] = """{"Result":"Success","ValidationErrors":{"Count":0}}""",
            ["base-count-key"] = """{"Result":"Success","ValidationErrors":{"PSBase":{"Count":0}}}""",
            ["enumerator-key"] = """{"Result":"Success","ValidationErrors":{"GetEnumerator":null}}""",
            ["array-root"] = """[{"Result":"Success","ValidationErrors":{"Count":0,"Errors":[]}}]""",
            ["empty-array-root"] = "[]",
            ["scalar-root"] = "\"Success\"",
            ["null-root"] = "null",
            ["empty-object"] = "{}",
            ["empty-json"] = "",
            ["invalid-json"] = """{"Result":"Success","ValidationErrors":{},"private-diagnostic-canary":C:\private-parser-canary}""",
        };
        (int exitCode, string stdout, string stderr) = RunFunctions(SbomValidationFunctions, $$"""
            $path = {{PowerShellLiteral(path)}}
            $cases = {{JsonExpression(cases)}}
            $results = foreach ($name in $cases.Keys) {
                [IO.File]::WriteAllText($path, $cases[$name], [Text.UTF8Encoding]::new($false, $true))
                $emitted = [Collections.Generic.List[object]]::new()
                $reason = ''
                try { Get-PublicSbomValidation $path | ForEach-Object { $emitted.Add($_) } }
                catch { $reason = $_.Exception.Message }
                [pscustomobject]@{ name = $name; emitted = $emitted.Count; reason = $reason
                    unchanged = ([IO.File]::ReadAllText($path) -ceq $cases[$name]) }
            }
            ConvertTo-Json -InputObject @($results) -Compress
            """);
        using JsonDocument result = ReadProbeJson((exitCode, stdout, stderr));
        CollectionAssert.AreEquivalent(cases.Keys.ToArray(), result.RootElement.EnumerateArray()
            .Select(static item => item.GetProperty("name").GetString()!).ToArray());
        foreach (JsonElement item in result.RootElement.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            Assert.AreEqual(0, item.GetProperty("emitted").GetInt32(), name);
            Assert.AreEqual(name == "invalid-json"
                ? "OSL012:SBOM_VALIDATION A readable, unchanged JSON validation result is required; inspect private build logs."
                : "OSL012:SBOM_VALIDATION Expected Result Success, integer Count zero and an empty Errors array; inspect private build logs.",
                item.GetProperty("reason").GetString(), name);
            Assert.IsTrue(item.GetProperty("unchanged").GetBoolean(), name);
        }
        Assert.IsFalse((stdout + stderr).Contains("canary", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrWhiteSpace(stderr));
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void SbomValidationMinimalSuccessKeepsRealToolGateAndPublicSummaryHashAndMetadataChecks()
    {
        using var fixture = new RejectionInputs();
        string path = Path.Combine(fixture.InputRoot, "sbom-validation.private.json");
        byte[] original = Encoding.UTF8.GetBytes("""{"Result":"Success","ValidationErrors":{"Count":0,"Errors":[]}}""");
        File.WriteAllBytes(path, original);
        using JsonDocument result = ReadProbeJson(RunFunctions(SbomValidationFunctions, $$"""
            $summary = Get-PublicSbomValidation {{PowerShellLiteral(path)}}
            Assert-PublicMetadata $summary
            $gates = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-BuildTool' -and
                $node.CommandElements[1].Extent.Text -ceq '$SbomToolPath' -and
                $node.CommandElements[2].Extent.Text -ceq '$validateArguments' -and
                $node.CommandElements[3].Extent.Text -ceq "'sbom-validate'"
            }, $true))
            $projections = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Get-PublicSbomValidation'
            }, $true))
            $checks = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Assert-PublicMetadata' -and
                $node.CommandElements[1].Extent.Text -cin @('$sbom', '$validation', '$release')
            }, $true))
            $summaryChecks = @($checks | Where-Object { $_.CommandElements[1].Extent.Text -ceq '$validation' })
            $creates = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Expression.Extent.Text -ceq '[IO.File]' -and
                $node.Member.Value -ceq 'Open' -and $node.Arguments[0].Extent.Text -ceq '$validationPath' -and
                $node.Arguments[1].Extent.Text -ceq '[IO.FileMode]::CreateNew'
            }, $true))
            $serializers = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'ConvertTo-Json' -and
                $node.Parent -is [Management.Automation.Language.PipelineAst] -and $node.Parent.PipelineElements[0].Extent.Text -ceq '$validation'
            }, $true))
            $hashes = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Get-FileEvidence' -and
                $node.CommandElements[1].Extent.Text -ceq '$validationPath'
            }, $true))
            $paths = [ordered]@{}
            foreach ($assignment in @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -cin @('privateValidationPath', 'validationPath')
            }, $true))) {
                $paths.Add($assignment.Left.VariablePath.UserPath, $assignment.Right.Extent.Text)
            }
            [pscustomobject]@{
                summary = $summary; paths = $paths
                gatedProjection = ($gates.Count -eq 1 -and $projections.Count -eq 1 -and
                    $gates[0].Extent.StartOffset -lt $projections[0].Extent.StartOffset)
                projectionInputs = @($projections | ForEach-Object { $_.CommandElements[1].Extent.Text })
                publicChecks = @($checks | ForEach-Object { $_.CommandElements[1].Extent.Text })
                checkedBeforeWrite = ($projections.Count -eq 1 -and $summaryChecks.Count -eq 1 -and $creates.Count -eq 1 -and
                    $projections[0].Extent.StartOffset -lt $summaryChecks[0].Extent.StartOffset -and
                    $summaryChecks[0].Extent.StartOffset -lt $creates[0].Extent.StartOffset)
                    summarySerializers = $serializers.Count; publicHashReads = $hashes.Count
                } | ConvertTo-Json -Depth 3 -Compress
            """));
        JsonElement root = result.RootElement;
        JsonElement summary = root.GetProperty("summary");
        Assert.AreEqual("Success", summary.GetProperty("result").GetString());
        Assert.AreEqual(0, summary.GetProperty("validationErrorCount").GetInt32());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), summary.GetProperty("sourceSha256").GetString());
        Assert.IsTrue(root.GetProperty("gatedProjection").GetBoolean(), "Only project diagnostics after the real validator's checked exit.");
        CollectionAssert.AreEqual(new[] { "$privateValidationPath" }, Strings(root, "projectionInputs"));
        CollectionAssert.AreEquivalent(new[] { "$sbom", "$validation", "$release" }, Strings(root, "publicChecks"));
        Assert.IsTrue(root.GetProperty("checkedBeforeWrite").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("summarySerializers").GetInt32());
        Assert.AreEqual(1, root.GetProperty("publicHashReads").GetInt32());
        Assert.AreEqual("Join-Path $logRoot 'sbom-validation.private.json'", root.GetProperty("paths").GetProperty("privateValidationPath").GetString());
        Assert.AreEqual("Join-Path $stageRoot 'Evidence/sbom-validation.json'", root.GetProperty("paths").GetProperty("validationPath").GetString());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void UnsafeAndNonsemanticVersionTokensAreRejectedBeforeTools()
    {
        foreach (string version in new[] { "1.2.3;Injected=true", "01.2.3", "1.2.3-01", "1.2.3\n" })
        {
            using var fixture = new RejectionInputs();
            fixture.Arguments["Version"] = version;
            AssertRejected(fixture, "OSL012:VERSION");
        }
    }

    [TestMethod]
    public void NonlocalTraversalAndMsBuildDelimiterPathsAreRejected()
    {
        foreach ((string path, string reason) in new[]
        {
            ("relative-payload", "OSL012:INPUT_PATH:PayloadPath"),
            (@"\\server\share\payload", "OSL012:INPUT_PATH:PayloadPath"),
            (@"C:\input\..\payload", "OSL012:INPUT_PATH:PayloadPath"),
            (@"C:\payload;Injected=true", "OSL012:MSBUILD_PATH:PayloadPath"),
        })
        {
            using var fixture = new RejectionInputs();
            fixture.Arguments["PayloadPath"] = path;
            AssertRejected(fixture, reason);
        }
    }

    [TestMethod]
    public void OutputMustRemainBelowPortableAndSeparateFromPayloadAndTools()
    {
        foreach (string scenario in new[] { "outside", "payload-ancestor", "payload-descendant", "dotnet", "sbom", "private" })
        {
            using var fixture = new RejectionInputs();
            string output = fixture.Arguments["OutputPath"];
            switch (scenario)
            {
                case "outside": fixture.Arguments["OutputPath"] = Path.Combine(fixture.InputRoot, "outside"); break;
                case "payload-ancestor": fixture.Arguments["PayloadPath"] = fixture.OutputParent; break;
                case "payload-descendant": fixture.Arguments["PayloadPath"] = Path.Combine(output, "payload"); break;
                case "dotnet": fixture.Arguments["DotnetPath"] = Path.Combine(output, "dotnet.exe"); break;
                case "private": Directory.CreateDirectory(output + ".private"); break;
                default: fixture.Arguments["SbomToolPath"] = Path.Combine(fixture.OutputParent, "sbom-tool.exe"); break;
            }
            AssertRejected(fixture, scenario switch
            {
                "outside" => "OSL012:OUTPUT_SCOPE",
                "private" => "OSL012:PRIVATE_EXISTS",
                _ => "OSL012:OVERLAP",
            });
        }
    }

    [TestMethod]
    public void ExistingOutputFileOrDirectoryIsPreservedBeforeAnySubprocess()
    {
        foreach (bool directory in new[] { false, true })
        {
            using var fixture = new RejectionInputs();
            string output = fixture.Arguments["OutputPath"];
            Directory.CreateDirectory(fixture.OutputParent);
            string sentinel = directory ? Path.Combine(Directory.CreateDirectory(output).FullName, "keep.txt") : output;
            File.WriteAllText(sentinel, "pre-existing output");
            File.SetAttributes(sentinel, File.GetAttributes(sentinel) | FileAttributes.ReadOnly);
            AssertRejected(fixture, "OSL012:OUTPUT_EXISTS");
            Assert.AreEqual("pre-existing output", File.ReadAllText(sentinel));
            Assert.IsTrue(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly));
        }
    }

    [TestMethod]
    public void MissingPreparedManifestIsRejectedWithoutStaging()
    {
        using var fixture = new RejectionInputs();
        File.Delete(fixture.ManifestPath);
        AssertRejected(fixture, "OSL012:MISSING");
    }

    [TestMethod]
    public void ModifiedPreparedContractAndStringApprovalFlagsAreRejected()
    {
        foreach ((string key, JsonNode value) in new (string, JsonNode)[]
        {
            ("schemaVersion", JsonValue.Create("2.0.0")), ("taskId", JsonValue.Create("OSL-012")),
            ("architecture", JsonValue.Create("arm64")), ("releaseApproved", JsonValue.Create(true)),
            ("releaseApproved", JsonValue.Create("false")), ("appPublishRequired", JsonValue.Create(false)),
        })
        {
            using var fixture = new RejectionInputs();
            fixture.ChangeManifest(manifest => manifest[key] = value);
            AssertRejected(fixture, "OSL012:MANIFEST");
        }
    }

    [TestMethod]
    public void SameSizePayloadModificationIsRejectedByHash()
    {
        using var fixture = new RejectionInputs();
        string path = Path.Combine(fixture.PayloadRoot, "Worker/data.bin");
        long size = new FileInfo(path).Length;
        File.WriteAllText(path, "modified payload", new UTF8Encoding(false));
        Assert.AreEqual(size, new FileInfo(path).Length);
        AssertRejected(fixture, "OSL012:PAYLOAD_FILES");
    }

    [TestMethod]
    public void UnlistedPayloadFileIsRejectedEvenWithAnUnchangedManifest()
    {
        using var fixture = new RejectionInputs();
        File.WriteAllText(Path.Combine(fixture.PayloadRoot, "Tools/untracked.bin"), "not inventoried");
        AssertRejected(fixture, "OSL012:PAYLOAD_FILES");
    }

    [TestMethod]
    public void ManifestTraversalCaseCollisionAndNonordinalOrderingAreRejected()
    {
        foreach (string scenario in new[] { "traversal", "case-collision", "order" })
        {
            using var fixture = new RejectionInputs();
            fixture.ChangeManifest(manifest =>
            {
                JsonArray files = manifest["files"]!.AsArray();
                if (scenario == "traversal") { files[0]!["path"] = "Evidence/../../escape.bin"; }
                else if (scenario == "case-collision")
                {
                    files[1] = files[0]!.DeepClone();
                    files[1]!["path"] = files[0]!["path"]!.GetValue<string>().ToUpperInvariant();
                }
                else
                {
                    JsonNode first = files[0]!.DeepClone();
                    files[0] = files[1]!.DeepClone();
                    files[1] = first;
                }
            });
            AssertRejected(fixture, "OSL012:MANIFEST_FILES");
        }
    }

    [TestMethod]
    public void RehashedComponentEvidenceMustStillMatchRepositoryOriginal()
    {
        using var fixture = new RejectionInputs();
        File.AppendAllText(Path.Combine(fixture.PayloadRoot, "Evidence/web-dependency-manifest.json"), " ");
        fixture.WriteManifest(); // Internally consistent hashes do not establish repository equivalence.
        AssertRejected(fixture, "OSL012:EVIDENCE_ORIGINAL");
    }

    [TestMethod]
    public void RehashedRuntimeModelCatalogMustMatchOriginalNotJustEvidenceCopy()
    {
        using var fixture = new RejectionInputs();
        File.AppendAllText(Path.Combine(fixture.PayloadRoot, "ModelCatalog/catalog.json"), " ");
        fixture.WriteManifest();
        AssertRejected(fixture, "OSL012:EVIDENCE_ORIGINAL");
    }

    [TestMethod]
    public void FixedSdkSingleFileHostAndCoreLibSelectIntegratedClrAndBundleV6WithoutLooseCoreClr()
    {
        Dictionary<string, string> sources = CreateBundleMetadata();
        Assert.IsFalse(sources.ContainsKey("coreclr.dll"));
        using JsonDocument result = ReadProbeJson(RunFunctions(["Get-BundleContract"], BundleMetadataScript(sources) + """

            Get-BundleContract $sources '10.0.400' | ConvertTo-Json -Compress
        """));
        Assert.AreEqual("integrated-clr", result.RootElement.GetProperty("runtimeKind").GetString());
        Assert.AreEqual("10.0.11", result.RootElement.GetProperty("runtimeVersion").GetString());
        Assert.AreEqual(6, result.RootElement.GetProperty("bundleMajorVersion").GetInt32());
        Assert.AreEqual(0, result.RootElement.GetProperty("bundleMinorVersion").GetInt32());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualBundleTsvCasingPreservesOrdinalNamesSourcesAndEvidence(bool lowercaseAllDestinations)
    {
        using var fixture = new RejectionInputs();
        Dictionary<string, string> metadata = CreateBundleMetadata();
        // Reproduce the actual candidate TSV spelling, not the former uppercase-UI fixture.
        string xamlSource = metadata["Microsoft.UI.Xaml.dll"].Replace("Microsoft.UI.Xaml.dll", "Microsoft.ui.xaml.dll", StringComparison.Ordinal);
        metadata.Remove("Microsoft.UI.Xaml.dll");
        metadata.Add("Microsoft.ui.xaml.dll", xamlSource);
        Dictionary<string, string> sources = metadata.ToDictionary(
            pair => lowercaseAllDestinations ? pair.Key.ToLowerInvariant() : pair.Key,
            static pair => pair.Value.Replace('/', '\\'), StringComparer.Ordinal);
        string report = Path.Combine(fixture.InputRoot, "bundle.tsv");
        File.WriteAllLines(report, sources.Reverse().Select(static pair => pair.Value + "\t" + pair.Key));
        byte[] originalReport = File.ReadAllBytes(report);
        string[] names = sources.Keys.Order(StringComparer.Ordinal).ToArray();
        // Empty-file metadata only; this does not create/copy executable bundle inputs.
        string emptySha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        string digestInput = string.Concat(names.Select(name => name + '\0' + "0" + '\0' + emptySha256 + '\n'));
        string expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant();
        using JsonDocument result = ReadProbeJson(RunFunctions(
            [.. BundleSourceFunctions, "Get-BundleContract", "Get-ArtifactSetSha256"], $$"""
            $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
            $sourceRoots = @('C:\metadata-only', (Join-Path $repoRoot 'src'))
            $stageRoot = {{PowerShellLiteral(Path.Combine(fixture.OutputParent, "candidate.partial"))}}
            $sources = Read-BundleSources {{PowerShellLiteral(report)}}
            $contract = Get-BundleContract $sources '10.0.400'
            $records = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
            foreach ($name in $sources.Keys) {
                $records.Add($name, [pscustomobject]@{ path = $name; byteSize = 0L; sha256 = '{{emptySha256}}' })
            }
            [pscustomobject]@{
                ordinal = [object]::ReferenceEquals($sources.Comparer, [StringComparer]::Ordinal)
                names = @($sources.Keys); sourcePaths = @($sources.Values); contract = $contract
                artifactSetSha256 = Get-ArtifactSetSha256 $records
            } | ConvertTo-Json -Depth 3 -Compress
        """));
        JsonElement root = result.RootElement;
        Assert.IsTrue(root.GetProperty("ordinal").GetBoolean());
        CollectionAssert.AreEqual(names, Strings(root, "names"));
        CollectionAssert.AreEqual(names.Select(name => sources[name]).ToArray(), Strings(root, "sourcePaths"));
        Assert.AreEqual(expectedDigest, root.GetProperty("artifactSetSha256").GetString());
        Assert.AreEqual("integrated-clr", root.GetProperty("contract").GetProperty("runtimeKind").GetString());
        Assert.AreEqual("10.0.11", root.GetProperty("contract").GetProperty("runtimeVersion").GetString());
        CollectionAssert.AreEqual(originalReport, File.ReadAllBytes(report));
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void ActualWinUiBundleDestinationCaseCollisionIsStillRejectedByTheReader()
    {
        using var fixture = new RejectionInputs();
        string source = Path.Combine(fixture.PayloadRoot, "Worker/data.bin");
        string report = Path.Combine(fixture.InputRoot, "bundle.tsv");
        File.WriteAllLines(report, [source + "\tMicrosoft.ui.xaml.dll", source + "\tMicrosoft.UI.Xaml.dll"]);
        using JsonDocument result = ReadProbeJson(RunFunctions(BundleSourceFunctions, $$"""
            $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
            $sourceRoots = @({{PowerShellLiteral(fixture.InputRoot)}})
            $stageRoot = {{PowerShellLiteral(Path.Combine(fixture.OutputParent, "candidate.partial"))}}
            $reason = ''
            try { Read-BundleSources {{PowerShellLiteral(report)}} | Out-Null }
            catch { $reason = $_.Exception.Message }
            ConvertTo-Json -InputObject $reason -Compress
            """));
        Assert.AreEqual("OSL012:BUNDLE_DUPLICATE Duplicate normalized bundle destination.", result.RootElement.GetString());
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void EveryRequiredRuntimeWinUiAndAppInputIsStillRequired()
    {
        Dictionary<string, string> sources = CreateBundleMetadata();
        using JsonDocument result = ReadProbeJson(RunFunctions(["Get-BundleContract"], BundleMetadataScript(sources) + """

            $results = foreach ($name in @($sources.Keys)) {
                $original = $sources[$name]
                [void]$sources.Remove($name)
                $reason = ''
                try { Get-BundleContract $sources '10.0.400' | Out-Null }
                catch { $reason = $_.Exception.Message }
                finally { $sources[$name] = $original }
                [pscustomobject]@{ name = $name; reason = $reason }
            }
            ConvertTo-Json -InputObject @($results) -Compress
            """));
        Assert.AreEqual(17, result.RootElement.GetArrayLength());
        foreach (JsonElement item in result.RootElement.EnumerateArray())
        {
            Assert.AreEqual("OSL012:BUNDLE_REQUIRED Missing actual bundle input: " + item.GetProperty("name").GetString(),
                item.GetProperty("reason").GetString());
        }
    }

    [TestMethod]
    public void PlainAppHostWrongHostOriginAndUnknownSdkOrRuntimeCannotUseTheIntegratedClrContract()
    {
        using JsonDocument result = ReadProbeJson(RunFunctions(["Get-BundleContract"], BundleMetadataScript(CreateBundleMetadata()) + """

            $hostSource = $sources['HubRadio.App.exe']
            $coreLibSource = $sources['System.Private.CoreLib.dll']
            $results = foreach ($scenario in @('apphost', 'host-origin', 'runtime', 'sdk')) {
                $sources['HubRadio.App.exe'] = $hostSource
                $sources['System.Private.CoreLib.dll'] = $coreLibSource
                $sdk = '10.0.400'
                switch ($scenario) {
                    'apphost' { $sources['HubRadio.App.exe'] = $hostSource.Replace('singlefilehost.exe', 'apphost.exe') }
                    'host-origin' { $sources['HubRadio.App.exe'] = 'C:\unrelated\singlefilehost.exe' }
                    'runtime' { $sources['System.Private.CoreLib.dll'] = $coreLibSource.Replace('10.0.11', '11.0.0') }
                    'sdk' { $sdk = '10.0.401' }
                }
                # A loose CLR must not turn an unrecognized host into an accepted one.
                $sources['coreclr.dll'] = 'C:\metadata-only\coreclr.dll'
                $reason = ''
                try { Get-BundleContract $sources $sdk | Out-Null }
                catch { $reason = $_.Exception.Message }
                [pscustomobject]@{ scenario = $scenario; reason = $reason }
            }
            ConvertTo-Json -InputObject @($results) -Compress
            """));
        Assert.AreEqual(4, result.RootElement.GetArrayLength());
        foreach (JsonElement item in result.RootElement.EnumerateArray())
        {
            StringAssert.StartsWith(item.GetProperty("reason").GetString()!,
                item.GetProperty("scenario").GetString() == "sdk" ? "OSL012:SDK_VERSION" : "OSL012:BUNDLE_RUNTIME");
        }
    }

    [TestMethod]
    public void DeveloperBundleInputsAreRejectedBeforeCopyWithoutDisclosingTheirContentsOrPaths()
    {
        using var fixture = new RejectionInputs();
        string privateFile = Path.Combine(fixture.InputRoot, "private.PuBxMl");
        const string canary = "test-only-private-publish-profile-canary";
        File.WriteAllText(privateFile, canary);
        string benign = Path.Combine(fixture.PayloadRoot, "Worker/data.bin");
        string originalProfile = Path.Combine(GetRepositoryRoot(), "src/HubRadio.App/Properties/PublishProfiles/win-x64-singlefile.pubxml");
        List<string[]> rejected =
        [
            [privateFile, "renamed.bin"], [benign, "nested/settings.PUBXML"],
            [originalProfile, "Properties\\PublishProfiles\\win-x64-singlefile.pubxml"],
            [originalProfile, "renamed.bin"],
            [Path.ChangeExtension(originalProfile, ".bin"), "renamed.bin"],
            [benign, "properties/publishprofiles/renamed.bin"],
            [Path.Combine(GetRepositoryRoot(), "src/HubRadio.App/App.xaml.cs"), "renamed.bin"],
            [Path.Combine(GetRepositoryRoot(), "src/HubRadio.App/MainWindow.xaml"), "renamed.bin"],
            [benign, "App.xaml.cs"], [benign, "MainWindow.xaml"],
            [Path.Combine(fixture.InputRoot, ".env"), "renamed.bin"], [benign, ".env.local"],
        ];
        foreach (string extension in new[] { ".csproj", ".targets", ".props", ".PDB" })
        {
            rejected.Add([Path.Combine(fixture.InputRoot, "developer" + extension), "renamed.bin"]);
            rejected.Add([benign, "nested/developer" + extension]);
        }
        string report = Path.Combine(fixture.InputRoot, "bundle.tsv");
        string stage = Path.Combine(fixture.OutputParent, "candidate.partial");
        string code = $$"""
            $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
            $sourceRoots = @({{PowerShellLiteral(fixture.InputRoot)}}, (Join-Path $repoRoot 'src'))
            $stageRoot = {{PowerShellLiteral(stage)}}
            $report = {{PowerShellLiteral(report)}}
            $cases = {{JsonExpression(rejected)}}
            $results = foreach ($case in $cases) {
                # The harmless first row must not be copied before a later rejection.
                [IO.File]::WriteAllLines($report, [string[]]@(
                    {{PowerShellLiteral(benign + "\tWorker/data.bin")}}, ($case -join "`t")))
                $reason = ''
                try { Read-BundleSources $report | Out-Null }
                catch { $reason = $_.Exception.Message }
                $reason
            }
            ConvertTo-Json -InputObject @($results) -Compress
            """;
        (int exitCode, string stdout, string stderr) = RunFunctions(BundleSourceFunctions, code);
        Assert.IsFalse((stdout + stderr).Contains(canary, StringComparison.Ordinal));
        Assert.IsFalse((stdout + stderr).Contains(privateFile, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse((stdout + stderr).Contains(originalProfile, StringComparison.OrdinalIgnoreCase));
        using JsonDocument result = ReadProbeJson((exitCode, stdout, stderr));
        Assert.AreEqual(rejected.Count, result.RootElement.GetArrayLength());
        foreach (JsonElement reason in result.RootElement.EnumerateArray())
        {
            StringAssert.StartsWith(reason.GetString()!, "OSL012:BUNDLE_DEVELOPER");
        }
        Assert.IsFalse(Directory.Exists(fixture.OutputParent), "Metadata rejection must not create Contents or staging.");
        Assert.AreEqual(canary, File.ReadAllText(privateFile));
    }

    [TestMethod]
    public void BundlePreflightPreservesVendorPythonHeadersAndRequiredRuntimeAssets()
    {
        using var fixture = new RejectionInputs();
        string[] destinations = ["Worker/Lib/site-packages/vendor/module.py", "Worker/include/vendor.h",
            "HubRadio.App.pri", "MainWindow.xbf", "Microsoft.UI.Xaml.dll", "WebAssets/index.html", "Profiles/default.json"];
        string report = Path.Combine(fixture.InputRoot, "bundle.tsv");
        foreach (string destination in destinations)
        {
            string source = Path.Combine(fixture.InputRoot, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "metadata fixture only");
        }
        File.WriteAllLines(report, destinations.Select(destination => Path.Combine(fixture.InputRoot, destination) + "\t" + destination));
        using JsonDocument result = ReadProbeJson(RunFunctions(BundleSourceFunctions, $$"""
            $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
            $sourceRoots = @({{PowerShellLiteral(fixture.InputRoot)}})
            $stageRoot = {{PowerShellLiteral(Path.Combine(fixture.OutputParent, "candidate.partial"))}}
            $sources = Read-BundleSources {{PowerShellLiteral(report)}}
            ConvertTo-Json -InputObject @($sources.Keys) -Compress
            """));
        CollectionAssert.AreEquivalent(destinations, result.RootElement.EnumerateArray().Select(static item => item.GetString()!).ToArray());
        Assert.IsFalse(Directory.Exists(fixture.OutputParent));
    }

    [TestMethod]
    public void AstWiresFullBundlePreflightBeforeContentsCopyAndBoundsProcessAndOutputWaits()
    {
        using JsonDocument result = ReadProbeJson(RunFunctions(["Invoke-BuildTool"], """
            $reads = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Read-BundleSources'
            }, $true))
            $contracts = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Get-BundleContract'
            }, $true))
            $copies = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Copy-VerifiedFile' -and
                $node.CommandElements[2].Extent.Text -ceq '(Join-Path $contentsRoot $relative)'
            }, $true))
            $tool = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-BuildTool'
            }, $true)
            $calls = @($tool.Body.FindAll({ param($node)
                $node -is [Management.Automation.Language.InvokeMemberExpressionAst]
            }, $true))
            $exitWaits = @($calls | Where-Object { $_.Member.Value -ceq 'WaitForExit' })
            $drainWaits = @($calls | Where-Object { $_.Expression.Extent.Text -ceq '$drainTask' -and $_.Member.Value -ceq 'Wait' })
            $copiesAsync = @($calls | Where-Object { $_.Member.Value -ceq 'CopyToAsync' })
            $unbounded = @($calls | Where-Object { $_.Member.Value -cin @('GetAwaiter', 'GetResult') })
            $kills = @($calls | Where-Object { $_.Member.Value -ceq 'Kill' -and $_.Arguments[0].Extent.Text -ceq '$true' })
            $overrides = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandParameterAst] -and $node.ParameterName -ceq 'TimeoutMilliseconds'
            }, $true))
            [pscustomobject]@{
                reads = $reads.Count; contracts = $contracts.Count; copies = $copies.Count
                preflightFirst = ($reads.Count -eq 1 -and $contracts.Count -eq 1 -and $copies.Count -eq 1 -and
                    $reads[0].Extent.StartOffset -lt $contracts[0].Extent.StartOffset -and
                    $contracts[0].Extent.StartOffset -lt $copies[0].Extent.StartOffset)
                exitWaitArities = @($exitWaits | ForEach-Object { @($_.Arguments).Count })
                drainWaits = @($drainWaits | ForEach-Object { $_.Arguments[0].SafeGetValue() })
                copyArities = @($copiesAsync | ForEach-Object { @($_.Arguments).Count })
                unbounded = $unbounded.Count; treeKills = $kills.Count; productionOverrides = $overrides.Count
            } | ConvertTo-Json -Compress
            """));
        JsonElement root = result.RootElement;
        Assert.IsTrue(root.GetProperty("preflightFirst").GetBoolean());
        CollectionAssert.AreEqual(new[] { 1, 1 }, root.GetProperty("exitWaitArities").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        CollectionAssert.AreEqual(new[] { 5000, 5000 }, root.GetProperty("drainWaits").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        CollectionAssert.AreEqual(new[] { 3, 3 }, root.GetProperty("copyArities").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        Assert.AreEqual(0, root.GetProperty("unbounded").GetInt32());
        Assert.AreEqual(1, root.GetProperty("treeKills").GetInt32());
        Assert.AreEqual(0, root.GetProperty("productionOverrides").GetInt32());
    }

    [TestMethod]
    public void FinalCandidateMoveKeepsBuildLogsAndComponentInputsInDeterministicPrivateSibling()
    {
        using var fixture = new RejectionInputs();
        string stage = Path.Combine(fixture.OutputParent, "candidate.partial");
        string output = Path.Combine(fixture.OutputParent, "candidate-review-regression");
        Directory.CreateDirectory(Path.Combine(stage, "BuildLogs"));
        Directory.CreateDirectory(Path.Combine(stage, "ComponentInputs"));
        File.WriteAllText(Path.Combine(stage, "BuildLogs/private.log"), "private log");
        File.WriteAllText(Path.Combine(stage, "ComponentInputs/project.assets.json"), "private descriptor");
        using JsonDocument result = ReadProbeJson(RunFunctions(PrivateEvidenceFunctions, $$"""
            $stage = {{PowerShellLiteral(stage)}}
            $output = {{PowerShellLiteral(output)}}
            $private = Move-PrivateEvidence $stage $output
            [pscustomobject]@{
                private = $private
                publicEntries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object Name)
                privateEntries = @(Get-ChildItem -LiteralPath $private -Force | ForEach-Object Name)
                log = [IO.File]::ReadAllText((Join-Path $private 'BuildLogs/private.log'))
                descriptor = [IO.File]::ReadAllText((Join-Path $private 'ComponentInputs/project.assets.json'))
            } | ConvertTo-Json -Compress
            """));
        JsonElement root = result.RootElement;
        Assert.AreEqual(output + ".private", root.GetProperty("private").GetString());
        CollectionAssert.AreEquivalent(Array.Empty<string>(), Strings(root, "publicEntries"));
        CollectionAssert.AreEquivalent(new[] { "BuildLogs", "ComponentInputs" }, Strings(root, "privateEntries"));
        Assert.AreEqual("private log", root.GetProperty("log").GetString());
        Assert.AreEqual("private descriptor", root.GetProperty("descriptor").GetString());
        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public void FinalCandidateMoveRefusesToOverwriteDeterministicPrivateSibling()
    {
        using var fixture = new RejectionInputs();
        string stage = Path.Combine(fixture.OutputParent, "candidate.partial");
        string output = Path.Combine(fixture.OutputParent, "candidate-review-regression");
        Directory.CreateDirectory(Path.Combine(stage, "BuildLogs"));
        Directory.CreateDirectory(Path.Combine(stage, "ComponentInputs"));
        Directory.CreateDirectory(output + ".private");
        (int exitCode, string stdout, string stderr) = RunFunctions(PrivateEvidenceFunctions, $$"""
                try { Move-PrivateEvidence {{PowerShellLiteral(stage)}} {{PowerShellLiteral(output)}} | Out-Null; exit 0 }
                catch { [Console]::Error.WriteLine($_.Exception.Message); exit 42 }
            """);
        Assert.AreEqual(42, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, "OSL012:PRIVATE_EXISTS");
        Assert.IsTrue(Directory.Exists(Path.Combine(stage, "BuildLogs")));
        Assert.IsTrue(Directory.Exists(Path.Combine(stage, "ComponentInputs")));
    }

    [TestMethod]
    public void NamedBuildSafetyBoundsAreFiniteAndUnknownNamesAreRejected()
    {
        using JsonDocument result = ReadProbeJson(RunFunctions(["Get-BuildToolTimeoutMilliseconds"], """
            $budgets = [ordered]@{}
            foreach ($name in @('publish', 'sbom-generate', 'sbom-validate', 'dotnet-version', 'sbom-version',
                    'git-head-before', 'git-head-after', 'git-status-before', 'git-status-after')) {
                $budgets[$name] = Get-BuildToolTimeoutMilliseconds $name
            }
            $reason = ''
            try { Get-BuildToolTimeoutMilliseconds '../private' | Out-Null }
            catch { $reason = $_.Exception.Message }
            [pscustomobject]@{ budgets = $budgets; reason = $reason } | ConvertTo-Json -Compress
            """));
        JsonProperty[] budgets = result.RootElement.GetProperty("budgets").EnumerateObject().ToArray();
        Assert.AreEqual(9, budgets.Length);
        foreach (JsonProperty budget in budgets)
        {
            Assert.AreEqual(budget.Name is "publish" or "sbom-generate" or "sbom-validate" ? 600_000 : 30_000, budget.Value.GetInt32());
        }
        Assert.AreEqual("OSL012:TOOL_NAME Unexpected build tool step.", result.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public void InvokeBuildToolTimeoutKillsTheRealPwshChildTreeAndKeepsPrivatePartialAndInputs()
    {
        using var fixture = new RejectionInputs();
        string stage = Path.Combine(fixture.OutputParent, "candidate.partial");
        string logs = Path.Combine(stage, "BuildLogs");
        Directory.CreateDirectory(Path.Combine(logs, "tmp"));
        string sentinel = Path.Combine(stage, "keep.txt");
        File.WriteAllText(sentinel, "retain partial");
        string pidPath = Path.Combine(stage, "owned-processes.json");
        Dictionary<string, byte[]> inputs = Directory.GetFiles(fixture.InputRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(static path => path, File.ReadAllBytes, StringComparer.Ordinal);
        const string hang = "[Threading.Tasks.TaskCompletionSource[bool]]::new().Task.GetAwaiter().GetResult()";
        string childCode = $$"""
            $ErrorActionPreference = 'Stop'
            $childStart = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
            $childStart.UseShellExecute = $false
            $childStart.CreateNoWindow = $true
            foreach ($arg in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', '{{EncodeCommand(hang)}}')) {
                $childStart.ArgumentList.Add($arg)
            }
            $child = [Diagnostics.Process]::Start($childStart)
            $self = [Diagnostics.Process]::GetCurrentProcess()
            $records = @($self, $child) | ForEach-Object {
                [pscustomobject]@{ id = $_.Id; startedUtcTicks = $_.StartTime.ToUniversalTime().Ticks }
            }
            [IO.File]::WriteAllText({{PowerShellLiteral(pidPath)}}, (ConvertTo-Json -InputObject @($records) -Compress))
            [Console]::Out.WriteLine('private-partial-stdout-canary')
            [Console]::Error.WriteLine('private-partial-stderr-canary')
            {{hang}}
            """;
        try
        {
            (int exitCode, string stdout, string stderr) = RunFunctions(BuildToolFunctions, ToolProbeScript(logs, childCode, "publish", 5000));
            Assert.AreEqual(42, exitCode, "Expected the helper rejection, not the outer watchdog.");
            StringAssert.StartsWith(stderr.Trim(), "OSL012:TIMEOUT publish ");
            Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), "A timed-out tool must not return output or candidate success JSON.");
            Assert.IsFalse(stderr.Contains("canary", StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains(pidPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(pidPath), "The inert tool must actually have spawned a descendant before timing out.");
            using JsonDocument recorded = JsonDocument.Parse(File.ReadAllBytes(pidPath));
            Assert.AreEqual(2, recorded.RootElement.GetArrayLength());
            foreach (JsonElement record in recorded.RootElement.EnumerateArray())
            {
                using Process? process = OpenRecordedProcess(record);
                Assert.IsTrue(process is null || process.WaitForExit(5000), "A timed-out tool/descendant was abandoned.");
            }
            Assert.AreEqual("retain partial", File.ReadAllText(sentinel));
            StringAssert.Contains(File.ReadAllText(Path.Combine(logs, "publish.stdout.log")), "private-partial-stdout-canary");
            StringAssert.Contains(File.ReadAllText(Path.Combine(logs, "publish.stderr.log")), "private-partial-stderr-canary");
            Assert.IsFalse(Directory.Exists(fixture.Arguments["OutputPath"]));
            CollectionAssert.AreEquivalent(inputs.Keys.ToArray(), Directory.GetFiles(fixture.InputRoot, "*", SearchOption.AllDirectories));
            foreach ((string path, byte[] bytes) in inputs) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        }
        finally
        {
            // Cleanup on assertion failure is limited to this GUID fixture's PID +
            // creation-time records; never enumerate/kill unrelated pwsh processes.
            if (File.Exists(pidPath))
            {
                using JsonDocument recorded = JsonDocument.Parse(File.ReadAllBytes(pidPath));
                foreach (JsonElement record in recorded.RootElement.EnumerateArray())
                {
                    using Process? process = OpenRecordedProcess(record);
                    if (process is null) { continue; }
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    Assert.IsTrue(process.WaitForExit(5000), "Owned test process cleanup exceeded its safety bound.");
                }
            }
        }
    }

    [TestMethod]
    public void InvokeBuildToolNonzeroExitDoesNotReturnPrivateOutputAsSuccess()
    {
        using var fixture = new RejectionInputs();
        string logs = Path.Combine(fixture.OutputParent, "candidate.partial", "BuildLogs");
        Directory.CreateDirectory(Path.Combine(logs, "tmp"));
        const string childCode = "[Console]::Out.WriteLine('private-output-canary'); [Console]::Error.WriteLine('private-error-canary'); exit 7";
        (int exitCode, string stdout, string stderr) = RunFunctions(BuildToolFunctions, ToolProbeScript(logs, childCode, "sbom-version", 5000));
        Assert.AreEqual(42, exitCode);
        StringAssert.Contains(stderr, "OSL012:PROCESS sbom-version failed (exit 7)");
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout));
        Assert.IsFalse(stderr.Contains("canary", StringComparison.Ordinal));
        StringAssert.Contains(File.ReadAllText(Path.Combine(logs, "sbom-version.stdout.log")), "private-output-canary");
        StringAssert.Contains(File.ReadAllText(Path.Combine(logs, "sbom-version.stderr.log")), "private-error-canary");
    }

    private static Dictionary<string, string> CreateBundleMetadata()
    {
        // Metadata only: no EXE is created or asserted runnable. This reflects the
        // fixed SDK's actual FilesToBundle selection, not a fake publish success.
        string[] required = ["HubRadio.App.exe", "HubRadio.App.dll", "HubRadio.App.pri", "HubRadio.App.deps.json",
            "HubRadio.App.runtimeconfig.json", "HubRadio.Core.dll", "HubRadio.Infrastructure.dll", "System.Private.CoreLib.dll",
            "Microsoft.WinUI.dll", "Microsoft.UI.Xaml.dll", "Microsoft.WindowsAppRuntime.dll", "WebAssets/index.html",
            "WebAssets/asset-manifest.json", "Worker/python.exe", "Tools/ffmpeg.exe", "Tools/ffprobe.exe", "WebView2Runtime/msedgewebview2.exe"];
        var sources = required.ToDictionary(static name => name, static name => Path.Combine(@"C:\metadata-only", name), StringComparer.Ordinal);
        sources["HubRadio.App.exe"] = Path.Combine(GetRepositoryRoot(),
            "src/HubRadio.App/obj/x64/Release/net10.0-windows10.0.26100.0/win-x64/singlefilehost.exe");
        sources["System.Private.CoreLib.dll"] = @"C:\metadata-only\microsoft.netcore.app.runtime.win-x64\10.0.11\runtimes\win-x64\lib\net10.0\System.Private.CoreLib.dll";
        return sources;
    }

    private static string BundleMetadataScript(Dictionary<string, string> sources) =>
        $$"""
        $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
        $metadata = {{JsonExpression(sources)}}
        $sources = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($name in $metadata.Keys) { $sources.Add($name, $metadata[$name]) }
        """;

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string EncodeCommand(string code) => Convert.ToBase64String(Encoding.Unicode.GetBytes(code));
    private static string JsonExpression<T>(T value) =>
        "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)) +
        "')) | ConvertFrom-Json -AsHashtable)";

    private static string ToolProbeScript(string logRoot, string childCode, string name, int timeoutMilliseconds) => $$"""
        $repoRoot = {{PowerShellLiteral(GetRepositoryRoot())}}
        $logRoot = {{PowerShellLiteral(logRoot)}}
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', '{{EncodeCommand(childCode)}}')
        try {
            Invoke-BuildTool ([Environment]::ProcessPath) $arguments '{{name}}' -TimeoutMilliseconds {{timeoutMilliseconds}}
            [Console]::Out.WriteLine('unexpected-success')
            exit 0
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 42
        }
        """;

    private static (int ExitCode, string Stdout, string Stderr) RunFunctions(string[] names, string body)
    {
        // Extract only real function definitions with the existing AST technique.
        // No script-body execution, publish/SBOM shims, or mocked process API.
        string code = $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                {{PowerShellLiteral(Path.Combine(GetRepositoryRoot(), ScriptPath))}}, [ref]$tokens, [ref]$errors)
            if (@($errors).Count -ne 0) { throw 'Builder AST must parse without errors.' }
            foreach ($name in @({{string.Join(", ", names.Select(PowerShellLiteral))}})) {
                $definitions = @($ast.FindAll({ param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
                }, $true))
                if ($definitions.Count -ne 1) { throw 'Expected exactly one builder function definition.' }
                . ([scriptblock]::Create($definitions[0].Extent.Text))
            }
            {{body}}
            """;
        ProcessStartInfo start = NewPowerShellStart();
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(EncodeCommand(code));
        return Run(start);
    }

    private static JsonDocument ReadProbeJson((int ExitCode, string Stdout, string Stderr) result)
    {
        Assert.AreEqual(0, result.ExitCode, result.Stdout + result.Stderr);
        return JsonDocument.Parse(result.Stdout);
    }

    private static Process? OpenRecordedProcess(JsonElement record)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(record.GetProperty("id").GetInt32());
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.GetProperty("startedUtcTicks").GetInt64()) { return process; }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        process?.Dispose();
        return null;
    }

    private static void AssertRejected(RejectionInputs fixture, string reason)
    {
        string[] inputsBefore = Entries(fixture.InputRoot);
        string[] outputsBefore = Entries(fixture.OutputParent);
        Dictionary<string, (byte[] Bytes, FileAttributes Attributes)> filesBefore =
            Directory.GetFiles(fixture.InputRoot, "*", SearchOption.AllDirectories)
                .Concat(outputsBefore.Where(File.Exists))
                .ToDictionary(static path => path, static path => (File.ReadAllBytes(path), File.GetAttributes(path)), StringComparer.Ordinal);
        bool outputParentExisted = Directory.Exists(fixture.OutputParent);
        ProcessStartInfo start = NewPowerShellStart();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(GetRepositoryRoot(), ScriptPath));
        foreach (string name in ParameterNames)
        {
            start.ArgumentList.Add("-" + name);
            start.ArgumentList.Add(fixture.Arguments[name]);
        }
        (int exitCode, string stdout, string stderr) = Run(start);
        Assert.AreNotEqual(0, exitCode, stdout + stderr);
        Assert.IsFalse(stderr.Contains("ParserError", StringComparison.OrdinalIgnoreCase), stderr);
        StringAssert.Contains(stderr, reason);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), "A rejection must not emit a candidate or launch build tools: " + stdout);
        CollectionAssert.AreEqual(inputsBefore, Entries(fixture.InputRoot));
        CollectionAssert.AreEqual(outputsBefore, Entries(fixture.OutputParent), "Rejection created output or partial staging.");
        Assert.AreEqual(outputParentExisted, Directory.Exists(fixture.OutputParent));
        foreach ((string path, (byte[] bytes, FileAttributes attributes)) in filesBefore)
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "Input/output bytes changed: " + path);
            Assert.AreEqual(attributes, File.GetAttributes(path), "Input/output attributes changed: " + path);
        }
    }

    private static ProcessStartInfo NewPowerShellStart()
    {
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
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive" }) { start.ArgumentList.Add(argument); }
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        return start;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(ProcessStartInfo start)
    {
        using var process = new Process { StartInfo = start };
        Assert.IsTrue(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.IsTrue(process.WaitForExit(5000), "Probe cleanup exceeded its safety bound.");
            Assert.Fail("The AST/rejection probe timed out; no publish, SBOM or component runtime should run.");
        }
        Assert.IsTrue(Task.WaitAll([stdout, stderr], 5000), "Probe output drain exceeded its safety bound.");
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private sealed class RejectionInputs : IDisposable
    {
        public string InputRoot { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.OSL012-" + Guid.NewGuid().ToString("N"));
        public string OutputParent { get; } = Path.Combine(GetRepositoryRoot(), "artifacts", "portable", "osl012-test-" + Guid.NewGuid().ToString("N"));
        public string PayloadRoot => Path.Combine(InputRoot, "prepared payload 日本語");
        public string ManifestPath => Path.Combine(PayloadRoot, "Evidence", "portable-payload.json");
        public Dictionary<string, string> Arguments { get; }

        public RejectionInputs()
        {
            Directory.CreateDirectory(InputRoot);
            foreach (string root in new[] { "Worker", "Tools", "ModelCatalog", "WebView2Runtime", "Licenses", "Evidence" })
            {
                Directory.CreateDirectory(Path.Combine(PayloadRoot, root));
            }
            foreach ((string destination, string original) in new[]
            {
                ("Evidence/python-runtime-manifest.json", "packaging/python/runtime-manifest.json"),
                ("Evidence/ffmpeg-manifest.json", "packaging/ffmpeg/manifest.json"),
                ("Evidence/model-catalog.json", "packaging/models/catalog.json"),
                ("Evidence/webview2-runtime-manifest.json", "packaging/webview2/runtime-manifest.json"),
                ("Evidence/web-dependency-manifest.json", "packaging/web/dependency-manifest.json"),
                ("ModelCatalog/catalog.json", "packaging/models/catalog.json"),
            })
            {
                File.Copy(Path.Combine(GetRepositoryRoot(), original), Path.Combine(PayloadRoot, destination));
            }
            File.WriteAllText(Path.Combine(PayloadRoot, "Worker/data.bin"), "original payload", new UTF8Encoding(false));
            string dotnet = Path.Combine(Directory.CreateDirectory(Path.Combine(InputRoot, "sdk")).FullName, "dotnet.exe");
            string sbom = Path.Combine(Directory.CreateDirectory(Path.Combine(InputRoot, "sbom")).FullName, "sbom-tool.exe");
            // Deliberately NOT executable: reaching either tool is a test failure, not a fake positive.
            File.WriteAllText(dotnet, "must never execute");
            File.WriteAllText(sbom, "must never execute");
            Arguments = new(StringComparer.Ordinal)
            {
                ["DotnetPath"] = dotnet,
                ["SbomToolPath"] = sbom,
                ["PayloadPath"] = PayloadRoot,
                ["Version"] = "1.2.3-local.1",
                ["OutputPath"] = Path.Combine(OutputParent, "candidate"),
            };
            WriteManifest();
        }

        public void WriteManifest()
        {
            var records = new JsonArray();
            var digestInput = new StringBuilder();
            long totalBytes = 0;
            foreach (string path in Directory.GetFiles(PayloadRoot, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, ManifestPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(PayloadRoot, path).Replace('\\', '/'), StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(PayloadRoot, path).Replace('\\', '/');
                byte[] bytes = File.ReadAllBytes(path);
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                records.Add(new JsonObject { ["path"] = relative, ["byteSize"] = bytes.LongLength, ["sha256"] = sha });
                digestInput.Append(relative).Append('\0').Append(bytes.LongLength.ToString(CultureInfo.InvariantCulture)).Append('\0').Append(sha).Append('\n');
                totalBytes += bytes.LongLength;
            }
            var manifest = new JsonObject
            {
                ["schemaVersion"] = "1.0.0",
                ["taskId"] = "OSL-009",
                ["architecture"] = "x64",
                ["releaseApproved"] = false,
                ["appPublishRequired"] = true,
                ["rootDirectories"] = new JsonArray("Worker", "Tools", "ModelCatalog", "WebView2Runtime", "Licenses", "Evidence"),
                ["fileCount"] = records.Count,
                ["totalBytes"] = totalBytes,
                ["artifactSetSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput.ToString()))).ToLowerInvariant(),
                ["files"] = records,
            };
            File.WriteAllText(ManifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
        }

        public void ChangeManifest(Action<JsonObject> change)
        {
            JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(ManifestPath))!.AsObject();
            change(manifest);
            File.WriteAllText(ManifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            // Only this fixture's GUID directories, including our read-only sentinel.
            foreach (string root in new[] { InputRoot, OutputParent }.Where(Directory.Exists))
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(static value => value.GetString()!).ToArray();

    private static string[] Entries(string root) => Directory.Exists(root)
        ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray() : [];

    private static string FindLatestPowerShell()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != CpuArchitecture.X64 || RuntimeInformation.ProcessArchitecture != CpuArchitecture.X64)
        {
            Assert.Inconclusive("OSL-012 process/AST tests require Windows x64 and PowerShell 7+ x64.");
        }
        var candidates = new List<string>();
        string installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
        if (Directory.Exists(installedRoot)) { candidates.AddRange(Directory.GetDirectories(installedRoot).Select(static path => Path.Combine(path, "pwsh.exe"))); }
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