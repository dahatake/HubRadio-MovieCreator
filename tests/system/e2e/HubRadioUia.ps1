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
    [pscustomobject]@{
        Work = $work; AppDir = $appCopy
        Exe = (Join-Path $appCopy (Split-Path -Leaf $appPath))
        Config = (Join-Path $appCopy 'workspace-location.json')
    }
}
function Remove-AppSandbox($sb) { if ($sb) { Remove-Item -LiteralPath $sb.Work -Recurse -Force -ErrorAction SilentlyContinue } }

function Start-App($sb) {
    $p = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{ FileName = $sb.Exe; WorkingDirectory = $sb.AppDir; UseShellExecute = $false })
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($p.MainWindowHandle -eq [IntPtr]::Zero -and -not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    if ($p.HasExited) { throw "app exited: $($p.ExitCode)" }
    $p
}
function Stop-App($p) { if ($p -and -not $p.HasExited) { $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(10000)) { $p.Kill($true) } } }
function Get-Root($p) { $script:UIA::FromHandle($p.MainWindowHandle) }

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
    $cond = [System.Windows.Automation.PropertyCondition]::new($script:UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $d = $root.FindFirst($script:Desc, $cond)
        if ($d) { return $d }
        Start-Sleep -Milliseconds 200
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    $null
}
function Set-DialogFolder($dlg, [string]$path) {
    $edit = $dlg.FindFirst($script:Desc, [System.Windows.Automation.PropertyCondition]::new($script:UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
    Set-Value $edit $path
    Invoke-Element (Find-ByName $dlg 'フォルダーの選択')
}
function Open-Settings($root) { Invoke-Element (Find-ByName $root '設定') }
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
    if (-not (Test-Path $ff)) { throw 'Tools\ffmpeg.exe not found in app folder' }
    $out = Join-Path $dir 'source.mp4'
    & $ff -y -f lavfi -i 'testsrc=size=1920x1080:rate=30:duration=3' -f lavfi -i 'sine=frequency=440:duration=3' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $out 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed to create synthetic video' }
    $out
}
function New-ProjectWithCompletedJobs($root, $sb, [string]$projectName, [int]$jobCount, [string]$videoPath) {
    Invoke-Element (Find-ByName $root '新規作成')
    Set-Value (Find-ByName $root 'Project名') $projectName
    Set-Value (Find-ByName $root '元動画のパス') $videoPath
    Invoke-Element (Find-ByName $root '編集を開始')
    for ($i = 1; $i -le $jobCount; $i++) {
        Invoke-Element (Find-ByName $root 'Jobを作成')
        $null = Find-ByName $root '完了' 600
    }
}
