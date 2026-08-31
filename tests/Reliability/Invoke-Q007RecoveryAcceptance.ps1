#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or
    $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$workRoot = Join-Path $repoRoot 'benchmarks\work\q007-recovery-acceptance'
$publishRoot = Join-Path $workRoot 'publish'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$node = Join-Path $env:ProgramFiles 'nodejs\node.exe'
$npmCli = Join-Path $env:ProgramFiles 'nodejs\node_modules\npm\bin\npm-cli.js'
$wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
$webRoot = Join-Path $repoRoot 'src\HubRadio.Web'
$reliabilityProject = Join-Path $PSScriptRoot 'HubRadio.Q007.ReliabilityProbe\HubRadio.Q007.ReliabilityProbe.csproj'
$jobRecoveryProject = Join-Path $PSScriptRoot 'HubRadio.Q007.JobRecoveryProbe\HubRadio.Q007.JobRecoveryProbe.csproj'
$processProbeProject = Join-Path $repoRoot 'benchmarks\probes\ProcessProbe\ProcessProbe.csproj'
$linuxNoSpaceLeftOnDeviceErrorCode = 28

function Assert-Condition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [string]$WorkingDirectory = $repoRoot,

        [ValidateRange(1, 1800)]
        [int]$TimeoutSeconds = 300
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw 'A captured process could not be started.'
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $operation = if ($ArgumentList.Count -gt 0) {
                [IO.Path]::GetFileName($ArgumentList[0])
            }
            else {
                [IO.Path]::GetFileName($FileName)
            }
            throw "The captured '$operation' operation exceeded its bounded timeout."
        }

        [void][Threading.Tasks.Task]::WhenAll(
            [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
        ).WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(10000)
        }
        $process.Dispose()
    }
}

function Open-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'A coordinated process could not be started.'
    }

    return [pscustomobject][ordered]@{
        process = $process
        stdoutTask = $process.StandardOutput.ReadToEndAsync()
        stderrTask = $process.StandardError.ReadToEndAsync()
    }
}

function Complete-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Capture,

        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds = 120
    )

    if (-not $Capture.process.WaitForExit($TimeoutSeconds * 1000)) {
        $Capture.process.Kill($true)
        $Capture.process.WaitForExit()
        throw 'A coordinated process exceeded its bounded timeout.'
    }

    [void][Threading.Tasks.Task]::WhenAll(
        [Threading.Tasks.Task[]]@($Capture.stdoutTask, $Capture.stderrTask)
    ).WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
    return [pscustomobject][ordered]@{
        exitCode = $Capture.process.ExitCode
        stdout = $Capture.stdoutTask.GetAwaiter().GetResult()
        stderr = $Capture.stderrTask.GetAwaiter().GetResult()
    }
}

function Close-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Capture
    )

    try {
        if (-not $Capture.process.HasExited) {
            $Capture.process.Kill($true)
            [void]$Capture.process.WaitForExit(10000)
        }
    }
    finally {
        $Capture.process.Dispose()
    }
}

function Wait-ForFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [TimeSpan]$Timeout,

        [pscustomobject]$Capture
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ($null -ne $Capture -and $Capture.process.HasExited) {
            throw 'A coordinated process exited before publishing its ready state.'
        }
        if ($stopwatch.Elapsed -ge $Timeout) {
            throw 'A coordinated ready-state file was not observed before timeout.'
        }
        [Threading.Thread]::Sleep(5)
    }
}

function ConvertFrom-StrictJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "$Description produced no JSON."
    }
    try {
        $value = $Json | ConvertFrom-Json
    }
    catch {
        throw "$Description produced invalid JSON."
    }
    if ($null -eq $value) {
        throw "$Description produced an empty JSON value."
    }
    return $value
}

function Read-StrictJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found."
    }
    return ConvertFrom-StrictJson -Json (
        Get-Content -LiteralPath $Path -Raw -Encoding utf8
    ) -Description $Description
}

function Assert-CanonicalGuid {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParseExact($Value, 'D', [ref]$parsed) -or
        $parsed -eq [Guid]::Empty -or
        $Value -cne $parsed.ToString('D')) {
        throw "$Description is not a canonical identity."
    }
}

function ConvertTo-UtcDateTimeOffset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Value -is [DateTimeOffset]) {
        return ([DateTimeOffset]$Value).ToUniversalTime()
    }
    if ($Value -is [DateTime]) {
        return [DateTimeOffset]::new(([DateTime]$Value).ToUniversalTime())
    }
    try {
        return [DateTimeOffset]::Parse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    catch {
        throw "$Description is not a valid round-trip timestamp."
    }
}

function Wait-ForProcessInstanceAbsent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ObservedProcessId,

        [Parameter(Mandatory)]
        [DateTime]$ExpectedStartTimeUtc,

        [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(10)
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt $Timeout) {
        try {
            $process = [Diagnostics.Process]::GetProcessById($ObservedProcessId)
        }
        catch [ArgumentException] {
            return $true
        }

        try {
            if ($process.HasExited -or
                $process.StartTime.ToUniversalTime() -ne $ExpectedStartTimeUtc) {
                return $true
            }
        }
        finally {
            $process.Dispose()
        }
        [Threading.Thread]::Sleep(25)
    }
    return $false
}

function Close-ObservedProcessInstance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ObservedProcessId,

        [Parameter(Mandatory)]
        [DateTime]$ExpectedStartTimeUtc
    )

    try {
        $process = [Diagnostics.Process]::GetProcessById($ObservedProcessId)
    }
    catch [ArgumentException] {
        return
    }

    try {
        if (-not $process.HasExited -and
            $process.StartTime.ToUniversalTime() -eq $ExpectedStartTimeUtc) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) {
                throw 'An observed process instance could not be cleaned up.'
            }
        }
    }
    finally {
        $process.Dispose()
    }
}

