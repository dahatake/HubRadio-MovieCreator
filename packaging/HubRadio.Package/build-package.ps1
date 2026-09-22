#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DotnetPath,

    [Parameter(Mandatory)]
    [string]$MakeAppxPath,

    [Parameter(Mandatory)]
    [string]$PythonRuntimePath,

    [Parameter(Mandatory)]
    [string]$FfmpegPath,

    [Parameter(Mandatory)]
    [string]$FfprobePath,

    [Parameter(Mandatory)]
    [string]$FfmpegLicensePath,

    [Parameter(Mandatory)]
    [string]$ModelBundlePath,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'The HubRadio MSIX package must be built on Windows x64.'
}

$packageProjectRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $packageProjectRoot '..\..'))
$artifactRoot = Join-Path $repoRoot 'artifacts\package'
$appProjectPath = Join-Path $repoRoot 'src\HubRadio.App\HubRadio.App.csproj'
$sourceManifestPath = Join-Path $packageProjectRoot 'Package.appxmanifest'
$notForReleasePath = Join-Path $packageProjectRoot 'PACKAGE-NOT-FOR-RELEASE.txt'
$square44LogoSourcePath = Join-Path $packageProjectRoot 'Assets\Square44x44Logo.png.b64'
$square150LogoSourcePath = Join-Path $packageProjectRoot 'Assets\Square150x150Logo.png.b64'
$storeLogoSourcePath = Join-Path $packageProjectRoot 'Assets\StoreLogo.png.b64'
$pythonSourceManifestPath = Join-Path $repoRoot 'packaging\python\runtime-manifest.json'
$pythonVerifyScriptPath = Join-Path $repoRoot 'packaging\python\verify-runtime.ps1'
$ffmpegManifestPath = Join-Path $repoRoot 'packaging\ffmpeg\manifest.json'
$modelCatalogPath = Join-Path $repoRoot 'packaging\models\catalog.json'
$webDependencyManifestPath = Join-Path $repoRoot 'packaging\web\dependency-manifest.json'
$thirdPartyInventoryPath = Join-Path $repoRoot 'docs\dev\licenses\THIRD-PARTY-NOTICES.md'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactRoot 'HubRadio.MovieCreator_0.1.3.0_x64.msix'
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
            throw 'A package input or output path contains a reparse point.'
        }

        if ($target -and $LeafFile) {
            if (-not ($item -is [IO.FileInfo])) {
                throw 'A package input is not a regular file.'
            }
        }
        elseif (-not ($item -is [IO.DirectoryInfo])) {
            throw 'A package path is not a directory.'
        }

        $current = [IO.Path]::GetDirectoryName($current)
        $target = $false
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
        [long]$ByteSize,

        [Parameter(Mandatory)]
        [string]$Sha256
    )

    Assert-SafePathChain -Path $Path -LeafFile
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -ne $ByteSize -or (Get-LowerSha256 -Path $Path) -cne $Sha256) {
        throw 'A package input does not match its fixed size and SHA-256.'
    }
}

function Copy-SafeFile {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    Assert-SafePathChain -Path $SourcePath -LeafFile
    $sourceItem = Get-Item -LiteralPath $SourcePath -Force
    $sourceByteSize = $sourceItem.Length
    $sourceSha256 = Get-LowerSha256 -Path $SourcePath
    $destinationDirectory = [IO.Path]::GetDirectoryName($DestinationPath)
    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    Assert-SafePathChain -Path $destinationDirectory
    if ($PSCmdlet.ShouldProcess($DestinationPath, 'Copy verified package file')) {
        [IO.File]::Copy($SourcePath, $DestinationPath, $false)
        Assert-SafePathChain -Path $DestinationPath -LeafFile
        $destinationItem = Get-Item -LiteralPath $DestinationPath -Force
        if (
            $destinationItem.Length -ne $sourceByteSize -or
            (Get-LowerSha256 -Path $DestinationPath) -cne $sourceSha256
        ) {
            throw 'A package input changed while it was being copied.'
        }
    }
}

