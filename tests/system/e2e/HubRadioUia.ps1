#Requires -Version 7.0
# System Test 共通部品（UI Automation）。期待値は持たない。各 .ps1 から dot-source する。
# アプリの配布フォルダーを一時領域にコピーして実行し、実利用者の設定には触れない。
# 環境変数 HUBRADIO_APP_PATH で HubRadio.App.exe を指定できる（既定は Release ビルドの出力）。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$script:UIA = [System.Windows.Automation.AutomationElement]
$script:Desc = [System.Windows.Automation.TreeScope]::Descendants
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

function New-AppSandbox {
    $appPath = $env:HUBRADIO_APP_PATH
    if ([string]::IsNullOrWhiteSpace($appPath)) {
        $appPath = Join-Path $script:RepoRoot 'src\HubRadio.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.exe'
    }
    $appPath = (Resolve-Path -LiteralPath $appPath).Path   # 未ビルドならここで失敗（実装前は正常）
    $work = Join-Path $env:TEMP ('hubradio-st-' + [guid]::NewGuid().ToString('N'))
    $appCopy = Join-Path $work 'app'
    New-Item -ItemType Directory -Path $appCopy | Out-Null
    Copy-Item -Path (Join-Path (Split-Path -Parent $appPath) '*') -Destination $appCopy -Recurse -Force
    # 配布形（portable）は設定をアプリのフォルダーに、通常ビルドは利用者の %LOCALAPPDATA%\HubRadio.MovieCreator に置く。
    # 後者は実利用者の設定なので、実行前の内容を退避して終了時に必ず戻す（Remove-AppSandbox）。
    $portable = Test-Path -LiteralPath (Join-Path $appCopy 'WebView2Runtime')
    $cfg = if ($portable) { Join-Path $appCopy 'workspace-location.json' } else { Join-Path $env:LOCALAPPDATA 'HubRadio.MovieCreator\workspace-location.json' }
    $saved = if (-not $portable -and (Test-Path -LiteralPath $cfg)) { [IO.File]::ReadAllBytes($cfg) } else { $null }
    if (-not $portable) {
        # 実利用者の Workspace（既存の Project）を汚さないよう、実行中だけ Workspace を一時領域へ向ける（終了時に設定を元に戻す）
        New-Item -ItemType Directory -Path (Split-Path -Parent $cfg) -Force | Out-Null
        [IO.File]::WriteAllText($cfg, (@{ version = 1; path = (Join-Path $work 'workspace') } | ConvertTo-Json -Compress))
    }
    [pscustomobject]@{
        Work = $work; AppDir = $appCopy
        Exe = (Join-Path $appCopy (Split-Path -Leaf $appPath))
        Config = $cfg; Portable = $portable; SavedConfig = $saved
    }
}
function Remove-AppSandbox($sb) {
    if (-not $sb) { return }
    if (-not $sb.Portable) {
        if ($null -ne $sb.SavedConfig) { [IO.File]::WriteAllBytes($sb.Config, $sb.SavedConfig) }
        elseif (Test-Path -LiteralPath $sb.Config) { Remove-Item -LiteralPath $sb.Config -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $sb.Work -Recurse -Force -ErrorAction SilentlyContinue
}

function Start-App($sb) {
    $p = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{ FileName = $sb.Exe; WorkingDirectory = $sb.AppDir; UseShellExecute = $false })
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($p.MainWindowHandle -eq [IntPtr]::Zero -and -not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    if ($p.HasExited) { throw "app exited: $($p.ExitCode)" }
    $p
}
function Stop-App($p) { if ($p -and -not $p.HasExited) { $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(10000)) { $p.Kill($true) } } }
# WinUI3+WebView2 では画面の中身が MainWindowHandle の子孫ではなく、UIA のデスクトップ直下の別のトップレベル要素
# （起動プロセスまたは子孫プロセスに属する）にある。探索の範囲は「起動したアプリのプロセスと子孫プロセスに属する UIA 要素の集合」とする。
function Get-AppPids($p) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId)
    $set = [System.Collections.Generic.HashSet[int]]::new()
    [void]$set.Add([int]$p.Id)
    do {
        $added = $false
        foreach ($x in $all) { if ($set.Contains([int]$x.ParentProcessId) -and $set.Add([int]$x.ProcessId)) { $added = $true } }
    } while ($added)
    , $set
}
function Get-AppTopLevels($scope) {
    $pids = Get-AppPids $scope.Process
    foreach ($t in $script:UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($pids.Contains([int]$t.Current.ProcessId)) { $t }
    }
}
function Get-Root($p) {
    $o = [pscustomobject]@{ Process = $p }
    $o | Add-Member -MemberType ScriptMethod -Name FindFirst -Value {
        param($scope, $cond)
        foreach ($t in (Get-AppTopLevels $this)) {
            if ($cond.Equals([System.Windows.Automation.Condition]::TrueCondition) -or $t.FindFirst([System.Windows.Automation.TreeScope]::Element, $cond)) { return $t }
            $e = $t.FindFirst($script:Desc, $cond); if ($e) { return $e }
        }
        $null
    }
    $o | Add-Member -MemberType ScriptMethod -Name FindAll -Value {
        param($scope, $cond)
        $r = [System.Collections.Generic.List[object]]::new()
        foreach ($t in (Get-AppTopLevels $this)) {
            $r.Add($t)
            foreach ($e in $t.FindAll($script:Desc, $cond)) { $r.Add($e) }
        }
        , $r.ToArray()
    }
    $o
}