function Complete-SafeProcessProbeDirectoryCleanup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator(
        [IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    $requiredPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
    $leaf = [IO.Path]::GetFileName($fullPath)
    if (-not $fullPath.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith(
            'hubradio-process-probe-',
            [StringComparison]::Ordinal)) {
        throw 'The process probe reported an unsafe temporary directory.'
    }

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-WslChecked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    $output = @(& $wsl @ArgumentList 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw 'A required WSL operation failed.'
    }
    return $output
}

function Get-Q007WslRootCount {
    [CmdletBinding()]
    param()

    $roots = @(Invoke-WslChecked -ArgumentList @(
        '--user', 'root', '--exec', 'find', '/tmp',
        '-mindepth', '1', '-maxdepth', '1', '-type', 'd',
        '-name', 'hubradio-q007-*', '-print'
    ))
    return $roots.Count
}

function Invoke-RequiredBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [string]$WorkingDirectory = $repoRoot,

        [ValidateRange(1, 1800)]
        [int]$TimeoutSeconds = 600
    )

    $result = Invoke-CapturedProcess -FileName $FileName `
        -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory `
        -TimeoutSeconds $TimeoutSeconds
    if ($result.exitCode -ne 0) {
        throw "$Description failed with exit code $($result.exitCode)."
    }
}

function Publish-Q007ProbeBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Debug', 'Release')]
        [string]$BuildConfiguration
    )

    $linuxPublish = Join-Path $publishRoot 'reliability-linux-x64'
    $windowsPublish = Join-Path $publishRoot 'reliability-win-x64'
    $jobPublish = Join-Path $publishRoot 'job-recovery-win-x64'
    $processPublish = Join-Path $publishRoot 'process-win-x64'
    foreach ($path in @(
        $linuxPublish,
        $windowsPublish,
        $jobPublish,
        $processPublish
    )) {
        [IO.Directory]::CreateDirectory($path) | Out-Null
    }

    Invoke-RequiredBuild -Description 'Web dependency restore' `
        -FileName $node -WorkingDirectory $webRoot -TimeoutSeconds 600 `
        -ArgumentList @(
            $npmCli,
            'ci'
        )
    Invoke-RequiredBuild -Description 'Web TypeScript check' `
        -FileName $node -WorkingDirectory $webRoot -ArgumentList @(
            (Join-Path $webRoot 'node_modules\typescript\bin\tsc'),
            '--noEmit'
        )
    Invoke-RequiredBuild -Description 'Web runtime dependency verification' `
        -FileName $node -WorkingDirectory $webRoot -ArgumentList @(
            (Join-Path $webRoot 'scripts\verify-runtime-dependencies.mjs')
        )
    Invoke-RequiredBuild -Description 'Vite production bundle build' `
        -FileName $node -WorkingDirectory $webRoot -ArgumentList @(
            (Join-Path $webRoot 'node_modules\vite\bin\vite.js'),
            'build'
        )
    Invoke-RequiredBuild -Description 'Web distribution verification' `
        -FileName $node -WorkingDirectory $webRoot -ArgumentList @(
            (Join-Path $webRoot 'scripts\verify-production-assets.mjs')
        )
    Invoke-RequiredBuild -Description 'Linux reliability probe publish' `
        -FileName $dotnet -TimeoutSeconds 900 -ArgumentList @(
            'publish', $reliabilityProject,
            '--configuration', $BuildConfiguration,
            '--runtime', 'linux-x64',
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:RestoreLockedMode=true',
            '-p:TreatWarningsAsErrors=true',
            '-warnaserror',
            '--output', $linuxPublish,
            '--verbosity', 'minimal'
        )
    Invoke-RequiredBuild -Description 'Windows reliability probe publish' `
        -FileName $dotnet -TimeoutSeconds 900 -ArgumentList @(
            'publish', $reliabilityProject,
            '--configuration', $BuildConfiguration,
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '-p:Platform=x64',
            '-p:PublishSingleFile=true',
            '-p:RestoreLockedMode=true',
            '-p:TreatWarningsAsErrors=true',
            '-warnaserror',
            '--output', $windowsPublish,
            '--verbosity', 'minimal'
        )
    Invoke-RequiredBuild -Description 'Job recovery probe publish' `
        -FileName $dotnet -TimeoutSeconds 900 -ArgumentList @(
            'publish', $jobRecoveryProject,
            '--configuration', $BuildConfiguration,
            '--runtime', 'win-x64',
            '--self-contained', 'false',
            '-p:Platform=x64',
            '-p:RestoreLockedMode=true',
            '-p:TreatWarningsAsErrors=true',
            '-warnaserror',
            '--output', $jobPublish,
            '--verbosity', 'minimal'
        )
    Invoke-RequiredBuild -Description 'Process containment probe publish' `
        -FileName $dotnet -TimeoutSeconds 900 -ArgumentList @(
            'publish', $processProbeProject,
            '--configuration', $BuildConfiguration,
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '-p:Platform=x64',
            '-p:PublishSingleFile=true',
            '-p:RestoreLockedMode=true',
            '-p:TreatWarningsAsErrors=true',
            '-warnaserror',
            '--output', $processPublish,
            '--verbosity', 'minimal'
        )

    $paths = [pscustomobject][ordered]@{
        linuxReliability = Join-Path $linuxPublish 'HubRadio.Q007.ReliabilityProbe'
        windowsReliability = Join-Path $windowsPublish 'HubRadio.Q007.ReliabilityProbe.exe'
        jobRecovery = Join-Path $jobPublish 'HubRadio.Q007.JobRecoveryProbe.exe'
        processProbe = Join-Path $processPublish 'HubRadio.ProcessProbe.exe'
    }
    foreach ($path in @(
        $paths.linuxReliability,
        $paths.windowsReliability,
        $paths.jobRecovery,
        $paths.processProbe
    )) {
        Assert-Condition -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
            -Message 'A required published probe executable was not found.'
    }
    return $paths
}

