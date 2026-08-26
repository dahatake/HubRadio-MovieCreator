#Requires -Version 7.0

<#
.SYNOPSIS
Compares the H.264 and AAC encoding paths that are usable with the approved LGPL FFmpeg build.

.DESCRIPTION
The approved build does not ship libx264, so the delivery path has to come from a Windows or vendor
encoder. This stage encodes the same source segment with each candidate, verifies that the result
decodes, and records perceptual quality against the same segment so that a later decision can be
based on measurements rather than on the presence of an encoder in the capability list.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath,
    [double]$SegmentStartSeconds = 300,
    [double]$SegmentDurationSeconds = 30,
    [int]$OutputWidth = 1280,
    [int]$OutputHeight = 720,
    [string]$OutputFrameRate = "30000/1001"
)

$ErrorActionPreference = "Stop"
$encodeTimeoutSeconds = 1800
$verifyTimeoutSeconds = 900

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [string]$WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    if ($WorkingDirectory) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start $([IO.Path]::GetFileName($FileName))."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
        }
        return [ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
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

function Protect-SensitiveText {
    param([AllowEmptyString()][AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) { return "" }
    $safe = $Text
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
    $safe = $safe.Replace($repositoryRoot, "<WORKSPACE>")
    if ($env:USERPROFILE) { $safe = $safe.Replace($env:USERPROFILE, "<USERPROFILE>") }
    return $safe
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workDirectory = Join-Path $repositoryRoot "benchmarks\work\encoder-matrix"
[IO.Directory]::CreateDirectory($workDirectory) | Out-Null

$capabilities = Get-Content (Join-Path $PSScriptRoot "..\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegPath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $capabilities.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $capabilities.executables.ffprobe.sha256

$videoFilter = "scale=${OutputWidth}:${OutputHeight}:flags=lanczos,fps=$OutputFrameRate,format=yuv420p"

$candidates = @(
    [ordered]@{
        id = "h264_nvenc"
        family = "NVIDIA NVENC"
        encoderArguments = @("-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23", "-b:v", "0")
    },
    [ordered]@{
        id = "h264_mf"
        family = "Windows Media Foundation"
        encoderArguments = @("-c:v", "h264_mf", "-b:v", "4M")
    }
)

$results = @()
foreach ($candidate in $candidates) {
    $outputPath = Join-Path $workDirectory "$($candidate.id).mp4"
    $arguments = @(
        "-nostdin", "-v", "error",
        "-ss", $SegmentStartSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-t", $SegmentDurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-i", $resolvedSample,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", $videoFilter
    ) + $candidate.encoderArguments + @(
        "-c:a", "aac", "-b:a", "160k",
        "-map_metadata", "-1", "-movflags", "+faststart",
        "-y", $outputPath
    )

    $started = Get-Date
    $encode = Invoke-CapturedProcess -FileName $ffmpegPath -Arguments $arguments -TimeoutSeconds $encodeTimeoutSeconds
    $finished = Get-Date

    $entry = [ordered]@{
        id = $candidate.id
        family = $candidate.family
        requestedRateControl = ($candidate.encoderArguments -join " ")
        succeeded = $encode.exitCode -eq 0
        exitCode = $encode.exitCode
        elapsedMilliseconds = [math]::Round(($finished - $started).TotalMilliseconds, 3)
        stderrExcerpt = (Protect-SensitiveText $encode.stderr).Split("`n") | Select-Object -First 3
    }

    if ($entry.succeeded -and (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        $item = Get-Item -LiteralPath $outputPath
        $probe = Invoke-CapturedProcess -FileName $ffprobePath -TimeoutSeconds 300 -Arguments @(
            "-v", "error",
            "-show_entries", "format=duration:stream=index,codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,sample_rate,channels,bit_rate",
            "-of", "json", $outputPath
        )
        $probed = if ($probe.exitCode -eq 0) { $probe.stdout | ConvertFrom-Json } else { $null }
        $video = if ($null -ne $probed) { @($probed.streams | Where-Object codec_type -eq "video")[0] } else { $null }
        $audio = if ($null -ne $probed) { @($probed.streams | Where-Object codec_type -eq "audio")[0] } else { $null }

        $decode = Invoke-CapturedProcess -FileName $ffmpegPath -TimeoutSeconds $verifyTimeoutSeconds -Arguments @(
            "-nostdin", "-v", "error", "-xerror", "-i", $outputPath,
            "-map", "0:v:0", "-map", "0:a:0?", "-f", "null", "NUL"
        )

        $entry.byteSize = [int64]$item.Length
        $entry.sha256 = (Get-FileHash $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry.observedVideoCodec = if ($null -eq $video) { $null } else { $video.codec_name }
        $entry.observedVideoProfile = if ($null -eq $video) { $null } else { $video.profile }
        $entry.observedWidth = if ($null -eq $video) { $null } else { [int]$video.width }
        $entry.observedHeight = if ($null -eq $video) { $null } else { [int]$video.height }
        $entry.observedPixelFormat = if ($null -eq $video) { $null } else { $video.pix_fmt }
        $entry.observedFrameRate = if ($null -eq $video) { $null } else { $video.r_frame_rate }
        $entry.observedAudioCodec = if ($null -eq $audio) { $null } else { $audio.codec_name }
        $entry.observedAudioSampleRate = if ($null -eq $audio) { $null } else { [int]$audio.sample_rate }
        $entry.observedAudioChannels = if ($null -eq $audio) { $null } else { [int]$audio.channels }
        $entry.observedDurationSeconds = if ($null -eq $probed) { $null } else { [decimal]$probed.format.duration }
        $entry.decodeVerified = $decode.exitCode -eq 0
        $entry.decodeExitCode = $decode.exitCode

        $vmafLogName = "$($candidate.id)-vmaf.json"
        $vmafLogPath = Join-Path $workDirectory $vmafLogName
        if (Test-Path -LiteralPath $vmafLogPath) { Remove-Item -LiteralPath $vmafLogPath -Force }
        # The filtergraph parser treats a colon as an option separator, so the log file is addressed
        # by a bare name from the working directory instead of by an absolute Windows path.
        $vmaf = Invoke-CapturedProcess -FileName $ffmpegPath -TimeoutSeconds $verifyTimeoutSeconds -WorkingDirectory $workDirectory -Arguments @(
            "-nostdin", "-v", "error",
            "-i", $outputPath,
            "-ss", $SegmentStartSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
            "-t", $SegmentDurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
            "-i", $resolvedSample,
            "-filter_complex", "[1:v]$videoFilter[reference];[0:v][reference]libvmaf=log_path=${vmafLogName}:log_fmt=json",
            "-f", "null", "NUL"
        )
        if ($vmaf.exitCode -eq 0 -and (Test-Path -LiteralPath $vmafLogPath)) {
            $vmafReport = Get-Content $vmafLogPath -Raw -Encoding utf8 | ConvertFrom-Json
            $entry.vmafMeasured = $true
            $entry.vmafMean = [math]::Round([double]$vmafReport.pooled_metrics.vmaf.mean, 4)
            $entry.vmafMinimum = [math]::Round([double]$vmafReport.pooled_metrics.vmaf.min, 4)
            $entry.vmafFrameCount = [int]$vmafReport.frames.Count
        }
        else {
            $entry.vmafMeasured = $false
            $entry.vmafFailureExcerpt = (Protect-SensitiveText $vmaf.stderr).Split("`n") | Select-Object -First 3
        }
    }

    $results += $entry
}

$successful = @($results | Where-Object { $_.succeeded -and $_.decodeVerified } | ForEach-Object { [pscustomobject]$_ })
$vmafComparable = @($successful | Where-Object { $_.vmafMeasured })

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    segment = [ordered]@{
        startSeconds = $SegmentStartSeconds
        durationSeconds = $SegmentDurationSeconds
        videoFilter = $videoFilter
        outputWidth = $OutputWidth
        outputHeight = $OutputHeight
        outputFrameRate = $OutputFrameRate
        audioCodec = "aac"
        audioBitrate = "160k"
    }
    build = [ordered]@{
        packageId = $capabilities.package.id
        packageVersion = $capabilities.package.version
        ffmpegSha256 = $capabilities.executables.ffmpeg.sha256
        libx264Available = [bool]$capabilities.capabilities.encoders.libx264
        libx265Available = [bool]$capabilities.capabilities.encoders.libx265
    }
    candidates = $results
    summary = [ordered]@{
        candidateCount = $results.Count
        succeededAndDecodedCount = $successful.Count
        fastestSucceededId = if ($successful.Count -gt 0) { (@($successful | Sort-Object -Property elapsedMilliseconds) | Select-Object -First 1).id } else { $null }
        smallestOutputId = if ($successful.Count -gt 0) { (@($successful | Sort-Object -Property byteSize) | Select-Object -First 1).id } else { $null }
        highestVmafId = if ($vmafComparable.Count -gt 0) { (@($vmafComparable | Sort-Object -Property vmafMean -Descending) | Select-Object -First 1).id } else { $null }
        vmafComparableCount = $vmafComparable.Count
        deliveryPathAvailableWithoutGplEncoder = $successful.Count -gt 0
    }
    samplePathConsumed = $true
} | ConvertTo-Json -Depth 10
