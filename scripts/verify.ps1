#Requires -Version 7.0

<#
.SYNOPSIS
Builds, runs static checks and runs every automated test; exits non-zero on any failure.

.DESCRIPTION
1. Checks the management data. The requirements are the index docs/requirement-definition.md
   plus the boundary files docs/requirements/*.md.
   - Each requirement family in -RequirementFamilies has a section ("## FR\-NNN"), appears in
     docs/catalog.md, and appears in at least one automated test file (comment or name), so a
     requirement ID can be traced to its tests. FR-036 to FR-038 predate this check and their
     tests carry no IDs yet, so they are not listed by default.
   - Requirement sections, index rows, acceptance-criteria IDs and questionnaire items (Q-NNN)
     are unique, and every requirement section has exactly one index row.
   - Every FR-/NFR- ID named in an acceptance-criteria heading or row has a section.
   - Every requirement family named in docs/catalog.md has a section, and the catalog's
     決定状態 equals the index's; every relative link in docs/catalog.md points to an existing file.
2. Runs scripts/test.ps1, which builds the solution and the Web UI (TypeScript type check included)
   and runs the .NET, Web, accessibility and worker tests.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string[]]$RequirementFamilies = @('FR-039', 'FR-040', 'FR-041', 'FR-042', 'FR-043', 'FR-045', 'FR-046', 'FR-047', 'FR-049', 'FR-050', 'FR-051', 'FR-052', 'FR-053', 'FR-054', 'FR-055', 'FR-056', 'FR-057', 'FR-058', 'FR-059', 'FR-060', 'FR-061', 'FR-062', 'FR-063')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-RequirementTraceability {
    $catalog = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\catalog.md') -Raw
    $index = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\requirement-definition.md') -Raw
    $boundaryFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs\requirements') -File -Filter *.md)
    $requirements = (@($index) + @($boundaryFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw })) -join "`n"
    $testFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -Recurse -File -Include *.cs, *.mjs
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\HubRadio.Web\src') -Recurse -File -Include *.test.ts, *.test.tsx
    ) | Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' }
    $testText = ($testFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

    $failures = @()
    # The requirements documents escape the hyphen in headings: "## FR\-040".
    $sections = @([regex]::Matches($requirements, '(?m)^## ((?:FR|NFR|BR))\\-(\d{3}) ') | ForEach-Object { "$($_.Groups[1].Value)-$($_.Groups[2].Value)" })
    $sectionSet = [Collections.Generic.HashSet[string]]::new([string[]]$sections)
    foreach ($duplicate in ($sections | Group-Object | Where-Object Count -gt 1)) { $failures += "$($duplicate.Name) has more than one section" }

    $indexStates = @{}
    foreach ($row in [regex]::Matches($index, '(?m)^\| ((?:FR|NFR|BR)-\d{3}) \| [^|]* \| ([^|]+?) \| [^|]+ \| [^\r\n]*\|\r?$')) {
        $id = $row.Groups[1].Value
        if ($indexStates.ContainsKey($id)) { $failures += "$id has more than one row in the index" }
        $indexStates[$id] = $row.Groups[2].Value.Trim()
        if (-not $sectionSet.Contains($id)) { $failures += "The index lists $id, which has no section" }
    }
    foreach ($id in $sectionSet) { if (-not $indexStates.ContainsKey($id)) { $failures += "$id has no row in the index docs/requirement-definition.md" } }

    foreach ($family in $RequirementFamilies) {
        if (-not $sectionSet.Contains($family)) { $failures += "$family has no section in docs/requirements" }
        if (-not $catalog.Contains($family)) { $failures += "$family is missing from docs/catalog.md" }
        if (-not $testText.Contains($family)) { $failures += "$family does not appear in any automated test" }
    }

    $acceptanceIds = @(
        [regex]::Matches($requirements, '(?m)^\| (AC-\d{3}(?:\.\d+)?) \|') | ForEach-Object { $_.Groups[1].Value }
        [regex]::Matches($requirements, '(?m)^## AC\\-(\d{3})') | ForEach-Object { "AC-$($_.Groups[1].Value)#heading" }
        [regex]::Matches($requirements, '(?m)^### (AC-\d{3})') | ForEach-Object { "$($_.Groups[1].Value)#heading" }
    )
    foreach ($duplicate in ($acceptanceIds | Group-Object | Where-Object Count -gt 1)) { $failures += "$($duplicate.Name.Replace('#heading', ' (heading)')) is defined more than once" }
    $questions = @([regex]::Matches($requirements, '(?m)^#{3,4} (Q-\d{3}) ') | ForEach-Object { $_.Groups[1].Value })
    foreach ($duplicate in ($questions | Group-Object | Where-Object Count -gt 1)) { $failures += "Questionnaire item $($duplicate.Name) is defined more than once" }

    $acceptanceReferences = @(
        [regex]::Matches($requirements, '(?m)^#{2,3} AC[^\r\n]*') | ForEach-Object Value
        [regex]::Matches($requirements, '(?m)^\| AC-\d{3}(?:\.\d+)? \| ([^|]+) \|') | ForEach-Object { $_.Groups[1].Value }
    )
    foreach ($reference in $acceptanceReferences) {
        foreach ($id in [regex]::Matches($reference, '(?:FR|NFR)-\d{3}')) {
            if (-not $sectionSet.Contains($id.Value)) { $failures += "An acceptance criterion refers to $($id.Value), which has no section: $($reference.Trim())" }
        }
    }

    foreach ($row in [regex]::Matches($catalog, '(?m)^\| ((?:FR|NFR)-\d{3}[^|]*) \| [^|]+ \| ([^|]+) \|')) {
        $state = $row.Groups[2].Value.Trim()
        foreach ($id in ([regex]::Matches($row.Groups[1].Value, '(?:FR|NFR)-\d{3}') | ForEach-Object Value | Select-Object -Unique)) {
            if (-not $sectionSet.Contains($id)) { $failures += "docs/catalog.md lists $id, which has no section" }
            elseif ($indexStates[$id] -ne $state) { $failures += "docs/catalog.md shows $id as '$state', but the index shows '$($indexStates[$id])'" }
        }
    }

    $docs = Join-Path $repoRoot 'docs'
    foreach ($match in [regex]::Matches($catalog, '\]\((?!https?:)([^)#]+)(?:#[^)]*)?\)')) {
        $target = Join-Path $docs ([uri]::UnescapeDataString($match.Groups[1].Value))
        if (-not (Test-Path -LiteralPath $target)) { $failures += "docs/catalog.md links to a missing file: $($match.Groups[1].Value)" }
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        throw 'Requirement traceability check failed.'
    }

    Write-Information "Requirement traceability OK: $($RequirementFamilies -join ', ')" -InformationAction Continue
}

Test-RequirementTraceability

& (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "test.ps1 failed with exit code $LASTEXITCODE."
}
