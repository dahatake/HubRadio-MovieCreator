#Requires -Version 7.0

<#
OSL-012: one-shot LOCAL CANDIDATE build, not a general release pipeline.
Prerequisites: acquire the SDK/tools first (git.exe must be on PATH), run build.ps1 for
restore/build (including Web dist), and run prepare-payload.ps1 separately.
Use the latest installed Windows x64 pwsh, -NoLogo -NoProfile -File, with all five
parameters. The four paths must be absolute local paths; Version is a SemVer
token. MSBuild delimiters/expansions in paths are deliberately unsupported.
Do not run concurrent builds or modify inputs during this invocation.

Only App/HubRadio.App.exe is the executable candidate. Contents is an audit copy
of FilesToBundle (including its unbundled host input), NOT a folder to ship.
BuildLogs (including the absolute-path TSV and sbom-validation.private.json
diagnostics) and ComponentInputs (including project.assets.json) are PRIVATE
build artifacts. Evidence/sbom-validation.json contains only a checked summary
and the private JSON's SHA-256, never raw tool parameters or exception text.
Evidence/portable-release.json binds the candidate and Contents by
SHA-256; it contains no local absolute paths. Git status and measured bundle
bytes describe this run, not a reproducible build from HEAD or release approval.
SBOM license fetching is disabled, but component-detection metadata requests
are not a network-isolation guarantee. Only curated dependency descriptors are
submitted for detection, never the repository, media, .env or other worktrees.

OutputPath must not exist and must be below artifacts/portable. Success renames
a unique sibling .partial without replacement and writes one compact JSON result
(relative candidate/evidence/SBOM paths, SHA-256, counts, releaseApproved=false).
Failure throws; an owned partial is retained for diagnosis, never recursively
deleted. This script neither runs the App nor modifies/deletes prepared inputs.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$DotnetPath,
    [Parameter(Mandatory)] [string]$SbomToolPath,
    [Parameter(Mandatory)] [string]$PayloadPath,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64' -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'OSL012:HOST Windows x64 and PowerShell 7+ Core x64 are required.'
}

function Get-LocalPath([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Trim() -or $Path -notmatch '^[A-Za-z]:[\\/]') {
        throw "OSL012:INPUT_PATH:${Name} requires a canonical absolute local path."
    }
    if ($Path -match '[;,%''"$@`\x00-\x1f\x7f]') {
        throw "OSL012:MSBUILD_PATH:${Name} contains unsupported MSBuild delimiters, expansions or control characters."
    }
    $normalized = [IO.Path]::TrimEndingDirectorySeparator($Path.Replace('/', '\'))
    foreach ($segment in $normalized.Substring(3).Split('\')) {
        if ($normalized.Length -eq 3) { break }
        if ([string]::IsNullOrEmpty($segment) -or $segment -cne $segment.Trim() -or $segment.EndsWith('.') -or
            $segment -match '[<>:"|?*]|~[0-9]' -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
            throw "OSL012:INPUT_PATH:${Name} forbids traversal, ADS, short aliases and device names."
        }
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($normalized, [IO.Path]::GetFullPath($normalized))) {
        throw "OSL012:INPUT_PATH:${Name} is not canonical."
    }
    return $normalized
}

function Test-Within([string]$Path, [string]$Root) {
    return [StringComparer]::OrdinalIgnoreCase.Equals($Path, $Root) -or
        $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoOverlap([string]$Path, [string[]]$Inputs) {
    foreach ($inputPath in $Inputs) {
        if ((Test-Within $Path $inputPath) -or (Test-Within $inputPath $Path)) {
            throw 'OSL012:OVERLAP Output/workspace must not overlap payload, tools or dependency sources.'
        }
    }
}

function Assert-SafePath([string]$Path, [switch]$File, [switch]$AllowMissing) {
    $Path = Get-LocalPath $Path 'Path'
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path)).DriveType -notin @(
        [IO.DriveType]::Fixed, [IO.DriveType]::Removable, [IO.DriveType]::CDRom, [IO.DriveType]::Ram)) {
        throw 'OSL012:NONLOCAL Mapped network/nonlocal drives are forbidden.'
    }
    # Managed ancestor checks, as in build-package.ps1. This is a bounded local
    # developer workspace, not a sandbox against a hostile concurrent filesystem.
    for ($current = $Path; $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] {
            if ($AllowMissing) { continue }
            throw 'OSL012:MISSING A required input is missing; prepare the payload and run build.ps1 first.'
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'OSL012:REPARSE A path chain contains a reparse point.'
        }
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals($current, [IO.Path]::TrimEndingDirectorySeparator($item.FullName))) {
            throw 'OSL012:CANONICAL A filesystem path is aliased.'
        }
        if ($File -and $current -ceq $Path) {
            if ($item -isnot [IO.FileInfo]) { throw 'OSL012:FILE A regular file is required.' }
        }
        elseif ($item -isnot [IO.DirectoryInfo]) { throw 'OSL012:DIRECTORY A directory is required.' }
    }
}

function Get-RelativeName([string]$Path) {
    $relative = $Path.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) {
        throw 'OSL012:RELATIVE_PATH An unrooted bundle-relative path is required.'
    }
    # Reuse segment validation without resolving away any traversal first.
    try { [void](Get-LocalPath ('C:\' + $relative) 'RelativePath') }
    catch { throw 'OSL012:RELATIVE_PATH Invalid bundle-relative path or unsupported delimiter.' }
    if ($relative.EndsWith('/')) { throw 'OSL012:RELATIVE_PATH A file path must not end in a separator.' }
    return $relative
}

function Get-SafeFiles([string]$Root) {
    Assert-SafePath $Root
    foreach ($entry in @(Get-ChildItem -LiteralPath $Root -Force)) {
        Assert-SafePath $entry.FullName -File:($entry -is [IO.FileInfo])
        if ($entry -is [IO.DirectoryInfo]) { Get-SafeFiles $entry.FullName }
        else { $entry }
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
        throw 'OSL012:HASH File bytes changed or do not match their evidence.'
    }
}

function Get-TreeRecords([string]$Root) {
    $records = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-SafeFiles $Root)) {
        $relative = Get-RelativeName ([IO.Path]::GetRelativePath($Root, $file.FullName))
        $evidence = Get-FileEvidence $file.FullName
        $records.Add($relative, [pscustomobject][ordered]@{
            path = $relative; byteSize = $evidence.byteSize; sha256 = $evidence.sha256
        })
    }
    return ,$records
}

