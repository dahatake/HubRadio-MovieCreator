#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$SamplePath
)

$ErrorActionPreference = "Stop"

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

function Test-Capability {
    param(
        [Parameter(Mandatory)][string]$Listing,
        [Parameter(Mandatory)][string]$Name
    )

    $escaped = [regex]::Escape($Name)
    return $Listing -match "(?m)^\s*[A-Z\.]+\s+$escaped\s"
}

$packageId = "BtbN.FFmpeg.LGPL.8.1"
$packageVersion = "8.1.2-20260731"
$archiveSha256 = "089e4169e93b2b3f3acbfced3c0704d24276a225641bdda04d796d28b07a2a38"
$expectedBuildIdentifier = "n8.1.2-34-g9b6c8969e0-20260731"
$ffmpegPath = Find-WinGetExecutable -PackageId $packageId -ExecutableName "ffmpeg.exe"
$ffprobePath = Find-WinGetExecutable -PackageId $packageId -ExecutableName "ffprobe.exe"

$versionOutput = ((& $ffmpegPath -version 2>&1) -join "`n")
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg -version failed with exit code $LASTEXITCODE."
}
& $ffmpegPath -buildconf 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg -buildconf failed with exit code $LASTEXITCODE."
}
$encoderOutput = ((& $ffmpegPath -hide_banner -encoders 2>&1) -join "`n")
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg -encoders failed with exit code $LASTEXITCODE."
}
$filterOutput = ((& $ffmpegPath -hide_banner -filters 2>&1) -join "`n")
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg -filters failed with exit code $LASTEXITCODE."
}
$hwaccelOutput = @(& $ffmpegPath -hide_banner -hwaccels 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg -hwaccels failed with exit code $LASTEXITCODE."
}

$configurationLine = ($versionOutput -split "`n" | Where-Object { $_ -match '^configuration:' } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($configurationLine)) {
    throw "FFmpeg configuration line was not found."
}
$firstLine = ($versionOutput -split "`n" | Select-Object -First 1).Trim()
if ($firstLine -notmatch [regex]::Escape($expectedBuildIdentifier)) {
    throw "Installed FFmpeg build does not match the approved build identifier $expectedBuildIdentifier."
}

$requiredFilters = @(
    "trim", "atrim", "concat", "crop", "scale", "overlay", "subtitles",
    "xfade", "acrossfade", "loudnorm", "silencedetect", "drawtext"
)
$filterCapabilities = [ordered]@{}
foreach ($name in $requiredFilters) {
    $filterCapabilities[$name] = Test-Capability -Listing $filterOutput -Name $name
}

$encoderNames = @("aac", "h264_mf", "h264_nvenc", "hevc_mf", "hevc_nvenc", "libx264", "libx265")
$encoderCapabilities = [ordered]@{}
foreach ($name in $encoderNames) {
    $encoderCapabilities[$name] = Test-Capability -Listing $encoderOutput -Name $name
}

$hwaccels = @($hwaccelOutput |
    ForEach-Object { $_.ToString().Trim() } |
    Where-Object { $_ -and $_ -notmatch '^Hardware acceleration methods:' })

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    package = [ordered]@{
        id = $packageId
        version = $packageVersion
        releaseTag = "autobuild-2026-07-31-14-10"
        archiveFile = "ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-8.1.zip"
        archiveSha256 = $archiveSha256
        archiveSha256Provenance = "WinGet manifest and installer verification output; the downloaded archive was not retained or rehashed by this stage."
        archiveBytesRehashedByStage = $false
        variant = "win64-lgpl-static"
        source = "https://github.com/BtbN/FFmpeg-Builds"
    }
    executables = [ordered]@{
        ffmpeg = [ordered]@{
            fileName = "ffmpeg.exe"
            byteSize = [int64](Get-Item $ffmpegPath).Length
            sha256 = (Get-FileHash $ffmpegPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        ffprobe = [ordered]@{
            fileName = "ffprobe.exe"
            byteSize = [int64](Get-Item $ffprobePath).Length
            sha256 = (Get-FileHash $ffprobePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    version = [ordered]@{
        firstLine = $firstLine
        configuration = $configurationLine
    }
    licenseAssessment = [ordered]@{
        enablesGpl = $configurationLine -match '(?:^|\s)--enable-gpl(?:\s|$)'
        enablesNonFree = $configurationLine -match '(?:^|\s)--enable-nonfree(?:\s|$)'
        expectedVariant = "LGPL-2.1-or-later base; dependencies require separate verification"
    }
    capabilities = [ordered]@{
        filters = $filterCapabilities
        encoders = $encoderCapabilities
        hardwareAccelerationMethodsListedByFfmpeg = $hwaccels
        runtimeEncodeSuccessMeasured = $false
    }
    samplePathConsumed = $false
} | ConvertTo-Json -Depth 8