function Invoke-ProcessContainmentScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProbePath
    )

    $killProcess = Invoke-CapturedProcess -FileName $ProbePath `
        -ArgumentList @('kill-on-close') -TimeoutSeconds 60
    $killResult = ConvertFrom-StrictJson -Json $killProcess.stdout `
        -Description 'Kill-on-close probe'
    Assert-Condition -Condition ($killProcess.exitCode -eq 0) `
        -Message 'Kill-on-close returned a nonzero exit code.'
    Assert-Condition -Condition ([bool]$killResult.passed) `
        -Message 'Kill-on-close did not pass.'
    Assert-Condition -Condition ([int]$killResult.observedProcessIdLeakCount -eq 0) `
        -Message 'Kill-on-close left an observed process running.'
    Assert-Condition -Condition (
        [bool]$killResult.childInJobBeforeClose -and
        [bool]$killResult.grandchildInJobBeforeClose -and
        [bool]$killResult.childExitedAfterClose -and
        [bool]$killResult.grandchildExitedAfterClose -and
        [bool]$killResult.allObservedProcessIdsExitedAfterClose
    ) -Message 'Kill-on-close process containment evidence is incomplete.'

    $crashStatePath = Join-Path $workRoot 'process-crash-state.json'
    $crashProcess = Invoke-CapturedProcess -FileName $ProbePath `
        -ArgumentList @('crash-host', $crashStatePath) -TimeoutSeconds 60
    $crashState = Read-StrictJson -Path $crashStatePath `
        -Description 'Crash-host state'
    $childStart = (ConvertTo-UtcDateTimeOffset `
        -Value $crashState.childStartTimeUtc `
        -Description 'Child start time').UtcDateTime
    $grandchildStart = (ConvertTo-UtcDateTimeOffset `
        -Value $crashState.grandchildStartTimeUtc `
        -Description 'Grandchild start time').UtcDateTime
    $childExited = Wait-ForProcessInstanceAbsent `
        -ObservedProcessId ([int]$crashState.childProcessId) `
        -ExpectedStartTimeUtc $childStart
    $grandchildExited = Wait-ForProcessInstanceAbsent `
        -ObservedProcessId ([int]$crashState.grandchildProcessId) `
        -ExpectedStartTimeUtc $grandchildStart
    $leaked = [Collections.Generic.List[pscustomobject]]::new()
    if (-not $childExited) {
        $leaked.Add([pscustomobject]@{
            id = [int]$crashState.childProcessId
            start = $childStart
        })
    }
    if (-not $grandchildExited) {
        $leaked.Add([pscustomobject]@{
            id = [int]$crashState.grandchildProcessId
            start = $grandchildStart
        })
    }
    foreach ($processInstance in $leaked) {
        Close-ObservedProcessInstance -ObservedProcessId $processInstance.id `
            -ExpectedStartTimeUtc $processInstance.start
    }

    Complete-SafeProcessProbeDirectoryCleanup `
        -Path ([string]$crashState.temporaryDirectory)
    $crashPassed = $crashProcess.exitCode -ne 0 -and
        [bool]$crashState.ready -and
        [bool]$crashState.childInJob -and
        [bool]$crashState.grandchildInJob -and
        $childExited -and
        $grandchildExited -and
        $leaked.Count -eq 0 -and
        $crashProcess.stderr.Contains(
            'Intentional ST-014 crash-host termination.',
            [StringComparison]::Ordinal)
    Assert-Condition -Condition $crashPassed `
        -Message 'Intentional host-crash containment did not pass.'

    return [pscustomobject][ordered]@{
        scenario = 'windows-job-object-containment'
        killOnClose = [pscustomobject][ordered]@{
            childInJobBeforeClose = [bool]$killResult.childInJobBeforeClose
            grandchildInJobBeforeClose = [bool]$killResult.grandchildInJobBeforeClose
            childExitedAfterClose = [bool]$killResult.childExitedAfterClose
            grandchildExitedAfterClose = [bool]$killResult.grandchildExitedAfterClose
            elapsedMilliseconds = [double]$killResult.elapsedMilliseconds
            observedOrphanProcessCount = [int]$killResult.observedProcessIdLeakCount
            passed = $true
        }
        hostCrash = [pscustomobject][ordered]@{
            nonzeroExitObserved = $crashProcess.exitCode -ne 0
            intentionalCrashMarkerObserved = $true
            childExitedAfterCrash = $childExited
            grandchildExitedAfterCrash = $grandchildExited
            observedOrphanProcessCount = $leaked.Count
            passed = $true
        }
        observedOrphanProcessCount = 0
        passed = $true
    }
}

