#Requires -Version 7.0
# FR-064 AC-039 (system): 「参照…」でフォルダーを選ぶ → 入力欄に反映 → 選んだだけでは
# workspace-location.json が変わらない → 「保存」で成功の案内 → 再起動後にその場所が使われる。
# 期待値は受入基準だけから決める。アプリの配布フォルダーを一時領域にコピーして実行し、実利用者の設定には触れない。
# 環境変数 HUBRADIO_APP_PATH で HubRadio.App.exe を指定できる（既定は Release ビルドの出力）。
[CmdletBinding()]
param([string]$AppPath = $env:HUBRADIO_APP_PATH)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $repoRoot 'src\HubRadio.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.exe'
}
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path   # アプリ未ビルドなら、ここで失敗する（実装前は正常）

$work = Join-Path $env:TEMP ('fr064-' + [guid]::NewGuid().ToString('N'))
$appCopy = Join-Path $work 'app'
$target = Join-Path $work 'picked-empty-folder'
New-Item -ItemType Directory -Path $appCopy, $target | Out-Null
Copy-Item -Path (Join-Path (Split-Path -Parent $AppPath) '*') -Destination $appCopy -Recurse -Force
$exe = Join-Path $appCopy (Split-Path -Leaf $AppPath)
$config = Join-Path $appCopy 'workspace-location.json'
$UIA = [System.Windows.Automation.AutomationElement]
$scopeDesc = [System.Windows.Automation.TreeScope]::Descendants

function Find-ByName($root, [string]$name, [int]$timeoutSec = 20) {
    $cond = [System.Windows.Automation.PropertyCondition]::new($UIA::NameProperty, $name)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $e = $root.FindFirst($scopeDesc, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 200
    }
    throw "UI element not found: '$name'"
}
function Invoke-Element($e) {
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { $e.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait(' ') }
}
function Start-App {
    $p = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{ FileName = $exe; WorkingDirectory = $appCopy; UseShellExecute = $false })
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($p.MainWindowHandle -eq [IntPtr]::Zero -and -not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    if ($p.HasExited) { throw "app exited: $($p.ExitCode)" }
    return $p
}
function Stop-App($p) { if (-not $p.HasExited) { $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(10000)) { $p.Kill($true) } } }
function Open-Settings($root) { Invoke-Element (Find-ByName $root '設定') }
function Get-Draft($root) {
    $edit = Find-ByName $root 'Workspaceフォルダーの場所'
    return $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

$failed = $null
$p = $null
try {
    # --- 1回目の起動: 参照…で選ぶ ---
    $p = Start-App
    $root = $UIA::FromHandle($p.MainWindowHandle)
    Open-Settings $root
    $before = if (Test-Path $config) { (Get-FileHash $config).Hash } else { 'absent' }

    Invoke-Element (Find-ByName $root '参照…')
    # OS 標準のフォルダー選択ダイアログ（アプリのウィンドウの子）
    $dlg = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $dlg -and $sw.Elapsed.TotalSeconds -lt 20) {
        $dlg = $root.FindFirst($scopeDesc, [System.Windows.Automation.PropertyCondition]::new($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))
        if (-not $dlg) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $dlg) { throw 'folder dialog did not open after pressing 参照…' }
    $nameEdit = $dlg.FindFirst($scopeDesc, [System.Windows.Automation.PropertyCondition]::new($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
    $nameEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($target)
    Invoke-Element (Find-ByName $dlg 'フォルダーの選択')

    # 入力欄にそのパスが入る
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Draft $root) -ne $target -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
    if ((Get-Draft $root) -ne $target) { throw "input not updated: '$(Get-Draft $root)'" }

    # 選んだだけでは workspace-location.json は変わらない
    $mid = if (Test-Path $config) { (Get-FileHash $config).Hash } else { 'absent' }
    if ($mid -ne $before) { throw 'workspace-location.json changed before pressing 保存' }

    # 保存 → FR-062.4 の成功の案内
    Invoke-Element (Find-ByName $root '保存')
    $null = Find-ByName $root '保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。'
    if (-not (Test-Path $config) -or -not ((Get-Content $config -Raw) -like "*$($target.Replace('\','\\'))*")) {
        throw 'workspace-location.json does not point to the picked folder after 保存'
    }
    Stop-App $p

    # --- 再起動後: 現在使っている場所が選んだフォルダー ---
    $p = Start-App
    $root = $UIA::FromHandle($p.MainWindowHandle)
    Open-Settings $root
    $null = Find-ByName $root '現在使っている場所'
    $null = Find-ByName $root $target 10
    if (-not (Get-ChildItem $target -Force | Select-Object -First 1)) { throw 'picked folder is not used by the app after restart (no workspace files)' }
}
catch { $failed = $_ }
finally {
    if ($p) { Stop-App $p }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { Write-Error "FR-064 AC-039 failed: $failed"; exit 1 }
Write-Host 'FR-064 AC-039 passed'
exit 0