function Find-ByName($root, [string]$name, [int]$timeoutSec = 20) {
    $cond = [System.Windows.Automation.PropertyCondition]::new($script:UIA::NameProperty, $name)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $e = $root.FindFirst($script:Desc, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 200
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw "UI element not found: '$name'"
}
function Test-ByName($root, [string]$name, [int]$timeoutSec = 3) {
    try { $null = Find-ByName $root $name $timeoutSec; $true } catch { $false }
}
function Find-AllByNameLike($root, [string]$regex) {
    $all = $root.FindAll($script:Desc, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { if ($e.Current.Name -match $regex) { $e } }
}
function Invoke-Element($e) {
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { $e.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait(' ') }
}
function Get-Value($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Set-Value($e, [string]$v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Find-Dialog($root, [int]$timeoutSec = 20) {
    $winCond = [System.Windows.Automation.PropertyCondition]::new($script:UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $main = [int]$root.Process.MainWindowHandle
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($t in (Get-AppTopLevels $root)) {
            $cands = @()
            if ($t.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $cands += $t }
            $cands += @($t.FindAll($script:Desc, $winCond))
            foreach ($c in $cands) {
                if ($c.Current.NativeWindowHandle -eq $main) { continue }
                if ($c.Current.Name -like 'Hubらじお Movie Creator*') { continue }
                return $c
            }
        }
        Start-Sleep -Milliseconds 200
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    $null
}
Add-Type -Namespace HubRadioSt -Name Win32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int n); [DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte s, int f, int e); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);' -ErrorAction SilentlyContinue
function Set-Foreground([IntPtr]$h) {
    $null = [HubRadioSt.Win32]::ShowWindow($h, 5)
    for ($i = 0; $i -lt 15; $i++) {
        [HubRadioSt.Win32]::keybd_event(0x12, 0, 0, 0); [HubRadioSt.Win32]::keybd_event(0x12, 0, 2, 0)   # Alt を押して前面化の制限を外す
        $null = [HubRadioSt.Win32]::SetForegroundWindow($h)
        Start-Sleep -Milliseconds 300
        if ([HubRadioSt.Win32]::GetForegroundWindow() -eq $h) { break }
    }
    Start-Sleep -Milliseconds 300
}
function Set-DialogForeground($dlg) { Set-Foreground ([IntPtr]$dlg.Current.NativeWindowHandle) }
# アプリのウィンドウを前面にし、要素を実際にマウスでクリックして本物のキーボードフォーカスを与える
function Focus-ByClick($p, $e) {
    Set-Foreground $p.MainWindowHandle
    $rc = $e.Current.BoundingRectangle
    $null = [HubRadioSt.Win32]::SetCursorPos([int]($rc.X + $rc.Width / 2), [int]($rc.Y + $rc.Height / 2))
    [HubRadioSt.Win32]::mouse_event(2, 0, 0, 0, 0); [HubRadioSt.Win32]::mouse_event(4, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 600
}
function ConvertTo-SendKeysText([string]$s) { [regex]::Replace($s, '[+^%~(){}\[\]]', '{$0}') }
function Click-Element($e, $hwnd) {
    if ($hwnd) { Set-Foreground ([IntPtr]$hwnd) }
    $rc = $e.Current.BoundingRectangle
    $null = [HubRadioSt.Win32]::SetCursorPos([int]($rc.X + $rc.Width / 2), [int]($rc.Y + $rc.Height / 2))
    [HubRadioSt.Win32]::mouse_event(2, 0, 0, 0, 0); [HubRadioSt.Win32]::mouse_event(4, 0, 0, 0, 0)
}
# ダイアログ要素の「キャンセル」を UIA で押す（InvokePattern が無ければその位置をクリック）
function Close-DialogByEsc($dlg, $root) {
    $c = Find-ByName $dlg 'キャンセル' 5
    try { $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { Click-Element $c $dlg.Current.NativeWindowHandle }
}
function Set-DialogFolder($dlg, [string]$path) {
    # OS のフォルダー選択ダイアログ: 前面にして、フォルダー名の欄（既定のフォーカス）へ入力し、「フォルダーの選択」を押す
    Set-DialogForeground $dlg
    [System.Windows.Forms.SendKeys]::SendWait('%f')
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysText $path))
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')   # 入力した場所へ移動する
    Start-Sleep -Milliseconds 1200
    # 欄を空にして Enter（＝表示中のフォルダーを選択）。応答しない場合は「フォルダーの選択」を押す
    [System.Windows.Forms.SendKeys]::SendWait('%f')
    [System.Windows.Forms.SendKeys]::SendWait('^a{DEL}')
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}
function Open-Settings($root) {
    # 画面の準備前の押下は無視されることがあるため、設定画面の項目が現れるまで押し直す
    for ($i = 0; $i -lt 5; $i++) {
        Invoke-Element (Find-ByName $root '設定')
        if (Test-ByName $root 'Workspaceフォルダーの場所' 6) { return }
    }
}
function Get-FileHashOrAbsent([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'absent' } }

function Invoke-Test([scriptblock]$Body, [string]$Label, $Sandbox) {
    $failed = $null
    try { & $Body } catch { $failed = $_ }
    finally { Remove-AppSandbox $Sandbox }
    if ($failed) { Write-Error "$Label failed: $failed"; exit 1 }
    Write-Host "$Label passed"; exit 0
}

# --- Job 成果物つき Project の作成（実際の操作で作る。合成した 2 秒の動画を元素材にする） ---
function New-SyntheticVideo($sb, [string]$dir) {
    $ff = Join-Path $sb.AppDir 'Tools\ffmpeg.exe'
    if (-not (Test-Path $ff)) {
        # 開発ビルドには同梱されない。テスト用の合成動画の作成だけに PATH 上の ffmpeg を使う（アプリの動作には使わない）
        $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if (-not $cmd) { throw 'ffmpeg not found (Tools\ffmpeg.exe in app folder or ffmpeg on PATH)' }
        $ff = $cmd.Source
    }
    $out = Join-Path $dir 'source.mp4'
    & $ff -y -f lavfi -i 'testsrc=size=1920x1080:rate=30:duration=3' -f lavfi -i 'sine=frequency=440:duration=3' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $out 2>$null
    if ($LASTEXITCODE -ne 0) {
        # libx264 の無い ffmpeg 向け（合成動画の作成だけの代替）
        & $ff -y -f lavfi -i 'testsrc=size=1920x1080:rate=30:duration=3' -f lavfi -i 'sine=frequency=440:duration=3' -c:v mpeg4 -pix_fmt yuv420p -c:a aac -shortest $out 2>$null
    }
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed to create synthetic video' }
    $out
}
function New-ProjectWithCompletedJobs($root, $sb, [string]$projectName, [int]$jobCount, [string]$videoPath) {
    # 「新しいProjectを作成」は OS の「開く」ダイアログで動画を 1 本選ぶ。選ぶとそのまま「かんたん編集」の画面になる。
    Invoke-Element (Find-ByName $root '新しいProjectを作成')
    $dlg = Find-Dialog $root 20
    if (-not $dlg) { throw 'file dialog did not open after pressing 新しいProjectを作成' }
    Set-DialogForeground $dlg
    [System.Windows.Forms.SendKeys]::SendWait('%n')
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysText $videoPath))
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $nameBox = Find-ByName $root 'Project名（この画面と一覧に表示される名前）' 30
    Set-Value $nameBox $projectName
    Invoke-Element (Find-ByName $root 'Project名を保存')
    # 最初の Job は「編集を開始」で作られる。完了は、その Job のサムネイルの名前（… 完了）で判定する
    Invoke-Element (Find-ByName $root '編集を開始')
    Wait-JobCompleted $root 1
    # 2 つ目以降は「新しいJobを作成」→ 確認の「作成する」
    for ($i = 2; $i -le $jobCount; $i++) {
        Invoke-Element (Find-ByName $root '新しいJobを作成' 60)
        Invoke-Element (Find-ByName $root '作成する' 30)
        Wait-JobCompleted $root $i
    }
}
function Wait-JobCompleted($root, [int]$jobNumber, [int]$timeoutSec = 900) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (@(Find-AllByNameLike $root "^サムネイル: Job $jobNumber、.*完了$").Count -gt 0) { return }
        $failed = @(Find-AllByNameLike $root '操作を完了できませんでした|^サムネイル: Job \d+、.*(失敗|取消)$')
        if ($failed.Count -gt 0) {
            $msg = (Find-AllByNameLike $root '編集を開始できません|失敗') | Select-Object -First 1
            throw "Job $jobNumber did not complete: $($msg.Current.Name)"
        }
        Start-Sleep -Seconds 2
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw "Job $jobNumber did not complete within $timeoutSec s"
}