function Invoke-JobRestartScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProbePath
    )

    $caseRoot = Join-Path $workRoot 'job-restart'
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $readyPath = Join-Path $caseRoot 'host.ready.json'
    $capture = $null
    try {
        $capture = Open-CapturedProcess -FileName $ProbePath -ArgumentList @(
            'run-until-killed',
            $caseRoot,
            $readyPath
        )
        Wait-ForFile -Path $readyPath -Timeout ([TimeSpan]::FromSeconds(90)) `
            -Capture $capture
        $ready = Read-StrictJson -Path $readyPath -Description 'Job host ready state'
        Assert-Condition -Condition ([int]$ready.processId -eq $capture.process.Id) `
            -Message 'The job host ready state did not identify the launched process.'
        $state = Read-StrictJson -Path (Join-Path $caseRoot 'job-state.json') `
            -Description 'Durable job host state'
        Assert-CanonicalGuid -Value ([string]$state.jobId) -Description 'Job ID'
        Assert-CanonicalGuid -Value ([string]$state.projectId) -Description 'Project ID'
        Assert-CanonicalGuid -Value ([string]$state.attempt.id) -Description 'Attempt ID'
        Assert-CanonicalGuid -Value ([string]$state.protectedArtifact.id) `
            -Description 'Protected artifact ID'
        $latestDurableEventAt = ConvertTo-UtcDateTimeOffset `
            -Value $state.durableEventAt `
            -Description 'Latest durable event time'

        $killAt = [DateTimeOffset]::UtcNow
        $restartStopwatch = [Diagnostics.Stopwatch]::StartNew()
        $capture.process.Kill($true)
        $killedHost = Complete-CapturedProcess -Capture $capture -TimeoutSeconds 30
        Assert-Condition -Condition ($killedHost.exitCode -ne 0) `
            -Message 'The externally killed job host exited successfully.'

        $recoveryProcess = Invoke-CapturedProcess -FileName $ProbePath -ArgumentList @(
            'recover-and-resume',
            $caseRoot
        ) -TimeoutSeconds 120
        $restartStopwatch.Stop()
        $recovery = ConvertFrom-StrictJson -Json $recoveryProcess.stdout `
            -Description 'Fresh-process job recovery'
        Assert-Condition -Condition ($recoveryProcess.exitCode -eq 0) `
            -Message 'Fresh-process job recovery returned a nonzero exit code.'
        Assert-Condition -Condition (
            [string]$recovery.implementationAssembly -ceq 'HubRadio.App' -and
            [bool]$recovery.durableRunningStateObserved -and
            [string]$recovery.stateBeforeRestart -ceq 'Running' -and
            [string]$recovery.recoveredState -ceq 'Queued' -and
            [bool]$recovery.automaticallyRequeued -and
            [int]$recovery.skippedJobCount -eq 0 -and
            [string]$recovery.finalState -ceq 'Succeeded' -and
            [int]$recovery.attemptCount -eq 2 -and
            [int]$recovery.progressRecordCount -eq 2 -and
            [int]$recovery.heartbeatCount -eq 2 -and
            [bool]$recovery.priorDurableEvidencePreserved -and
            [bool]$recovery.firstAttemptInterrupted -and
            [bool]$recovery.secondAttemptSucceeded -and
            [bool]$recovery.protectedArtifactUnchanged -and
            [bool]$recovery.passed
        ) -Message 'Production job recovery evidence is incomplete.'
        Assert-Condition -Condition ($latestDurableEventAt -le $killAt) `
            -Message 'The durable event timestamp followed the external kill timestamp.'
        $durableEventAgeAtKill = [Math]::Max(
            0,
            ($killAt - $latestDurableEventAt).TotalMilliseconds)

        return [pscustomobject][ordered]@{
            scenario = 'production-job-service-kill-restart-resume'
            scope = 'fresh production service process; not WinUI startup composition'
            implementationAssembly = [string]$recovery.implementationAssembly
            externalKillObserved = $true
            durableRunningStateObserved = [bool]$recovery.durableRunningStateObserved
            transition = 'Running -> Interrupted -> Queued -> Running -> Succeeded'
            attemptCount = [int]$recovery.attemptCount
            progressRecordCount = [int]$recovery.progressRecordCount
            heartbeatCount = [int]$recovery.heartbeatCount
            priorDurableEvidencePreserved = [bool]$recovery.priorDurableEvidencePreserved
            protectedArtifactUnchanged = [bool]$recovery.protectedArtifactUnchanged
            lostDurableProgressRecordCount = 0
            durableEventAgeAtKillMilliseconds = [Math]::Round(
                $durableEventAgeAtKill,
                3)
            recoveryStateTransitionMilliseconds = [double]$recovery.recoveryMilliseconds
            retryExecutionMilliseconds = [double]$recovery.resumeMilliseconds
            externalKillToSucceededMilliseconds = [Math]::Round(
                $restartStopwatch.Elapsed.TotalMilliseconds,
                3)
            observedOrphanProcessCount = 0
            passed = $true
        }
    }
    finally {
        if ($null -ne $capture) {
            Close-CapturedProcess -Capture $capture
        }
    }
}

