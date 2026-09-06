#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'OSL013:HOST Windows x64 PowerShell 7+ Core x64 is required.'
}

function Get-AbsolutePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Trim() -or
        $Path -notmatch '^[A-Za-z]:[\\/]') {
        throw 'OSL013:INPUT CandidatePath must be a canonical absolute local path.'
    }
    $full = [IO.Path]::GetFullPath($Path.Replace('/', '\'))
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($full, $Path.TrimEnd('\'))) {
        throw 'OSL013:INPUT CandidatePath must be canonical.'
    }
    return $full
}

function Assert-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw 'OSL013:LAYOUT Required candidate directory is missing.' }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'OSL013:FILE Required candidate file is missing.' }
}

function Get-FileEvidence([string]$Path) {
    Assert-File $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [pscustomobject]@{
            byteSize = $stream.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
    }
    finally { $stream.Dispose() }
}

function Read-Json([string]$Path) {
    Assert-File $Path
    try { return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -AsHashtable -NoEnumerate }
    catch { throw 'OSL013:JSON Candidate evidence is not valid UTF-8 JSON.' }
}

function Assert-Evidence([object]$Actual, [object]$Expected, [string]$Code) {
    if ($Actual.byteSize -ne $Expected.byteSize -or $Actual.sha256 -cne $Expected.sha256) { throw "OSL013:$Code File bytes do not match declared evidence." }
}

function Get-Relative([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\') -or $Path.StartsWith('./', [StringComparison]::Ordinal) -or
        $Path.Split('/').Contains('..')) { throw 'OSL013:PATH A relative candidate path is invalid.' }
    return $Path
}

function Get-Records([string]$Root) {
    $records = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(?:BuildLogs|ComponentInputs)(?:/|$)|(?i)(?:\.pubxml|\.csproj|\.targets|\.props|\.pdb)$|(^|/)(?:\.env(?:\..*)?)$') {
            throw 'OSL013:DEVELOPER Candidate contains forbidden development content or private build artifacts.'
        }
        $relative = Get-Relative $relative
        $evidence = Get-FileEvidence $file.FullName
        $records.Add($relative, [pscustomobject][ordered]@{ path = $relative; byteSize = $evidence.byteSize; sha256 = $evidence.sha256 })
    }
    return ,$records
}

function Get-Digest([object]$Records) {
    $text = [Text.StringBuilder]::new()
    foreach ($record in $Records.Values) {
        [void]$text.Append($record.path).Append([char]0).Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture)).Append(
            [char]0).Append($record.sha256).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant()
}

function Assert-RecordSet([object]$Declared, [object]$Actual, [string]$Code) {
    if ($Declared.Count -ne $Actual.Count) { throw "OSL013:$Code Declared and actual file sets differ." }
    foreach ($name in $Declared.Keys) {
        if (-not $Actual.ContainsKey($name) -or $Declared[$name].byteSize -ne $Actual[$name].byteSize -or
            $Declared[$name].sha256 -cne $Actual[$name].sha256) { throw "OSL013:$Code Declared and actual file hashes differ." }
    }
}

function Assert-ManifestRecord([object]$Record) {
    if ($Record -isnot [Collections.IDictionary] -or $Record.path -isnot [string] -or
        ($Record.byteSize -isnot [int] -and $Record.byteSize -isnot [long]) -or $Record.byteSize -lt 0 -or
        $Record.sha256 -isnot [string] -or $Record.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'OSL013:MANIFEST Invalid file record.' }
    if ((Get-Relative $Record.path) -cne $Record.path) { throw 'OSL013:MANIFEST Noncanonical file record.' }
}

