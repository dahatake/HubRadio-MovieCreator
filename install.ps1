#Requires -Version 7.0

<#
.SYNOPSIS
「編集を開始」に必要な実行時リソースを取得・検証し、ビルド済みアプリの実行フォルダーへ配置します。

.DESCRIPTION
ペイロードを指定しない通常のソースビルド（scripts/build.ps1・run.ps1）の出力には、
Tools\ffmpeg.exe などの実行時リソースが含まれません。このため「編集を開始」で
「動画処理ツール（Tools\ffmpeg.exe）が見つからないか、検証に失敗しました。」が表示されます。

このスクリプトは、リポジトリの固定契約（manifest）に記録された版だけを取得し、
byte size と SHA-256 を照合してから、アプリの実行フォルダーへ次を配置します。

  Tools\ffmpeg.exe, Tools\ffprobe.exe        packaging/ffmpeg/manifest.json
  Worker\ (CPython 3.12 + 固定 wheel)         packaging/python/runtime-manifest.json (build-runtime.ps1 で構築)
  ModelCatalog\Bundles\faster-whisper-small  packaging/models/catalog.json
  Fonts\ (Noto Sans JP)                       packaging/fonts/font-manifest.json

取得物は artifacts\inputs\install にキャッシュされ、再実行時は照合済みのものを再利用します。
ハッシュ不一致のファイルは使用せず、処理を中止します。

これは開発ビルドをローカルで動かすための手順です。配布・再配布の承認を意味しません。

.PARAMETER Configuration
配置先とするビルド構成。既定は Release（run.ps1 と同じ出力）。

.PARAMETER AppDirectory
HubRadio.App.exe があるフォルダーを直接指定します。省略時は Configuration から決まります。

.PARAMETER SkipBuild
アプリ出力が無い場合でも scripts/build.ps1 を実行しません。

.PARAMETER Force
配置済みリソースが検証に合格していても、取り直して配置し直します。

.EXAMPLE
pwsh -NoLogo -NoProfile -File .\install.ps1

.EXAMPLE
pwsh -NoLogo -NoProfile -File .\install.ps1 -Configuration Debug
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Force', Justification = 'Read by the install functions.')]
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$AppDirectory,

    [switch]$SkipBuild,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) が必要です。pwsh.exe で実行してください。'
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Windows x64 が必要です。'
}

$repoRoot = $PSScriptRoot
$cacheRoot = Join-Path $repoRoot 'artifacts\inputs\install'
$ffmpegManifestPath = Join-Path $repoRoot 'packaging\ffmpeg\manifest.json'
$pythonManifestPath = Join-Path $repoRoot 'packaging\python\runtime-manifest.json'
$modelCatalogPath = Join-Path $repoRoot 'packaging\models\catalog.json'
$fontManifestPath = Join-Path $repoRoot 'packaging\fonts\font-manifest.json'
$wheelMatrixPath = Join-Path $repoRoot 'src\HubRadio.Worker\wheel-matrix.json'
$requirementsPath = Join-Path $repoRoot 'src\HubRadio.Worker\requirements.lock'
$buildRuntimeScript = Join-Path $repoRoot 'packaging\python\build-runtime.ps1'

function Write-Step([string]$Message) {
    Write-Information "==> $Message" -InformationAction Continue
}

function Read-Json([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-FileEvidence([string]$Path, [long]$ByteSize, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path -Force).Length -ne $ByteSize) { return $false }
    return (Get-Sha256 $Path) -ceq $Sha256
}

function Assert-FileEvidence([string]$Path, [long]$ByteSize, [string]$Sha256, [string]$Name) {
    if (-not (Test-FileEvidence $Path $ByteSize $Sha256)) {
        throw "$Name の byte size または SHA-256 が固定値と一致しません: $Path"
    }
}

function Get-ArtifactSetSha256([object[]]$Records) {
    # Same digest as build-runtime.ps1 (en-US order, path NUL size NUL sha LF).
    $builder = [Text.StringBuilder]::new()
    foreach ($record in @($Records | Sort-Object -Property path -CaseSensitive -Culture en-US)) {
        [void]$builder.Append([string]$record.path).Append([char]0)
        [void]$builder.Append(([long]$record.byteSize).ToString([Globalization.CultureInfo]::InvariantCulture)).Append([char]0)
        [void]$builder.Append([string]$record.sha256).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
}

function Get-TreeRecord([string]$Root) {
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            byteSize = $_.Length
            sha256 = Get-Sha256 $_.FullName
        }
    })
}