function Get-ArtifactSetSha256([object]$Records) {
    # OSL-009 contract: Ordinal path order, UTF-8 without BOM,
    # concatenated(path NUL byteSize-invariant NUL lowercase-sha256 LF).
    $text = [Text.StringBuilder]::new()
    foreach ($record in $Records.Values) {
        [void]$text.Append($record.path).Append([char]0).Append(
            ([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture)).Append(
            [char]0).Append($record.sha256).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant()
}

function Copy-VerifiedFile([string]$Source, [string]$Destination, [object]$Expected) {
    if (-not (Test-Within $Destination $stageRoot)) { throw 'OSL012:COPY_SCOPE Destination escaped staging.' }
    Assert-FileEvidence $Source $Expected
    $parent = [IO.Path]::GetDirectoryName($Destination)
    Assert-SafePath $parent -AllowMissing
    [void][IO.Directory]::CreateDirectory($parent)
    Assert-SafePath $parent
    $readOnly = [IO.File]::GetAttributes($Source) -band [IO.FileAttributes]::ReadOnly
    $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        # Only the measured data stream; do not propagate unmeasured ADS.
        $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $sourceStream.CopyTo($destinationStream) }
        finally { $destinationStream.Dispose() }
    }
    finally { $sourceStream.Dispose() }
    if ($readOnly -ne 0) { [IO.File]::SetAttributes($Destination, [IO.FileAttributes]::ReadOnly) }
    Assert-FileEvidence $Source $Expected
    Assert-FileEvidence $Destination $Expected
}

function Read-Json([string]$Path) {
    Assert-SafePath $Path -File
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -AsHashtable
}

function Get-PublicSbomValidation([string]$PrivateValidationPath) {
    try {
        $source = Get-FileEvidence $PrivateValidationPath
        # A one-item root array must not become an accepted validation object.
        $validation = [IO.File]::ReadAllText($PrivateValidationPath, [Text.UTF8Encoding]::new($false, $true)) |
            ConvertFrom-Json -AsHashtable -NoEnumerate
        Assert-FileEvidence $PrivateValidationPath $source
    }
    catch { throw 'OSL012:SBOM_VALIDATION A readable, unchanged JSON validation result is required; inspect private build logs.' }
    if ($validation -isnot [Collections.IDictionary] -or $validation['Result'] -isnot [string] -or
        $validation['Result'] -cne 'Success' -or $validation['ValidationErrors'] -isnot [Collections.IDictionary]) {
        throw 'OSL012:SBOM_VALIDATION Expected Result Success, integer Count zero and an empty Errors array; inspect private build logs.'
    }
    # sbom-tool 4.1.5 emits { Count: 0, Errors: [] }, not an empty dictionary.
    # Enumerate keys explicitly: .Count would read the JSON member, not its shape.
    $errorSummary = $validation['ValidationErrors']
    $errorKeys = @($errorSummary.GetEnumerator() | ForEach-Object Key)
    $reportedCount = $errorSummary['Count']
    $reportedErrors = $errorSummary['Errors']
    if ($errorKeys.Count -ne 2 -or $errorKeys -cnotcontains 'Count' -or $errorKeys -cnotcontains 'Errors' -or
        ($reportedCount -isnot [long] -and $reportedCount -isnot [int]) -or $reportedCount -ne 0 -or
        $reportedErrors -isnot [array] -or $reportedErrors.Length -ne 0) {
        throw 'OSL012:SBOM_VALIDATION Expected Result Success, integer Count zero and an empty Errors array; inspect private build logs.'
    }
    return [ordered]@{
        schemaVersion = '1.0.0'; result = $validation['Result']
        validationErrorCount = [int]$reportedCount
        toolVersion = '4.1.5'; sourceSha256 = $source.sha256
    }
}

function Assert-PublicMetadata([object]$Value) {
    if ($Value -is [string]) {
        if ($Value -match '(?i)(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\|file:/)') {
            throw 'OSL012:METADATA_PATH Public JSON must not disclose local absolute paths; inspect private build logs.'
        }
    }
    elseif ($Value -is [Collections.IDictionary]) {
        foreach ($entry in $Value.GetEnumerator()) { Assert-PublicMetadata $entry.Key; Assert-PublicMetadata $entry.Value }
    }
    elseif ($Value -is [Collections.IList]) {
        foreach ($entry in $Value) { Assert-PublicMetadata $entry }
    }
}

function Get-BuildToolTimeoutMilliseconds([string]$Name) {
    # Build safety bounds, NOT performance SLAs. Keep step names closed so they
    # are also safe log basenames and cannot disclose arguments in diagnostics.
    switch -CaseSensitive -Regex ($Name) {
        '\A(?:publish|sbom-generate|sbom-validate)\z' { return 600000 }
        '\A(?:dotnet-version|sbom-version|git-(?:head|status)-(?:before|after))\z' { return 30000 }
        default { throw 'OSL012:TOOL_NAME Unexpected build tool step.' }
    }
}

