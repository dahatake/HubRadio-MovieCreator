#Requires -Version 7.0
# FR-145 AC-181（system）。期待値は受入基準だけから決める。
# 自動選択が有効な Project で、(1) 解析中でも Project 作成直後の画面移動・元動画の確認ができ、
# (2) (a) そのまま使う＝自動選択、(b) 別のテンプレートへ変える＝手動、(c) テンプレートなし＝なし の 3 値のどれか 1 つが記録・表示され、
#     適用されるテンプレートが利用者の最終の選択と一致する。
# 注: 「解析がスキップされた Project」（自動選択を使わない設定）は integration（AC-182/183 側）で検証する。
param([ValidateSet('AC-181')][string]$Ac = 'AC-181')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
Set-StrictMode -Off

function Assert-JobOrigin($root, $sb, [string]$case, [string]$label, [string]$labelRx, [string]$expectedTemplate) {
    # 直近に作られた Job の出自の記録（Workspace 配下の template-origin.json の最新）。3 値のうちちょうど 1 つが記録され、適用テンプレートが最終の選択と一致する。
    $ws = Get-WorkspaceDir $sb
    $f = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter 'template-origin.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
    if ($f.Count -eq 0) { throw "($case) no origin record found under workspace" }
    $text = Get-Content -LiteralPath $f[-1].FullName -Raw -Encoding UTF8
    $hits = @('(自動選択|auto)', '(手動|manual)', '(なし|none)') | Where-Object { $text -match $_ }
    if (@($hits).Count -ne 1 -or $text -notmatch $labelRx) { throw "($case) origin must be exactly one of 自動選択/手動/なし and be '$label': $text" }
    if ($expectedTemplate) {
        if ($text -notmatch [regex]::Escape($expectedTemplate)) { throw "($case) applied template is not the final selection '$expectedTemplate': $text" }
    } else {
        foreach ($n in $script:TemplateNames) { if ($text -match [regex]::Escape($n)) { throw "($case) a template '$n' was applied although none was chosen: $text" } }
    }
    # 表示: 出自が画面にも出る
    if (-not (Find-AllByNameLike $root ([regex]::Escape($label)) | Select-Object -First 1)) { Start-Sleep -Seconds 3 }
    if (-not (Find-AllByNameLike $root ([regex]::Escape($label)) | Select-Object -First 1)) { throw "($case) origin '$label' is not displayed" }
}

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
        # (a) 自動選択されたテンプレートをそのまま使って Job を実行する → 適用はそのテンプレート、出自は「自動選択」
        New-TemplateJob $root 1
        Assert-JobOrigin $root $sb 'a' '自動選択' '(自動選択|auto)' $auto
        # (b) 別のテンプレートへ変えて Job を実行する → 変えた後のテンプレートが適用され、出自は「手動」。自動選択だったテンプレートは適用されない
        $other = $script:TemplateNames | Where-Object { $_ -ne $auto } | Select-Object -First 1
        Select-Template $root $other
        New-TemplateJob $root 2
        Assert-JobOrigin $root $sb 'b' '手動' '(手動|manual)' $other
        $r = Get-RenderRecord $sb 0
        $applied = "$($r.templateName)$($r.template)$($r.templateId)"
        if ($applied -and $applied -notmatch [regex]::Escape($other)) { throw "changed template '$other' was not applied (auto='$auto')" }
        # 異常系: 解析の完了で、手動で変えた選択が上書きされない（Job 作成後も選択は変えた後のまま）
        Start-Sleep -Seconds 5
        $e2 = Find-AllByNameLike $root ("^" + [regex]::Escape($other) + "$") | Select-Object -First 1
        if ($e2) {
            $sel2 = $null; try { $sel2 = $e2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
            if ($sel2 -eq $false) { throw 'manual selection was overwritten after the analysis finished' }
        }
        # (c) 「テンプレートなし」を選んで Job を実行する → どのテンプレートも適用されず、出自は「なし」（3 値以外・空欄は記録されない）
        Invoke-Element (Find-ByName $root '元動画')
        Invoke-Element (Find-ByName $root 'テンプレートを使わない')
        New-TemplateJob $root 3
        Assert-JobOrigin $root $sb 'c' 'なし' '(なし|none)' $null
    } finally { Stop-App $p }
}
