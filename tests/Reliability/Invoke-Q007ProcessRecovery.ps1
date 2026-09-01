#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repoRoot 'benchmarks\probes\ProcessProbe\ProcessProbe.csproj'
$workRoot = Join-Path $repoRoot 'benchmarks\work\q007-process-recovery'
$publishPath = Join-Path $workRoot 'publish'
$statePath = Join-Path $workRoot 'crash-state.json'
$dotnet = (Get-Command -Name 'dotnet.exe' -ErrorAction Stop).Source

function Invoke-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds = 120
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The reliability probe process could not be started.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The reliability probe exceeded its bounded timeout.'
        }

        [Threading.Tasks.Task]::WhenAll(
            [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
        ).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        return [pscustomobject][ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function Wait-ForProcessAbsent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ObservedProcessId
    )

    try {
        $process = [Diagnostics.Process]::GetProcessById($ObservedProcessId)
    }
    catch [ArgumentException] {
        return $true
    }

    try {
        return $process.WaitForExit(10000)
    }
    finally {
        $process.Dispose()
    }
}

function Stop-LeakedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ObservedProcessId
    )

    try {
        $process = [Diagnostics.Process]::GetProcessById($ObservedProcessId)
    }
    catch [ArgumentException] {
        return
    }

    try {
        if (-not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) {
                throw 'An observed probe process could not be cleaned up.'
            }
        }
    }
    finally {
        $process.Dispose()
    }
}

function Remove-ProbeTemporaryDirectory {
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
        throw 'The probe reported an unsafe temporary directory.'
    }

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($publishPath) | Out-Null

try {
    $publish = Invoke-CapturedProcess -FileName $dotnet -TimeoutSeconds 300 -ArgumentList @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:RestoreLockedMode=true',
        '--output', $publishPath,
        '--verbosity', 'minimal'
    )
    if ($publish.exitCode -ne 0) {
        throw "ProcessProbe publish failed with exit code $($publish.exitCode)."
    }

    $probePath = Join-Path $publishPath 'HubRadio.ProcessProbe.exe'
    if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) {
        throw 'The published ProcessProbe executable was not found.'
    }

    $killOnCloseProcess = Invoke-CapturedProcess -FileName $probePath -ArgumentList @(
        'kill-on-close'
    )
    $killOnClose = $killOnCloseProcess.stdout | ConvertFrom-Json

    $crashProcess = Invoke-CapturedProcess -FileName $probePath -ArgumentList @(
        'crash-host',
        $statePath
    )
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw 'The crash probe did not persist its pre-crash state.'
    }

    $crashState = Get-Content -LiteralPath $statePath -Raw -Encoding utf8 |
        ConvertFrom-Json
    $childExited = Wait-ForProcessAbsent -ObservedProcessId ([int]$crashState.childProcessId)
    $grandchildExited = Wait-ForProcessAbsent -ObservedProcessId ([int]$crashState.grandchildProcessId)
    $leakedProcessIds = [Collections.Generic.List[int]]::new()
    if (-not $childExited) {
        $leakedProcessIds.Add([int]$crashState.childProcessId)
    }
    if (-not $grandchildExited) {
        $leakedProcessIds.Add([int]$crashState.grandchildProcessId)
    }
    foreach ($processId in $leakedProcessIds) {
        Stop-LeakedProcess -ObservedProcessId $processId
    }

    Remove-ProbeTemporaryDirectory -Path ([string]$crashState.temporaryDirectory)
    $crashPassed = $crashProcess.exitCode -ne 0 -and
        [bool]$crashState.ready -and
        [bool]$crashState.childInJob -and
        [bool]$crashState.grandchildInJob -and
        $childExited -and
        $grandchildExited -and
        $crashProcess.stderr.Contains('Intentional ST-014 crash-host termination.')
    $passed = $killOnCloseProcess.exitCode -eq 0 -and
        [bool]$killOnClose.passed -and
        $crashPassed -and
        $leakedProcessIds.Count -eq 0
    $report = [pscustomobject][ordered]@{
        schemaVersion = '1.0.0'
        powershellEdition = $PSVersionTable.PSEdition
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        dotnetSdkVersion = (& $dotnet --version).Trim()
        probeSha256 = (Get-FileHash -LiteralPath $probePath -Algorithm SHA256).Hash.ToLowerInvariant()
        killOnClose = [pscustomobject][ordered]@{
            childInJobBeforeClose = [bool]$killOnClose.childInJobBeforeClose
            grandchildInJobBeforeClose = [bool]$killOnClose.grandchildInJobBeforeClose
            childExitedAfterClose = [bool]$killOnClose.childExitedAfterClose
            grandchildExitedAfterClose = [bool]$killOnClose.grandchildExitedAfterClose
            allObservedProcessIdsExitedAfterClose = [bool]$killOnClose.allObservedProcessIdsExitedAfterClose
            observedProcessIdLeakCount = [int]$killOnClose.observedProcessIdLeakCount
            passed = [bool]$killOnClose.passed
        }
        hostCrash = [pscustomobject][ordered]@{
            hostExitCode = [int]$crashProcess.exitCode
            stateReady = [bool]$crashState.ready
            childInJobBeforeCrash = [bool]$crashState.childInJob
            grandchildInJobBeforeCrash = [bool]$crashState.grandchildInJob
            childExitedAfterHostCrash = $childExited
            grandchildExitedAfterHostCrash = $grandchildExited
            intentionalCrashMarkerObserved = $crashProcess.stderr.Contains(
                'Intentional ST-014 crash-host termination.')
            observedProcessIdLeakCount = $leakedProcessIds.Count
            passed = $crashPassed
        }
        passed = $passed
    }

    $report | ConvertTo-Json -Depth 6
    if (-not $passed) {
        throw 'Q-007 process recovery verification failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
