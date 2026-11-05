#Requires -Version 7.0
# FR-145 AC-181（system）。期待値は受入基準だけから決める。
# 自動選択が有効な Project で、(1) 解析中でも Project 作成直後の画面移動・元動画の確認ができ、
# (2) 最初の Job（編集を開始）は自動選択を適用せず出自「なし」。(a) 2 つ目以降でそのまま使う＝自動選択、(b) 別のテンプレートへ変える＝手動、(c) 「テンプレートなし」を明示選択＝手動、(d) 自動選択を使わない設定の Project でテンプレートを選ばず実行＝なし。3 値のどれか 1 つが記録・表示され、
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
    # 出自の値は Label（なければ Origin）の欄だけで判定する（RuleVersion 等ほかの欄の「なし」を混ぜない。AC-181）
    $rec = $text | ConvertFrom-Json
    $originVal = if ($rec.Label) { [string]$rec.Label } else { [string]$rec.Origin }
    $kinds = @{ '自動選択' = '^(自動選択|auto)$'; '手動' = '^(手動|manual)$'; 'なし' = '^(なし|none)$' }
    $hits = @($kinds.Keys | Where-Object { $originVal -match $kinds[$_] })
    if ($hits.Count -ne 1 -or $hits[0] -ne $label) { throw "($case) origin must be exactly one of 自動選択/手動/なし and be '$label': $text" }
    # AC-188: 規則版は自動選択のときだけ版番号、それ以外は「なし」
    $rv = [string]$rec.RuleVersion
    if ($label -eq '自動選択') { if (-not $rv -or $rv -match '^(なし|none)$') { throw "($case) RuleVersion must be a version when origin is 自動選択: $text" } }
    elseif ($rv -notmatch '^(なし|none)$') { throw "($case) RuleVersion must be 'なし' when origin is not 自動選択: $text" }
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
        # 最初の Job（「編集を開始」）は自動選択を適用せず、出自は「なし」でテンプレートは適用されない（解析の完了前後を問わない）
        Assert-JobOrigin $root $sb 'first' 'なし' '((?<!テンプレート)なし|\bnone\b)' $null
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
        # (c) 「テンプレートなし」を明示的に選んで Job を実行する → どのテンプレートも適用されず、出自は「手動」（「なし」ではない）
        Invoke-Element (Find-ByName $root '元動画')
        Invoke-Element (Find-ByName $root 'テンプレートを使わない')
        New-TemplateJob $root 3
        Assert-JobOrigin $root $sb 'c' '手動' '(手動|manual)' $null
        # (d) スキップ: [設定] で自動選択を「使わない」にして新しい Project を作り、テンプレートを選ばずに Job を実行する → 出自は「なし」
        # 保存した設定は「次に作成するJob」（開いている Project）にだけ効くため、Project 一覧へ戻ってから全体の設定を変える
        Invoke-Element (Find-ByName $root 'Project一覧へ戻る' 30)
        Open-Settings $root
        # 導線: タブ「Projectと出力」→ 項目「テンプレートの自動選択」→ 選択肢「使わない」→「保存」（変更は保存して初めて有効。保存後に作る Project から有効）
        Invoke-Element (Find-ByName $root 'Projectと出力' 30)
        # 設定の一覧パネルが非表示（画面の配置）のときは「設定の一覧を表示」で出す
        if (Test-ByName $root '設定の一覧を表示' 5) { Invoke-Element (Find-ByName $root '設定の一覧を表示') }
        # 「設定の詳細」が最大化されていると一覧が隠れるため、最大化を戻して一覧を出す
        if (-not (Find-AllByNameLike $root '^設定の一覧$' | Select-Object -First 1)) {
            $mx = Find-AllByNameLike $root '^最大化$' | Where-Object { try { $_.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On' } catch { $false } } | Select-Object -First 1
            if ($mx) { Invoke-Element $mx }
        }
        $item = $null; $sw3 = [Diagnostics.Stopwatch]::StartNew()
        do {
            $item = Find-AllByNameLike $root 'テンプレートの自動選択|テンプレート自動選択|Template auto-selection' | Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text } | Select-Object -First 1
            if (-not $item) { Start-Sleep -Milliseconds 300 }
        } while (-not $item -and $sw3.Elapsed.TotalSeconds -lt 30)
        if (-not $item) { Find-AllByNameLike $root '.' | Out-Null; throw 'settings item for template auto-selection not found in the Projectと出力 tab' }
        Invoke-Element $item
        Invoke-Element (Find-ByName $root '使わない' 30)
        # 項目の「保存」は名前が「 保存」（先頭に空白）。Workspaceフォルダー欄の「保存」とは別のボタン
        $saveBtn = Find-AllByNameLike $root '^\s+保存$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
        if (-not $saveBtn) { throw 'settings item save button not found' }
        Invoke-Element $saveBtn
        $null = Find-ByName $root '未保存の変更はありません。' 30
        if (Test-ByName $root '設定を閉じて戻る' 3) { Invoke-Element (Find-ByName $root '設定を閉じて戻る') }
        $null = New-ProjectWithCompletedJobs $root $sb 'auto-skip' 0 $video
        New-TemplateJob $root 1
        Assert-JobOrigin $root $sb 'd' 'なし' '((?<!テンプレート)なし|\bnone\b)' $null
    } finally { Stop-App $p }
}
