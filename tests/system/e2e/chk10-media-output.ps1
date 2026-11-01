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
Invoke-Test -Label "CHK10 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb
        $root = Get-Root $p
        switch ($Ac) {
            'AC-049' {
                # FR-006 AC-049: 登録した元素材のメタデータが記録・表示される。異常系: 破損動画で警告。
                Start-QuickStart $root $video
                foreach ($k in 'ファイル名','再生時間','解像度','縦横比','フレームレート','音声','容量','作成日時','登録者') {
                    $null = Find-ByName $root $k 10
                }
                $bad = Join-Path $sb.Work 'broken.mp4'
                [IO.File]::WriteAllBytes($bad, [byte[]](1..64))
                Start-QuickStart $root $bad
                $null = Find-ByName $root '警告' 10
            }
            'AC-001' {
                # FR-001 AC-001: quick start で 16:9 長尺版。ロゴ・外部 URL なし。異常系: 元動画なしで原因と次の行動。
                Start-QuickStart $root $video
                $out = Get-ChildItem $sb.Work -Recurse -Filter '*.mp4' | Where-Object { $_.Name -notmatch 'short|9x16|vertical' } | Select-Object -First 1
                if (-not $out) { throw 'no 16:9 long output' }
                $v = (Get-Probe $out.FullName).streams | Where-Object codec_type -eq video
                if ([math]::Abs($v.width / $v.height - 16 / 9) -gt 0.01) { throw "not 16:9: $($v.width)x$($v.height)" }
                $manifest = Get-ChildItem $sb.Work -Recurse -Filter 'manifest*.json' | Select-Object -First 1
                if ($manifest -and (Get-Content $manifest.FullName -Raw) -match 'https?://') { throw 'external URL guessed into output' }
                Invoke-Element (Find-ByName $root '編集を開始')
                $null = Find-ByName $root '次の行動' 10
            }
            'AC-057' {
                # FR-002 AC-057: YouTube 横長版の形式。異常系: 満たせない場合に原因と次の行動。
                Start-QuickStart $root $video
                $out = Get-ChildItem $sb.Work -Recurse -Filter '*youtube*.mp4' | Select-Object -First 1
                if (-not $out) { throw 'no youtube output' }
                $pr = Get-Probe $out.FullName
                $v = $pr.streams | Where-Object codec_type -eq video
                $a = $pr.streams | Where-Object codec_type -eq audio
                if ($v.width -ne 1920 -or $v.height -ne 1080) { throw 'not 1920x1080' }
                if ($v.codec_name -ne 'h264' -or $v.profile -ne 'High' -or $v.pix_fmt -ne 'yuv420p') { throw 'video codec mismatch' }
                if ($a.codec_name -ne 'aac' -or [int]$a.sample_rate -ne 48000) { throw 'audio mismatch' }
                if (Test-ByName $root '公開承認済み' 2) { throw 'forbidden approval label shown' }
            }
            'AC-002' {
                # FR-002 AC-002: 縦型ショート 1080x1920。異常系: 領域を確定できない場合の扱い。
                Start-QuickStart $root $video
                $out = Get-ChildItem $sb.Work -Recurse -Filter '*.mp4' | Where-Object { $_.Name -match 'short|9x16|vertical' } | Select-Object -First 1
                if (-not $out) { throw 'no short output' }
                $v = (Get-Probe $out.FullName).streams | Where-Object codec_type -eq video
                if ($v.width -ne 1080 -or $v.height -ne 1920) { throw 'not 1080x1920' }
                $null = Find-ByName $root '30' 10
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
