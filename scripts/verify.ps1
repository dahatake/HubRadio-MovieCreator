#Requires -Version 7.0

<#
.SYNOPSIS
Builds, runs static checks and runs every automated test; exits non-zero on any failure.

.DESCRIPTION
1. Checks that each requirement family in -RequirementFamilies has a section in
   docs/requirement-definition.md, appears in docs/catalog.md, and appears in at least one
   automated test file (comment or name), so a requirement ID can be traced to its tests.
   FR-036 to FR-038 predate this check and their tests carry no IDs yet, so they are not
   listed by default.
2. Runs test.ps1, which builds the solution and the Web UI (TypeScript type check included)
   and runs the .NET, Web, accessibility and worker tests.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string[]]$RequirementFamilies = @('FR-039', 'FR-040', 'FR-041', 'FR-042', 'FR-043', 'FR-045', 'FR-046')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-RequirementTraceability {
    $catalog = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\catalog.md') -Raw
    $requirements = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\requirement-definition.md') -Raw
    $testFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -Recurse -File -Include *.cs, *.mjs
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\HubRadio.Web\src') -Recurse -File -Include *.test.ts, *.test.tsx
    ) | Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' }
    $testText = ($testFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

    $failures = @()
    foreach ($family in $RequirementFamilies) {
        # The requirements document escapes the hyphen in headings: "## FR\-040".
        $heading = '(?m)^## ' + $family.Replace('-', '\\?-')
        if ($requirements -notmatch $heading) { $failures += "$family has no section in docs/requirement-definition.md" }
        if (-not $catalog.Contains($family)) { $failures += "$family is missing from docs/catalog.md" }
        if (-not $testText.Contains($family)) { $failures += "$family does not appear in any automated test" }
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        throw 'Requirement traceability check failed.'
    }

    Write-Information "Requirement traceability OK: $($RequirementFamilies -join ', ')" -InformationAction Continue
}

Test-RequirementTraceability

& (Join-Path $repoRoot 'test.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "test.ps1 failed with exit code $LASTEXITCODE."
}