function Copy-SafeTree {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath,

        [string[]]$ExcludedExtensions = @()
    )

    Assert-SafePathChain -Path $SourcePath
    [IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    foreach ($entry in @(Get-ChildItem -LiteralPath $SourcePath -Force -Recurse)) {
        $current = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A package input tree contains a reparse point.'
        }

        $relativePath = [IO.Path]::GetRelativePath($SourcePath, $current.FullName)
        if ($relativePath -eq '..' -or $relativePath.StartsWith('..\', [StringComparison]::Ordinal)) {
            throw 'A package input tree entry escaped its declared root.'
        }
        $destination = Join-Path $DestinationPath $relativePath
        if ($current -is [IO.DirectoryInfo]) {
            [IO.Directory]::CreateDirectory($destination) | Out-Null
            continue
        }

        if (-not ($current -is [IO.FileInfo])) {
            throw 'A package input tree contains a non-regular entry.'
        }

        if ($ExcludedExtensions -contains $current.Extension.ToLowerInvariant()) {
            continue
        }

        Copy-SafeFile `
            -SourcePath $current.FullName `
            -DestinationPath $destination `
            -Confirm:$false
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
        throw 'A generated package tree root is unsafe and requires reconciliation.'
    }

    if (-not $PSCmdlet.ShouldProcess($Path, 'Remove generated package tree')) {
        return
    }

    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        $current = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A generated package tree contains a reparse point and requires reconciliation.'
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

function New-PackageLogo {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$ByteSize,

        [Parameter(Mandatory)]
        [string]$Sha256
    )

    Assert-SafePathChain -Path $SourcePath -LeafFile
    $base64 = [IO.File]::ReadAllText(
        $SourcePath,
        [Text.UTF8Encoding]::new($false, $true)).Trim()
    if ($base64 -notmatch '^[A-Za-z0-9+/]+={0,2}$') {
        throw 'A package logo source is not canonical Base64.'
    }

    $bytes = [Convert]::FromBase64String($base64)
    $actualSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($bytes.LongLength -ne $ByteSize -or $actualSha256 -cne $Sha256) {
        throw 'A package logo does not match its fixed size and SHA-256.'
    }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Create deterministic package logo')) {
        return
    }

    $destinationDirectory = [IO.Path]::GetDirectoryName($Path)
    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Write-ManualPackageManifest {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    Assert-SafePathChain -Path $SourcePath -LeafFile
    $source = [IO.File]::ReadAllText(
        $SourcePath,
        [Text.UTF8Encoding]::new($false, $true))
    $token = 'Executable="$targetnametoken$.exe"'
    if ($source.IndexOf($token, [StringComparison]::Ordinal) -lt 0 -or
        $source.IndexOf(
            $token,
            $source.IndexOf($token, [StringComparison]::Ordinal) + $token.Length,
            [StringComparison]::Ordinal) -ge 0) {
        throw 'The WAP manifest must contain exactly one executable target-name token.'
    }

    $manual = $source.Replace(
        $token,
        'Executable="App\HubRadio.App.exe"',
        [StringComparison]::Ordinal)
    $destinationDirectory = [IO.Path]::GetDirectoryName($DestinationPath)
    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    Assert-SafePathChain -Path $destinationDirectory
    if ($PSCmdlet.ShouldProcess($DestinationPath, 'Write concrete manual package manifest')) {
        [IO.File]::WriteAllText(
            $DestinationPath,
            $manual,
            [Text.UTF8Encoding]::new($false, $true))
    }
}

$DotnetPath = Get-NormalizedLocalPath -Path $DotnetPath -Name 'DotnetPath'
$MakeAppxPath = Get-NormalizedLocalPath -Path $MakeAppxPath -Name 'MakeAppxPath'
$PythonRuntimePath = Get-NormalizedLocalPath -Path $PythonRuntimePath -Name 'PythonRuntimePath'
$FfmpegPath = Get-NormalizedLocalPath -Path $FfmpegPath -Name 'FfmpegPath'
$FfprobePath = Get-NormalizedLocalPath -Path $FfprobePath -Name 'FfprobePath'
$FfmpegLicensePath = Get-NormalizedLocalPath -Path $FfmpegLicensePath -Name 'FfmpegLicensePath'
$ModelBundlePath = Get-NormalizedLocalPath -Path $ModelBundlePath -Name 'ModelBundlePath'
$OutputPath = Get-NormalizedLocalPath -Path $OutputPath -Name 'OutputPath'
$artifactRoot = [IO.Path]::GetFullPath($artifactRoot)

if (-not $OutputPath.StartsWith(
        [IO.Path]::TrimEndingDirectorySeparator($artifactRoot) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be below the repository artifacts/package directory.'
}

foreach ($path in @(
    $DotnetPath,
    $MakeAppxPath,
    $FfmpegPath,
    $FfprobePath,
    $FfmpegLicensePath,
    $appProjectPath,
    $sourceManifestPath,
    $notForReleasePath,
    $square44LogoSourcePath,
    $square150LogoSourcePath,
    $storeLogoSourcePath,
    $pythonSourceManifestPath,
    $pythonVerifyScriptPath,
    $ffmpegManifestPath,
    $modelCatalogPath,
    $webDependencyManifestPath,
    $thirdPartyInventoryPath
)) {
    Assert-SafePathChain -Path $path -LeafFile
}
$evidenceSourceHashes = [ordered]@{
    'python-runtime-manifest.json' = Get-LowerSha256 -Path $pythonSourceManifestPath
    'ffmpeg-manifest.json' = Get-LowerSha256 -Path $ffmpegManifestPath
    'model-catalog.json' = Get-LowerSha256 -Path $modelCatalogPath
    'web-dependency-manifest.json' = Get-LowerSha256 -Path $webDependencyManifestPath
}
Assert-SafePathChain -Path $PythonRuntimePath
Assert-SafePathChain -Path $ModelBundlePath

Assert-FileEvidence `
    -Path $MakeAppxPath `
    -ByteSize 567328 `
    -Sha256 '9981418e9a18a9aa19bb69f835137a3bf8fe2e6fe7e18d5ca9b76191cc331f3f'
$makeAppxSignature = Get-AuthenticodeSignature -LiteralPath $MakeAppxPath
if ($makeAppxSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    throw 'The fixed MakeAppx executable signature is not valid.'
}

$ffmpegManifest = Get-Content -LiteralPath $ffmpegManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
foreach ($entry in @($ffmpegManifest.selectedFiles)) {
    $inputPath = switch ([string]$entry.role) {
        'ffmpeg' { $FfmpegPath }
        'ffprobe' { $FfprobePath }
        'license' { $FfmpegLicensePath }
        default { throw 'The FFmpeg package manifest contains an unsupported selected file.' }
    }
    Assert-FileEvidence `
        -Path $inputPath `
        -ByteSize ([long]$entry.byteSize) `
        -Sha256 ([string]$entry.sha256)
}

$modelCatalog = Get-Content -LiteralPath $modelCatalogPath -Raw -Encoding utf8 | ConvertFrom-Json
$runtimeModels = @($modelCatalog.models | Where-Object runtimeEligible)
if ($runtimeModels.Count -ne 1 -or $runtimeModels[0].modelId -cne 'asr.faster-whisper-small.cpu-int8') {
    throw 'The model catalog runtime allowlist is invalid.'
}
$runtimeModel = $runtimeModels[0]
$expectedModelFiles = [string[]]@($runtimeModel.bundle.files | ForEach-Object name | Sort-Object)
$actualModelFiles = [string[]]@(
    Get-ChildItem -LiteralPath $ModelBundlePath -File -Force |
        ForEach-Object Name |
        Sort-Object)
$modelDirectories = @(
    Get-ChildItem -LiteralPath $ModelBundlePath -Directory -Force)
if (
    @($modelDirectories | Where-Object {
        $_.Name -cne '.cache' -or
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0 -or
    [string]::Join("`n", $expectedModelFiles) -cne [string]::Join("`n", $actualModelFiles)
) {
    throw 'The model bundle file set does not match the catalog.'
}
foreach ($file in @($runtimeModel.bundle.files)) {
    Assert-FileEvidence `
        -Path (Join-Path $ModelBundlePath ([string]$file.name)) `
        -ByteSize ([long]$file.byteSize) `
        -Sha256 ([string]$file.sha256)
}

if (Test-Path -LiteralPath $OutputPath) {
    throw 'The MSIX output already exists and will not be overwritten.'
}

[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
Assert-SafePathChain -Path $artifactRoot
$workRoot = Join-Path $artifactRoot ('.build-' + [guid]::NewGuid().ToString('N') + '.partial')
$layoutPath = Join-Path $workRoot 'layout'
$appPublishPath = Join-Path $workRoot 'app-publish'
$appBuildOutputArgument =
    [IO.Path]::TrimEndingDirectorySeparator($appPublishPath) +
    [IO.Path]::DirectorySeparatorChar
$packageStagingPath = Join-Path $workRoot 'HubRadio.MovieCreator.msix'
$verificationWorkspace = Join-Path $workRoot 'worker-verification'
[IO.Directory]::CreateDirectory($layoutPath) | Out-Null
[IO.Directory]::CreateDirectory($verificationWorkspace) | Out-Null

$completed = $false
try {
    & $pythonVerifyScriptPath `
        -RuntimePath $PythonRuntimePath `
        -WorkspacePath $verificationWorkspace

    Invoke-CheckedNative -FilePath $DotnetPath -ArgumentList @(
        'build', $appProjectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--no-restore',
        '-p:Platform=x64',
        '-p:SelfContained=true',
        '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:PublishSingleFile=false',
        '--output', $appBuildOutputArgument
    )

    foreach ($required in @(
        'HubRadio.App.exe',
        'App.xbf',
        'MainWindow.xbf',
        'HubRadio.App.pri',
        'coreclr.dll',
        'Microsoft.WinUI.dll',
        'WebAssets\asset-manifest.json',
        'ModelCatalog\catalog.json'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $appPublishPath $required) -PathType Leaf)) {
            throw 'The self-contained App build output is incomplete.'
        }
    }

    Copy-SafeTree `
        -SourcePath $appPublishPath `
        -DestinationPath (Join-Path $layoutPath 'App') `
        -ExcludedExtensions @('.pdb') `
        -Confirm:$false
    Copy-SafeTree `
        -SourcePath $PythonRuntimePath `
        -DestinationPath (Join-Path $layoutPath 'Worker') `
        -Confirm:$false
    foreach ($file in @($runtimeModel.bundle.files)) {
        Copy-SafeFile `
            -SourcePath (Join-Path $ModelBundlePath ([string]$file.name)) `
            -DestinationPath (Join-Path $layoutPath ('Models\faster-whisper-small\' + [string]$file.name)) `
            -Confirm:$false
    }

    Copy-SafeFile `
        -SourcePath $FfmpegPath `
        -DestinationPath (Join-Path $layoutPath 'Tools\ffmpeg.exe') `
        -Confirm:$false
    Copy-SafeFile `
        -SourcePath $FfprobePath `
        -DestinationPath (Join-Path $layoutPath 'Tools\ffprobe.exe') `
        -Confirm:$false
    Copy-SafeFile `
        -SourcePath $FfmpegLicensePath `
        -DestinationPath (Join-Path $layoutPath 'Licenses\FFmpeg-LICENSE.txt') `
        -Confirm:$false
    Copy-SafeFile `
        -SourcePath (Join-Path $PythonRuntimePath 'LICENSE.txt') `
        -DestinationPath (Join-Path $layoutPath 'Licenses\CPython-LICENSE.txt') `
        -Confirm:$false
    Copy-SafeFile `
        -SourcePath $thirdPartyInventoryPath `
        -DestinationPath (Join-Path $layoutPath 'Licenses\THIRD-PARTY-NOTICES-NONFINAL.md') `
        -Confirm:$false
    Copy-SafeFile `
        -SourcePath $notForReleasePath `
        -DestinationPath (Join-Path $layoutPath 'PACKAGE-NOT-FOR-RELEASE.txt') `
        -Confirm:$false

    $evidenceFiles = [ordered]@{
        'python-runtime-manifest.json' = $pythonSourceManifestPath
        'ffmpeg-manifest.json' = $ffmpegManifestPath
        'model-catalog.json' = $modelCatalogPath
        'web-dependency-manifest.json' = $webDependencyManifestPath
    }
    foreach ($evidence in $evidenceFiles.GetEnumerator()) {
        $destinationPath = Join-Path $layoutPath ('Evidence\' + $evidence.Key)
        Copy-SafeFile `
            -SourcePath $evidence.Value `
            -DestinationPath $destinationPath `
            -Confirm:$false
        if ((Get-LowerSha256 -Path $destinationPath) -cne $evidenceSourceHashes[$evidence.Key]) {
            throw 'Component evidence changed after package input validation.'
        }
    }

    Write-ManualPackageManifest `
        -SourcePath $sourceManifestPath `
        -DestinationPath (Join-Path $layoutPath 'AppxManifest.xml') `
        -Confirm:$false

    New-PackageLogo `
        -SourcePath $square44LogoSourcePath `
        -Path (Join-Path $layoutPath 'Assets\Square44x44Logo.png') `
        -ByteSize 255 `
        -Sha256 'bf764a0cdfcc4ccf7080781ce5580adb538c87e320e2dc1fa6634ef1c076026c' `
        -Confirm:$false
    New-PackageLogo `
        -SourcePath $square150LogoSourcePath `
        -Path (Join-Path $layoutPath 'Assets\Square150x150Logo.png') `
        -ByteSize 1028 `
        -Sha256 'f44fceb26344cdaeeb5ff5437574938364830e349118f2bf43cba807a232096e' `
        -Confirm:$false
    New-PackageLogo `
        -SourcePath $storeLogoSourcePath `
        -Path (Join-Path $layoutPath 'Assets\StoreLogo.png') `
        -ByteSize 288 `
        -Sha256 '7ac7bd4603467a0ccd9dc451a151852e7e8677b4b3d7d6d8d4c5bad5fcf7e382' `
        -Confirm:$false

    if (
        @(Get-ChildItem -LiteralPath $layoutPath -File -Recurse -Force |
            Where-Object Extension -in @('.pdb', '.map', '.pyc')).Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $layoutPath -Directory -Recurse -Force |
            Where-Object Name -eq '__pycache__').Count -ne 0
    ) {
        throw 'The package layout contains a development or mutable cache artifact.'
    }

    $layoutFiles = @(
        Get-ChildItem -LiteralPath $layoutPath -File -Recurse -Force |
            Where-Object FullName -ne (Join-Path $layoutPath 'Evidence\package-layout.json') |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    path = [IO.Path]::GetRelativePath($layoutPath, $_.FullName).Replace('\', '/')
                    byteSize = $_.Length
                    sha256 = Get-LowerSha256 -Path $_.FullName
                }
            } |
            Sort-Object path -CaseSensitive
    )
    $layoutManifest = [ordered]@{
        schemaVersion = '1.0.0'
        packageIdentity = 'HubRadio.MovieCreator.Development'
        packageVersion = '0.1.3.0'
        architecture = 'x64'
        signed = $false
        releaseApproved = $false
        fileCount = $layoutFiles.Count
        totalBytes = [long](($layoutFiles | Measure-Object byteSize -Sum).Sum)
        artifactSetSha256 = Get-ArtifactSetSha256 -Records $layoutFiles
        files = $layoutFiles
    }
    [IO.File]::WriteAllText(
        (Join-Path $layoutPath 'Evidence\package-layout.json'),
        ($layoutManifest | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false, $true))

    Invoke-CheckedNative -FilePath $MakeAppxPath -ArgumentList @(
        'pack',
        '/d', $layoutPath,
        '/p', $packageStagingPath,
        '/o'
    )

    if (-not (Test-Path -LiteralPath $packageStagingPath -PathType Leaf)) {
        throw 'MakeAppx did not produce the expected MSIX package.'
    }

    try {
        [IO.File]::Move($packageStagingPath, $OutputPath, $false)
    }
    catch [IO.IOException] {
        throw 'The MSIX output path was created by another operation during the build.'
    }
    $completed = $true
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-GeneratedTree -Path $workRoot -Confirm:$false
    }
}

if (-not $completed) {
    throw 'The unsigned MSIX package build did not complete.'
}

$packageItem = Get-Item -LiteralPath $OutputPath
Write-Information "Built unsigned local-development MSIX at $OutputPath" -InformationAction Continue
[pscustomobject]@{
    PackagePath = $OutputPath
    ByteSize = $packageItem.Length
    Sha256 = Get-LowerSha256 -Path $OutputPath
    AuthenticodeStatus = (Get-AuthenticodeSignature -LiteralPath $OutputPath).Status.ToString()
    Signed = $false
    ReleaseApproved = $false
} | Format-List