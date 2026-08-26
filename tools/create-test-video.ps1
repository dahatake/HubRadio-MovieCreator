#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(60, 21600)][int]$TimeoutSeconds = 10800
)

$ErrorActionPreference = "Stop"
$invariant = [Globalization.CultureInfo]::InvariantCulture
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$approvedCapabilitiesPath = Join-Path $repoRoot "benchmarks\results\ffmpeg-capabilities.json"
$fixtureManifestPath = Join-Path $repoRoot "benchmarks\fixtures\fixture-manifest.json"

function Protect-SensitiveText {
    param([Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    $safe = $Text.Replace($repoRoot, "<WORKSPACE>")
    if ($env:USERPROFILE) {
        $safe = $safe.Replace($env:USERPROFILE, "<USERPROFILE>")
    }
    return $safe
}

function Find-ApprovedExecutable {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$ExecutableName,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    $packagesRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (-not (Test-Path -LiteralPath $packagesRoot -PathType Container)) {
        throw "WinGet packages directory is unavailable."
    }

    $packageDirectory = Get-ChildItem $packagesRoot -Directory -Filter "$PackageId*" -ErrorAction Stop |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $packageDirectory) {
        throw "Approved FFmpeg package is not installed: $PackageId"
    }

    $approved = @(Get-ChildItem $packageDirectory.FullName -Recurse -File -Filter $ExecutableName | Where-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedSha256
    })
    if ($approved.Count -ne 1) {
        throw "Expected exactly one approved $ExecutableName, found $($approved.Count)."
    }
    return $approved[0].FullName
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$SafetyTimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Failed to start $([IO.Path]::GetFileName($FileName))."
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($SafetyTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$([IO.Path]::GetFileName($FileName)) exceeded the safety timeout of $SafetyTimeoutSeconds seconds."
        }
        return [ordered]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = Protect-SensitiveText $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function Get-MediaDescription {
    param(
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$Path
    )

    $result = Invoke-Tool -FileName $FfprobePath -SafetyTimeoutSeconds 300 -Arguments @(
        "-v", "error",
        "-show_entries", "format=format_name,duration,size:stream=index,codec_type,codec_name,width,height,pix_fmt,r_frame_rate,sample_rate,channels",
        "-of", "json", $Path
    )
    if ($result.exitCode -ne 0) {
        throw "ffprobe failed with exit code $($result.exitCode): $($result.stderr)"
    }
    if ([string]::IsNullOrWhiteSpace($result.stdout)) {
        throw "ffprobe returned no JSON."
    }
    return $result.stdout | ConvertFrom-Json -ErrorAction Stop
}

function Write-ProblemReport {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Phenomenon,
        [Parameter(Mandatory)][string]$ObservedFacts
    )

    $safePhenomenon = Protect-SensitiveText $Phenomenon
    $safeFacts = Protect-SensitiveText $ObservedFacts
    $encoding = [Text.UTF8Encoding]::new($false)
    $recordedAt = (Get-Date).ToUniversalTime().ToString("O")
    $existingContent = ""
    $problemNumber = 1
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existingContent = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        $problemNumbers = @([regex]::Matches($existingContent, '(?m)^## 問題(?<number>[0-9]+)(?=[:\r\n])') | ForEach-Object {
            [int]$_.Groups["number"].Value
        })
        if ($problemNumbers.Count -gt 0) {
            $problemNumber = [int](($problemNumbers | Measure-Object -Maximum).Maximum) + 1
        }
    }

    $header = @"
# 動画生成 問題レポート

- 初回記録日時（UTC）: $recordedAt
- 入力: sample/$([IO.Path]::GetFileName($SourcePath))
- 予定出力: test/$([IO.Path]::GetFileName($OutputPath))
"@

    $entry = @"
## 問題${problemNumber}: 自動記録された失敗

- 記録日時（UTC）: $recordedAt

### 現象の説明

$safePhenomenon

### 根本原因

根本原因は特定できていません。確認できた事実は次のとおりです。

$safeFacts

原因が確認できていないため、推測を根本原因として記載していません。

### 考えられる解決策の選択肢

1. 記録された終了コードと秘匿済みエラーを基に、同じ入力の短区間で再現確認する。
2. NVIDIA encoder固有のエラーであることが確認できた場合のみ、Media Foundationの`h264_mf`を別試験する。
3. 入力破損が確認された場合は、元動画を変更せず、修復済み中間素材と時刻写像を別成果物として作る。
4. 容量不足が確認された場合は、既存成果物を自動削除せず、別の固定ディスクを選択する。

### 推奨する解決策