function Invoke-PartialStagingScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProbePath
    )

    $caseRoot = Join-Path $workRoot 'partial-staging'
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $readyPath = Join-Path $caseRoot 'host.ready.json'
    $goPath = Join-Path $caseRoot 'host.go'
    $capture = $null
    try {
        $capture = Open-CapturedProcess -FileName $ProbePath -ArgumentList @(
            'partial-host',
            $caseRoot,
            $readyPath,
            $goPath
        )
        Wait-ForFile -Path $readyPath -Timeout ([TimeSpan]::FromSeconds(120)) `
            -Capture $capture
        $ready = Read-StrictJson -Path $readyPath `
            -Description 'Partial-import host ready state'
        Assert-Condition -Condition ([int]$ready.processId -eq $capture.process.Id) `
            -Message 'The partial-import ready state did not identify the launched process.'
        $state = Read-StrictJson -Path (Join-Path $caseRoot 'partial-state.json') `
            -Description 'Partial-import state'
        foreach ($identity in @(
            [pscustomobject]@{
                value = [string]$state.projectId
                description = 'Partial project ID'
            },
            [pscustomobject]@{
                value = [string]$state.jobId
                description = 'Partial job ID'
            },
            [pscustomobject]@{
                value = [string]$state.candidateId
                description = 'Partial candidate ID'
            },
            [pscustomobject]@{
                value = [string]$state.approvedArtifactId
                description = 'Approved artifact ID'
            }
        )) {
            Assert-CanonicalGuid -Value $identity.value `
                -Description $identity.description
        }

        $partialPath = Join-Path $caseRoot (
            'workspace\projects\{0}\staging\imports\{1}\{2}.partial' -f
                [string]$state.projectId,
                [string]$state.jobId,
                [string]$state.candidateId
        )
        $sourceByteSize = [long]$state.sourceByteSize
        Assert-Condition -Condition ($sourceByteSize -gt 0) `
            -Message 'The partial source size is invalid.'
        [IO.File]::WriteAllText(
            $goPath,
            'go',
            [Text.UTF8Encoding]::new($false))

        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $observedPartialByteSize = 0L
        while ($observedPartialByteSize -le 0 -or
            $observedPartialByteSize -ge $sourceByteSize) {
            if ($capture.process.HasExited) {
                throw 'The production import completed before an external kill could be applied.'
            }
            if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
                $observedPartialByteSize = (Get-Item -LiteralPath $partialPath).Length
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw 'A nonempty partial staging file was not observed before timeout.'
            }
            [Threading.Thread]::Sleep(1)
        }

        $killToRecovery = [Diagnostics.Stopwatch]::StartNew()
        $capture.process.Kill($true)
        $killedHost = Complete-CapturedProcess -Capture $capture -TimeoutSeconds 30
        Assert-Condition -Condition ($killedHost.exitCode -ne 0) `
            -Message 'The externally killed partial-import host exited successfully.'
        $stablePartialByteSize = (Get-Item -LiteralPath $partialPath).Length
        Assert-Condition -Condition (
            $stablePartialByteSize -gt 0 -and
            $stablePartialByteSize -lt $sourceByteSize
        ) -Message 'The external kill did not leave a genuine incomplete staging file.'

        $recoveryProcess = Invoke-CapturedProcess -FileName $ProbePath -ArgumentList @(
            'partial-recover',
            $caseRoot
        ) -TimeoutSeconds 180
        $killToRecovery.Stop()
        $recovery = ConvertFrom-StrictJson -Json $recoveryProcess.stdout `
            -Description 'Fresh-process partial staging recovery'
        Assert-Condition -Condition ($recoveryProcess.exitCode -eq 0) `
            -Message 'Partial staging recovery returned a nonzero exit code.'
        Assert-Condition -Condition (
            [string]$recovery.implementationAssembly -ceq 'HubRadio.Infrastructure' -and
            [long]$recovery.recoveredPartialByteSize -eq $stablePartialByteSize -and
            [bool]$recovery.sourceUnchanged -and
            [bool]$recovery.approvedArtifactUnchanged -and
            [int]$recovery.candidateCount -eq 1 -and
            [bool]$recovery.exactCandidateDetected -and
            [bool]$recovery.cleanupEvidenceProduced -and
            [int]$recovery.candidateCountAfterCleanup -eq 0 -and
            [string]$recovery.manualRecovery -ceq 'verified-candidate-cleanup' -and
            [bool]$recovery.passed
        ) -Message 'Partial staging recovery evidence is incomplete.'

        return [pscustomobject][ordered]@{
            scenario = 'external-kill-partial-staging-recovery'
            implementationAssembly = [string]$recovery.implementationAssembly
            externalKillObserved = $true
            sourceByteSize = $sourceByteSize
            discardedIncompleteStagingBytes = $stablePartialByteSize
            sourceUnchanged = [bool]$recovery.sourceUnchanged
            approvedArtifactUnchanged = [bool]$recovery.approvedArtifactUnchanged
            exactCandidateDetected = [bool]$recovery.exactCandidateDetected
            recoveryAction = [string]$recovery.manualRecovery
            detectionMilliseconds = [double]$recovery.detectionMilliseconds
            cleanupMilliseconds = [double]$recovery.cleanupMilliseconds
            externalKillToVerifiedCleanupMilliseconds = [Math]::Round(
                $killToRecovery.Elapsed.TotalMilliseconds,
                3)
            durableOriginalBytesLost = 0
            durableApprovedBytesLost = 0
            observedOrphanProcessCount = 0
            passed = $true
        }
    }
    finally {
        if ($null -ne $capture) {
            Close-CapturedProcess -Capture $capture
        }
    }
}

function Invoke-DiskFullScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LinuxProbePath
    )

    $caseRoot = Join-Path $workRoot 'disk-full'
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $observedPath = Join-Path $caseRoot 'disk-full-observed.json'
    $continuePath = Join-Path $caseRoot 'disk-full-continue'
    $token = [Guid]::NewGuid().ToString('N')
    $linuxRoot = "/tmp/hubradio-q007-$token"
    $linuxMount = "$linuxRoot/fs"
    $mounted = $false
    $capture = $null
    $scenarioResult = $null
    $caught = $null
    $cleanupSucceeded = $false
    try {
        $uid = (@(Invoke-WslChecked -ArgumentList @('--exec', 'id', '-u'))[0]).Trim()
        $gid = (@(Invoke-WslChecked -ArgumentList @('--exec', 'id', '-g'))[0]).Trim()
        Assert-Condition -Condition ($uid -match '^\d+$' -and $gid -match '^\d+$') `
            -Message 'WSL identity output was invalid.'
        Invoke-WslChecked -ArgumentList @(
            '--user', 'root', '--exec', 'mkdir', '-p', '--', $linuxMount
        ) | Out-Null
        Invoke-WslChecked -ArgumentList @(
            '--user', 'root', '--exec', 'chown', "$uid`:$gid", '--',
            $linuxRoot, $linuxMount
        ) | Out-Null
        Invoke-WslChecked -ArgumentList @(
            '--user', 'root', '--exec', 'chmod', '700', '--',
            $linuxRoot, $linuxMount
        ) | Out-Null
        Invoke-WslChecked -ArgumentList @(
            '--user', 'root', '--exec', 'mount', '-t', 'tmpfs', '-o',
            "size=4m,nr_inodes=1024,uid=$uid,gid=$gid,mode=0700",
            'tmpfs', $linuxMount
        ) | Out-Null
        $mounted = $true
        $linuxWorkspace = "$linuxMount/workspace"
        Invoke-WslChecked -ArgumentList @(
            '--exec', 'mkdir', '-p', '--', $linuxWorkspace
        ) | Out-Null

        $linuxBinary = (@(Invoke-WslChecked -ArgumentList @(
            '--exec', 'wslpath', '-u', $LinuxProbePath
        ))[0]).Trim()
        $linuxCaseRoot = (@(Invoke-WslChecked -ArgumentList @(
            '--exec', 'wslpath', '-u', $caseRoot
        ))[0]).Trim()
        $linuxObserved = (@(Invoke-WslChecked -ArgumentList @(
            '--exec', 'wslpath', '-u', $observedPath
        ))[0]).Trim()
        $linuxContinue = (@(Invoke-WslChecked -ArgumentList @(
            '--exec', 'wslpath', '-u', $continuePath
        ))[0]).Trim()

        $failureDetection = [Diagnostics.Stopwatch]::StartNew()
        $capture = Open-CapturedProcess -FileName $wsl -ArgumentList @(
            '--exec', '/usr/bin/env',
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$linuxRoot/extract",
            $linuxBinary,
            'disk-full-import',
            $linuxCaseRoot,
            $linuxWorkspace,
            $linuxObserved,
            $linuxContinue
        )
        Wait-ForFile -Path $observedPath -Timeout ([TimeSpan]::FromSeconds(60)) `
            -Capture $capture
        $failureDetection.Stop()
        $observed = Read-StrictJson -Path $observedPath `
            -Description 'Native disk-full observation'
        $dfLines = @(Invoke-WslChecked -ArgumentList @(
            '--exec', 'df', '-kP', '--', $linuxMount
        ))
        Assert-Condition -Condition ($dfLines.Count -ge 2) `
            -Message 'WSL filesystem capacity output was incomplete.'
        $fields = @($dfLines[-1] -split '\s+' | Where-Object { $_ })
        Assert-Condition -Condition ($fields.Count -ge 6) `
            -Message 'WSL filesystem capacity fields were incomplete.'
        $capacityBytes = [long]$fields[1] * 1024L
        $availableAtFailureBytes = [long]$fields[3] * 1024L
        $failureCleanup = [Diagnostics.Stopwatch]::StartNew()
        [IO.File]::WriteAllText(
            $continuePath,
            'continue',
            [Text.UTF8Encoding]::new($false))
        $processResult = Complete-CapturedProcess -Capture $capture -TimeoutSeconds 60
        $failureCleanup.Stop()
        $probe = ConvertFrom-StrictJson -Json $processResult.stdout `
            -Description 'Production disk-full import probe'
        $nativeCodes = @($probe.observedNativeErrorCodes)
        Assert-Condition -Condition (
            $processResult.exitCode -eq 0 -and
            $capacityBytes -eq 4194304 -and
            $availableAtFailureBytes -eq 0 -and
            [int]$observed.nativeErrorCode -eq $linuxNoSpaceLeftOnDeviceErrorCode -and
            ([int]$observed.nativeHResult -band 0xffff) -eq
                $linuxNoSpaceLeftOnDeviceErrorCode -and
            $nativeCodes.Count -eq 1 -and
            [int]$nativeCodes[0] -eq $linuxNoSpaceLeftOnDeviceErrorCode -and
            [string]$probe.implementationAssembly -ceq 'HubRadio.Infrastructure' -and
            [string]$probe.operatingSystem -ceq 'linux' -and
            [string]$probe.exceptionType -ceq 'HubRadio.Infrastructure.Media.MediaImportException' -and
            [string]$probe.failureReason -ceq 'CopyFailed' -and
            -not [bool]$probe.recoveryRequired -and
            [bool]$probe.sourceDigestUnchanged -and
            [bool]$probe.sourceMetadataUnchanged -and
            [bool]$probe.existingDestinationUnchanged -and
            [int]$probe.partialFileCount -eq 0 -and
            [int]$probe.readyFileCount -eq 0 -and
            [bool]$probe.passed
        ) -Message 'Genuine production disk-full evidence is incomplete.'

        $scenarioResult = [pscustomobject][ordered]@{
            scenario = 'genuine-capacity-exhaustion-during-media-import'
            implementationAssembly = [string]$probe.implementationAssembly
            operatingSystem = [string]$probe.operatingSystem
            constrainedFilesystem = 'root-mounted WSL tmpfs'
            capacityBytes = $capacityBytes
            availableAtFailureBytes = $availableAtFailureBytes
            nativeHResult = [int]$observed.nativeHResult
            nativeErrorCode = [int]$observed.nativeErrorCode
            nativeErrorCodeContract = 'IOException.HResult low 16 bits; Linux ENOSPC'
            failureReason = [string]$probe.failureReason
            sourceDigestUnchanged = [bool]$probe.sourceDigestUnchanged
            sourceMetadataUnchanged = [bool]$probe.sourceMetadataUnchanged
            existingDestinationUnchanged = [bool]$probe.existingDestinationUnchanged
            residualPartialFileCount = [int]$probe.partialFileCount
            residualReadyFileCount = [int]$probe.readyFileCount
            failureDetectionMilliseconds = [Math]::Round(
                $failureDetection.Elapsed.TotalMilliseconds,
                3)
            failureCleanupMilliseconds = [Math]::Round(
                $failureCleanup.Elapsed.TotalMilliseconds,
                3)
            passed = $true
        }
    }
    catch {
        $caught = $_
    }
    finally {
        if ($null -ne $capture -and -not $capture.process.HasExited) {
            if (Test-Path -LiteralPath $observedPath -PathType Leaf) {
                try {
                    $observedForCleanup = Read-StrictJson -Path $observedPath `
                        -Description 'Disk-full cleanup state'
                    if ([int]$observedForCleanup.processId -gt 1) {
                        $null = & $wsl --user root --exec kill -9 -- (
                            [string][int]$observedForCleanup.processId
                        ) 2>$null
                    }
                }
                catch {
                    Write-Verbose 'Best-effort WSL process cleanup was not available.'
                }
            }
            try {
                $capture.process.Kill($true)
                [void]$capture.process.WaitForExit(10000)
            }
            catch {
                Write-Verbose 'Best-effort capture cleanup was not available.'
            }
        }
        if ($null -ne $capture) {
            Close-CapturedProcess -Capture $capture
        }

        $unmountCode = 0
        if ($mounted) {
            $null = & $wsl --user root --exec umount -- $linuxMount 2>$null
            $unmountCode = $LASTEXITCODE
        }
        if ($linuxRoot -match '^/tmp/hubradio-q007-[0-9a-f]{32}$') {
            $null = & $wsl --user root --exec rm -rf -- $linuxRoot 2>$null
            $removeCode = $LASTEXITCODE
            $null = & $wsl --user root --exec test '!' -e $linuxRoot 2>$null
            $absenceCode = $LASTEXITCODE
            $cleanupSucceeded = $unmountCode -eq 0 -and
                $removeCode -eq 0 -and
                $absenceCode -eq 0
        }
    }

    if ($null -ne $caught) {
        throw $caught
    }
    Assert-Condition -Condition $cleanupSucceeded `
        -Message 'The constrained WSL filesystem was not cleaned up.'
    $scenarioResult | Add-Member -NotePropertyName cleanupSucceeded `
        -NotePropertyValue $true
    return $scenarioResult
}

