#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$AppPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'PowerShell 7+ (PSEdition Core) is required.'
}

Add-Type -AssemblyName UIAutomationClient

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $repoRoot 'src\HubRadio.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.exe'
}
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path
$appDirectory = Split-Path -Parent $AppPath

function Get-InstallTreeSnapshot {
    Get-ChildItem -LiteralPath $appDirectory -File -Recurse |
        Sort-Object -Property FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName.Substring($appDirectory.Length)
                Length = $_.Length
                Digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
}

function Start-HubRadioApp {
    param(
        [string]$MissingRuntimePath = ''
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($AppPath)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $appDirectory
    if (-not [string]::IsNullOrWhiteSpace($MissingRuntimePath)) {
        $startInfo.Environment['WEBVIEW2_BROWSER_EXECUTABLE_FOLDER'] = $MissingRuntimePath
    }

    return [System.Diagnostics.Process]::Start($startInfo)
}

function Wait-MainWindow {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($Process.MainWindowHandle -eq [IntPtr]::Zero -and
        -not $Process.HasExited -and
        $stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
        [System.Threading.Thread]::Sleep(100)
        $Process.Refresh()
    }

    if ($Process.HasExited) {
        throw "HubRadio.App exited during startup with code $($Process.ExitCode)."
    }
    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'HubRadio.App did not expose a main window.'
    }

    return [System.Windows.Automation.AutomationElement]::FromHandle(
        $Process.MainWindowHandle)
}

function Wait-AutomationElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId,

        [Parameter(Mandatory)]
        [string]$ExpectedName
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $element -and $element.Current.Name -eq $ExpectedName) {
            return $element
        }

        [System.Threading.Thread]::Sleep(100)
    }

    throw "Automation element '$AutomationId' did not reach '$ExpectedName'."
}

function Stop-HubRadioApp {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    if (-not $Process.HasExited) {
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(10000)) {
            $Process.Kill($true)
            $Process.WaitForExit()
            throw 'HubRadio.App did not close normally.'
        }
    }
    if ($Process.ExitCode -ne 0) {
        throw "HubRadio.App closed with code $($Process.ExitCode)."
    }

    $Process.Dispose()
}

function Invoke-ReadySmoke {
    $process = Start-HubRadioApp
    try {
        $root = Wait-MainWindow -Process $process
        $webApplication = Wait-AutomationElement `
            -Root $root `
            -AutomationId 'BundledWebApplication' `
            -ExpectedName 'Bundled Web UI ready'
        if ($webApplication.Current.IsOffscreen) {
            throw 'The bundled Web UI remained offscreen.'
        }
    }
    finally {
        Stop-HubRadioApp -Process $process
    }
}

function Invoke-RuntimeUnavailableSmoke {
    $missingRuntimePath = Join-Path $env:TEMP (
        'missing-webview2-' + [guid]::NewGuid().ToString('N'))
    $process = Start-HubRadioApp -MissingRuntimePath $missingRuntimePath
    try {
        $root = Wait-MainWindow -Process $process
        $null = Wait-AutomationElement `
            -Root $root `
            -AutomationId 'WebViewStartupStatus' `
            -ExpectedName 'WebView2 Runtime を利用できません'

        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $names = @($elements | ForEach-Object { $_.Current.Name })
        if ($names -notcontains '診断コード: webview.runtime_unavailable') {
            throw 'The runtime-unavailable diagnostic code was not displayed.'
        }
    }
    finally {
        Stop-HubRadioApp -Process $process
    }
}

$before = @(Get-InstallTreeSnapshot)
Invoke-ReadySmoke
Invoke-RuntimeUnavailableSmoke
$after = @(Get-InstallTreeSnapshot)
$changes = @(Compare-Object -ReferenceObject $before -DifferenceObject $after -Property Path, Length, Digest)
if ($changes.Count -ne 0) {
    throw 'The application modified its install tree during startup smoke tests.'
}
if (@(Get-ChildItem -LiteralPath $appDirectory -Directory -Filter '*.WebView2').Count -ne 0) {
    throw 'WebView2 user data was created beside the executable.'
}

[pscustomobject]@{
    Ready = $true
    RuntimeUnavailableRecovery = $true
    InstallTreeUnchanged = $true
    ExecutableAdjacentProfileAbsent = $true
}
