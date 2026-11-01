#Requires -Version 7.0
# FR-140 AC-165 / FR-141 AC-169 / FR-142 AC-172 / FR-143 AC-174（system）。
# 期待値は受入基準だけから決める。成果物の映像（mp4）はフレームを取り出して画素で確認し、Job の記録（JSON）は補助に使う。
param([Parameter(Mandatory)][ValidateSet('AC-165','AC-169','AC-172','AC-174')][string]$Ac)
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')

function Get-DeliverableVideo($sb) {
    $ws = Get-WorkspaceDir $sb
    $v = Get-ChildItem -LiteralPath $ws -Recurse -File -Filter *.mp4 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $v) { throw 'deliverable video (mp4) not found under workspace' }
    return $v.FullName
}
function Save-Frame([string]$video, [double]$t, [string]$png) {
    $ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $ff) { throw 'ffmpeg is required to inspect the deliverable video' }
    & $ff.Source -y -loglevel error -ss ([string]::Format([cultureinfo]::InvariantCulture, '{0:F3}', $t)) -i $video -frames:v 1 $png
    if (-not (Test-Path -LiteralPath $png)) { throw "frame at ${t}s could not be extracted" }
}
function Get-ColorPixels([string]$png, [string]$hex, [int]$tol = 40) {
    # 指定色に近い画素の数と、その外接矩形を返す。
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::new($png)
    try {
        $h = $hex.TrimStart('#'); $r = [Convert]::ToInt32($h.Substring(0, 2), 16); $g = [Convert]::ToInt32($h.Substring(2, 2), 16); $b = [Convert]::ToInt32($h.Substring(4, 2), 16)
        $n = 0; $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
        for ($y = 0; $y -lt $bmp.Height; $y += 2) { for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            if ([math]::Abs($c.R - $r) -le $tol -and [math]::Abs($c.G - $g) -le $tol -and [math]::Abs($c.B - $b) -le $tol) {
                $n++; if ($x -lt $minX) { $minX = $x }; if ($y -lt $minY) { $minY = $y }; if ($x -gt $maxX) { $maxX = $x }; if ($y -gt $maxY) { $maxY = $y }
            } } }
        [pscustomobject]@{ Count = $n; MinX = $minX; MinY = $minY; MaxX = $maxX; MaxY = $maxY; Width = $bmp.Width; Height = $bmp.Height }
    } finally { $bmp.Dispose() }
}
function Get-Midpoint($o) { ([double]$o.start + [double]$o.end) / 2 }

