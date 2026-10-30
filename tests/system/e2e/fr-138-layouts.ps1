#Requires -Version 7.0
# FR-138 AC-156 AC-157 AC-158（integration）。余白 PARAM-005: 上 250・下 480・右 160 px（AC-156 の記述値）。
param([Parameter(Mandatory)][ValidateSet('AC-156','AC-157','AC-158')][string]$Ac)
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-138 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        $null = New-ProjectWithCompletedJobs $root $sb 'tpl-layout' 0 $video
        switch ($Ac) {
            'AC-156' {
                foreach ($n in 'ショート飛び出し', '縦型3段') { Select-Template $root $n; Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root ([int](@('ショート飛び出し', '縦型3段').IndexOf($n)) + 1) | Out-Null }
                $recs = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)templateId|template_id' })
                if ($recs.Count -lt 2) { throw 'portrait job records missing' }
                foreach ($r in $recs) {
                    $j = $r.Text | ConvertFrom-Json -Depth 32
                    $w = [int]$j.output.width; $h = [int]$j.output.height; $area = $j.subtitleArea
                    foreach ($t in @($j.subtitles) + @($j.emphasis)) {
                        $b = $t.box
                        if ($b.y -lt 250) { throw 'text inside top margin (250px)' }
                        if (($b.y + $b.height) -gt ($h - 480)) { throw 'text inside bottom margin (480px)' }
                        if (($b.x + $b.width) -gt ($w - 160)) { throw 'text inside right margin (160px)' }
                        if ($b.x -lt 160) { throw 'text inside left margin (160px)' }
                        if ($b.x -lt $area.x -or $b.y -lt $area.y -or ($b.x + $b.width) -gt ($area.x + $area.width) -or ($b.y + $b.height) -gt ($area.y + $area.height)) { throw 'text outside FR-014 subtitle area' }
                    }
                }
            }
            'AC-157' {
                # 対談番組: 話題が続く間だけサイドテロップ。名前・肩書きは入力のある話者だけ。
                Select-Template $root '対談番組'
                Set-Value (Find-EditByName $root '話者1の名前') '山田太郎'
                Set-Value (Find-EditByName $root '話者1の肩書き') '代表取締役'
                Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root 1 | Out-Null
                $rec = Get-TemplateJobRecord $sb | Select-Object -First 1
                $j = $rec.Text | ConvertFrom-Json -Depth 32
                $topics = @($j.topics | Sort-Object start)
                if ($topics.Count -lt 2) { throw 'need at least two topics in the sample' }
                $side = @($j.sideTelops)
                foreach ($t in $topics) {
                    $mine = @($side | Where-Object { $_.topicId -eq $t.id })
                    if ($mine.Count -lt 1) { throw "no side telop for topic $($t.id)" }
                    foreach ($m in $mine) { if ($m.start -lt $t.start -or $m.end -gt $t.end) { throw 'side telop shown outside its topic span' } }
                }
                foreach ($m in $side) { if ($m.position -notin 'top-left', 'top-right') { throw 'side telop not at top-left/top-right' } }
                $bands = @($j.nameBands)
                if (-not ($bands | Where-Object { $_.name -eq '山田太郎' })) { throw 'name band missing for speaker with input' }
                foreach ($b in $bands) { if (-not $b.name) { throw 'name band shown without name' } }
                # 肩書きが未確認の話者には肩書きが出ない・匿名ラベルは帯に出ない
                foreach ($b in $bands) { if ($b.title -and -not $b.titleConfirmed) { throw 'unconfirmed title shown in band' }; if (([string]$b.name) -match '(話者\s*[0-9A-Z]+|Speaker\s*\d+|SPEAKER_\d+)') { throw 'anonymous speaker label in band' } }
                # 話題が変わると前の話題のサイドテロップが消える
                for ($k = 0; $k -lt $topics.Count - 1; $k++) { foreach ($m in @($side | Where-Object { $_.topicId -eq $topics[$k].id })) { if ($m.end -gt $topics[$k + 1].start) { throw 'previous topic side telop remains after topic change' } } }
                $others = @($j.speakers | Where-Object { -not $_.name })
                foreach ($s in $others) { if ($bands | Where-Object { $_.speakerId -eq $s.id }) { throw "name band invented for speaker without input: $($s.id)" } }
            }
            'AC-158' {
                Select-Template $root '縦型3段'
                Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root 1 | Out-Null
                $rec = Get-TemplateJobRecord $sb | Select-Object -First 1
                $j = $rec.Text | ConvertFrom-Json -Depth 32
                if ([int]$j.output.width -ne 1080 -or [int]$j.output.height -ne 1920) { throw "output is $($j.output.width)x$($j.output.height), expected 1080x1920" }
                $L = $j.layout
                if (-not $L.top.title -and -not $L.top.hook) { throw 'top band has no title/hook' }
                if (-not $L.middle.source) { throw 'middle band has no source video' }
                if (-not $L.bottom.subtitles) { throw 'bottom band has no subtitles' }
                if (-not ($L.top.y -lt $L.middle.y -and $L.middle.y -lt $L.bottom.y)) { throw 'bands not ordered top/middle/bottom' }
                $first = @($j.timeline)[0]
                if ($first.source -ne 'hook' -and $first.hookSegmentId -ne $j.hook.segmentId) { throw 'timeline does not start with the hook segment' }
                foreach ($s in @($L.bottom.subtitles)) { if (([string]$s.text).Length -gt 20) { throw "bottom subtitle longer than 20 chars: $($s.text)" } }
            }
        }
    } finally { Stop-App $p }
}
