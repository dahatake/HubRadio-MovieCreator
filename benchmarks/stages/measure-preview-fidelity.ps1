#Requires -Version 7.0

<#
.SYNOPSIS
Compares actual WebView2 Canvas/SVG captures with an FFmpeg faithful interval render.

.DESCRIPTION
Consumes one tracked normalized LayoutSpec and neutral Cue in both render paths. A source-derived
static frame, interval render, browser captures, WebView2 profile, and diagnostics remain in an
ignored ACL-protected directory. The public payload contains hashes and objective differences only.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 600

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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
    $stagingPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($stagingPath, $Value, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $stagingPath -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-LocalEvidenceAclEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for preview-fidelity evidence protection."
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
        throw "The preview-fidelity evidence directory ACL did not match the approved local principals."
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
        Write-Verbose "The preview-fidelity evidence directory requires ACL normalization."
    }
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw "The current Windows SID could not be resolved for preview-fidelity evidence protection."
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

    $observedIds = [Collections.Generic.HashSet[int]]::new()
    [void]$observedIds.Add($Process.Id)
    foreach ($processId in @(Get-DescendantProcessId -RootProcessId $Process.Id)) {
        [void]$observedIds.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    foreach ($processId in $observedIds) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit(10000)) {
                throw "A timed-out preview-fidelity process remained alive."
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
            throw "$([IO.Path]::GetFileName($FileName)) exceeded the timeout of $TimeoutSeconds seconds."
        }
        $drainTask = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        $drainTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
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
    throw "Preview-fidelity process output did not contain a JSON object."
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
    return ([decimal]$Value.value / [decimal]$Value.rate).ToString(
        "0.#########",
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function Get-PixelValue {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][int]$Extent
    )

    $scaled = [int64]$Value.value * [int64]$Extent
    if ([int64]$Value.rate -le 0 -or $scaled % [int64]$Value.rate -ne 0) {
        throw "A normalized LayoutSpec value does not resolve to an integer pixel."
    }
    return [int]($scaled / [int64]$Value.rate)
}

function Get-PixelRectangle {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height
    )

    return [pscustomobject][ordered]@{
        x = Get-PixelValue -Value $Value.x -Extent $Width
        y = Get-PixelValue -Value $Value.y -Extent $Height
        width = Get-PixelValue -Value $Value.width -Extent $Width
        height = Get-PixelValue -Value $Value.height -Extent $Height
    }
}

function Get-FfmpegColor {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Role
    )

    $variableName = [string]$Manifest.palette.roles.$Role
    $value = [string]$Manifest.palette.resolvedLight.$variableName
    if ($value -notmatch '^#[0-9a-fA-F]{6}$') {
        throw "The resolved shared palette color is invalid."
    }
    return "0x$($value.Substring(1))"
}