$root = Get-AbsolutePath $CandidatePath
Assert-Directory $root
$top = @(Get-ChildItem -LiteralPath $root -Force)
# Keep the public candidate boundary exact: private evidence is the sibling
# `${CandidatePath}.private`, never a child of the candidate itself.
$allowed = @('App', 'Contents', 'Evidence', 'Sbom')
if ($top.Count -ne $allowed.Count -or @($top | Where-Object { $_ -isnot [IO.DirectoryInfo] -or $_.Name -cnotin $allowed }).Count -ne 0) {
    throw 'OSL013:PUBLIC_LAYOUT Private logs and non-public candidate content must be excluded.'
}
foreach ($name in $allowed) { Assert-Directory (Join-Path $root $name) }

$appRoot = Join-Path $root 'App'
$appEntries = @(Get-ChildItem -LiteralPath $appRoot -Force)
if ($appEntries.Count -ne 1 -or $appEntries[0] -isnot [IO.FileInfo] -or $appEntries[0].Name -cne 'HubRadio.App.exe') { throw 'OSL013:APP_LAYOUT App must contain exactly one HubRadio.App.exe.' }
$appPath = $appEntries[0].FullName
$releasePath = Join-Path $root 'Evidence/portable-release.json'
$payloadPath = Join-Path $root 'Contents/Evidence/portable-payload.json'
$release = Read-Json $releasePath
$payload = Read-Json $payloadPath
if ($release.schemaVersion -cne '1.0.0' -or $release.taskId -cne 'OSL-012' -or $release.architecture -cne 'x64' -or
    $release.releaseApproved -isnot [bool] -or $release.releaseApproved -ne $false) { throw 'OSL013:RELEASE Release evidence is not an unapproved OSL-012 x64 candidate.' }
if ($release.candidate.path -cne 'App/HubRadio.App.exe' -or $release.contents.path -cne 'Contents' -or
    $release.sbom.format -cne 'SPDX-2.2' -or $release.sbom.path -notmatch '^Sbom/.+\.json$') { throw 'OSL013:RELEASE Release evidence paths or format are invalid.' }
$actualCandidate = Get-FileEvidence $appPath
Assert-Evidence $actualCandidate $release.candidate 'RELEASE_CANDIDATE'

$actualContents = Get-Records (Join-Path $root 'Contents')
$declaredContents = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
if ($release.contents.files -isnot [array]) { throw 'OSL013:CONTENTS Release Contents file list is missing.' }
foreach ($record in $release.contents.files) { Assert-ManifestRecord $record; $declaredContents.Add($record.path, $record) }
Assert-RecordSet $declaredContents $actualContents 'CONTENTS'
if ($release.contents.fileCount -ne $actualContents.Count -or $release.contents.totalBytes -ne [long](($actualContents.Values | Measure-Object byteSize -Sum).Sum) -or
    $release.contents.artifactSetSha256 -cne (Get-Digest $actualContents)) { throw 'OSL013:CONTENTS Contents count, bytes or digest do not match.' }

if ($payload.schemaVersion -cne '1.0.0' -or $payload.taskId -cne 'OSL-009' -or $payload.releaseApproved -isnot [bool] -or $payload.releaseApproved -ne $false -or
    $payload.files -isnot [array]) { throw 'OSL013:PAYLOAD Payload manifest is invalid or approved.' }
$actualPayload = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($record in $payload.files) { Assert-ManifestRecord $record; $actualPayload.Add($record.path, $record) }
$payloadContents = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($name in $actualContents.Keys) {
    if ($name -in @('Worker', 'Tools', 'ModelCatalog', 'WebView2Runtime', 'Licenses', 'Evidence') -or
        $name.StartsWith('Worker/', [StringComparison]::Ordinal) -or
        $name.StartsWith('Tools/', [StringComparison]::Ordinal) -or
        $name.StartsWith('ModelCatalog/', [StringComparison]::Ordinal) -or
        $name.StartsWith('WebView2Runtime/', [StringComparison]::Ordinal) -or
        $name.StartsWith('Licenses/', [StringComparison]::Ordinal) -or
        $name.StartsWith('Evidence/', [StringComparison]::Ordinal)) {
        if ($name -cne 'Evidence/portable-payload.json') {
            $payloadContents.Add($name, $actualContents[$name])
        }
    }
}
Assert-RecordSet $actualPayload $payloadContents 'PAYLOAD'
if ($payload.fileCount -ne $actualPayload.Count -or $payload.totalBytes -ne [long](($actualPayload.Values | Measure-Object byteSize -Sum).Sum) -or
    $payload.artifactSetSha256 -cne (Get-Digest $actualPayload)) { throw 'OSL013:PAYLOAD Payload relation or digest does not match Contents.' }
