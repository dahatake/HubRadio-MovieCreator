#Requires -Version 7.0

<#
OSL-015: independent runtime smoke for an already-built portable candidate.
This is a bounded technical check, not clean-OS acceptance, ASR accuracy evaluation,
or release approval. The candidate is read-only input; RunDirectory is disposable output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidatePath,

    [Parameter(Mandatory)]
    [string]$RunDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}
if (-not $IsWindows -or [Environment]::Is64BitOperatingSystem -ne $true) {
    throw 'OSL-015 requires Windows x64.'
}

function Resolve-AbsoluteLocalPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "$Name must be an explicit absolute local path."
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
}

$CandidatePath = Resolve-AbsoluteLocalPath $CandidatePath 'CandidatePath'
$RunDirectory = Resolve-AbsoluteLocalPath $RunDirectory 'RunDirectory'
if (-not (Test-Path -LiteralPath $CandidatePath -PathType Container)) {
    throw 'CandidatePath must be an existing directory.'
}
if (Test-Path -LiteralPath $RunDirectory) {
    if (-not (Test-Path -LiteralPath $RunDirectory -PathType Container) -or
        @(Get-ChildItem -LiteralPath $RunDirectory -Force).Count -ne 0) {
        throw 'RunDirectory must be a new or empty disposable directory.'
    }
}
else {
    [IO.Directory]::CreateDirectory($RunDirectory) | Out-Null
}

