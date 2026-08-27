#Requires -Version 7.0

<#
.SYNOPSIS
Renders handwritten 16:9 and 9:16 compositions and verifies their source mappings.

.DESCRIPTION
Exercises trim, concat, exact-ratio crop, neutral subtitle burn-in, and audio gain using the pinned
LGPL FFmpeg build. Source-derived renders, frames, PTS lists, and diagnostics remain in an ignored,
ACL-protected local directory. The public payload contains hashes and objective aggregate evidence.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$renderTimeoutSeconds = 1800
$verifyTimeoutSeconds = 600

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StringSha256 {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))
    ).ToLowerInvariant()
}

function Get-FileDescriptor {
    param([Parameter(Mandatory)][string]$Path)

    $file = Get-Item -LiteralPath $Path
    return [pscustomobject][ordered]@{
        fileName = $file.Name
        byteSize = [int64]$file.Length
        sha256 = Get-FileSha256 -Path $file.FullName
    }
}

function Write-LocalTextAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $staging = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($staging, $Value, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $staging -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
    }
}

function Get-LocalEvidenceAclEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for manual-render evidence protection."
    }
    $allowedSids = @(
        $currentSid,
        [Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    )
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = Get-Acl -LiteralPath $Path
    $allowedValues = @($allowedSids | ForEach-Object { $_.Value })
    $unexpected = @($acl.Access | Where-Object {
        $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedValues -or
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl -or
        ($_.InheritanceFlags -band $inheritance) -ne $inheritance
    })
    $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected -or $ownerSid -ne $currentSid.Value -or
        $unexpected.Count -ne 0 -or @($acl.Access).Count -ne 3) {
        throw "The manual-render evidence directory ACL did not match the approved local principals."
    }
    return [pscustomobject][ordered]@{
        inheritanceProtected = $true
        approvedPrincipalCount = 3
        accessRuleCount = @($acl.Access).Count
        unexpectedAccessRuleCount = $unexpected.Count
        ownerIsCurrentPrincipal = $true
        approvedRulesGrantFullControl = $true
        principalIdentifiersIncludedInPayload = $false
    }
}

function Protect-LocalEvidenceDirectory {
    param([Parameter(Mandatory)][string]$Path)

    try {
        return Get-LocalEvidenceAclEvidence -Path $Path
    }
    catch {
        Write-Verbose "The manual-render evidence directory requires ACL normalization."
    }
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for manual-render evidence protection."
    }
    $allowedSids = @(
        $currentSid,
        [Security.Principal.SecurityIdentifier]::new("S-1-5-18"),
        [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    )
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($currentSid)
    foreach ($sid in $allowedSids) {
        [void]$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        ))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    return Get-LocalEvidenceAclEvidence -Path $Path
}

function Get-DescendantProcessId {
    param([Parameter(Mandatory)][int]$RootProcessId)

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
    $descendants = [Collections.Generic.HashSet[int]]::new()
    $queue = [Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootProcessId)
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        foreach ($child in $processes | Where-Object { [int]$_.ParentProcessId -eq $parent }) {
            $childId = [int]$child.ProcessId
            if ($descendants.Add($childId)) {
                $queue.Enqueue($childId)
            }
        }
    }
    return @($descendants)
}

function Invoke-ProcessTreeTermination {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($Process.Id)
    foreach ($processId in Get-DescendantProcessId -RootProcessId $Process.Id) {
        [void]$ids.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    foreach ($processId in $ids) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit(10000)) {
                throw "A timed-out manual-render process remained alive."
            }
        }
        finally {
            $observed.Dispose()
        }
    }
}

function Invoke-MeasuredProcess {
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
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
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
            Invoke-ProcessTreeTermination -Process $process
            throw "$([IO.Path]::GetFileName($FileName)) exceeded the manual-render timeout of $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        $stopwatch.Stop()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
            elapsedMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            Invoke-ProcessTreeTermination -Process $process
        }
        $process.Dispose()
    }
}

function ConvertFrom-LastJsonLine {
    param([Parameter(Mandatory)][string]$Value)

    $lines = @($Value -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        try {
            return $lines[$index] | ConvertFrom-Json -Depth 100 -ErrorAction Stop
        }
        catch {
            continue
        }
    }
    throw "Manual-render process output did not contain a JSON object."
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
    $approvedExecutables = @(Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName | Where-Object {
        (Get-FileSha256 -Path $_.FullName) -eq $ExpectedSha256
    })
    if ($approvedExecutables.Count -ne 1) {
        throw "Expected exactly one approved $ExecutableName, found $($approvedExecutables.Count)."
    }
    return $approvedExecutables[0].FullName
}

