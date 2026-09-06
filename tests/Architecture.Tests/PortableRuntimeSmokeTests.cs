using System.Text.RegularExpressions;

namespace HubRadio.Architecture.Tests;

// OSL-015 contract tests only. They never launch the candidate, build the app, or
// turn an unavailable model/environment into a fake success.
[TestClass]
public sealed class PortableRuntimeSmokeTests
{
    private const string ScriptPath = "tests/smoke/Test-PortableRuntime.ps1";

    [TestMethod]
    public void SmokeScriptRequiresExplicitAbsoluteCandidateAndDisposableRunDirectory()
    {
        string script = ReadScript();
        StringAssert.StartsWith(script, "#Requires -Version 7.0");
        StringAssert.Contains(script, "[Parameter(Mandatory)]\n    [string]$CandidatePath");
        StringAssert.Contains(script, "[Parameter(Mandatory)]\n    [string]$RunDirectory");
        StringAssert.Contains(script, "$Name must be an explicit absolute local path.");
        StringAssert.Contains(script, "RunDirectory must be a new or empty disposable directory.");
        StringAssert.Contains(script, "CandidatePath must be an existing directory.");
        StringAssert.Contains(script, "Set-StrictMode -Version Latest");
        StringAssert.Contains(script, "[Environment]::Is64BitOperatingSystem -ne $true");
    }

    [TestMethod]
    public void SmokeScriptUsesCandidateLayoutAndNeverWritesToCandidate()
    {
        string script = ReadScript();
        foreach (string required in new[]
        {
            "Join-Path $CandidatePath 'Contents'",
            "Join-Path $contents 'Worker'",
            "Join-Path $contents 'Tools'",
            "Join-Path $contents 'ModelCatalog\\catalog.json'",
            "Join-Path $worker 'python.exe'",
            "Join-Path $tools 'ffmpeg.exe'",
            "Join-Path $tools 'ffprobe.exe'",
            "Get-TreeSnapshot $CandidatePath",
            "candidateHashesPreserved",
            "Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256",
        })
        {
            StringAssert.Contains(script, required);
        }

        foreach (string forbidden in new[]
        {
            "Set-Content", "Out-File", "Add-Content", "Copy-Item", "Move-Item", "Remove-Item",
            "[IO.File]::Write", "[IO.File]::WriteAll", "Start-Process", "HubRadio.App.exe",
            "Invoke-WebRequest", "Invoke-RestMethod", "curl", "wget",
        })
        {
            Assert.IsFalse(script.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
    }

    [TestMethod]
    public void SmokeScriptRunsEachDependencyInAnOfflineEmptyPathChildProcess()
    {
        string script = ReadScript();
        foreach (string required in new[]
        {
            "info.Environment.Clear()",
            "info.Environment['PATH'] = ''",
            "info.Environment['HF_HUB_OFFLINE'] = '1'",
            "info.Environment['TRANSFORMERS_OFFLINE'] = '1'",
            "info.WorkingDirectory = $RunDirectory",
            "Invoke-Isolated $python @('-I', '-B', '-m', 'hubradio_worker', '--health')",
            "Invoke-Isolated $python @('-I', '-B', '-m', 'hubradio_worker', '--protocol')",
            "Invoke-Isolated $tool.path @('-version')",
            "local_files_only=True",
            "device=\"cpu\"",
            "compute_type=\"int8\"",
        })
        {
            StringAssert.Contains(script, required);
        }
        StringAssert.Contains(script, "hf-cache");
        StringAssert.Contains(script, "PYTHONPATH'] = 'C:\\untrusted-python-path'");
        StringAssert.Contains(script, "PYTHONHOME'] = 'C:\\untrusted-python-home'");
    }

    [TestMethod]
    public void SmokeScriptHasExplicitNonSuccessStatesAndDoesNotClaimAccuracyOrCleanOs()
    {
        string script = ReadScript();
        foreach (string status in new[] { "'PASS'", "'FAIL'", "'BLOCKED'", "'NOT EVALUATED'" })
        {
            StringAssert.Contains(script, status);
        }
        StringAssert.Contains(script, "asrAccuracy = New-Result 'NOT EVALUATED'");
        StringAssert.Contains(script, "cleanOs = New-Result 'NOT EVALUATED'");
        StringAssert.Contains(script, "no ASR accuracy claim is made.");
        StringAssert.Contains(script, "exit 1");
        StringAssert.Contains(script, "exit 2");
        Assert.IsFalse(Regex.IsMatch(script, @"accuracy\s*=\s*['""](?:pass|success)", RegexOptions.IgnoreCase));
        Assert.IsFalse(script.Contains("clean OS: PASS", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SmokeScriptDoesNotEmitRawMediaOrSecrets()
    {
        string script = ReadScript();
        foreach (string forbidden in new[]
        {
            ".MOV", ".MP4", "audio_path", "Authorization", "Bearer ", "api_key", "password", "secret",
            "StandardInput.Write($rawMedia", "Get-Content -Raw $media",
        })
        {
            Assert.IsFalse(script.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
        StringAssert.Contains(script, "This is not clean Windows acceptance.");
        StringAssert.Contains(script, "does not measure transcription accuracy.");
    }

    [TestMethod]
    public void SmokeScriptLoadsOnlyTheCatalogEligibleLocalCpuModel()
    {
        string script = ReadScript();
        StringAssert.Contains(script, "$_.runtimeEligible -eq $true -and $_.bundle -ne $null");
        StringAssert.Contains(script, "CPU model load is optional when the candidate has no eligible local model.");
        StringAssert.Contains(script, "Eligible local CPU model bundle is missing.");
        StringAssert.Contains(script, "could not be loaded offline");
        StringAssert.Contains(script, "WhisperModel(sys.argv[1]");
        Assert.IsFalse(script.Contains("transcribe(", StringComparison.Ordinal),
            "OSL-015 must not claim ASR quality by running transcription.");
    }

    private static string ReadScript() => File.ReadAllText(
        Path.Combine(GetRepositoryRoot(), ScriptPath)).ReplaceLineEndings("\n");

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
