#Requires -Version 7.0
# FR-137 AC-153 AC-154（integration）。AC-155 は Q-022 で BLOCKED のためケースなし。
param([Parameter(Mandatory)][ValidateSet('AC-153','AC-154')][string]$Ac)
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
                    Select-Template $root $n; Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root $i | Out-Null
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
                    }
                }
            }
            'AC-154' {
                foreach ($n in 'ショート飛び出し', 'バラエティ') { Select-Template $root $n; Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root ([array]::IndexOf(@('ショート飛び出し', 'バラエティ'), $n) + 1) | Out-Null }
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
                }
                $jv = $vari.Text | ConvertFrom-Json -Depth 32
                foreach ($e in @($jv.emphasis)) {
                    if (-not $e.sfx) { throw 'variety emphasis telop has no sound effect' }
                    if ([math]::Abs([double]$e.motionSeconds - 0.5) -gt 0.15) { throw "motion not about 0.5s: $($e.motionSeconds)" }
                }
                foreach ($j in $js, $jv) { if (@($j.emphasis).Count -gt $j.limits.maxEmphasisTelops) { throw 'emphasis telops exceed FR-059 limit' } }
            }
        }
    } finally { Stop-App $p }
}
