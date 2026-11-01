#Requires -Version 7.0
# AC-009 AC-010 AC-017 AC-030 AC-036 AC-038（system）。期待値は受入基準だけから決める。
param([Parameter(Mandatory)][ValidateSet('AC-009','AC-010','AC-017','AC-030','AC-036','AC-038')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "CHK10 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb
        $root = Get-Root $p
        switch ($Ac) {
            'AC-009' {
                # FR-009: 依存欠落時 project.runtime_unavailable。path・SID を含まない。
                $tool = Get-ChildItem (Split-Path (Get-Process -Id $p.Id).Path) -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
                if (-not $tool) { throw 'ffmpeg.exe not found in distribution' }
                Stop-App $p
                $bak = "$($tool.FullName).bak"
                Move-Item $tool.FullName $bak
                try {
                    $p = Start-App $sb; $root = Get-Root $p
                    Invoke-Element (Find-ByName $root '編集を開始')
                    $null = Find-ByName $root '編集を開始できません。' 20
                    $txt = (Find-AllByNameLike $root '.*' | ForEach-Object { $_.Current.Name }) -join "`n"
                    if ($txt -match '[A-Z]:\\|S-1-5-|Exception') { throw 'path/SID/exception leaked' }
                } finally { if (Test-Path $bak) { Move-Item $bak $tool.FullName -Force } }
            }
            'AC-010' {
                # FR-010: Project の新規作成・削除・削除キャンセル。
                Invoke-Element (Find-ByName $root '新規作成')
                $null = Find-ByName $root 'Project' 10
                Invoke-Element (Find-ByName $root '削除')
                Invoke-Element (Find-ByName $root 'キャンセル')
                $null = Find-ByName $root 'Project' 10
                Invoke-Element (Find-ByName $root '削除')
                Invoke-Element (Find-ByName $root '確認')
                if (Test-ByName $root 'Project' 3) { throw 'project still listed after delete' }
            }
            'AC-017' {
                # FR-017: finalizeDraft 成功でジョブ状態画面へ自動遷移。失敗時は移動せず原因表示。
                New-ProjectWithCompletedJobs $root $sb 'nav-sample' 1 $video
                $null = Find-ByName $root 'ジョブの状態' 10
                Invoke-Element (Find-ByName $root 'Project 一覧')
                Invoke-Element (Find-ByName $root '新規作成')
                Invoke-Element (Find-ByName $root '編集を開始')
                $null = Find-ByName $root '次の行動' 10
            }
            'AC-030' {
                # FR-030: 設定違いの Job を 2 つ以上。識別・並べて比較。
                New-ProjectWithCompletedJobs $root $sb 'cmp-sample' 2 $video
                Invoke-Element (Find-ByName $root '仕上がりの比較')
                if (@(Find-AllByNameLike $root '(?i)(Job|ジョブ)').Count -lt 2) { throw 'jobs not distinguishable' }
            }
            'AC-036' {
                # FR-036: 3 タブのみ。「保存」タブ・「仕上がりへ戻る」なし。
                New-ProjectWithCompletedJobs $root $sb 'tab-sample' 2 $video
                foreach ($t in '元動画','ジョブの状態の確認','仕上がり') { $null = Find-ByName $root $t 10 }
                if (Test-ByName $root '仕上がりへ戻る' 2) { throw 'forbidden back button exists' }
                if (Test-ByName $root '保存タブ' 2) { throw 'forbidden save tab exists' }
            }
            'AC-038' {
                # FR-038: 完了 Job 2〜4 の比較表示、5 つ以上は選べない。
                New-ProjectWithCompletedJobs $root $sb 'cmp4-sample' 5 $video
                Invoke-Element (Find-ByName $root '仕上がりの比較')
                $boxes = @(Find-AllByNameLike $root '(?i)(選択|select)')
                if ($boxes.Count -lt 5) { throw 'selection controls missing' }
                $boxes[0..4] | ForEach-Object { try { Invoke-Element $_ } catch {} }
                $sel = @(Find-AllByNameLike $root '(?i)(比較列|compare column)')
                if ($sel.Count -gt 4) { throw '5 or more jobs selectable' }
            }
        }
    } finally { Stop-App $p }
}
