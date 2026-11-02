#Requires -Version 7.0
# FR-064 AC-041 / FR-132 AC-139: historical entry point, now executable.
# The current Windows actor has no assigned application roles in an isolated
# temporary DB; the real authorization service handles the native requests.
$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
try {
    & dotnet test --project tests/system/contract/HubRadio.System.Contracts.csproj -c Release --filter 'FullyQualifiedName~PermissionContracts'
    exit $LASTEXITCODE
} finally { Pop-Location }
