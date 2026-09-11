#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeArchivePath,

    [Parameter(Mandatory)]
    [string]$WheelhousePath,

    [Parameter(Mandatory)]
    [string]$BuildPythonPath,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

if (-not $IsWindows -or [Environment]::Is64BitOperatingSystem -ne $true) {
    throw 'The worker runtime package must be built on Windows x64.'
}

$packagingRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $packagingRoot '..\..'))
$manifestPath = Join-Path $packagingRoot 'runtime-manifest.json'
$pthTemplatePath = Join-Path $packagingRoot 'python312._pth'
$wheelMatrixPath = Join-Path $repoRoot 'src\HubRadio.Worker\wheel-matrix.json'
$requirementsPath = Join-Path $repoRoot 'src\HubRadio.Worker\requirements.lock'
$workerSourceRoot = Join-Path $repoRoot 'src\HubRadio.Worker\src'
$artifactRoot = Join-Path $repoRoot 'artifacts\python'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactRoot 'HubRadio.WorkerRuntime'
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

function Assert-SafeExistingPathChain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$LeafFile
    )

    $current = $Path
    $isTarget = $true
    while ($null -ne $current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A package input or output path contains a reparse point.'
        }

        if ($isTarget -and $LeafFile) {
            if (-not ($item -is [IO.FileInfo])) {
                throw 'A required package input is not a regular file.'
            }
        }
        elseif (-not ($item -is [IO.DirectoryInfo])) {
            throw 'A required package path is not a directory.'
        }

        $current = [IO.Path]::GetDirectoryName($current)
        $isTarget = $false
    }
}

function Get-LowerSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-FileEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$ExpectedByteSize,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256
    )

    Assert-SafeExistingPathChain -Path $Path -LeafFile
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -ne $ExpectedByteSize -or (Get-LowerSha256 -Path $Path) -cne $ExpectedSha256) {
        throw 'A package input does not match its fixed size and SHA-256.'
    }
}

# LEGACY fixed-culture compatibility: committed manifests use en-US linguistic order,
# not Ordinal (which changes their digests). Keep all manifest/file-array sorts on en-US.
function Get-ArtifactSetSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Records,

        [Parameter(Mandatory)]
        [string]$PathProperty
    )

    $builder = [Text.StringBuilder]::new()
    foreach ($record in @($Records | Sort-Object -Property $PathProperty -CaseSensitive -Culture en-US)) {
        [void]$builder.Append([string]$record.$PathProperty)
        [void]$builder.Append([char]0)
        [void]$builder.Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$record.sha256)
        [void]$builder.Append("`n")
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($builder.ToString())
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
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

    if (-not $PSCmdlet.ShouldProcess($Path, 'Remove generated package tree')) {
        return
    }

    $entries = @(Get-ChildItem -LiteralPath $Path -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A generated package tree contains a reparse point and requires reconciliation.'
        }

        if ($entry -is [IO.DirectoryInfo]) {
            Remove-GeneratedTree -Path $entry.FullName -Confirm:$false
        }
        else {
            [IO.File]::SetAttributes($entry.FullName, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($entry.FullName)
        }
    }

    [IO.Directory]::Delete($Path, $false)
}

function Invoke-CheckedNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw 'A package build subprocess failed.'
    }
}

function Move-GeneratedTreeWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $maximumAttempts = 6
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        try {
            [IO.Directory]::Move($SourcePath, $DestinationPath)
            return
        }
        catch [UnauthorizedAccessException], [IO.IOException] {
            # Destination existence alone cannot prove that this move succeeded.
            $sourceExists = Test-Path -LiteralPath $SourcePath -PathType Container
            $destinationExists = Test-Path -LiteralPath $DestinationPath
            if (-not $sourceExists -or $destinationExists -or $attempt -eq $maximumAttempts) {
                throw
            }

            $delayMilliseconds = [Math]::Min(25 * [Math]::Pow(2, $attempt - 1), 200)
            [Threading.Tasks.Task]::Delay([int]$delayMilliseconds).GetAwaiter().GetResult()
        }
    }
}

function Update-RuntimeWheelRecord {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$SitePackagesPath
    )

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    foreach ($recordPath in @(
        Get-ChildItem -LiteralPath $SitePackagesPath -Filter 'RECORD' -File -Recurse -Force)) {
        $lines = [IO.File]::ReadAllLines($recordPath.FullName, $strictUtf8)
        $retained = [string[]]@(
            $lines | Where-Object {
                -not $_.StartsWith('../../bin/', [StringComparison]::Ordinal)
            })
        if ($PSCmdlet.ShouldProcess($recordPath.FullName, 'Normalize runtime wheel RECORD')) {
            [IO.File]::WriteAllText(
                $recordPath.FullName,
                [string]::Join("`n", $retained) + "`n",
                $strictUtf8)
        }
    }
}

