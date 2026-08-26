#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$invariant = [Globalization.CultureInfo]::InvariantCulture
$metadataTimeoutSeconds = 300
$packetTimeoutSeconds = 3600
$decodeTimeoutSeconds = 7200

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Find-WinGetExecutable {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$ExecutableName
    )

    $command = Get-Command $ExecutableName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $packagesRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (-not (Test-Path -LiteralPath $packagesRoot -PathType Container)) {
        throw "WinGet packages directory was not found. Install $PackageId before running this stage."
    }
    $packageDirectory = Get-ChildItem $packagesRoot -Directory -Filter "$PackageId*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $packageDirectory) {
        throw "WinGet package is not installed: $PackageId"
    }

    $executableMatches = @(Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName)
    if ($executableMatches.Count -ne 1) {
        throw "Expected exactly one $ExecutableName in $PackageId, found $($executableMatches.Count)."
    }

    return $executableMatches[0].FullName
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start: $([IO.Path]::GetFileName($FileName))"
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

function Convert-OptionalInt64 {
    param([object]$Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace($Value.ToString()) -or $Value -eq "N/A") {
        return $null
    }
    return [int64]::Parse($Value.ToString(), [Globalization.NumberStyles]::Integer, $invariant)
}

function Convert-OptionalDecimal {
    param([object]$Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace($Value.ToString()) -or $Value -eq "N/A") {
        return $null
    }
    return [decimal]::Parse($Value.ToString(), [Globalization.NumberStyles]::Float, $invariant)
}

function Convert-RationalToDouble {
    param([string]$Value)

    if ($Value -notmatch '^(-?\d+)/(-?\d+)$' -or [int64]$Matches[2] -eq 0) {
        return $null
    }
    return [math]::Round(([double]$Matches[1] / [double]$Matches[2]), 9)
}