function Invoke-BuildTool([string]$Tool, [string[]]$Arguments, [string]$Name, [int]$TimeoutMilliseconds = 0) {
    $safetyBound = Get-BuildToolTimeoutMilliseconds $Name
    # Internal direct-test override only; never a builder parameter or a way to
    # extend/disable the named safety bound in production calls.
    if ($TimeoutMilliseconds -eq 0) { $TimeoutMilliseconds = $safetyBound }
    if ($TimeoutMilliseconds -lt 1 -or $TimeoutMilliseconds -gt $safetyBound) {
        throw 'OSL012:TOOL_TIMEOUT Invalid internal build tool timeout.'
    }
    Assert-SafePath $Tool -File
    Assert-SafePath $logRoot
    $start = [Diagnostics.ProcessStartInfo]::new($Tool)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $repoRoot
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $start.Environment['TEMP'] = Join-Path $logRoot 'tmp'
    $start.Environment['TMP'] = $start.Environment['TEMP']
    $stdoutPath = Join-Path $logRoot ($Name + '.stdout.log')
    $stdout = $null
    $stderr = $null
    $drainTask = $null
    $drainCancellation = [Threading.CancellationTokenSource]::new()
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $completed = $false
    try {
        $stdout = [IO.File]::Open($stdoutPath, [IO.FileMode]::CreateNew)
        $stderr = [IO.File]::Open((Join-Path $logRoot ($Name + '.stderr.log')), [IO.FileMode]::CreateNew)
        $started = $process.Start()
        if (-not $started) { throw 'OSL012:PROCESS Unable to start a build tool.' }
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout, 81920, $drainCancellation.Token)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr, 81920, $drainCancellation.Token)
        $drainTask = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($outTask, $errTask))
        # A descendant can keep a redirected pipe open even after the tool exits.
        # Never follow a timed wait with parameterless WaitForExit/GetResult.
        if (-not $process.WaitForExit($TimeoutMilliseconds) -or -not $drainTask.Wait(5000)) {
            throw "OSL012:TIMEOUT $Name exceeded its build safety bound; see BuildLogs/$Name.*.log."
        }
        if ($process.ExitCode -ne 0) { throw "OSL012:PROCESS $Name failed (exit $($process.ExitCode)); see BuildLogs/$Name.*.log." }
        $completed = $true
    }
    finally {
        if ($started -and -not $completed) {
            # Kill is asynchronous; cleanup is bounded too. An exit race or kill
            # failure must not replace the stable TIMEOUT diagnostic with tool data.
            try { $process.Kill($true) } catch { }
            try { [void]$process.WaitForExit(5000) } catch { }
        }
        $drainCancellation.Cancel()
        if ($null -ne $drainTask) {
            try { [void]$drainTask.Wait(5000) } catch { }
        }
        $process.Dispose()
        if ($null -ne $stdout) { $stdout.Dispose() }
        if ($null -ne $stderr) { $stderr.Dispose() }
        $drainCancellation.Dispose()
    }
    return [IO.File]::ReadAllText($stdoutPath)
}

function Assert-DistributableBundleInput([string]$Source, [string]$Relative) {
    # Independent of PortablePayload.targets. Inspect names, never the contents
    # of a rejected file, and never echo a potentially sensitive source/name.
    foreach ($path in @($Source, $Relative)) {
        $normalized = $path.Replace('\', '/')
        if ([IO.Path]::GetExtension($normalized) -iin @('.pubxml', '.csproj', '.targets', '.props', '.pdb') -or
            $normalized -match '(?i)(?:^|/)Properties/PublishProfiles(?:/|$)' -or
            [IO.Path]::GetFileName($normalized) -match '(?i)\A\.env(?:\..*)?\z') {
            throw 'OSL012:BUNDLE_DEVELOPER Developer-only files cannot be bundle inputs.'
        }
    }
    if (((Test-Within $Source (Join-Path $repoRoot 'src/HubRadio.App')) -and
            [IO.Path]::GetExtension($Source) -iin @('.cs', '.xaml')) -or
        ($Relative -match '(?i)\A(?:src/HubRadio\.App/.*)?[^/]+\.(?:cs|xaml)\z')) {
        throw 'OSL012:BUNDLE_DEVELOPER App source files cannot be bundle inputs.'
    }
    # Vendor Python sources and native headers are legitimate runtime payload;
    # do not blacklist .py/.h (or every source-looking extension) globally.
}

function Read-BundleSources([string]$ManifestPath) {
    Assert-SafePath $ManifestPath -File
    $sources = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines($ManifestPath)) {
        $columns = $line.Split("`t")
        if ($columns.Count -ne 2) { throw 'OSL012:BUNDLE_TSV Expected sourceFullPath TAB relativePath, without a header.' }
        $source = Get-LocalPath $columns[0] 'BundleSource'
        $relative = Get-RelativeName $columns[1]
        Assert-DistributableBundleInput $source $relative
        if (-not $names.Add($relative)) { throw 'OSL012:BUNDLE_DUPLICATE Duplicate normalized bundle destination.' }
        if (@($sourceRoots | Where-Object { Test-Within $source $_ }).Count -eq 0 -or (Test-Within $source $stageRoot)) {
            throw 'OSL012:BUNDLE_SOURCE Bundle source must be an existing project, SDK, restored dependency or prepared payload file.'
        }
        $sources.Add($relative, $source)
    }
    # Complete metadata preflight before the caller measures/copies ANY Contents.
    return ,$sources
}