$RuntimeArchivePath = Get-NormalizedLocalPath -Path $RuntimeArchivePath -Name 'RuntimeArchivePath'
$WheelhousePath = Get-NormalizedLocalPath -Path $WheelhousePath -Name 'WheelhousePath'
$BuildPythonPath = Get-NormalizedLocalPath -Path $BuildPythonPath -Name 'BuildPythonPath'
$OutputPath = Get-NormalizedLocalPath -Path $OutputPath -Name 'OutputPath'
$artifactRoot = [IO.Path]::GetFullPath($artifactRoot)

if (-not $OutputPath.StartsWith(
        [IO.Path]::TrimEndingDirectorySeparator($artifactRoot) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be below the repository artifacts/python directory.'
}

Assert-SafeExistingPathChain -Path $RuntimeArchivePath -LeafFile
Assert-SafeExistingPathChain -Path $WheelhousePath
Assert-SafeExistingPathChain -Path $BuildPythonPath -LeafFile
Assert-SafeExistingPathChain -Path $manifestPath -LeafFile
Assert-SafeExistingPathChain -Path $pthTemplatePath -LeafFile
Assert-SafeExistingPathChain -Path $wheelMatrixPath -LeafFile
Assert-SafeExistingPathChain -Path $requirementsPath -LeafFile
Assert-SafeExistingPathChain -Path $workerSourceRoot

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if (
    $manifest.schemaVersion -cne '1.0.0' -or
    $manifest.releaseApproved -ne $false -or
    $manifest.runtime.operatingSystem -cne 'Windows' -or
    $manifest.runtime.architecture -cne 'x64' -or
    $manifest.runtime.version -cne '3.12.10' -or
    $manifest.isolation.importSiteEnabled -ne $false -or
    $manifest.isolation.pipIncludedAtRuntime -ne $false -or
    $manifest.isolation.systemPathRequired -ne $false
) {
    throw 'The Python runtime manifest policy is invalid.'
}

Assert-FileEvidence `
    -Path $RuntimeArchivePath `
    -ExpectedByteSize ([long]$manifest.runtime.byteSize) `
    -ExpectedSha256 ([string]$manifest.runtime.sha256)

if ((Split-Path -Leaf $RuntimeArchivePath) -cne [string]$manifest.runtime.fileName) {
    throw 'The runtime archive name does not match the manifest.'
}

Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($RuntimeArchivePath)
try {
    $actualArchiveEntries = [string[]]@($archive.Entries | ForEach-Object FullName)
    $expectedArchiveEntries = [string[]]@($manifest.runtime.archiveEntries)
    [Array]::Sort($actualArchiveEntries, [StringComparer]::Ordinal)
    [Array]::Sort($expectedArchiveEntries, [StringComparer]::Ordinal)
    if (
        $actualArchiveEntries.Count -ne [int]$manifest.runtime.archiveEntryCount -or
        $actualArchiveEntries.Count -ne $expectedArchiveEntries.Count -or
        [string]::Join("`n", $actualArchiveEntries) -cne
            [string]::Join("`n", $expectedArchiveEntries) -or
        @($archive.Entries | Where-Object {
            $_.FullName.Contains('/') -or
            $_.FullName.Contains('\') -or
            [string]::IsNullOrEmpty($_.Name)
        }).Count -ne 0
    ) {
        throw 'The runtime archive entry set is invalid.'
    }
}
finally {
    $archive.Dispose()
}

if ((Get-LowerSha256 -Path $wheelMatrixPath) -cne [string]$manifest.wheelInput.matrixSha256) {
    throw 'The wheel matrix does not match the runtime manifest.'
}

$wheelMatrix = Get-Content -LiteralPath $wheelMatrixPath -Raw -Encoding utf8 | ConvertFrom-Json
$runtimeWheels = @($wheelMatrix.packages | Where-Object lock -eq 'runtime' | Sort-Object fileName -Culture en-US)
if (
    $runtimeWheels.Count -ne [int]$manifest.wheelInput.runtimeWheelCount -or
    [long](($runtimeWheels | Measure-Object byteSize -Sum).Sum) -ne
        [long]$manifest.wheelInput.runtimeWheelBytes -or
    (Get-ArtifactSetSha256 -Records $runtimeWheels -PathProperty 'fileName') -cne
        [string]$manifest.wheelInput.runtimeWheelSetSha256
) {
    throw 'The runtime wheel set does not match the runtime manifest.'
}

