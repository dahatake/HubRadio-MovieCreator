#Requires -Version 7.0

<#
.SYNOPSIS
Measures Windows Job Object behavior and an unsigned multi-executable MSIX package proof of concept.

.DESCRIPTION
The stage verifies kill-on-close, host-crash cleanup, active-process and process-memory limits,
then builds and unpacks one unsigned MSIX containing two first-party executables, FFmpeg/ffprobe,
a pinned local model, and a short codec smoke clip. Signing, installation, upgrade, and clean-machine
validation remain explicitly unmeasured because no signing identity or release machine is available.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SamplePath
)

$ErrorActionPreference = "Stop"
$processTimeoutSeconds = 900
$buildToolsVersion = "10.0.26100.1"
$buildToolsSource = "https://packagefeedproxy.microsoft.io/nuget/v3/index.json"
$modelUrl = "https://storage.googleapis.com/mediapipe-models/face_detector/blaze_face_short_range/float16/1/blaze_face_short_range.tflite"
$modelByteSize = 229746
$modelSha256 = "b4578f35940bf5a1a655214a1cce5cab13eba73c1297cd78e1a04c2380b0152f"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

function Get-DescendantProcessIds {
    param([Parameter(Mandatory)][int]$RootProcessId)

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
    $descendants = [Collections.Generic.HashSet[int]]::new()
    $parents = [Collections.Generic.Queue[int]]::new()
    $parents.Enqueue($RootProcessId)
    while ($parents.Count -gt 0) {
        $parent = $parents.Dequeue()
        foreach ($child in $processes | Where-Object { [int]$_.ParentProcessId -eq $parent }) {
            $childId = [int]$child.ProcessId
            if ($descendants.Add($childId)) {
                $parents.Enqueue($childId)
            }
        }
    }
    return @($descendants)
}

function Stop-ProcessTreeAndWait {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [ValidateRange(1000, 60000)][int]$WaitMilliseconds = 10000
    )

    $observedIds = [Collections.Generic.HashSet[int]]::new()
    [void]$observedIds.Add($Process.Id)
    foreach ($processId in Get-DescendantProcessIds -RootProcessId $Process.Id) {
        [void]$observedIds.Add($processId)
    }
    if (-not $Process.HasExited) {
        $Process.Kill($true)
    }
    $Process.WaitForExit()
    foreach ($processId in Get-DescendantProcessIds -RootProcessId $Process.Id) {
        [void]$observedIds.Add($processId)
    }

    $remaining = [Collections.Generic.List[int]]::new()
    foreach ($processId in $observedIds) {
        try {
            $observed = [Diagnostics.Process]::GetProcessById($processId)
        }
        catch [ArgumentException] {
            continue
        }
        try {
            if (-not $observed.HasExited -and -not $observed.WaitForExit($WaitMilliseconds)) {
                $remaining.Add($processId)
            }
        }
        finally {
            $observed.Dispose()
        }
    }
    if ($remaining.Count -gt 0) {
        throw "Timed-out process descendants did not exit: $($remaining.Count)."
    }
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
            Stop-ProcessTreeAndWait -Process $process
            throw "$([IO.Path]::GetFileName($FileName)) exceeded the operational safety timeout of $TimeoutSeconds seconds."
        }
        $drainTask = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        try {
            $drainTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        }
        catch [TimeoutException] {
            Stop-ProcessTreeAndWait -Process $process
            try {
                $drainTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            }
            catch [TimeoutException] {
                throw "$([IO.Path]::GetFileName($FileName)) left redirected streams open after process-tree cleanup."
            }
        }
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
            Stop-ProcessTreeAndWait -Process $process
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

