#Requires -Version 7.0
# FR-064 AC-041 / FR-132 AC-139.
# This path intentionally participates in the repository's existing
# system_test.serial_patterns ("tests/system/e2e/"). Concurrent dotnet builds
# of a WinUI project share generated XAML files even when filters differ.
param([Parameter(Mandatory)][ValidateSet('AC-041','AC-139')][string]$Ac)
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
try {
    $filter = if ($Ac -eq 'AC-041') { 'FullyQualifiedName~FR064_AC041' } else { 'FullyQualifiedName~FR132_AC139' }
    & dotnet test --project tests/system/contract/HubRadio.System.Contracts.csproj -c Release --filter $filter
    exit $LASTEXITCODE
} finally { Pop-Location }
