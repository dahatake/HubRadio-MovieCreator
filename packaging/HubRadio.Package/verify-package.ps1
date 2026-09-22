#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$MakeAppxPath,

    [Parameter(Mandatory)]
    [string]$WorkspacePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'The HubRadio MSIX package must be verified on Windows x64.'
}

$packageProjectRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $packageProjectRoot '..\..'))
$workerVerifyScriptPath = Join-Path $repoRoot 'packaging\python\verify-runtime.ps1'
$webViewSmokeScriptPath = Join-Path $repoRoot 'tests\smoke\Test-WebViewStartup.ps1'
$sourceEvidence = [ordered]@{
    'ffmpeg-manifest.json' = Join-Path $repoRoot 'packaging\ffmpeg\manifest.json'
    'model-catalog.json' = Join-Path $repoRoot 'packaging\models\catalog.json'
    'python-runtime-manifest.json' = Join-Path $repoRoot 'packaging\python\runtime-manifest.json'
    'web-dependency-manifest.json' = Join-Path $repoRoot 'packaging\web\dependency-manifest.json'
}

function Get-NormalizedLocalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        $Path -cne $Path.Trim() -or
        -not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('//', [StringComparison]::Ordinal)
    ) {
        throw "$Name must be a nonblank fully qualified local path."
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if (
        [string]::IsNullOrEmpty($root) -or
        $fullPath.Substring($root.Length).Contains(':', [StringComparison]::Ordinal)
    ) {
        throw "$Name must not use a device namespace or alternate data stream."
    }

    return [IO.Path]::TrimEndingDirectorySeparator($fullPath)
}

function Assert-SafePathChain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$LeafFile
    )

    $current = $Path
    $target = $true
    while ($null -ne $current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A package verification path contains a reparse point.'
        }

        if ($target -and $LeafFile) {
            if (-not ($item -is [IO.FileInfo])) {
                throw 'A package verification input is not a regular file.'
            }
        }
        elseif (-not ($item -is [IO.DirectoryInfo])) {
            throw 'A package verification path is not a directory.'
        }

        $current = [IO.Path]::GetDirectoryName($current)
        $target = $false
    }
}

function Assert-NoReparseTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-SafePathChain -Path $Path
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force -Recurse)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The unpacked package contains a reparse point.'
        }
    }
}

function Remove-GeneratedTree {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (
        -not ($rootItem -is [IO.DirectoryInfo]) -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'The package verification workspace root is unsafe and requires reconciliation.'
    }

    if (-not $PSCmdlet.ShouldProcess($Path, 'Remove package verification workspace')) {
        return
    }

    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        $current = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The package verification workspace contains a reparse point and requires reconciliation.'
        }

        if ($current -is [IO.DirectoryInfo]) {
            Remove-GeneratedTree -Path $current.FullName -Confirm:$false
        }
        else {
            [IO.File]::SetAttributes($current.FullName, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($current.FullName)
        }
    }

    [IO.Directory]::Delete($Path, $false)
}

function Get-LowerSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ArtifactSetSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $builder = [Text.StringBuilder]::new()
    foreach ($record in @($Records | Sort-Object path -CaseSensitive)) {
        [void]$builder.Append([string]$record.path)
        [void]$builder.Append([char]0)
        [void]$builder.Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$record.sha256)
        [void]$builder.Append("`n")
    }

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
}

function Assert-UniqueJsonPropertySet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Text.Json.JsonElement]$Element,

        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "JSON evidence contains a duplicate property at $Path."
            }
            Assert-UniqueJsonPropertySet -Element $property.Value -Path ($Path + '.' + $property.Name)
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-UniqueJsonPropertySet -Element $item -Path ($Path + '[' + $index + ']')
            $index++
        }
    }
}

function Read-StrictJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 64
    $document = [Text.Json.JsonDocument]::Parse($text, $options)
    try {
        Assert-UniqueJsonPropertySet -Element $document.RootElement -Path '$'
    }
    finally {
        $document.Dispose()
    }

    return $text | ConvertFrom-Json
}

function Read-SafeXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-ExactPropertySet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Context
    )

    $actual = [string[]]@($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $sortedExpected = [string[]]@($Expected | Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $actual) -cne [string]::Join("`n", $sortedExpected)) {
        throw "$Context has an unexpected property set."
    }
}

