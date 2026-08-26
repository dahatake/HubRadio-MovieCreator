#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath,
    [int]$ProxyWidth = 640,
    [int]$ProxyHeight = 360,
    [string]$ProxyFrameRate = "30000/1001",
    [int]$ProxyGopSize = 30,
    [int]$VisualSamples = 10
)

$ErrorActionPreference = "Stop"
$probeTimeoutSeconds = 3600
$proxyTimeoutSeconds = 7200
$analysisTimeoutSeconds = 3600

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
        Sort-Object LastWriteTimeUtc -Descending |
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

function Get-StreamProperty {
    param(
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Entries
    )

    $result = Invoke-CapturedProcess -FileName $FfprobePath -TimeoutSeconds 300 -Arguments @(
        "-v", "error", "-select_streams", "v:0", "-show_entries", $Entries, "-of", "json", $Path
    )
    if ($result.exitCode -ne 0) {
        throw "ffprobe failed with exit code $($result.exitCode)."
    }
    return $result.stdout | ConvertFrom-Json -ErrorAction Stop
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workDirectory = Join-Path $repositoryRoot "benchmarks\work"
[IO.Directory]::CreateDirectory($workDirectory) | Out-Null

$capabilities = Get-Content (Join-Path $PSScriptRoot "..\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegPath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $capabilities.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $capabilities.executables.ffprobe.sha256

$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing. Run benchmarks/setup-python-env.ps1 first."
}

$sourceProbe = Get-StreamProperty -FfprobePath $ffprobePath -Path $resolvedSample -Entries "stream=time_base"
$sourceTimeBase = $sourceProbe.streams[0].time_base

$proxyPath = Join-Path $workDirectory "proxy-640x360.mp4"
$proxyStagingPath = Join-Path $workDirectory ".proxy-$([Guid]::NewGuid().ToString("N")).partial.mp4"
$sourcePacketsPath = Join-Path $workDirectory "source-video-packet-pts.csv"
$proxyPacketsPath = Join-Path $workDirectory "proxy-video-packet-pts.csv"

try {
    $proxyStarted = Get-Date
    $render = Invoke-CapturedProcess -FileName $ffmpegPath -TimeoutSeconds $proxyTimeoutSeconds -Arguments @(
        "-nostdin", "-v", "error",
        "-i", $resolvedSample,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", "scale=${ProxyWidth}:${ProxyHeight}:flags=bilinear,fps=$ProxyFrameRate,format=yuv420p",
        "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "28", "-b:v", "0", "-g", "$ProxyGopSize",
        "-c:a", "aac", "-b:a", "96k",
        "-map_metadata", "-1", "-movflags", "+faststart",
        "-y", $proxyStagingPath
    )
    if ($render.exitCode -ne 0) {
        throw "Proxy generation failed with exit code $($render.exitCode)."
    }
    $proxyFinished = Get-Date
    [IO.File]::Move($proxyStagingPath, $proxyPath, $true)

    $sourcePackets = Invoke-CapturedProcess -FileName $ffprobePath -TimeoutSeconds $probeTimeoutSeconds -StandardOutputPath $sourcePacketsPath -Arguments @(
        "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "csv=p=0", $resolvedSample
    )
    if ($sourcePackets.exitCode -ne 0) {
        throw "Source packet scan failed with exit code $($sourcePackets.exitCode)."
    }

    $proxyPackets = Invoke-CapturedProcess -FileName $ffprobePath -TimeoutSeconds $probeTimeoutSeconds -StandardOutputPath $proxyPacketsPath -Arguments @(
        "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "csv=p=0", $proxyPath
    )
    if ($proxyPackets.exitCode -ne 0) {
        throw "Proxy packet scan failed with exit code $($proxyPackets.exitCode)."
    }

    $proxyProbe = Get-StreamProperty -FfprobePath $ffprobePath -Path $proxyPath -Entries "stream=time_base,width,height,r_frame_rate,codec_name,pix_fmt"
    $proxyStream = $proxyProbe.streams[0]

    $analysis = Invoke-CapturedProcess -FileName $pythonPath -TimeoutSeconds $analysisTimeoutSeconds -Arguments @(
        (Join-Path $PSScriptRoot "proxy_frame_map.py"),
        "--source-packets", $sourcePacketsPath,
        "--source-time-base", $sourceTimeBase,
        "--proxy-packets", $proxyPacketsPath,
        "--proxy-time-base", $proxyStream.time_base,
        "--source-video", $resolvedSample,
        "--proxy-video", $proxyPath,
        "--ffmpeg", $ffmpegPath,
        "--visual-samples", "$VisualSamples"
    )
    if ($analysis.exitCode -ne 0) {
        throw "Frame map analysis failed with exit code $($analysis.exitCode): $($analysis.stderr)"
    }
    $report = $analysis.stdout | ConvertFrom-Json -ErrorAction Stop

    $proxyItem = Get-Item -LiteralPath $proxyPath
    [ordered]@{
        schemaVersion = "1.0.0"
        recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        proxy = [ordered]@{
            relativePath = "benchmarks/work/$($proxyItem.Name)"
            byteSize = [int64]$proxyItem.Length
            sha256 = (Get-FileHash $proxyPath -Algorithm SHA256).Hash.ToLowerInvariant()
            width = [int]$proxyStream.width
            height = [int]$proxyStream.height
            frameRate = $proxyStream.r_frame_rate
            codec = $proxyStream.codec_name
            pixelFormat = $proxyStream.pix_fmt
            encoder = "h264_nvenc"
            requestedGopSize = $ProxyGopSize
            generationElapsedMilliseconds = [math]::Round(($proxyFinished - $proxyStarted).TotalMilliseconds, 3)
            trackedByGit = $false
        }
        sourceTiming = $report.sourceTiming
        proxyTiming = $report.proxyTiming
        mapping = $report.mapping
        roundTrip = $report.roundTrip
        visualCorrespondence = $report.visualCorrespondence
        samplePathConsumed = $true
    } | ConvertTo-Json -Depth 10
}
finally {
    if (Test-Path -LiteralPath $proxyStagingPath) {
        Remove-Item -LiteralPath $proxyStagingPath -Force
    }
}