$wheelhouseFiles = @(Get-ChildItem -LiteralPath $WheelhousePath -File -Force | Sort-Object Name -Culture en-US)
$expectedWheelNames = @($runtimeWheels | ForEach-Object fileName)
$actualWheelNames = @($wheelhouseFiles | ForEach-Object Name)
if (
    $actualWheelNames.Count -ne $expectedWheelNames.Count -or
    [string]::Join("`n", $actualWheelNames) -cne [string]::Join("`n", $expectedWheelNames)
) {
    throw 'The wheelhouse must contain exactly the fixed runtime wheel set.'
}

foreach ($wheel in $runtimeWheels) {
    Assert-FileEvidence `
        -Path (Join-Path $WheelhousePath ([string]$wheel.fileName)) `
        -ExpectedByteSize ([long]$wheel.byteSize) `
        -ExpectedSha256 ([string]$wheel.sha256)
}

$workerFiles = @($manifest.workerSource.files | Sort-Object path -Culture en-US)
if (
    $workerFiles.Count -ne [int]$manifest.workerSource.fileCount -or
    [long](($workerFiles | Measure-Object byteSize -Sum).Sum) -ne
        [long]$manifest.workerSource.totalBytes -or
    (Get-ArtifactSetSha256 -Records $workerFiles -PathProperty 'path') -cne
        [string]$manifest.workerSource.artifactSetSha256
) {
    throw 'The worker source manifest is invalid.'
}

foreach ($source in $workerFiles) {
    $sourcePath = Join-Path $workerSourceRoot (
        ([string]$source.path).Replace('/', [IO.Path]::DirectorySeparatorChar))
    Assert-FileEvidence `
        -Path $sourcePath `
        -ExpectedByteSize ([long]$source.byteSize) `
        -ExpectedSha256 ([string]$source.sha256)
}

$pthLines = @(Get-Content -LiteralPath $pthTemplatePath -Encoding utf8)
if (
    $pthLines.Count -ne @($manifest.isolation.pthLines).Count -or
    [string]::Join("`n", $pthLines) -cne
        [string]::Join("`n", @($manifest.isolation.pthLines))
) {
    throw 'The isolated path template does not match the runtime manifest.'
}

$pythonInspection = & $BuildPythonPath -c (
    'import json,struct,sys;' +
    'print(json.dumps({"implementation":sys.implementation.name,' +
    '"major":sys.version_info.major,"minor":sys.version_info.minor,' +
    '"platform":sys.platform,"pointerBits":struct.calcsize("P")*8},' +
    'sort_keys=True,separators=(",",":")))')
if ($LASTEXITCODE -ne 0) {
    throw 'The build Python could not be inspected.'
}

$buildPython = $pythonInspection | ConvertFrom-Json
if (
    $buildPython.implementation -cne 'cpython' -or
    $buildPython.major -ne 3 -or
    $buildPython.minor -ne 12 -or
    $buildPython.platform -cne 'win32' -or
    $buildPython.pointerBits -ne 64
) {
    throw 'BuildPythonPath must identify CPython 3.12 for Windows x64.'
}

if (Test-Path -LiteralPath $OutputPath) {
    throw 'The Python runtime output already exists and will not be overwritten.'
}