function Get-VerifiedAsset {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][int64]$ExpectedByteSize,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $downloaded = $false
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
        $stagingPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $stagingPath -TimeoutSec 120
            if ([int64](Get-Item -LiteralPath $stagingPath).Length -ne $ExpectedByteSize -or
                (Get-FileHash -LiteralPath $stagingPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedSha256) {
                throw "Downloaded model failed byte-size or SHA-256 verification."
            }
            Move-Item -LiteralPath $stagingPath -Destination $Path
            $downloaded = $true
        }
        finally {
            Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
        }
    }

    $file = Get-Item -LiteralPath $Path
    $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([int64]$file.Length -ne $ExpectedByteSize -or $actualSha256 -ne $ExpectedSha256) {
        throw "Cached model failed byte-size or SHA-256 verification."
    }
    return [pscustomobject][ordered]@{
        path = $Path
        fileName = $file.Name
        byteSize = [int64]$file.Length
        sha256 = $actualSha256
        sourceUrl = $Url
        downloadedDuringThisRun = $downloaded
    }
}

function Wait-ForProcessAbsent {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )

    try {
        $process = [Diagnostics.Process]::GetProcessById($ProcessId)
    }
    catch [ArgumentException] {
        return $true
    }
    try {
        return $process.WaitForExit($TimeoutMilliseconds)
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-CrashCleanupTest {
    param(
        [Parameter(Mandatory)][string]$ProcessProbePath,
        [Parameter(Mandatory)][string]$WorkDirectory
    )

    $statePath = Join-Path $WorkDirectory "crash-host-state.json"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ProcessProbePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("crash-host")
    $startInfo.ArgumentList.Add($statePath)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Unable to start crash-host probe."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Crash-host probe exceeded its timeout."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
            throw "Crash-host probe did not persist its pre-crash state."
        }
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json
        $childExited = Wait-ForProcessAbsent -ProcessId ([int]$state.childProcessId) -TimeoutMilliseconds 10000
        $grandchildExited = Wait-ForProcessAbsent -ProcessId ([int]$state.grandchildProcessId) -TimeoutMilliseconds 10000
        if (-not $childExited) {
            Stop-Process -Id $state.childProcessId -Force -ErrorAction SilentlyContinue
        }
        if (-not $grandchildExited) {
            Stop-Process -Id $state.grandchildProcessId -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $state.temporaryDirectory) {
            Remove-Item -LiteralPath $state.temporaryDirectory -Recurse -Force
        }
        return [pscustomobject][ordered]@{
            hostExitCode = $process.ExitCode
            stateReady = [bool]$state.ready
            childInJobBeforeCrash = [bool]$state.childInJob
            grandchildInJobBeforeCrash = [bool]$state.grandchildInJob
            childExitedAfterHostCrash = $childExited
            grandchildExitedAfterHostCrash = $grandchildExited
            stdoutLength = $stdout.Length
            stderrContainsIntentionalMarker = $stderr.Contains("Intentional ST-014 crash-host termination.")
            passed = $process.ExitCode -ne 0 -and [bool]$state.ready -and [bool]$state.childInJob -and
                [bool]$state.grandchildInJob -and $childExited -and $grandchildExited
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
        Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
    }
}

function Copy-VerifiedFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Destination)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $destinationHash) {
        throw "Copied file failed SHA-256 verification: $([IO.Path]::GetFileName($Source))"
    }
}

