#Requires -Version 7.0
# AC-007 AC-127 AC-129（system）。期待値は受入基準だけから決める。
param([Parameter(Mandatory)][ValidateSet('AC-007','AC-127','AC-129')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "CHK10 $Ac" -Sandbox $sb -Body {
    try {
        switch ($Ac) {
            'AC-007' {
                # NFR-DIST-001: 1 回の起動で UI へ到達。異常系: 管理者昇格を要求しない・欠落を成功扱いしない。
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $p = Start-App $sb
                $root = Get-Root $p
                $null = Find-ByName $root '設定' 60
                $sw.Stop()
                if ($p.StartInfo.Verb -eq 'runas') { throw 'elevation requested' }
                $null = Find-ByName $root 'Project一覧' 10   # 起動直後のタブ（Project を開くと「元動画」になる）
                Write-Host "first visible UI: $($sw.Elapsed.TotalSeconds)s"
                Stop-App $p
                $p = Start-App $sb   # 再起動後も設定・Project を失わない
                $null = Find-ByName (Get-Root $p) '設定' 60
            }
            'AC-127' {
                # 配置の復元と既定に戻す。
                # 起動直後（FR-061.1）の画面は Project 一覧（「Projectの操作」「Projectのカード」の 2 パネル）。
                # 配置を変更 → 再起動で復元 → 既定に戻す（確認つき）で初期配置、を検査する。
                $isOn = {
                    param($el)
                    try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On }
                    catch { $false }
                }
                $p = Start-App $sb; $root = Get-Root $p
                $null = Find-ByName $root 'Projectの操作' 60
                $null = Find-ByName $root 'Projectのカード' 10
                Invoke-Element (Find-ByName $root '配置を変更')
                Invoke-Element (Find-ByName $root '横に並べる')
                if (-not (& $isOn (Find-ByName $root '横に並べる'))) { throw 'arrangement change not applied' }
                Stop-App $p
                $p = Start-App $sb; $root = Get-Root $p
                $null = Find-ByName $root 'Projectの操作' 60
                Invoke-Element (Find-ByName $root '配置を変更')
                if (-not (& $isOn (Find-ByName $root '横に並べる' 10))) { throw 'arrangement not restored after restart' }
                # 異常系: 確認で「やめる」なら配置は保たれる
                Invoke-Element (Find-ByName $root '既定に戻す')
                Invoke-Element (Find-ByName $root 'やめる')
                if (-not (& $isOn (Find-ByName $root '横に並べる' 10))) { throw 'arrangement lost after cancel' }
                # 確認で確定すると初期配置（標準）
                Invoke-Element (Find-ByName $root '既定に戻す')
                Invoke-Element (Find-ByName $root '既定に戻す（確定）')
                if (& $isOn (Find-ByName $root '横に並べる' 10)) { throw 'arrangement not reset' }
                if (-not (& $isOn (Find-ByName $root '標準' 10))) { throw 'default arrangement not shown' }
            }
            'AC-129' {
                # 移動・タブ化・非表示・最大化で入力中の内容が失われない。
                $p = Start-App $sb; $root = Get-Root $p
                Open-Settings $root
                $f = Find-ByName $root 'Workspaceフォルダーの場所'
                Set-Value $f 'typed-in-progress'
                foreach ($op in '移動','タブ化','非表示','最大化') { Invoke-Element (Find-ByName $root $op) }
                if ((Get-Value (Find-ByName $root 'Workspaceフォルダーの場所')) -ne 'typed-in-progress') { throw 'input lost' }
            }
        }
    } finally { Stop-App $p }
}
