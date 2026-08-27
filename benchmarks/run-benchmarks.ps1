#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet("hardware", "ffmpeg-capabilities", "media-ingest", "proxy-frame-map", "encoder-matrix", "shot-detection", "face-detection", "audio-event-and-recording-quality")]
    [string]$Stage = "hardware",
    [string]$FixtureManifest = (Join-Path $PSScriptRoot "fixtures\fixture-manifest.json"),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "results\runs"),
    [ValidateRange(60, 86400)]
    [int]$ChildTimeoutSeconds = 10800
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-RelativeSourcePath {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RelativePath
    )

    $manifestDirectory = Split-Path -Parent $ManifestPath
    return [IO.Path]::GetFullPath((Join-Path $manifestDirectory $RelativePath))
}

function Invoke-JsonChildProcess {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwsh
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-NoLogo", "-NoProfile", "-File", $ScriptPath, "-SamplePath", $SourcePath)) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start benchmark child process."
        }
        $started = $true

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Benchmark child process exceeded the operational safety timeout of $TimeoutSeconds seconds."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $safeError = $stderr.Replace($SourcePath, "<SOURCE>")
        if ($env:USERPROFILE) { $safeError = $safeError.Replace($env:USERPROFILE, "<USERPROFILE>") }

        if ($process.ExitCode -ne 0) {
            if ($safeError.Length -gt 2000) { $safeError = $safeError.Substring(0, 2000) + " [truncated]" }
            throw "Benchmark child process exited with code $($process.ExitCode): $safeError"
        }
        if ([string]::IsNullOrWhiteSpace($stdout)) {
            throw "Benchmark child process returned no JSON."
        }

        return $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        throw
    }
    finally {
        $process.Dispose()
    }
}

function Assert-HardwarePayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "collection", "operatingSystem",
        "processor", "memory", "graphics", "npuDevices", "storage",
        "runtimesAndTools", "sampleAsset"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Hardware payload is missing required property: $name"
        }
    }

    foreach ($name in @("byteSize", "sha256")) {
        if ($Payload.sampleAsset.PSObject.Properties.Name -notcontains $name) {
            throw "Hardware payload sampleAsset is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported hardware payload schemaVersion: $($Payload.schemaVersion)"
    }
}

function Assert-FfmpegPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "package", "executables", "version",
        "licenseAssessment", "capabilities", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "FFmpeg payload is missing required property: $name"
        }
    }
    if ($Payload.package.archiveSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "FFmpeg package archive SHA-256 is invalid."
    }
    if ($Payload.executables.ffmpeg.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.executables.ffprobe.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "FFmpeg executable SHA-256 is invalid."
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported FFmpeg payload schemaVersion: $($Payload.schemaVersion)"
    }
}

function Assert-MediaPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "source", "format", "streams",
        "packetStatistics", "timingAssessment", "audioVideo", "fullDecode",
        "warnings", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Media payload is missing required property: $name"
        }
    }
    if ($Payload.fullDecode.PSObject.Properties.Name -notcontains "succeeded") {
        throw "Media payload fullDecode is missing succeeded."
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported media payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Media payload did not confirm fixture path consumption."
    }
}

function Assert-ProxyFrameMapPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "proxy", "sourceTiming", "proxyTiming",
        "mapping", "roundTrip", "visualCorrespondence", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Proxy frame map payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported proxy frame map payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.proxy.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Proxy frame map payload does not carry a valid proxy content hash."
    }
    if ([int64]$Payload.mapping.proxyFrameCount -le 0 -or [int64]$Payload.mapping.distinctSourceFramesReferenced -le 0) {
        throw "Proxy frame map payload does not describe a usable mapping."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Proxy frame map payload did not confirm fixture path consumption."
    }
}

function Assert-EncoderMatrixPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "segment", "build", "candidates", "summary", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Encoder matrix payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported encoder matrix payload schemaVersion: $($Payload.schemaVersion)"
    }
    if (@($Payload.candidates).Count -lt 1) {
        throw "Encoder matrix payload did not record any candidate."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Encoder matrix payload did not confirm fixture path consumption."
    }
}

