#Requires -Version 7.0

<#
.SYNOPSIS
Creates the isolated Python 3.12 environment used by Stage 0 machine-learning benchmarks.

.DESCRIPTION
The default interpreter on the development machine is newer than the versions the selected
machine-learning wheels are published for, so Stage 0 pins its own interpreter and direct
benchmark runtime packages. Resolved package metadata is recorded for traceability; the complete
hash-locked transitive dependency set remains a PK-001 deliverable after Stage 0 technology choices.
#>

[CmdletBinding()]
param(
    [string]$PythonExecutable = (Join-Path $env:LOCALAPPDATA "Programs\Python\Python312\python.exe"),
    [string]$EnvironmentPath = (Join-Path $PSScriptRoot ".venv"),
    [switch]$Recreate
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "PowerShell 7+ (Core) is required."
}

$pinnedPackages = @(
    "faster-whisper==1.2.1",
    "scenedetect==0.7.1",
    "mediapipe==1.0.1",
    "onnxruntime==1.29.0",
    "av==18.1.0",
    "numpy==2.5.2"
)

function Invoke-Python {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Activity
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) {
    throw "Python 3.12 interpreter was not found at the expected location. Install it before running this script."
}

$reportedVersion = (& $PythonExecutable --version 2>&1 | Select-Object -First 1).ToString().Trim()
if ($reportedVersion -notmatch '^Python 3\.12\.\d+$') {
    throw "Expected a Python 3.12 interpreter but found: $reportedVersion"
}

if ($Recreate -and (Test-Path -LiteralPath $EnvironmentPath)) {
    Remove-Item -LiteralPath $EnvironmentPath -Recurse -Force
}

if (-not (Test-Path -LiteralPath $EnvironmentPath)) {
    Invoke-Python -Executable $PythonExecutable -Arguments @("-m", "venv", $EnvironmentPath) -Activity "Virtual environment creation"
}

$environmentPython = Join-Path $EnvironmentPath "Scripts\python.exe"
if (-not (Test-Path -LiteralPath $environmentPython -PathType Leaf)) {
    throw "The virtual environment does not contain an interpreter."
}

Invoke-Python -Executable $environmentPython -Arguments @("-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--quiet", "pip") -Activity "pip upgrade"
Invoke-Python -Executable $environmentPython -Arguments (@("-m", "pip", "install", "--disable-pip-version-check", "--quiet") + $pinnedPackages) -Activity "Pinned package installation"

$installed = & $environmentPython -m pip list --format=json
if ($LASTEXITCODE -ne 0) {
    throw "Unable to enumerate installed packages."
}

$packages = $installed | ConvertFrom-Json
$requested = @($pinnedPackages | ForEach-Object {
    $parts = $_ -split "=="
    $distribution = ($parts[0] -replace '\[.*\]', '')
    $match = $packages | Where-Object { $_.name -eq $distribution } | Select-Object -First 1
    [ordered]@{
        name = $distribution
        requirement = $_
        requestedVersion = $parts[1]
        installedVersion = if ($null -eq $match) { $null } else { $match.version }
        satisfied = $null -ne $match -and $match.version -eq $parts[1]
    }
})

$unsatisfied = @($requested | Where-Object { -not $_.satisfied })
if ($unsatisfied.Count -gt 0) {
    throw "These pinned packages did not resolve to the requested version: $(($unsatisfied | ForEach-Object { $_.name }) -join ', ')"
}

[ordered]@{
    schemaVersion = "1.0.0"
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    interpreter = [ordered]@{
        baseVersion = $reportedVersion
        environmentVersion = (& $environmentPython --version 2>&1 | Select-Object -First 1).ToString().Trim()
        environmentRelativePath = "benchmarks/.venv"
    }
    pinnedPackages = $requested
    totalInstalledPackages = $packages.Count
} | ConvertTo-Json -Depth 6