function Invoke-BackupRestoreScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProbePath
    )

    $caseRoot = Join-Path $workRoot 'backup-restore'
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $processResult = Invoke-CapturedProcess -FileName $ProbePath -ArgumentList @(
        'backup-restore',
        $caseRoot
    ) -TimeoutSeconds 300
    $probe = ConvertFrom-StrictJson -Json $processResult.stdout `
        -Description 'Backup and restore probe'
    Assert-Condition -Condition (
        $processResult.exitCode -eq 0 -and
        [string]$probe.implementationAssembly -ceq 'HubRadio.Infrastructure' -and
        [int]$probe.backupFileCount -eq 3 -and
        [bool]$probe.baselineContentMatchesRegistrations -and
        [bool]$probe.artifact.mismatchDetected -and
        [bool]$probe.artifact.previousWorkspaceRetained -and
        [bool]$probe.artifact.restoreMatchedBackup -and
        [bool]$probe.database.invalidDetected -and
        [bool]$probe.database.previousWorkspaceRetained -and
        [bool]$probe.database.restoreMatchedBackup -and
        [bool]$probe.projectUnchanged -and
        [bool]$probe.originalUnchanged -and
        [bool]$probe.approvedArtifactUnchanged -and
        [bool]$probe.compositionDigestUnchanged -and
        [bool]$probe.compositionVersionUnchanged -and
        [bool]$probe.approvalRecordUnchanged -and
        [bool]$probe.auditChainUnchanged -and
        [int]$probe.approvalCount -eq 1 -and
        [int]$probe.auditEventCount -eq 2 -and
        [long]$probe.lostOriginalBytes -eq 0 -and
        [long]$probe.lostApprovedBytes -eq 0 -and
        [int]$probe.lostApprovalRecords -eq 0 -and
        [int]$probe.lostAuditEvents -eq 0 -and
        [bool]$probe.passed
    ) -Message 'Backup, corruption detection, and restore evidence is incomplete.'

    return [pscustomobject][ordered]@{
        scenario = 'verified-backup-artifact-and-database-restore'
        implementationAssembly = [string]$probe.implementationAssembly
        backupFileCount = [int]$probe.backupFileCount
        backupMilliseconds = [double]$probe.backupMilliseconds
        baselineContentMatchesRegistrations = [bool]$probe.baselineContentMatchesRegistrations
        artifactCorruption = [pscustomobject][ordered]@{
            sameSizeDigestMismatchDetected = [bool]$probe.artifact.mismatchDetected
            previousCorruptWorkspaceRetained = [bool]$probe.artifact.previousWorkspaceRetained
            restoreMatchedBackup = [bool]$probe.artifact.restoreMatchedBackup
            backupAgeAtFaultMilliseconds = [double]$probe.artifact.backupAgeAtFaultMilliseconds
            detectionMilliseconds = [double]$probe.artifact.detectionMilliseconds
            detectionThroughVerifiedRecoveryMilliseconds = [double]$probe.artifact.detectionThroughVerifiedRecoveryMilliseconds
            passed = $true
        }
        databaseCorruption = [pscustomobject][ordered]@{
            invalidDatabaseDetected = [bool]$probe.database.invalidDetected
            previousCorruptWorkspaceRetained = [bool]$probe.database.previousWorkspaceRetained
            restoreMatchedBackup = [bool]$probe.database.restoreMatchedBackup
            backupAgeAtFaultMilliseconds = [double]$probe.database.backupAgeAtFaultMilliseconds
            detectionMilliseconds = [double]$probe.database.detectionMilliseconds
            detectionThroughVerifiedRecoveryMilliseconds = [double]$probe.database.detectionThroughVerifiedRecoveryMilliseconds
            passed = $true
        }
        projectUnchanged = [bool]$probe.projectUnchanged
        originalUnchanged = [bool]$probe.originalUnchanged
        approvedArtifactUnchanged = [bool]$probe.approvedArtifactUnchanged
        compositionDigestUnchanged = [bool]$probe.compositionDigestUnchanged
        compositionVersionUnchanged = [bool]$probe.compositionVersionUnchanged
        approvalRecordUnchanged = [bool]$probe.approvalRecordUnchanged
        auditChainUnchanged = [bool]$probe.auditChainUnchanged
        lostOriginalBytes = 0
        lostApprovedBytes = 0
        lostApprovalRecords = 0
        lostAuditEvents = 0
        passed = $true
    }
}

foreach ($requiredFile in @(
    $dotnet,
    $node,
    $npmCli,
    $wsl,
    $reliabilityProject,
    $jobRecoveryProject,
    $processProbeProject
)) {
    Assert-Condition -Condition (Test-Path -LiteralPath $requiredFile -PathType Leaf) `
        -Message 'A required Q-007 tool or project file is unavailable.'
}

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($publishRoot) | Out-Null

