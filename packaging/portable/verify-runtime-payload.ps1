#Requires -Version 7.0

# Offline data-only verification. Never run the App, Python, FFmpeg or downloads.
# A prepared inventory is build identity, not an independent binary trust anchor.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PayloadPath,
    [string]$CopyDestination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows) { throw 'RUNTIME:HOST Windows PowerShell 7+ Core is required.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$roots = @('Worker', 'Tools', 'ModelCatalog', 'Fonts', 'WebView2Runtime', 'Licenses', 'Evidence')
$copyRoots = @('Worker', 'Tools', 'ModelCatalog', 'Fonts', 'Licenses', 'Evidence')

function Assert-SafePath([string]$Path, [bool]$File) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]') { throw 'RUNTIME:PATH Expected a local absolute path.' }
    $chain = [Collections.Generic.Stack[string]]::new()
    for ($current = $Path; $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) { $chain.Push($current) }
    foreach ($current in $chain) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0 -or
            (($item -is [IO.FileInfo]) -ne ($File -and $current -ceq $Path))) { throw 'RUNTIME:PATH Unsafe file or ancestor.' }
    }
}

function Get-Evidence([string]$Path) {
    Assert-SafePath $Path $true
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        return [pscustomobject]@{
            byteSize = $stream.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
    }
    finally { $stream.Dispose() }
}

function Read-Manifest([string]$Path) {
    Assert-SafePath $Path $true
    if ((Get-Item -LiteralPath $Path -Force).Length -gt 4MB) { throw 'RUNTIME:MANIFEST Oversized manifest.' }
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -AsHashtable -NoEnumerate
}

function Get-RelativePath([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or $Name -match '[\\:<>"|?*\x00-\x1f\x7f]') {
        throw 'RUNTIME:MANIFEST Invalid relative path.'
    }
    foreach ($part in $Name.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') { throw 'RUNTIME:MANIFEST Unsafe path segment.' }
    }
    return $Name
}

function Get-Records([string]$Root) {
    $records = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-SafePath $directory $false
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force)) {
            Assert-SafePath $entry.FullName ($entry -is [IO.FileInfo])
            if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry.FullName); continue }
            $relative = Get-RelativePath ([IO.Path]::GetRelativePath($Root, $entry.FullName).Replace('\', '/'))
            if (-not $names.Add($relative)) { throw 'RUNTIME:MANIFEST Duplicate destination.' }
            $evidence = Get-Evidence $entry.FullName
            $records.Add($relative, [pscustomobject]@{ path = $relative; byteSize = $evidence.byteSize; sha256 = $evidence.sha256 })
        }
    }
    return ,$records
}