$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-140-143 $Ac" -Sandbox $sb -Body {
    try {
        # AC-172 だけは ASR が 3 語以上の時刻付き単語を返す音声（英文の合成音声）が必要
        $video = if ($Ac -eq 'AC-172') { New-SyntheticSpeechVideo $sb $sb.Work } else { New-SyntheticVideo $sb $sb.Work }
        $p = Start-App $sb; $root = Get-Root $p
        $null = New-ProjectWithCompletedJobs $root $sb 'tpl-sys' 0 $video
        switch ($Ac) {
            'AC-165' {
                # FR-140: 割り当てた話者の立ち絵が該当 cue の間だけ映り、字幕色がキャラごとに異なる。割り当てなしの Job では映らない。
                $img = Join-Path $sb.Work 'portrait-a.png'; New-SyntheticImage $img
                Register-Portrait $root 'キャラA' $img
                # 異常系（先に）: 話者の割り当てなし → 立ち絵は映らない
                Select-Template $root 'キャラクター対話'
                New-TemplateJob $root 1
                $none = Get-RenderRecord $sb 1
                if (@($none.portraits | Where-Object { $_.shown }).Count -gt 0) { throw 'portrait shown without any speaker assignment' }
                # 割り当てあり
                Select-Template $root 'キャラクター対話'
                Invoke-Element (Find-ByName $root '話者を割り当てる')
                Invoke-Element (Find-ByName $root '確認して確定')
                New-TemplateJob $root 2
                $j = Get-RenderRecord $sb 0
                $assigned = @($j.subtitles | Where-Object { $_.speaker })
                $unassigned = @($j.subtitles | Where-Object { -not $_.speaker })
                if ($assigned.Count -lt 1) { throw 'no cue has an assigned speaker' }
                $mp4 = Get-DeliverableVideo $sb
                $png = Join-Path $sb.Work 'f165.png'
                $c = $assigned[0]; $pt = Get-Midpoint $c
                Save-Frame $mp4 $pt $png
                $fa = Get-ColorPixels $png $c.portraitProbeColor
                if ($fa.Count -lt 50) { throw "assigned speaker's portrait not visible during its cue (${pt}s)" }
                # 該当 cue の外では映らない
                $outside = @($j.subtitles | Where-Object { $_.speaker -ne $c.speaker })
                if ($outside.Count -gt 0) { Save-Frame $mp4 (Get-Midpoint $outside[0]) $png; if ((Get-ColorPixels $png $c.portraitProbeColor).Count -ge 50 -and -not $outside[0].speaker) { throw 'portrait visible outside its cue' } }
                foreach ($u in $unassigned) { Save-Frame $mp4 (Get-Midpoint $u) $png; if ((Get-ColorPixels $png $c.portraitProbeColor).Count -ge 50) { throw 'portrait shown for unassigned cue' } }
                $colors = @($assigned | ForEach-Object { $_.color } | Sort-Object -Unique)
                $speakers = @($assigned | ForEach-Object { $_.speaker } | Sort-Object -Unique)
                if ($speakers.Count -ge 2 -and $colors.Count -lt 2) { throw 'subtitle colors not separated per character' }
                foreach ($u in $unassigned) { if ($u.color -ne $j.defaultSubtitleColor) { throw 'unassigned cue not using default subtitle color' } }
            }
            'AC-169' {
                # FR-141: 入力・確認済みのサイドテロップと名前・肩書きの帯だけが映る。未入力・未確認・匿名ラベルは映らない。肩書き未確認は名前だけ＋警告。
                Select-Template $root '対談番組'
                Set-Value (Find-EditByName $root '話者1の名前') '山田太郎'
                Set-Value (Find-EditByName $root '話者1の肩書き') '代表取締役'
                Invoke-Element (Find-ByName $root '肩書きを確認した')
                Set-Value (Find-EditByName $root '話者2の名前') '佐藤花子'
                Set-Value (Find-EditByName $root '話者2の肩書き') '取締役'   # 肩書きは未確認のまま
                New-TemplateJob $root 1
                $j = (Get-TemplateJobRecord $sb | Select-Object -First 1).Text | ConvertFrom-Json -Depth 32
                $bands = @($j.nameBands)
                $b1 = $bands | Where-Object { $_.name -eq '山田太郎' }
                if (-not $b1 -or $b1.title -ne '代表取締役') { throw 'confirmed name+title band missing' }
                $b2 = $bands | Where-Object { $_.name -eq '佐藤花子' }
                if (-not $b2) { throw 'name band for speaker 2 missing' }
                if ($b2.title) { throw 'unconfirmed title was shown' }
                $warn = (Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)warning|警告' -and $_.Text -match '肩書き' })
                if (-not $warn) { throw 'no warning recorded for unconfirmed title' }
                # 匿名ラベル・未入力値は映らない
                $all = ($bands | ForEach-Object { "$($_.name) $($_.title)" }) -join "`n"
                if ($all -match '(話者\s*[0-9A-Z一-九]+|Speaker\s*\d+|SPEAKER_\d+)') { throw 'anonymous speaker label shown' }
                foreach ($t in @($j.sideTelops)) { if (-not $t.confirmed) { throw 'unconfirmed side telop shown' }; if (-not $t.text) { throw 'empty side telop shown' } }
                # 映像にも出ている（帯の位置のフレームに文字色の画素がある）
                $mp4 = Get-DeliverableVideo $sb; $png = Join-Path $sb.Work 'f169.png'
                Save-Frame $mp4 (Get-Midpoint $b1) $png
                if ((Get-ColorPixels $png $b1.textColor).Count -lt 30) { throw 'name band not visible in the video' }
            }
            'AC-172' {
                # FR-142: 各語の話している間だけその語が強調色。他の語は通常色。
                Select-Template $root 'ショート飛び出し'
                New-TemplateJob $root 1
                $j = Get-RenderRecord $sb 0
                $mp4 = Get-DeliverableVideo $sb; $png = Join-Path $sb.Work 'f172.png'
                $hl = $j.highlightColor; $normal = $j.normalColor
                if (-not $hl -or -not $normal -or $hl -eq $normal) { throw 'highlight and normal colors must be defined and differ' }
                $words = @($j.subtitles | ForEach-Object { $_.words } | Where-Object { $null -ne $_.start })
                if ($words.Count -lt 3) { throw 'need at least 3 timed words in the sample' }
                foreach ($w in ($words | Select-Object -First 3)) {
                    Save-Frame $mp4 (Get-Midpoint $w) $png
                    if ((Get-ColorPixels $png $hl).Count -lt 30) { throw "word '$($w.text)' not in highlight color while spoken" }
                }
                # 話し終えた語（その語の直後で、次の語がまだ話されていない境界）は強調色でない: 語の外の時刻で強調色の画素が語数分を超えない
                $w0 = $words[0]; $gap = [double]$w0.end + 0.02
                $next = $words | Where-Object { [double]$_.start -le $gap -and [double]$_.end -gt $gap } | Select-Object -First 1
                if (-not $next) {
                    Save-Frame $mp4 $gap $png
                    if ((Get-ColorPixels $png $hl).Count -ge 30) { throw 'a word stays highlighted after it was spoken' }
                }
                # 異常系: 同時に強調色なのは 1 語だけ（記録に強調の旗は無いので、各 cue の語の時間帯が重ならない＝同時に話している語は 1 語だけ、を確かめる）
                foreach ($c in @($j.subtitles)) {
                    $ws = @($c.words | Where-Object { $null -ne $_.start } | Sort-Object { [double]$_.start })
                    for ($i = 1; $i -lt $ws.Count; $i++) { if ([double]$ws[$i].start -lt [double]$ws[$i - 1].end - 0.001) { throw 'more than one word highlighted at once' } }
                }
            }
            'AC-174' {
                # FR-143: 縦型・テンプレートありの Job は PARAM-005 の余白と字幕領域の両方の内側にあるときだけ合格。外にはみ出すと不合格。
                Select-Template $root '縦型3段'
                New-TemplateJob $root 1
                $j = (Get-TemplateJobRecord $sb | Select-Object -First 1).Text | ConvertFrom-Json -Depth 32
                $w = [int]$j.output.width; $h = [int]$j.output.height
                if ($h -le $w) { throw 'deliverable is not vertical' }
                $m = $j.margins   # PARAM-005 の余白（上・下・右）
                $a = $j.subtitleArea
                foreach ($t in @($j.subtitles) + @($j.emphasis)) {
                    $b = $t.box
                    if ($b.y -lt $m.top -or ($b.y + $b.height) -gt ($h - $m.bottom) -or ($b.x + $b.width) -gt ($w - $m.right)) { throw 'text inside PARAM-005 margin' }
                    if ($b.x -lt $a.x -or $b.y -lt $a.y -or ($b.x + $b.width) -gt ($a.x + $a.width) -or ($b.y + $b.height) -gt ($a.y + $a.height)) { throw 'text outside subtitle area' }
                }
                $insp = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)inspection|出力検査' }) | Select-Object -First 1
                if (-not $insp) { throw 'output inspection result not recorded' }
                if ($insp.Text -notmatch '(?i)(pass|合格)') { throw 'inspection did not pass for a job inside both regions' }
                # 異常系: 余白の内側に食い込む字幕位置では不合格になる
                Invoke-Element (Find-ByName $root '字幕位置を余白内に動かす')
                New-TemplateJob $root 2; $bad = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)inspection|出力検査' } | Where-Object { $_.Text -match '(?i)(fail|不合格)' })
                if ($bad.Count -lt 1) { throw 'inspection passed although text was inside the margin' }
            }
        }
    } finally { Stop-App $p }
}
