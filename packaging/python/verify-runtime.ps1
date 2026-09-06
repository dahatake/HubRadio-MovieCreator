#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimePath,

    [Parameter(Mandatory)]
    [string]$WorkspacePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

if (-not $IsWindows -or [Environment]::Is64BitOperatingSystem -ne $true) {
    throw 'The worker runtime package must be verified on Windows x64.'
}

$packagingRoot = $PSScriptRoot
$manifestPath = Join-Path $packagingRoot 'runtime-manifest.json'
$RuntimePath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RuntimePath))
$WorkspacePath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($WorkspacePath))

function Get-LowerSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# LEGACY fixed-culture compatibility: committed manifests use en-US linguistic order,
# not Ordinal (which changes their digests). Keep all manifest/file-array sorts on en-US.
function Get-ArtifactSetSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $builder = [Text.StringBuilder]::new()
    foreach ($record in @($Records | Sort-Object path -CaseSensitive -Culture en-US)) {
        [void]$builder.Append([string]$record.path)
        [void]$builder.Append([char]0)
        [void]$builder.Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$record.sha256)
        [void]$builder.Append("`n")
    }

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
}

function Assert-NoReparseTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $root = Get-Item -LiteralPath $Path -Force
    if (
        -not ($root -is [IO.DirectoryInfo]) -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'The runtime or workspace root is unsafe.'
    }

    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force -Recurse)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The runtime or workspace tree contains a reparse point.'
        }
    }
}

function Get-VerifiedPayloadSnapshot {
    [CmdletBinding()]
    param()

    Assert-NoReparseTree -Path $RuntimePath
    $fileManifestPath = Join-Path $RuntimePath ([string]$sourceManifest.output.fileManifestName)
    $fileManifest = Get-Content -LiteralPath $fileManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    if (
        $fileManifest.schemaVersion -cne '1.0.0' -or
        $fileManifest.runtimeIdentity -cne [string]$sourceManifest.runtime.identity -or
        $fileManifest.runtimeVersion -cne [string]$sourceManifest.runtime.version -or
        $fileManifest.architecture -cne [string]$sourceManifest.runtime.architecture -or
        $fileManifest.sourceManifestSha256 -cne (Get-LowerSha256 -Path $manifestPath) -or
        $fileManifest.fileCount -ne [int]$sourceManifest.output.expectedFileCount -or
        $fileManifest.totalBytes -ne [long]$sourceManifest.output.expectedTotalBytes -or
        $fileManifest.artifactSetSha256 -cne
            [string]$sourceManifest.output.expectedArtifactSetSha256
    ) {
        throw 'The built runtime file manifest metadata is invalid.'
    }

    $expectedFiles = @($fileManifest.files | Sort-Object path -CaseSensitive -Culture en-US)
    if (
        $expectedFiles.Count -ne [int]$fileManifest.fileCount -or
        [long](($expectedFiles | Measure-Object byteSize -Sum).Sum) -ne
            [long]$fileManifest.totalBytes -or
        (Get-ArtifactSetSha256 -Records $expectedFiles) -cne
            [string]$fileManifest.artifactSetSha256
    ) {
        throw 'The built runtime file manifest aggregate is invalid.'
    }

    $actualFiles = @(
        Get-ChildItem -LiteralPath $RuntimePath -File -Force -Recurse |
            Where-Object Name -ne ([string]$sourceManifest.output.fileManifestName) |
            ForEach-Object {
                [pscustomobject]@{
                    path = [IO.Path]::GetRelativePath($RuntimePath, $_.FullName).Replace('\', '/')
                    byteSize = $_.Length
                    sha256 = Get-LowerSha256 -Path $_.FullName
                    readOnly = ($_.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0
                }
            } |
            Sort-Object path -CaseSensitive -Culture en-US
    )

    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'The built runtime contains missing or extra files.'
    }

    for ($index = 0; $index -lt $actualFiles.Count; $index++) {
        $actual = $actualFiles[$index]
        $expected = $expectedFiles[$index]
        if (
            $actual.path -cne [string]$expected.path -or
            $actual.byteSize -ne [long]$expected.byteSize -or
            $actual.sha256 -cne [string]$expected.sha256 -or
            -not $actual.readOnly
        ) {
            throw 'A built runtime file does not match the package manifest.'
        }
    }

    $manifestItem = Get-Item -LiteralPath $fileManifestPath -Force
    if (($manifestItem.Attributes -band [IO.FileAttributes]::ReadOnly) -eq 0) {
        throw 'The built runtime file manifest must be read-only.'
    }

    return [pscustomobject]@{
        fileCount = $actualFiles.Count
        totalBytes = [long](($actualFiles | Measure-Object byteSize -Sum).Sum)
        artifactSetSha256 = Get-ArtifactSetSha256 -Records $actualFiles
    }
}

function Invoke-IsolatedRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [string]$StandardInput
    )

    $pythonPath = Join-Path $RuntimePath ([string]$sourceManifest.output.entryPoint)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pythonPath
    $startInfo.WorkingDirectory = $WorkspacePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $null -ne $StandardInput
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $false)
    if ($null -ne $StandardInput) {
        $startInfo.StandardInputEncoding = [Text.UTF8Encoding]::new($false, $true)
    }

    $startInfo.Environment.Clear()
    $startInfo.Environment['PATH'] = ''
    $startInfo.Environment['SystemRoot'] = $env:SystemRoot
    $startInfo.Environment['WINDIR'] = $env:WINDIR
    $startInfo.Environment['HOME'] = $WorkspacePath
    $startInfo.Environment['USERPROFILE'] = $WorkspacePath
    $startInfo.Environment['APPDATA'] = $WorkspacePath
    $startInfo.Environment['LOCALAPPDATA'] = $WorkspacePath
    $startInfo.Environment['TEMP'] = $WorkspacePath
    $startInfo.Environment['TMP'] = $WorkspacePath
    $startInfo.Environment['HF_HOME'] = Join-Path $WorkspacePath 'hf-cache'
    $startInfo.Environment['HUGGINGFACE_HUB_CACHE'] = Join-Path $WorkspacePath 'hf-cache\hub'
    $startInfo.Environment['XDG_CACHE_HOME'] = Join-Path $WorkspacePath 'cache'
    $startInfo.Environment['HF_HUB_OFFLINE'] = '1'
    $startInfo.Environment['TRANSFORMERS_OFFLINE'] = '1'
    $startInfo.Environment['PYTHONDONTWRITEBYTECODE'] = '1'
    $startInfo.Environment['PYTHONPATH'] = 'C:\untrusted-python-path'
    $startInfo.Environment['PYTHONHOME'] = 'C:\untrusted-python-home'

    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The isolated Python process did not start.'
        }

        if ($null -ne $StandardInput) {
            $process.StandardInput.Write($StandardInput)
            $process.StandardInput.Flush()
            $process.StandardInput.Close()
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'The isolated Python process exceeded its timeout.'
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    }
    finally {
        $process.Dispose()
    }
}