function Assert-CanonicalRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        $Path -cne $Path.Trim() -or
        $Path.Contains('\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('/', [StringComparison]::Ordinal) -or
        $Path.EndsWith('/', [StringComparison]::Ordinal) -or
        $Path.Contains('//', [StringComparison]::Ordinal) -or
        $Path.Contains(':', [StringComparison]::Ordinal) -or
        [IO.Path]::IsPathFullyQualified($Path)
    ) {
        throw 'A package layout path is not canonical.'
    }

    foreach ($segment in $Path.Split('/')) {
        if ($segment -in @('.', '..') -or [string]::IsNullOrWhiteSpace($segment)) {
            throw 'A package layout path contains an unsafe segment.'
        }
    }
}

function Invoke-IsolatedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [int]$TimeoutSeconds = 60,

        [hashtable]$Environment = @{}
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $false)
    $startInfo.Environment.Clear()
    $startInfo.Environment['PATH'] = ''
    $startInfo.Environment['SystemRoot'] = $env:SystemRoot
    $startInfo.Environment['WINDIR'] = $env:WINDIR
    $startInfo.Environment['TEMP'] = $WorkingDirectory
    $startInfo.Environment['TMP'] = $WorkingDirectory
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'A package verification subprocess did not start.'
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'A package verification subprocess exceeded its timeout.'
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-PayloadRecordSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    return @(
        Get-ChildItem -LiteralPath $Root -File -Force -Recurse |
            Where-Object {
                [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') -notin @(
                    'AppxBlockMap.xml',
                    'Evidence/package-layout.json')
            } |
            ForEach-Object {
                [pscustomobject]@{
                    path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
                    byteSize = $_.Length
                    sha256 = Get-LowerSha256 -Path $_.FullName
                }
            } |
            Sort-Object path -CaseSensitive)
}

function Assert-PayloadMatchesLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object]$Layout
    )

    $expected = @($Layout.files | Sort-Object path -CaseSensitive)
    $actual = @(Get-PayloadRecordSet -Root $Root)
    if ($expected.Count -ne $actual.Count) {
        throw 'The unpacked package contains missing or extra payload files.'
    }

    for ($index = 0; $index -lt $expected.Count; $index++) {
        if (
            [string]$expected[$index].path -cne [string]$actual[$index].path -or
            [long]$expected[$index].byteSize -ne [long]$actual[$index].byteSize -or
            [string]$expected[$index].sha256 -cne [string]$actual[$index].sha256
        ) {
            throw 'An unpacked package payload file does not match package-layout.json.'
        }
    }

    if (
        [long](($actual | Measure-Object byteSize -Sum).Sum) -ne [long]$Layout.totalBytes -or
        (Get-ArtifactSetSha256 -Records $actual) -cne [string]$Layout.artifactSetSha256
    ) {
        throw 'The unpacked package payload aggregate is invalid.'
    }

    return [pscustomobject]@{
        FileCount = $actual.Count
        TotalBytes = [long](($actual | Measure-Object byteSize -Sum).Sum)
        ArtifactSetSha256 = Get-ArtifactSetSha256 -Records $actual
    }
}

$PackagePath = Get-NormalizedLocalPath -Path $PackagePath -Name 'PackagePath'
$MakeAppxPath = Get-NormalizedLocalPath -Path $MakeAppxPath -Name 'MakeAppxPath'
$WorkspacePath = Get-NormalizedLocalPath -Path $WorkspacePath -Name 'WorkspacePath'

foreach ($path in @($PackagePath, $MakeAppxPath, $workerVerifyScriptPath, $webViewSmokeScriptPath)) {
    Assert-SafePathChain -Path $path -LeafFile
}
foreach ($path in $sourceEvidence.Values) {
    Assert-SafePathChain -Path $path -LeafFile
}
if ([IO.Path]::GetExtension($PackagePath) -cne '.msix') {
    throw 'PackagePath must identify an MSIX file.'
}
if (Test-Path -LiteralPath $WorkspacePath) {
    throw 'WorkspacePath already exists and will not be overwritten.'
}
$workspaceParent = [IO.Path]::GetDirectoryName($WorkspacePath)
Assert-SafePathChain -Path $workspaceParent

