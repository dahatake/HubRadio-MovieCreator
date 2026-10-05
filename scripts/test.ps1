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

$repoRoot = Split-Path -Parent $PSScriptRoot
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

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration

$npm = Get-RequiredCommandPath -Name 'npm.cmd'

$dotnetTestExecutables = @(
    'tests\Architecture.Tests\bin\{0}\net10.0\Architecture.Tests.exe' -f $Configuration
    'tests\HubRadio.Core.Tests\bin\{0}\net10.0\HubRadio.Core.Tests.exe' -f $Configuration
    'tests\HubRadio.Infrastructure.Tests\bin\{0}\net10.0\HubRadio.Infrastructure.Tests.exe' -f $Configuration
    'tests\HubRadio.App.Tests\bin\{0}\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.Tests.exe' -f $Configuration
)

foreach ($relativeExecutable in $dotnetTestExecutables) {
    $testExecutable = Join-Path $repoRoot $relativeExecutable
    if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
        throw "Built .NET test executable is missing: $relativeExecutable"
    }

    $previousProcessorCount = $env:DOTNET_PROCESSOR_COUNT
    try {
        if ($relativeExecutable -like '*HubRadio.Infrastructure.Tests*') {
            # SQLite/ACL/file-cutover integration tests contend nondeterministically
            # under MTP worker concurrency; keep this assembly deterministic.
            $env:DOTNET_PROCESSOR_COUNT = '1'
        }

        Invoke-CheckedCommand -FilePath $testExecutable -ArgumentList @(
            '--progress', 'off'
        ) `
            -WorkingDirectory $repoRoot -Description ".NET tests: $relativeExecutable"
    }
    finally {
        $env:DOTNET_PROCESSOR_COUNT = $previousProcessorCount
    }
}

Invoke-CheckedCommand -FilePath $npm -ArgumentList @('test') `
    -WorkingDirectory $webRoot -Description 'Web tests'

Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @('-m', 'pytest') `
    -WorkingDirectory $workerRoot -Description 'Python worker tests'

Write-Information 'All automated test runners completed successfully. Environment-gated inconclusive tests remain separate acceptance evidence.' -InformationAction Continue