function Get-FaithfulFilterGraph {
    param([Parameter(Mandatory)][object]$Manifest)

    $width = [int]$Manifest.profile.width
    $height = [int]$Manifest.profile.height
    $layout = $Manifest.layoutSpec
    $safeFrame = Get-PixelRectangle -Value $layout.safeFrame -Width $width -Height $height
    $topicPanel = Get-PixelRectangle -Value $layout.topicPanel -Width $width -Height $height
    $topicAccent = Get-PixelRectangle -Value $layout.topicAccent -Width $width -Height $height
    $cueBox = Get-PixelRectangle -Value $layout.cueBox -Width $width -Height $height
    $topicX = Get-PixelValue -Value $layout.topicLabel.x -Extent $width
    $topicCenterY = Get-PixelValue -Value $layout.topicLabel.centerY -Extent $height
    $cueStart = ConvertTo-InvariantSecond -Value $Manifest.cues[0].start
    $cueEnd = ConvertTo-InvariantSecond -Value $Manifest.cues[0].end
    $enable = "gte(t,$cueStart)*lt(t,$cueEnd)"
    foreach ($text in @([string]$layout.topicLabel.text, [string]$Manifest.cues[0].text)) {
        if ($text -notmatch '^[A-Z0-9 ]+$') {
            throw "Only the fixed neutral uppercase text contract may reach FFmpeg."
        }
    }
    $surface = Get-FfmpegColor -Manifest $Manifest -Role "surface"
    $textColor = Get-FfmpegColor -Manifest $Manifest -Role "text"
    $accent = Get-FfmpegColor -Manifest $Manifest -Role "accent"
    $warning = Get-FfmpegColor -Manifest $Manifest -Role "warning"
    return @(
        "scale=$width`:$height`:flags=lanczos",
        "format=$($Manifest.profile.pixelFormat)",
        "drawbox=x=$($safeFrame.x):y=$($safeFrame.y):w=$($safeFrame.width):h=$($safeFrame.height):color=$warning`:t=$($layout.safeFrame.strokeWidthPixels)",
        "drawbox=x=$($topicPanel.x):y=$($topicPanel.y):w=$($topicPanel.width):h=$($topicPanel.height):color=$surface`:t=fill",
        "drawbox=x=$($topicAccent.x):y=$($topicAccent.y):w=$($topicAccent.width):h=$($topicAccent.height):color=$accent`:t=fill",
        "drawtext=fontfile=$($layout.topicLabel.fontFileName):text='$($layout.topicLabel.text)':fontcolor=$textColor`:fontsize=$($layout.topicLabel.fontSizePixels):x=$topicX`:y=$topicCenterY-text_h/2",
        "drawbox=x=$($cueBox.x):y=$($cueBox.y):w=$($cueBox.width):h=$($cueBox.height):color=$surface`:t=fill:enable='$enable'",
        "drawbox=x=$($cueBox.x):y=$($cueBox.y):w=$($cueBox.width):h=$($cueBox.height):color=$accent`:t=$($layout.cueBox.borderWidthPixels):enable='$enable'",
        "drawtext=fontfile=$($layout.cueText.fontFileName):text='$($Manifest.cues[0].text)':fontcolor=$textColor`:fontsize=$($layout.cueText.fontSizePixels):x=$($cueBox.x)+($($cueBox.width)-text_w)/2:y=$($cueBox.y)+($($cueBox.height)-text_h)/2:enable='$enable'"
    ) -join ","
}