function Assert-ShotDetectionPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "library", "detectors", "agreement",
        "controlClip", "note", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Shot detection payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported shot detection payload schemaVersion: $($Payload.schemaVersion)"
    }
    if (@($Payload.detectors).Count -lt 1) {
        throw "Shot detection payload did not record any detector configuration."
    }
    $detectorNames = @($Payload.detectors | ForEach-Object { $_.detector })
    foreach ($requiredDetector in @("content", "adaptive")) {
        if ($detectorNames -notcontains $requiredDetector) {
            throw "Shot detection payload did not compare the required detector: $requiredDetector"
        }
    }
    if ($Payload.library.proxySha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Shot detection payload does not identify the analysed proxy by content hash."
    }
    if ($Payload.controlClip.PSObject.Properties.Name -notcontains "allConfigurationsDetectKnownCuts" -or
        $Payload.controlClip.allConfigurationsDetectKnownCuts -ne $true) {
        throw "Shot detection control clip did not confirm all detector configurations against known cuts."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Shot detection payload did not confirm fixture path consumption."
    }
}

function Assert-FaceDetectionPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "library", "model", "control", "proxy",
        "sampling", "detection", "observations", "privacy", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Face detection payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported face detection payload schemaVersion: $($Payload.schemaVersion)"
    }
    if ($Payload.library.proxySha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Face detection payload does not identify the analysed proxy by content hash."
    }
    if ($Payload.model.sha256 -notmatch '^[0-9a-f]{64}$' -or [int64]$Payload.model.byteSize -le 0) {
        throw "Face detection payload does not identify a usable model by content hash and byte size."
    }
    if ($Payload.sampling.runningMode -ne "VIDEO") {
        throw "Face detection payload did not use MediaPipe VIDEO mode."
    }
    if ($Payload.control.runningMode -ne "VIDEO" -or
        $Payload.control.passed -ne $true -or
        [int]$Payload.control.detectedFaceCount -lt 1 -or
        $Payload.control.asset.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Face detection positive control did not validate the pinned VIDEO-mode pipeline."
    }
    if ([int]$Payload.sampling.requestedSampleCount -ne 300 -or
        [int]$Payload.sampling.attemptedSampleCount -ne 300 -or
        [int]$Payload.sampling.decodedSampleCount -ne 300 -or
        [int]$Payload.sampling.decodeFailureCount -ne 0) {
        throw "Face detection payload did not decode all 300 required stratified samples."
    }
    if (@($Payload.observations).Count -ne [int]$Payload.sampling.decodedSampleCount) {
        throw "Face detection observation count does not match the decoded sample count."
    }
    if ($Payload.privacy.imagePixelsPersisted -ne $false) {
        throw "Face detection payload unexpectedly persisted sampled image pixels."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Face detection payload did not confirm fixture path consumption."
    }
}

function Assert-AudioEventPayload {
    param([Parameter(Mandatory)][object]$Payload)

    foreach ($name in @(
        "schemaVersion", "recordedAtUtc", "tooling", "analysisInput", "decodeEvidence",
        "models", "controls", "methods", "agreement", "performance", "privacy",
        "annotations", "automaticDeletionPerformed",
        "candidateThresholdsAreStage0ConfigurationsNotProductionValues", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -notcontains $name) {
            throw "Audio event payload is missing required property: $name"
        }
    }
    if ($Payload.schemaVersion -ne "1.0.0") {
        throw "Unsupported audio event payload schemaVersion: $($Payload.schemaVersion)"
    }
    foreach ($controlName in @("ffmpegSilencedetect", "waveformRules", "sileroVad", "yamnet")) {
        if ($Payload.controls.PSObject.Properties.Name -notcontains $controlName -or
            $Payload.controls.$controlName.passed -ne $true) {
            throw "Audio event positive control did not pass: $controlName"
        }
    }
    if ($Payload.models.yamnet.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Payload.models.sileroVad.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Audio event payload does not identify both local models by content hash."
    }
    if ([int64]$Payload.analysisInput.decodedAnalysis.decodedSampleCountPerChannel -le 0 -or
        [double]$Payload.analysisInput.decodedAnalysis.decodedDurationSeconds -le 0) {
        throw "Audio event payload does not describe a usable decoded analysis input."
    }
    if ([string]::IsNullOrWhiteSpace([string]$Payload.analysisInput.sourceAudio.timelineAuthority) -or
        $Payload.analysisInput.decodedAnalysis.PSObject.Properties.Name -notcontains "decodedTimelineEvidence" -or
        $Payload.analysisInput.decodedAnalysis.decodedTimelineEvidence.PSObject.Properties.Name -notcontains "firstDecodedFramePts" -or
        $Payload.analysisInput.decodedAnalysis.decodedTimelineEvidence.PSObject.Properties.Name -notcontains "firstDecodedFrameSeconds") {
        throw "Audio event payload does not preserve decoded-frame timeline evidence."
    }
    if ($Payload.methods.ffmpegSilencedetect.PSObject.Properties.Name -notcontains "configuration" -or
        $Payload.methods.ffmpegSilencedetect.configuration.PSObject.Properties.Name -notcontains "noiseDb" -or
        $Payload.methods.ffmpegSilencedetect.configuration.PSObject.Properties.Name -notcontains "minimumDurationSeconds" -or
        $Payload.methods.ffmpegSilencedetect.configuration.thresholdIsProductionApproved -ne $false) {
        throw "Audio event payload does not preserve the unapproved FFmpeg silencedetect configuration."
    }
    if ($Payload.decodeEvidence.fullDecodeSucceeded -ne $true -or
        [int]$Payload.decodeEvidence.decoderReportedErrorLineCount -ne 0) {
        throw "ST-003 decode evidence does not support a clean decoder result."
    }
    if ($Payload.annotations.removalReferenceAvailable -ne $false -or
        $Payload.annotations.precisionRecallMeasured -ne $false) {
        throw "Audio event payload unexpectedly claims removal annotations or precision/recall."
    }
    if ($Payload.automaticDeletionPerformed -ne $false) {
        throw "Audio event stage must not automatically delete candidate intervals."
    }
    if ($Payload.candidateThresholdsAreStage0ConfigurationsNotProductionValues -ne $true) {
        throw "Audio event payload did not mark thresholds as unapproved Stage 0 configurations."
    }
    if ($Payload.privacy.sourceAudioPersisted -ne $false -or
        $Payload.privacy.sourceAudioSamplesIncludedInReport -ne $false -or
        $Payload.privacy.sourceTranscriptIncludedInReport -ne $false) {
        throw "Audio event payload persisted or reported source audio content."
    }
    if ($Payload.samplePathConsumed -ne $true) {
        throw "Audio event payload did not confirm fixture path consumption."
    }
}

