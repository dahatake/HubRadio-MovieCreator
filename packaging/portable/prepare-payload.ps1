#Requires -Version 7.0

<#
OSL-009: offline component payload only, not an application publish or release.
Use the latest installed Windows x64 pwsh. All eight paths are mandatory, absolute
local paths. ModelBundlePath contains the four catalog files directly (an optional
HF .cache is ignored). WebView2RuntimePath is the expanded vendor directory.
OutputPath must not exist and must be below this repository's artifacts/portable.
It also follows build-portable.ps1's MSBuild development-path restrictions;
these do not apply to literal vendor input/file names.
The caller must later combine this payload with the self-contained App publish.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PythonRuntimePath,
    [Parameter(Mandatory)] [string]$FfmpegPath,
    [Parameter(Mandatory)] [string]$FfprobePath,
    [Parameter(Mandatory)] [string]$FfmpegLicensePath,
    [Parameter(Mandatory)] [string]$ModelBundlePath,
    [Parameter(Mandatory)] [string]$WebView2RuntimePath,
    [Parameter(Mandatory)] [string]$WebView2LicensePath,
    [Parameter(Mandatory)] [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64' -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'OSL009:HOST Windows x64 and PowerShell 7+ Core x64 are required.'
}

function Get-LocalPath([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Trim() -or
        $Path -notmatch '^[A-Za-z]:[\\/]') {
        throw "OSL009:INPUT_PATH:${Name} requires a canonical absolute local path."
    }
    $normalized = [IO.Path]::TrimEndingDirectorySeparator($Path.Replace('/', '\'))
    foreach ($segment in $normalized.Substring(3).Split('\')) {
        if ($normalized.Length -eq 3) { break }
        if ([string]::IsNullOrEmpty($segment) -or $segment -cne $segment.Trim() -or
            $segment.EndsWith('.') -or $segment -match '[<>:"|?*\x00-\x1f]' -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
            throw "OSL009:INPUT_PATH:${Name} forbids traversal, ADS and device names."
        }
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($normalized, [IO.Path]::GetFullPath($normalized))) {
        throw "OSL009:INPUT_PATH:${Name} is not canonical."
    }
    return $normalized
}

function Test-Within([string]$Path, [string]$Root) {
    return [StringComparer]::OrdinalIgnoreCase.Equals($Path, $Root) -or
        $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repoRoot 'artifacts\portable'
$inputPaths = [ordered]@{
    PythonRuntimePath = $PythonRuntimePath
    FfmpegPath = $FfmpegPath
    FfprobePath = $FfprobePath
    FfmpegLicensePath = $FfmpegLicensePath
    ModelBundlePath = $ModelBundlePath
    WebView2RuntimePath = $WebView2RuntimePath
    WebView2LicensePath = $WebView2LicensePath
}
foreach ($name in @($inputPaths.Keys)) {
    $inputPaths[$name] = Get-LocalPath $inputPaths[$name] $name
}
# OutputPath becomes build-portable.ps1's PayloadPath, an MSBuild property.
# Keep its development-path contract here, not in vendor/tree path validation.
if ($OutputPath -match '[;,%''"$@`\x00-\x1f\x7f]' -or $OutputPath -match '~[0-9]') {
    throw 'OSL009:OUTPUT_MSBUILD_PATH OutputPath contains unsupported MSBuild delimiters, expansions, control characters or short aliases.'
}
$OutputPath = Get-LocalPath $OutputPath 'OutputPath'
if ($OutputPath -ieq $artifactRoot -or -not (Test-Within $OutputPath $artifactRoot)) {
    throw 'OSL009:OUTPUT_SCOPE OutputPath must be below artifacts/portable.'
}
foreach ($path in $inputPaths.Values) {
    if ((Test-Within $OutputPath $path) -or (Test-Within $path $OutputPath)) {
        throw 'OSL009:OVERLAP OutputPath must not overlap any input.'
    }
}
if (Test-Path -LiteralPath $OutputPath) {
    throw 'OSL009:OUTPUT_EXISTS OutputPath will not be overwritten.'
}

# Only OS interop missing from the managed APIs: canonical handles and exclusive
# directory creation.
if (-not ('HubRadio.PortablePayloadNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace HubRadio
{
    public static class PortablePayloadNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);

        public static void CreateNewDirectory(string path)
        {
            if (!CreateDirectoryW(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static string CanonicalPath(string path)
        {
            // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT; never follow a leaf link.
            using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve canonical local path.");
                string value = buffer.ToString();
                if (!value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Nonlocal path resolution is forbidden.");
                return Path.TrimEndingDirectorySeparator(value.Substring(4));
            }
        }
    }
}
'@
}

function Assert-SafePath([string]$Path, [switch]$File, [switch]$AllowMissing) {
    $Path = Get-LocalPath $Path 'Path'
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path)).DriveType -notin @(
        [IO.DriveType]::Fixed, [IO.DriveType]::Removable, [IO.DriveType]::CDRom, [IO.DriveType]::Ram)) {
        throw 'OSL009:NONLOCAL Mapped network and nonlocal drives are forbidden.'
    }
    $current = $Path
    $nearest = $null
    while ($null -ne $current) {
        try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] {
            if (-not $AllowMissing) { throw "OSL009:MISSING_INPUT $Path" }
            $current = [IO.Path]::GetDirectoryName($current)
            continue
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'OSL009:REPARSE A path chain contains a reparse point.'
        }
        if ($File -and $current -ceq $Path) {
            if ($item -isnot [IO.FileInfo]) { throw 'OSL009:FILE A regular file is required.' }
        }
        elseif ($item -isnot [IO.DirectoryInfo]) { throw 'OSL009:DIRECTORY A directory is required.' }
        if ($null -eq $nearest) { $nearest = $current }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        $nearest, [HubRadio.PortablePayloadNative]::CanonicalPath($nearest))) {
        throw 'OSL009:CANONICAL Aliased or noncanonical filesystem paths are forbidden.'
    }
}

function New-SafeDirectory([string]$Path) {
    Assert-SafePath $Path -AllowMissing
    if (-not (Test-Path -LiteralPath $Path)) {
        New-SafeDirectory ([IO.Path]::GetDirectoryName($Path))
        [HubRadio.PortablePayloadNative]::CreateNewDirectory($Path)
    }
    Assert-SafePath $Path
}

function Get-SafeTree([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-SafePath $directory
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force)) {
            Assert-SafePath $entry.FullName -File:($entry -is [IO.FileInfo])
            if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry.FullName) }
            $entry
        }
    }
}