function Get-VideoProbe {
    param(
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$VideoPath
    )

    $probeProcess = Invoke-MeasuredProcess -FileName $FfprobePath -TimeoutSeconds 120 -Arguments @(
        "-v", "error", "-count_frames",
        "-show_entries", "format=duration,size:stream=codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,time_base,duration,nb_read_frames",
        "-of", "json", $VideoPath
    )
    if ($probeProcess.exitCode -ne 0) {
        throw "FFprobe could not inspect the faithful preview interval."
    }
    $probeValue = $probeProcess.stdout | ConvertFrom-Json -Depth 30
    $videoStreams = @($probeValue.streams | Where-Object codec_type -eq "video")
    if ($videoStreams.Count -ne 1) {
        throw "The faithful preview interval does not contain exactly one video stream."
    }
    return [pscustomobject][ordered]@{
        durationSeconds = [double]$probeValue.format.duration
        byteSizeReported = [int64]$probeValue.format.size
        video = [ordered]@{
            codecName = $videoStreams[0].codec_name
            profile = $videoStreams[0].profile
            width = [int]$videoStreams[0].width
            height = [int]$videoStreams[0].height
            pixelFormat = $videoStreams[0].pix_fmt
            frameRate = $videoStreams[0].r_frame_rate
            timeBase = $videoStreams[0].time_base
            durationSeconds = [double]$videoStreams[0].duration
            decodedFrameCount = [int]$videoStreams[0].nb_read_frames
        }
    }
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$fixtureManifestPath = Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json"
$layoutManifestPath = Join-Path $repositoryRoot "benchmarks\fixtures\preview-fidelity-layout.json"
$htmlPath = Join-Path $repositoryRoot "benchmarks\web-preview\index.html"
$driverPath = Join-Path $PSScriptRoot "preview_fidelity_benchmark.py"
$probeProjectRoot = Join-Path $repositoryRoot "benchmarks\probes\WebPreviewProbe"
$probeProjectPath = Join-Path $probeProjectRoot "WebPreviewProbe.csproj"
$probeProgramPath = Join-Path $probeProjectRoot "Program.cs"
$probeLockPath = Join-Path $probeProjectRoot "packages.lock.json"
$probeGlobalJsonPath = Join-Path $repositoryRoot "benchmarks\probes\global.json"
$schemaPath = Join-Path $repositoryRoot "benchmarks\results\schema.json"
$manualCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\manual-render.json"
$proxyCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\proxy-frame-map.json"
$ffmpegCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json"
$hardwareCanonicalPath = Join-Path $repositoryRoot "benchmarks\results\hardware-inventory.json"
$fixtureManifest = Get-Content $fixtureManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 30
$layoutManifest = Get-Content $layoutManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$manualCanonical = Get-Content $manualCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$proxyCanonical = Get-Content $proxyCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$ffmpegCanonical = Get-Content $ffmpegCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$hardwareCanonical = Get-Content $hardwareCanonicalPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100

if (@($fixtureManifest.sourceAssets).Count -ne 1 -or
    (Get-FileSha256 -Path $resolvedSample) -ne $fixtureManifest.sourceAssets[0].sha256 -or
    [int64](Get-Item -LiteralPath $resolvedSample).Length -ne [int64]$fixtureManifest.sourceAssets[0].byteSize -or
    $manualCanonical.fixture.sourceSha256 -ne $fixtureManifest.sourceAssets[0].sha256 -or
    $proxyCanonical.source.sha256 -ne $fixtureManifest.sourceAssets[0].sha256) {
    throw "ST-013 source and upstream evidence do not identify one immutable fixture."
}
$manualRawPath = Join-Path $repositoryRoot "benchmarks\results\runs\$($manualCanonical.benchmarkRunId).json"
$proxyRawPath = Join-Path $repositoryRoot "benchmarks\results\runs\$($proxyCanonical.benchmarkRunId).json"
if (-not (Test-Path -LiteralPath $manualRawPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $proxyRawPath -PathType Leaf) -or
    (Get-FileSha256 -Path $manualRawPath) -ne $manualCanonical.benchmarkRunSha256 -or
    (Get-FileSha256 -Path $proxyRawPath) -ne $proxyCanonical.benchmarkRunSha256) {
    throw "ST-013 cannot validate the immutable ST-004/ST-012 raw evidence chain."
}
if ($manualCanonical.configurationDisposition.requiredOperationsExercised -ne $true -or
    $manualCanonical.configurationDisposition.allOutputsPassedFullDecode -ne $true -or
    $manualCanonical.configurationDisposition.productionThresholdsApproved -ne $false -or
    $ffmpegCanonical.licenseAssessment.enablesGpl -ne $false -or
    $ffmpegCanonical.capabilities.encoders.h264_mf -ne $true) {
    throw "The approved upstream evidence does not support the ST-013 local render path."
}

$implementationSpecs = @(
    @{ Id = "benchmark-harness"; Path = (Join-Path $repositoryRoot "benchmarks\run-benchmarks.ps1") },
    @{ Id = "preview-fidelity-wrapper"; Path = (Join-Path $repositoryRoot "benchmarks\stages\measure-preview-fidelity.ps1") },
    @{ Id = "preview-fidelity-driver"; Path = $driverPath },
    @{ Id = "benchmark-schema"; Path = $schemaPath },
    @{ Id = "preview-layout-manifest"; Path = $layoutManifestPath },
    @{ Id = "web-preview-html"; Path = $htmlPath },
    @{ Id = "webview2-probe-project"; Path = $probeProjectPath },
    @{ Id = "webview2-probe-program"; Path = $probeProgramPath },
    @{ Id = "webview2-probe-lock"; Path = $probeLockPath },
    @{ Id = "probe-dotnet-sdk-selector"; Path = $probeGlobalJsonPath }
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
    throw "Unable to record the ST-013 implementation state."
}

$ffmpegPath = Find-ApprovedExecutable -PackageId $ffmpegCanonical.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $ffmpegCanonical.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $ffmpegCanonical.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $ffmpegCanonical.executables.ffprobe.sha256
$filterProcess = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds 120 -Arguments @("-hide_banner", "-filters")
if ($filterProcess.exitCode -ne 0 -or
    $filterProcess.stdout -notmatch '(?m)^\s*[TSC\.]{2}\s+drawbox\s' -or
    $filterProcess.stdout -notmatch '(?m)^\s*[TSC\.]{2}\s+drawtext\s') {
    throw "The approved FFmpeg executable does not expose drawbox and drawtext."
}
$pythonPath = Join-Path $repositoryRoot "benchmarks\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "The Stage 0 Python environment is missing."
}
$pythonPackageProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    "-c", "import cv2,json,numpy;print(json.dumps({'opencv':cv2.__version__,'numpy':numpy.__version__}))"
)
if ($pythonPackageProcess.exitCode -ne 0) {
    throw "Unable to inspect the preview-fidelity Python runtime."
}
$pythonPackages = ConvertFrom-LastJsonLine -Value $pythonPackageProcess.stdout

