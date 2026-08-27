#Requires -Version 7.0

<#
.SYNOPSIS
Compares local audio-event and recording-quality candidate methods on the Stage 0 fixture.

.DESCRIPTION
The stage compares approved-FFmpeg silencedetect, deterministic waveform rules, Silero VAD,
YAMNet audio-event classification, and the decode evidence already measured by ST-003. It writes
candidate evidence only; it never edits media or marks a candidate for automatic deletion.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 7200
$silenceNoiseDb = -50.0
$silenceMinimumSeconds = 2.0
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
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
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
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
        $stopwatch.Stop()
        return [ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
            elapsedMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        }
    }
    finally {
        $stopwatch.Stop()
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

function Convert-InvariantDouble {
    param([Parameter(Mandatory)][string]$Text)

    return [double]::Parse($Text, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-SilenceDetectReport {
    param(
        [Parameter(Mandatory)][string]$StandardError,
        [Parameter(Mandatory)][double]$MediaDurationSeconds,
        [Parameter(Mandatory)][double]$SourceTimelineOffsetSeconds,
        [Parameter(Mandatory)][double]$ElapsedMilliseconds
    )

    $numberPattern = '[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?'
    $candidates = [Collections.Generic.List[object]]::new()
    $pendingStart = $null
    foreach ($line in ($StandardError -split "`r?`n")) {
        $startMatch = [regex]::Match($line, "silence_start:\s*(?<value>$numberPattern)")
        if ($startMatch.Success) {
            $pendingStart = Convert-InvariantDouble $startMatch.Groups["value"].Value
            continue
        }

        $endMatch = [regex]::Match(
            $line,
            "silence_end:\s*(?<end>$numberPattern)\s*\|\s*silence_duration:\s*(?<duration>$numberPattern)"
        )
        if (-not $endMatch.Success) {
            continue
        }
        $end = Convert-InvariantDouble $endMatch.Groups["end"].Value
        $duration = Convert-InvariantDouble $endMatch.Groups["duration"].Value
        $start = if ($null -eq $pendingStart) { $end - $duration } else { [double]$pendingStart }
        $candidates.Add([ordered]@{
            sourceStartSeconds = [math]::Round($SourceTimelineOffsetSeconds + $start, 6)
            sourceEndSeconds = [math]::Round($SourceTimelineOffsetSeconds + $end, 6)
            durationSeconds = [math]::Round($duration, 6)
        })
        $pendingStart = $null
    }

    if ($null -ne $pendingStart -and $MediaDurationSeconds -gt [double]$pendingStart) {
        $duration = $MediaDurationSeconds - [double]$pendingStart
        $candidates.Add([ordered]@{
            sourceStartSeconds = [math]::Round($SourceTimelineOffsetSeconds + [double]$pendingStart, 6)
            sourceEndSeconds = [math]::Round($SourceTimelineOffsetSeconds + $MediaDurationSeconds, 6)
            durationSeconds = [math]::Round($duration, 6)
        })
    }

    $totalDuration = if ($candidates.Count -eq 0) {
        0.0
    }
    else {
        [double](($candidates | Measure-Object -Property durationSeconds -Sum).Sum)
    }
    return [ordered]@{
        candidateCount = $candidates.Count
        totalCandidateSeconds = [math]::Round($totalDuration, 6)
        candidates = @($candidates)
        elapsedMilliseconds = $ElapsedMilliseconds
    }
}

function Invoke-SilenceDetect {
    param(
        [Parameter(Mandatory)][string]$FfmpegPath,
        [Parameter(Mandatory)][string]$MediaPath,
        [Parameter(Mandatory)][double]$MediaDurationSeconds,
        [Parameter(Mandatory)][double]$SourceTimelineOffsetSeconds
    )

    $result = Invoke-CapturedProcess -FileName $FfmpegPath -TimeoutSeconds $processTimeoutSeconds -Arguments @(
        "-nostdin", "-hide_banner", "-nostats", "-v", "info",
        "-i", $MediaPath,
        "-map", "0:a:0", "-vn", "-sn", "-dn",
        "-af", "silencedetect=noise=$($silenceNoiseDb.ToString('0.0###', $invariantCulture))dB:d=$($silenceMinimumSeconds.ToString('0.0###', $invariantCulture))",
        "-f", "null", "NUL"
    )
    if ($result.exitCode -ne 0) {
        throw "FFmpeg silencedetect failed with exit code $($result.exitCode)."
    }
    return Get-SilenceDetectReport -StandardError $result.stderr -MediaDurationSeconds $MediaDurationSeconds -SourceTimelineOffsetSeconds $SourceTimelineOffsetSeconds -ElapsedMilliseconds $result.elapsedMilliseconds
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$workDirectory = Join-Path $repositoryRoot "benchmarks\work"
$modelDirectory = Join-Path $repositoryRoot "benchmarks\models"
[IO.Directory]::CreateDirectory($workDirectory) | Out-Null
[IO.Directory]::CreateDirectory($modelDirectory) | Out-Null

$capabilities = Get-Content (Join-Path $PSScriptRoot "..\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegPath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $capabilities.executables.ffmpeg.sha256
$mediaIngest = Get-Content (Join-Path $PSScriptRoot "..\results\media-ingest.json") -Raw -Encoding utf8 | ConvertFrom-Json
if ($mediaIngest.source.sha256 -ne (Get-FileHash -LiteralPath $resolvedSample -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw "The source does not match the ST-003 canonical media-ingest result."
}
$audioStreams = @($mediaIngest.streams | Where-Object { $_.codecType -eq "audio" })
if ($audioStreams.Count -ne 1) {
    throw "ST-016 requires exactly one source audio stream."
}
$audioStream = $audioStreams[0]
$sourceTimelineOffsetSeconds = [double]$audioStream.startSeconds
$sourceTimelineOffsetText = $sourceTimelineOffsetSeconds.ToString("0.######", $invariantCulture)
$mediaDurationSeconds = [double]$mediaIngest.format.durationSeconds

$controlPath = Join-Path $workDirectory "audio-silence-control.wav"
$controlStagingPath = "$controlPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $controlGeneration = Invoke-CapturedProcess -FileName $ffmpegPath -TimeoutSeconds 600 -Arguments @(
        "-nostdin", "-hide_banner", "-v", "error",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=2",
        "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=16000:d=3",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=2",
        "-filter_complex", "[0:a][1:a][2:a]concat=n=3:v=0:a=1[out]",
        "-map", "[out]", "-c:a", "pcm_s16le", "-f", "wav", "-y", $controlStagingPath
    )
    if ($controlGeneration.exitCode -ne 0) {
        throw "Audio silence control generation failed with exit code $($controlGeneration.exitCode)."
    }
    Move-Item -LiteralPath $controlStagingPath -Destination $controlPath -Force
}
finally {
    Remove-Item -LiteralPath $controlStagingPath -Force -ErrorAction SilentlyContinue
}

$controlSilence = Invoke-SilenceDetect -FfmpegPath $ffmpegPath -MediaPath $controlPath -MediaDurationSeconds 7.0 -SourceTimelineOffsetSeconds 0.0
$matchingControlCandidates = @($controlSilence.candidates | Where-Object {
    [math]::Abs([double]$_.sourceStartSeconds - 2.0) -le 0.15 -and
    [math]::Abs([double]$_.sourceEndSeconds - 5.0) -le 0.15
})
$controlPassed = $matchingControlCandidates.Count -ge 1
if (-not $controlPassed) {
    throw "FFmpeg silencedetect did not find the known 2-5 second control silence."
}
$fixtureSilence = Invoke-SilenceDetect -FfmpegPath $ffmpegPath -MediaPath $resolvedSample -MediaDurationSeconds $mediaDurationSeconds -SourceTimelineOffsetSeconds $sourceTimelineOffsetSeconds

$ffmpegSilencePayload = [ordered]@{
    configuration = [ordered]@{
        noiseDb = $silenceNoiseDb
        minimumDurationSeconds = $silenceMinimumSeconds
        thresholdIsProductionApproved = $false
    }
    control = [ordered]@{
        expectedSourceStartSeconds = 2.0
        expectedSourceEndSeconds = 5.0
        candidateCount = $controlSilence.candidateCount
        candidates = $controlSilence.candidates
        generationElapsedMilliseconds = $controlGeneration.elapsedMilliseconds
        detectionElapsedMilliseconds = $controlSilence.elapsedMilliseconds
        passed = $controlPassed
    }
    fixture = $fixtureSilence
}
$ffmpegSilencePath = Join-Path $workDirectory "audio-ffmpeg-silence.json"
$ffmpegSilenceStagingPath = "$ffmpegSilencePath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $ffmpegSilenceStagingPath,
        ($ffmpegSilencePayload | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false)
    )
    Move-Item -LiteralPath $ffmpegSilenceStagingPath -Destination $ffmpegSilencePath -Force
}
finally {
    Remove-Item -LiteralPath $ffmpegSilenceStagingPath -Force -ErrorAction SilentlyContinue
}

$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing. Run benchmarks/setup-python-env.ps1 first."
}
$sileroModelPath = Join-Path $repositoryRoot "benchmarks\.venv\Lib\site-packages\faster_whisper\assets\silero_vad_v6.onnx"
$detection = Invoke-CapturedProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -Arguments @(
    (Join-Path $PSScriptRoot "audio_event_detection.py"),
    "--source", $resolvedSample,
    "--source-start-seconds", $sourceTimelineOffsetText,
    "--yamnet-model-path", (Join-Path $modelDirectory "yamnet_audio_classifier_with_metadata.tflite"),
    "--speech-control-path", (Join-Path $workDirectory "speech_16000_hz_mono.wav"),
    "--silero-model-path", $sileroModelPath,
    "--ffmpeg-silence-json", $ffmpegSilencePath
)
if ($detection.exitCode -ne 0) {
    throw "Audio event detection failed with exit code $($detection.exitCode): $($detection.stderr)"
}
$report = $detection.stdout | ConvertFrom-Json -ErrorAction Stop

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    tooling = [ordered]@{
        ffmpegPackageId = $capabilities.package.id
        ffmpegSha256 = $capabilities.executables.ffmpeg.sha256
        pythonPackages = $report.packages
    }
    analysisInput = [ordered]@{
        sourceSha256 = $mediaIngest.source.sha256
        mediaIngestBenchmarkRunId = $mediaIngest.benchmarkRunId
        sourceAudio = [ordered]@{
            codec = $audioStream.codecName
            profile = $audioStream.profile
            sampleRate = $audioStream.sampleRate
            channels = $audioStream.channels
            channelLayout = $audioStream.channelLayout
            decodedTimelineStartSeconds = $sourceTimelineOffsetSeconds
            minimumPacketPresentationSeconds = [double]$mediaIngest.timingAssessment.negativeStartSecondsAudio
            timelineAuthority = "decoded audio frame PTS; PyAV first decoded frame was independently observed at stream PTS 0"
        }
        decodedAnalysis = $report.analysisInput
    }
    decodeEvidence = [ordered]@{
        fullDecodeSucceeded = $mediaIngest.fullDecode.succeeded
        fullDecodeExitCode = $mediaIngest.fullDecode.exitCode
        decoderReportedErrorLineCount = $mediaIngest.fullDecode.decoderReportedErrorLineCount
        stderrOrigin = $mediaIngest.fullDecode.stderrOrigin
        nullMuxerMessageCount = $mediaIngest.fullDecode.stderrLineCount
        interpretation = "ST-003 recorded no decoder error. Null-muxer timestamp messages are not classified as recording corruption."
    }
    models = $report.models
    controls = $report.controls
    methods = $report.methods
    agreement = $report.agreement
    performance = $report.performance
    privacy = $report.privacy
    annotations = $report.annotations
    automaticDeletionPerformed = $report.automaticDeletionPerformed
    candidateThresholdsAreStage0ConfigurationsNotProductionValues = $report.candidateThresholdsAreStage0ConfigurationsNotProductionValues
    samplePathConsumed = $true
} | ConvertTo-Json -Depth 15