function Get-FileEvidence([string]$Path) {
    Assert-SafePath $Path -File
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        return [pscustomobject]@{
            byteSize = $stream.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
    }
    finally { $stream.Dispose() }
}

function Assert-FileEvidence([string]$Path, [object]$Expected) {
    $actual = Get-FileEvidence $Path
    if ($actual.byteSize -ne $Expected.byteSize -or $actual.sha256 -cne $Expected.sha256) {
        throw "OSL009:HASH File bytes differ from validated evidence: $Path"
    }
}

function Get-TreeRecords([string]$Root) {
    $records = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in @(Get-SafeTree $Root)) {
        if ($entry -is [IO.FileInfo]) {
            $relative = [IO.Path]::GetRelativePath($Root, $entry.FullName).Replace('\', '/')
            $evidence = Get-FileEvidence $entry.FullName
            $records.Add($relative, [pscustomobject][ordered]@{
                path = $relative; byteSize = $evidence.byteSize; sha256 = $evidence.sha256
            })
        }
    }
    return ,$records
}

function Get-ArtifactSetSha256([object]$Records) {
    # Ordinal, NOT Sort-Object's culture-sensitive ordering. UTF-8 without BOM;
    # concatenated(path NUL byteSize-invariant NUL lowercase-sha256 LF).
    $builder = [Text.StringBuilder]::new()
    foreach ($record in $Records.Values) {
        [void]$builder.Append($record.path).Append([char]0).Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture)).Append(
            [char]0).Append($record.sha256).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
}