function Remove-Tree {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path) -or -not $PSCmdlet.ShouldProcess($Path, 'Remove')) { return }
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File | ForEach-Object {
        [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal)
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Copy-FileTo([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    if (Test-Path -LiteralPath $Destination) {
        [IO.File]::SetAttributes($Destination, [IO.FileAttributes]::Normal)
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    [IO.File]::SetAttributes($Destination, [IO.FileAttributes]::Normal)
}

function Save-VerifiedFile([string]$Uri, [string]$Path, [long]$ByteSize, [string]$Sha256, [string]$Name) {
    if (Test-FileEvidence $Path $ByteSize $Sha256) {
        Write-Step "$Name はキャッシュ済みです（照合 OK）"
        return
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $partial = "$Path.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    Write-Step ("$Name をダウンロードしています（{0:N1} MB）: $Uri" -f ($ByteSize / 1MB))
    $curl = Get-Command -Name 'curl.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $curl) {
        & $curl.Source --fail --location --silent --show-error --retry 3 --output $partial $Uri
        if ($LASTEXITCODE -ne 0) { throw "$Name のダウンロードに失敗しました（curl exit $LASTEXITCODE）。" }
    }
    else {
        Invoke-WebRequest -Uri $Uri -OutFile $partial -UseBasicParsing
    }
    if (-not (Test-FileEvidence $partial $ByteSize $Sha256)) {
        Remove-Item -LiteralPath $partial -Force
        throw "$Name の byte size または SHA-256 が固定値と一致しません。別の版や破損したファイルは使用しません。"
    }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    Move-Item -LiteralPath $partial -Destination $Path
}

function Resolve-AppDirectory {
    if (-not [string]::IsNullOrWhiteSpace($AppDirectory)) {
        return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($AppDirectory))
    }
    return Join-Path $repoRoot "src\HubRadio.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
}

function Stop-RunningApp {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Directory)
    $exe = Join-Path $Directory 'HubRadio.App.exe'
    $running = @(Get-Process -Name 'HubRadio.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $running) {
        Write-Step "実行中の HubRadio.App（PID $($process.Id)）を終了します（ファイルを置き換えるため）"
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(30000) | Out-Null
    }
}

function Resolve-BuildPython {
    $candidates = [Collections.Generic.List[string]]::new()
    $launcher = Get-Command -Name 'py.exe'     -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 # PATH に py.exe が複数あっても先頭 1 件だけ使う
    if ($null -ne $launcher) {
        $resolved = & $launcher.Source -3.12 -c 'import sys; print(sys.executable)' 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolved)) { $candidates.Add($resolved.Trim()) }
    }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'))
    $candidates.Add((Join-Path $repoRoot 'src\HubRadio.Worker\.venv\Scripts\python.exe'))
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $info = & $candidate -c 'import json,struct,sys; print(json.dumps({"i":sys.implementation.name,"v":list(sys.version_info[:2]),"b":struct.calcsize("P")*8}))' 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        $parsed = $info | ConvertFrom-Json
        if ($parsed.i -ceq 'cpython' -and $parsed.v[0] -eq 3 -and $parsed.v[1] -eq 12 -and $parsed.b -eq 64) {
            & $candidate -m pip --version *> $null
            if ($LASTEXITCODE -eq 0) { return [IO.Path]::GetFullPath($candidate) }
        }
    }
    throw 'Windows x64 版 CPython 3.12（pip 付き）が見つかりません。python.org から 3.12 をインストールしてから再実行してください。'
}

