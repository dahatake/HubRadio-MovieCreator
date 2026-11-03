#Requires -Version 7.0
# AC-009 AC-010 AC-017 AC-030 AC-036 AC-038（system）。期待値は受入基準だけから決める。
# 画面の名前は現行の UI に合わせる: 起動直後は「Project一覧」「ジョブの状態の確認」のタブ、Project を開くと「元動画」「ジョブの状態の確認」「仕上がり」。
param([Parameter(Mandatory)][ValidateSet('AC-009','AC-010','AC-017','AC-030','AC-036','AC-038')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null

# 実行基盤の依存（Tools\ffmpeg.exe）を使えない状態にする。アプリを止めて退避する（戻すのは Restore-Ffmpeg）。
function Hide-Ffmpeg($p, $sb) {
    $tool = Get-ChildItem $sb.AppDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $tool) { throw 'ffmpeg.exe not found in distribution' }
    Stop-App $p
    $bak = "$($tool.FullName).bak"
    Move-Item $tool.FullName $bak
    [pscustomobject]@{ Tool = $tool.FullName; Bak = $bak }
}
function Restore-Ffmpeg($h) { if ($h -and (Test-Path $h.Bak)) { Move-Item $h.Bak $h.Tool -Force } }
# 「編集を開始」が失敗したときの表示（原因と次の行動・path/SID/例外文なし・ジョブの状態の画面へ移動しない）
function Assert-StartFailureShown($root) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $msg = $null
    do {
        $msg = @(Find-AllByNameLike $root '編集を開始できません。') | Select-Object -First 1
        if (-not $msg) { Start-Sleep -Milliseconds 500 }
    } while (-not $msg -and $sw.Elapsed.TotalSeconds -lt 60)
    if (-not $msg) { throw "failure message '編集を開始できません。' not shown" }
    $txt = (Find-AllByNameLike $root '.*' | ForEach-Object { $_.Current.Name }) -join "`n"
    if ($txt -match '[A-Z]:\\|S-1-5-|Exception') { throw 'path/SID/exception leaked' }
    if ($txt -notmatch '自動修復') { throw 'message lacks the note that automatic repair was tried' }
    if ($txt -notmatch '対処|もう一度|確認してください') { throw 'message lacks the next action' }
    $null = Find-ByName $root 'もう一度試す（自動で再確認）' 10
    if (Test-TabSelected $root 'ジョブの状態の確認') { throw 'moved to the job status screen although finalizeDraft failed' }
}
# 比較の列の数（「N つ目: Job …」の見出し／「N つ目に表示するJob」の選択欄の最大の N）
function Get-CompareColumnCount($root) {
    $d = @(Find-AllByNameLike $root '^(\d+)つ目(: Job \d+|に表示するJob)' | ForEach-Object { [int]([regex]::Match($_.Current.Name, '^(\d+)つ目').Groups[1].Value) } | Sort-Object -Unique)
    if ($d.Count -eq 0) { return 0 }
    ($d | Measure-Object -Maximum).Maximum
}
Invoke-Test -Label "CHK10 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb
        $root = Get-Root $p
        switch ($Ac) {
            'AC-009' {
                # FR-009: 依存欠落時 project.runtime_unavailable。path・SID を含まない。
                $h = Hide-Ffmpeg $p $sb
                try {
                    $p = Start-App $sb; $root = Get-Root $p
                    New-ProjectDraft $root $sb 'runtime-missing' $video | Out-Null
                    Invoke-Element (Find-ByName $root '編集を開始')
                    Assert-StartFailureShown $root
                } finally { Restore-Ffmpeg $h }
            }
            'AC-010' {
                # FR-010: Project 一覧での新規作成（初期値の名前で一覧に現れる）・削除の確認・確認でのキャンセル・削除。
                $initial = @(New-ProjectDraft $root $sb 'unused' $video -KeepInitialName)[-1]
                if ([string]::IsNullOrWhiteSpace($initial)) { throw 'new Project has no initial name' }
                Invoke-Element (Find-ByName $root 'Project一覧へ戻る')
                $null = Find-ByName $root "${initial}を開く" 15
                # 削除を選ぶ → 確認 → キャンセル: Project は変更されない
                Invoke-Element (Find-ByName $root "${initial}を削除")
                Answer-ConfirmDialog $root $false
                if (-not (Test-ByName $root "${initial}を開く" 5)) { throw 'project vanished after cancelling the delete' }
                # 削除を選ぶ → 確認 → 削除される
                Invoke-Element (Find-ByName $root "${initial}を削除")
                Answer-ConfirmDialog $root $true
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while ((Test-ByName $root "${initial}を開く" 1) -and $sw.Elapsed.TotalSeconds -lt 30) { }
                if (Test-ByName $root "${initial}を開く" 2) { throw 'project still listed after delete' }
            }
            'AC-017' {
                # FR-017: 「編集を開始」で finalizeDraft が成功するとジョブの状態の確認へ自動で移動し、進み具合と Job の状態が出る。失敗時は移動せず原因表示。
                New-ProjectDraft $root $sb 'nav-sample' $video | Out-Null
                if (Test-TabSelected $root 'ジョブの状態の確認') { throw 'already on the job status screen before 編集を開始' }
                Invoke-Element (Find-ByName $root '編集を開始')
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while (-not (Test-TabSelected $root 'ジョブの状態の確認') -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 500 }
                if (-not (Test-TabSelected $root 'ジョブの状態の確認')) { throw 'did not move to the job status screen after finalizeDraft succeeded' }
                $state = '^サムネイル: Job 1、|進み具合|処理中|待機|実行中|完了'
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while (@(Find-AllByNameLike $root $state).Count -eq 0 -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
                if (@(Find-AllByNameLike $root $state).Count -eq 0) { throw 'progress / Job state not shown' }
                # 失敗時: 移動せず、原因と次の行動が出る
                $h = Hide-Ffmpeg $p $sb
                try {
                    $p = Start-App $sb; $root = Get-Root $p
                    New-ProjectDraft $root $sb 'nav-failure' $video | Out-Null
                    Invoke-Element (Find-ByName $root '編集を開始')
                    Assert-StartFailureShown $root
                } finally { Restore-Ffmpeg $h }
            }
            'AC-030' {
                # FR-030: 設定違いの Job を 2 つ以上。識別・並べて比較。
                New-ProjectWithCompletedJobs $root $sb 'cmp-sample' 2 $video
                Invoke-Element (Find-ByName $root '仕上がり')
                foreach ($n in 1, 2) {
                    if (@(Find-AllByNameLike $root "Job $n").Count -eq 0) { throw "Job $n is not identifiable on the results screen" }
                }
                Invoke-Element (Find-ByName $root 'Jobを比較')
                $null = Find-ByName $root '設定の比較' 20
                $names = @(Find-AllByNameLike $root '^[12]つ目: Job \d+' | ForEach-Object { $_.Current.Name } | Select-Object -Unique)
                if ($names.Count -lt 2) { throw "jobs are not compared side by side: $($names -join ' / ')" }
                if ((@($names | ForEach-Object { $_ -replace '^\d+つ目: ', '' } | Select-Object -Unique)).Count -lt 2) { throw 'compared columns are not distinguishable' }
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
                Invoke-Element (Find-ByName $root '仕上がり')
                Invoke-Element (Find-ByName $root 'Jobを比較')
                $cb = $null; $sw = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $cb = Find-AllByNameLike $root '^比べるJobの数$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox } | Select-Object -First 1
                    if (-not $cb) { Start-Sleep -Milliseconds 300 }
                } while (-not $cb -and $sw.Elapsed.TotalSeconds -lt 20)
                if (-not $cb) { throw "combo box '比べるJobの数' not found" }
                # HTML select のポップアップでは DOWN は候補の移動だけで、
                # ENTER で確定するまで change が届かない場合がある。各回、
                # HOME から絶対位置を選び直し、確定してから画面の列数を検証する。
                # SendKeys は UI スレッド・フォーカス競合で例外（"この操作を正しく終了しました。" など）になることがある。
                # 各回でウィンドウを前面にして要素をクリックし直し、例外時は最大 3 回（間隔 500ms）再試行する。
                # 例外でもキーが届いていることがあるため、成否は例外の有無ではなく画面の列数を読み戻して判定する（成功偽装なし）。
                function Send-CompareSelection([string]$keys, [int]$want) {
                    $got = -1; $lastErr = $null
                    for ($try = 1; $try -le 3; $try++) {
                        $lastErr = $null
                        try {
                            $c = Find-AllByNameLike $root '^比べるJobの数$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox } | Select-Object -First 1
                            if (-not $c) { throw "combo box '比べるJobの数' not found" }
                            Focus-ByClick $p $c
                            [System.Windows.Forms.SendKeys]::SendWait($keys)
                        } catch { $lastErr = $_.Exception.Message }
                        $wait = [Diagnostics.Stopwatch]::StartNew()
                        do {
                            $got = Get-CompareColumnCount $root
                            if ($got -eq $want) { return }
                            Start-Sleep -Milliseconds 200
                        } while ($wait.Elapsed.TotalSeconds -lt 10)
                        Start-Sleep -Milliseconds 500
                    }
                    throw "expected $want compared columns after committed selection (3 attempts), got $got; last SendKeys error: $lastErr"
                }
                foreach ($want in 2, 3, 4) {
                    Send-CompareSelection ('{HOME}' + ('{DOWN}' * ($want - 2)) + '{ENTER}{TAB}') $want
                }
                # 5つ目が存在する素材でも、4を超える選択候補はなく、
                # DOWN を繰り返して確定しても比較列数は4のまま。
                Send-CompareSelection '{END}{DOWN}{DOWN}{ENTER}{TAB}' 4
                Start-Sleep -Milliseconds 700
                if ((Get-CompareColumnCount $root) -ne 4) { throw 'comparison did not retain the 4-Job limit' }
                if (@(Find-AllByNameLike $root '^[5-9]つ目').Count -gt 0) { throw '5 or more jobs selectable' }
            }
        }
    } finally { Stop-App $p }
}
