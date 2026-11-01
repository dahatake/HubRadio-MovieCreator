#Requires -Version 7.0
# 要求: FR-001/FR-002/FR-009 ほか。AC-049 AC-001 AC-057 AC-002 AC-060 AC-079（system）
# 期待値は受入基準だけから決める。実装が未完成なら失敗する（not_run から開始）。
param([Parameter(Mandatory)][ValidateSet('AC-049','AC-001','AC-057','AC-002','AC-060','AC-079')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
function Get-Probe([string]$file) {
    $ff = Get-Command ffprobe -ErrorAction SilentlyContinue
    if (-not $ff) { throw 'ffprobe not available to verify output' }
    (& $ff.Source -v error -print_format json -show_streams -show_format $file | ConvertFrom-Json)
}
function Start-QuickStart($root, $video) {
    New-ProjectWithCompletedJobs $root $sb 'chk10-sample' 1 $video
}
# Workspace 内の成果物の動画（元動画の複製は除く）を、中身（ffprobe）で分類して返す。成果物のファイル名は ID なので名前では判別しない。
function Get-OutputVideos([string]$sourceVideo) {
    $srcHash = (Get-FileHash -LiteralPath $sourceVideo).Hash
    foreach ($f in Get-ChildItem (Get-WorkspaceDir $sb) -Recurse -File -Filter '*.mp4' -ErrorAction SilentlyContinue) {
        if ($f.Length -eq 0) { continue }
        if ((Get-FileHash -LiteralPath $f.FullName).Hash -eq $srcHash) { continue }
        $pr = Get-Probe $f.FullName
        $v = $pr.streams | Where-Object codec_type -eq video | Select-Object -First 1
        if (-not $v) { continue }
        [pscustomobject]@{ File = $f; Probe = $pr; Video = $v; Audio = ($pr.streams | Where-Object codec_type -eq audio | Select-Object -First 1) }
    }
}
Invoke-Test -Label "CHK10 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb
        $root = Get-Root $p
        switch ($Ac) {
            'AC-049' {
                # FR-006 AC-049: 登録した元素材のメタデータが記録・表示される。異常系: 破損動画で警告。
                # 「元動画の情報」は Quick start（かんたん編集）画面に出る。元動画を選んだ直後の画面（Job 作成前）で探す。
                $null = New-ProjectDraft $root $sb 'chk10-meta' $video -KeepInitialName
                foreach ($k in 'ファイル名','再生時間','解像度','縦横比','フレームレート','音声','容量','作成日時','登録者') {
                    $null = Find-ByName $root $k 10
                }
                $bad = Join-Path $sb.Work 'broken.mp4'
                [IO.File]::WriteAllBytes($bad, [byte[]](1..64))
                # 一覧へ戻る操作に依存しないよう、再起動して一覧から新しい Project（破損動画）を作る
                Stop-App $p
                $p = Start-App $sb; $root = Get-Root $p
                $null = New-ProjectDraft $root $sb 'chk10-broken' $bad -KeepInitialName
                $null = Find-ByName $root '警告' 10
            }
            'AC-001' {
                # FR-001 AC-001: quick start で 16:9 長尺版。ロゴ・外部 URL なし。異常系: 元動画なしで原因と次の行動。
                Start-QuickStart $root $video
                $outs = @(Get-OutputVideos $video)
                $out = $outs | Where-Object { [math]::Abs($_.Video.width / $_.Video.height - 16 / 9) -le 0.01 } | Select-Object -First 1
                if (-not $out) { throw 'no 16:9 long output' }
                $manifest = Get-ChildItem $sb.Work -Recurse -Filter 'manifest*.json' | Select-Object -First 1
                if ($manifest -and (Get-Content $manifest.FullName -Raw) -match 'https?://') { throw 'external URL guessed into output' }
                # 異常系: 満たせない（実行基盤の欠落）ときは、原因と次の行動が出る
                $tool = Get-ChildItem $sb.AppDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
                if (-not $tool) { throw 'ffmpeg.exe not found in distribution' }
                Stop-App $p
                $bak = "$($tool.FullName).bak"
                Move-Item $tool.FullName $bak
                try {
                    $p = Start-App $sb; $root = Get-Root $p
                    New-ProjectDraft $root $sb 'chk10-no-runtime' $video | Out-Null
                    Invoke-Element (Find-ByName $root '編集を開始')
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    while (@(Find-AllByNameLike $root '編集を開始できません。').Count -eq 0 -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
                    $txt = (Find-AllByNameLike $root '.*' | ForEach-Object { $_.Current.Name }) -join "`n"
                    if ($txt -notmatch '編集を開始できません。') { throw 'cause not shown when the build cannot be made' }
                    if ($txt -notmatch '対処|もう一度|確認してください') { throw 'next action not shown' }
                } finally { if (Test-Path $bak) { Move-Item $bak $tool.FullName -Force } }
            }
            'AC-057' {
                # FR-002 AC-057: YouTube 横長版の形式。異常系: 満たせない場合に原因と次の行動。
                Start-QuickStart $root $video
                $outs = @(Get-OutputVideos $video | Where-Object { $_.Video.width -eq 1920 -and $_.Video.height -eq 1080 })
                if ($outs.Count -eq 0) { throw 'no 1920x1080 output' }
                foreach ($o in $outs) {
                    $v = $o.Video; $a = $o.Audio
                    if ($v.codec_name -ne 'h264' -or $v.profile -ne 'High' -or $v.pix_fmt -ne 'yuv420p') { throw "video codec mismatch: $($o.File.Name)" }
                    if (-not $a -or $a.codec_name -ne 'aac' -or [int]$a.sample_rate -ne 48000) { throw "audio mismatch: $($o.File.Name)" }
                }
                if (Test-ByName $root '公開承認済み' 2) { throw 'forbidden approval label shown' }
            }
            'AC-002' {
                # FR-002 AC-002: 縦型ショート 1080x1920。異常系: 領域を確定できない場合の扱い。
                Start-QuickStart $root $video
                $out = Get-OutputVideos $video | Where-Object { $_.Video.width -eq 1080 -and $_.Video.height -eq 1920 } | Select-Object -First 1
                if (-not $out) { throw 'no short output (1080x1920)' }
                # 目標尺（既定 30 秒）が製品判断として表示される（現在の画面 → 元動画 → 設定の順に探す）
                $shown = (@(Find-AllByNameLike $root '30\s*秒').Count -gt 0)
                if (-not $shown) { Invoke-Element (Find-ByName $root '元動画'); $shown = (@(Find-AllByNameLike $root '30\s*秒').Count -gt 0) }
                if (-not $shown) { Open-Settings $root; $shown = (@(Find-AllByNameLike $root '30\s*秒').Count -gt 0) }
                if (-not $shown) { throw 'target duration (30 seconds) not displayed' }
            }
            'AC-060' {
                # FR-010 AC-060: 字幕の生成。異常系: 満たせない場合の原因表示。意味的完全性 NOT EVALUATED。
                Start-QuickStart $root $video
                $srt = Get-ChildItem $sb.Work -Recurse -Include '*.srt','*.vtt' | Select-Object -First 1
                if (-not $srt) { throw 'no subtitle file' }
                if (-not (Get-ChildItem $sb.Work -Recurse -Filter '*.json' | Select-String 'NOT EVALUATED' -List)) { throw 'NOT EVALUATED not recorded' }
            }
            'AC-079' {
                # FR-013 AC-079: 標準成果物一式。異常系: 不成立成果物は空の偽物なし・manifest に理由。
                Start-QuickStart $root $video
                $manifest = Get-ChildItem $sb.Work -Recurse -Filter 'manifest*.json' | Select-Object -First 1
                if (-not $manifest) { throw 'no manifest' }
                foreach ($f in Get-ChildItem $sb.Work -Recurse -Filter '*.mp4') {
                    if ($f.Length -eq 0) { throw "empty fake output: $($f.Name)" }
                }
            }
        }
    } finally { Stop-App $p }
}