function Copy-PayloadFile([string]$SourcePath, [string]$RelativePath, [object]$Expected) {
    Assert-FileEvidence $SourcePath $Expected
    $destination = Get-LocalPath (Join-Path $stageRoot $RelativePath) 'Destination'
    if (-not (Test-Within $destination $stageRoot)) { throw 'OSL009:COPY_SCOPE Invalid destination.' }
    New-SafeDirectory ([IO.Path]::GetDirectoryName($destination))
    $readOnly = [IO.File]::GetAttributes($SourcePath) -band [IO.FileAttributes]::ReadOnly
    if ($RelativePath.StartsWith('Worker/', [StringComparison]::Ordinal) -and $readOnly -eq 0) {
        throw 'OSL009:READONLY Every validated Worker file must remain read-only.'
    }
    $source = [IO.File]::Open($SourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        # Copy only the hashed data stream, never unmeasured ADS such as Zone.Identifier.
        $target = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($target) }
        finally { $target.Dispose() }
    }
    finally { $source.Dispose() }
    if ($readOnly -ne 0) { [IO.File]::SetAttributes($destination, [IO.FileAttributes]::ReadOnly) }
    Assert-FileEvidence $SourcePath $Expected
    Assert-FileEvidence $destination $Expected
    if (([IO.File]::GetAttributes($SourcePath) -band [IO.FileAttributes]::ReadOnly) -ne $readOnly -or
        ([IO.File]::GetAttributes($destination) -band [IO.FileAttributes]::ReadOnly) -ne $readOnly) {
        throw 'OSL009:READONLY Source and copied read-only attributes must be preserved.'
    }
    $relative = [IO.Path]::GetRelativePath($stageRoot, $destination).Replace('\', '/')
    $payloadRecords.Add($relative, [pscustomobject][ordered]@{
        path = $relative; byteSize = [long]$Expected.byteSize; sha256 = [string]$Expected.sha256
    })
}

function Remove-OwnedTree([string]$Path) {
    # Called only for directories exclusively created by this invocation. Never
    # recursively follow a link, even during failure cleanup; leave unsafe debris.
    Assert-SafePath $Path
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        Assert-SafePath $entry.FullName -File:($entry -is [IO.FileInfo])
        if ($entry -is [IO.DirectoryInfo]) { Remove-OwnedTree $entry.FullName }
        else {
            [IO.File]::SetAttributes($entry.FullName, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($entry.FullName)
        }
    }
    [IO.Directory]::Delete($Path, $false)
}

foreach ($name in $inputPaths.Keys) {
    Assert-SafePath $inputPaths[$name] -File:($name -notin @('PythonRuntimePath', 'ModelBundlePath', 'WebView2RuntimePath'))
}
Assert-SafePath $OutputPath -AllowMissing
$PythonRuntimePath = $inputPaths.PythonRuntimePath
$ModelBundlePath = $inputPaths.ModelBundlePath
$WebView2RuntimePath = $inputPaths.WebView2RuntimePath
$evidenceSources = [ordered]@{
    'python-runtime-manifest.json' = Join-Path $repoRoot 'packaging\python\runtime-manifest.json'
    'ffmpeg-manifest.json' = Join-Path $repoRoot 'packaging\ffmpeg\manifest.json'
    'model-catalog.json' = Join-Path $repoRoot 'packaging\models\catalog.json'
    'webview2-runtime-manifest.json' = Join-Path $repoRoot 'packaging\webview2\runtime-manifest.json'
    'web-dependency-manifest.json' = Join-Path $repoRoot 'packaging\web\dependency-manifest.json'
}
$evidenceHashes = @{}
foreach ($name in $evidenceSources.Keys) { $evidenceHashes[$name] = Get-FileEvidence $evidenceSources[$name] }
$pythonVerifyScriptPath = Join-Path $repoRoot 'packaging\python\verify-runtime.ps1'
$verifyScriptEvidence = Get-FileEvidence $pythonVerifyScriptPath
$noticesPath = Join-Path $repoRoot 'docs\dev\licenses\THIRD-PARTY-NOTICES.md'
$noticesEvidence = Get-FileEvidence $noticesPath
$pythonManifest = Get-Content -LiteralPath $evidenceSources['python-runtime-manifest.json'] -Raw -Encoding utf8 | ConvertFrom-Json
$ffmpegManifest = Get-Content -LiteralPath $evidenceSources['ffmpeg-manifest.json'] -Raw -Encoding utf8 | ConvertFrom-Json
$modelCatalog = Get-Content -LiteralPath $evidenceSources['model-catalog.json'] -Raw -Encoding utf8 | ConvertFrom-Json
$webView2Manifest = Get-Content -LiteralPath $evidenceSources['webview2-runtime-manifest.json'] -Raw -Encoding utf8 | ConvertFrom-Json

$ffmpegInputs = [ordered]@{ ffmpeg = $inputPaths.FfmpegPath; ffprobe = $inputPaths.FfprobePath; license = $inputPaths.FfmpegLicensePath }
if (@($ffmpegManifest.selectedFiles).Count -ne 3) { throw 'OSL009:FFMPEG_ROLES Exactly three roles are required.' }
$ffmpegFiles = @{}
foreach ($role in $ffmpegInputs.Keys) {
    $selected = @($ffmpegManifest.selectedFiles | Where-Object { $_.role -ceq $role })
    if ($selected.Count -ne 1) { throw 'OSL009:FFMPEG_ROLES Duplicate or missing role.' }
    $ffmpegFiles[$role] = $selected[0]
    Assert-FileEvidence $ffmpegInputs[$role] $selected[0]
}

