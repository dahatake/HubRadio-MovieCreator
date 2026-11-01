#Requires -Version 7.0
# FR-064 AC-039 (system): 「参照…」でフォルダーを選ぶ → 入力欄に反映 → 選んだだけでは
# workspace-location.json が変わらない → 「保存」で成功の案内と、選んだパスの保存（再起動後は AC の範囲外）。
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$target = Join-Path $sb.Work 'picked-empty-folder'
New-Item -ItemType Directory -Path $target | Out-Null
$p = $null
Invoke-Test -Label 'FR-064 AC-039' -Sandbox $sb -Body {
    try {
        # --- 1回目の起動: 参照…で選ぶ ---
        $p = Start-App $sb
        $root = Get-Root $p
        Open-Settings $root
        $before = Get-FileHashOrAbsent $sb.Config

        Invoke-Element (Find-ByName $root '参照…')
        $dlg = Find-Dialog $root
        if (-not $dlg) { throw 'folder dialog did not open after pressing 参照…' }
        Set-DialogFolder $dlg $target

        # 入力欄にそのパスが入る
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ((Get-Value (Find-ByName $root 'Workspaceフォルダーの場所')) -ne $target -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
        $cur = Get-Value (Find-ByName $root 'Workspaceフォルダーの場所')
        if ($cur -ne $target) { throw "input not updated: '$cur'" }

        # 選んだだけでは workspace-location.json は変わらない
        if ((Get-FileHashOrAbsent $sb.Config) -ne $before) { throw 'workspace-location.json changed before pressing 保存' }

        # 保存 → FR-062.4 の成功の案内
        Invoke-Element (Find-ByName $root '保存')
        $null = Find-ByName $root '保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。'
        if (-not (Test-Path $sb.Config) -or -not (@((Get-Content $sb.Config -Raw | ConvertFrom-Json).PSObject.Properties.Value) | Where-Object { $_ -is [string] -and $_.TrimEnd('\') -ieq $target.TrimEnd('\') })) {
            throw 'workspace-location.json does not point to the picked folder after 保存'
        }
    } finally { Stop-App $p }
}