function Install-ToolSet([string]$Target) {
    $manifest = Read-Json $ffmpegManifestPath
    $selected = @{}
    foreach ($file in $manifest.selectedFiles) { $selected[[string]$file.role] = $file }
    $toolsDir = Join-Path $Target 'Tools'
    $ffmpegTarget = Join-Path $toolsDir 'ffmpeg.exe'
    $ffprobeTarget = Join-Path $toolsDir 'ffprobe.exe'
    if (-not $Force -and
        (Test-FileEvidence $ffmpegTarget $selected['ffmpeg'].byteSize $selected['ffmpeg'].sha256) -and
        (Test-FileEvidence $ffprobeTarget $selected['ffprobe'].byteSize $selected['ffprobe'].sha256)) {
        Write-Step 'Tools（FFmpeg）は配置済みです（照合 OK）'
        return
    }

    $extractDir = Join-Path $cacheRoot 'ffmpeg'
    $allCached = $true
    foreach ($role in @('ffmpeg', 'ffprobe', 'license')) {
        $name = [IO.Path]::GetFileName([string]$selected[$role].pathSuffixInArchive)
        if (-not (Test-FileEvidence (Join-Path $extractDir $name) $selected[$role].byteSize $selected[$role].sha256)) { $allCached = $false }
    }
    if (-not $allCached) {
        $archive = $manifest.package.archive
        $archivePath = Join-Path $cacheRoot ([string]$archive.fileName)
        Save-VerifiedFile $archive.downloadUrl $archivePath $archive.expectedByteSize $archive.expectedSha256 'FFmpeg アーカイブ'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Directory]::CreateDirectory($extractDir) | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($role in @('ffmpeg', 'ffprobe', 'license')) {
                $suffix = '/' + [string]$selected[$role].pathSuffixInArchive
                $entries = @($zip.Entries | Where-Object { $_.FullName.EndsWith($suffix, [StringComparison]::Ordinal) })
                if ($entries.Count -ne 1) { throw "FFmpeg アーカイブに $suffix がちょうど1つ含まれていません。" }
                $destination = Join-Path $extractDir ([IO.Path]::GetFileName($suffix))
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination, $true)
            }
        }
        finally { $zip.Dispose() }
    }
    foreach ($role in @('ffmpeg', 'ffprobe', 'license')) {
        $name = [IO.Path]::GetFileName([string]$selected[$role].pathSuffixInArchive)
        Assert-FileEvidence (Join-Path $extractDir $name) $selected[$role].byteSize $selected[$role].sha256 $name
    }

    Write-Step "Tools を配置しています: $toolsDir"
    Remove-Tree $toolsDir
    Copy-FileTo (Join-Path $extractDir 'ffmpeg.exe') $ffmpegTarget
    Copy-FileTo (Join-Path $extractDir 'ffprobe.exe') $ffprobeTarget
    Copy-FileTo (Join-Path $extractDir 'LICENSE.txt') (Join-Path $Target 'Licenses\FFmpeg-LICENSE.txt')
    Assert-FileEvidence $ffmpegTarget $selected['ffmpeg'].byteSize $selected['ffmpeg'].sha256 'Tools\ffmpeg.exe'
    Assert-FileEvidence $ffprobeTarget $selected['ffprobe'].byteSize $selected['ffprobe'].sha256 'Tools\ffprobe.exe'
}