$resolvedSample = (Resolve-Path -LiteralPath $SamplePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$probesRoot = Join-Path $repositoryRoot "benchmarks\probes"
$workRoot = Join-Path $repositoryRoot "benchmarks\work\process-and-package"
$publishHost = Join-Path $workRoot "publish-host"
$publishWorker = Join-Path $workRoot "publish-worker"
$packageLayout = Join-Path $workRoot "layout"
$unpackedLayout = Join-Path $workRoot "unpacked"
$packagePath = Join-Path $workRoot "HubRadio.Stage0.msix"
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

$capabilities = Get-Content (Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json") -Raw -Encoding utf8 | ConvertFrom-Json
$hardware = Get-Content (Join-Path $repositoryRoot "benchmarks\results\hardware-inventory.json") -Raw -Encoding utf8 | ConvertFrom-Json
$fixtureManifest = Get-Content (Join-Path $repositoryRoot "benchmarks\fixtures\fixture-manifest.json") -Raw -Encoding utf8 | ConvertFrom-Json
if (@($fixtureManifest.sourceAssets).Count -ne 1) {
    throw "ST-014 requires exactly one fixture source asset."
}
$fixtureSource = $fixtureManifest.sourceAssets[0]
if ((Get-FileHash -LiteralPath $resolvedSample -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixtureSource.sha256) {
    throw "The supplied source does not match the fixture manifest."
}
$ffmpegPath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $capabilities.executables.ffmpeg.sha256
$ffprobePath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $capabilities.executables.ffprobe.sha256
$dotnetPath = (Get-Command dotnet.exe -ErrorAction Stop).Source
$winAppCommand = Get-Command winapp.exe -ErrorAction SilentlyContinue
$winAppPackage = Get-AppxPackage -Name "winapp" -ErrorAction SilentlyContinue | Select-Object -First 1
$winAppBinary = if ($null -eq $winAppPackage) {
    $null
}
else {
    Get-ChildItem -LiteralPath $winAppPackage.InstallLocation -Recurse -File -Filter "winapp.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
}
$sdkVersionResult = Invoke-CapturedProcess -FileName $dotnetPath -TimeoutSeconds 60 -WorkingDirectory $probesRoot -Arguments @("--version")
$dotnetSdkVersion = $sdkVersionResult.stdout.Trim()
if ($sdkVersionResult.exitCode -ne 0 -or $dotnetSdkVersion -ne "10.0.400") {
    throw "The probe requires the exact .NET SDK selected by benchmarks/probes/global.json (10.0.400)."
}
$toolchainProject = Join-Path $repositoryRoot "benchmarks\probes\PackageToolchain\PackageToolchain.csproj"
$toolchainRestore = Invoke-CapturedProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $probesRoot -Arguments @(
    "restore", $toolchainProject, "--source", $buildToolsSource, "--locked-mode", "--nologo"
)
if ($toolchainRestore.exitCode -ne 0) {
    throw "Windows SDK BuildTools locked restore failed with exit code $($toolchainRestore.exitCode)."
}
$buildToolsRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\$buildToolsVersion"
$makeAppxBinary = Get-ChildItem -LiteralPath $buildToolsRoot -Recurse -File -Filter "makeappx.exe" -ErrorAction Stop |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]makeappx\.exe$' } |
    Select-Object -First 1
if ($null -eq $makeAppxBinary) {
    throw "The x64 MakeAppx binary was not restored from the pinned BuildTools package."
}
$toolchainLock = Get-Content (Join-Path $repositoryRoot "benchmarks\probes\PackageToolchain\packages.lock.json") -Raw -Encoding utf8 | ConvertFrom-Json
$toolchainFramework = $toolchainLock.dependencies.PSObject.Properties["net10.0"].Value
$buildToolsLockEntry = $toolchainFramework.PSObject.Properties["Microsoft.Windows.SDK.BuildTools"].Value

$probeProjects = @(
    (Join-Path $repositoryRoot "benchmarks\probes\ProcessProbe\ProcessProbe.csproj"),
    (Join-Path $repositoryRoot "benchmarks\probes\WorkerProbe\WorkerProbe.csproj")
)
foreach ($project in $probeProjects) {
    $restore = Invoke-CapturedProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $probesRoot -Arguments @(
        "restore", $project, "--source", $buildToolsSource, "--locked-mode", "--nologo"
    )
    if ($restore.exitCode -ne 0) {
        throw "Probe locked restore failed with exit code $($restore.exitCode): $([IO.Path]::GetFileName($project))"
    }
}

$hostPublish = Invoke-CapturedProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $probesRoot -Arguments @(
    "publish", (Join-Path $repositoryRoot "benchmarks\probes\ProcessProbe\ProcessProbe.csproj"),
    "-c", "Release", "-o", $publishHost, "--no-restore", "--nologo"
)
if ($hostPublish.exitCode -ne 0) {
    throw "ProcessProbe publish failed with exit code $($hostPublish.exitCode)."
}
$workerPublish = Invoke-CapturedProcess -FileName $dotnetPath -TimeoutSeconds $processTimeoutSeconds -WorkingDirectory $probesRoot -Arguments @(
    "publish", (Join-Path $repositoryRoot "benchmarks\probes\WorkerProbe\WorkerProbe.csproj"),
    "-c", "Release", "-o", $publishWorker, "--no-restore", "--nologo"
)
if ($workerPublish.exitCode -ne 0) {
    throw "WorkerProbe publish failed with exit code $($workerPublish.exitCode)."
}

$processProbePath = Join-Path $publishHost "HubRadio.ProcessProbe.exe"
$workerProbePath = Join-Path $publishWorker "HubRadio.WorkerProbe.exe"
$processProbeFile = Get-Item -LiteralPath $processProbePath
$workerProbeFile = Get-Item -LiteralPath $workerProbePath
$timeoutWork = Join-Path $workRoot "timeout-cleanup"
[IO.Directory]::CreateDirectory($timeoutWork) | Out-Null
$timeoutReadyPath = Join-Path $timeoutWork "child.pid"
$timeoutGoPath = Join-Path $timeoutWork "go"
$timeoutGrandchildPath = Join-Path $timeoutWork "grandchild.pid"
[IO.File]::WriteAllText($timeoutGoPath, "go", [Text.UTF8Encoding]::new($false))
$timeoutExceptionWasExpected = $false
try {
    Invoke-CapturedProcess -FileName $processProbePath -TimeoutSeconds 2 -Arguments @(
        "wait-child", $timeoutReadyPath, $timeoutGoPath, $timeoutGrandchildPath
    ) | Out-Null
}
catch {
    $timeoutExceptionWasExpected = $_.Exception.Message -like "*exceeded the operational safety timeout*"
}
$timeoutChildPid = if (Test-Path -LiteralPath $timeoutReadyPath -PathType Leaf) {
    [int](Get-Content -LiteralPath $timeoutReadyPath -Raw -Encoding utf8)
}
else { $null }
$timeoutGrandchildPid = if (Test-Path -LiteralPath $timeoutGrandchildPath -PathType Leaf) {
    [int](Get-Content -LiteralPath $timeoutGrandchildPath -Raw -Encoding utf8)
}
else { $null }
$timeoutChildExited = $null -ne $timeoutChildPid -and (Wait-ForProcessAbsent -ProcessId $timeoutChildPid -TimeoutMilliseconds 10000)
$timeoutGrandchildExited = $null -ne $timeoutGrandchildPid -and (Wait-ForProcessAbsent -ProcessId $timeoutGrandchildPid -TimeoutMilliseconds 10000)
$timeoutCleanup = [pscustomobject][ordered]@{
    timeoutSeconds = 2
    expectedTimeoutObserved = $timeoutExceptionWasExpected
    childPidRecorded = $null -ne $timeoutChildPid
    grandchildPidRecorded = $null -ne $timeoutGrandchildPid
    childExitedAfterTimeout = $timeoutChildExited
    grandchildExitedAfterTimeout = $timeoutGrandchildExited
    passed = $timeoutExceptionWasExpected -and $timeoutChildExited -and $timeoutGrandchildExited
}
if (-not $timeoutCleanup.passed) {
    if ($null -ne $timeoutChildPid) { Stop-Process -Id $timeoutChildPid -Force -ErrorAction SilentlyContinue }
    if ($null -ne $timeoutGrandchildPid) { Stop-Process -Id $timeoutGrandchildPid -Force -ErrorAction SilentlyContinue }
    throw "The timeout process-tree cleanup control did not pass."
}
Remove-Item -LiteralPath $timeoutWork -Recurse -Force
$selfTestProcess = Invoke-CapturedProcess -FileName $processProbePath -TimeoutSeconds 120 -Arguments @("self-test")
if ($selfTestProcess.exitCode -ne 0) {
    throw "Job Object self-test failed with exit code $($selfTestProcess.exitCode)."
}
$selfTest = $selfTestProcess.stdout | ConvertFrom-Json -ErrorAction Stop
$crashTest = Invoke-CrashCleanupTest -ProcessProbePath $processProbePath -WorkDirectory $workRoot
if (@(Get-Process -Name "HubRadio.ProcessProbe" -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "ProcessProbe descendants remain after Job Object tests."
}

$model = Get-VerifiedAsset -Path (Join-Path $repositoryRoot "benchmarks\models\blaze_face_short_range.tflite") `
    -Url $modelUrl -ExpectedByteSize $modelByteSize -ExpectedSha256 $modelSha256

$smokeMediaPath = Join-Path $workRoot "codec-smoke.mov"
$smokeMedia = Invoke-CapturedProcess -FileName $ffmpegPath -TimeoutSeconds 300 -Arguments @(
    "-v", "error", "-ss", "0", "-i", $resolvedSample, "-t", "2",
    "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-y", $smokeMediaPath
)
if ($smokeMedia.exitCode -ne 0 -or -not (Test-Path -LiteralPath $smokeMediaPath -PathType Leaf)) {
    throw "Unable to create the codec smoke clip."
}

$layoutFiles = [ordered]@{
    processProbe = "ProcessProbe\HubRadio.ProcessProbe.exe"
    workerProbe = "WorkerProbe\HubRadio.WorkerProbe.exe"
    ffmpeg = "Tools\ffmpeg.exe"
    ffprobe = "Tools\ffprobe.exe"
    model = "Models\blaze_face_short_range.tflite"
    media = "Fixtures\codec-smoke.mov"
    ffmpegEvidence = "Evidence\ffmpeg-capabilities.json"
    faceEvidence = "Evidence\face-detection.json"
}
Copy-VerifiedFile -Source $processProbePath -Destination (Join-Path $packageLayout $layoutFiles.processProbe)
Copy-VerifiedFile -Source $workerProbePath -Destination (Join-Path $packageLayout $layoutFiles.workerProbe)
Copy-VerifiedFile -Source $ffmpegPath -Destination (Join-Path $packageLayout $layoutFiles.ffmpeg)
Copy-VerifiedFile -Source $ffprobePath -Destination (Join-Path $packageLayout $layoutFiles.ffprobe)
Copy-VerifiedFile -Source $model.path -Destination (Join-Path $packageLayout $layoutFiles.model)
Copy-VerifiedFile -Source $smokeMediaPath -Destination (Join-Path $packageLayout $layoutFiles.media)
Copy-VerifiedFile -Source (Join-Path $repositoryRoot "benchmarks\results\ffmpeg-capabilities.json") -Destination (Join-Path $packageLayout $layoutFiles.ffmpegEvidence)
Copy-VerifiedFile -Source (Join-Path $repositoryRoot "benchmarks\results\face-detection.json") -Destination (Join-Path $packageLayout $layoutFiles.faceEvidence)

foreach ($asset in @(
    @{ Name = "Square44x44Logo.png"; Size = 44 },
    @{ Name = "Square150x150Logo.png"; Size = 150 },
    @{ Name = "StoreLogo.png"; Size = 50 }
)) {
    $assetPath = Join-Path $packageLayout "Assets\$($asset.Name)"
    $assetResult = Invoke-CapturedProcess -FileName $processProbePath -TimeoutSeconds 60 -Arguments @("write-png", $assetPath, "$($asset.Size)")
    if ($assetResult.exitCode -ne 0 -or -not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Unable to generate package asset $($asset.Name)."
    }
}

$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="HubRadio.Stage0.ProcessAndPackage" Publisher="CN=HubRadio Stage0" Version="1.0.0.0" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>HubRadio Stage 0 Process Probe</DisplayName>
    <PublisherDisplayName>HubRadio Stage 0</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="ProcessProbe" Executable="ProcessProbe\HubRadio.ProcessProbe.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="HubRadio Stage 0 Process Probe"
                          Description="Stage 0 package structure probe"
                          BackgroundColor="transparent"
                          Square44x44Logo="Assets\Square44x44Logo.png"
                          Square150x150Logo="Assets\Square150x150Logo.png" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
'@
[IO.File]::WriteAllText((Join-Path $packageLayout "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))

$pack = Invoke-CapturedProcess -FileName $makeAppxBinary.FullName -TimeoutSeconds $processTimeoutSeconds -Arguments @(
    "pack", "/d", $packageLayout, "/p", $packagePath, "/o"
)
if ($pack.exitCode -ne 0 -or -not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "MakeAppx pack failed with exit code $($pack.exitCode): $($pack.stderr)"
}
$unpack = Invoke-CapturedProcess -FileName $makeAppxBinary.FullName -TimeoutSeconds $processTimeoutSeconds -Arguments @(
    "unpack", "/p", $packagePath, "/d", $unpackedLayout, "/o"
)
if ($unpack.exitCode -ne 0 -or -not (Test-Path -LiteralPath $unpackedLayout -PathType Container)) {
    throw "MakeAppx unpack failed with exit code $($unpack.exitCode): $($unpack.stderr)"
}

$expectedRelativePaths = @($layoutFiles.Values) + @(
    "Assets\Square44x44Logo.png",
    "Assets\Square150x150Logo.png",
    "Assets\StoreLogo.png",
    "AppxManifest.xml"
)
$missingRelativePaths = @($expectedRelativePaths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $unpackedLayout $_) -PathType Leaf)
})
$selectedHashComparisons = @($layoutFiles.GetEnumerator() | ForEach-Object {
    $layoutPath = Join-Path $packageLayout $_.Value
    $unpackedPath = Join-Path $unpackedLayout $_.Value
    [pscustomobject][ordered]@{
        id = $_.Key
        matched = (Get-FileHash -LiteralPath $layoutPath -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $unpackedPath -Algorithm SHA256).Hash
    }
})

$unpackedProcessProbe = Join-Path $unpackedLayout $layoutFiles.processProbe
$unpackedWorkerProbe = Join-Path $unpackedLayout $layoutFiles.workerProbe
$packageVerificationProcess = Invoke-CapturedProcess -FileName $unpackedProcessProbe -TimeoutSeconds 300 -Arguments @(
    "verify-package",
    (Join-Path $unpackedLayout $layoutFiles.ffprobe),
    (Join-Path $unpackedLayout $layoutFiles.ffmpeg),
    (Join-Path $unpackedLayout $layoutFiles.media),
    (Join-Path $unpackedLayout $layoutFiles.model),
    $modelSha256
)
if ($packageVerificationProcess.exitCode -ne 0) {
    throw "Unpacked process probe verification failed with exit code $($packageVerificationProcess.exitCode)."
}
$packageVerification = $packageVerificationProcess.stdout | ConvertFrom-Json -ErrorAction Stop
$workerVerificationProcess = Invoke-CapturedProcess -FileName $unpackedWorkerProbe -TimeoutSeconds 120 -Arguments @(
    "verify-model", (Join-Path $unpackedLayout $layoutFiles.model), $modelSha256
)
if ($workerVerificationProcess.exitCode -ne 0) {
    throw "Unpacked worker probe verification failed with exit code $($workerVerificationProcess.exitCode)."
}
$workerVerification = $workerVerificationProcess.stdout | ConvertFrom-Json -ErrorAction Stop

$signatureFilePresent = Test-Path -LiteralPath (Join-Path $unpackedLayout "AppxSignature.p7x") -PathType Leaf
$authenticode = Get-AuthenticodeSignature -LiteralPath $packagePath
$packageFile = Get-Item -LiteralPath $packagePath
$unpackedExecutables = @(Get-ChildItem -LiteralPath $unpackedLayout -Recurse -File -Filter "*.exe")
$firstPartyExecutableCount = @($unpackedExecutables | Where-Object { $_.Name -in @("HubRadio.ProcessProbe.exe", "HubRadio.WorkerProbe.exe") }).Count
$packagePassed = $missingRelativePaths.Count -eq 0 -and
    @($selectedHashComparisons | Where-Object { -not $_.matched }).Count -eq 0 -and
    $firstPartyExecutableCount -eq 2 -and
    [bool]$packageVerification.passed -and [bool]$workerVerification.passed -and -not $signatureFilePresent

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    host = [ordered]@{
        hardwareBenchmarkRunId = $hardware.benchmarkRunId
        hardwareBenchmarkRunSha256 = $hardware.benchmarkRunSha256
        recordedOperatingSystemVersion = $hardware.operatingSystem.version
        currentOperatingSystemVersion = [Environment]::OSVersion.Version.ToString(3)
        currentProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        recordedHostIsWindowsInsider = $hardware.operatingSystem.caption -like "*Insider*"
    }
    tooling = [ordered]@{
        dotnetSdkVersion = $dotnetSdkVersion
        dotnet = [ordered]@{
            sdkVersion = $dotnetSdkVersion
            globalJsonSha256 = (Get-FileHash -LiteralPath (Join-Path $probesRoot "global.json") -Algorithm SHA256).Hash.ToLowerInvariant()
            processProbeLockSha256 = (Get-FileHash -LiteralPath (Join-Path $probesRoot "ProcessProbe\packages.lock.json") -Algorithm SHA256).Hash.ToLowerInvariant()
            workerProbeLockSha256 = (Get-FileHash -LiteralPath (Join-Path $probesRoot "WorkerProbe\packages.lock.json") -Algorithm SHA256).Hash.ToLowerInvariant()
            selfContained = $true
            publishSingleFile = $true
        }
        winAppCli = [ordered]@{
            available = $null -ne $winAppCommand -and $null -ne $winAppPackage -and $null -ne $winAppBinary
            version = if ($null -eq $winAppPackage) { $null } else { $winAppPackage.Version.ToString() }
            executableByteSize = if ($null -eq $winAppBinary) { $null } else { [int64]$winAppBinary.Length }
            executableSha256 = if ($null -eq $winAppBinary) { $null } else { (Get-FileHash -LiteralPath $winAppBinary.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            usedForPackaging = $false
            disposition = "optional-observation-only; implicit-buildtools-bootstrap-not-used"
        }
        makeAppx = [ordered]@{
            fileName = $makeAppxBinary.Name
            fileVersion = $makeAppxBinary.VersionInfo.FileVersion
            byteSize = [int64]$makeAppxBinary.Length
            sha256 = (Get-FileHash -LiteralPath $makeAppxBinary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            buildToolsPackageVersion = $buildToolsVersion
            buildToolsPackageContentHash = $buildToolsLockEntry.contentHash
            buildToolsRestoreSource = $buildToolsSource
            lockedRestoreSucceeded = $toolchainRestore.exitCode -eq 0
            invokedThroughWinAppCli = $false
        }
        ffmpegPackageId = $capabilities.package.id
        ffmpegSha256 = $capabilities.executables.ffmpeg.sha256
        ffprobeSha256 = $capabilities.executables.ffprobe.sha256
    }
    jobObject = [ordered]@{
        timeoutCleanup = $timeoutCleanup
        selfTest = $selfTest
        hostCrash = $crashTest
        candidateLimits = [ordered]@{
            killOnJobClose = $true
            processMemoryLimitBytes = [int64]$selfTest.processMemoryLimit.processMemoryLimitBytes
            activeProcessLimit = [int]$selfTest.activeProcessLimit.activeProcessLimit
            processMemoryLimitIsProductionApproved = $false
            activeProcessLimitDisposition = "not-adopted-on-this-host-because-rejected-assignment-required-explicit-caller-cleanup"
        }
        officialBehaviorReference = "https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject#remarks"
        orphanProcessCountAfterTests = @(Get-Process -Name "HubRadio.ProcessProbe" -ErrorAction SilentlyContinue).Count
    }
    model = [ordered]@{
        fileName = $model.fileName
        byteSize = $model.byteSize
        sha256 = $model.sha256
        sourceUrl = $model.sourceUrl
        downloadedDuringThisRun = $model.downloadedDuringThisRun
    }
    package = [ordered]@{
        fileName = $packageFile.Name
        byteSize = [int64]$packageFile.Length
        sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        identityName = "HubRadio.Stage0.ProcessAndPackage"
        identityVersion = "1.0.0.0"
        processorArchitecture = "x64"
        applicationEntryPoint = "Windows.FullTrustApplication"
        firstPartyExecutableCount = $firstPartyExecutableCount
        firstPartyExecutables = @(
            [ordered]@{
                fileName = $processProbeFile.Name
                byteSize = [int64]$processProbeFile.Length
                sha256 = (Get-FileHash -LiteralPath $processProbeFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            },
            [ordered]@{
                fileName = $workerProbeFile.Name
                byteSize = [int64]$workerProbeFile.Length
                sha256 = (Get-FileHash -LiteralPath $workerProbeFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        )
        totalExecutableCount = $unpackedExecutables.Count
        expectedFileCount = $expectedRelativePaths.Count
        missingExpectedFileCount = $missingRelativePaths.Count
        selectedHashComparisonCount = $selectedHashComparisons.Count
        selectedHashMismatchCount = @($selectedHashComparisons | Where-Object { -not $_.matched }).Count
        packElapsedMilliseconds = $pack.elapsedMilliseconds
        unpackElapsedMilliseconds = $unpack.elapsedMilliseconds
        signatureFilePresent = $signatureFilePresent
        authenticodeStatus = $authenticode.Status.ToString()
        unsignedByDesignForStage0 = $true
        packageBuildAndUnpackPassed = $packagePassed
    }
    unpackedVerification = [ordered]@{
        processProbe = $packageVerification
        workerProbe = $workerVerification
        executedFromUnpackedLayout = $true
    }
    appContainer = [ordered]@{
        packageApplicationEntryPoint = "Windows.FullTrustApplication"
        appContainerExecutionTested = $false
        candidateDisposition = "not-selected-by-this-spike"
        reason = "The Stage 0 package is unsigned and was not installed; AppContainer launch and managed-workspace access therefore remain unmeasured. The PoC uses a full-trust desktop entry point plus Job Object containment."
    }
    distributionValidation = [ordered]@{
        packageBuilt = $packagePassed
        packageInstalled = $false
        packageLaunchThroughIdentityTested = $false
        signingTested = $false
        upgradeTested = $false
        uninstallTested = $false
        cleanProductionWindowsTested = $false
        blockingInputs = @(
            "No signing subject or certificate was provided.",
            "No production-channel Windows 11 24H2 x64 clean test machine was provided.",
            "The fixture rights status is unconfirmed and external publication is not allowed."
        )
    }
    privacy = [ordered]@{
        sourceMediaIncludedInPackage = $true
        generatedCodecSmokeClipIncluded = $true
        originalSourceFileIncludedInPackage = $false
        sourceDerivedMediaIncludedInPackage = $true
        sourceDerivedMediaDescription = "A stream-copied opening excerpt used only for local HEVC/AAC package verification."
        fixtureRightsStatus = $fixtureSource.rights.status
        fixtureAllowedUse = $fixtureSource.rights.allowedUse
        fixtureExternalPublicationAllowed = [bool]$fixtureSource.rights.externalPublicationAllowed
        packageRestrictedToLocalTechnicalEvaluation = $true
        sourcePathIncludedInReport = $false
        packagePathsIncludedInReport = $false
    }
    samplePathConsumed = $true
} | ConvertTo-Json -Depth 12