まず短区間で同じ変換を再現し、終了コードとエラーを分類します。分類後にだけ、encoder変更・入力修復・保存先変更の該当する一案を選びます。

### 選択理由

現時点では根本原因が未特定です。先に再現と分類を行う方法だけが、推測による誤った修正や元動画の変更を避けながら、観測事実を増やせるためです。
"@
    $content = if ([string]::IsNullOrEmpty($existingContent)) {
        $header + [Environment]::NewLine + $entry
    }
    else {
        $existingContent.TrimEnd([char[]]"`r`n") + [Environment]::NewLine + [Environment]::NewLine + $entry
    }
    $reportStagingPath = Join-Path ([IO.Path]::GetDirectoryName($Path)) ".$([IO.Path]::GetFileName($Path)).$([Guid]::NewGuid().ToString("N")).partial.md"
    try {
        [IO.File]::WriteAllText($reportStagingPath, $content, $encoding)
        [IO.File]::Move($reportStagingPath, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $reportStagingPath) {
            Remove-Item -LiteralPath $reportStagingPath -Force
        }
    }
}

function Publish-FilePair {
    param(
        [Parameter(Mandatory)][string]$StagedOutput,
        [Parameter(Mandatory)][string]$FinalOutput,
        [Parameter(Mandatory)][string]$StagedManifest,
        [Parameter(Mandatory)][string]$FinalManifest
    )

    $token = [Guid]::NewGuid().ToString("N")
    $outputBackup = Join-Path ([IO.Path]::GetDirectoryName($FinalOutput)) ".$([IO.Path]::GetFileName($FinalOutput)).$token.partial.rollback"
    $manifestBackup = Join-Path ([IO.Path]::GetDirectoryName($FinalManifest)) ".$([IO.Path]::GetFileName($FinalManifest)).$token.partial.rollback"
    $hadOutput = Test-Path -LiteralPath $FinalOutput -PathType Leaf
    $hadManifest = Test-Path -LiteralPath $FinalManifest -PathType Leaf
    $newOutputMoved = $false
    $newManifestMoved = $false

    try {
        if ($hadOutput) {
            [IO.File]::Move($FinalOutput, $outputBackup)
        }
        if ($hadManifest) {
            [IO.File]::Move($FinalManifest, $manifestBackup)
        }
        [IO.File]::Move($StagedOutput, $FinalOutput)
        $newOutputMoved = $true
        [IO.File]::Move($StagedManifest, $FinalManifest)
        $newManifestMoved = $true
    }
    catch {
        $commitMessage = Protect-SensitiveText $_.Exception.Message
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        try {
            if ($hadOutput -and (Test-Path -LiteralPath $outputBackup -PathType Leaf)) {
                [IO.File]::Move($outputBackup, $FinalOutput, $true)
            }
            elseif ($newOutputMoved -and (Test-Path -LiteralPath $FinalOutput -PathType Leaf)) {
                [IO.File]::Delete($FinalOutput)
            }
        }
        catch {
            $rollbackErrors.Add("output rollback: $(Protect-SensitiveText $_.Exception.Message)")
        }
        try {
            if ($hadManifest -and (Test-Path -LiteralPath $manifestBackup -PathType Leaf)) {
                [IO.File]::Move($manifestBackup, $FinalManifest, $true)
            }
            elseif ($newManifestMoved -and (Test-Path -LiteralPath $FinalManifest -PathType Leaf)) {
                [IO.File]::Delete($FinalManifest)
            }
        }
        catch {
            $rollbackErrors.Add("manifest rollback: $(Protect-SensitiveText $_.Exception.Message)")
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "Final file-pair commit failed and rollback was incomplete. Commit: $commitMessage Rollback: $($rollbackErrors -join '; ')"
        }
        throw "Final file-pair commit failed; the prior final pair was restored. Commit: $commitMessage"
    }

    $cleanupErrors = [Collections.Generic.List[string]]::new()
    foreach ($backup in @($outputBackup, $manifestBackup)) {
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            try {
                [IO.File]::Delete($backup)
            }
            catch {
                $cleanupErrors.Add((Protect-SensitiveText $_.Exception.Message))
            }
        }
    }
    return [ordered]@{
        outputCommitted = $newOutputMoved
        manifestCommitted = $newManifestMoved
        backupCleanupErrors = @($cleanupErrors)
    }
}

$sourceFullPath = [IO.Path]::GetFullPath($SourcePath)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot "sample"))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot "test"))