$currentStage = 'preflight'
try {
    $wslIdentity = @(Invoke-WslChecked -ArgumentList @('--exec', 'id', '-u'))
    Assert-Condition -Condition (
        $wslIdentity.Count -eq 1 -and
        $wslIdentity[0].Trim() -match '^\d+$'
    ) -Message 'A usable default WSL distribution was not detected.'
    Assert-Condition -Condition ((Get-Q007WslRootCount) -eq 0) `
        -Message 'A stale Q-007 WSL temporary root exists; refusing to overlap runs.'

    $currentStage = 'probe publishing'
    $probes = Publish-Q007ProbeBundle -BuildConfiguration $Configuration
    $currentStage = 'process containment'
    $processContainment = Invoke-ProcessContainmentScenario `
        -ProbePath $probes.processProbe
    $currentStage = 'production job restart'
    $jobRestart = Invoke-JobRestartScenario -ProbePath $probes.jobRecovery
    $currentStage = 'partial staging recovery'
    $partialStaging = Invoke-PartialStagingScenario `
        -ProbePath $probes.windowsReliability
    $currentStage = 'genuine disk-full recovery'
    $diskFull = Invoke-DiskFullScenario `
        -LinuxProbePath $probes.linuxReliability
    Assert-Condition -Condition ((Get-Q007WslRootCount) -eq 0) `
        -Message 'Q-007 WSL temporary state remained after disk-full recovery.'
    $currentStage = 'backup restore'
    $backupRestore = Invoke-BackupRestoreScenario `
        -ProbePath $probes.windowsReliability

    $orphanCount = [int]$processContainment.observedOrphanProcessCount +
        [int]$jobRestart.observedOrphanProcessCount +
        [int]$partialStaging.observedOrphanProcessCount
    Assert-Condition -Condition ($orphanCount -eq 0) `
        -Message 'At least one observed Q-007 process remained after recovery.'
    $dotnetVersion = (Invoke-CapturedProcess -FileName $dotnet `
        -ArgumentList @('--version') -TimeoutSeconds 30).stdout.Trim()
    $result = [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        requirement = 'Q-007'
        measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = [pscustomobject][ordered]@{
            operatingSystem = 'Windows'
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            powershellEdition = $PSVersionTable.PSEdition
            powershellVersion = $PSVersionTable.PSVersion.ToString()
            dotnetSdkVersion = $dotnetVersion
            wslCapacityIsolation = 'root-mounted tmpfs'
        }
        processContainment = $processContainment
        jobRestart = $jobRestart
        partialStaging = $partialStaging
        diskFull = $diskFull
        backupRestore = $backupRestore
        recoveryPoint = [pscustomobject][ordered]@{
            lostOriginalBytes = [long]$backupRestore.lostOriginalBytes +
                [long]$partialStaging.durableOriginalBytesLost
            lostApprovedBytes = [long]$backupRestore.lostApprovedBytes +
                [long]$partialStaging.durableApprovedBytesLost
            lostApprovalRecords = [int]$backupRestore.lostApprovalRecords
            lostAuditEvents = [int]$backupRestore.lostAuditEvents
            lostDurableProgressRecordCount = [int]$jobRestart.lostDurableProgressRecordCount
        }
        observedOrphanProcessCount = $orphanCount
        scopeLimit = 'Production services were restarted in fresh processes; full WinUI startup composition was not exercised.'
        passed = $true
    }
    $result | ConvertTo-Json -Depth 12 -Compress
}
catch {
    throw "Q-007 acceptance failed during $currentStage`: $($_.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}