function Get-TreeSnapshot {
    param([Parameter(Mandatory)][string]$Root)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse | Sort-Object FullName)) {
        $records.Add([pscustomobject]@{
            path = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            byteSize = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return ,$records.ToArray()
}

function Invoke-Isolated {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$InputText = ''
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    $info.WorkingDirectory = $RunDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $false)
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $info.Environment.Clear()
    $info.Environment['PATH'] = ''
    $info.Environment['SystemRoot'] = $env:SystemRoot
    $info.Environment['WINDIR'] = $env:WINDIR
    $info.Environment['TEMP'] = $RunDirectory
    $info.Environment['TMP'] = $RunDirectory
    $info.Environment['HOME'] = $RunDirectory
    $info.Environment['USERPROFILE'] = $RunDirectory
    $info.Environment['APPDATA'] = $RunDirectory
    $info.Environment['LOCALAPPDATA'] = $RunDirectory
    $info.Environment['HF_HOME'] = Join-Path $RunDirectory 'hf-cache'
    $info.Environment['HUGGINGFACE_HUB_CACHE'] = Join-Path $RunDirectory 'hf-cache\hub'
    $info.Environment['TRANSFORMERS_CACHE'] = Join-Path $RunDirectory 'transformers-cache'
    $info.Environment['XDG_CACHE_HOME'] = Join-Path $RunDirectory 'cache'
    $info.Environment['HF_HUB_OFFLINE'] = '1'
    $info.Environment['TRANSFORMERS_OFFLINE'] = '1'
    $info.Environment['PYTHONDONTWRITEBYTECODE'] = '1'
    $info.Environment['PYTHONPATH'] = 'C:\untrusted-python-path'
    $info.Environment['PYTHONHOME'] = 'C:\untrusted-python-home'
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo = $info
        if (-not $process.Start()) { throw "Could not start isolated process: $FileName" }
        if ($InputText.Length -gt 0) {
            $process.StandardInput.Write($InputText)
        }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "Isolated process timed out: $FileName"
        }
        [pscustomobject]@{
            exitCode = $process.ExitCode
            stdout = $stdout.GetAwaiter().GetResult()
            stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function New-Result {
    param([string]$Status, [string]$Reason = '')
    [ordered]@{ status = $Status; reason = $Reason }
}

$before = @(Get-TreeSnapshot $CandidatePath)
$contents = Join-Path $CandidatePath 'Contents'
$worker = Join-Path $contents 'Worker'
$tools = Join-Path $contents 'Tools'
$catalogPath = Join-Path $contents 'ModelCatalog\catalog.json'
$python = Join-Path $worker 'python.exe'
$ffmpeg = Join-Path $tools 'ffmpeg.exe'
$ffprobe = Join-Path $tools 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ffmpeg -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
    throw 'Candidate is missing the required Worker or Tools executables.'
}

$results = [ordered]@{}
$inspectionCode = @'
import importlib.metadata
import json
import struct
import sys
import av
import ctranslate2
import faster_whisper
import numpy
import onnxruntime
import tokenizers
print(json.dumps({"architectureBits":struct.calcsize("P") * 8,"dontWriteBytecode":sys.dont_write_bytecode,"path":sys.path,"versions":{"av":importlib.metadata.version("av"),"ctranslate2":importlib.metadata.version("ctranslate2"),"faster-whisper":importlib.metadata.version("faster-whisper"),"numpy":importlib.metadata.version("numpy"),"onnxruntime":importlib.metadata.version("onnxruntime"),"tokenizers":importlib.metadata.version("tokenizers")}},sort_keys=True,separators=(",",":")))
'@
$inspection = Invoke-Isolated $python @('-I', '-B', '-c', $inspectionCode)
$results.nativeImports = if ($inspection.exitCode -eq 0 -and [string]::IsNullOrEmpty($inspection.stderr)) { New-Result 'PASS' } else { New-Result 'FAIL' 'Bundled native imports failed.' }

$health = Invoke-Isolated $python @('-I', '-B', '-m', 'hubradio_worker', '--health')
$results.workerHealth = if ($health.exitCode -eq 0 -and $health.stdout.Trim() -ceq '{"status":"ok"}' -and [string]::IsNullOrEmpty($health.stderr)) { New-Result 'PASS' } else { New-Result 'FAIL' 'Bundled Worker health contract failed.' }

$request = '{"method":"worker.ping","params":{},"requestId":"123e4567-e89b-12d3-a456-426614174000","type":"request","version":1}' + "`n"
$protocol = Invoke-Isolated $python @('-I', '-B', '-m', 'hubradio_worker', '--protocol') $request
$protocolLines = @($protocol.stdout -split "\r?\n" | Where-Object Length -gt 0)
$results.workerProtocol = if ($protocol.exitCode -eq 0 -and [string]::IsNullOrEmpty($protocol.stderr) -and $protocolLines.Count -eq 3 -and
    (@($protocolLines | ForEach-Object { $_ | ConvertFrom-Json })[2].method -ceq 'worker.ping')) { New-Result 'PASS' } else { New-Result 'FAIL' 'Bundled Worker protocol contract failed.' }

foreach ($tool in @(@{ name = 'ffmpeg'; path = $ffmpeg }, @{ name = 'ffprobe'; path = $ffprobe })) {
    $version = Invoke-Isolated $tool.path @('-version')
    $results[$tool.name] = if ($version.exitCode -eq 0 -and $version.stdout -match ('^' + $tool.name + ' version ') -and [string]::IsNullOrEmpty($version.stderr)) { New-Result 'PASS' } else { New-Result 'FAIL' "Bundled $($tool.name) did not run from its candidate path." }
}

$modelResult = New-Result 'NOT EVALUATED' 'CPU model load is optional when the candidate has no eligible local model.'
if (Test-Path -LiteralPath $catalogPath -PathType Leaf) {
    $catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding utf8 | ConvertFrom-Json
    $model = @($catalog.models | Where-Object { $_.runtimeEligible -eq $true -and $_.bundle -ne $null }) | Select-Object -First 1
    if ($null -ne $model) {
        $modelPath = Join-Path (Join-Path $contents 'ModelCatalog') ([string]$model.bundle.relativePath)
        if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) {
            $modelResult = New-Result 'BLOCKED' 'Eligible local CPU model bundle is missing.'
        }
        else {
            $modelCode = @'
import json
import sys
from faster_whisper import WhisperModel
WhisperModel(sys.argv[1], device="cpu", compute_type="int8", cpu_threads=0, num_workers=1, local_files_only=True)
print(json.dumps({"loaded":True}, separators=(",",":")))
'@
            $modelRun = Invoke-Isolated $python @('-I', '-B', '-c', $modelCode, $modelPath)
            $modelResult = if ($modelRun.exitCode -eq 0 -and $modelRun.stdout.Trim() -ceq '{"loaded":true}' -and [string]::IsNullOrEmpty($modelRun.stderr)) { New-Result 'PASS' } else { New-Result 'BLOCKED' 'Eligible CPU model could not be loaded offline; no ASR accuracy claim is made.' }
        }
    }
}
$results.cpuModelLoad = $modelResult

$after = @(Get-TreeSnapshot $CandidatePath)
$beforeJson = $before | ConvertTo-Json -Depth 4 -Compress
$afterJson = $after | ConvertTo-Json -Depth 4 -Compress
$results.candidateHashesPreserved = if ($beforeJson -ceq $afterJson) { New-Result 'PASS' } else { New-Result 'FAIL' 'Candidate bytes changed during the smoke test.' }
$results.asrAccuracy = New-Result 'NOT EVALUATED' 'This smoke test loads the local model only; it does not measure transcription accuracy.'
$results.cleanOs = New-Result 'NOT EVALUATED' 'This is not clean Windows acceptance.'

$blocked = @($results.Values | Where-Object { $_.status -eq 'BLOCKED' }).Count
$failed = @($results.Values | Where-Object { $_.status -eq 'FAIL' }).Count
$summary = [ordered]@{ taskId = 'OSL-015'; candidatePathSupplied = $true; runDirectorySupplied = $true; results = $results }
$summary | ConvertTo-Json -Depth 8
if ($failed -gt 0) { exit 1 }
if ($blocked -gt 0) { exit 2 }