function Get-BundleContract([object]$Sources, [string]$SdkVersion) {
    if ($SdkVersion -cne '10.0.401') { throw 'OSL012:SDK_VERSION Expected the fixed .NET SDK 10.0.401.' }
    # Windows destination lookup only: keep the original names and Ordinal order
    # intact for Contents/evidence hashing. Read-BundleSources rejects case collisions.
    $sourceLookup = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Sources.Keys) { $sourceLookup.Add($name, $Sources[$name]) }
    foreach ($required in @(
        'HubRadio.App.exe', 'HubRadio.App.dll', 'HubRadio.App.pri', 'HubRadio.App.deps.json', 'HubRadio.App.runtimeconfig.json',
        'HubRadio.Core.dll', 'HubRadio.Infrastructure.dll', 'System.Private.CoreLib.dll',
        'Microsoft.WinUI.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
        'WebAssets/index.html', 'WebAssets/asset-manifest.json', 'Worker/python.exe',
        'Tools/ffmpeg.exe', 'Tools/ffprobe.exe', 'WebView2Runtime/msedgewebview2.exe',
        'Worker/runtime-files.json', 'Fonts/NotoSansJP-Regular.otf', 'Fonts/font-manifest.json', 'Fonts/LICENSE',
        'Evidence/application-build.json'
    )) {
        if (-not $sourceLookup.ContainsKey($required)) { throw "OSL012:BUNDLE_REQUIRED Missing actual bundle input: $required" }
    }
    # SDK 10.0.401 BundledVersions.props selects runtime/host pack 10.0.12.
    # That runtime pack's data/RuntimeList.xml marks coreclr.dll DropFromSingleFile=true:
    # the CLR is integrated into singlefilehost, not a missing loose runtime DLL.
    $expectedHost = Join-Path $repoRoot 'src/HubRadio.App/obj/x64/Release/net10.0-windows10.0.26100.0/win-x64/singlefilehost.exe'
    $coreLibSuffix = '/microsoft.netcore.app.runtime.win-x64/10.0.12/runtimes/win-x64/lib/net10.0/System.Private.CoreLib.dll'
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($sourceLookup['HubRadio.App.exe'].Replace('/', '\'), $expectedHost.Replace('/', '\')) -or
        -not $sourceLookup['System.Private.CoreLib.dll'].Replace('\', '/').EndsWith($coreLibSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OSL012:BUNDLE_RUNTIME Expected the fixed win-x64 singlefilehost input and .NET 10.0.12 CoreLib.'
    }
    # Verified in dotnet/runtime v10.0.12: Bundle/TargetInfo.cs chooses major 6,
    # Bundle/Manifest.cs writes minor 0; native corehost/bundle/header.cpp accepts it.
    # The obj host carries App resources, so its product version is NOT the SDK version.
    return [pscustomobject]@{ runtimeKind = 'integrated-clr'; runtimeVersion = '10.0.12'; bundleMajorVersion = 6; bundleMinorVersion = 0 }
}

function Assert-ApplicationBuildEvidence([string]$ContentsRoot) {
    $identity = Read-Json (Join-Path $ContentsRoot 'Evidence/application-build.json')
    $dll = Join-Path $ContentsRoot 'HubRadio.App.dll'
    $actual = Get-FileEvidence $dll
    if ($identity -isnot [Collections.IDictionary] -or @($identity.GetEnumerator()).Count -ne 5 -or
        $identity['schemaVersion'] -ne 1 -or $identity['relativePath'] -cne 'HubRadio.App.dll' -or
        ($identity['size'] -isnot [long] -and $identity['size'] -isnot [int]) -or $identity['size'] -ne $actual.byteSize -or
        $identity['sha256'] -cne $actual.sha256 -or
        $identity['version'] -cne [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()) {
        throw 'OSL012:APPLICATION_BUILD App build evidence must identify the actual compiled DLL, not the bundle EXE or release approval.'
    }
}

function Assert-Candidate([string]$AppRoot, [long]$ContentBytes, [long]$HostBytes, [object]$Contract) {
    Assert-SafePath $AppRoot
    $entries = @(Get-ChildItem -LiteralPath $AppRoot -Force)
    if ($entries.Count -ne 1 -or $entries[0] -isnot [IO.FileInfo] -or $entries[0].Name -cne 'HubRadio.App.exe') {
        throw 'OSL012:APP_LAYOUT App must contain only HubRadio.App.exe, with no other files or directories.'
    }
    Assert-SafePath $entries[0].FullName -File
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($entries[0].FullName))
    try {
        # .NET apphost marker is preceded by the Int64 bundle-header offset.
        # Use the SDK's actual unbundled host size: a self-contained host can be
        # larger than a plain apphost. Never scan the multi-GB embedded payload.
        if ($HostBytes -lt 64 -or $HostBytes -gt 64MB -or $HostBytes -ge $reader.BaseStream.Length) {
            throw 'OSL012:BUNDLE Invalid .NET unbundled host size.'
        }
        $prefix = $reader.ReadBytes([int]$HostBytes)
        $marker = [Convert]::ToHexString($prefix).IndexOf(
            '8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE', [StringComparison]::Ordinal)
        # Contents includes the unbundled host once; it becomes the EXE prefix,
        # not another embedded file. Retain the uncompressed size lower bound.
        if ($prefix.Length -lt 64 -or $prefix[0] -ne 0x4d -or $prefix[1] -ne 0x5a -or
            $marker -lt 16 -or $marker % 2 -ne 0 -or $reader.BaseStream.Length -le $ContentBytes) {
            throw 'OSL012:BUNDLE An uncompressed single-file bundle, not a plain apphost, is required.'
        }
        $offset = [BitConverter]::ToInt64($prefix, [int]($marker / 2) - 8)
        if ($offset -le 0 -or $offset -ge $reader.BaseStream.Length - 12) { throw 'OSL012:BUNDLE Invalid bundle header offset.' }
        $reader.BaseStream.Position = $offset
        if ($reader.ReadUInt32() -ne $Contract.bundleMajorVersion -or $reader.ReadUInt32() -ne $Contract.bundleMinorVersion -or
            $reader.ReadInt32() -le 0) {
            throw 'OSL012:BUNDLE Expected a nonempty .NET bundle v6 header.'
        }
    }
    finally { $reader.Dispose() }
}

function Move-PrivateEvidence([string]$StageRoot, [string]$OutputPath) {
    $privateRoot = $OutputPath + '.private'
    Assert-SafePath $privateRoot -AllowMissing
    if (Test-Path -LiteralPath $privateRoot) { throw 'OSL012:PRIVATE_EXISTS Private evidence path will not be overwritten.' }
    $privateParent = [IO.Path]::GetDirectoryName($privateRoot)
    Assert-SafePath $privateParent
    [void][IO.Directory]::CreateDirectory($privateRoot)
    [IO.Directory]::Move((Join-Path $StageRoot 'BuildLogs'), (Join-Path $privateRoot 'BuildLogs'))
    [IO.Directory]::Move((Join-Path $StageRoot 'ComponentInputs'), (Join-Path $privateRoot 'ComponentInputs'))
    return $privateRoot
}

$semver = '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'
if ($Version.Length -gt 128 -or $Version -cnotmatch $semver) { throw 'OSL012:VERSION Version must be a safe SemVer token (major.minor.patch[-prerelease][+metadata]).' }
$DotnetPath = Get-LocalPath $DotnetPath 'DotnetPath'
$SbomToolPath = Get-LocalPath $SbomToolPath 'SbomToolPath'
$PayloadPath = Get-LocalPath $PayloadPath 'PayloadPath'
$OutputPath = Get-LocalPath $OutputPath 'OutputPath'
$repoRoot = Get-LocalPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))) 'Repository'
$artifactRoot = Join-Path $repoRoot 'artifacts\portable'
if ($OutputPath -ieq $artifactRoot -or -not (Test-Within $OutputPath $artifactRoot)) { throw 'OSL012:OUTPUT_SCOPE OutputPath must be below artifacts/portable.' }
$inputRoots = @($PayloadPath, [IO.Path]::GetDirectoryName($DotnetPath), [IO.Path]::GetDirectoryName($SbomToolPath))
Assert-NoOverlap $OutputPath $inputRoots
if (Test-Path -LiteralPath $OutputPath) { throw 'OSL012:OUTPUT_EXISTS OutputPath will not be overwritten.' }
$privateEvidencePath = $OutputPath + '.private'
if (Test-Path -LiteralPath $privateEvidencePath) { throw 'OSL012:PRIVATE_EXISTS Private evidence path will not be overwritten.' }
Assert-SafePath $OutputPath -AllowMissing
Assert-SafePath $privateEvidencePath -AllowMissing
Assert-SafePath $DotnetPath -File
Assert-SafePath $SbomToolPath -File
Assert-SafePath $PayloadPath

