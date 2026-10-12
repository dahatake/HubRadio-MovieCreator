#Requires -Version 7.0
# FR-064 AC-040 AC-042 (system): 取り消し・二重押下の防止・キーボード操作・読み上げ名・直接入力。
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label 'FR-064 AC-040/AC-042' -Sandbox $sb -Body {
    try {
        $p = Start-App $sb
        $root = Get-Root $p
        Open-Settings $root
        $edit = Find-ByName $root 'Workspaceフォルダーの場所'
        $draft0 = Get-Value $edit
        $cfg0 = Get-FileHashOrAbsent $sb.Config

        # AC-042: 「参照…」は名前を持ち（支援技術が読み上げる）、キーボードだけで開ける
        $browse = Find-ByName $root '参照…'
        if (-not $browse.Current.IsKeyboardFocusable) { throw '参照… is not keyboard focusable' }
        $browse.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        $dlg = Find-Dialog $root
        if (-not $dlg) { throw 'folder dialog did not open by keyboard (AC-042)' }

        # AC-040: ダイアログの表示中は二重に押せない
        $again = Find-ByName $root '参照…'
        if ($again.Current.IsEnabled) { throw '参照… is still enabled while dialog is open (AC-040)' }

        # AC-040: 取り消し → 入力欄・保存済みの場所とも不変
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ((Find-Dialog $root 1) -and $sw.Elapsed.TotalSeconds -lt 10) { }
        if ((Get-Value (Find-ByName $root 'Workspaceフォルダーの場所')) -ne $draft0) { throw 'input changed after cancel' }
        if ((Get-FileHashOrAbsent $sb.Config) -ne $cfg0) { throw 'workspace-location.json changed after cancel' }
        if (-not (Find-ByName $root '参照…').Current.IsEnabled) { throw '参照… not re-enabled after cancel' }

        # AC-040: フォルダーでない場所（存在しないパス）が返されたとき
        Invoke-Element (Find-ByName $root '参照…')
        $dlg = Find-Dialog $root
        $edit2 = $dlg.FindFirst($script:Desc, [System.Windows.Automation.PropertyCondition]::new($script:UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
        Set-Value $edit2 (Join-Path $sb.Work 'does-not-exist\x.txt')
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Seconds 1
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        if ((Get-Value (Find-ByName $root 'Workspaceフォルダーの場所')) -ne $draft0) { throw 'input changed after invalid selection' }
        if ((Get-FileHashOrAbsent $sb.Config) -ne $cfg0) { throw 'config changed after invalid selection' }

        # AC-042: 直接入力でも同じ結果（保存の案内）が得られる
        $target = Join-Path $sb.Work 'typed-folder'
        New-Item -ItemType Directory -Path $target | Out-Null
        Set-Value (Find-ByName $root 'Workspaceフォルダーの場所') $target
        Invoke-Element (Find-ByName $root '保存')
        $null = Find-ByName $root '保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。'
    } finally { Stop-App $p }
}
