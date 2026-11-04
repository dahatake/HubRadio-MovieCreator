#Requires -Version 7.0
# FR-145 AC-181（system）。期待値は受入基準だけから決める。
# 自動選択が有効な Project で、(1) 解析中でも Project 作成直後の画面移動・元動画の確認ができ、
# (2) 自動選択されたテンプレートを別のものに変えて Job を実行すると、変えた後のテンプレートが適用される。
param([ValidateSet('AC-181')][string]$Ac = 'AC-181')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
Set-StrictMode -Off

$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-145 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        # 自動選択は初期値（有効）のまま。Project を作成する。
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $null = New-ProjectWithCompletedJobs $root $sb 'auto-sys' 0 $video
        # 解析中の画面操作: 元動画の画面へ移動でき、元動画の確認ができる（解析の完了を待たない。作成後 30 秒以内に操作できる）
        Invoke-Element (Find-ByName $root '元動画' 30)
        if ($sw.Elapsed.TotalSeconds -gt 120) { throw 'screen navigation after project creation waited for the analysis' }
        # 自動選択の候補が初期選択として現れるまで待つ（FR-137 の 7 種のいずれか）
        $auto = $null; $wait = [Diagnostics.Stopwatch]::StartNew()
        do {
            foreach ($n in $script:TemplateNames) {
                $e = Find-AllByNameLike $root ("^" + [regex]::Escape($n) + "$") | Select-Object -First 1
                if ($e) {
                    $sel = $null; try { $sel = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
                    if ($sel) { $auto = $n; break }
                }
            }
            if (-not $auto) { Start-Sleep -Milliseconds 500 }
        } while (-not $auto -and $wait.Elapsed.TotalSeconds -lt 60)
        if (-not $auto) { throw 'no template was preselected by the automatic selection' }
        # 別のテンプレートへ変えて Job を実行する
        $other = $script:TemplateNames | Where-Object { $_ -ne $auto } | Select-Object -First 1
        Select-Template $root $other
        New-TemplateJob $root 1
        $rec = Get-TemplateJobRecord $sb
        $text = ($rec | ForEach-Object { $_.Text }) -join "`n"
        # 変えた後のテンプレートが適用される（出自は「手動」）。自動選択だったテンプレートの ID は Job の適用 ID にならない
        $r = Get-RenderRecord $sb 0
        $applied = "$($r.templateName)$($r.template)$($r.templateId)"
        if ($applied -and $applied -notmatch [regex]::Escape($other) -and $text -notmatch [regex]::Escape($other)) { throw "changed template '$other' was not applied (auto='$auto')" }
        if ($text -notmatch '(手動|manual)') { throw 'origin "手動" was not recorded for the changed template' }
        # 異常系: 解析の完了で、手動で変えた選択が上書きされない（Job 作成後も選択は変えた後のまま）
        Start-Sleep -Seconds 5
        $e2 = Find-AllByNameLike $root ("^" + [regex]::Escape($other) + "$") | Select-Object -First 1
        if ($e2) {
            $sel2 = $null; try { $sel2 = $e2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
            if ($sel2 -eq $false) { throw 'manual selection was overwritten after the analysis finished' }
        }
    } finally { Stop-App $p }
}
