#Requires -Version 7.0

<#
.SYNOPSIS
Builds HubRadio from the latest source code and launches the built application.

.DESCRIPTION
1. Optionally (-Pull) fast-forwards the current branch to its upstream with `git pull --ff-only`.
2. Delegates to scripts/run.ps1, which stops a running Release instance, runs
   scripts/build.ps1 -Configuration Release, and starts the built HubRadio.App.exe.

.EXAMPLE
pwsh -File .\run.ps1

.EXAMPLE
pwsh -File .\run.ps1 -Pull -Wait
#>
[CmdletBinding()]
param(
    # Fetch the latest commits from the upstream branch (fast-forward only) before building.
    [switch]$Pull,

    # Opt-in prepared runtime forwarded to scripts/build.ps1.
    [string]$RuntimePayloadPath,

    # Block until the application exits and return its exit code.
    [switch]$Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

$repoRoot = $PSScriptRoot

if ($Pull) {
    Write-Information '==> Pulling latest source (git pull --ff-only)' -InformationAction Continue
    & git -C $repoRoot pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw "git pull failed with exit code $LASTEXITCODE."
    }
}

$runArguments = @{}
if (-not [string]::IsNullOrWhiteSpace($RuntimePayloadPath)) {
    $runArguments.RuntimePayloadPath = $RuntimePayloadPath
}
if ($Wait) {
    $runArguments.Wait = $true
}

& (Join-Path $repoRoot 'scripts\run.ps1') @runArguments
if ($Wait) {
    exit $LASTEXITCODE
}