function Write-Diagnostic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][hashtable]$Replacements
    )

    $safe = [regex]::Replace($Value, '\x1B\[[0-9;?]*[ -/]*[@-~]', '')
    foreach ($source in @($Replacements.Keys | Sort-Object { ([string]$_).Length } -Descending)) {
        foreach ($variant in @([string]$source, ([string]$source).Replace('\', '/')) | Sort-Object -Unique) {
            $safe = [regex]::Replace(
                $safe,
                [regex]::Escape($variant),
                [Text.RegularExpressions.MatchEvaluator]{ param($match) [void]$match; [string]$Replacements[$source] },
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        }
    }
    if ($safe.Length -gt 65536) {
        $safe = $safe.Substring(0, 65536) + " [truncated]"
    }
    Write-LocalTextAtomically -Path $Path -Value $safe
}

function ConvertTo-InvariantSecond {
    param([Parameter(Mandatory)][object]$Value)
    $seconds = [decimal]$Value.value / [decimal]$Value.rate
    return $seconds.ToString("0.#########", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-RenderArgumentList {
    param(
        [Parameter(Mandatory)][object]$Composition,
        [Parameter(Mandatory)][object]$RenderProfile,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][object]$Manifest
    )

    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @("-nostdin", "-v", "error")) { $arguments.Add($argument) }
    foreach ($segment in @($Composition.segments)) {
        foreach ($argument in @(
            "-ss", (ConvertTo-InvariantSecond -Value $segment.sourceStart),
            "-t", (ConvertTo-InvariantSecond -Value $segment.sourceDuration),
            "-i", $SourcePath
        )) { $arguments.Add($argument) }
    }

    $filters = [Collections.Generic.List[string]]::new()
    $videoLabels = [Collections.Generic.List[string]]::new()
    $audioLabels = [Collections.Generic.List[string]]::new()
    $concatLabels = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt @($Composition.segments).Count; $index++) {
        $duration = ConvertTo-InvariantSecond -Value $Composition.segments[$index].sourceDuration
        if ($Composition.compositionId -eq "vertical-trim-center-crop") {
            $crop = $Composition.crop
            $videoFilter = "[$index`:v:0]trim=start=0:end=$duration,setpts=PTS-STARTPTS,crop=$($crop.width):$($crop.height):$($crop.x):$($crop.y),fps=$($RenderProfile.frameRate),format=$($RenderProfile.pixelFormat)[v$index]"
        }
        else {
            $videoFilter = "[$index`:v:0]trim=start=0:end=$duration,setpts=PTS-STARTPTS,scale=$($RenderProfile.width):$($RenderProfile.height):flags=lanczos,fps=$($RenderProfile.frameRate),format=$($RenderProfile.pixelFormat)[v$index]"
        }
        $filters.Add($videoFilter)
        $filters.Add("[$index`:a:0]atrim=start=0:end=$duration,asetpts=PTS-STARTPTS,aresample=48000[a$index]")
        $videoLabels.Add("[v$index]")
        $audioLabels.Add("[a$index]")
        $concatLabels.Add("[v$index]")
        $concatLabels.Add("[a$index]")
    }

    if (@($Composition.segments).Count -gt 1) {
        $filters.Add("$($concatLabels -join '')concat=n=$(@($Composition.segments).Count):v=1:a=1[vcat][acat]")
        $videoInput = "[vcat]"
        $audioInput = "[acat]"
    }
    else {
        $videoInput = $videoLabels[0]
        $audioInput = $audioLabels[0]
    }
    if ($Composition.subtitleEnabled -eq $true) {
        $filters.Add("${videoInput}subtitles=filename='$($Manifest.subtitle.relativePath)'[vout]")
    }
    else {
        $filters.Add("${videoInput}null[vout]")
    }
    $gain = ([decimal]$Composition.audioGain).ToString("0.#########", [Globalization.CultureInfo]::InvariantCulture)
    $filters.Add("${audioInput}volume=$gain[aout]")

    foreach ($argument in @(
        "-filter_complex", ($filters -join ";"),
        "-map", "[vout]", "-map", "[aout]",
        "-c:v", $Manifest.toolchain.videoEncoder,
        "-preset", $Manifest.toolchain.videoPreset,
        "-cq", ([string]$Manifest.toolchain.constantQuality),
        "-b:v", "0",
        "-c:a", $Manifest.toolchain.audioEncoder,
        "-b:a", $Manifest.toolchain.audioBitrate,
        "-ar", "48000", "-ac", "2",
        "-map_metadata", "-1", "-movflags", "+faststart",
        "-shortest", "-y", $OutputPath
    )) { $arguments.Add($argument) }
    return @($arguments)
}

function Get-ProbeRecord {
    param(
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $probe = Invoke-MeasuredProcess -FileName $FfprobePath -TimeoutSeconds 300 -Arguments @(
        "-v", "error", "-count_frames",
        "-show_entries", "format=duration,size:stream=index,codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,time_base,duration,sample_rate,channels,nb_read_frames",
        "-of", "json", $OutputPath
    )
    if ($probe.exitCode -ne 0) {
        throw "ffprobe could not inspect a manual-render output."
    }
    $value = $probe.stdout | ConvertFrom-Json -Depth 30
    $video = @($value.streams | Where-Object codec_type -eq "video")
    $audio = @($value.streams | Where-Object codec_type -eq "audio")
    if ($video.Count -ne 1 -or $audio.Count -ne 1) {
        throw "Manual-render output does not contain exactly one video and one audio stream."
    }
    return [pscustomobject][ordered]@{
        durationSeconds = [double]$value.format.duration
        byteSizeReported = [int64]$value.format.size
        video = [ordered]@{
            codecName = $video[0].codec_name
            profile = $video[0].profile
            width = [int]$video[0].width
            height = [int]$video[0].height
            pixelFormat = $video[0].pix_fmt
            frameRate = $video[0].r_frame_rate
            timeBase = $video[0].time_base
            durationSeconds = [double]$video[0].duration
            decodedFrameCount = [int]$video[0].nb_read_frames
        }
        audio = [ordered]@{
            codecName = $audio[0].codec_name
            sampleRate = [int]$audio[0].sample_rate
            channels = [int]$audio[0].channels
            timeBase = $audio[0].time_base
            durationSeconds = [double]$audio[0].duration
            decodedFrameCount = [int]$audio[0].nb_read_frames
        }
        audioVideoEndDifferenceSeconds = [math]::Round([math]::Abs([double]$video[0].duration - [double]$audio[0].duration), 9)
    }
}

function Get-DecodedStreamHash {
    param(
        [Parameter(Mandatory)][string]$FfmpegPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][ValidateSet("video", "audio")][string]$StreamType
    )

    $arguments = if ($StreamType -eq "video") {
        @("-nostdin", "-v", "error", "-i", $OutputPath, "-map", "0:v:0", "-f", "hash", "-hash", "sha256", "-")
    }
    else {
        @("-nostdin", "-v", "error", "-i", $OutputPath, "-map", "0:a:0", "-f", "hash", "-hash", "sha256", "-")
    }
    $result = Invoke-MeasuredProcess -FileName $FfmpegPath -TimeoutSeconds $verifyTimeoutSeconds -Arguments $arguments
    $match = [regex]::Match($result.stdout.Trim(), '(?i)^SHA256=(?<hash>[0-9a-f]{64})$')
    if ($result.exitCode -ne 0 -or -not $match.Success) {
        throw "Decoded $StreamType stream hash could not be computed."
    }
    return $match.Groups["hash"].Value.ToLowerInvariant()
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$manifestPath = Join-Path $repositoryRoot "benchmarks\fixtures\manual-render-manifest.json"
$subtitlePath = Join-Path $repositoryRoot "benchmarks\fixtures\manual-render-subtitles.ass"
$driverPath = Join-Path $PSScriptRoot "manual_render_benchmark.py"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$fixtureManifest = Get-Content (Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json") -Raw -Encoding utf8 | ConvertFrom-Json
$mediaCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\media-ingest.json"
$proxyCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\proxy-frame-map.json"
$encoderCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\encoder-matrix.json"
$ffmpegCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json"
$media = Get-Content $mediaCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$proxy = Get-Content $proxyCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$encoder = Get-Content $encoderCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$ffmpegCapabilities = Get-Content $ffmpegCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100

if (@($fixtureManifest.sourceAssets).Count -ne 1 -or
    (Get-FileSha256 -Path $resolvedSample) -ne $fixtureManifest.sourceAssets[0].sha256 -or
    $manifest.source.sha256 -ne $fixtureManifest.sourceAssets[0].sha256 -or
    [int64]$manifest.source.byteSize -ne [int64]$fixtureManifest.sourceAssets[0].byteSize -or
    $media.source.sha256 -ne $manifest.source.sha256 -or
    $proxy.source.sha256 -ne $manifest.source.sha256) {
    throw "ST-012 source evidence does not identify one immutable fixture."
}
if ($encoder.summary.highestVmafId -ne "h264_nvenc" -or
    $encoder.summary.deliveryPathAvailableWithoutGplEncoder -ne $true -or
    $ffmpegCapabilities.licenseAssessment.enablesGpl -ne $false -or
    $ffmpegCapabilities.capabilities.filters.trim -ne $true -or
    $ffmpegCapabilities.capabilities.filters.concat -ne $true -or
    $ffmpegCapabilities.capabilities.filters.crop -ne $true -or
    $ffmpegCapabilities.capabilities.filters.subtitles -ne $true) {
    throw "The approved FFmpeg evidence does not support the handwritten render operations."
}

$implementationSpecs = @(
    @{ Id = "benchmark-harness"; Path = (Join-Path $repositoryRoot "benchmarks\run-benchmarks.ps1") },
    @{ Id = "manual-render-wrapper"; Path = (Join-Path $repositoryRoot "benchmarks\stages\measure-manual-render.ps1") },
    @{ Id = "manual-render-driver"; Path = $driverPath },
    @{ Id = "benchmark-schema"; Path = (Join-Path $repositoryRoot "benchmarks\results\schema.json") },
    @{ Id = "manual-render-manifest"; Path = $manifestPath },
    @{ Id = "manual-render-subtitle"; Path = $subtitlePath }
)
$implementationFiles = @($implementationSpecs | ForEach-Object {
    $descriptor = Get-FileDescriptor -Path $_.Path
    [pscustomobject][ordered]@{
        id = $_.Id
        fileName = $descriptor.fileName
        byteSize = $descriptor.byteSize
        sha256 = $descriptor.sha256
    }
})
$gitPath = (Get-Command git.exe -ErrorAction Stop).Source
$gitHeadProcess = Invoke-MeasuredProcess -FileName $gitPath -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments @("rev-parse", "HEAD")
$gitStatusProcess = Invoke-MeasuredProcess -FileName $gitPath -TimeoutSeconds 60 -WorkingDirectory $repositoryRoot -Arguments (@("status", "--porcelain", "--") + @($implementationSpecs.Path))
if ($gitHeadProcess.exitCode -ne 0 -or $gitHeadProcess.stdout.Trim() -notmatch '^[0-9a-f]{40}$' -or $gitStatusProcess.exitCode -ne 0) {
    throw "Unable to record the ST-012 implementation state."
}

$ffmpegPath = Find-ApprovedExecutable -PackageId $ffmpegCapabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $ffmpegCapabilities.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $ffmpegCapabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $ffmpegCapabilities.executables.ffprobe.sha256
$filterListProcess = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 120 -Arguments @("-hide_banner", "-filters")
if ($filterListProcess.exitCode -ne 0 -or $filterListProcess.stdout -notmatch '(?m)^\s*[TSC\.]{2}\s+volume\s') {
    throw "The approved FFmpeg executable does not expose the required volume filter."
}
$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing."
}
$pythonPackageProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    "-c", "import cv2,json,numpy;print(json.dumps({'opencv':cv2.__version__,'numpy':numpy.__version__}))"
)
if ($pythonPackageProcess.exitCode -ne 0) {
    throw "Unable to inspect the manual-render Python runtime."
}
$pythonPackages = ConvertFrom-LastJsonLine -Value $pythonPackageProcess.stdout

$workRoot = Join-Path $repositoryRoot ("benchmarks\work\manual-render-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$script:manualRenderCompleted = $false
trap {
    $stageError = $_
    if (-not $script:manualRenderCompleted -and (Test-Path -LiteralPath $workRoot -PathType Container)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $stageError
}
$workAcl = Protect-LocalEvidenceDirectory -Path $workRoot
if (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -ne 0) {
    throw "The manual-render work directory was not empty immediately after ACL protection."
}
$diagnosticReplacements = @{
    $resolvedSample = "<SOURCE>"
    $repositoryRoot = "<WORKSPACE>"
    $workRoot = "<MANUAL_RENDER_WORK>"
    $ffmpegPath = "<FFMPEG>"
    $ffprobePath = "<FFPROBE>"
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $diagnosticReplacements[$env:USERPROFILE] = "<USERPROFILE>"
}

$manifestValidationProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    $driverPath, "validate-manifest", "--manifest", $manifestPath,
    "--source-sha256", $fixtureManifest.sourceAssets[0].sha256,
    "--source-byte-size", ([string]$fixtureManifest.sourceAssets[0].byteSize),
    "--ffmpeg-sha256", $ffmpegCapabilities.executables.ffmpeg.sha256,
    "--ffprobe-sha256", $ffmpegCapabilities.executables.ffprobe.sha256
)
if ($manifestValidationProcess.exitCode -ne 0) {
    throw "The handwritten ST-012 manifest failed validation."
}
$manifestValidation = ConvertFrom-LastJsonLine -Value $manifestValidationProcess.stdout
if ($manifestValidation.status -ne "succeeded" -or
    $manifestValidation.allRequiredOperationsDeclared -ne $true -or
    $manifestValidation.verticalCropExactNineBySixteen -ne $true -or
    $manifestValidation.sourceTextUsedBySubtitle -ne $false) {
    throw "The handwritten ST-012 manifest does not cover the required operations."
}

$sourcePtsPath = Join-Path $workRoot "source-video-packet-pts.csv"
$sourcePtsProcess = Invoke-MeasuredProcess -FileName $ffprobePath -TimeoutSeconds 900 -Arguments @(
    "-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts", "-of", "csv=p=0", $resolvedSample
)
if ($sourcePtsProcess.exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sourcePtsProcess.stdout)) {
    throw "Source video PTS collection failed."
}
Write-LocalTextAtomically -Path $sourcePtsPath -Value $sourcePtsProcess.stdout

$renderRecords = [Collections.Generic.List[object]]::new()
$firstOutputs = @{}
foreach ($composition in @($manifest.compositions)) {
    $renderProfile = $manifest.renderProfiles | Where-Object profileId -eq $composition.profileId | Select-Object -First 1
    if ($null -eq $renderProfile) {
        throw "Composition references a missing render profile."
    }
    for ($iteration = 1; $iteration -le [int]$manifest.reproducibility.renderCountPerComposition; $iteration++) {
        $outputPath = Join-Path $workRoot "$($composition.compositionId)-run$iteration.mp4"
        $arguments = Get-RenderArgumentList -Composition $composition -RenderProfile $renderProfile -SourcePath $resolvedSample -OutputPath $outputPath -Manifest $manifest
        $render = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds $renderTimeoutSeconds -WorkingDirectory (Split-Path -Parent $manifestPath) -Arguments $arguments
        $diagnosticPath = Join-Path $workRoot "$($composition.compositionId)-run$iteration.stderr.txt"
        Write-Diagnostic -Path $diagnosticPath -Value $render.stderr -Replacements $diagnosticReplacements
        if ($render.exitCode -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf) -or (Get-Item $outputPath).Length -le 0) {
            throw "A required manual-render output failed."
        }
        $probe = Get-ProbeRecord -FfprobePath $ffprobePath -OutputPath $outputPath
        $decode = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds $verifyTimeoutSeconds -Arguments @(
            "-nostdin", "-v", "error", "-xerror", "-i", $outputPath,
            "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "NUL"
        )
        if ($decode.exitCode -ne 0) {
            throw "A manual-render output failed full decode."
        }
        $expectedDuration = [double](@($composition.segments | ForEach-Object {
            [decimal]$_.outputDuration.value / [decimal]$_.outputDuration.rate
        }) | Measure-Object -Sum).Sum
        $frameRateParts = @($renderProfile.frameRate -split '/')
        if ($frameRateParts.Count -ne 2 -or [double]$frameRateParts[1] -le 0) {
            throw "Render profile frame rate is not a positive rational."
        }
        $expectedFrameCount = [int](@($composition.segments | ForEach-Object {
            $segmentDuration = [double]([decimal]$_.outputDuration.value / [decimal]$_.outputDuration.rate)
            [math]::Floor($segmentDuration * [double]$frameRateParts[0] / [double]$frameRateParts[1])
        }) | Measure-Object -Sum).Sum
        if ($probe.video.codecName -ne "h264" -or $probe.video.width -ne [int]$renderProfile.width -or
            $probe.video.height -ne [int]$renderProfile.height -or $probe.video.pixelFormat -ne $renderProfile.pixelFormat -or
            $probe.video.frameRate -ne $renderProfile.frameRate -or $probe.audio.codecName -ne "aac" -or
            $probe.audio.sampleRate -ne 48000 -or $probe.audio.channels -ne 2 -or
            [math]::Abs($probe.durationSeconds - $expectedDuration) -gt 0.1 -or
            [math]::Abs($probe.video.decodedFrameCount - $expectedFrameCount) -gt 1 -or
            $probe.audioVideoEndDifferenceSeconds -gt 0.1) {
            $safeProbe = [ordered]@{
                compositionId = $composition.compositionId
                iteration = $iteration
                videoCodec = $probe.video.codecName
                width = $probe.video.width
                height = $probe.video.height
                pixelFormat = $probe.video.pixelFormat
                frameRate = $probe.video.frameRate
                decodedFrameCount = $probe.video.decodedFrameCount
                expectedFrameCount = $expectedFrameCount
                audioCodec = $probe.audio.codecName
                audioSampleRate = $probe.audio.sampleRate
                audioChannels = $probe.audio.channels
                durationSeconds = $probe.durationSeconds
                expectedDurationSeconds = $expectedDuration
                audioVideoEndDifferenceSeconds = $probe.audioVideoEndDifferenceSeconds
            } | ConvertTo-Json -Compress
            throw "A manual-render output failed its technical profile or duration checks: $safeProbe"
        }
        $record = [pscustomobject][ordered]@{
            compositionId = $composition.compositionId
            profileId = $composition.profileId
            iteration = $iteration
            operations = @($composition.operations)
            output = Get-FileDescriptor -Path $outputPath
            process = [ordered]@{
                exitCode = $render.exitCode
                elapsedMilliseconds = $render.elapsedMilliseconds
            }
            probe = $probe
            fullDecode = [ordered]@{
                succeeded = $true
                exitCode = $decode.exitCode
                elapsedMilliseconds = $decode.elapsedMilliseconds
            }
            decodedVideoSha256 = Get-DecodedStreamHash -FfmpegPath $ffmpegPath -OutputPath $outputPath -StreamType video
            decodedAudioSha256 = Get-DecodedStreamHash -FfmpegPath $ffmpegPath -OutputPath $outputPath -StreamType audio
            diagnostic = Get-FileDescriptor -Path $diagnosticPath
            commandArgumentsIncludedInPayload = $false
        }
        $renderRecords.Add($record)
        if ($iteration -eq 1) {
            $firstOutputs[$composition.compositionId] = $outputPath
        }
    }
}