$payloadRoots = @('Worker', 'Tools', 'ModelCatalog', 'Fonts', 'WebView2Runtime', 'Licenses', 'Evidence')
$manifestName = 'Evidence/portable-payload.json'
$manifestPath = Join-Path $PayloadPath $manifestName
$manifestEvidence = Get-FileEvidence $manifestPath
try { $manifest = Read-Json $manifestPath }
catch { throw 'OSL012:MANIFEST Prepared evidence must be valid JSON.' }
if ($manifest -isnot [Collections.IDictionary] -or $manifest['schemaVersion'] -cne '1.0.0' -or
    $manifest['taskId'] -cne 'OSL-009' -or $manifest['architecture'] -cne 'x64' -or
    $manifest['releaseApproved'] -isnot [bool] -or $manifest['releaseApproved'] -ne $false -or
    $manifest['appPublishRequired'] -isnot [bool] -or $manifest['appPublishRequired'] -ne $true -or
    $manifest['rootDirectories'] -isnot [array] -or ($manifest['rootDirectories'] -join '/') -cne ($payloadRoots -join '/') -or
    $manifest['files'] -isnot [array] -or $manifest['files'].Count -eq 0 -or
    ($manifest['fileCount'] -isnot [long] -and $manifest['fileCount'] -isnot [int]) -or
    ($manifest['totalBytes'] -isnot [long] -and $manifest['totalBytes'] -isnot [int]) -or
    $manifest['artifactSetSha256'] -isnot [string] -or $manifest['artifactSetSha256'] -cnotmatch '\A[0-9a-f]{64}\z') {
    throw 'OSL012:MANIFEST Expected the OSL-009 schema 1.0.0 x64, unapproved, publish-required payload.'
}
$declared = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$previous = $null
foreach ($record in $manifest['files']) {
    if ($record -isnot [Collections.IDictionary] -or $record['path'] -isnot [string] -or
        ($record['byteSize'] -isnot [long] -and $record['byteSize'] -isnot [int]) -or $record['byteSize'] -lt 0 -or
        $record['sha256'] -isnot [string] -or $record['sha256'] -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'OSL012:MANIFEST_FILES Invalid path/size/hash record.'
    }
    try { $relative = Get-RelativeName $record['path'] }
    catch { throw 'OSL012:MANIFEST_FILES Invalid relative path.' }
    if ($relative -cne $record['path'] -or $relative -ieq $manifestName -or $relative -ieq 'Evidence/application-build.json' -or $relative.Split('/')[0] -cnotin $payloadRoots -or
        -not $names.Add($relative) -or ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $relative) -ge 0)) {
        throw 'OSL012:MANIFEST_FILES Paths must be unique, canonical and strictly Ordinal, excluding the manifest itself.'
    }
    $declared.Add($relative, [pscustomobject]@{ path = $relative; byteSize = [long]$record['byteSize']; sha256 = $record['sha256'] })
    $previous = $relative
}
$roots = @(Get-ChildItem -LiteralPath $PayloadPath -Force)
if ($roots.Count -ne $payloadRoots.Count -or @($roots | Where-Object { $_ -isnot [IO.DirectoryInfo] -or $_.Name -cnotin $payloadRoots }).Count -ne 0) {
    throw 'OSL012:PAYLOAD_FILES Unexpected prepared root layout.'
}
$payloadRecords = Get-TreeRecords $PayloadPath
Assert-FileEvidence $manifestPath $manifestEvidence
[void]$payloadRecords.Remove($manifestName)
$payloadDigest = Get-ArtifactSetSha256 $payloadRecords
if ($payloadRecords.Count -ne $declared.Count -or $manifest['fileCount'] -ne $declared.Count -or
    $manifest['totalBytes'] -ne [long](($payloadRecords.Values | Measure-Object byteSize -Sum).Sum) -or
    $manifest['artifactSetSha256'] -cne $payloadDigest -or (Get-ArtifactSetSha256 $declared) -cne $payloadDigest) {
    throw 'OSL012:PAYLOAD_FILES Prepared paths, sizes or hashes differ; missing and untracked extra files are forbidden.'
}
$payloadRecords.Add($manifestName, [pscustomobject]@{ path = $manifestName; byteSize = $manifestEvidence.byteSize; sha256 = $manifestEvidence.sha256 })
$preparedSetDigest = Get-ArtifactSetSha256 $payloadRecords
$evidenceSources = [ordered]@{
    'Evidence/python-runtime-manifest.json' = 'packaging/python/runtime-manifest.json'
    'Evidence/ffmpeg-manifest.json' = 'packaging/ffmpeg/manifest.json'
    'Evidence/model-catalog.json' = 'packaging/models/catalog.json'
    'Evidence/webview2-runtime-manifest.json' = 'packaging/webview2/runtime-manifest.json'
    'Evidence/web-dependency-manifest.json' = 'packaging/web/dependency-manifest.json'
    'Evidence/font-manifest.json' = 'packaging/fonts/font-manifest.json'
    'Fonts/font-manifest.json' = 'packaging/fonts/font-manifest.json'
    'ModelCatalog/catalog.json' = 'packaging/models/catalog.json'
}
$originalEvidence = @{}
foreach ($name in $evidenceSources.Keys) {
    $original = Get-FileEvidence (Join-Path $repoRoot $evidenceSources[$name])
    if (-not $payloadRecords.ContainsKey($name) -or $payloadRecords[$name].byteSize -ne $original.byteSize -or
        $payloadRecords[$name].sha256 -cne $original.sha256) { throw 'OSL012:EVIDENCE_ORIGINAL Prepared evidence/catalog differs from the repository original.' }
    $originalEvidence[$name] = $original
}