$models = @($modelCatalog.models | Where-Object { $_.runtimeEligible -eq $true })
if ($models.Count -ne 1 -or $models[0].modelId -cne 'asr.faster-whisper-small.cpu-int8' -or
    $models[0].disposition -cne 'adopted-conditional') { throw 'OSL009:MODEL_ALLOWLIST Invalid eligible model.' }
$runtimeModel = $models[0]
$modelNames = [string[]]@($runtimeModel.bundle.files | ForEach-Object name)
[Array]::Sort($modelNames, [StringComparer]::Ordinal)
if ($modelNames.Count -ne 4 -or ($modelNames -join '/') -cne 'config.json/model.bin/tokenizer.json/vocabulary.txt') {
    throw 'OSL009:MODEL_FILES The catalog must describe the fixed four direct files.'
}
$modelRelativePath = [string]$runtimeModel.bundle.relativePath
if ([string]::IsNullOrWhiteSpace($modelRelativePath) -or [IO.Path]::IsPathRooted($modelRelativePath)) {
    throw 'OSL009:MODEL_PATH The catalog bundle path must be local-relative.'
}
$modelCatalogRoot = Join-Path $OutputPath 'ModelCatalog'
$modelDestination = Get-LocalPath (Join-Path $modelCatalogRoot $modelRelativePath) 'ModelBundleRelativePath'
if ($modelDestination -ieq $modelCatalogRoot -or -not (Test-Within $modelDestination $modelCatalogRoot)) {
    throw 'OSL009:MODEL_PATH The catalog bundle path must remain below ModelCatalog.'
}
function Assert-ModelBundle {
    Assert-SafePath $ModelBundlePath
    $entries = @(Get-ChildItem -LiteralPath $ModelBundlePath -Force)
    $names = [string[]]@($entries | Where-Object { $_ -is [IO.FileInfo] } | ForEach-Object Name)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    if (($names -join '/') -cne ($modelNames -join '/')) { throw 'OSL009:MODEL_FILES Unexpected model file set.' }
    foreach ($entry in $entries) {
        Assert-SafePath $entry.FullName -File:($entry -is [IO.FileInfo])
        if ($entry -is [IO.DirectoryInfo]) {
            if ($entry.Name -cne '.cache') { throw 'OSL009:MODEL_FILES Only the optional HF .cache directory is allowed.' }
            Get-SafeTree $entry.FullName | Out-Null
        }
    }
    foreach ($file in $runtimeModel.bundle.files) { Assert-FileEvidence (Join-Path $ModelBundlePath $file.name) $file }
}
Assert-ModelBundle