$reproducibility = @($manifest.compositions | ForEach-Object {
    $compositionId = $_.compositionId
    $records = @($renderRecords | Where-Object compositionId -eq $compositionId | Sort-Object iteration)
    [pscustomobject][ordered]@{
        compositionId = $compositionId
        runsCompared = $records.Count
        outputBytesIdentical = $records.Count -eq 2 -and $records[0].output.sha256 -eq $records[1].output.sha256
        decodedVideoIdentical = $records.Count -eq 2 -and $records[0].decodedVideoSha256 -eq $records[1].decodedVideoSha256
        decodedAudioIdentical = $records.Count -eq 2 -and $records[0].decodedAudioSha256 -eq $records[1].decodedAudioSha256
        profileAndDurationIdentical = $records.Count -eq 2 -and
            ($records[0].probe | ConvertTo-Json -Depth 10 -Compress) -eq ($records[1].probe | ConvertTo-Json -Depth 10 -Compress)
        sameMachineGpuDriverObservation = $true
        crossMachineOrDriverReproducibilityMeasured = $false
        byteIdentityRequiredForTaskCompletion = $false
    }
})

$visualAnalyses = [Collections.Generic.List[object]]::new()
foreach ($composition in @($manifest.compositions)) {
    $outputPath = $firstOutputs[$composition.compositionId]
    $outputPtsPath = Join-Path $workRoot "$($composition.compositionId)-output-pts.csv"
    $outputRecord = $renderRecords | Where-Object { $_.compositionId -eq $composition.compositionId -and $_.iteration -eq 1 } | Select-Object -First 1
    $outputPtsProcess = Invoke-MeasuredProcess -FileName $ffprobePath -TimeoutSeconds 300 -Arguments @(
        "-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts", "-of", "csv=p=0", $outputPath
    )
    if ($outputPtsProcess.exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($outputPtsProcess.stdout)) {
        throw "Output video PTS collection failed."
    }
    Write-LocalTextAtomically -Path $outputPtsPath -Value $outputPtsProcess.stdout
    $analysisProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 900 -WorkingDirectory $repositoryRoot -Arguments @(
        $driverPath, "analyze-visual", "--manifest", $manifestPath,
        "--composition-id", $composition.compositionId,
        "--source-video", $resolvedSample,
        "--output-video", $outputPath,
        "--source-pts", $sourcePtsPath,
        "--output-pts", $outputPtsPath,
        "--output-time-base", $outputRecord.probe.video.timeBase,
        "--ffmpeg", $ffmpegPath,
        "--maximum-source-frame-duration-seconds", ([double]$proxy.roundTrip.errorSecondsMax).ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    $analysisDiagnosticPath = Join-Path $workRoot "$($composition.compositionId)-visual-analysis.stderr.txt"
    Write-Diagnostic -Path $analysisDiagnosticPath -Value $analysisProcess.stderr -Replacements $diagnosticReplacements
    if ($analysisProcess.exitCode -ne 0) {
        throw "Manual-render visual and reverse-map analysis failed."
    }
    $analysis = ConvertFrom-LastJsonLine -Value $analysisProcess.stdout
    if ($analysis.status -ne "succeeded" -or
        $analysis.allSourceFrameSelectionErrorsWithinObservedMaximumDuration -ne $true -or
        $analysis.allOffsetControlsExceedMappedDifferences -ne $true -or
        ($composition.subtitleEnabled -eq $true -and $analysis.subtitleControl.activeDifferenceExceedsInactive -ne $true)) {
        throw "Manual-render reverse-map, visual control, or subtitle control did not pass."
    }
    $analysis | Add-Member -NotePropertyName diagnostic -NotePropertyValue (Get-FileDescriptor -Path $analysisDiagnosticPath)
    $visualAnalyses.Add($analysis)
}

$landscapeOutput = $firstOutputs["landscape-trim-concat-subtitle-audio"]
$audioProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 900 -WorkingDirectory $repositoryRoot -Arguments @(
    $driverPath, "analyze-audio", "--manifest", $manifestPath,
    "--composition-id", "landscape-trim-concat-subtitle-audio",
    "--source-video", $resolvedSample, "--output-video", $landscapeOutput,
    "--ffmpeg", $ffmpegPath
)
$audioDiagnosticPath = Join-Path $workRoot "audio-analysis.stderr.txt"
Write-Diagnostic -Path $audioDiagnosticPath -Value $audioProcess.stderr -Replacements $diagnosticReplacements
if ($audioProcess.exitCode -ne 0) {
    throw "Manual-render audio analysis failed."
}
$audioAnalysis = ConvertFrom-LastJsonLine -Value $audioProcess.stdout
if ($audioAnalysis.status -ne "succeeded" -or
    $audioAnalysis.gainMechanismWithinStage0Tolerance -ne $true -or
    $audioAnalysis.outputIsNotSilent -ne $true -or
    $audioAnalysis.toleranceIsProductionApproved -ne $false) {
    throw "Manual-render audio mechanism control did not pass."
}
$audioAnalysis | Add-Member -NotePropertyName diagnostic -NotePropertyValue (Get-FileDescriptor -Path $audioDiagnosticPath)

$workAcl = Get-LocalEvidenceAclEvidence -Path $workRoot
$payload = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    implementation = [ordered]@{
        gitHead = $gitHeadProcess.stdout.Trim()
        implementationFilesCleanAtRun = [string]::IsNullOrWhiteSpace($gitStatusProcess.stdout)
        files = $implementationFiles
    }
    upstream = [ordered]@{
        mediaIngest = [ordered]@{ fileName = "media-ingest.json"; sha256 = Get-FileSha256 -Path $mediaCanonicalPath; benchmarkRunId = $media.benchmarkRunId; benchmarkRunSha256 = $media.benchmarkRunSha256 }
        proxyFrameMap = [ordered]@{ fileName = "proxy-frame-map.json"; sha256 = Get-FileSha256 -Path $proxyCanonicalPath; benchmarkRunId = $proxy.benchmarkRunId; benchmarkRunSha256 = $proxy.benchmarkRunSha256 }
        encoderMatrix = [ordered]@{ fileName = "encoder-matrix.json"; sha256 = Get-FileSha256 -Path $encoderCanonicalPath; benchmarkRunId = $encoder.benchmarkRunId; benchmarkRunSha256 = $encoder.benchmarkRunSha256 }
        ffmpegCapabilities = [ordered]@{ fileName = "ffmpeg-capabilities.json"; sha256 = Get-FileSha256 -Path $ffmpegCanonicalPath; benchmarkRunId = $ffmpegCapabilities.benchmarkRunId; benchmarkRunSha256 = $ffmpegCapabilities.benchmarkRunSha256 }
    }
    manifest = [ordered]@{
        manifestId = $manifest.manifestId
        descriptor = Get-FileDescriptor -Path $manifestPath
        subtitle = Get-FileDescriptor -Path $subtitlePath
        validation = $manifestValidation
        rights = $manifest.rights
    }
    tooling = [ordered]@{
        ffmpegPackageId = $ffmpegCapabilities.package.id
        ffmpegSha256 = $ffmpegCapabilities.executables.ffmpeg.sha256
        ffprobeSha256 = $ffmpegCapabilities.executables.ffprobe.sha256
        videoEncoder = $manifest.toolchain.videoEncoder
        audioEncoder = $manifest.toolchain.audioEncoder
        pythonVersion = (& $pythonPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
        pythonPackages = $pythonPackages
    }
    sourceTiming = [ordered]@{
        authority = "observed-source-presentation-timestamps"
        timeBase = $media.streams[0].timeBase
        packetCount = $media.packetStatistics[0].packetCount
        maximumObservedFrameDurationSeconds = [double]$proxy.roundTrip.errorSecondsMax
        packetPtsArtifact = Get-FileDescriptor -Path $sourcePtsPath
        nominalFrameRateUsedForSourceMapping = $false
    }
    renders = @($renderRecords)
    reproducibility = $reproducibility
    visualAndReverseMapping = @($visualAnalyses)
    audioMechanism = $audioAnalysis
    assessment = [ordered]@{
        requiredOperationsExercised = $true
        landscapeAndVerticalOutputsProduced = $true
        allOutputsPassedFullDecode = @($renderRecords | Where-Object { $_.fullDecode.succeeded -ne $true }).Count -eq 0
        allReverseMapSamplesWithinObservedFrameDuration = @($visualAnalyses | Where-Object { $_.allSourceFrameSelectionErrorsWithinObservedMaximumDuration -ne $true }).Count -eq 0
        allVisualControlsSeparate = @($visualAnalyses | Where-Object { $_.allOffsetControlsExceedMappedDifferences -ne $true }).Count -eq 0
        subtitleMechanismPassed = @($visualAnalyses | Where-Object { $null -ne $_.subtitleControl -and $_.subtitleControl.activeDifferenceExceedsInactive -eq $true }).Count -eq 1
        subtitleSourceSpeechTraceabilityMeasured = $false
        contentLevelOriginRefTraceabilityMeasured = $false
        audioGainMechanismPassed = $audioAnalysis.gainMechanismWithinStage0Tolerance
        perceptualQualityMeasured = $false
        productionThresholdsApproved = $false
        finalStage0DecisionPending = $true
    }
    privacy = [ordered]@{
        localEvidenceAcl = $workAcl
        sourceDerivedVideoArtifactsPersistedOnlyInIgnoredWorkDirectory = $true
        sourcePixelsIncludedInPayload = $false
        sourceAudioSamplesIncludedInPayload = $false
        sourceTranscriptTextIncludedInPayload = $false
        neutralSubtitleTextIncludedInTrackedFixture = $true
        sourceMetadataTagsCopiedToOutputs = $false
        absoluteHostPathsIncludedInPayload = $false
        externalPublicationAllowed = $false
        commandArgumentsIncludedInPayload = $false
        workDirectoryWasEmptyAfterAclProtection = $true
    }
    samplePathConsumed = $true
}
$payloadJson = $payload | ConvertTo-Json -Depth 30
if ($payloadJson -match '[A-Za-z]:\\' -or $payloadJson -match '"(?:sourcePath|outputPath|command|stderr)"\s*:') {
    throw "Manual-render payload contains an absolute path or forbidden process field."
}
Write-LocalTextAtomically -Path (Join-Path $workRoot "stage-payload.json") -Value $payloadJson
Write-LocalTextAtomically -Path (Join-Path $workRoot "completed.json") -Value (([ordered]@{
    schemaVersion = "1.0.0"
    completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceSha256 = $manifest.source.sha256
    outputCount = $renderRecords.Count
}) | ConvertTo-Json -Compress)
$script:manualRenderCompleted = $true
$payloadJson
