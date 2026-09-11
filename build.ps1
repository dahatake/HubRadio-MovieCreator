#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Opt-in prepared runtime, never downloaded implicitly and never required as a
    # launch-time environment variable. Omit for ordinary lightweight source builds.
    [string]$RuntimePayloadPath
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
$workerRuntimeLock = Join-Path $workerRoot 'requirements.lock'
$workerTestLock = Join-Path $workerRoot 'requirements-test.lock'
$runtimePayloadArguments = @()
if (-not [string]::IsNullOrWhiteSpace($RuntimePayloadPath)) {
    if (-not [IO.Path]::IsPathFullyQualified($RuntimePayloadPath) -or
        $RuntimePayloadPath -match '[;,%''"$@`\x00-\x1f\x7f]') {
        throw 'RuntimePayloadPath must be an absolute prepared payload path without MSBuild delimiters.'
    }
    $RuntimePayloadPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RuntimePayloadPath))
    if (-not (Test-Path -LiteralPath $RuntimePayloadPath -PathType Container)) {
        throw 'RuntimePayloadPath does not exist. Prepare the approved component payload first.'
    }
    $runtimePayloadArguments = @("-p:HubRadioRuntimePayloadPath=$RuntimePayloadPath")
}

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

function Test-PowerShellSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$GitPath
    )

    $analyzer = Get-Module -ListAvailable -Name PSScriptAnalyzer |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -eq $analyzer -or $analyzer.Version -lt [version]'1.25.0') {
        throw 'PSScriptAnalyzer 1.25.0 or later is required for repository linting.'
    }

    Import-Module PSScriptAnalyzer -MinimumVersion 1.25.0 -ErrorAction Stop

    $tracked = @(& $GitPath -C $repoRoot ls-files -- '*.ps1' '*.psm1' '*.psd1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate tracked PowerShell files.'
    }

    $trackedPaths = @(
        $tracked | ForEach-Object { Join-Path $repoRoot $_ }
        Join-Path $repoRoot 'build.ps1'
        Join-Path $repoRoot 'test.ps1'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique

    $parseErrors = @(
        foreach ($path in $trackedPaths) {
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $path,
                [ref]$tokens,
                [ref]$errors) | Out-Null
            $errors
        }
    )
    if ($parseErrors.Count -ne 0) {
        $messages = $parseErrors | ForEach-Object {
            '{0}:{1}:{2}: {3}' -f $_.Extent.File, $_.Extent.StartLineNumber, $_.Extent.StartColumnNumber, $_.Message
        }
        throw "PowerShell parsing failed:$([Environment]::NewLine)$($messages -join [Environment]::NewLine)"
    }

    # Stage 0 scripts are immutable benchmark evidence. Parse all tracked scripts,
    # but apply current analyzer policy only to the active root orchestrators.
    $lintPaths = @(
        Join-Path $repoRoot 'build.ps1'
        Join-Path $repoRoot 'test.ps1'
    )
    $diagnostics = @(
        foreach ($path in $lintPaths) {
            Invoke-ScriptAnalyzer -Path $path -Severity Error, Warning
        }
    )
    if ($diagnostics.Count -ne 0) {
        $messages = $diagnostics | ForEach-Object {
            '{0}:{1}:{2}: {3}: {4}' -f $_.ScriptPath, $_.Line, $_.Column, $_.RuleName, $_.Message
        }
        throw "PowerShell lint failed:$([Environment]::NewLine)$($messages -join [Environment]::NewLine)"
    }
}

$dotnet = Get-RequiredCommandPath -Name 'dotnet.exe'
$git = Get-RequiredCommandPath -Name 'git.exe'
$npm = Get-RequiredCommandPath -Name 'npm.cmd'

Invoke-CheckedCommand -FilePath $dotnet -ArgumentList @(
    'restore', $solutionPath,
    '-p:Platform=x64',
    '-p:TreatWarningsAsErrors=true',
    '--verbosity', 'minimal'
) -WorkingDirectory $repoRoot -Description '.NET dependency restore'

