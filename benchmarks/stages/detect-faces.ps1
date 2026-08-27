#Requires -Version 7.0

<#
.SYNOPSIS
Measures MediaPipe face detection coverage on the Stage 0 fixture.

.DESCRIPTION
Vertical reframing needs to know where faces are, so this stage records how often a face is found,
how large it is relative to the frame, and how long detection takes. Sampling runs on the ST-004
proxy and reports every observation on the source presentation timeline.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath,
    [ValidateRange(1, 10000)]
    [int]$SampleCount = 300
)

$ErrorActionPreference = "Stop"
$probeTimeoutSeconds = 3600
$detectionTimeoutSeconds = 7200

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [string]$StandardOutputPath
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $writer = $null
    try {
        if (-not $process.Start()) {
            throw "Failed to start $([IO.Path]::GetFileName($FileName))."
        }
        $started = $true
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if ($StandardOutputPath) {
            $writer = [IO.StreamWriter]::new($StandardOutputPath, $false, [Text.UTF8Encoding]::new($false))
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ($true) {
                $remaining = $deadline - [DateTime]::UtcNow
                if ($remaining -le [TimeSpan]::Zero) {
                    throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
                }
                try {
                    $line = $process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
                }
                catch [TimeoutException] {
                    throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
                }
                if ($null -eq $line) { break }
                $writer.WriteLine($line)
            }
            $writer.Flush()
            $stdout = ""
        }
        else {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
            }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
        }

        $process.WaitForExit()
        return [ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdout
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function Find-ApprovedExecutable {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$ExecutableName,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $packagesRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    $packageDirectory = Get-ChildItem $packagesRoot -Directory -Filter "$PackageId*" -ErrorAction Stop |
        Sort-Object -Property LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $packageDirectory) {
        throw "Approved FFmpeg package is not installed: $PackageId"
    }
    $approved = @(Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName | Where-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedSha256
    })
    if ($approved.Count -ne 1) {
        throw "Expected exactly one approved $ExecutableName, found $($approved.Count)."
    }
    return $approved[0].FullName
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workDirectory = Join-Path $repositoryRoot "benchmarks\work"
$modelDirectory = Join-Path $repositoryRoot "benchmarks\models"

$proxyMap = Get-Content (Join-Path $PSScriptRoot "..\results\proxy-frame-map.json") -Raw -Encoding utf8 | ConvertFrom-Json
$proxyPath = Join-Path $repositoryRoot ($proxyMap.proxy.relativePath -replace '/', '\')
if (-not (Test-Path -LiteralPath $proxyPath -PathType Leaf)) {
    throw "The ST-004 proxy is missing. Run the proxy-frame-map stage before this stage."
}
$proxyHash = (Get-FileHash $proxyPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($proxyHash -ne $proxyMap.proxy.sha256) {
    throw "The proxy on disk does not match the content hash recorded by ST-004."
}

$capabilities = Get-Content (Join-Path $PSScriptRoot "..\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffprobePath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $capabilities.executables.ffprobe.sha256

$sourcePacketsPath = Join-Path $workDirectory "face-source-video-packet-pts.csv"
$proxyPacketsPath = Join-Path $workDirectory "face-proxy-video-packet-pts.csv"
foreach ($pair in @(
    @{ Path = $sourcePacketsPath; Media = $resolvedSample },
    @{ Path = $proxyPacketsPath; Media = $proxyPath }
)) {
    $stagingPath = "$($pair.Path).$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $scan = Invoke-CapturedProcess -FileName $ffprobePath -TimeoutSeconds $probeTimeoutSeconds -StandardOutputPath $stagingPath -Arguments @(
            "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "csv=p=0", $pair.Media
        )
        if ($scan.exitCode -ne 0) {
            throw "Packet scan failed with exit code $($scan.exitCode)."
        }
        Move-Item -LiteralPath $stagingPath -Destination $pair.Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Force
        }
    }
}

$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing. Run benchmarks/setup-python-env.ps1 first."
}

$detection = Invoke-CapturedProcess -FileName $pythonPath -TimeoutSeconds $detectionTimeoutSeconds -Arguments @(
    (Join-Path $PSScriptRoot "face_detection.py"),
    "--proxy-video", $proxyPath,
    "--source-packets", $sourcePacketsPath,
    "--source-time-base", $proxyMap.sourceTiming.timeBase,
    "--proxy-packets", $proxyPacketsPath,
    "--proxy-time-base", $proxyMap.proxyTiming.timeBase,
    "--model-path", (Join-Path $modelDirectory "blaze_face_short_range.tflite"),
    "--control-image-path", (Join-Path $workDirectory "mediapipe-face-control-portrait.jpg"),
    "--sample-count", "$SampleCount"
)
if ($detection.exitCode -ne 0) {
    throw "Face detection failed with exit code $($detection.exitCode): $($detection.stderr)"
}
$report = $detection.stdout | ConvertFrom-Json -ErrorAction Stop

$mediapipeVersion = (Invoke-CapturedProcess -FileName $pythonPath -TimeoutSeconds 300 -Arguments @("-c", "import mediapipe;print(mediapipe.__version__)")).stdout.Trim()

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    library = [ordered]@{
        name = "MediaPipe Face Detector"
        version = $mediapipeVersion
        analysedMedia = "proxy"
        proxySha256 = $proxyHash
        proxyBenchmarkRunId = $proxyMap.benchmarkRunId
    }
    model = $report.model
    control = $report.control
    proxy = $report.proxy
    sampling = $report.sampling
    detection = $report.detection
    observations = $report.observations
    privacy = $report.privacy
    samplePathConsumed = $true
} | ConvertTo-Json -Depth 12