function Get-SetDigest([object[]]$Records) {
    $text = [Text.StringBuilder]::new()
    foreach ($record in $Records) {
        [void]$text.Append($record.path).Append([char]0).Append(([long]$record.byteSize).ToString(
            [Globalization.CultureInfo]::InvariantCulture)).Append([char]0).Append($record.sha256).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant()
}

function Assert-Match([object]$Actual, [object]$Expected) {
    if ($null -eq $Actual -or $null -eq $Expected -or $Actual.byteSize -ne $Expected.byteSize -or $Actual.sha256 -cne $Expected.sha256) {
        throw 'RUNTIME:HASH Resource differs from its build input contract.'
    }
}

if (-not [IO.Path]::IsPathFullyQualified($PayloadPath)) { throw 'RUNTIME:PATH PayloadPath must be absolute.' }
$PayloadPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PayloadPath))
Assert-SafePath $PayloadPath $false
$top = @(Get-ChildItem -LiteralPath $PayloadPath -Force)
if ($top.Count -ne $roots.Count -or @($top | Where-Object { $_ -isnot [IO.DirectoryInfo] -or $_.Name -cnotin $roots }).Count -ne 0) {
    throw 'RUNTIME:LAYOUT Expected the complete prepared payload, including Fonts.'
}
$manifest = Read-Manifest (Join-Path $PayloadPath 'Evidence/portable-payload.json')
if ($manifest -isnot [Collections.IDictionary] -or $manifest['schemaVersion'] -cne '1.0.0' -or $manifest['taskId'] -cne 'OSL-009' -or
    $manifest['architecture'] -cne 'x64' -or $manifest['releaseApproved'] -isnot [bool] -or $manifest['releaseApproved'] -ne $false -or
    $manifest['appPublishRequired'] -isnot [bool] -or $manifest['appPublishRequired'] -ne $true -or
    ($manifest['rootDirectories'] -join '/') -cne ($roots -join '/') -or $manifest['files'] -isnot [array]) {
    throw 'RUNTIME:MANIFEST Expected unapproved prepared component evidence.'
}
$actual = Get-Records $PayloadPath
if ($actual.ContainsKey('Evidence/application-build.json')) { throw 'RUNTIME:BUILD_EVIDENCE App evidence must be generated after compiling, never staged as component input.' }
$declared = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$previous = $null
[long]$total = 0
foreach ($record in $manifest['files']) {
    $name = Get-RelativePath $record['path']
    if ($name -ieq 'Evidence/portable-payload.json' -or $name.Split('/')[0] -cnotin $roots -or -not $names.Add($name) -or
        ($record['byteSize'] -isnot [long] -and $record['byteSize'] -isnot [int]) -or $record['byteSize'] -lt 0 -or
        $record['sha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $name) -ge 0)) { throw 'RUNTIME:MANIFEST Invalid ordered inventory.' }
    Assert-Match $actual[$name] $record
    $declared.Add($name, [pscustomobject]@{ path = $name; byteSize = [long]$record['byteSize']; sha256 = $record['sha256'] })
    $total += [long]$record['byteSize']
    $previous = $name
}
if ($actual.Count -ne $declared.Count + 1 -or $declared.Count -ne $manifest['fileCount'] -or $total -ne $manifest['totalBytes'] -or
    (Get-SetDigest @($declared.Values)) -cne $manifest['artifactSetSha256']) { throw 'RUNTIME:MANIFEST Missing, extra or changed payload files.' }

# Repository source contracts, NOT payload-self-reported hashes, authorize input bytes.
$sources = [ordered]@{
    'Evidence/python-runtime-manifest.json' = 'packaging/python/runtime-manifest.json'
    'Evidence/ffmpeg-manifest.json' = 'packaging/ffmpeg/manifest.json'
    'Evidence/model-catalog.json' = 'packaging/models/catalog.json'
    'Evidence/webview2-runtime-manifest.json' = 'packaging/webview2/runtime-manifest.json'
    'Evidence/web-dependency-manifest.json' = 'packaging/web/dependency-manifest.json'
    'Evidence/font-manifest.json' = 'packaging/fonts/font-manifest.json'
    'Fonts/font-manifest.json' = 'packaging/fonts/font-manifest.json'
    'ModelCatalog/catalog.json' = 'packaging/models/catalog.json'
    'Licenses/THIRD-PARTY-NOTICES-NONFINAL.md' = 'docs/dev/licenses/THIRD-PARTY-NOTICES.md'
}
foreach ($name in $sources.Keys) { Assert-Match $actual[$name] (Get-Evidence (Join-Path $repoRoot $sources[$name])) }
if (@($actual.Keys | Where-Object { $_.StartsWith('Evidence/', [StringComparison]::Ordinal) }).Count -ne 7 -or
    @($actual.Keys | Where-Object { $_.StartsWith('Licenses/', [StringComparison]::Ordinal) }).Count -ne 5) {
    throw 'RUNTIME:NOTICES_SET Unexpected evidence or notice files; self-inventory is not approval.'
}
$font = Read-Manifest (Join-Path $repoRoot 'packaging/fonts/font-manifest.json')
if ($font['schemaVersion'] -ne 1 -or $font['family'] -cne 'Noto Sans JP' -or $font['style'] -cne 'Regular' -or
    $font['version'] -cne '2.004' -or $font['sourceCommit'] -cne '523d033d6cb47f4a80c58a35753646f5c3608a78' -or
    $font['sourcePath'] -cne 'Sans/SubsetOTF/JP/NotoSansJP-Regular.otf' -or
    $font['relativePath'] -cne 'NotoSansJP-Regular.otf' -or $font['sha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or $font['byteSize'] -le 0 -or
    $font['license']['relativePath'] -cne 'LICENSE' -or $font['license']['sourcePath'] -cne 'LICENSE' -or $font['license']['spdxId'] -cne 'OFL-1.1' -or
    $font['license']['sha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or $font['license']['byteSize'] -le 0) { throw 'RUNTIME:FONT_PIN Fill measured official font/license SHA-256 and byte sizes before staging.' }
Assert-Match $actual['Fonts/NotoSansJP-Regular.otf'] $font
Assert-Match $actual['Fonts/LICENSE'] $font['license']
Assert-Match $actual['Licenses/NotoSansJP-OFL.txt'] $font['license']
if (@($actual.Keys | Where-Object { $_.StartsWith('Fonts/', [StringComparison]::Ordinal) }).Count -ne 3) { throw 'RUNTIME:FONT_SET Unexpected font search-directory content.' }

$pythonSource = Read-Manifest (Join-Path $repoRoot 'packaging/python/runtime-manifest.json')
$wheelMatrix = Get-Evidence (Join-Path $repoRoot 'src/HubRadio.Worker/wheel-matrix.json')
if ($wheelMatrix.sha256 -cne $pythonSource['wheelInput']['matrixSha256'] -or
    $pythonSource['workerSource']['root'] -cne 'src/HubRadio.Worker/src' -or
    $pythonSource['isolation']['importSiteEnabled'] -ne $false -or
    $pythonSource['isolation']['pipIncludedAtRuntime'] -ne $false -or
    $pythonSource['isolation']['systemPathRequired'] -ne $false) { throw 'RUNTIME:WORKER_INPUT Invalid locked dependency or isolation input.' }
$sourceFiles = @($pythonSource['workerSource']['files'])
$sourceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $sourceFiles) {
    $name = Get-RelativePath $file['path']
    if (-not $sourceNames.Add($name)) { throw 'RUNTIME:WORKER_SOURCE Duplicate Worker source input.' }
    Assert-Match (Get-Evidence (Join-Path $repoRoot ('src/HubRadio.Worker/src/' + $name))) $file
    Assert-Match $actual['Worker/Lib/site-packages/' + $name] $file
}
$currentSource = Get-Records (Join-Path $repoRoot 'src/HubRadio.Worker/src')
$currentPython = @($currentSource.Keys | Where-Object { [IO.Path]::GetExtension($_) -ceq '.py' })
if ($currentPython.Count -ne $sourceFiles.Count -or @($currentPython | Where-Object { -not $sourceNames.Contains($_) }).Count -ne 0) {
    throw 'RUNTIME:WORKER_SOURCE Rebuild Worker and update its measured packaging input; do not use an old runtime payload.'
}
$worker = Read-Manifest (Join-Path $PayloadPath 'Worker/runtime-files.json')
$output = $pythonSource['output']
if ($worker['sourceManifestSha256'] -cne $actual['Evidence/python-runtime-manifest.json'].sha256 -or
    $worker['schemaVersion'] -cne '1.0.0' -or $worker['runtimeIdentity'] -cne 'CPython' -or $worker['runtimeVersion'] -cne '3.12.10' -or
    $worker['architecture'] -cne 'x64' -or $worker['fileCount'] -ne $output['expectedFileCount'] -or
    $worker['totalBytes'] -ne $output['expectedTotalBytes'] -or $worker['artifactSetSha256'] -cne $output['expectedArtifactSetSha256']) { throw 'RUNTIME:WORKER_BUILD Wrong Worker build input or output identity.' }
$workerRecords = @($worker['files'] | Sort-Object { $_['path'] } -CaseSensitive -Culture en-US)
$workerNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[long]$workerBytes = 0
foreach ($file in $workerRecords) {
    $name = Get-RelativePath $file['path']
    if (-not $workerNames.Add($name) -or $name -ieq 'runtime-files.json') { throw 'RUNTIME:WORKER_SET Invalid Worker inventory.' }
    Assert-Match $actual['Worker/' + $name] $file
    $workerBytes += [long]$file['byteSize']
}
if ($workerRecords.Count -ne $worker['fileCount'] -or $workerBytes -ne $worker['totalBytes'] -or
    @($actual.Keys | Where-Object { $_.StartsWith('Worker/', [StringComparison]::Ordinal) }).Count -ne $workerRecords.Count + 1 -or
    (Get-SetDigest $workerRecords) -cne $output['expectedArtifactSetSha256']) { throw 'RUNTIME:WORKER_SET Worker bytes are not the pinned build output.' }
Assert-Match $actual['Worker/python.exe'] ([pscustomobject]@{ byteSize = 104952L; sha256 = '4d6f5f81a4bca11191c4c7c6b43632694d0a4ce74e068619d8fdc161d469859a' })
foreach ($file in $pythonSource['runtime']['selectedSignedFiles']) { Assert-Match $actual['Worker/' + $file['name']] $file }
Assert-Match $actual['Worker/LICENSE.txt'] $pythonSource['runtime']['license']
Assert-Match $actual['Licenses/CPython-LICENSE.txt'] $pythonSource['runtime']['license']
$pth = [IO.File]::ReadAllLines((Join-Path $PayloadPath 'Worker/python312._pth'))
if (($pth -join "`n") -cne ($pythonSource['isolation']['pthLines'] -join "`n")) { throw 'RUNTIME:WORKER_ISOLATION The runtime must use only its package-local isolated paths.' }

$ffmpeg = Read-Manifest (Join-Path $repoRoot 'packaging/ffmpeg/manifest.json')
foreach ($role in @('ffmpeg', 'ffprobe')) {
    $selected = @($ffmpeg['selectedFiles'] | Where-Object { $_['role'] -ceq $role })
    if ($selected.Count -ne 1) { throw 'RUNTIME:TOOLS Invalid tool pin.' }
    Assert-Match $actual['Tools/' + $role + '.exe'] $selected[0]
}
$ffmpegLicense = @($ffmpeg['selectedFiles'] | Where-Object { $_['role'] -ceq 'license' })
if ($ffmpegLicense.Count -ne 1) { throw 'RUNTIME:TOOLS Missing tool license pin.' }
Assert-Match $actual['Licenses/FFmpeg-LICENSE.txt'] $ffmpegLicense[0]
if (@($actual.Keys | Where-Object { $_.StartsWith('Tools/', [StringComparison]::Ordinal) }).Count -ne 2) { throw 'RUNTIME:TOOLS Extra tool binaries are forbidden.' }
$catalog = Read-Manifest (Join-Path $repoRoot 'packaging/models/catalog.json')
$models = @($catalog['models'] | Where-Object { $_['runtimeEligible'] -eq $true })
if ($models.Count -ne 1 -or $models[0]['modelId'] -cne 'asr.faster-whisper-small.cpu-int8') { throw 'RUNTIME:MODEL Invalid eligible model set.' }
foreach ($model in $models) {
    foreach ($file in $model['bundle']['files']) { Assert-Match $actual['ModelCatalog/' + $model['bundle']['relativePath'] + '/' + $file['name']] $file }
}
if (@($actual.Keys | Where-Object { $_.StartsWith('ModelCatalog/', [StringComparison]::Ordinal) }).Count -ne 5) { throw 'RUNTIME:MODEL Unexpected model file set.' }

$webview = Read-Manifest (Join-Path $repoRoot 'packaging/webview2/runtime-manifest.json')
$webRecords = @($actual.Values | Where-Object { $_.path.StartsWith('WebView2Runtime/', [StringComparison]::Ordinal) } | ForEach-Object {
    [pscustomobject]@{ path = $_.path.Substring('WebView2Runtime/'.Length); byteSize = $_.byteSize; sha256 = $_.sha256 }
})
if ($webRecords.Count -ne $webview['output']['expectedFileCount'] -or
    (Get-SetDigest $webRecords) -cne $webview['output']['expectedArtifactSetSha256']) { throw 'RUNTIME:WEBVIEW2 Expanded vendor tree differs from its fixed input.' }
if ($actual['Licenses/WebView2-FIXED-LICENSE-PAGE.txt'].sha256 -cne $webview['runtime']['license']['evidence']['sha256']) { throw 'RUNTIME:WEBVIEW2_LICENSE Wrong license snapshot.' }

if (-not [string]::IsNullOrWhiteSpace($CopyDestination)) {
    if (-not [IO.Path]::IsPathFullyQualified($CopyDestination)) { throw 'RUNTIME:PATH CopyDestination must be absolute.' }
    $CopyDestination = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($CopyDestination))
    Assert-SafePath $CopyDestination $false
    foreach ($copyRoot in $copyRoots) {
        $copied = Get-Records (Join-Path $CopyDestination $copyRoot)
        if ($copyRoot -ceq 'Evidence') { [void]$copied.Remove('application-build.json') }
        $expectedNames = @($actual.Keys | Where-Object { $_.StartsWith($copyRoot + '/', [StringComparison]::Ordinal) })
        if ($copied.Count -ne $expectedNames.Count) { throw 'RUNTIME:COPY_SET Stale, missing or extra output resources; use a clean output directory.' }
        foreach ($name in $expectedNames) { Assert-Match $copied[$name.Substring($copyRoot.Length + 1)] $actual[$name] }
    }
}