Invoke-CheckedCommand -FilePath $npm -ArgumentList @('ci') `
    -WorkingDirectory $webRoot -Description 'Web dependency restore'

if (-not (Test-Path -LiteralPath $workerPython -PathType Leaf)) {
    $pythonLauncher = Get-RequiredCommandPath -Name 'py.exe'
    Invoke-CheckedCommand -FilePath $pythonLauncher -ArgumentList @(
        '-3.12', '-m', 'venv', '.venv'
    ) -WorkingDirectory $workerRoot -Description 'Python 3.12 virtual environment creation'
}

foreach ($lockPath in @($workerRuntimeLock, $workerTestLock)) {
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw "Python dependency lock is missing: $lockPath"
    }
}

Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @(
    '-m', 'pip', 'install', '--disable-pip-version-check',
    '--require-hashes', '--only-binary=:all:',
    '-r', $workerTestLock
) -WorkingDirectory $workerRoot -Description 'Hash-locked Python dependency restore'

Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @(
    '-m', 'pip', 'install', '--disable-pip-version-check',
    '--no-index', '--no-deps', '--no-build-isolation', '-e', '.'
) -WorkingDirectory $workerRoot -Description 'Local Python worker installation'

Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @(
    '-m', 'pip', 'check'
) -WorkingDirectory $workerRoot -Description 'Python dependency consistency verification'

Invoke-CheckedCommand -FilePath $npm -ArgumentList @('run', 'typecheck') `
    -WorkingDirectory $webRoot -Description 'Web TypeScript verification'
Invoke-CheckedCommand -FilePath $npm -ArgumentList @('run', 'build') `
    -WorkingDirectory $webRoot -Description 'Web production build'

$pythonVersionJson = & $workerPython -c 'import json, struct, sys; print(json.dumps({"implementation": sys.implementation.name, "major": sys.version_info.major, "minor": sys.version_info.minor, "platform": sys.platform, "pointerBits": struct.calcsize("P") * 8}))'
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the worker Python version.'
}
$pythonVersion = $pythonVersionJson | ConvertFrom-Json
if (
    $pythonVersion.implementation -ne 'cpython' -or
    $pythonVersion.major -ne 3 -or
    $pythonVersion.minor -ne 12 -or
    $pythonVersion.platform -ne 'win32' -or
    $pythonVersion.pointerBits -ne 64
) {
    throw "Worker CPython 3.12 for Windows x64 is required; found $($pythonVersion.implementation) $($pythonVersion.major).$($pythonVersion.minor), $($pythonVersion.platform), $($pythonVersion.pointerBits)-bit."
}

$previousConfiguration = $env:Configuration
try {
    $env:Configuration = $Configuration
    Invoke-CheckedCommand -FilePath $dotnet -ArgumentList @(
        'format', $solutionPath,
        '--verify-no-changes',
        '--no-restore',
        '--verbosity', 'minimal'
    ) -WorkingDirectory $repoRoot -Description '.NET formatting and analyzer verification'
}
finally {
    $env:Configuration = $previousConfiguration
}

Invoke-CheckedCommand -FilePath $dotnet -ArgumentList @(
    'build', $solutionPath,
    '--configuration', $Configuration,
    '--no-restore',
    '-p:Platform=x64',
    '-p:TreatWarningsAsErrors=true',
    '-warnaserror',
    '--verbosity', 'minimal'
    $runtimePayloadArguments
) -WorkingDirectory $repoRoot -Description '.NET build'

$pythonFiles = @(& $git -C $repoRoot ls-files -- '*.py')
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked Python files.'
}
foreach ($relativePath in $pythonFiles) {
    Invoke-CheckedCommand -FilePath $workerPython -ArgumentList @(
        '-m', 'py_compile', (Join-Path $repoRoot $relativePath)
    ) -WorkingDirectory $repoRoot -Description "Python syntax verification: $relativePath"
}

Test-PowerShellSource -GitPath $git
Write-Information 'Build and static analysis completed successfully.' -InformationAction Continue
