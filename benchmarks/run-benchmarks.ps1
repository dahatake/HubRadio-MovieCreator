#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet("hardware", "ffmpeg-capabilities", "media-ingest")]
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
        payload = [ordered]@{}
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