$webOutput = $webView2Manifest.output
if ($webView2Manifest.schemaVersion -cne '1.0.0' -or $webView2Manifest.runtime.distributionMode -cne 'FixedVersion' -or
    $webView2Manifest.runtime.architecture -cne 'x64' -or $webOutput.relativePath -cne 'WebView2Runtime' -or
    $webOutput.entryPoint -cne 'msedgewebview2.exe' -or $webOutput.copyMode -cne 'entire-unmodified-tree' -or
    $webOutput.artifactSetDigestAlgorithm.pathOrder -cne 'Ordinal') {
    throw 'OSL009:WEBVIEW2_MANIFEST Unsupported Fixed Runtime contract.'
}
$webFiles = Get-TreeRecords $WebView2RuntimePath
if ($webFiles.Count -ne $webOutput.expectedFileCount -or
    [long](($webFiles.Values | Measure-Object byteSize -Sum).Sum) -ne $webOutput.expectedTotalBytes -or
    (Get-ArtifactSetSha256 $webFiles) -cne $webOutput.expectedArtifactSetSha256) {
    throw 'OSL009:WEBVIEW2_HASH The entire expanded vendor tree must match the manifest.'
}
# OSL-005 accepted this signature evidence. Offline assembly rechecks the pinned
# entire-tree and selected-file hashes, not the current certificate store.
$microsoftSubject = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
if (@($webView2Manifest.runtime.selectedSignedFiles).Count -ne 2) { throw 'OSL009:SIGNATURE_FILES Expected two signed files.' }
foreach ($name in @('msedge.dll', 'msedgewebview2.exe')) {
    $selected = @($webView2Manifest.runtime.selectedSignedFiles | Where-Object { $_.path -ceq $name })
    if ($selected.Count -ne 1 -or $selected[0].signatureStatus -cne 'Valid' -or
        $selected[0].signerSubject -cne $microsoftSubject) { throw 'OSL009:SIGNATURE_FILES Unexpected signer evidence.' }
    Assert-FileEvidence (Join-Path $WebView2RuntimePath $name) $selected[0]
}
$licenseSnapshot = $webView2Manifest.runtime.license.evidence
$licenseEvidence = Get-FileEvidence $inputPaths.WebView2LicensePath
if ($licenseSnapshot.kind -cne 'official-license-modal-page-text-snapshot' -or
    $licenseSnapshot.isStandaloneLicenseFile -ne $false -or $licenseEvidence.sha256 -cne $licenseSnapshot.sha256) {
    throw 'OSL009:LICENSE_SNAPSHOT The fixed license-page-text snapshot must match its evidence SHA-256.'
}
Assert-FileEvidence (Join-Path $PythonRuntimePath 'LICENSE.txt') $pythonManifest.runtime.license
$workerFiles = Get-TreeRecords $PythonRuntimePath
if ($workerFiles.Count -ne ([int]$pythonManifest.output.expectedFileCount + 1) -or
    -not $workerFiles.ContainsKey([string]$pythonManifest.output.fileManifestName)) {
    # The reused verifier omits files by manifest NAME. Reject extra copies of that
    # name in subdirectories rather than copying unverified extras into Worker.
    throw 'OSL009:WORKER_FILES Expected the runtime payload plus exactly one root file manifest.'
}