if ($release.preparedPayload.manifestPath -cne 'Contents/Evidence/portable-payload.json' -or
    $release.preparedPayload.manifestSha256 -cne (Get-FileEvidence $payloadPath).sha256 -or
    $release.preparedPayload.artifactSetSha256 -cne $payload.artifactSetSha256) { throw 'OSL013:PAYLOAD Release payload relation does not match.' }

$sbomPath = Join-Path $root $release.sbom.path.Replace('/', '\')
$sbomEvidence = Get-FileEvidence $sbomPath
Assert-Evidence $sbomEvidence $release.sbom 'SBOM'
$sbom = Read-Json $sbomPath
if ($sbom.spdxVersion -cne 'SPDX-2.2' -or $sbom.files -isnot [array] -or $sbom.files.Count -ne $actualContents.Count) { throw 'OSL013:SBOM SPDX file list is incomplete.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $sbom.files) {
    $name = [string]$file.fileName
    if ($name.StartsWith('./', [StringComparison]::Ordinal)) { $name = $name.Substring(2) }
    $name = Get-Relative $name
    $hashes = @($file.checksums | Where-Object { $_.algorithm -ceq 'SHA256' })
    if (-not $seen.Add($name) -or $hashes.Count -ne 1 -or -not $actualContents.ContainsKey($name) -or
        $hashes[0].checksumValue -ine $actualContents[$name].sha256) { throw 'OSL013:SBOM SPDX paths or SHA-256 hashes do not match Contents.' }
}
if ($seen.Count -ne $actualContents.Count) { throw 'OSL013:SBOM SPDX file list contains missing or extra paths.' }

$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($appPath))
try {
    if ($reader.BaseStream.Length -lt 64 -or $reader.ReadByte() -ne 0x4d -or $reader.ReadByte() -ne 0x5a) {
        throw 'OSL013:APP_BUNDLE Candidate is not a Windows executable bundle.'
    }
    $reader.BaseStream.Position = 0
    $prefixLength = [int][Math]::Min(64MB, $reader.BaseStream.Length)
    $prefix = $reader.ReadBytes($prefixLength)
    $markerBytes = [Convert]::FromHexString('8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE')
    $marker = -1
    for ($i = 16; $i -le $prefix.Length - $markerBytes.Length; $i++) {
        $matches = $true
        for ($j = 0; $j -lt $markerBytes.Length; $j++) { if ($prefix[$i + $j] -ne $markerBytes[$j]) { $matches = $false; break } }
        if ($matches) { $marker = $i; break }
    }
    if ($marker -lt 16 -or $marker % 2 -ne 0) { throw 'OSL013:APP_BUNDLE Candidate is not the expected single-file bundle.' }
    $offset = [BitConverter]::ToInt64($prefix, $marker - 8)
    if ($offset -le 0 -or $offset -ge $reader.BaseStream.Length - 12) { throw 'OSL013:APP_BUNDLE Candidate bundle header offset is invalid.' }
    $reader.BaseStream.Position = $offset
    if ($reader.ReadUInt32() -ne 6 -or $reader.ReadUInt32() -ne 0 -or $reader.ReadInt32() -le 0) { throw 'OSL013:APP_BUNDLE Candidate must contain a nonempty v6 bundle header.' }
}
finally { $reader.Dispose() }
Write-Output ([ordered]@{ candidatePath = 'App/HubRadio.App.exe'; releaseApproved = $false; contentsFileCount = $actualContents.Count; sbomPath = $release.sbom.path } | ConvertTo-Json -Compress)