$workRoot = Join-Path $repositoryRoot ("benchmarks\work\preview-fidelity-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$script:previewFidelityCompleted = $false
trap {
    $stageError = $_
    if (-not $script:previewFidelityCompleted -and (Test-Path -LiteralPath $workRoot -PathType Container)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $stageError
}
$workAcl = Protect-LocalEvidenceDirectory -Path $workRoot
if (@(Get-ChildItem -LiteralPath $workRoot -Force).Count -ne 0) {
    throw "The preview-fidelity work directory was not empty immediately after ACL protection."
}
$diagnosticReplacements = @{
    $resolvedSample = "<SOURCE>"
    $repositoryRoot = "<WORKSPACE>"
    $workRoot = "<PREVIEW_FIDELITY_WORK>"
    $ffmpegPath = "<FFMPEG>"
    $ffprobePath = "<FFPROBE>"
}
$userProfilePath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($userProfilePath)) {
    throw "The Windows user profile path is unavailable for diagnostic redaction."
}
$diagnosticReplacements[$userProfilePath] = "<USERPROFILE>"
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE) -and $env:USERPROFILE -ne $userProfilePath) {
    $diagnosticReplacements[$env:USERPROFILE] = "<USERPROFILE>"
}

$manifestValidationProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds 120 -WorkingDirectory $repositoryRoot -Arguments @(
    $driverPath, "validate-manifest", "--manifest", $layoutManifestPath
)
if ($manifestValidationProcess.exitCode -ne 0) {
    throw "The shared ST-013 LayoutSpec/Cue manifest failed validation."
}
$manifestValidation = ConvertFrom-LastJsonLine -Value $manifestValidationProcess.stdout
if ($manifestValidation.status -ne "succeeded" -or
    $manifestValidation.productionThresholdsApproved -ne $false -or
    $manifestValidation.cueTextIncludedInValidationOutput -ne $false) {
    throw "The shared ST-013 contract overstates approval or leaks Cue text."
}

$dotnetPath = (Get-Command dotnet.exe -ErrorAction Stop).Source
$restoreProcess = Invoke-MeasuredProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory (Split-Path -Parent $probeProjectRoot) -Arguments @(
    "restore", $probeProjectPath, "--locked-mode"
)
$restoreDiagnosticPath = Join-Path $workRoot "webview2-probe-restore.txt"
Write-Diagnostic -Path $restoreDiagnosticPath -Value ($restoreProcess.stdout + $restoreProcess.stderr) -Replacements $diagnosticReplacements
if ($restoreProcess.exitCode -ne 0) {
    throw "The locked WebView2 probe restore failed."
}
$buildProcess = Invoke-MeasuredProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory (Split-Path -Parent $probeProjectRoot) -Arguments @(
    "build", $probeProjectPath, "-c", "Release", "--no-restore"
)
$buildDiagnosticPath = Join-Path $workRoot "webview2-probe-build.txt"
Write-Diagnostic -Path $buildDiagnosticPath -Value ($buildProcess.stdout + $buildProcess.stderr) -Replacements $diagnosticReplacements
if ($buildProcess.exitCode -ne 0) {
    throw "The WebView2 probe build failed."
}
$probeExecutablePath = Join-Path $probeProjectRoot "bin\Release\net10.0-windows10.0.26100.0\win-x64\HubRadio.WebPreviewProbe.exe"
if (-not (Test-Path -LiteralPath $probeExecutablePath -PathType Leaf)) {
    throw "The built WebView2 probe executable is missing."
}