if ([IO.Path]::GetDirectoryName($sourceFullPath) -ne $sourceDirectory) {
    throw "Source must be a direct child of the repository sample directory."
}
if ([IO.Path]::GetDirectoryName($outputFullPath) -ne $outputDirectory) {
    throw "Output must be a direct child of the repository test directory."
}
if ([IO.Path]::GetFileName($sourceFullPath) -ne [IO.Path]::GetFileName($outputFullPath)) {
    throw "Output file name must match the source file name."
}
if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "Source video does not exist."
}
if ($sourceFullPath -eq $outputFullPath) {
    throw "Source and output paths must differ."
}

[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$stem = [IO.Path]::GetFileNameWithoutExtension($outputFullPath)
$problemReportPath = Join-Path $outputDirectory "$stem-problems.md"
$manifestPath = "$outputFullPath.manifest.json"
$stagingPath = Join-Path $outputDirectory ".$stem.$([Guid]::NewGuid().ToString("N")).partial.mov"
$manifestStagingPath = Join-Path $outputDirectory ".$stem.$([Guid]::NewGuid().ToString("N")).manifest.partial.json"
$outputCommitted = $false
$manifestCommitted = $false

try {
    $capabilities = Get-Content $approvedCapabilitiesPath -Raw -Encoding utf8 | ConvertFrom-Json -ErrorAction Stop
    if (-not $capabilities.capabilities.encoders.h264_nvenc -or -not $capabilities.capabilities.encoders.aac) {
        throw "Approved capability record does not contain h264_nvenc and aac encoders."
    }
    $ffmpegPath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffmpeg.exe" -ExpectedSha256 $capabilities.executables.ffmpeg.sha256
    $ffprobePath = Find-ApprovedExecutable -PackageId $capabilities.package.id -ExecutableName "ffprobe.exe" -ExpectedSha256 $capabilities.executables.ffprobe.sha256

    $fixture = Get-Content $fixtureManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -ErrorAction Stop
    $sourceSpec = @($fixture.sourceAssets | Where-Object fileName -eq ([IO.Path]::GetFileName($sourceFullPath)))
    if ($sourceSpec.Count -ne 1) {
        throw "Fixture manifest does not contain exactly one matching source asset."
    }
    $sourceInfo = Get-Item -LiteralPath $sourceFullPath
    $sourceHash = (Get-FileHash $sourceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $sourceSpec[0].sha256 -or [int64]$sourceInfo.Length -ne [int64]$sourceSpec[0].byteSize) {
        throw "Source video does not match the fixture manifest."
    }

    $sourceMedia = Get-MediaDescription -FfprobePath $ffprobePath -Path $sourceFullPath
    $sourceDuration = [decimal]::Parse($sourceMedia.format.duration, [Globalization.NumberStyles]::Float, $invariant)
    $sourceAudio = @($sourceMedia.streams | Where-Object codec_type -eq "audio")
    $sourceHasAudio = $sourceAudio.Count -gt 0
    $startedAt = (Get-Date).ToUniversalTime()
    $render = Invoke-Tool -FileName $ffmpegPath -SafetyTimeoutSeconds $TimeoutSeconds -Arguments @(
        "-nostdin", "-v", "error", "-progress", "pipe:1", "-nostats",
        "-i", $sourceFullPath,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", "scale=1280:720:flags=lanczos,fps=30000/1001,format=yuv420p",
        "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23", "-b:v", "0",
        "-c:a", "aac", "-b:a", "160k",
        "-map_metadata", "-1", "-movflags", "+faststart",
        "-y", $stagingPath
    )
    if ($render.exitCode -ne 0) {
        throw "FFmpeg render failed with exit code $($render.exitCode): $($render.stderr)"
    }
    if (-not (Test-Path -LiteralPath $stagingPath -PathType Leaf) -or (Get-Item $stagingPath).Length -le 0) {
        throw "FFmpeg reported success but produced no non-empty staging output."
    }

    $outputMedia = Get-MediaDescription -FfprobePath $ffprobePath -Path $stagingPath
    $video = @($outputMedia.streams | Where-Object codec_type -eq "video")
    $audio = @($outputMedia.streams | Where-Object codec_type -eq "audio")
    if ($video.Count -ne 1 -or $video[0].codec_name -ne "h264" -or $video[0].width -ne 1280 -or $video[0].height -ne 720 -or $video[0].pix_fmt -ne "yuv420p" -or $video[0].r_frame_rate -ne "30000/1001") {
        throw "Output video stream does not match the fixed H.264 1280x720 yuv420p 30000/1001 profile."
    }
    if ($sourceHasAudio -and ($audio.Count -ne 1 -or $audio[0].codec_name -ne "aac")) {
        throw "Source has audio, but output does not contain exactly one AAC audio stream."
    }
    if (-not $sourceHasAudio -and $audio.Count -ne 0) {
        throw "Source has no audio, but output unexpectedly contains an audio stream."
    }
    $outputDuration = [decimal]::Parse($outputMedia.format.duration, [Globalization.NumberStyles]::Float, $invariant)
    $durationDifference = [math]::Abs([double]($sourceDuration - $outputDuration))
    if ($durationDifference -gt 1.0) {
        throw "Output duration differs from source by more than the one-second integrity guard: $durationDifference seconds."
    }

    $decode = Invoke-Tool -FileName $ffmpegPath -SafetyTimeoutSeconds $TimeoutSeconds -Arguments @(
        "-nostdin", "-v", "error", "-xerror",
        "-i", $stagingPath,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-f", "null", "NUL"
    )
    if ($decode.exitCode -ne 0) {
        throw "Full output decode failed with exit code $($decode.exitCode): $($decode.stderr)"
    }

    $outputHash = (Get-FileHash $stagingPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($outputHash -eq $sourceHash) {
        throw "Output content hash unexpectedly matches the source."
    }
    $finishedAt = (Get-Date).ToUniversalTime()
    $outputByteSize = [int64](Get-Item $stagingPath).Length

    $manifest = [ordered]@{
        schemaVersion = "1.0.0"
        status = "succeeded"
        startedAtUtc = $startedAt.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        elapsedMilliseconds = [math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 3)
        source = [ordered]@{
            relativePath = "sample/$([IO.Path]::GetFileName($sourceFullPath))"
            byteSize = [int64]$sourceInfo.Length
            sha256 = $sourceHash
            durationSeconds = $sourceDuration
        }
        operation = [ordered]@{
            kind = "internal-development-proxy-render"
            video = "H.264 NVENC, 1280x720, 30000/1001 fps, yuv420p"
            audio = "AAC 160 kbit/s when source audio is present"
            metadata = "source global metadata not copied (-map_metadata -1); muxer/codec technical tags may remain"
            externalPublicationAllowed = $false
        }
        tool = [ordered]@{
            packageId = $capabilities.package.id
            packageVersion = $capabilities.package.version
            ffmpegSha256 = $capabilities.executables.ffmpeg.sha256
            ffprobeSha256 = $capabilities.executables.ffprobe.sha256
        }
        validation = [ordered]@{
            fullDecode = [ordered]@{
                succeeded = $true
                ffmpegExitCode = [int]$decode.exitCode
                failOnDecodeError = $true
            }
        }
        output = [ordered]@{
            relativePath = "test/$([IO.Path]::GetFileName($outputFullPath))"
            byteSize = $outputByteSize
            sha256 = $outputHash
            durationSeconds = $outputDuration
            absoluteDurationDifferenceSeconds = [math]::Round($durationDifference, 6)
            streams = $outputMedia.streams
        }
    }
    [IO.File]::WriteAllText($manifestStagingPath, ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $commit = Publish-FilePair -StagedOutput $stagingPath -FinalOutput $outputFullPath -StagedManifest $manifestStagingPath -FinalManifest $manifestPath
    $outputCommitted = $commit.outputCommitted
    $manifestCommitted = $commit.manifestCommitted
    if ($commit.backupCleanupErrors.Count -gt 0) {
        throw "Output pair was committed, but rollback backup cleanup failed: $($commit.backupCleanupErrors -join '; ')"
    }
    [ordered]@{
        status = "succeeded"
        outputPath = $outputFullPath
        manifestPath = $manifestPath
        outputSha256 = $outputHash
        outputByteSize = $outputByteSize
        durationSeconds = $outputDuration
    } | ConvertTo-Json
}
catch {
    $facts = "Exception type: $($_.Exception.GetType().FullName)`nMessage: $(Protect-SensitiveText $_.Exception.Message)`nOutput committed: $outputCommitted`nManifest committed: $manifestCommitted"
    $phenomenon = if ($outputCommitted) {
        "動画ファイルは生成・配置されましたが、付帯manifestを含む処理全体は完了しませんでした。出力動画はtestディレクトリに存在する可能性があります。"
    }
    else {
        "動画生成処理は完了しませんでした。既存の最終出力は置換していません。"
    }
    Write-ProblemReport -Path $problemReportPath -Phenomenon $phenomenon -ObservedFacts $facts
    throw "Video generation failed. Detailed report: test/$([IO.Path]::GetFileName($problemReportPath))"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Force
    }
    if (Test-Path -LiteralPath $manifestStagingPath) {
        Remove-Item -LiteralPath $manifestStagingPath -Force
    }
}