function Protect-SensitiveText {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$SourcePath
    )

    $safe = $Text.Replace($SourcePath, "<SOURCE>")
    $workspacePath = Split-Path -Parent $PSScriptRoot
    $safe = $safe.Replace($workspacePath, "<WORKSPACE>")
    if ($env:USERPROFILE) { $safe = $safe.Replace($env:USERPROFILE, "<USERPROFILE>") }
    return $safe
}

function Get-SafeFailurePayload {
    param([AllowNull()][object]$Payload)

    $safePayload = [ordered]@{}
    if ($null -eq $Payload) {
        return [pscustomobject]$safePayload
    }

    foreach ($name in @(
        "schemaVersion", "library", "model", "control", "proxy",
        "sampling", "detection", "tooling", "decodeEvidence", "models", "controls",
        "performance", "privacy", "annotations", "automaticDeletionPerformed",
        "candidateThresholdsAreStage0ConfigurationsNotProductionValues", "samplePathConsumed"
    )) {
        if ($Payload.PSObject.Properties.Name -contains $name) {
            $safePayload[$name] = $Payload.$name
        }
    }
    return [pscustomobject]$safePayload
}

function Write-BenchmarkResult {
    param(
        [Parameter(Mandatory)][object]$Result,
        [Parameter(Mandatory)][string]$FinalPath
    )

    $schemaPath = Join-Path $PSScriptRoot "results\schema.json"
    $json = $Result | ConvertTo-Json -Depth 12
    if (-not ($json | Test-Json -SchemaFile $schemaPath -ErrorAction Stop)) {
        throw "Benchmark result failed JSON Schema validation."
    }

    [IO.Directory]::CreateDirectory((Split-Path -Parent $FinalPath)) | Out-Null
    $stagingPath = Join-Path (Split-Path -Parent $FinalPath) ("." + [IO.Path]::GetFileName($FinalPath) + ".tmp")
    try {
        [IO.File]::WriteAllText($stagingPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $stagingPath -Destination $FinalPath
    }
    finally {
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Force
        }
    }
}

$manifestPath = (Resolve-Path $FixtureManifest).Path
$manifestText = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false, $true))
$manifest = $manifestText | ConvertFrom-Json

if ($manifest.sourceAssets.Count -ne 1) {
    throw "Stage 0 harness currently requires exactly one source asset."
}

$sourceSpec = $manifest.sourceAssets[0]
$sourcePath = Get-RelativeSourcePath -ManifestPath $manifestPath -RelativePath $sourceSpec.relativePath
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Fixture source does not exist: $sourcePath"
}

$sourceBefore = Get-Item -LiteralPath $sourcePath
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceHash -ne $sourceSpec.sha256) {
    throw "Fixture SHA-256 mismatch. Expected $($sourceSpec.sha256), got $sourceHash."
}
if ([int64]$sourceBefore.Length -ne [int64]$sourceSpec.byteSize) {
    throw "Fixture byte-size mismatch. Expected $($sourceSpec.byteSize), got $($sourceBefore.Length)."
}

