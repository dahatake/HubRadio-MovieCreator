#Requires -Version 7.0

<#
.SYNOPSIS
Builds the current working tree in Release and launches HubRadio.App.

.DESCRIPTION
1. Stops any HubRadio.App instance already running from this repository's Release output,
   so the build can overwrite its files.
2. Runs scripts/build.ps1 -Configuration Release (dependency restore, Web build, .NET build
   and static analysis). Use -SkipBuild to launch the existing Release output as-is.
3. Starts the Release HubRadio.App.exe and confirms that its process is still alive.
#>
[CmdletBinding()]
param(
    # Launch the existing Release output without rebuilding it.
    [switch]$SkipBuild,

    # Opt-in prepared runtime forwarded to build.ps1. Omit for ordinary source builds.
    [string]$RuntimePayloadPath,

    # Block until the application exits and return its exit code.
    [switch]$Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$configuration = 'Release'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appExecutable = Join-Path $repoRoot "src\HubRadio.App\bin\$configuration\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.exe"

$runningInstances = @(
    Get-Process -Name 'HubRadio.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $appExecutable, [StringComparison]::OrdinalIgnoreCase) }
)
foreach ($instance in $runningInstances) {
    Write-Information "==> Stopping running HubRadio.App (PID $($instance.Id))" -InformationAction Continue
    Stop-Process -Id $instance.Id -Force
    $instance.WaitForExit(30000) | Out-Null
}

if (-not $SkipBuild) {
    $buildArguments = @{ Configuration = $configuration }
    if (-not [string]::IsNullOrWhiteSpace($RuntimePayloadPath)) {
        $buildArguments.RuntimePayloadPath = $RuntimePayloadPath
    }

    & (Join-Path $PSScriptRoot 'build.ps1') @buildArguments
}

if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "Release application executable is missing: $appExecutable. Run without -SkipBuild."
}

Write-Information "==> Launching $appExecutable" -InformationAction Continue
$process = Start-Process -FilePath $appExecutable -WorkingDirectory (Split-Path -Parent $appExecutable) -PassThru

if ($Wait) {
    $process.WaitForExit()
    Write-Information "HubRadio.App exited with code $($process.ExitCode)." -InformationAction Continue
    exit $process.ExitCode
}

Start-Sleep -Seconds 3
if ($process.HasExited) {
    throw "HubRadio.App exited during startup with code $($process.ExitCode)."
}

Write-Information "HubRadio.App is running (PID $($process.Id))." -InformationAction Continue