$makeAppxItem = Get-Item -LiteralPath $MakeAppxPath -Force
if (
    $makeAppxItem.Length -ne 567328 -or
    (Get-LowerSha256 -Path $MakeAppxPath) -cne
        '9981418e9a18a9aa19bb69f835137a3bf8fe2e6fe7e18d5ca9b76191cc331f3f' -or
    (Get-AuthenticodeSignature -LiteralPath $MakeAppxPath).Status -ne
        [Management.Automation.SignatureStatus]::Valid
) {
    throw 'The fixed MakeAppx verifier executable is invalid.'
}

$packageSignature = Get-AuthenticodeSignature -LiteralPath $PackagePath
if ($packageSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
    throw 'PK-006 accepts only the explicitly unsigned development package.'
}
$packageItem = Get-Item -LiteralPath $PackagePath -Force
$packageByteSize = $packageItem.Length
$packageSha256 = Get-LowerSha256 -Path $PackagePath

$unpackedRoot = Join-Path $WorkspacePath 'unpacked'
$workerWorkspace = Join-Path $WorkspacePath 'worker-workspace'
$modelWorkspace = Join-Path $WorkspacePath 'model-workspace'
[IO.Directory]::CreateDirectory($WorkspacePath) | Out-Null
[IO.Directory]::CreateDirectory($workerWorkspace) | Out-Null
[IO.Directory]::CreateDirectory($modelWorkspace) | Out-Null