if (
    -not [IO.Path]::IsPathFullyQualified($RuntimePath) -or
    -not [IO.Path]::IsPathFullyQualified($WorkspacePath) -or
    $RuntimePath.StartsWith('\\', [StringComparison]::Ordinal) -or
    $WorkspacePath.StartsWith('\\', [StringComparison]::Ordinal)
) {
    throw 'RuntimePath and WorkspacePath must be fully qualified local paths.'
}

$sourceManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $RuntimePath -PathType Container)) {
    throw 'The built Python runtime does not exist.'
}

if (-not (Test-Path -LiteralPath $WorkspacePath -PathType Container)) {
    [IO.Directory]::CreateDirectory($WorkspacePath) | Out-Null
}

Assert-NoReparseTree -Path $WorkspacePath
$before = Get-VerifiedPayloadSnapshot

$pthPath = Join-Path $RuntimePath ([string]$sourceManifest.isolation.pthFile)
$pthLines = @(Get-Content -LiteralPath $pthPath -Encoding utf8)
if (
    $pthLines.Count -ne @($sourceManifest.isolation.pthLines).Count -or
    [string]::Join("`n", $pthLines) -cne
        [string]::Join("`n", @($sourceManifest.isolation.pthLines))
) {
    throw 'The built Python path-isolation file is invalid.'
}

$inspectionCode = @'
import importlib.metadata
import importlib.util
import json
import pathlib
import struct
import sys
import av
import ctranslate2
import faster_whisper
import numpy
import onnxruntime
import tokenizers
print(json.dumps({
    "architectureBits": struct.calcsize("P") * 8,
    "dontWriteBytecode": sys.dont_write_bytecode,
    "executable": sys.executable,
    "isolated": sys.flags.isolated,
    "noUserSite": sys.flags.no_user_site,
    "path": sys.path,
    "pipAvailable": importlib.util.find_spec("pip") is not None,
    "versions": {
        "av": importlib.metadata.version("av"),
        "ctranslate2": importlib.metadata.version("ctranslate2"),
        "faster-whisper": importlib.metadata.version("faster-whisper"),
        "numpy": importlib.metadata.version("numpy"),
        "onnxruntime": importlib.metadata.version("onnxruntime"),
        "tokenizers": importlib.metadata.version("tokenizers"),
    },
    "version": list(sys.version_info[:3]),
}, sort_keys=True, separators=(",", ":")))
'@
$inspection = Invoke-IsolatedRuntime -ArgumentList @('-I', '-B', '-c', $inspectionCode)
if ($inspection.ExitCode -ne 0 -or -not [string]::IsNullOrEmpty($inspection.StandardError)) {
    throw 'The isolated runtime native dependency inspection failed.'
}