[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
Assert-SafeExistingPathChain -Path $artifactRoot
$stagingPath = Join-Path $artifactRoot ('.HubRadio.WorkerRuntime.' + [guid]::NewGuid().ToString('N') + '.partial')
[IO.Directory]::CreateDirectory($stagingPath) | Out-Null

$moved = $false
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($RuntimeArchivePath, $stagingPath)
    Assert-SafeExistingPathChain -Path $stagingPath

    $extractedNames = [string[]]@(
        Get-ChildItem -LiteralPath $stagingPath -File -Force |
            ForEach-Object Name)
    [Array]::Sort($extractedNames, [StringComparer]::Ordinal)
    if ([string]::Join("`n", $extractedNames) -cne [string]::Join("`n", $expectedArchiveEntries)) {
        throw 'The extracted runtime entry set is invalid.'
    }

    Assert-FileEvidence `
        -Path (Join-Path $stagingPath ([string]$manifest.runtime.license.fileName)) `
        -ExpectedByteSize ([long]$manifest.runtime.license.byteSize) `
        -ExpectedSha256 ([string]$manifest.runtime.license.sha256)

    foreach ($signedFile in @($manifest.runtime.selectedSignedFiles)) {
        $signedPath = Join-Path $stagingPath ([string]$signedFile.name)
        Assert-FileEvidence `
            -Path $signedPath `
            -ExpectedByteSize ([long]$signedFile.byteSize) `
            -ExpectedSha256 ([string]$signedFile.sha256)
        $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
        if (
            $signature.Status.ToString() -cne [string]$signedFile.signatureStatus -or
            $signature.SignerCertificate.Subject -cne [string]$signedFile.signerSubject -or
            $signature.SignerCertificate.Thumbprint -cne [string]$signedFile.signerThumbprint
        ) {
            throw 'A selected Python runtime signature does not match the manifest.'
        }
    }

    Copy-Item -LiteralPath $pthTemplatePath -Destination (Join-Path $stagingPath 'python312._pth') -Force
    $sitePackages = Join-Path $stagingPath 'Lib\site-packages'
    [IO.Directory]::CreateDirectory($sitePackages) | Out-Null

    Invoke-CheckedNative -FilePath $BuildPythonPath -ArgumentList @(
        '-m', 'pip', 'install',
        '--disable-pip-version-check',
        '--require-hashes',
        '--only-binary=:all:',
        '--no-index',
        '--no-deps',
        '--no-compile',
        '--no-cache-dir',
        '--no-warn-script-location',
        '--find-links', $WheelhousePath,
        '--target', $sitePackages,
        '-r', $requirementsPath
    )

    $generatedBin = Join-Path $sitePackages 'bin'
    if (Test-Path -LiteralPath $generatedBin) {
        Remove-GeneratedTree -Path $generatedBin -Confirm:$false
    }

    Update-RuntimeWheelRecord -SitePackagesPath $sitePackages -Confirm:$false

    foreach ($source in $workerFiles) {
        $relative = ([string]$source.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $sourcePath = Join-Path $workerSourceRoot $relative
        $destinationPath = Join-Path $sitePackages $relative
        $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
        [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        [IO.File]::Copy($sourcePath, $destinationPath, $false)
    }

    $pipPayload = @(
        Get-ChildItem -LiteralPath $sitePackages -Force |
            Where-Object { $_.Name -eq 'pip' -or $_.Name -like 'pip-*.dist-info' }
    )
    if (
        @(Get-ChildItem -LiteralPath $stagingPath -Filter '*.pyc' -File -Recurse).Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $stagingPath -Directory -Recurse |
            Where-Object Name -eq '__pycache__').Count -ne 0 -or
        $pipPayload.Count -ne 0
    ) {
        throw 'The runtime payload contains a mutable cache or build-only pip payload.'
    }

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $stagingPath -File -Recurse -Force |
            Where-Object Name -ne ([string]$manifest.output.fileManifestName) |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($stagingPath, $_.FullName).Replace('\', '/')
                [pscustomobject][ordered]@{
                    path = $relative
                    byteSize = $_.Length
                    sha256 = Get-LowerSha256 -Path $_.FullName
                }
            } |
            Sort-Object path -CaseSensitive -Culture en-US
    )

    $payloadBytes = [long](($payloadFiles | Measure-Object byteSize -Sum).Sum)
    $payloadDigest = Get-ArtifactSetSha256 -Records $payloadFiles -PathProperty 'path'
    if (
        $payloadFiles.Count -ne [int]$manifest.output.expectedFileCount -or
        $payloadBytes -ne [long]$manifest.output.expectedTotalBytes -or
        $payloadDigest -cne [string]$manifest.output.expectedArtifactSetSha256
    ) {
        throw 'The built runtime payload does not match the fixed output manifest.'
    }

    $fileManifest = [ordered]@{
        schemaVersion = '1.0.0'
        runtimeIdentity = [string]$manifest.runtime.identity
        runtimeVersion = [string]$manifest.runtime.version
        architecture = [string]$manifest.runtime.architecture
        sourceManifestSha256 = Get-LowerSha256 -Path $manifestPath
        fileCount = $payloadFiles.Count
        totalBytes = $payloadBytes
        artifactSetSha256 = $payloadDigest
        files = $payloadFiles
    }
    $fileManifestPath = Join-Path $stagingPath ([string]$manifest.output.fileManifestName)
    [IO.File]::WriteAllText(
        $fileManifestPath,
        ($fileManifest | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false, $true))

    foreach ($file in @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse -Force)) {
        [IO.File]::SetAttributes($file.FullName, $file.Attributes -bor [IO.FileAttributes]::ReadOnly)
    }

    Move-GeneratedTreeWithRetry -SourcePath $stagingPath -DestinationPath $OutputPath
    $moved = $true
}
finally {
    if (-not $moved) {
        try {
            if (Test-Path -LiteralPath $stagingPath) {
                Remove-GeneratedTree -Path $stagingPath -Confirm:$false
            }
        }
        catch {
            # Cleanup must not replace the original build/move error, even with WarningPreference=Stop.
            Write-Warning 'The generated partial runtime tree could not be removed and requires reconciliation.' -WarningAction Continue
        }
    }
}

Write-Information "Built self-contained worker runtime at $OutputPath" -InformationAction Continue