$payloadRoots = @('Worker', 'Tools', 'ModelCatalog', 'WebView2Runtime', 'Licenses', 'Evidence')
$payloadRecords = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
$parent = [IO.Path]::GetDirectoryName($OutputPath)
New-SafeDirectory $parent
$nonce = [guid]::NewGuid().ToString('N')
$stageRoot = $OutputPath + '.' + $nonce + '.partial'
$verificationWorkspace = $OutputPath + '.' + $nonce + '.verification.partial'
foreach ($temporaryPath in @($stageRoot, $verificationWorkspace)) {
    Assert-SafePath $temporaryPath -AllowMissing
    foreach ($inputPath in $inputPaths.Values) {
        if ((Test-Within $temporaryPath $inputPath) -or (Test-Within $inputPath $temporaryPath)) {
            throw 'OSL009:OVERLAP Temporary directories must not overlap any input.'
        }
    }
}
$stageOwned = $false
$verificationOwned = $false
try {
    # Both are siblings of the final directory: same-volume rename, no replacement.
    [HubRadio.PortablePayloadNative]::CreateNewDirectory($stageRoot)
    $stageOwned = $true
    Assert-SafePath $stageRoot
    [HubRadio.PortablePayloadNative]::CreateNewDirectory($verificationWorkspace)
    $verificationOwned = $true
    Assert-SafePath $verificationWorkspace
    Assert-FileEvidence $pythonVerifyScriptPath $verifyScriptEvidence
    & $pythonVerifyScriptPath -RuntimePath $PythonRuntimePath -WorkspacePath $verificationWorkspace 6>$null | Out-Null
    Assert-FileEvidence $pythonVerifyScriptPath $verifyScriptEvidence
    foreach ($name in $evidenceSources.Keys) { Assert-FileEvidence $evidenceSources[$name] $evidenceHashes[$name] }

    foreach ($root in $payloadRoots) { New-SafeDirectory (Join-Path $stageRoot $root) }
    foreach ($tree in @(
        @{ source = $PythonRuntimePath; destination = 'Worker'; files = $workerFiles },
        @{ source = $WebView2RuntimePath; destination = 'WebView2Runtime'; files = $webFiles }
    )) {
        # Preserve every vendor file and empty directory, including BAT and licenses.
        # This is data copying, not shell execution or blanket signature approval.
        foreach ($entry in @(Get-SafeTree $tree.source)) {
            $relative = [IO.Path]::GetRelativePath($tree.source, $entry.FullName).Replace('\', '/')
            $target = $tree.destination + '/' + $relative
            if ($entry -is [IO.DirectoryInfo]) { New-SafeDirectory (Join-Path $stageRoot $target) }
            else {
                if (-not $tree.files.ContainsKey($relative)) { throw 'OSL009:TREE_CHANGED An input file was added.' }
                Copy-PayloadFile $entry.FullName $target $tree.files[$relative]
            }
        }
        if ((Get-ArtifactSetSha256 (Get-TreeRecords $tree.source)) -cne (Get-ArtifactSetSha256 $tree.files)) {
            throw 'OSL009:TREE_CHANGED An input tree changed after validation.'
        }
    }
    Copy-PayloadFile $inputPaths.FfmpegPath 'Tools/ffmpeg.exe' $ffmpegFiles.ffmpeg
    Copy-PayloadFile $inputPaths.FfprobePath 'Tools/ffprobe.exe' $ffmpegFiles.ffprobe
    Copy-PayloadFile $inputPaths.FfmpegLicensePath 'Licenses/FFmpeg-LICENSE.txt' $ffmpegFiles.license
    Copy-PayloadFile (Join-Path $PythonRuntimePath 'LICENSE.txt') 'Licenses/CPython-LICENSE.txt' $pythonManifest.runtime.license
    Copy-PayloadFile $inputPaths.WebView2LicensePath 'Licenses/WebView2-FIXED-LICENSE-PAGE.txt' $licenseEvidence
    Copy-PayloadFile $noticesPath 'Licenses/THIRD-PARTY-NOTICES-NONFINAL.md' $noticesEvidence
    Copy-PayloadFile $evidenceSources['model-catalog.json'] 'ModelCatalog/catalog.json' $evidenceHashes['model-catalog.json']
    foreach ($file in $runtimeModel.bundle.files) {
        Copy-PayloadFile (Join-Path $ModelBundlePath $file.name) ('ModelCatalog/' + $runtimeModel.bundle.relativePath + '/' + $file.name) $file
    }
    Assert-ModelBundle
    foreach ($name in $evidenceSources.Keys) {
        Copy-PayloadFile $evidenceSources[$name] ('Evidence/' + $name) $evidenceHashes[$name]
    }

    $actual = Get-TreeRecords $stageRoot
    $digest = Get-ArtifactSetSha256 $payloadRecords
    if ($actual.Count -ne $payloadRecords.Count -or (Get-ArtifactSetSha256 $actual) -cne $digest -or
        @(Get-ChildItem -LiteralPath $stageRoot -Force | Where-Object { $_.Name -cnotin $payloadRoots }).Count -ne 0) {
        throw 'OSL009:LAYOUT The staged payload differs from its validated inputs or allowed roots.'
    }
    # Inventoried BEFORE writing this file: the manifest never hashes itself.
    $payloadManifest = [ordered]@{
        schemaVersion = '1.0.0'
        taskId = 'OSL-009'
        architecture = 'x64'
        releaseApproved = $false
        appPublishRequired = $true
        rootDirectories = $payloadRoots
        webView2LicenseSnapshot = [ordered]@{
            path = 'Licenses/WebView2-FIXED-LICENSE-PAGE.txt'
            kind = 'official-license-modal-page-text-snapshot'
            isStandaloneLicenseFile = $false
        }
        fileCount = $actual.Count
        totalBytes = [long](($actual.Values | Measure-Object byteSize -Sum).Sum)
        artifactSetSha256 = $digest
        files = @($actual.Values)
    }
    $manifestPath = Join-Path $stageRoot 'Evidence\portable-payload.json'
    Assert-SafePath ([IO.Path]::GetDirectoryName($manifestPath))
    $manifestStream = [IO.File]::Open($manifestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($payloadManifest | ConvertTo-Json -Depth 8) + "`n")
        $manifestStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $manifestStream.Dispose() }
    Remove-OwnedTree $verificationWorkspace
    $verificationOwned = $false
    Assert-SafePath $stageRoot
    Assert-SafePath $OutputPath -AllowMissing
    [IO.Directory]::Move($stageRoot, $OutputPath)
    $stageOwned = $false
}
finally {
    try { if ($verificationOwned) { Remove-OwnedTree $verificationWorkspace } }
    finally { if ($stageOwned) { Remove-OwnedTree $stageRoot } }
}

[pscustomobject][ordered]@{
    OutputPath = $OutputPath
    ArtifactSetSha256 = $digest
    FileCount = $payloadManifest.fileCount
    TotalBytes = $payloadManifest.totalBytes
    ReleaseApproved = $false
} | ConvertTo-Json -Compress