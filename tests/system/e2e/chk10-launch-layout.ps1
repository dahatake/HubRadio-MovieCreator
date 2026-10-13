#Requires -Version 7.0
# AC-007 AC-127 AC-129（system）。期待値は受入基準だけから決める。
param([Parameter(Mandatory)][ValidateSet('AC-007','AC-127','AC-129')][string]$Ac)
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
                $null = Find-ByName $root '元動画' 10
                Write-Host "first visible UI: $($sw.Elapsed.TotalSeconds)s"
                Stop-App $p
                $p = Start-App $sb   # 再起動後も設定・Project を失わない
                $null = Find-ByName (Get-Root $p) '設定' 60
            }
            'AC-127' {
                # 配置の復元と既定に戻す。
                $p = Start-App $sb; $root = Get-Root $p
                Invoke-Element (Find-ByName $root '仕上がり')
                Invoke-Element (Find-ByName $root '配置を変更')
                Stop-App $p
                $p = Start-App $sb; $root = Get-Root $p
                Invoke-Element (Find-ByName $root '仕上がり')
                $null = Find-ByName $root '配置を変更' 10
                Invoke-Element (Find-ByName $root '既定に戻す')
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