$fontPath = Join-Path $env:WINDIR "Fonts\$($layoutManifest.layoutSpec.cueText.fontFileName)"
if (-not (Test-Path -LiteralPath $fontPath -PathType Leaf)) {
    throw "The declared Windows font file is missing."
}
$baseFramePath = Join-Path $workRoot "source-frame.png"
$sourceSecond = ConvertTo-InvariantSecond -Value $layoutManifest.sourceFrame.sourceTime
$baseFrameProcess = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds $processTimeoutSeconds -Arguments @(
    "-nostdin", "-v", "error", "-i", $resolvedSample, "-ss", $sourceSecond,
    "-frames:v", "1", "-vf", "scale=$($layoutManifest.profile.width):$($layoutManifest.profile.height):flags=lanczos",
    "-map_metadata", "-1", "-y", $baseFramePath
)
$baseDiagnosticPath = Join-Path $workRoot "source-frame.stderr.txt"
Write-Diagnostic -Path $baseDiagnosticPath -Value $baseFrameProcess.stderr -Replacements $diagnosticReplacements
if ($baseFrameProcess.exitCode -ne 0 -or -not (Test-Path -LiteralPath $baseFramePath -PathType Leaf)) {
    throw "The source-derived static preview frame could not be extracted."
}

$faithfulPath = Join-Path $workRoot "faithful-preview.mp4"
$filterGraph = Get-FaithfulFilterGraph -Manifest $layoutManifest
$faithfulProcess = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory (Split-Path -Parent $fontPath) -Arguments @(
    "-nostdin", "-v", "error", "-loop", "1", "-framerate", $layoutManifest.profile.frameRate,
    "-i", $baseFramePath, "-t", (ConvertTo-InvariantSecond -Value $layoutManifest.profile.duration),
    "-vf", $filterGraph, "-an", "-c:v", "h264_mf", "-b:v", "4M",
    "-pix_fmt", $layoutManifest.profile.pixelFormat, "-map_metadata", "-1", "-movflags", "+faststart", "-y", $faithfulPath
)
$faithfulDiagnosticPath = Join-Path $workRoot "faithful-preview.stderr.txt"
Write-Diagnostic -Path $faithfulDiagnosticPath -Value $faithfulProcess.stderr -Replacements $diagnosticReplacements
if ($faithfulProcess.exitCode -ne 0 -or -not (Test-Path -LiteralPath $faithfulPath -PathType Leaf)) {
    throw "The FFmpeg faithful preview interval render failed."
}
$faithfulProbe = Get-VideoProbe -FfprobePath $ffprobePath -VideoPath $faithfulPath
$fullDecodeProcess = Invoke-MeasuredProcess -FileName $ffmpegPath -TimeoutSeconds $processTimeoutSeconds -Arguments @(
    "-nostdin", "-v", "error", "-xerror", "-i", $faithfulPath, "-map", "0:v:0", "-f", "null", "NUL"
)
if ($fullDecodeProcess.exitCode -ne 0 -or
    $faithfulProbe.video.codecName -ne "h264" -or
    $faithfulProbe.video.width -ne [int]$layoutManifest.profile.width -or
    $faithfulProbe.video.height -ne [int]$layoutManifest.profile.height -or
    $faithfulProbe.video.pixelFormat -ne $layoutManifest.profile.pixelFormat -or
    $faithfulProbe.video.frameRate -ne $layoutManifest.profile.frameRate -or
    [math]::Abs($faithfulProbe.durationSeconds - 2.0) -gt 0.1 -or
    $faithfulProbe.video.decodedFrameCount -lt 59 -or $faithfulProbe.video.decodedFrameCount -gt 60) {
    throw "The FFmpeg faithful preview interval failed its technical profile or full decode."
}