$startedAt = (Get-Date).ToUniversalTime()
$runId = "{0}-{1}-{2}" -f $startedAt.ToString("yyyyMMddTHHmmssfffffffZ"), $Stage, ([Guid]::NewGuid().ToString("N").Substring(0, 8))

try {
    switch ($Stage) {
        "hardware" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "collect-hardware.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-HardwarePayload -Payload $payload
        }
        "ffmpeg-capabilities" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\inspect-ffmpeg.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-FfmpegPayload -Payload $payload
        }
        "media-ingest" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\inspect-media.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-MediaPayload -Payload $payload
        }
        "proxy-frame-map" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\build-proxy-frame-map.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ProxyFrameMapPayload -Payload $payload
        }
        "encoder-matrix" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\measure-encoders.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-EncoderMatrixPayload -Payload $payload
        }
        "shot-detection" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-shots.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-ShotDetectionPayload -Payload $payload
        }
        "face-detection" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-faces.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-FaceDetectionPayload -Payload $payload
        }
        "audio-event-and-recording-quality" {
            $payload = Invoke-JsonChildProcess -ScriptPath (Join-Path $PSScriptRoot "stages\detect-audio-events.ps1") -SourcePath $sourcePath -TimeoutSeconds $ChildTimeoutSeconds
            Assert-AudioEventPayload -Payload $payload
        }
        default {
            throw "Unsupported benchmark stage: $Stage"
        }
    }
    if ($Stage -eq "hardware" -and
        ([int64]$payload.sampleAsset.byteSize -ne [int64]$sourceSpec.byteSize -or $payload.sampleAsset.sha256 -ne $sourceSpec.sha256)) {
        throw "Benchmark payload does not describe the validated fixture source."
    }
    if ($Stage -eq "media-ingest" -and [int64]$payload.source.byteSize -ne [int64]$sourceSpec.byteSize) {
        throw "Media payload does not describe the validated fixture source."
    }
    $sourceAfter = Get-Item -LiteralPath $sourcePath
    if ($sourceAfter.Length -ne $sourceBefore.Length -or $sourceAfter.LastWriteTimeUtc -ne $sourceBefore.LastWriteTimeUtc) {
        throw "The source fixture changed while the benchmark was running."
    }
    $sourceHashAfter = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHashAfter -ne $sourceHash) {
        throw "The source fixture content changed while the benchmark was running."
    }
}
catch {
    $finishedAt = (Get-Date).ToUniversalTime()
    $safeMessage = Protect-SensitiveText -Text $_.Exception.Message -SourcePath $sourcePath
    $safeFailurePayload = Get-SafeFailurePayload -Payload $payload
    $failureResult = [ordered]@{
        schemaVersion = "1.0.0"
        runId = $runId
        stage = $Stage
        status = "failed"
        startedAtUtc = $startedAt.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        elapsedMilliseconds = [math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 3)
        fixture = [ordered]@{
            manifestPath = [IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $manifestPath).Replace('\', '/')
            manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            sourceAssetId = $sourceSpec.id
            sourceSha256 = $sourceHash
            sourceByteSize = [int64]$sourceBefore.Length
        }
        payload = $safeFailurePayload
        error = [ordered]@{
            code = "BENCHMARK_STAGE_FAILED"
            message = $safeMessage
        }
    }
    $failurePath = Join-Path $OutputDirectory "$runId.json"
    Write-BenchmarkResult -Result $failureResult -FinalPath $failurePath
    throw "Benchmark stage failed. Machine-readable failure: $([IO.Path]::GetFileName($failurePath))"
}

$finishedAt = (Get-Date).ToUniversalTime()
$result = [ordered]@{
    schemaVersion = "1.0.0"
    runId = $runId
    stage = $Stage
    status = "succeeded"
    startedAtUtc = $startedAt.ToString("O")
    finishedAtUtc = $finishedAt.ToString("O")
    elapsedMilliseconds = [math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 3)
    fixture = [ordered]@{
        manifestPath = [IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $manifestPath).Replace('\', '/')
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceAssetId = $sourceSpec.id
        sourceSha256 = $sourceHash
        sourceByteSize = [int64]$sourceBefore.Length
    }
    payload = $payload
    error = $null
}

$finalPath = Join-Path $OutputDirectory "$runId.json"
Write-BenchmarkResult -Result $result -FinalPath $finalPath

[ordered]@{
    resultPath = (Resolve-Path $finalPath).Path
    resultSha256 = (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash.ToLowerInvariant()
    runId = $runId
    status = "succeeded"
} | ConvertTo-Json