$verified = $false
try {
    $unpack = Invoke-IsolatedProcess `
        -FilePath $MakeAppxPath `
        -ArgumentList @('unpack', '/p', $PackagePath, '/d', $unpackedRoot, '/no') `
        -WorkingDirectory $WorkspacePath `
        -TimeoutSeconds 600
    if ($unpack.ExitCode -ne 0 -or -not [string]::IsNullOrEmpty($unpack.StandardError)) {
        throw 'MakeAppx did not unpack and validate the MSIX package.'
    }

    Assert-NoReparseTree -Path $unpackedRoot
    $signaturePath = Join-Path $unpackedRoot 'AppxSignature.p7x'
    $blockMapPath = Join-Path $unpackedRoot 'AppxBlockMap.xml'
    $layoutManifestPath = Join-Path $unpackedRoot 'Evidence\package-layout.json'
    if (Test-Path -LiteralPath $signaturePath) {
        throw 'The unsigned PK-006 package unexpectedly contains a signature.'
    }
    foreach ($required in @($blockMapPath, $layoutManifestPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw 'The unpacked package is missing required integrity evidence.'
        }
    }

    $layout = Read-StrictJson -Path $layoutManifestPath
    Assert-ExactPropertySet `
        -Value $layout `
        -Expected @(
            'schemaVersion',
            'packageIdentity',
            'packageVersion',
            'architecture',
            'signed',
            'releaseApproved',
            'fileCount',
            'totalBytes',
            'artifactSetSha256',
            'files') `
        -Context 'package-layout.json'
    if (
        $layout.schemaVersion -cne '1.0.0' -or
        $layout.packageIdentity -cne 'HubRadio.MovieCreator.Development' -or
        $layout.packageVersion -cne '0.1.2.0' -or
        $layout.architecture -cne 'x64' -or
        $layout.signed -ne $false -or
        $layout.releaseApproved -ne $false -or
        [string]$layout.artifactSetSha256 -notmatch '^[0-9a-f]{64}$'
    ) {
        throw 'The package layout metadata is invalid.'
    }

    $pathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $layoutPaths = [string[]]@($layout.files | ForEach-Object { [string]$_.path })
    foreach ($record in @($layout.files)) {
        Assert-ExactPropertySet `
            -Value $record `
            -Expected @('path', 'byteSize', 'sha256') `
            -Context 'package layout file record'
        Assert-CanonicalRelativePath -Path ([string]$record.path)
        if (
            -not $pathSet.Add([string]$record.path) -or
            [long]$record.byteSize -lt 0 -or
            [string]$record.sha256 -notmatch '^[0-9a-f]{64}$'
        ) {
            throw 'A package layout file record is invalid.'
        }
    }
    if (
        $layoutPaths -contains 'Evidence/package-layout.json' -or
        [string]::Join("`n", $layoutPaths) -cne
            [string]::Join("`n", @($layoutPaths | Sort-Object -CaseSensitive)) -or
        $layout.files.Count -ne [int]$layout.fileCount -or
        [long](($layout.files | Measure-Object byteSize -Sum).Sum) -ne [long]$layout.totalBytes -or
        (Get-ArtifactSetSha256 -Records @($layout.files)) -cne [string]$layout.artifactSetSha256
    ) {
        throw 'The package layout file aggregate is invalid.'
    }

    $initialPayload = Assert-PayloadMatchesLayout -Root $unpackedRoot -Layout $layout

    $topLevelDirectories = [string[]]@(
        Get-ChildItem -LiteralPath $unpackedRoot -Directory -Force |
            ForEach-Object Name |
            Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $topLevelDirectories) -cne [string]::Join("`n", @(
        'App', 'Assets', 'Evidence', 'Licenses', 'Models', 'Tools', 'Worker'))) {
        throw 'The package contains an unexpected top-level directory.'
    }
    $topLevelPayloadFiles = [string[]]@(
        Get-ChildItem -LiteralPath $unpackedRoot -File -Force |
            Where-Object Name -ne 'AppxBlockMap.xml' |
            ForEach-Object Name |
            Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $topLevelPayloadFiles) -cne
        [string]::Join("`n", @('AppxManifest.xml', 'PACKAGE-NOT-FOR-RELEASE.txt'))) {
        throw 'The package contains an unexpected top-level payload file.'
    }

    if (
        @(Get-ChildItem -LiteralPath $unpackedRoot -File -Force -Recurse |
            Where-Object Extension -in @('.pdb', '.map', '.pyc')).Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $unpackedRoot -Directory -Force -Recurse |
            Where-Object Name -eq '__pycache__').Count -ne 0
    ) {
        throw 'The unpacked package contains a development or mutable cache artifact.'
    }

    $blockMap = Read-SafeXml -Path $blockMapPath
    $blockMapNamespaces = [Xml.XmlNamespaceManager]::new($blockMap.NameTable)
    $blockMapNamespaces.AddNamespace('b', 'http://schemas.microsoft.com/appx/2010/blockmap')
    $blockMapRoot = $blockMap.SelectSingleNode('/b:BlockMap', $blockMapNamespaces)
    if (
        $null -eq $blockMapRoot -or
        $blockMapRoot.GetAttribute('HashMethod') -cne
            'http://www.w3.org/2001/04/xmlenc#sha256'
    ) {
        throw 'The package block map does not use SHA-256.'
    }
    $blockMapPaths = [string[]]@(
        $blockMap.SelectNodes('/b:BlockMap/b:File', $blockMapNamespaces) |
            ForEach-Object { $_.GetAttribute('Name').Replace('\', '/') } |
            Sort-Object -CaseSensitive)
    $expectedBlockMapPaths = [string[]]@(
        @($layoutPaths + 'Evidence/package-layout.json') | Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $blockMapPaths) -cne
        [string]::Join("`n", $expectedBlockMapPaths)) {
        throw 'The package block map file set does not match the payload.'
    }

    $manifestPath = Join-Path $unpackedRoot 'AppxManifest.xml'
    $manifest = Read-SafeXml -Path $manifestPath
    $manifestNamespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $manifestNamespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $manifestNamespaces.AddNamespace('u10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
    $manifestNamespaces.AddNamespace(
        'r',
        'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $manifestNamespaces)
    $targetFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $manifestNamespaces)
    $applications = @($manifest.SelectNodes('/f:Package/f:Applications/f:Application', $manifestNamespaces))
    $capabilities = @($manifest.SelectNodes('/f:Package/f:Capabilities/*', $manifestNamespaces))
    if (
        $null -eq $identity -or
        $identity.GetAttribute('Name') -cne [string]$layout.packageIdentity -or
        $identity.GetAttribute('Publisher') -cne 'CN=HubRadio MovieCreator Development' -or
        $identity.GetAttribute('Version') -cne [string]$layout.packageVersion -or
        $identity.GetAttribute('ProcessorArchitecture') -cne [string]$layout.architecture -or
        $null -eq $targetFamily -or
        $targetFamily.GetAttribute('Name') -cne 'Windows.Desktop' -or
        $targetFamily.GetAttribute('MinVersion') -cne '10.0.26100.0' -or
        $targetFamily.GetAttribute('MaxVersionTested') -cne '10.0.26100.0' -or
        $applications.Count -ne 1 -or
        $applications[0].GetAttribute('Id') -cne 'HubRadioMovieCreator' -or
        $applications[0].GetAttribute('Executable') -cne 'App\HubRadio.App.exe' -or
        $applications[0].GetAttribute('RuntimeBehavior', $manifestNamespaces.LookupNamespace('u10')) -cne
            'packagedClassicApp' -or
        $applications[0].GetAttribute('TrustLevel', $manifestNamespaces.LookupNamespace('u10')) -cne
            'mediumIL' -or
        $capabilities.Count -ne 1 -or
        $capabilities[0].NamespaceURI -cne $manifestNamespaces.LookupNamespace('r') -or
        $capabilities[0].LocalName -cne 'Capability' -or
        $capabilities[0].GetAttribute('Name') -cne 'runFullTrust'
    ) {
        throw 'The MSIX package manifest identity or full-trust entry point is invalid.'
    }

    $marker = [IO.File]::ReadAllText(
        (Join-Path $unpackedRoot 'PACKAGE-NOT-FOR-RELEASE.txt'),
        [Text.UTF8Encoding]::new($false, $true))
    foreach ($requiredText in @(
        'unsigned package',
        'not approved for installation',
        'Package signing',
        'clean production-channel Windows validation',
        'release approval remain open')) {
        if (-not $marker.Contains($requiredText, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package release-blocking marker is incomplete.'
        }
    }

    $evidenceFiles = [string[]]@(
        Get-ChildItem -LiteralPath (Join-Path $unpackedRoot 'Evidence') -File -Force |
            ForEach-Object Name |
            Sort-Object -CaseSensitive)
    $expectedEvidenceFiles = [string[]]@(
        @($sourceEvidence.Keys + 'package-layout.json') | Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $evidenceFiles) -cne [string]::Join("`n", $expectedEvidenceFiles)) {
        throw 'The package evidence directory contains missing or extra files.'
    }
    foreach ($entry in $sourceEvidence.GetEnumerator()) {
        if ((Get-LowerSha256 -Path (Join-Path $unpackedRoot ('Evidence\' + $entry.Key))) -cne
            (Get-LowerSha256 -Path $entry.Value)) {
            throw 'Packaged component evidence does not match the repository source.'
        }
    }

    $ffmpegManifest = Read-StrictJson -Path (Join-Path $unpackedRoot 'Evidence\ffmpeg-manifest.json')
    $ffmpegDestinations = [ordered]@{
        ffmpeg = 'Tools\ffmpeg.exe'
        ffprobe = 'Tools\ffprobe.exe'
        license = 'Licenses\FFmpeg-LICENSE.txt'
    }
    if (@($ffmpegManifest.selectedFiles).Count -ne $ffmpegDestinations.Count) {
        throw 'The packaged FFmpeg selection boundary is invalid.'
    }
    foreach ($entry in @($ffmpegManifest.selectedFiles)) {
        $destination = $ffmpegDestinations[[string]$entry.role]
        if ([string]::IsNullOrEmpty($destination)) {
            throw 'The packaged FFmpeg evidence contains an unsupported role.'
        }
        $path = Join-Path $unpackedRoot $destination
        if (
            (Get-Item -LiteralPath $path -Force).Length -ne [long]$entry.byteSize -or
            (Get-LowerSha256 -Path $path) -cne [string]$entry.sha256
        ) {
            throw 'A packaged FFmpeg file does not match its fixed evidence.'
        }
    }

    $ffmpegVersion = Invoke-IsolatedProcess `
        -FilePath (Join-Path $unpackedRoot 'Tools\ffmpeg.exe') `
        -ArgumentList @('-version') `
        -WorkingDirectory $modelWorkspace
    $ffprobeVersion = Invoke-IsolatedProcess `
        -FilePath (Join-Path $unpackedRoot 'Tools\ffprobe.exe') `
        -ArgumentList @('-version') `
        -WorkingDirectory $modelWorkspace
    $expectedFfmpegVersion = [string]$ffmpegManifest.versionEvidence.firstLine
    $expectedFfprobeVersion = 'ffprobe version ' +
        [string]$ffmpegManifest.package.buildIdentifier +
        ' Copyright (c) 2007-2026 the FFmpeg developers'
    if (
        $ffmpegVersion.ExitCode -ne 0 -or
        $ffprobeVersion.ExitCode -ne 0 -or
        ($ffmpegVersion.StandardOutput -split "\r?\n")[0] -cne $expectedFfmpegVersion -or
        ($ffprobeVersion.StandardOutput -split "\r?\n")[0] -cne $expectedFfprobeVersion
    ) {
        throw 'A packaged FFmpeg executable did not report the fixed version.'
    }

    $modelCatalog = Read-StrictJson -Path (Join-Path $unpackedRoot 'Evidence\model-catalog.json')
    $runtimeModels = @($modelCatalog.models | Where-Object runtimeEligible)
    if ($runtimeModels.Count -ne 1 -or
        $runtimeModels[0].modelId -cne 'asr.faster-whisper-small.cpu-int8') {
        throw 'The packaged model runtime allowlist is invalid.'
    }
    $runtimeModel = $runtimeModels[0]
    $modelRoot = Join-Path $unpackedRoot 'Models\faster-whisper-small'
    $actualModelFiles = [string[]]@(
        Get-ChildItem -LiteralPath $modelRoot -File -Force |
            ForEach-Object Name |
            Sort-Object -CaseSensitive)
    $expectedModelFiles = [string[]]@(
        $runtimeModel.bundle.files |
            ForEach-Object { [string]$_.name } |
            Sort-Object -CaseSensitive)
    if (
        @(Get-ChildItem -LiteralPath $modelRoot -Directory -Force).Count -ne 0 -or
        [string]::Join("`n", $actualModelFiles) -cne [string]::Join("`n", $expectedModelFiles)
    ) {
        throw 'The packaged model bundle contains missing or extra entries.'
    }
    $modelRecords = @()
    foreach ($entry in @($runtimeModel.bundle.files | Sort-Object name -CaseSensitive)) {
        $path = Join-Path $modelRoot ([string]$entry.name)
        $record = [pscustomobject]@{
            path = [string]$entry.name
            byteSize = (Get-Item -LiteralPath $path -Force).Length
            sha256 = Get-LowerSha256 -Path $path
        }
        if (
            $record.byteSize -ne [long]$entry.byteSize -or
            $record.sha256 -cne [string]$entry.sha256
        ) {
            throw 'A packaged model file does not match its fixed evidence.'
        }
        $modelRecords += $record
    }
    if (
        [long](($modelRecords | Measure-Object byteSize -Sum).Sum) -ne
            [long]$runtimeModel.bundle.totalBytes -or
        (Get-ArtifactSetSha256 -Records $modelRecords) -cne
            [string]$runtimeModel.bundle.manifestSha256
    ) {
        throw 'The packaged model bundle aggregate is invalid.'
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $unpackedRoot -File -Force -Recurse)) {
        $file.IsReadOnly = $true
    }

    # verify-runtime.ps1 is fail-closed: every incomplete health, native import,
    # protocol, isolation, or tree-integrity check throws before normal return.
    & $workerVerifyScriptPath `
        -RuntimePath (Join-Path $unpackedRoot 'Worker') `
        -WorkspacePath $workerWorkspace

    $modelProbeCode = @'
import json
import sys
from faster_whisper import WhisperModel

WhisperModel(
    sys.argv[1],
    device="cpu",
    compute_type="int8",
    cpu_threads=1,
    num_workers=1,
    local_files_only=True,
)
print(json.dumps(
    {"computeType": "int8", "device": "cpu", "loaded": True},
    sort_keys=True,
    separators=(",", ":"),
))
'@
    $modelProbe = Invoke-IsolatedProcess `
        -FilePath (Join-Path $unpackedRoot 'Worker\python.exe') `
        -ArgumentList @('-I', '-B', '-c', $modelProbeCode, $modelRoot) `
        -WorkingDirectory $modelWorkspace `
        -TimeoutSeconds 300 `
        -Environment @{
            APPDATA = $modelWorkspace
            HOME = $modelWorkspace
            USERPROFILE = $modelWorkspace
            LOCALAPPDATA = $modelWorkspace
            HF_HOME = Join-Path $modelWorkspace 'hf-cache'
            HUGGINGFACE_HUB_CACHE = Join-Path $modelWorkspace 'hf-cache\hub'
            XDG_CACHE_HOME = Join-Path $modelWorkspace 'cache'
            HF_HUB_OFFLINE = '1'
            TRANSFORMERS_OFFLINE = '1'
            PYTHONDONTWRITEBYTECODE = '1'
            PYTHONPATH = 'C:\untrusted-python-path'
            PYTHONHOME = 'C:\untrusted-python-home'
        }
    if (
        $modelProbe.ExitCode -ne 0 -or
        -not [string]::IsNullOrEmpty($modelProbe.StandardError) -or
        $modelProbe.StandardOutput.Trim() -cne
            '{"computeType":"int8","device":"cpu","loaded":true}'
    ) {
        throw 'The packaged CPU/int8 ASR model did not load offline.'
    }

    $appRoot = Join-Path $unpackedRoot 'App'
    foreach ($required in @(
        'HubRadio.App.exe',
        'App.xbf',
        'MainWindow.xbf',
        'HubRadio.App.pri',
        'coreclr.dll',
        'Microsoft.WinUI.dll',
        'WebAssets\asset-manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appRoot $required) -PathType Leaf)) {
            throw 'The packaged self-contained App payload is incomplete.'
        }
    }

    $webAssetManifest = Read-StrictJson -Path (Join-Path $appRoot 'WebAssets\asset-manifest.json')
    if (
        $webAssetManifest.schemaVersion -cne '1.0.0' -or
        $webAssetManifest.entryPoint -cne 'index.html' -or
        $webAssetManifest.sourceMapsIncluded -ne $false -or
        $webAssetManifest.remoteAssetsAllowed -ne $false
    ) {
        throw 'The packaged Web asset manifest is invalid.'
    }
    $webAssetPaths = [string[]]@(
        $webAssetManifest.assets | ForEach-Object { [string]$_.path } | Sort-Object -CaseSensitive)
    $actualWebAssetPaths = [string[]]@(
        Get-ChildItem -LiteralPath (Join-Path $appRoot 'WebAssets') -File -Force -Recurse |
            Where-Object Name -ne 'asset-manifest.json' |
            ForEach-Object {
                [IO.Path]::GetRelativePath((Join-Path $appRoot 'WebAssets'), $_.FullName).Replace('\', '/')
            } |
            Sort-Object -CaseSensitive)
    if ([string]::Join("`n", $webAssetPaths) -cne [string]::Join("`n", $actualWebAssetPaths)) {
        throw 'The packaged Web bundle contains missing or extra assets.'
    }
    foreach ($asset in @($webAssetManifest.assets)) {
        $path = Join-Path (Join-Path $appRoot 'WebAssets') ([string]$asset.path)
        if (
            (Get-Item -LiteralPath $path -Force).Length -ne [long]$asset.byteSize -or
            (Get-LowerSha256 -Path $path) -cne [string]$asset.sha256
        ) {
            throw 'A packaged Web asset does not match its fixed manifest.'
        }
    }

    & $webViewSmokeScriptPath -AppPath (Join-Path $appRoot 'HubRadio.App.exe')

    $finalPayload = Assert-PayloadMatchesLayout -Root $unpackedRoot -Layout $layout
    if (
        $finalPayload.FileCount -ne $initialPayload.FileCount -or
        $finalPayload.TotalBytes -ne $initialPayload.TotalBytes -or
        $finalPayload.ArtifactSetSha256 -cne $initialPayload.ArtifactSetSha256 -or
        (Get-Item -LiteralPath $PackagePath -Force).Length -ne $packageByteSize -or
        (Get-LowerSha256 -Path $PackagePath) -cne $packageSha256
    ) {
        throw 'Package verification modified the MSIX or its unpacked payload.'
    }

    $verified = $true
    Write-Information 'Unsigned PK-006 MSIX payload verification passed.' -InformationAction Continue
    [pscustomobject]@{
        PackagePath = $PackagePath
        PackageBytes = $packageByteSize
        PackageSha256 = $packageSha256
        PayloadFiles = $finalPayload.FileCount
        PayloadBytes = $finalPayload.TotalBytes
        PayloadArtifactSetSha256 = $finalPayload.ArtifactSetSha256
        PackageSigned = $false
        AppSmokePassed = $true
        WorkerHealthPassed = $true
        WorkerProtocolPassed = $true
        FfmpegAndFfprobePassed = $true
        CpuInt8ModelLoadPassed = $true
        InstalledMsixValidated = $false
        CleanMachineValidated = $false
        ReleaseApproved = $false
        EvidenceStatus = 'unpacked-development-evidence'
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $WorkspacePath) {
        Remove-GeneratedTree -Path $WorkspacePath -Confirm:$false
    }
}

if (-not $verified) {
    throw 'The unsigned MSIX package verification did not complete.'
}