function Install-FontSet([string]$Target) {
    $font = Read-Json $fontManifestPath
    $fontsDir = Join-Path $Target 'Fonts'
    $fontTarget = Join-Path $fontsDir ([string]$font.relativePath)
    $licenseTarget = Join-Path $fontsDir ([string]$font.license.relativePath)
    $manifestTarget = Join-Path $fontsDir 'font-manifest.json'
    $manifestEvidence = Get-Item -LiteralPath $fontManifestPath
    $manifestSha = Get-Sha256 $fontManifestPath
    if (-not $Force -and
        (Test-FileEvidence $fontTarget $font.byteSize $font.sha256) -and
        (Test-FileEvidence $licenseTarget $font.license.byteSize $font.license.sha256) -and
        (Test-FileEvidence $manifestTarget $manifestEvidence.Length $manifestSha) -and
        @(Get-ChildItem -LiteralPath $fontsDir -Force -Recurse).Count -eq 3) {
        Write-Step 'Fonts は配置済みです（照合 OK）'
        return
    }

    $base = "https://raw.githubusercontent.com/notofonts/noto-cjk/$($font.sourceCommit)"
    $fontCache = Join-Path $cacheRoot ('fonts\' + [string]$font.relativePath)
    $licenseCache = Join-Path $cacheRoot 'fonts\LICENSE'
    Save-VerifiedFile "$base/$($font.sourcePath)" $fontCache $font.byteSize $font.sha256 'Noto Sans JP フォント'
    Save-VerifiedFile "$base/$($font.license.sourcePath)" $licenseCache $font.license.byteSize $font.license.sha256 'Noto Sans JP ライセンス'

    Write-Step "Fonts を配置しています: $fontsDir"
    Remove-Tree $fontsDir
    Copy-FileTo $fontCache $fontTarget
    Copy-FileTo $licenseCache $licenseTarget
    Copy-FileTo $fontManifestPath $manifestTarget
    Copy-FileTo $licenseCache (Join-Path $Target 'Licenses\NotoSansJP-OFL.txt')
}

function Install-Model([string]$Target) {
    $catalog = Read-Json $modelCatalogPath
    $models = @($catalog.models | Where-Object { $_.runtimeEligible -eq $true })
    if ($models.Count -ne 1) { throw 'catalog.json の実行対象モデルがちょうど1件ではありません。' }
    $model = $models[0]
    if ([string]$model.identity -notmatch '\A[\w.-]+/[\w.-]+\z' -or
        [string]$model.version -notmatch '\Arevision ([0-9a-f]{40})\z') {
        throw 'catalog.json のモデル識別子またはリビジョンが想定形式ではありません。'
    }
    $revision = $Matches[1]

    $catalogTarget = Join-Path $Target 'ModelCatalog\catalog.json'
    Copy-FileTo $modelCatalogPath $catalogTarget

    $bundleDir = Join-Path $Target ('ModelCatalog\' + ([string]$model.bundle.relativePath).Replace('/', '\'))
    $installed = -not $Force -and (Test-Path -LiteralPath $bundleDir -PathType Container) -and
        @(Get-ChildItem -LiteralPath $bundleDir -Force -Recurse).Count -eq @($model.bundle.files).Count
    if ($installed) {
        foreach ($file in $model.bundle.files) {
            if (-not (Test-FileEvidence (Join-Path $bundleDir $file.name) $file.byteSize $file.sha256)) { $installed = $false; break }
        }
    }
    if ($installed) {
        Write-Step '文字起こしモデルは配置済みです（照合 OK）'
        return
    }

    $cacheDir = Join-Path $cacheRoot ('models\' + [IO.Path]::GetFileName($bundleDir))
    foreach ($file in $model.bundle.files) {
        $uri = "https://huggingface.co/$($model.identity)/resolve/$revision/$($file.name)"
        Save-VerifiedFile $uri (Join-Path $cacheDir $file.name) $file.byteSize $file.sha256 "モデル $($file.name)"
    }

    Write-Step "文字起こしモデルを配置しています: $bundleDir"
    Remove-Tree $bundleDir
    foreach ($file in $model.bundle.files) {
        Copy-FileTo (Join-Path $cacheDir $file.name) (Join-Path $bundleDir $file.name)
    }
}

function Test-WorkerTree([string]$Directory, [object]$Output) {
    $runtimeFiles = Join-Path $Directory ([string]$Output.fileManifestName)
    if (-not (Test-Path -LiteralPath $runtimeFiles -PathType Leaf)) { return $false }
    $records = @(Get-TreeRecord $Directory | Where-Object { $_.path -cne [string]$Output.fileManifestName })
    return $records.Count -eq [int]$Output.expectedFileCount -and
        (Get-ArtifactSetSha256 $records) -ceq [string]$Output.expectedArtifactSetSha256
}

function Install-Worker([string]$Target) {
    $manifest = Read-Json $pythonManifestPath
    $output = $manifest.output
    $workerDir = Join-Path $Target 'Worker'
    if (-not $Force -and (Test-WorkerTree $workerDir $output)) {
        Write-Step 'Worker（Python 実行環境）は配置済みです（照合 OK）'
        return
    }

    $builtDir = Join-Path $repoRoot ('artifacts\python\HubRadio.WorkerRuntime-' + ([string]$output.expectedArtifactSetSha256).Substring(0, 16))
    if ((Test-Path -LiteralPath $builtDir) -and -not (Test-WorkerTree $builtDir $output)) {
        Write-Step '既存の Worker ビルド出力が固定値と一致しないため作り直します'
        Remove-Tree $builtDir
    }
    if (-not (Test-Path -LiteralPath $builtDir)) {
        $runtime = $manifest.runtime
        $archivePath = Join-Path $cacheRoot ([string]$runtime.fileName)
        Save-VerifiedFile $runtime.source $archivePath $runtime.byteSize $runtime.sha256 'CPython 埋め込み版'

        $wheelMatrix = Read-Json $wheelMatrixPath
        $runtimeWheels = @($wheelMatrix.packages | Where-Object lock -eq 'runtime')
        $wheelhouse = Join-Path $cacheRoot 'wheelhouse'
        $wheelsReady = (Test-Path -LiteralPath $wheelhouse -PathType Container) -and
            @(Get-ChildItem -LiteralPath $wheelhouse -Force).Count -eq $runtimeWheels.Count
        if ($wheelsReady) {
            foreach ($wheel in $runtimeWheels) {
                if (-not (Test-FileEvidence (Join-Path $wheelhouse $wheel.fileName) $wheel.byteSize $wheel.sha256)) { $wheelsReady = $false; break }
            }
        }
        $buildPython = Resolve-BuildPython
        if (-not $wheelsReady) {
            Write-Step "固定 wheel（$($runtimeWheels.Count) 件）をダウンロードしています"
            Remove-Tree $wheelhouse
            [IO.Directory]::CreateDirectory($wheelhouse) | Out-Null
            & $buildPython -m pip download --disable-pip-version-check --no-cache-dir --no-deps `
                --require-hashes --only-binary=:all: --dest $wheelhouse -r $requirementsPath
            if ($LASTEXITCODE -ne 0) { throw "固定 wheel のダウンロードに失敗しました（pip exit $LASTEXITCODE）。" }
            if (@(Get-ChildItem -LiteralPath $wheelhouse -Force).Count -ne $runtimeWheels.Count) {
                throw 'ダウンロードした wheel の集合が wheel-matrix.json と一致しません。'
            }
            foreach ($wheel in $runtimeWheels) {
                Assert-FileEvidence (Join-Path $wheelhouse $wheel.fileName) $wheel.byteSize $wheel.sha256 $wheel.fileName
            }
        }

        Write-Step 'Worker（Python 実行環境）を構築しています（packaging\python\build-runtime.ps1）'
        & $buildRuntimeScript -RuntimeArchivePath $archivePath -WheelhousePath $wheelhouse `
            -BuildPythonPath $buildPython -OutputPath $builtDir
        if (-not (Test-WorkerTree $builtDir $output)) {
            throw 'Worker の構築結果が packaging\python\runtime-manifest.json の固定値と一致しません。'
        }
    }

    Write-Step "Worker を配置しています: $workerDir"
    Remove-Tree $workerDir
    Copy-Item -LiteralPath $builtDir -Destination $workerDir -Recurse -Force
    Get-ChildItem -LiteralPath $workerDir -Recurse -Force -File | ForEach-Object {
        [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal)
    }
    if (-not (Test-WorkerTree $workerDir $output)) {
        throw 'Worker の配置結果が固定値と一致しません。'
    }
    Copy-FileTo (Join-Path $workerDir ([string]$manifest.runtime.license.fileName)) (Join-Path $Target 'Licenses\CPython-LICENSE.txt')
}

$target = Resolve-AppDirectory
$appExecutable = Join-Path $target 'HubRadio.App.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    if ($SkipBuild -or -not [string]::IsNullOrWhiteSpace($AppDirectory)) {
        throw "HubRadio.App.exe が見つかりません: $target 。先に scripts\build.ps1 を実行してください。"
    }
    Write-Step "アプリ出力が無いため $Configuration 構成でビルドします（scripts\build.ps1）"
    & (Join-Path $repoRoot 'scripts\build.ps1') -Configuration $Configuration
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
        throw "ビルド後も HubRadio.App.exe が見つかりません: $target"
    }
}

Write-Step "配置先: $target"
[IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
Stop-RunningApp $target

Install-ToolSet $target
Install-FontSet $target
Install-Model $target
Install-Worker $target

if (-not (Test-Path -LiteralPath (Join-Path $target 'Evidence\application-build.json') -PathType Leaf)) {
    Write-Warning 'Evidence\application-build.json がありません。scripts\build.ps1 でアプリをビルドし直してください。'
}

Write-Information '' -InformationAction Continue
Write-Information 'インストールが完了しました。アプリを起動（または「もう一度試す」を押）して「編集を開始」を実行してください。' -InformationAction Continue
Write-Information "  起動: pwsh -NoLogo -NoProfile -File .\run.ps1 -SkipBuild" -InformationAction Continue