# Data-only gate. Source metadata is embedded by this build; an old Worker or a
# rehashed arbitrary font/payload cannot supply its own independent trust anchor.
& (Join-Path $PSScriptRoot 'verify-runtime-payload.ps1') -PayloadPath $PayloadPath

# Exactly nine curated descriptor files; no recursive component-source copy.
$componentInputs = [ordered]@{}
$sourceRoots = @($PayloadPath, [IO.Path]::GetDirectoryName($DotnetPath), (Join-Path $repoRoot 'src'))
foreach ($project in @('App', 'Core', 'Infrastructure')) {
    $prefix = 'src/HubRadio.' + $project
    $componentInputs["$prefix/HubRadio.$project.csproj"] = "$prefix/HubRadio.$project.csproj"
    $componentInputs["$prefix/obj/project.assets.json"] = "$prefix/obj/project.assets.json"
    foreach ($directory in @('bin', 'obj')) { Assert-NoOverlap (Join-Path $repoRoot "$prefix/$directory") $inputRoots }
    $assets = Read-Json (Join-Path $repoRoot "$prefix/obj/project.assets.json")
    foreach ($folder in $assets['packageFolders'].Keys) { $sourceRoots += Get-LocalPath $folder 'PackageFolder' }
}
$componentInputs['src/HubRadio.Web/package.json'] = 'src/HubRadio.Web/package.json'
$componentInputs['src/HubRadio.Web/package-lock.json'] = 'src/HubRadio.Web/package-lock.json'
$componentInputs['src/HubRadio.Worker/requirements.txt'] = 'src/HubRadio.Worker/requirements.lock'
$componentEvidence = @{}
foreach ($name in $componentInputs.Keys) { $componentEvidence[$name] = Get-FileEvidence (Join-Path $repoRoot $componentInputs[$name]) }
Assert-NoOverlap $OutputPath ($inputRoots + $sourceRoots)

$expectedContent = [Collections.Generic.SortedDictionary[string, object]]::new($payloadRecords, [StringComparer]::Ordinal)
$appContentTrees = [ordered]@{
    WebAssets = 'src/HubRadio.Web/dist'; Profiles = 'src/HubRadio.App/Profiles'; DefaultSettings = 'src/HubRadio.App/DefaultSettings'
}
foreach ($root in $appContentTrees.Keys) {
    $files = Get-TreeRecords (Join-Path $repoRoot $appContentTrees[$root])
    if ($files.Count -eq 0) { throw 'OSL012:PREREQUISITE Web assets and App profiles must exist; run build.ps1 first.' }
    foreach ($file in $files.Values) {
        if (($root -eq 'WebAssets' -and $file.path.EndsWith('.partial')) -or
            ($root -ne 'WebAssets' -and [IO.Path]::GetExtension($file.path) -ine '.json')) { continue }
        $expectedContent.Add($root + '/' + $file.path, $file)
    }
}
$sbomToolEvidence = [pscustomobject]@{ byteSize = 81033848L; sha256 = '625767b371b7fdd58f40f618b8a86da0247a33c89e419039c86b4edba1dad4b5' }
Assert-FileEvidence $SbomToolPath $sbomToolEvidence
$gitPath = Get-LocalPath (Get-Command git.exe -CommandType Application -ErrorAction Stop).Source 'GitPath'
Assert-SafePath $gitPath -File