$captureDirectory = Join-Path $workRoot "captures"
[IO.Directory]::CreateDirectory($captureDirectory) | Out-Null
$webViewProcess = Invoke-MeasuredProcess -FileName $probeExecutablePath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -Arguments @(
    "capture", $htmlPath, $layoutManifestPath, $baseFramePath, $captureDirectory
)
$webViewDiagnosticPath = Join-Path $workRoot "webview2-capture.stderr.txt"
Write-Diagnostic -Path $webViewDiagnosticPath -Value $webViewProcess.stderr -Replacements $diagnosticReplacements
if ($webViewProcess.exitCode -ne 0) {
    throw "The actual WebView2 Canvas/SVG capture failed."
}
$webViewReport = ConvertFrom-LastJsonLine -Value $webViewProcess.stdout
if ($webViewReport.actualWebView2Executed -ne $true -or
    $webViewReport.captureApi -ne "CoreWebView2.CapturePreviewAsync" -or
    [int]$webViewReport.captureCount -ne 6 -or
    $webViewReport.sourcePixelsTransferredToLocalWebView2 -ne $true -or
    $webViewReport.browserProcessExitObservedBeforeHostResult -ne $true -or
    $webViewReport.userDataDirectoryRemoved -ne $true -or
    $webViewReport.sourcePixelsIncludedInResult -ne $false) {
    throw "The WebView2 probe did not return the complete bounded capture evidence."
}
$webViewReportPath = Join-Path $captureDirectory "webview2-report.json"
Write-LocalTextAtomically -Path $webViewReportPath -Value ($webViewReport | ConvertTo-Json -Depth 30)
$webViewUserDataPath = Join-Path $captureDirectory "webview2-user-data"
if (Test-Path -LiteralPath $webViewUserDataPath) {
    throw "The probe reported WebView2 cleanup, but its user-data directory still exists."
}

$analysisProcess = Invoke-MeasuredProcess -FileName $pythonPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $repositoryRoot -Arguments @(
    $driverPath, "analyze", "--manifest", $layoutManifestPath,
    "--base-image", $baseFramePath, "--faithful-video", $faithfulPath,
    "--webview-report", $webViewReportPath, "--ffmpeg", $ffmpegPath,
    "--ffprobe", $ffprobePath, "--work-directory", $workRoot
)
$analysisDiagnosticPath = Join-Path $workRoot "preview-comparison.stderr.txt"
Write-Diagnostic -Path $analysisDiagnosticPath -Value $analysisProcess.stderr -Replacements $diagnosticReplacements
if ($analysisProcess.exitCode -ne 0) {
    throw "The WebView2-to-faithful preview comparison failed."
}
$analysis = ConvertFrom-LastJsonLine -Value $analysisProcess.stdout
if ($analysis.status -ne "succeeded" -or
    $analysis.summary.sharedLayoutSpecAndCueConsumedByAllRenderers -ne $true -or
    $analysis.summary.webView2VersusFaithfulDifferenceRecorded -ne $true -or
    $analysis.summary.canvasVersusSvgDifferenceRecorded -ne $true -or
    $analysis.summary.allWebView2CueStatesMatchSharedCue -ne $true -or
    $analysis.summary.allRendererCueControlsSeparateFromOutsideRegion -ne $true -or
    $analysis.summary.perceptualQualityMeasured -ne $false -or
    $analysis.summary.productionThresholdsApproved -ne $false) {
    throw "The preview comparison did not record the required bounded mechanism evidence."
}

$runtimeVersion = [string]$webViewReport.runtimeVersion
$runtimeExecutablePath = Join-Path "${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application" "$runtimeVersion\msedgewebview2.exe"
if (-not (Test-Path -LiteralPath $runtimeExecutablePath -PathType Leaf)) {
    throw "The executed WebView2 runtime binary could not be identified by version."
}
$packageLock = Get-Content $probeLockPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 30
$packageRecord = $packageLock.dependencies.'net10.0-windows10.0.26100'.'Microsoft.Web.WebView2'
if ($packageRecord.resolved -ne "1.0.4129.50" -or [string]::IsNullOrWhiteSpace([string]$packageRecord.contentHash)) {
    throw "The WebView2 SDK lock does not identify the expected package."
}
$nugetPackagePath = Join-Path $userProfilePath ".nuget\packages\microsoft.web.webview2\1.0.4129.50\microsoft.web.webview2.1.0.4129.50.nupkg"
if (-not (Test-Path -LiteralPath $nugetPackagePath -PathType Leaf)) {
    throw "The locked WebView2 SDK package archive is missing from the local package cache."
}

