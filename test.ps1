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

$repoRoot = $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'HubRadio.slnx'
$webRoot = Join-Path $repoRoot 'src\HubRadio.Web'
$workerRoot = Join-Path $repoRoot 'src\HubRadio.Worker'
$workerPython = Join-Path $workerRoot '.venv\Scripts\python.exe'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NPM_CONFIG_FUND = 'false'

function Get-RequiredCommandPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command -Name $Name -ErrorAction Stop
    return $command.Source
}

function Invoke-CheckedCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$Description
    )

    Write-Information "==> $Description" -InformationAction Continue
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

& (Join-Path $repoRoot 'build.ps1') -Configuration $Configuration

$dotnet = Get-RequiredCommandPath -Name 'dotnet.exe'
$npm = Get-RequiredCommandPath -Name 'npm.cmd'

Invoke-CheckedCommand -FilePath $dotnet -ArgumentList @(
    'test', $solutionPath,
    '--configuration', $Configuration,
    '--no-build',
    '--no-restore',
    '-p:Platform=x64',
    '--verbosity', 'minimal'
) -WorkingDirectory $repoRoot -Description '.NET tests'

Invoke-CheckedCommand -FilePath $npm -ArgumentList @('test') `
    -WorkingDirectory $webRoot -Description 'Web tests'

Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @('-m', 'pytest') `
    -WorkingDirectory $workerRoot -Description 'Python worker tests'

Write-Information 'All test runners completed successfully.' -InformationAction Continue
