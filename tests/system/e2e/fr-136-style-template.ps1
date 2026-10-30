#Requires -Version 7.0
# FR-136 AC-150 AC-151 AC-152（integration）。期待値は受入基準だけから決める。
param([Parameter(Mandatory)][ValidateSet('AC-150','AC-151','AC-152')][string]$Ac)
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-136 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        switch ($Ac) {
            'AC-150' {
                # 一覧に 名前・向く用途・縦型/横長・見た目の例。設定値は入力せずに Job が完了し、演出が適用される。
                $null = New-ProjectWithCompletedJobs $root $sb 'tpl-list' 0 $video
                Invoke-Element (Find-ByName $root '演出テンプレート')
                foreach ($n in $script:TemplateNames | Where-Object { $_ -ne 'キャラクター対話' }) { $null = Find-ByName $root $n }
                if (@(Find-AllByNameLike $root '(縦型|横長)').Count -lt 1) { throw 'orientation (portrait/landscape) not shown in list' }
                if (@(Find-AllByNameLike $root '(用途|向く)').Count -lt 1) { throw 'intended use not shown in list' }
                if (@(Find-AllByNameLike $root '(見た目|例|プレビュー)').Count -lt 1) { throw 'visual example not shown in list' }
                Invoke-Element (Find-ByName $root '解説')
                Invoke-Element (Find-ByName $root '実行')
                Wait-JobCompleted $root 1 | Out-Null
                $rec = Get-TemplateJobRecord $sb
                if (-not ($rec | Where-Object { $_.Text -match '解説' })) { throw 'selected template not applied/recorded' }
            }
            'AC-151' {
                # テンプレートなし = 既定の出力。テンプレート ID は付かず、同じ入力の 2 回で字幕・テロップ・効果音が同じ。
                $null = New-ProjectWithCompletedJobs $root $sb 'tpl-none' 2 $video
                $caps = @(Get-DeliverableCaptions $sb)
                if ($caps.Count -lt 2) { throw 'deliverable captions missing' }
                foreach ($c in $caps | Where-Object { $_.Extension -eq '.json' }) {
                    $t = Get-Content -LiteralPath $c.FullName -Raw -Encoding UTF8
                    if ($t -match '(?i)"template(Id|_id)"\s*:\s*"[^"]+"') { throw "template id present without selection: $($c.Name)" }
                }
                $srt = @($caps | Where-Object { $_.Extension -in '.srt', '.vtt' } | ForEach-Object { (Get-FileHash $_.FullName).Hash })
                if (@($srt | Sort-Object -Unique).Count -gt 1) { throw 'default captions differ between identical inputs' }
            }
            'AC-152' {
                # ID と版が保存される。同じ設定で再実行すると同じ演出。調整すると「テンプレート＋変更あり」。
                $null = New-ProjectWithCompletedJobs $root $sb 'tpl-repro' 0 $video
                foreach ($i in 1, 2) { Select-Template $root '解説'; Invoke-Element (Find-ByName $root '実行'); Wait-JobCompleted $root $i | Out-Null }
                $rec = Get-TemplateJobRecord $sb
                foreach ($r in $rec) {
                    if ($r.Text -notmatch '(?i)(templateVersion|template_version|版)') { throw "template version missing: $($r.Path)" }
                }
                $plans = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)templateId|template_id' } | ForEach-Object {
                    ($_.Text -replace '(?i)"(createdAt|created_at|jobId|job_id|id|at)"\s*:\s*"[^"]*"', '') })
                if ($plans.Count -ge 2 -and (@($plans | Sort-Object -Unique).Count -gt 1)) { throw 'rerun with same template produced a different effect plan' }
                Invoke-Element (Find-ByName $root '調整')
                Set-Value (Find-EditByName $root '開始') '0.5'
                Invoke-Element (Find-ByName $root '保存')
                if (-not (Test-ByName $root 'テンプレート＋変更あり' 10)) { throw 'adjusted job not identified as template + modified' }
            }
        }
    } finally { Stop-App $p }
}
