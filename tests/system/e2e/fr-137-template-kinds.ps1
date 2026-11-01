#Requires -Version 7.0
# FR-137 AC-153 AC-154（integration）。AC-155 は Q-022 で BLOCKED のためケースなし。
param([Parameter(Mandatory)][ValidateSet('AC-153','AC-154')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-137 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        $null = New-ProjectWithCompletedJobs $root $sb 'tpl-kinds' 0 $video
        switch ($Ac) {
            'AC-153' {
                # (1)〜(5)(7) を適用。強調は字幕の一部、強調色・書体は PARAM-006/007 以内、字幕は FR-051 の上限内。
                $i = 0
                foreach ($n in '解説', 'ショート飛び出し', 'バラエティ', '対談番組', '縦型3段', '手作り感') {
                    $i++
                    Select-Template $root $n; New-TemplateJob $root $i
                }
                $recs = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)templateId|template_id' })
                if ($recs.Count -lt 6) { throw "expected 6 job records with templates, got $($recs.Count)" }
                foreach ($r in $recs) {
                    $j = $r.Text | ConvertFrom-Json -Depth 32
                    $emph = @($j.emphasis); $subs = @($j.subtitles)
                    if (-not $emph -or -not $subs) { throw "emphasis/subtitles not declared: $($r.Path)" }
                    $colors = @($emph | ForEach-Object { $_.color } | Sort-Object -Unique)
                    $fonts = @($j.fonts | Sort-Object -Unique)
                    if ($colors.Count -gt $j.limits.maxEmphasisColors) { throw "too many emphasis colors: $($colors.Count)" }
                    if ($fonts.Count -gt $j.limits.maxFonts) { throw "too many fonts: $($fonts.Count)" }
                    $all = ($subs | ForEach-Object { $_.text }) -join "`n"
                    foreach ($e in $emph) { if (-not $all.Contains([string]$e.text)) { throw "emphasis text not a substring of subtitles: $($e.text)" } }
                    foreach ($s in $subs) {
                        if ($s.lines -gt $j.limits.maxLines) { throw 'subtitle lines exceed FR-051 limit' }
                        if ($s.maxLineChars -gt $j.limits.maxLineChars) { throw 'line length exceeds FR-051 limit' }
                        if (($s.end - $s.start) -gt $j.limits.maxCueSeconds) { throw 'cue length exceeds FR-051 limit' }
                        # 最短 cue の下限（FR-012・FR-051）: 元の素材の cue が最初から下限未満の箇所だけ例外（警告つき）。テンプレートの分割で下限を割らない
                        if ($null -ne $j.limits.PSObject.Properties['minCueSeconds'] -and ($s.end - $s.start) -lt $j.limits.minCueSeconds) {
                            if (-not (($null -ne $s.PSObject.Properties['sourceCueBelowMinimum']) -and $s.sourceCueBelowMinimum)) { throw "cue shorter than the minimum created by the template split: $($s.text)" }
                        }
                    }
                    if ($null -ne $j.limits.PSObject.Properties['minSubtitleCount'] -and $subs.Count -lt $j.limits.minSubtitleCount) { throw 'fewer subtitles than the minimum count' }
                    # 強調以外のテロップの文字は、字幕の一部・利用者の入力・確認済みの要約のいずれか
                    if ($null -ne $j.PSObject.Properties['telops']) {
                        $inputs = @($j.userInputs) + @($j.confirmedSummaries)
                        foreach ($t in @($j.telops)) {
                            if (-not $all.Contains([string]$t.text) -and -not ($inputs | Where-Object { ([string]$_).Contains([string]$t.text) })) { throw "telop text not from subtitles / user input / confirmed summary: $($t.text)" }
                        }
                    }
                }
            }
            'AC-154' {
                foreach ($n in 'ショート飛び出し', 'バラエティ') { Select-Template $root $n; New-TemplateJob $root ([array]::IndexOf(@('ショート飛び出し', 'バラエティ'), $n) + 1) }
                $recs = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)templateId|template_id' })
                $short = $recs | Where-Object { $_.Text -match 'ショート飛び出し' } | Select-Object -First 1
                $vari = $recs | Where-Object { $_.Text -match 'バラエティ' } | Select-Object -First 1
                if (-not $short -or -not $vari) { throw 'job records for (2)/(3) missing' }
                $js = $short.Text | ConvertFrom-Json -Depth 32
                foreach ($c in @($js.subtitles)) {
                    $w = @(([string]$c.text) -split '\s+' | Where-Object { $_ }).Count
                    if ($w -lt 1 -or $w -gt 3) { throw "short template shows $w words at once" }
                    $hl = @($c.words | Where-Object { $_.highlight })
                    if ($hl.Count -gt 1) { throw 'more than the currently spoken word is highlighted' }
                    # 最短 cue の下限を割る分割は行わない（警告は元の cue が最初から下限未満のときだけ）
                    if ($null -ne $js.limits.PSObject.Properties['minCueSeconds'] -and ([double]$c.end - [double]$c.start) -lt $js.limits.minCueSeconds) {
                        if (-not (($null -ne $c.PSObject.Properties['sourceCueBelowMinimum']) -and $c.sourceCueBelowMinimum)) { throw "split created a cue below the minimum length: $($c.text)" }
                    }
                }
                $jv = $vari.Text | ConvertFrom-Json -Depth 32
                foreach ($e in @($jv.emphasis)) {
                    if (-not $e.sfx) { throw 'variety emphasis telop has no sound effect' }
                    if ([double]$e.motionSeconds -lt 0.4 -or [double]$e.motionSeconds -gt 0.6) { throw "motion not within PARAM-009 0.4-0.6s: $($e.motionSeconds)" }
                }
                # PARAM-008: (2) の縮む動きは開始 120〜140%（既定 130）から終了 100%
                foreach ($e in @($js.emphasis)) { if ([double]$e.scaleStartPercent -lt 120 -or [double]$e.scaleStartPercent -gt 140 -or [double]$e.scaleEndPercent -ne 100) { throw "shrink motion not 120-140% -> 100%: $($e.scaleStartPercent)->$($e.scaleEndPercent)" } }
                # FR-142: 単語時刻のない cue は語ごとの強調をせず（cue 全体を強調か強調なし）、警告を出す
                $untimed = @($js.subtitles | Where-Object { @($_.words | Where-Object { $null -ne $_.start }).Count -eq 0 })
                foreach ($c in $untimed) { if (@($c.words | Where-Object { $_.highlight }).Count -gt 0 -and -not $c.wholeCueHighlight) { throw 'per-word highlight on a cue without word timings' } }
                if ($untimed.Count -gt 0 -and -not ($short.Text -match '(?i)(warning|警告)')) { throw 'no warning for cues without word timings' }
                foreach ($j in $js, $jv) { if (@($j.emphasis).Count -gt $j.limits.maxEmphasisTelops) { throw 'emphasis telops exceed FR-059 limit' } }
            }
        }
    } finally { Stop-App $p }
}