$stageRoot = $OutputPath + '.' + [guid]::NewGuid().ToString('N') + '.partial'
Assert-NoOverlap $stageRoot ($inputRoots + $sourceRoots + @([IO.Path]::GetDirectoryName($gitPath)))
Assert-NoOverlap $privateEvidencePath ($inputRoots + $sourceRoots + @([IO.Path]::GetDirectoryName($gitPath)))
Assert-SafePath $stageRoot -AllowMissing
$parent = [IO.Path]::GetDirectoryName($OutputPath)
[void][IO.Directory]::CreateDirectory($parent)
Assert-SafePath $parent
$stageOwned = $false
try {
    # No -Force: do not reuse an existing directory. GUID siblings plus CreateNew
    # file writes suffice for this non-concurrent, bounded developer workflow.
    New-Item -ItemType Directory -Path $stageRoot -ErrorAction Stop | Out-Null
    $stageOwned = $true
    foreach ($directory in @('App', 'Contents', 'Evidence', 'Sbom', 'ComponentInputs', 'BuildLogs/tmp')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $stageRoot $directory))
    }
    Assert-SafePath $stageRoot
    $appRoot = Join-Path $stageRoot 'App'
    $contentsRoot = Join-Path $stageRoot 'Contents'
    $logRoot = Join-Path $stageRoot 'BuildLogs'
    $sbomRoot = Join-Path $stageRoot 'Sbom'
    $componentRoot = Join-Path $stageRoot 'ComponentInputs'
    $bundleManifestPath = Join-Path $logRoot 'files-to-bundle.tsv'
    $privateValidationPath = Join-Path $logRoot 'sbom-validation.private.json'
    $validationPath = Join-Path $stageRoot 'Evidence/sbom-validation.json'
    $appProjectPath = Join-Path $repoRoot 'src/HubRadio.App/HubRadio.App.csproj'
    $sdkVersion = (Invoke-BuildTool $DotnetPath @('--version') 'dotnet-version').Trim()
    if ($sdkVersion -cne '10.0.401') { throw 'OSL012:SDK_VERSION Expected the fixed .NET SDK 10.0.401.' }
    $toolVersion = (Invoke-BuildTool $SbomToolPath @('--version') 'sbom-version').Trim()
    if ($toolVersion -cnotmatch '(?<![0-9.])4\.1\.5(?:\.0)?(?:\+[0-9A-Za-z.-]+)?(?![0-9.])') { throw 'OSL012:SBOM_VERSION Expected official sbom-tool 4.1.5.' }
    $commitArguments = @('-C', $repoRoot, 'rev-parse', '--verify', 'HEAD')
    $statusArguments = @('-C', $repoRoot, 'status', '--porcelain=v1', '--untracked-files=all')
    $sourceCommit = (Invoke-BuildTool $gitPath $commitArguments 'git-head-before').Trim()
    if ($sourceCommit -cnotmatch '\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z') { throw 'OSL012:GIT A valid source commit is required.' }
    $statusBefore = Invoke-BuildTool $gitPath $statusArguments 'git-status-before'
    foreach ($name in $componentInputs.Keys) {
        Copy-VerifiedFile (Join-Path $repoRoot $componentInputs[$name]) (Join-Path $componentRoot $name) $componentEvidence[$name]
    }
    $publishArguments = @(
        'publish', $appProjectPath, '-c', 'Release', '-r', 'win-x64', '--no-restore',
        '-p:Platform=x64', '-p:PublishProfile=win-x64-singlefile',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:HubRadioPortablePayloadPath=$PayloadPath", "-p:HubRadioPortableBundleManifestPath=$bundleManifestPath",
        "-p:Version=$Version", '--output', $appRoot
    )
    Invoke-BuildTool $DotnetPath $publishArguments 'publish' | Out-Null

    $reportEvidence = Get-FileEvidence $bundleManifestPath
    $bundleSources = Read-BundleSources $bundleManifestPath
    $bundleContract = Get-BundleContract $bundleSources $sdkVersion
    $contents = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($relative in $bundleSources.Keys) {
        $source = $bundleSources[$relative]
        $evidence = Get-FileEvidence $source
        Copy-VerifiedFile $source (Join-Path $contentsRoot $relative) $evidence
        $contents.Add($relative, [pscustomobject][ordered]@{ path = $relative; byteSize = $evidence.byteSize; sha256 = $evidence.sha256 })
    }
    foreach ($name in $expectedContent.Keys) {
        if (-not $contents.ContainsKey($name) -or $contents[$name].byteSize -ne $expectedContent[$name].byteSize -or
            $contents[$name].sha256 -cne $expectedContent[$name].sha256) { throw 'OSL012:BUNDLE_CONTENT Prepared payload, Web assets or profiles are missing/changed in actual FilesToBundle.' }
    }
    Assert-ApplicationBuildEvidence $contentsRoot
    # XAML resources may be PRI-embedded; loose App.xbf/MainWindow.xbf are NOT required.
    $contentsDigest = Get-ArtifactSetSha256 $contents
    $contentsBytes = [long](($contents.Values | Measure-Object byteSize -Sum).Sum)
    $hostBytes = $contents['HubRadio.App.exe'].byteSize
    Assert-Candidate $appRoot $contentsBytes $hostBytes $bundleContract
    $candidate = Get-FileEvidence (Join-Path $appRoot 'HubRadio.App.exe')

    Assert-FileEvidence $SbomToolPath $sbomToolEvidence
    $generateArguments = @(
        'generate', '-mi', 'SPDX:2.2', '-b', $contentsRoot, '-m', $sbomRoot,
        '-pn', 'HubRadio.MovieCreator', '-pv', $Version, '-ps', 'HubRadio-MovieCreator',
        '-nsb', 'https://github.com/dahatake/HubRadio-MovieCreator', '-li', 'false', '-F', 'false', '-bc', $componentRoot
    )
    Invoke-BuildTool $SbomToolPath $generateArguments 'sbom-generate' | Out-Null
    $sbomRelativePath = 'Sbom/_manifest/spdx_2.2/manifest.spdx.json'
    $sbomPath = Join-Path $stageRoot $sbomRelativePath
    $sbom = Read-Json $sbomPath
    Assert-PublicMetadata $sbom
    if ($sbom['spdxVersion'] -cne 'SPDX-2.2' -or $sbom['files'] -isnot [array] -or
        $sbom['files'].Count -eq 0 -or $sbom['files'].Count -ne $contents.Count) { throw 'OSL012:SBOM_FILES Expected SPDX-2.2 with the complete nonempty Contents file list.' }
    $sbomNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $sbom['files']) {
        $name = [string]$file['fileName']
        if ($name.StartsWith('./', [StringComparison]::Ordinal)) { $name = $name.Substring(2) }
        $name = Get-RelativeName $name
        $hashes = @($file['checksums'] | Where-Object { $_['algorithm'] -ceq 'SHA256' })
        if (-not $sbomNames.Add($name) -or -not $contents.ContainsKey($name) -or $hashes.Count -ne 1 -or
            $hashes[0]['checksumValue'] -ine $contents[$name].sha256) { throw 'OSL012:SBOM_FILES SBOM paths/SHA-256 differ from Contents.' }
    }
    $validateArguments = @(
        'validate', '-b', $contentsRoot, '-m', (Join-Path $sbomRoot '_manifest'),
        '-o', $privateValidationPath, '-mi', 'SPDX:2.2'
    )
    Invoke-BuildTool $SbomToolPath $validateArguments 'sbom-validate' | Out-Null
    $validation = Get-PublicSbomValidation $privateValidationPath
    Assert-PublicMetadata $validation
    $validationStream = [IO.File]::Open($validationPath, [IO.FileMode]::CreateNew)
    try {
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($validation | ConvertTo-Json -Depth 2) + "`n")
        $validationStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $validationStream.Dispose() }

    Assert-FileEvidence $SbomToolPath $sbomToolEvidence
    Assert-FileEvidence $bundleManifestPath $reportEvidence
    foreach ($name in $bundleSources.Keys) { Assert-FileEvidence $bundleSources[$name] $contents[$name] }
    foreach ($name in $evidenceSources.Keys) { Assert-FileEvidence (Join-Path $repoRoot $evidenceSources[$name]) $originalEvidence[$name] }
    foreach ($name in $componentInputs.Keys) { Assert-FileEvidence (Join-Path $repoRoot $componentInputs[$name]) $componentEvidence[$name] }
    if ((Get-ArtifactSetSha256 (Get-TreeRecords $PayloadPath)) -cne $preparedSetDigest -or
        (Get-ArtifactSetSha256 (Get-TreeRecords $contentsRoot)) -cne $contentsDigest) { throw 'OSL012:TREE_CHANGED An input or audit tree changed during the build.' }
    Assert-Candidate $appRoot $contentsBytes $hostBytes $bundleContract
    Assert-FileEvidence (Join-Path $appRoot 'HubRadio.App.exe') $candidate
    $statusAfter = Invoke-BuildTool $gitPath $statusArguments 'git-status-after'
    if ((Invoke-BuildTool $gitPath $commitArguments 'git-head-after').Trim() -cne $sourceCommit) { throw 'OSL012:SOURCE_CHANGED HEAD changed during the build.' }
    $sbomEvidence = Get-FileEvidence $sbomPath
    $release = [ordered]@{
        schemaVersion = '1.0.0'; taskId = 'OSL-012'; version = $Version; architecture = 'x64'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O'); releaseApproved = $false
        scope = 'bounded-local-candidate'; reproducibleBuildClaim = $false
        sourceCommit = $sourceCommit
        sourceTreeDirty = (-not [string]::IsNullOrWhiteSpace($statusBefore) -or -not [string]::IsNullOrWhiteSpace($statusAfter))
        sourceProvenance = 'HEAD is not a WIP snapshot; actual compiled/generated inputs are measured in Contents. Git status is private build evidence.'
        candidate = [ordered]@{ path = 'App/HubRadio.App.exe'; byteSize = $candidate.byteSize; sha256 = $candidate.sha256 }
        preparedPayload = [ordered]@{ manifestPath = 'Contents/Evidence/portable-payload.json'; manifestSha256 = $manifestEvidence.sha256; artifactSetSha256 = $payloadDigest }
        applicationBuild = [ordered]@{
            path = 'Contents/Evidence/application-build.json'
            sha256 = (Get-FileEvidence (Join-Path $contentsRoot 'Evidence/application-build.json')).sha256
            purpose = 'build-identity-not-authenticity-or-release-approval'
        }
        contents = [ordered]@{ path = 'Contents'; purpose = 'audit-only-not-for-folder-distribution'; fileCount = $contents.Count; totalBytes = $contentsBytes; artifactSetSha256 = $contentsDigest; files = @($contents.Values) }
        sbom = [ordered]@{
            path = $sbomRelativePath; byteSize = $sbomEvidence.byteSize; sha256 = $sbomEvidence.sha256; format = 'SPDX-2.2'
            validationPath = 'Evidence/sbom-validation.json'; validationExitCode = 0
            validationSha256 = (Get-FileEvidence $validationPath).sha256
            scope = 'Contents file hashes plus curated dependency detection; not full license/CVE approval'
            componentInputCount = $componentInputs.Count; licenseFetching = $false; networkIsolationClaim = $false
            tool = [ordered]@{ version = '4.1.5'; byteSize = $sbomToolEvidence.byteSize; sha256 = $sbomToolEvidence.sha256; source = 'https://github.com/microsoft/sbom-tool/releases/download/v4.1.5/sbom-tool-win-x64.exe' }
        }
        remainingReviewItems = @(
            'OSL-013 independent bundle/release verification and clean-machine runtime acceptance',
            'Python/model/native component attribution may be NOASSERTION; reconcile dependencies and notices',
            'License and redistribution review (including FFmpeg, models and WebView2) is not approved',
            'CVE/security review, signing and distribution approval remain outstanding'
        )
    }
    Assert-PublicMetadata $release
    $releasePath = Join-Path $stageRoot 'Evidence/portable-release.json'
    $releaseStream = [IO.File]::Open($releasePath, [IO.FileMode]::CreateNew)
    try {
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($release | ConvertTo-Json -Depth 12) + "`n")
        $releaseStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $releaseStream.Dispose() }
    $releaseEvidence = Get-FileEvidence $releasePath
    Assert-SafePath $OutputPath -AllowMissing
    if (Test-Path -LiteralPath $OutputPath) { throw 'OSL012:OUTPUT_EXISTS OutputPath appeared during the build; it will not be overwritten.' }
    Move-PrivateEvidence $stageRoot $OutputPath | Out-Null
    Get-SafeFiles $stageRoot | Out-Null
    [IO.Directory]::Move($stageRoot, $OutputPath)
    $stageOwned = $false
}
catch {
    if ($stageOwned) {
        $retained = @()
        if (Test-Path -LiteralPath $stageRoot) { $retained += [IO.Path]::GetRelativePath($repoRoot, $stageRoot) }
        if (Test-Path -LiteralPath $privateEvidencePath) { $retained += [IO.Path]::GetRelativePath($repoRoot, $privateEvidencePath) }
        Write-Warning ('OSL012:PARTIAL Retained private build artifacts: ' + ($retained -join ', '))
    }
    throw
}

[pscustomobject][ordered]@{
    output = [IO.Path]::GetRelativePath($repoRoot, $OutputPath).Replace('\', '/')
    privateEvidencePath = [IO.Path]::GetRelativePath($repoRoot, $privateEvidencePath).Replace('\', '/')
    candidatePath = 'App/HubRadio.App.exe'; candidateSha256 = $candidate.sha256; candidateByteSize = $candidate.byteSize
    fileCount = $contents.Count; totalBytes = $contentsBytes; artifactSetSha256 = $contentsDigest
    releaseManifestPath = 'Evidence/portable-release.json'; releaseManifestSha256 = $releaseEvidence.sha256
    sbomPath = $sbomRelativePath; sbomSha256 = $sbomEvidence.sha256; sbomValidationPath = 'Evidence/sbom-validation.json'
    releaseApproved = $false
} | ConvertTo-Json -Compress