$workAcl = Get-LocalEvidenceAclEvidence -Path $workRoot
$payload = [ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        hardwareBenchmarkRunId = $hardwareCanonical.benchmarkRunId
        hardwareBenchmarkRunSha256 = $hardwareCanonical.benchmarkRunSha256
        recordedOperatingSystemVersion = $hardwareCanonical.operatingSystem.version
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    implementation = [ordered]@{
        gitHead = $gitHeadProcess.stdout.Trim()
        implementationFilesCleanAtRun = [string]::IsNullOrWhiteSpace($gitStatusProcess.stdout)
        files = $implementationFiles
    }
    upstream = [ordered]@{
        manualRender = [ordered]@{ fileName = "manual-render.json"; sha256 = Get-FileSha256 -Path $manualCanonicalPath; benchmarkRunId = $manualCanonical.benchmarkRunId; benchmarkRunSha256 = $manualCanonical.benchmarkRunSha256 }
        proxyFrameMap = [ordered]@{ fileName = "proxy-frame-map.json"; sha256 = Get-FileSha256 -Path $proxyCanonicalPath; benchmarkRunId = $proxyCanonical.benchmarkRunId; benchmarkRunSha256 = $proxyCanonical.benchmarkRunSha256 }
        ffmpegCapabilities = [ordered]@{ fileName = "ffmpeg-capabilities.json"; sha256 = Get-FileSha256 -Path $ffmpegCanonicalPath; benchmarkRunId = $ffmpegCanonical.benchmarkRunId; benchmarkRunSha256 = $ffmpegCanonical.benchmarkRunSha256 }
    }
    contract = [ordered]@{
        manifestId = $layoutManifest.manifestId
        layoutSpecId = $layoutManifest.layoutSpec.layoutSpecId
        manifest = Get-FileDescriptor -Path $layoutManifestPath
        webPreviewHtml = Get-FileDescriptor -Path $htmlPath
        validation = $manifestValidation
        normalizedCoordinatesResolveToExactIntegerPixels = $true
        cueTextSourceTranscriptDerived = $false
        cueTextIncludedInPayload = $false
        rights = $layoutManifest.rights
    }
    tooling = [ordered]@{
        ffmpegPackageId = $ffmpegCanonical.package.id
        ffmpegSha256 = $ffmpegCanonical.executables.ffmpeg.sha256
        ffprobeSha256 = $ffmpegCanonical.executables.ffprobe.sha256
        videoEncoder = "h264_mf"
        pythonVersion = (& $pythonPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
        pythonPackages = $pythonPackages
        dotnetSdkVersion = (& $dotnetPath --version).Trim()
        webView2 = [ordered]@{
            sdkPackageVersion = $packageRecord.resolved
            sdkPackageContentHash = $packageRecord.contentHash
            sdkPackage = Get-FileDescriptor -Path $nugetPackagePath
            runtimeVersion = $webViewReport.runtimeVersion
            environmentBrowserVersion = $webViewReport.environmentBrowserVersion
            runtimeExecutable = Get-FileDescriptor -Path $runtimeExecutablePath
            evergreenRuntimeUsed = $true
            runtimeBundledByBenchmark = $false
        }
        windowsFont = [ordered]@{
            family = $layoutManifest.layoutSpec.cueText.fontFamily
            file = Get-FileDescriptor -Path $fontPath
            bundledByBenchmark = $false
        }
    }
    sourceFrame = [ordered]@{
        requestedSourceTime = $layoutManifest.sourceFrame.sourceTime
        selectionRule = $layoutManifest.sourceFrame.selectionRule
        exactSourceFrameSelectionAccuracyMeasured = $false
        transform = $layoutManifest.sourceFrame.transform
        artifact = Get-FileDescriptor -Path $baseFramePath
        process = [ordered]@{ exitCode = $baseFrameProcess.exitCode; elapsedMilliseconds = $baseFrameProcess.elapsedMilliseconds }
        sourcePixelsIncludedInPayload = $false
    }
    faithfulInterval = [ordered]@{
        renderer = "ffmpeg"
        staticSourceFrameInterval = $true
        output = Get-FileDescriptor -Path $faithfulPath
        process = [ordered]@{ exitCode = $faithfulProcess.exitCode; elapsedMilliseconds = $faithfulProcess.elapsedMilliseconds }
        probe = $faithfulProbe
        fullDecode = [ordered]@{ succeeded = $true; exitCode = $fullDecodeProcess.exitCode; elapsedMilliseconds = $fullDecodeProcess.elapsedMilliseconds }
        diagnostic = Get-FileDescriptor -Path $faithfulDiagnosticPath
        commandArgumentsIncludedInPayload = $false
    }
    webView2Capture = [ordered]@{
        actualWebView2Executed = $true
        captureApi = $webViewReport.captureApi
        runtimeVersion = $webViewReport.runtimeVersion
        sdkAssemblyVersion = $webViewReport.sdkAssemblyVersion
        hostDpiScale = $webViewReport.hostDpiScale
        devicePixelRatios = @($analysis.webView2.devicePixelRatios)
        cssViewports = @($analysis.webView2.cssViewports)
        captureCount = $webViewReport.captureCount
        captures = @($webViewReport.captures)
        allCaptureDimensionsMatchProfile = $analysis.webView2.allCaptureDimensionsMatchProfile
        allRequestedFontsAvailable = $analysis.webView2.allRequestedFontsAvailable
        externalNavigationAllowed = $false
        dynamicDataSentViaPostWebMessageAsJson = $true
        sourcePixelsTransferredToLocalWebView2 = $true
        externalSubresourceAndConnectAllowedByDocument = $false
        browserProcessExitObservedBeforeHostResult = $true
        userDataDirectoryRemoved = $true
        process = [ordered]@{ exitCode = $webViewProcess.exitCode; elapsedMilliseconds = $webViewProcess.elapsedMilliseconds }
        diagnostic = Get-FileDescriptor -Path $webViewDiagnosticPath
        sourcePixelsIncludedInPayload = $false
    }
    comparison = $analysis
    assessment = [ordered]@{
        actualWebView2CanvasAndSvgCaptured = $true
        faithfulIntervalRenderedAndDecoded = $true
        sharedLayoutSpecAndCueConsumed = $analysis.summary.sharedLayoutSpecAndCueConsumedByAllRenderers
        geometryAndTimingEvidenceRecorded = $true
        webView2VersusFaithfulDifferencesRecorded = $analysis.summary.webView2VersusFaithfulDifferenceRecorded
        canvasVersusSvgDifferencesRecorded = $analysis.summary.canvasVersusSvgDifferenceRecorded
        cueMechanismControlsPassed = $analysis.summary.allRendererCueControlsSeparateFromOutsideRegion
        exactPixelEqualityRequired = $false
        exactPixelEquivalenceClaimed = $false
        perceptualQualityMeasured = $false
        movingVideoPlaybackMeasured = $false
        audioPreviewMeasured = $false
        crossDpiScaleReproducibilityMeasured = $false
        crossRuntimeGpuDriverReproducibilityMeasured = $false
        faithfulPreviewProductionAcceptanceMeasured = $false
        productionThresholdsApproved = $false
        finalStage0DecisionPending = $true
    }
    privacy = [ordered]@{
        localEvidenceAcl = $workAcl
        sourceDerivedArtifactsPersistedOnlyInIgnoredWorkDirectory = $true
        sourcePixelsIncludedInPayload = $false
        sourceAudioSamplesIncludedInPayload = $false
        sourceTranscriptTextIncludedInPayload = $false
        neutralCueTextIncludedInTrackedFixture = $true
        neutralCueTextIncludedInPayload = $false
        sourcePixelsTransferredOnlyToLocalWebView2 = $true
        webView2UserDataRemovedAfterCapture = $true
        absoluteHostPathsIncludedInPayload = $false
        externalPublicationAllowed = $false
        commandArgumentsIncludedInPayload = $false
        workDirectoryWasEmptyAfterAclProtection = $true
    }
    samplePathConsumed = $true
}
$payloadJson = $payload | ConvertTo-Json -Depth 40
if ($payloadJson -match '[A-Za-z]:\\' -or
    $payloadJson -match '"(?:sourcePath|outputPath|command|stderr|baseImageDataUrl)"\s*:') {
    throw "Preview-fidelity payload contains an absolute path, pixel data, or forbidden process field."
}
Write-LocalTextAtomically -Path (Join-Path $workRoot "stage-payload.json") -Value $payloadJson
Write-LocalTextAtomically -Path (Join-Path $workRoot "completed.json") -Value (([ordered]@{
    schemaVersion = "1.0.0"
    completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceSha256 = $fixtureManifest.sourceAssets[0].sha256
    captureCount = $webViewReport.captureCount
    comparisonCount = $analysis.summary.comparisonCount
}) | ConvertTo-Json -Compress)
$script:previewFidelityCompleted = $true
$payloadJson