function Get-FilePrefixHex {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Count = 32
    )

    $buffer = [byte[]]::new($Count)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        return [Convert]::ToHexString($buffer, 0, $read).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PacketStatistics {
    param(
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FfprobePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
        "-v", "error", "-show_entries", "packet=stream_index,pts,dts,duration",
        "-of", "csv=p=0", $Path
    )) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start ffprobe packet scan."
        }
        $started = $true
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $stats = @{}
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

        while ($true) {
            $remaining = $deadline - [DateTime]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) {
                throw "ffprobe packet scan exceeded the operational safety timeout of $TimeoutSeconds seconds."
            }
            try {
                $line = $process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
            }
            catch [TimeoutException] {
                throw "ffprobe packet scan exceeded the operational safety timeout of $TimeoutSeconds seconds."
            }
            if ($null -eq $line) {
                break
            }
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            $parts = $line.Split(',')
            if ($parts.Count -lt 4) {
                throw "Unexpected ffprobe packet row."
            }

            [int]$streamIndex = 0
            if (-not [int]::TryParse($parts[0], [ref]$streamIndex)) {
                throw "Invalid packet stream index."
            }
            if (-not $stats.ContainsKey($streamIndex)) {
                $stats[$streamIndex] = [ordered]@{
                    packetCount = [int64]0
                    ptsCount = [int64]0
                    dtsCount = [int64]0
                    minPts = $null
                    maxPts = $null
                    previousPts = $null
                    previousDts = $null
                    presentationReorderedPacketCount = [int64]0
                    nonMonotonicDtsCount = [int64]0
                    durationValues = [Collections.Generic.HashSet[long]]::new()
                }
            }

            $streamStats = $stats[$streamIndex]
            $streamStats.packetCount++
            [long]$value = 0
            if ([long]::TryParse($parts[1], [ref]$value)) {
                $streamStats.ptsCount++
                if ($null -eq $streamStats.minPts -or $value -lt $streamStats.minPts) { $streamStats.minPts = $value }
                if ($null -eq $streamStats.maxPts -or $value -gt $streamStats.maxPts) { $streamStats.maxPts = $value }
                if ($null -ne $streamStats.previousPts -and $value -lt $streamStats.previousPts) {
                    $streamStats.presentationReorderedPacketCount++
                }
                $streamStats.previousPts = $value
            }
            if ([long]::TryParse($parts[2], [ref]$value)) {
                $streamStats.dtsCount++
                if ($null -ne $streamStats.previousDts -and $value -lt $streamStats.previousDts) {
                    $streamStats.nonMonotonicDtsCount++
                }
                $streamStats.previousDts = $value
            }
            if ([long]::TryParse($parts[3], [ref]$value)) {
                [void]$streamStats.durationValues.Add($value)
            }
        }

        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "ffprobe packet scan failed with exit code $($process.ExitCode): $stderr"
        }

        $output = @()
        foreach ($streamIndex in @($stats.Keys | Sort-Object)) {
            $streamStats = $stats[$streamIndex]
            $output += [ordered]@{
                streamIndex = [int]$streamIndex
                packetCount = $streamStats.packetCount
                ptsCount = $streamStats.ptsCount
                dtsCount = $streamStats.dtsCount
                minPts = $streamStats.minPts
                maxPts = $streamStats.maxPts
                presentationReorderedPacketCount = $streamStats.presentationReorderedPacketCount
                nonMonotonicDtsCount = $streamStats.nonMonotonicDtsCount
                packetDurationValues = @($streamStats.durationValues | Sort-Object)
            }
        }
        return $output
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$sample = Get-Item -LiteralPath $resolvedSample
$ffmpegPath = Find-WinGetExecutable -PackageId "BtbN.FFmpeg.LGPL.8.1" -ExecutableName "ffmpeg.exe"
$ffprobePath = Find-WinGetExecutable -PackageId "BtbN.FFmpeg.LGPL.8.1" -ExecutableName "ffprobe.exe"
$ffmpegCapabilitiesPath = Join-Path $PSScriptRoot "..\results\ffmpeg-capabilities.json"
$ffmpegCapabilities = Get-Content $ffmpegCapabilitiesPath -Raw -Encoding utf8 | ConvertFrom-Json -ErrorAction Stop
if ((Get-FileHash $ffmpegPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ffmpegCapabilities.executables.ffmpeg.sha256 -or
    (Get-FileHash $ffprobePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ffmpegCapabilities.executables.ffprobe.sha256) {
    throw "Installed FFmpeg executables do not match the approved ST-002 capability record."
}

$probe = Invoke-CapturedProcess -FileName $ffprobePath -Arguments @(
    "-v", "error", "-show_entries",
    "format=format_name,format_long_name,duration,size,bit_rate,probe_score:stream=index,codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,avg_frame_rate,time_base,start_time,duration,sample_rate,channels,channel_layout",
    "-of", "json", $resolvedSample
) -TimeoutSeconds $metadataTimeoutSeconds
if ($probe.exitCode -ne 0) {
    throw "ffprobe metadata scan failed with exit code $($probe.exitCode): $($probe.stderr)"
}
if ([string]::IsNullOrWhiteSpace($probe.stdout)) {
    throw "ffprobe metadata scan returned no JSON."
}
$metadata = $probe.stdout | ConvertFrom-Json -ErrorAction Stop
$packetStats = @(Get-PacketStatistics -FfprobePath $ffprobePath -Path $resolvedSample -TimeoutSeconds $packetTimeoutSeconds)

$streams = @($metadata.streams | ForEach-Object {
    [ordered]@{
        index = [int]$_.index
        codecType = $_.codec_type
        codecName = $_.codec_name
        profile = $_.profile
        width = if ($null -eq $_.width) { $null } else { [int]$_.width }
        height = if ($null -eq $_.height) { $null } else { [int]$_.height }
        pixelFormat = $_.pix_fmt
        nominalFrameRate = $_.r_frame_rate
        averageFrameRate = $_.avg_frame_rate
        nominalFramesPerSecond = Convert-RationalToDouble $_.r_frame_rate
        averageFramesPerSecond = Convert-RationalToDouble $_.avg_frame_rate
        timeBase = $_.time_base
        startSeconds = Convert-OptionalDecimal $_.start_time
        durationSeconds = Convert-OptionalDecimal $_.duration
        sampleRate = if ($null -eq $_.sample_rate) { $null } else { [int]$_.sample_rate }
        channels = if ($null -eq $_.channels) { $null } else { [int]$_.channels }
        channelLayout = $_.channel_layout
    }
})

$videoStreams = @($streams | Where-Object codecType -eq "video")
$audioStreams = @($streams | Where-Object codecType -eq "audio")
$video = $videoStreams | Select-Object -First 1
$audio = $audioStreams | Select-Object -First 1
$videoEnd = if ($null -eq $video) { $null } else { $video.startSeconds + $video.durationSeconds }
$audioEnd = if ($null -eq $audio) { $null } else { $audio.startSeconds + $audio.durationSeconds }
$avEndDifference = if ($null -eq $videoEnd -or $null -eq $audioEnd) { $null } else { [math]::Round([math]::Abs([double]($videoEnd - $audioEnd)), 6) }

$videoPacketStats = $packetStats | Where-Object streamIndex -eq $video.index | Select-Object -First 1
$rateFieldsDiffer = $null -ne $video -and $video.nominalFrameRate -ne $video.averageFrameRate
$variablePacketDurations = $null -ne $videoPacketStats -and $videoPacketStats.packetDurationValues.Count -gt 1
$warnings = [Collections.Generic.List[string]]::new()
if ($videoStreams.Count -eq 0) { $warnings.Add("no-video-stream") }
if ($audioStreams.Count -eq 0) { $warnings.Add("no-audio-stream") }
if ($rateFieldsDiffer -or $variablePacketDurations) { $warnings.Add("variable-frame-timing-indicator") }
foreach ($stat in $packetStats) {
    if ($stat.nonMonotonicDtsCount -gt 0) { $warnings.Add("non-monotonic-dts-stream-$($stat.streamIndex)") }
}
if ([int64]$metadata.format.size -ne [int64]$sample.Length) { $warnings.Add("container-size-does-not-match-file-size") }

$decodeStarted = Get-Date
$decode = Invoke-CapturedProcess -FileName $ffmpegPath -Arguments @(
    "-nostdin", "-v", "error", "-xerror", "-i", $resolvedSample,
    "-map", "0:v?", "-map", "0:a?", "-f", "null", "NUL"
) -TimeoutSeconds $decodeTimeoutSeconds
$decodeFinished = Get-Date
$sanitizedDecodeError = $decode.stderr.Replace($resolvedSample, "<SOURCE>")
if ($env:USERPROFILE) { $sanitizedDecodeError = $sanitizedDecodeError.Replace($env:USERPROFILE, "<USERPROFILE>") }
$decodeErrorLines = @($sanitizedDecodeError -split "`r?`n" | Where-Object { $_ } | Select-Object -First 100)
if ($decode.exitCode -ne 0) { $warnings.Add("full-decode-failed") }

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    source = [ordered]@{
        fileName = $sample.Name
        extension = $sample.Extension
        byteSize = [int64]$sample.Length
        prefixHex = Get-FilePrefixHex -Path $resolvedSample
    }
    format = [ordered]@{
        names = $metadata.format.format_name
        longName = $metadata.format.format_long_name
        durationSeconds = Convert-OptionalDecimal $metadata.format.duration
        declaredByteSize = Convert-OptionalInt64 $metadata.format.size
        bitRate = Convert-OptionalInt64 $metadata.format.bit_rate
        probeScore = [int]$metadata.format.probe_score
    }
    streams = $streams
    packetStatistics = $packetStats
    timingAssessment = [ordered]@{
        authority = "stream-time-base-and-observed-timestamps"
        nominalAndAverageRateDiffer = $rateFieldsDiffer
        variablePacketDurationsObserved = $variablePacketDurations
        classification = if ($rateFieldsDiffer -or $variablePacketDurations) { "variable-timing-indicators-observed" } else { "constant-timing-indicators-only" }
        presentationReorderingIsNotTreatedAsDtsFailure = $true
    }
    audioVideo = [ordered]@{
        videoStartSeconds = if ($null -eq $video) { $null } else { $video.startSeconds }
        videoEndSeconds = $videoEnd
        audioStartSeconds = if ($null -eq $audio) { $null } else { $audio.startSeconds }
        audioEndSeconds = $audioEnd
        absoluteEndDifferenceSeconds = $avEndDifference
    }
    fullDecode = [ordered]@{
        exitCode = $decode.exitCode
        succeeded = $decode.exitCode -eq 0
        elapsedMilliseconds = [math]::Round(($decodeFinished - $decodeStarted).TotalMilliseconds, 3)
        errorLineCount = @($sanitizedDecodeError -split "`r?`n" | Where-Object { $_ }).Count
        firstErrorLines = $decodeErrorLines
        decoder = "software-default"
    }
    warnings = @($warnings | Select-Object -Unique)
    privacy = [ordered]@{
        metadataTagsPersisted = $false
        absolutePathPersisted = $false
    }
    samplePathConsumed = $true
} | ConvertTo-Json -Depth 10
