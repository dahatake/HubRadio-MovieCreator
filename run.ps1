#Requires -Version 7.0

<#
.SYNOPSIS
Builds HubRadio from the latest source, sets up the environment, and launches the app.

.DESCRIPTION
1. Checks prerequisites (git, npm, py, .NET SDK from global.json) unless -SkipBuild.
2. Optionally (-Pull) fast-forwards the current branch with `git pull --ff-only`.
3. Stops a running Release HubRadio.App so the build can overwrite its files.
4. Runs scripts\build.ps1 -Configuration Release (unless -SkipBuild).
5. Installs resources (ffmpeg, Worker, model, fonts) via install.ps1 -SkipBuild
   (unless -SkipInstall or -RuntimePayloadPath).
6. Starts HubRadio.App.exe.

.EXAMPLE
pwsh -File .\run.ps1

.EXAMPLE
pwsh -File .\run.ps1 -Pull -Wait
#>
[CmdletBinding()]
param(
    # Fast-forward the current branch to its upstream before building.
    [switch]$Pull,

    # Launch the existing Release output without rebuilding (also skips the prerequisite check).
    [switch]$SkipBuild,

    # Skip resource installation (ffmpeg, Worker, model, fonts).
    [switch]$SkipInstall,

    # Opt-in prepared runtime forwarded to build.ps1. Implies -SkipInstall.
    [string]$RuntimePayloadPath,

    # Block until the application exits and return its exit code.
    [switch]$Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Information $Message -InformationAction Continue
}

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}
if (-not $IsWindows -or
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'HubRadio requires Windows x64.'
}

$configuration = 'Release'
$repoRoot = $PSScriptRoot
$appExecutable = Join-Path $repoRoot "src\HubRadio.App\bin\$configuration\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.exe"

if (-not $SkipBuild) {
    Write-Step '==> Checking prerequisites'
    $tools = [ordered]@{
        'git.exe'   = 'Git (https://git-scm.com/)'
        'npm.cmd'   = 'Node.js with npm (https://nodejs.org/)'
        'py.exe'    = 'Python launcher (https://www.python.org/)'
        'dotnet.exe' = '.NET SDK (https://dotnet.microsoft.com/)'
    }
    foreach ($tool in $tools.Keys) {
        if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "$tool was not found on PATH. Install $($tools[$tool])."
        }
    }

    $globalJson = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
    $requiredSdk = [string]$globalJson.sdk.version
    $installedSdks = @(& dotnet.exe --list-sdks | ForEach-Object { ($_ -split '\s+')[0] })
    if ($installedSdks -notcontains $requiredSdk) {
        throw ".NET SDK $requiredSdk (from global.json) is not installed. Installed: $($installedSdks -join ', ')."
    }
}

if ($Pull) {
    Write-Step '==> Pulling latest source (git pull --ff-only)'
    & git -C $repoRoot pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw "git pull failed with exit code $LASTEXITCODE."
    }
}

$runningInstances = @(
    Get-Process -Name 'HubRadio.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $appExecutable, [StringComparison]::OrdinalIgnoreCase) }
)
foreach ($instance in $runningInstances) {
    Write-Step "==> Stopping running HubRadio.App (PID $($instance.Id))"
    Stop-Process -Id $instance.Id -Force
    $instance.WaitForExit(30000) | Out-Null
}

if (-not $SkipBuild) {
    $buildArguments = @{ Configuration = $configuration }
    if (-not [string]::IsNullOrWhiteSpace($RuntimePayloadPath)) {
        $buildArguments.RuntimePayloadPath = $RuntimePayloadPath
    }
    & (Join-Path $repoRoot 'scripts\build.ps1') @buildArguments
}

if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "Release application executable is missing: $appExecutable. Run without -SkipBuild."
}

if (-not $SkipInstall -and [string]::IsNullOrWhiteSpace($RuntimePayloadPath)) {
    Write-Step '==> Installing resources (ffmpeg, Worker, model, fonts)'
    & (Join-Path $repoRoot 'install.ps1') -Configuration $configuration -SkipBuild
}

Write-Step "==> Launching $appExecutable"
$process = Start-Process -FilePath $appExecutable -WorkingDirectory (Split-Path -Parent $appExecutable) -PassThru

if ($Wait) {
    $process.WaitForExit()
    Write-Step "HubRadio.App exited with code $($process.ExitCode)."
    exit $process.ExitCode
}

Start-Sleep -Seconds 3
if ($process.HasExited) {
    throw "HubRadio.App exited during startup with code $($process.ExitCode)."
}

Write-Step "HubRadio.App is running (PID $($process.Id))."