$inspectionValue = $inspection.StandardOutput.Trim() | ConvertFrom-Json
if (
    $inspectionValue.version[0] -ne 3 -or
    $inspectionValue.version[1] -ne 12 -or
    $inspectionValue.version[2] -ne 10 -or
    $inspectionValue.architectureBits -ne 64 -or
    $inspectionValue.isolated -ne 1 -or
    $inspectionValue.noUserSite -ne 1 -or
    $inspectionValue.dontWriteBytecode -ne $true -or
    $inspectionValue.pipAvailable -ne $false -or
    $inspectionValue.versions.av -cne '18.1.0' -or
    $inspectionValue.versions.ctranslate2 -cne '4.8.1' -or
    $inspectionValue.versions.'faster-whisper' -cne '1.2.1' -or
    $inspectionValue.versions.numpy -cne '2.5.2' -or
    $inspectionValue.versions.onnxruntime -cne '1.29.0' -or
    $inspectionValue.versions.tokenizers -cne '0.23.1'
) {
    throw 'The isolated runtime inspection returned an unexpected configuration.'
}

$runtimePrefix = [IO.Path]::TrimEndingDirectorySeparator($RuntimePath) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($inspectionValue.path)) {
    $fullPath = [IO.Path]::GetFullPath([string]$path)
    if (
        $fullPath -ine [IO.Path]::TrimEndingDirectorySeparator($RuntimePath) -and
        -not $fullPath.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'The isolated runtime resolved a module search path outside its package tree.'
    }
}

$health = Invoke-IsolatedRuntime -ArgumentList @($sourceManifest.output.healthArguments)
if (
    $health.ExitCode -ne 0 -or
    -not [string]::IsNullOrEmpty($health.StandardError) -or
    $health.StandardOutput.Trim() -cne '{"status":"ok"}'
) {
    throw 'The isolated worker health check failed.'
}

$request = '{"method":"worker.ping","params":{},"requestId":"123e4567-e89b-12d3-a456-426614174000","type":"request","version":1}' + "`n"
$protocol = Invoke-IsolatedRuntime `
    -ArgumentList @($sourceManifest.output.protocolArguments) `
    -StandardInput $request
if (
    $protocol.ExitCode -ne 0 -or
    -not [string]::IsNullOrEmpty($protocol.StandardError)
) {
    throw 'The isolated worker protocol check failed.'
}

$protocolLines = @($protocol.StandardOutput -split "\r?\n" | Where-Object Length -gt 0)
if ($protocolLines.Count -ne 3) {
    throw 'The isolated worker protocol emitted an unexpected record count.'
}

$envelopes = @($protocolLines | ForEach-Object { $_ | ConvertFrom-Json })
if (
    $envelopes[0].type -cne 'progress' -or
    $envelopes[0].sequence -ne 1 -or
    $envelopes[1].type -cne 'progress' -or
    $envelopes[1].sequence -ne 2 -or
    $envelopes[2].type -cne 'response' -or
    $envelopes[2].method -cne 'worker.ping' -or
    $envelopes[2].requestId -cne '123e4567-e89b-12d3-a456-426614174000'
) {
    throw 'The isolated worker protocol output is invalid.'
}

$after = Get-VerifiedPayloadSnapshot
if (
    $after.fileCount -ne $before.fileCount -or
    $after.totalBytes -ne $before.totalBytes -or
    $after.artifactSetSha256 -cne $before.artifactSetSha256
) {
    throw 'The worker modified its read-only package tree while running.'
}

Write-Information 'Self-contained Python worker runtime verification passed.' -InformationAction Continue
[pscustomobject]@{
    RuntimeVersion = [string]$sourceManifest.runtime.version
    RuntimeFiles = $after.fileCount
    RuntimeBytes = $after.totalBytes
    RuntimeArtifactSetSha256 = $after.artifactSetSha256
    NativeImports = 6
    Health = 'passed'
    Protocol = 'passed'
    SystemPathRequired = $false
    RuntimeTreeModified = $false
} | Format-List